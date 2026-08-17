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

namespace Voidstrap.Integrations.RiShade
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct RiShadeParams
    {
        public Vector4 PA;
        public Vector4 PB;
        public Vector4 PC;
        public Vector4 PD;
        public Vector4 PE;
        public Vector4 PF;
        public Vector4 PG;
        public Vector4 PH;
        public Vector4 PI;
        public Vector4 PJ;
        public Vector4 PK;
        public Vector4 PL;
        public Vector4 PM;
        public Vector4 PN;
        public Vector4 PO;
        public Vector4 PP;
        public Vector4 PQ;
        public Vector4 PR;
        public Vector4 PS;
        public Vector4 PT;
    }

    internal sealed class RiShadeOverlay
    {
        private const string ClassName = "VoidstrapRiShadeOverlay";
        private const string LOG_IDENT = "RiShade";

        private RiShadeInterop.WndProcDelegate? _wndProc;
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
        private RiShadeWgc? _wgc;
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
        private double _lastHwndResolve;

        private ID3D11Texture2D? _inputTex;
        private ID3D11ShaderResourceView? _inputSrv;
        private readonly ID3D11Texture2D?[] _workTex = new ID3D11Texture2D?[16];
        private readonly ID3D11ShaderResourceView?[] _workSrv = new ID3D11ShaderResourceView?[16];
        private readonly ID3D11RenderTargetView?[] _workRtv = new ID3D11RenderTargetView?[16];
        private const int RtA = 0;
        private const int RtB = 1;
        private const int RtSsr = 2;
        private const int RtGlossWide = 3;
        private const int RtGlossTemp = 4;
        private const int RtDown0 = 5;
        private const int RtUp0 = 10;
        private const int RtSceneBlurA = 14;
        private const int RtSceneBlurB = 15;

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psMain;
        private ID3D11PixelShader? _psDownPrefilter;
        private ID3D11PixelShader? _psDown;
        private ID3D11PixelShader? _psUpTent;
        private ID3D11PixelShader? _psBlurH;
        private ID3D11PixelShader? _psBlurV;
        private ID3D11PixelShader? _psBloomCombine;
        private ID3D11PixelShader? _psDepthUp;
        private ID3D11PixelShader? _psGi;
        private ID3D11PixelShader? _psSsr;
        private ID3D11PixelShader? _psComposite;
        private ID3D11PixelShader? _psPassthrough;
        private ID3D11SamplerState? _sampler;
        private ID3D11Buffer? _cbuffer;
        private ID3D11Buffer? _passCbuffer;

        private ID3D11Texture2D? _depthInputTex;
        private ID3D11RenderTargetView? _depthInputRtv;
        private ID3D11ShaderResourceView? _depthInputSrv;
        private ID3D11Texture2D? _aiDepthUpTex;
        private ID3D11RenderTargetView? _aiDepthUpRtv;
        private ID3D11ShaderResourceView? _aiDepthUpSrv;
        private float _adaptAvg;
        private float _adaptExposure = 1f;
        private Vector3 _planeN = new(0f, 1f, 0f);
        private float _planeD;
        private bool _planeValid;
        private readonly RiShadeAntiSmear _antiSmear = new();
        private float _depthBaseX;
        private float _depthBaseY;
        private float _lastFitAccumX;
        private float _lastFitAccumY;
        private const int AiFeedStride = 2;
        private const float DepthPredictLead = 2.5f;
        private int _aiFeedTick;
        private int _framesSinceFeed;
        private float _velAccumX;
        private float _velAccumY;
        private float _prevFeedAccumX;
        private float _prevFeedAccumY;
        private float _predAccumX;
        private float _predAccumY;
        private ID3D11Texture2D? _depthStagingTex;
        private ID3D11Texture2D? _depthStagingTexB;
        private int _stagingFlip;
        private bool _stagingPrimed;
        private bool _timerRaised;
        private ID3D11Texture2D? _aiDepthTex;
        private ID3D11ShaderResourceView? _aiDepthSrv;
        private readonly byte[] _depthReadback = new byte[RiShadeDepth.Size * RiShadeDepth.Size * 4];
        private readonly float[] _depthFloats = new float[RiShadeDepth.Size * RiShadeDepth.Size];
        private int _depthSeenVersion;
        private bool _aiDepthUploaded;
        private bool _lastAiFlag;

        private int _width;
        private int _height;
        private int _rw;
        private int _rh;
        private int _rectLeft;
        private int _rectTop;

        private IntPtr _robloxHwnd;
        private bool _hiddenByFocus;
        private int _lastSettingsVersion = -1;
        private bool _firstFrameLogged;
        private long _framesPresented;
        private long _captureTimeouts;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
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
                if (!RobloxLightingOverlay.RobloxWindow.TryGet(out var rect))
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
                if (_robloxHwnd != IntPtr.Zero)
                    _wgc = RiShadeWgc.TryCreate(_device!, _robloxHwnd);
                if (_wgc == null)
                {
                    RiShadeInterop.SetWindowDisplayAffinity(_hwnd, RiShadeInterop.WDA_EXCLUDEFROMCAPTURE);
                    App.Logger.WriteLine(LOG_IDENT, "Using monitor capture, the overlay stays hidden from recordings");
                    if (!CreateDuplicationForRect(_rectLeft, _rectTop))
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Could not create desktop duplication for the Roblox monitor, overlay aborted");
                        return;
                    }
                }
                CreateComposition();
                CreatePipeline();
                LoadCustomEffects();
                _ = RiShadeInterop.timeBeginPeriod(1);
                _timerRaised = true;
                _captureFailures = 0;
                _stableCaptureFrames = 0;
                _captureUnstableSinceMs = 0;
                _lastRecreateMs = 0;

                var msg = default(RiShadeInterop.MSG);
                while (!token.IsCancellationRequested)
                {
                    while (RiShadeInterop.PeekMessageW(out msg, IntPtr.Zero, 0, 0, RiShadeInterop.PM_REMOVE))
                    {
                        if (msg.message == RiShadeInterop.WM_HOTKEY)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "F8 pressed, toggling the RiShade panel");
                            RiShadePanel.Toggle();
                            continue;
                        }
                        RiShadeInterop.TranslateMessage(ref msg);
                        RiShadeInterop.DispatchMessageW(ref msg);
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
                App.Logger.WriteException("RiShadeOverlay::Run", ex);
            }
            finally
            {
                Cleanup();
            }
        }

        private void CreateWindow()
        {
            _hInstance = RiShadeInterop.GetModuleHandleW(null);
            _wndProc = (h, m, w, l) => RiShadeInterop.DefWindowProcW(h, m, w, l);
            IntPtr classNamePtr = Marshal.StringToHGlobalUni(ClassName);
            try
            {
                var wc = new RiShadeInterop.WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<RiShadeInterop.WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = _hInstance,
                    lpszClassName = classNamePtr,
                };
                _classAtom = RiShadeInterop.RegisterClassExW(ref wc);

                int exStyle = RiShadeInterop.WS_EX_NOACTIVATE | RiShadeInterop.WS_EX_TOOLWINDOW | RiShadeInterop.WS_EX_TRANSPARENT | RiShadeInterop.WS_EX_TOPMOST | RiShadeInterop.WS_EX_LAYERED | RiShadeInterop.WS_EX_NOREDIRECTIONBITMAP;
                _hwnd = RiShadeInterop.CreateWindowExW(exStyle, new IntPtr(_classAtom), ClassName, RiShadeInterop.WS_POPUP, _rectLeft, _rectTop, _width, _height, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

                RiShadeInterop.SetLayeredWindowAttributes(_hwnd, 0, 255, RiShadeInterop.LWA_ALPHA);
                RiShadeInterop.SetWindowPos(_hwnd, RiShadeInterop.HWND_TOPMOST, _rectLeft, _rectTop, _width, _height, RiShadeInterop.SWP_NOACTIVATE | RiShadeInterop.SWP_SHOWWINDOW);
                OverlayDiagnostics.RaiseOverlayWindows();
                RiShadeInterop.ShowWindow(_hwnd, RiShadeInterop.SW_SHOWNOACTIVATE);
                if (RiShadeInterop.RegisterHotKey(_hwnd, 1, RiShadeInterop.MOD_NOREPEAT, RiShadeInterop.VK_F8))
                    App.Logger.WriteLine(LOG_IDENT, "F8 registered to toggle the RiShade panel");
                App.Logger.WriteLine(LOG_IDENT, "Overlay window created, click through and capture excluded");
            }
            finally
            {
                Marshal.FreeHGlobal(classNamePtr);
            }
        }

        private static readonly FeatureLevel[] _featureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

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

        private readonly System.Collections.Generic.List<ID3D11PixelShader> _customEffects = [];

        private void LoadCustomEffects()
        {
            try
            {
                string dir = System.IO.Path.Combine(Paths.RiShade, "Effects");
                System.IO.Directory.CreateDirectory(dir);
                foreach (string file in System.IO.Directory.GetFiles(dir, "*.hlsl"))
                {
                    string name = System.IO.Path.GetFileName(file);
                    try
                    {
                        string source = RiShadeShaders.Source + "\n" + System.IO.File.ReadAllText(file);
                        Vortice.D3DCompiler.Compiler.Compile(source, "PSCustom", name, "ps_5_0", out var blob, out var err);
                        using (err)
                        {
                            if (blob == null)
                            {
                                string msg = err != null ? err.AsString() : "unknown";
                                App.Logger.WriteLine(LOG_IDENT, $"Custom effect {name} failed to compile: {msg}");
                                continue;
                            }
                        }
                        using (blob)
                        {
                            _customEffects.Add(_device!.CreatePixelShader(blob.AsBytes()));
                        }
                        App.Logger.WriteLine(LOG_IDENT, $"Custom effect {name} loaded, entry PSCustom, scene on t0 and AI depth on t1");
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Custom effect {name} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Custom effects folder scan failed: " + ex.Message);
            }
        }

        private ID3D11PixelShader CompilePs(string entry)
        {
            Vortice.D3DCompiler.Compiler.Compile(RiShadeShaders.Source, entry, "RiShade", "ps_5_0", out var blob, out var err);
            using (err)
            {
                if (blob == null)
                {
                    string msg = err != null ? err.AsString() : "unknown";
                    throw new InvalidOperationException("RiShade shader compile failed for " + entry + ": " + msg);
                }
            }
            using (blob)
            {
                return _device!.CreatePixelShader(blob.AsBytes());
            }
        }

        private int _builtRenderScaleIndex;

        private void CreatePipeline()
        {
            _builtRenderScaleIndex = RiShadeSettings.Current.RenderScaleIndex;
            float renderScale = RiShadeSettings.Current.ResolveRenderScale();
            _rw = Math.Max(64, (int)Math.Round(_width * renderScale));
            _rh = Math.Max(64, (int)Math.Round(_height * renderScale));
            var sw = Stopwatch.StartNew();
            Vortice.D3DCompiler.Compiler.Compile(RiShadeShaders.Source, "VSMain", "RiShade", "vs_5_0", out var vsBlob, out var vsErr);
            using (vsErr)
            {
                if (vsBlob == null)
                    throw new InvalidOperationException("RiShade vertex shader compile failed");
            }
            using (vsBlob)
            {
                _vs = _device!.CreateVertexShader(vsBlob.AsBytes());
            }
            _psMain = CompilePs("PSMain");
            _psDownPrefilter = CompilePs("PSDownsamplePrefilter");
            _psDown = CompilePs("PSDownsample");
            _psUpTent = CompilePs("PSUpsampleTent");
            _psBlurH = CompilePs("PSBlurH");
            _psBlurV = CompilePs("PSBlurV");
            _psBloomCombine = CompilePs("PSBloomCombine");
            _psDepthUp = CompilePs("PSDepthUp");
            _psGi = CompilePs("PSGi");
            _psSsr = CompilePs("PSSsr");
            _psComposite = CompilePs("PSComposite");

            _psPassthrough = CompilePs("PSPassthrough");
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
                ByteWidth = (uint)Marshal.SizeOf<RiShadeParams>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            _passCbuffer = _device!.CreateBuffer(new BufferDescription
            {
                ByteWidth = (uint)Marshal.SizeOf<Vector4>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            uint ds = (uint)RiShadeDepth.Size;
            _depthInputTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = ds,
                Height = ds,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _depthInputRtv = _device!.CreateRenderTargetView(_depthInputTex);
            _depthInputSrv = _device!.CreateShaderResourceView(_depthInputTex);
            _depthStagingTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = ds,
                Height = ds,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _depthStagingTexB = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = ds,
                Height = ds,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _aiDepthTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = ds,
                Height = ds,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _aiDepthSrv = _device!.CreateShaderResourceView(_aiDepthTex);

            CreateSizedResources();
        }

        private void CreateSizedResources()
        {
            _inputSrv?.Dispose();
            _inputTex?.Dispose();
            _inputTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _inputSrv = _device!.CreateShaderResourceView(_inputTex);

            for (int i = 0; i < _workTex.Length; i++)
            {
                _workRtv[i]?.Dispose();
                _workSrv[i]?.Dispose();
                _workTex[i]?.Dispose();
                var (w, h) = WorkTexSize(i);
                _workTex[i] = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = w,
                    Height = h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R16G16B16A16_Float,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                });
                _workSrv[i] = _device!.CreateShaderResourceView(_workTex[i]!);
                _workRtv[i] = _device!.CreateRenderTargetView(_workTex[i]!);
            }

            _aiDepthUpSrv?.Dispose();
            _aiDepthUpRtv?.Dispose();
            _aiDepthUpTex?.Dispose();
            _aiDepthUpTex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)LvlW(1),
                Height = (uint)LvlH(1),
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R32_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });
            _aiDepthUpSrv = _device!.CreateShaderResourceView(_aiDepthUpTex);
            _aiDepthUpRtv = _device!.CreateRenderTargetView(_aiDepthUpTex);
        }

        private int LvlW(int level) => Math.Max(8, _rw >> level);

        private int LvlH(int level) => Math.Max(8, _rh >> level);

        private (uint, uint) WorkTexSize(int index)
        {
            if (index >= RtDown0 && index < RtUp0)
            {
                int level = index - RtDown0 + 1;
                return ((uint)LvlW(level), (uint)LvlH(level));
            }
            if (index >= RtUp0 && index < RtSceneBlurA)
            {
                int level = index - RtUp0 + 1;
                return ((uint)LvlW(level), (uint)LvlH(level));
            }
            if (index == RtSceneBlurA || index == RtSceneBlurB || index == RtGlossWide || index == RtGlossTemp)
                return ((uint)LvlW(1), (uint)LvlH(1));
            return ((uint)_rw, (uint)_rh);
        }

        private void SetPassPx(int srcW, int srcH, bool upscale = false)
        {
            var v = new Vector4(1f / Math.Max(srcW, 1), 1f / Math.Max(srcH, 1), upscale ? 1f : 0f, 0f);
            _context!.UpdateSubresource(v, _passCbuffer!);
        }

        private void SetVp(int w, int h)
        {
            _context!.RSSetViewport(new Viewport(0, 0, w, h, 0, 1));
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
                App.Logger.WriteException("RiShadeOverlay::CreateDuplication", ex);
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
                var p = System.Diagnostics.Process.GetProcessesByName("RobloxPlayerBeta");
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

        private bool UpdateVisibility(CancellationToken token)
        {
			long tick = Environment.TickCount64;
			if (tick < _nextVisibilityCheckMs)
				return !_hiddenByFocus;
			_nextVisibilityCheckMs = tick + 500;
            if (_robloxHwnd == IntPtr.Zero)
                ResolveRobloxHwnd();
            IntPtr fg = RiShadeInterop.GetForegroundWindow();
            bool robloxActive = (_robloxHwnd != IntPtr.Zero && fg == _robloxHwnd) || (fg != IntPtr.Zero && fg == RiShadePanel.CurrentHwnd) || OverlayDiagnostics.IsOverlayHandle(fg);
            if (robloxActive)
            {
                if (_hiddenByFocus)
                {
                    _hiddenByFocus = false;
                    RiShadeInterop.ShowWindow(_hwnd, RiShadeInterop.SW_SHOWNOACTIVATE);
                    AssertZOrder();
                    App.Logger.WriteLine(LOG_IDENT, "Roblox focused, overlay visible again");
                }
                return true;
            }
            if (!_hiddenByFocus)
            {
                _hiddenByFocus = true;
                RiShadeInterop.ShowWindow(_hwnd, RiShadeInterop.SW_HIDE);
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
            if (!RobloxLightingOverlay.RobloxWindow.TryGet(out var rect))
                return;
            if (rect.Left <= -30000 || rect.Top <= -30000)
                return;
            int w = Math.Max(16, rect.Right - rect.Left);
            int h = Math.Max(16, rect.Bottom - rect.Top);
            if (rect.Left == _rectLeft && rect.Top == _rectTop && w == _width && h == _height)
                return;

            bool sizeChanged = w != _width || h != _height;
            _rectLeft = rect.Left;
            _rectTop = rect.Top;
            _width = w;
            _height = h;

            if (sizeChanged)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Roblox window resized, rebuilding targets at {_width}x{_height}");
                _backBufferRtv?.Dispose();
                _backBufferRtv = null;
                _swapChain!.ResizeBuffers(3, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, SwapChainFlags.None);
                CreateBackBufferRtv();
                CreateSizedResources();
                if (_wgc == null)
                    CreateDuplicationForRect(_rectLeft, _rectTop);
            }
            else if (_wgc == null)
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
            IntPtr panel = RiShadePanel.CurrentHwnd;
            IntPtr insertAfter;
            if (panel != IntPtr.Zero)
            {
                insertAfter = panel;
            }
            else
            {
                IntPtr aaOverlay = RiShadeInterop.FindWindowW("VoidstrapAntiAliasingOverlay", null);
                insertAfter = aaOverlay != IntPtr.Zero ? aaOverlay : RiShadeInterop.HWND_TOPMOST;
            }
            RiShadeInterop.SetWindowPos(_hwnd, insertAfter, _rectLeft, _rectTop, _width, _height, RiShadeInterop.SWP_NOACTIVATE);
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

            if (_wgc != null)
            {
                if (_wgc.IsClosed)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Captured window closed, restarting the session");
                    _deviceLost = true;
                    return false;
                }
                if (_wgc.TryCopyLatestFrame(_context!, _inputTex, _width, _height))
                {
                    _hasFirstCapture = true;
                    return true;
                }
                return false;
            }

            if (_duplication == null)
                return false;

            IDXGIResource? desktopResource = null;
            bool acquired = false;
            try
            {
                var result = _duplication.AcquireNextFrame(16, out _, out desktopResource);
                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    _captureTimeouts++;
                    return false;
                }
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

                var box = new Box(srcLeft, srcTop, 0, right, bottom, 1);
                _context!.CopySubresourceRegion(_inputTex, 0, 0, 0, 0, desktopTex, 0, box);
                return true;
            }
            finally
            {
                desktopResource?.Dispose();
                if (acquired)
                    _duplication.ReleaseFrame();
            }
        }

        private unsafe void UpdateAiDepth(RiShadeSettings s, ID3D11ShaderResourceView inputSrv)
        {
            RiShadeDepth.EnsureStarted();
            SetPassPx(_width, _height);
            SetVp(LvlW(1), LvlH(1));
            DrawPass(_psDown!, _workRtv[RtDown0]!, inputSrv);
            SetVp(_width, _height);
            if (!RiShadeDepth.IsReady)
                return;

            bool doFeed = (_aiFeedTick++ % AiFeedStride) == 0;
            if (doFeed)
            {
                SetVp(LvlW(2), LvlH(2));
                DrawPass(_psDown!, _workRtv[RtDown0 + 1]!, _workSrv[RtDown0]);
                int depthSrc = RtDown0 + 1;
                int depthSrcLvl = 2;
                if (LvlW(2) > 700)
                {
                    SetPassPx(LvlW(2), LvlH(2));
                    SetVp(LvlW(3), LvlH(3));
                    DrawPass(_psDown!, _workRtv[RtDown0 + 2]!, _workSrv[RtDown0 + 1]);
                    depthSrc = RtDown0 + 2;
                    depthSrcLvl = 3;
                }
                SetPassPx(LvlW(depthSrcLvl), LvlH(depthSrcLvl));
                SetVp(RiShadeDepth.Size, RiShadeDepth.Size);
                DrawPass(_psDown!, _depthInputRtv!, _workSrv[depthSrc]);
                SetVp(_width, _height);
                var writeTex = _stagingFlip == 0 ? _depthStagingTex! : _depthStagingTexB!;
                var readTex = _stagingFlip == 0 ? _depthStagingTexB! : _depthStagingTex!;
                _stagingFlip ^= 1;
                _context!.CopyResource(writeTex, _depthInputTex!);
                if (_stagingPrimed)
                {
                    var mapped = _context.Map(readTex, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        int rowBytes = RiShadeDepth.Size * 4;
                        byte* src = (byte*)mapped.DataPointer;
                        fixed (byte* dst = _depthReadback)
                        {
                            for (int y = 0; y < RiShadeDepth.Size; y++)
                                Buffer.MemoryCopy(src + y * (int)mapped.RowPitch, dst + y * rowBytes, rowBytes, rowBytes);
                        }
                    }
                    finally
                    {
                        _context.Unmap(readTex, 0);
                    }
                    _antiSmear.Analyze(_depthReadback);
                    float feedAx = _antiSmear.AccumX;
                    float feedAy = _antiSmear.AccumY;
                    int span = Math.Max(1, _framesSinceFeed);
                    _velAccumX = _velAccumX * 0.5f + ((feedAx - _prevFeedAccumX) / span) * 0.5f;
                    _velAccumY = _velAccumY * 0.5f + ((feedAy - _prevFeedAccumY) / span) * 0.5f;
                    _prevFeedAccumX = feedAx;
                    _prevFeedAccumY = feedAy;
                    _framesSinceFeed = 0;
                    RiShadeDepth.SubmitFrame(_depthReadback, feedAx, feedAy);
                    if (s.EyeAdaptEnabled)
                        UpdateAdaptExposure(s);
                    else
                        _adaptExposure = 1f;
                }
                _stagingPrimed = true;
            }

            _framesSinceFeed++;
            _predAccumX = _antiSmear.AccumX + _velAccumX * (_framesSinceFeed + DepthPredictLead);
            _predAccumY = _antiSmear.AccumY + _velAccumY * (_framesSinceFeed + DepthPredictLead);

            if (RiShadeDepth.TryGetDepth(ref _depthSeenVersion, _depthFloats, out float tagX, out float tagY))
            {
                fixed (float* p = _depthFloats)
                {
                    _context!.UpdateSubresource(_aiDepthTex!, 0, null, (IntPtr)p, (uint)(RiShadeDepth.Size * 4), 0);
                }
                if (!_aiDepthUploaded)
                {
                    _aiDepthUploaded = true;
                    App.Logger.WriteLine(LOG_IDENT, "AI depth map is live in the shader pipeline");
                }
                _depthBaseX = tagX;
                _depthBaseY = tagY;
                if (s.SsrEnabled)
                    FitFloorPlane();
            }

            if (_aiDepthUploaded)
            {
                SetPassPx(LvlW(1), LvlH(1));
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psDepthUp!, _aiDepthUpRtv!, _workSrv[RtDown0], _aiDepthSrv, _depthInputSrv);
                SetVp(_width, _height);
            }
        }

        private void FitFloorPlane()
        {
            int size = RiShadeDepth.Size;
            float aspect = (float)_width / Math.Max(_height, 1);
            const float tanHalf = 0.7f;
            int yStart = (int)(size * 0.40f);
            double a = 0, b = 0, c = 0;
            bool haveFit = false;
            for (int round = 0; round < 2; round++)
            {
                double sxx = 0, sxz = 0, sx = 0, szz = 0, sz = 0, sw = 0, sxy = 0, szy = 0, sy = 0;
                for (int py = yStart; py < size; py += 3)
                {
                    float gy = 1f - (py + 0.5f) / size;
                    for (int px = 0; px < size; px += 3)
                    {
                        float disp = _depthFloats[py * size + px];
                        float z = 1f / (disp * 3f + 0.25f);
                        float gx = (px + 0.5f) / size;
                        double X = (gx * 2f - 1f) * tanHalf * aspect * z;
                        double Y = (gy * 2f - 1f) * tanHalf * z;
                        double w = 1.0;
                        if (haveFit)
                        {
                            double r = Y - (a * X + b * z + c);
                            w = 1.0 / (1.0 + (r * r) / 0.0064);
                        }
                        sxx += w * X * X; sxz += w * X * z; sx += w * X;
                        szz += w * z * z; sz += w * z; sw += w;
                        sxy += w * X * Y; szy += w * z * Y; sy += w * Y;
                    }
                }
                double det = sxx * (szz * sw - sz * sz) - sxz * (sxz * sw - sz * sx) + sx * (sxz * sz - szz * sx);
                if (Math.Abs(det) < 1e-9 || sw < 200)
                {
                    _planeValid = false;
                    return;
                }
                double detA = sxy * (szz * sw - sz * sz) - sxz * (szy * sw - sz * sy) + sx * (szy * sz - szz * sy);
                double detB = sxx * (szy * sw - sy * sz) - sxy * (sxz * sw - sz * sx) + sx * (sxz * sy - szy * sx);
                double detC = sxx * (szz * sy - sz * szy) - sxz * (sxz * sy - szy * sx) + sxy * (sxz * sz - szz * sx);
                a = detA / det;
                b = detB / det;
                c = detC / det;
                haveFit = true;
            }
            float nx = (float)(-a), ny = 1f, nz = (float)(-b);
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            var n = new Vector3(nx / len, ny / len, nz / len);
            float d = (float)c / len;
            if (n.Y < 0.55f)
            {
                _planeValid = false;
                return;
            }
            if (!_planeValid)
            {
                _planeN = n;
                _planeD = d;
                _planeValid = true;
            }
            else
            {
                float dev = 1f - Math.Clamp(Vector3.Dot(n, _planeN), -1f, 1f);
                float ddev = Math.Abs(d - _planeD);
                float pa;
                if (dev < 0.02f && ddev < 0.02f)
                    pa = 0.05f;
                else if (dev < 0.08f && ddev < 0.06f)
                    pa = 0.15f;
                else
                    pa = 0.30f;
                var blended = _planeN + (n - _planeN) * pa;
                _planeN = Vector3.Normalize(blended);
                _planeD += (d - _planeD) * pa;
            }
            _lastFitAccumX = _antiSmear.AccumX;
            _lastFitAccumY = _antiSmear.AccumY;
        }

        private void UpdateAdaptExposure(RiShadeSettings s)
        {
            float sum = 0f;
            int n = 0;
            for (int i = 0; i + 3 < _depthReadback.Length; i += 64)
            {
                sum += _depthReadback[i + 2] * 0.299f + _depthReadback[i + 1] * 0.587f + _depthReadback[i] * 0.114f;
                n++;
            }
            float avg = sum / (n * 255f);
            if (_adaptAvg <= 0f)
                _adaptAvg = avg;
            else
                _adaptAvg += (avg - _adaptAvg) * 0.04f;
            float target = 0.42f;
            float ratio = target / Math.Max(_adaptAvg, 0.06f);
            float exp = 1f + (ratio - 1f) * Math.Clamp(s.EyeAdaptStrength, 0f, 1f);
            _adaptExposure = Math.Clamp(exp, 0.6f, 1.7f);
        }

        private void UpdateParamsIfNeeded(RiShadeSettings s)
        {
            int version = RiShadeSettings.Version;
            bool aiFlag = _aiDepthUploaded;
            bool needsTime = s.GrainEnabled || s.EyeAdaptEnabled || _aiDepthUploaded;
            if (version == _lastSettingsVersion && !needsTime && aiFlag == _lastAiFlag)
                return;
            _lastSettingsVersion = version;
            _lastAiFlag = aiFlag;

            float[] temp = s.ResolveColorTemp();
            var p = new RiShadeParams
            {
                PA = new Vector4(s.GradeEnabled ? 1f : 0f, 1f, 1f, s.Brightness),
                PB = new Vector4(s.Gamma, s.HueShift, (float)_clock.Elapsed.TotalSeconds, s.ChromaEnabled ? 1f : 0f),
                PC = new Vector4(s.Lift[0], s.Lift[1], s.Lift[2], s.TonemapEnabled ? 1f : 0f),
                PD = new Vector4(s.Gain[0], s.Gain[1], s.Gain[2], s.TonemapMode),
                PE = new Vector4(s.ColorBalance[0], s.ColorBalance[1], s.ColorBalance[2], s.TonemapExposure),
                PF = new Vector4(temp[0], temp[1], temp[2], s.TonemapWhitepoint),
                PG = new Vector4(s.VignetteEnabled ? 1f : 0f, s.VignetteStrength, s.VignetteFeather, s.VignetteCenterX),
                PH = new Vector4(s.VignetteCenterY, s.SharpenEnabled ? 1f : 0f, s.SharpenStrength, s.SharpenRadius),
                PI = new Vector4(s.SharpenClamp, s.ChromaStrength, s.ChromaRadial ? 1f : 0f, s.GrainEnabled ? 1f : 0f),
                PJ = new Vector4(s.GrainStrength, s.GrainSize, s.GrainColored ? 1f : 0f, s.DofEnabled ? 1f : 0f),
                PK = new Vector4(s.DofStrength, s.DofFocusRange, s.DofFeather, s.AoEnabled ? 1f : 0f),
                PL = new Vector4(s.AoStrength, s.AoRadius, s.ResolveAoSamples(), _rw),
                PM = new Vector4(_rh, s.BloomStrength, s.BloomThreshold, s.BloomRadius),
                PN = new Vector4(s.BloomTint[0], s.BloomTint[1], s.BloomTint[2], s.SsrIntensity),
                PO = new Vector4(s.SsrGlossiness, s.SsrReflectivity, s.SsrDistance, s.ClarityStrength),
                PP = new Vector4(s.DebandEnabled ? 1f : 0f, s.DebandStrength, s.GiStrength, s.GiRadius),
                PQ = new Vector4(s.FogStrength, s.FogStart, s.FogBrightness, s.AmbientStrength),
                PR = new Vector4(s.EyeAdaptEnabled ? _adaptExposure : 1f, _planeN.X, _planeN.Y, _planeN.Z),
                PS = new Vector4(s.SsrSheen, Math.Clamp((_predAccumX - _depthBaseX) / RiShadeDepth.Size, -0.25f, 0.25f), Math.Clamp(-(_predAccumY - _depthBaseY) / RiShadeDepth.Size, -0.25f, 0.25f), 0f),
                PT = new Vector4(_planeD, aiFlag ? 1f : 0f, s.DebugView, _planeValid ? 1f : 0f),
            };
            _context!.UpdateSubresource(p, _cbuffer!);
        }

        private static readonly ID3D11ShaderResourceView?[] _nullSrvs = new ID3D11ShaderResourceView?[4];

        private void DrawPass(ID3D11PixelShader ps, ID3D11RenderTargetView target, ID3D11ShaderResourceView? t0, ID3D11ShaderResourceView? t1 = null, ID3D11ShaderResourceView? t2 = null, ID3D11ShaderResourceView? t3 = null)
        {
            _context!.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(target);
            _context.PSSetShader(ps);
            _context.PSSetShaderResource(0, t0);
            if (t1 != null) _context.PSSetShaderResource(1, t1);
            if (t2 != null) _context.PSSetShaderResource(2, t2);
            if (t3 != null) _context.PSSetShaderResource(3, t3);
            _context.Draw(3, 0);
        }

        private void RebuildForScaleIfNeeded()
        {
            if (RiShadeSettings.Current.RenderScaleIndex == _builtRenderScaleIndex)
                return;
            _builtRenderScaleIndex = RiShadeSettings.Current.RenderScaleIndex;
            float scale = RiShadeSettings.Current.ResolveRenderScale();
            _rw = Math.Max(64, (int)Math.Round(_width * scale));
            _rh = Math.Max(64, (int)Math.Round(_height * scale));
            try
            {
                CreateSizedResources();
                _lastSettingsVersion = -1;
                App.Logger.WriteLine(LOG_IDENT, $"Render resolution changed live to {_rw}x{_rh}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeOverlay::RebuildForScale", ex);
            }
        }

        private void RenderFrame()
        {
            RebuildForScaleIfNeeded();
            bool fresh = CaptureFrame();
            if (!fresh)
            {
                if (!_hasFirstCapture || RiShadeSettings.Version == _lastSettingsVersion)
                {
                    if (_wgc != null)
                        _wgc.WaitForFrame(20);
                    else if (_duplication == null)
						_runToken.WaitHandle.WaitOne(15);
                    return;
                }
            }

            RenderPasses(RiShadeSettings.Current, _inputSrv!, _backBufferRtv!);

            var presentResult = _swapChain!.Present(0, PresentFlags.None);
            if (presentResult == Vortice.DXGI.ResultCode.DeviceRemoved || presentResult == Vortice.DXGI.ResultCode.DeviceReset)
            {
                App.Logger.WriteLine(LOG_IDENT, "Graphics device was lost, the session will restart");
                _deviceLost = true;
                return;
            }
            _framesPresented++;

            if (!_firstFrameLogged)
            {
                _firstFrameLogged = true;
                App.Logger.WriteLine(LOG_IDENT, $"RiShade is live, first frame presented at {_width}x{_height}");
            }
        }

        private void RenderPasses(RiShadeSettings s, ID3D11ShaderResourceView inputSrv, ID3D11RenderTargetView dst)
        {
            _context!.VSSetShader(_vs);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetConstantBuffer(1, _passCbuffer);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            if (!s.HasVisibleEffects)
            {
                SetPassPx(_width, _height);
                SetVp(_width, _height);
                DrawPass(_psPassthrough!, dst, inputSrv);
                _context.PSSetShaderResources(0, _nullSrvs);
                return;
            }
            if (s.NeedsDepth)
                UpdateAiDepth(s, inputSrv);
            UpdateParamsIfNeeded(s);

            SetVp(_rw, _rh);
            var depthSrv = _aiDepthUploaded ? _aiDepthUpSrv : _aiDepthSrv;
            var srcSrv = inputSrv;
            bool wantSoft = s.ClarityStrength > 0f || s.AmbientStrength > 0f;
            if (wantSoft)
            {
                if (!s.NeedsDepth)
                {
                    SetPassPx(_width, _height);
                    SetVp(LvlW(1), LvlH(1));
                    DrawPass(_psDown!, _workRtv[RtDown0]!, srcSrv);
                }
                SetPassPx(LvlW(1), LvlH(1));
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psBlurH!, _workRtv[RtSceneBlurB]!, _workSrv[RtDown0]);
                DrawPass(_psBlurV!, _workRtv[RtSceneBlurA]!, _workSrv[RtSceneBlurB]);
                SetVp(_rw, _rh);
            }
            if (s.GiStrength > 0f && _aiDepthUploaded)
            {
                SetPassPx(LvlW(1), LvlH(1));
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psGi!, _workRtv[RtUp0]!, _workSrv[RtDown0], depthSrv);
                SetVp(_rw, _rh);
            }
            DrawPass(_psMain!, _workRtv[RtA]!, srcSrv, depthSrv, _workSrv[RtSceneBlurA], _workSrv[RtUp0]);
            int scene = RtA;

            if (s.BloomEnabled && !s.PerfMode)
            {
                int levels = Math.Clamp(s.BloomPasses + 1, 2, 5);
                SetPassPx(_rw, _rh);
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psDownPrefilter!, _workRtv[RtDown0]!, _workSrv[scene]);
                for (int i = 1; i < levels; i++)
                {
                    SetPassPx(LvlW(i), LvlH(i));
                    SetVp(LvlW(i + 1), LvlH(i + 1));
                    DrawPass(_psDown!, _workRtv[RtDown0 + i]!, _workSrv[RtDown0 + i - 1]);
                }
                int src = RtDown0 + levels - 1;
                int srcLevel = levels;
                for (int i = levels - 2; i >= 0; i--)
                {
                    SetPassPx(LvlW(srcLevel), LvlH(srcLevel));
                    SetVp(LvlW(i + 1), LvlH(i + 1));
                    DrawPass(_psUpTent!, _workRtv[RtUp0 + i]!, _workSrv[src], _workSrv[RtDown0 + i]);
                    src = RtUp0 + i;
                    srcLevel = i + 1;
                }
                SetVp(_rw, _rh);
                DrawPass(_psBloomCombine!, _workRtv[RtB]!, _workSrv[scene], _workSrv[src]);
                scene = RtB;
            }

            if (s.SsrEnabled && !s.PerfMode)
            {
                SetPassPx(_rw, _rh);
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psDown!, _workRtv[RtSceneBlurA]!, _workSrv[scene]);
                SetPassPx(LvlW(1), LvlH(1));
                DrawPass(_psBlurH!, _workRtv[RtSceneBlurB]!, _workSrv[RtSceneBlurA]);
                DrawPass(_psBlurV!, _workRtv[RtSceneBlurA]!, _workSrv[RtSceneBlurB]);
                SetPassPx(LvlW(1) / 2, LvlH(1) / 2);
                DrawPass(_psBlurH!, _workRtv[RtGlossTemp]!, _workSrv[RtSceneBlurA]);
                DrawPass(_psBlurV!, _workRtv[RtGlossWide]!, _workSrv[RtGlossTemp]);
                SetVp(LvlW(1), LvlH(1));
                DrawPass(_psSsr!, _workRtv[RtSceneBlurB]!, _workSrv[scene], _workSrv[RtGlossWide], _workSrv[RtSceneBlurA], depthSrv);
                SetVp(_rw, _rh);

                int other = scene == RtA ? RtB : RtA;
                DrawPass(_psComposite!, _workRtv[other]!, _workSrv[scene], _workSrv[RtSceneBlurB]);
                scene = other;
            }

            foreach (var custom in _customEffects)
            {
                int next = scene == RtA ? RtB : RtA;
                DrawPass(custom, _workRtv[next]!, _workSrv[scene], depthSrv);
                scene = next;
            }

            SetPassPx(_rw, _rh, _rw < _width || _rh < _height);
            SetVp(_width, _height);
            DrawPass(_psPassthrough!, dst, _workSrv[scene], depthSrv);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        public void AttachExternal(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
        {
            _device = device;
            _context = context;
            _width = Math.Max(16, width);
            _height = Math.Max(16, height);
            CreatePipeline();
            LoadCustomEffects();
            _lastSettingsVersion = -1;
            App.Logger.WriteLine(LOG_IDENT, $"Attached as a composited stage at {_width}x{_height}, render {_rw}x{_rh}");
        }

        public void EnsureExternalSize(int width, int height)
        {
            width = Math.Max(16, width);
            height = Math.Max(16, height);
            if (width == _width && height == _height)
                return;
            _width = width;
            _height = height;
            float scale = RiShadeSettings.Current.ResolveRenderScale();
            _rw = Math.Max(64, (int)Math.Round(_width * scale));
            _rh = Math.Max(64, (int)Math.Round(_height * scale));
            CreateSizedResources();
            _lastSettingsVersion = -1;
        }

        public void RenderInto(ID3D11Texture2D inputTex, ID3D11RenderTargetView output, int width, int height)
        {
            EnsureExternalSize(width, height);
            RebuildForScaleIfNeeded();
            _context!.CopyResource(_inputTex!, inputTex);
            RenderPasses(RiShadeSettings.Current, _inputSrv!, output);
        }

        public void DisposeExternal()
        {
            try
            {
                RiShadeDepth.Shutdown();
                _context?.ClearState();
                for (int i = 0; i < _workTex.Length; i++)
                {
                    _workRtv[i]?.Dispose();
                    _workSrv[i]?.Dispose();
                    _workTex[i]?.Dispose();
					_workRtv[i] = null;
					_workSrv[i] = null;
					_workTex[i] = null;
                }
                _inputSrv?.Dispose();
                _inputTex?.Dispose();
                _aiDepthSrv?.Dispose();
                _aiDepthTex?.Dispose();
                _aiDepthUpSrv?.Dispose();
                _aiDepthUpRtv?.Dispose();
                _aiDepthUpTex?.Dispose();
                _depthInputRtv?.Dispose();
                _depthInputSrv?.Dispose();
                _depthInputTex?.Dispose();
                _depthStagingTex?.Dispose();
                _depthStagingTexB?.Dispose();
                foreach (var custom in _customEffects)
                    custom.Dispose();
                _customEffects.Clear();
                _psMain?.Dispose();
                _psDownPrefilter?.Dispose();
                _psDown?.Dispose();
                _psUpTent?.Dispose();
                _psBlurH?.Dispose();
                _psBlurV?.Dispose();
                _psBloomCombine?.Dispose();
                _psDepthUp?.Dispose();
                _psGi?.Dispose();
                _psSsr?.Dispose();
                _psComposite?.Dispose();
                _psPassthrough?.Dispose();
                _sampler?.Dispose();
                _cbuffer?.Dispose();
                _passCbuffer?.Dispose();
                _vs?.Dispose();
				_inputSrv = null;
				_inputTex = null;
				_aiDepthSrv = null;
				_aiDepthTex = null;
				_aiDepthUpSrv = null;
				_aiDepthUpRtv = null;
				_aiDepthUpTex = null;
				_depthInputRtv = null;
				_depthInputSrv = null;
				_depthInputTex = null;
				_depthStagingTex = null;
				_depthStagingTexB = null;
				_psMain = null;
				_psDownPrefilter = null;
				_psDown = null;
				_psUpTent = null;
				_psBlurH = null;
				_psBlurV = null;
				_psBloomCombine = null;
				_psDepthUp = null;
				_psGi = null;
				_psSsr = null;
				_psComposite = null;
				_psPassthrough = null;
				_sampler = null;
				_cbuffer = null;
				_passCbuffer = null;
				_vs = null;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeOverlay::DisposeExternal", ex);
            }
        }

        private void LogStatsIfDue()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (_lastStatsLog == 0)
            {
                _lastStatsLog = now;
                return;
            }
			if (now - _lastStatsLog < 60.0)
                return;
            long frames = _framesPresented - _framesAtLastLog;
            double fps = frames / (now - _lastStatsLog);
            App.Logger.WriteLine(LOG_IDENT, $"Running at {fps:0} fps, {_framesPresented} frames total, {_captureTimeouts} idle waits");
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
                    _ = RiShadeInterop.timeEndPeriod(1);
                    _timerRaised = false;
                }
                _wgc?.Dispose();
                _wgc = null;
                _hasFirstCapture = false;
                _context?.ClearState();
                _context?.Flush();
                _duplication?.Dispose();
                for (int i = 0; i < _workTex.Length; i++)
                {
                    _workRtv[i]?.Dispose();
                    _workSrv[i]?.Dispose();
                    _workTex[i]?.Dispose();
                }
                _aiDepthSrv?.Dispose();
                _aiDepthTex?.Dispose();
                _depthStagingTex?.Dispose();
                _depthStagingTexB?.Dispose();
                _stagingFlip = 0;
                _stagingPrimed = false;
                _adaptAvg = 0f;
                _adaptExposure = 1f;
                _antiSmear.Reset();
                _depthBaseX = 0f;
                _depthBaseY = 0f;
                _aiDepthUpSrv?.Dispose();
                _aiDepthUpRtv?.Dispose();
                _aiDepthUpTex?.Dispose();
                _depthInputSrv?.Dispose();
                _depthInputRtv?.Dispose();
                _depthInputTex?.Dispose();
                _inputSrv?.Dispose();
                _inputTex?.Dispose();
                _cbuffer?.Dispose();
                _passCbuffer?.Dispose();
                _sampler?.Dispose();
                _psMain?.Dispose();
                _psDownPrefilter?.Dispose();
                _psDown?.Dispose();
                _psUpTent?.Dispose();
                _psBlurH?.Dispose();
                _psBlurV?.Dispose();
                _psBloomCombine?.Dispose();
                foreach (var custom in _customEffects)
                    custom.Dispose();
                _customEffects.Clear();
                _psDepthUp?.Dispose();
                _psGi?.Dispose();
                _psSsr?.Dispose();
                _psComposite?.Dispose();

                _psPassthrough?.Dispose();
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
                App.Logger.WriteException("RiShadeOverlay::Cleanup", ex);
            }
            try
            {
                if (_hwnd != IntPtr.Zero)
                    RiShadeInterop.UnregisterHotKey(_hwnd, 1);
                if (_hwnd != IntPtr.Zero)
                    RiShadeInterop.DestroyWindow(_hwnd);
                if (_classAtom != 0)
                    RiShadeInterop.UnregisterClassW(new IntPtr(_classAtom), _hInstance);
            }
            catch
            {
            }
            _hwnd = IntPtr.Zero;
            _classAtom = 0;
            _wndProc = null;
            App.Logger.WriteLine(LOG_IDENT, "Overlay stopped and all GPU resources released");
        }
    }
}
