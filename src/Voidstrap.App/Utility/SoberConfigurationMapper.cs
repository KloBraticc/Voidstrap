using Voidstrap.Models.Persistable;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Utility;

internal static class SoberConfigurationMapper
{
	public static LinuxPlayerPreparationOptions CreatePlayerOptions(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		return new LinuxPlayerPreparationOptions(settings.UseFastFlagManager, CreateNativeOptions(settings));
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
			DiscordRpcEnabled: settings.SoberDiscordRpcEnabled,
			DiscordRpcShowJoinButton: settings.SoberDiscordRpcShowJoinButton,
			EnableGameMode: settings.SoberEnableGameMode,
			EnableHiDpi: settings.SoberEnableHiDpi,
			EnableMobileHomeScreen: settings.SoberEnableMobileHomeScreen,
			GraphicsOptimizationMode: settings.SoberGraphicsOptimizationMode,
			ServerLocationIndicatorEnabled: settings.SoberServerLocationIndicatorEnabled,
			TouchMode: settings.SoberTouchMode,
			UseConsoleExperience: settings.SoberUseConsoleExperience,
			UseLibsecret: settings.SoberUseLibsecret,
			UseOpenGl: settings.SoberUseOpenGl);
	}

	public static bool HasNativeOverrides(AppSettings settings)
	{
		ArgumentNullException.ThrowIfNull(settings);
		return settings.SoberAllowGamepadPermission is not null
			|| settings.SoberCloseOnLeave is not null
			|| settings.SoberDiscordRpcEnabled is not null
			|| settings.SoberDiscordRpcShowJoinButton is not null
			|| settings.SoberEnableGameMode is not null
			|| settings.SoberEnableHiDpi is not null
			|| settings.SoberEnableMobileHomeScreen is not null
			|| settings.SoberGraphicsOptimizationMode is not null
			|| settings.SoberServerLocationIndicatorEnabled is not null
			|| settings.SoberTouchMode is not null
			|| settings.SoberUseConsoleExperience is not null
			|| settings.SoberUseLibsecret is not null
			|| settings.SoberUseOpenGl is not null;
	}
}
