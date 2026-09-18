using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Platform.Linux;

namespace Voidstrap.UI;

internal static class LinuxApplicationIdentity
{
	private static readonly object Sync = new();

	private static readonly int[] IconSizes = new[] { 32, 48, 64, 128 };

	private static BitmapSource? _windowIcon;

	private static nint[] _nativeIcon = Array.Empty<nint>();

	private static BootstrapperIcon _cachedIcon;

	private static string _cachedCustom = string.Empty;

	private static bool _cached;

	private static readonly HashSet<Window> Pending = new();

	private static DispatcherTimer? _retryTimer;

	private static int _retryPasses;

	public static bool Apply(Window window)
	{
		return Apply(window, false);
	}

	public static void Refresh()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
			return;

		lock (Sync)
			_cached = false;

		Voidstrap.Utility.LinuxDesktopEntry.RefreshIcon();

		try
		{
			Application application = Application.Current;
			if (application == null)
				return;

			application.Dispatcher.Invoke(delegate
			{
				foreach (Window window in application.Windows)
					Apply(window, true);
			});
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxApplicationIdentity::Refresh", "Could not refresh the application icon: " + ex.Message);
		}
	}

	private static bool Apply(Window window, bool force)
	{
		if (!Voidstrap.Utility.Platform.IsLinux || window == null)
			return true;

		try
		{
			EnsureCurrent();

			BitmapSource? icon;
			nint[] native;
			lock (Sync)
			{
				icon = _windowIcon;
				native = _nativeIcon;
			}

			if (icon != null && (force || window.Icon == null))
				window.Icon = icon;

			IReadOnlyList<nint> handles = LinuxWindowInterop.FindOwnManagedWindowsByTitle(window.Title);
			if (handles.Count == 0)
			{
				ScheduleRetry(window);
				return false;
			}

			int applied = 0;
			foreach (nint handle in handles)
			{
				if (LinuxWindowInterop.TrySetApplicationIdentity(handle, "voidstrap", "Voidstrap", native))
					applied++;
			}

			if (applied == 0)
			{
				App.Logger?.WriteLine("LinuxApplicationIdentity::Apply", "Could not set the native application identity for " + window.Title);
				ScheduleRetry(window);
				return false;
			}

			Forget(window);
			App.Logger?.WriteLine("LinuxApplicationIdentity::Apply", "Applied the " + _cachedIcon + " icon and desktop identity to " + applied + " window(s) titled " + window.Title);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxApplicationIdentity::Apply", "Could not set the application icon for " + window.Title + ": " + ex.Message);
			return false;
		}
	}

	private static void ScheduleRetry(Window window)
	{
		bool added;
		lock (Sync)
			added = Pending.Add(window);

		if (added)
			window.Closed += OnWindowClosed;

		if (_retryTimer != null)
			return;

		_retryPasses = 0;
		_retryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
		_retryTimer.Tick += OnRetryTick;
		_retryTimer.Start();
	}

	private static void OnRetryTick(object? sender, EventArgs e)
	{
		List<Window> windows;
		lock (Sync)
			windows = new List<Window>(Pending);

		foreach (Window window in windows)
			Apply(window, true);

		_retryPasses++;

		int remaining;
		lock (Sync)
			remaining = Pending.Count;

		if (_retryPasses >= 8 || remaining == 0)
			StopRetry();
	}

	private static void StopRetry()
	{
		if (_retryTimer == null)
			return;

		_retryTimer.Stop();
		_retryTimer.Tick -= OnRetryTick;
		_retryTimer = null;
	}

	private static void OnWindowClosed(object? sender, EventArgs e)
	{
		if (sender is Window window)
			Forget(window);
	}

	private static void Forget(Window window)
	{
		bool removed;
		lock (Sync)
			removed = Pending.Remove(window);

		if (removed)
			window.Closed -= OnWindowClosed;
	}

	private static void EnsureCurrent()
	{
		BootstrapperIcon selected = App.Settings.Prop.ActiveBootstrapperIcon;
		string custom = App.Settings.Prop.BootstrapperIconCustomLocation ?? string.Empty;

		lock (Sync)
		{
			if (_cached && _cachedIcon == selected && string.Equals(_cachedCustom, custom, StringComparison.Ordinal))
				return;

			_windowIcon = IconEx.LoadPortableIcon(selected, 256) ?? ReadFallbackIcon(256);
			_nativeIcon = BuildNativeIcon(selected);
			_cachedIcon = selected;
			_cachedCustom = custom;
			_cached = true;
		}
	}

	private static BitmapSource? ReadFallbackIcon(int decodeWidth)
	{
		return Voidstrap.Utility.SafeImaging.FromUri(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute), decodeWidth);
	}

	private static nint[] BuildNativeIcon(BootstrapperIcon selected)
	{
		List<nint> data = new();
		HashSet<long> emitted = new();

		foreach (int size in IconSizes)
		{
			BitmapSource? decoded = IconEx.LoadPortableIcon(selected, size) ?? ReadFallbackIcon(size);
			if (decoded == null)
				continue;

			BitmapSource source = decoded.Format == PixelFormats.Bgra32
				? decoded
				: new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);

			int width = source.PixelWidth;
			int height = source.PixelHeight;
			if (width <= 0 || height <= 0 || !emitted.Add((long)width << 32 | (uint)height))
				continue;

			int stride = checked(width * 4);
			byte[] pixels = new byte[checked(stride * height)];
			source.CopyPixels(pixels, stride, 0);

			data.Add(width);
			data.Add(height);
			for (int offset = 0; offset < pixels.Length; offset += 4)
			{
				uint argb = (uint)(pixels[offset + 3] << 24 | pixels[offset + 2] << 16 | pixels[offset + 1] << 8 | pixels[offset]);
				data.Add(unchecked((nint)(long)argb));
			}
		}

		return data.ToArray();
	}
}
