using System;
using System.Globalization;
using System.Text;
using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.FrameGeneration;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class LinuxEffectMapper
{
	public const int SupportedAntiAliasingMethods = 5;

	static LinuxEffectMapper()
	{
		if (Platform.IsLinux)
			LinuxSoberRuntimeProvider.EffectLayersDisabled += OnEffectLayersDisabled;
	}

	private static void OnEffectLayersDisabled()
	{
		Voidstrap.Integrations.Overlays.OverlayHub.SetLinuxHomepageNativeShaderActive(false);
	}

	public static LinuxEffectOptions CreateOptions()
	{
		int soberSharpness = Math.Clamp(App.Settings.Prop.SoberSharpness, 0, 100);
		bool soberSharpen = soberSharpness > 0;
		float sharpnessAmount = soberSharpen ? soberSharpness / 100f : 0.4f;

		LinuxHomepageShader.HomepageMedia? homepageMedia = LinuxHomepageShader.ResolveMedia();
		string? homepageShader = LinuxHomepageShader.Build(homepageMedia);

		return new LinuxEffectOptions(
			Enabled: soberSharpen || MapAntiAliasing() is not null || MapFrameGenMultiplier() > 1 || homepageShader is not null,
			AntiAliasing: MapAntiAliasing(),
			AntiAliasingUltra: AntiAliasingSettings.MethodIndex is 2 or 4,
			Sharpening: soberSharpen,
			SharpnessAmount: sharpnessAmount,
			GradingShader: null,
			HomepageShader: homepageShader,
			HomepageMediaPath: homepageMedia?.Path,
			FrameGenMultiplier: MapFrameGenMultiplier());
	}

	public static string? MapAntiAliasing()
	{
		return AntiAliasingSettings.MethodIndex switch
		{
			1 or 2 => "fxaa",
			3 or 4 => "smaa",
			_ => LinuxNvidiaSettings.Read().Fxaa ? "fxaa" : null
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
}
