namespace Voidstrap.Integrations.RiShade
{
    internal static class RiShadeShaders
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

cbuffer Params : register(b0)
{
    float4 pA; float4 pB; float4 pC; float4 pD; float4 pE; float4 pF;
    float4 pG; float4 pH; float4 pI; float4 pJ; float4 pK; float4 pL;
    float4 pM; float4 pN; float4 pO; float4 pP; float4 pQ; float4 pR; float4 pS; float4 pT;
};

cbuffer PassInfo : register(b1)
{
    float4 passPx;
};

#define uGrade      (pA.x > 0.5)
#define uSat        pA.y
#define uCon        pA.z
#define uBri        pA.w
#define uGamma      pB.x
#define uHue        pB.y
#define uTime       pB.z
#define uChroma     (pB.w > 0.5)
#define uLift       pC.xyz
#define uTonemap    (pC.w > 0.5)
#define uGain       pD.xyz
#define uTonemapMode ((int)pD.w)
#define uBalance    pE.xyz
#define uExposure   pE.w
#define uTemp       pF.xyz
#define uWhitepoint pF.w
#define uVignette   (pG.x > 0.5)
#define uVigStr     pG.y
#define uVigFeat    pG.z
#define uVigCX      pG.w
#define uVigCY      pH.x
#define uSharpen    (pH.y > 0.5)
#define uShStr      pH.z
#define uShRadius   pH.w
#define uShClamp    pI.x
#define uChStr      pI.y
#define uChRadial   (pI.z > 0.5)
#define uGrain      (pI.w > 0.5)
#define uGrStr      pJ.x
#define uGrSize     pJ.y
#define uGrColored  (pJ.z > 0.5)
#define uDOF        (pJ.w > 0.5)
#define uDOFStr     pK.x
#define uDOFFocus   pK.y
#define uDOFFeather pK.z
#define uAO         (pK.w > 0.5)
#define uAOStr      pL.x
#define uAORadius   pL.y
#define uAOSamples  ((int)pL.z)
#define uTexW       pL.w
#define uTexH       pM.x
#define uBloomStr    pM.y
#define uBloomThresh pM.z
#define uBloomRadius pM.w
#define uBloomTint  pN.xyz
#define uSsrIntensity pN.w
#define uSsrGloss   pO.x
#define uSsrRefl    pO.y
#define uSsrDist    pO.z
#define uClarityStr pO.w
#define uDebandOn   (pP.x > 0.5)
#define uDebandStr  pP.y
#define uGiStr      pP.z
#define uGiRadius   pP.w
#define uFogStr     pQ.x
#define uFogStart   pQ.y
#define uFogGray    pQ.z
#define uALStr      pQ.w
#define uAdaptExp   pR.x
#define uPlaneN     pR.yzw
#define uSsrSheen   pS.x
#define uDepthShift pS.yz
#define uPlaneD     pT.x
#define uPlaneValid (pT.w > 0.5)
#define uAiDepth    (pT.y > 0.5)
#define uDebugView  ((int)pT.z)
#define uUpscale    (passPx.z > 0.5)

Texture2D tex0 : register(t0);
Texture2D tex1 : register(t1);
Texture2D tex2 : register(t2);
Texture2D tex3 : register(t3);
SamplerState smp : register(s0);

float lum(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5); }
float ign(float2 p) { return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715)))); }
float2 texPx() { return 1.0 / float2(uTexW, uTexH); }

float3 sampG(Texture2D t, float2 g) { return t.Sample(smp, float2(g.x, 1.0 - g.y)).rgb; }
float4 sampG4(Texture2D t, float2 g) { return t.Sample(smp, float2(g.x, 1.0 - g.y)); }

static const float AI_TANHALF = 0.7;

float aiZ(float disp) { return 1.0 / (saturate(disp) * 3.0 + 0.25); }

float3 aiViewPos(Texture2D depthTex, float2 g)
{
    float z = aiZ(sampG(depthTex, g).r);
    float aspect = uTexW / max(uTexH, 1.0);
    return float3((g.x * 2.0 - 1.0) * AI_TANHALF * aspect * z, (g.y * 2.0 - 1.0) * AI_TANHALF * z, z);
}

float2 aiProject(float3 p)
{
    float aspect = uTexW / max(uTexH, 1.0);
    float2 ndc = p.xy / (max(p.z, 0.02) * AI_TANHALF * float2(aspect, 1.0));
    return ndc * 0.5 + 0.5;
}

