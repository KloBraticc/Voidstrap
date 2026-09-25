using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class LinuxWindowReveal
{
#if CROSSPLAT
	private static readonly TimeSpan RevealDeadline = TimeSpan.FromSeconds(8);

	private static readonly MethodInfo? InitializeHidden = typeof(System.Windows.Media.ProGPU.ProGpuWpfWindowHost)
		.GetMethod("InitializeHidden", BindingFlags.Instance | BindingFlags.NonPublic);

	private static readonly bool Disabled = Environment.GetEnvironmentVariable("VOIDSTRAP_WINDOW_REVEAL") == "0";

	private static bool _sessionDisabled;

	private static void DisableForSession(string reason)
	{
		if (_sessionDisabled)
			return;
		_sessionDisabled = true;
		App.Logger.WriteLine("LinuxWindowReveal", "Showing windows right away for the rest of this session: " + reason);
	}

	internal static void Prepare(object window, System.Windows.Media.ProGPU.ProGpuWpfWindowHost host)
	{
		if (Disabled || _sessionDisabled || Voidstrap.Utility.LinuxStartup.SafeMode || InitializeHidden is null || window is not Window wpf || wpf.AllowsTransparency)
			return;

		nint handle = 0;
		try
		{
			InitializeHidden.Invoke(host, null);
			handle = host.SilkWindow?.Native?.X11 is { } x11 ? (nint)x11.Window : 0;
			if (handle == 0)
				return;

			Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowBackground(handle, ResolveBackground());
			if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowOpacity(handle, 0.0))
				return;

			new Pending(wpf, host, handle).Start();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("LinuxWindowReveal", "Could not prepare the window reveal: " + ex.Message);
			DisableForSession("preparing a window failed");
			if (handle != 0)
				Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowOpacity(handle, 1.0);
		}
	}

	private static uint ResolveBackground()
	{
		if (Application.Current?.TryFindResource("WindowBackgroundColorPrimary") is Color color)
			return (uint)((color.R << 16) | (color.G << 8) | color.B);
		return 0x1C1C1C;
	}

	private sealed class Pending
	{
		private readonly Window _window;
		private readonly System.Windows.Media.ProGPU.ProGpuWpfWindowHost _host;
		private readonly nint _handle;
		private DispatcherTimer? _deadline;
		private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
		private bool _done;
		private bool _revealQueued;

		internal Pending(Window window, System.Windows.Media.ProGPU.ProGpuWpfWindowHost host, nint handle)
		{
			_window = window;
			_host = host;
			_handle = handle;
		}

		internal void Start()
		{
			CompositionTarget.Rendering += OnRendering;
			_window.Closed += OnClosed;
			_deadline = new DispatcherTimer(DispatcherPriority.Background, _window.Dispatcher) { Interval = RevealDeadline };
			_deadline.Tick += OnDeadline;
			_deadline.Start();
		}

		private void OnRendering(object? sender, EventArgs e)
		{
			try
			{
				if (!_revealQueued && _host.PresentedFrameCount > 0)
				{
					_revealQueued = true;
					_ = _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RevealAfterFrame));
				}
			}
			catch (Exception ex)
			{
				DisableForSession("reading the frame count failed, " + ex.Message);
				Reveal("an error");
			}
		}

		private void OnDeadline(object? sender, EventArgs e)
		{
			DisableForSession("a window drew no frame in time");
			Reveal("the deadline");
		}

		private void RevealAfterFrame()
		{
			Reveal("its first frame");
		}

		private void OnClosed(object? sender, EventArgs e)
		{
			Finish();
		}

		private void Reveal(string reason)
		{
			if (_done)
				return;
			try
			{
				Finish();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("LinuxWindowReveal", "Could not stop watching the window: " + ex.Message);
			}
			if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetWindowOpacity(_handle, 1.0))
			{
				DisableForSession("the window could not be made visible");
				return;
			}
			App.Logger.WriteLine("LinuxWindowReveal", "Showed " + _window.GetType().Name + " after " + _elapsed.ElapsedMilliseconds + " ms on " + reason);
			LinuxUiPerformance.FirstPresented(_window, _elapsed.ElapsedMilliseconds);
		}

		private void Finish()
		{
			_done = true;
			CompositionTarget.Rendering -= OnRendering;
			_window.Closed -= OnClosed;
			if (_deadline is null)
				return;
			_deadline.Stop();
			_deadline.Tick -= OnDeadline;
			_deadline = null;
		}
	}
#endif
}
