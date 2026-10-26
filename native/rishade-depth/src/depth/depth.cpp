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
#include <dxgi1_4.h>

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
static const float PeakSmoothing = 0.08;
static const float FarPlane = 1000.0;
static const float Nearest = 0.01;
static const float SpatialSigma = 1.0;
static const float ColorSigma = 0.1;
static const uint Bins = 256;
static const float ReferenceShare = 0.1;
static const float SkyStart = 0.03;
static const float SkyRange = 0.05;
static const float EdgeStart = 0.12;
static const float EdgeRange = 0.12;

float3 AverageSource(int2 p)
{
    int2 limit = int2(SourceSize) - 1;
    float3 sum = 0;
    for (int dy = -1; dy <= 1; ++dy)
        for (int dx = -1; dx <= 1; ++dx)
            sum += Source.Load(int3(clamp(p + int2(dx, dy), 0, limit), 0)).rgb;
    return sum / 9;
}

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
    float3 color = AverageSource(int2(id.xy));
    float2 position = (id.xy + 0.5) * ModelSize / SourceSize - 0.5;
    int2 corner = int2(floor(position)) - 1;
    float2 f = position - floor(position);
    float4 wx = float4(((-0.5 * f.x + 1.0) * f.x - 0.5) * f.x, (1.5 * f.x - 2.5) * f.x * f.x + 1.0, ((-1.5 * f.x + 2.0) * f.x + 0.5) * f.x, (0.5 * f.x - 0.5) * f.x * f.x);
    float4 wy = float4(((-0.5 * f.y + 1.0) * f.y - 0.5) * f.y, (1.5 * f.y - 2.5) * f.y * f.y + 1.0, ((-1.5 * f.y + 2.0) * f.y + 0.5) * f.y, (0.5 * f.y - 0.5) * f.y * f.y);
    float values[16];
    float spatial[16];
    float tones[16];
    float mean = 0;
    float slopeX = 0;
    float slopeY = 0;
    float smooth = 0;
    float lowest = 1e9;
    float highest = -1e9;
    for (uint k = 0; k < 16; ++k)
    {
        int2 cell = int2(k % 4, k / 4);
        int2 texel = corner + cell;
        float2 offset = texel - position;
        texel = clamp(texel, 0, int2(ModelSize) - 1);
        float3 difference = Guide.Load(int3(texel, 0)).rgb - color;
        values[k] = Load(texel.y * ModelSize.x + texel.x);
        spatial[k] = exp(-dot(offset, offset) / (2 * SpatialSigma * SpatialSigma));
        tones[k] = exp(-dot(difference, difference) / (2 * ColorSigma * ColorSigma)) + 1e-3;
        float2 centered = cell - 1.5;
        mean += values[k];
        slopeX += values[k] * centered.x;
        slopeY += values[k] * centered.y;
        smooth += wx[cell.x] * wy[cell.y] * values[k];
        lowest = min(lowest, values[k]);
        highest = max(highest, values[k]);
    }
    mean /= 16;
    slopeX /= 20;
    slopeY /= 20;
    float residual = 0;
    for (uint r = 0; r < 16; ++r)
    {
        float2 centered = float2(r % 4, r / 4) - 1.5;
        residual = max(residual, abs(values[r] - (mean + slopeX * centered.x + slopeY * centered.y)));
    }
    float edge = saturate((residual / max(abs(mean), 1e-6) - EdgeStart) / EdgeRange);
    smooth = clamp(smooth, lowest, highest);
    float split = 0.5 * (lowest + highest);
    float2 sides = 0;
    float2 sideWeights = 0;
    for (uint j = 0; j < 16; ++j)
    {
        float match = spatial[j] * tones[j] * tones[j];
        if (values[j] > split)
        {
            sides.x += match * values[j];
            sideWeights.x += match;
        }
        else
        {
            sides.y += match * values[j];
            sideWeights.y += match;
        }
    }
    float nearShare = sideWeights.x * sideWeights.x / max(sideWeights.x * sideWeights.x + sideWeights.y * sideWeights.y, 1e-12);
    float snapped = lerp(sides.y / max(sideWeights.y, 1e-12), sides.x / max(sideWeights.x, 1e-12), nearShare);
    if (sideWeights.x <= 0)
        snapped = sides.y / max(sideWeights.y, 1e-12);
    if (sideWeights.y <= 0)
        snapped = sides.x / max(sideWeights.x, 1e-12);
    float value = lerp(smooth, snapped, edge);

    float closeness = value / max(Smoothed[0].x, 1e-6);
    float distance01 = Nearest / max(closeness, Nearest);
    distance01 = lerp(distance01, 1, 1 - saturate((closeness - SkyStart) / SkyRange));
    Depth[id.xy] = distance01 * FarPlane / (1 + distance01 * (FarPlane - 1));
}
)";

constexpr char kTrackShaders[] = R"(
cbuffer Track : register(b0)
{
    uint2 SourceSize;
    uint2 WorkSize;
    uint Flags;
    uint3 Padding;
};
Texture2D<float4> Color : register(t0);
Texture2D<float> Luma : register(t1);
Texture2D<float> PrevLuma : register(t2);
Texture2D<float> Quarter : register(t3);
Texture2D<float> PrevQuarter : register(t4);
Texture2D<float2> Coarse : register(t5);
Texture2D<float4> Flow : register(t6);
Texture2D<float> Anchor : register(t7);
Texture2D<float4> Warped : register(t8);
StructuredBuffer<float4> Motion : register(t9);
Texture2D<float> Mask : register(t10);
Texture2D<float> FastAnchor : register(t11);
Texture2D<float> Ratio : register(t12);
Texture2D<float> RatioBlur : register(t13);
RWTexture2D<float> LumaTarget : register(u0);
RWTexture2D<float> QuarterTarget : register(u1);
RWTexture2D<float2> CoarseTarget : register(u2);
RWTexture2D<float4> FlowTarget : register(u3);
RWTexture2D<float> Depth : register(u4);
RWTexture2D<float4> WarpedTarget : register(u5);
RWStructuredBuffer<float4> Affine : register(u6);
RWTexture2D<float> MaskTarget : register(u7);
RWByteAddressBuffer FocusMotion : register(u1);

static const int CoarseSearch = 8;
static const int Patch = 2;
static const float MotionCost = 0.002;
static const float LostMatch = 0.06;
static const float CornerScale = 400.0;
static const float OutlierDistance = 1.5;
static const float MinimumWeight = 4.0;
static const float Regularization = 1e-3;
static const float TemporalBase = 0.5;
static const float TemporalResponse = 6.0;
static const int RefineRadius = 2;
static const int RefineStride = 2;
static const float RefineColor = 0.08;
static const float RefineEdgeStart = 0.15;
static const float RefineEdgeRange = 0.15;
static const float RefineSurfaceSigma = 0.05;
static const int PlaneRadius = 4;
static const int PlaneStride = 5;
static const float PlaneColor = 0.12;
static const float PlaneAgree = 0.025;
static const float PlaneSeedAgree = 0.03;
static const float FocusHold = 0.92;
static const float FocusStillStart = 0.008;
static const float FocusStillRange = 0.03;
static const int FocusReach = 8;
static const int2 FocusRing[8] = { int2(-1, -1), int2(0, -1), int2(1, -1), int2(-1, 0), int2(1, 0), int2(-1, 1), int2(0, 1), int2(1, 1) };
static const float FocusDepthStart = 0.10;
static const float FocusDepthRange = 0.10;
static const float FocusMovedLuma = 0.02;
static const float FocusSceneStart = 0.002;
static const float FocusSceneRange = 0.008;

groupshared uint FocusMovedCount;
static const float PlaneGate = 0.3;
static const float PlaneResidualSigma = 0.04;
static const float LinearFar = 1000.0;
static const float StaleStart = 0.04;
static const float StaleRange = 0.06;
static const int ErodeRadius = 1;
static const int DilateRadius = 2;
static const uint Composed = 1;
static const uint Advance = 2;
static const uint Fused = 4;
static const uint Exact = 128;
static const uint Reset = 8;
static const uint Published = 16;
static const uint DetailRows = 2;
static const uint FastRows = 10;
static const int BlurRadius = 4;
static const int BlurStride = 2;
static const float RatioFloor = 1e-3;
static const float RatioMin = 0.25;
static const float RatioMax = 4.0;

uint2 QuarterSize()
{
    return (WorkSize + 3) / 4;
}

float2 Centre()
{
    return (float2(WorkSize) - 1) * 0.5;
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
    bool edge = bx == 0 || bx == 4 || by == 0 || by == 4;
    float confidence = saturate(corner * CornerScale) * saturate(1 - best * 12) * (edge || best > LostMatch ? 0 : 1);
    FlowTarget[id.xy] = float4(offset, best, confidence);
}

groupshared float Sums[256][13];

