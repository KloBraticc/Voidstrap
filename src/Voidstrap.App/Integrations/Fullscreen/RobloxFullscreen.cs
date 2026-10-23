using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Voidstrap.Integrations.Overlays;
using Interop = Voidstrap.Integrations.AntiAliasing.AntiAliasingInterop;

namespace Voidstrap.Integrations.Fullscreen
{
    public static partial class RobloxFullscreen
    {
        private const string LOG_IDENT = "RobloxFullscreen";

        private enum Mode
        {
            Normal,
            Borderless,
            Exclusive
        }

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_BORDER = 0x00800000;
        private const int WS_DLGFRAME = 0x00400000;
        private const int WS_MAXIMIZE = 0x01000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int FrameStyles = WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU | WS_BORDER | WS_DLGFRAME | WS_MAXIMIZE;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(2);
        private const int BurstLimit = 12;

        private static readonly object _sync = new object();
        private static IDisposable? _trackerLease;
        private static bool _started;
        private static bool _retrying;
        private static IntPtr _hwnd;
        private static RECT _savedRect;
        private static int _savedStyle;
        private static int _savedExStyle;
        private static bool _saved;
        private static bool _logged;
        private static DateTime _burstStart;
        private static int _burstCount;

        private static Mode CurrentMode
        {
            get
            {
                var prop = App.Settings.Prop;
                if (prop.FakeExclusiveFullscreen)
                    return Mode.Exclusive;
                return prop.FakeBorderlessFullscreen ? Mode.Borderless : Mode.Normal;
            }
        }

        public static void Start()
        {
            if (!Voidstrap.Utility.Platform.IsWindows || CurrentMode == Mode.Normal)
                return;

            if (!OnDispatcher(Start))
                return;

            lock (_sync)
            {
                if (!_started)
                {
                    _started = true;
                    _trackerLease = RobloxWindowTracker.Acquire();
                    RobloxWindowTracker.Changed += OnTrackerChanged;
                    SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                    App.Logger.WriteLine(LOG_IDENT, $"Keeping the Roblox window in {CurrentMode} fullscreen");
                }

                if (EnforceLocked() || _retrying)
                    return;
                _retrying = true;
            }
            _ = WaitForWindowAsync();
        }

        public static void OnGameJoin()
        {
            Start();
        }

        public static void Shutdown()
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return;

            if (!OnDispatcher(Shutdown))
                return;

            lock (_sync)
            {
                if (!_started)
                    return;
                _started = false;
                RobloxWindowTracker.Changed -= OnTrackerChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                _trackerLease?.Dispose();
                _trackerLease = null;
                RestoreLocked();
            }
        }

