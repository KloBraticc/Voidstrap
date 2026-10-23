#include "depth.h"
#include "depth_model.h"
#include "../addon.h"
#include "../capture.h"
#include "../config.h"
#include "../log.h"
#include "../reshade_imgui.h"
#include "../state.h"

#include <d3d12.h>
#include <d3dcompiler.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <initializer_list>
#include <memory>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <thread>
#include <utility>
#include <vector>

namespace
{
constexpr UINT kRowBytes = 16 * D3D12_TEXTURE_DATA_PITCH_ALIGNMENT;

constexpr char kShaders[] = R"(
cbuffer Sizes : register(b0)
{
    uint2 SourceSize;
    uint2 ModelSize;
    uint InputWidth;
    uint OutputWidth;
    uint Distance;
};
Texture2D<float4> Source : register(t0);
Texture2D<float> Result : register(t1);
StructuredBuffer<float4> Smoothed : register(t2);
Texture2D<float4> Guide : register(t3);
RWTexture2D<float> Input : register(u0);
RWByteAddressBuffer Keys : register(u1);
RWStructuredBuffer<float4> State : register(u2);
RWTexture2D<float> Depth : register(u3);
RWTexture2D<float4> GuideTarget : register(u4);

static const float3 Mean = float3(0.485, 0.456, 0.406);
static const float3 Std = float3(0.229, 0.224, 0.225);
static const float PeakSmoothing = 0.2;
static const float FarPlane = 1000.0;
static const float Nearest = 0.01;
static const float SpatialSigma = 1.0;
static const float ColorSigma = 0.1;
static const uint Bins = 256;
static const float ReferenceShare = 0.02;

void Store(uint index, float value)
{
    Input[uint2(index % InputWidth, index / InputWidth)] = value;
}

float Load(uint index)
{
    float value = Result.Load(int3(index % OutputWidth, index / OutputWidth, 0));
    return Distance ? 1.0 / max(value, 1e-4) : value;
}

[numthreads(16, 16, 1)]
void Preprocess(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= ModelSize))
        return;
    uint2 begin = id.xy * SourceSize / ModelSize;
    uint2 end = max((id.xy + 1) * SourceSize / ModelSize, begin + 1);
    float3 sum = 0;
    for (uint y = begin.y; y < end.y; ++y)
        for (uint x = begin.x; x < end.x; ++x)
            sum += Source.Load(int3(x, y, 0)).rgb;
    float3 average = sum / ((end.x - begin.x) * (end.y - begin.y));
    GuideTarget[id.xy] = float4(average, 1);
    float3 color = (average - Mean) / Std;
    uint plane = ModelSize.x * ModelSize.y;
    uint index = id.y * ModelSize.x + id.x;
    Store(index, color.r);
    Store(index + plane, color.g);
    Store(index + 2 * plane, color.b);
}

uint Ordered(float value)
{
    uint bits = asuint(value);
    return bits & 0x80000000 ? ~bits : bits | 0x80000000;
}

float Unordered(uint key)
{
    return asfloat(key & 0x80000000 ? key & 0x7FFFFFFF : ~key);
}

groupshared float Peaks[256];

[numthreads(256, 1, 1)]
void Peak(uint3 id : SV_DispatchThreadID, uint thread : SV_GroupIndex)
{
    Peaks[thread] = Load(min(id.x, ModelSize.x * ModelSize.y - 1));
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 128; step > 0; step >>= 1)
    {
        if (thread < step)
            Peaks[thread] = max(Peaks[thread], Peaks[thread + step]);
        GroupMemoryBarrierWithGroupSync();
    }
    if (thread == 0)
    {
        uint previous;
        Keys.InterlockedMax(0, Ordered(Peaks[0]), previous);
    }
}

[numthreads(256, 1, 1)]
void Histogram(uint3 id : SV_DispatchThreadID)
{
    uint pixels = ModelSize.x * ModelSize.y;
    if (id.x >= pixels)
        return;
    float peak = Unordered(Keys.Load(0));
    if (!(peak > 0))
        return;
    uint bin = min(uint(saturate(Load(id.x) / peak) * Bins), Bins - 1);
    uint previous;
    Keys.InterlockedAdd(4 + bin * 4, 1, previous);
}

[numthreads(1, 1, 1)]
void Smooth()
{
    float maximum = Unordered(Keys.Load(0));
    float peak = maximum;
    if (maximum > 0)
    {
        uint wanted = max(1u, uint(ModelSize.x * ModelSize.y * ReferenceShare));
        uint seen = 0;
        for (int bin = int(Bins) - 1; bin >= 0; --bin)
        {
            seen += Keys.Load(4 + uint(bin) * 4);
            if (seen >= wanted)
            {
                peak = max((bin + 1.0) / Bins * maximum, maximum * 0.05);
                break;
            }
        }
    }
    float4 state = State[0];
    if (state.z == 0)
        state.x = peak;
    State[0] = float4(state.x + (peak - state.x) * PeakSmoothing, 0, 1, 0);
}

[numthreads(16, 16, 1)]
void Upsample(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= SourceSize))
        return;
    float3 color = Source.Load(int3(id.xy, 0)).rgb;
    float2 position = (id.xy + 0.5) * ModelSize / SourceSize - 0.5;
    int2 corner = int2(floor(position)) - 1;
    float sum = 0;
    float weights = 0;
    for (int y = 0; y < 4; ++y)
        for (int x = 0; x < 4; ++x)
        {
            int2 texel = corner + int2(x, y);
            float2 offset = texel - position;
            texel = clamp(texel, 0, int2(ModelSize) - 1);
            float3 difference = Guide.Load(int3(texel, 0)).rgb - color;
            float weight = exp(-dot(offset, offset) / (2 * SpatialSigma * SpatialSigma)) *
                           (exp(-dot(difference, difference) / (2 * ColorSigma * ColorSigma)) + 1e-3);
            sum += weight * Load(texel.y * ModelSize.x + texel.x);
            weights += weight;
        }

    float closeness = sum / weights / max(Smoothed[0].x, 1e-6);
    float distance01 = Nearest / max(closeness, Nearest);
    Depth[id.xy] = distance01 * FarPlane / (1 + distance01 * (FarPlane - 1));
}
)";

constexpr char kTrackShaders[] = R"(
cbuffer Track : register(b0)
{
    uint2 SourceSize;
    uint2 WorkSize;
};
Texture2D<float4> Color : register(t0);
Texture2D<float> Luma : register(t1);
Texture2D<float> PrevLuma : register(t2);
Texture2D<float> Quarter : register(t3);
Texture2D<float> PrevQuarter : register(t4);
Texture2D<float2> Coarse : register(t5);
Texture2D<float4> Flow : register(t6);
Texture2D<float> Anchor : register(t7);
Texture2D<float> Warped : register(t8);
Texture2D<float4> Map : register(t9);
RWTexture2D<float> LumaTarget : register(u0);
RWTexture2D<float> QuarterTarget : register(u1);
RWTexture2D<float2> CoarseTarget : register(u2);
RWTexture2D<float4> FlowTarget : register(u3);
RWTexture2D<float> Depth : register(u4);
RWTexture2D<float> WarpedTarget : register(u5);
RWTexture2D<float4> SettledTarget : register(u6);
RWTexture2D<float4> MapTarget : register(u7);