void FitMotion(uint thread, bool refine)
{
    float sums[13];
    for (int i = 0; i < 13; ++i)
        sums[i] = 0;
    float2 centre = Centre();
    float scale = max(WorkSize.x, WorkSize.y) * 0.5;
    float4 row0 = Affine[0];
    float4 row1 = Affine[1];
    uint columns = (WorkSize.x + 1) / 2;
    uint count = columns * ((WorkSize.y + 1) / 2);
    for (uint n = thread; n < count; n += 256)
    {
        uint2 p = uint2(n % columns, n / columns) * 2;
        float4 flow = Flow.Load(int3(p, 0));
        float weight = flow.w;
        float2 x = float2(p) - centre;
        float2 q = x - flow.xy;
        if (refine)
        {
            float2 predicted = float2(dot(row0.xyz, float3(x, 1)), dot(row1.xyz, float3(x, 1)));
            weight *= saturate(1 - distance(predicted, q) / OutlierDistance);
        }
        if (weight <= 0)
            continue;
        float3 v = float3(x / scale, 1);
        sums[0] += weight * v.x * v.x;
        sums[1] += weight * v.x * v.y;
        sums[2] += weight * v.x;
        sums[3] += weight * v.y * v.y;
        sums[4] += weight * v.y;
        sums[5] += weight;
        sums[6] += weight * v.x * q.x;
        sums[7] += weight * v.y * q.x;
        sums[8] += weight * q.x;
        sums[9] += weight * v.x * q.y;
        sums[10] += weight * v.y * q.y;
        sums[11] += weight * q.y;
        sums[12] += weight;
    }
    for (int s = 0; s < 13; ++s)
        Sums[thread][s] = sums[s];
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 128; step > 0; step >>= 1)
    {
        if (thread < step)
            for (int s2 = 0; s2 < 13; ++s2)
                Sums[thread][s2] += Sums[thread + step][s2];
        GroupMemoryBarrierWithGroupSync();
    }
    if (thread != 0)
        return;
    if (Sums[0][12] < MinimumWeight)
    {
        Affine[0] = float4(1, 0, 0, 0);
        Affine[1] = float4(0, 1, 0, 0);
        return;
    }
    float lambda = Regularization * Sums[0][5];
    float3x3 m = float3x3(Sums[0][0] + lambda, Sums[0][1], Sums[0][2],
                          Sums[0][1], Sums[0][3] + lambda, Sums[0][4],
                          Sums[0][2], Sums[0][4], Sums[0][5] + lambda);
    float3 bx = float3(Sums[0][6] + lambda * scale, Sums[0][7], Sums[0][8]);
    float3 by = float3(Sums[0][9], Sums[0][10] + lambda * scale, Sums[0][11]);
    float det = determinant(m);
    float3x3 inverse = float3x3(cross(m[1], m[2]), cross(m[2], m[0]), cross(m[0], m[1])) / det;
    float3 ax = mul(transpose(inverse), bx);
    float3 ay = mul(transpose(inverse), by);
    Affine[0] = float4(ax.x / scale, ax.y / scale, ax.z, 0);
    Affine[1] = float4(ay.x / scale, ay.y / scale, ay.z, 0);
}

[numthreads(256, 1, 1)]
void Fit(uint3 id : SV_DispatchThreadID, uint thread : SV_GroupIndex)
{
    FitMotion(thread, false);
}

[numthreads(256, 1, 1)]
void FitRefine(uint3 id : SV_DispatchThreadID, uint thread : SV_GroupIndex)
{
    FitMotion(thread, true);
}

void Combine(float4 k0, float4 k1, float4 l0, float4 l1, out float4 r0, out float4 r1)
{
    r0 = float4(k0.x * l0.x + k0.y * l1.x, k0.x * l0.y + k0.y * l1.y, k0.x * l0.z + k0.y * l1.z + k0.z, 0);
    r1 = float4(k1.x * l0.x + k1.y * l1.x, k1.x * l0.y + k1.y * l1.y, k1.x * l0.z + k1.y * l1.z + k1.z, 0);
}

void StepLane(uint rows, uint shift, float4 motion0, float4 motion1)
{
    float4 identity0 = float4(1, 0, 0, 0);
    float4 identity1 = float4(0, 1, 0, 0);
    uint flags = Flags >> shift;
    float4 anchor0 = Affine[rows];
    float4 anchor1 = Affine[rows + 1];
    float4 pending0 = Affine[rows + 4];
    float4 pending1 = Affine[rows + 5];
    if (flags & Published)
    {
        anchor0 = pending0;
        anchor1 = pending1;
    }
    float4 anchorLive0;
    float4 anchorLive1;
    float4 pendingLive0;
    float4 pendingLive1;
    Combine(anchor0, anchor1, motion0, motion1, anchorLive0, anchorLive1);
    Combine(pending0, pending1, motion0, motion1, pendingLive0, pendingLive1);
    if (Flags & Advance)
    {
        anchor0 = anchorLive0;
        anchor1 = anchorLive1;
        pending0 = pendingLive0;
        pending1 = pendingLive1;
    }
    if (flags & Reset)
    {
        float determinant = motion0.x * motion1.y - motion0.y * motion1.x;
        float4 inverse0 = float4(motion1.y, -motion0.y, 0, 0) / determinant;
        float4 inverse1 = float4(-motion1.x, motion0.x, 0, 0) / determinant;
        inverse0.z = -(inverse0.x * motion0.z + inverse0.y * motion1.z);
        inverse1.z = -(inverse1.x * motion0.z + inverse1.y * motion1.z);
        bool fresh = (Flags & Advance) != 0;
        pending0 = fresh ? identity0 : inverse0;
        pending1 = fresh ? identity1 : inverse1;
        pendingLive0 = identity0;
        pendingLive1 = identity1;
    }
    Affine[rows] = anchor0;
    Affine[rows + 1] = anchor1;
    Affine[rows + 2] = anchorLive0;
    Affine[rows + 3] = anchorLive1;
    Affine[rows + 4] = pending0;
    Affine[rows + 5] = pending1;
    Affine[rows + 6] = pendingLive0;
    Affine[rows + 7] = pendingLive1;
}

[numthreads(1, 1, 1)]
void Step(uint3 id : SV_DispatchThreadID)
{
    float4 motion0 = Flags & Composed ? Affine[0] : float4(1, 0, 0, 0);
    float4 motion1 = Flags & Composed ? Affine[1] : float4(0, 1, 0, 0);
    StepLane(DetailRows, 0, motion0, motion1);
    StepLane(FastRows, 2, motion0, motion1);
}

float2 OriginOf(float2 work, uint rows)
{
    float2 centre = Centre();
    float3 x = float3(work - centre, 1);
    return float2(dot(Motion[rows + 2].xyz, x), dot(Motion[rows + 3].xyz, x)) + centre;
}

float LumaBefore(float2 position)
{
    int2 size = int2(WorkSize);
    int2 base = int2(floor(position));
    float2 t = position - base;
    float a = PrevLuma.Load(int3(clamp(base, 0, size - 1), 0));
    float b = PrevLuma.Load(int3(clamp(base + int2(1, 0), 0, size - 1), 0));
    float c = PrevLuma.Load(int3(clamp(base + int2(0, 1), 0, size - 1), 0));
    float d = PrevLuma.Load(int3(clamp(base + int2(1, 1), 0, size - 1), 0));
    return lerp(lerp(a, b, t.x), lerp(c, d, t.x), t.y);
}

[numthreads(16, 16, 1)]
void Stale(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    float difference = abs(Luma.Load(int3(id.xy, 0)) - LumaBefore(OriginOf(float2(id.xy), DetailRows)));
    MaskTarget[id.xy] = saturate((difference - StaleStart) / StaleRange);
}

[numthreads(16, 16, 1)]
void Erode(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    int2 size = int2(WorkSize);
    float value = 1;
    for (int dy = -ErodeRadius; dy <= ErodeRadius; ++dy)
        for (int dx = -ErodeRadius; dx <= ErodeRadius; ++dx)
            value = min(value, Mask.Load(int3(clamp(int2(id.xy) + int2(dx, dy), 0, size - 1), 0)));
    MaskTarget[id.xy] = value;
}

[numthreads(16, 16, 1)]
void Dilate(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    int2 size = int2(WorkSize);
    float sum = 0;
    float value = 0;
    for (int dy = -DilateRadius; dy <= DilateRadius; ++dy)
        for (int dx = -DilateRadius; dx <= DilateRadius; ++dx)
        {
            float sample = Mask.Load(int3(clamp(int2(id.xy) + int2(dx, dy), 0, size - 1), 0));
            value = max(value, sample);
            sum += sample;
        }
    MaskTarget[id.xy] = lerp(sum / 25, value, 0.5);
}

float MaskAt(float2 position)
{
    int2 size = int2(WorkSize);
    int2 base = int2(floor(position));
    float2 t = position - base;
    float a = Mask.Load(int3(clamp(base, 0, size - 1), 0));
    float b = Mask.Load(int3(clamp(base + int2(1, 0), 0, size - 1), 0));
    float c = Mask.Load(int3(clamp(base + int2(0, 1), 0, size - 1), 0));
    float d = Mask.Load(int3(clamp(base + int2(1, 1), 0, size - 1), 0));
    return lerp(lerp(a, b, t.x), lerp(c, d, t.x), t.y);
}

float SampleDepth(Texture2D<float> source, float2 work)
{
    int2 size = int2(SourceSize);
    float2 position = (work + 0.5) * float2(SourceSize) / float2(WorkSize) - 0.5;
    int2 base = int2(floor(position));
    float2 t = position - base;
    float a = source.Load(int3(clamp(base, 0, size - 1), 0));
    float b = source.Load(int3(clamp(base + int2(1, 0), 0, size - 1), 0));
    float c = source.Load(int3(clamp(base + int2(0, 1), 0, size - 1), 0));
    float d = source.Load(int3(clamp(base + int2(1, 1), 0, size - 1), 0));
    return lerp(lerp(a, b, t.x), lerp(c, d, t.x), t.y);
}

float RatioBlurAt(float2 position)
{
    int2 size = int2(WorkSize);
    int2 base = int2(floor(position));
    float2 t = position - base;
    float a = RatioBlur.Load(int3(clamp(base, 0, size - 1), 0));
    float b = RatioBlur.Load(int3(clamp(base + int2(1, 0), 0, size - 1), 0));
    float c = RatioBlur.Load(int3(clamp(base + int2(0, 1), 0, size - 1), 0));
    float d = RatioBlur.Load(int3(clamp(base + int2(1, 1), 0, size - 1), 0));
    return lerp(lerp(a, b, t.x), lerp(c, d, t.x), t.y);
}

