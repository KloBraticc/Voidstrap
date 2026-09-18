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

static const int Taps = 16;

float4 PSPass(VSOut inp) : SV_Target
{
    return float4(tex0.Sample(smp, inp.uv).rgb, 1.0);
}

float2 motionAt(float2 uv)
{
    float4 f = tex1.SampleLevel(smp, uv, 0);
    float2 previous = tex2.SampleLevel(smp, uv, 0).xy * dims.xy;
    float2 current = f.xy * dims.xy;
    float agreement = saturate(1.5 - 1.5 * length(current - previous) / (length(current) + length(previous) + 1.0));
    float confidence = saturate(1.0 - f.z * 2.8) * agreement;
    float2 px = current * mb.x * confidence;
    float len = length(px);
    if (len < mb.z)
        return float2(0.0, 0.0);
    return len > mb.y ? px * (mb.y / len) : px;
}

float4 PSBlur(VSOut inp) : SV_Target
{
    float3 center = tex0.SampleLevel(smp, inp.uv, 0).rgb;
    float2 v = motionAt(inp.uv);
    float lenV = length(v);
    if (lenV < 0.5)
        return float4(center, 1.0);

    float3 sum = center;
    float weight = 1.0;
    [unroll] for (int i = 0; i < Taps; i++)
    {
        float t = ((i + 0.5) / Taps) - 0.5;
        float2 offsetPx = v * t;
        float2 uv = inp.uv + offsetPx * dims.zw;
        float dist = length(offsetPx);
        float lenS = length(motionAt(uv));
        float reach = max(saturate(lenV * 0.5 - dist + 1.0), saturate(lenS * 0.5 - dist + 1.0));
        float w = reach;
        sum += tex0.SampleLevel(smp, uv, 0).rgb * w;
        weight += w;
    }
    return float4(sum / weight, 1.0);
}
";
    }
}