static const int CoarseSearch = 8;
static const int Patch = 2;
static const float MotionCost = 0.002;
static const float LostMatch = 0.06;
static const int FillDirections = 8;
static const int FillSteps = 4;
static const float TemporalBase = 0.5;
static const float TemporalResponse = 6.0;
static const int SettleRadius = 2;
static const float EdgeSpread = 2.0;
static const float CornerScale = 400.0;
static const float ConfidenceFloor = 0.02;
static const float SettleLuma = 0.05;
static const float SettleCost = 0.03;
static const int RefineRadius = 2;
static const int RefineStride = 2;
static const float RefineColor = 0.08;

uint2 QuarterSize()
{
    return (WorkSize + 3) / 4;
}

[numthreads(16, 16, 1)]
void Shrink(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    uint2 begin = id.xy * SourceSize / WorkSize;
    uint2 end = max((id.xy + 1) * SourceSize / WorkSize, begin + 1);
    float sum = 0;
    for (uint y = begin.y; y < end.y; ++y)
        for (uint x = begin.x; x < end.x; ++x)
            sum += dot(Color.Load(int3(x, y, 0)).rgb, float3(0.299, 0.587, 0.114));
    LumaTarget[id.xy] = sum / ((end.x - begin.x) * (end.y - begin.y));
}

[numthreads(16, 16, 1)]
void Reduce(uint3 id : SV_DispatchThreadID)
{
    uint2 size = QuarterSize();
    if (any(id.xy >= size))
        return;
    float sum = 0;
    float count = 0;
    for (uint y = 0; y < 4; ++y)
        for (uint x = 0; x < 4; ++x)
        {
            uint2 p = id.xy * 4 + uint2(x, y);
            if (all(p < WorkSize))
            {
                sum += Luma.Load(int3(p, 0));
                count += 1;
            }
        }
    QuarterTarget[id.xy] = sum / max(count, 1);
}

[numthreads(16, 16, 1)]
void MatchCoarse(uint3 id : SV_DispatchThreadID)
{
    int2 size = int2(QuarterSize());
    int2 p = int2(id.xy);
    if (any(p >= size))
        return;
    float centre[25];
    for (uint k = 0; k < 25; ++k)
        centre[k] = Quarter.Load(int3(clamp(p + (int2(k % 5, k / 5) - Patch), 0, size - 1), 0));
    float best = 1e9;
    int2 bestOffset = 0;
    for (int dy = -CoarseSearch; dy <= CoarseSearch; ++dy)
        for (int dx = -CoarseSearch; dx <= CoarseSearch; ++dx)
        {
            float cost = MotionCost * (abs(dx) + abs(dy));
            for (uint k2 = 0; k2 < 25; ++k2)
                cost += abs(centre[k2] - PrevQuarter.Load(int3(clamp(p - int2(dx, dy) + (int2(k2 % 5, k2 / 5) - Patch), 0, size - 1), 0)));
            if (cost < best)
            {
                best = cost;
                bestOffset = int2(dx, dy);
            }
        }
    CoarseTarget[id.xy] = float2(bestOffset);
}

[numthreads(16, 16, 1)]
void MatchFine(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    int2 p = int2(id.xy);
    int2 size = int2(WorkSize);
    float centre[25];
    for (uint k = 0; k < 25; ++k)
        centre[k] = Luma.Load(int3(clamp(p + (int2(k % 5, k / 5) - Patch), 0, size - 1), 0));
    float xx = 0;
    float yy = 0;
    float xy = 0;
    for (uint g = 0; g < 9; ++g)
    {
        uint gx = g % 3 + 1;
        uint gy = g / 3 + 1;
        float dx = centre[gy * 5 + gx + 1] - centre[gy * 5 + gx - 1];
        float dy = centre[(gy + 1) * 5 + gx] - centre[(gy - 1) * 5 + gx];
        xx += dx * dx;
        yy += dy * dy;
        xy += dx * dy;
    }
    float corner = (xx + yy) * 0.5 - sqrt((xx - yy) * (xx - yy) * 0.25 + xy * xy);
    int2 start = int2(round(Coarse.Load(int3(min(id.xy / 4, QuarterSize() - 1), 0)) * 4));
    float costs[25];
    float best = 1e9;
    uint bestIndex = 12;
    for (uint j = 0; j < 25; ++j)
    {
        int2 offset = start + (int2(j % 5, j / 5) - 2);
        float cost = 0;
        for (uint k2 = 0; k2 < 25; ++k2)
            cost += abs(centre[k2] - PrevLuma.Load(int3(clamp(p - offset + (int2(k2 % 5, k2 / 5) - Patch), 0, size - 1), 0)));
        cost /= 25;
        costs[j] = cost;
        if (cost < best)
        {
            best = cost;
            bestIndex = j;
        }
    }
    uint bx = bestIndex % 5;
    uint by = bestIndex / 5;
    float2 sub = 0;
    if (bx > 0 && bx < 4)
    {
        float curve = costs[bestIndex - 1] - 2 * best + costs[bestIndex + 1];
        if (curve > 1e-5)
            sub.x = clamp(0.5 * (costs[bestIndex - 1] - costs[bestIndex + 1]) / curve, -0.5, 0.5);
    }
    if (by > 0 && by < 4)
    {
        float curve = costs[bestIndex - 5] - 2 * best + costs[bestIndex + 5];
        if (curve > 1e-5)
            sub.y = clamp(0.5 * (costs[bestIndex - 5] - costs[bestIndex + 5]) / curve, -0.5, 0.5);
    }
    float2 offset = float2(start + (int2(bx, by) - 2)) + sub;
    FlowTarget[id.xy] = float4(offset, best, saturate(corner * CornerScale) * saturate(1 - best * 12));
}

float4 SettleAt(int2 p, int stride)
{
    int2 size = int2(WorkSize);
    float centre = Luma.Load(int3(p, 0));
    float2 sum = 0;
    float confidence = 0;
    float weights = 0;
    for (int dy = -SettleRadius; dy <= SettleRadius; ++dy)
        for (int dx = -SettleRadius; dx <= SettleRadius; ++dx)
        {
            int2 q = clamp(p + int2(dx, dy) * stride, 0, size - 1);
            float4 flow = Flow.Load(int3(q, 0));
            float difference = Luma.Load(int3(q, 0)) - centre;
            float weight = exp(-difference * difference / (2 * SettleLuma * SettleLuma)) * exp(-flow.z / SettleCost) * (flow.w + ConfidenceFloor) + 1e-6;
            sum += weight * flow.xy;
            confidence += weight * flow.w;
            weights += weight;
        }
    return float4(sum / weights, Flow.Load(int3(p, 0)).z, confidence / weights);
}

[numthreads(16, 16, 1)]
void Settle(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    SettledTarget[id.xy] = SettleAt(int2(id.xy), 1);
}

[numthreads(16, 16, 1)]
void SettleWide(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    FlowTarget[id.xy] = SettleAt(int2(id.xy), 4);
}

