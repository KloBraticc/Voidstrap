using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.FrameGeneration;

namespace Voidstrap.Integrations.Overlays
{
    public static class OverlayDiagnostics
    {
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly object _handleLock = new object();
        private static readonly System.Collections.Generic.HashSet<IntPtr> _registeredHandles = new System.Collections.Generic.HashSet<IntPtr>();
        private static readonly System.Collections.Generic.HashSet<IntPtr> _discoveredHandles = new System.Collections.Generic.HashSet<IntPtr>();
        private static IntPtr[] _overlayHandles = Array.Empty<IntPtr>();
		private static int _raisePending;

        public static void RegisterOverlayHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;
            lock (_handleLock)
            {
                _registeredHandles.Add(handle);
                PublishHandlesLocked();
            }
            SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }

        public static void UnregisterOverlayHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return;
            lock (_handleLock)
            {
                _registeredHandles.Remove(handle);
                PublishHandlesLocked();
            }
        }

        public static void RaiseOverlayWindows()
        {
            var app = Application.Current;
            if (app?.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
                return;

            if (!app.Dispatcher.CheckAccess())
            {
				if (Interlocked.Exchange(ref _raisePending, 1) != 0)
					return;
				try
				{
					app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushRaiseOverlayWindows));
				}
				catch (InvalidOperationException)
				{
					Interlocked.Exchange(ref _raisePending, 0);
				}
                return;
            }

			RaiseOverlayWindowsCore(app);
		}

		private static void FlushRaiseOverlayWindows()
		{
			Interlocked.Exchange(ref _raisePending, 0);
			var app = Application.Current;
			if (app?.Dispatcher == null || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished)
				return;
			RaiseOverlayWindowsCore(app);
		}

		private static void RaiseOverlayWindowsCore(Application app)
		{
            var handles = new System.Collections.Generic.List<IntPtr>();
            foreach (Window window in app.Windows)
            {
                if (window == null || !IsOverlayWindow(window))
                    continue;
                try
                {
                    IntPtr handle = new WindowInteropHelper(window).Handle;
                    if (handle != IntPtr.Zero)
                    {
                        handles.Add(handle);
                        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
                    }
                }
                catch
                {
                }
            }
            lock (_handleLock)
            {
                _discoveredHandles.Clear();
                foreach (IntPtr handle in handles)
                    _discoveredHandles.Add(handle);
                PublishHandlesLocked();
            }
        }

        private static void PublishHandlesLocked()
        {
            _registeredHandles.RemoveWhere(handle => handle == IntPtr.Zero || !IsWindow(handle));
            _discoveredHandles.RemoveWhere(handle => handle == IntPtr.Zero || !IsWindow(handle));
            var combined = new System.Collections.Generic.HashSet<IntPtr>(_registeredHandles);
            foreach (IntPtr handle in _discoveredHandles)
                combined.Add(handle);
            Volatile.Write(ref _overlayHandles, combined.ToArray());
        }

        public static bool IsOverlayHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return false;
            foreach (IntPtr overlay in Volatile.Read(ref _overlayHandles))
                if (overlay == handle)
                    return true;
            return false;
        }

        private static bool IsOverlayWindow(Window window)
        {
            string ns = window.GetType().Namespace ?? "";
            string name = window.GetType().Name;
            return ns.Contains("Overlay")
                || ns.Contains("GameChat")
                || name.Contains("Overlay")
                || name.Contains("Crosshair")
                || name.Contains("Cursor");
        }

        public static string BuildReport()
        {
            using IDisposable? lease = TryAcquireTracker();

            var sb = new StringBuilder();
            var prop = App.Settings.Prop;

            bool fpsOverlay = prop.OverlaysEnabled;
            bool crosshair = prop.Crosshair;
            bool gpuCompositor = OverlaySettings.AnyEnabled;
            bool fakeExclusive = prop.FakeExclusiveFullscreen;
            bool fakeBorderless = prop.FakeBorderlessFullscreen;

            RobloxWindowRect roblox = ResolveRobloxRect();
            bool inGame = roblox.Valid;

            sb.AppendLine("Voidstrap overlay diagnostics");
            sb.AppendLine();

            if (!fpsOverlay && !crosshair && !gpuCompositor)
            {
                sb.AppendLine("CAUSE: Every overlay is turned off.");
                sb.AppendLine();
                sb.AppendLine("Nothing is set to show. Turn on what you want:");
                sb.AppendLine("  Stats overlay: Extensions page, Overlays.");
                sb.AppendLine("  Crosshair: Mods page, Crosshair.");
                sb.AppendLine("  RiShade, Anti Aliasing, Frame Generation: Extensions page.");
                return sb.ToString();
            }

            sb.AppendLine("What is turned on:");
            sb.AppendLine("  Stats overlay (FPS, ping, memory): " + OnOff(fpsOverlay));
            sb.AppendLine("  Crosshair: " + OnOff(crosshair));
            sb.AppendLine("  GPU compositor (RiShade, Anti Aliasing, Frame Generation): " + OnOff(gpuCompositor));
            sb.AppendLine("  Fake Exclusive Fullscreen: " + OnOff(fakeExclusive));
            sb.AppendLine("  Fake Borderless Fullscreen: " + OnOff(fakeBorderless));
            sb.AppendLine("  Roblox detected in a game: " + OnOff(inGame));
            sb.AppendLine();

            if (!inGame)
            {
                sb.AppendLine("CAUSE: Roblox is not in a game right now.");
                sb.AppendLine("Overlays only draw while a Roblox game window is open and focused. Join a game, then check again.");
                return sb.ToString();
            }

            bool likelyRobloxFullscreen = IsRobloxOwnFullscreen(roblox, fakeExclusive, fakeBorderless);

            sb.AppendLine("Most likely cause, in order:");
            sb.AppendLine();

            int n = 1;

            if (likelyRobloxFullscreen)
            {
                sb.AppendLine(n++ + ". Roblox is in its own Fullscreen display mode (exclusive fullscreen).");
                sb.AppendLine("   Windows gives an exclusive fullscreen game the whole screen and will not draw ANY external overlay on top of it, including Voidstrap's. This is the number one reason overlays vanish once you are in first person.");
                sb.AppendLine("   FIX: In Roblox, press Esc, open Settings, set Display Mode to Windowed. Or enable Fake Borderless Fullscreen in Voidstrap so Roblox runs borderless (overlays can draw over borderless, never over exclusive fullscreen).");
                sb.AppendLine();
            }

            if (fakeExclusive)
            {
                sb.AppendLine(n++ + ". Fake Exclusive Fullscreen is ON.");
                sb.AppendLine("   It presents Roblox through a black fullscreen backdrop window that sits on top of everything. Voidstrap now re raises the overlays above that backdrop automatically, but if an overlay still does not show, this backdrop is the cause.");
                sb.AppendLine("   FIX: Turn off Fake Exclusive Fullscreen (Deployment page) and test again. If the overlay comes back, leave it off, or use Fake Borderless Fullscreen instead.");
                sb.AppendLine();
            }

            if (gpuCompositor && fpsOverlay)
            {
                sb.AppendLine(n++ + ". The GPU compositor and the stats overlay can fight for the top layer.");
                sb.AppendLine("   RiShade, Anti Aliasing and Frame Generation draw a full screen GPU layer over Roblox and paint their own on screen readout. The separate stats overlay window can end up underneath that GPU layer.");
                sb.AppendLine("   FIX: Use the GPU compositor's built in readout, or turn the GPU compositor off if you only want the plain stats overlay.");
                sb.AppendLine();
            }

            if (fpsOverlay && !OverlayWindowExists("Overlay"))
            {
                sb.AppendLine(n++ + ". The stats overlay is enabled but its window is not open.");
                sb.AppendLine("   It failed to create, or the game was already running when you turned it on. FIX: rejoin the game, or toggle the overlay off and on.");
                sb.AppendLine();
            }

            if (crosshair && !OverlayWindowExists("Crosshair") && !OverlayWindowExists("Cursor"))
            {
                sb.AppendLine(n++ + ". The crosshair is enabled but its window is not open.");
                sb.AppendLine("   FIX: rejoin the game, or toggle the crosshair off and on.");
                sb.AppendLine();
            }

            if (n == 1)
            {
                sb.AppendLine("Everything looks correctly set up and the overlay windows are open.");
                sb.AppendLine("If you still cannot see them, Roblox is almost certainly running in its own exclusive Fullscreen display mode. Press Esc in Roblox, open Settings and set Display Mode to Windowed, or enable Fake Borderless Fullscreen in Voidstrap.");
            }

            return sb.ToString();
        }

        private static IDisposable? TryAcquireTracker()
        {
            try
            {
                return RobloxWindowTracker.Acquire();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("OverlayDiagnostics", "Window tracker unavailable: " + ex.Message);
                return null;
            }
        }

        private static RobloxWindowRect ResolveRobloxRect()
        {
            RobloxWindowRect tracked = RobloxWindowTracker.Current;
            if (tracked.Valid)
                return tracked;

            try
            {
                IntPtr handle = RobloxLightingOverlay.RobloxWindow.GetHandle();
                if (handle == IntPtr.Zero || !RobloxLightingOverlay.RobloxWindow.TryGet(out RobloxLightingOverlay.RECT rect))
                    return tracked;
                bool foreground = AntiAliasingInterop.GetForegroundWindow() == handle;
                return new RobloxWindowRect(handle, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, true, foreground);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("OverlayDiagnostics", "Direct window lookup failed: " + ex.Message);
                return tracked;
            }
        }

        private static bool IsRobloxOwnFullscreen(RobloxWindowRect roblox, bool fakeExclusive, bool fakeBorderless)
        {
            if (fakeExclusive || fakeBorderless)
                return false;
            if (roblox.Hwnd == IntPtr.Zero)
                return false;
            try
            {
                if (!TryGetMonitorBounds(roblox.Hwnd, out int mLeft, out int mTop, out int mRight, out int mBottom))
                    return false;

                bool coversMonitor = roblox.Left <= mLeft + 1 && roblox.Top <= mTop + 1
                    && roblox.Left + roblox.Width >= mRight - 1
                    && roblox.Top + roblox.Height >= mBottom - 1;
                if (!coversMonitor)
                    return false;

                int style = GetWindowLong(roblox.Hwnd, GWL_STYLE);
                bool borderless = (style & (WS_CAPTION | WS_THICKFRAME)) == 0;
                return borderless;
            }
            catch
            {
                return false;
            }
        }

        private static bool OverlayWindowExists(string marker)
        {
            var app = Application.Current;
            if (app == null)
                return false;
            foreach (Window window in app.Windows)
            {
                if (window == null)
                    continue;
                if (window.GetType().Name.Contains(marker) || (window.GetType().Namespace ?? "").Contains(marker))
                    return true;
            }
            return false;
        }

        private static string OnOff(bool value) => value ? "ON" : "off";

        private static bool TryGetMonitorBounds(IntPtr hwnd, out int left, out int top, out int right, out int bottom)
        {
            left = top = right = bottom = 0;
            IntPtr monitor = AntiAliasingInterop.MonitorFromWindow(hwnd, AntiAliasingInterop.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;
            var info = new AntiAliasingInterop.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<AntiAliasingInterop.MONITORINFOEXW>() };
            if (!AntiAliasingInterop.GetMonitorInfoW(monitor, ref info))
                return false;
            left = info.rcMonitor.Left;
            top = info.rcMonitor.Top;
            right = info.rcMonitor.Right;
            bottom = info.rcMonitor.Bottom;
            return true;
        }

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
    }
}