[numthreads(16, 16, 1)]
void Warp(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= SourceSize))
        return;
    float2 work = (id.xy + 0.5) * float2(WorkSize) / float2(SourceSize) - 0.5;
    if (Flags & Fused)
    {
        float fast = SampleDepth(FastAnchor, OriginOf(work, FastRows));
        float2 origin = OriginOf(work, DetailRows);
        float ratio = lerp(SampleDepth(Ratio, origin), RatioBlurAt(origin), MaskAt(work));
        WarpedTarget[id.xy] = float4(saturate(fast * ratio), 0, 0, 0);
    }
    else
        WarpedTarget[id.xy] = float4(SampleDepth(Anchor, Flags & Exact ? work : OriginOf(work, DetailRows)), 0, 0, 0);
}

[numthreads(16, 16, 1)]
void Pair(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= SourceSize))
        return;
    float detail = Anchor.Load(int3(id.xy, 0));
    float fast = FastAnchor.Load(int3(id.xy, 0));
    MaskTarget[id.xy] = clamp(detail / max(fast, RatioFloor), RatioMin, RatioMax);
}

[numthreads(16, 16, 1)]
void Spread(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    int2 size = int2(WorkSize);
    float sum = 0;
    float count = 0;
    for (int dy = -BlurRadius; dy <= BlurRadius; ++dy)
        for (int dx = -BlurRadius; dx <= BlurRadius; ++dx)
        {
            int2 w = clamp(int2(id.xy) + int2(dx, dy) * BlurStride, 0, size - 1);
            int2 q = min(int2((float2(w) + 0.5) * float2(SourceSize) / float2(WorkSize)), int2(SourceSize) - 1);
            sum += Ratio.Load(int3(q, 0));
            count += 1;
        }
    MaskTarget[id.xy] = sum / count;
}

float3 AverageColor(int2 p, int2 size)
{
    float3 sum = 0;
    for (int dy = -1; dy <= 1; ++dy)
        for (int dx = -1; dx <= 1; ++dx)
            sum += Color.Load(int3(clamp(p + int2(dx, dy), 0, size - 1), 0)).rgb;
    return sum / 9;
}

float Linear(float depth)
{
    return depth / max(LinearFar - depth * (LinearFar - 1), 1e-6);
}

float Encoded(float distance01)
{
    return distance01 * LinearFar / (1 + distance01 * (LinearFar - 1));
}

bool SolvePlane(float3x3 m, float3 b, out float3 plane)
{
    float det = determinant(m);
    plane = 0;
    if (abs(det) < 1e-9)
        return false;
    float3x3 mx = m;
    mx[0] = b;
    float3x3 my = m;
    my[1] = b;
    float3x3 mz = m;
    mz[2] = b;
    plane = float3(determinant(mx), determinant(my), determinant(mz)) / det;
    return true;
}

float FitSurface(int2 p, int2 size, float centre, float3 tone, float fallback)
{
    float samples[81];
    float2 offsets[81];
    float spatial[81];
    float tones[81];
    for (uint k = 0; k < 81; ++k)
    {
        int2 cell = int2(k % 9, k / 9) - PlaneRadius;
        int2 q = clamp(p + cell * PlaneStride, 0, size - 1);
        samples[k] = Linear(Warped.Load(int3(q, 0)).x);
        offsets[k] = float2(cell);
        float3 difference = Color.Load(int3(q, 0)).rgb - tone;
        spatial[k] = exp(-dot(float2(cell), float2(cell)) / (2.0 * PlaneRadius * PlaneRadius));
        tones[k] = exp(-dot(difference, difference) / (2 * PlaneColor * PlaneColor));
    }
    float3 plane = float3(fallback, 0, 0);
    for (uint iteration = 0; iteration < 2; ++iteration)
    {
        float3x3 m = 0;
        float3 b = 0;
        for (uint j = 0; j < 81; ++j)
        {
            float predicted = iteration == 0 ? centre : plane.x + plane.y * offsets[j].x + plane.z * offsets[j].y;
            float relative = (samples[j] - predicted) / max(predicted, 1e-6);
            float gate = iteration == 0 ? step(abs(relative), PlaneGate) : exp(-0.5 * relative * relative / (PlaneResidualSigma * PlaneResidualSigma));
            float agree = iteration == 0 ? PlaneSeedAgree : PlaneAgree;
            float belongs = max(tones[j], exp(-0.5 * relative * relative / (agree * agree)));
            float weight = spatial[j] * gate * belongs;
            float3 basis = float3(1, offsets[j]);
            m += weight * float3x3(basis * basis.x, basis * basis.y, basis * basis.z);
            b += weight * basis * samples[j];
        }
        float3 solved;
        if (!SolvePlane(m, b, solved))
            return fallback;
        plane = solved;
    }
    return plane.x > 0 ? plane.x : fallback;
}

bool RefinePixel(uint3 id)
{
    if (any(id.xy >= SourceSize))
        return false;
    int2 p = int2(id.xy);
    int2 size = int2(SourceSize);
    float3 centre = AverageColor(p, size);
    float values[25];
    float spatial[25];
    float tones[25];
    float mean = 0;
    float total = 0;
    float lowest = 1e9;
    float highest = -1e9;
    for (uint k = 0; k < 25; ++k)
    {
        int2 offset = int2(k % 5, k / 5) - RefineRadius;
        int2 q = clamp(p + offset * RefineStride, 0, size - 1);
        float3 difference = Color.Load(int3(q, 0)).rgb - centre;
        values[k] = Linear(Warped.Load(int3(q, 0)).x);
        spatial[k] = exp(-dot(float2(offset), float2(offset)) / 4.0);
        tones[k] = exp(-dot(difference, difference) / (2 * RefineColor * RefineColor));
        mean += spatial[k] * values[k];
        total += spatial[k];
        lowest = min(lowest, values[k]);
        highest = max(highest, values[k]);
    }
    mean /= total;
    float edge = saturate(((highest - lowest) / max(highest, 1e-6) - RefineEdgeStart) / RefineEdgeRange);
    float surface = 0;
    float surfaceWeights = 0;
    float snapped = 0;
    float snappedWeights = 0;
    for (uint j = 0; j < 25; ++j)
    {
        float relative = (values[j] - mean) / max(mean * RefineSurfaceSigma, 1e-6);
        float weight = spatial[j] * exp(-0.5 * relative * relative);
        surface += weight * values[j];
        surfaceWeights += weight;
        snapped += spatial[j] * tones[j] * values[j];
        snappedWeights += spatial[j] * tones[j];
    }
    float fitted = FitSurface(p, size, values[12], centre, surface / max(surfaceWeights, 1e-6));
    float current = Encoded(lerp(fitted, snapped / max(snappedWeights, 1e-6), edge));
    float previous = Depth[id.xy];
    float change = abs(current - previous) / max(max(current, previous), 1e-4);
    if (Flags & Exact)
    {
        const float3 weights = float3(0.299, 0.587, 0.114);
        float luma = dot(centre, weights);
        LumaTarget[id.xy] = luma;
        float history = Luma.Load(int3(p, 0));
        float motion = abs(luma - history);
        for (uint n = 0; n < 8; ++n)
        {
            int2 q = clamp(p + FocusRing[n] * FocusReach, 0, size - 1);
            float before = Luma.Load(int3(q, 0));
            history = min(history, before);
            motion = max(motion, abs(dot(AverageColor(q, size), weights) - before));
        }
        float colorStill = 1 - saturate((motion - FocusStillStart) / FocusStillRange);
        float depthStill = 1 - saturate((change - FocusDepthStart) / FocusDepthRange);
        float moving = FocusMotion.Load(16 * (Padding.x ^ 1)) / float(SourceSize.x * SourceSize.y);
        float sceneStill = 1 - saturate((moving - FocusSceneStart) / FocusSceneRange);
        float stillness = history < 0 ? 0 : colorStill * depthStill * sceneStill;
        Depth[id.xy] = lerp(current, previous, FocusHold * stillness);
        return abs(luma - Luma.Load(int3(p, 0))) > FocusMovedLuma;
    }
    Depth[id.xy] = lerp(previous, current, saturate(TemporalBase + change * TemporalResponse));
    return false;
}

[numthreads(16, 16, 1)]
void Refine(uint3 id : SV_DispatchThreadID, uint index : SV_GroupIndex)
{
    if (index == 0)
        FocusMovedCount = 0;
    GroupMemoryBarrierWithGroupSync();
    if (RefinePixel(id))
        InterlockedAdd(FocusMovedCount, 1);
    GroupMemoryBarrierWithGroupSync();
    if (index == 0 && (Flags & Exact) && FocusMovedCount > 0)
        FocusMotion.InterlockedAdd(16 * Padding.x, FocusMovedCount);
}
)";

constexpr const char* kEntryPoints[] = { "Preprocess", "Peak", "Histogram", "Smooth", "Upsample" };
constexpr const char* kTrackEntryPoints[] = { "Shrink", "Reduce", "MatchCoarse", "MatchFine", "Fit", "FitRefine", "Step", "Warp", "Refine", "Stale", "Erode", "Dilate", "Pair", "Spread" };
constexpr size_t kShaderCount = std::size(kEntryPoints) + std::size(kTrackEntryPoints);
constexpr size_t kShrink = std::size(kEntryPoints);
constexpr size_t kReduce = kShrink + 1;
constexpr size_t kMatchCoarse = kShrink + 2;
constexpr size_t kMatchFine = kShrink + 3;
constexpr size_t kFit = kShrink + 4;
constexpr size_t kFitRefine = kShrink + 5;
constexpr size_t kStep = kShrink + 6;
constexpr size_t kWarp = kShrink + 7;
constexpr size_t kRefine = kShrink + 8;
constexpr size_t kStale = kShrink + 9;
constexpr size_t kErode = kShrink + 10;
constexpr size_t kDilate = kShrink + 11;
constexpr size_t kPair = kShrink + 12;
constexpr size_t kSpread = kShrink + 13;
constexpr UINT kComposed = 1;
constexpr UINT kAdvance = 2;
constexpr UINT kFused = 4;
constexpr UINT kReset = 8;
constexpr UINT kPublished = 16;
constexpr UINT kExact = 128;
constexpr DWORD kExactTimeoutMs = 100;
constexpr double kExactBudgetMs = 6.0;
constexpr int kSlowFramesBeforeStepDown = 90;
constexpr UINT kLaneShift = 2;
constexpr UINT kAffineRows = 18;
constexpr size_t kDetail = 0;
constexpr size_t kFast = 1;