float4 MapAt(float2 position)
{
    int2 size = int2(WorkSize);
    int2 base = int2(floor(position));
    float2 t = position - base;
    float4 a = Map.Load(int3(clamp(base, 0, size - 1), 0));
    float4 b = Map.Load(int3(clamp(base + int2(1, 0), 0, size - 1), 0));
    float4 c = Map.Load(int3(clamp(base + int2(0, 1), 0, size - 1), 0));
    float4 d = Map.Load(int3(clamp(base + int2(1, 1), 0, size - 1), 0));
    float spread = max(max(distance(a.xy, b.xy), distance(a.xy, c.xy)), distance(a.xy, d.xy));
    bool smooth = spread <= EdgeSpread && min(min(a.z, b.z), min(c.z, d.z)) >= 0.5;
    float4 nearest = Map.Load(int3(clamp(int2(round(position)), 0, size - 1), 0));
    return smooth ? lerp(lerp(a, b, t.x), lerp(c, d, t.x), t.y) : nearest;
}

float AnchorAt(float2 work)
{
    int2 size = int2(SourceSize);
    float2 position = (work + 0.5) * float2(SourceSize) / float2(WorkSize) - 0.5;
    int2 base = int2(floor(position));
    float2 t = position - base;
    float a = Anchor.Load(int3(clamp(base, 0, size - 1), 0));
    float b = Anchor.Load(int3(clamp(base + int2(1, 0), 0, size - 1), 0));
    float c = Anchor.Load(int3(clamp(base + int2(0, 1), 0, size - 1), 0));
    float d = Anchor.Load(int3(clamp(base + int2(1, 1), 0, size - 1), 0));
    return lerp(lerp(a, b, t.x), lerp(c, d, t.x), t.y);
}

[numthreads(16, 16, 1)]
void Compose(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    float4 flow = Flow.Load(int3(id.xy, 0));
    float2 from = float2(id.xy) - flow.xy;
    float4 origin = MapAt(from);
    bool outside = any(from < -0.5) || any(from > float2(WorkSize) - 0.5);
    MapTarget[id.xy] = float4(origin.xy, flow.z > LostMatch || outside ? 0 : origin.z, 0);
}

[numthreads(16, 16, 1)]
void Identity(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    MapTarget[id.xy] = float4(float2(id.xy), 1, 0);
}

[numthreads(16, 16, 1)]
void Warp(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= SourceSize))
        return;
    float2 position = (id.xy + 0.5) * float2(WorkSize) / float2(SourceSize) - 0.5;
    int2 w = clamp(int2(round(position)), 0, int2(WorkSize) - 1);
    float4 origin = MapAt(position);
    float current = -1;
    if (origin.z >= 0.5)
        current = AnchorAt(origin.xy);
    else
    {
        for (int step = 1; step <= FillSteps; ++step)
            for (int direction = 0; direction < FillDirections; ++direction)
            {
                float angle = direction * 6.2831853 / FillDirections;
                int2 n = clamp(w + int2(round(float2(cos(angle), sin(angle)) * (2 << step))), 0, int2(WorkSize) - 1);
                float4 neighbour = Map.Load(int3(n, 0));
                if (neighbour.z >= 0.5)
                    current = max(current, AnchorAt(neighbour.xy + position - n));
            }
        if (current < 0)
            current = AnchorAt(position);
    }
    WarpedTarget[id.xy] = current;
}

[numthreads(16, 16, 1)]
void Refine(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= SourceSize))
        return;
    int2 p = int2(id.xy);
    int2 size = int2(SourceSize);
    float3 centre = Color.Load(int3(p, 0)).rgb;
    float sum = 0;
    float weights = 0;
    for (int dy = -RefineRadius; dy <= RefineRadius; ++dy)
        for (int dx = -RefineRadius; dx <= RefineRadius; ++dx)
        {
            int2 q = clamp(p + int2(dx, dy) * RefineStride, 0, size - 1);
            float3 difference = Color.Load(int3(q, 0)).rgb - centre;
            float weight = exp(-dot(difference, difference) / (2 * RefineColor * RefineColor)) * exp(-(dx * dx + dy * dy) / 4.0);
            sum += weight * Warped.Load(int3(q, 0));
            weights += weight;
        }
    float current = sum / weights;
    float previous = Depth[id.xy];
    float change = abs(current - previous) / max(max(current, previous), 1e-4);
    Depth[id.xy] = lerp(previous, current, saturate(TemporalBase + change * TemporalResponse));
}
)";

constexpr const char* kEntryPoints[] = { "Preprocess", "Peak", "Histogram", "Smooth", "Upsample" };
constexpr const char* kTrackEntryPoints[] = { "Shrink", "Reduce", "MatchCoarse", "MatchFine", "Warp", "Settle", "Refine", "SettleWide", "Compose", "Identity" };
constexpr size_t kShaderCount = std::size(kEntryPoints) + std::size(kTrackEntryPoints);
constexpr size_t kShrink = std::size(kEntryPoints);
constexpr size_t kReduce = kShrink + 1;
constexpr size_t kMatchCoarse = kShrink + 2;
constexpr size_t kMatchFine = kShrink + 3;
constexpr size_t kWarp = kShrink + 4;
constexpr size_t kSettle = kShrink + 5;
constexpr size_t kRefine = kShrink + 6;
constexpr size_t kSettleWide = kShrink + 7;
constexpr size_t kCompose = kShrink + 8;
constexpr size_t kIdentity = kShrink + 9;

struct Surface
{
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::com_ptr<ID3D11UnorderedAccessView> target;
    winrt::com_ptr<ID3D11ShaderResourceView> view;
};

constexpr std::chrono::milliseconds kMinDepthInterval{ 50 };
constexpr std::chrono::milliseconds kKeyframeInterval{ 33 };

struct Depth
{
    bool enabled = false;
    std::wstring directory;

    int width = 0;
    int height = 0;
    int size = 0;
    UINT sourceWidth = 0;
    UINT sourceHeight = 0;
    std::optional<int64_t> lastInput;
    FrameStatistics::Clock::time_point requestedAt{};
    FrameStatistics::Clock::time_point nextRequestAt{};
    std::atomic<double> workerMs = 0;
    bool slowRequestLogged = false;
    std::array<winrt::com_ptr<ID3DBlob>, kShaderCount> code;
    std::array<winrt::com_ptr<ID3D11ComputeShader>, kShaderCount> shaders;
    winrt::com_ptr<ID3D11Buffer> sizes;
    winrt::com_ptr<ID3D11Buffer> trackSizes;
    winrt::com_ptr<ID3D11Texture2D> liveCopy;
    winrt::com_ptr<ID3D11ShaderResourceView> liveView;
    std::array<Surface, 2> luma;
    std::array<Surface, 2> quarter;
    std::array<Surface, 2> anchorMaps;
    std::array<Surface, 2> pendingMaps;
    size_t key = 0;
    bool tracking = false;
    FrameStatistics::Clock::time_point keyAt{};
    bool pending = false;
    bool resetPending = false;
    Surface coarse;
    Surface flow;
    Surface anchor;
    Surface settled;
    Surface warped;
    UINT workWidth = 0;
    UINT workHeight = 0;
    bool anchored = false;
    std::optional<int64_t> lastTracked;
    winrt::com_ptr<ID3D11Texture2D> frameCopy;
    winrt::com_ptr<ID3D11ShaderResourceView> frameView;

