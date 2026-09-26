using System.Runtime.CompilerServices;
using System;
using System.Windows;
using System.Windows.Media;

namespace Voidstrap.UI;

public static class RoundedWindowChrome
{
	public const double CornerRadius = 8.0;

	private const double ContentWidthTolerance = 0.5;

	private static bool _installed;
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> LinuxIdentityRetries = new();
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, object> ContentWidthPending = new();
	private static readonly object PendingMarker = new();

	public static void Install()
	{
		if (_installed || Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		_installed = true;
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
	}

	public static void Prepare(Window window)
	{
		if (window == null || Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		try
		{
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				window.SourceInitialized -= OnLinuxWindowReady;
				window.SourceInitialized += OnLinuxWindowReady;
				window.Activated -= OnLinuxWindowReady;
				window.Activated += OnLinuxWindowReady;
				window.ContentRendered -= OnLinuxWindowReady;
				window.ContentRendered += OnLinuxWindowReady;
				EnsureLinuxIdentity(window);
			}
			ApplyTransparentStyle(window);
			window.Initialized -= OnWindowInitialized;
			window.Initialized += OnWindowInitialized;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RoundedWindowChrome::Prepare", "Could not enable transparency: " + ex.Message);
		}
	}

	internal static void Refresh(Window window)
	{
		ApplyContentWidth(window);
		ApplyClip(window);
		ApplyNativeRounding(window);
		window.InvalidateMeasure();
		window.InvalidateVisual();
	}

	private static void OnWindowInitialized(object? sender, EventArgs e)
	{
		if (sender is not Window window)
		{
			return;
		}

		window.Initialized -= OnWindowInitialized;
		ApplyTransparentStyle(window);
	}

	private static void ApplyTransparentStyle(Window window)
	{
		try
		{
			if (window.WindowStyle != WindowStyle.None)
			{
				window.WindowStyle = WindowStyle.None;
			}

			if (!window.AllowsTransparency)
			{
				window.AllowsTransparency = true;

				if (Voidstrap.Utility.Platform.IsLinux && !window.AllowsTransparency)
				{
					App.Logger?.WriteLine(
						"RoundedWindowChrome::ApplyTransparentStyle",
						"AllowsTransparency did not take effect for " + window.Title + ", the platform ignored it");
				}
			}
		}
		catch (InvalidOperationException ex)
		{
			App.Logger?.WriteLine(
				"RoundedWindowChrome::ApplyTransparentStyle",
				"Transparency could not be enabled for " + window.Title + ": " + ex.Message);
		}
	}

	private static void OnWindowLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not Window window)
		{
			return;
		}
		if (Voidstrap.Utility.Platform.IsLinux && IsOverlaySurface(window))
		{
			window.ShowActivated = false;
			window.ShowInTaskbar = false;
			LinuxTextGuard.SetPreserveCompactLayout(window, true);
			return;
		}
		ConstrainContentWidth(window);
		ApplyStartupLocation(window);
		ApplyClip(window);
		EnsureLinuxIdentity(window);
		LinuxTitleBar.Apply(window);
		window.SizeChanged -= OnWindowSizeChanged;
		window.SizeChanged += OnWindowSizeChanged;
		window.StateChanged -= OnWindowStateChanged;
		window.StateChanged += OnWindowStateChanged;
		window.Closed -= OnWindowClosed;
		window.Closed += OnWindowClosed;
		if (window.ResizeMode == ResizeMode.CanResize || window.ResizeMode == ResizeMode.CanResizeWithGrip)
		{
			WindowEdgeResizer.Attach(window);
		}
	}

	internal static bool IsOverlaySurface(Window window)
	{
		if (Voidstrap.Integrations.Overlays.LinuxOverlaySurface.IsOverlayWindow(window))
			return true;
		string name = window.GetType().Name;
		string area = window.GetType().Namespace ?? string.Empty;
		return area.Contains(".Overlay", System.StringComparison.Ordinal)
			|| area.Contains(".Crosshair", System.StringComparison.Ordinal)
			|| name.Contains("Overlay", System.StringComparison.Ordinal)
			|| name.Contains("Crosshair", System.StringComparison.Ordinal);
	}

	private static async System.Threading.Tasks.Task ApplyLinuxIdentityAfterMappingAsync(Window window)
	{
		try
		{
			for (int attempt = 0; attempt < 60; attempt++)
			{
				await System.Threading.Tasks.Task.Delay(50).ConfigureAwait(false);
				if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
					return;

				bool applied = await window.Dispatcher.InvokeAsync(() => ApplyLinuxIdentity(window), System.Windows.Threading.DispatcherPriority.Loaded);
				if (applied)
					return;
			}
		}
		finally
		{
			LinuxIdentityRetries.Remove(window);
		}
	}

	private static void EnsureLinuxIdentity(Window window)
	{
		if (ApplyLinuxIdentity(window) || LinuxIdentityRetries.TryGetValue(window, out _))
			return;

		LinuxIdentityRetries.Add(window, new object());
		_ = ApplyLinuxIdentityAfterMappingAsync(window);
	}

	private static void OnLinuxWindowReady(object? sender, EventArgs e)
	{
		if (sender is Window window)
			ApplyLinuxIdentity(window);
	}

	private static readonly ConditionalWeakTable<Window, object> ClipReported = new();

	private static readonly ConditionalWeakTable<Window, object> LinuxIdentityApplied = new();

	private static readonly ConditionalWeakTable<Window, StrongBox<nint>> ShadowHandles = new();

	private static readonly ConditionalWeakTable<Window, System.Windows.Threading.DispatcherTimer> ShapeSyncTimers = new();

	private const int ShapeSyncAttempts = 20;

	private static readonly TimeSpan ShapeSyncInterval = TimeSpan.FromMilliseconds(30);

	private static bool ApplyLinuxIdentity(Window window)
	{
		if (LinuxIdentityApplied.TryGetValue(window, out _))
			return true;

		if (!LinuxApplicationIdentity.Apply(window))
			return false;

		LinuxIdentityApplied.Add(window, new object());
		ApplyNativeRounding(window);
		window.SourceInitialized -= OnLinuxWindowReady;
		window.Activated -= OnLinuxWindowReady;
		window.ContentRendered -= OnLinuxWindowReady;
		return true;
	}

	private static void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
	{
		ApplyClip(sender as Window);
		if (sender is Window window)
		{
			QueueContentWidth(window);
			QueueNativeShapeSync(window);
		}
	}

	private static void QueueNativeShapeSync(Window window)
	{
		if (!Voidstrap.Utility.Platform.IsLinux
			|| window.Dispatcher.HasShutdownStarted
			|| window.Dispatcher.HasShutdownFinished
			|| ShapeSyncTimers.TryGetValue(window, out _))
		{
			return;
		}

		int attempts = 0;
		System.Windows.Threading.DispatcherTimer timer = new(System.Windows.Threading.DispatcherPriority.Background, window.Dispatcher)
		{
			Interval = ShapeSyncInterval
		};
		timer.Tick += (_, _) =>
		{
			if (!NativeSizeMatches(window) && ++attempts < ShapeSyncAttempts)
			{
				return;
			}
			timer.Stop();
			ShapeSyncTimers.Remove(window);
			ApplyNativeRounding(window);
		};
		ShapeSyncTimers.Add(window, timer);
		timer.Start();
	}

	private static bool NativeSizeMatches(Window window)
	{
		nint handle = LinuxWindowMode.ResolveNativeWindow(window);
		if (handle == 0 || !Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(handle, out _, out _, out int width, out int height))
		{
			return true;
		}
		double scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
		if (scale <= 0.0 || double.IsNaN(scale))
			scale = 1.0;
		return Math.Abs(width - window.ActualWidth * scale) <= 1.0 && Math.Abs(height - window.ActualHeight * scale) <= 1.0;
	}

	private static void OnWindowStateChanged(object? sender, EventArgs e)
	{
		if (sender is not Window window)
		{
			return;
		}

		ApplyContentWidth(window);
		if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
		{
			return;
		}

		window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)delegate
		{
			ApplyClip(window);
			ApplyContentWidth(window);
		});
	}

	private static void OnWindowClosed(object? sender, EventArgs e)
	{
		if (sender is not Window window)
		{
			return;
		}
		window.SizeChanged -= OnWindowSizeChanged;
		window.StateChanged -= OnWindowStateChanged;
		window.SourceInitialized -= OnLinuxWindowReady;
		window.Activated -= OnLinuxWindowReady;
		window.ContentRendered -= OnLinuxWindowReady;
		LinuxIdentityRetries.Remove(window);
		if (ShadowHandles.TryGetValue(window, out StrongBox<nint>? shadow))
		{
			Voidstrap.Platform.Linux.LinuxWindowShadow.Forget(shadow.Value);
			ShadowHandles.Remove(window);
		}
		window.Closed -= OnWindowClosed;
	}

	private static void ApplyStartupLocation(Window window)
	{
		try
		{
			if (window.WindowStartupLocation == WindowStartupLocation.Manual)
			{
				return;
			}
			if (window.WindowState == System.Windows.WindowState.Maximized)
			{
				return;
			}
			double width = window.ActualWidth > 0.0 ? window.ActualWidth : window.Width;
			double height = window.ActualHeight > 0.0 ? window.ActualHeight : window.Height;
			if (double.IsNaN(width) || double.IsNaN(height) || width <= 0.0 || height <= 0.0)
			{
				return;
			}

			double left;
			double top;
			Window? owner = window.Owner;
			if (window.WindowStartupLocation == WindowStartupLocation.CenterOwner
				&& owner != null && owner.ActualWidth > 0.0 && !double.IsNaN(owner.Left) && !double.IsNaN(owner.Top))
			{
				left = owner.Left + (owner.ActualWidth - width) / 2.0;
				top = owner.Top + (owner.ActualHeight - height) / 2.0;
			}
			else
			{
				Rect centerArea = Voidstrap.Utility.ScreenMetrics.WorkArea;
				left = centerArea.Left + (centerArea.Width - width) / 2.0;
				top = centerArea.Top + (centerArea.Height - height) / 2.0;
			}

			Rect area = Voidstrap.Utility.ScreenMetrics.WorkArea;
			left = Math.Max(area.Left, Math.Min(left, area.Right - width));
			top = Math.Max(area.Top, Math.Min(top, area.Bottom - height));

			window.Left = left;
			window.Top = top;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RoundedWindowChrome::ApplyStartupLocation", "Could not position window: " + ex.Message);
		}
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, System.Runtime.CompilerServices.StrongBox<double>> ContentWidths = new();

	private static void ApplyContentWidth(Window? window)
	{
		if (window?.Content is not FrameworkElement root)
		{
			return;
		}

		if (!ContentWidths.TryGetValue(root, out System.Runtime.CompilerServices.StrongBox<double>? stored))
		{
			return;
		}

		if (IsMaximizedOrFullscreen(window))
		{
			SetContentWidth(root, double.PositiveInfinity);
			return;
		}

		double available = window.ActualWidth;
		if (double.IsNaN(available) || available <= 0.0)
		{
			available = stored.Value;
		}

		SetContentWidth(root, available);
	}

	private static void SetContentWidth(FrameworkElement root, double width)
	{
		double current = root.MaxWidth;
		if (double.IsPositiveInfinity(width))
		{
			if (double.IsPositiveInfinity(current))
			{
				return;
			}
		}
		else if (!double.IsNaN(current) && !double.IsPositiveInfinity(current) && Math.Abs(current - width) < ContentWidthTolerance)
		{
			return;
		}

		root.MaxWidth = width;
	}

	private static void QueueContentWidth(Window window)
	{
		if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
		{
			return;
		}

		if (ContentWidthPending.TryGetValue(window, out _))
		{
			return;
		}

		ContentWidthPending.Add(window, PendingMarker);
		window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)delegate
		{
			ContentWidthPending.Remove(window);
			ApplyContentWidth(window);
		});
	}

	private static void ConstrainContentWidth(Window window)
	{
		try
		{
			if (window.SizeToContent != SizeToContent.Height && window.SizeToContent != SizeToContent.Manual)
			{
				return;
			}
			double target = window.Width;
			if (double.IsNaN(target) || target <= 0.0)
			{
				return;
			}
			if (window.Content is FrameworkElement root && (double.IsNaN(root.MaxWidth) || root.MaxWidth > target))
			{
				ContentWidths.Remove(root);
				ContentWidths.Add(root, new System.Runtime.CompilerServices.StrongBox<double>(target));
				ApplyContentWidth(window);
			}
		}
		catch
		{
		}
	}

	private static void ApplyNativeRounding(Window window)
	{
		if (!Voidstrap.Utility.Platform.IsLinux || IsOverlaySurface(window))
		{
			return;
		}

		try
		{
			double scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
			if (scale <= 0.0 || double.IsNaN(scale))
				scale = 1.0;
			double width = window.ActualWidth * scale;
			double height = window.ActualHeight * scale;

			nint handle = LinuxWindowMode.ResolveNativeWindow(window);

			if (handle == 0)
			{
				return;
			}

			Voidstrap.Platform.Linux.LinuxWindowInterop.TryResetInputShape(handle);

			bool report = !ClipReported.TryGetValue(window, out _);
			if (report)
			{
				ClipReported.Add(window, new object());
			}

			if (IsMaximizedOrFullscreen(window) || width <= 0.0 || height <= 0.0)
			{
				Voidstrap.Platform.Linux.LinuxWindowInterop.TryClearShape(handle);
				TrackShadow(window, handle, 0, scale, true);
				return;
			}

			int radius = (int)Math.Round(CornerRadius * scale);
			bool rounded = Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetRoundedCorners(
				handle,
				(int)Math.Round(width),
				(int)Math.Round(height),
				radius);
			TrackShadow(window, handle, rounded ? radius : 0, scale, false);

			if (report)
			{
				App.Logger?.WriteLine(
					"RoundedWindowChrome::ApplyNativeRounding",
					"Shaped " + window.Title + " at 0x" + handle.ToString("x") + " size " + (int)width + "x" + (int)height + " result=" + rounded);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RoundedWindowChrome::ApplyNativeRounding", "Native rounding failed: " + ex.Message);
		}
	}

	private static void TrackShadow(Window window, nint handle, int radius, double scale, bool suppressed)
	{
		if (Voidstrap.Utility.LinuxStartup.SafeMode)
		{
			return;
		}

		if (ShadowHandles.TryGetValue(window, out StrongBox<nint>? tracked))
		{
			if (tracked.Value != handle)
			{
				Voidstrap.Platform.Linux.LinuxWindowShadow.Forget(tracked.Value);
				tracked.Value = handle;
			}
		}
		else
		{
			ShadowHandles.Add(window, new StrongBox<nint>(handle));
		}

		Voidstrap.Platform.Linux.LinuxWindowShadow.Track(handle, radius, scale, suppressed, ReportShadow);
	}

	private static void ReportShadow(string message)
	{
		App.Logger?.WriteLine("LinuxWindowShadow", message);
	}

	private static void ApplyClip(Window? window)
	{
		if (window == null)
		{
			return;
		}
		if (!window.AllowsTransparency)
		{
			window.Clip = null;
			ApplyNativeRounding(window);
			return;
		}
		double width = window.ActualWidth;
		double height = window.ActualHeight;
		if (IsMaximizedOrFullscreen(window) || width <= 0.0 || height <= 0.0)
		{
			window.Clip = null;
			return;
		}
		RectangleGeometry geometry = new(new Rect(0.0, 0.0, width, height), CornerRadius, CornerRadius);
		geometry.Freeze();
		window.Clip = geometry;


	}

	private static bool IsMaximizedOrFullscreen(Window window)
	{
		if (LinuxWindowMode.IsFullscreen(window))
			return true;
		return Voidstrap.Utility.Platform.IsLinux
			? LinuxWindowMode.IsCompositorMaximized(window)
			: window.WindowState == System.Windows.WindowState.Maximized;
	}
}
