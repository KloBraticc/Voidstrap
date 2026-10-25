#include "ReShade.fxh"

#ifndef RISHADE_SSR_HALF_RES
	#define RISHADE_SSR_HALF_RES 0
#endif

#if RISHADE_SSR_HALF_RES
	#define RSSR_W (BUFFER_WIDTH / 2)
	#define RSSR_H (BUFFER_HEIGHT / 2)
#else
	#define RSSR_W BUFFER_WIDTH
	#define RSSR_H BUFFER_HEIGHT
#endif

uniform float FieldOfView <
	ui_type = "slider"; ui_min = 40.0; ui_max = 120.0; ui_step = 1.0;
	ui_label = "Camera field of view";
	ui_tooltip = "Match the vertical field of view of the game camera. Roblox uses 70 by default.";
	ui_category = "Scene";
> = 70.0;

uniform float Intensity <
	ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01;
	ui_label = "Reflection strength";
	ui_category = "Look";
> = 0.55;

uniform float BaseReflectivity <
	ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01;
	ui_label = "Base reflectivity";
	ui_tooltip = "How reflective surfaces are when viewed head on. Grazing angles always reflect more.";
	ui_category = "Look";
> = 0.08;

uniform float Roughness <
	ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01;
	ui_label = "Surface roughness";
	ui_tooltip = "Softens the reflection. Higher values hide noise from the estimated depth.";
	ui_category = "Look";
> = 0.35;

uniform float FloorBias <
	ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01;
	ui_label = "Floor only";
	ui_tooltip = "Limits reflections to surfaces that face up. Walls, characters and props stay clean.";
	ui_category = "Surfaces";
> = 0.85;

uniform float FlatnessBias <
	ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01;
	ui_label = "Flat surfaces only";
	ui_tooltip = "Skips bumpy or noisy areas where the estimated depth is unreliable.";
	ui_category = "Surfaces";
> = 0.7;

uniform float MaxDistance <
	ui_type = "slider"; ui_min = 0.05; ui_max = 1.0; ui_step = 0.01;
	ui_label = "Ray length";
	ui_category = "Tracing";
> = 0.35;

uniform float Thickness <
	ui_type = "slider"; ui_min = 0.005; ui_max = 0.2; ui_step = 0.001;
	ui_label = "Object thickness";
	ui_category = "Tracing";
> = 0.03;

uniform int Steps <
	ui_type = "slider"; ui_min = 8; ui_max = 64;
	ui_label = "Ray steps";
	ui_category = "Tracing";
> = 28;

uniform float EdgeFade <
	ui_type = "slider"; ui_min = 0.01; ui_max = 0.3; ui_step = 0.01;
	ui_label = "Screen edge fade";
	ui_category = "Tracing";
> = 0.12;

uniform float DistanceFade <
	ui_type = "slider"; ui_min = 0.0; ui_max = 1.0; ui_step = 0.01;
	ui_label = "Far distance fade";
	ui_tooltip = "Fades reflections on distant surfaces where depth estimation is weakest.";
	ui_category = "Tracing";
> = 0.6;

uniform int DebugView <
	ui_type = "combo";
	ui_items = "Off\0Reflections\0Normals\0Reflection mask\0Depth\0";
	ui_label = "Debug view";
	ui_category = "Debug";
> = 0;

uniform uint FrameCount < source = "framecount"; >;

texture RSSR_NormalTex { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; Format = RGBA16F; };
texture RSSR_NormalBlurTex { Width = BUFFER_WIDTH; Height = BUFFER_HEIGHT; Format = RGBA16F; };
texture RSSR_TraceTex { Width = RSSR_W; Height = RSSR_H; Format = RGBA16F; };
texture RSSR_FilterTex { Width = RSSR_W; Height = RSSR_H; Format = RGBA16F; };

sampler RSSR_Normal { Texture = RSSR_NormalTex; MagFilter = POINT; MinFilter = POINT; MipFilter = POINT; };
sampler RSSR_NormalBlur { Texture = RSSR_NormalBlurTex; MagFilter = POINT; MinFilter = POINT; MipFilter = POINT; };
sampler RSSR_TracePoint { Texture = RSSR_TraceTex; MagFilter = POINT; MinFilter = POINT; MipFilter = POINT; };
sampler RSSR_FilterPoint { Texture = RSSR_FilterTex; MagFilter = POINT; MinFilter = POINT; MipFilter = POINT; };
sampler RSSR_Color { Texture = ReShade::BackBufferTex; AddressU = CLAMP; AddressV = CLAMP; };