        private static bool OnDispatcher(Action action)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return false;
            if (dispatcher.CheckAccess())
                return true;
            dispatcher.BeginInvoke(action);
            return false;
        }

        private static async Task WaitForWindowAsync()
        {
            try
            {
                for (int attempt = 0; attempt < 120; attempt++)
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher == null || dispatcher.HasShutdownStarted)
                        return;
                    bool done = await dispatcher.InvokeAsync(() =>
                    {
                        lock (_sync)
                            return !_started || EnforceLocked();
                    });
                    if (done)
                        return;
                }
                App.Logger.WriteLine(LOG_IDENT, "The Roblox window never appeared, fullscreen was not applied");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Waiting for the Roblox window failed: " + ex.Message);
            }
            finally
            {
                lock (_sync)
                    _retrying = false;
            }
        }

        private static void OnTrackerChanged(object? sender, RobloxWindowRect rect)
        {
            if (!OnDispatcher(() => OnTrackerChanged(sender, rect)))
                return;
            lock (_sync)
            {
                if (_started)
                    EnforceLocked();
            }
        }

        private static void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            if (!OnDispatcher(() => OnDisplaySettingsChanged(sender, e)))
                return;
            lock (_sync)
            {
                if (!_started)
                    return;
                App.Logger.WriteLine(LOG_IDENT, "Display settings changed, refitting the Roblox window");
                EnforceLocked();
            }
        }

        private static bool EnforceLocked()
        {
            Mode mode = CurrentMode;
            IntPtr hwnd = RobloxWindowTracker.Current.Hwnd;
            if (mode == Mode.Normal || hwnd == IntPtr.Zero || !IsWindow(hwnd))
                return false;

            if (hwnd != _hwnd)
            {
                _hwnd = hwnd;
                _saved = false;
                _logged = false;
            }

            if (IsIconic(hwnd))
                return true;

            if (!TryGetMonitor(hwnd, out RECT monitor))
                return false;
            int width = monitor.Right - monitor.Left;
            int height = monitor.Bottom - monitor.Top;
            if (width <= 1 || height <= 1)
                return false;

            int style = GetWindowLong(hwnd, GWL_STYLE);
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (!GetWindowRect(hwnd, out RECT current))
                return false;

            if (!_saved)
            {
                _savedRect = current;
                _savedStyle = style;
                _savedExStyle = exStyle;
                _saved = true;
            }

            int wantedStyle = (style & ~FrameStyles) | WS_POPUP;
            bool styleWrong = style != wantedStyle;
            bool sizeWrong = current.Left != monitor.Left || current.Top != monitor.Top || current.Right != monitor.Right || current.Bottom != monitor.Bottom;
            bool foreground = GetForegroundWindow() == hwnd;
            bool topmost = (exStyle & WS_EX_TOPMOST) != 0;
            bool wantTopmost = mode == Mode.Exclusive && foreground;
            bool zOrderWrong = mode == Mode.Exclusive ? topmost != wantTopmost : topmost && (_savedExStyle & WS_EX_TOPMOST) == 0;

            if (!styleWrong && !sizeWrong && !zOrderWrong)
                return true;

            if (TooManyCorrections())
                return true;

            if (styleWrong)
                _ = SetWindowLong(hwnd, GWL_STYLE, wantedStyle);

            if (styleWrong || sizeWrong)
                SetWindowPos(hwnd, IntPtr.Zero, monitor.Left, monitor.Top, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);

            if (zOrderWrong)
            {
                SetWindowPos(hwnd, wantTopmost ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                if (wantTopmost)
                    OverlayDiagnostics.RaiseOverlayWindows();
            }

            if (!_logged)
            {
                _logged = true;
                App.Logger.WriteLine(LOG_IDENT, $"Roblox window set to {width}x{height} {mode} fullscreen");
            }
            return true;
        }

        private static bool TooManyCorrections()
        {
            DateTime now = DateTime.UtcNow;
            if (now - _burstStart > BurstWindow)
            {
                _burstStart = now;
                _burstCount = 0;
            }
            if (++_burstCount <= BurstLimit)
                return false;
            if (_burstCount == BurstLimit + 1)
                App.Logger.WriteLine(LOG_IDENT, "Roblox keeps resizing its window, pausing corrections for a moment");
            return true;
        }

        private static void RestoreLocked()
        {
            IntPtr hwnd = _hwnd;
            bool saved = _saved;
            _hwnd = IntPtr.Zero;
            _saved = false;
            if (!saved || hwnd == IntPtr.Zero || !IsWindow(hwnd))
                return;

            _ = SetWindowLong(hwnd, GWL_STYLE, _savedStyle);
            _ = SetWindowLong(hwnd, GWL_EXSTYLE, _savedExStyle);
            SetWindowPos(hwnd, (_savedExStyle & WS_EX_TOPMOST) != 0 ? HWND_TOPMOST : HWND_NOTOPMOST,
                _savedRect.Left, _savedRect.Top,
                _savedRect.Right - _savedRect.Left, _savedRect.Bottom - _savedRect.Top,
                SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
            App.Logger.WriteLine(LOG_IDENT, "Restored the Roblox window to its original size and style");
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
        private partial struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static partial int GetWindowLong(IntPtr hwnd, int index);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static partial int SetWindowLong(IntPtr hwnd, int index, int newLong);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();
    }
}