float3 aiNormalE(Texture2D depthTex, float2 g, float e)
{
    float3 p = aiViewPos(depthTex, g);
    float3 pr = aiViewPos(depthTex, g + float2(e, 0.0));
    float3 pl = aiViewPos(depthTex, g - float2(e, 0.0));
    float3 pu = aiViewPos(depthTex, g + float2(0.0, e));
    float3 pd = aiViewPos(depthTex, g - float2(0.0, e));
    float3 dx = (abs(pr.z - p.z) < abs(p.z - pl.z)) ? (pr - p) : (p - pl);
    float3 dy = (abs(pu.z - p.z) < abs(p.z - pd.z)) ? (pu - p) : (p - pd);
    float3 n = normalize(cross(dy, dx));
    if (dot(n, -normalize(p)) < 0.0)
        n = -n;
    return n;
}

float3 aiNormal(Texture2D depthTex, float2 g)
{
    return aiNormalE(depthTex, g, 3.0 / 256.0);
}

float3 rgb2hsv(float3 c)
{
    float4 K = float4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
    float4 p = lerp(float4(c.bg, K.wz), float4(c.gb, K.xy), step(c.b, c.g));
    float4 q = lerp(float4(p.xyw, c.r), float4(c.r, p.yzx), step(p.x, c.r));
    float d = q.x - min(q.w, q.y);
    return float3(abs(q.z + (q.w - q.y) / (6.0 * d + 1e-10)), d / (q.x + 1e-10), q.x);
}

float3 hsv2rgb(float3 c)
{
    float4 K = float4(1.0, 2.0 / 3.0, 1.0 / 3.0, 3.0);
    float3 p = abs(frac(c.xxx + K.xyz) * 6.0 - K.www);
    return c.z * lerp(K.xxx, clamp(p - K.xxx, 0.0, 1.0), c.y);
}

float3 reinhard(float3 c, float wp) { return c * (1.0 + c / (wp * wp)) / (1.0 + c); }

float3 aces(float3 x)
{
    float a = 2.51, b = 0.03, g = 2.43, d = 0.59, e = 0.14;
    return clamp((x * (a * x + b)) / (x * (g * x + d) + e), 0.0, 1.0);
}

float3 uncharted2(float3 x)
{
    float A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
    return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
}

float3 filmic(float3 c)
{
    c = max(float3(0.0, 0.0, 0.0), c - 0.004);
    return (c * (6.2 * c + 0.5)) / (c * (6.2 * c + 1.7) + 0.06);
}

