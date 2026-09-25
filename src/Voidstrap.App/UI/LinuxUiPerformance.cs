using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Utility;

namespace Voidstrap.UI;

internal static class LinuxUiPerformance
{
	private static readonly bool TraceEnabled = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("VOIDSTRAP_PERF_TRACE") == "1";
	private static readonly long Started = Stopwatch.GetTimestamp();
	private static readonly ConditionalWeakTable<Window, WindowProbe> Windows = new();
	private static bool _installed;
	private static bool _reducedMotion;
	private static int _inputSamples;
	private static readonly long[] InputTimes = new long[100];
	private static string _lastRenderer = string.Empty;
	private static DispatcherTimer? _dispatchTimer;
	private static long _lastDispatchTick;
	private static int _reportedStalls;

	internal static bool ReducedMotion => OperatingSystem.IsLinux() && (_reducedMotion || LinuxStartup.ActiveStage == "software");

	internal static event EventHandler? ReducedMotionChanged;

	internal static void Install()
	{
		if (_installed || !OperatingSystem.IsLinux())
			return;

		_installed = true;
		EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
		if (TraceEnabled)
		{
			_lastDispatchTick = Stopwatch.GetTimestamp();
			_dispatchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
			_dispatchTimer.Tick += OnDispatchTick;
			_dispatchTimer.Start();
		}
		if (ReducedMotion)
			SetReducedMotion();
	}

	internal static void Mark(string stage)
	{
		if (TraceEnabled)
			App.Logger.WriteLine("LinuxUiPerformance", stage + " at " + (int)Stopwatch.GetElapsedTime(Started).TotalMilliseconds + " ms");
	}