static const float RSSR_Far = RESHADE_DEPTH_LINEARIZATION_FAR_PLANE;
static const float2 RSSR_TracePixel = float2(1.0 / RSSR_W, 1.0 / RSSR_H);

float2 ProjectionScale()
{
	float t = tan(radians(FieldOfView) * 0.5);
	return float2(t * BUFFER_ASPECT_RATIO, -t);
}

float LinearDepth(float2 uv)
{
	return ReShade::GetLinearizedDepth(uv);
}

float3 ViewPosition(float2 uv, float depth)
{
	float z = max(depth, 1e-5) * RSSR_Far;
	return float3((uv * 2.0 - 1.0) * ProjectionScale() * z, z);
}

float3 ViewPositionAt(float2 uv)
{
	return ViewPosition(uv, LinearDepth(uv));
}

float2 ProjectToUv(float3 p)
{
	return (p.xy / max(p.z, 1e-4)) / ProjectionScale() * 0.5 + 0.5;
}

float3 ToLinear(float3 c)
{
	return c * c;
}

float3 ToGamma(float3 c)
{
	return sqrt(max(c, 0.0));
}

float Noise(float2 pixel)
{
	float2 p = pixel + float(FrameCount % 64u) * float2(5.588238, 5.588238);
	return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
}

float3 PickTangent(float3 center, float3 a, float3 b)
{
	float3 da = center - a;
	float3 db = b - center;
	return abs(da.z) < abs(db.z) ? da : db;
}

void PS_Normals(float4 vpos : SV_Position, float2 uv : TEXCOORD, out float4 result : SV_Target)
{
	float depth = LinearDepth(uv);
	float3 center = ViewPosition(uv, depth);
	float2 offset = ReShade::PixelSize * 2.0;
	float3 left = ViewPositionAt(uv - float2(offset.x, 0.0));
	float3 right = ViewPositionAt(uv + float2(offset.x, 0.0));
	float3 up = ViewPositionAt(uv - float2(0.0, offset.y));
	float3 down = ViewPositionAt(uv + float2(0.0, offset.y));
	float3 tx = PickTangent(center, left, right);
	float3 ty = PickTangent(center, up, down);
	float3 n = normalize(cross(ty, tx));
	if (dot(n, center) > 0.0)
		n = -n;
	result = float4(n, depth);
}

void PS_NormalBlur(float4 vpos : SV_Position, float2 uv : TEXCOORD, out float4 result : SV_Target)
{
	float4 center = tex2Dlod(RSSR_Normal, float4(uv, 0, 0));
	float3 sum = 0.0;
	float weightSum = 0.0;
	float agreement = 0.0;
	[unroll]
	for (int y = -2; y <= 2; y++)
	{
		[unroll]
		for (int x = -2; x <= 2; x++)
		{
			float2 tap = uv + float2(x, y) * ReShade::PixelSize * 2.0;
			float4 s = tex2Dlod(RSSR_Normal, float4(tap, 0, 0));
			float depthWeight = exp(-abs(s.w - center.w) / max(center.w * 0.02, 1e-6));
			float spatial = exp(-float(x * x + y * y) * 0.18);
			float w = depthWeight * spatial;
			sum += s.xyz * w;
			weightSum += w;
			agreement += saturate(dot(s.xyz, center.xyz)) * w;
		}
	}
	float3 n = normalize(sum / max(weightSum, 1e-6));
	float flatness = agreement / max(weightSum, 1e-6);
	flatness = saturate((flatness - 0.85) / 0.13);
	result = float4(n.xy * 0.5 + 0.5, flatness, center.w);
}

float3 DecodeNormal(float4 data)
{
	float2 xy = data.xy * 2.0 - 1.0;
	return float3(xy, -sqrt(saturate(1.0 - dot(xy, xy))));
}

float SurfaceMask(float3 n, float flatness, float depth)
{
	float floorMask = lerp(1.0, smoothstep(0.25, 0.65, n.y), FloorBias);
	float flatMask = lerp(1.0, flatness, FlatnessBias);
	float sky = 1.0 - smoothstep(0.6, 0.95, depth);
	float far = lerp(1.0, 1.0 - smoothstep(0.02, 0.25, depth), DistanceFade);
	return floorMask * flatMask * sky * far;
}

