using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Voidstrap.Core;

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
		private const long MaxGifDecodedBytes = 160L * 1024L * 1024L;
		private const int MaxGifFrameWidth = 1280;
		private const int MaxGifFrameHeight = 720;
		private static readonly bool LowEndCpu = Environment.ProcessorCount <= 4;
		private readonly int _staticMaxWidth;
		private readonly int _staticMaxHeight;
		private readonly int _animatedMaxWidth;
		private readonly int _animatedMaxHeight;
		private const long MaxPortableGifDecodedBytes = 192L * 1024L * 1024L;
		private const long MaxImageEncodedBytes = 64L * 1024L * 1024L;
		private readonly string _path;
		private readonly Thread _thread;
		private readonly Lock _frameLock = new();
		private readonly Lock _videoProcessLock = new();
		private readonly Lock _portableGifLock = new();
		private readonly CancellationTokenSource _mediaCancellation = new();
		private Dispatcher? _dispatcher;
		private DispatcherTimer? _timer;
		private System.Threading.Timer? _portableGifTimer;
		private MediaPlayer? _player;
		private Process? _linuxVideoProcess;
		private VideoDrawing? _videoDrawing;
		private DrawingVisual? _videoVisual;
		private RenderTargetBitmap? _videoTarget;
		private List<Frame>? _gifFrames;
		private SixLabors.ImageSharp.Image<Bgra32>? _portableGifImage;
		private int[]? _portableGifDelays;
		private byte[]? _portableGifWritePixels;
		private int _gifIndex;
		private long _nextGifTick;
		private byte[]? _latestPixels;
		private byte[]? _videoWritePixels;
		private TimeSpan _lastVideoPosition = TimeSpan.MinValue;
		private int _latestWidth;
		private int _latestHeight;
		private long _version;
		private volatile bool _disposed;
		private int _linuxVideoErrorLogged;

		private readonly TimeSpan _tickInterval;

		private volatile bool _animated;

		public bool IsAnimated => _animated;

		public HomepageBackgroundMedia(string path, double refreshHz, int targetWidth = 0, int targetHeight = 0)
		{
			_path = path;
			int videoCapWidth = LowEndCpu ? 1280 : MaxAnimatedWidth;
			int videoCapHeight = LowEndCpu ? 720 : MaxAnimatedHeight;
			_staticMaxWidth = targetWidth > 0 ? Math.Clamp(targetWidth, 1280, MaxStaticWidth) : MaxStaticWidth;
			_staticMaxHeight = targetHeight > 0 ? Math.Clamp(targetHeight, 720, MaxStaticHeight) : MaxStaticHeight;
			_animatedMaxWidth = targetWidth > 0 ? Math.Clamp(targetWidth, 960, videoCapWidth) : videoCapWidth;
			_animatedMaxHeight = targetHeight > 0 ? Math.Clamp(targetHeight, 540, videoCapHeight) : videoCapHeight;
			double hz = refreshHz > 1.0 && refreshHz < 1000.0 ? refreshHz : 60.0;
			_tickInterval = TimeSpan.FromMilliseconds(1000.0 / hz);
			_thread = new Thread(Run)
			{
				IsBackground = true,
				Name = "Homepage Background Media",
				Priority = ThreadPriority.BelowNormal
			};
			if (!Voidstrap.Utility.Platform.IsLinux)
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
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				RunPortable();
				return;
			}
			Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
			_dispatcher = dispatcher;
			dispatcher.UnhandledException += OnDispatcherUnhandledException;
			try
			{
				if (_disposed)
					return;
				string extension = Path.GetExtension(_path).ToLowerInvariant();
				if (IsVideoExtension(extension))
					OpenVideo();
				else
					OpenImage(IsGif());
				if (!_disposed && (_timer != null || _player != null))
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
				_dispatcher = null;
			}
		}

		private void RunPortable()
		{
			try
			{
				if (_disposed)
					return;
				string extension = Path.GetExtension(_path).ToLowerInvariant();
				if (IsVideoExtension(extension))
					OpenVideo();
				else
					OpenImage(IsGif());
				if (!_disposed && _portableGifTimer != null)
					_mediaCancellation.Token.WaitHandle.WaitOne();
			}
			catch (OperationCanceledException) when (_disposed)
			{
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("HomepageBackgroundMedia::Run", ex);
			}
			finally
			{
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
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				if (animated)
					OpenPortableGif();
				else
					OpenPortableImage();
				return;
			}
			if (!animated)
			{
				BitmapSource? image = Voidstrap.Utility.SafeImaging.FromFile(_path, _staticMaxWidth);
				if (image != null)
					Publish(ToFrame(image, 1000, _staticMaxWidth, _staticMaxHeight));
				else
					App.Logger.WriteLine("HomepageBackgroundMedia", "The background image could not be decoded, nothing will show: " + _path);
				return;
			}
			using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
			if (decoder.Frames.Count == 0)
				return;
			if (decoder.Frames.Count == 1)
			{
				Publish(ToFrame(decoder.Frames[0], 1000, _staticMaxWidth, _staticMaxHeight));
				return;
			}
			List<Frame> frames = ComposeGifFrames(decoder, Math.Min(_animatedMaxWidth, MaxGifFrameWidth), Math.Min(_animatedMaxHeight, MaxGifFrameHeight), Publish);
			if (frames.Count == 0 || _disposed)
				return;
			_gifFrames = frames;
			_animated = frames.Count > 1;
			_gifIndex = 0;
			_nextGifTick = Environment.TickCount64 + frames[0].DelayMilliseconds;
			_timer = new DispatcherTimer(DispatcherPriority.Render)
			{
				Interval = _tickInterval
			};
			_timer.Tick += OnGifTick;
			_timer.Start();
		}

		private void OpenPortableImage()
		{
			CancellationToken token = _mediaCancellation.Token;
			DecoderOptions identifyOptions = new()
			{
				MaxFrames = 1,
				SkipMetadata = true
			};
			SixLabors.ImageSharp.ImageInfo? info = SixLabors.ImageSharp.Image.IdentifyAsync(identifyOptions, _path, token).GetAwaiter().GetResult();
			if (_disposed || info == null || info.Width < 1 || info.Height < 1)
				return;
			long sourceBytes = checked((long)info.Width * info.Height * 4);
			if (info.Width > MaxCanvasEdge || info.Height > MaxCanvasEdge || sourceBytes > MaxCanvasBytes)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "The static background canvas is too large for bounded Linux decoding");
				return;
			}
			(int targetWidth, int targetHeight) = FitSize(info.Width, info.Height, _staticMaxWidth, _staticMaxHeight);
			DecoderOptions decodeOptions = new()
			{
				MaxFrames = 1,
				SkipMetadata = true,
				TargetSize = new SixLabors.ImageSharp.Size(targetWidth, targetHeight)
			};
			using SixLabors.ImageSharp.Image<Bgra32> image = SixLabors.ImageSharp.Image.LoadAsync<Bgra32>(decodeOptions, _path, token).GetAwaiter().GetResult();
			if (_disposed || image.Frames.Count == 0)
				return;
			if (image.Width != targetWidth || image.Height != targetHeight)
				image.Mutate(context => context.Resize(targetWidth, targetHeight));
			long decodedBytes = checked((long)image.Width * image.Height * 4);
			if (decodedBytes > MaxCanvasBytes)
				return;
			byte[] pixels = GC.AllocateUninitializedArray<byte>(checked((int)decodedBytes));
			image.Frames.RootFrame.CopyPixelDataTo(pixels);
			Publish(new Frame(pixels, image.Width, image.Height, 1000));
		}

		private void OpenVideo()
		{
			_animated = true;
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				OpenLinuxVideo();
				return;
			}
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
				Interval = TimeSpan.FromMilliseconds(Math.Max(_tickInterval.TotalMilliseconds, 1000.0 / 60.0))
			};
			_timer.Tick += OnVideoTick;
			_timer.Start();
		}

		private void OpenPortableGif()
		{
			CancellationToken token = _mediaCancellation.Token;
			DecoderOptions identifyOptions = new()
			{
				MaxFrames = MaxGifFrames,
				SkipMetadata = false
			};
			SixLabors.ImageSharp.ImageInfo? info = SixLabors.ImageSharp.Image.IdentifyAsync(identifyOptions, _path, token).GetAwaiter().GetResult();
			if (_disposed || info == null || info.Width < 1 || info.Height < 1)
				return;
			int frameCount = Math.Clamp(info.FrameMetadataCollection.Count, 1, MaxGifFrames);
			long sourceFrameBytes = checked((long)info.Width * info.Height * 4);
			long sourceDecodeBudget = MaxPortableGifDecodedBytes / 2;
			if (sourceFrameBytes > sourceDecodeBudget)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "The animation canvas is too large for bounded Linux decoding");
				return;
			}
			frameCount = Math.Min(frameCount, Math.Max(1, (int)(sourceDecodeBudget / sourceFrameBytes)));
			(int targetWidth, int targetHeight) = FitSize(info.Width, info.Height, Math.Min(_animatedMaxWidth, MaxGifFrameWidth), Math.Min(_animatedMaxHeight, MaxGifFrameHeight));
			long requestedBytes = checked((long)targetWidth * targetHeight * 4 * frameCount);
			if (requestedBytes > MaxPortableGifDecodedBytes)
			{
				double scale = Math.Sqrt(MaxPortableGifDecodedBytes / (double)requestedBytes);
				targetWidth = Math.Max(1, (int)Math.Floor(targetWidth * scale));
				targetHeight = Math.Max(1, (int)Math.Floor(targetHeight * scale));
			}
			long frameBytes = checked((long)targetWidth * targetHeight * 4);
			frameCount = Math.Min(frameCount, Math.Max(1, (int)(MaxPortableGifDecodedBytes / frameBytes)));
			DecoderOptions decodeOptions = new()
			{
				MaxFrames = (uint)frameCount,
				SkipMetadata = false,
				TargetSize = new SixLabors.ImageSharp.Size(targetWidth, targetHeight)
			};
			SixLabors.ImageSharp.Image<Bgra32> image = SixLabors.ImageSharp.Image.LoadAsync<Bgra32>(decodeOptions, _path, token).GetAwaiter().GetResult();
			if (_disposed || image.Frames.Count == 0)
			{
				image.Dispose();
				return;
			}
			long decodedBytes = checked((long)image.Width * image.Height * 4 * image.Frames.Count);
			if (decodedBytes > MaxPortableGifDecodedBytes)
			{
				image.Dispose();
				return;
			}
			int[] delays = new int[image.Frames.Count];
			for (int index = 0; index < delays.Length; index++)
				delays[index] = ReadPortableGifDelay(image.Frames[index]);

			lock (_portableGifLock)
			{
				if (_disposed)
				{
					image.Dispose();
					return;
				}
				_portableGifImage = image;
				_portableGifDelays = delays;
				_animated = image.Frames.Count > 1;
				_gifIndex = 0;
				PublishPortableGifFrameLocked(0);
				if (image.Frames.Count > 1)
					_portableGifTimer = new System.Threading.Timer(OnPortableGifTick, null, delays[0], System.Threading.Timeout.Infinite);
			}
		}

		private void OnPortableGifTick(object? state)
		{
			lock (_portableGifLock)
			{
				SixLabors.ImageSharp.Image<Bgra32>? image = _portableGifImage;
				int[]? delays = _portableGifDelays;
				if (_disposed || image == null || delays == null || image.Frames.Count < 2)
					return;
				_gifIndex = (_gifIndex + 1) % image.Frames.Count;
				PublishPortableGifFrameLocked(_gifIndex);
				try
				{
					_portableGifTimer?.Change(delays[_gifIndex], System.Threading.Timeout.Infinite);
				}
				catch (ObjectDisposedException)
				{
				}
			}
		}

		private void PublishPortableGifFrameLocked(int index)
		{
			SixLabors.ImageSharp.Image<Bgra32>? image = _portableGifImage;
			if (image == null)
				return;
			int length = checked(image.Width * image.Height * 4);
			byte[] pixels = _portableGifWritePixels != null && _portableGifWritePixels.Length == length
				? _portableGifWritePixels
				: GC.AllocateUninitializedArray<byte>(length);
			_portableGifWritePixels = null;
			image.Frames[index].CopyPixelDataTo(pixels);
			lock (_frameLock)
			{
				byte[]? previous = _latestPixels;
				_latestPixels = pixels;
				_latestWidth = image.Width;
				_latestHeight = image.Height;
				_version++;
				_portableGifWritePixels = previous != null && previous.Length == length ? previous : null;
			}
		}

		private static int ReadPortableGifDelay(SixLabors.ImageSharp.ImageFrame<Bgra32> frame)
		{
			try
			{
				GifFrameMetadata metadata = SixLabors.ImageSharp.MetadataExtensions.GetGifMetadata(frame.Metadata);
				int delay = metadata.FrameDelay * 10;
				return delay <= 10 ? 100 : Math.Clamp(delay, 20, 10000);
			}
			catch (Exception)
			{
				return 100;
			}
		}

		private void OpenLinuxVideo()
		{
			string? executable = new SystemProcessService().FindExecutable("gst-launch-1.0");
			if (string.IsNullOrWhiteSpace(executable))
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "GStreamer is not installed, so the video cannot be decoded");
				return;
			}

			bool probed = TryProbeLinuxVideoSize(executable, out int width, out int height);
			if (!probed)
			{
				width = 960;
				height = 540;
			}
			int frameSize = checked(width * height * 4);
			int consecutiveFailures = 0;
			while (!_disposed)
			{
				using Process process = CreateLinuxVideoProcess(executable, width, height, !probed);
				bool deliveredFrame = false;

				try
				{
					process.ErrorDataReceived += OnLinuxVideoError;
					if (!TryStartLinuxVideoProcess(process))
						return;
					process.BeginErrorReadLine();
					byte[] pixels = new byte[frameSize];
					while (!_disposed && ReadFrame(process.StandardOutput.BaseStream, pixels))
					{
						deliveredFrame = true;
						PublishVideo(pixels, width, height);
						lock (_frameLock)
						{
							pixels = _videoWritePixels != null && _videoWritePixels.Length == frameSize
								? _videoWritePixels
								: new byte[frameSize];
							_videoWritePixels = null;
						}
					}
					if (!_disposed)
						process.WaitForExit(1000);
				}
				catch (Exception ex) when (!_disposed)
				{
					App.Logger.WriteLine("HomepageBackgroundMedia", "GStreamer video decoding stopped: " + ex.Message);
				}
				finally
				{
					process.ErrorDataReceived -= OnLinuxVideoError;
					try
					{
						if (!process.HasExited)
							process.Kill(true);
					}
					catch
					{
					}
					lock (_videoProcessLock)
					{
						if (ReferenceEquals(_linuxVideoProcess, process))
							_linuxVideoProcess = null;
					}
				}

				if (_disposed)
					return;
				consecutiveFailures = deliveredFrame ? 0 : Math.Min(consecutiveFailures + 1, 4);
				int retryDelay = deliveredFrame ? 100 : Math.Min(5000, 500 << consecutiveFailures);
				if (_mediaCancellation.Token.WaitHandle.WaitOne(retryDelay))
					return;
			}
		}

		private bool TryProbeLinuxVideoSize(string executable, out int width, out int height)
		{
			width = 0;
			height = 0;
			using Process process = CreateLinuxVideoProbeProcess(executable);
			try
			{
				if (!TryStartLinuxVideoProcess(process))
					return false;
				Task<string> outputTask = process.StandardOutput.ReadToEndAsync(_mediaCancellation.Token);
				Task<string> errorTask = process.StandardError.ReadToEndAsync(_mediaCancellation.Token);
				if (!process.WaitForExit(5000))
				{
					process.Kill(true);
					return false;
				}
				string output = outputTask.GetAwaiter().GetResult();
				_ = errorTask.GetAwaiter().GetResult();
				foreach (string line in output.Split('\n'))
				{
					if (!line.Contains("GstFakeSink:voidstrapprobe.GstPad:sink", StringComparison.Ordinal)
						|| !TryReadCapsValue(line, "width=(int)", out width)
						|| !TryReadCapsValue(line, "height=(int)", out height))
						continue;
					return width > 0 && height > 0 && width <= 960 && height <= 540;
				}
			}
			catch (OperationCanceledException) when (_disposed)
			{
			}
			catch (Exception ex) when (!_disposed)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "GStreamer size detection failed: " + ex.Message);
			}
			finally
			{
				try
				{
					if (!process.HasExited)
						process.Kill(true);
				}
				catch
				{
				}
				ClearLinuxVideoProcess(process);
			}
			width = 0;
			height = 0;
			return false;
		}

		private Process CreateLinuxVideoProbeProcess(string executable)
		{
			ProcessStartInfo startInfo = new(executable)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (string argument in new[]
			{
				"-v",
				"filesrc",
				"location=" + _path,
				"!",
				"decodebin",
				"!",
				"videoconvert",
				"!",
				"videoscale",
				"add-borders=false",
				"!",
				"videorate",
				"!",
				"video/x-raw,format=(string)BGRA,width=(int)[1,960],height=(int)[1,540],framerate=(fraction)30/1,pixel-aspect-ratio=(fraction)1/1",
				"!",
				"fakesink",
				"name=voidstrapprobe",
				"num-buffers=1",
				"sync=false"
			})
			{
				startInfo.ArgumentList.Add(argument);
			}
			return new Process { StartInfo = startInfo };
		}

		private static bool TryReadCapsValue(string line, string marker, out int value)
		{
			value = 0;
			int start = line.IndexOf(marker, StringComparison.Ordinal);
			if (start < 0)
				return false;
			start += marker.Length;
			int end = start;
			while (end < line.Length && char.IsDigit(line[end]))
				end++;
			return end > start && int.TryParse(line.AsSpan(start, end - start), out value);
		}

		private bool TryStartLinuxVideoProcess(Process process)
		{
			lock (_videoProcessLock)
			{
				if (_disposed || !process.Start())
					return false;
				_linuxVideoProcess = process;
				return true;
			}
		}

		private void ClearLinuxVideoProcess(Process process)
		{
			lock (_videoProcessLock)
			{
				if (ReferenceEquals(_linuxVideoProcess, process))
					_linuxVideoProcess = null;
			}
		}

		private Process CreateLinuxVideoProcess(string executable, int width, int height, bool addBorders)
		{
			ProcessStartInfo startInfo = new(executable)
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true
			};
			foreach (string argument in new[]
			{
				"-q",
				"filesrc",
				"location=" + _path,
				"!",
				"decodebin",
				"!",
				"videoconvert",
				"!",
				"videoscale",
				"add-borders=" + (addBorders ? "true" : "false"),
				"!",
				"video/x-raw,format=BGRA,width=" + width + ",height=" + height,
				"!",
				"fdsink",
				"fd=1",
				"sync=true"
			})
			{
				startInfo.ArgumentList.Add(argument);
			}
			return new Process { StartInfo = startInfo };
		}

		private static bool ReadFrame(Stream stream, byte[] pixels)
		{
			int offset = 0;
			while (offset < pixels.Length)
			{
				int read = stream.Read(pixels, offset, pixels.Length - offset);
				if (read == 0)
					return false;
				offset += read;
			}
			return true;
		}

		private void OnLinuxVideoError(object sender, DataReceivedEventArgs e)
		{
			if (!string.IsNullOrWhiteSpace(e.Data) && System.Threading.Interlocked.Exchange(ref _linuxVideoErrorLogged, 1) == 0)
				App.Logger.WriteLine("HomepageBackgroundMedia", "GStreamer: " + e.Data);
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
			(int width, int height) = FitSize(_player.NaturalVideoWidth, _player.NaturalVideoHeight, _animatedMaxWidth, _animatedMaxHeight);
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

		private List<Frame> ComposeGifFrames(BitmapDecoder decoder, int maxFrameWidth, int maxFrameHeight, Action<Frame> onFirstFrame)
		{
			List<Frame> frames = [];
			(int canvasWidth, int canvasHeight) = ReadLogicalScreen(decoder);
			if (canvasWidth < 1 || canvasHeight < 1 || canvasWidth > MaxCanvasEdge || canvasHeight > MaxCanvasEdge)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "The animation canvas is " + canvasWidth + "x" + canvasHeight + ", falling back to the first frame");
				TryAddSingleFrame(decoder, frames, maxFrameWidth, maxFrameHeight);
				return frames;
			}

			int stride = canvasWidth * 4;
			long canvasBytes = (long)stride * canvasHeight;
			if (canvasBytes > MaxCanvasBytes)
			{
				App.Logger.WriteLine("HomepageBackgroundMedia", "The animation canvas needs " + canvasBytes + " bytes, falling back to the first frame");
				TryAddSingleFrame(decoder, frames, maxFrameWidth, maxFrameHeight);
				return frames;
			}

			byte[] canvas = new byte[canvasBytes];
			byte[]? saved = null;
			long decodedBytes = 0;
			int count = Math.Min(decoder.Frames.Count, MaxGifFrames);
			(int fitWidth, int fitHeight) = FitSize(canvasWidth, canvasHeight, maxFrameWidth, maxFrameHeight);
			long perFrame = (long)fitWidth * fitHeight * 4;
			if (perFrame * count > MaxGifDecodedBytes)
			{
				double shrink = Math.Max(0.5, Math.Sqrt(MaxGifDecodedBytes / (double)(perFrame * count)));
				maxFrameWidth = Math.Max(1, (int)Math.Round(fitWidth * shrink));
				maxFrameHeight = Math.Max(1, (int)Math.Round(fitHeight * shrink));
			}

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
					Frame frame = ToFrame(snapshot, ReadGifDelay(source), maxFrameWidth, maxFrameHeight);
					long frameBytes = frame.Pixels.LongLength;
					if (decodedBytes > MaxGifDecodedBytes - frameBytes)
					{
						App.Logger.WriteLine("HomepageBackgroundMedia", "The animation was cut to " + frames.Count + " of " + count + " frames to stay within the memory budget");
						break;
					}
					decodedBytes += frameBytes;
					frames.Add(frame);
					if (frames.Count == 1)
						onFirstFrame(frame);
					if (_disposed)
						break;

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
				TryAddSingleFrame(decoder, frames, maxFrameWidth, maxFrameHeight);
			return frames;
		}

		private static void TryAddSingleFrame(BitmapDecoder decoder, List<Frame> frames, int maxFrameWidth, int maxFrameHeight)
		{
			try
			{
				if (decoder.Frames.Count > 0)
					frames.Add(ToFrame(decoder.Frames[0], 1000, maxFrameWidth, maxFrameHeight));
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
			lock (_portableGifLock)
			{
				Interlocked.Exchange(ref _portableGifTimer, null)?.Dispose();
				_portableGifImage?.Dispose();
				_portableGifImage = null;
				_portableGifDelays = null;
				_portableGifWritePixels = null;
				_gifFrames = null;
			}
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
			StopLinuxVideoProcess();
		}

		private void StopLinuxVideoProcess()
		{
			lock (_videoProcessLock)
			{
				try
				{
					if (_linuxVideoProcess is { HasExited: false } process)
						process.Kill(true);
				}
				catch
				{
				}
			}
		}

		private void StopDispatcher()
		{
			Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
		}

		public void Dispose()
		{
			lock (_portableGifLock)
			{
				if (_disposed)
					return;
				_disposed = true;
				if (Voidstrap.Utility.Platform.IsLinux)
					_mediaCancellation.Cancel();
				Interlocked.Exchange(ref _portableGifTimer, null)?.Dispose();
				_portableGifImage?.Dispose();
				_portableGifImage = null;
				_portableGifDelays = null;
				_portableGifWritePixels = null;
				_gifFrames = null;
			}
			StopLinuxVideoProcess();
			Dispatcher? dispatcher = _dispatcher;
			if (dispatcher != null && !dispatcher.HasShutdownStarted)
				dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(StopDispatcher));
			if (_thread.IsAlive && Thread.CurrentThread != _thread)
			{
				TimeSpan timeout = Voidstrap.Utility.Platform.IsLinux
					? TimeSpan.FromMilliseconds(100)
					: TimeSpan.FromSeconds(2);
				_thread.Join(timeout);
			}
			if (!_thread.IsAlive)
				_mediaCancellation.Dispose();
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