float3 agxContrast(float3 x)
{
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

float3 agx(float3 c)
{
    float3x3 agxMat = float3x3(
        0.842479062253094, 0.0423282422610123, 0.0423756549057051,
        0.0784335999999992, 0.878468636469772, 0.0784336,
        0.0792237451477643, 0.0791661274605434, 0.879142973793104);
    float minEv = -12.47393;
    float maxEv = 4.026069;
    c = mul(agxMat, c);
    c = clamp(log2(max(c, 1e-10)), minEv, maxEv);
    c = (c - minEv) / (maxEv - minEv);
    c = agxContrast(c);
    float3x3 agxMatInv = float3x3(
        1.19687900512017, -0.0528968517574562, -0.0529716355144438,
        -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
        -0.0990297440797205, -0.0989611768448433, 1.15107367264116);
    c = mul(agxMatInv, c);
    return pow(max(c, 0.0), 2.2);
}

float3 srgbToLinear(float3 c)
{
    c = max(c, 0.0);
    return lerp(c / 12.92, pow((c + 0.055) / 1.055, 2.4), step(0.04045, c));
}

float3 linearToSrgb(float3 c)
{
    c = max(c, 0.0);
    return lerp(c * 12.92, 1.055 * pow(c, 1.0 / 2.4) - 0.055, step(0.0031308, c));
}

float3 applyTonemap(float3 c)
{
    c = srgbToLinear(c) * uExposure;
    if (uTonemapMode == 0) return saturate(linearToSrgb(reinhard(c, uWhitepoint)));
    if (uTonemapMode == 1) return saturate(linearToSrgb(aces(c)));
    if (uTonemapMode == 2) { float3 w = uncharted2(float3(uWhitepoint, uWhitepoint, uWhitepoint)); return saturate(linearToSrgb(uncharted2(c) / w)); }
    if (uTonemapMode == 4) return saturate(linearToSrgb(agx(c)));
    return saturate(filmic(c));
}

float sampleAO(float2 uv)
{
    if (!uAiDepth)
        return 1.0;
    float ao = 0.0;
    int n = max(uAOSamples, 1);
    float2 g = float2(uv.x, 1.0 - uv.y);
    float3 P = aiViewPos(tex1, g);
    float3 N = aiNormal(tex1, g);
    [loop]
    for (int i = 0; i < n; i++)
    {
        float a = float(i) * (6.28318 / float(n)) + hash(uv * 91.7) * 6.28318;
        float2 gs = g + float2(cos(a), sin(a)) * uAORadius * 4.0;
        float3 Ps = aiViewPos(tex1, gs);
        float3 dv = Ps - P;
        float len = max(length(dv), 1e-4);
        float occ = saturate(dot(N, dv / len) - 0.12);
        float falloff = saturate(1.0 - len / (P.z * 0.6));
        ao += occ * falloff;
    }
    return 1.0 - (ao / float(n)) * uAOStr * 1.4;
}

float4 PSMain(VSOut inp) : SV_Target
{
    float2 uv = inp.uv;
    float3 col;
    if (uChroma)
    {
        float2 off = float2(uChStr, 0.0);
        if (uChRadial)
        {
            float2 dir = uv - 0.5;
            float r2 = dot(dir, dir);
            off = dir * uChStr * (0.4 + r2 * 3.2);
        }
        col.r = tex0.Sample(smp, uv - off).r;
        col.g = tex0.Sample(smp, uv).g;
        col.b = tex0.Sample(smp, uv + off).b;
    }
    else
    {
        col = tex0.Sample(smp, uv).rgb;
    }
    if (uDebandOn)
    {
        float2 dpx = texPx();
        float n = ign(uv / dpx);
        float a = n * 6.28318;
        float2 dir = float2(cos(a), sin(a));
        float reach = 2.0 + uDebandStr * 6.0;
        float3 avg = float3(0.0, 0.0, 0.0);
        [unroll]
        for (int di = 1; di <= 3; di++)
        {
            avg += tex0.Sample(smp, uv + dir * dpx * reach * float(di)).rgb;
            avg += tex0.Sample(smp, uv - dir * dpx * reach * float(di)).rgb;
        }
        avg /= 6.0;
        float3 diff = abs(col - avg);
        float thr = 0.008 + uDebandStr * 0.012;
        if (max(diff.r, max(diff.g, diff.b)) < thr)
            col = avg + (n - 0.5) * (1.5 / 255.0);
    }
    if (uDOF && uAiDepth)
    {
        float dc = tex1.Sample(smp, uv).r;
        float focusDepth = (tex1.Sample(smp, float2(0.5, 0.5)).r
                          + tex1.Sample(smp, float2(0.46, 0.5)).r
                          + tex1.Sample(smp, float2(0.54, 0.5)).r
                          + tex1.Sample(smp, float2(0.5, 0.44)).r
                          + tex1.Sample(smp, float2(0.5, 0.56)).r) * 0.2;
        float coc = abs(dc - focusDepth);
        float blurAmt = smoothstep(uDOFFocus * 0.5, uDOFFocus * 0.5 + uDOFFeather * 0.6, coc) * uDOFStr;
        if (blurAmt > 0.001)
        {
            float2 px = texPx();
            float radius = blurAmt * 18.0;
            float jitter = hash(uv * 517.0) * 6.28318;
            float cj = cos(jitter);
            float sj = sin(jitter);
            float3 acc = float3(0.0, 0.0, 0.0);
            float wsum = 0.0;
            [unroll]
            for (int i = 0; i < 32; i++)
            {
                float a = float(i) * 2.39996323;
                float r = sqrt((float(i) + 0.5) / 32.0);
                float2 dir = float2(cos(a), sin(a));
                dir = float2(dir.x * cj - dir.y * sj, dir.x * sj + dir.y * cj);
                float2 off = dir * r * radius * px;
                float3 sc = tex0.Sample(smp, uv + off).rgb;
                float l = lum(sc);
                float w = 1.0 + smoothstep(0.55, 1.0, l) * 8.0 * blurAmt;
                acc += sc * w;
                wsum += w;
            }
            col = lerp(col, acc / max(wsum, 1e-4), saturate(blurAmt * 1.6));
        }
    }
    if (uSharpen && uShStr > 0.0)
    {
        float2 px = texPx() * uShRadius;
        float3 nN = tex0.Sample(smp, uv + float2(0.0, -px.y)).rgb;
        float3 nS = tex0.Sample(smp, uv + float2(0.0, px.y)).rgb;
        float3 nW = tex0.Sample(smp, uv + float2(-px.x, 0.0)).rgb;
        float3 nE = tex0.Sample(smp, uv + float2(px.x, 0.0)).rgb;
        float3 mnc = min(col, min(min(nN, nS), min(nW, nE)));
        float3 mxc = max(col, max(max(nN, nS), max(nW, nE)));
        float3 amp = saturate(min(mnc, 2.0 - mxc) / max(mxc, 1e-4));
        amp = sqrt(amp);
        float peak = -1.0 / lerp(8.0, 5.0, saturate(uShStr));
        float3 w = amp * peak;
        float3 rcpW = 1.0 / (1.0 + 4.0 * w);
        float3 sharp = saturate(((nN + nS + nW + nE) * w + col) * rcpW);
        float3 delta = clamp((sharp - col) * saturate(uShStr), -uShClamp * 4.0, uShClamp * 4.0);
        col = clamp(col + delta, 0.0, 1.0);
    }
    if (uAO) col *= sampleAO(uv);
    if (uClarityStr > 0.0)
    {
        float hp = lum(col) - lum(tex2.Sample(smp, uv).rgb);
        col += hp * uClarityStr * (1.0 - abs(hp)) * 2.0;
        col = max(col, 0.0);
    }
    if (uGiStr > 0.0 && uAiDepth)
    {
        float3 gi = tex3.Sample(smp, uv).rgb;
        col += gi * col * uGiStr * 1.6;
    }
    if (uGrade)
    {
        col *= uTemp * uBalance;
        col = col * (uGain - uLift) + uLift;
        float3 hsv = rgb2hsv(col);
        hsv.x = frac(hsv.x + uHue / 360.0);
        col = hsv2rgb(hsv);
        col = col + uBri;
        col = pow(max(col, float3(0.0, 0.0, 0.0)), 1.0 / max(uGamma, 0.01));
    }
    col *= uAdaptExp;
    if (uTonemap) col = applyTonemap(col);
    if (uFogStr > 0.0 && uAiDepth)
    {
        float disp = tex1.Sample(smp, uv).r;
        float fog = smoothstep(uFogStart, 1.0, 1.0 - disp) * uFogStr;
        col = lerp(col, float3(uFogGray, uFogGray, uFogGray), saturate(fog * 0.85));
    }
    if (uALStr > 0.0)
    {
        float3 soft = tex2.Sample(smp, uv).rgb;
        col += soft * soft * uALStr * 0.8;
    }
    if (uGrain)
    {
        float2 gp = (uv / texPx()) / max(uGrSize, 0.5) + frac(uTime * 9.7) * 173.0;
        float lw = 0.35 + 0.65 * (1.0 - smoothstep(0.15, 0.9, lum(col)));
        if (uGrColored)
        {
            float3 noise = float3(ign(gp), ign(gp + 41.7), ign(gp + 83.3)) - 0.5;
            col += noise * uGrStr * lw;
        }
        else
        {
            col += (ign(gp) - 0.5) * uGrStr * lw;
        }
    }
    if (uVignette)
    {
        float2 v = (uv - float2(0.5 + uVigCX, 0.5 - uVigCY)) * 2.0;
        float fall = pow(max(dot(v, v), 0.0), uVigFeat);
        float vig = 1.0 - uVigStr * smoothstep(0.15, 1.8, fall);
        col *= clamp(vig, 0.0, 1.0);
    }
    return float4(max(col, 0.0), 1.0);
}

float3 catmullRom(Texture2D t, float2 uv, float2 texSize)
{
    float2 samplePos = uv * texSize;
    float2 texPos1 = floor(samplePos - 0.5) + 0.5;
    float2 f = samplePos - texPos1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 offset12 = w2 / max(w12, 1e-5);
    float2 texPos0 = (texPos1 - 1.0) / texSize;
    float2 texPos3 = (texPos1 + 2.0) / texSize;
    float2 texPos12 = (texPos1 + offset12) / texSize;
    float3 r = float3(0.0, 0.0, 0.0);
    r += t.Sample(smp, float2(texPos12.x, texPos0.y)).rgb * w12.x * w0.y;
    r += t.Sample(smp, float2(texPos0.x, texPos12.y)).rgb * w0.x * w12.y;
    r += t.Sample(smp, float2(texPos12.x, texPos12.y)).rgb * w12.x * w12.y;
    r += t.Sample(smp, float2(texPos3.x, texPos12.y)).rgb * w3.x * w12.y;
    r += t.Sample(smp, float2(texPos12.x, texPos3.y)).rgb * w12.x * w3.y;
    return r / max(w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y, 1e-5);
}

float3 jimenez13(Texture2D t, float2 uv, float2 ts)
{
    float3 a = t.Sample(smp, uv + ts * float2(-2.0, -2.0)).rgb;
    float3 b = t.Sample(smp, uv + ts * float2(0.0, -2.0)).rgb;
    float3 c = t.Sample(smp, uv + ts * float2(2.0, -2.0)).rgb;
    float3 d = t.Sample(smp, uv + ts * float2(-2.0, 0.0)).rgb;
    float3 e = t.Sample(smp, uv).rgb;
    float3 f = t.Sample(smp, uv + ts * float2(2.0, 0.0)).rgb;
    float3 g = t.Sample(smp, uv + ts * float2(-2.0, 2.0)).rgb;
    float3 h = t.Sample(smp, uv + ts * float2(0.0, 2.0)).rgb;
    float3 i = t.Sample(smp, uv + ts * float2(2.0, 2.0)).rgb;
    float3 j = t.Sample(smp, uv + ts * float2(-1.0, -1.0)).rgb;
    float3 k = t.Sample(smp, uv + ts * float2(1.0, -1.0)).rgb;
    float3 l = t.Sample(smp, uv + ts * float2(-1.0, 1.0)).rgb;
    float3 m = t.Sample(smp, uv + ts * float2(1.0, 1.0)).rgb;
    return e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + (j + k + l + m) * 0.125;
}

float4 PSDownsamplePrefilter(VSOut inp) : SV_Target
{
    float3 c = max(jimenez13(tex0, inp.uv, passPx.xy), 0.0);
    float knee = max(uBloomThresh * 0.5, 1e-4);
    float br = max(c.r, max(c.g, c.b));
    float soft = br - uBloomThresh + knee;
    soft = clamp(soft, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee);
    float contribution = max(soft, br - uBloomThresh) / max(br, 1e-4);
    return float4(c * saturate(contribution), 1.0);
}

float4 PSDownsample(VSOut inp) : SV_Target
{
    return float4(jimenez13(tex0, inp.uv, passPx.xy), 1.0);
}

float4 PSUpsampleTent(VSOut inp) : SV_Target
{
    float2 ts = passPx.xy * uBloomRadius;
    float3 s = tex0.Sample(smp, inp.uv + ts * float2(-1.0, -1.0)).rgb
             + tex0.Sample(smp, inp.uv + ts * float2(0.0, -1.0)).rgb * 2.0
             + tex0.Sample(smp, inp.uv + ts * float2(1.0, -1.0)).rgb
             + tex0.Sample(smp, inp.uv + ts * float2(-1.0, 0.0)).rgb * 2.0
             + tex0.Sample(smp, inp.uv).rgb * 4.0
             + tex0.Sample(smp, inp.uv + ts * float2(1.0, 0.0)).rgb * 2.0
             + tex0.Sample(smp, inp.uv + ts * float2(-1.0, 1.0)).rgb
             + tex0.Sample(smp, inp.uv + ts * float2(0.0, 1.0)).rgb * 2.0
             + tex0.Sample(smp, inp.uv + ts * float2(1.0, 1.0)).rgb;
    return float4(s / 16.0 + tex1.Sample(smp, inp.uv).rgb, 1.0);
}

float4 PSBlurH(VSOut inp) : SV_Target
{
    float2 px = passPx.xy;
    float w[9] = { 0.028, 0.067, 0.124, 0.179, 0.204, 0.179, 0.124, 0.067, 0.028 };
    float3 c = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int i = 0; i < 9; i++)
        c += tex0.Sample(smp, inp.uv + float2((float(i) - 4.0) * px.x, 0.0)).rgb * w[i];
    return float4(c, 1.0);
}