void PS_Trace(float4 vpos : SV_Position, float2 uv : TEXCOORD, out float4 result : SV_Target)
{
	result = 0.0;
	float4 data = tex2Dlod(RSSR_NormalBlur, float4(uv, 0, 0));
	float depth = data.w;
	float3 n = DecodeNormal(data);
	float mask = SurfaceMask(n, data.z, depth);
	if (mask <= 0.002)
		return;

	float3 origin = ViewPosition(uv, depth);
	float3 view = normalize(origin);
	float3 dir = reflect(view, n);
	if (dir.z < -0.6)
		return;

	float rayLength = origin.z * MaxDistance;
	if (dir.z < 0.0)
		rayLength = min(rayLength, origin.z * 0.9 / max(-dir.z, 1e-4));
	float3 endPoint = origin + dir * rayLength;

	float2 startUv = uv;
	float2 endUv = ProjectToUv(endPoint);
	float2 deltaPixels = (endUv - startUv) / RSSR_TracePixel;
	float screenLength = length(deltaPixels);
	if (screenLength < 3.0)
		return;

	float invStart = 1.0 / origin.z;
	float invEnd = 1.0 / endPoint.z;
	float jitter = Noise(vpos.xy);
	float minT = 1.5 / screenLength;
	float prevT = minT;
	float hitT = -1.0;
	int stepCount = Steps;

	[loop]
	for (int i = 1; i <= stepCount; i++)
	{
		float f = (float(i) - 1.0 + jitter) / float(stepCount);
		float t = max(minT, f * f);
		float2 sampleUv = lerp(startUv, endUv, t);
		if (any(sampleUv < 0.0) || any(sampleUv > 1.0))
			break;
		float rayZ = 1.0 / lerp(invStart, invEnd, t);
		float sceneZ = LinearDepth(sampleUv) * RSSR_Far;
		float diff = rayZ - sceneZ;
		if (diff > 0.0)
		{
			if (diff < Thickness * rayZ + abs(rayZ - 1.0 / lerp(invStart, invEnd, prevT)))
				hitT = t;
			break;
		}
		prevT = t;
	}

	if (hitT < 0.0)
		return;

	float lo = prevT;
	float hi = hitT;
	[unroll]
	for (int r = 0; r < 6; r++)
	{
		float mid = 0.5 * (lo + hi);
		float2 midUv = lerp(startUv, endUv, mid);
		float rayZ = 1.0 / lerp(invStart, invEnd, mid);
		float sceneZ = LinearDepth(midUv) * RSSR_Far;
		if (rayZ > sceneZ)
			hi = mid;
		else
			lo = mid;
	}

	float2 hitUv = lerp(startUv, endUv, hi);
	float hitDepth = LinearDepth(hitUv);
	if (hitDepth > 0.98)
		return;

	float4 hitData = tex2Dlod(RSSR_NormalBlur, float4(hitUv, 0, 0));
	float3 hitNormal = DecodeNormal(hitData);
	float facing = saturate(-dot(hitNormal, dir) * 4.0 + 0.5);

	float2 edge = min(hitUv, 1.0 - hitUv);
	float edgeMask = saturate(min(edge.x, edge.y) / EdgeFade);
	edgeMask *= edgeMask;
	float travel = 1.0 - smoothstep(0.6, 1.0, hi);
	float confidence = mask * facing * edgeMask * travel;
	float3 color = ToLinear(tex2Dlod(RSSR_Color, float4(hitUv, 0, 0)).rgb);
	result = float4(color * confidence, confidence);
}

float4 BilateralFilter(sampler source, float2 uv, float2 axis)
{
	float4 centerData = tex2Dlod(RSSR_NormalBlur, float4(uv, 0, 0));
	float centerDepth = centerData.w;
	float3 centerNormal = DecodeNormal(centerData);
	float radius = 1.0 + Roughness * 4.0;
	float4 sum = 0.0;
	float weightSum = 0.0;
	[unroll]
	for (int i = -6; i <= 6; i++)
	{
		float2 tap = uv + axis * RSSR_TracePixel * float(i) * radius;
		float4 s = tex2Dlod(source, float4(tap, 0, 0));
		float4 g = tex2Dlod(RSSR_NormalBlur, float4(tap, 0, 0));
		float depthWeight = exp(-abs(g.w - centerDepth) / max(centerDepth * 0.03, 1e-6));
		float normalWeight = pow(saturate(dot(DecodeNormal(g), centerNormal)), 16.0);
		float spatial = exp(-float(i * i) / 18.0);
		float w = spatial * depthWeight * normalWeight + (i == 0 ? 1e-5 : 0.0);
		sum += s * w;
		weightSum += w;
	}
	return sum / max(weightSum, 1e-6);
}

