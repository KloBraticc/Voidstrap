using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Interop = Voidstrap.Integrations.AntiAliasing.AntiAliasingInterop;

namespace Voidstrap.Integrations.Fullscreen
{
    public static partial class RobloxTitleBarGuard
    {
        private const int GuardPixels = 6;

        private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);

        private static DispatcherTimer? _timer;

        private static IDisposable? _trackerLease;

        private static bool _clipped;

        private static RECT _clip;

        public static bool Enabled => Voidstrap.Utility.Platform.IsWindows && App.Settings.Prop.HideFullscreenTitleBar;

        public static void Start()
        {
            const string LOG_IDENT = "RobloxTitleBarGuard::Start";
            if (!Enabled)
                return;
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(Start);
                return;
            }
            if (_timer != null)
                return;
            _trackerLease = RobloxWindowTracker.Acquire();
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
            _timer.Tick += OnTick;
            _timer.Start();
            App.Logger.WriteLine(LOG_IDENT, "Keeping the cursor out of the Roblox fullscreen title bar");
        }

        public static void Shutdown()
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(Shutdown);
                return;
            }
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                _timer = null;
            }
            _trackerLease?.Dispose();
            _trackerLease = null;
            Release();
        }

        private static void OnTick(object? sender, EventArgs e)
        {
            const string LOG_IDENT = "RobloxTitleBarGuard::OnTick";
            if (!Enabled)
            {
                Shutdown();
                return;
            }
            IntPtr hwnd = RobloxWindowTracker.Current.Hwnd;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || GetForegroundWindow() != hwnd || IsIconic(hwnd) || !TryGetFullscreenMonitor(hwnd, out RECT monitor))
            {
                Release();
                return;
            }
            RECT wanted = monitor with { Top = monitor.Top + GuardPixels };
            if (_clipped && GetClipCursor(out RECT current) && current.Equals(wanted))
                return;
            if (!ClipCursor(in wanted))
                return;
            if (!_clipped)
                App.Logger.WriteLine(LOG_IDENT, "Roblox is fullscreen and focused, the title bar strip is now blocked");
            _clipped = true;
            _clip = wanted;
        }

        private static void Release()
        {
            if (!_clipped)
                return;
            _clipped = false;
            if (GetClipCursor(out RECT current) && current.Equals(_clip))
                ClipCursor(IntPtr.Zero);
        }

        private static bool TryGetFullscreenMonitor(IntPtr hwnd, out RECT bounds)
        {
            bounds = default;
            if (!GetWindowRect(hwnd, out RECT window))
                return false;
            IntPtr monitor = Interop.MonitorFromWindow(hwnd, Interop.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;
            var info = new Interop.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<Interop.MONITORINFOEXW>() };
            if (!Interop.GetMonitorInfoW(monitor, ref info))
                return false;
            bounds = new RECT { Left = info.rcMonitor.Left, Top = info.rcMonitor.Top, Right = info.rcMonitor.Right, Bottom = info.rcMonitor.Bottom };
            return window.Left <= bounds.Left && window.Top <= bounds.Top && window.Right >= bounds.Right && window.Bottom >= bounds.Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private partial record struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ClipCursor(in RECT rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ClipCursor(IntPtr rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClipCursor(out RECT rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr hwnd, out RECT rect);

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
