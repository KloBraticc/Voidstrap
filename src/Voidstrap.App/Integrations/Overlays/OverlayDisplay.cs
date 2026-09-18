using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Interop = Voidstrap.Integrations.AntiAliasing.AntiAliasingInterop;

namespace Voidstrap.Integrations.Overlays
{
    public static class OverlayDisplay
    {
		private static readonly object _refreshLock = new object();
		private static IntPtr _cachedMonitor;
		private static int _cachedHz = 60;
		private static long _cachedAtMs;

        public static int RefreshHz()
        {
            try
            {
				IntPtr hwnd = RobloxWindowTracker.Current.Hwnd;
				long now = Environment.TickCount64;
				if (hwnd == IntPtr.Zero)
				{
					lock (_refreshLock)
					{
						if (_cachedAtMs != 0 && now - _cachedAtMs < 5000)
							return _cachedHz;
					}
					hwnd = FindRobloxWindow();
				}
                IntPtr mon = hwnd != IntPtr.Zero
                    ? Interop.MonitorFromWindow(hwnd, Interop.MONITOR_DEFAULTTONEAREST)
                    : IntPtr.Zero;
				lock (_refreshLock)
				{
					if (_cachedAtMs != 0 && mon == _cachedMonitor && now - _cachedAtMs < 2000)
						return _cachedHz;
				}
				int refreshHz = ReadRefreshHz(mon);
				lock (_refreshLock)
				{
					_cachedMonitor = mon;
					_cachedHz = refreshHz;
					_cachedAtMs = now;
				}
				return refreshHz;
			}
			catch
			{
				return 60;
			}
		}

		private static int ReadRefreshHz(IntPtr mon)
		{
			try
			{
                if (mon != IntPtr.Zero)
                {
                    var mi = new Interop.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<Interop.MONITORINFOEXW>() };
                    if (Interop.GetMonitorInfoW(mon, ref mi))
                    {
                        var dm = new Interop.DEVMODEW { dmSize = (ushort)Marshal.SizeOf<Interop.DEVMODEW>() };
                        if (Interop.EnumDisplaySettingsW(mi.szDevice, Interop.ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1)
                            return (int)dm.dmDisplayFrequency;
                    }
                }
                var primary = new Interop.DEVMODEW { dmSize = (ushort)Marshal.SizeOf<Interop.DEVMODEW>() };
                if (Interop.EnumDisplaySettingsW(null, Interop.ENUM_CURRENT_SETTINGS, ref primary) && primary.dmDisplayFrequency > 1)
                    return (int)primary.dmDisplayFrequency;
            }
            catch
            {
            }
            return 60;
        }

        private static IntPtr FindRobloxWindow()
        {
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
				foreach (Process process in processes)
				{
					try
					{
						IntPtr hwnd = process.MainWindowHandle;
						if (hwnd != IntPtr.Zero)
							return hwnd;
					}
					catch
					{
					}
				}
				return IntPtr.Zero;
			}
			finally
			{
				foreach (Process process in processes)
				{
					try
					{
						process.Dispose();
					}
					catch
					{
					}
				}
			}
        }
    }
}
