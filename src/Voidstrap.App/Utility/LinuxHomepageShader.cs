using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class LinuxHomepageShader
{
	private const int MinimumMediaWidth = 540;
	private const int MinimumMediaHeight = 405;
	private const int MaximumMediaEdge = 8192;
	private const long MaximumMediaBytes = 64L * 1024 * 1024;
	private const long MaximumDecodedBytes = 128L * 1024 * 1024;
	private static readonly object MediaResolutionGate = new();
	private static string? _cachedMediaPath;
	private static long _cachedMediaLength = -1;
	private static long _cachedMediaWriteTicks = -1;
	private static bool _cachedMediaResolutionValid;

	internal sealed record HomepageMedia(string Path, int Width, int Height);

	public static HomepageMedia? ResolveMedia()
	{
		if (
			!Platform.IsLinux
			|| VirtualMachineProfile.ShouldForceSafeGraphics
			|| !App.Settings.Prop.HomepageBackgroundOverlayEnabled
			|| OverlaySettings.HomepageBackgroundMode != "Media"
		)
			return null;
		string path = App.Settings.Prop.HomepageBackgroundOverlayMediaPath ?? string.Empty;
		if (
			Path.GetExtension(path).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".webp")
			|| !IsHighResolutionImage(path)
		)
			return null;
		try
		{
			FileInfo sourceFile = new(path);
			(double displayWidth, double displayHeight) = ScreenMetrics.GetPrimary();
			int targetWidth = Math.Clamp((int)Math.Round(displayWidth), 320, 7680);
			int targetHeight = Math.Clamp((int)Math.Round(displayHeight), 240, 4320);
			string identity =
				Path.GetFullPath(path)
				+ "|"
				+ sourceFile.Length.ToString(CultureInfo.InvariantCulture)
				+ "|"
				+ sourceFile.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
				+ "|"
				+ targetWidth.ToString(CultureInfo.InvariantCulture)
				+ "x"
				+ targetHeight.ToString(CultureInfo.InvariantCulture);
			string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
			Directory.CreateDirectory(LinuxEffectLayers.ConfigDirectory);
			string preparedPath = Path.Combine(LinuxEffectLayers.ConfigDirectory, "VoidstrapHomepagePrepared" + fingerprint + ".png");
			if (!File.Exists(preparedPath))
			{
				Raster image = Raster.Decode(File.ReadAllBytes(path), MaximumDecodedBytes);
				double targetAspect = targetWidth / (double)targetHeight;
				int cropWidth = Math.Clamp((int)Math.Round(image.Height * targetAspect), 1, image.Width);
				int cropHeight = Math.Clamp((int)Math.Round(image.Width / targetAspect), 1, image.Height);
				image = image.Crop((image.Width - cropWidth) / 2, (image.Height - cropHeight) / 2, cropWidth, cropHeight);
				image.Resize(targetWidth, targetHeight, RasterFilter.Lanczos3).SavePng(preparedPath);
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

	public static string? Build(HomepageMedia? media)
	{
		if (!Platform.IsLinux || !App.Settings.Prop.HomepageBackgroundOverlayEnabled || VirtualMachineProfile.ShouldForceSafeGraphics)
			return null;
		string mode = OverlaySettings.HomepageBackgroundMode;
		if (mode == "Media" && media is null)
			return null;
		(float firstRed, float firstGreen, float firstBlue) = ParseColor(App.Settings.Prop.HomepageBackgroundOverlayColor, 18, 18, 21);
		(float secondRed, float secondGreen, float secondBlue) = ParseColor(
			App.Settings.Prop.HomepageBackgroundOverlayGradientColor,
			91,
			46,
			255
		);
		float angle = (float)(App.Settings.Prop.HomepageBackgroundOverlayGradientAngle * Math.PI / 180d);
		StringBuilder shader = new();
		shader.AppendLine("texture VoidstrapHomepageBackBufferTex : COLOR;");
		shader.AppendLine(
			"sampler VoidstrapHomepageBackBuffer { Texture = VoidstrapHomepageBackBufferTex; MinFilter = POINT; MagFilter = POINT; AddressU = CLAMP; AddressV = CLAMP; };"
		);
		shader.AppendLine("texture VoidstrapHomepageStateTex");
		shader.AppendLine("{");
		shader.AppendLine("    Width = 1;");
		shader.AppendLine("    Height = 1;");
		shader.AppendLine("    Format = R16F;");
		shader.AppendLine("};");
		shader.AppendLine(
			"sampler VoidstrapHomepageState { Texture = VoidstrapHomepageStateTex; MinFilter = POINT; MagFilter = POINT; AddressU = CLAMP; AddressV = CLAMP; };"
		);
		shader.AppendLine("texture VoidstrapHomepageHistoryTex");
		shader.AppendLine("{");
		shader.AppendLine("    Width = 1;");
		shader.AppendLine("    Height = 1;");
		shader.AppendLine("    Format = R16F;");
		shader.AppendLine("};");
		shader.AppendLine(
			"sampler VoidstrapHomepageHistory { Texture = VoidstrapHomepageHistoryTex; MinFilter = POINT; MagFilter = POINT; AddressU = CLAMP; AddressV = CLAMP; };"
		);
		if (media is not null)
		{
			string mediaName = Path.GetFileName(LinuxEffectLayers.HomepageMediaFile(media.Path))
				.Replace("\\", "\\\\", StringComparison.Ordinal)
				.Replace("\"", "\\\"", StringComparison.Ordinal);
			shader.AppendLine("texture VoidstrapHomepageMediaTex < source = \"" + mediaName + "\"; >");
			shader.AppendLine("{");
			shader.AppendLine("    Width = " + media.Width.ToString(CultureInfo.InvariantCulture) + ";");
			shader.AppendLine("    Height = " + media.Height.ToString(CultureInfo.InvariantCulture) + ";");
			shader.AppendLine("    Format = RGBA8;");
			shader.AppendLine("    MipLevels = 1;");
			shader.AppendLine("};");
			shader.AppendLine(
				"sampler VoidstrapHomepageMedia { Texture = VoidstrapHomepageMediaTex; MinFilter = LINEAR; MagFilter = LINEAR; MipFilter = LINEAR; AddressU = CLAMP; AddressV = CLAMP; };"
			);
		}
		shader.AppendLine(
			"void VoidstrapHomepageVS(in uint id : SV_VertexID, out float4 position : SV_Position, out float2 texcoord : TEXCOORD)"
		);
		shader.AppendLine("{");
		shader.AppendLine("    texcoord.x = (id == 2) ? 2.0 : 0.0;");
		shader.AppendLine("    texcoord.y = (id == 1) ? 2.0 : 0.0;");
		shader.AppendLine("    position = float4(texcoord * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);");
		shader.AppendLine("}");
		shader.AppendLine("float VoidstrapHomepageWeight(float3 pixel)");
		shader.AppendLine("{");
		shader.AppendLine(
			"    float originalDistance = max(abs(pixel.r - 18.0 / 255.0), max(abs(pixel.g - 18.0 / 255.0), abs(pixel.b - 21.0 / 255.0)));"
		);
		shader.AppendLine(
			"    float soberDistance = max(abs(pixel.r - 18.0 / 255.0), max(abs(pixel.g - 18.0 / 255.0), abs(pixel.b - 24.0 / 255.0)));"
		);
		shader.AppendLine("    return 1.0 - smoothstep(1.0 / 255.0, 10.0 / 255.0, min(originalDistance, soberDistance));");
		shader.AppendLine("}");
		shader.AppendLine("float VoidstrapHomepageDetect(float4 pos : SV_Position, float2 texcoord : TEXCOORD) : SV_Target");
		shader.AppendLine("{");
		shader.AppendLine("    float matches = 0.0;");
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.03, 0.04)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.18, 0.10)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.39, 0.20)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.62, 0.18)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.82, 0.12)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.97, 0.04)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.05, 0.48)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.95, 0.48)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.04, 0.94)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.24, 0.89)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.76, 0.89)).rgb));"
		);
		shader.AppendLine(
			"    matches += step(0.90, VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, float2(0.96, 0.94)).rgb));"
		);
		shader.AppendLine("    float previous = tex2D(VoidstrapHomepageHistory, float2(0.5, 0.5)).r;");
		shader.AppendLine("    if (matches >= 3.5)");
		shader.AppendLine("        return 1.0;");
		shader.AppendLine("    if (matches >= 1.5 && previous > 0.0)");
		shader.AppendLine("        return max(previous - 1.0 / 90.0, 0.0);");
		shader.AppendLine("    return 0.0;");
		shader.AppendLine("}");
		shader.AppendLine("float VoidstrapHomepageRemember(float4 pos : SV_Position, float2 texcoord : TEXCOORD) : SV_Target");
		shader.AppendLine("{");
		shader.AppendLine("    return tex2D(VoidstrapHomepageState, float2(0.5, 0.5)).r;");
		shader.AppendLine("}");
		shader.AppendLine("float3 VoidstrapHomepagePass(float4 pos : SV_Position, float2 texcoord : TEXCOORD) : SV_Target");
		shader.AppendLine("{");
		shader.AppendLine("    float3 pixel = tex2D(VoidstrapHomepageBackBuffer, texcoord).rgb;");
		shader.AppendLine("    if (tex2D(VoidstrapHomepageState, float2(0.5, 0.5)).r <= 0.0)");
		shader.AppendLine("        return pixel;");
		shader.AppendLine("    float own = VoidstrapHomepageWeight(pixel);");
		shader.AppendLine("    if (own <= 0.001)");
		shader.AppendLine("        return pixel;");
		shader.AppendLine("    float2 sampleStep = float2(BUFFER_RCP_WIDTH, BUFFER_RCP_HEIGHT) * 2.0;");
		shader.AppendLine("    float support = 0.0;");
		shader.AppendLine("    [unroll] for (int y = -1; y <= 1; y++)");
		shader.AppendLine("        [unroll] for (int x = -1; x <= 1; x++)");
		shader.AppendLine(
			"            support += VoidstrapHomepageWeight(tex2D(VoidstrapHomepageBackBuffer, texcoord + float2(x, y) * sampleStep).rgb);"
		);
		shader.AppendLine("    float replace = saturate(own * smoothstep(0.30, 0.55, support / 9.0));");
		shader.AppendLine("    float3 background = float3(" + F(firstRed) + ", " + F(firstGreen) + ", " + F(firstBlue) + ");");
		if (mode == "Gradient")
		{
			shader.AppendLine("    float2 direction = float2(" + F(MathF.Cos(angle)) + ", " + F(MathF.Sin(angle)) + ");");
			shader.AppendLine(
				"    float gradientPosition = saturate(dot(texcoord - 0.5, direction) / max(abs(direction.x) + abs(direction.y), 0.0001) + 0.5);"
			);
			shader.AppendLine(
				"    background = lerp(background, float3("
					+ F(secondRed)
					+ ", "
					+ F(secondGreen)
					+ ", "
					+ F(secondBlue)
					+ "), gradientPosition);"
			);
		}
		else if (media is not null)
		{
			shader.AppendLine("    float sourceAspect = " + F(media.Width / (float)media.Height) + ";");
			shader.AppendLine("    float targetAspect = float(BUFFER_WIDTH) / float(BUFFER_HEIGHT);");
			shader.AppendLine(
				"    float2 coverScale = targetAspect > sourceAspect ? float2(1.0, sourceAspect / targetAspect) : float2(targetAspect / sourceAspect, 1.0);"
			);
			shader.AppendLine("    float2 mediaCoordinate = (texcoord - 0.5) * coverScale + 0.5;");
			shader.AppendLine("    float4 mediaPixel = tex2D(VoidstrapHomepageMedia, mediaCoordinate);");
			shader.AppendLine("    background = lerp(background, mediaPixel.rgb, mediaPixel.a);");
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
		shader.AppendLine("    pass VoidstrapHomepageMemory");
		shader.AppendLine("    {");
		shader.AppendLine("        VertexShader = VoidstrapHomepageVS;");
		shader.AppendLine("        PixelShader = VoidstrapHomepageRemember;");
		shader.AppendLine("        RenderTarget = VoidstrapHomepageHistoryTex;");
		shader.AppendLine("    }");
		shader.AppendLine("    pass");
		shader.AppendLine("    {");
		shader.AppendLine("        VertexShader = VoidstrapHomepageVS;");
		shader.AppendLine("        PixelShader = VoidstrapHomepagePass;");
		shader.AppendLine("    }");
		shader.AppendLine("}");
		return shader.ToString();
	}

	public static bool IsHighResolutionImage(string? path)
	{
		if (!Platform.IsLinux)
			return true;
		if (string.IsNullOrWhiteSpace(path))
			return false;
		try
		{
			FileInfo file = new(path);
			if (!file.Exists || file.Length <= 0 || file.Length > MaximumMediaBytes)
				return false;
			string fullPath = file.FullName;
			long writeTicks = file.LastWriteTimeUtc.Ticks;
			lock (MediaResolutionGate)
			{
				if (
					string.Equals(_cachedMediaPath, fullPath, StringComparison.Ordinal)
					&& _cachedMediaLength == file.Length
					&& _cachedMediaWriteTicks == writeTicks
				)
					return _cachedMediaResolutionValid;
			}
			byte[] encoded = File.ReadAllBytes(fullPath);
			bool valid =
				Raster.TryIdentify(encoded, out int width, out int height)
				&& width >= MinimumMediaWidth
				&& height >= MinimumMediaHeight
				&& width <= MaximumMediaEdge
				&& height <= MaximumMediaEdge
				&& (long)width * height * 4 <= MaximumDecodedBytes;
			lock (MediaResolutionGate)
			{
				_cachedMediaPath = fullPath;
				_cachedMediaLength = file.Length;
				_cachedMediaWriteTicks = writeTicks;
				_cachedMediaResolutionValid = valid;
			}
			return valid;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static (float Red, float Green, float Blue) ParseColor(string? value, byte fallbackRed, byte fallbackGreen, byte fallbackBlue)
	{
		value ??= string.Empty;
		if (
			value.Length == 7
			&& value[0] == '#'
			&& int.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)
		)
			return (((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
		return (fallbackRed / 255f, fallbackGreen / 255f, fallbackBlue / 255f);
	}

	private static string F(float value)
	{
		return value.ToString("0.0000", CultureInfo.InvariantCulture);
	}
}
