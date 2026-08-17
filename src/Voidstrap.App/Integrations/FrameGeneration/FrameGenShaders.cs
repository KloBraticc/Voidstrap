namespace Voidstrap.Integrations.FrameGeneration
{
    internal static class FrameGenShaders
    {
        public const string Source = @"
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

cbuffer FgParams : register(b0)
{
    float4 dims;
    float4 srcRect;
    float4 interp;
};

Texture2D tex0 : register(t0);
Texture2D tex1 : register(t1);
Texture2D tex2 : register(t2);
Texture2D tex3 : register(t3);
Texture2D tex4 : register(t4);
Texture2D tex5 : register(t5);
Texture2D tex6 : register(t6);
Texture2D tex7 : register(t7);
SamplerState smp : register(s0);

float lum(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

float4 PSPass(VSOut inp) : SV_Target
{
    return float4(tex0.Sample(smp, inp.uv).rgb, 1.0);
}

float4 PSOverlay(VSOut inp) : SV_Target
{
    return tex0.Sample(smp, inp.uv);
}

float4 sampleBicubic(Texture2D source, float2 uv, float2 texel)
{
    float2 size = 1.0 / texel;
    float2 p = uv * size - 0.5;
    float2 f = frac(p);
    p -= f;
    float2 f2 = f * f;
    float2 f3 = f2 * f;
    float2 w0 = -0.5 * f3 + f2 - 0.5 * f;
    float2 w1 = 1.5 * f3 - 2.5 * f2 + 1.0;
    float2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
    float2 w3 = 0.5 * f3 - 0.5 * f2;
    float2 g0 = w0 + w1;
    float2 g1 = w2 + w3;
    float2 h0 = (p + w1 / g0 - 0.5) * texel;
    float2 h1 = (p + w3 / g1 + 1.5) * texel;
    float4 a = source.Sample(smp, float2(h0.x, h0.y));
    float4 b = source.Sample(smp, float2(h1.x, h0.y));
    float4 c = source.Sample(smp, float2(h0.x, h1.y));
    float4 d = source.Sample(smp, float2(h1.x, h1.y));
    return lerp(lerp(a, b, g1.x), lerp(c, d, g1.x), g1.y);
}

float pageBackgroundWeight(float3 p, float3 source)
{
    float d = max(abs(p.r - source.r), max(abs(p.g - source.g), abs(p.b - source.b)));
    return 1.0 - smoothstep(1.0 / 255.0, 10.0 / 255.0, d);
}

float4 PSHomeBackground(VSOut inp) : SV_Target
{
    float3 c = tex0.Sample(smp, inp.uv).rgb;
    float3 source = float3(18.0, 18.0, 21.0) / 255.0;

    float own = pageBackgroundWeight(c, source);

    float2 step = float2(abs(ddx(inp.uv.x)), abs(ddy(inp.uv.y))) * 2.0;
    float support = 0.0;
    [unroll] for (int oy = -1; oy <= 1; oy++)
    {
        [unroll] for (int ox = -1; ox <= 1; ox++)
        {
            support += pageBackgroundWeight(tex0.Sample(smp, inp.uv + float2(ox, oy) * step).rgb, source);
        }
    }
    support /= 9.0;

    float region = smoothstep(0.30, 0.55, support);
    float replace = saturate(own * region);

    float2 mediaUv = (inp.uv - 0.5) * srcRect.xy + 0.5;
    float directionLength = max(abs(srcRect.x) + abs(srcRect.y), 0.0001);
    float gradientPosition = saturate(dot(inp.uv - 0.5, srcRect.xy) / directionLength + 0.5);
    float3 gradient = lerp(interp.rgb, dims.rgb, gradientPosition);
    float3 flatOrGradient = dims.a > 0.5 ? gradient : interp.rgb;
    float3 background = interp.a > 0.5 ? sampleBicubic(tex1, mediaUv, srcRect.zw).rgb : flatOrGradient;

    return float4(saturate(c + replace * (background - source)), 1.0);
}

float4 PSCropSrgb(VSOut inp) : SV_Target
{
    float3 c = tex0.Sample(smp, srcRect.xy + inp.uv * srcRect.zw).rgb;
    c = saturate(c);
    c = pow(c, 1.0 / 2.2);
    return float4(c, 1.0);
}

float4 PSLumaColor(VSOut inp) : SV_Target
{
    float2 px = dims.zw * 0.25;
    float l = 0.0;
    l += lum(tex0.SampleLevel(smp, inp.uv + float2(-px.x, -px.y), 0).rgb);
    l += lum(tex0.SampleLevel(smp, inp.uv + float2(px.x, -px.y), 0).rgb);
    l += lum(tex0.SampleLevel(smp, inp.uv + float2(-px.x, px.y), 0).rgb);
    l += lum(tex0.SampleLevel(smp, inp.uv + float2(px.x, px.y), 0).rgb);
    return float4(l * 0.25, 0.0, 0.0, 1.0);
}

float4 PSLumaDown(VSOut inp) : SV_Target
{
    float2 px = dims.zw * 0.25;
    float l = 0.0;
    l += tex0.SampleLevel(smp, inp.uv + float2(-px.x, -px.y), 0).r;
    l += tex0.SampleLevel(smp, inp.uv + float2(px.x, -px.y), 0).r;
    l += tex0.SampleLevel(smp, inp.uv + float2(-px.x, px.y), 0).r;
    l += tex0.SampleLevel(smp, inp.uv + float2(px.x, px.y), 0).r;
    return float4(l * 0.25, 0.0, 0.0, 1.0);
}

float sadPatch(float2 uvA, float2 uvB, float2 px)
{
    float s = 0.0;
    [unroll] for (int y = -2; y <= 2; y++)
    {
        [unroll] for (int x = -2; x <= 2; x++)
        {
            float2 o = float2(x, y) * px;
            s += abs(tex0.SampleLevel(smp, uvA + o, 0).r - tex1.SampleLevel(smp, uvB + o, 0).r);
        }
    }
    return s;
}

float sadPatchFast(float2 uvA, float2 uvB, float2 px)
{
    float s = 0.0;
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 o = float2(x, y) * px;
            s += abs(tex0.SampleLevel(smp, uvA + o, 0).r - tex1.SampleLevel(smp, uvB + o, 0).r);
        }
    }
    return s;
}

float4 PSFlowCoarseFast(VSOut inp) : SV_Target
{
    float2 px = dims.zw;
    float best = 1e9;
    float2 bestD = float2(0.0, 0.0);
    int R = (int)(interp.y + 0.5);
    if (R < 12) R = 12;
    if (R > 40) R = 40;
    float bias = 0.18 / (float)R;
    int step1 = 4;
    [loop] for (int y1 = -R; y1 <= R; y1 += step1)
    {
        [loop] for (int x1 = -R; x1 <= R; x1 += step1)
        {
            float2 d = float2(x1, y1) * px;
            float s = sadPatchFast(inp.uv, inp.uv + d, px) + (abs((float)x1) + abs((float)y1)) * bias;
            if (s < best) { best = s; bestD = d; }
        }
    }
    int bx2 = (int)(bestD.x / px.x + 0.5);
    int by2 = (int)(bestD.y / px.y + 0.5);
    [loop] for (int y2 = -4; y2 <= 4; y2 += 2)
    {
        [loop] for (int x2 = -4; x2 <= 4; x2 += 2)
        {
            int cx = clamp(bx2 + x2, -R, R);
            int cy = clamp(by2 + y2, -R, R);
            float2 d = float2(cx, cy) * px;
            float s = sadPatchFast(inp.uv, inp.uv + d, px) + (abs((float)cx) + abs((float)cy)) * bias;
            if (s < best) { best = s; bestD = d; }
        }
    }
    int bx3 = (int)(bestD.x / px.x + 0.5);
    int by3 = (int)(bestD.y / px.y + 0.5);
    [loop] for (int y3 = -2; y3 <= 2; y3++)
    {
        [loop] for (int x3 = -2; x3 <= 2; x3++)
        {
            int cx = clamp(bx3 + x3, -R, R);
            int cy = clamp(by3 + y3, -R, R);
            float2 d = float2(cx, cy) * px;
            float s = sadPatchFast(inp.uv, inp.uv + d, px) + (abs((float)cx) + abs((float)cy)) * bias;
            if (s < best) { best = s; bestD = d; }
        }
    }

    return float4(bestD, best / 9.0, 1.0);
}

float4 PSFlowRefineFast(VSOut inp) : SV_Target
{
    float2 px = dims.zw;
    float2 carried = tex2.SampleLevel(smp, inp.uv, 0).xy;
    float best = 1e9;
    float2 bestD = carried;
    [loop] for (int y = -2; y <= 2; y++)
    {
        [loop] for (int x = -2; x <= 2; x++)
        {
            float2 d = carried + float2(x, y) * px;
            float s = sadPatchFast(inp.uv, inp.uv + d, px);
            if (s < best) { best = s; bestD = d; }
        }
    }
    float sZero = sadPatchFast(inp.uv, inp.uv, px);
    if (sZero - 0.4 < best) { best = sZero; bestD = float2(0.0, 0.0); }

    float sL = sadPatchFast(inp.uv, inp.uv + bestD - float2(px.x, 0.0), px);
    float sR = sadPatchFast(inp.uv, inp.uv + bestD + float2(px.x, 0.0), px);
    float dX = sL - 2.0 * best + sR;
    float subX = abs(dX) > 1e-5 ? clamp(0.5 * (sL - sR) / dX, -0.5, 0.5) : 0.0;
    float sU = sadPatchFast(inp.uv, inp.uv + bestD - float2(0.0, px.y), px);
    float sDn = sadPatchFast(inp.uv, inp.uv + bestD + float2(0.0, px.y), px);
    float dY = sU - 2.0 * best + sDn;
    float subY = abs(dY) > 1e-5 ? clamp(0.5 * (sU - sDn) / dY, -0.5, 0.5) : 0.0;
    bestD += float2(subX * px.x, subY * px.y);
    return float4(bestD, best / 9.0, 1.0);
}

float4 PSFlowCoarse(VSOut inp) : SV_Target
{
    float2 px = dims.zw;
    float best = 1e9;
    float2 bestD = float2(0.0, 0.0);
    int R = (int)(interp.y + 0.5);
    if (R < 12) R = 12;
    if (R > 40) R = 40;
    float bias = 0.18 / (float)R;

    // Pass 1: coarse sweep with step=4
    int step1 = 4;
    [loop] for (int y1 = -R; y1 <= R; y1 += step1)
    {
        [loop] for (int x1 = -R; x1 <= R; x1 += step1)
        {
            float2 d = float2(x1, y1) * px;
            float s = sadPatch(inp.uv, inp.uv + d, px) + (abs((float)x1) + abs((float)y1)) * bias;
            if (s < best) { best = s; bestD = d; }
        }
    }

    // Pass 2: refine around best with step=2, radius 4
    int bx2 = (int)(bestD.x / px.x + 0.5);
    int by2 = (int)(bestD.y / px.y + 0.5);
    [loop] for (int y2 = -4; y2 <= 4; y2 += 2)
    {
        [loop] for (int x2 = -4; x2 <= 4; x2 += 2)
        {
            int cx = clamp(bx2 + x2, -R, R);
            int cy = clamp(by2 + y2, -R, R);
            float2 d = float2(cx, cy) * px;
            float s = sadPatch(inp.uv, inp.uv + d, px) + (abs((float)cx) + abs((float)cy)) * bias;
            if (s < best) { best = s; bestD = d; }
        }
    }

    // Pass 3: final refine with step=1, radius 2
    int bx3 = (int)(bestD.x / px.x + 0.5);
    int by3 = (int)(bestD.y / px.y + 0.5);
    [loop] for (int y3 = -2; y3 <= 2; y3++)
    {
        [loop] for (int x3 = -2; x3 <= 2; x3++)
        {
            int cx = clamp(bx3 + x3, -R, R);
            int cy = clamp(by3 + y3, -R, R);
            float2 d = float2(cx, cy) * px;
            float s = sadPatch(inp.uv, inp.uv + d, px) + (abs((float)cx) + abs((float)cy)) * bias;
            if (s < best) { best = s; bestD = d; }
        }
    }

    return float4(bestD, best / 25.0, 1.0);
}

float4 PSFlowRefine(VSOut inp) : SV_Target
{
    float2 px = dims.zw;
    float2 carried = tex2.SampleLevel(smp, inp.uv, 0).xy;
    float best = 1e9;
    float2 bestD = carried;
    [loop] for (int y = -2; y <= 2; y++)
    {
        [loop] for (int x = -2; x <= 2; x++)
        {
            float2 d = carried + float2(x, y) * px;
            float s = sadPatch(inp.uv, inp.uv + d, px);
            if (s < best) { best = s; bestD = d; }
        }
    }
    float sZero = sadPatch(inp.uv, inp.uv, px);
    if (sZero - 0.4 < best) { best = sZero; bestD = float2(0.0, 0.0); }

    float sL = sadPatch(inp.uv, inp.uv + bestD - float2(px.x, 0.0), px);
    float sR = sadPatch(inp.uv, inp.uv + bestD + float2(px.x, 0.0), px);
    float dX = sL - 2.0 * best + sR;
    float subX = abs(dX) > 1e-5 ? clamp(0.5 * (sL - sR) / dX, -0.5, 0.5) : 0.0;
    float sU = sadPatch(inp.uv, inp.uv + bestD - float2(0.0, px.y), px);
    float sDn = sadPatch(inp.uv, inp.uv + bestD + float2(0.0, px.y), px);
    float dY = sU - 2.0 * best + sDn;
    float subY = abs(dY) > 1e-5 ? clamp(0.5 * (sU - sDn) / dY, -0.5, 0.5) : 0.0;
    bestD += float2(subX * px.x, subY * px.y);
    return float4(bestD, best / 25.0, 1.0);
}

float sadPatch7(float2 uvA, float2 uvB, float2 px)
{
    float s = 0.0;
    [unroll] for (int y = -3; y <= 3; y++)
    {
        [unroll] for (int x = -3; x <= 3; x++)
        {
            float2 o = float2(x, y) * px;
            s += abs(tex0.SampleLevel(smp, uvA + o, 0).r - tex1.SampleLevel(smp, uvB + o, 0).r);
        }
    }
    return s;
}

float4 PSFlowRefineFine(VSOut inp) : SV_Target
{
    float2 px = dims.zw;
    float2 carried = tex2.SampleLevel(smp, inp.uv, 0).xy;
    float best = 1e9;
    float2 bestD = carried;
    [loop] for (int y = -2; y <= 2; y++)
    {
        [loop] for (int x = -2; x <= 2; x++)
        {
            float2 d = carried + float2(x, y) * px;
            float s = sadPatch7(inp.uv, inp.uv + d, px);
            if (s < best) { best = s; bestD = d; }
        }
    }
    float sZero = sadPatch7(inp.uv, inp.uv, px);
    if (sZero - 0.8 < best) { best = sZero; bestD = float2(0.0, 0.0); }

    float sL = sadPatch7(inp.uv, inp.uv + bestD - float2(px.x, 0.0), px);
    float sR = sadPatch7(inp.uv, inp.uv + bestD + float2(px.x, 0.0), px);
    float dX = sL - 2.0 * best + sR;
    float subX = abs(dX) > 1e-5 ? clamp(0.5 * (sL - sR) / dX, -0.5, 0.5) : 0.0;
    float sU = sadPatch7(inp.uv, inp.uv + bestD - float2(0.0, px.y), px);
    float sDn = sadPatch7(inp.uv, inp.uv + bestD + float2(0.0, px.y), px);
    float dY = sU - 2.0 * best + sDn;
    float subY = abs(dY) > 1e-5 ? clamp(0.5 * (sU - sDn) / dY, -0.5, 0.5) : 0.0;
    bestD += float2(subX * px.x, subY * px.y);
    return float4(bestD, best / 49.0, 1.0);
}

float4 PSFlowSmooth(VSOut inp) : SV_Target
{
    float2 px = dims.zw;
    float centerL = tex1.SampleLevel(smp, inp.uv, 0).r;
    float2 acc = float2(0.0, 0.0);
    float wsum = 0.0;
    float sadC = 0.0;
    [unroll] for (int y = -1; y <= 1; y++)
    {
        [unroll] for (int x = -1; x <= 1; x++)
        {
            float2 off = float2(x, y) * px;
            float4 fl = tex0.SampleLevel(smp, inp.uv + off, 0);
            float nl = tex1.SampleLevel(smp, inp.uv + off, 0).r;
            float wEdge = exp(-abs(nl - centerL) * 16.0);
            float wConf = 1.0 / (fl.z + 0.03);
            float w = wEdge * wConf;
            acc += fl.xy * w;
            wsum += w;
            if (x == 0 && y == 0) sadC = fl.z;
        }
    }
    float2 cur = acc / max(wsum, 1e-5);
    float wPrev = interp.y;
    if (wPrev > 0.001)
    {
        float2 prevF = tex2.SampleLevel(smp, inp.uv, 0).xy;
        float sim = saturate(1.0 - length(cur - prevF) / (length(cur) * 0.5 + 12.0 * px.x));
        float direction = dot(cur, prevF) / max(length(cur) * length(prevF), 1e-7);
        float acceleration = length(cur - prevF) / (length(cur) + length(prevF) + 8.0 * length(px));
        sim *= smoothstep(-0.1, 0.35, direction) * (1.0 - smoothstep(0.45, 1.0, acceleration));
        cur = lerp(cur, prevF, wPrev * sim);
    }
    return float4(cur, sadC, 1.0);
}

float4 PSFlowSmoothWide(VSOut inp) : SV_Target
{
    float2 px = dims.zw;
    float centerL = tex1.SampleLevel(smp, inp.uv, 0).r;
    float2 acc = float2(0.0, 0.0);
    float wsum = 0.0;
    float sadC = 0.0;
    [unroll] for (int y = -2; y <= 2; y++)
    {
        [unroll] for (int x = -2; x <= 2; x++)
        {
            float2 off = float2(x, y) * px;
            float4 fl = tex0.SampleLevel(smp, inp.uv + off, 0);
            float nl = tex1.SampleLevel(smp, inp.uv + off, 0).r;
            float wEdge = exp(-abs(nl - centerL) * 16.0);
            float wConf = 1.0 / (fl.z + 0.03);
            float wDist = 1.0 / (1.0 + 0.35 * (abs((float)x) + abs((float)y)));
            float w = wEdge * wConf * wDist;
            acc += fl.xy * w;
            wsum += w;
            if (x == 0 && y == 0) sadC = fl.z;
        }
    }
    float2 cur = acc / max(wsum, 1e-5);
    float wPrev = interp.y;
    if (wPrev > 0.001)
    {
        float2 prevF = tex2.SampleLevel(smp, inp.uv, 0).xy;
        float sim = saturate(1.0 - length(cur - prevF) / (length(cur) * 0.5 + 12.0 * px.x));
        float direction = dot(cur, prevF) / max(length(cur) * length(prevF), 1e-7);
        float acceleration = length(cur - prevF) / (length(cur) + length(prevF) + 8.0 * length(px));
        sim *= smoothstep(-0.1, 0.35, direction) * (1.0 - smoothstep(0.45, 1.0, acceleration));
        cur = lerp(cur, prevF, wPrev * sim);
    }
    return float4(cur, sadC, 1.0);
}

float4 PSWarpFast(VSOut inp) : SV_Target
{
    float t = interp.x;
    float2 gF = tex3.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float2 gB = tex5.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float2 gPF = tex6.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float2 gPB = tex7.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float4 fF = tex2.SampleLevel(smp, inp.uv, 0);
    float4 fB = tex4.SampleLevel(smp, inp.uv, 0);
    float limScale = max(1.0, interp.y);
    float confF = saturate(1.0 - fF.z * 2.8);
    float confB = saturate(1.0 - fB.z * 2.8);
    float2 lim = dims.zw * 18.0 * limScale;
    float2 localF = clamp((fF.xy - gF) * confF, -lim, lim);
    float2 localB = clamp((fB.xy - gB) * confB, -lim, lim);
    float2 accF = clamp((gF - gPF) * 0.75, -lim, lim);
    float2 accB = clamp((gB - gPB) * 0.75, -lim, lim);
    float2 flF = gF + localF;
    float2 flB = gB + localB;
    float2 reverseF = tex4.SampleLevel(smp, inp.uv + flF, 0).xy;
    float2 reverseB = tex2.SampleLevel(smp, inp.uv + flB, 0).xy;
    float consF = saturate(1.1 - length(flF + reverseF) / (length(flF) + length(reverseF) + 6.0 * length(dims.zw)) * 1.2);
    float consB = saturate(1.1 - length(flB + reverseB) / (length(flB) + length(reverseB) + 6.0 * length(dims.zw)) * 1.2);
    float lc = lum(tex1.Sample(smp, inp.uv).rgb);
    float edge = saturate((abs(lc - lum(tex1.Sample(smp, inp.uv + float2(dims.z, 0.0)).rgb)) + abs(lc - lum(tex1.Sample(smp, inp.uv + float2(0.0, dims.w)).rgb))) * 8.0);
    float occF = confF * consF * lerp(1.0, 0.72, edge);
    float occB = confB * consB * lerp(1.0, 0.72, edge);
    float consW = sqrt(saturate(occF * occB));
    float globalMag = max(length(gF), length(gB));
    float pinnedF = saturate(1.0 - length(fF.xy) / max(globalMag * 0.3, 3.0 * dims.z));
    float pinnedB = saturate(1.0 - length(fB.xy) / max(globalMag * 0.3, 3.0 * dims.z));
    float uiMask = max(pinnedF, pinnedB) * saturate(globalMag / (5.0 * dims.z) - 0.4);
    float uiKeep = 1.0 - uiMask * 0.85;
    float u = 1.0 - t;
    float2 dispF = (gF * t + 0.5 * accF * (t * t - t) + localF * t * consW) * uiKeep;
    float2 dispB = (gB * u + 0.5 * accB * (u * u - u) + localB * u * consW) * uiKeep;
    float3 cP = tex0.Sample(smp, inp.uv - dispF).rgb;
    float3 cC = tex1.Sample(smp, inp.uv - dispB).rgb;
    float w = lerp(smoothstep(0.42, 0.58, t), smoothstep(0.0, 1.0, t), consW);
    float confSum = occF + occB;
    if (confSum > 1e-4)
        w = lerp(w, occB / confSum, saturate(1.0 - consW) * 0.6);
    float3 nearest = t < 0.5 ? tex0.Sample(smp, inp.uv).rgb : tex1.Sample(smp, inp.uv).rgb;
    float reliable = smoothstep(0.18, 0.72, lerp(occF, occB, t));
    float uiStable = smoothstep(0.2, 0.8, uiMask);
    return float4(lerp(lerp(nearest, lerp(cP, cC, w), reliable), nearest, uiStable), 1.0);
}

float4 PSWarpXFast(VSOut inp) : SV_Target
{
    float e = interp.x;
    float2 g = tex0.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float2 gP = tex3.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float4 f = tex2.SampleLevel(smp, inp.uv, 0);
    float limScale = max(1.0, interp.y);
    float conf = saturate(1.0 - f.z * 2.8);
    float2 lim = dims.zw * 18.0 * limScale;
    float2 local = clamp((f.xy - g) * conf, -lim, lim);
    float2 acc = clamp((gP - g) * 0.75, -lim, lim);
    float globalMag = length(g);
    float pinned = saturate(1.0 - length(f.xy) / max(globalMag * 0.3, 3.0 * dims.z));
    float uiMask = pinned * saturate(globalMag / (5.0 * dims.z) - 0.4);
    float2 disp = (-g + 0.5 * acc) * e * exp(-e * 0.35) + 0.5 * acc * e * e * exp(-e * 0.35);
    disp -= local * e * exp(-e * 0.35) * smoothstep(0.03, 0.3, conf);
    float3 warped = tex1.Sample(smp, inp.uv - disp * (1.0 - uiMask * 0.85)).rgb;
    float3 frozen = tex1.Sample(smp, inp.uv).rgb;
    return float4(lerp(warped, frozen, smoothstep(0.2, 0.8, uiMask)), 1.0);
}

float4 PSWarp(VSOut inp) : SV_Target
{
    float t = interp.x;
    float2 gF = tex3.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float2 gB = tex5.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float2 gPF = tex6.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float2 gPB = tex7.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float4 fF = tex2.SampleLevel(smp, inp.uv, 0);
    float4 fB = tex4.SampleLevel(smp, inp.uv, 0);
    float limScale = max(1.0, interp.y);
    float confK = lerp(3.2, 2.6, saturate((limScale - 1.0) * 0.5));
    float confF = saturate(1.0 - fF.z * confK);
    float confB = saturate(1.0 - fB.z * confK);

    float lc0 = lum(tex1.Sample(smp, inp.uv).rgb);
    float lcx = lum(tex1.Sample(smp, inp.uv + float2(dims.z * 2.0, 0.0)).rgb);
    float lcy = lum(tex1.Sample(smp, inp.uv + float2(0.0, dims.w * 2.0)).rgb);
    float lcx2 = lum(tex1.Sample(smp, inp.uv - float2(dims.z * 2.0, 0.0)).rgb);
    float lcy2 = lum(tex1.Sample(smp, inp.uv - float2(0.0, dims.w * 2.0)).rgb);
    float contrast = abs(lc0 - lcx) + abs(lc0 - lcy) + abs(lc0 - lcx2) + abs(lc0 - lcy2);
    float texConf = saturate(contrast * 9.0);

    // HUD detection: a screen pinned element barely moves (near zero TOTAL flow)
    // even while the camera (global) clearly moves. World geometry moves WITH the
    // camera so its total flow equals the global, never near zero, and stays warped.
    float globalMag = max(length(gF), length(gB));
    float totalMagF = length(fF.xy);
    float totalMagB = length(fB.xy);
    float camMoving = saturate(globalMag / (5.0 * dims.z) - 0.4);
    float pinnedF = saturate(1.0 - totalMagF / max(globalMag * 0.3, 3.0 * dims.z));
    float pinnedB = saturate(1.0 - totalMagB / max(globalMag * 0.3, 3.0 * dims.z));
    float uiMask = max(pinnedF, pinnedB) * camMoving * saturate(contrast * 6.0);
    float uiDamp = 1.0 - uiMask * 0.85;

    float2 lim = dims.zw * 24.0 * limScale;
    float2 localF = clamp((fF.xy - gF) * confF * texConf * uiDamp, -lim, lim);
    float2 localB = clamp((fB.xy - gB) * confB * texConf * uiDamp, -lim, lim);

    float curveW = saturate(2.0 - limScale);
    float2 accF = (gF - gPF) * curveW;
    float aMaxF = 0.6 * length(gF) + 4.0 * dims.z;
    float aLenF = length(accF);
    accF *= aLenF > aMaxF ? aMaxF / max(aLenF, 1e-6) : 1.0;
    float2 accB = (gB - gPB) * curveW;
    float aMaxB = 0.6 * length(gB) + 4.0 * dims.z;
    float aLenB = length(accB);
    accB *= aLenB > aMaxB ? aMaxB / max(aLenB, 1e-6) : 1.0;

    float u = 1.0 - t;
    float uiKeep = 1.0 - uiMask * 0.9;

    float2 flFsum = gF + localF;
    float2 flBsum = gB + localB;
    float2 reverseF = tex4.SampleLevel(smp, inp.uv + flFsum, 0).xy;
    float2 reverseB = tex2.SampleLevel(smp, inp.uv + flBsum, 0).xy;
    float consF = saturate(1.15 - length(flFsum + reverseF) / (length(flFsum) + length(reverseF) + 6.0 * length(dims.zw)) * 1.3);
    float consB = saturate(1.15 - length(flBsum + reverseB) / (length(flBsum) + length(reverseB) + 6.0 * length(dims.zw)) * 1.3);
    float edgeTrust = lerp(1.0, 0.68, saturate(contrast * 5.0));
    float occF = smoothstep(0.12, 0.86, confF * consF * edgeTrust);
    float occB = smoothstep(0.12, 0.86, confB * consB * edgeTrust);
    float consW = sqrt(saturate(occF * occB));

    float2 dispF = (gF * t + 0.5 * accF * (t * t - t) + localF * t * consW * 0.82) * uiKeep;
    float2 dispB = (gB * u + 0.5 * accB * (u * u - u) + localB * u * consW * 0.82) * uiKeep;
    float3 cP = tex0.Sample(smp, inp.uv - dispF).rgb;
    float3 cC = tex1.Sample(smp, inp.uv - dispB).rgb;
    float w = lerp(smoothstep(0.42, 0.58, t), smoothstep(0.0, 1.0, t), consW);
    float confSum = occF + occB;
    if (confSum > 1e-4)
        w = lerp(w, occB / confSum, saturate(1.0 - consW) * 0.6);
    float3 nearest = t < 0.5 ? tex0.Sample(smp, inp.uv).rgb : tex1.Sample(smp, inp.uv).rgb;
    float reliable = smoothstep(0.16, 0.72, lerp(occF, occB, t));
    float uiStable = smoothstep(0.18, 0.82, uiMask);
    return float4(lerp(lerp(nearest, lerp(cP, cC, w), reliable), nearest, uiStable), 1.0);
}

float4 PSFlowGlobal(VSOut inp) : SV_Target
{
    float2 acc = float2(0.0, 0.0);
    float wsum = 0.0;
    [unroll] for (int y = 0; y < 8; y++)
    {
        [unroll] for (int x = 0; x < 8; x++)
        {
            float2 uv = float2((x + 0.5) / 8.0, (y + 0.5) / 8.0);
            float4 f = tex0.SampleLevel(smp, uv, 0);
            float w = 1.0 / (f.z + 0.05);
            acc += f.xy * w;
            wsum += w;
        }
    }
    float2 fresh = acc / max(wsum, 1e-5);
    float2 prevG = tex1.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float direction = dot(fresh, prevG) / max(length(fresh) * length(prevG), 1e-7);
    float acceleration = length(fresh - prevG) / (length(fresh) + length(prevG) + 6.0 * length(dims.zw));
    float history = interp.x * smoothstep(-0.1, 0.35, direction) * (1.0 - smoothstep(0.42, 0.95, acceleration));
    return float4(lerp(fresh, prevG, history), 0.0, 1.0);
}

float4 PSWarpX(VSOut inp) : SV_Target
{
    float e = interp.x;
    float2 g = tex0.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float2 gP = tex3.SampleLevel(smp, float2(0.5, 0.5), 0).xy;
    float4 f = tex2.SampleLevel(smp, inp.uv, 0);
    float limScale = max(1.0, interp.y);
    float confK = lerp(3.2, 2.6, saturate((limScale - 1.0) * 0.5));
    float conf = saturate(1.0 - f.z * confK);

    float lc0 = lum(tex1.Sample(smp, inp.uv).rgb);
    float lcx = lum(tex1.Sample(smp, inp.uv + float2(dims.z * 2.0, 0.0)).rgb);
    float lcy = lum(tex1.Sample(smp, inp.uv + float2(0.0, dims.w * 2.0)).rgb);
    float lcx2 = lum(tex1.Sample(smp, inp.uv - float2(dims.z * 2.0, 0.0)).rgb);
    float lcy2 = lum(tex1.Sample(smp, inp.uv - float2(0.0, dims.w * 2.0)).rgb);
    float contrast = abs(lc0 - lcx) + abs(lc0 - lcy) + abs(lc0 - lcx2) + abs(lc0 - lcy2);
    float texConf = saturate(contrast * 9.0);

    // HUD detection for forward warp: pinned (near zero total flow) while camera moves
    float globalMag = length(g);
    float totalMag = length(f.xy);
    float camMoving = saturate(globalMag / (5.0 * dims.z) - 0.4);
    float pinned = saturate(1.0 - totalMag / max(globalMag * 0.3, 3.0 * dims.z));
    float uiMask = pinned * camMoving * saturate(contrast * 6.0);
    float uiDamp = 1.0 - uiMask * 0.85;

    float2 lim = dims.zw * 24.0 * limScale;
    float2 local = clamp((f.xy - g) * conf * texConf * uiDamp, -lim, lim);

    float curveW = saturate((limScale - 1.0) * 1.5);
    float2 acc = (gP - g) * curveW;
    float aMax = 0.6 * length(g) + 4.0 * dims.z;
    float aLen = length(acc);
    acc *= aLen > aMax ? aMax / max(aLen, 1e-6) : 1.0;
    float2 fwd = -g;
    float decay = exp(-e * 0.35);
    float2 disp = (fwd + 0.5 * acc) * e * decay + 0.5 * acc * e * e * decay;

    float w = smoothstep(0.03, 0.30, conf) * saturate(1.0 - e * 0.4);
    float2 fullDisp = (disp - local * e * decay * w) * (1.0 - uiMask * 0.9);
    float3 warped = tex1.Sample(smp, inp.uv - fullDisp).rgb;
    float3 frozen = tex1.Sample(smp, inp.uv).rgb;
    return float4(lerp(warped, frozen, smoothstep(0.18, 0.82, uiMask)), 1.0);
}
";
    }
}
