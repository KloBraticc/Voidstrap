using System;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.Integrations.MotionBlur
{
    public static class MotionBlurManager
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;
            App.Logger.WriteLine("MotionBlur", "Installed, strength is " + MotionBlurSettings.StrengthNames[MotionBlurSettings.StrengthIndex]);
            OverlayHub.Refresh();
        }

        public static void SetStrength(int strengthIndex)
        {
            Install();
            App.Settings.Prop.MotionBlurStrengthIndex = Math.Clamp(strengthIndex, 0, MotionBlurSettings.StrengthNames.Length - 1);
            App.Settings.SaveDeferred();
            App.Logger.WriteLine("MotionBlur", "Strength set to " + MotionBlurSettings.StrengthNames[MotionBlurSettings.StrengthIndex]);
            OverlayHub.Refresh();
        }

        public static void OnGameJoin()
        {
            OverlayHub.OnGameJoin();
        }

        public static void OnGameLeave()
        {
            OverlayHub.OnGameLeave();
        }

        public static void Shutdown()
        {
            _installed = false;
            OverlayHub.Shutdown();
        }
    }
}
