using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Interop = Voidstrap.Integrations.AntiAliasing.AntiAliasingInterop;

namespace Voidstrap.Integrations.Fullscreen
{
    public static unsafe partial class RobloxTitleBarGuard
    {
        private const int GuardPixels = 6;

        private const int WH_MOUSE_LL = 14;

        private const int WM_MOUSEMOVE = 0x0200;

        private const uint WM_QUIT = 0x0012;

        private const uint LLMHF_INJECTED = 0x00000001;

        private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);

        private static readonly object _sync = new();

        private static DispatcherTimer? _timer;

        private static IDisposable? _trackerLease;

        private static Thread? _hookThread;

        private static uint _hookThreadId;

        private static nint _hook;

        private static nint _guardHwnd;

        private static int _left;

        private static int _right;

        private static int _limit;

        private static bool _loggedBlock;

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
            RobloxWindowTracker.Changed += OnTrackerChanged;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CheckInterval };
            _timer.Tick += OnTick;
            _timer.Start();
            lock (_sync)
            {
                _hookThread = new Thread(RunHook) { IsBackground = true, Name = "RobloxTitleBarGuard", Priority = ThreadPriority.AboveNormal };
                _hookThread.Start();
            }
            Refresh();
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
                RobloxWindowTracker.Changed -= OnTrackerChanged;
            }
            _trackerLease?.Dispose();
            _trackerLease = null;
            Volatile.Write(ref _guardHwnd, 0);
            lock (_sync)
            {
                if (_hookThreadId != 0)
                    _ = PostThreadMessageW(_hookThreadId, WM_QUIT, 0, 0);
                _hookThread = null;
                _hookThreadId = 0;
            }
        }

        private static void OnTick(object? sender, EventArgs e)
        {
            if (!Enabled)
            {
                Shutdown();
                return;
            }
            Refresh();
        }

        private static void OnTrackerChanged(object? sender, RobloxWindowRect rect)
        {
            Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return;
            dispatcher.BeginInvoke(Refresh);
        }

        private static void Refresh()
        {
            const string LOG_IDENT = "RobloxTitleBarGuard::Refresh";
            if (_timer == null)
                return;
            IntPtr hwnd = RobloxWindowTracker.Current.Hwnd;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsIconic(hwnd) || !TryGetFullscreenMonitor(hwnd, out RECT monitor))
            {
                Volatile.Write(ref _guardHwnd, 0);
                return;
            }
            _left = monitor.Left;
            _right = monitor.Right;
            _limit = monitor.Top + GuardPixels;
            Volatile.Write(ref _guardHwnd, hwnd);
            if (!_loggedBlock)
            {
                _loggedBlock = true;
                App.Logger.WriteLine(LOG_IDENT, "Roblox is fullscreen, the title bar strip is blocked while it has focus");
            }
        }

        private static void RunHook()
        {
            const string LOG_IDENT = "RobloxTitleBarGuard::RunHook";
            lock (_sync)
            {
                if (!ReferenceEquals(_hookThread, Thread.CurrentThread))
                    return;
                _hookThreadId = GetCurrentThreadId();
            }
            _hook = SetWindowsHookExW(WH_MOUSE_LL, (nint)(delegate* unmanaged<int, nint, nint, nint>)&HookProc, GetModuleHandleW(null), 0);
            if (_hook == 0)
            {
                App.Logger.WriteLine(LOG_IDENT, "The mouse hook could not be installed, error " + Marshal.GetLastPInvokeError());
                return;
            }
            try
            {
                while (GetMessageW(out Msg _, 0, 0, 0) > 0)
                {
                }
            }
            finally
            {
                _ = UnhookWindowsHookEx(_hook);
                _hook = 0;
            }
        }

        [UnmanagedCallersOnly]
        private static nint HookProc(int code, nint wParam, nint lParam)
        {
            if (code >= 0 && wParam == WM_MOUSEMOVE)
            {
                nint hwnd = Volatile.Read(ref _guardHwnd);
                MouseHook* info = (MouseHook*)lParam;
                if (hwnd != 0 && info->Y < _limit && info->X >= _left && info->X < _right && (info->Flags & LLMHF_INJECTED) == 0 && GetForegroundWindow() == hwnd)
                {
                    _ = SetCursorPos(info->X, _limit);
                    return 1;
                }
            }
            return CallNextHookEx(0, code, wParam, lParam);
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
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseHook
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public nuint ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public nint Hwnd;
            public uint Message;
            public nuint WParam;
            public nint LParam;
            public uint Time;
            public int X;
            public int Y;
        }

        [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
        private static partial nint SetWindowsHookExW(int hookId, nint proc, nint module, uint threadId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnhookWindowsHookEx(nint hook);

        [LibraryImport("user32.dll")]
        private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        private static partial int GetMessageW(out Msg msg, nint hwnd, uint filterMin, uint filterMax);

        [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
        private static partial int PostThreadMessageW(uint threadId, uint message, nuint wParam, nint lParam);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetCurrentThreadId();

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint GetModuleHandleW(string? moduleName);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetCursorPos(int x, int y);

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
        private static partial nint GetForegroundWindow();
    }
}
