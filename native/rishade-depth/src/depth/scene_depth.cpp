#include "scene_depth.h"
#include "../addon.h"
#include "../capture.h"
#include "../config.h"
#include "../log.h"
#include "../reshade_imgui.h"
#include "../state.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <optional>
#include <stdexcept>
#include <vector>

namespace
{
constexpr char kShaders[] = R"(
cbuffer Sizes : register(b0)
{
    uint2 SourceSize;
    uint2 WorkSize;
    float Temporal;
    float3 Padding;
};
Texture2D<float4> Source : register(t0);
Texture2D<float4> Guide : register(t1);
StructuredBuffer<uint> SkyEnd : register(t2);
StructuredBuffer<float4> Scene : register(t3);
Texture2D<float> WorkDepthView : register(t4);
Texture2D<float4> Blurred : register(t5);
RWTexture2D<float4> GuideTarget : register(u0);
RWStructuredBuffer<uint> SkyEndTarget : register(u1);
RWStructuredBuffer<float4> SceneTarget : register(u2);
RWTexture2D<float> WorkDepth : register(u3);
RWTexture2D<float> Depth : register(u4);
RWTexture2D<float4> BlurredTarget : register(u5);

static const float FarPlane = 1000.0;
static const float Nearest = 0.002;
static const float HalfFieldOfView = 0.6108652;
static const float FocusHeight = 4.5;
static const float CameraZoom = 12.5;
static const float DefaultPitch = 0.35;
static const float HorizonPercentile = 0.8;
static const float InterfaceBand = 0.07;
static const float SkyEdge = 0.035;
static const float SkyDrift = 0.35;
static const float ObjectEdge = 0.06;
static const float Relax = 0.03;
static const float HazeReach = 0.5;
static const float HazeWeight = 0.7;
static const float HazeLimit = 3.0;
static const float HazeStart = 0.1;
static const int BlurRadius = 3;
static const float SpatialSigma = 1.4;
static const float ColorSigma = 0.18;

[numthreads(16, 16, 1)]
void Reduce(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    uint2 begin = id.xy * SourceSize / WorkSize;
    uint2 end = max((id.xy + 1) * SourceSize / WorkSize, begin + 1);
    float3 sum = 0;
    for (uint y = begin.y; y < end.y; ++y)
        for (uint x = begin.x; x < end.x; ++x)
            sum += saturate(Source.Load(int3(x, y, 0)).rgb);
    float3 average = sum / ((end.x - begin.x) * (end.y - begin.y));
    GuideTarget[id.xy] = float4(average, dot(average, float3(0.2126, 0.7152, 0.0722)));
}

[numthreads(16, 16, 1)]
void Blur(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    float3 sum = 0;
    for (int y = -BlurRadius; y <= BlurRadius; ++y)
        for (int x = -BlurRadius; x <= BlurRadius; ++x)
            sum += Guide.Load(int3(clamp(int2(id.xy) + int2(x, y), 0, int2(WorkSize) - 1), 0)).rgb;
    BlurredTarget[id.xy] = float4(sum / ((2 * BlurRadius + 1) * (2 * BlurRadius + 1)), 1);
}

[numthreads(64, 1, 1)]
void Sky(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= WorkSize.x)
        return;
    uint start = uint(WorkSize.y * InterfaceBand);
    float3 mean = Blurred.Load(int3(id.x, start, 0)).rgb;
    float3 previous = mean;
    uint y = start + 1;
    for (; y < WorkSize.y; ++y)
    {
        float3 color = Blurred.Load(int3(id.x, y, 0)).rgb;
        if (length(color - previous) > SkyEdge || length(color - mean) > SkyDrift)
            break;
        mean += (color - mean) / (y - start + 1);
        previous = color;
    }
    SkyEndTarget[id.x] = y >= start + 3 ? y : 0;
}

groupshared uint Histogram[256];
groupshared float4 Totals[256];

[numthreads(256, 1, 1)]
void Horizon(uint3 id : SV_DispatchThreadID, uint thread : SV_GroupIndex)
{
    Histogram[thread] = 0;
    GroupMemoryBarrierWithGroupSync();
    float4 total = 0;
    for (uint x = thread; x < WorkSize.x; x += 256)
    {
        uint end = SkyEnd[x];
        if (end == 0)
            continue;
        uint bin = min(end * 256 / WorkSize.y, 255);
        InterlockedAdd(Histogram[bin], 1);
        total += float4(1, Blurred.Load(int3(x, end / 2, 0)).rgb);
    }
    Totals[thread] = total;
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 128; step > 0; step >>= 1)
    {
        if (thread < step)
            Totals[thread] += Totals[thread + step];
        GroupMemoryBarrierWithGroupSync();
    }
    if (thread != 0)
        return;
    float skyColumns = Totals[0].x;
    float4 previous = Scene[0];
    float4 previousSky = Scene[1];
    float horizon;
    float3 skyColor;
    if (skyColumns >= WorkSize.x * 0.15)
    {
        uint wanted = uint(skyColumns * HorizonPercentile);
        uint seen = 0;
        uint bin = 0;
        for (; bin < 255; ++bin)
        {
            seen += Histogram[bin];
            if (seen > wanted)
                break;
        }
        horizon = (bin + 0.5) * WorkSize.y / 256;
        skyColor = Totals[0].yzw / skyColumns;
    }
    else
    {
        float focal = WorkSize.y * 0.5 / tan(HalfFieldOfView);
        horizon = previous.w > 0 ? previous.x : WorkSize.y * 0.5 - focal * tan(DefaultPitch);
        skyColor = previousSky.w > 0 ? previousSky.rgb : float3(0.62, 0.72, 0.85);
    }
    if (previous.w > 0)
    {
        horizon = lerp(previous.x, horizon, 0.15);
        skyColor = lerp(previousSky.rgb, skyColor, 0.15);
    }
    SceneTarget[0] = float4(horizon, 0, 0, 1);
    SceneTarget[1] = float4(skyColor, 1);
}

