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

	private readonly CancellationTokenSource _lifetime = new();

	private WriteableBitmap? _bitmap;

	private byte[] _raw = [];

	private byte[] _ownWeights = [];

	private byte[] _mask = [];

	private byte[] _background = [];
	private byte[] _frame = [];
	private int[] _mediaXMap = [];
	private int[] _mediaYMap = [];
	private HomepageBackgroundMedia? _media;
	private string _mediaPath = string.Empty;
	private long _mediaVersion;
	private long _nextCapture;
	private int _contentWidth;
	private int _contentHeight;
	private int _windowNumber;
	private bool _hasCapture;

	private int _width;

	private int _height;

	private LinuxHomepageVisualSettings _settings;

	private bool _hasBackground;

	private int _busy;

	private bool _visible;

	private volatile bool _disposed;

	private int _captureFailureLogged;

	private int _firstCaptureLogged;

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
		_timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
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
			StopMedia();
			_hasCapture = false;
			_timer.Interval = TimeSpan.FromMilliseconds(500);
			Interlocked.Exchange(ref _busy, 0);
			return;
		}
		int windowNumber = rect.Hwnd.ToInt32();
		int titleBar = RobloxWindowTracker.MacTitleBarOffset;
		int contentWidth = rect.Width;
		int contentHeight = rect.Height;
		LinuxHomepageVisualSettings settings = LinuxHomepageVisualSettings.Read();
		CancellationToken token = _lifetime.Token;
		_ = Task.Run(() => RenderFrame(windowNumber, contentWidth, contentHeight, titleBar, settings, token));
	}

	private bool Capture(int windowNumber, int contentWidth, int contentHeight, int titleBar, out int width, out int height)
	{
		int rawWidth;
		int rawHeight;
		bool captured = Voidstrap.Platform.MacOS.MacOSScreenCapture.IsAvailable
			? Voidstrap.Platform.MacOS.MacOSScreenCapture.TryCapture(windowNumber, contentWidth, contentHeight + titleBar, ref _raw, out rawWidth, out rawHeight)
			: Voidstrap.Platform.MacOS.MacOSWindowCapture.TryCapture(windowNumber, true, ref _raw, out rawWidth, out rawHeight);
		width = rawWidth;
		height = rawHeight;
		if (!captured)
			return false;
		int skip = titleBar <= 0 ? 0 : rawHeight - (int)Math.Round(rawHeight * (double)contentHeight / (contentHeight + titleBar));
		height = rawHeight - skip;
		if (height <= 0)
			return false;
		int needed = rawWidth * height * 4;
		if (skip > 0)
			Buffer.BlockCopy(_raw, skip * rawWidth * 4, _raw, 0, needed);
		return true;
	}

	private void RenderFrame(int windowNumber, int contentWidth, int contentHeight, int titleBar, LinuxHomepageVisualSettings settings, CancellationToken token)
	{
		bool pending = false;
		try
		{
			if (token.IsCancellationRequested)
				return;
			bool captureDue = Environment.TickCount64 >= _nextCapture || contentWidth != _contentWidth || contentHeight != _contentHeight || windowNumber != _windowNumber;
			if (!_hasCapture && !captureDue)
				return;
			if (captureDue)
			{
				_nextCapture = Environment.TickCount64 + 500;
				_contentWidth = contentWidth;
				_contentHeight = contentHeight;
				_windowNumber = windowNumber;
			}
			int width = _width;
			int height = _height;
			if (captureDue && !Capture(windowNumber, contentWidth, contentHeight, titleBar, out width, out height))
			{
				_hasCapture = false;
				if (Interlocked.Exchange(ref _captureFailureLogged, 1) == 0)
					App.Logger.WriteLine(LogIdent, "The Roblox window could not be captured, check Screen Recording permission for Voidstrap");
				Post(() => SetVisible(false));
				return;
			}
			if (token.IsCancellationRequested)
				return;
			Interlocked.Exchange(ref _captureFailureLogged, 0);
			int pixels = width * height;
			int maskWidth = (width + MaskScale - 1) / MaskScale;
			int maskHeight = (height + MaskScale - 1) / MaskScale;
			if (_ownWeights.Length != maskWidth * maskHeight)
			{
				_ownWeights = new byte[maskWidth * maskHeight];
				_mask = new byte[maskWidth * maskHeight];
			}
			if (captureDue)
			{
				int matched = LinuxHomepageBackgroundMask.BuildSampled(_raw, width, height, MaskScale, _ownWeights, _mask);
				if (Interlocked.Exchange(ref _firstCaptureLogged, 1) == 0)
					App.Logger.WriteLine(LogIdent, $"Captured the Roblox window at {width}x{height}, {matched} home background samples matched");
				int minimumRegion = Math.Min(_ownWeights.Length, Math.Clamp(_ownWeights.Length / 512, 64, 4096));
				_hasCapture = matched >= minimumRegion;
				_nextCapture = Environment.TickCount64 + 500;
				_contentWidth = contentWidth;
				_contentHeight = contentHeight;
				_windowNumber = windowNumber;
				if (!_hasCapture)
				{
					Post(() => SetVisible(false));
					return;
				}
			}
			if (_background.Length != pixels * 4)
			{
				_background = new byte[pixels * 4];
				_frame = new byte[pixels * 4];
				_mediaXMap = new int[width];
				_mediaYMap = new int[height];
				_hasBackground = false;
			}
			if (_mediaXMap.Length != width || _mediaYMap.Length != height)
			{
				_mediaXMap = new int[width];
				_mediaYMap = new int[height];
			}
			string path = settings.Mode == "Media" ? settings.MediaPath : string.Empty;
			if (!string.Equals(path, _mediaPath, StringComparison.Ordinal))
			{
				StopMedia();
				_mediaPath = path;
				if (System.IO.File.Exists(path))
					_media = new HomepageBackgroundMedia(path, 30d, contentWidth, contentHeight);
				_hasBackground = false;
			}
			bool backgroundChanged = !_hasBackground || width != _width || height != _height || settings != _settings;
			if (backgroundChanged)
				_mediaVersion = 0;
			bool mediaChanged = _media?.TryReadFrame(_mediaVersion, (mediaPixels, mediaWidth, mediaHeight, version) =>
			{
				LinuxHomepageBackgroundMask.BuildBackground(width, height, settings, mediaPixels, mediaWidth, mediaHeight, _background, _mediaXMap, _mediaYMap);
				_mediaVersion = version;
			}) == true;
			if (backgroundChanged)
			{
				if (!mediaChanged)
					LinuxHomepageBackgroundMask.BuildBackground(width, height, settings, null, 0, 0, _background, _mediaXMap, _mediaYMap);
				_settings = settings;
				_width = width;
				_height = height;
				_hasBackground = true;
			}
			if (!captureDue && !backgroundChanged && !mediaChanged)
				return;
			LinuxHomepageBackgroundMask.ApplyBackgroundMask(_raw, _mask, _background, width, height, _frame, MaskScale);
			if (token.IsCancellationRequested)
				return;
			byte[] frame = _frame;
			pending = Post(() =>
			{
				try
				{
					_timer.Interval = TimeSpan.FromMilliseconds(_media?.IsAnimated == true ? 1000d / 30d : 500d);
					Present(frame, width, height);
				}
				finally
				{
					FinishFrame();
				}
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "A homepage frame failed: " + ex.Message);
		}
		finally
		{
			if (!pending)
				FinishFrame();
		}
	}

	private void Present(byte[] frame, int width, int height)
	{
		if (_disposed || !OverlayHub.HomepageBackgroundActive || !RobloxWindowTracker.Current.Foreground)
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

	private bool Post(Action action)
	{
		if (_disposed || _window.Dispatcher.HasShutdownStarted)
			return false;
		return _window.Dispatcher.BeginInvoke(DispatcherPriority.Render, action).Status != DispatcherOperationStatus.Aborted;
	}

	private void FinishFrame()
	{
		Interlocked.Exchange(ref _busy, 0);
		if (_disposed && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
		{
			_raw = [];
			_ownWeights = [];
			_mask = [];
			_background = [];
			_frame = [];
			_mediaXMap = [];
			_mediaYMap = [];
			StopMedia();
			Voidstrap.Platform.MacOS.MacOSScreenCapture.ReleaseWindow();
			Interlocked.Exchange(ref _busy, 0);
		}
	}

	private void StopMedia()
	{
		_media?.Dispose();
		_media = null;
		_mediaPath = string.Empty;
		_mediaVersion = 0;
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_timer.Stop();
		_timer.Tick -= OnTick;
		_lifetime.Cancel();
		_lifetime.Dispose();
		_image.Source = null;
		_bitmap = null;
		_window.Content = null;
		if (Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
			FinishFrame();
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