struct Surface
{
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::com_ptr<ID3D11UnorderedAccessView> target;
    winrt::com_ptr<ID3D11ShaderResourceView> view;
};

constexpr std::chrono::milliseconds kMinDepthInterval{ 8 };
constexpr std::chrono::milliseconds kKeyframeInterval{ 33 };

struct Lane
{
    size_t index = 0;
    std::wstring modelFile;
    std::atomic<bool> enabled = false;
    int width = 0;
    int height = 0;
    int size = 0;
    std::optional<int64_t> lastInput;
    FrameStatistics::Clock::time_point requestedAt{};
    FrameStatistics::Clock::time_point nextRequestAt{};
    std::atomic<double> workerMs = 0;
    bool slowRequestLogged = false;
    winrt::com_ptr<ID3D11Buffer> sizes;
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
    Surface anchor;
    Surface anchorLuma;
    bool anchored = false;
    bool resetPending = false;
    bool published = false;
    winrt::com_ptr<ID3D11Fence> fence;
    uint64_t fenceValue = 0;

    winrt::com_ptr<ID3D12Device> d3d12Proxy;
    winrt::com_ptr<ID3D12Device> d3d12;
    winrt::com_ptr<ID3D12Fence> d3d12Fence;
    HANDLE fenceHandle = nullptr;
    winrt::com_ptr<ID3D12CommandAllocator> allocator;
    winrt::com_ptr<ID3D12GraphicsCommandList> list;

    std::thread worker;
    HANDLE request = nullptr;
    HANDLE finished = nullptr;
    std::atomic<bool> stopping = false;
    std::atomic<bool> busy = false;
    std::atomic<bool> done = false;
    std::atomic<bool> failed = false;
    int removals = 0;
    uint64_t inputValue = 0;
    unsigned version = 0;
    HANDLE inputHandle = nullptr;
    HANDLE outputHandle = nullptr;
    int modelWidth = 0;
    int modelHeight = 0;
    bool halfInput = false;
    bool halfOutput = false;
    bool distanceOutput = false;
    bool paired = false;
};

struct Depth
{
    bool enabled = false;
    std::wstring directory;
    std::array<Lane, 2> lanes;

    UINT sourceWidth = 0;
    UINT sourceHeight = 0;
    LUID adapter{};
    bool adapterKnown = false;
    int slowFrames = 0;
    bool background = false;
    UINT workWidth = 0;
    UINT workHeight = 0;
    std::array<winrt::com_ptr<ID3DBlob>, kShaderCount> code;
    std::array<winrt::com_ptr<ID3D11ComputeShader>, kShaderCount> shaders;
    winrt::com_ptr<ID3D11Buffer> trackSizes;
    winrt::com_ptr<ID3D11Texture2D> liveCopy;
    winrt::com_ptr<ID3D11ShaderResourceView> liveView;
    std::array<Surface, 2> luma;
    std::array<Surface, 2> quarter;
    winrt::com_ptr<ID3D11Buffer> affine;
    winrt::com_ptr<ID3D11UnorderedAccessView> affineTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> affineView;
    size_t key = 0;
    bool tracking = false;
    FrameStatistics::Clock::time_point keyAt{};
    Surface coarse;
    Surface flow;
    Surface warped;
    Surface staleRaw;
    Surface staleOpen;
    Surface paired;
    Surface ratio;
    Surface ratioBlur;
    Surface focus[2];
    size_t focusIndex = 0;
    winrt::com_ptr<ID3D11Buffer> focusMotion;
    winrt::com_ptr<ID3D11UnorderedAccessView> focusMotionTarget;
    std::array<winrt::com_ptr<ID3D11UnorderedAccessView>, 2> focusMotionSlots;
    winrt::com_ptr<ID3D11Texture2D> pairCopy;
    bool pairPending = false;
    bool pairReady = false;
    bool ratioReady = false;
    std::optional<int64_t> lastTracked;
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::com_ptr<ID3D11UnorderedAccessView> textureTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> view;
    std::vector<reshade::api::effect_runtime*> runtimes;
};
Depth& d = *new Depth();

Lane& Detail()
{
    return d.lanes[kDetail];
}

Lane& Fast()
{
    return d.lanes[kFast];
}

std::mutex nativeMutex;
std::vector<ID3D12Device*> nativeDevices;
std::mutex creationMutex;
constexpr int kMaxDeviceRemovals = 2;

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

void CreateD3D12(Lane& l)
{
    winrt::com_ptr<IDXGIAdapter1> sameAdapter;
    if (d.adapterKnown)
    {
        winrt::com_ptr<IDXGIFactory4> factory;
        if (SUCCEEDED(CreateDXGIFactory1(IID_PPV_ARGS(factory.put()))))
            factory->EnumAdapterByLuid(d.adapter, IID_PPV_ARGS(sameAdapter.put()));
    }
    {
        winrt::com_ptr<ID3D12DeviceRemovedExtendedDataSettings> dred;
        if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(dred.put()))))
        {
            dred->SetAutoBreadcrumbsEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
            dred->SetPageFaultEnablement(D3D12_DRED_ENABLEMENT_FORCED_ON);
        }
    }
    const std::lock_guard creation(creationMutex);
    std::vector<ID3D12Device*> before;
    {
        const std::lock_guard lock(nativeMutex);
        before = nativeDevices;
    }
    winrt::com_ptr<ID3D12Device> proxy;
    if (FAILED(D3D12CreateDevice(sameAdapter.get(), D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(proxy.put()))))
        throw std::runtime_error("DirectX 12 is unavailable on this GPU.");
    if (FAILED(proxy->GetDeviceRemovedReason()))
        throw std::runtime_error("DirectX 12 returned the device that already stopped responding, the depth model cannot restart this session.");
    winrt::com_ptr<ID3D12Device> device;
    const LUID adapter = proxy->GetAdapterLuid();
    {
        const std::lock_guard lock(nativeMutex);
        for (ID3D12Device* native : nativeDevices)
            if (const LUID luid = native->GetAdapterLuid(); luid.LowPart == adapter.LowPart && luid.HighPart == adapter.HighPart
                && std::find(before.begin(), before.end(), native) == before.end() && SUCCEEDED(native->GetDeviceRemovedReason()))
                device.copy_from(native);
    }
    l.d3d12Proxy = proxy;
    l.d3d12 = device ? device : proxy;

    winrt::check_hresult(l.d3d12->CreateFence(0, D3D12_FENCE_FLAG_SHARED, IID_PPV_ARGS(l.d3d12Fence.put())));
    winrt::check_hresult(l.d3d12->CreateSharedHandle(l.d3d12Fence.get(), nullptr, GENERIC_ALL, nullptr, &l.fenceHandle));
    winrt::check_hresult(l.d3d12->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(l.allocator.put())));
    winrt::check_hresult(l.d3d12->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, l.allocator.get(), nullptr, IID_PPV_ARGS(l.list.put())));
    winrt::check_hresult(l.list->Close());
}

void LogDeviceRemoval(Lane& l, const wchar_t* lane)
{
    winrt::com_ptr<ID3D12DeviceRemovedExtendedData> dred = l.d3d12.try_as<ID3D12DeviceRemovedExtendedData>();
    if (!dred)
        return;
    D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT breadcrumbs{};
    if (SUCCEEDED(dred->GetAutoBreadcrumbsOutput(&breadcrumbs)))
        for (const D3D12_AUTO_BREADCRUMB_NODE* node = breadcrumbs.pHeadAutoBreadcrumbNode; node; node = node->pNext)
        {
            const UINT completed = node->pLastBreadcrumbValue ? *node->pLastBreadcrumbValue : 0;
            if (completed >= node->BreadcrumbCount)
                continue;
            Log(LogLevel::Info, L"Depth %ls GPU work stopped at operation %u of %u, op=%d, queue=%ls, list=%ls.", lane, completed, node->BreadcrumbCount,
                node->pCommandHistory ? static_cast<int>(node->pCommandHistory[completed]) : -1,
                node->pCommandQueueDebugNameW ? node->pCommandQueueDebugNameW : L"unnamed", node->pCommandListDebugNameW ? node->pCommandListDebugNameW : L"unnamed");
            break;
        }
    D3D12_DRED_PAGE_FAULT_OUTPUT fault{};
    if (SUCCEEDED(dred->GetPageFaultAllocationOutput(&fault)) && fault.PageFaultVA)
        Log(LogLevel::Info, L"Depth %ls GPU page fault at 0x%llX.", lane, static_cast<unsigned long long>(fault.PageFaultVA));
}

void ReleaseD3D12(Lane& l)
{
    l.list = nullptr;
    l.allocator = nullptr;
    l.d3d12Fence = nullptr;
    if (l.fenceHandle)
        CloseHandle(std::exchange(l.fenceHandle, nullptr));
    l.d3d12 = nullptr;
    l.d3d12Proxy = nullptr;
}

bool D3D12Removed(const Lane& l)
{
    return l.d3d12 && FAILED(l.d3d12->GetDeviceRemovedReason());
}

