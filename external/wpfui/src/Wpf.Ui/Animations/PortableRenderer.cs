using System;

namespace Wpf.Ui.Animations
{
    public static class PortableRenderer
    {
        public static bool IsActive { get; } = OperatingSystem.IsLinux()
            || OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("VOIDSTRAP_MAC_PORTABLE_MOTION") != "0";
    }
}
