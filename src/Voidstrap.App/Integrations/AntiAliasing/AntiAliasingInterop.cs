using System;
using System.Runtime.InteropServices;

namespace Voidstrap.Integrations.AntiAliasing
{
    internal static partial class AntiAliasingInterop
    {
        public const uint WS_POPUP = 0x80000000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        public const uint LWA_ALPHA = 0x00000002;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_HIDE = 0;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint WDA_NONE = 0;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint PM_REMOVE = 0x0001;
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        public partial struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public partial struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public partial struct WNDCLASSEXW
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public IntPtr lpszMenuName;
            public IntPtr lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public partial struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool UnregisterClassW(IntPtr lpClassName, IntPtr hInstance);

        [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr CreateWindowExW(int dwExStyle, IntPtr lpClassName, string lpWindowName, uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [LibraryImport("user32.dll")]
        public static partial IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool TranslateMessage(ref MSG lpMsg);

        [LibraryImport("user32.dll")]
        public static partial IntPtr DispatchMessageW(ref MSG lpMsg);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial IntPtr GetModuleHandleW(string? lpModuleName);

        [LibraryImport("user32.dll")]
        public static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool IsWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [LibraryImport("user32.dll")]
        public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO mi);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [LibraryImport("kernel32.dll")]
        public static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [LibraryImport("winmm.dll")]
        public static partial uint timeBeginPeriod(uint uPeriod);

        [LibraryImport("winmm.dll")]
        public static partial uint timeEndPeriod(uint uPeriod);

        [StructLayout(LayoutKind.Sequential)]
        public partial struct UNSIGNED_RATIO
        {
            public uint uiNumerator;
            public uint uiDenominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        public partial struct DWM_TIMING_INFO
        {
            public uint cbSize;
            public UNSIGNED_RATIO rateRefresh;
            public ulong qpcRefreshPeriod;
            public UNSIGNED_RATIO rateCompose;
            public ulong qpcVBlank;
            public ulong cRefresh;
            public uint cDXRefresh;
            public ulong qpcCompose;
            public ulong cFrame;
            public uint cDXPresent;
            public ulong cRefreshFrame;
            public ulong cFrameSubmitted;
            public uint cDXPresentSubmitted;
            public ulong cFrameConfirmed;
            public uint cDXPresentConfirmed;
            public ulong cRefreshConfirmed;
            public uint cDXRefreshConfirmed;
            public ulong cFramesLate;
            public uint cFramesOutstanding;
            public ulong cFrameDisplayed;
            public ulong qpcFrameDisplayed;
            public ulong cRefreshFrameDisplayed;
            public ulong cFrameComplete;
            public ulong qpcFrameComplete;
            public ulong cFramePending;
            public ulong qpcFramePending;
            public ulong cFramesDisplayed;
            public ulong cFramesComplete;
            public ulong cFramesPending;
            public ulong cFramesAvailable;
            public ulong cFramesDropped;
            public ulong cFramesMissed;
            public ulong cRefreshNextDisplayed;
            public ulong cRefreshNextPresented;
            public ulong cRefreshesDisplayed;
            public ulong cRefreshesPresented;
            public ulong cRefreshStarted;
            public ulong cPixelsReceived;
            public ulong cPixelsDrawn;
            public ulong cBuffersEmpty;
        }

        [LibraryImport("dwmapi.dll")]
        public static partial int DwmGetCompositionTimingInfo(IntPtr hwnd, ref DWM_TIMING_INFO pTimingInfo);

        public const int ENUM_CURRENT_SETTINGS = -1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public partial struct MONITORINFOEXW
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public partial struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfoExNative(IntPtr hMonitor, IntPtr mi);

        public static bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW mi)
        {
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<MONITORINFOEXW>());
            try
            {
                Marshal.StructureToPtr(mi, buffer, false);
                bool result = GetMonitorInfoExNative(hMonitor, buffer);
                mi = Marshal.PtrToStructure<MONITORINFOEXW>(buffer);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure<MONITORINFOEXW>(buffer);
                Marshal.FreeHGlobal(buffer);
            }
        }

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, EntryPoint = "EnumDisplaySettingsW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumDisplaySettingsNative(string? lpszDeviceName, int iModeNum, IntPtr lpDevMode);

        public static bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode)
        {
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf<DEVMODEW>());
            try
            {
                Marshal.StructureToPtr(lpDevMode, buffer, false);
                bool result = EnumDisplaySettingsNative(lpszDeviceName, iModeNum, buffer);
                lpDevMode = Marshal.PtrToStructure<DEVMODEW>(buffer);
                return result;
            }
            finally
            {
                Marshal.DestroyStructure<DEVMODEW>(buffer);
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
