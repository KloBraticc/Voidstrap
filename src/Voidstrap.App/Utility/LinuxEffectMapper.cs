using System;
using System.Globalization;
using System.Text;
using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.FrameGeneration;
using Voidstrap.Integrations.RiShade;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class LinuxEffectMapper
{
	public const int SupportedAntiAliasingMethods = 5;

	public static LinuxEffectOptions CreateOptions()
	{
		RiShadeSettings shade = RiShadeSettings.Current;
		bool shadeOn = App.Settings.Prop.RiShadeEnabled && shade.HasVisibleEffects;

		int soberSharpness = Math.Clamp(App.Settings.Prop.SoberSharpness, 0, 100);
		bool soberSharpen = soberSharpness > 0;
		bool shadeSharpen = shadeOn && shade.SharpenEnabled;

		float sharpnessAmount = soberSharpen
			? soberSharpness / 100f
			: shadeSharpen ? Math.Clamp(shade.SharpenStrength, 0f, 1f) : 0.4f;

		return new LinuxEffectOptions(
			Enabled: shadeOn || soberSharpen || MapAntiAliasing() is not null || MapFrameGenMultiplier() > 1,
			AntiAliasing: MapAntiAliasing(),
			AntiAliasingUltra: AntiAliasingSettings.MethodIndex is 2 or 4,
			Sharpening: shadeSharpen || soberSharpen,
			SharpnessAmount: sharpnessAmount,
			GradingShader: shadeOn ? BuildGradingShader(shade) : null,
			FrameGenMultiplier: MapFrameGenMultiplier());
	}

	public static string? MapAntiAliasing()
	{
		return AntiAliasingSettings.MethodIndex switch
		{
			1 or 2 => "fxaa",
			3 or 4 => "smaa",
			_ => null
		};
	}

	public static bool IsAntiAliasingSupported()
	{
		return AntiAliasingSettings.MethodIndex < SupportedAntiAliasingMethods;
	}

	private static int MapFrameGenMultiplier()
	{
		return FrameGenSettings.ModeIndex > 0 ? 2 : 0;
	}

	private static string F(float value)
	{
		return value.ToString("0.0000", CultureInfo.InvariantCulture);
	}

	public static void RefreshConfiguration()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		try
		{
			Voidstrap.Platform.Linux.LinuxEffectOptions options = CreateOptions();

			if (!options.Enabled)
				return;

			Voidstrap.Platform.Linux.LinuxEffectLayers.WriteConfiguration(options);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxEffectMapper::RefreshConfiguration", "The effect configuration could not be refreshed: " + ex.Message);
		}
	}

	private static string BuildGradingShader(RiShadeSettings shade)
	{
		float brightness = shade.GradeEnabled ? shade.Brightness : 0f;
		float gamma = shade.GradeEnabled ? Math.Clamp(shade.Gamma, 0.1f, 5f) : 1f;
		float[] gain = shade.GradeEnabled ? shade.Gain : [1f, 1f, 1f];
		float[] lift = shade.GradeEnabled ? shade.Lift : [0f, 0f, 0f];
		float[] balance = shade.GradeEnabled ? shade.ColorBalance : [1f, 1f, 1f];
		float vignette = shade.VignetteEnabled ? Math.Clamp(shade.VignetteStrength, 0f, 1f) : 0f;
		float feather = shade.VignetteEnabled ? Math.Max(0.01f, shade.VignetteFeather) : 1f;

		StringBuilder shader = new();
		shader.AppendLine("texture VoidstrapBackBufferTex : COLOR;");
		shader.AppendLine("sampler VoidstrapBackBuffer { Texture = VoidstrapBackBufferTex; };");
		shader.AppendLine();
		shader.AppendLine("void PostProcessVS(in uint id : SV_VertexID, out float4 position : SV_Position, out float2 texcoord : TEXCOORD)");
		shader.AppendLine("{");
		shader.AppendLine("    texcoord.x = (id == 2) ? 2.0 : 0.0;");
		shader.AppendLine("    texcoord.y = (id == 1) ? 2.0 : 0.0;");
		shader.AppendLine("    position = float4(texcoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);");
		shader.AppendLine("}");
		shader.AppendLine();
		shader.AppendLine("float3 VoidstrapGradePass(float4 pos : SV_Position, float2 texcoord : TEXCOORD) : SV_Target");
		shader.AppendLine("{");
		shader.AppendLine("    float3 color = tex2D(VoidstrapBackBuffer, texcoord).rgb;");
		shader.AppendLine("    color = saturate(color + " + F(brightness) + ");");
		shader.AppendLine("    color = pow(max(color, 0.0001), 1.0 / " + F(gamma) + ");");
		shader.AppendLine("    color = color * float3(" + F(gain[0]) + ", " + F(gain[1]) + ", " + F(gain[2]) + ");");
		shader.AppendLine("    color = color + float3(" + F(lift[0]) + ", " + F(lift[1]) + ", " + F(lift[2]) + ");");
		shader.AppendLine("    color = color * float3(" + F(balance[0]) + ", " + F(balance[1]) + ", " + F(balance[2]) + ");");

		if (vignette > 0f)
		{
			shader.AppendLine("    float2 centered = texcoord - float2(" + F(0.5f + shade.VignetteCenterX) + ", " + F(0.5f + shade.VignetteCenterY) + ");");
			shader.AppendLine("    float falloff = 1.0 - saturate(length(centered) * " + F(feather) + ");");
			shader.AppendLine("    color = lerp(color, color * falloff, " + F(vignette) + ");");
		}

		shader.AppendLine("    return saturate(color);");
		shader.AppendLine("}");
		shader.AppendLine();
		shader.AppendLine("technique voidstrapGrade");
		shader.AppendLine("{");
		shader.AppendLine("    pass");
		shader.AppendLine("    {");
		shader.AppendLine("        VertexShader = PostProcessVS;");
		shader.AppendLine("        PixelShader = VoidstrapGradePass;");
		shader.AppendLine("    }");
		shader.AppendLine("}");
		return shader.ToString();
	}
}