    winrt::com_ptr<ID3D11Texture2D> input;
    winrt::com_ptr<ID3D11UnorderedAccessView> inputTarget;
    winrt::com_ptr<ID3D11Texture2D> guide;
    winrt::com_ptr<ID3D11UnorderedAccessView> guideTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> guideView;
    winrt::com_ptr<ID3D11Texture2D> output;
    winrt::com_ptr<ID3D11ShaderResourceView> outputView;
    winrt::com_ptr<ID3D11Buffer> keys;
    winrt::com_ptr<ID3D11UnorderedAccessView> keysTarget;
    winrt::com_ptr<ID3D11Buffer> state;
    winrt::com_ptr<ID3D11UnorderedAccessView> stateTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> stateView;
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::com_ptr<ID3D11UnorderedAccessView> textureTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> view;
    winrt::com_ptr<ID3D11Fence> fence;
    uint64_t fenceValue = 0;
    std::vector<reshade::api::effect_runtime*> runtimes;

    winrt::com_ptr<ID3D12Device> d3d12Proxy;
    winrt::com_ptr<ID3D12Device> d3d12;
    winrt::com_ptr<ID3D12Fence> d3d12Fence;
    HANDLE fenceHandle = nullptr;
    winrt::com_ptr<ID3D12CommandAllocator> allocator;
    winrt::com_ptr<ID3D12GraphicsCommandList> list;

    std::thread worker;
    HANDLE request = nullptr;
    std::atomic<bool> stopping = false;
    std::atomic<bool> busy = false;
    std::atomic<bool> done = false;
    std::atomic<bool> failed = false;
    uint64_t inputValue = 0;
    unsigned version = 0;
    HANDLE inputHandle = nullptr;
    HANDLE outputHandle = nullptr;
    int modelWidth = 0;
    int modelHeight = 0;
    bool halfInput = false;
    bool halfOutput = false;
    bool distanceOutput = false;
};
Depth& d = *new Depth();

std::mutex nativeMutex;
std::vector<ID3D12Device*> nativeDevices;

void Bind(reshade::api::effect_runtime* runtime, ID3D11ShaderResourceView* view)
{
    const reshade::api::resource_view handle{ reinterpret_cast<uint64_t>(view) };
    runtime->update_texture_bindings("DEPTH", handle, handle);
    runtime->enumerate_uniform_variables(nullptr, [ready = view != nullptr](reshade::api::effect_runtime* runtime, reshade::api::effect_uniform_variable variable) {
        char source[32];
        if (runtime->get_annotation_string_from_uniform_variable(variable, "source", source) && std::strcmp(source, "bufready_depth") == 0)
            runtime->set_uniform_value_bool(variable, ready);
    });
}

void OnInitRuntime(reshade::api::effect_runtime* runtime)
{
    d.runtimes.push_back(runtime);
    if (d.view)
        Bind(runtime, d.view.get());
}

void OnDestroyRuntime(reshade::api::effect_runtime* runtime)
{
    d.runtimes.erase(std::remove(d.runtimes.begin(), d.runtimes.end(), runtime), d.runtimes.end());
}

void OnReloadedEffects(reshade::api::effect_runtime* runtime)
{
    if (d.view)
        Bind(runtime, d.view.get());
}

void OnInitDevice(reshade::api::device* device)
{
    if (device->get_api() != reshade::api::device_api::d3d12)
        return;
    const std::lock_guard lock(nativeMutex);
    nativeDevices.push_back(reinterpret_cast<ID3D12Device*>(device->get_native()));}

void OnDestroyDevice(reshade::api::device* device)
{
    if (device->get_api() != reshade::api::device_api::d3d12)
        return;
    const std::lock_guard lock(nativeMutex);
    std::erase(nativeDevices, reinterpret_cast<ID3D12Device*>(device->get_native()));
}

void CreateD3D12()
{
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(d.d3d12Proxy.put()))))
        throw std::runtime_error("DirectX 12 is unavailable on this GPU.");
    const LUID adapter = d.d3d12Proxy->GetAdapterLuid();
    {
        const std::lock_guard lock(nativeMutex);
        for (ID3D12Device* native : nativeDevices)
            if (const LUID luid = native->GetAdapterLuid(); luid.LowPart == adapter.LowPart && luid.HighPart == adapter.HighPart)
                d.d3d12.copy_from(native);
    }
    if (!d.d3d12)
        d.d3d12 = d.d3d12Proxy;

    winrt::check_hresult(d.d3d12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(d.d3d12Fence.put())));
    winrt::check_hresult(d.d3d12->CreateSharedHandle(d.d3d12Fence.get(), nullptr, GENERIC_ALL, nullptr, &d.fenceHandle));
    winrt::check_hresult(d.d3d12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(d.allocator.put())));
    winrt::check_hresult(d.d3d12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, d.allocator.get(), nullptr, IID_PPV_ARGS(d.list.put())));
    winrt::check_hresult(d.list->Close());
}

void ReleaseD3D12()
{
    d.list = nullptr;
    d.allocator = nullptr;
    d.d3d12Fence = nullptr;
    if (d.fenceHandle)
        CloseHandle(std::exchange(d.fenceHandle, nullptr));
    d.d3d12 = nullptr;
    d.d3d12Proxy = nullptr;
}

bool D3D12Removed()
{
    return d.d3d12 && FAILED(d.d3d12->GetDeviceRemovedReason());
}

winrt::com_ptr<ID3D12Resource> CreateD3D12Buffer(UINT64 size)
{
    const D3D12_HEAP_PROPERTIES heap{ D3D12_HEAP_TYPE_DEFAULT };
    D3D12_RESOURCE_DESC desc{};
    desc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
    desc.Width = size;
    desc.Height = 1;
    desc.DepthOrArraySize = 1;
    desc.MipLevels = 1;
    desc.SampleDesc = { 1, 0 };
    desc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
    desc.Flags = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    winrt::com_ptr<ID3D12Resource> buffer;
    winrt::check_hresult(d.d3d12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(buffer.put())));
    return buffer;
}

void Copy(ID3D12CommandQueue* queue, ID3D12Resource* texture, ID3D12Resource* buffer, bool toBuffer)
{
    const D3D12_RESOURCE_DESC desc = texture->GetDesc();
    D3D12_TEXTURE_COPY_LOCATION textureLocation{ texture, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX };
    textureLocation.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION bufferLocation{ buffer, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT };
    bufferLocation.PlacedFootprint = { 0, { desc.Format, static_cast<UINT>(desc.Width), desc.Height, 1, kRowBytes } };
    winrt::check_hresult(d.list->Reset(d.allocator.get(), nullptr));
    if (toBuffer)
        d.list->CopyTextureRegion(&bufferLocation, 0, 0, 0, &textureLocation, nullptr);
    else
    {
        d.list->CopyTextureRegion(&textureLocation, 0, 0, 0, &bufferLocation, nullptr);
        D3D12_RESOURCE_BARRIER barrier{ D3D12_RESOURCE_BARRIER_TYPE_TRANSITION };
        barrier.Transition = { texture, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON };
        d.list->ResourceBarrier(1, &barrier);
    }
    winrt::check_hresult(d.list->Close());
    ID3D12CommandList* lists[] = { d.list.get() };
    queue->ExecuteCommandLists(1, lists);
}

