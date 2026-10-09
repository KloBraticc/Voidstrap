using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.Integrations.Overlays;

internal sealed class MacHomepageBackgroundOverlay : IDisposable
{
	private const string LogIdent = "MacHomepageBackground";

	private const int MaskScale = 2;

	private static int _permissionRequested;

	private readonly Window _window;

	private readonly Image _image;

	private readonly RobloxOverlayAnchor _anchor;

	private readonly DispatcherTimer _timer;

	private WriteableBitmap? _bitmap;

	private byte[] _capture = [];

	private byte[] _ownWeights = [];

	private byte[] _mask = [];

	private byte[] _background = [];

	private byte[] _output = [];

	private int _width;

	private int _height;

	private LinuxHomepageVisualSettings _settings;

	private bool _hasBackground;

	private int _busy;

	private bool _visible;

	private bool _disposed;

	private int _captureFailureLogged;

	public MacHomepageBackgroundOverlay()
	{
		_image = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
		_window = new Window
		{
			Title = "Voidstrap Homepage Background",
			WindowStyle = WindowStyle.None,
			AllowsTransparency = true,
			Background = Brushes.Transparent,
			ShowInTaskbar = false,
			ShowActivated = false,
			Topmost = true,
			Width = 1,
			Height = 1,
			Content = _image
		};
		LinuxOverlaySurface.ReleaseMainWindowClaim(_window);
		_anchor = new RobloxOverlayAnchor(_window, hideWhenUnfocused: true, placement: RobloxOverlayPlacement.Fill);
		_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
		_timer.Tick += OnTick;
		_window.Show();
		_timer.Start();
		if (!Voidstrap.Platform.MacOS.MacOSWindowCapture.HasPermission && Interlocked.Exchange(ref _permissionRequested, 1) == 0)
		{
			App.Logger.WriteLine(LogIdent, "Screen Recording permission is needed to read the Roblox home screen, asking macOS for it");
			Voidstrap.Platform.MacOS.MacOSWindowCapture.RequestPermission();
		}
		App.Logger.WriteLine(LogIdent, "Homepage background overlay started");
	}

	private void OnTick(object? sender, EventArgs e)
	{
		if (_disposed || Interlocked.Exchange(ref _busy, 1) != 0)
			return;
		RobloxWindowRect rect = RobloxWindowTracker.Current;
		if (!OverlayHub.HomepageBackgroundActive || !rect.Valid || !rect.Foreground || rect.Hwnd == IntPtr.Zero)
		{
			SetVisible(false);
			Interlocked.Exchange(ref _busy, 0);
			return;
		}
		int windowNumber = rect.Hwnd.ToInt32();
		LinuxHomepageVisualSettings settings = LinuxHomepageVisualSettings.Read();
		_ = Task.Run(() => RenderFrame(windowNumber, settings));
	}

	private void RenderFrame(int windowNumber, LinuxHomepageVisualSettings settings)
	{
		try
		{
			if (!Voidstrap.Platform.MacOS.MacOSWindowCapture.TryCapture(windowNumber, true, ref _capture, out int width, out int height))
			{
				if (Interlocked.Exchange(ref _captureFailureLogged, 1) == 0)
					App.Logger.WriteLine(LogIdent, "The Roblox window could not be captured, check Screen Recording permission for Voidstrap");
				Post(() => SetVisible(false));
				return;
			}
			Interlocked.Exchange(ref _captureFailureLogged, 0);
			int pixels = width * height;
			int maskWidth = (width + MaskScale - 1) / MaskScale;
			int maskHeight = (height + MaskScale - 1) / MaskScale;
			if (_ownWeights.Length != maskWidth * maskHeight)
			{
				_ownWeights = new byte[maskWidth * maskHeight];
				_mask = new byte[maskWidth * maskHeight];
			}
			if (_background.Length != pixels * 4)
			{
				_background = new byte[pixels * 4];
				_output = new byte[pixels * 4];
				_hasBackground = false;
			}
			int matched = LinuxHomepageBackgroundMask.BuildSampled(_capture, width, height, MaskScale, _ownWeights, _mask);
			int minimumRegion = Math.Min(_ownWeights.Length, Math.Clamp(_ownWeights.Length / 512, 64, 4096));
			if (matched < minimumRegion)
			{
				Post(() => SetVisible(false));
				return;
			}
			if (!_hasBackground || width != _width || height != _height || settings != _settings)
			{
				LinuxHomepageBackgroundMask.BuildBackground(width, height, settings, null, 0, 0, _background, new int[width], new int[height]);
				_settings = settings;
				_width = width;
				_height = height;
				_hasBackground = true;
			}
			LinuxHomepageBackgroundMask.ApplyBackgroundMask(_capture, _mask, _background, width, height, _output, MaskScale);
			byte[] frame = _output;
			Post(() => Present(frame, width, height));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "A homepage frame failed: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _busy, 0);
		}
	}

	private void Present(byte[] frame, int width, int height)
	{
		if (_disposed)
			return;
		if (_bitmap == null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
		{
			_bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
			_image.Source = _bitmap;
		}
		_bitmap.WritePixels(new Int32Rect(0, 0, width, height), frame, width * 4, 0);
		SetVisible(true);
		LinuxOverlaySurface.WakePresentation(_window);
	}

	private void SetVisible(bool visible)
	{
		if (_disposed || _visible == visible)
			return;
		_visible = visible;
		_image.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
	}

	private void Post(Action action)
	{
		if (_disposed || _window.Dispatcher.HasShutdownStarted)
			return;
		_window.Dispatcher.BeginInvoke(DispatcherPriority.Render, action);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_timer.Stop();
		_timer.Tick -= OnTick;
		_anchor.Dispose();
		try
		{
			_window.Close();
		}
		catch (InvalidOperationException)
		{
		}
		App.Logger.WriteLine(LogIdent, "Homepage background overlay stopped");
		GC.SuppressFinalize(this);
	}
}
