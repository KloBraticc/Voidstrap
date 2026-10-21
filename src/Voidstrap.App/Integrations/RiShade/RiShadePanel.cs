using System;
using System.Collections.Generic;

namespace Voidstrap.Integrations.RiShade
{
    public static class RiShadePanel
    {
        private static bool _isOpen;
        private static long _lastToggleTick;

        public static IntPtr CurrentHwnd => IntPtr.Zero;

        public static bool IsOpen => RiShadeManager.IsOverlayOpen;

        public static string? CurrentPresetPath => RiShadeManager.CurrentPresetPath;

        public static IReadOnlyList<string> AvailablePresets => RiShadeManager.GetAvailablePresets();

        public static bool SelectPreset(string presetPath) => RiShadeManager.SelectPreset(presetPath);

        public static event Action<bool>? OpenChanged;

        public static void Toggle(bool fromUi = false)
        {
            long now = Environment.TickCount64;
            if (!fromUi && now - _lastToggleTick < 250)
                return;
            _lastToggleTick = now;

            if (!App.Settings.Prop.RiShadeEnabled)
            {
                App.Logger.WriteLine("RiShadePanel", "Overlay toggle ignored because RiShade is turned off");
                return;
            }

            bool open = RiShadeManager.RequestOverlayToggle();
            App.Logger.WriteLine("RiShadePanel", RiShadeManager.HasRuntime
                ? (open ? "ReShade overlay open requested" : "ReShade overlay close requested")
                : "ReShade overlay requested, it opens as soon as Roblox is in the foreground and the ReShade runtime is ready");
        }

        internal static void Close()
        {
            RiShadeManager.RequestOverlayClose();
            NotifyOpenChanged(false);
        }

        internal static void NotifyOpenChanged(bool open)
        {
            if (_isOpen == open)
                return;
            _isOpen = open;
            OpenChanged?.Invoke(open);
        }
    }
}
