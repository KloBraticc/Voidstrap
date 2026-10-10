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
    public static partial class OverlayDiagnostics
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
            RaiseHandle(handle);
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
                        RaiseHandle(handle);
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
            _registeredHandles.RemoveWhere(handle => !IsLiveHandle(handle));
            _discoveredHandles.RemoveWhere(handle => !IsLiveHandle(handle));
            var combined = new System.Collections.Generic.HashSet<IntPtr>(_registeredHandles);
            foreach (IntPtr handle in _discoveredHandles)
                combined.Add(handle);
            Volatile.Write(ref _overlayHandles, combined.ToArray());
        }

		private static bool IsLiveHandle(IntPtr handle)
		{
			if (handle == IntPtr.Zero)
				return false;
			return Voidstrap.Utility.Platform.IsLinux
				? Voidstrap.Platform.Linux.LinuxWindowInterop.IsPreparedOverlayWindow(handle)
					|| Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(handle)
				: IsWindow(handle);
		}

		private static void RaiseHandle(IntPtr handle)
		{
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				if (Voidstrap.Platform.Linux.LinuxWindowInterop.IsPreparedOverlayWindow(handle))
					Voidstrap.Platform.Linux.LinuxWindowInterop.TryRaiseWindow(handle);
				else
					Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetAlwaysOnTop(handle);
				return;
			}
			SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
		}

        public static bool IsOverlayHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                return false;
            foreach (IntPtr overlay in Volatile.Read(ref _overlayHandles))
				if (overlay == handle || Voidstrap.Utility.Platform.IsLinux
					&& Voidstrap.Platform.Linux.LinuxWindowInterop.IsSameOrDescendantWindow(handle, overlay))
                    return true;
            return false;
        }

        private static bool IsOverlayWindow(Window window)
        {
            string ns = window.GetType().Namespace ?? "";
            string name = window.GetType().Name;
            return ns.Contains("Overlay")
                || name.Contains("Overlay")
                || name.Contains("Crosshair")
                || name.Contains("Cursor");
        }

        public static string BuildReport(ActivityWatcher? activity = null)
        {
            var app = Application.Current;
            if (app != null && !app.Dispatcher.CheckAccess())
                throw new InvalidOperationException(Text("DispatcherRequired"));

            var sb = new StringBuilder();
            var prop = App.Settings.Prop;
            RobloxWindowRect roblox = ResolveRobloxRect();
            sb.AppendLine(Text("Title"));
            sb.AppendLine(string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("Sample"), DateTimeOffset.Now));
            sb.AppendLine(Text("Scope"));
            sb.AppendLine();
            sb.AppendLine(Text("Configuration"));
            AppendValue(sb, "Stats", OnOff(Voidstrap.UI.Elements.Overlay.OverlayWindow.SurfaceRequired));
            AppendValue(sb, "Crosshair", OnOff(prop.Crosshair));
            AppendValue(sb, "GameEffects", OnOff(OverlaySettings.GameEffectsEnabled));
            AppendValue(sb, "Homepage", OnOff(OverlaySettings.HomepageBackgroundEnabled));
            AppendValue(sb, "FakeExclusive", OnOff(prop.FakeExclusiveFullscreen));
            AppendValue(sb, "FakeBorderless", OnOff(prop.FakeBorderlessFullscreen));
            sb.AppendLine();
            sb.AppendLine(Text("Runtime"));
            AppendValue(sb, "GameSession", activity == null ? Text("Unavailable") : OnOff(activity.InGame));
            AppendValue(sb, "Place", activity?.Data.PlaceId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Text("Unavailable"));
            AppendValue(sb, "Teleporting", activity == null ? Text("Unavailable") : OnOff(activity.IsTeleporting));
            AppendValue(sb, "GameSignal", OnOff(OverlayHub.InGame));
            AppendValue(sb, "Transition", OnOff(OverlayHub.GameTransition));
            AppendValue(sb, "Worker", OnOff(OverlayHub.WorkerRunning));
            AppendValue(sb, "Compositor", OnOff(OverlayHub.CompositorLive));
            AppendValue(sb, "CompositeCrosshair", OnOff(OverlayHub.CompositorCrosshairActive));
            if (Voidstrap.Utility.Platform.IsLinux)
            {
                AppendValue(sb, "X11", OnOff(Voidstrap.Platform.Linux.LinuxWindowInterop.IsAvailable));
                AppendValue(sb, "X11Compositor", OnOff(Voidstrap.Platform.Linux.LinuxWindowInterop.HasActiveX11Compositor));
                AppendValue(sb, "HomepageWorker", OnOff(OverlayHub.LinuxHomepageRunning));
                AppendValue(sb, "GameLease", OnOff(OverlayHub.LinuxGameplayLeaseOperational));
            }
            sb.AppendLine();
            sb.AppendLine(Text("RobloxWindow"));
            AppendValue(sb, "UsableRect", OnOff(roblox.Valid));
            AppendValue(sb, "Handle", FormatHandle(roblox.Hwnd));
            if (roblox.Hwnd != IntPtr.Zero && IsLiveHandle(roblox.Hwnd))
            {
                AppendValue(sb, "Foreground", OnOff(roblox.Foreground));
                AppendValue(sb, "Bounds", $"{roblox.Width} × {roblox.Height}, {roblox.Left}, {roblox.Top}");
                if (Voidstrap.Utility.Platform.IsWindows)
                {
                    AppendValue(sb, "NativeVisible", OnOff(NativeIsWindowVisible(roblox.Hwnd)));
                    AppendValue(sb, "Minimized", OnOff(NativeIsIconic(roblox.Hwnd)));
                    AppendValue(sb, "MonitorCoverage", OnOff(IsBorderlessMonitorWindow(roblox)));
                }
            }
            sb.AppendLine(Text("FullscreenUnknown"));
            sb.AppendLine();
            sb.AppendLine(Text("Windows"));
            int statsCount = 0;
            int crosshairCount = 0;
            IntPtr[] registered = Volatile.Read(ref _overlayHandles);
            var observed = new System.Collections.Generic.HashSet<IntPtr>();
            if (app != null)
            {
                foreach (Window window in app.Windows)
                {
                    if (!IsOverlayWindow(window))
                        continue;
                    if (window is Voidstrap.UI.Elements.Overlay.OverlayWindow)
                        statsCount++;
                    if (window is Voidstrap.UI.Elements.Crosshair.CrosshairWindow)
                        crosshairCount++;
                    IntPtr handle = new WindowInteropHelper(window).Handle;
                    observed.Add(handle);
                    sb.AppendLine(window.GetType().Name + ": " + FormatHandle(handle));
                    AppendValue(sb, "Visible", OnOff(window.IsVisible));
                    AppendValue(sb, "Opacity", window.Opacity.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture));
                    AppendValue(sb, "WpfTopmost", OnOff(window.Topmost));
                    AppendNativeWindow(sb, handle, roblox);
                }
            }
            IntPtr compositor = OverlayHub.CompositorWindow;
            if (compositor != IntPtr.Zero)
            {
                observed.Add(compositor);
                sb.AppendLine(Text("NativeCompositor") + ": " + FormatHandle(compositor));
                AppendNativeWindow(sb, compositor, roblox);
            }
            foreach (IntPtr handle in registered)
            {
                if (observed.Contains(handle))
                    continue;
                sb.AppendLine(Text("RegisteredWindow") + ": " + FormatHandle(handle));
                AppendNativeWindow(sb, handle, roblox);
            }
            if (observed.Count == 0 && registered.Length == 0)
                sb.AppendLine(Text("NoWindows"));
            sb.AppendLine();
            sb.AppendLine(Text("Findings"));
            if (!Voidstrap.UI.Elements.Overlay.OverlayWindow.SurfaceRequired && !prop.Crosshair && !OverlaySettings.GameEffectsEnabled && !OverlaySettings.HomepageBackgroundEnabled)
                sb.AppendLine(Text("Disabled"));
            if (!roblox.Valid)
                sb.AppendLine(Text("NoUsableWindow"));
            else if (!roblox.Foreground)
                sb.AppendLine(Text("Unfocused"));
            if (Voidstrap.UI.Elements.Overlay.OverlayWindow.SurfaceRequired && statsCount == 0)
                sb.AppendLine(Text("MissingStats"));
            if (prop.Crosshair && crosshairCount == 0 && !OverlayHub.CompositorCrosshairActive)
                sb.AppendLine(Text("MissingCrosshair"));
            if (OverlayHub.InGame && OverlayHub.LinuxHomepageRunning)
                sb.AppendLine(Text("HomepageInGame"));
            if (Voidstrap.Utility.Platform.IsLinux && !Voidstrap.Platform.Linux.LinuxWindowInterop.IsAvailable)
                sb.AppendLine(Text("X11Unavailable"));
            sb.AppendLine(Text("PixelsUnknown"));
            return sb.ToString();
        }

        private static string Text(string key) => Voidstrap.Resources.Strings.ResourceManager.GetString("OverlayDiagnostics." + key, Voidstrap.Resources.Strings.Culture) ?? key;

        private static void AppendValue(StringBuilder sb, string key, string value) => sb.AppendLine("  " + Text(key) + ": " + value);

        private static string FormatHandle(IntPtr handle) => "0x" + handle.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture);

        private static void AppendNativeWindow(StringBuilder sb, IntPtr handle, RobloxWindowRect roblox)
        {
            if (Voidstrap.Utility.Platform.IsLinux && !Voidstrap.Platform.Linux.LinuxWindowInterop.IsAvailable)
            {
                AppendValue(sb, "NativeLive", Text("Unavailable"));
                AppendValue(sb, "NativeVisible", Text("Unavailable"));
                AppendValue(sb, "NativeTopmost", Text("Unavailable"));
                return;
            }
            bool live = Voidstrap.Utility.Platform.IsLinux
                ? Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(handle)
                : IsLiveHandle(handle);
            AppendValue(sb, "NativeLive", OnOff(live));
            if (!live)
                return;
            if (!Voidstrap.Utility.Platform.IsWindows)
            {
                AppendValue(sb, "NativeVisible", Text("Unavailable"));
                AppendValue(sb, "NativeTopmost", Text("Unavailable"));
                return;
            }
            AppendValue(sb, "NativeVisible", OnOff(NativeIsWindowVisible(handle)));
            AppendValue(sb, "Minimized", OnOff(NativeIsIconic(handle)));
            int? style = GetWindowLong(handle, GWL_EXSTYLE);
            AppendValue(sb, "NativeTopmost", OnOff(style.HasValue ? (style.Value & WS_EX_TOPMOST) != 0 : null));
            if (AntiAliasingInterop.GetWindowRect(handle, out AntiAliasingInterop.RECT rect))
            {
                AppendValue(sb, "Bounds", $"{rect.Right - rect.Left} × {rect.Bottom - rect.Top}, {rect.Left}, {rect.Top}");
                if (roblox.Valid)
                    AppendValue(sb, "Intersects", OnOff(rect.Right > roblox.Left && rect.Bottom > roblox.Top
                        && rect.Left < roblox.Left + roblox.Width && rect.Top < roblox.Top + roblox.Height));
            }
        }
        private static RobloxWindowRect ResolveRobloxRect()
        {
            RobloxWindowRect tracked = RobloxWindowTracker.Current;
            if (!tracked.Valid)
                return tracked;
            bool live = Voidstrap.Utility.Platform.IsLinux
                ? Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(tracked.Hwnd)
                : IsLiveHandle(tracked.Hwnd) && NativeIsWindowVisible(tracked.Hwnd) && !NativeIsIconic(tracked.Hwnd);
            return live ? tracked : new RobloxWindowRect(tracked.Hwnd, 0, 0, 0, 0, false, false);
        }
        private static bool? IsBorderlessMonitorWindow(RobloxWindowRect roblox)
        {
            if (roblox.Hwnd == IntPtr.Zero)
                return null;
            try
            {
                if (!TryGetMonitorBounds(roblox.Hwnd, out int mLeft, out int mTop, out int mRight, out int mBottom))
                    return null;

                bool coversMonitor = roblox.Left <= mLeft + 1 && roblox.Top <= mTop + 1
                    && roblox.Left + roblox.Width >= mRight - 1
                    && roblox.Top + roblox.Height >= mBottom - 1;
                if (!coversMonitor)
                    return false;

                int? style = GetWindowLong(roblox.Hwnd, GWL_STYLE);
                return style.HasValue ? (style.Value & (WS_CAPTION | WS_THICKFRAME)) == 0 : null;
            }
            catch
            {
                return null;
            }
        }

        private static string OnOff(bool? value) => Text(value.HasValue ? value.Value ? "Yes" : "No" : "Unavailable");

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

        [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool NativeIsWindowVisible(IntPtr hwnd);

        [LibraryImport("user32.dll", EntryPoint = "IsIconic")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool NativeIsIconic(IntPtr hwnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongA", SetLastError = true)]
        private static partial int NativeGetWindowLong(IntPtr hwnd, int index);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool NativeSetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "IsWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool NativeIsWindow(IntPtr hwnd);

        private static int? GetWindowLong(IntPtr hwnd, int index)
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return null;
            Marshal.SetLastPInvokeError(0);
            int value = NativeGetWindowLong(hwnd, index);
            return value != 0 || Marshal.GetLastPInvokeError() == 0 ? value : null;
        }

        private static bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags)
        {
            return Voidstrap.Utility.Platform.IsWindows && NativeSetWindowPos(hwnd, insertAfter, x, y, cx, cy, flags);
        }

        private static bool IsWindow(IntPtr hwnd)
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return hwnd != IntPtr.Zero;

            return NativeIsWindow(hwnd);
        }
    }
}
