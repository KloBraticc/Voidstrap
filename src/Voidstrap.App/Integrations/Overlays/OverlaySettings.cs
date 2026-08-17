using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.FrameGeneration;
using Voidstrap.Integrations.RiShade;

namespace Voidstrap.Integrations.Overlays
{
    public static class OverlaySettings
    {
        public static bool GameEffectsEnabled =>
            App.Settings.Prop.RiShadeEnabled
            || AntiAliasingSettings.MethodIndex > 0
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
    }
}