float4 PSBlurV(VSOut inp) : SV_Target
{
    float2 px = passPx.xy;
    float w[9] = { 0.028, 0.067, 0.124, 0.179, 0.204, 0.179, 0.124, 0.067, 0.028 };
    float3 c = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int i = 0; i < 9; i++)
        c += tex0.Sample(smp, inp.uv + float2(0.0, (float(i) - 4.0) * px.y)).rgb * w[i];
    return float4(c, 1.0);
}

float4 PSBloomCombine(VSOut inp) : SV_Target
{
    float3 scene = tex0.Sample(smp, inp.uv).rgb;
    float3 bloom = max(tex1.Sample(smp, inp.uv).rgb, 0.0);
    return float4(scene + bloom * uBloomStr * uBloomTint, 1.0);
}

float4 PSSsr(VSOut inp) : SV_Target
{
    if (!uAiDepth || !uPlaneValid || uSsrIntensity <= 0.0)
        return float4(0.0, 0.0, 0.0, 0.0);
    float2 g = float2(inp.uv.x, 1.0 - inp.uv.y);
    float aspect = uTexW / max(uTexH, 1.0);
    float3 Npl = normalize(uPlaneN);
    float z = aiZ(sampG(tex3, g).r);
    float3 P = float3((g.x * 2.0 - 1.0) * AI_TANHALF * aspect * z, (g.y * 2.0 - 1.0) * AI_TANHALF * z, z);
    float distP = dot(Npl, P) - uPlaneD;
    float band = 0.05 + z * 0.04;
    float member = 1.0 - saturate(abs(distP) / band);
    float eo = 6.0 / 256.0;
    float zNear = min(min(aiZ(sampG(tex3, g + float2(eo, 0.0)).r), aiZ(sampG(tex3, g - float2(eo, 0.0)).r)),
                      min(aiZ(sampG(tex3, g + float2(0.0, eo)).r), aiZ(sampG(tex3, g - float2(0.0, eo)).r)));
    float nearObj = saturate((z - zNear) / (0.08 + z * 0.12));
    member *= (1.0 - nearObj * 0.85);
    bool isFloor = member >= 0.05;
    float3 refN = Npl;
    float consistency = 1.0;
    if (isFloor)
    {
        float3 nUp = aiNormalE(tex3, g, 8.0 / 256.0);
        if (nUp.y < 0.25)
            isFloor = false;
    }
    if (!isFloor)
    {
        if (uSsrSheen <= 0.0)
            return float4(0.0, 0.0, 0.0, 0.0);
        float es = 6.0 / 256.0;
        float zr = aiZ(sampG(tex3, g + float2(es, 0.0)).r);
        float zl = aiZ(sampG(tex3, g - float2(es, 0.0)).r);
        float zu = aiZ(sampG(tex3, g + float2(0.0, es)).r);
        float zd = aiZ(sampG(tex3, g - float2(0.0, es)).r);
        float silh = max(max(abs(zr - z), abs(zl - z)), max(abs(zu - z), abs(zd - z)));
        if (silh > 0.16 * z + 0.04)
            return float4(0.0, 0.0, 0.0, 0.0);
        float3 nFine = aiNormalE(tex3, g, 4.0 / 256.0);
        float3 nWide = aiNormalE(tex3, g, 12.0 / 256.0);
        if (dot(nFine, nWide) < 0.5)
            return float4(0.0, 0.0, 0.0, 0.0);
        refN = normalize(nWide * 0.65 + nFine * 0.35);
        if (refN.y < -0.25)
            return float4(0.0, 0.0, 0.0, 0.0);
        float facing = saturate(-refN.z);
        consistency = 1.0 - saturate((facing - 0.85) / 0.12);
        if (consistency <= 0.02)
            return float4(0.0, 0.0, 0.0, 0.0);
    }
    float3 V = normalize(P);
    float3 R = reflect(V, refN);
    float ndv = saturate(dot(-V, refN));
    float f0 = uSsrRefl * 0.8;
    float fres = f0 + (1.0 - f0) * pow(1.0 - ndv, 5.0);
    float maxLen = 4.0 + uSsrDist * 8.0;
    float tmax = maxLen;
    if (R.z < 0.0)
        tmax = min(tmax, (0.06 - P.z) / R.z * 0.99);
    if (tmax < 0.05)
        return float4(0.0, 0.0, 0.0, 0.0);
    float2 hitG = float2(-1.0, -1.0);
    float hitT = 0.0;
    float conf = 0.0;
    bool haveHit = false;
    float prevT = 0.0;
    [loop]
    for (int i = 1; i <= 64; i++)
    {
        float tt = float(i) / 64.0;
        float t = tt * tt * tmax;
        float pz = P.z + R.z * t;
        if (pz < 0.05)
            break;
        float su = (P.x + R.x * t) / (pz * AI_TANHALF * aspect) * 0.5 + 0.5;
        float sv = (P.y + R.y * t) / (pz * AI_TANHALF) * 0.5 + 0.5;
        if (su < 0.0 || su > 1.0 || sv < 0.0 || sv > 1.0)
            break;
        float sceneZ = aiZ(sampG(tex3, float2(su, sv)).r);
        float diff = pz - sceneZ;
        float thick = 0.06 + sceneZ * 0.30;
        if (diff > 0.01 && diff < thick)
        {
            float lo = prevT;
            float hi = t;
            [unroll]
            for (int r = 0; r < 4; r++)
            {
                float mid = (lo + hi) * 0.5;
                float mz = P.z + R.z * mid;
                float mu = saturate((P.x + R.x * mid) / (mz * AI_TANHALF * aspect) * 0.5 + 0.5);
                float mv = saturate((P.y + R.y * mid) / (mz * AI_TANHALF) * 0.5 + 0.5);
                if (mz - aiZ(sampG(tex3, float2(mu, mv)).r) > 0.01) hi = mid; else lo = mid;
            }
            float fz = P.z + R.z * hi;
            hitG = float2((P.x + R.x * hi) / (fz * AI_TANHALF * aspect) * 0.5 + 0.5, (P.y + R.y * hi) / (fz * AI_TANHALF) * 0.5 + 0.5);
            hitT = hi / maxLen;
            float2 e = min(hitG, 1.0 - hitG);
            conf = (1.0 - hitT * 0.35) * saturate(min(e.x, e.y) * 10.0 + 0.15);
            haveHit = true;
            break;
        }
        prevT = t;
    }
    float3 refl;
    if (haveHit)
    {
        float cone = saturate((1.0 - uSsrGloss) * (0.25 + hitT * 2.2) * 1.5 + hitT * 0.12);
        float3 sharp = sampG(tex0, hitG);
        float3 soft = sampG(tex2, hitG);
        float3 wide = sampG(tex1, hitG);
        refl = cone < 0.5 ? lerp(sharp, soft, cone * 2.0) : lerp(soft, wide, cone * 2.0 - 1.0);
    }
    else
    {
        if (!isFloor)
            return float4(0.0, 0.0, 0.0, 0.0);
        if (R.z < 0.02)
            return float4(0.0, 0.0, 0.0, 0.0);
        float upward = saturate(R.y * 2.0);
        if (upward < 0.03)
            return float4(0.0, 0.0, 0.0, 0.0);
        float fz2 = max(P.z + R.z * tmax, 0.06);
        float fu = saturate((P.x + R.x * tmax) / (fz2 * AI_TANHALF * aspect) * 0.5 + 0.5);
        float fv = saturate((P.y + R.y * tmax) / (fz2 * AI_TANHALF) * 0.5 + 0.5);
        refl = sampG(tex2, float2(fu, fv));
        conf = 0.10 * upward;
    }
    float w;
    if (isFloor)
        w = saturate(uSsrIntensity * (0.30 + 0.70 * fres) * member * conf) * (0.55 + 0.45 * member);
    else
        w = saturate(uSsrSheen * uSsrIntensity * (0.2 + 0.8 * fres) * consistency * conf);
    return float4(refl, w);
}