[numthreads(64, 1, 1)]
void Columns(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= WorkSize.x)
        return;
    float horizon = Scene[0].x;
    float3 skyColor = Scene[1].rgb;
    float center = WorkSize.y * 0.5;
    float focal = center / tan(HalfFieldOfView);
    float pitch = clamp(atan((center - horizon) / focal), -0.2, 1.4);
    float cameraHeight = FocusHeight + CameraZoom * sin(max(pitch, 0));
    uint skyEnd = SkyEnd[id.x];
    float carried = 1;
    float3 below = 0;
    for (int y = int(WorkSize.y) - 1; y >= 0; --y)
    {
        float3 color = Blurred.Load(int3(id.x, y, 0)).rgb;
        float distance;
        if (uint(y) < skyEnd)
        {
            distance = 1;
        }
        else
        {
            float offAxis = atan((y + 0.5 - center) / focal);
            float descent = pitch + offAxis;
            float ground = descent > 0.002 ? clamp(cameraHeight / sin(descent) * cos(offAxis) / FarPlane, Nearest, 1) : 1;
            float haze = saturate(1 - length(color - skyColor) / HazeReach);
            float estimate = max(ground, min(haze * haze * HazeWeight, ground * HazeLimit) * smoothstep(HazeStart * 0.5, HazeStart, ground));
            bool joined = y < int(WorkSize.y) - 1 && length(color - below) < ObjectEdge;
            distance = joined ? min(estimate, lerp(carried, estimate, Relax)) : estimate;
        }
        carried = distance;
        below = color;
        float previous = WorkDepth[uint2(id.x, y)];
        WorkDepth[uint2(id.x, y)] = previous > 0 ? lerp(previous, distance, Temporal) : distance;
    }
}

[numthreads(16, 16, 1)]
void Upsample(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= SourceSize))
        return;
    float3 color = saturate(Source.Load(int3(id.xy, 0)).rgb);
    float2 position = (id.xy + 0.5) * WorkSize / SourceSize - 0.5;
    int2 corner = int2(floor(position)) - 1;
    float sum = 0;
    float weights = 0;
    for (int y = 0; y < 4; ++y)
        for (int x = 0; x < 4; ++x)
        {
            int2 texel = corner + int2(x, y);
            float2 offset = texel - position;
            texel = clamp(texel, 0, int2(WorkSize) - 1);
            float3 difference = Guide.Load(int3(texel, 0)).rgb - color;
            float weight = exp(-dot(offset, offset) / (2 * SpatialSigma * SpatialSigma)) *
                           (exp(-dot(difference, difference) / (2 * ColorSigma * ColorSigma)) + 1e-3);
            sum += weight * WorkDepthView.Load(int3(texel, 0));
            weights += weight;
        }
    float distance01 = clamp(sum / weights, Nearest, 1);
    Depth[id.xy] = distance01 * FarPlane / (1 + distance01 * (FarPlane - 1));
}
)";

constexpr char kMotionShaders[] = R"(
cbuffer Sizes : register(b0)
{
    uint2 SourceSize;
    uint2 WorkSize;
    float Temporal;
    float3 Padding;
};
Texture2D<float4> Guide : register(t0);
Texture2D<float4> PrevGuide : register(t1);
Texture2D<float> LumaQ : register(t2);
Texture2D<float> PrevLumaQ : register(t3);
Texture2D<float2> FlowCoarse : register(t4);
Texture2D<float4> Flow : register(t5);
Texture2D<float> Prior : register(t6);
StructuredBuffer<float4> Motion : register(t7);
Texture2D<float2> FusedPrev : register(t8);
Texture2D<float2> RawFused : register(t9);
RWTexture2D<float> LumaQTarget : register(u0);
RWTexture2D<float2> FlowCoarseTarget : register(u1);
RWTexture2D<float4> FlowTarget : register(u2);
RWStructuredBuffer<float4> MotionTarget : register(u3);
RWTexture2D<float2> FusedTarget : register(u4);
RWTexture2D<float2> RawTarget : register(u5);

static const float HalfFieldOfView = 0.6108652;
static const float FarPlane = 1000.0;
static const float SkyDistance = 900.0;
static const float NearestStuds = 2.0;
static const int CoarseSearch = 6;
static const int Patch = 2;
static const float LostMatch = 0.12;
static const float MinTravel = 0.02;
static const float PriorUncertainty = 0.35;
static const float PriorFloor = 0.03;
static const float PriorPersistence = 40.0;
static const float Drift = 0.01;
static const float FlowNoise = 0.15;
static const float CornerScale = 400.0;
static const float FitConfidence = 0.2;
static const float TiltDamping = 1.0;
static const int SettleRadius = 5;
static const float SettleDepthSigma = 0.6;
static const float SettleColorSigma = 0.1;

uint2 QuarterSize()
{
    return (WorkSize + 3) / 4;
}

float Square(float value)
{
    return value * value;
}

float Focal()
{
    return WorkSize.y * 0.5 / tan(HalfFieldOfView);
}