void Worker()
{
    InitThreadLog();
    Log(LogLevel::Info, L"Depth worker started.");
    winrt::com_ptr<ID3D12Resource> input, output, inputBuffer, outputBuffer;
    std::unique_ptr<DepthModel> model;
    int loadedWidth = 0, loadedHeight = 0;
    unsigned boundVersion = 0;
    for (;;)
    {
        WaitForSingleObject(d.request, INFINITE);
        if (d.stopping)
        {
            if (d.inputValue && d.d3d12Fence && d.d3d12Fence->GetCompletedValue() < d.inputValue + 1)
                if (const HANDLE finished = CreateEventW(nullptr, FALSE, FALSE, nullptr))
                {
                    if (SUCCEEDED(d.d3d12Fence->SetEventOnCompletion(d.inputValue + 1, finished)))
                        WaitForSingleObject(finished, 1000);
                    CloseHandle(finished);
                }
            return;
        }
        try
        {
            const auto started = FrameStatistics::Clock::now();
            if (!d.inputValue && d.d3d12 && (!d.enabled || !d.list || D3D12Removed()))
            {
                Log(LogLevel::Info, L"Releasing the depth D3D12 device: removed_reason=0x%08X, depth_enabled=%d.",
                    static_cast<unsigned>(d.d3d12->GetDeviceRemovedReason()), d.enabled);
                model.reset();
                input = output = inputBuffer = outputBuffer = nullptr;
                loadedWidth = loadedHeight = 0;
                ReleaseD3D12();
            }
            if (!d.enabled)
            {
                d.done = true;
                continue;
            }
            if (!d.d3d12)
                CreateD3D12();
            if (d.width != loadedWidth || d.height != loadedHeight)
            {
                model.reset();
                boundVersion = 0;
                model = std::make_unique<DepthModel>();
                model->Load(d.directory, ModelFile(), d.width, d.height, d.d3d12.get());
                loadedWidth = d.modelWidth = d.width;
                loadedHeight = d.modelHeight = d.height;
                Log(LogLevel::Info, L"Depth model loaded: %ls%ls, input=%dx%d, half_input=%d, half_output=%d.", d.directory.c_str(),
                    ModelFile().c_str(), d.width, d.height, model->HalfInput(), model->HalfOutput());
                d.halfInput = model->HalfInput();
                d.halfOutput = model->HalfOutput();
                d.distanceOutput = model->DistanceOutput();
                Report(LogLevel::Ok, L"Depth estimation ready (%dx%d)", d.width, d.height);
            }
            if (d.inputValue)
            {
                if (boundVersion != d.version)
                {
                    winrt::check_hresult(d.d3d12->OpenSharedHandle(d.inputHandle, IID_PPV_ARGS(input.put())));
                    winrt::check_hresult(d.d3d12->OpenSharedHandle(d.outputHandle, IID_PPV_ARGS(output.put())));
                    inputBuffer = CreateD3D12Buffer(input->GetDesc().Height * kRowBytes);
                    outputBuffer = CreateD3D12Buffer(output->GetDesc().Height * kRowBytes);
                    model->Bind(inputBuffer.get(), outputBuffer.get());
                    boundVersion = d.version;
                }
                ID3D12CommandQueue* queue = model->Queue();
                winrt::check_hresult(queue->Wait(d.d3d12Fence.get(), d.inputValue));
                winrt::check_hresult(d.allocator->Reset());
                Copy(queue, input.get(), inputBuffer.get(), true);
                model->Run();
                Copy(queue, output.get(), outputBuffer.get(), false);
                winrt::check_hresult(queue->Signal(d.d3d12Fence.get(), d.inputValue + 1));
            }
            d.workerMs = std::chrono::duration<double, std::milli>(FrameStatistics::Clock::now() - started).count();
        }
        catch (const std::exception& e)
        {
            if (D3D12Removed())
                Log(LogLevel::Info, L"Depth run failed on a removed D3D12 device: %hs", e.what());
            else
            {
                Log(LogLevel::Error, L"Depth estimation stopped: %hs", e.what());
                LogStackTrace();
                d.failed = true;
            }
        }
        catch (const winrt::hresult_error& e)
        {
            if (D3D12Removed())
                Log(LogLevel::Info, L"Depth run failed on a removed D3D12 device: %ls (0x%08X)", e.message().c_str(),
                    static_cast<unsigned>(e.code()));
            else
            {
                Log(LogLevel::Error, L"Depth estimation stopped: %ls (0x%08X)", e.message().c_str(), static_cast<unsigned>(e.code()));
                Log(LogLevel::Info, L"Depth worker state: model=%dx%d, requested=%dx%d, input_fence=%llu, resource_version=%u.",
                    loadedWidth, loadedHeight, d.width, d.height, d.inputValue, d.version);
                LogStackTrace();
                d.failed = true;
            }
        }
        d.done = true;
    }
}

void Request(uint64_t value)
{
    d.inputValue = value;
    d.requestedAt = FrameStatistics::Clock::now();
    d.slowRequestLogged = false;
    d.busy = true;
    SetEvent(d.request);
}

void CreateShaders()
{
    for (size_t i = 0; i < kShaderCount; ++i)
        winrt::check_hresult(g.device->CreateComputeShader(d.code[i]->GetBufferPointer(), d.code[i]->GetBufferSize(), nullptr, d.shaders[i].put()));

    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = 32;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    winrt::check_hresult(g.device->CreateBuffer(&desc, nullptr, d.sizes.put()));
    desc.ByteWidth = 16;
    winrt::check_hresult(g.device->CreateBuffer(&desc, nullptr, d.trackSizes.put()));
}

void CompileShaders()
{
    for (size_t i = 0; i < kShaderCount; ++i)
    {
        const bool track = i >= kShrink;
        const char* source = track ? kTrackShaders : kShaders;
        const size_t length = track ? sizeof(kTrackShaders) - 1 : sizeof(kShaders) - 1;
        const char* entry = track ? kTrackEntryPoints[i - kShrink] : kEntryPoints[i];
        winrt::com_ptr<ID3DBlob> errors;
        const HRESULT hr = D3DCompile(source, length, "depth", nullptr, nullptr, entry, "cs_5_0", 0, 0, d.code[i].put(), errors.put());
        if (FAILED(hr))
            throw std::runtime_error(errors ? static_cast<const char*>(errors->GetBufferPointer()) : "compute shaders are unavailable on this GPU");
    }
    CreateShaders();
}

void Dispatch(size_t shader, std::array<ID3D11ShaderResourceView*, 10> views, std::array<ID3D11UnorderedAccessView*, 8> targets, UINT x, UINT y)
{
    ID3D11Buffer* constants[] = { shader >= kShrink ? d.trackSizes.get() : d.sizes.get() };
    g.context->CSSetShader(d.shaders[shader].get(), nullptr, 0);
    g.context->CSSetConstantBuffers(0, 1, constants);
    g.context->CSSetShaderResources(0, static_cast<UINT>(views.size()), views.data());
    g.context->CSSetUnorderedAccessViews(0, static_cast<UINT>(targets.size()), targets.data(), nullptr);
    g.context->Dispatch(x, y, 1);
    views.fill(nullptr);
    targets.fill(nullptr);
    g.context->CSSetShaderResources(0, static_cast<UINT>(views.size()), views.data());
    g.context->CSSetUnorderedAccessViews(0, static_cast<UINT>(targets.size()), targets.data(), nullptr);
}

