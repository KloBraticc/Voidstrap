using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.FrameGeneration;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Integrations.RiShade;
using Voidstrap.Platform.Linux;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

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
		bool liveColor = LinuxSoberRuntimeProvider.UseCompositor;
		bool colorGrade = !liveColor && HasLiveColorEffect();
		bool shadeSharpen = shadeOn && shade.SharpenEnabled;
		HomepageMedia? homepageMedia = ResolveHomepageMedia();
		string? homepageShader = BuildHomepageShader(homepageMedia);

		float sharpnessAmount = soberSharpen
			? soberSharpness / 100f
			: shadeSharpen ? Math.Clamp(shade.SharpenStrength, 0f, 1f) : 0.4f;

		return new LinuxEffectOptions(
			Enabled: shadeOn || colorGrade || soberSharpen || MapAntiAliasing() is not null || MapFrameGenMultiplier() > 1 || homepageShader is not null,
			AntiAliasing: MapAntiAliasing(),
			AntiAliasingUltra: AntiAliasingSettings.MethodIndex is 2 or 4,
			Sharpening: shadeSharpen || soberSharpen,
			SharpnessAmount: sharpnessAmount,
			GradingShader: shadeOn || colorGrade ? BuildGradingShader(shade, !liveColor) : null,
			HomepageShader: homepageShader,
			HomepageMediaPath: homepageMedia?.Path,
			FrameGenMultiplier: MapFrameGenMultiplier());
	}

	private static HomepageMedia? ResolveHomepageMedia()
	{
		if (!App.Settings.Prop.HomepageBackgroundOverlayEnabled || OverlaySettings.HomepageBackgroundMode != "Media")
			return null;
		string path = App.Settings.Prop.HomepageBackgroundOverlayMediaPath ?? string.Empty;
		if (!File.Exists(path))
			return null;
		if (Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga"))
			return null;
		try
		{
			SixLabors.ImageSharp.ImageInfo? sourceInfo = SixLabors.ImageSharp.Image.Identify(path);
			if (sourceInfo is null || sourceInfo.Width <= 0 || sourceInfo.Height <= 0 || sourceInfo.Width > 8192 || sourceInfo.Height > 8192)
				return null;
			(double displayWidth, double displayHeight) = ScreenMetrics.GetPrimary();
			int targetWidth = Math.Clamp((int)Math.Round(displayWidth), 320, 7680);
			int targetHeight = Math.Clamp((int)Math.Round(displayHeight), 240, 4320);
			FileInfo sourceFile = new(path);
			string identity = Path.GetFullPath(path)
				+ "|" + sourceFile.Length.ToString(CultureInfo.InvariantCulture)
				+ "|" + sourceFile.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
				+ "|" + targetWidth.ToString(CultureInfo.InvariantCulture)
				+ "x" + targetHeight.ToString(CultureInfo.InvariantCulture);
			string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
			Directory.CreateDirectory(LinuxEffectLayers.ConfigDirectory);
			string preparedPath = Path.Combine(LinuxEffectLayers.ConfigDirectory, "VoidstrapHomepagePrepared" + fingerprint + ".png");
			if (!File.Exists(preparedPath))
			{
				using SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(path);
				image.Mutate(context => context.Resize(new SixLabors.ImageSharp.Processing.ResizeOptions
				{
					Size = new SixLabors.ImageSharp.Size(targetWidth, targetHeight),
					Mode = SixLabors.ImageSharp.Processing.ResizeMode.Crop,
					Position = SixLabors.ImageSharp.Processing.AnchorPositionMode.Center,
					Sampler = SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3
				}));
				image.SaveAsPng(preparedPath);
				foreach (string previous in Directory.EnumerateFiles(LinuxEffectLayers.ConfigDirectory, "VoidstrapHomepagePrepared*.png"))
				{
					if (!string.Equals(previous, preparedPath, StringComparison.Ordinal))
						File.Delete(previous);
				}
			}
			return new HomepageMedia(preparedPath, targetWidth, targetHeight);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static string? BuildHomepageShader(HomepageMedia? media)
	{
		if (!App.Settings.Prop.HomepageBackgroundOverlayEnabled || VirtualMachineProfile.ShouldForceSafeGraphics)
			return null;
		string mode = OverlaySettings.HomepageBackgroundMode;
		if (mode == "Media" && media is null)
			return null;
		(float firstRed, float firstGreen, float firstBlue) = ParseColor(App.Settings.Prop.HomepageBackgroundOverlayColor, 18, 18, 21);
		(float secondRed, float secondGreen, float secondBlue) = ParseColor(App.Settings.Prop.HomepageBackgroundOverlayGradientColor, 91, 46, 255);
		float angle = (float)(App.Settings.Prop.HomepageBackgroundOverlayGradientAngle * Math.PI / 180d);
		StringBuilder shader = new();
		shader.AppendLine("texture VoidstrapHomepageBackBufferTex : COLOR;");
		shader.AppendLine("sampler VoidstrapHomepageBackBuffer { Texture = VoidstrapHomepageBackBufferTex; };");
		shader.AppendLine("texture VoidstrapHomepageStateTex");
		shader.AppendLine("{");
		shader.AppendLine("    Width = 1;");
		shader.AppendLine("    Height = 1;");
		shader.AppendLine("    Format = R8;");
		shader.AppendLine("};");
		shader.AppendLine("sampler VoidstrapHomepageState { Texture = VoidstrapHomepageStateTex; MinFilter = POINT; MagFilter = POINT; AddressU = CLAMP; AddressV = CLAMP; };");
		if (media is not null)
		{
			string mediaName = Path.GetFileName(LinuxEffectLayers.HomepageMediaFile(media.Path)).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
			shader.AppendLine("texture VoidstrapHomepageMediaTex < source = \"" + mediaName + "\"; >");
			shader.AppendLine("{");
			shader.AppendLine("    Width = " + media.Width.ToString(CultureInfo.InvariantCulture) + ";");
			shader.AppendLine("    Height = " + media.Height.ToString(CultureInfo.InvariantCulture) + ";");
			shader.AppendLine("    Format = RGBA8;");
			shader.AppendLine("    MipLevels = 1;");
			shader.AppendLine("};");
			shader.AppendLine("sampler VoidstrapHomepageMedia { Texture = VoidstrapHomepageMediaTex; MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR; AddressU = CLAMP; AddressV = CLAMP; };");
		}
		shader.AppendLine("void VoidstrapHomepageVS(in uint id : SV_VertexID, out float4 position : SV_Position, out float2 texcoord : TEXCOORD)");
		shader.AppendLine("{");
		shader.AppendLine("    texcoord.x = (id == 2) ? 2.0 : 0.0;");
		shader.AppendLine("    texcoord.y = (id == 1) ? 2.0 : 0.0;");
		shader.AppendLine("    position = float4(texcoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);");
		shader.AppendLine("}");
		shader.AppendLine("float VoidstrapHomepageWeight(float3 pixel)");
		shader.AppendLine("{");
		shader.AppendLine("    float originalDistance = max(abs(pixel.r - 18.0 / 255.0), max(abs(pixel.g - 18.0 / 255.0), abs(pixel.b - 21.0 / 255.0)));");
		shader.AppendLine("    float soberDistance = max(abs(pixel.r - 18.0 / 255.0), max(abs(pixel.g - 18.0 / 255.0), abs(pixel.b - 24.0 / 255.0)));");
		shader.AppendLine("    return 1.0 - smoothstep(1.0 / 255.0, 10.0 / 255.0, min(originalDistance, soberDistance));");
		shader.AppendLine("}");
		shader.AppendLine("float VoidstrapHomepageDetect(float4 pos : SV_Position, float2 texcoord : TEXCOORD) : SV_Target");
		shader.AppendLine("{");
		shader.AppendLine("    float matches = 0.0;");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.03, 0.04)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.18, 0.10)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.39, 0.20)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.62, 0.18)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.82, 0.12)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.97, 0.04)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.05, 0.48)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.95, 0.48)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.04, 0.94)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.24, 0.89)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.76, 0.89)).rgb));");
		shader.AppendLine("    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.96, 0.94)).rgb));");
		shader.AppendLine("    return step(3.5, matches);");
		shader.AppendLine("}");
		shader.AppendLine("float3 VoidstrapHomepagePass(float4 pos : SV_Position, float2 texcoord : TEXCOORD) : SV_Target");
		shader.AppendLine("{");
		shader.AppendLine("    float3 pixel = tex2D(VoidstrapHomepageBackBuffer, texcoord).rgb;");
		shader.AppendLine("    if (tex2D(VoidstrapHomepageState, float2(0.5, 0.5)).r < 0.5)");
		shader.AppendLine("        return pixel;");
		shader.AppendLine("    float own = VoidstrapHomepageWeight(pixel);");
		shader.AppendLine("    if (own <= 0.001)");
		shader.AppendLine("        return pixel;");
		shader.AppendLine("    float2 sampleStep = float2(BUFFER_RCP_WIDTH, BUFFER_RCP_HEIGHT) * 2.0;");
		shader.AppendLine("    float support = 0.0;");
		shader.AppendLine("    [unroll] for (int y = -1; y <= 1; y++)");
		shader.AppendLine("        [unroll] for (int x = -1; x <= 1; x++)");
		shader.AppendLine("            support += VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, texcoord + float2(x, y) * sampleStep).rgb);");
		shader.AppendLine("    float replace = saturate(own * smoothstep(0.30, 0.55, support / 9.0));");
		shader.AppendLine("    float3 background = float3(" + F(firstRed) + ", " + F(firstGreen) + ", " + F(firstBlue) + ");");
		if (mode == "Gradient")
		{
			shader.AppendLine("    float2 direction = float2(" + F(MathF.Cos(angle)) + ", " + F(MathF.Sin(angle)) + ");");
			shader.AppendLine("    float gradientPosition = saturate(dot(texcoord - 0.5, direction) / max(abs(direction.x) + abs(direction.y), 0.0001) + 0.5);");
			shader.AppendLine("    background = lerp(background, float3(" + F(secondRed) + ", " + F(secondGreen) + ", " + F(secondBlue) + "), gradientPosition);");
		}
		else if (media is not null)
		{
			shader.AppendLine("    float sourceAspect = " + F(media.Width / (float)media.Height) + ";");
			shader.AppendLine("    float targetAspect = float(BUFFER_WIDTH) / float(BUFFER_HEIGHT);");
			shader.AppendLine("    float2 coverScale = targetAspect > sourceAspect ? float2(1.0, sourceAspect / targetAspect) : float2(targetAspect / sourceAspect, 1.0);");
			shader.AppendLine("    float2 mediaCoordinate = (texcoord - 0.5) * coverScale + 0.5;");
			shader.AppendLine("    background = tex2D(VoidstrapHomepageMedia, mediaCoordinate).rgb;");
		}
		shader.AppendLine("    float keyBlue = abs(pixel.b - 21.0 / 255.0) <= abs(pixel.b - 24.0 / 255.0) ? 21.0 / 255.0 : 24.0 / 255.0;");
		shader.AppendLine("    float3 corrected = saturate(pixel + background - float3(18.0 / 255.0, 18.0 / 255.0, keyBlue));");
		shader.AppendLine("    return lerp(pixel, corrected, replace);");
		shader.AppendLine("}");
		shader.AppendLine("technique voidstrapHomepage");
		shader.AppendLine("{");
		shader.AppendLine("    pass VoidstrapHomepageLifecycle");
		shader.AppendLine("    {");
		shader.AppendLine("        VertexShader = VoidstrapHomepageVS;");
		shader.AppendLine("        PixelShader = VoidstrapHomepageDetect;");
		shader.AppendLine("        RenderTarget = VoidstrapHomepageStateTex;");
		shader.AppendLine("    }");
		shader.AppendLine("    pass");
		shader.AppendLine("    {");
		shader.AppendLine("        VertexShader = VoidstrapHomepageVS;");
		shader.AppendLine("        PixelShader = VoidstrapHomepagePass;");
		shader.AppendLine("    }");
		shader.AppendLine("}");
		return shader.ToString();
	}

	private sealed record HomepageMedia(string Path, int Width, int Height);

	private static (float Red, float Green, float Blue) ParseColor(string? value, byte fallbackRed, byte fallbackGreen, byte fallbackBlue)
	{
		value ??= string.Empty;
		if (value.Length == 7
			&& value[0] == '#'
			&& int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
			return (((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
		return (fallbackRed / 255f, fallbackGreen / 255f, fallbackBlue / 255f);
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

	private const float NeutralColorLevel = 100f;

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

	public static bool HasLiveColorEffect()
	{
		return HasColorGrade()
			|| Math.Abs(App.Settings.Prop.ColorTemperature) > 0.5
			|| App.Settings.Prop.ColorBlindnessEnabled;
	}

	public static bool HasColorGrade()
	{
		return Math.Abs(App.Settings.Prop.Saturation - NeutralColorLevel) > 0.5
			|| Math.Abs(App.Settings.Prop.Contrast - NeutralColorLevel) > 0.5;
	}

	private static string BuildGradingShader(RiShadeSettings shade, bool includeColorLevels)
	{
		float[]? colorMatrix = includeColorLevels
			? ScreenColorEffect.BuildMatrix(
				App.Settings.Prop.Saturation,
				App.Settings.Prop.Contrast,
				App.Settings.Prop.ColorTemperature,
				App.Settings.Prop.ColorBlindnessEnabled,
				(ScreenColorEffect.ColorBlindnessType)App.Settings.Prop.ColorBlindnessType,
				App.Settings.Prop.ColorBlindnessSeverity / 100.0,
				App.Settings.Prop.ColorBlindnessSimulate)
			: null;

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

		if (colorMatrix is { Length: >= 23 })
		{
			shader.AppendLine("    color = saturate(color);");
			shader.AppendLine("    color = float3(");
			shader.AppendLine("        dot(color, float3(" + F(colorMatrix[0]) + ", " + F(colorMatrix[5]) + ", " + F(colorMatrix[10]) + ")) + " + F(colorMatrix[20]) + ",");
			shader.AppendLine("        dot(color, float3(" + F(colorMatrix[1]) + ", " + F(colorMatrix[6]) + ", " + F(colorMatrix[11]) + ")) + " + F(colorMatrix[21]) + ",");
			shader.AppendLine("        dot(color, float3(" + F(colorMatrix[2]) + ", " + F(colorMatrix[7]) + ", " + F(colorMatrix[12]) + ")) + " + F(colorMatrix[22]) + ");");
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
