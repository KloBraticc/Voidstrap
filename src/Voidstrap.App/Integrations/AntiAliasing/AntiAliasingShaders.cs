namespace Voidstrap.Integrations.AntiAliasing
{
    internal static class AntiAliasingShaders
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

cbuffer AaParams : register(b0)
{
    float4 dims;
    float4 srcRect;
};

Texture2D tex0 : register(t0);
Texture2D tex1 : register(t1);
SamplerState smp : register(s0);

float lum(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
float2 texPx() { return dims.zw; }

float4 PSPass(VSOut inp) : SV_Target
{
    return float4(tex0.Sample(smp, inp.uv).rgb, 1.0);
}

float4 PSCropSrgb(VSOut inp) : SV_Target
{
    float3 c = tex0.Sample(smp, srcRect.xy + inp.uv * srcRect.zw).rgb;
    c = saturate(c);
    c = pow(c, 1.0 / 2.2);
    return float4(c, 1.0);
}

float fxLuma(float3 c) { return dot(c, float3(0.299, 0.587, 0.114)); }

static const float FX_Q[12] = { 1.0, 1.0, 1.0, 1.0, 1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 4.0, 8.0 };

float4 fxaaCore(float2 uv, int steps, float subpixMax)
{
    float2 px = texPx();
    float3 rgbM = tex0.Sample(smp, uv).rgb;
    float lM = fxLuma(rgbM);
    float lN = fxLuma(tex0.Sample(smp, uv + float2(0.0, -px.y)).rgb);
    float lS = fxLuma(tex0.Sample(smp, uv + float2(0.0, px.y)).rgb);
    float lW = fxLuma(tex0.Sample(smp, uv + float2(-px.x, 0.0)).rgb);
    float lE = fxLuma(tex0.Sample(smp, uv + float2(px.x, 0.0)).rgb);
    float lMin = min(lM, min(min(lN, lS), min(lW, lE)));
    float lMax = max(lM, max(max(lN, lS), max(lW, lE)));
    float range = lMax - lMin;
    if (range < max(0.0312, lMax * 0.125))
        return float4(rgbM, 1.0);

    float lNW = fxLuma(tex0.Sample(smp, uv + float2(-px.x, -px.y)).rgb);
    float lNE = fxLuma(tex0.Sample(smp, uv + float2(px.x, -px.y)).rgb);
    float lSW = fxLuma(tex0.Sample(smp, uv + float2(-px.x, px.y)).rgb);
    float lSE = fxLuma(tex0.Sample(smp, uv + float2(px.x, px.y)).rgb);
    float lNS = lN + lS;
    float lWE = lW + lE;
    float edgeH = abs(-2.0 * lW + (lNW + lSW)) + abs(-2.0 * lM + lNS) * 2.0 + abs(-2.0 * lE + (lNE + lSE));
    float edgeV = abs(-2.0 * lN + (lNW + lNE)) + abs(-2.0 * lM + lWE) * 2.0 + abs(-2.0 * lS + (lSW + lSE));
    bool horz = edgeH >= edgeV;

    float luma1 = horz ? lN : lW;
    float luma2 = horz ? lS : lE;
    float grad1 = luma1 - lM;
    float grad2 = luma2 - lM;
    bool is1 = abs(grad1) >= abs(grad2);
    float gradScaled = 0.25 * max(abs(grad1), abs(grad2));
    float stepLen = horz ? px.y : px.x;
    float lumaLocal;
    if (is1) { stepLen = -stepLen; lumaLocal = 0.5 * (luma1 + lM); }
    else { lumaLocal = 0.5 * (luma2 + lM); }

    float2 curr = uv;
    if (horz) curr.y += stepLen * 0.5; else curr.x += stepLen * 0.5;
    float2 off = horz ? float2(px.x, 0.0) : float2(0.0, px.y);
    float2 uv1 = curr - off;
    float2 uv2 = curr + off;
    float end1 = fxLuma(tex0.Sample(smp, uv1).rgb) - lumaLocal;
    float end2 = fxLuma(tex0.Sample(smp, uv2).rgb) - lumaLocal;
    bool done1 = abs(end1) >= gradScaled;
    bool done2 = abs(end2) >= gradScaled;
    if (!done1) uv1 -= off;
    if (!done2) uv2 += off;

    [loop]
    for (int i = 0; i < steps; i++)
    {
        if (done1 && done2) break;
        float q = FX_Q[min(i, 11)];
        if (!done1)
        {
            end1 = fxLuma(tex0.Sample(smp, uv1).rgb) - lumaLocal;
            done1 = abs(end1) >= gradScaled;
            if (!done1) uv1 -= off * q;
        }
        if (!done2)
        {
            end2 = fxLuma(tex0.Sample(smp, uv2).rgb) - lumaLocal;
            done2 = abs(end2) >= gradScaled;
            if (!done2) uv2 += off * q;
        }
    }

    float d1 = horz ? (uv.x - uv1.x) : (uv.y - uv1.y);
    float d2 = horz ? (uv2.x - uv.x) : (uv2.y - uv.y);
    bool dir1 = d1 < d2;
    float distFinal = min(d1, d2);
    float spanLen = d1 + d2;
    float pixOff = 0.5 - distFinal / max(spanLen, 1e-5);
    bool centerSmaller = lM < lumaLocal;
    bool goodSpan = ((dir1 ? end1 : end2) < 0.0) != centerSmaller;
    float finalOff = goodSpan ? pixOff : 0.0;

    float lumaAvg = (1.0 / 12.0) * (2.0 * (lNS + lWE) + (lNW + lNE + lSW + lSE));
    float subpix = saturate(abs(lumaAvg - lM) / range);
    subpix = (-2.0 * subpix + 3.0) * subpix * subpix;
    subpix = subpix * subpix * subpixMax;

    float edgeStrength = saturate(range / max(lMax, 1e-5) * 4.0 - 0.35);
    float longEnough = saturate(spanLen / (8.0 * (horz ? px.x : px.y)));
    subpix *= edgeStrength * lerp(0.35, 1.0, longEnough);
    finalOff = max(finalOff, subpix);

    float2 outUv = uv;
    if (horz) outUv.y += finalOff * stepLen; else outUv.x += finalOff * stepLen;
    float3 aa = tex0.Sample(smp, outUv).rgb;
    return float4(lerp(rgbM, aa, edgeStrength), 1.0);
}

float4 PSFxaa(VSOut inp) : SV_Target { return fxaaCore(inp.uv, 12, 0.5); }
float4 PSFxaaUltra(VSOut inp) : SV_Target { return fxaaCore(inp.uv, 24, 0.7); }

float4 PSSmaaEdge(VSOut inp) : SV_Target
{
    float2 uv = inp.uv;
    float2 px = texPx();
    float l = lum(tex0.Sample(smp, uv).rgb);
    float ll = lum(tex0.Sample(smp, uv - float2(px.x, 0.0)).rgb);
    float lt = lum(tex0.Sample(smp, uv - float2(0.0, px.y)).rgb);
    float2 delta = abs(l - float2(ll, lt));
    float2 edges = step(0.06, delta);
    if (edges.x + edges.y <= 0.0)
        return float4(0.0, 0.0, 0.0, 1.0);
    float lr = lum(tex0.Sample(smp, uv + float2(px.x, 0.0)).rgb);
    float lb = lum(tex0.Sample(smp, uv + float2(0.0, px.y)).rgb);
    float2 delta2 = abs(l - float2(lr, lb));
    float2 maxd = max(delta, delta2);
    float lll = lum(tex0.Sample(smp, uv - float2(px.x * 2.0, 0.0)).rgb);
    float ltt = lum(tex0.Sample(smp, uv - float2(0.0, px.y * 2.0)).rgb);
    float2 deltaL = abs(float2(ll, lt) - float2(lll, ltt));
    maxd = max(maxd, deltaL);
    float finalDelta = max(maxd.x, maxd.y);
    edges *= step(finalDelta, 2.0 * delta);
    return float4(edges, 0.0, 1.0);
}

float2 smaaE(int2 p)
{
    return tex0.Load(int3(p, 0)).rg;
}

float2 segArea(float x1, float y1, float x2, float y2, float px)
{
    float xa = max(px, x1);
    float xb = min(px + 1.0, x2);
    if (xb <= xa)
        return float2(0.0, 0.0);
    float inv = 1.0 / max(x2 - x1, 1e-5);
    float ya = lerp(y1, y2, (xa - x1) * inv);
    float yb = lerp(y1, y2, (xb - x1) * inv);
    if (ya * yb >= 0.0)
    {
        float a = (ya + yb) * 0.5 * (xb - xa);
        return a >= 0.0 ? float2(0.0, a) : float2(-a, 0.0);
    }
    float xc = xa + (xb - xa) * (ya / (ya - yb));
    float a1 = ya * 0.5 * (xc - xa);
    float a2 = yb * 0.5 * (xb - xc);
    float2 r = float2(0.0, 0.0);
    if (a1 < 0.0) r.x -= a1; else r.y += a1;
    if (a2 < 0.0) r.x -= a2; else r.y += a2;
    return r;
}

float2 smaaArea(float o1, float o2, float d, float px)
{
    if (o1 == 0.0 && o2 == 0.0)
        return float2(0.0, 0.0);
    if (o1 != 0.0 && o2 == 0.0)
        return segArea(0.0, o1, d * 0.5, 0.0, px);
    if (o1 == 0.0)
        return segArea(d * 0.5, 0.0, d, o2, px);
    if (o1 * o2 < 0.0)
        return segArea(0.0, o1, d, o2, px);
    return segArea(0.0, o1, d * 0.5, 0.0, px) + segArea(d * 0.5, 0.0, d, o2, px);
}

float smaaOffset(float cNear, float cFar)
{
    if (cNear > 0.5 && cFar < 0.5)
        return -0.5;
    if (cFar > 0.5 && cNear < 0.5)
        return 0.5;
    return 0.0;
}

float4 smaaWeightsCore(int2 p, int steps)
{
    float2 e = smaaE(p);
    float4 w = float4(0.0, 0.0, 0.0, 0.0);
    if (e.y > 0.5)
    {
        int dl = 0;
        [loop]
        for (int i = 0; i < steps; i++)
        {
            int xl = p.x - dl;
            if (smaaE(int2(xl, p.y)).x > 0.5 || smaaE(int2(xl, p.y - 1)).x > 0.5)
                break;
            if (smaaE(int2(xl - 1, p.y)).y < 0.5)
                break;
            dl++;
        }
        int dr = 0;
        [loop]
        for (int j = 0; j < steps; j++)
        {
            int xr = p.x + dr;
            if (smaaE(int2(xr + 1, p.y)).x > 0.5 || smaaE(int2(xr + 1, p.y - 1)).x > 0.5)
                break;
            if (smaaE(int2(xr + 1, p.y)).y < 0.5)
                break;
            dr++;
        }
        float o1 = smaaOffset(smaaE(int2(p.x - dl, p.y)).x, smaaE(int2(p.x - dl, p.y - 1)).x);
        float o2 = smaaOffset(smaaE(int2(p.x + dr + 1, p.y)).x, smaaE(int2(p.x + dr + 1, p.y - 1)).x);
        float2 a = smaaArea(o1, o2, float(dl + dr) + 1.0, float(dl));
        w.x = a.x;
        w.y = a.y;
    }
    if (e.x > 0.5)
    {
        int dt = 0;
        [loop]
        for (int k = 0; k < steps; k++)
        {
            int yt = p.y - dt;
            if (smaaE(int2(p.x, yt)).y > 0.5 || smaaE(int2(p.x - 1, yt)).y > 0.5)
                break;
            if (smaaE(int2(p.x, yt - 1)).x < 0.5)
                break;
            dt++;
        }
        int db = 0;
        [loop]
        for (int m = 0; m < steps; m++)
        {
            int yb = p.y + db;
            if (smaaE(int2(p.x, yb + 1)).y > 0.5 || smaaE(int2(p.x - 1, yb + 1)).y > 0.5)
                break;
            if (smaaE(int2(p.x, yb + 1)).x < 0.5)
                break;
            db++;
        }
        float o1 = smaaOffset(smaaE(int2(p.x, p.y - dt)).y, smaaE(int2(p.x - 1, p.y - dt)).y);
        float o2 = smaaOffset(smaaE(int2(p.x, p.y + db + 1)).y, smaaE(int2(p.x - 1, p.y + db + 1)).y);
        float2 a = smaaArea(o1, o2, float(dt + db) + 1.0, float(dt));
        w.z = a.x;
        w.w = a.y;
    }
    return w;
}

float4 PSSmaaWeights(VSOut inp) : SV_Target { return smaaWeightsCore(int2(inp.pos.xy), 16); }
float4 PSSmaaWeightsUltra(VSOut inp) : SV_Target { return smaaWeightsCore(int2(inp.pos.xy), 32); }

float4 PSSmaaBlend(VSOut inp) : SV_Target
{
    int2 p = int2(inp.pos.xy);
    float2 uv = inp.uv;
    float2 px = texPx();
    float4 m = tex1.Load(int3(p, 0));
    float wT = min(m.x, 0.5);
    float wL = min(m.z, 0.5);
    float wB = min(tex1.Load(int3(p.x, p.y + 1, 0)).y, 0.5);
    float wR = min(tex1.Load(int3(p.x + 1, p.y, 0)).w, 0.5);
    float3 c = tex0.Sample(smp, uv).rgb;
    float maxV = max(wT, wB);
    float maxH = max(wL, wR);
    if (maxV + maxH < 0.004)
        return float4(c, 1.0);
    if (maxH > maxV)
    {
        float3 cl = tex0.Sample(smp, uv - float2(px.x, 0.0)).rgb;
        float3 cr = tex0.Sample(smp, uv + float2(px.x, 0.0)).rgb;
        float3 b1 = lerp(c, cl, saturate(wL));
        float3 b2 = lerp(c, cr, saturate(wR));
        return float4((b1 * wL + b2 * wR) / (wL + wR), 1.0);
    }
    float3 ct = tex0.Sample(smp, uv - float2(0.0, px.y)).rgb;
    float3 cb = tex0.Sample(smp, uv + float2(0.0, px.y)).rgb;
    float3 b3 = lerp(c, ct, saturate(wT));
    float3 b4 = lerp(c, cb, saturate(wB));
    return float4((b3 * wT + b4 * wB) / (wT + wB), 1.0);
}

float4 PSDlaaMask(VSOut inp) : SV_Target
{
    float2 uv = inp.uv;
    float2 px = texPx();
    float3 c = tex0.Sample(smp, uv).rgb;
    float3 l = tex0.Sample(smp, uv - float2(px.x, 0.0)).rgb;
    float3 r = tex0.Sample(smp, uv + float2(px.x, 0.0)).rgb;
    float3 t = tex0.Sample(smp, uv - float2(0.0, px.y)).rgb;
    float3 b = tex0.Sample(smp, uv + float2(0.0, px.y)).rgb;
    float e = lum(abs(4.0 * c - l - r - t - b));
    return float4(c, saturate(e * 3.0));
}

float dlaaT(float lTarget, float lC, float lN)
{
    float den = lN - lC;
    if (abs(den) < 1e-3)
        return 0.0;
    return saturate((lTarget - lC) / den);
}

float4 PSDlaa(VSOut inp) : SV_Target
{
    float2 uv = inp.uv;
    float2 px = texPx();
    float4 c = tex0.Sample(smp, uv);
    float4 wh1 = tex0.Sample(smp, uv - float2(px.x * 1.5, 0.0));
    float4 wh2 = tex0.Sample(smp, uv + float2(px.x * 1.5, 0.0));
    float4 wv1 = tex0.Sample(smp, uv - float2(0.0, px.y * 1.5));
    float4 wv2 = tex0.Sample(smp, uv + float2(0.0, px.y * 1.5));
    float3 sumH = wh1.rgb + wh2.rgb;
    float3 sumV = wv1.rgb + wv2.rgb;
    float edgeH = lum(abs(sumH * 0.5 - c.rgb));
    float edgeV = lum(abs(sumV * 0.5 - c.rgb));
    float3 avgH = (sumH + c.rgb) / 3.0;
    float3 avgV = (sumV + c.rgb) / 3.0;
    float blurH = saturate(edgeH * 3.0 / (lum(avgH) + 0.1));
    float blurV = saturate(edgeV * 3.0 / (lum(avgV) + 0.1));
    float3 col = lerp(c.rgb, avgH, blurH * 0.5);
    col = lerp(col, avgV, blurV * 0.5);
    float4 h3 = tex0.Sample(smp, uv - float2(px.x * 3.5, 0.0));
    float4 h4 = tex0.Sample(smp, uv + float2(px.x * 3.5, 0.0));
    float4 h5 = tex0.Sample(smp, uv - float2(px.x * 5.5, 0.0));
    float4 h6 = tex0.Sample(smp, uv + float2(px.x * 5.5, 0.0));
    float4 h7 = tex0.Sample(smp, uv - float2(px.x * 7.5, 0.0));
    float4 h8 = tex0.Sample(smp, uv + float2(px.x * 7.5, 0.0));
    float4 v3 = tex0.Sample(smp, uv - float2(0.0, px.y * 3.5));
    float4 v4 = tex0.Sample(smp, uv + float2(0.0, px.y * 3.5));
    float4 v5 = tex0.Sample(smp, uv - float2(0.0, px.y * 5.5));
    float4 v6 = tex0.Sample(smp, uv + float2(0.0, px.y * 5.5));
    float4 v7 = tex0.Sample(smp, uv - float2(0.0, px.y * 7.5));
    float4 v8 = tex0.Sample(smp, uv + float2(0.0, px.y * 7.5));
    float maskLongH = (c.a + wh1.a + wh2.a + h3.a + h4.a + h5.a + h6.a + h7.a + h8.a) / 9.0;
    float maskLongV = (c.a + wv1.a + wv2.a + v3.a + v4.a + v5.a + v6.a + v7.a + v8.a) / 9.0;
    float longH = saturate(maskLongH * 2.0 - 1.0);
    float longV = saturate(maskLongV * 2.0 - 1.0);
    float lc = lum(c.rgb);
    if (longH > 0.0)
    {
        float3 longBlurH = (c.rgb + wh1.rgb + wh2.rgb + h3.rgb + h4.rgb + h5.rgb + h6.rgb + h7.rgb + h8.rgb) / 9.0;
        float3 up = tex0.Sample(smp, uv - float2(0.0, px.y)).rgb;
        float3 dn = tex0.Sample(smp, uv + float2(0.0, px.y)).rgb;
        float lLong = lum(longBlurH);
        float tU = dlaaT(lLong, lc, lum(up));
        float tD = dlaaT(lLong, lc, lum(dn));
        float3 resH = lerp(c.rgb, up, tU * 0.5);
        resH = lerp(resH, dn, tD * 0.5);
        col = lerp(col, resH, longH);
    }
    if (longV > 0.0)
    {
        float3 longBlurV = (c.rgb + wv1.rgb + wv2.rgb + v3.rgb + v4.rgb + v5.rgb + v6.rgb + v7.rgb + v8.rgb) / 9.0;
        float3 lf = tex0.Sample(smp, uv - float2(px.x, 0.0)).rgb;
        float3 rt = tex0.Sample(smp, uv + float2(px.x, 0.0)).rgb;
        float lLong = lum(longBlurV);
        float tL = dlaaT(lLong, lc, lum(lf));
        float tR = dlaaT(lLong, lc, lum(rt));
        float3 resV = lerp(c.rgb, lf, tL * 0.5);
        resV = lerp(resV, rt, tR * 0.5);
        col = lerp(col, resV, longV);
    }
    return float4(col, 1.0);
}

float4 PSNfaa(VSOut inp) : SV_Target
{
    float2 uv = inp.uv;
    float2 px = texPx();
    float3 c = tex0.Sample(smp, uv).rgb;
    float ltl = lum(tex0.Sample(smp, uv + px * float2(-1.0, -1.0)).rgb);
    float lt = lum(tex0.Sample(smp, uv + px * float2(0.0, -1.0)).rgb);
    float ltr = lum(tex0.Sample(smp, uv + px * float2(1.0, -1.0)).rgb);
    float ll = lum(tex0.Sample(smp, uv + px * float2(-1.0, 0.0)).rgb);
    float lr = lum(tex0.Sample(smp, uv + px * float2(1.0, 0.0)).rgb);
    float lbl = lum(tex0.Sample(smp, uv + px * float2(-1.0, 1.0)).rgb);
    float lb = lum(tex0.Sample(smp, uv + px * float2(0.0, 1.0)).rgb);
    float lbr = lum(tex0.Sample(smp, uv + px * float2(1.0, 1.0)).rgb);
    float gx = (ltl + 2.0 * ll + lbl) - (ltr + 2.0 * lr + lbr);
    float gy = (ltl + 2.0 * lt + ltr) - (lbl + 2.0 * lb + lbr);
    float mag = length(float2(gx, gy));
    if (mag < 0.075)
        return float4(c, 1.0);
    float2 dir = normalize(float2(gy, -gx)) * px * saturate(mag * 2.0);
    float3 acc = c * 2.0
        + tex0.Sample(smp, uv + dir).rgb
        + tex0.Sample(smp, uv - dir).rgb
        + tex0.Sample(smp, uv + dir * 0.5).rgb
        + tex0.Sample(smp, uv - dir * 0.5).rgb;
    return float4(acc / 6.0, 1.0);
}

float3 rgb2ycocg(float3 c)
{
    return float3(
        0.25 * c.r + 0.5 * c.g + 0.25 * c.b,
        0.5 * c.r - 0.5 * c.b,
        -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}

float3 ycocg2rgb(float3 c)
{
    return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}

float4 PSTsaa(VSOut inp) : SV_Target
{
    float2 uv = inp.uv;
    float2 px = texPx();
    float3 cur = rgb2ycocg(tex0.Sample(smp, uv).rgb);
    float3 m1 = float3(0.0, 0.0, 0.0);
    float3 m2 = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float3 s = rgb2ycocg(tex0.Sample(smp, uv + px * float2(x, y)).rgb);
            m1 += s;
            m2 += s * s;
        }
    }
    float3 mu = m1 / 9.0;
    float3 sigma = sqrt(max(m2 / 9.0 - mu * mu, 0.0));
    float3 hist = rgb2ycocg(tex1.Sample(smp, uv).rgb);
    float3 d = hist - mu;
    float3 e = max(sigma, 1e-5);
    float3 t3 = abs(d) / e;
    float t = max(t3.x, max(t3.y, t3.z));
    float3 histClipped = t > 1.0 ? mu + d / t : hist;
    float clipEvent = saturate(t - 1.0);
    float histWeight = lerp(0.9, 0.6, clipEvent);
    float3 blended = lerp(cur, histClipped, histWeight);
    return float4(saturate(ycocg2rgb(blended)), 1.0);
}

float4 PSTsaaSharpen(VSOut inp) : SV_Target
{
    float2 uv = inp.uv;
    float2 px = texPx();
    float3 c = tex0.Sample(smp, uv).rgb;
    float3 blur = (tex0.Sample(smp, uv - float2(px.x, 0.0)).rgb
                 + tex0.Sample(smp, uv + float2(px.x, 0.0)).rgb
                 + tex0.Sample(smp, uv - float2(0.0, px.y)).rgb
                 + tex0.Sample(smp, uv + float2(0.0, px.y)).rgb) * 0.25;
    float3 outc = c + (c - blur) * 0.35;
    return float4(saturate(outc), 1.0);
}
";
    }
}