winrt::com_ptr<ID3D12Resource> CreateD3D12Buffer(Lane& l, UINT64 size)
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
    winrt::check_hresult(l.d3d12->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COMMON, nullptr, IID_PPV_ARGS(buffer.put())));
    return buffer;
}

void Copy(Lane& l, ID3D12CommandQueue* queue, ID3D12Resource* texture, ID3D12Resource* buffer, bool toBuffer)
{
    const D3D12_RESOURCE_DESC desc = texture->GetDesc();
    D3D12_TEXTURE_COPY_LOCATION textureLocation{ texture, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX };
    textureLocation.SubresourceIndex = 0;
    D3D12_TEXTURE_COPY_LOCATION bufferLocation{ buffer, D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT };
    bufferLocation.PlacedFootprint = { 0, { desc.Format, static_cast<UINT>(desc.Width), desc.Height, 1, kRowBytes } };
    winrt::check_hresult(l.list->Reset(l.allocator.get(), nullptr));
    if (toBuffer)
        l.list->CopyTextureRegion(&bufferLocation, 0, 0, 0, &textureLocation, nullptr);
    else
    {
        l.list->CopyTextureRegion(&textureLocation, 0, 0, 0, &bufferLocation, nullptr);
        D3D12_RESOURCE_BARRIER barrier{ D3D12_RESOURCE_BARRIER_TYPE_TRANSITION };
        barrier.Transition = { texture, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_COMMON };
        l.list->ResourceBarrier(1, &barrier);
    }
    winrt::check_hresult(l.list->Close());
    ID3D12CommandList* lists[] = { l.list.get() };
    queue->ExecuteCommandLists(1, lists);
}

const wchar_t* LaneName(const Lane& l)
{
    return l.index == kDetail ? L"detail" : L"fast";
}

void Worker(Lane& l)
{
    InitThreadLog();
    Log(LogLevel::Info, L"Depth %ls worker started.", LaneName(l));
    winrt::com_ptr<ID3D12Resource> input, output, inputBuffer, outputBuffer;
    std::unique_ptr<DepthModel> model;
    int loadedWidth = 0, loadedHeight = 0;
    unsigned boundVersion = 0;
    for (;;)
    {
        WaitForSingleObject(l.request, INFINITE);
        if (l.stopping)
        {
            if (l.inputValue && l.d3d12Fence && l.d3d12Fence->GetCompletedValue() < l.inputValue + 1)
                if (const HANDLE finished = CreateEventW(nullptr, FALSE, FALSE, nullptr))
                {
                    if (SUCCEEDED(l.d3d12Fence->SetEventOnCompletion(l.inputValue + 1, finished)))
                        WaitForSingleObject(finished, 1000);
                    CloseHandle(finished);
                }
            return;
        }
        try
        {
            const auto started = FrameStatistics::Clock::now();
            if (!l.inputValue && l.d3d12 && (!l.enabled || !l.list || D3D12Removed(l)))
            {
                Log(LogLevel::Info, L"Releasing the depth %ls D3D12 device: removed_reason=0x%08X, lane_enabled=%d.", LaneName(l),
                    static_cast<unsigned>(l.d3d12->GetDeviceRemovedReason()), l.enabled.load());
                if (D3D12Removed(l))
                {
                    ++l.removals;
                    LogDeviceRemoval(l, LaneName(l));
                }
                model.reset();
                input = output = inputBuffer = outputBuffer = nullptr;
                loadedWidth = loadedHeight = 0;
                ReleaseD3D12(l);
            }
            if (!l.enabled)
            {
                l.done = true;
                SetEvent(l.finished);
                continue;
            }
            if (!l.d3d12 && l.removals >= kMaxDeviceRemovals)
                throw std::runtime_error("The GPU stopped responding while running the depth model again, the depth model stays off this session.");
            if (!l.d3d12)
                CreateD3D12(l);
            if (l.width != loadedWidth || l.height != loadedHeight)
            {
                model.reset();
                boundVersion = 0;
                model = std::make_unique<DepthModel>();
                model->Load(d.directory, l.modelFile, l.width, l.height, l.d3d12.get());
                loadedWidth = l.modelWidth = l.width;
                loadedHeight = l.modelHeight = l.height;
                Log(LogLevel::Info, L"Depth %ls model loaded: %ls%ls, input=%dx%d, half_input=%d, half_output=%d.", LaneName(l), d.directory.c_str(),
                    model->LoadedFile().c_str(), l.width, l.height, model->HalfInput(), model->HalfOutput());
                if (!model->FusionError().empty())
                    Log(LogLevel::Warning, L"Depth %ls model runs without DirectML graph fusion because fused setup failed: %hs", LaneName(l), model->FusionError().c_str());
                l.halfInput = model->HalfInput();
                l.halfOutput = model->HalfOutput();
                l.distanceOutput = model->DistanceOutput();
                if (l.index == kDetail)
                    Report(LogLevel::Ok, L"Depth estimation ready (%dx%d)", l.width, l.height);
            }
            if (l.inputValue)
            {
                if (boundVersion != l.version)
                {
                    winrt::check_hresult(l.d3d12->OpenSharedHandle(l.inputHandle, IID_PPV_ARGS(input.put())));
                    winrt::check_hresult(l.d3d12->OpenSharedHandle(l.outputHandle, IID_PPV_ARGS(output.put())));
                    inputBuffer = CreateD3D12Buffer(l, input->GetDesc().Height * kRowBytes);
                    outputBuffer = CreateD3D12Buffer(l, output->GetDesc().Height * kRowBytes);
                    model->Bind(inputBuffer.get(), outputBuffer.get());
                    boundVersion = l.version;
                }
                ID3D12CommandQueue* queue = model->Queue();
                winrt::check_hresult(queue->Wait(l.d3d12Fence.get(), l.inputValue));
                winrt::check_hresult(l.allocator->Reset());
                Copy(l, queue, input.get(), inputBuffer.get(), true);
                model->Run();
                Copy(l, queue, output.get(), outputBuffer.get(), false);
                winrt::check_hresult(queue->Signal(l.d3d12Fence.get(), l.inputValue + 1));
            }
            l.workerMs = std::chrono::duration<double, std::milli>(FrameStatistics::Clock::now() - started).count();
        }
        catch (const std::exception& e)
        {
            if (D3D12Removed(l))
                Log(LogLevel::Info, L"Depth %ls run failed on a removed D3D12 device: %hs", LaneName(l), e.what());
            else
            {
                Log(LogLevel::Error, L"Depth %ls estimation stopped: %hs", LaneName(l), e.what());
                LogStackTrace();
                l.failed = true;
            }
        }
        catch (const winrt::hresult_error& e)
        {
            if (D3D12Removed(l))
                Log(LogLevel::Info, L"Depth %ls run failed on a removed D3D12 device: %ls (0x%08X)", LaneName(l), e.message().c_str(),
                    static_cast<unsigned>(e.code()));
            else
            {
                Log(LogLevel::Error, L"Depth %ls estimation stopped: %ls (0x%08X)", LaneName(l), e.message().c_str(), static_cast<unsigned>(e.code()));
                Log(LogLevel::Info, L"Depth %ls worker state: model=%dx%d, requested=%dx%d, input_fence=%llu, resource_version=%u.", LaneName(l),
                    loadedWidth, loadedHeight, l.width, l.height, l.inputValue, l.version);
                LogStackTrace();
                l.failed = true;
            }
        }
        catch (...)
        {
            Log(LogLevel::Error, L"Depth %ls estimation stopped on an unexpected error.", LaneName(l));
            LogStackTrace();
            l.failed = true;
        }
        l.done = true;
        SetEvent(l.finished);
    }
}

void Request(Lane& l, uint64_t value)
{
    l.inputValue = value;
    l.requestedAt = FrameStatistics::Clock::now();
    l.slowRequestLogged = false;
    l.busy = true;
    SetEvent(l.request);
}