void ReleaseShared()
{
    for (auto* runtime : d.runtimes)
        Bind(runtime, nullptr);
    d.view = nullptr;
    d.textureTarget = nullptr;
    d.texture = nullptr;
    for (Surface* surface : { &d.luma[0], &d.luma[1], &d.quarter[0], &d.quarter[1], &d.anchorMaps[0], &d.anchorMaps[1], &d.pendingMaps[0],
                              &d.pendingMaps[1], &d.coarse, &d.flow, &d.anchor, &d.settled, &d.warped })
        *surface = {};
    d.anchored = false;
    d.tracking = false;
    d.pending = false;
    d.resetPending = false;
    d.stateView = nullptr;
    d.stateTarget = nullptr;
    d.state = nullptr;
    d.keysTarget = nullptr;
    d.keys = nullptr;
    d.outputView = nullptr;
    d.output = nullptr;
    d.guideView = nullptr;
    d.guideTarget = nullptr;
    d.guide = nullptr;
    d.inputTarget = nullptr;
    d.input = nullptr;
    for (HANDLE* handle : { &d.inputHandle, &d.outputHandle })
        if (*handle)
            CloseHandle(std::exchange(*handle, nullptr));
}

void ReleaseResources()
{
    if (d.busy && d.inputValue && d.d3d12Fence->GetCompletedValue() < d.inputValue)
        d.d3d12Fence->Signal(d.inputValue);
    ReleaseShared();
    d.fence = nullptr;
    d.frameView = nullptr;
    d.frameCopy = nullptr;
    d.liveView = nullptr;
    d.liveCopy = nullptr;
    d.sizes = nullptr;
    d.trackSizes = nullptr;
    d.lastTracked.reset();
    for (auto& shader : d.shaders)
        shader = nullptr;
    d.sourceWidth = 0;
    d.sourceHeight = 0;
    d.lastInput.reset();
}

void Stop()
{
    ReleaseResources();
    if (DepthSize() == kDefaultDepthSize)
    {
        d.enabled = false;
        Request(0);
        return;
    }
    SetDepthSize(kDefaultDepthSize);
    Report(LogLevel::Warning, L"Depth estimation failed, so Depth detail went back to High.");
}

void Resize(const D3D11_TEXTURE2D_DESC& frame)
{
    const bool landscape = frame.Width >= frame.Height;
    const float ratio = landscape ? static_cast<float>(frame.Height) / frame.Width : static_cast<float>(frame.Width) / frame.Height;
    d.size = DepthSize();
    const int shortSide = std::max(14, static_cast<int>(std::lround(d.size * ratio / 14)) * 14);
    d.width = landscape ? d.size : shortSide;
    d.height = landscape ? shortSide : d.size;
    d.sourceWidth = frame.Width;
    d.sourceHeight = frame.Height;
    d.workWidth = std::max(1u, (frame.Width + 3) / 4);
    d.workHeight = std::max(1u, (frame.Height + 3) / 4);
    d.lastInput.reset();
    d.lastTracked.reset();
    Log(LogLevel::Info, L"Depth resize: source=%ux%u, model=%dx%d, detail=%d.", frame.Width, frame.Height, d.width, d.height, d.size);
    ReleaseShared();

    D3D11_TEXTURE2D_DESC copy = frame;
    copy.MipLevels = 1;
    copy.ArraySize = 1;
    copy.SampleDesc = { 1, 0 };
    copy.Usage = D3D11_USAGE_DEFAULT;
    copy.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    copy.CPUAccessFlags = 0;
    copy.MiscFlags = 0;
    d.frameCopy = nullptr;
    d.frameView = nullptr;
    winrt::check_hresult(g.device->CreateTexture2D(&copy, nullptr, d.frameCopy.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.frameCopy.get(), nullptr, d.frameView.put()));
    d.liveCopy = nullptr;
    d.liveView = nullptr;
    winrt::check_hresult(g.device->CreateTexture2D(&copy, nullptr, d.liveCopy.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.liveCopy.get(), nullptr, d.liveView.put()));
}

void CreateSurface(UINT width, UINT height, DXGI_FORMAT format, Surface& surface)
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc = { 1, 0 };
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, surface.texture.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(surface.texture.get(), nullptr, surface.target.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(surface.texture.get(), nullptr, surface.view.put()));
}

void CreateSharedTexture(size_t count, bool half, UINT bind, winrt::com_ptr<ID3D11Texture2D>& texture, HANDLE& handle)
{
    const UINT width = kRowBytes / (half ? 2 : 4);
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = static_cast<UINT>((count + width - 1) / width);
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = half ? DXGI_FORMAT_R16_FLOAT : DXGI_FORMAT_R32_FLOAT;
    desc.SampleDesc = { 1, 0 };
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = bind;
    desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, texture.put()));
    winrt::check_hresult(texture.as<IDXGIResource1>()->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle));
}