[numthreads(16, 16, 1)]
void Quarter(uint3 id : SV_DispatchThreadID)
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
                sum += Guide.Load(int3(p, 0)).a;
                count += 1;
            }
        }
    LumaQTarget[id.xy] = sum / max(count, 1);
}

[numthreads(16, 16, 1)]
void MatchCoarse(uint3 id : SV_DispatchThreadID)
{
    int2 size = int2(QuarterSize());
    int2 p = int2(id.xy);
    if (any(p >= size))
        return;
    float centre[25];
    for (int k = 0; k < 25; ++k)
        centre[k] = LumaQ.Load(int3(clamp(p + int2(k % 5 - Patch, k / 5 - Patch), 0, size - 1), 0));
    float best = 1e9;
    int2 bestOffset = 0;
    for (int dy = -CoarseSearch; dy <= CoarseSearch; ++dy)
        for (int dx = -CoarseSearch; dx <= CoarseSearch; ++dx)
        {
            float cost = 0.002 * (abs(dx) + abs(dy));
            for (int k = 0; k < 25; ++k)
                cost += abs(centre[k] - PrevLumaQ.Load(int3(clamp(p - int2(dx, dy) + int2(k % 5 - Patch, k / 5 - Patch), 0, size - 1), 0)));
            if (cost < best)
            {
                best = cost;
                bestOffset = int2(dx, dy);
            }
        }
    FlowCoarseTarget[id.xy] = float2(bestOffset);
}

float FineCost(int2 p, int2 offset, float centre[25])
{
    int2 size = int2(WorkSize);
    float cost = 0;
    for (int k = 0; k < 25; ++k)
        cost += abs(centre[k] - PrevGuide.Load(int3(clamp(p - offset + int2(k % 5 - Patch, k / 5 - Patch), 0, size - 1), 0)).a);
    return cost / 25;
}

[numthreads(16, 16, 1)]
void MatchFine(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    int2 p = int2(id.xy);
    int2 size = int2(WorkSize);
    float centre[25];
    for (int k = 0; k < 25; ++k)
        centre[k] = Guide.Load(int3(clamp(p + int2(k % 5 - Patch, k / 5 - Patch), 0, size - 1), 0)).a;
    float xx = 0;
    float yy = 0;
    float xy = 0;
    for (int g = 0; g < 9; ++g)
    {
        int gx = g % 3 + 1;
        int gy = g / 3 + 1;
        float dx = centre[gy * 5 + gx + 1] - centre[gy * 5 + gx - 1];
        float dy = centre[(gy + 1) * 5 + gx] - centre[(gy - 1) * 5 + gx];
        xx += dx * dx;
        yy += dy * dy;
        xy += dx * dy;
    }
    float corner = (xx + yy) * 0.5 - sqrt((xx - yy) * (xx - yy) * 0.25 + xy * xy);
    int2 start = int2(round(FlowCoarse.Load(int3(min(id.xy / 4, QuarterSize() - 1), 0)) * 4));
    float costs[25];
    float best = 1e9;
    int bestIndex = 12;
    for (int j = 0; j < 25; ++j)
    {
        float cost = FineCost(p, start + int2(j % 5 - 2, j / 5 - 2), centre);
        costs[j] = cost;
        if (cost < best)
        {
            best = cost;
            bestIndex = j;
        }
    }
    int bx = bestIndex % 5;
    int by = bestIndex / 5;
    float2 sub = 0;
    if (bx > 0 && bx < 4)
    {
        float left = costs[bestIndex - 1];
        float right = costs[bestIndex + 1];
        float curve = left - 2 * best + right;
        if (curve > 1e-5)
            sub.x = clamp(0.5 * (left - right) / curve, -0.5, 0.5);
    }
    if (by > 0 && by < 4)
    {
        float up = costs[bestIndex - 5];
        float down = costs[bestIndex + 5];
        float curve = up - 2 * best + down;
        if (curve > 1e-5)
            sub.y = clamp(0.5 * (up - down) / curve, -0.5, 0.5);
    }
    bool edge = bx == 0 || bx == 4 || by == 0 || by == 4;
    float confidence = saturate(corner * CornerScale) * saturate(1 - best * 12) * (edge ? 0.3 : 1);
    FlowTarget[id.xy] = float4(float2(start + int2(bx - 2, by - 2)) + sub, confidence, best);
}

static const int Unknowns = 6;
static const int SumCount = 27;

groupshared float Sums[256][SumCount];

void MotionRows(float x, float y, float z, float focal, out float rowX[Unknowns], out float rowY[Unknowns])
{
    rowX[0] = x * y / focal;
    rowX[1] = -(focal + x * x / focal);
    rowX[2] = 0;
    rowX[3] = -focal / z;
    rowX[4] = 0;
    rowX[5] = x / z;
    rowY[0] = focal + y * y / focal;
    rowY[1] = -x * y / focal;
    rowY[2] = 0;
    rowY[3] = 0;
    rowY[4] = -focal / z;
    rowY[5] = y / z;
}

float2 RotationFlow(float x, float y, float focal, float3 rotation)
{
    return float2(x * y / focal * rotation.x - (focal + x * x / focal) * rotation.y + y * rotation.z,
                  (focal + y * y / focal) * rotation.x - x * y / focal * rotation.y - x * rotation.z);
}

