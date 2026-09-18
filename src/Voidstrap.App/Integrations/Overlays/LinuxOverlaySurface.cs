using System;

using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
#if CROSSPLAT
using System.Windows.Media.ProGPU;
#endif
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations.Overlays
{
    internal static class LinuxOverlaySurface
    {
        public static bool IsSupported => Voidstrap.Utility.Platform.IsLinux && LinuxWindowInterop.IsAvailable;

		private static readonly ConditionalWeakTable<Window, PassivePreparation> PassivePreparations = new();

		private static readonly ConditionalWeakTable<Window, object> OverlayWindows = new();

		public static void ReleaseMainWindowClaim(Window window)
		{
			if (window == null)
				return;

			OverlayWindows.AddOrUpdate(window, OverlayMarker);
		}

		public static bool IsOverlayWindow(Window window)
		{
			return window != null && OverlayWindows.TryGetValue(window, out _);
		}

		private static readonly object OverlayMarker = new();

        public static void MakeClickThrough(Window window, int cornerRadius = 0)
        {
            if (window == null || !IsSupported)
                return;

			Voidstrap.UI.LinuxTextGuard.SetPreserveCompactLayout(window, true);
			PassivePreparation preparation = PassivePreparations.GetValue(window, value => new PassivePreparation(value, cornerRadius));
			preparation.SetCornerRadius(cornerRadius);
			preparation.Start();
        }

		public static bool ApplyRoundedShape(Window window, int cornerRadius)
		{
			if (window == null || !IsSupported || cornerRadius <= 0)
				return false;

			return ApplyRoundedShape(window, ResolveHandle(window), cornerRadius);
		}

		public static bool TryMakeClickThroughNow(Window window)
		{
			return TryMakeClickThroughNow(window, out _);
		}

		internal static bool TryMakeClickThroughNow(Window window, out nint handle)
		{
			handle = 0;
			if (window == null || !IsSupported)
				return false;
			Voidstrap.UI.LinuxTextGuard.SetPreserveCompactLayout(window, true);
			return TryMakeClickThrough(window, out handle);
		}

        public static void KeepAbove(Window window)
        {
            if (window == null || !IsSupported)
                return;

            try
            {
                nint handle = ResolveHandle(window);
				if (handle != 0)
				{
					if (LinuxWindowInterop.IsPreparedOverlayWindow(handle))
						LinuxWindowInterop.TryRaiseWindow(handle);
					else
						LinuxWindowInterop.TrySetAlwaysOnTop(handle);
				}
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("LinuxOverlaySurface", "The overlay could not be kept on top: " + ex.Message);
            }
        }

		public static bool PrepareInteractive(Window window)
		{
			return PrepareInteractive(window, ResolveHandle(window));
		}

		public static bool PrepareInteractive(Window window, nint handle)
		{
			if (window == null || !IsSupported)
				return false;

			try
			{
				Voidstrap.UI.LinuxTextGuard.SetPreserveCompactLayout(window, true);
				if (handle == 0 || !LinuxWindowInterop.TryPrepareOverlayWindow(handle))
					return false;
				OverlayDiagnostics.RegisterOverlayHandle(handle);
				LinuxWindowInterop.TrySetAlwaysOnTop(handle);
				App.Logger?.WriteLine("LinuxOverlaySurface", "Prepared unmanaged interactive overlay " + window.Title);
				return true;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("LinuxOverlaySurface", "The interactive overlay could not be prepared: " + ex.Message);
				return false;
			}
		}

		public static bool WakePresentation(Window window)
		{
			if (window == null || !IsSupported)
				return false;

#if CROSSPLAT
			try
			{
				return ProGpuWpfDiagnostics.TryRequestRender(window);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("LinuxOverlaySurface", "The overlay presentation loop could not be awakened: " + ex.Message);
				return false;
			}
#else
			return false;
#endif
		}

		public static void Focus(Window window)
		{
			if (window == null || !IsSupported)
				return;
			nint handle = ResolveHandle(window);
			if (handle != 0)
				LinuxWindowInterop.TryFocusWindow(handle);
		}

		private sealed class PassivePreparation
		{
			private const int MaxAttempts = 50;
			private readonly Window _window;
			private System.Windows.Threading.DispatcherTimer? _timer;
			private nint _handle;
			private int _attempts;
			private int _started;
			private int _cornerRadius;

			public PassivePreparation(Window window, int cornerRadius)
			{
				_window = window;
				_cornerRadius = Math.Max(0, cornerRadius);
			}

			public void SetCornerRadius(int cornerRadius)
			{
				_cornerRadius = Math.Max(_cornerRadius, cornerRadius);
			}

			public void Start()
			{
				if (Interlocked.Exchange(ref _started, 1) != 0)
					return;
				_window.Closed += OnClosed;
				if (TryApply())
					return;
				_timer = new System.Windows.Threading.DispatcherTimer
				{
					Interval = TimeSpan.FromMilliseconds(100)
				};
				_timer.Tick += OnTick;
				_timer.Start();
			}

			private void OnTick(object? sender, EventArgs e)
			{
				_attempts++;
				if (TryApply())
				{
					StopTimer();
					return;
				}
				if (_attempts < MaxAttempts)
					return;
				StopTimer();
				App.Logger?.WriteLine("LinuxOverlaySurface", "Could not enable input passthrough for " + _window.Title);
			}

			private bool TryApply()
			{
				if (!TryMakeClickThrough(_window, out nint handle))
					return false;
				_handle = handle;
				if (_cornerRadius > 0)
					ApplyRoundedShape(_window, handle, _cornerRadius);
				return true;
			}

			private void OnClosed(object? sender, EventArgs e)
			{
				StopTimer();
				_window.Closed -= OnClosed;
				if (_handle != 0)
					OverlayDiagnostics.UnregisterOverlayHandle(_handle);
				PassivePreparations.Remove(_window);
			}

			private void StopTimer()
			{
				if (_timer == null)
					return;
				_timer.Stop();
				_timer.Tick -= OnTick;
				_timer = null;
			}
		}

        private static bool TryMakeClickThrough(Window window, out nint handle)
        {
			handle = 0;
            try
            {
				handle = ResolveHandle(window);
                if (handle == 0)
                    return false;

				if (!LinuxWindowInterop.TryPrepareOverlayWindow(handle))
					LinuxWindowInterop.TrySetOverlayWindowType(handle);
				if (!LinuxWindowInterop.TrySetClickThrough(handle))
					return false;
				OverlayDiagnostics.RegisterOverlayHandle(handle);
                LinuxWindowInterop.TrySetAlwaysOnTop(handle);
				App.Logger?.WriteLine("LinuxOverlaySurface", "Input passthrough applied to " + window.Title);
                return true;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("LinuxOverlaySurface", "Input passthrough could not be applied: " + ex.Message);
				handle = 0;
				return false;
            }
        }

        private static nint ResolveHandle(Window window)
        {
            try
            {
                nint handle = new WindowInteropHelper(window).Handle;
                if (handle != 0 && LinuxWindowInterop.IsLiveWindow(handle))
                    return handle;
            }
            catch (Exception)
            {
            }

            return string.IsNullOrWhiteSpace(window.Title) ? 0 : LinuxWindowInterop.FindOwnWindowByTitle(window.Title);
        }

		private static bool ApplyRoundedShape(Window window, nint handle, int cornerRadius)
		{
			if (handle == 0
				|| !LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out int width, out int height)
				|| width <= 0
				|| height <= 0)
				return false;

			double logicalWidth = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
			double scale = double.IsFinite(logicalWidth) && logicalWidth > 0 ? width / logicalWidth : 1;
			int radius = Math.Max(1, (int)Math.Round(cornerRadius * Math.Max(0.5, scale)));
			return LinuxWindowInterop.TrySetRoundedCorners(handle, width, height, radius);
		}
    }
}
