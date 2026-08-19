using System;
using System.Windows;
using System.Windows.Interop;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations.Overlays
{
    internal static class LinuxOverlaySurface
    {
        public static bool IsSupported => Voidstrap.Utility.Platform.IsLinux && LinuxWindowInterop.IsAvailable;

        public static void MakeClickThrough(Window window)
        {
            if (window == null || !IsSupported)
                return;

            try
            {
                nint handle = ResolveHandle(window);
                if (handle == 0)
                {
                    App.Logger?.WriteLine("LinuxOverlaySurface", "The overlay window handle could not be resolved, input passthrough is off");
                    return;
                }

                if (!LinuxWindowInterop.TrySetClickThrough(handle))
                    App.Logger?.WriteLine("LinuxOverlaySurface", "The X shape extension is unavailable, input passthrough is off");
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("LinuxOverlaySurface", "Input passthrough could not be applied: " + ex.Message);
            }
        }

        private static nint ResolveHandle(Window window)
        {
            try
            {
                nint handle = new WindowInteropHelper(window).Handle;
                if (handle != 0 && LinuxWindowInterop.IsLiveWindow(handle))
                    return handle;
            }
            catch (Exception)
            {
            }

            return string.IsNullOrWhiteSpace(window.Title) ? 0 : LinuxWindowInterop.FindOwnWindowByTitle(window.Title);
        }
    }
}