[numthreads(256, 1, 1)]
void Fit(uint3 id : SV_DispatchThreadID, uint thread : SV_GroupIndex)
{
    float sums[SumCount];
    for (int i = 0; i < SumCount; ++i)
        sums[i] = 0;
    float focal = Focal();
    float2 centre = WorkSize * 0.5;
    uint columns = WorkSize.x / 2;
    uint count = columns * (WorkSize.y / 2);
    for (uint n = thread; n < count; n += 256)
    {
        uint2 p = uint2(n % columns, n / columns) * 2;
        float4 flow = Flow.Load(int3(p, 0));
        float known = FusedPrev.Load(int3(p, 0)).x;
        float z = max((known > 0 ? known : Prior.Load(int3(p, 0))) * FarPlane, NearestStuds);
        float weight = Prior.Load(int3(p, 0)) * FarPlane > SkyDistance || flow.z < FitConfidence ? 0 : flow.z * (p.y > WorkSize.y * 0.65 ? 1.0 : 0.15);
        if (weight <= 0.01)
            continue;
        float rowX[Unknowns];
        float rowY[Unknowns];
        MotionRows(p.x + 0.5 - centre.x, p.y + 0.5 - centre.y, z, focal, rowX, rowY);
        int k = 0;
        for (int a = 0; a < Unknowns; ++a)
        {
            for (int b = a; b < Unknowns; ++b)
                sums[k++] += weight * (rowX[a] * rowX[b] + rowY[a] * rowY[b]);
            sums[21 + a] += weight * (rowX[a] * flow.x + rowY[a] * flow.y);
        }
    }
    for (int s = 0; s < SumCount; ++s)
        Sums[thread][s] = sums[s];
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 128; step > 0; step >>= 1)
    {
        if (thread < step)
            for (int s2 = 0; s2 < SumCount; ++s2)
                Sums[thread][s2] += Sums[thread + step][s2];
        GroupMemoryBarrierWithGroupSync();
    }
    if (thread != 0)
        return;
    float m[Unknowns][Unknowns + 1];
    int k2 = 0;
    for (int a2 = 0; a2 < Unknowns; ++a2)
        for (int b2 = a2; b2 < Unknowns; ++b2)
        {
            m[a2][b2] = Sums[0][k2];
            m[b2][a2] = Sums[0][k2];
            ++k2;
        }
    float trace = 0;
    for (int r = 0; r < Unknowns; ++r)
    {
        m[r][Unknowns] = Sums[0][21 + r];
        trace += m[r][r];
    }
    for (int r2 = 0; r2 < Unknowns; ++r2)
        m[r2][r2] += trace * 1e-5 + 1e-6;
    m[0][0] *= 1 + TiltDamping;
    m[4][4] *= 1 + TiltDamping;
    for (int c = 0; c < Unknowns; ++c)
    {
        float pivot = m[c][c];
        for (int j2 = c; j2 <= Unknowns; ++j2)
            m[c][j2] /= pivot;
        for (int r3 = 0; r3 < Unknowns; ++r3)
        {
            if (r3 == c)
                continue;
            float factor = m[r3][c];
            for (int j3 = c; j3 <= Unknowns; ++j3)
                m[r3][j3] -= factor * m[c][j3];
        }
    }
    float4 first = float4(m[0][Unknowns], m[1][Unknowns], m[2][Unknowns], m[3][Unknowns]);
    float2 second = float2(m[4][Unknowns], m[5][Unknowns]);
    float travel = length(float3(first.w, second));
    bool valid = Sums[0][0] > 1e-3 && travel > MinTravel && all(isfinite(first)) && all(isfinite(second));
    MotionTarget[0] = first;
    MotionTarget[1] = float4(second, valid ? 1 : 0, travel);
}

float2 LoadPrevious(float2 position)
{
    float2 texel = position - 0.5;
    if (any(texel < -0.5) || any(texel > float2(WorkSize) - 0.5))
        return -1;
    int2 base = int2(floor(texel));
    float2 t = texel - base;
    int2 high = int2(WorkSize) - 1;
    float2 a = FusedPrev.Load(int3(clamp(base, 0, high), 0));
    float2 b = FusedPrev.Load(int3(clamp(base + int2(1, 0), 0, high), 0));
    float2 c = FusedPrev.Load(int3(clamp(base + int2(0, 1), 0, high), 0));
    float2 e = FusedPrev.Load(int3(clamp(base + int2(1, 1), 0, high), 0));
    if (min(min(a.x, b.x), min(c.x, e.x)) <= 0)
        return -1;
    float2 inverse = lerp(lerp(float2(1 / a.x, a.y), float2(1 / b.x, b.y), t.x), lerp(float2(1 / c.x, c.y), float2(1 / e.x, e.y), t.x), t.y);
    return float2(1 / inverse.x, inverse.y);
}