void PS_FilterH(float4 vpos : SV_Position, float2 uv : TEXCOORD, out float4 result : SV_Target)
{
	result = BilateralFilter(RSSR_TracePoint, uv, float2(1.0, 0.0));
}

void PS_FilterV(float4 vpos : SV_Position, float2 uv : TEXCOORD, out float4 result : SV_Target)
{
	result = BilateralFilter(RSSR_FilterPoint, uv, float2(0.0, 1.0));
}

float4 UpsampleReflection(float2 uv)
{
#if RISHADE_SSR_HALF_RES
	float centerDepth = tex2Dlod(RSSR_NormalBlur, float4(uv, 0, 0)).w;
	float2 base = uv / RSSR_TracePixel - 0.5;
	float2 cell = floor(base);
	float4 sum = 0.0;
	float weightSum = 0.0;
	[unroll]
	for (int y = 0; y <= 1; y++)
	{
		[unroll]
		for (int x = 0; x <= 1; x++)
		{
			float2 tapUv = (cell + float2(x, y) + 0.5) * RSSR_TracePixel;
			float2 frac2 = abs(base - (cell + float2(x, y)));
			float bilinear = (1.0 - frac2.x) * (1.0 - frac2.y);
			float tapDepth = tex2Dlod(RSSR_NormalBlur, float4(tapUv, 0, 0)).w;
			float w = bilinear * exp(-abs(tapDepth - centerDepth) / max(centerDepth * 0.02, 1e-6)) + 1e-4;
			sum += tex2Dlod(RSSR_TracePoint, float4(tapUv, 0, 0)) * w;
			weightSum += w;
		}
	}
	return sum / weightSum;
#else
	return tex2Dlod(RSSR_TracePoint, float4(uv, 0, 0));
#endif
}

void PS_Composite(float4 vpos : SV_Position, float2 uv : TEXCOORD, out float4 result : SV_Target)
{
	float4 color = tex2Dlod(RSSR_Color, float4(uv, 0, 0));
	float4 data = tex2Dlod(RSSR_NormalBlur, float4(uv, 0, 0));
	float3 n = DecodeNormal(data);
	float depth = data.w;

	if (DebugView == 2)
	{
		result = float4(n * float3(0.5, 0.5, -0.5) + 0.5, 1.0);
		return;
	}
	if (DebugView == 3)
	{
		result = float4(SurfaceMask(n, data.z, depth).xxx, 1.0);
		return;
	}
	if (DebugView == 4)
	{
		result = float4(saturate(depth * 8.0).xxx, 1.0);
		return;
	}

	float4 reflection = UpsampleReflection(uv);
	float coverage = saturate(reflection.a);
	float3 reflected = reflection.a > 1e-4 ? reflection.rgb / reflection.a : 0.0;

	if (DebugView == 1)
	{
		result = float4(ToGamma(reflected * coverage), 1.0);
		return;
	}

	float3 view = normalize(ViewPosition(uv, depth));
	float cosTheta = saturate(dot(-view, n));
	float fresnel = BaseReflectivity + (1.0 - BaseReflectivity) * pow(1.0 - cosTheta, 5.0);
	float amount = saturate(fresnel * Intensity * coverage * (1.0 - Roughness * 0.35));

	float3 base = ToLinear(color.rgb);
	float3 outColor = lerp(base, reflected, amount);
	result = float4(ToGamma(outColor), color.a);
}

technique RiShade_SSR <
	ui_label = "RiShade Reflections";
	ui_tooltip = "Screen space reflections tuned for the estimated depth RiShade provides in Roblox.";
>
{
	pass Normals { VertexShader = PostProcessVS; PixelShader = PS_Normals; RenderTarget = RSSR_NormalTex; }
	pass NormalBlur { VertexShader = PostProcessVS; PixelShader = PS_NormalBlur; RenderTarget = RSSR_NormalBlurTex; }
	pass Trace { VertexShader = PostProcessVS; PixelShader = PS_Trace; RenderTarget = RSSR_TraceTex; }
	pass FilterH { VertexShader = PostProcessVS; PixelShader = PS_FilterH; RenderTarget = RSSR_FilterTex; }
	pass FilterV { VertexShader = PostProcessVS; PixelShader = PS_FilterV; RenderTarget = RSSR_TraceTex; }
	pass Composite { VertexShader = PostProcessVS; PixelShader = PS_Composite; }
}