void CreateShaders()
{
    for (size_t i = 0; i < kShaderCount; ++i)
        winrt::check_hresult(g.device->CreateComputeShader(d.code[i]->GetBufferPointer(), d.code[i]->GetBufferSize(), nullptr, d.shaders[i].put()));

    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = 32;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    for (Lane& l : d.lanes)
    {
        l.sizes = nullptr;
        winrt::check_hresult(g.device->CreateBuffer(&desc, nullptr, l.sizes.put()));
    }
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

void Dispatch(size_t shader, std::array<ID3D11ShaderResourceView*, 14> views, std::array<ID3D11UnorderedAccessView*, 8> targets, UINT x, UINT y,
              ID3D11Buffer* constants = nullptr)
{
    ID3D11Buffer* buffers[] = { constants ? constants : d.trackSizes.get() };
    g.context->CSSetShader(d.shaders[shader].get(), nullptr, 0);
    g.context->CSSetConstantBuffers(0, 1, buffers);
    g.context->CSSetShaderResources(0, static_cast<UINT>(views.size()), views.data());
    g.context->CSSetUnorderedAccessViews(0, static_cast<UINT>(targets.size()), targets.data(), nullptr);
    g.context->Dispatch(x, y, 1);
    views.fill(nullptr);
    targets.fill(nullptr);
    g.context->CSSetShaderResources(0, static_cast<UINT>(views.size()), views.data());
    g.context->CSSetUnorderedAccessViews(0, static_cast<UINT>(targets.size()), targets.data(), nullptr);
}

void ReleaseLaneShared(Lane& l)
{
    l.anchor = {};
    l.anchorLuma = {};
    l.anchored = false;
    l.resetPending = false;
    l.published = false;
    l.stateView = nullptr;
    l.stateTarget = nullptr;
    l.state = nullptr;
    l.keysTarget = nullptr;
    l.keys = nullptr;
    l.outputView = nullptr;
    l.output = nullptr;
    l.guideView = nullptr;
    l.guideTarget = nullptr;
    l.guide = nullptr;
    l.inputTarget = nullptr;
    l.input = nullptr;
    for (HANDLE* handle : { &l.inputHandle, &l.outputHandle })
        if (*handle)
            CloseHandle(std::exchange(*handle, nullptr));
}

void ReleaseShared()
{
    for (auto* runtime : d.runtimes)
        Bind(runtime, nullptr);
    d.view = nullptr;
    d.textureTarget = nullptr;
    d.texture = nullptr;
    for (Surface* surface : { &d.luma[0], &d.luma[1], &d.quarter[0], &d.quarter[1], &d.coarse, &d.flow, &d.warped, &d.staleRaw, &d.staleOpen, &d.paired, &d.ratio,
                              &d.ratioBlur, &d.focus[0], &d.focus[1] })
        *surface = {};
    d.affineView = nullptr;
    d.affineTarget = nullptr;
    d.affine = nullptr;
    d.focusMotionTarget = nullptr;
    for (auto& slot : d.focusMotionSlots)
        slot = nullptr;
    d.focusMotion = nullptr;
    d.tracking = false;
    d.pairPending = false;
    d.pairReady = false;
    d.ratioReady = false;
    for (Lane& l : d.lanes)
        ReleaseLaneShared(l);
}

void ReleaseLaneResources(Lane& l)
{
    if (l.busy && l.inputValue && l.d3d12Fence && l.d3d12Fence->GetCompletedValue() < l.inputValue)
        l.d3d12Fence->Signal(l.inputValue);
    l.fence = nullptr;
    l.frameView = nullptr;
    l.frameCopy = nullptr;
    l.sizes = nullptr;
    l.lastInput.reset();
}

void ReleaseResources()
{
    ReleaseShared();
    for (Lane& l : d.lanes)
        ReleaseLaneResources(l);
    d.liveView = nullptr;
    d.liveCopy = nullptr;
    d.pairCopy = nullptr;
    d.trackSizes = nullptr;
    d.lastTracked.reset();
    for (auto& shader : d.shaders)
        shader = nullptr;
    d.sourceWidth = 0;
    d.sourceHeight = 0;
}

void StopFast()
{
    Lane& l = Fast();
    ReleaseLaneShared(l);
    ReleaseLaneResources(l);
    l.enabled = false;
    Request(l, 0);
    Report(LogLevel::Warning, L"The fast depth model failed, so only the chosen depth model is used.");
}

void Disable()
{
    ReleaseResources();
    d.enabled = false;
    for (Lane& l : d.lanes)
    {
        l.enabled = false;
        if (l.request)
            Request(l, 0);
    }
}

void Stop()
{
    if (DepthSize() == kDefaultDepthSize)
    {
        Disable();
        return;
    }
    ReleaseResources();
    SetDepthSize(kDefaultDepthSize);
    Report(LogLevel::Warning, L"Depth estimation failed, so Depth detail went back to High.");
}

int LaneSize(const Lane& l)
{
    return l.index == kDetail ? DepthSize() : *std::min_element(std::begin(kDepthSizes), std::end(kDepthSizes));
}

void ResizeLane(Lane& l, const D3D11_TEXTURE2D_DESC& frame, const D3D11_TEXTURE2D_DESC& copy)
{
    const bool landscape = frame.Width >= frame.Height;
    const float ratio = landscape ? static_cast<float>(frame.Height) / frame.Width : static_cast<float>(frame.Width) / frame.Height;
    l.size = LaneSize(l);
    const int shortSide = std::max(14, static_cast<int>(std::lround(l.size * ratio / 14)) * 14);
    l.width = landscape ? l.size : shortSide;
    l.height = landscape ? shortSide : l.size;
    l.lastInput.reset();
    l.frameCopy = nullptr;
    l.frameView = nullptr;
    winrt::check_hresult(g.device->CreateTexture2D(&copy, nullptr, l.frameCopy.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(l.frameCopy.get(), nullptr, l.frameView.put()));
    Log(LogLevel::Info, L"Depth %ls resize: source=%ux%u, model=%dx%d, detail=%d.", LaneName(l), frame.Width, frame.Height, l.width, l.height, l.size);
}

void FindAdapter()
{
    d.adapterKnown = false;
    winrt::com_ptr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC desc{};
    if (const auto device = g.device.try_as<IDXGIDevice>(); device && SUCCEEDED(device->GetAdapter(adapter.put())) && SUCCEEDED(adapter->GetDesc(&desc)))
    {
        d.adapter = desc.AdapterLuid;
        d.adapterKnown = true;
    }
}

void Resize(const D3D11_TEXTURE2D_DESC& frame)
{
    FindAdapter();
    d.slowFrames = 0;
    d.sourceWidth = frame.Width;
    d.sourceHeight = frame.Height;
    d.workWidth = std::max(1u, (frame.Width + 3) / 4);
    d.workHeight = std::max(1u, (frame.Height + 3) / 4);
    d.lastTracked.reset();
    ReleaseShared();

    D3D11_TEXTURE2D_DESC copy = frame;
    copy.MipLevels = 1;
    copy.ArraySize = 1;
    copy.SampleDesc = { 1, 0 };
    copy.Usage = D3D11_USAGE_DEFAULT;
    copy.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    copy.CPUAccessFlags = 0;
    copy.MiscFlags = 0;
    for (Lane& l : d.lanes)
        if (l.enabled)
            ResizeLane(l, frame, copy);
    d.liveCopy = nullptr;
    d.liveView = nullptr;
    winrt::check_hresult(g.device->CreateTexture2D(&copy, nullptr, d.liveCopy.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.liveCopy.get(), nullptr, d.liveView.put()));
    d.pairCopy = nullptr;
    winrt::check_hresult(g.device->CreateTexture2D(&copy, nullptr, d.pairCopy.put()));
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

void CreateCommon()
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = d.sourceWidth;
    desc.Height = d.sourceHeight;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R32_FLOAT;
    desc.SampleDesc = { 1, 0 };
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, d.texture.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(d.texture.get(), nullptr, d.textureTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.texture.get(), nullptr, d.view.put()));
    const float farPlane[4] = { 1, 1, 1, 1 };
    g.context->ClearUnorderedAccessViewFloat(d.textureTarget.get(), farPlane);

    const UINT quarterWidth = (d.workWidth + 3) / 4;
    const UINT quarterHeight = (d.workHeight + 3) / 4;
    for (size_t i = 0; i < 2; ++i)
    {
        CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32_FLOAT, d.luma[i]);
        CreateSurface(quarterWidth, quarterHeight, DXGI_FORMAT_R32_FLOAT, d.quarter[i]);
    }
    CreateSurface(quarterWidth, quarterHeight, DXGI_FORMAT_R32G32_FLOAT, d.coarse);
    CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32G32B32A32_FLOAT, d.flow);
    CreateSurface(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32G32B32A32_FLOAT, d.warped);
    CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32_FLOAT, d.staleRaw);
    CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32_FLOAT, d.staleOpen);
    CreateSurface(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32_FLOAT, d.paired);
    CreateSurface(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32_FLOAT, d.ratio);
    CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32_FLOAT, d.ratioBlur);
    const float noHistory[4] = { -1, -1, -1, -1 };
    for (Surface& focus : d.focus)
    {
        CreateSurface(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32_FLOAT, focus);
        g.context->ClearUnorderedAccessViewFloat(focus.target.get(), noHistory);
    }
    std::array<float, 4 * kAffineRows> identity{};
    for (UINT row = 0; row < kAffineRows; ++row)
        identity[row * 4 + row % 2] = 1;
    D3D11_BUFFER_DESC affine{};
    affine.ByteWidth = sizeof(identity);
    affine.Usage = D3D11_USAGE_DEFAULT;
    affine.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    affine.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    affine.StructureByteStride = 16;
    const D3D11_SUBRESOURCE_DATA initial{ identity.data() };
    winrt::check_hresult(g.device->CreateBuffer(&affine, &initial, d.affine.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(d.affine.get(), nullptr, d.affineTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.affine.get(), nullptr, d.affineView.put()));
    D3D11_BUFFER_DESC motion{};
    motion.ByteWidth = 32;
    motion.Usage = D3D11_USAGE_DEFAULT;
    motion.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    motion.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
    winrt::check_hresult(g.device->CreateBuffer(&motion, nullptr, d.focusMotion.put()));
    D3D11_UNORDERED_ACCESS_VIEW_DESC motionView{};
    motionView.Format = DXGI_FORMAT_R32_TYPELESS;
    motionView.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    motionView.Buffer.NumElements = 8;
    motionView.Buffer.Flags = D3D11_BUFFER_UAV_FLAG_RAW;
    winrt::check_hresult(g.device->CreateUnorderedAccessView(d.focusMotion.get(), &motionView, d.focusMotionTarget.put()));
    for (UINT i = 0; i < 2; ++i)
    {
        motionView.Buffer.FirstElement = i * 4;
        motionView.Buffer.NumElements = 4;
        winrt::check_hresult(g.device->CreateUnorderedAccessView(d.focusMotion.get(), &motionView, d.focusMotionSlots[i].put()));
    }
    const UINT zero[4] = {};
    g.context->ClearUnorderedAccessViewUint(d.focusMotionTarget.get(), zero);
    const UINT constants[8] = { d.sourceWidth, d.sourceHeight, d.workWidth, d.workHeight };
    g.context->UpdateSubresource(d.trackSizes.get(), 0, nullptr, constants, 0, 0);
    for (auto* runtime : d.runtimes)
        Bind(runtime, d.view.get());
}