[numthreads(16, 16, 1)]
void Fuse(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    float prior = max(Prior.Load(int3(id.xy, 0)) * FarPlane, NearestStuds);
    float priorInverse = 1 / prior;
    float priorVariance = Square(PriorUncertainty * priorInverse) + (prior > SkyDistance ? Square(PriorFloor) : 0);
    float4 flow = Flow.Load(int3(id.xy, 0));
    float2 previous = LoadPrevious(id.xy + 0.5 - flow.xy);
    float inverse = priorInverse;
    float variance = priorVariance;
    if (previous.x > 0 && flow.w <= LostMatch)
    {
        inverse = 1 / (previous.x * FarPlane);
        variance = previous.y + Square(Drift * inverse);
    }
    float settleVariance = priorVariance * PriorPersistence;
    inverse = (inverse * settleVariance + priorInverse * variance) / (variance + settleVariance);
    variance = variance * settleVariance / (variance + settleVariance);
    float4 first = Motion[0];
    float4 second = Motion[1];
    if (second.z > 0 && flow.z > 0.01)
    {
        float focal = Focal();
        float2 centre = WorkSize * 0.5;
        float x = id.x + 0.5 - centre.x;
        float y = id.y + 0.5 - centre.y;
        float2 direction = float2(x * second.y - focal * first.w, y * second.y - focal * second.x);
        float2 residual = flow.xy - RotationFlow(x, y, focal, first.xyz);
        float strength = dot(direction, direction);
        if (strength > 1e-8)
        {
            float length = sqrt(strength);
            float measured = max(dot(direction, residual) / strength, 1 / FarPlane);
            float across = abs(direction.x * residual.y - direction.y * residual.x) / length;
            float spread = FlowNoise + across;
            float measuredVariance = Square(spread / length) / max(flow.z, 0.05);
            inverse = (inverse * measuredVariance + measured * variance) / (variance + measuredVariance);
            variance = variance * measuredVariance / (variance + measuredVariance);
        }
    }
    float distance = clamp(1 / max(inverse, 1 / FarPlane), NearestStuds, FarPlane);
    RawTarget[id.xy] = float2(distance / FarPlane, variance);
}

[numthreads(16, 16, 1)]
void Settle(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    int2 high = int2(WorkSize) - 1;
    float3 color = Guide.Load(int3(id.xy, 0)).rgb;
    float2 raw = RawFused.Load(int3(id.xy, 0));
    float centre = log(max(raw.x * FarPlane, NearestStuds));
    float sum = 0;
    float weights = 0;
    for (int y = -SettleRadius; y <= SettleRadius; ++y)
        for (int x = -SettleRadius; x <= SettleRadius; ++x)
        {
            int2 texel = clamp(int2(id.xy) + int2(x, y), 0, high);
            float value = log(max(RawFused.Load(int3(texel, 0)).x * FarPlane, NearestStuds));
            float3 difference = Guide.Load(int3(texel, 0)).rgb - color;
            float gap = (value - centre) / SettleDepthSigma;
            float weight = exp(-(x * x + y * y) / (2 * SettleRadius * SettleRadius)) *
                           exp(-dot(difference, difference) / (2 * SettleColorSigma * SettleColorSigma)) *
                           saturate(1 - gap * gap);
            sum += weight * value;
            weights += weight;
        }
    FusedTarget[id.xy] = float2(clamp(exp(sum / max(weights, 1e-6)), NearestStuds, FarPlane) / FarPlane, raw.y);
}
)";

struct ShaderEntry
{
    const char* source;
    size_t length;
    const char* entry;
};

constexpr ShaderEntry kEntryPoints[] = {
    { kShaders, sizeof(kShaders) - 1, "Reduce" },
    { kShaders, sizeof(kShaders) - 1, "Blur" },
    { kShaders, sizeof(kShaders) - 1, "Sky" },
    { kShaders, sizeof(kShaders) - 1, "Horizon" },
    { kShaders, sizeof(kShaders) - 1, "Columns" },
    { kShaders, sizeof(kShaders) - 1, "Upsample" },
    { kMotionShaders, sizeof(kMotionShaders) - 1, "Quarter" },
    { kMotionShaders, sizeof(kMotionShaders) - 1, "MatchCoarse" },
    { kMotionShaders, sizeof(kMotionShaders) - 1, "MatchFine" },
    { kMotionShaders, sizeof(kMotionShaders) - 1, "Fit" },
    { kMotionShaders, sizeof(kMotionShaders) - 1, "Fuse" },
    { kMotionShaders, sizeof(kMotionShaders) - 1, "Settle" },
};

enum Shader : size_t
{
    ReduceShader,
    BlurShader,
    SkyShader,
    HorizonShader,
    ColumnsShader,
    UpsampleShader,
    QuarterShader,
    MatchCoarseShader,
    MatchFineShader,
    FitShader,
    FuseShader,
    SettleShader,
};

constexpr float kTemporal = 0.55f;

struct Depth
{
    bool enabled = false;
    int size = 0;
    UINT workWidth = 0;
    UINT workHeight = 0;
    UINT sourceWidth = 0;
    UINT sourceHeight = 0;
    std::optional<int64_t> lastInput;
    std::array<winrt::com_ptr<ID3DBlob>, std::size(kEntryPoints)> code;
    std::array<winrt::com_ptr<ID3D11ComputeShader>, std::size(kEntryPoints)> shaders;
    winrt::com_ptr<ID3D11Buffer> sizes;
    winrt::com_ptr<ID3D11Texture2D> frameCopy;
    winrt::com_ptr<ID3D11ShaderResourceView> frameView;
    winrt::com_ptr<ID3D11Texture2D> guide;
    winrt::com_ptr<ID3D11UnorderedAccessView> guideTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> guideView;
    winrt::com_ptr<ID3D11Texture2D> blurred;
    winrt::com_ptr<ID3D11UnorderedAccessView> blurredTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> blurredView;
    winrt::com_ptr<ID3D11Buffer> skyEnd;
    winrt::com_ptr<ID3D11UnorderedAccessView> skyEndTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> skyEndView;
    winrt::com_ptr<ID3D11Buffer> scene;
    winrt::com_ptr<ID3D11UnorderedAccessView> sceneTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> sceneView;
    winrt::com_ptr<ID3D11Texture2D> workDepth;
    winrt::com_ptr<ID3D11UnorderedAccessView> workDepthTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> workDepthView;
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::com_ptr<ID3D11UnorderedAccessView> textureTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> view;
    winrt::com_ptr<ID3D11Texture2D> previousGuide;
    winrt::com_ptr<ID3D11ShaderResourceView> previousGuideView;
    winrt::com_ptr<ID3D11Texture2D> lumaQuarter;
    winrt::com_ptr<ID3D11UnorderedAccessView> lumaQuarterTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> lumaQuarterView;
    winrt::com_ptr<ID3D11Texture2D> previousLumaQuarter;
    winrt::com_ptr<ID3D11ShaderResourceView> previousLumaQuarterView;
    winrt::com_ptr<ID3D11Texture2D> flowCoarse;
    winrt::com_ptr<ID3D11UnorderedAccessView> flowCoarseTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> flowCoarseView;
    winrt::com_ptr<ID3D11Texture2D> flow;
    winrt::com_ptr<ID3D11UnorderedAccessView> flowTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> flowView;
    winrt::com_ptr<ID3D11Buffer> motion;
    winrt::com_ptr<ID3D11UnorderedAccessView> motionTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> motionView;
    std::array<winrt::com_ptr<ID3D11Texture2D>, 2> fused;
    std::array<winrt::com_ptr<ID3D11UnorderedAccessView>, 2> fusedTarget;
    std::array<winrt::com_ptr<ID3D11ShaderResourceView>, 2> fusedView;
    winrt::com_ptr<ID3D11Texture2D> rawFused;
    winrt::com_ptr<ID3D11UnorderedAccessView> rawFusedTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> rawFusedView;
    size_t fusedIndex = 0;
    bool hasPrevious = false;
    std::vector<reshade::api::effect_runtime*> runtimes;
};
Depth d;

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