void CreateShared()
{
    const size_t pixels = static_cast<size_t>(d.width) * d.height;
    CreateSharedTexture(3 * pixels, d.halfInput, D3D11_BIND_UNORDERED_ACCESS, d.input, d.inputHandle);
    CreateSharedTexture(pixels, d.halfOutput, D3D11_BIND_SHADER_RESOURCE, d.output, d.outputHandle);
    ++d.version;
    winrt::check_hresult(g.device->CreateUnorderedAccessView(d.input.get(), nullptr, d.inputTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.output.get(), nullptr, d.outputView.put()));
    if (!d.fence)
        winrt::check_hresult(g.device.as<ID3D11Device5>()->OpenSharedFence(d.fenceHandle, IID_PPV_ARGS(d.fence.put())));

    D3D11_BUFFER_DESC buffer{};
    buffer.ByteWidth = 4 * 260;
    buffer.Usage = D3D11_USAGE_DEFAULT;
    buffer.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    buffer.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
    winrt::check_hresult(g.device->CreateBuffer(&buffer, nullptr, d.keys.put()));
    D3D11_UNORDERED_ACCESS_VIEW_DESC raw{};
    raw.Format = DXGI_FORMAT_R32_TYPELESS;
    raw.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    raw.Buffer.NumElements = 260;
    raw.Buffer.Flags = D3D11_BUFFER_UAV_FLAG_RAW;
    winrt::check_hresult(g.device->CreateUnorderedAccessView(d.keys.get(), &raw, d.keysTarget.put()));

    buffer.ByteWidth = 16;
    buffer.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    buffer.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    buffer.StructureByteStride = 16;
    winrt::check_hresult(g.device->CreateBuffer(&buffer, nullptr, d.state.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(d.state.get(), nullptr, d.stateTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.state.get(), nullptr, d.stateView.put()));
    const UINT zero[4] = {};
    g.context->ClearUnorderedAccessViewUint(d.stateTarget.get(), zero);

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = d.width;
    desc.Height = d.height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc = { 1, 0 };
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, d.guide.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(d.guide.get(), nullptr, d.guideTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.guide.get(), nullptr, d.guideView.put()));

    desc.Width = d.sourceWidth;
    desc.Height = d.sourceHeight;
    desc.Format = DXGI_FORMAT_R32_FLOAT;
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, d.texture.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(d.texture.get(), nullptr, d.textureTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.texture.get(), nullptr, d.view.put()));
    const float farPlane[4] = { 1, 1, 1, 1 };
    g.context->ClearUnorderedAccessViewFloat(d.textureTarget.get(), farPlane);

    const UINT quarterWidth = (d.workWidth + 3) / 4;
    const UINT quarterHeight = (d.workHeight + 3) / 4;
    CreateSurface(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32_FLOAT, d.anchor);
    for (size_t i = 0; i < 2; ++i)
    {
        CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32_FLOAT, d.luma[i]);
        CreateSurface(quarterWidth, quarterHeight, DXGI_FORMAT_R32_FLOAT, d.quarter[i]);
        CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32G32B32A32_FLOAT, d.anchorMaps[i]);
        CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32G32B32A32_FLOAT, d.pendingMaps[i]);
    }
    CreateSurface(quarterWidth, quarterHeight, DXGI_FORMAT_R32G32_FLOAT, d.coarse);
    CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32G32B32A32_FLOAT, d.flow);
    CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32G32B32A32_FLOAT, d.settled);
    CreateSurface(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32_FLOAT, d.warped);
    g.context->ClearUnorderedAccessViewFloat(d.anchor.target.get(), farPlane);
    const UINT trackSizes[4] = { d.sourceWidth, d.sourceHeight, d.workWidth, d.workHeight };
    g.context->UpdateSubresource(d.trackSizes.get(), 0, nullptr, trackSizes, 0, 0);

    const UINT sizes[8] = { d.sourceWidth, d.sourceHeight, static_cast<UINT>(d.width), static_cast<UINT>(d.height),
                            kRowBytes / (d.halfInput ? 2 : 4), kRowBytes / (d.halfOutput ? 2 : 4), d.distanceOutput ? 1u : 0u };
    g.context->UpdateSubresource(d.sizes.get(), 0, nullptr, sizes, 0, 0);
    for (auto* runtime : d.runtimes)
        Bind(runtime, d.view.get());
}

void Publish()
{
    winrt::check_hresult(g.context.as<ID3D11DeviceContext4>()->Wait(d.fence.get(), d.inputValue + 1));
    const UINT zero[4] = {};
    g.context->ClearUnorderedAccessViewUint(d.keysTarget.get(), zero);
    const UINT pixels = static_cast<UINT>(d.width * d.height);
    Dispatch(1, { nullptr, d.outputView.get() }, { nullptr, d.keysTarget.get() }, (pixels + 255) / 256, 1);
    Dispatch(2, { nullptr, d.outputView.get() }, { nullptr, d.keysTarget.get() }, (pixels + 255) / 256, 1);
    Dispatch(3, {}, { nullptr, d.keysTarget.get(), d.stateTarget.get() }, 1, 1);
    Dispatch(4, { d.frameView.get(), d.outputView.get(), d.stateView.get(), d.guideView.get() }, { nullptr, nullptr, nullptr, d.anchor.target.get() },
             (d.sourceWidth + 15) / 16, (d.sourceHeight + 15) / 16);
    std::swap(d.anchorMaps, d.pendingMaps);
    d.anchored = d.pending;
    d.pending = false;
}

void ComposeMap(const std::array<Surface, 2>& maps, UINT groupsX, UINT groupsY)
{
    Dispatch(kCompose, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.flow.view.get(), nullptr, nullptr, maps[0].view.get() },
             { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, maps[1].target.get() }, groupsX, groupsY);
}

void Track(ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (!d.texture || !d.liveCopy || (d.lastTracked && *d.lastTracked == frameTimestamp))
        return;
    d.lastTracked = frameTimestamp;
    g.context->CopyResource(d.liveCopy.get(), frame);
    const UINT workGroupsX = (d.workWidth + 15) / 16;
    const UINT workGroupsY = (d.workHeight + 15) / 16;
    const UINT quarterGroupsX = ((d.workWidth + 3) / 4 + 15) / 16;
    const UINT quarterGroupsY = ((d.workHeight + 3) / 4 + 15) / 16;
    const Surface& luma = d.luma[d.key ^ 1];
    const Surface& keyLuma = d.luma[d.key];
    const Surface& quarter = d.quarter[d.key ^ 1];
    const Surface& keyQuarter = d.quarter[d.key];
    Dispatch(kShrink, { d.liveView.get() }, { luma.target.get() }, workGroupsX, workGroupsY);
    Dispatch(kReduce, { nullptr, luma.view.get() }, { nullptr, quarter.target.get() }, quarterGroupsX, quarterGroupsY);
    const bool composed = d.tracking;
    if (composed)
    {
        Dispatch(kMatchCoarse, { nullptr, nullptr, nullptr, quarter.view.get(), keyQuarter.view.get() }, { nullptr, nullptr, d.coarse.target.get() },
                 quarterGroupsX, quarterGroupsY);
        Dispatch(kMatchFine, { nullptr, luma.view.get(), keyLuma.view.get(), nullptr, nullptr, d.coarse.view.get() },
                 { nullptr, nullptr, nullptr, d.flow.target.get() }, workGroupsX, workGroupsY);
        Dispatch(kSettle, { nullptr, luma.view.get(), nullptr, nullptr, nullptr, nullptr, d.flow.view.get() },
                 { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.settled.target.get() }, workGroupsX, workGroupsY);
        Dispatch(kSettleWide, { nullptr, luma.view.get(), nullptr, nullptr, nullptr, nullptr, d.settled.view.get() },
                 { nullptr, nullptr, nullptr, d.flow.target.get() }, workGroupsX, workGroupsY);
        if (d.anchored)
            ComposeMap(d.anchorMaps, workGroupsX, workGroupsY);
        if (d.pending)
            ComposeMap(d.pendingMaps, workGroupsX, workGroupsY);
    }
    const auto now = FrameStatistics::Clock::now();
    const bool advance = !d.tracking || d.resetPending || now - d.keyAt >= kKeyframeInterval;
    if (advance)
    {
        d.key ^= 1;
        d.keyAt = now;
        d.tracking = true;
        if (composed)
        {
            std::swap(d.anchorMaps[0], d.anchorMaps[1]);
            std::swap(d.pendingMaps[0], d.pendingMaps[1]);
        }
    }
    if (d.resetPending)
    {
        Dispatch(kIdentity, {}, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.pendingMaps[0].target.get() }, workGroupsX, workGroupsY);
        d.resetPending = false;
        d.pending = true;
    }
    if (!d.anchored || !composed)
        return;
    const Surface& map = d.anchorMaps[advance ? 0 : 1];
    const UINT sourceGroupsX = (d.sourceWidth + 15) / 16;
    const UINT sourceGroupsY = (d.sourceHeight + 15) / 16;
    Dispatch(kWarp, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.anchor.view.get(), nullptr, map.view.get() },
             { nullptr, nullptr, nullptr, nullptr, nullptr, d.warped.target.get() }, sourceGroupsX, sourceGroupsY);
    Dispatch(kRefine, { d.liveView.get(), nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.warped.view.get() },
             { nullptr, nullptr, nullptr, nullptr, d.textureTarget.get() }, sourceGroupsX, sourceGroupsY);
}
}

