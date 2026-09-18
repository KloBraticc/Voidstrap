using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Platform.Linux;
using Wpf.Ui.Controls;

namespace Voidstrap.UI;

internal static class LinuxWindowMode
{
	private sealed class WindowModeState
	{
		public bool Fullscreen;
		public int Generation;
		public int MaximizeGeneration;
		public bool ApplyingManagedState;
		public bool MaximizeFallback;
		public nint NativeWindow;
		public ResizeMode ResizeMode;
		public System.Windows.WindowState WindowState;
		public Rect RestoreBounds;
		public bool NormalGeometryValid;
		public int NormalLeft;
		public int NormalTop;
		public int NormalWidth;
		public int NormalHeight;
		public bool FullscreenGeometryValid;
		public int FullscreenLeft;
		public int FullscreenTop;
		public int FullscreenWidth;
		public int FullscreenHeight;
		public readonly List<(TitleBar TitleBar, Visibility Visibility)> TitleBars = [];
	}

	private static readonly ConditionalWeakTable<Window, WindowModeState> States = new();

	public static void Attach(Window window)
	{
		if (!OperatingSystem.IsLinux() || IsOverlaySurface(window) || States.TryGetValue(window, out _))
			return;

		States.Add(window, new WindowModeState());
		window.PreviewKeyDown += OnPreviewKeyDown;
		window.Loaded += OnLoaded;
		window.StateChanged += OnStateChanged;
		window.Closed += OnClosed;
	}

	public static bool IsFullscreen(Window? window)
	{
		return window != null && States.TryGetValue(window, out WindowModeState? state) && state.Fullscreen;
	}

	public static bool IsMaximized(Window window)
	{
		if (IsFullscreen(window))
			return false;
		nint nativeWindow = ResolveNativeWindow(window);
		return nativeWindow != 0
			? IsMaximizedSurface(nativeWindow)
			: window.WindowState == System.Windows.WindowState.Maximized;
	}

	public static bool IsCompositorMaximized(Window window)
	{
		if (!OperatingSystem.IsLinux() || IsFullscreen(window))
			return false;
		nint nativeWindow = ResolveNativeWindow(window);
		return nativeWindow != 0 && IsMaximizedSurface(nativeWindow);
	}

	public static void ToggleMaximize(Window window)
	{
		if (!OperatingSystem.IsLinux() || window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
			return;
		if (IsFullscreen(window))
		{
			ExitFullscreen(window);
			return;
		}

		Attach(window);
		if (!States.TryGetValue(window, out WindowModeState? state))
			return;
		SetMaximized(window, state, !IsMaximized(window));
	}

	public static void ToggleFullscreen(Window window)
	{
		if (!OperatingSystem.IsLinux())
			return;
		Attach(window);
		if (!States.TryGetValue(window, out WindowModeState? state))
			return;
		if (state.Fullscreen)
			ExitFullscreen(window);
		else
			EnterFullscreen(window, state);
	}

	public static bool TryGetRestorePlacement(Window window, out bool maximized, out Rect bounds)
	{
		maximized = false;
		bounds = Rect.Empty;
		if (!States.TryGetValue(window, out WindowModeState? state))
			return false;
		if (state.Fullscreen)
		{
			maximized = state.WindowState == System.Windows.WindowState.Maximized;
			bounds = state.RestoreBounds;
			return !bounds.IsEmpty;
		}
		if (!IsMaximized(window) || !state.NormalGeometryValid)
			return false;
		maximized = true;
		bounds = GetNormalRestoreBounds(window, state);
		return !bounds.IsEmpty;
	}

	private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (sender is not Window window)
			return;
		if (e.Key == Key.F11)
		{
			ToggleFullscreen(window);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape && IsFullscreen(window))
		{
			ExitFullscreen(window);
			e.Handled = true;
		}
	}