void OnInitRuntime(reshade::api::effect_runtime* runtime) noexcept
{
    try
    {
        d.runtimes.push_back(runtime);
        if (d.view)
            Bind(runtime, d.view.get());
    }
    catch (...)
    {
        Log(LogLevel::Error, L"The depth add-on could not attach to a ReShade runtime.");
    }
}

void OnDestroyRuntime(reshade::api::effect_runtime* runtime) noexcept
{
    d.runtimes.erase(std::remove(d.runtimes.begin(), d.runtimes.end(), runtime), d.runtimes.end());
}

void OnReloadedEffects(reshade::api::effect_runtime* runtime) noexcept
{
    try
    {
        if (d.view)
            Bind(runtime, d.view.get());
    }
    catch (...)
    {
        Log(LogLevel::Error, L"The depth add-on could not rebind depth after ReShade reloaded effects.");
    }
}

void CreateShaders()
{
    for (size_t i = 0; i < std::size(kEntryPoints); ++i)
        winrt::check_hresult(g.device->CreateComputeShader(d.code[i]->GetBufferPointer(), d.code[i]->GetBufferSize(), nullptr, d.shaders[i].put()));

    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = 32;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    winrt::check_hresult(g.device->CreateBuffer(&desc, nullptr, d.sizes.put()));
}

void CompileShaders()
{
    for (size_t i = 0; i < std::size(kEntryPoints); ++i)
    {
        winrt::com_ptr<ID3DBlob> errors;
        const HRESULT hr = D3DCompile(kEntryPoints[i].source, kEntryPoints[i].length, "depth", nullptr, nullptr, kEntryPoints[i].entry, "cs_5_0",
                                      D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, d.code[i].put(), errors.put());
        if (FAILED(hr))
            throw std::runtime_error(errors ? static_cast<const char*>(errors->GetBufferPointer()) : "compute shaders are unavailable on this GPU");
    }
    CreateShaders();
}

void Dispatch(Shader shader, std::array<ID3D11ShaderResourceView*, 10> views, std::array<ID3D11UnorderedAccessView*, 6> targets, UINT x, UINT y)
{
    ID3D11Buffer* constants[] = { d.sizes.get() };
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

void ReleaseTargets()
{
    for (auto* runtime : d.runtimes)
        Bind(runtime, nullptr);
    for (size_t i = 0; i < 2; ++i)
    {
        d.fusedView[i] = nullptr;
        d.fusedTarget[i] = nullptr;
        d.fused[i] = nullptr;
    }
    d.rawFusedView = nullptr;
    d.rawFusedTarget = nullptr;
    d.rawFused = nullptr;
    d.motionView = nullptr;
    d.motionTarget = nullptr;
    d.motion = nullptr;
    d.flowView = nullptr;
    d.flowTarget = nullptr;
    d.flow = nullptr;
    d.flowCoarseView = nullptr;
    d.flowCoarseTarget = nullptr;
    d.flowCoarse = nullptr;
    d.previousLumaQuarterView = nullptr;
    d.previousLumaQuarter = nullptr;
    d.lumaQuarterView = nullptr;
    d.lumaQuarterTarget = nullptr;
    d.lumaQuarter = nullptr;
    d.previousGuideView = nullptr;
    d.previousGuide = nullptr;
    d.hasPrevious = false;
    d.view = nullptr;
    d.textureTarget = nullptr;
    d.texture = nullptr;
    d.workDepthView = nullptr;
    d.workDepthTarget = nullptr;
    d.workDepth = nullptr;
    d.sceneView = nullptr;
    d.sceneTarget = nullptr;
    d.scene = nullptr;
    d.skyEndView = nullptr;
    d.skyEndTarget = nullptr;
    d.skyEnd = nullptr;
    d.blurredView = nullptr;
    d.blurredTarget = nullptr;
    d.blurred = nullptr;
    d.guideView = nullptr;
    d.guideTarget = nullptr;
    d.guide = nullptr;
}

void ReleaseResources()
{
    ReleaseTargets();
    d.frameView = nullptr;
    d.frameCopy = nullptr;
    d.sizes = nullptr;
    for (auto& shader : d.shaders)
        shader = nullptr;
    d.sourceWidth = 0;
    d.sourceHeight = 0;
    d.lastInput.reset();
}

void Stop()
{
    ReleaseResources();
    d.enabled = false;
    Report(LogLevel::Warning, L"Depth estimation stopped, color effects keep working.");
}

void CreateBuffer(UINT elements, UINT stride, winrt::com_ptr<ID3D11Buffer>& buffer, winrt::com_ptr<ID3D11UnorderedAccessView>& target, winrt::com_ptr<ID3D11ShaderResourceView>& view)
{
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = elements * stride;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    desc.StructureByteStride = stride;
    winrt::check_hresult(g.device->CreateBuffer(&desc, nullptr, buffer.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(buffer.get(), nullptr, target.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(buffer.get(), nullptr, view.put()));
}

void CreateTexture(UINT width, UINT height, DXGI_FORMAT format, winrt::com_ptr<ID3D11Texture2D>& texture, winrt::com_ptr<ID3D11UnorderedAccessView>& target, winrt::com_ptr<ID3D11ShaderResourceView>& view)
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
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, texture.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(texture.get(), nullptr, target.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(texture.get(), nullptr, view.put()));
}

void CreateReadable(UINT width, UINT height, DXGI_FORMAT format, winrt::com_ptr<ID3D11Texture2D>& texture, winrt::com_ptr<ID3D11ShaderResourceView>& view)
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc = { 1, 0 };
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, texture.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(texture.get(), nullptr, view.put()));
}