bool InitDepth()
{
    d.directory = ExeDirectory();
    if (GetFileAttributesW((d.directory + ModelFile()).c_str()) == INVALID_FILE_ATTRIBUTES)
    {
        Log(LogLevel::Info, L"Depth model not found: %ls%ls.", d.directory.c_str(), ModelFile().c_str());
        return false;
    }
    if (!AddonRegistered())
    {
        Log(LogLevel::Warning, L"Depth estimation is off because ReShade did not load the Unishade add-on.");
        return false;
    }
    static bool deviceEvents = false;
    if (!deviceEvents)
    {
        deviceEvents = true;
        reshade::register_event<reshade::addon_event::init_device>(OnInitDevice);
        reshade::register_event<reshade::addon_event::destroy_device>(OnDestroyDevice);
    }
    reshade::register_event<reshade::addon_event::init_effect_runtime>(OnInitRuntime);
    reshade::register_event<reshade::addon_event::destroy_effect_runtime>(OnDestroyRuntime);
    reshade::register_event<reshade::addon_event::reshade_reloaded_effects>(OnReloadedEffects);

    try
    {
        CompileShaders();
    }
    catch (const std::exception& e)
    {
        Log(LogLevel::Error, L"Depth estimation is off: %hs", e.what());
        LogStackTrace();
        return false;
    }
    d.request = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    d.worker = std::thread(Worker);
    d.enabled = true;
    Log(LogLevel::Info, L"Depth estimation initialized: model=%ls%ls, depth_size=%d.", d.directory.c_str(), ModelFile().c_str(), DepthSize());
    return true;
}

namespace
{
void Schedule(ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (d.busy)
    {
        if (!d.slowRequestLogged && FrameStatistics::Clock::now() - d.requestedAt >= std::chrono::seconds(2))
        {
            d.slowRequestLogged = true;
            LogDepthDiagnostics();
        }
        if (!d.done)
            return;
        if (d.failed)
        {
            d.done = false;
            d.busy = false;
            d.failed = false;
            Stop();
            return;
        }
        if (d.inputValue && d.d3d12Fence->GetCompletedValue() < d.inputValue + 1)
            return;
        d.done = false;
        d.busy = false;
        const auto completedAt = FrameStatistics::Clock::now();
        d.nextRequestAt = std::max(d.requestedAt + kMinDepthInterval, completedAt);
        if (d.inputValue && d.texture && !D3D12Removed())
        {
            Publish();
            g.frameStatistics.RecordDepth(std::chrono::duration<double, std::milli>(FrameStatistics::Clock::now() - d.requestedAt).count(),
                                          d.workerMs.load());
        }
    }

    if (D3D12Removed())
    {
        ReleaseShared();
        d.fence = nullptr;
        d.modelWidth = d.modelHeight = 0;
    }

    if (!d.shaders[0])
        CreateShaders();
    D3D11_TEXTURE2D_DESC desc{};
    frame->GetDesc(&desc);
    if (desc.Width != d.sourceWidth || desc.Height != d.sourceHeight || d.size != DepthSize())
        Resize(desc);
    if (d.lastInput && *d.lastInput == frameTimestamp)
        return;
    if (d.modelWidth != d.width || d.modelHeight != d.height)
    {
        Request(0);
        return;
    }
    if (FrameStatistics::Clock::now() < d.nextRequestAt)
        return;
    if (!d.texture)
        CreateShared();

    g.context->CopyResource(d.frameCopy.get(), frame);
    Dispatch(0, { d.frameView.get() }, { d.inputTarget.get(), nullptr, nullptr, nullptr, d.guideTarget.get() }, (d.width + 15) / 16, (d.height + 15) / 16);
    d.fenceValue += 2;
    winrt::check_hresult(g.context.as<ID3D11DeviceContext4>()->Signal(d.fence.get(), d.fenceValue - 1));
    Request(d.fenceValue - 1);
    d.resetPending = true;
    d.lastInput = frameTimestamp;
}
}

void UpdateDepth(ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (!d.enabled)
        return;

    try
    {
        Schedule(frame, frameTimestamp);
        if (d.enabled)
            Track(frame, frameTimestamp);
    }
    catch (const winrt::hresult_error& e)
    {
        if (DeviceLost(e.code()))
            throw;
        Log(LogLevel::Error, L"Depth estimation stopped: %ls (0x%08X)", e.message().c_str(), static_cast<unsigned>(e.code()));
        Stop();
    }
}

bool DepthEnabled()
{
    return d.enabled;
}

void ResetDepthInput()
{
    d.lastInput.reset();
}

void LogDepthDiagnostics()
{
    const double pendingMs = d.busy ? std::chrono::duration<double, std::milli>(FrameStatistics::Clock::now() - d.requestedAt).count() : 0;
    const bool fenceReady = !d.busy || d.done || d.inputValue;
    Log(LogLevel::Info, L"Depth state: enabled=%d, busy=%d, worker_done=%d, pending_ms=%.2f, worker_submit_ms=%.2f, "
                       L"input_fence=%llu, completed_fence=%llu, source=%ux%u, requested_model=%dx%d, resource_version=%u.",
        d.enabled, d.busy.load(), d.done.load(), pendingMs, d.workerMs.load(), d.inputValue,
        fenceReady && d.d3d12Fence ? d.d3d12Fence->GetCompletedValue() : 0, d.sourceWidth, d.sourceHeight, d.width, d.height, d.version);
}

void ReleaseDepthDevice()
{
    ReleaseResources();
}

void ShutdownDepth()
{
    if (d.worker.joinable())
    {
        d.stopping = true;
        SetEvent(d.request);
        d.worker.join();
    }
    ReleaseD3D12();
    for (HANDLE handle : { d.inputHandle, d.outputHandle, d.request })
        if (handle)
            CloseHandle(handle);
    d.inputHandle = d.outputHandle = d.request = nullptr;
    reshade::unregister_event<reshade::addon_event::init_effect_runtime>(OnInitRuntime);
    reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(OnDestroyRuntime);
    reshade::unregister_event<reshade::addon_event::reshade_reloaded_effects>(OnReloadedEffects);
    for (auto* runtime : d.runtimes)
        Bind(runtime, nullptr);
    d.runtimes.clear();
    d.enabled = false;
    d.stopping = false;
    d.busy = false;
    d.done = false;
    d.failed = false;
    d.lastInput.reset();
    d.lastTracked.reset();
    d.anchored = false;
    d.tracking = false;
    d.pending = false;
    d.resetPending = false;
    d.inputValue = 0;
    d.version = 0;
    d.slowRequestLogged = false;
    d.nextRequestAt = {};
    d.width = d.height = d.size = 0;
    d.modelWidth = d.modelHeight = 0;
    d.distanceOutput = false;
}

void AdoptDepthRuntime(reshade::api::effect_runtime* runtime)
{
    if (d.enabled && std::find(d.runtimes.begin(), d.runtimes.end(), runtime) == d.runtimes.end())
        OnInitRuntime(runtime);
}
