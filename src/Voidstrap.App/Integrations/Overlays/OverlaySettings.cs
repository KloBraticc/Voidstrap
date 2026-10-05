using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.FrameGeneration;
using Voidstrap.Integrations.MotionBlur;
using Voidstrap.Integrations.RiShade;

namespace Voidstrap.Integrations.Overlays
{
    public static class OverlaySettings
    {
        public static bool GameEffectsEnabled =>
            App.Settings.Prop.RiShadeEnabled
            || AntiAliasingSettings.MethodIndex > 0
            || MotionBlurSettings.StrengthIndex > 0
            || FrameGenSettings.ModeIndex > 0;

        public static bool HomepageBackgroundEnabled => App.Settings.Prop.HomepageBackgroundOverlayEnabled;

		public static string HomepageBackgroundMode
		{
			get
			{
				string mode = App.Settings.Prop.HomepageBackgroundOverlayMode ?? "Auto";
				if (mode is "Solid" or "Gradient" or "Media")
					return mode;
				if (!string.IsNullOrWhiteSpace(App.Settings.Prop.HomepageBackgroundOverlayMediaPath))
					return "Media";
				return App.Settings.Prop.HomepageBackgroundOverlayGradientEnabled ? "Gradient" : "Solid";
			}
		}

        public static bool AnyEnabled => OverlayHub.InGame ? GameEffectsEnabled : HomepageBackgroundEnabled;

		public static bool RequiresLinuxX11Session => Voidstrap.Utility.Platform.IsLinux
			&& (App.Settings.Prop.FakeExclusiveFullscreen
				|| App.Settings.Prop.FakeBorderlessFullscreen
				|| App.Settings.Prop.SoberAutoFullscreen
				|| App.Settings.Prop.SnapTapEnabled
				|| App.Settings.Prop.DuckRobloxAudioOnUnfocus
				|| HomepageBackgroundEnabled
				|| App.Settings.Prop.SoberPreferXWayland);

		public static bool LinuxSessionIsWayland => Voidstrap.Utility.Platform.IsLinux
			&& (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))
				|| string.Equals(System.Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", System.StringComparison.OrdinalIgnoreCase));

		public static bool SoberHasX11Window => Voidstrap.Utility.Platform.IsLinux
			&& (!LinuxSessionIsWayland
				|| (RequiresLinuxX11Session && !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("DISPLAY"))));

		public static string DescribeLinuxSoberSession(bool forcedX11)
		{
			if (forcedX11)
				return App.Settings.Prop.SoberPreferXWayland
					? "Run Roblox through XWayland is on, starting Sober on X11 so custom cursors and the Roblox window title and icon work"
					: "Snap Tap, audio ducking, fullscreen helpers or the homepage background are on, starting Sober on X11 so Voidstrap can control its window";
			if (LinuxSessionIsWayland && (LinuxRobloxWindow.IsEnabled || LinuxCustomCursorNeedsX11))
				return "Starting Sober natively on Wayland so the camera keeps the mouse locked, the custom cursor and Roblox window title need Run Roblox through XWayland";
			return "Starting Sober in its default display mode";
		}

		public static bool LinuxCustomCursorNeedsX11 => Voidstrap.Utility.Platform.IsLinux
			&& App.Settings.Prop.CursorType != Voidstrap.Enums.CursorType.Default
			&& App.Settings.Prop.ModApplyTarget is Voidstrap.Enums.ModApplyTarget.Both or Voidstrap.Enums.ModApplyTarget.Player;
    }
}
