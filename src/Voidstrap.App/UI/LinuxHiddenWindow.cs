using System;
using System.Windows;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxHiddenWindow
{
    private const int MaxAttempts = 20;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500.0);

    public static void Hide(string title)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(title) || TryHide(title))
        {
            return;
        }

        int attempts = 0;
        DispatcherTimer timer = new() { Interval = RetryInterval };
        void tick(object? sender, EventArgs e)
        {
            attempts++;
            if (!TryHide(title) && attempts < MaxAttempts)
            {
                return;
            }

            timer.Stop();
            timer.Tick -= tick;
        }

        timer.Tick += tick;
        timer.Start();
    }

    private static bool TryHide(string title)
    {
        try
        {
            nint handle = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(title);

            if (handle == 0)
                return false;

            Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetHiddenFromShell(handle);
            return Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetInvisible(handle);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("LinuxHiddenWindow::TryHide", "The window could not be hidden: " + ex.Message);
            return true;
        }
    }
}
