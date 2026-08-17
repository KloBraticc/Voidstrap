using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Voidstrap.Integrations.Overlays;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.DirectComposition;
using Vortice.Mathematics;
using D3D11 = Vortice.Direct3D11.D3D11;
using DCompApi = Vortice.DirectComposition.DComp;

namespace Voidstrap.Integrations.AntiAliasing
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AntiAliasingParams
    {
        public Vector4 Dims;
        public Vector4 SrcRect;
    }

    internal sealed class AntiAliasingOverlay
    {
        private const string ClassName = "VoidstrapAntiAliasingOverlay";
        private const string LOG_IDENT = "AntiAliasing";

        private AntiAliasingInterop.WndProcDelegate? _wndProc;
        private IntPtr _hwnd;
        private ushort _classAtom;
        private IntPtr _hInstance;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIFactory2? _factory;
        private IDXGISwapChain1? _swapChain;
        private ID3D11RenderTargetView? _backBufferRtv;
        private IDCompositionDevice? _dcompDevice;
        private IDCompositionTarget? _dcompTarget;
        private IDCompositionVisual? _dcompVisual;

        private IDXGIOutputDuplication? _duplication;
        private bool _hasFirstCapture;
        private int _outputLeft;
        private int _outputTop;
        private int _outputRight;
        private int _outputBottom;
        private int _captureFailures;
        private bool _deviceLost;
        private int _stableCaptureFrames;
        private long _captureUnstableSinceMs;
        private long _lastRecreateMs;

        private ID3D11Texture2D? _inputTex;
        private ID3D11ShaderResourceView? _inputSrv;
        private ID3D11RenderTargetView? _inputRtv;
        private Vector4 _dims;
        private ID3D11Texture2D? _workTexA;
        private ID3D11ShaderResourceView? _workSrvA;
        private ID3D11RenderTargetView? _workRtvA;
        private ID3D11Texture2D? _workTexB;
        private ID3D11ShaderResourceView? _workSrvB;
        private ID3D11RenderTargetView? _workRtvB;

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psPass;
        private ID3D11PixelShader? _psCropSrgb;
        private ID3D11PixelShader? _psFxaa;
        private ID3D11PixelShader? _psFxaaUltra;
        private ID3D11PixelShader? _psSmaaEdge;
        private ID3D11PixelShader? _psSmaaWeights;
        private ID3D11PixelShader? _psSmaaWeightsUltra;
        private ID3D11PixelShader? _psSmaaBlend;
        private ID3D11PixelShader? _psDlaaMask;
        private ID3D11PixelShader? _psDlaa;
        private ID3D11PixelShader? _psNfaa;
        private ID3D11PixelShader? _psTsaa;
        private ID3D11PixelShader? _psTsaaSharpen;
        private ID3D11SamplerState? _sampler;
        private ID3D11Buffer? _cbuffer;
        private bool _timerRaised;

        private int _width;
        private int _height;
        private int _rectLeft;
        private int _rectTop;

        private IntPtr _robloxHwnd;
        private bool _hiddenByFocus;
        private int _lastMethodRendered = -1;
        private int _pendingW;
        private int _pendingH;
        private int _tsaaHistory;
        private bool _tsaaSeeded;
        private long _framesPresented;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastHwndResolve;
        private double _lastStatsLog;
        private long _framesAtLastLog;
		private long _nextVisibilityCheckMs;
		private long _nextFollowCheckMs;
		private long _nextZOrderCheckMs;
		private int _cleanedUp;
		private CancellationToken _runToken;

        public void Run(CancellationToken token)
        {
			_runToken = token;
            try
            {
                ResolveRobloxHwnd();
                if (!TryGetRobloxRect(out var rect))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Roblox window disappeared before overlay start");
                    return;
                }
                _rectLeft = rect.Left;
                _rectTop = rect.Top;
                _width = Math.Max(16, rect.Right - rect.Left);
                _height = Math.Max(16, rect.Bottom - rect.Top);
                App.Logger.WriteLine(LOG_IDENT, $"Starting overlay for Roblox at {_rectLeft},{_rectTop} size {_width}x{_height}");

                CreateWindow();
                CreateDevice();
                ResolveRobloxHwnd();
                if (!CreateDuplicationForRect(_rectLeft, _rectTop))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not create desktop duplication for the Roblox monitor, overlay aborted");
                    return;
                }
                CreateComposition();
                CreatePipeline();
                AntiAliasingInterop.timeBeginPeriod(1);
                _timerRaised = true;
                _captureFailures = 0;
                _stableCaptureFrames = 0;
                _captureUnstableSinceMs = 0;
                _lastRecreateMs = 0;

                var msg = default(AntiAliasingInterop.MSG);
                while (!token.IsCancellationRequested)
                {
                    while (AntiAliasingInterop.PeekMessageW(out msg, IntPtr.Zero, 0, 0, AntiAliasingInterop.PM_REMOVE))
                    {
                        AntiAliasingInterop.TranslateMessage(ref msg);
                        AntiAliasingInterop.DispatchMessageW(ref msg);
                    }

                    if (_deviceLost)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Restarting the overlay session to recover");
                        break;
                    }

                    if (!UpdateVisibility(token))
                        continue;

					long now = Environment.TickCount64;
					if (now >= _nextFollowCheckMs)
					{
						_nextFollowCheckMs = now + 500;
						FollowRoblox();
					}
					if (now >= _nextZOrderCheckMs)
					{
						_nextZOrderCheckMs = now + 1000;
						AssertZOrder();
					}
                    RenderFrame();
                    LogStatsIfDue();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AntiAliasingOverlay::Run", ex);
            }
            finally
            {
                Cleanup();
            }
        }

        private void CreateWindow()
        {
            _hInstance = AntiAliasingInterop.GetModuleHandleW(null);
            _wndProc = (h, m, w, l) => AntiAliasingInterop.DefWindowProcW(h, m, w, l);
            IntPtr classNamePtr = Marshal.StringToHGlobalUni(ClassName);
            try
            {
                var wc = new AntiAliasingInterop.WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<AntiAliasingInterop.WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = _hInstance,
                    lpszClassName = classNamePtr,
                };
                _classAtom = AntiAliasingInterop.RegisterClassExW(ref wc);

                int exStyle = AntiAliasingInterop.WS_EX_NOACTIVATE | AntiAliasingInterop.WS_EX_TOOLWINDOW | AntiAliasingInterop.WS_EX_TRANSPARENT | AntiAliasingInterop.WS_EX_TOPMOST | AntiAliasingInterop.WS_EX_LAYERED | AntiAliasingInterop.WS_EX_NOREDIRECTIONBITMAP;
                _hwnd = AntiAliasingInterop.CreateWindowExW(exStyle, new IntPtr(_classAtom), ClassName, AntiAliasingInterop.WS_POPUP, _rectLeft, _rectTop, _width, _height, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

                AntiAliasingInterop.SetLayeredWindowAttributes(_hwnd, 0, 255, AntiAliasingInterop.LWA_ALPHA);
                AntiAliasingInterop.SetWindowDisplayAffinity(_hwnd, AntiAliasingInterop.WDA_EXCLUDEFROMCAPTURE);
                AntiAliasingInterop.SetWindowPos(_hwnd, AntiAliasingInterop.HWND_TOPMOST, _rectLeft, _rectTop, _width, _height, AntiAliasingInterop.SWP_NOACTIVATE | AntiAliasingInterop.SWP_SHOWWINDOW);
                OverlayDiagnostics.RaiseOverlayWindows();
                AntiAliasingInterop.ShowWindow(_hwnd, AntiAliasingInterop.SW_SHOWNOACTIVATE);
                App.Logger.WriteLine(LOG_IDENT, "Overlay window created, click through and capture excluded");
            }
            finally
            {
                Marshal.FreeHGlobal(classNamePtr);
            }
        }

        private static readonly FeatureLevel[] _featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        private void CreateDevice()
        {
            _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

            IDXGIAdapter1? chosen = null;
            try
            {
                int cx = _rectLeft + _width / 2;
                int cy = _rectTop + _height / 2;
                for (uint i = 0; chosen == null; i++)
                {
                    var res = _factory.EnumAdapters1(i, out var adapter);
                    if (res.Failure || adapter == null)
                        break;
                    bool owns = false;
                    for (uint j = 0; !owns; j++)
                    {
                        var ores = adapter.EnumOutputs(j, out var output);
                        if (ores.Failure || output == null)
                            break;
                        try
                        {
                            var dc = output.Description.DesktopCoordinates;
                            owns = cx >= dc.Left && cx < dc.Right && cy >= dc.Top && cy < dc.Bottom;
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                    if (owns)
                        chosen = adapter;
                    else
                        adapter.Dispose();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Adapter probe failed, using the default adapter: " + ex.Message);
            }

            try
            {
                if (chosen != null)
                {
                    D3D11.D3D11CreateDevice(chosen, DriverType.Unknown, DeviceCreationFlags.BgraSupport, _featureLevels, out _device, out _context).CheckError();
                    App.Logger.WriteLine(LOG_IDENT, $"D3D11 device created on {chosen.Description1.Description}, feature level {_device!.FeatureLevel}");
                    return;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Device creation on the display adapter failed: " + ex.Message);
                _context?.Dispose();
                _device?.Dispose();
                _context = null;
                _device = null;
            }
            finally
            {
                chosen?.Dispose();
            }

            try
            {
                D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, _featureLevels, out _device, out _context).CheckError();
                App.Logger.WriteLine(LOG_IDENT, $"D3D11 device created on the default adapter, feature level {_device!.FeatureLevel}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Hardware device unavailable, using the software rasterizer: " + ex.Message);
                _context?.Dispose();
                _device?.Dispose();
                _context = null;
                _device = null;
                D3D11.D3D11CreateDevice((IDXGIAdapter?)null, DriverType.Warp, DeviceCreationFlags.BgraSupport, _featureLevels, out _device, out _context).CheckError();
                App.Logger.WriteLine(LOG_IDENT, $"WARP device created, feature level {_device!.FeatureLevel}");
            }
        }

        private void CreateComposition()
        {
            var swapDesc = new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 3,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            _swapChain = _factory!.CreateSwapChainForComposition(_device!, swapDesc, null);
            CreateBackBufferRtv();

            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            _dcompDevice = DCompApi.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _dcompDevice.CreateTargetForHwnd(_hwnd, true, out _dcompTarget);
            _dcompVisual = _dcompDevice.CreateVisual();
            _dcompVisual.SetContent(_swapChain);
            _dcompTarget!.SetRoot(_dcompVisual);
            _dcompDevice.Commit();
            App.Logger.WriteLine(LOG_IDENT, "DirectComposition swapchain attached to the overlay window");
        }

        private void CreateBackBufferRtv()
        {
            using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            _backBufferRtv = _device!.CreateRenderTargetView(backBuffer);
        }

        private ID3D11PixelShader CompilePs(string entry)
        {
            Vortice.D3DCompiler.Compiler.Compile(AntiAliasingShaders.Source, entry, "AntiAliasing", "ps_5_0", out var blob, out var err);
            using (err)
            {
                if (blob == null)
                {
                    string msg = err != null ? err.AsString() : "unknown";
                    throw new InvalidOperationException("AntiAliasing shader compile failed for " + entry + ": " + msg);
                }
            }
            using (blob)
            {
                return _device!.CreatePixelShader(blob.AsBytes());
            }
        }

        private void CreatePipeline()
        {
            var sw = Stopwatch.StartNew();
            Vortice.D3DCompiler.Compiler.Compile(AntiAliasingShaders.Source, "VSMain", "AntiAliasing", "vs_5_0", out var vsBlob, out var vsErr);
            using (vsErr)
            {
                if (vsBlob == null)
                    throw new InvalidOperationException("AntiAliasing vertex shader compile failed");
            }
            using (vsBlob)
            {
                _vs = _device!.CreateVertexShader(vsBlob.AsBytes());
            }
            _psPass = CompilePs("PSPass");
            _psCropSrgb = CompilePs("PSCropSrgb");
            _psFxaa = CompilePs("PSFxaa");
            _psFxaaUltra = CompilePs("PSFxaaUltra");
            _psSmaaEdge = CompilePs("PSSmaaEdge");
            _psSmaaWeights = CompilePs("PSSmaaWeights");
            _psSmaaWeightsUltra = CompilePs("PSSmaaWeightsUltra");
            _psSmaaBlend = CompilePs("PSSmaaBlend");
            _psDlaaMask = CompilePs("PSDlaaMask");
            _psDlaa = CompilePs("PSDlaa");
            _psNfaa = CompilePs("PSNfaa");
            _psTsaa = CompilePs("PSTsaa");
            _psTsaaSharpen = CompilePs("PSTsaaSharpen");
            sw.Stop();
            App.Logger.WriteLine(LOG_IDENT, $"Compiled shaders in {sw.ElapsedMilliseconds}ms");

            _sampler = _device!.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0,
                MaxLOD = float.MaxValue,
            });

            _cbuffer = _device!.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<AntiAliasingParams>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            CreateSizedResources();
        }

        private void CreateSizedResources()
        {
            _inputRtv?.Dispose();
            _inputSrv?.Dispose();
            _inputTex?.Dispose();
            _inputTex = CreateTexture(BindFlags.ShaderResource | BindFlags.RenderTarget);
            _inputSrv = _device!.CreateShaderResourceView(_inputTex);
            _inputRtv = _device!.CreateRenderTargetView(_inputTex);

            _workRtvA?.Dispose();
            _workSrvA?.Dispose();
            _workTexA?.Dispose();
            _workTexA = CreateTexture(BindFlags.ShaderResource | BindFlags.RenderTarget);
            _workSrvA = _device!.CreateShaderResourceView(_workTexA);
            _workRtvA = _device!.CreateRenderTargetView(_workTexA);

            _workRtvB?.Dispose();
            _workSrvB?.Dispose();
            _workTexB?.Dispose();
            _workTexB = CreateTexture(BindFlags.ShaderResource | BindFlags.RenderTarget);
            _workSrvB = _device!.CreateShaderResourceView(_workTexB);
            _workRtvB = _device!.CreateRenderTargetView(_workTexB);

            _dims = new Vector4(_width, _height, 1f / Math.Max(_width, 1), 1f / Math.Max(_height, 1));
            _context!.UpdateSubresource(new AntiAliasingParams { Dims = _dims, SrcRect = new Vector4(0f, 0f, 1f, 1f) }, _cbuffer!);
            _tsaaSeeded = false;
        }

        private ID3D11Texture2D CreateTexture(BindFlags bind)
        {
            return _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = bind,
                CPUAccessFlags = CpuAccessFlags.None,
            });
        }

        private bool CreateDuplicationForRect(int left, int top)
        {
            try
            {
                _duplication?.Dispose();
                _duplication = null;
                using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
                dxgiDevice.GetAdapter(out var adapter).CheckError();
                try
                {
                    int cx = left + _width / 2;
                    int cy = top + _height / 2;
                    for (uint i = 0; ; i++)
                    {
                        var res = adapter.EnumOutputs(i, out var output);
                        if (res.Failure || output == null)
                            break;
                        try
                        {
                            var dc = output.Description.DesktopCoordinates;
                            bool contains = cx >= dc.Left && cx < dc.Right && cy >= dc.Top && cy < dc.Bottom;
                            if (contains)
                            {
                                _outputLeft = dc.Left;
                                _outputTop = dc.Top;
                                _outputRight = dc.Right;
                                _outputBottom = dc.Bottom;
                                using var output1 = output.QueryInterface<IDXGIOutput1>();
                                _duplication = output1.DuplicateOutput(_device!);
                                _captureFailures = 0;
                                App.Logger.WriteLine(LOG_IDENT, $"Screen capture active on monitor at {dc.Left},{dc.Top} to {dc.Right},{dc.Bottom}");
                                return true;
                            }
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                    return false;
                }
                finally
                {
                    adapter.Dispose();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AntiAliasingOverlay::CreateDuplication", ex);
                return false;
            }
        }

        private long _nextHwndResolveMs;

        private void ResolveRobloxHwnd()
        {
            if (_robloxHwnd != IntPtr.Zero)
                return;
            long now = Environment.TickCount64;
            if (now < _nextHwndResolveMs)
                return;
            _nextHwndResolveMs = now + 400;
            try
            {
                var p = Process.GetProcessesByName("RobloxPlayerBeta");
                foreach (var proc in p)
                {
                    if (_robloxHwnd == IntPtr.Zero && proc.MainWindowHandle != IntPtr.Zero)
                        _robloxHwnd = proc.MainWindowHandle;
                    proc.Dispose();
                }
            }
            catch
            {
            }
        }

        private bool TryGetRobloxRect(out AntiAliasingInterop.RECT rect)
        {
            rect = default;
            if (_robloxHwnd == IntPtr.Zero || !AntiAliasingInterop.IsWindow(_robloxHwnd))
            {
                _robloxHwnd = IntPtr.Zero;
                ResolveRobloxHwnd();
                if (_robloxHwnd == IntPtr.Zero)
                    return false;
            }
            if (!AntiAliasingInterop.GetWindowRect(_robloxHwnd, out rect))
            {
                _robloxHwnd = IntPtr.Zero;
                return false;
            }
            IntPtr monitor = AntiAliasingInterop.MonitorFromWindow(_robloxHwnd, AntiAliasingInterop.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var mi = new AntiAliasingInterop.MONITORINFO { cbSize = (uint)Marshal.SizeOf<AntiAliasingInterop.MONITORINFO>() };
                if (AntiAliasingInterop.GetMonitorInfoW(monitor, ref mi))
                {
                    int mw = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    int mh = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    if (Math.Abs((rect.Right - rect.Left) - mw) < 6 && Math.Abs((rect.Bottom - rect.Top) - mh) < 6)
                        rect = mi.rcMonitor;
                }
            }
            return true;
        }

        private bool UpdateVisibility(CancellationToken token)
        {
			long tick = Environment.TickCount64;
			if (tick < _nextVisibilityCheckMs)
				return !_hiddenByFocus;
			_nextVisibilityCheckMs = tick + 500;
            if (_robloxHwnd == IntPtr.Zero)
                ResolveRobloxHwnd();
            IntPtr fg = AntiAliasingInterop.GetForegroundWindow();
			bool robloxActive = (_robloxHwnd != IntPtr.Zero && fg == _robloxHwnd) || (fg != IntPtr.Zero && fg == Voidstrap.Integrations.RiShade.RiShadePanel.CurrentHwnd) || OverlayDiagnostics.IsOverlayHandle(fg);
            if (robloxActive)
            {
                if (_hiddenByFocus)
                {
                    _hiddenByFocus = false;
                    AntiAliasingInterop.ShowWindow(_hwnd, AntiAliasingInterop.SW_SHOWNOACTIVATE);
                    AssertZOrder();
                    App.Logger.WriteLine(LOG_IDENT, "Roblox focused, overlay visible again");
                }
                return true;
            }
            if (!_hiddenByFocus)
            {
                _hiddenByFocus = true;
                AntiAliasingInterop.ShowWindow(_hwnd, AntiAliasingInterop.SW_HIDE);
                App.Logger.WriteLine(LOG_IDENT, "Roblox lost focus, overlay hidden");
            }
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _lastHwndResolve > 5.0)
            {
                _lastHwndResolve = now;
                _robloxHwnd = IntPtr.Zero;
                ResolveRobloxHwnd();
            }
			token.WaitHandle.WaitOne(500);
            return false;
        }

        private void FollowRoblox()
        {
            if (!TryGetRobloxRect(out var rect))
                return;
            if (rect.Left <= -30000 || rect.Top <= -30000)
                return;
            int w = Math.Max(16, rect.Right - rect.Left);
            int h = Math.Max(16, rect.Bottom - rect.Top);
            if (rect.Left == _rectLeft && rect.Top == _rectTop && w == _width && h == _height)
            {
                _pendingW = 0;
                _pendingH = 0;
                return;
            }

            bool sizeChanged = w != _width || h != _height;
            if (sizeChanged && (w != _pendingW || h != _pendingH))
            {
                _pendingW = w;
                _pendingH = h;
                _rectLeft = rect.Left;
                _rectTop = rect.Top;
                AntiAliasingInterop.SetWindowPos(_hwnd, AntiAliasingInterop.HWND_TOPMOST, _rectLeft, _rectTop, w, h, AntiAliasingInterop.SWP_NOACTIVATE);
                return;
            }

            _rectLeft = rect.Left;
            _rectTop = rect.Top;
            _width = w;
            _height = h;
            _pendingW = 0;
            _pendingH = 0;

            if (sizeChanged)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Roblox window resized, rebuilding targets at {_width}x{_height}");
                _backBufferRtv?.Dispose();
                _backBufferRtv = null;
                _swapChain!.ResizeBuffers(3, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                CreateBackBufferRtv();
                CreateSizedResources();
                CreateDuplicationForRect(_rectLeft, _rectTop);
            }
            else
            {
                int cx = _rectLeft + _width / 2;
                int cy = _rectTop + _height / 2;
                bool sameOutput = cx >= _outputLeft && cx < _outputRight && cy >= _outputTop && cy < _outputBottom;
                if (!sameOutput)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Roblox moved to another monitor, reacquiring capture");
                    CreateDuplicationForRect(_rectLeft, _rectTop);
                }
            }
            AssertZOrder();
        }

        private void AssertZOrder()
        {
            if (_hwnd == IntPtr.Zero || _hiddenByFocus)
                return;
            AntiAliasingInterop.SetWindowPos(_hwnd, AntiAliasingInterop.HWND_TOPMOST, _rectLeft, _rectTop, _width, _height, AntiAliasingInterop.SWP_NOACTIVATE);
        }

        private bool HandleCaptureUnstable(string reason)
        {
            _stableCaptureFrames = 0;
            long now = Environment.TickCount64;
            if (_captureUnstableSinceMs == 0)
                _captureUnstableSinceMs = now;
            _captureFailures++;
            if (_captureFailures == 1 || _captureFailures % 30 == 0)
                App.Logger.WriteLine(LOG_IDENT, $"{reason}, reacquiring the monitor");
            if (now - _captureUnstableSinceMs > 6000)
            {
                App.Logger.WriteLine(LOG_IDENT, "Screen capture stayed unstable, ending this overlay session");
                _deviceLost = true;
                return false;
            }
            if (now - _lastRecreateMs >= 500)
            {
                _lastRecreateMs = now;
                CreateDuplicationForRect(_rectLeft, _rectTop);
            }
			_runToken.WaitHandle.WaitOne(100);
            return false;
        }

        private bool CaptureFrame()
        {
            if (_inputTex == null)
                return false;

            if (_duplication == null)
                return HandleCaptureUnstable("Screen capture not available");

            IDXGIResource? desktopResource = null;
            bool acquired = false;
            try
            {
                var result = _duplication.AcquireNextFrame(16, out _, out desktopResource);
                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                    return false;
                if (result == Vortice.DXGI.ResultCode.AccessLost || result.Failure || desktopResource == null)
                    return HandleCaptureUnstable("Capture access lost");
                acquired = true;
                _stableCaptureFrames++;
                if (_stableCaptureFrames >= 15)
                {
                    _captureUnstableSinceMs = 0;
                    _captureFailures = 0;
                }

                using var desktopTex = desktopResource.QueryInterface<ID3D11Texture2D>();
                int srcLeft = _rectLeft - _outputLeft;
                int srcTop = _rectTop - _outputTop;
                var desc = desktopTex.Description;
                int right = Math.Min(srcLeft + _width, (int)desc.Width);
                int bottom = Math.Min(srcTop + _height, (int)desc.Height);
                srcLeft = Math.Max(0, srcLeft);
                srcTop = Math.Max(0, srcTop);
                if (right <= srcLeft || bottom <= srcTop)
                    return false;

                if (desc.Format == Format.B8G8R8A8_UNorm)
                {
                    var box = new Box(srcLeft, srcTop, 0, right, bottom, 1);
                    _context!.CopySubresourceRegion(_inputTex, 0, 0, 0, 0, desktopTex, 0, box);
                }
                else
                {
                    using var desktopSrv = _device!.CreateShaderResourceView(desktopTex);
                    var crop = new AntiAliasingParams
                    {
                        Dims = _dims,
                        SrcRect = new Vector4(
                            (float)srcLeft / desc.Width,
                            (float)srcTop / desc.Height,
                            (float)(right - srcLeft) / desc.Width,
                            (float)(bottom - srcTop) / desc.Height),
                    };
                    _context!.UpdateSubresource(crop, _cbuffer!);
                    DrawPass(_psCropSrgb!, _inputRtv!, desktopSrv);
                    _context.UpdateSubresource(new AntiAliasingParams { Dims = _dims, SrcRect = new Vector4(0f, 0f, 1f, 1f) }, _cbuffer!);
                }
                _hasFirstCapture = true;
                return true;
            }
            finally
            {
                desktopResource?.Dispose();
                if (acquired)
                    _duplication.ReleaseFrame();
            }
        }

        private static readonly ID3D11ShaderResourceView?[] _nullSrvs = new ID3D11ShaderResourceView?[2];

        private void DrawPass(ID3D11PixelShader ps, ID3D11RenderTargetView target, ID3D11ShaderResourceView? t0, ID3D11ShaderResourceView? t1 = null)
        {
            _context!.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(target);
            _context.PSSetShader(ps);
            _context.PSSetShaderResource(0, t0);
            if (t1 != null) _context.PSSetShaderResource(1, t1);
            _context.Draw(3, 0);
        }

        private void RenderFrame()
        {
            int method = AntiAliasingSettings.MethodIndex;
            _context!.VSSetShader(_vs);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
            bool fresh = CaptureFrame();
            if (!fresh)
            {
                if (!_hasFirstCapture || method == _lastMethodRendered)
                    return;
            }
            if (method != _lastMethodRendered)
            {
                _lastMethodRendered = method;
                _tsaaSeeded = false;
                App.Logger.WriteLine(LOG_IDENT, "Method active: " + AntiAliasingSettings.MethodNames[method]);
            }

            RenderMethodInto(method, _inputSrv!, _backBufferRtv!);

            var presentResult = _swapChain!.Present(0, PresentFlags.None);
            if (presentResult == Vortice.DXGI.ResultCode.DeviceRemoved || presentResult == Vortice.DXGI.ResultCode.DeviceReset)
            {
                App.Logger.WriteLine(LOG_IDENT, "Graphics device was lost, the session will restart");
                _deviceLost = true;
                return;
            }
            _framesPresented++;
        }

        private void RenderMethodInto(int method, ID3D11ShaderResourceView input, ID3D11RenderTargetView output)
        {
            switch (method)
            {
                case 1:
                    DrawPass(_psFxaa!, output, input);
                    break;
                case 2:
                    DrawPass(_psFxaaUltra!, output, input);
                    break;
                case 3:
                case 4:
                    DrawPass(_psSmaaEdge!, _workRtvA!, input);
                    DrawPass(method == 4 ? _psSmaaWeightsUltra! : _psSmaaWeights!, _workRtvB!, _workSrvA);
                    DrawPass(_psSmaaBlend!, output, input, _workSrvB);
                    break;
                case 5:
                    DrawPass(_psDlaaMask!, _workRtvA!, input);
                    DrawPass(_psDlaa!, output, _workSrvA);
                    break;
                case 6:
                    DrawPass(_psNfaa!, output, input);
                    break;
                case 7:
                    var histSrv = _tsaaHistory == 0 ? _workSrvA : _workSrvB;
                    var histRtv = _tsaaHistory == 0 ? _workRtvA : _workRtvB;
                    var nextSrv = _tsaaHistory == 0 ? _workSrvB : _workSrvA;
                    var nextRtv = _tsaaHistory == 0 ? _workRtvB : _workRtvA;
                    if (!_tsaaSeeded)
                    {
                        DrawPass(_psPass!, histRtv!, input);
                        _tsaaSeeded = true;
                    }
                    DrawPass(_psTsaa!, nextRtv!, input, histSrv);
                    DrawPass(_psTsaaSharpen!, output, nextSrv);
                    _tsaaHistory ^= 1;
                    break;
                default:
                    DrawPass(_psPass!, output, input);
                    break;
            }
            _context!.PSSetShaderResources(0, _nullSrvs);
        }

        public void AttachExternal(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
        {
            _device = device;
            _context = context;
            _width = Math.Max(16, width);
            _height = Math.Max(16, height);
            CreatePipeline();
            _lastMethodRendered = -1;
            App.Logger.WriteLine(LOG_IDENT, $"Attached as a composited stage at {_width}x{_height}");
        }

        public void EnsureExternalSize(int width, int height)
        {
            width = Math.Max(16, width);
            height = Math.Max(16, height);
            if (width == _width && height == _height)
                return;
            _width = width;
            _height = height;
            CreateSizedResources();
        }

        public void RenderInto(ID3D11ShaderResourceView input, ID3D11RenderTargetView output, int width, int height)
        {
            EnsureExternalSize(width, height);
            int method = AntiAliasingSettings.MethodIndex;
            _context!.VSSetShader(_vs);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
            if (method != _lastMethodRendered)
            {
                _lastMethodRendered = method;
                _tsaaSeeded = false;
            }
            RenderMethodInto(method, input, output);
        }

        public void DisposeExternal()
        {
            try
            {
                _inputRtv?.Dispose();
                _inputSrv?.Dispose();
                _inputTex?.Dispose();
                _workRtvA?.Dispose();
                _workSrvA?.Dispose();
                _workTexA?.Dispose();
                _workRtvB?.Dispose();
                _workSrvB?.Dispose();
                _workTexB?.Dispose();
                _cbuffer?.Dispose();
                _sampler?.Dispose();
                _psPass?.Dispose();
                _psCropSrgb?.Dispose();
                _psFxaa?.Dispose();
                _psFxaaUltra?.Dispose();
                _psSmaaEdge?.Dispose();
                _psSmaaWeights?.Dispose();
                _psSmaaWeightsUltra?.Dispose();
                _psSmaaBlend?.Dispose();
                _psDlaaMask?.Dispose();
                _psDlaa?.Dispose();
                _psNfaa?.Dispose();
                _psTsaa?.Dispose();
                _psTsaaSharpen?.Dispose();
                _vs?.Dispose();
				_inputRtv = null;
				_inputSrv = null;
				_inputTex = null;
				_workRtvA = null;
				_workSrvA = null;
				_workTexA = null;
				_workRtvB = null;
				_workSrvB = null;
				_workTexB = null;
				_cbuffer = null;
				_sampler = null;
				_psPass = null;
				_psCropSrgb = null;
				_psFxaa = null;
				_psFxaaUltra = null;
				_psSmaaEdge = null;
				_psSmaaWeights = null;
				_psSmaaWeightsUltra = null;
				_psSmaaBlend = null;
				_psDlaaMask = null;
				_psDlaa = null;
				_psNfaa = null;
				_psTsaa = null;
				_psTsaaSharpen = null;
				_vs = null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AntiAliasingOverlay::DisposeExternal", ex);
            }
        }

        private void LogStatsIfDue()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _lastStatsLog < 60.0)
                return;
            if (_lastStatsLog > 0)
            {
                long frames = _framesPresented - _framesAtLastLog;
                App.Logger.WriteLine(LOG_IDENT, $"Presented {frames} frames in the last minute");
            }
            _lastStatsLog = now;
            _framesAtLastLog = _framesPresented;
        }

        private void Cleanup()
        {
			if (Interlocked.Exchange(ref _cleanedUp, 1) != 0)
				return;
            try
            {
                if (_timerRaised)
				{
                    AntiAliasingInterop.timeEndPeriod(1);
					_timerRaised = false;
				}
				_context?.ClearState();
				_context?.Flush();
                _duplication?.Dispose();
                _inputRtv?.Dispose();
                _inputSrv?.Dispose();
                _inputTex?.Dispose();
                _workRtvA?.Dispose();
                _workSrvA?.Dispose();
                _workTexA?.Dispose();
                _workRtvB?.Dispose();
                _workSrvB?.Dispose();
                _workTexB?.Dispose();
                _cbuffer?.Dispose();
                _sampler?.Dispose();
                _psPass?.Dispose();
                _psCropSrgb?.Dispose();
                _psFxaa?.Dispose();
                _psFxaaUltra?.Dispose();
                _psSmaaEdge?.Dispose();
                _psSmaaWeights?.Dispose();
                _psSmaaWeightsUltra?.Dispose();
                _psSmaaBlend?.Dispose();
                _psDlaaMask?.Dispose();
                _psDlaa?.Dispose();
                _psNfaa?.Dispose();
                _psTsaa?.Dispose();
                _psTsaaSharpen?.Dispose();
                _vs?.Dispose();
                _dcompVisual?.Dispose();
                _dcompTarget?.Dispose();
                _dcompDevice?.Dispose();
                _backBufferRtv?.Dispose();
                _swapChain?.Dispose();
                _factory?.Dispose();
                _context?.Dispose();
                _device?.Dispose();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("AntiAliasingOverlay::Cleanup", ex);
            }
            try
            {
                if (_hwnd != IntPtr.Zero)
                    AntiAliasingInterop.DestroyWindow(_hwnd);
                if (_classAtom != 0)
                    AntiAliasingInterop.UnregisterClassW(new IntPtr(_classAtom), _hInstance);
            }
            catch
            {
            }
            _hwnd = IntPtr.Zero;
            _classAtom = 0;
			_wndProc = null;
            App.Logger.WriteLine(LOG_IDENT, "Overlay session cleaned up");
        }
    }
}