void CreateLaneShared(Lane& l)
{
    const size_t pixels = static_cast<size_t>(l.width) * l.height;
    CreateSharedTexture(3 * pixels, l.halfInput, D3D11_BIND_UNORDERED_ACCESS, l.input, l.inputHandle);
    CreateSharedTexture(pixels, l.halfOutput, D3D11_BIND_SHADER_RESOURCE, l.output, l.outputHandle);
    ++l.version;
    winrt::check_hresult(g.device->CreateUnorderedAccessView(l.input.get(), nullptr, l.inputTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(l.output.get(), nullptr, l.outputView.put()));
    if (!l.fence)
        winrt::check_hresult(g.device.as<ID3D11Device5>()->OpenSharedFence(l.fenceHandle, IID_PPV_ARGS(l.fence.put())));

    D3D11_BUFFER_DESC buffer{};
    buffer.ByteWidth = 4 * 260;
    buffer.Usage = D3D11_USAGE_DEFAULT;
    buffer.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
    buffer.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
    winrt::check_hresult(g.device->CreateBuffer(&buffer, nullptr, l.keys.put()));
    D3D11_UNORDERED_ACCESS_VIEW_DESC raw{};
    raw.Format = DXGI_FORMAT_R32_TYPELESS;
    raw.ViewDimension = D3D11_UAV_DIMENSION_BUFFER;
    raw.Buffer.NumElements = 260;
    raw.Buffer.Flags = D3D11_BUFFER_UAV_FLAG_RAW;
    winrt::check_hresult(g.device->CreateUnorderedAccessView(l.keys.get(), &raw, l.keysTarget.put()));

    buffer.ByteWidth = 16;
    buffer.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    buffer.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    buffer.StructureByteStride = 16;
    winrt::check_hresult(g.device->CreateBuffer(&buffer, nullptr, l.state.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(l.state.get(), nullptr, l.stateTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(l.state.get(), nullptr, l.stateView.put()));
    const UINT zero[4] = {};
    g.context->ClearUnorderedAccessViewUint(l.stateTarget.get(), zero);

    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = l.width;
    desc.Height = l.height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.SampleDesc = { 1, 0 };
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, l.guide.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(l.guide.get(), nullptr, l.guideTarget.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(l.guide.get(), nullptr, l.guideView.put()));

    CreateSurface(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32_FLOAT, l.anchor);
    CreateSurface(d.workWidth, d.workHeight, DXGI_FORMAT_R32_FLOAT, l.anchorLuma);
    const float farPlane[4] = { 1, 1, 1, 1 };
    g.context->ClearUnorderedAccessViewFloat(l.anchor.target.get(), farPlane);

    const UINT sizes[8] = { d.sourceWidth, d.sourceHeight, static_cast<UINT>(l.width), static_cast<UINT>(l.height),
                            kRowBytes / (l.halfInput ? 2 : 4), kRowBytes / (l.halfOutput ? 2 : 4), l.distanceOutput ? 1u : 0u };
    g.context->UpdateSubresource(l.sizes.get(), 0, nullptr, sizes, 0, 0);
}

void Publish(Lane& l)
{
    winrt::check_hresult(g.context.as<ID3D11DeviceContext4>()->Wait(l.fence.get(), l.inputValue + 1));
    const UINT zero[4] = {};
    g.context->ClearUnorderedAccessViewUint(l.keysTarget.get(), zero);
    const UINT pixels = static_cast<UINT>(l.width * l.height);
    ID3D11Buffer* sizes = l.sizes.get();
    Dispatch(1, { nullptr, l.outputView.get() }, { nullptr, l.keysTarget.get() }, (pixels + 255) / 256, 1, sizes);
    Dispatch(2, { nullptr, l.outputView.get() }, { nullptr, l.keysTarget.get() }, (pixels + 255) / 256, 1, sizes);
    Dispatch(3, {}, { nullptr, l.keysTarget.get(), l.stateTarget.get() }, 1, 1, sizes);
    const bool paired = l.paired;
    Dispatch(4, { l.frameView.get(), l.outputView.get(), l.stateView.get(), l.guideView.get() },
             { nullptr, nullptr, nullptr, paired ? d.paired.target.get() : l.anchor.target.get() }, (d.sourceWidth + 15) / 16, (d.sourceHeight + 15) / 16,
             sizes);
    if (paired)
    {
        d.pairReady = true;
        return;
    }
    if (l.index == kDetail)
    {
        Dispatch(kShrink, { l.frameView.get() }, { l.anchorLuma.target.get() }, (d.workWidth + 15) / 16, (d.workHeight + 15) / 16);
        if (d.pairReady)
        {
            Dispatch(kPair, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, l.anchor.view.get(), nullptr, nullptr, nullptr, d.paired.view.get() },
                     { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.ratio.target.get() }, (d.sourceWidth + 15) / 16,
                     (d.sourceHeight + 15) / 16);
            Dispatch(kSpread, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.ratio.view.get() },
                     { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.ratioBlur.target.get() }, (d.workWidth + 15) / 16,
                     (d.workHeight + 15) / 16);
            d.pairReady = false;
            d.ratioReady = true;
        }
    }
    l.anchored = true;
    l.published = true;
}

bool ExactActive()
{
    return ExactDepth() && !Fast().enabled && !d.background;
}

void TrackExact()
{
    Lane& detail = Detail();
    detail.resetPending = false;
    detail.published = false;
    if (!detail.anchored)
        return;
    const Surface& history = d.focus[d.focusIndex];
    d.focusIndex ^= 1;
    const UINT parity = static_cast<UINT>(d.focusIndex);
    const UINT zero[4] = {};
    g.context->ClearUnorderedAccessViewUint(d.focusMotionSlots[parity].get(), zero);
    const UINT constants[8] = { d.sourceWidth, d.sourceHeight, d.workWidth, d.workHeight, kExact, parity };
    g.context->UpdateSubresource(d.trackSizes.get(), 0, nullptr, constants, 0, 0);
    const UINT sourceGroupsX = (d.sourceWidth + 15) / 16;
    const UINT sourceGroupsY = (d.sourceHeight + 15) / 16;
    Dispatch(kWarp, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, detail.anchor.view.get() },
             { nullptr, nullptr, nullptr, nullptr, nullptr, d.warped.target.get() }, sourceGroupsX, sourceGroupsY);
    Dispatch(kRefine, { d.liveView.get(), history.view.get(), nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.warped.view.get() },
             { d.focus[d.focusIndex].target.get(), d.focusMotionTarget.get(), nullptr, nullptr, d.textureTarget.get() }, sourceGroupsX, sourceGroupsY);
}

void Track(ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (!d.texture || !d.liveCopy || (d.lastTracked && *d.lastTracked == frameTimestamp))
        return;
    d.lastTracked = frameTimestamp;
    if (ExactActive())
    {
        g.context->CopyResource(d.liveCopy.get(), frame);
        d.tracking = false;
        TrackExact();
        return;
    }
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
        Dispatch(kFit, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.flow.view.get() },
                 { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.affineTarget.get() }, 1, 1);
        Dispatch(kFitRefine, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.flow.view.get() },
                 { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.affineTarget.get() }, 1, 1);
    }
    Lane& detail = Detail();
    Lane& fast = Fast();
    const bool fused = detail.anchored && fast.enabled && fast.anchored && d.ratioReady;
    const auto now = FrameStatistics::Clock::now();
    const bool advance = !d.tracking || now - d.keyAt >= kKeyframeInterval;
    UINT flags = (composed ? kComposed : 0) | (advance ? kAdvance : 0) | (fused ? kFused : 0);
    for (Lane& l : d.lanes)
    {
        const UINT shift = static_cast<UINT>(l.index) * kLaneShift;
        flags |= ((l.resetPending ? kReset : 0) | (l.published ? kPublished : 0)) << shift;
        l.resetPending = false;
        l.published = false;
    }
    const UINT constants[8] = { d.sourceWidth, d.sourceHeight, d.workWidth, d.workHeight, flags };
    g.context->UpdateSubresource(d.trackSizes.get(), 0, nullptr, constants, 0, 0);
    Dispatch(kStep, {}, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.affineTarget.get() }, 1, 1);
    if (advance)
    {
        d.key ^= 1;
        d.keyAt = now;
        d.tracking = true;
    }
    if (!detail.anchored)
        return;
    if (fused)
    {
        Dispatch(kStale, { nullptr, luma.view.get(), detail.anchorLuma.view.get(), nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.affineView.get() },
                 { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.staleRaw.target.get() }, workGroupsX, workGroupsY);
        Dispatch(kErode, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.staleRaw.view.get() },
                 { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.staleOpen.target.get() }, workGroupsX, workGroupsY);
        Dispatch(kDilate, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.staleOpen.view.get() },
                 { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.staleRaw.target.get() }, workGroupsX, workGroupsY);
    }
    const UINT sourceGroupsX = (d.sourceWidth + 15) / 16;
    const UINT sourceGroupsY = (d.sourceHeight + 15) / 16;
    Dispatch(kWarp, { nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, detail.anchor.view.get(), nullptr, d.affineView.get(),
                      d.staleRaw.view.get(), fused ? fast.anchor.view.get() : nullptr, d.ratio.view.get(), d.ratioBlur.view.get() },
             { nullptr, nullptr, nullptr, nullptr, nullptr, d.warped.target.get() }, sourceGroupsX, sourceGroupsY);
    Dispatch(kRefine, { d.liveView.get(), nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.warped.view.get() },
             { nullptr, nullptr, nullptr, nullptr, d.textureTarget.get() }, sourceGroupsX, sourceGroupsY);
}

