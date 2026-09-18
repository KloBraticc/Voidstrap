using Voidstrap.Models.Persistable;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class SoberConfigurationMapper
{
	public static LinuxPlayerPreparationOptions CreatePlayerOptions(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		bool modsAllowed = settings.ModApplyTarget is Voidstrap.Enums.ModApplyTarget.Both or Voidstrap.Enums.ModApplyTarget.Player;
		return new LinuxPlayerPreparationOptions(
			settings.UseFastFlagManager,
			ApplyVirtualMachineProfile(CreateNativeOptions(settings)),
			modsAllowed,
			modsAllowed ? CollectManagedModSources() : null);
	}

	private static List<LinuxModSource> CollectManagedModSources()
	{
		List<LinuxModSource> sources = [];
		try
		{
			ManagedModScanResult scan = ManagedModStore.ScanEnabledFiles();
			foreach (ManagedModFile file in scan.Files)
			{
				string relative = file.Relative.Replace('\\', '/');
				if (relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
					continue;
				sources.Add(new LinuxModSource(relative, file.Source));
			}

			foreach ((string id, string message) in scan.Failures)
				App.Logger.WriteLine("SoberConfigurationMapper::CollectManagedModSources", "Managed mod " + id[..Math.Min(8, id.Length)] + " could not be indexed: " + message);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("SoberConfigurationMapper::CollectManagedModSources", "Managed mods could not be indexed: " + ex.Message);
		}

		return sources;
	}

	private static SoberNativeConfigurationOptions? ApplyVirtualMachineProfile(SoberNativeConfigurationOptions? options)
	{
		if (!Voidstrap.Utility.VirtualMachineProfile.ShouldForceSafeGraphics)
		{
			return options;
		}

		SoberNativeConfigurationOptions current = options ?? new SoberNativeConfigurationOptions();
		return current with
		{
			UseOpenGl = true,
			GraphicsOptimizationMode = SoberGraphicsOptimizationMode.Performance
		};
	}

	public static SoberNativeConfigurationOptions? CreateNativeOptions(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		if (!HasNativeOverrides(settings))
		{
			return null;
		}

		return new SoberNativeConfigurationOptions(
			AllowGamepadPermission: settings.SoberAllowGamepadPermission,
			CloseOnLeave: settings.SoberCloseOnLeave,
			DiscordRpcEnabled: false,
			DiscordRpcShowJoinButton: false,
			EnableGameMode: settings.SoberEnableGameMode,
			EnableHiDpi: settings.SoberEnableHiDpi,
			EnableMobileHomeScreen: settings.SoberEnableMobileHomeScreen,
			GraphicsOptimizationMode: settings.SoberGraphicsOptimizationMode,
			ServerLocationIndicatorEnabled: false,
			TouchMode: settings.SoberTouchMode,
			UseConsoleExperience: settings.SoberUseConsoleExperience,
			UseLibsecret: settings.SoberUseLibsecret,
			UseOpenGl: RequiresNativeHomepageShader(settings) ? false : settings.SoberUseOpenGl);
	}

	public static bool HasNativeOverrides(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		return settings.SoberAllowGamepadPermission is not null
			|| settings.SoberCloseOnLeave is not null
			|| settings.SoberEnableGameMode is not null
			|| settings.SoberEnableHiDpi is not null
			|| settings.SoberEnableMobileHomeScreen is not null
			|| settings.SoberGraphicsOptimizationMode is not null
			|| settings.SoberTouchMode is not null
			|| settings.SoberUseConsoleExperience is not null
			|| settings.SoberUseLibsecret is not null
			|| settings.SoberUseOpenGl is not null
			|| RequiresNativeHomepageShader(settings);
	}

	private static bool RequiresNativeHomepageShader(AppSettings settings)
	{
		if (!settings.HomepageBackgroundOverlayEnabled || VirtualMachineProfile.ShouldForceSafeGraphics)
			return false;
		if (Integrations.Overlays.OverlaySettings.HomepageBackgroundMode != "Media")
			return true;
		string path = settings.HomepageBackgroundOverlayMediaPath ?? string.Empty;
		return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga";
	}
}
