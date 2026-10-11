using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
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
        public (int Left, int Top, int Width, int Height)? Bounds;
    }

    private static readonly ConditionalWeakTable<Window, State> States = new();

    public static bool Active => Voidstrap.Utility.Platform.IsLinux || Voidstrap.Utility.Platform.IsMacOS;

    public static DpiScale Scale(Visual visual) => Active ? new DpiScale(1, 1) : VisualTreeHelper.GetDpi(visual);

    public static void Prepare(Window window)
    {
        if (!Active)
            return;
        LinuxOverlaySurface.ReleaseMainWindowClaim(window);
        LinuxWindowUpdatePump.Attach(window);
        LinuxOverlayToolTips.Attach(window);
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
            state.Bounds = null;
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
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        bool shown = !window.IsVisible;
        bool resized = window.Width != width || window.Height != height;
        if (window.Left != left)
            window.Left = left;
        if (window.Top != top)
            window.Top = top;
        if (window.Width != width)
            window.Width = width;
        if (window.Height != height)
            window.Height = height;
        if (shown)
            window.Show();
        nint handle = Handle(window);
        State state = States.GetOrCreateValue(window);
        var bounds = ((int)Math.Round(left), (int)Math.Round(top), (int)Math.Ceiling(width), (int)Math.Ceiling(height));
        bool moved = shown || state.Bounds != bounds;
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            if (moved && LinuxWindowInterop.TryMoveResize(handle, bounds.Item1, bounds.Item2, bounds.Item3, bounds.Item4))
                state.Bounds = bounds;
            if (raise && moved)
                LinuxWindowInterop.TryShowOverlayWindow(handle);
        }
        else if (Voidstrap.Utility.Platform.IsMacOS)
        {
            if (moved)
            {
                MacOSOverlayWindow.MoveTo(handle, left, top, width, height);
                state.Bounds = bounds;
            }
            if (raise && moved)
                MacOSOverlayWindow.ShowWithoutActivating(handle);
        }
        if (wake && (shown || resized))
            LinuxOverlaySurface.WakePresentation(window);
    }

    public static Task RunAsync(Window window, Func<Task> action)
    {
        if (!Active)
            return action();
        if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
            return Task.CompletedTask;
        if (!window.Dispatcher.CheckAccess())
            return window.Dispatcher.InvokeAsync(() => RunAsync(window, action)).Task.Unwrap();
        SynchronizationContext? previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
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
        if (!window.IsVisible)
            return;
        nint handle = Handle(window);
        if (Voidstrap.Utility.Platform.IsLinux && window.IsVisible)
            LinuxWindowInterop.TryShowOverlayWindow(handle);
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