bool FastModelUsable()
{
    const std::wstring file = FastModelFile();
    return !file.empty() && file != ModelFile() && GetFileAttributesW((d.directory + file).c_str()) != INVALID_FILE_ATTRIBUTES;
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
    for (size_t i = 0; i < d.lanes.size(); ++i)
    {
        Lane& l = d.lanes[i];
        l.index = i;
        l.modelFile = i == kDetail ? ModelFile() : FastModelFile();
        l.enabled = i == kDetail || FastModelUsable();
        if (!l.enabled)
            continue;
        l.request = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        l.finished = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        l.worker = std::thread(Worker, std::ref(l));
    }
    d.enabled = true;
    Log(LogLevel::Info, L"Depth estimation initialized: model=%ls%ls, fast_model=%ls, depth_size=%d.", d.directory.c_str(), ModelFile().c_str(),
        Fast().enabled ? Fast().modelFile.c_str() : L"none", DepthSize());
    return true;
}

namespace
{
bool Schedule(Lane& l, ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (l.busy)
    {
        if (!l.slowRequestLogged && FrameStatistics::Clock::now() - l.requestedAt >= std::chrono::seconds(2))
        {
            l.slowRequestLogged = true;
            LogDepthDiagnostics();
        }
        if (!l.done)
            return true;
        if (l.failed)
        {
            l.done = false;
            l.busy = false;
            l.failed = false;
            return false;
        }
        if (l.inputValue && l.d3d12Fence->GetCompletedValue() < l.inputValue + 1)
            return true;
        if (l.index == kDetail && l.inputValue && Fast().enabled && (d.pairPending || (Fast().paired && Fast().busy)))
            return true;
        l.done = false;
        l.busy = false;
        const auto completedAt = FrameStatistics::Clock::now();
        l.nextRequestAt = std::max(l.requestedAt + kMinDepthInterval, completedAt);
        if (l.inputValue && d.texture && l.input && !D3D12Removed(l))
        {
            Publish(l);
            if (l.index == kDetail)
                g.frameStatistics.RecordDepth(std::chrono::duration<double, std::milli>(FrameStatistics::Clock::now() - l.requestedAt).count(),
                                              l.workerMs.load());
        }
    }

    if (D3D12Removed(l))
    {
        ReleaseLaneShared(l);
        l.fence = nullptr;
        l.modelWidth = l.modelHeight = 0;
    }
    const bool pairing = l.index == kFast && d.pairPending;
    if (!pairing && l.lastInput && *l.lastInput == frameTimestamp)
        return true;
    if (l.modelWidth != l.width || l.modelHeight != l.height)
    {
        Request(l, 0);
        return true;
    }
    if (!pairing && FrameStatistics::Clock::now() < l.nextRequestAt)
        return true;
    if (!d.texture)
        CreateCommon();
    if (!l.input)
        CreateLaneShared(l);

    g.context->CopyResource(l.frameCopy.get(), pairing ? d.pairCopy.get() : frame);
    l.paired = pairing;
    if (pairing)
        d.pairPending = false;
    if (l.index == kDetail && Fast().enabled)
    {
        g.context->CopyResource(d.pairCopy.get(), frame);
        d.pairPending = true;
        d.pairReady = false;
    }
    Dispatch(0, { l.frameView.get() }, { l.inputTarget.get(), nullptr, nullptr, nullptr, l.guideTarget.get() }, (l.width + 15) / 16, (l.height + 15) / 16,
             l.sizes.get());
    l.fenceValue += 2;
    winrt::check_hresult(g.context.as<ID3D11DeviceContext4>()->Signal(l.fence.get(), l.fenceValue - 1));
    Request(l, l.fenceValue - 1);
    if (!pairing)
    {
        l.resetPending = true;
        l.lastInput = frameTimestamp;
    }
    return true;
}

void StepDownWhenSlow(double spentMs)
{
    d.slowFrames = spentMs > kExactBudgetMs ? d.slowFrames + 1 : std::max(0, d.slowFrames - 1);
    if (d.slowFrames < kSlowFramesBeforeStepDown)
        return;
    d.slowFrames = 0;
    for (const int size : kDepthSizes)
        if (size < DepthSize())
        {
            Log(LogLevel::Info, L"Depth took %.1f ms per frame, lowering the depth size from %d to %d.", spentMs, DepthSize(), size);
            SetDepthSize(size);
            return;
        }
    Log(LogLevel::Warning, L"Depth takes %.1f ms per frame even at the smallest size, updating it in the background with motion tracking.", spentMs);
    d.background = true;
}

bool WaitLane(Lane& l)
{
    return l.done || WaitForSingleObject(l.finished, kExactTimeoutMs) == WAIT_OBJECT_0 || l.done;
}

bool ScheduleExact(Lane& l, ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (l.busy)
    {
        if (l.inputValue ? !WaitLane(l) : !l.done)
            return true;
        if (l.failed)
        {
            l.done = false;
            l.busy = false;
            l.failed = false;
            return false;
        }
        l.done = false;
        l.busy = false;
        if (l.inputValue && !D3D12Removed(l))
            Publish(l);
    }
    if (D3D12Removed(l))
    {
        ReleaseLaneShared(l);
        l.fence = nullptr;
        l.modelWidth = l.modelHeight = 0;
    }
    if (l.lastInput && *l.lastInput == frameTimestamp)
        return true;
    if (l.modelWidth != l.width || l.modelHeight != l.height)
    {
        ResetEvent(l.finished);
        Request(l, 0);
        return true;
    }
    if (!d.texture)
        CreateCommon();
    if (!l.input)
        CreateLaneShared(l);

    g.context->CopyResource(l.frameCopy.get(), frame);
    l.paired = false;
    Dispatch(0, { l.frameView.get() }, { l.inputTarget.get(), nullptr, nullptr, nullptr, l.guideTarget.get() }, (l.width + 15) / 16, (l.height + 15) / 16,
             l.sizes.get());
    l.fenceValue += 2;
    winrt::check_hresult(g.context.as<ID3D11DeviceContext4>()->Signal(l.fence.get(), l.fenceValue - 1));
    g.context->Flush();
    ResetEvent(l.finished);
    Request(l, l.fenceValue - 1);
    l.lastInput = frameTimestamp;
    if (!WaitLane(l))
        return true;
    if (l.failed)
    {
        l.done = false;
        l.busy = false;
        l.failed = false;
        return false;
    }
    l.done = false;
    l.busy = false;
    if (D3D12Removed(l))
        return true;
    Publish(l);
    const double spent = std::chrono::duration<double, std::milli>(FrameStatistics::Clock::now() - l.requestedAt).count();
    g.frameStatistics.RecordDepth(spent, l.workerMs.load());
    StepDownWhenSlow(spent);
    return true;
}

void ScheduleAll(ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (!d.shaders[0])
        CreateShaders();
    D3D11_TEXTURE2D_DESC desc{};
    frame->GetDesc(&desc);
    if (desc.Width != d.sourceWidth || desc.Height != d.sourceHeight || Detail().size != DepthSize())
        Resize(desc);
    if (!(ExactActive() ? ScheduleExact(Detail(), frame, frameTimestamp) : Schedule(Detail(), frame, frameTimestamp)))
    {
        Stop();
        return;
    }
    if (Fast().enabled && !Schedule(Fast(), frame, frameTimestamp))
        StopFast();
}
}

void UpdateDepth(ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (!d.enabled)
        return;

    try
    {
        ScheduleAll(frame, frameTimestamp);
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
    for (Lane& l : d.lanes)
        l.lastInput.reset();
}

void LogDepthDiagnostics()
{
    for (const Lane& l : d.lanes)
    {
        if (!l.enabled)
            continue;
        const double pendingMs = l.busy ? std::chrono::duration<double, std::milli>(FrameStatistics::Clock::now() - l.requestedAt).count() : 0;
        const bool fenceReady = !l.busy || l.done || l.inputValue;
        Log(LogLevel::Info, L"Depth %ls state: enabled=%d, busy=%d, worker_done=%d, pending_ms=%.2f, worker_submit_ms=%.2f, "
                           L"input_fence=%llu, completed_fence=%llu, source=%ux%u, requested_model=%dx%d, resource_version=%u.",
            LaneName(l), d.enabled, l.busy.load(), l.done.load(), pendingMs, l.workerMs.load(), l.inputValue,
            fenceReady && l.d3d12Fence ? l.d3d12Fence->GetCompletedValue() : 0, d.sourceWidth, d.sourceHeight, l.width, l.height, l.version);
    }
}

void ReleaseDepthDevice()
{
    ReleaseResources();
}

void ShutdownDepth()
{
    for (Lane& l : d.lanes)
    {
        if (l.worker.joinable())
        {
            l.stopping = true;
            SetEvent(l.request);
            l.worker.join();
        }
        ReleaseD3D12(l);
        for (HANDLE handle : { l.inputHandle, l.outputHandle, l.request, l.finished })
            if (handle)
                CloseHandle(handle);
        l.inputHandle = l.outputHandle = l.request = l.finished = nullptr;
        l.enabled = false;
        l.stopping = false;
        l.busy = false;
        l.done = false;
        l.failed = false;
        l.lastInput.reset();
        l.anchored = false;
        l.resetPending = false;
        l.published = false;
        l.inputValue = 0;
        l.version = 0;
        l.slowRequestLogged = false;
        l.nextRequestAt = {};
        l.width = l.height = l.size = 0;
        l.modelWidth = l.modelHeight = 0;
        l.distanceOutput = false;
    }
    reshade::unregister_event<reshade::addon_event::init_effect_runtime>(OnInitRuntime);
    reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(OnDestroyRuntime);
    reshade::unregister_event<reshade::addon_event::reshade_reloaded_effects>(OnReloadedEffects);
    for (auto* runtime : d.runtimes)
        Bind(runtime, nullptr);
    d.runtimes.clear();
    d.enabled = false;
    d.lastTracked.reset();
    d.tracking = false;
    d.background = false;
}

void AdoptDepthRuntime(reshade::api::effect_runtime* runtime)
{
    if (d.enabled && std::find(d.runtimes.begin(), d.runtimes.end(), runtime) == d.runtimes.end())
        OnInitRuntime(runtime);
}
