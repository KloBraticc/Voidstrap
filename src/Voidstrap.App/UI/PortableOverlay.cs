using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Platform.Linux;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.UI;

internal static class PortableOverlay
{
    private sealed class State
    {
        public nint Handle;
    }

    private static readonly ConditionalWeakTable<Window, State> States = new();

    public static bool Active => Voidstrap.Utility.Platform.IsLinux || Voidstrap.Utility.Platform.IsMacOS;

    public static DpiScale Scale(Visual visual) => Active ? new DpiScale(1, 1) : VisualTreeHelper.GetDpi(visual);

    public static void Prepare(Window window)
    {
        if (!Active)
            return;
        LinuxOverlaySurface.ReleaseMainWindowClaim(window);
        LinuxTextGuard.SetPreserveCompactLayout(window, true);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Topmost = true;
    }

    public static nint Handle(Window window)
    {
        nint handle = Voidstrap.Utility.Platform.IsLinux ? LinuxWindowMode.ResolveExactNativeWindow(window)
            : Voidstrap.Utility.Platform.IsMacOS ? MacWindowMode.ResolveNativeWindow(window) : 0;
        State state = States.GetOrCreateValue(window);
        if (handle != state.Handle)
        {
            OverlayDiagnostics.UnregisterOverlayHandle(state.Handle);
            state.Handle = handle;
            if (handle != 0)
            {
                OverlayDiagnostics.RegisterOverlayHandle(handle);
                if (Voidstrap.Utility.Platform.IsLinux)
                {
                    LinuxWindowInterop.TryPrepareOverlayWindow(handle);
                    LinuxWindowInterop.TryResetInputShape(handle);
                }
                else
                    MacOSOverlayWindow.Configure(handle, false);
            }
        }
        return handle;
    }

    public static void Place(Window window, double left, double top, double width, double height, bool wake = true, bool raise = true)
    {
        window.Left = left;
        window.Top = top;
        window.Width = Math.Max(1, width);
        window.Height = Math.Max(1, height);
        if (!window.IsVisible)
            window.Show();
        nint handle = Handle(window);
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            LinuxWindowInterop.TryMoveResize(handle, (int)Math.Round(left), (int)Math.Round(top),
                Math.Max(1, (int)Math.Ceiling(width)), Math.Max(1, (int)Math.Ceiling(height)));
            if (raise)
                LinuxWindowInterop.TryRaiseWindow(handle);
        }
        else if (Voidstrap.Utility.Platform.IsMacOS)
        {
            MacOSOverlayWindow.MoveTo(handle, left, top, width, height);
            if (raise)
                MacOSOverlayWindow.ShowWithoutActivating(handle);
        }
        if (wake)
            LinuxOverlaySurface.WakePresentation(window);
    }

    public static void Focus(Window window)
    {
        nint handle = Handle(window);
        window.Activate();
        Raise(window);
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            LinuxWindowInterop.TrySetAcceptsKeyboard(handle, true);
            LinuxWindowInterop.TryFocusWindow(handle);
        }
        else if (Voidstrap.Utility.Platform.IsMacOS)
            MacOSOverlayWindow.Focus(handle);
        if (!window.IsKeyboardFocusWithin)
            System.Windows.Input.Keyboard.Focus(window);
    }

    public static void ReturnFocus()
    {
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            LinuxWindowInterop.TryActivateWindow(RobloxWindowTracker.Current.Hwnd);
            LinuxWindowInterop.TryFocusWindow(RobloxWindowTracker.Current.Hwnd);
        }
        else if (Voidstrap.Utility.Platform.IsMacOS)
            MacOSOverlayWindow.ActivateProcess(RobloxWindowTracker.MacProcessId);
    }

    public static bool IsForeground()
    {
        if (!Voidstrap.Utility.Platform.IsMacOS || MacOSOverlayWindow.FrontmostWindowOwner() != Environment.ProcessId)
            return false;
        foreach (var pair in States)
        {
            if (pair.Key.IsVisible && MacOSOverlayWindow.IsKeyWindow(pair.Value.Handle))
                return true;
        }
        return false;
    }

    public static void Raise(Window window)
    {
        nint handle = Handle(window);
        if (Voidstrap.Utility.Platform.IsLinux)
            LinuxWindowInterop.TryRaiseWindow(handle);
        else if (Voidstrap.Utility.Platform.IsMacOS)
            MacOSOverlayWindow.ShowWithoutActivating(handle);
    }

    public static void Release(Window window)
    {
        if (!States.TryGetValue(window, out State? state))
            return;
        OverlayDiagnostics.UnregisterOverlayHandle(state.Handle);
        if (Voidstrap.Utility.Platform.IsLinux)
            LinuxWindowInterop.ForgetPreparedOverlayWindow(state.Handle);
        States.Remove(window);
    }
}
