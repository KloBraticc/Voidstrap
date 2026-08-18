using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Voidstrap.Integrations.Overlays;
using Interop = Voidstrap.Integrations.AntiAliasing.AntiAliasingInterop;

namespace Voidstrap.Integrations.Fullscreen
{
    public static class FakeExclusiveFullscreen
    {
        private const string LOG_IDENT = "FakeExclusiveFullscreen";

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_LAYERED = 0x00080000;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        private const int DWM_TNP_RECTDESTINATION = 0x00000001;
        private const int DWM_TNP_OPACITY = 0x00000004;
        private const int DWM_TNP_VISIBLE = 0x00000008;
        private const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

        private static readonly object _sync = new object();
        private static Window? _backdrop;
        private static IntPtr _thumb;
        private static IntPtr _robloxHwnd;
        private static RECT _savedRect;
        private static RECT _monitor;
        private static int _savedStyle;
        private static int _savedExStyle;
        private static bool _applied;
        private static bool _tracking;
        private static bool _watchingDisplay;
        private static IDisposable? _trackerLease;

        public static bool Enabled => App.Settings.Prop.FakeExclusiveFullscreen;

        public static void OnGameJoin()
        {
            if (Enabled)
                Apply();
            else
                Restore();
        }

        public static void OnGameLeave() => Restore();

        public static void Shutdown() => Restore();

        public static bool Apply()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return false;

            if (!dispatcher.CheckAccess())
                return dispatcher.Invoke(Apply);

            lock (_sync)
            {
                _trackerLease ??= RobloxWindowTracker.Acquire();
                IntPtr hwnd = RobloxWindowTracker.Current.Hwnd;
                if (hwnd == IntPtr.Zero)
                {
                    App.Logger.WriteLine(LOG_IDENT, "No Roblox window yet, cannot apply");
                    if (!_applied)
                    {
                        _trackerLease.Dispose();
                        _trackerLease = null;
                    }
                    return false;
                }

                if (!TryGetMonitor(hwnd, out RECT monitor))
                    return false;

                if (_applied && _robloxHwnd != hwnd)
                    RestoreLocked();

                int width = monitor.Right - monitor.Left;
                int height = monitor.Bottom - monitor.Top;
                if (width <= 1 || height <= 1)
                    return false;

                if (!_applied)
                {
                    if (!GetWindowRect(hwnd, out _savedRect))
                        return false;
                    _savedStyle = GetWindowLong(hwnd, GWL_STYLE);
                    _savedExStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    _robloxHwnd = hwnd;
                }

                int style = GetWindowLong(hwnd, GWL_STYLE);
                style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME);
                style |= WS_POPUP;
                SetWindowLong(hwnd, GWL_STYLE, style);

                SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, width, height,
                    SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);

                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);

                _monitor = monitor;

                if (GetForegroundWindow() != hwnd)
                    SetForegroundWindow(hwnd);

                if (!_tracking)
                {
                    _tracking = true;
                    RobloxWindowTracker.Changed += OnTrackerChanged;
                }

                if (!_watchingDisplay)
                {
                    _watchingDisplay = true;
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                }

                _applied = true;
                App.Logger.WriteLine(LOG_IDENT, $"Roblox window set to {width}x{height} borderless fullscreen");
                return true;
            }
        }

        public static void Restore()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.Invoke(Restore);
                return;
            }

            lock (_sync)
            {
                RestoreLocked();
            }
        }

        private static void RestoreLocked()
        {
            if (!_applied && _backdrop == null)
                return;

            if (_tracking)
            {
                _tracking = false;
                RobloxWindowTracker.Changed -= OnTrackerChanged;
            }

            _trackerLease?.Dispose();
            _trackerLease = null;

            if (_watchingDisplay)
            {
                _watchingDisplay = false;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            }

            if (_thumb != IntPtr.Zero)
            {
                try { DwmUnregisterThumbnail(_thumb); } catch { }
                _thumb = IntPtr.Zero;
            }

            if (_backdrop != null)
            {
                try { _backdrop.Close(); } catch { }
                _backdrop = null;
            }

            if (_applied && _robloxHwnd != IntPtr.Zero && IsWindow(_robloxHwnd))
            {
                SetWindowLong(_robloxHwnd, GWL_STYLE, _savedStyle);
                SetWindowLong(_robloxHwnd, GWL_EXSTYLE, _savedExStyle);
                SetWindowPos(_robloxHwnd, (_savedExStyle & WS_EX_TOPMOST) != 0 ? HWND_TOPMOST : HWND_NOTOPMOST,
                    _savedRect.Left, _savedRect.Top,
                    _savedRect.Right - _savedRect.Left, _savedRect.Bottom - _savedRect.Top,
                    SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
                App.Logger.WriteLine(LOG_IDENT, "Restored the Roblox window to its original size, style and z order");
            }

            _applied = false;
            _robloxHwnd = IntPtr.Zero;
        }

        private static void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted)
                    return;

                if (!dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(() => OnDisplaySettingsChanged(sender, e)));
                    return;
                }

                lock (_sync)
                {
                    if (!_applied)
                        return;
                }

                App.Logger.WriteLine(LOG_IDENT, "Display settings changed, refitting the Roblox window to the new monitor size");
                Apply();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT + "::OnDisplaySettingsChanged", ex);
            }
        }

        private static void OnTrackerChanged(object? sender, RobloxWindowRect rect)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => OnTrackerChanged(sender, rect)));
                return;
            }

            bool reapply;
            lock (_sync)
            {
                if (!_applied)
                    return;

                bool monitorChanged = rect.Valid
                    && rect.Hwnd != IntPtr.Zero
                    && TryGetMonitor(rect.Hwnd, out RECT currentMonitor)
                    && (currentMonitor.Left != _monitor.Left
                        || currentMonitor.Top != _monitor.Top
                        || currentMonitor.Right != _monitor.Right
                        || currentMonitor.Bottom != _monitor.Bottom);
                reapply = rect.Valid && rect.Foreground && rect.Hwnd != IntPtr.Zero && (rect.Hwnd != _robloxHwnd || monitorChanged);
            }

            if (reapply)
                Apply();
        }

        private static void LogZOrder(IntPtr roblox)
        {
            try
            {
                if (_backdrop == null)
                    return;

                IntPtr backdrop = new WindowInteropHelper(_backdrop).Handle;
                int robloxEx = GetWindowLong(roblox, GWL_EXSTYLE);
                int backdropEx = GetWindowLong(backdrop, GWL_EXSTYLE);

                int robloxDepth = -1;
                int backdropDepth = -1;
                int index = 0;

                for (IntPtr w = GetTopWindow(IntPtr.Zero); w != IntPtr.Zero && index < 4000; w = GetWindow(w, GW_HWNDNEXT), index++)
                {
                    if (w == roblox && robloxDepth < 0) robloxDepth = index;
                    if (w == backdrop && backdropDepth < 0) backdropDepth = index;
                    if (robloxDepth >= 0 && backdropDepth >= 0) break;
                }

                string winner = backdropDepth < 0 ? "backdrop NOT IN Z ORDER (hidden?)"
                    : robloxDepth < 0 ? "roblox not found in z order"
                    : backdropDepth < robloxDepth ? "backdrop is ABOVE roblox, correct"
                    : "ROBLOX IS ABOVE THE BACKDROP, this is the two windows bug";

                App.Logger.WriteLine(LOG_IDENT,
                    $"zorder: {winner}. roblox depth={robloxDepth} topmost={(robloxEx & WS_EX_TOPMOST) != 0}, " +
                    $"backdrop depth={backdropDepth} topmost={(backdropEx & WS_EX_TOPMOST) != 0} layered={(backdropEx & WS_EX_LAYERED) != 0} transparent={(backdropEx & WS_EX_TRANSPARENT) != 0} visible={_backdrop.IsVisible}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "zorder probe failed: " + ex.Message);
            }
        }

        private static void RaiseBackdrop()
        {
            if (_backdrop == null)
                return;

            IntPtr handle = new WindowInteropHelper(_backdrop).Handle;
            if (handle == IntPtr.Zero)
                return;

            SetWindowPos(handle, HWND_TOPMOST, _monitor.Left, _monitor.Top,
                _monitor.Right - _monitor.Left, _monitor.Bottom - _monitor.Top,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER);

            OverlayDiagnostics.RaiseOverlayWindows();
        }

        private static void EnsureBackdrop(RECT monitor)
        {
            if (_backdrop != null)
                return;

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Black,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Topmost = true,
                IsHitTestVisible = false,
                Focusable = false,
                Title = "Voidstrap Fullscreen",
                Left = monitor.Left,
                Top = monitor.Top,
                Width = 1,
                Height = 1
            };

            var helper = new WindowInteropHelper(window);
            IntPtr handle = helper.EnsureHandle();

            int exStyle = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

            window.Show();

            SetWindowPos(handle, HWND_TOPMOST, monitor.Left, monitor.Top,
                monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER);

            _backdrop = window;
        }

        private static bool RegisterThumbnail(IntPtr source, int width, int height)
        {
            if (_backdrop == null)
                return false;

            IntPtr destination = new WindowInteropHelper(_backdrop).Handle;
            if (destination == IntPtr.Zero)
                return false;

            if (_thumb != IntPtr.Zero)
            {
                try { DwmUnregisterThumbnail(_thumb); } catch { }
                _thumb = IntPtr.Zero;
            }

            if (DwmRegisterThumbnail(destination, source, out IntPtr thumb) != 0 || thumb == IntPtr.Zero)
                return false;

            _thumb = thumb;

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new RECT { Left = 0, Top = 0, Right = width, Bottom = height },
                opacity = 255,
                fVisible = true,
                fSourceClientAreaOnly = true
            };

            return DwmUpdateThumbnailProperties(_thumb, ref props) == 0;
        }

        private static bool TryGetMonitor(IntPtr hwnd, out RECT bounds)
        {
            bounds = default;
            IntPtr monitor = Interop.MonitorFromWindow(hwnd, Interop.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;

            var info = new Interop.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<Interop.MONITORINFOEXW>() };
            if (!Interop.GetMonitorInfoW(monitor, ref info))
                return false;

            bounds = new RECT
            {
                Left = info.rcMonitor.Left,
                Top = info.rcMonitor.Top,
                Right = info.rcMonitor.Right,
                Bottom = info.rcMonitor.Bottom
            };
            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        private const uint GW_HWNDNEXT = 2;

        [DllImport("dwmapi.dll")]
        private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("dwmapi.dll")]
        private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int newLong);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetTopWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hwnd);
    }
}