void Resize(const D3D11_TEXTURE2D_DESC& frame)
{
    ReleaseTargets();
    d.size = DepthSize();
    const bool landscape = frame.Width >= frame.Height;
    const float ratio = landscape ? static_cast<float>(frame.Height) / frame.Width : static_cast<float>(frame.Width) / frame.Height;
    const UINT longSide = static_cast<UINT>(std::clamp(d.size, 64, 1024));
    const UINT shortSide = std::max(16u, static_cast<UINT>(std::lround(longSide * ratio)));
    d.workWidth = std::min(landscape ? longSide : shortSide, frame.Width);
    d.workHeight = std::min(landscape ? shortSide : longSide, frame.Height);
    d.sourceWidth = frame.Width;
    d.sourceHeight = frame.Height;
    d.lastInput.reset();
    Log(LogLevel::Info, L"Depth resize: source=%ux%u, work=%ux%u, detail=%d.", frame.Width, frame.Height, d.workWidth, d.workHeight, d.size);

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

    CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R16G16B16A16_FLOAT, d.guide, d.guideTarget, d.guideView);
    CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R16G16B16A16_FLOAT, d.blurred, d.blurredTarget, d.blurredView);
    CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R32_FLOAT, d.workDepth, d.workDepthTarget, d.workDepthView);
    CreateTexture(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32_FLOAT, d.texture, d.textureTarget, d.view);
    CreateBuffer(d.workWidth, sizeof(UINT), d.skyEnd, d.skyEndTarget, d.skyEndView);
    CreateBuffer(2, 4 * sizeof(float), d.scene, d.sceneTarget, d.sceneView);
    const UINT quarterWidth = (d.workWidth + 3) / 4;
    const UINT quarterHeight = (d.workHeight + 3) / 4;
    CreateReadable(d.workWidth, d.workHeight, DXGI_FORMAT_R16G16B16A16_FLOAT, d.previousGuide, d.previousGuideView);
    CreateTexture(quarterWidth, quarterHeight, DXGI_FORMAT_R16_FLOAT, d.lumaQuarter, d.lumaQuarterTarget, d.lumaQuarterView);
    CreateReadable(quarterWidth, quarterHeight, DXGI_FORMAT_R16_FLOAT, d.previousLumaQuarter, d.previousLumaQuarterView);
    CreateTexture(quarterWidth, quarterHeight, DXGI_FORMAT_R32G32_FLOAT, d.flowCoarse, d.flowCoarseTarget, d.flowCoarseView);
    CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R32G32B32A32_FLOAT, d.flow, d.flowTarget, d.flowView);
    CreateBuffer(2, 4 * sizeof(float), d.motion, d.motionTarget, d.motionView);
    for (size_t i = 0; i < 2; ++i)
        CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R32G32_FLOAT, d.fused[i], d.fusedTarget[i], d.fusedView[i]);
    CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R32G32_FLOAT, d.rawFused, d.rawFusedTarget, d.rawFusedView);

    const float zero[4] = {};
    const UINT zeroUint[4] = {};
    g.context->ClearUnorderedAccessViewFloat(d.workDepthTarget.get(), zero);
    for (const auto& target : d.fusedTarget)
        g.context->ClearUnorderedAccessViewFloat(target.get(), zero);
    g.context->ClearUnorderedAccessViewUint(d.sceneTarget.get(), zeroUint);
    const float farPlane[4] = { 1, 1, 1, 1 };
    g.context->ClearUnorderedAccessViewFloat(d.textureTarget.get(), farPlane);

    struct
    {
        UINT sourceWidth, sourceHeight, workWidth, workHeight;
        float temporal, padding[3];
    } sizes{ d.sourceWidth, d.sourceHeight, d.workWidth, d.workHeight, kTemporal, {} };
    g.context->UpdateSubresource(d.sizes.get(), 0, nullptr, &sizes, 0, 0);
    for (auto* runtime : d.runtimes)
        Bind(runtime, d.view.get());
}

