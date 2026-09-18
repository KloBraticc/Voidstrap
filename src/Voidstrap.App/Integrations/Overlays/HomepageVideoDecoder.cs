using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Voidstrap.Integrations.Overlays
{
	internal sealed class HomepageVideoDecoder : IDisposable
	{
		private readonly record struct QueuedFrame(IMFSample Sample, double DueMs);

		private const int QueueDepth = 3;
		private const double ResyncLateMs = 250.0;
		private static int _mediaFoundationStarted;

		private readonly IMFDXGIDeviceManager _manager;
		private readonly IMFSourceReader _reader;
		private readonly ID3D11Texture2D _texture;
		private readonly Thread _thread;
		private readonly CancellationTokenSource _cancellation = new();
		private readonly AutoResetEvent _consumed = new(false);
		private readonly Lock _gate = new();
		private readonly Queue<QueuedFrame> _queue = new();
		private readonly Stopwatch _clock = new();
		private IMFSample? _current;
		private double _shiftMs;
		private volatile bool _failed;
		private bool _disposed;

		public int Width { get; }

		public int Height { get; }

		public ID3D11ShaderResourceView Srv { get; }

		public bool HasFrame { get; private set; }

		public bool Failed => _failed;

		public HomepageVideoDecoder(ID3D11Device device, string path)
		{
			if (Interlocked.Exchange(ref _mediaFoundationStarted, 1) == 0)
				MediaFactory.MFStartup(false).CheckError();

			using (ID3D11Multithread multithread = device.QueryInterface<ID3D11Multithread>())
				multithread.SetMultithreadProtected(true);

			_manager = MediaFactory.MFCreateDXGIDeviceManager();
			IMFSourceReader? reader = null;
			try
			{
				_manager.ResetDevice(device).CheckError();
				using (IMFAttributes attributes = MediaFactory.MFCreateAttributes(3))
				{
					attributes.Set(SourceReaderAttributeKeys.D3DManager, _manager);
					attributes.Set(SourceReaderAttributeKeys.EnableAdvancedVideoProcessing, true);
					attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, true);
					reader = MediaFactory.MFCreateSourceReaderFromURL(path, attributes);
				}
				reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
				reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
				using (IMFMediaType type = MediaFactory.MFCreateMediaType())
				{
					type.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
					type.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Argb32);
					reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, type);
				}
				using (IMFMediaType current = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
				{
					ulong size = current.GetUInt64(MediaTypeAttributeKeys.FrameSize);
					Width = (int)(size >> 32);
					Height = (int)(size & 0xFFFFFFFF);
				}
				if (Width < 1 || Height < 1)
					throw new InvalidOperationException("The video has no frame size");
				_texture = device.CreateTexture2D(new Texture2DDescription
				{
					Width = (uint)Width,
					Height = (uint)Height,
					MipLevels = 1,
					ArraySize = 1,
					Format = Format.B8G8R8A8_UNorm,
					SampleDescription = new SampleDescription(1, 0),
					Usage = ResourceUsage.Default,
					BindFlags = BindFlags.ShaderResource,
					CPUAccessFlags = CpuAccessFlags.None,
				});
				Srv = device.CreateShaderResourceView(_texture);
			}
			catch
			{
				reader?.Dispose();
				_manager.Dispose();
				throw;
			}
			_reader = reader;
			_thread = new Thread(Decode)
			{
				IsBackground = true,
				Name = "Homepage Video Decode",
				Priority = ThreadPriority.BelowNormal
			};
			_thread.Start();
		}

		private void Decode()
		{
			CancellationToken token = _cancellation.Token;
			double loopBaseMs = 0;
			long loopFirstTicks = -1;
			long lastTicks = 0;
			long lastDurationTicks = 0;
			int framesThisLoop = 0;
			try
			{
				while (!token.IsCancellationRequested)
				{
					bool full;
					lock (_gate)
						full = _queue.Count >= QueueDepth;
					if (full)
					{
						_consumed.WaitOne(8);
						continue;
					}

					IMFSample? sample = _reader.ReadSample(SourceReaderIndex.FirstVideoStream, 0, out _, out SourceReaderFlag flags, out long timestamp);
					if ((flags & SourceReaderFlag.Error) != 0)
					{
						sample?.Dispose();
						App.Logger.WriteLine("HomepageVideoDecoder", "The hardware video decoder reported an error, falling back to the software player");
						_failed = true;
						return;
					}
					if ((flags & SourceReaderFlag.EndOfStream) != 0)
					{
						sample?.Dispose();
						if (framesThisLoop == 0)
						{
							App.Logger.WriteLine("HomepageVideoDecoder", "The video produced no frames, falling back to the software player");
							_failed = true;
							return;
						}
						loopBaseMs += (lastTicks - loopFirstTicks + Math.Max(lastDurationTicks, 1)) / 10000.0;
						loopFirstTicks = -1;
						framesThisLoop = 0;
						_reader.SetCurrentPosition(0);
						continue;
					}
					if (sample == null)
						continue;
					if (loopFirstTicks < 0)
						loopFirstTicks = timestamp;
					lastTicks = timestamp;
					lastDurationTicks = sample.SampleDuration;
					framesThisLoop++;
					double due = loopBaseMs + (timestamp - loopFirstTicks) / 10000.0;
					lock (_gate)
					{
						if (_disposed)
						{
							sample.Dispose();
							return;
						}
						if (!_clock.IsRunning)
							_clock.Start();
						_queue.Enqueue(new QueuedFrame(sample, due));
					}
				}
			}
			catch (Exception ex)
			{
				if (!token.IsCancellationRequested)
				{
					App.Logger.WriteLine("HomepageVideoDecoder", "Hardware video decoding stopped, falling back to the software player: " + ex.Message);
					_failed = true;
				}
			}
		}

		public bool Update(ID3D11DeviceContext context)
		{
			IMFSample? next = null;
			lock (_gate)
			{
				if (_disposed || !_clock.IsRunning)
					return false;
				double now = _clock.Elapsed.TotalMilliseconds;
				while (_queue.Count > 0 && _queue.Peek().DueMs + _shiftMs <= now)
				{
					QueuedFrame frame = _queue.Dequeue();
					if (next != null)
						next.Dispose();
					next = frame.Sample;
					if (now - (frame.DueMs + _shiftMs) > ResyncLateMs)
						_shiftMs = now - frame.DueMs;
				}
				if (next == null)
					return false;
				_consumed.Set();
				_current?.Dispose();
				_current = next;
			}
			using IMFMediaBuffer buffer = next.GetBufferByIndex(0);
			using IMFDXGIBuffer dxgiBuffer = buffer.QueryInterface<IMFDXGIBuffer>();
			using ID3D11Texture2D source = new(dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID));
			context.CopySubresourceRegion(_texture, 0, 0, 0, 0, source, dxgiBuffer.SubresourceIndex, new Vortice.Mathematics.Box(0, 0, 0, Width, Height, 1));
			HasFrame = true;
			return true;
		}

		public void Dispose()
		{
			lock (_gate)
			{
				if (_disposed)
					return;
				_disposed = true;
			}
			_cancellation.Cancel();
			_consumed.Set();
			if (_thread.IsAlive && Thread.CurrentThread != _thread)
				_thread.Join(2000);
			lock (_gate)
			{
				while (_queue.Count > 0)
					_queue.Dequeue().Sample.Dispose();
				_current?.Dispose();
				_current = null;
			}
			_reader.Dispose();
			Srv.Dispose();
			_texture.Dispose();
			_manager.Dispose();
			_consumed.Dispose();
			_cancellation.Dispose();
		}
	}
}
