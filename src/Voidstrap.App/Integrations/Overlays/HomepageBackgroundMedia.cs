using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Voidstrap.Integrations.Overlays
{
	internal sealed class HomepageBackgroundMedia : IDisposable
	{
		private readonly record struct Frame(byte[] Pixels, int Width, int Height, int DelayMilliseconds);

		private const int MaxStaticWidth = 3840;
		private const int MaxStaticHeight = 2160;
		private const int MaxAnimatedWidth = 1920;
		private const int MaxAnimatedHeight = 1080;
		private const int MaxGifFrames = 180;
		private const int MaxCanvasEdge = 8192;
		private const long MaxCanvasBytes = 256L * 1024L * 1024L;
		private const long MaxGifDecodedBytes = 320L * 1024L * 1024L;
		private const long MaxImageEncodedBytes = 64L * 1024L * 1024L;
		private readonly string _path;
		private readonly Thread _thread;
		private readonly Lock _frameLock = new();
		private Dispatcher? _dispatcher;
		private DispatcherTimer? _timer;
		private MediaPlayer? _player;
		private VideoDrawing? _videoDrawing;
		private DrawingVisual? _videoVisual;
		private RenderTargetBitmap? _videoTarget;
		private List<Frame>? _gifFrames;
		private int _gifIndex;
		private long _nextGifTick;
		private byte[]? _latestPixels;
		private byte[]? _videoWritePixels;
		private TimeSpan _lastVideoPosition = TimeSpan.MinValue;
		private int _latestWidth;
		private int _latestHeight;
		private long _version;
		private bool _disposed;

		private readonly TimeSpan _tickInterval;

		private volatile bool _animated;

		public bool IsAnimated => _animated;

		public HomepageBackgroundMedia(string path, double refreshHz)
		{
			_path = path;
			double hz = refreshHz > 1.0 && refreshHz < 1000.0 ? refreshHz : 60.0;
			_tickInterval = TimeSpan.FromMilliseconds(1000.0 / hz);
			_thread = new Thread(Run)
			{
				IsBackground = true,
				Name = "Homepage Background Media",
				Priority = ThreadPriority.BelowNormal
			};
			_thread.SetApartmentState(ApartmentState.STA);
			_thread.Start();
		}

		public bool TryReadFrame(long previousVersion, Action<byte[], int, int, long> reader)
		{
			lock (_frameLock)
			{
				if (_version == previousVersion || _latestPixels == null)
					return false;
				reader(_latestPixels, _latestWidth, _latestHeight, _version);
				return true;
			}
		}

		private void Run()
		{
			Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
			_dispatcher = dispatcher;
			dispatcher.UnhandledException += OnDispatcherUnhandledException;
			try
			{
				string extension = Path.GetExtension(_path).ToLowerInvariant();
				if (IsVideoExtension(extension))
					OpenVideo();
				else
					OpenImage(IsGif());
				if (!_disposed)
					Dispatcher.Run();
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("HomepageBackgroundMedia::Run", ex);
			}
			finally
			{
				dispatcher.UnhandledException -= OnDispatcherUnhandledException;
				ReleaseThreadResources();
			}
		}

		private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
		{
			e.Handled = true;
			App.Logger.WriteException("HomepageBackgroundMedia::Dispatcher", e.Exception);
		}

		private static bool IsVideoExtension(string extension)
		{
			return extension is ".mp4" or ".m4v" or ".webm" or ".avi" or ".mov" or ".wmv" or ".mpeg" or ".mpg" or ".mkv";
		}

		private bool IsGif()
		{
			try
			{
				Span<byte> signature = stackalloc byte[6];
				using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				if (stream.Read(signature) != signature.Length)
					return false;
				return signature.SequenceEqual("GIF87a"u8) || signature.SequenceEqual("GIF89a"u8);
			}
			catch
			{
				return false;
			}
		}

		private void OpenImage(bool animated)
		{
			try
			{
				if (new FileInfo(_path).Length > MaxImageEncodedBytes)
					return;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "The background file could not be read: " + ex.Message);
				return;
			}
			if (!animated)
			{
				BitmapSource? image = Voidstrap.Utility.SafeImaging.FromFile(_path, MaxStaticWidth);
				if (image != null)
					Publish(ToFrame(image, 1000, MaxStaticWidth, MaxStaticHeight));
				return;
			}
			using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
			if (decoder.Frames.Count == 0)
				return;
			if (decoder.Frames.Count == 1)
			{
				Publish(ToFrame(decoder.Frames[0], 1000, MaxAnimatedWidth, MaxAnimatedHeight));
				return;
			}
			List<Frame> frames = ComposeGifFrames(decoder);
			if (frames.Count == 0)
				return;
			_gifFrames = frames;
			_animated = frames.Count > 1;
			_gifIndex = 0;
			Publish(frames[0]);
			_nextGifTick = Environment.TickCount64 + frames[0].DelayMilliseconds;
			_timer = new DispatcherTimer(DispatcherPriority.Render)
			{
				Interval = _tickInterval
			};
			_timer.Tick += OnGifTick;
			_timer.Start();
		}

		private void OpenVideo()
		{
			_animated = true;
			_player = new MediaPlayer
			{
				Volume = 0
			};
			_player.MediaOpened += OnVideoOpened;
			_player.MediaEnded += OnVideoEnded;
			_player.MediaFailed += OnVideoFailed;
			_player.Open(new Uri(_path, UriKind.Absolute));
			_player.Play();
			_timer = new DispatcherTimer(DispatcherPriority.Render)
			{
				Interval = _tickInterval
			};
			_timer.Tick += OnVideoTick;
			_timer.Start();
		}

		private void OnGifTick(object? sender, EventArgs e)
		{
			if (_disposed || _gifFrames == null || _gifFrames.Count < 2 || Environment.TickCount64 < _nextGifTick)
				return;
			_gifIndex = (_gifIndex + 1) % _gifFrames.Count;
			Frame frame = _gifFrames[_gifIndex];
			Publish(frame);
			long scheduled = _nextGifTick + frame.DelayMilliseconds;
			long earliest = Environment.TickCount64 + 1;
			_nextGifTick = scheduled < earliest ? earliest : scheduled;
		}

		private void OnVideoOpened(object? sender, EventArgs e)
		{
			if (_disposed || _player == null || _player.NaturalVideoWidth < 1 || _player.NaturalVideoHeight < 1)
				return;
			(int width, int height) = FitSize(_player.NaturalVideoWidth, _player.NaturalVideoHeight, MaxAnimatedWidth, MaxAnimatedHeight);
			_videoDrawing = new VideoDrawing
			{
				Player = _player,
				Rect = new Rect(0, 0, width, height)
			};
			_videoVisual = new DrawingVisual();
			_videoTarget = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
		}

		private void OnVideoEnded(object? sender, EventArgs e)
		{
			if (_disposed || _player == null)
				return;
			_player.Position = TimeSpan.Zero;
			_player.Play();
		}

		private void OnVideoFailed(object? sender, ExceptionEventArgs e)
		{
			App.Logger.WriteLine("HomepageBackgroundMedia", "Background video could not be decoded: " + e.ErrorException.Message);
		}

		private void OnVideoTick(object? sender, EventArgs e)
		{
			if (_disposed || _videoDrawing == null || _videoVisual == null || _videoTarget == null || _player == null)
				return;
			TimeSpan position = _player.Position;
			if (position == _lastVideoPosition)
				return;
			_lastVideoPosition = position;
			using (DrawingContext drawing = _videoVisual.RenderOpen())
				drawing.DrawDrawing(_videoDrawing);
			_videoTarget.Render(_videoVisual);
			int stride = _videoTarget.PixelWidth * 4;
			int size = stride * _videoTarget.PixelHeight;
			_videoWritePixels ??= new byte[size];
			if (_videoWritePixels.Length != size)
				_videoWritePixels = new byte[size];
			_videoTarget.CopyPixels(_videoWritePixels, stride, 0);
			PublishVideo(_videoWritePixels, _videoTarget.PixelWidth, _videoTarget.PixelHeight);
		}

		private static Frame ToFrame(BitmapSource source, int delayMilliseconds, int maxWidth, int maxHeight)
		{
			(int width, int height) = FitSize(source.PixelWidth, source.PixelHeight, maxWidth, maxHeight);
			BitmapSource scaled = source;
			if (width != source.PixelWidth || height != source.PixelHeight)
				scaled = new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
			FormatConvertedBitmap converted = new(scaled, PixelFormats.Bgra32, null, 0);
			int stride = width * 4;
			byte[] pixels = new byte[stride * height];
			converted.CopyPixels(pixels, stride, 0);
			return new Frame(pixels, width, height, Math.Clamp(delayMilliseconds, 20, 10000));
		}

		private static (int Width, int Height) FitSize(int width, int height, int maxWidth, int maxHeight)
		{
			if (width < 1 || height < 1)
				return (1, 1);
			double scale = Math.Min(1.0, Math.Min((double)maxWidth / width, (double)maxHeight / height));
			return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
		}

		private static List<Frame> ComposeGifFrames(BitmapDecoder decoder)
		{
			List<Frame> frames = [];
			(int canvasWidth, int canvasHeight) = ReadLogicalScreen(decoder);
			if (canvasWidth < 1 || canvasHeight < 1 || canvasWidth > MaxCanvasEdge || canvasHeight > MaxCanvasEdge)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "The animation canvas is " + canvasWidth + "x" + canvasHeight + ", falling back to the first frame");
				TryAddSingleFrame(decoder, frames);
				return frames;
			}

			int stride = canvasWidth * 4;
			long canvasBytes = (long)stride * canvasHeight;
			if (canvasBytes > MaxCanvasBytes)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "The animation canvas needs " + canvasBytes + " bytes, falling back to the first frame");
				TryAddSingleFrame(decoder, frames);
				return frames;
			}

			byte[] canvas = new byte[canvasBytes];
			byte[]? saved = null;
			long decodedBytes = 0;
			int count = Math.Min(decoder.Frames.Count, MaxGifFrames);

			for (int i = 0; i < count; i++)
			{
				try
				{
					BitmapFrame source = decoder.Frames[i];
					int left = ReadMetadataInt(source, "/imgdesc/Left");
					int top = ReadMetadataInt(source, "/imgdesc/Top");
					int disposal = ReadMetadataInt(source, "/grctlext/Disposal");

					if (disposal == 3)
						saved ??= new byte[canvas.Length];
					if (disposal == 3)
						Array.Copy(canvas, saved!, canvas.Length);

					FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);
					int frameWidth = converted.PixelWidth;
					int frameHeight = converted.PixelHeight;
					long framePixelBytes = (long)frameWidth * 4 * frameHeight;
					if (frameWidth < 1 || frameHeight < 1 || framePixelBytes > MaxCanvasBytes)
						continue;

					byte[] framePixels = new byte[framePixelBytes];
					converted.CopyPixels(framePixels, frameWidth * 4, 0);

					BlendOver(canvas, canvasWidth, canvasHeight, framePixels, frameWidth, frameHeight, left, top);

					BitmapSource snapshot = BitmapSource.Create(canvasWidth, canvasHeight, 96, 96, PixelFormats.Bgra32, null, canvas, stride);
					Frame frame = ToFrame(snapshot, ReadGifDelay(source), MaxAnimatedWidth, MaxAnimatedHeight);
					long frameBytes = frame.Pixels.LongLength;
					if (decodedBytes > MaxGifDecodedBytes - frameBytes)
						break;
					decodedBytes += frameBytes;
					frames.Add(frame);

					if (disposal == 2)
						ClearRect(canvas, canvasWidth, canvasHeight, left, top, frameWidth, frameHeight);
					else if (disposal == 3 && saved != null)
						Array.Copy(saved, canvas, canvas.Length);
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("HomepageBackgroundMedia", "Animation frame " + i + " could not be decoded: " + ex.Message);
					break;
				}
			}

			if (frames.Count == 0)
				TryAddSingleFrame(decoder, frames);
			return frames;
		}

		private static void TryAddSingleFrame(BitmapDecoder decoder, List<Frame> frames)
		{
			try
			{
				if (decoder.Frames.Count > 0)
					frames.Add(ToFrame(decoder.Frames[0], 1000, MaxAnimatedWidth, MaxAnimatedHeight));
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "The first animation frame could not be decoded: " + ex.Message);
			}
		}

		private static (int Width, int Height) ReadLogicalScreen(BitmapDecoder decoder)
		{
			int width = 0;
			int height = 0;
			try
			{
				if (decoder.Metadata is BitmapMetadata metadata)
				{
					if (metadata.ContainsQuery("/logscrdesc/Width") && metadata.GetQuery("/logscrdesc/Width") is ushort w)
						width = w;
					if (metadata.ContainsQuery("/logscrdesc/Height") && metadata.GetQuery("/logscrdesc/Height") is ushort h)
						height = h;
				}
			}
			catch
			{
			}
			if (width > 0 && height > 0)
				return (width, height);

			foreach (BitmapFrame frame in decoder.Frames)
			{
				width = Math.Max(width, ReadMetadataInt(frame, "/imgdesc/Left") + frame.PixelWidth);
				height = Math.Max(height, ReadMetadataInt(frame, "/imgdesc/Top") + frame.PixelHeight);
			}
			return (width, height);
		}

		private static int ReadMetadataInt(BitmapFrame frame, string query)
		{
			try
			{
				if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery(query))
				{
					object value = metadata.GetQuery(query);
					if (value is ushort unsigned)
						return unsigned;
					if (value is byte small)
						return small;
					if (value is int signed)
						return signed;
				}
			}
			catch
			{
			}
			return 0;
		}

		private static void BlendOver(byte[] canvas, int canvasWidth, int canvasHeight, byte[] source, int sourceWidth, int sourceHeight, int left, int top)
		{
			for (int y = 0; y < sourceHeight; y++)
			{
				int targetY = top + y;
				if (targetY < 0 || targetY >= canvasHeight)
					continue;
				int sourceRow = y * sourceWidth * 4;
				int targetRow = targetY * canvasWidth * 4;
				for (int x = 0; x < sourceWidth; x++)
				{
					int targetX = left + x;
					if (targetX < 0 || targetX >= canvasWidth)
						continue;
					int s = sourceRow + (x * 4);
					int t = targetRow + (targetX * 4);
					int sourceAlpha = source[s + 3];
					if (sourceAlpha == 0)
						continue;
					if (sourceAlpha == 255)
					{
						canvas[t] = source[s];
						canvas[t + 1] = source[s + 1];
						canvas[t + 2] = source[s + 2];
						canvas[t + 3] = 255;
						continue;
					}
					int inverse = 255 - sourceAlpha;
					int targetAlpha = canvas[t + 3];
					int outAlpha = sourceAlpha + (targetAlpha * inverse / 255);
					if (outAlpha == 0)
					{
						canvas[t] = 0;
						canvas[t + 1] = 0;
						canvas[t + 2] = 0;
						canvas[t + 3] = 0;
						continue;
					}
					for (int channel = 0; channel < 3; channel++)
					{
						int blended = (source[s + channel] * sourceAlpha) + (canvas[t + channel] * targetAlpha * inverse / 255);
						canvas[t + channel] = (byte)(blended / outAlpha);
					}
					canvas[t + 3] = (byte)outAlpha;
				}
			}
		}

		private static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height)
		{
			for (int y = 0; y < height; y++)
			{
				int targetY = top + y;
				if (targetY < 0 || targetY >= canvasHeight)
					continue;
				int targetRow = targetY * canvasWidth * 4;
				for (int x = 0; x < width; x++)
				{
					int targetX = left + x;
					if (targetX < 0 || targetX >= canvasWidth)
						continue;
					int t = targetRow + (targetX * 4);
					canvas[t] = 0;
					canvas[t + 1] = 0;
					canvas[t + 2] = 0;
					canvas[t + 3] = 0;
				}
			}
		}

		private static int ReadGifDelay(BitmapFrame frame)
		{
			try
			{
				if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay"))
				{
					object value = metadata.GetQuery("/grctlext/Delay");
					if (value is ushort centiseconds && centiseconds > 0)
						return centiseconds * 10;
				}
			}
			catch
			{
			}
			return 100;
		}

		private void Publish(Frame frame)
		{
			lock (_frameLock)
			{
				_latestPixels = frame.Pixels;
				_latestWidth = frame.Width;
				_latestHeight = frame.Height;
				_version++;
			}
		}

		private void PublishVideo(byte[] pixels, int width, int height)
		{
			lock (_frameLock)
			{
				byte[]? previous = _latestPixels;
				_latestPixels = pixels;
				_latestWidth = width;
				_latestHeight = height;
				_version++;
				_videoWritePixels = previous != null && previous.Length == pixels.Length ? previous : null;
			}
		}

		private void ReleaseThreadResources()
		{
			if (_timer != null)
			{
				_timer.Stop();
				_timer.Tick -= OnGifTick;
				_timer.Tick -= OnVideoTick;
				_timer = null;
			}
			if (_player != null)
			{
				_player.MediaOpened -= OnVideoOpened;
				_player.MediaEnded -= OnVideoEnded;
				_player.MediaFailed -= OnVideoFailed;
				_player.Stop();
				_player.Close();
				_player = null;
			}
			_videoDrawing = null;
			_videoVisual = null;
			_videoTarget = null;
			_gifFrames = null;
		}

		private void StopDispatcher()
		{
			Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;
			Dispatcher? dispatcher = _dispatcher;
			if (dispatcher != null && !dispatcher.HasShutdownStarted)
				dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(StopDispatcher));
			if (_thread.IsAlive && Thread.CurrentThread != _thread)
				_thread.Join(TimeSpan.FromSeconds(2));
			lock (_frameLock)
			{
				_latestPixels = null;
				_latestWidth = 0;
				_latestHeight = 0;
				_videoWritePixels = null;
			}
			GC.SuppressFinalize(this);
		}
	}
}