	internal static void Duration(string stage, long started)
	{
		if (TraceEnabled)
			App.Logger.WriteLine("LinuxUiPerformance", stage + " took " + (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds + " ms");
	}

	internal static void WindowConstructed(Window window)
	{
		if (OperatingSystem.IsLinux())
			Windows.GetValue(window, static item => new WindowProbe(item));
	}

	internal static void FirstPresented(Window window, long elapsedMilliseconds)
	{
		if (TraceEnabled)
			App.Logger.WriteLine("LinuxUiPerformance", window.GetType().Name + " first presented frame after " + elapsedMilliseconds + " ms");
		if (Application.Current is App application)
			application.StartLinuxDeferredServices();
	}

	internal static void Renderer(string adapter, bool software)
	{
		if (TraceEnabled && _lastRenderer != adapter)
			App.Logger.WriteLine("LinuxUiPerformance", "Renderer " + adapter + (software ? ", software" : ", accelerated"));
		_lastRenderer = adapter;
		if (software)
			SetReducedMotion();
	}

	private static void OnWindowLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is Window window)
			Windows.GetValue(window, static item => new WindowProbe(item)).Loaded();
	}

	private static void RecordInput(long elapsedMilliseconds)
	{
		InputTimes[_inputSamples % InputTimes.Length] = elapsedMilliseconds;
		_inputSamples++;
		int recentCount = Math.Min(_inputSamples, 40);
		int slowCount = 0;
		for (int i = 0; i < recentCount; i++)
		{
			if (InputTimes[(_inputSamples - 1 - i) % InputTimes.Length] > 33)
				slowCount++;
		}
		if (TraceEnabled && _inputSamples % 20 == 0 && _inputSamples <= 1000)
		{
			int count = Math.Min(_inputSamples, InputTimes.Length);
			long[] sorted = new long[count];
			Array.Copy(InputTimes, sorted, count);
			Array.Sort(sorted);
			App.Logger.WriteLine("LinuxUiPerformance", "Input to presented frame p95 " + sorted[(int)Math.Ceiling(count * 0.95) - 1] + " ms, " + slowCount + " of the last " + recentCount + " samples exceeded 33 ms");
		}
	}

	private static void SetReducedMotion()
	{
		if (_reducedMotion)
			return;
		_reducedMotion = true;
		Wpf.Ui.Animations.RenderReady.ReducedMotion = true;
		App.Logger.WriteLine("LinuxUiPerformance", "Reduced decorative motion for this session");
		ReducedMotionChanged?.Invoke(null, EventArgs.Empty);
	}

	private static void OnDispatchTick(object? sender, EventArgs e)
	{
		long now = Stopwatch.GetTimestamp();
		long delayed = (long)Stopwatch.GetElapsedTime(_lastDispatchTick, now).TotalMilliseconds - 500;
		_lastDispatchTick = now;
		if (delayed > 50 && _reportedStalls++ < 100)
			App.Logger.WriteLine("LinuxUiPerformance", "UI thread timer was delayed by " + delayed + " ms");
	}

	internal static void Shutdown()
	{
		if (_dispatchTimer == null)
			return;
		_dispatchTimer.Stop();
		_dispatchTimer.Tick -= OnDispatchTick;
		_dispatchTimer = null;
	}

	private sealed class WindowProbe
	{
		private readonly Window _window;
		private readonly long _constructed = Stopwatch.GetTimestamp();
		private long _inputStarted;
#if CROSSPLAT
		private long _presentedBefore;
#endif
		private object? _hoverTarget;
		private bool _renderPending;
		private bool _loaded;

		internal WindowProbe(Window window)
		{
			_window = window;
			window.ContentRendered += OnContentRendered;
			window.Closed += OnClosed;
			window.PreviewMouseMove += OnMouseMove;
			window.PreviewMouseDown += OnMouseButton;
			window.PreviewMouseUp += OnMouseButton;
		}

		internal void Loaded()
		{
			if (_loaded)
				return;
			_loaded = true;
			Duration(_window.GetType().Name + " construction to loaded", _constructed);
			Mark(_window.GetType().Name + " loaded");
		}

		private void OnContentRendered(object? sender, EventArgs e)
		{
			_window.ContentRendered -= OnContentRendered;
			Duration(_window.GetType().Name + " construction to content rendered", _constructed);
		}

		private void OnMouseMove(object sender, MouseEventArgs e)
		{
			object? target = e.OriginalSource;
			if (ReferenceEquals(target, _hoverTarget))
				return;
			_hoverTarget = target;
			StartInputSample();
		}

		private void OnMouseButton(object sender, MouseButtonEventArgs e)
		{
			StartInputSample();
		}

		private void StartInputSample()
		{
			if (!TraceEnabled || _renderPending)
				return;
			_inputStarted = Stopwatch.GetTimestamp();
#if CROSSPLAT
			if (System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(_window, out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host))
				_presentedBefore = host?.PresentedFrameCount ?? 0;
#endif
			_renderPending = true;
			CompositionTarget.Rendering += OnRendering;
		}

		private void OnRendering(object? sender, EventArgs e)
		{
			long elapsed = (long)Stopwatch.GetElapsedTime(_inputStarted).TotalMilliseconds;
			bool presented = true;
#if CROSSPLAT
			if (System.Windows.Media.ProGPU.ProGpuWpfDiagnostics.TryGetWindowHost(_window, out System.Windows.Media.ProGPU.ProGpuWpfWindowHost? host))
				presented = host != null && host.PresentedFrameCount > _presentedBefore;
#endif
			if (!presented && elapsed < 200)
				return;
			CompositionTarget.Rendering -= OnRendering;
			_renderPending = false;
			if (presented)
				RecordInput(elapsed);
			else if (TraceEnabled)
				App.Logger.WriteLine("LinuxUiPerformance", _window.GetType().Name + " input had no presented frame within 200 ms");
		}

		private void OnClosed(object? sender, EventArgs e)
		{
			_window.ContentRendered -= OnContentRendered;
			_window.Closed -= OnClosed;
			_window.PreviewMouseMove -= OnMouseMove;
			_window.PreviewMouseDown -= OnMouseButton;
			_window.PreviewMouseUp -= OnMouseButton;
			if (_renderPending)
				CompositionTarget.Rendering -= OnRendering;
			Windows.Remove(_window);
		}
	}
}
