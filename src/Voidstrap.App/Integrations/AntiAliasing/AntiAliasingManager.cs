using System;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.Integrations.AntiAliasing
{
    public static class AntiAliasingManager
    {
        private static bool _installed;

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;
            App.Logger.WriteLine("AntiAliasing", "Installed, method is " + AntiAliasingSettings.MethodNames[AntiAliasingSettings.MethodIndex]);
            OverlayHub.Refresh();
        }

        public static void SetMethod(int methodIndex)
        {
            Install();
            App.Settings.Prop.AntiAliasingMethodIndex = Math.Clamp(methodIndex, 0, AntiAliasingSettings.MethodNames.Length - 1);
            App.Settings.SaveDeferred();
            App.Logger.WriteLine("AntiAliasing", "Method set to " + AntiAliasingSettings.MethodNames[AntiAliasingSettings.MethodIndex]);
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
