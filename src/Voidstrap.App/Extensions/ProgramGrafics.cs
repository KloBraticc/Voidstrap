using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RobloxLightingOverlay
{
    public static partial class RobloxWindow
    {
        private const int RescanIntervalMs = 500;
        private static readonly object _sync = new object();
        private static readonly HashSet<uint> _pids = new HashSet<uint>();
        private static readonly char[] _className = new char[64];
        private static readonly EnumWindowsProc _enumProc = EnumProc;
        private static readonly IntPtr _enumProcPointer = Marshal.GetFunctionPointerForDelegate(_enumProc);
        private static IntPtr _cachedHwnd;
        private static long _cacheAt;
        private static IntPtr _best;
        private static long _bestArea;

        public static IntPtr GetHandle() => Voidstrap.Utility.Platform.IsWindows ? GetHwnd() : GetLinuxHandle();

        public static bool TryGet(out RECT r)
        {
            r = new RECT();
            if (!Voidstrap.Utility.Platform.IsWindows)
            {
                return TryGetLinux(out r);
            }

            IntPtr hwnd = GetHwnd();
            if (hwnd == IntPtr.Zero)
                return false;
            if (!GetWindowRect(hwnd, out r))
                return false;
            try
            {
                var s = System.Windows.Forms.Screen.FromHandle(hwnd);
                if (Math.Abs((r.Right - r.Left) - s.Bounds.Width) < 6 &&
                    Math.Abs((r.Bottom - r.Top) - s.Bounds.Height) < 6)
                {
                    r.Left = s.Bounds.Left;
                    r.Top = s.Bounds.Top;
                    r.Right = s.Bounds.Right;
                    r.Bottom = s.Bounds.Bottom;
                }
            }
            catch
            {
            }
            return true;
        }

        private static bool TryGetLinux(out RECT r)
        {
            r = new RECT();
            try
            {
                Voidstrap.Platform.Linux.LinuxWindowGeometry geometry = Voidstrap.Platform.Linux.LinuxWindowInterop.FindRuntimeWindow();
                if (!geometry.Valid || geometry.Window == 0 || geometry.Width <= 0 || geometry.Height <= 0)
                {
                    return false;
                }

                r.Left = geometry.Left;
                r.Top = geometry.Top;
                r.Right = geometry.Left + geometry.Width;
                r.Bottom = geometry.Top + geometry.Height;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static IntPtr GetLinuxHandle()
        {
            try
            {
                return (IntPtr)Voidstrap.Platform.Linux.LinuxWindowInterop.FindRuntimeWindow().Window;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        private static IntPtr GetHwnd()
        {
            lock (_sync)
            {
                if (_cachedHwnd != IntPtr.Zero && IsWindow(_cachedHwnd) && IsWindowVisible(_cachedHwnd) && !IsIconic(_cachedHwnd))
                    return _cachedHwnd;

                long now = Environment.TickCount64;
                if (_cacheAt != 0 && now - _cacheAt < RescanIntervalMs)
                    return IntPtr.Zero;

                _cachedHwnd = Find();
                _cacheAt = now;
                return _cachedHwnd;
            }
        }

        private static IntPtr Find()
        {
            _pids.Clear();
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("RobloxPlayerBeta");
            }
            catch
            {
                return IntPtr.Zero;
            }
            try
            {
                foreach (Process p in processes)
                {
                    try { _pids.Add((uint)p.Id); } catch { }
                }
            }
            finally
            {
                foreach (Process p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
            if (_pids.Count == 0)
                return IntPtr.Zero;

            _best = IntPtr.Zero;
            _bestArea = 0;
            try { _ = EnumWindows(_enumProcPointer, IntPtr.Zero); } catch { }
            return _best;
        }

        private static bool EnumProc(IntPtr hwnd, IntPtr lparam)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;
            GetWindowThreadProcessId(hwnd, out uint wpid);
            if (wpid == 0 || !_pids.Contains(wpid))
                return true;
            int classLength = GetClassName(hwnd, _className, _className.Length);
            if (!_className.AsSpan(0, Math.Max(0, classLength)).SequenceEqual("WINDOWSCLIENT"))
                return true;
            if (!GetClientRect(hwnd, out RECT rc))
                return true;
            long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
            if (area > _bestArea)
            {
                _bestArea = area;
                _best = hwnd;
            }
            return true;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(IntPtr callback, IntPtr lparam);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int GetClassName(IntPtr hwnd, [Out] char[] className, int maxCount);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hwnd);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(IntPtr hwnd, out RECT rect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr hWnd, out RECT rect);
    }

    public partial struct RECT { public int Left, Top, Right, Bottom; }
}
