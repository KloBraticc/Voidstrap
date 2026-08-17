using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RobloxLightingOverlay
{
    public static class RobloxWindow
    {
        private const int RescanIntervalMs = 500;
        private static readonly object _sync = new object();
        private static readonly HashSet<uint> _pids = new HashSet<uint>();
        private static readonly StringBuilder _className = new StringBuilder(64);
        private static readonly EnumWindowsProc _enumProc = EnumProc;
        private static IntPtr _cachedHwnd;
        private static long _cacheAt;
        private static IntPtr _best;
        private static long _bestArea;

        public static IntPtr GetHandle() => GetHwnd();

        public static bool TryGet(out RECT r)
        {
            r = new RECT();
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
            try { EnumWindows(_enumProc, IntPtr.Zero); } catch { }
            return _best;
        }

        private static bool EnumProc(IntPtr hwnd, IntPtr lparam)
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                return true;
            GetWindowThreadProcessId(hwnd, out uint wpid);
            if (wpid == 0 || !_pids.Contains(wpid))
                return true;
            _className.Clear();
            GetClassName(hwnd, _className, _className.Capacity);
            if (!_className.Equals("WINDOWSCLIENT".AsSpan()))
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

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lparam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    }

    public struct RECT { public int Left, Top, Right, Bottom; }
}