	private static void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is Window window)
			RequestMaximizeSynchronization(window);
	}

	private static void OnStateChanged(object? sender, EventArgs e)
	{
		if (sender is Window window && !IsFullscreen(window))
			RequestMaximizeSynchronization(window);
	}

	private static void RequestMaximizeSynchronization(Window window)
	{
		if (window.WindowState == System.Windows.WindowState.Minimized
			|| !States.TryGetValue(window, out WindowModeState? state)
			|| state.ApplyingManagedState
			|| state.Fullscreen)
			return;
		bool maximized = window.WindowState == System.Windows.WindowState.Maximized;
		if (maximized == IsMaximized(window))
		{
			RoundedWindowChrome.Refresh(window);
			return;
		}
		SetMaximized(window, state, maximized);
	}

	private static void SetMaximized(Window window, WindowModeState state, bool maximized, bool captureNormal = true)
	{
		state.MaximizeGeneration++;
		state.NativeWindow = ResolveNativeWindow(window);
		if (maximized && captureNormal)
		{
			CaptureNormalGeometry(window, state);
			state.MaximizeFallback = false;
		}
		state.ApplyingManagedState = true;
		try
		{
			window.WindowState = System.Windows.WindowState.Normal;
		}
		finally
		{
			state.ApplyingManagedState = false;
		}
		if (state.NativeWindow != 0)
		{
			LinuxWindowInterop.TrySetMaximized(state.NativeWindow, maximized);
			if (!maximized)
				ApplyNormalGeometry(window, state, state.NativeWindow);
		}
		int generation = state.MaximizeGeneration;
		_ = SynchronizeMaximizeAsync(window, state, generation, maximized);
		LinuxTitleBar.RefreshMaximized(window, maximized);
		RoundedWindowChrome.Refresh(window);
	}

	private static async Task SynchronizeMaximizeAsync(Window window, WindowModeState state, int generation, bool maximized)
	{
		try
		{
			for (int attempt = 0; attempt < 10; attempt++)
			{
				await Task.Delay(attempt == 0 ? 1 : 40).ConfigureAwait(false);
				if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
					return;
				bool done = await window.Dispatcher.InvokeAsync(() =>
				{
					if (state.MaximizeGeneration != generation || state.Fullscreen)
						return true;
					nint nativeWindow = state.NativeWindow != 0 ? state.NativeWindow : ResolveNativeWindow(window);
					if (nativeWindow == 0)
						return false;
					state.NativeWindow = nativeWindow;
					if (maximized)
					{
						if (IsMaximizedSurface(nativeWindow))
						{
							LinuxTitleBar.RefreshMaximized(window, true);
							RoundedWindowChrome.Refresh(window);
							return true;
						}
						LinuxWindowInterop.TrySetMaximized(nativeWindow, true);
						if (attempt >= 5)
							ApplyMaximizeFallback(window, state, nativeWindow);
						return false;
					}
					bool stateCleared = !LinuxWindowInterop.IsMaximized(nativeWindow);
					bool geometryRestored = !state.NormalGeometryValid || MatchesNormalGeometry(nativeWindow, state);
					if (stateCleared && geometryRestored)
					{
						state.MaximizeFallback = false;
						LinuxTitleBar.RefreshMaximized(window, false);
						RoundedWindowChrome.Refresh(window);
						return true;
					}
					LinuxWindowInterop.TrySetMaximized(nativeWindow, false);
					ApplyNormalGeometry(window, state, nativeWindow);
					return false;
				}, DispatcherPriority.Loaded);
				if (done)
					return;
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxWindowMode::SynchronizeMaximizeAsync", "Window maximize synchronization failed: " + ex.Message);
		}
	}

	private static void CaptureNormalGeometry(Window window, WindowModeState state)
	{
		if (state.NativeWindow != 0
			&& !IsMaximizedSurface(state.NativeWindow)
			&& LinuxWindowInterop.TryGetWindowGeometry(state.NativeWindow, out int left, out int top, out int width, out int height))
		{
			state.NormalGeometryValid = true;
			state.NormalLeft = left;
			state.NormalTop = top;
			state.NormalWidth = width;
			state.NormalHeight = height;
			return;
		}
		Rect bounds = GetRestoreBounds(window);
		if (bounds.IsEmpty)
			return;
		double scale = GetScale(window);
		state.NormalGeometryValid = true;
		state.NormalLeft = (int)Math.Round(bounds.Left * scale);
		state.NormalTop = (int)Math.Round(bounds.Top * scale);
		state.NormalWidth = (int)Math.Round(bounds.Width * scale);
		state.NormalHeight = (int)Math.Round(bounds.Height * scale);
	}

	private static void ApplyMaximizeFallback(Window window, WindowModeState state, nint nativeWindow)
	{
		LinuxDisplayBounds workArea = LinuxDisplayMetrics.Refresh().WorkArea;
		if (!workArea.IsUsable)
			return;
		state.MaximizeFallback = true;
		double scale = GetScale(window);
		window.Left = workArea.Left / scale;
		window.Top = workArea.Top / scale;
		window.Width = workArea.Width / scale;
		window.Height = workArea.Height / scale;
		LinuxWindowInterop.TryMoveResize(nativeWindow, workArea.Left, workArea.Top, workArea.Width, workArea.Height);
		LinuxTitleBar.RefreshMaximized(window, true);
		RoundedWindowChrome.Refresh(window);
	}

	private static void ApplyNormalGeometry(Window window, WindowModeState state, nint nativeWindow)
	{
		if (!state.NormalGeometryValid)
			return;
		double scale = GetScale(window);
		window.Left = state.NormalLeft / scale;
		window.Top = state.NormalTop / scale;
		window.Width = state.NormalWidth / scale;
		window.Height = state.NormalHeight / scale;
		LinuxWindowInterop.TryMoveResize(nativeWindow, state.NormalLeft, state.NormalTop, state.NormalWidth, state.NormalHeight);
		LinuxTitleBar.RefreshMaximized(window, false);
	}

	private static bool MatchesNormalGeometry(nint nativeWindow, WindowModeState state)
	{
		return LinuxWindowInterop.TryGetWindowGeometry(nativeWindow, out int left, out int top, out int width, out int height)
			&& Math.Abs(left - state.NormalLeft) <= 2
			&& Math.Abs(top - state.NormalTop) <= 2
			&& Math.Abs(width - state.NormalWidth) <= 2
			&& Math.Abs(height - state.NormalHeight) <= 2;
	}

	private static bool IsMaximizedSurface(nint nativeWindow)
	{
		if (LinuxWindowInterop.IsMaximized(nativeWindow))
			return true;
		LinuxDisplayBounds workArea = LinuxDisplayMetrics.Current.WorkArea;
		return workArea.IsUsable
			&& LinuxWindowInterop.TryGetWindowGeometry(nativeWindow, out int left, out int top, out int width, out int height)
			&& Math.Abs(left - workArea.Left) <= 2
			&& Math.Abs(top - workArea.Top) <= 2
			&& Math.Abs(width - workArea.Width) <= 2
			&& Math.Abs(height - workArea.Height) <= 2;
	}

	private static void EnterFullscreen(Window window, WindowModeState state)
	{
		bool wasMaximized = IsMaximized(window);
		state.Generation++;
		state.MaximizeGeneration++;
		state.Fullscreen = true;
		state.ResizeMode = window.ResizeMode;
		state.WindowState = wasMaximized ? System.Windows.WindowState.Maximized : System.Windows.WindowState.Normal;
		state.NativeWindow = ResolveNativeWindow(window);
		if (!wasMaximized)
			CaptureNormalGeometry(window, state);
		state.RestoreBounds = GetNormalRestoreBounds(window, state);
		CaptureFullscreenGeometry(state);
		state.TitleBars.Clear();
		CollectTitleBars(window, state.TitleBars);
		foreach ((TitleBar titleBar, _) in state.TitleBars)
			titleBar.Visibility = Visibility.Collapsed;
		state.ApplyingManagedState = true;
		try
		{
			window.WindowState = System.Windows.WindowState.Normal;
		}
		finally
		{
			state.ApplyingManagedState = false;
		}
		if (state.NativeWindow != 0)
			LinuxWindowInterop.TrySetMaximized(state.NativeWindow, false);
		window.ResizeMode = ResizeMode.NoResize;
		RoundedWindowChrome.Refresh(window);
		int generation = state.Generation;
		_ = SynchronizeFullscreenAsync(window, state, generation, true);
		App.Logger?.WriteLine("LinuxWindowMode::EnterFullscreen", "Entered fullscreen for " + window.Title);
	}

	private static void ExitFullscreen(Window window)
	{
		if (!States.TryGetValue(window, out WindowModeState? state) || !state.Fullscreen)
			return;

		state.Generation++;
		state.MaximizeGeneration++;
		state.Fullscreen = false;
		int generation = state.Generation;
		nint nativeWindow = state.NativeWindow != 0 ? state.NativeWindow : ResolveNativeWindow(window);
		if (nativeWindow != 0)
			LinuxWindowInterop.TrySetFullscreen(nativeWindow, false);
		window.ResizeMode = state.ResizeMode;
		foreach ((TitleBar titleBar, Visibility visibility) in state.TitleBars)
			titleBar.Visibility = visibility;
		state.TitleBars.Clear();
		RestorePlacement(window, state, nativeWindow);
		RoundedWindowChrome.Refresh(window);
		_ = SynchronizeFullscreenAsync(window, state, generation, false);
		App.Logger?.WriteLine("LinuxWindowMode::ExitFullscreen", "Exited fullscreen for " + window.Title);
	}

	private static async Task SynchronizeFullscreenAsync(Window window, WindowModeState state, int generation, bool fullscreen)
	{
		try
		{
			for (int attempt = 0; attempt < 8; attempt++)
			{
				await Task.Delay(attempt == 0 ? 1 : 40).ConfigureAwait(false);
				if (window.Dispatcher.HasShutdownStarted || window.Dispatcher.HasShutdownFinished)
					return;
				bool done = await window.Dispatcher.InvokeAsync(() =>
				{
					if (state.Generation != generation || state.Fullscreen != fullscreen)
						return true;
					nint nativeWindow = state.NativeWindow != 0 ? state.NativeWindow : ResolveNativeWindow(window);
					if (nativeWindow == 0)
						return false;
					state.NativeWindow = nativeWindow;
					if (fullscreen)
					{
						if (LinuxWindowInterop.IsFullscreen(nativeWindow))
							return true;
						LinuxWindowInterop.TrySetFullscreen(nativeWindow, true);
						return false;
					}
					bool stateCleared = !LinuxWindowInterop.IsFullscreen(nativeWindow);
					bool placementRestored = state.WindowState == System.Windows.WindowState.Maximized
						? IsMaximizedSurface(nativeWindow)
						: MatchesFullscreenGeometry(nativeWindow, state);
					if (stateCleared && placementRestored)
						return true;
					LinuxWindowInterop.TrySetFullscreen(nativeWindow, false);
					if (state.WindowState == System.Windows.WindowState.Maximized)
					{
						LinuxWindowInterop.TrySetMaximized(nativeWindow, true);
						if (attempt >= 5)
							ApplyMaximizeFallback(window, state, nativeWindow);
					}
					else
					{
						ApplyFullscreenRestoreGeometry(window, state, nativeWindow);
					}
					return false;
				}, DispatcherPriority.Loaded);
				if (done)
					return;
			}

			if (fullscreen && state.Generation == generation && state.Fullscreen)
			{
				await window.Dispatcher.InvokeAsync(() => ApplyFullscreenFallback(window, state), DispatcherPriority.Loaded);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxWindowMode::SynchronizeFullscreenAsync", "Window mode synchronization failed: " + ex.Message);
		}
	}

	private static void ApplyFullscreenFallback(Window window, WindowModeState state)
	{
		if (!state.Fullscreen)
			return;
		LinuxDisplayBounds bounds = LinuxDisplayMetrics.Refresh().Bounds;
		if (!bounds.IsUsable)
			return;
		window.WindowState = System.Windows.WindowState.Normal;
		double scale = GetScale(window);
		window.Left = bounds.Left / scale;
		window.Top = bounds.Top / scale;
		window.Width = bounds.Width / scale;
		window.Height = bounds.Height / scale;
		if (state.NativeWindow != 0)
			LinuxWindowInterop.TryMoveResize(state.NativeWindow, bounds.Left, bounds.Top, bounds.Width, bounds.Height);
		RoundedWindowChrome.Refresh(window);
	}

	private static void RestorePlacement(Window window, WindowModeState state, nint nativeWindow)
	{
		state.ApplyingManagedState = true;
		try
		{
			window.WindowState = System.Windows.WindowState.Normal;
		}
		finally
		{
			state.ApplyingManagedState = false;
		}
		if (state.WindowState == System.Windows.WindowState.Maximized)
		{
			if (nativeWindow != 0)
				LinuxWindowInterop.TrySetMaximized(nativeWindow, true);
			LinuxTitleBar.RefreshMaximized(window, true);
			return;
		}
		LinuxTitleBar.RefreshMaximized(window, false);
		if (nativeWindow != 0 && state.FullscreenGeometryValid)
		{
			LinuxWindowInterop.TrySetMaximized(nativeWindow, false);
			ApplyFullscreenRestoreGeometry(window, state, nativeWindow);
			return;
		}
		if (state.RestoreBounds.IsEmpty)
			return;
		window.Left = state.RestoreBounds.Left;
		window.Top = state.RestoreBounds.Top;
		window.Width = state.RestoreBounds.Width;
		window.Height = state.RestoreBounds.Height;
		if (nativeWindow != 0)
		{
			double scale = GetScale(window);
			LinuxWindowInterop.TrySetMaximized(nativeWindow, false);
			LinuxWindowInterop.TryMoveResize(
				nativeWindow,
				(int)Math.Round(state.RestoreBounds.Left * scale),
				(int)Math.Round(state.RestoreBounds.Top * scale),
				(int)Math.Round(state.RestoreBounds.Width * scale),
				(int)Math.Round(state.RestoreBounds.Height * scale));
		}
	}

	private static void CaptureFullscreenGeometry(WindowModeState state)
	{
		state.FullscreenGeometryValid = state.NativeWindow != 0
			&& LinuxWindowInterop.TryGetWindowGeometry(
				state.NativeWindow,
				out state.FullscreenLeft,
				out state.FullscreenTop,
				out state.FullscreenWidth,
				out state.FullscreenHeight);
	}

	private static void ApplyFullscreenRestoreGeometry(Window window, WindowModeState state, nint nativeWindow)
	{
		if (!state.FullscreenGeometryValid)
			return;
		double scale = GetScale(window);
		window.Left = state.FullscreenLeft / scale;
		window.Top = state.FullscreenTop / scale;
		window.Width = state.FullscreenWidth / scale;
		window.Height = state.FullscreenHeight / scale;
		LinuxWindowInterop.TryMoveResize(nativeWindow, state.FullscreenLeft, state.FullscreenTop, state.FullscreenWidth, state.FullscreenHeight);
	}

	private static bool MatchesFullscreenGeometry(nint nativeWindow, WindowModeState state)
	{
		return !state.FullscreenGeometryValid
			|| LinuxWindowInterop.TryGetWindowGeometry(nativeWindow, out int left, out int top, out int width, out int height)
			&& Math.Abs(left - state.FullscreenLeft) <= 2
			&& Math.Abs(top - state.FullscreenTop) <= 2
			&& Math.Abs(width - state.FullscreenWidth) <= 2
			&& Math.Abs(height - state.FullscreenHeight) <= 2;
	}

	private static Rect GetNormalRestoreBounds(Window window, WindowModeState state)
	{
		if (!state.NormalGeometryValid)
			return GetRestoreBounds(window);
		double scale = GetScale(window);
		return new Rect(
			state.NormalLeft / scale,
			state.NormalTop / scale,
			state.NormalWidth / scale,
			state.NormalHeight / scale);
	}

	private static Rect GetRestoreBounds(Window window)
	{
		if (window.WindowState != System.Windows.WindowState.Normal && !window.RestoreBounds.IsEmpty)
			return window.RestoreBounds;
		double width = window.ActualWidth > 0.0 ? window.ActualWidth : window.Width;
		double height = window.ActualHeight > 0.0 ? window.ActualHeight : window.Height;
		return width > 0.0 && height > 0.0
			? new Rect(window.Left, window.Top, width, height)
			: Rect.Empty;
	}

	private static double GetScale(Window window)
	{
		double scale = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
		return scale > 0.0 && !double.IsNaN(scale) ? scale : 1.0;
	}

	private static nint ResolveNativeWindow(Window window)
	{
		string title = window.Title ?? string.Empty;
		return title.Length == 0 ? 0 : LinuxWindowInterop.FindOwnWindowByTitle(title);
	}

	private static void CollectTitleBars(DependencyObject root, List<(TitleBar, Visibility)> output)
	{
		if (root is TitleBar titleBar)
			output.Add((titleBar, titleBar.Visibility));
		int count = VisualTreeHelper.GetChildrenCount(root);
		for (int index = 0; index < count; index++)
			CollectTitleBars(VisualTreeHelper.GetChild(root, index), output);
	}

	private static bool IsOverlaySurface(Window window)
	{
		string name = window.GetType().Name;
		string area = window.GetType().Namespace ?? string.Empty;
		return area.Contains(".Overlay", StringComparison.Ordinal)
			|| area.Contains(".Crosshair", StringComparison.Ordinal)
			|| name.Contains("Overlay", StringComparison.Ordinal)
			|| name.Contains("Crosshair", StringComparison.Ordinal);
	}

	private static void OnClosed(object? sender, EventArgs e)
	{
		if (sender is not Window window)
			return;
		if (States.TryGetValue(window, out WindowModeState? state) && state.Fullscreen && state.NativeWindow != 0)
			LinuxWindowInterop.TrySetFullscreen(state.NativeWindow, false);
		window.PreviewKeyDown -= OnPreviewKeyDown;
		window.Loaded -= OnLoaded;
		window.StateChanged -= OnStateChanged;
		window.Closed -= OnClosed;
		States.Remove(window);
	}
}