float4 PSComposite(VSOut inp) : SV_Target
{
    float3 scene = tex0.Sample(smp, inp.uv).rgb;
    float4 r = tex1.Sample(smp, inp.uv);
    float w = saturate(r.a);
    float darken = 1.0 - 0.08 * uSsrRefl * saturate(w * 3.0);
    return float4(scene * darken * (1.0 - w) + r.rgb * w, 1.0);
}

float4 PSDepthUp(VSOut inp) : SV_Target
{
    float2 g = float2(inp.uv.x, 1.0 - inp.uv.y);
    float3 guide = sampG(tex0, g);
    float2 lpx = 1.0 / 256.0;
    float total = 0.0;
    float wsum = 1e-5;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float2 gs = g + float2(x, y) * lpx;
            float d = sampG(tex1, gs - uDepthShift).r;
            float3 gLo = sampG(tex2, gs);
            float3 dc = guide - gLo;
            float wc = exp(-dot(dc, dc) * 18.0);
            float ws = exp(-float(x * x + y * y) * 0.35);
            float w = wc * ws;
            total += d * w;
            wsum += w;
        }
    }
    return float4(total / wsum, 0.0, 0.0, 1.0);
}

float4 PSGi(VSOut inp) : SV_Target
{
    if (!uAiDepth)
        return float4(0.0, 0.0, 0.0, 1.0);
    float2 g = float2(inp.uv.x, 1.0 - inp.uv.y);
    float3 P = aiViewPos(tex1, g);
    float3 N = aiNormal(tex1, g);
    float3 acc = float3(0.0, 0.0, 0.0);
    float wsum = 1e-4;
    float baseAng = hash(g * 217.0) * 6.28318;
    [loop]
    for (int i = 0; i < 12; i++)
    {
        float a = baseAng + float(i) * 0.5236;
        float r = (0.02 + uGiRadius * 0.10) * (0.35 + frac(float(i) * 0.618) * 0.65);
        float2 gs = g + float2(cos(a), sin(a)) * r;
        if (gs.x < 0.0 || gs.x > 1.0 || gs.y < 0.0 || gs.y > 1.0)
            continue;
        float3 Ps = aiViewPos(tex1, gs);
        float3 dv = Ps - P;
        float len = max(length(dv), 1e-3);
        float facing = saturate(dot(N, dv / len));
        float atten = saturate(1.0 - len / (P.z * 0.9));
        float3 c = sampG(tex0, gs);
        float w = facing * atten * (lum(c) + 0.05);
        acc += c * w;
        wsum += w;
    }
    float3 bounce = acc / wsum;
    float strength = saturate(wsum * 0.35);
    return float4(bounce * strength, 1.0);
}

