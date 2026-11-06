using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Voidstrap.UI;

internal static class WindowSystemMenu
{
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_NCRBUTTONDOWN = 0x00A4;
    private const int WM_NCRBUTTONUP = 0x00A5;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;
    private const int SC_MOUSEMENU = 0xF090;

    public static void Disable(Window window)
    {
        window.PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;
        window.Closed += OnClosed;
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            Attach(window);
        else
            window.SourceInitialized += OnSourceInitialized;
    }

    private static void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.SourceInitialized -= OnSourceInitialized;
        Attach(window);
    }

    private static void Attach(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
    }

    private static void OnClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window)
            return;
        window.PreviewMouseRightButtonUp -= OnPreviewMouseRightButtonUp;
        window.SourceInitialized -= OnSourceInitialized;
        window.Closed -= OnClosed;
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
            HwndSource.FromHwnd(handle)?.RemoveHook(Hook);
    }

    private static void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WM_CONTEXTMENU or WM_NCRBUTTONDOWN or WM_NCRBUTTONUP)
        {
            handled = true;
        }
        else if (msg == WM_SYSCOMMAND)
        {
            int command = (int)(wParam.ToInt64() & 0xFFF0);
            if (command is SC_KEYMENU or SC_MOUSEMENU)
                handled = true;
        }
        return IntPtr.Zero;
    }
}
