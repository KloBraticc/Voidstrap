namespace Voidstrap.Integrations.MotionBlur
{
    internal static class MotionBlurShaders
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

cbuffer MbParams : register(b0)
{
    float4 dims;
    float4 mb;
};

Texture2D tex0 : register(t0);
Texture2D tex1 : register(t1);
Texture2D tex2 : register(t2);
SamplerState smp : register(s0);

float4 PSPass(VSOut inp) : SV_Target
{
    return float4(tex0.Sample(smp, inp.uv).rgb, 1.0);
}

float3 flowAt(float2 uv)
{
    float4 f = tex1.SampleLevel(smp, uv, 0);
    return float3(f.xy * dims.xy, f.z);
}

float2 motionAt(float2 uv)
{
    float2 o = dims.zw * 6.0;
    float3 c = flowAt(uv);
    if (length(c.xy) < 0.75)
        return float2(0, 0);
    float3 n0 = flowAt(uv + float2(o.x, 0));
    float3 n1 = flowAt(uv - float2(o.x, 0));
    float3 n2 = flowAt(uv + float2(0, o.y));
    float3 n3 = flowAt(uv - float2(0, o.y));
    float w0 = 1.0 / (1.0 + length(n0.xy - c.xy));
    float w1 = 1.0 / (1.0 + length(n1.xy - c.xy));
    float w2 = 1.0 / (1.0 + length(n2.xy - c.xy));
    float w3 = 1.0 / (1.0 + length(n3.xy - c.xy));
    float2 current = (c.xy * 2.0 + n0.xy * w0 + n1.xy * w1 + n2.xy * w2 + n3.xy * w3) / (2.0 + w0 + w1 + w2 + w3);
    float err = (c.z * 2.0 + n0.z + n1.z + n2.z + n3.z) / 6.0;
    float2 previous = tex2.SampleLevel(smp, uv, 0).xy * dims.xy;
    float diff = length(current - previous) / (length(current) + length(previous) + 1.0);
    float agreement = saturate(1.6 - 1.6 * diff);
    float2 blended = lerp(current, lerp(previous, current, 0.55), agreement);
    if (agreement < 0.5)
        blended = length(previous) < length(blended) ? previous : blended;
    float confidence = saturate(1.0 - err * 2.2) * lerp(0.4, 1.0, agreement);
    float2 px = blended * mb.x * confidence;
    float raw = length(px);
    px *= raw > mb.z ? (raw - mb.z * 0.5) / max(raw, 0.001) : 0.0;
    float len = length(px);
    return len > mb.y ? px * (mb.y / len) : px;
}

float4 PSBlur(VSOut inp) : SV_Target
{
    float3 center = tex0.SampleLevel(smp, inp.uv, 0).rgb;
    float2 v = motionAt(inp.uv);
    float lenV = length(v);
    [branch] if (lenV < 0.5)
        return float4(center, 1.0);

    int taps = (int)clamp(ceil(lenV * 0.34), 4.0, 24.0);
    float jitter = frac(52.9829189 * frac(dot(inp.pos.xy, float2(0.06711056, 0.00583715)))) - 0.5;
    float3 sum = center;
    float weight = 1.0;
    float2 selfFlow = flowAt(inp.uv).xy;
    float selfLen = max(length(selfFlow), 0.75);
    float2 selfDir = selfFlow / selfLen;
    [loop] for (int i = 0; i < taps; i++)
    {
        float t = ((i + 0.5 + jitter) / taps) - 0.5;
        float2 tuv = inp.uv + v * t * dims.zw;
        float2 tf = flowAt(tuv).xy;
        float along = saturate(dot(tf, selfDir) / selfLen);
        float match = saturate(1.0 - length(tf - selfFlow) / (selfLen + 1.0));
        float w = along * match;
        sum += tex0.SampleLevel(smp, tuv, 0).rgb * w;
        weight += w;
    }
    return float4(sum / weight, 1.0);
}
";
    }
}