float4 PSPassthrough(VSOut inp) : SV_Target
{
    if (uDebugView == 1 && uAiDepth)
    {
        float d = saturate(tex1.Sample(smp, inp.uv).r);
        float3 s0 = float3(0.001, 0.000, 0.014);
        float3 s1 = float3(0.258, 0.039, 0.406);
        float3 s2 = float3(0.577, 0.148, 0.404);
        float3 s3 = float3(0.865, 0.317, 0.226);
        float3 s4 = float3(0.988, 0.645, 0.040);
        float3 s5 = float3(0.988, 0.998, 0.645);
        float t = d * 5.0;
        float3 c = lerp(s0, s1, saturate(t));
        c = lerp(c, s2, saturate(t - 1.0));
        c = lerp(c, s3, saturate(t - 2.0));
        c = lerp(c, s4, saturate(t - 3.0));
        c = lerp(c, s5, saturate(t - 4.0));
        return float4(c, 1.0);
    }
    if (uDebugView == 2 && uAiDepth)
    {
        float2 g = float2(inp.uv.x, 1.0 - inp.uv.y);
        float3 nrm = aiNormal(tex1, g);
        return float4(nrm * 0.5 + 0.5, 1.0);
    }
    if (uDebugView == 3 && uAiDepth)
    {
        float2 g = float2(inp.uv.x, 1.0 - inp.uv.y);
        float2 grid = float2(96.0, 54.0);
        float2 cellId = floor(g * grid);
        float2 c0 = (cellId + 0.5) / grid;
        float3 p0 = aiViewPos(tex1, c0);
        float3 p1 = aiViewPos(tex1, c0 + float2(1.0 / grid.x, 0.0));
        float3 p2 = aiViewPos(tex1, c0 + float2(0.0, 1.0 / grid.y));
        float3 fn = normalize(cross(p2 - p0, p1 - p0));
        if (fn.z > 0.0)
            fn = -fn;
        float lambert = saturate(dot(fn, normalize(float3(0.4, 0.7, -0.6))));
        float3 base = lerp(float3(0.05, 0.08, 0.15), float3(0.55, 0.75, 1.0), lambert);
        base *= 0.35 + saturate(1.0 - p0.z * 0.22);
        float2 fr = frac(g * grid);
        float edge = min(min(fr.x, 1.0 - fr.x), min(fr.y, 1.0 - fr.y));
        float wire = 1.0 - smoothstep(0.02, 0.10, edge);
        return float4(lerp(base, float3(0.9, 0.95, 1.0), wire * 0.85), 1.0);
    }
    if (uUpscale)
        return float4(saturate(catmullRom(tex0, inp.uv, 1.0 / passPx.xy)), 1.0);
    return float4(saturate(tex0.Sample(smp, inp.uv).rgb), 1.0);
}
";
    }
}
