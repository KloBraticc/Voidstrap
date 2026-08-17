using System;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using ApiInformation = Windows.Foundation.Metadata.ApiInformation;

namespace Voidstrap.Integrations.RiShade
{
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    internal sealed partial class RiShadeWgc : IDisposable
    {
        private const string LOG_IDENT = "RiShade";
        private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        private static readonly bool SupportsMinUpdateInterval = ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "MinUpdateInterval");

        [LibraryImport("d3d11.dll")]
        private static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        private IDirect3DDevice? _winrtDevice;
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _framePool;
        private GraphicsCaptureSession? _session;
        private readonly AutoResetEvent _frameEvent = new(false);
        private int _width;
        private int _height;
        private bool _closed;
        private double _targetFps;
		private int _disposed;

        public int Width => _width;
        public int Height => _height;
        public bool IsClosed => _closed;

        public static RiShadeWgc? TryCreate(ID3D11Device device, IntPtr targetHwnd, double targetFps = 0)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                return null;
            try
            {
                var wgc = new RiShadeWgc();
                if (!wgc.Initialize(device, targetHwnd, targetFps))
                {
                    wgc.Dispose();
                    return null;
                }
                return wgc;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Window capture unavailable, falling back to monitor capture: " + ex.Message);
                return null;
            }
        }

        private bool Initialize(ID3D11Device device, IntPtr targetHwnd, double targetFps)
        {
            if (!GraphicsCaptureSession.IsSupported())
                return false;

            using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            {
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
                if (hr < 0)
                    return false;
                _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                Marshal.Release(inspectable);
            }

            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            Guid iid = GraphicsCaptureItemIid;
            IntPtr itemAbi = interop.CreateForWindow(targetHwnd, ref iid);
            _item = GraphicsCaptureItem.FromAbi(itemAbi);
            Marshal.Release(itemAbi);
            if (_item == null)
                return false;

            _item.Closed += Item_Closed;
            _width = Math.Max(16, _item.Size.Width);
            _height = Math.Max(16, _item.Size.Height);
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = _width, Height = _height });
            _framePool.FrameArrived += FramePool_FrameArrived;
            _session = _framePool.CreateCaptureSession(_item);
            SetTargetFps(targetFps);
            try
            {
                _session.IsCursorCaptureEnabled = false;
            }
            catch
            {
            }
            try
            {
                _session.IsBorderRequired = false;
            }
            catch
            {
            }
            _session.StartCapture();
            string cadence = _targetFps > 0 ? $", requested up to {_targetFps:0} FPS" : "";
            App.Logger.WriteLine(LOG_IDENT, $"Window capture active at {_width}x{_height}{cadence}, visible to recording software");
            return true;
        }

        public void SetTargetFps(double targetFps)
        {
            if (_session == null || !SupportsMinUpdateInterval)
                return;
            targetFps = targetFps > 0 ? Math.Clamp(targetFps, 30.0, 500.0) : 0;
            if (Math.Abs(targetFps - _targetFps) < 0.5)
                return;
            try
            {
                _session.MinUpdateInterval = targetFps > 0 ? TimeSpan.FromSeconds(1.0 / targetFps) : TimeSpan.Zero;
                _targetFps = targetFps;
            }
            catch
            {
            }
        }

        private void Item_Closed(GraphicsCaptureItem sender, object args)
        {
			if (Volatile.Read(ref _disposed) != 0)
				return;
            _closed = true;
            try
            {
                _frameEvent.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private long _lastUsedPresentTicks;

        private long _droppedTotal;

        public long DroppedCount => System.Threading.Interlocked.Read(ref _droppedTotal);

        private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
			if (Volatile.Read(ref _disposed) != 0)
				return;
            try
            {
                _frameEvent.Set();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void WaitForFrame(int timeoutMs)
        {
            try
            {
                _frameEvent.WaitOne(timeoutMs);
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public bool TryCopyLatestFrame(ID3D11DeviceContext context, ID3D11Texture2D destination, int destWidth, int destHeight)
        {
            return TryCopyLatestFrame(context, destination, destWidth, destHeight, out _);
        }

        public bool TryCopyLatestFrame(ID3D11DeviceContext context, ID3D11Texture2D destination, int destWidth, int destHeight, out double sourceTimeMs)
        {
            sourceTimeMs = 0;
            if (_framePool == null || _closed)
                return false;
            Direct3D11CaptureFrame? frame = null;
            try
            {
                int dropped = 0;
                while (true)
                {
                    Direct3D11CaptureFrame? next = _framePool.TryGetNextFrame();
                    if (next == null)
                        break;
                    if (frame != null)
                    {
                        frame.Dispose();
                        dropped++;
                    }
                    frame = next;
                }
                if (dropped > 0)
                    Interlocked.Add(ref _droppedTotal, dropped);
                if (frame == null)
                    return false;
                sourceTimeMs = frame.SystemRelativeTime.Ticks / 10000.0;

                long presentTicks = frame.SystemRelativeTime.Ticks;
                if (presentTicks == _lastUsedPresentTicks)
                    return false;
                _lastUsedPresentTicks = presentTicks;

                var size = frame.ContentSize;
                if (size.Width != _width || size.Height != _height)
                {
                    _width = Math.Max(16, size.Width);
                    _height = Math.Max(16, size.Height);
                    frame.Dispose();
                    frame = null;
                    _framePool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, new SizeInt32 { Width = _width, Height = _height });
                    return false;
                }

                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                Guid iid = Texture2DIid;
                IntPtr texPtr = access.GetInterface(ref iid);
                using var frameTex = new ID3D11Texture2D(texPtr);
                var desc = frameTex.Description;
                int w = Math.Min(destWidth, (int)desc.Width);
                int h = Math.Min(destHeight, (int)desc.Height);
                var box = new Box(0, 0, 0, w, h, 1);
                context.CopySubresourceRegion(destination, 0, 0, 0, 0, frameTex, 0, box);
                return true;
            }
            finally
            {
                frame?.Dispose();
            }
        }

        public void Dispose()
        {
			if (Interlocked.Exchange(ref _disposed, 1) != 0)
				return;
			_closed = true;
			try { if (_item != null) _item.Closed -= Item_Closed; } catch { }
			try { if (_framePool != null) _framePool.FrameArrived -= FramePool_FrameArrived; } catch { }
			try { _session?.Dispose(); } catch { }
			try { _framePool?.Dispose(); } catch { }
			try { _winrtDevice?.Dispose(); } catch { }
            _session = null;
            _framePool = null;
            _item = null;
            _winrtDevice = null;
            _frameEvent.Dispose();
			GC.SuppressFinalize(this);
        }
    }
}
