using System;
using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxWindowSize
{
    private const int MaxAttempts = 8;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(350.0);

    public static void Apply(string title, int width, int height)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(title) || width <= 0 || height <= 0)
        {
            return;
        }

        if (TryApply(title, width, height))
        {
            return;
        }

        int attempts = 0;
        DispatcherTimer timer = new() { Interval = RetryInterval };
        void tick(object? sender, EventArgs e)
        {
            attempts++;
            if (!TryApply(title, width, height) && attempts < MaxAttempts)
            {
                return;
            }

            timer.Stop();
            timer.Tick -= tick;
        }

        timer.Tick += tick;
        timer.Start();
    }

    public static bool MoveTopRight(string title, int margin)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        try
        {
            nint handle = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(title);
            if (handle == 0
                || !Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out int width, out int height)
                || width <= 0
                || height <= 0)
            {
                return false;
            }

            if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWorkArea(out int areaLeft, out int areaTop, out int areaWidth, out int areaHeight)
                || areaWidth <= 0
                || areaHeight <= 0)
            {
                return false;
            }

            int x = areaLeft + Math.Max(0, areaWidth - width - margin);
            int y = areaTop + margin;
            return Voidstrap.Platform.Linux.LinuxWindowInterop.TryMoveResize(handle, x, y, width, height);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LinuxWindowSize::MoveTopRight", "The window could not be positioned: " + ex.Message);
            return false;
        }
    }

    public static bool TryGet(string title, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        try
        {
            nint handle = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(title);
            return handle != 0
                && Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out width, out height);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LinuxWindowSize::TryGet", "The window size could not be read: " + ex.Message);
            return false;
        }
    }

    private static bool TryApply(string title, int width, int height)
    {
        try
        {
            nint handle = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(title);
            if (handle == 0)
            {
                return false;
            }

            if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out int x, out int y, out int currentWidth, out int currentHeight))
            {
                return false;
            }

            if (currentWidth == width && currentHeight == height)
            {
                return true;
            }

            if (Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWorkArea(out int areaLeft, out int areaTop, out int areaWidth, out int areaHeight)
                && areaWidth > 0 && areaHeight > 0)
            {
                x = areaLeft + Math.Max(0, (areaWidth - width) / 2);
                y = areaTop + Math.Max(0, (areaHeight - height) / 2);
            }

            if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryMoveResize(handle, x, y, width, height))
            {
                return false;
            }

            return Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out int appliedWidth, out int appliedHeight)
                && appliedWidth == width
                && appliedHeight == height;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LinuxWindowSize::TryApply", "The window size could not be applied: " + ex.Message);
            return true;
        }
    }
}
