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

            bool open = !IsOpen;
            if (!RiShadeManager.ToggleOverlayMenu(open))
            {
                App.Logger.WriteLine("RiShadePanel", "Overlay toggle was requested, but no active ReShade runtime accepted it");
                return;
            }

            _isOpen = RiShadeManager.IsOverlayOpen;
            App.Logger.WriteLine("RiShadePanel", _isOpen ? "ReShade overlay opened" : "ReShade overlay closed");
            OpenChanged?.Invoke(_isOpen);
        }

        internal static void Close()
        {
            if (!IsOpen && !_isOpen)
                return;
            RiShadeManager.ToggleOverlayMenu(false);
            RuntimeUnavailable();
        }

        internal static void RuntimeUnavailable()
        {
            if (!_isOpen)
                return;
            _isOpen = false;
            OpenChanged?.Invoke(false);
        }
    }
}