void Estimate()
{
    const UINT workX = (d.workWidth + 15) / 16;
    const UINT workY = (d.workHeight + 15) / 16;
    const UINT columns = (d.workWidth + 63) / 64;
    const UINT quarterX = ((d.workWidth + 3) / 4 + 15) / 16;
    const UINT quarterY = ((d.workHeight + 3) / 4 + 15) / 16;
    ID3D11ShaderResourceView* guide = d.guideView.get();
    ID3D11ShaderResourceView* blurred = d.blurredView.get();
    Dispatch(ReduceShader, { d.frameView.get() }, { d.guideTarget.get() }, workX, workY);
    Dispatch(BlurShader, { nullptr, guide }, { nullptr, nullptr, nullptr, nullptr, nullptr, d.blurredTarget.get() }, workX, workY);
    Dispatch(SkyShader, { nullptr, nullptr, nullptr, nullptr, nullptr, blurred }, { nullptr, d.skyEndTarget.get() }, columns, 1);
    Dispatch(HorizonShader, { nullptr, nullptr, d.skyEndView.get(), nullptr, nullptr, blurred }, { nullptr, nullptr, d.sceneTarget.get() }, 1, 1);
    Dispatch(ColumnsShader, { nullptr, nullptr, d.skyEndView.get(), d.sceneView.get(), nullptr, blurred }, { nullptr, nullptr, nullptr, d.workDepthTarget.get() }, columns, 1);
    Dispatch(QuarterShader, { guide }, { d.lumaQuarterTarget.get() }, quarterX, quarterY);
    if (!d.hasPrevious)
    {
        g.context->CopyResource(d.previousGuide.get(), d.guide.get());
        g.context->CopyResource(d.previousLumaQuarter.get(), d.lumaQuarter.get());
        d.hasPrevious = true;
    }
    const size_t current = d.fusedIndex;
    const size_t previous = current ^ 1;
    Dispatch(MatchCoarseShader, { nullptr, nullptr, d.lumaQuarterView.get(), d.previousLumaQuarterView.get() }, { nullptr, d.flowCoarseTarget.get() }, quarterX, quarterY);
    Dispatch(MatchFineShader, { guide, d.previousGuideView.get(), nullptr, nullptr, d.flowCoarseView.get() }, { nullptr, nullptr, d.flowTarget.get() }, workX, workY);
    Dispatch(FitShader, { nullptr, nullptr, nullptr, nullptr, nullptr, d.flowView.get(), d.workDepthView.get(), nullptr, d.fusedView[previous].get() },
             { nullptr, nullptr, nullptr, d.motionTarget.get() }, 1, 1);
    Dispatch(FuseShader, { nullptr, nullptr, nullptr, nullptr, nullptr, d.flowView.get(), d.workDepthView.get(), d.motionView.get(), d.fusedView[previous].get() },
             { nullptr, nullptr, nullptr, nullptr, nullptr, d.rawFusedTarget.get() }, workX, workY);
    Dispatch(SettleShader, { guide, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, nullptr, d.rawFusedView.get() },
             { nullptr, nullptr, nullptr, nullptr, d.fusedTarget[current].get() }, workX, workY);
    Dispatch(UpsampleShader, { d.frameView.get(), guide, nullptr, nullptr, d.fusedView[current].get() }, { nullptr, nullptr, nullptr, nullptr, d.textureTarget.get() },
             (d.sourceWidth + 15) / 16, (d.sourceHeight + 15) / 16);
    g.context->CopyResource(d.previousGuide.get(), d.guide.get());
    g.context->CopyResource(d.previousLumaQuarter.get(), d.lumaQuarter.get());
    d.fusedIndex = previous;
}
}

bool SceneInitDepth()
{
    if (!AddonRegistered())
    {
        Log(LogLevel::Warning, L"Depth estimation is off because ReShade did not load the depth add-on.");
        return false;
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
        return false;
    }
    d.enabled = true;
    Log(LogLevel::Info, L"Scene depth estimation initialized, depth_size=%d.", DepthSize());
    return true;
}

void SceneUpdateDepth(ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (!d.enabled)
        return;
    try
    {
        if (!d.shaders[0])
            CreateShaders();
        D3D11_TEXTURE2D_DESC desc{};
        frame->GetDesc(&desc);
        if (desc.Width != d.sourceWidth || desc.Height != d.sourceHeight || d.size != DepthSize() || !d.texture)
            Resize(desc);
        if (d.lastInput && *d.lastInput == frameTimestamp)
            return;
        g.context->CopyResource(d.frameCopy.get(), frame);
        Estimate();
        d.lastInput = frameTimestamp;
    }
    catch (const winrt::hresult_error& e)
    {
        if (DeviceLost(e.code()))
            throw;
        Log(LogLevel::Error, L"Depth estimation stopped: %ls (0x%08X)", e.message().c_str(), static_cast<unsigned>(e.code()));
        Stop();
    }
}

bool SceneDepthEnabled()
{
    return d.enabled;
}

void SceneResetDepthInput()
{
    d.lastInput.reset();
}

void SceneLogDepthDiagnostics()
{
    Log(LogLevel::Info, L"Depth state: enabled=%d, source=%ux%u, work=%ux%u.", d.enabled, d.sourceWidth, d.sourceHeight, d.workWidth, d.workHeight);
}

void SceneReleaseDepthDevice()
{
    ReleaseResources();
}

void SceneShutdownDepth()
{
    ReleaseResources();
    d.enabled = false;
}
