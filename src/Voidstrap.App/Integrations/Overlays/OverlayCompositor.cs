using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.DirectComposition;
using Vortice.Mathematics;
using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.FrameGeneration;
using Voidstrap.Integrations.RiShade;
using D3D11 = Vortice.Direct3D11.D3D11;
using DCompApi = Vortice.DirectComposition.DComp;
using Interop = Voidstrap.Integrations.AntiAliasing.AntiAliasingInterop;

namespace Voidstrap.Integrations.Overlays
{
    internal sealed class OverlayCompositor
    {
        private const string ClassName = "VoidstrapOverlayCompositor";
        private const string CaptureWindowName = "Voidstrap Game Output";
        private const string LOG_IDENT = "Overlays";
        private const uint CreateWaitableTimerHighResolution = 2;
        private const uint TimerAllAccess = 0x1F0003;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr timerAttributes, string? timerName, uint flags, uint desiredAccess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, bool resume);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private Interop.WndProcDelegate? _wndProc;
        private IntPtr _hwnd;
        private ushort _classAtom;
        private IntPtr _hInstance;

        private ID3D11Device? _device;
        private ID3D11DeviceContext? _context;
        private IDXGIFactory2? _factory;
        private IDXGISwapChain1? _swapChain;
        private IDXGISwapChain2? _swapChain2;
        private ID3D11RenderTargetView? _backBufferRtv;
        private IDCompositionDevice? _dcompDevice;
        private IDCompositionTarget? _dcompTarget;
        private IDCompositionVisual? _dcompVisual;
        private IntPtr _frameLatencyHandle;
        private IntPtr _paceTimer;
        private SwapChainFlags _swapChainFlags;

        private IDXGIOutputDuplication? _duplication;
        private RiShadeWgc? _wgc;
        private IntPtr _wgcHwnd;
        private double _wgcRebindAtMs;
        private int _outputLeft;
        private int _outputTop;
        private int _outputRight;
        private int _outputBottom;
		private IntPtr _displayMonitor;
        private int _captureFailures;
        private bool _deviceLost;
        private int _stableCaptureFrames;
        private long _captureUnstableSinceMs;
        private long _lastRecreateMs;
        private bool _hotkeyRegistered;

        private ID3D11VertexShader? _vs;
        private ID3D11PixelShader? _psPass;
        private ID3D11PixelShader? _psCropSrgb;
        private ID3D11PixelShader? _psOverlay;
        private ID3D11PixelShader? _psHomeBackground;
		private HomepageBackgroundMedia? _homepageMedia;
		private string _homepageMediaPath = "";
		private string _homepageMediaRequestedPath = "";
		private string _homepageMediaResolvedPath = "";
		private long _homepageMediaProbeMs;
		private const long HomepageMediaProbeIntervalMs = 1000;
		private ID3D11Texture2D? _homepageMediaTexture;
		private ID3D11ShaderResourceView? _homepageMediaSrv;
		private int _homepageMediaWidth;
		private int _homepageMediaHeight;
		private long _homepageMediaVersion;
        private ID3D11SamplerState? _sampler;
        private ID3D11Buffer? _cbuffer;
        private ID3D11BlendState? _hudBlend;
        private readonly OverlayHud _hud = new OverlayHud();
        private readonly OverlayCrosshair _crosshair = new OverlayCrosshair();
        private const double OverlayRefreshIntervalMs = 250.0;
        private double _crosshairRefreshMs;
        private double _statusRefreshMs;
        private string _statusCachedText = "";
        private bool _hudPainted;
        private double _hudLastMs;
        private bool _hudEnabled;
        private long _hudRealBase;
        private long _hudGenBase;
        private ID3D11Texture2D? _backBufferTex;
        private ID3D11Texture2D? _splitLeftTex;
        private ID3D11ShaderResourceView? _splitLeftSrv;
        private int _splitLeftW, _splitLeftH;
        private ID3D11Texture2D? _splitRightTex;
        private ID3D11ShaderResourceView? _splitRightSrv;
        private int _splitRightW, _splitRightH;
        private ID3D11Texture2D? _splitLineTex;
        private ID3D11ShaderResourceView? _splitLineSrv;
        private ID3D11Texture2D? _fgStatusTex;
        private ID3D11ShaderResourceView? _fgStatusSrv;
        private int _fgStatusW;
        private int _fgStatusH;
        private int _fgStatusWindowWidth;
        private string _fgStatusText = "";
		private readonly string[] _hudLabelsFour = ["FPS", "Real", "Generated", "Engine"];
		private readonly string[] _hudValuesFour = new string[4];
		private readonly string[] _hudLabelsThree = ["FPS", "Real", "Generated"];
		private readonly string[] _hudValuesThree = new string[3];
        private static readonly System.Drawing.Color HudAccent = System.Drawing.Color.FromArgb(86, 156, 255);
        private const int HudX = 18;
        private const int HudY = 120;
        private bool _timerRaised;

        private ID3D11Texture2D? _rawTex;
        private ID3D11ShaderResourceView? _rawSrv;
        private ID3D11RenderTargetView? _rawRtv;
        private readonly ID3D11Texture2D?[] _stageTex = new ID3D11Texture2D?[2];
        private readonly ID3D11ShaderResourceView?[] _stageSrv = new ID3D11ShaderResourceView?[2];
        private readonly ID3D11RenderTargetView?[] _stageRtv = new ID3D11RenderTargetView?[2];
        private readonly ID3D11Texture2D?[] _compTex = new ID3D11Texture2D?[2];
        private readonly ID3D11ShaderResourceView?[] _compSrv = new ID3D11ShaderResourceView?[2];
        private readonly ID3D11RenderTargetView?[] _compRtv = new ID3D11RenderTargetView?[2];
        private Vector4 _dims;

        private readonly RiShadeOverlay _rishade = new RiShadeOverlay();
        private readonly AntiAliasingOverlay _aa = new AntiAliasingOverlay();
        private readonly FrameGenPipeline _fg = new FrameGenPipeline();
        private bool _riAttached;
        private bool _aaAttached;
        private bool _fgAttached;
        private bool _fgJoinHeld;
        private static long _fgHoldUntilTick;

        public static void RequestFrameGenHold(int ms, string reason)
        {
            long until = Environment.TickCount64 + ms;
            if (until > Volatile.Read(ref _fgHoldUntilTick))
            {
                Volatile.Write(ref _fgHoldUntilTick, until);
                App.Logger.WriteLine(LOG_IDENT, $"Frame Generation turning off for {ms / 1000.0:0.#}s: {reason}");
            }
        }
        private bool _pairStale;
        private double _lastSlotMs;
        private int _lastTotal;
        private int _fillCount;
        private double _lastFillLogSec;
        private double _lastPairEndMs;

        private NvFrucEngine? _fruc;
        private bool _frucFailed;
        private int _frucRetries;
        private bool _frucLive;
        private double _lastMultLogSec;

        private int _cur;
        private bool _hasPrev;
        private double _emaIntervalMs;
        private double _freshCaptureMs;
        private double _captureGapEma;
        private uint _lastPresentStat;
        private uint _lastRefreshStat;

        private string DisplayedFramesNote(long presented)
        {
            try
            {
                if (_swapChain!.GetFrameStatistics(out var fs).Failure)
                    return "";
                uint presents = fs.PresentCount;
                uint refreshes = fs.PresentRefreshCount;
                string note = "";
                if (_lastPresentStat != 0 && presents > _lastPresentStat && refreshes > _lastRefreshStat)
                {
                    long shown = presents - _lastPresentStat;
                    long scanouts = refreshes - _lastRefreshStat;
                    double perRefresh = (double)shown / scanouts;
                    note = perRefresh > 1.08
                        ? $", displayed present ratio {perRefresh:0.000}, {shown} presents over only {scanouts} scanouts, the extra ones are discarded"
                        : $", displayed present ratio {perRefresh:0.000}, {shown} presents over {scanouts} scanouts";
                }
                _lastPresentStat = presents;
                _lastRefreshStat = refreshes;
                return note;
            }
            catch
            {
                return "";
            }
        }

        private double _lastFreshPresentMs;

        private double _captureSrcMs;
        private double _lastSrcMs;
        private bool _srcWasCapture;
        private double _paceAnchorMs;
        private double _multErr;
        private double _refreshHz = 60.0;
        private int _lastLoggedMult = -1;
        private readonly double[] _dtRing = new double[15];
        private int _dtCount;
        private int _dtHead;
        private int _dtOutlierRun;
        private double _vbBaseMs;
        private double _vbPeriodMs;
        private double _lastVbQuerySec;
        private double _lastAlignedMs;
        private double _presentWaitMs;
        private long _presentWaitCount;
        private int _fgFailures;
        private bool _fgDisabledByError;
        private const double HoldPresentIntervalMs = 200.0;
        private double _holdNextPresentMs;
        private int _lastDisplayBudget;
        private int _lastGenCount;
        private int _fgQuality = 2;
        private int _effectiveFgQuality = 2;
        private const int AutoGeneratedCeiling = 7;
        private const int MinimumHeadroomCap = 30;
        private const double DropAfterSlotsBehind = 3.0;
        private int _autoGeneratedLimit = AutoGeneratedCeiling;
        private const double SourceRegressionRatio = 0.7;
        private const double SourceRegressionFloor = 30.0;
        private const double SourcePeakDecay = 0.002;
        private const double SourceRegressionHoldSec = 3.0;
        private double _sourceFpsPeak;
        private double _sourceRegressedSinceSec;
        private bool _sourceRegressed;
        private double _lastMissSec;
        private int _fgSixLoadLimit = 5;
        private const int MinimumSixGeneratedLoad = 2;

        private ThreadPriority _threadPriorityApplied = ThreadPriority.Normal;

        private bool _threadPriorityLogged;
        private double _fgQualityLastChangeSec;
        private int _qualitySlotWindow;
        private int _qualityMissWindow;
        private int _qualityStableSlots;
        private double _captureBackpressureUntilSec;
        private long _missedSlotsTotal;
        private long _frucRequestedTotal;
        private long _frucUsedTotal;
        private long _lastCaptureDropCount;
        private double _sourceIntervalMs;
        private double _consumeRatioEma = 1.0;
        private long _captureHealthDropBase;
        private int _outputDeficitWindows;
        private bool _headroomCapApplied;
        private const double SustainedConsumeRatio = 0.9;
        private const double DeficitOutputRatio = 0.9;
        private readonly double[] _paceErrorSamples = new double[2048];
        private int _paceErrorHead;
        private int _paceErrorCount;
        private readonly double[] _captureAgeSamples = new double[2048];
        private int _captureAgeHead;
        private int _captureAgeCount;
        private long _droppedAtLastLog;
        private long _missedAtLastLog;
        private long _frucRequestedAtLastLog;
        private long _frucUsedAtLastLog;
        private double _frucLastFedMs;
        private double _poolWaitEma;
        private double _poolWaitMs;
        private bool _tearingActive;
        private PresentFlags _presentFlags = PresentFlags.None;

        private int _width;
        private int _height;
        private int _rectLeft;
        private int _rectTop;

        private IntPtr _robloxHwnd;
        private bool _hiddenByFocus;
        private int _pendingW;
        private int _pendingH;
        private long _realPresented;
        private long _genPresented;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _lastHwndResolve;
		private long _nextVisibilityCheckMs;
		private long _nextFollowMs;
        private double _lastStatsLog;
        private long _realAtLastLog;
        private long _genAtLastLog;
        private string _lastChainLog = "";
        private bool _fgGenerating;
        private bool _fgEverGenerated;
        private int _fgResetCount;
        private bool _firstCaptureLogged;
        private bool? _recordingVisible;
        private double _windowCaptureRetryAtMs;
        private double _captureTargetFps;
        private double _captureTargetUpdateAtMs;
        private double _captureHealthWindowAtMs;
        private int _captureHealthFrames;
        private int _captureHealthFailures;
        private double _captureRestartAllowedAtMs;
        private int _activeFgMode = -1;

        private static int InitialFrameGenQuality(int mode) => 2;

        private static int InitialGeneratedLoadLimit(int mode) => mode == 5 ? MinimumSixGeneratedLoad : 5;

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        private static ProcessPriorityClass ReadRobloxPriority()
        {
            try
            {
                RobloxWindowRect rect = RobloxWindowTracker.Current;
                if (rect.Hwnd == IntPtr.Zero)
                    return ProcessPriorityClass.Normal;
                GetWindowThreadProcessId(rect.Hwnd, out uint pid);
                if (pid == 0)
                    return ProcessPriorityClass.Normal;
                using Process process = Process.GetProcessById((int)pid);
                return process.PriorityClass;
            }
            catch
            {
                return ProcessPriorityClass.Normal;
            }
        }

        private void ApplyCompositorThreadPriority()
        {
            ProcessPriorityClass roblox = ReadRobloxPriority();
            bool robloxOutranksUs = roblox == ProcessPriorityClass.AboveNormal
                || roblox == ProcessPriorityClass.High
                || roblox == ProcessPriorityClass.RealTime;

            ThreadPriority desired = robloxOutranksUs || _fgAttached ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
            if (_threadPriorityApplied == desired && _threadPriorityLogged)
                return;

            try
            {
                Thread.CurrentThread.Priority = desired;
                _threadPriorityApplied = desired;
                _threadPriorityLogged = true;
                App.Logger.WriteLine(LOG_IDENT, $"Compositor thread priority set to {desired} because Roblox runs at {roblox}");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Compositor thread priority could not be set: " + ex.Message);
            }
        }

        public void Run(CancellationToken token)
        {
            try
            {
                ApplyCompositorThreadPriority();
                ResolveRobloxHwnd();
                if (!TryGetRobloxRect(out var rect))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Roblox window disappeared before compositor start");
                    return;
                }
                _rectLeft = rect.Left;
                _rectTop = rect.Top;
                _width = Math.Max(16, rect.Right - rect.Left);
                _height = Math.Max(16, rect.Bottom - rect.Top);
                App.Logger.WriteLine(LOG_IDENT, $"Starting compositor for Roblox at {_rectLeft},{_rectTop} size {_width}x{_height}");

                CreateWindow();
                CreateDevice();
                ResolveRobloxHwnd();
                _refreshHz = QueryRefreshHz();
                CreateCapture();
                CreateComposition();
                CreatePipeline();
                SyncFrameGenRuntimeServices();

                string riState = !App.Settings.Prop.RiShadeEnabled ? "off"
                    : RiShadeSettings.Current.HasVisibleEffects ? "on"
                    : "on but no effects are switched on, press F8 for the panel";
                App.Logger.WriteLine(LOG_IDENT, $"Compositor started. RiShade {riState}, Anti Aliasing {AntiAliasingSettings.MethodNames[AntiAliasingSettings.MethodIndex]}, Frame Generation {FrameGenSettings.ModeNames[FrameGenSettings.ModeIndex]}, display {_refreshHz:0}Hz, Roblox FPS cap {RobloxFpsCap.Describe()}");
                OverlayHub.SetCompositorLive(true);

                var msg = default(Interop.MSG);
                while (!token.IsCancellationRequested)
                {
                    while (Interop.PeekMessageW(out msg, IntPtr.Zero, 0, 0, Interop.PM_REMOVE))
                    {
                        if (msg.message == RiShadeInterop.WM_HOTKEY)
                        {
                            RiShadePanel.Toggle();
                            continue;
                        }
                        Interop.TranslateMessage(ref msg);
                        Interop.DispatchMessageW(ref msg);
                    }

                    if (_deviceLost)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Restarting the compositor session to recover");
                        break;
                    }

                    if (!OverlaySettings.AnyEnabled)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "All overlays turned off, closing the compositor");
                        break;
                    }

                    if (!UpdateVisibility(token))
                        continue;

                    FollowRoblox();
                    ReloadSettingsIfChanged();
                    SyncFrameGenRuntimeServices();
                    RenderFrame(token);
                    UpdateHudIfDue();
                    LogStatsIfDue();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::Run", ex);
            }
            finally
            {
                Cleanup();
            }
        }

        private void CreateWindow()
        {
            _hInstance = Interop.GetModuleHandleW(null);
            _wndProc = (h, m, w, l) => Interop.DefWindowProcW(h, m, w, l);
            IntPtr classNamePtr = Marshal.StringToHGlobalUni(ClassName);
            try
            {
                var wc = new Interop.WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<Interop.WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = _hInstance,
                    lpszClassName = classNamePtr,
                };
                _classAtom = Interop.RegisterClassExW(ref wc);

                int exStyle = Interop.WS_EX_NOACTIVATE | Interop.WS_EX_TOOLWINDOW | Interop.WS_EX_TRANSPARENT | Interop.WS_EX_TOPMOST | Interop.WS_EX_LAYERED | Interop.WS_EX_NOREDIRECTIONBITMAP;
                _hwnd = Interop.CreateWindowExW(exStyle, new IntPtr(_classAtom), CaptureWindowName, Interop.WS_POPUP, _rectLeft, _rectTop, _width, _height, IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

                Interop.SetLayeredWindowAttributes(_hwnd, 0, 255, Interop.LWA_ALPHA);
                Interop.SetWindowPos(_hwnd, Interop.HWND_TOPMOST, _rectLeft, _rectTop, _width, _height, Interop.SWP_NOACTIVATE | Interop.SWP_SHOWWINDOW);
                Interop.ShowWindow(_hwnd, Interop.SW_SHOWNOACTIVATE);
                OverlayDiagnostics.RaiseOverlayWindows();
                App.Logger.WriteLine(LOG_IDENT, "Compositor window created, click through");
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

        private void CreateCapture()
        {
            if (_robloxHwnd == IntPtr.Zero || !Interop.IsWindow(_robloxHwnd))
                ResolveRobloxHwnd();

            _wgc = _robloxHwnd != IntPtr.Zero ? RiShadeWgc.TryCreate(_device!, _robloxHwnd, CaptureTargetFps()) : null;
            _wgcHwnd = _wgc != null ? _robloxHwnd : IntPtr.Zero;
            if (_wgc == null && _robloxHwnd != IntPtr.Zero)
                App.Logger.WriteLine(LOG_IDENT, "Window capture unavailable for the Roblox window, falling back to desktop duplication which is limited to the monitor refresh rate");
            if (_wgc != null)
            {
                SetRecordingVisibility(true);
                App.Logger.WriteLine(LOG_IDENT, "Using window capture for the Roblox window");
                return;
            }
            if (!CreateDuplicationForRect(_rectLeft, _rectTop))
                App.Logger.WriteLine(LOG_IDENT, "Could not create desktop duplication, will retry while running");
        }

        private void SetRecordingVisibility(bool visible)
        {
            if (_recordingVisible == visible || _hwnd == IntPtr.Zero)
                return;
            uint affinity = visible ? Interop.WDA_NONE : Interop.WDA_EXCLUDEFROMCAPTURE;
            if (!Interop.SetWindowDisplayAffinity(_hwnd, affinity))
            {
                App.Logger.WriteLine(LOG_IDENT, visible
                    ? "Could not make the game output visible to recording software"
                    : "Could not protect screen capture fallback from feedback");
                return;
            }
            _recordingVisible = visible;
            App.Logger.WriteLine(LOG_IDENT, visible
                ? "OBS output ready as Voidstrap Game Output"
                : "OBS output paused while screen capture fallback prevents feedback");
        }

        private void CreateComposition()
        {
            _swapChainFlags = SwapChainFlags.FrameLatencyWaitableObject;
            var swapDesc = new SwapChainDescription1
            {
                Width = (uint)_width,
                Height = (uint)_height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 4,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
                Flags = _swapChainFlags,
            };

            try
            {
                _swapChain = _factory!.CreateSwapChainForComposition(_device!, swapDesc, null);
            }
            catch (Exception ex)
            {
                _swapChainFlags = SwapChainFlags.None;
                swapDesc.Flags = _swapChainFlags;
                _swapChain = _factory!.CreateSwapChainForComposition(_device!, swapDesc, null);
                App.Logger.WriteLine(LOG_IDENT, "Frame latency waitable swapchain unavailable, using the standard composition queue: " + ex.Message);
            }
            CreateBackBufferRtv();
            if ((_swapChainFlags & SwapChainFlags.FrameLatencyWaitableObject) != 0)
            {
                try
                {
                    _swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
                    if (_swapChain2 != null)
                    {
                        uint latency = FrameGenSettings.ModeIndex > 0 ? 3u : 1u;
                        _swapChain2.MaximumFrameLatency = latency;
                        _frameLatencyHandle = _swapChain2.FrameLatencyWaitableObject;
                        App.Logger.WriteLine(LOG_IDENT, $"Present queue depth {latency}");
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Frame latency synchronization unavailable, continuing with queue depth one: " + ex.Message);
                }
            }
            if (_frameLatencyHandle == IntPtr.Zero)
                RaiseFrameLatencyLimit(1);
            _paceTimer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_paceTimer == IntPtr.Zero)
                _paceTimer = CreateWaitableTimerExW(IntPtr.Zero, null, 0, TimerAllAccess);

            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            _dcompDevice = DCompApi.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
            _dcompDevice.CreateTargetForHwnd(_hwnd, true, out _dcompTarget);
            _dcompVisual = _dcompDevice.CreateVisual();
            _dcompVisual.SetContent(_swapChain);
            _dcompTarget!.SetRoot(_dcompVisual);
            _dcompDevice.Commit();
            App.Logger.WriteLine(LOG_IDENT, "DirectComposition swapchain attached to the compositor window");
        }

        private void RaiseFrameLatencyLimit(int frames)
        {
            try
            {
                using var dxgiDevice1 = _device!.QueryInterface<IDXGIDevice1>();
                dxgiDevice1.MaximumFrameLatency = (uint)Math.Max(1, frames);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not set the frame latency limit: " + ex.Message);
            }
        }

        private void CreateBackBufferRtv()
        {
            _backBufferTex?.Dispose();
            _backBufferTex = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            _backBufferRtv = _device!.CreateRenderTargetView(_backBufferTex);
        }

        private ID3D11PixelShader CompilePs(string entry)
        {
            Vortice.D3DCompiler.Compiler.Compile(FrameGenShaders.Source, entry, "FrameGen", "ps_5_0", out var blob, out var err);
            using (err)
            {
                if (blob == null)
                {
                    string msg = err != null ? err.AsString() : "unknown";
                    throw new InvalidOperationException("Compositor shader compile failed for " + entry + ": " + msg);
                }
            }
            using (blob)
            {
                return _device!.CreatePixelShader(blob.AsBytes());
            }
        }

        private void CreatePipeline()
        {
            Vortice.D3DCompiler.Compiler.Compile(FrameGenShaders.Source, "VSMain", "FrameGen", "vs_5_0", out var vsBlob, out var vsErr);
            using (vsErr)
            {
                if (vsBlob == null)
                    throw new InvalidOperationException("Compositor vertex shader compile failed");
            }
            using (vsBlob)
            {
                _vs = _device!.CreateVertexShader(vsBlob.AsBytes());
            }
            _psPass = CompilePs("PSPass");
            _psCropSrgb = CompilePs("PSCropSrgb");
            _psOverlay = CompilePs("PSOverlay");
            _psHomeBackground = CompilePs("PSHomeBackground");

            _hudBlend = _device!.CreateBlendState(new BlendDescription(Blend.SourceAlpha, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
            _hud.Init(_device!);
            _crosshair.Init(_device!);
            CreateSplitAssets();

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
                ByteWidth = (uint)Marshal.SizeOf<FrameGenPipelineParams>(),
                BindFlags = BindFlags.ConstantBuffer,
                Usage = ResourceUsage.Default,
                CPUAccessFlags = CpuAccessFlags.None,
            });

            CreateSizedResources();
        }

        private void CreateSizedResources()
        {
            ReleaseSizedResources();
            _rawTex = CreateTex();
            _rawSrv = _device!.CreateShaderResourceView(_rawTex);
            _rawRtv = _device!.CreateRenderTargetView(_rawTex);
            for (int i = 0; i < 2; i++)
            {
                _stageTex[i] = CreateTex();
                _stageSrv[i] = _device!.CreateShaderResourceView(_stageTex[i]);
                _stageRtv[i] = _device!.CreateRenderTargetView(_stageTex[i]);
                _compTex[i] = CreateTex();
                _compSrv[i] = _device!.CreateShaderResourceView(_compTex[i]);
                _compRtv[i] = _device!.CreateRenderTargetView(_compTex[i]);
            }
            _dims = new Vector4(_width, _height, 1f / Math.Max(_width, 1), 1f / Math.Max(_height, 1));
            _cur = 0;
            _hasPrev = false;
            _emaIntervalMs = 0;
            _lastSrcMs = 0;
            _paceAnchorMs = 0;
            _multErr = 0;
            _lastLoggedMult = -1;
            _dtCount = 0;
            _dtHead = 0;
            _dtOutlierRun = 0;
            _lastAlignedMs = 0;
			_captureGapEma = 0;
			_lastFreshPresentMs = 0;
			_freshCaptureMs = 0;
			_captureSrcMs = 0;
			ResetHudTelemetry();
        }

        private ID3D11Texture2D CreateTex()
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
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
            });
        }

        private void ReleaseSizedResources()
        {
            _rawRtv?.Dispose();
            _rawSrv?.Dispose();
            _rawTex?.Dispose();
            _rawRtv = null;
            _rawSrv = null;
            _rawTex = null;
            for (int i = 0; i < 2; i++)
            {
                _stageRtv[i]?.Dispose();
                _stageSrv[i]?.Dispose();
                _stageTex[i]?.Dispose();
                _stageRtv[i] = null;
                _stageSrv[i] = null;
                _stageTex[i] = null;
                _compRtv[i]?.Dispose();
                _compSrv[i]?.Dispose();
                _compTex[i]?.Dispose();
                _compRtv[i] = null;
                _compSrv[i] = null;
                _compTex[i] = null;
            }
        }

        private bool CreateDuplicationForRect(int left, int top)
        {
            try
            {
                SetRecordingVisibility(false);
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
                App.Logger.WriteException("OverlayCompositor::CreateDuplication", ex);
                return false;
            }
        }

        private void RebindCaptureIfWindowChanged()
        {
            if (_wgc == null || _robloxHwnd == IntPtr.Zero || _robloxHwnd == _wgcHwnd)
                return;

            App.Logger.WriteLine(LOG_IDENT, $"Roblox window handle changed, rebinding window capture to 0x{_robloxHwnd.ToInt64():X}");
            _wgc.Dispose();
            _wgc = RiShadeWgc.TryCreate(_device!, _robloxHwnd, CaptureTargetFps());
            _wgcHwnd = _wgc != null ? _robloxHwnd : IntPtr.Zero;
            if (_wgc == null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Window capture failed on the new window, falling back to desktop duplication");
                _refreshHz = QueryRefreshHz();
                CreateDuplicationForRect(_rectLeft, _rectTop);
            }
            else
            {
                _duplication?.Dispose();
                _duplication = null;
                SetRecordingVisibility(true);
            }
			ResetCaptureMeasurements();
        }

        private bool TryRestoreWindowCapture()
        {
            double nowMs = _clock.Elapsed.TotalMilliseconds;
            if (_wgc != null || nowMs < _windowCaptureRetryAtMs)
                return _wgc != null;
            _windowCaptureRetryAtMs = nowMs + 2000.0;
            ResolveRobloxHwnd();
            if (_robloxHwnd == IntPtr.Zero || !Interop.IsWindow(_robloxHwnd))
                return false;
            _wgc = RiShadeWgc.TryCreate(_device!, _robloxHwnd, CaptureTargetFps());
            if (_wgc == null)
                return false;
            _wgcHwnd = _robloxHwnd;
            _duplication?.Dispose();
            _duplication = null;
            SetRecordingVisibility(true);
			ResetCaptureMeasurements();
            App.Logger.WriteLine(LOG_IDENT, "Window capture restored, OBS output resumed with the finished compositor frame");
            return true;
        }

        private void ResolveRobloxHwnd()
        {
            try
            {
                IntPtr handle = RobloxLightingOverlay.RobloxWindow.GetHandle();
                if (handle != IntPtr.Zero)
                    _robloxHwnd = handle;
            }
            catch
            {
            }
        }

        private bool TryGetRobloxRect(out Interop.RECT rect)
        {
            rect = default;
            if (_robloxHwnd == IntPtr.Zero || !Interop.IsWindow(_robloxHwnd))
            {
                _robloxHwnd = IntPtr.Zero;
                ResolveRobloxHwnd();
                if (_robloxHwnd == IntPtr.Zero)
                    return false;
            }
            if (!Interop.GetWindowRect(_robloxHwnd, out rect))
            {
                _robloxHwnd = IntPtr.Zero;
                return false;
            }
            IntPtr monitor = Interop.MonitorFromWindow(_robloxHwnd, Interop.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var mi = new Interop.MONITORINFO { cbSize = (uint)Marshal.SizeOf<Interop.MONITORINFO>() };
                if (Interop.GetMonitorInfoW(monitor, ref mi))
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
			_nextVisibilityCheckMs = tick + 250;
            if (_robloxHwnd == IntPtr.Zero)
                ResolveRobloxHwnd();
            IntPtr fg = Interop.GetForegroundWindow();
            bool robloxActive = _robloxHwnd != IntPtr.Zero && fg == _robloxHwnd
                             || OverlayDiagnostics.IsOverlayHandle(fg)
                             || RobloxWindowTracker.IsRobloxForeground();
            if (robloxActive)
            {
                if (_hiddenByFocus)
                {
                    _hiddenByFocus = false;
                    App.Logger.WriteLine(LOG_IDENT, "Roblox is in the foreground again, the overlay is rendering");
                    TryRestoreWindowCapture();
                    _wgc?.SetTargetFps(CaptureTargetFps());
                    if (FrameGenSettings.ModeIndex > 0)
                    {
                        ResetFrameGenAfterFocus();
                        RequestFrameGenHold(500, "window focus changed, capture is settling before generation resumes");
                    }
                    Interop.ShowWindow(_hwnd, Interop.SW_SHOWNOACTIVATE);
                    AssertZOrder();
                }
                return true;
            }
            if (!_hiddenByFocus)
            {
                _hiddenByFocus = true;
                App.Logger.WriteLine(LOG_IDENT, $"Idle, Roblox is not the foreground window (roblox 0x{_robloxHwnd.ToInt64():X}, foreground 0x{fg.ToInt64():X}), nothing renders until it comes back");
                if (FrameGenSettings.ModeIndex > 0)
                {
                    ResetFrameGenState();
                    _fruc?.Deprime();
                }
                _wgc?.Dispose();
                _wgc = null;
                _wgcHwnd = IntPtr.Zero;
                Interop.ShowWindow(_hwnd, Interop.SW_HIDE);
            }
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _lastHwndResolve > 5.0)
            {
                _lastHwndResolve = now;
                _robloxHwnd = IntPtr.Zero;
                ResolveRobloxHwnd();
            }
            token.WaitHandle.WaitOne(750);
            return false;
        }

        private void FollowRoblox()
        {
			long tick = Environment.TickCount64;
			if (tick < _nextFollowMs)
				return;
			_nextFollowMs = tick + 250;
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
			IntPtr currentMonitor = Interop.MonitorFromWindow(_robloxHwnd, Interop.MONITOR_DEFAULTTONEAREST);
			bool monitorChanged = currentMonitor != IntPtr.Zero && currentMonitor != _displayMonitor;
            if (sizeChanged && (w != _pendingW || h != _pendingH))
            {
                _pendingW = w;
                _pendingH = h;
                _rectLeft = rect.Left;
                _rectTop = rect.Top;
                Interop.SetWindowPos(_hwnd, IntPtr.Zero, _rectLeft, _rectTop, w, h, Interop.SWP_NOACTIVATE | Interop.SWP_NOZORDER);
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
                _backBufferTex?.Dispose();
                _backBufferTex = null;
                _swapChain!.ResizeBuffers(4, (uint)_width, (uint)_height, Format.B8G8R8A8_UNorm, _swapChainFlags | (_tearingActive ? SwapChainFlags.AllowTearing : SwapChainFlags.None));
                CreateBackBufferRtv();
                CreateSizedResources();
                _refreshHz = QueryRefreshHz();
                if (_wgc == null)
                    CreateDuplicationForRect(_rectLeft, _rectTop);
            }
			else if (monitorChanged)
			{
				App.Logger.WriteLine(LOG_IDENT, "Roblox moved to another monitor, refreshing capture and pacing");
				_refreshHz = QueryRefreshHz();
				_wgc?.SetTargetFps(CaptureTargetFps());
				if (_wgc == null)
					CreateDuplicationForRect(_rectLeft, _rectTop);
				ResetCaptureMeasurements();
			}
            else if (_wgc == null)
            {
                int cx = _rectLeft + _width / 2;
                int cy = _rectTop + _height / 2;
                bool sameOutput = cx >= _outputLeft && cx < _outputRight && cy >= _outputTop && cy < _outputBottom;
                if (!sameOutput)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Roblox moved to another monitor, reacquiring capture");
                    _refreshHz = QueryRefreshHz();
                    CreateDuplicationForRect(_rectLeft, _rectTop);
                }
            }
            RebindCaptureIfWindowChanged();
            AssertZOrder();
        }

        private void AssertZOrder()
        {
            Interop.SetWindowPos(_hwnd, IntPtr.Zero, _rectLeft, _rectTop, _width, _height, Interop.SWP_NOACTIVATE | Interop.SWP_NOZORDER);
        }

        private double QueryRefreshHz()
        {
            try
            {
                IntPtr target = _robloxHwnd != IntPtr.Zero ? _robloxHwnd : _hwnd;
                IntPtr mon = Interop.MonitorFromWindow(target, Interop.MONITOR_DEFAULTTONEAREST);
                if (mon == IntPtr.Zero)
                    return 60.0;
				_displayMonitor = mon;
                var mi = new Interop.MONITORINFOEXW { cbSize = (uint)Marshal.SizeOf<Interop.MONITORINFOEXW>() };
                if (!Interop.GetMonitorInfoW(mon, ref mi))
                    return 60.0;
                var dm = new Interop.DEVMODEW { dmSize = (ushort)Marshal.SizeOf<Interop.DEVMODEW>() };
                if (Interop.EnumDisplaySettingsW(mi.szDevice, Interop.ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1)
                    return dm.dmDisplayFrequency;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not read the display refresh rate, assuming 60Hz: " + ex.Message);
            }
            return 60.0;
        }

        private double CaptureTargetFps()
        {
			if (OverlayHub.HomepageBackgroundActive && !App.Settings.Prop.RiShadeEnabled)
				return _homepageMedia?.IsAnimated == true ? _refreshHz : Math.Min(60.0, _refreshHz);
            int cap = RobloxFpsCap.Cap;
            if (FrameGenSettings.ModeIndex <= 0)
                return cap > 0 && cap < 1000 ? Math.Min(_refreshHz, cap) : _refreshHz;
            double actual = ReliableActualFps();
            double target;
            if (actual > 0)
                target = Math.Ceiling(actual * 1.12 / 15.0) * 15.0;
            else if (cap > 0 && cap < 1000)
                target = cap;
            else
                target = Math.Min(_refreshHz, 120.0);
            return Math.Clamp(target, 30.0, Math.Max(30.0, _refreshHz));
        }

        private double ReliableActualFps()
        {
            double actual = RobloxPresentTracer.FramesPerSecond;
            double ceiling = Math.Max(90.0, _refreshHz * 1.25);
            return actual >= 5.0 && actual <= ceiling ? actual : 0;
        }

		private static Vector4 ReadHomepageBackgroundColor()
		{
			return ReadHomepageColor(App.Settings.Prop.HomepageBackgroundOverlayColor, new Vector4(18f / 255f, 18f / 255f, 21f / 255f, 1f));
		}

		private static Vector4 ReadHomepageGradientColor()
		{
			return ReadHomepageColor(App.Settings.Prop.HomepageBackgroundOverlayGradientColor, new Vector4(91f / 255f, 46f / 255f, 1f, 1f));
		}

		private static Vector4 ReadHomepageColor(string? value, Vector4 fallback)
		{
			value ??= "";
			if (value.Length == 7 && value[0] == '#'
				&& int.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int rgb))
			{
				return new Vector4(
					((rgb >> 16) & 0xFF) / 255f,
					((rgb >> 8) & 0xFF) / 255f,
					(rgb & 0xFF) / 255f,
					1f);
			}
			return fallback;
		}

        private void UpdateWindowCaptureHealth(bool captured)
        {
            if (_wgc == null || _hiddenByFocus)
                return;
            double now = _clock.Elapsed.TotalMilliseconds;
            if (captured)
                _captureHealthFrames++;
            if (now >= _captureTargetUpdateAtMs)
            {
                _captureTargetUpdateAtMs = now + 1000.0;
                double target = CaptureTargetFps();
                if (_captureTargetFps <= 0 || Math.Abs(target - _captureTargetFps) >= 10.0)
                {
                    _captureTargetFps = target;
                    _wgc.SetTargetFps(target);
                }
            }
            if (_captureHealthWindowAtMs == 0)
            {
                _captureHealthWindowAtMs = now;
                _captureHealthFrames = 0;
                _captureHealthDropBase = _wgc.DroppedCount;
                return;
            }
            double windowMs = now - _captureHealthWindowAtMs;
            if (windowMs < 2000.0)
                return;
            double captureFps = _captureHealthFrames * 1000.0 / windowMs;
            long dropsNow = _wgc.DroppedCount;
            double droppedFps = Math.Max(0, dropsNow - _captureHealthDropBase) * 1000.0 / windowMs;
            _captureHealthDropBase = dropsNow;
            double sourceFps = Math.Max(ReliableActualFps(), captureFps + droppedFps);
            bool unhealthy = sourceFps >= 30.0 && captureFps < Math.Max(8.0, sourceFps * 0.45);
            _captureHealthFailures = unhealthy ? _captureHealthFailures + 1 : 0;
            _captureHealthWindowAtMs = now;
            _captureHealthFrames = 0;
            if (_captureHealthFailures >= 2 && now >= _captureRestartAllowedAtMs)
                RestartWindowCapture(captureFps, sourceFps);
        }

        private void RestartWindowCapture(double captureFps, double actualFps)
        {
            _captureRestartAllowedAtMs = _clock.Elapsed.TotalMilliseconds + 8000.0;
            _captureHealthFailures = 0;
            App.Logger.WriteLine(LOG_IDENT, $"Window capture slowed to {captureFps:0} FPS while Roblox remained at {actualFps:0} FPS, rebuilding capture");
            _wgc?.Dispose();
            _wgc = null;
            _wgcHwnd = IntPtr.Zero;
            ResolveRobloxHwnd();
            if (_robloxHwnd != IntPtr.Zero && Interop.IsWindow(_robloxHwnd))
            {
                _captureTargetFps = CaptureTargetFps();
                _wgc = RiShadeWgc.TryCreate(_device!, _robloxHwnd, _captureTargetFps);
                _wgcHwnd = _wgc != null ? _robloxHwnd : IntPtr.Zero;
            }
            if (_wgc == null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Window capture recovery failed, using screen capture until it can retry");
                CreateDuplicationForRect(_rectLeft, _rectTop);
                _windowCaptureRetryAtMs = _clock.Elapsed.TotalMilliseconds + 2000.0;
            }
            else
            {
                _duplication?.Dispose();
                _duplication = null;
                SetRecordingVisibility(true);
            }
            ResetCaptureMeasurements();
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
            if (now - _captureUnstableSinceMs > 20000)
            {
                App.Logger.WriteLine(LOG_IDENT, "Screen capture stayed unstable, ending this compositor session");
                _deviceLost = true;
                return false;
            }
            if (now - _lastRecreateMs >= 500)
            {
                _lastRecreateMs = now;
                CreateDuplicationForRect(_rectLeft, _rectTop);
            }
            Thread.Sleep(_captureFailures < 4 ? 1 : 8);
            return false;
        }

        private bool CaptureFrame()
        {
            if (_rawTex == null)
                return false;

            if (_wgc == null && TryRestoreWindowCapture())
                return false;

            if (_wgc != null)
            {
                if (_wgc.IsClosed)
                {
                    _wgcHwnd = IntPtr.Zero;
                    double nowMs = _clock.Elapsed.TotalMilliseconds;
                    if (nowMs >= _wgcRebindAtMs)
                    {
                        _wgcRebindAtMs = nowMs + 1000.0;
                        ResolveRobloxHwnd();
                        if (_robloxHwnd != IntPtr.Zero && Interop.IsWindow(_robloxHwnd))
                        {
                            RebindCaptureIfWindowChanged();
                            return false;
                        }
                    }
                    App.Logger.WriteLine(LOG_IDENT, "Captured window closed, restarting the session");
                    _deviceLost = true;
                    return false;
                }
                if (!_wgc.TryCopyLatestFrame(_context!, _rawTex, _width, _height, out double wgcTimeMs))
                {
                    UpdateWindowCaptureHealth(false);
                    return false;
                }
                UpdateWindowCaptureHealth(true);
                _captureSrcMs = wgcTimeMs;
                RobloxPresentTracer.ReportCapturedFrame(wgcTimeMs);
                return true;
            }

            if (_duplication == null)
                return HandleCaptureUnstable("Screen capture not available");

            IDXGIResource? desktopResource = null;
            bool acquired = false;
            try
            {
                var result = _duplication.AcquireNextFrame(16, out var frameInfo, out desktopResource);
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
                if (frameInfo.LastPresentTime == 0 && _hasPrev)
                    return false;
                _captureSrcMs = frameInfo.LastPresentTime != 0
                    ? frameInfo.LastPresentTime * 1000.0 / Stopwatch.Frequency
                    : 0;

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
                    _context!.CopySubresourceRegion(_rawTex, 0, 0, 0, 0, desktopTex, 0, box);
                }
                else
                {
                    using var desktopSrv = _device!.CreateShaderResourceView(desktopTex);
                    _context!.VSSetShader(_vs);
                    _context.PSSetConstantBuffer(0, _cbuffer);
                    _context.PSSetSampler(0, _sampler);
                    _context.IASetInputLayout(null);
                    _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                    _context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
                    _context.UpdateSubresource(new FrameGenPipelineParams
                    {
                        Dims = _dims,
                        SrcRect = new Vector4(
                            (float)srcLeft / desc.Width,
                            (float)srcTop / desc.Height,
                            (float)(right - srcLeft) / desc.Width,
                            (float)(bottom - srcTop) / desc.Height),
                        Interp = Vector4.Zero,
                    }, _cbuffer!);
                    DrawBlit(_psCropSrgb!, _rawRtv!, desktopSrv);
                }
                return true;
            }
            finally
            {
                desktopResource?.Dispose();
                if (acquired)
                    _duplication.ReleaseFrame();
            }
        }

        private static readonly ID3D11ShaderResourceView?[] _nullSrvs = new ID3D11ShaderResourceView?[3];

        private void DrawBlitSized(ID3D11PixelShader ps, ID3D11RenderTargetView target, ID3D11ShaderResourceView input, int w, int h)
        {
            _context!.VSSetShader(_vs);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.RSSetViewport(new Viewport(0, 0, w, h, 0, 1));
            _context.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(target);
            _context.PSSetShader(ps);
            _context.PSSetShaderResource(0, input);
            _context.Draw(3, 0);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        private void DrawBlit(ID3D11PixelShader ps, ID3D11RenderTargetView target, ID3D11ShaderResourceView input)
        {
            _context!.VSSetShader(_vs);
            _context.PSSetConstantBuffer(0, _cbuffer);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
            _context.PSSetShaderResources(0, _nullSrvs);
            _context.OMSetRenderTargets(target);
            _context.PSSetShader(ps);
            _context.PSSetShaderResource(0, input);
            _context.Draw(3, 0);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

		private void DrawHomepage(ID3D11RenderTargetView target, ID3D11ShaderResourceView input, ID3D11ShaderResourceView? media)
		{
			_context!.VSSetShader(_vs);
			_context.PSSetConstantBuffer(0, _cbuffer);
			_context.PSSetSampler(0, _sampler);
			_context.IASetInputLayout(null);
			_context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
			_context.RSSetViewport(new Viewport(0, 0, _width, _height, 0, 1));
			_context.PSSetShaderResources(0, _nullSrvs);
			_context.OMSetRenderTargets(target);
			_context.PSSetShader(_psHomeBackground);
			_context.PSSetShaderResource(0, input);
			_context.PSSetShaderResource(1, media);
			_context.Draw(3, 0);
			_context.PSSetShaderResources(0, _nullSrvs);
		}

		private bool UpdateHomepageMedia()
		{
			try
			{
				string path = OverlaySettings.HomepageBackgroundMode == "Media" ? App.Settings.Prop.HomepageBackgroundOverlayMediaPath ?? "" : "";
				if (!string.Equals(path, _homepageMediaRequestedPath, StringComparison.OrdinalIgnoreCase))
				{
					_homepageMediaRequestedPath = path;
					_homepageMediaProbeMs = 0;
				}
				long nowMs = Environment.TickCount64;
				if (nowMs - _homepageMediaProbeMs >= HomepageMediaProbeIntervalMs)
				{
					_homepageMediaProbeMs = nowMs;
					_homepageMediaResolvedPath = path.Length > 0 && File.Exists(path) ? path : "";
				}
				string resolved = _homepageMediaResolvedPath;
				if (!string.Equals(resolved, _homepageMediaPath, StringComparison.OrdinalIgnoreCase))
				{
					ReleaseHomepageMedia();
					_homepageMediaPath = resolved;
					if (resolved.Length > 0)
						_homepageMedia = new HomepageBackgroundMedia(resolved, _refreshHz);
				}
				if (_homepageMedia == null)
					return false;
				_homepageMedia.TryReadFrame(_homepageMediaVersion, UploadHomepageMediaFrame);
				return _homepageMediaSrv != null;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "The homepage background media failed, continuing without it: " + ex.Message);
				ReleaseHomepageMedia();
				_homepageMediaPath = "";
				_homepageMediaResolvedPath = "";
				return false;
			}
		}

		private void UploadHomepageMediaFrame(byte[] pixels, int width, int height, long version)
		{
			if (_homepageMediaTexture == null || width != _homepageMediaWidth || height != _homepageMediaHeight)
			{
				_homepageMediaSrv?.Dispose();
				_homepageMediaTexture?.Dispose();
				_homepageMediaTexture = _device!.CreateTexture2D(new Texture2DDescription
				{
					Width = (uint)width,
					Height = (uint)height,
					MipLevels = 1,
					ArraySize = 1,
					Format = Format.B8G8R8A8_UNorm,
					SampleDescription = new SampleDescription(1, 0),
					Usage = ResourceUsage.Dynamic,
					BindFlags = BindFlags.ShaderResource,
					CPUAccessFlags = CpuAccessFlags.Write
				});
				_homepageMediaSrv = _device.CreateShaderResourceView(_homepageMediaTexture);
				_homepageMediaWidth = width;
				_homepageMediaHeight = height;
			}
			var mapped = _context!.Map(_homepageMediaTexture!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
			try
			{
				int rowBytes = width * 4;
				unsafe
				{
					fixed (byte* source = pixels)
					{
						byte* targetRow = (byte*)mapped.DataPointer;
						for (int y = 0; y < height; y++)
							Buffer.MemoryCopy(source + y * rowBytes, targetRow + y * (int)mapped.RowPitch, rowBytes, rowBytes);
					}
				}
			}
			finally
			{
				_context.Unmap(_homepageMediaTexture!, 0);
			}
			_homepageMediaVersion = version;
		}

		private void ReleaseHomepageMedia()
		{
			_homepageMedia?.Dispose();
			_homepageMedia = null;
			_homepageMediaSrv?.Dispose();
			_homepageMediaSrv = null;
			_homepageMediaTexture?.Dispose();
			_homepageMediaTexture = null;
			_homepageMediaWidth = 0;
			_homepageMediaHeight = 0;
			_homepageMediaVersion = 0;
		}

        private void ResetFrameGenState()
        {
            _fg.ResetHistory();
            _hasPrev = false;
            _pairStale = true;
            _fgGenerating = false;
            _emaIntervalMs = 0;
            _dtCount = 0;
            _dtHead = 0;
            _dtOutlierRun = 0;
            _paceAnchorMs = 0;
            _lastAlignedMs = 0;
            _multErr = 0;
            _lastSrcMs = 0;
            _qualitySlotWindow = 0;
            _qualityMissWindow = 0;
            _qualityStableSlots = 0;
            _captureBackpressureUntilSec = 0;
            _fillCount = 0;
            _lastTotal = 0;
            _lastSlotMs = 0;
            _lastPairEndMs = 0;
            _lastLoggedMult = -1;
            _frucLastFedMs = 0;
            _holdNextPresentMs = 0;
            _lastCaptureDropCount = _wgc?.DroppedCount ?? 0;
            _sourceIntervalMs = 0;
            _consumeRatioEma = 1.0;
            RobloxPresentTracer.ReportFrameGenerationCadence(0);
        }

        private void ResetFrameGenAfterFocus()
        {
			ResetCaptureMeasurements();
            _fruc?.Deprime();
            _effectiveFgQuality = _fgQuality;
            _captureAgeHead = 0;
            _captureAgeCount = 0;
            _paceErrorHead = 0;
            _paceErrorCount = 0;
            _lastStatsLog = _clock.Elapsed.TotalSeconds;
            _realAtLastLog = _realPresented;
            _genAtLastLog = _genPresented;
            _droppedAtLastLog = _wgc?.DroppedCount ?? 0;
            _missedAtLastLog = _missedSlotsTotal;
            _frucRequestedAtLastLog = _frucRequestedTotal;
            _frucUsedAtLastLog = _frucUsedTotal;
        }

		private void ResetCaptureMeasurements()
		{
			ResetFrameGenState();
			ResetHudTelemetry();
			_captureGapEma = 0;
			_lastFreshPresentMs = 0;
			_freshCaptureMs = 0;
			_captureSrcMs = 0;
			_poolWaitMs = 0;
			_poolWaitEma = 0;
			_captureHealthDropBase = _wgc?.DroppedCount ?? 0;
			_outputDeficitWindows = 0;
		}

		private void ResetHudTelemetry()
		{
			_hudLastMs = 0;
			_hudRealBase = _realPresented;
			_hudGenBase = _genPresented;
			_hudPainted = false;
		}

        private void SyncStages(bool riEnabled, bool riOn, bool aaOn, bool fgOn)
        {
            if (riOn && !_riAttached)
            {
                try { _rishade.AttachExternal(_device!, _context!, _width, _height); _riAttached = true; }
                catch (Exception ex) { App.Logger.WriteException("OverlayCompositor::AttachRiShade", ex); }
            }
            else if (!riOn && _riAttached)
            {
                _rishade.DisposeExternal();
                _riAttached = false;
                App.Logger.WriteLine(LOG_IDENT, "RiShade stage detached, no effects are switched on so the frame is left untouched");
            }

            if (riEnabled && !_hotkeyRegistered && _hwnd != IntPtr.Zero)
                _hotkeyRegistered = RiShadeInterop.RegisterHotKey(_hwnd, 1, RiShadeInterop.MOD_NOREPEAT, RiShadeInterop.VK_F8);
            else if (!riEnabled && _hotkeyRegistered)
            {
                RiShadeInterop.UnregisterHotKey(_hwnd, 1);
                _hotkeyRegistered = false;
            }

            if (aaOn && !_aaAttached)
            {
                try { _aa.AttachExternal(_device!, _context!, _width, _height); _aaAttached = true; }
                catch (Exception ex) { App.Logger.WriteException("OverlayCompositor::AttachAA", ex); }
            }
            else if (!aaOn && _aaAttached)
            {
                _aa.DisposeExternal();
                _aaAttached = false;
            }

            if (fgOn && !_fgAttached && !_fgDisabledByError)
            {
                if (!FrameGenPipeline.IsPrepared && !FrameGenPipeline.PrepareFailed)
                    return;
                try
                {
                    _fgQuality = InitialFrameGenQuality(FrameGenSettings.ModeIndex);
                    _effectiveFgQuality = _fgQuality;
                    _autoGeneratedLimit = AutoGeneratedCeiling;
                    _sourceFpsPeak = 0;
                    _sourceRegressed = false;
                    _sourceRegressedSinceSec = 0;
                    _lastMissSec = 0;
                    _fgSixLoadLimit = InitialGeneratedLoadLimit(FrameGenSettings.ModeIndex);
                    _fg.Attach(_device!, _context!);
                    _fgAttached = true;
                    ResetFrameGenState();
                    App.Logger.WriteLine(LOG_IDENT, "Frame Generation stage attached, mode " + FrameGenSettings.ModeNames[FrameGenSettings.ModeIndex] + ", waiting for a steady capture rate to start generating");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("OverlayCompositor::AttachFrameGen", ex);
                    _fgFailures++;
                    if (_fgFailures >= 2)
                    {
                        _fgDisabledByError = true;
                        App.Logger.WriteLine(LOG_IDENT, "Frame Generation turned off for this session after repeated graphics errors, the overlay keeps running without it");
                    }
                }
            }
            else if (!fgOn && _fgAttached)
            {
                _fg.Dispose();
                _fruc?.Dispose();
                _fruc = null;
                _frucLive = false;
                _fgAttached = false;
                ResetFrameGenState();
                App.Logger.WriteLine(LOG_IDENT, "Frame Generation stage detached");
            }
        }

        private void RenderFrame(CancellationToken token)
        {
            bool riEnabled = App.Settings.Prop.RiShadeEnabled;
            bool riOn = riEnabled && RiShadeSettings.Current.HasVisibleEffects;
            int aaMethod = AntiAliasingSettings.MethodIndex;
            bool aaOn = aaMethod > 0;
            int fgMode = FrameGenSettings.ModeIndex;
            if (_activeFgMode != fgMode)
            {
                int previousMode = _activeFgMode;
                _activeFgMode = fgMode;
				ResetHudTelemetry();
                if (previousMode > 0 && fgMode > 0)
                {
                    _fgQuality = InitialFrameGenQuality(fgMode);
                    _qualitySlotWindow = 0;
                    _qualityMissWindow = 0;
                    _qualityStableSlots = 0;
                    _fgSixLoadLimit = InitialGeneratedLoadLimit(fgMode);
                    _multErr = 0;
                    _lastLoggedMult = -1;
                    App.Logger.WriteLine(LOG_IDENT, "Frame Generation multiplier changed live to " + FrameGenSettings.ModeNames[fgMode] + ", pacing limits reset");
                }
            }
            bool fgConfigured = fgMode > 0;
            bool fgOn = fgConfigured && Environment.TickCount64 >= Volatile.Read(ref _fgHoldUntilTick);
            SyncStages(riEnabled, riOn, aaOn, fgConfigured);
            bool joinHeld = fgConfigured && !fgOn;
            if (joinHeld != _fgJoinHeld)
            {
                _fgJoinHeld = joinHeld;
                ResetFrameGenState();
                _fruc?.Deprime();
                _fgSixLoadLimit = InitialGeneratedLoadLimit(fgMode);
                if (!joinHeld && fgConfigured)
                    App.Logger.WriteLine(LOG_IDENT, "Frame Generation restart complete, building fresh frame history");
            }

            if (FrameGenSettings.ModeIndex > 0)
                UpdateVBlankInfo();
            bool fresh = CaptureFrame();
            if (!fresh)
            {
                bool filled = TryStutterFill(token);
                if (!filled)
                {
                    if (_wgc != null)
                        _wgc.WaitForFrame(_fgGenerating ? 1 : 20);
                }
                return;
            }
            double priorFreshMs = _lastFreshPresentMs;
            _freshCaptureMs = _clock.Elapsed.TotalMilliseconds;
            _lastFreshPresentMs = _freshCaptureMs;
            if (priorFreshMs > 0)
            {
                double gap = _freshCaptureMs - priorFreshMs;
                if (gap > 0.05 && gap < 500.0)
                    _captureGapEma = _captureGapEma <= 0 ? gap : _captureGapEma * 0.8 + gap * 0.2;
            }
            if (_captureSrcMs > 0)
            {
                double drainQpcMs = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
                double poolWait = drainQpcMs - _captureSrcMs;
                _poolWaitMs = poolWait >= 0 && poolWait < 1000 ? poolWait : 0;
                if (poolWait >= 0 && poolWait < 100)
                {
                    _poolWaitEma = _poolWaitEma <= 0 ? poolWait : _poolWaitEma * 0.9 + poolWait * 0.1;
                    _captureAgeSamples[_captureAgeHead] = poolWait;
                    _captureAgeHead = (_captureAgeHead + 1) % _captureAgeSamples.Length;
                    if (_captureAgeCount < _captureAgeSamples.Length)
                        _captureAgeCount++;
                }
            }

            if (!_firstCaptureLogged)
            {
                _firstCaptureLogged = true;
                App.Logger.WriteLine(LOG_IDENT, $"First frame captured at {_width}x{_height} via {(_wgc != null ? "window capture" : "desktop duplication")}, compositor is live");
            }

            ID3D11ShaderResourceView composited = _rawSrv!;
            if (riOn && _riAttached)
            {
                try
                {
                    _rishade.RenderInto(_rawTex!, _stageRtv[0]!, _width, _height);
                    composited = _stageSrv[0]!;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("OverlayCompositor::RiShadeStage", ex);
                    _rishade.DisposeExternal();
                    _riAttached = false;
                }
            }
            if (aaOn && _aaAttached)
            {
                try
                {
                    _aa.RenderInto(composited, _stageRtv[1]!, _width, _height);
                    composited = _stageSrv[1]!;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("OverlayCompositor::AntiAliasingStage", ex);
                    _aa.DisposeExternal();
                    _aaAttached = false;
                }
            }

            LogChain(riOn && _riAttached, aaOn && _aaAttached, fgOn && _fgAttached);

            if (fgOn && _fgAttached && OverlayHub.HomepageBackgroundActive)
            {
                try
                {
                    int homeStage = ReferenceEquals(composited, _stageSrv[0]) ? 1 : 0;
                    if (_stageRtv[homeStage] != null && _stageSrv[homeStage] != null)
                    {
                        ComposeHomepage(_stageRtv[homeStage]!, composited);
                        composited = _stageSrv[homeStage]!;
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("OverlayCompositor::HomepageStage", ex);
                }
            }

            if (!(fgOn && _fgAttached))
            {
				if (OverlayHub.HomepageBackgroundActive)
				{
					ComposeHomepage(_backBufferRtv!, composited);
				}
				else
				{
					DrawBlit(_psPass!, _backBufferRtv!, composited);
				}
                if (!Present())
                    return;
                _realPresented++;
                _hasPrev = false;
                return;
            }

            try
            {
                PresentWithFrameGen(fgMode, composited, token);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::FrameGenStage", ex);
                _fgFailures++;
                if (_fgFailures >= 2)
                {
                    DisableFrameGenForSession("Frame Generation turned off for this session after repeated graphics errors, the overlay keeps running without it");
                }
                else
                {
                    try { _fg.Dispose(); } catch { }
                    try { _fruc?.Dispose(); } catch { }
                    _fruc = null;
                    _frucLive = false;
                    _fgAttached = false;
                    _fgGenerating = false;
                    _hasPrev = false;
                }
                DrawBlit(_psPass!, _backBufferRtv!, composited);
                if (Present())
                    _realPresented++;
            }
        }

        private bool TrackSourceHealth(double baseFps)
        {
            if (baseFps < 1.0)
                return false;
            if (baseFps > _sourceFpsPeak)
            {
                _sourceFpsPeak = baseFps;
                return false;
            }
            _sourceFpsPeak += (baseFps - _sourceFpsPeak) * SourcePeakDecay;
            if (_sourceFpsPeak < SourceRegressionFloor)
            {
                _sourceRegressedSinceSec = 0;
                _sourceRegressed = false;
                return false;
            }
            double now = _clock.Elapsed.TotalSeconds;
            if (baseFps >= _sourceFpsPeak * SourceRegressionRatio)
            {
                _sourceRegressedSinceSec = 0;
                _sourceRegressed = false;
                return false;
            }
            if (_sourceRegressedSinceSec <= 0)
            {
                _sourceRegressedSinceSec = now;
                return false;
            }
            if (now - _sourceRegressedSinceSec < SourceRegressionHoldSec)
                return false;
            if (!_sourceRegressed)
            {
                _sourceRegressed = true;
                App.Logger.WriteLine(LOG_IDENT, $"Frame Generation is backing off, Roblox held about {baseFps:0} frames per second against a usual {_sourceFpsPeak:0}");
            }
            return true;
        }

        private void ApplyGenerationHeadroomCap()
        {
            try
            {
                if (FrameGenSettings.ModeIndex != 1 || App.Settings.Prop.FrameGenTargetFps != 0)
                    return;
                if (_refreshHz < 90.0)
                    return;
                if (!_fgEverGenerated || _consumeRatioEma < SustainedConsumeRatio)
                    return;

                int cap = (int)Math.Round(_refreshHz / (AutoGeneratedCeiling + 1));
                cap = Math.Clamp(cap, MinimumHeadroomCap, 240);
                int ratio = (int)Math.Floor(_refreshHz / cap);
                if (ratio < 4)
                    return;

                App.Logger.WriteLine(LOG_IDENT, $"Frame Generation is capping Roblox at {cap} fps so it can present about {ratio}x on this {_refreshHz:0}Hz display, set a Frame Generation target fps to override this");
                _headroomCapApplied = true;
                Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetTargetCap(cap);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::ApplyGenerationHeadroomCap", ex);
            }
        }

        private void ReleaseGenerationHeadroomCap()
        {
            if (!_headroomCapApplied)
                return;
            _headroomCapApplied = false;
            try
            {
                App.Logger.WriteLine(LOG_IDENT, "Frame Generation released the Roblox frame rate cap it had set");
                Voidstrap.Integrations.FrameGeneration.FrameGenManager.SetTargetCap(0);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::ReleaseGenerationHeadroomCap", ex);
            }
        }

        private void DisableFrameGenForSession(string reason)
        {
            if (_fgDisabledByError)
                return;
            _fgDisabledByError = true;
            try { _fg.Dispose(); } catch { }
            try { _fruc?.Dispose(); } catch { }
            _fruc = null;
            _frucLive = false;
            _fgAttached = false;
            _fgGenerating = false;
            _hasPrev = false;
            ReleaseGenerationHeadroomCap();
            App.Logger.WriteLine(LOG_IDENT, reason);
        }

        private void ComposeHomepage(ID3D11RenderTargetView target, ID3D11ShaderResourceView source)
        {
            bool hasMedia = UpdateHomepageMedia();
            float sourceAspect = hasMedia ? (float)_homepageMediaWidth / _homepageMediaHeight : 1f;
            float targetAspect = (float)_width / _height;
            float scaleX = sourceAspect > targetAspect ? targetAspect / sourceAspect : 1f;
            float scaleY = sourceAspect > targetAspect ? 1f : sourceAspect / targetAspect;
            bool gradientEnabled = !hasMedia && OverlaySettings.HomepageBackgroundMode == "Gradient";
            float angle = (float)(App.Settings.Prop.HomepageBackgroundOverlayGradientAngle * Math.PI / 180.0);
            float directionX = MathF.Cos(angle);
            float directionY = MathF.Sin(angle);
            Vector4 color = ReadHomepageBackgroundColor();
            color.W = hasMedia ? 1f : 0f;
            Vector4 gradientColor = ReadHomepageGradientColor();
            gradientColor.W = gradientEnabled ? 1f : 0f;
            _context!.UpdateSubresource(new FrameGenPipelineParams
            {
                Dims = gradientColor,
                SrcRect = new Vector4(hasMedia ? scaleX : directionX, hasMedia ? scaleY : directionY, hasMedia ? 1f / _homepageMediaWidth : 1f, hasMedia ? 1f / _homepageMediaHeight : 1f),
                Interp = color,
            }, _cbuffer!);
            DrawHomepage(target, source, _homepageMediaSrv);
        }

        private void RecordPacingSlot(bool missed)
        {
            _qualitySlotWindow++;
            if (!missed)
                return;
            _lastMissSec = _clock.Elapsed.TotalSeconds;
            _qualityMissWindow++;
            _missedSlotsTotal++;
        }

        private void RecordPacingResult(double targetMs, bool missed)
        {
            double error = Math.Abs(_clock.Elapsed.TotalMilliseconds - targetMs);
            _paceErrorSamples[_paceErrorHead] = error;
            _paceErrorHead = (_paceErrorHead + 1) % _paceErrorSamples.Length;
            if (_paceErrorCount < _paceErrorSamples.Length)
                _paceErrorCount++;
            RecordPacingSlot(missed);
        }

        private void ConsiderFrameGenQuality(int minimumQuality, bool captureBackpressure)
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (captureBackpressure)
            {
                _captureBackpressureUntilSec = now + 5.0;
                _qualityStableSlots = 0;
                if (now - _fgQualityLastChangeSec >= 3.0)
                {
                    _fgQualityLastChangeSec = now;
                    if (_fgQuality > minimumQuality)
                    {
                        _fgQuality--;
                        App.Logger.WriteLine(LOG_IDENT, _fgQuality == 0
                            ? "Frame Generation switched to fast flow because capture fell behind Roblox"
                            : "Frame Generation reduced flow quality because capture fell behind Roblox");
                    }
                    else if (_activeFgMode == 5 && _fgSixLoadLimit > MinimumSixGeneratedLoad)
                    {
                        _fgSixLoadLimit--;
                        App.Logger.WriteLine(LOG_IDENT, $"Frame Generation reduced 6x to {_fgSixLoadLimit + 1}x because capture fell behind Roblox");
                    }
                    else if (_activeFgMode == 1 && _autoGeneratedLimit > 1)
                    {
                        _autoGeneratedLimit--;
                        App.Logger.WriteLine(LOG_IDENT, $"Frame Generation Auto reduced its stable ceiling to {_autoGeneratedLimit + 1}x because capture fell behind Roblox");
                    }
                }
                return;
            }
            if (_activeFgMode == 1
                && _autoGeneratedLimit < AutoGeneratedCeiling
                && !_sourceRegressed
                && now - _fgQualityLastChangeSec >= 12.0
                && now - _lastMissSec >= 8.0
                && now >= _captureBackpressureUntilSec)
            {
                _fgQualityLastChangeSec = now;
                _autoGeneratedLimit++;
                App.Logger.WriteLine(LOG_IDENT, $"Frame Generation Auto restored its ceiling to {_autoGeneratedLimit + 1}x after a quiet period");
            }
            if (_qualitySlotWindow < 120)
                return;

            int slots = _qualitySlotWindow;
            int misses = _qualityMissWindow;
            _qualitySlotWindow = 0;
            _qualityMissWindow = 0;
            if (misses * 4 > slots)
            {
                _qualityStableSlots = 0;
                _fgQualityLastChangeSec = now;
                if (_fgQuality > minimumQuality)
                {
                    _fgQuality--;
                    App.Logger.WriteLine(LOG_IDENT, _fgQuality == 0
                        ? "Frame Generation switched to fast flow to protect frame pacing"
                        : "Frame Generation reduced its flow quality to protect frame pacing");
                }
                else if (_activeFgMode == 5 && _fgSixLoadLimit > MinimumSixGeneratedLoad)
                {
                    _fgSixLoadLimit--;
                    App.Logger.WriteLine(LOG_IDENT, $"Frame Generation reduced 6x to {_fgSixLoadLimit + 1}x to protect Roblox performance");
                }
                else if (_activeFgMode == 1 && _autoGeneratedLimit > 1)
                {
                    _autoGeneratedLimit--;
                    App.Logger.WriteLine(LOG_IDENT, $"Frame Generation Auto reduced its stable ceiling to {_autoGeneratedLimit + 1}x to protect Roblox performance");
                }
                return;
            }

            if (misses == 0)
                _qualityStableSlots += slots;
            else
                _qualityStableSlots = 0;
            if (_qualityStableSlots < 240 || now - _fgQualityLastChangeSec < 6.0 || now < _captureBackpressureUntilSec)
                return;

            _qualityStableSlots = 0;
            _fgQualityLastChangeSec = now;
            if (_activeFgMode == 1)
            {
                if (_autoGeneratedLimit < AutoGeneratedCeiling && !_sourceRegressed)
                {
                    _autoGeneratedLimit++;
                    App.Logger.WriteLine(LOG_IDENT, $"Frame Generation Auto restored its ceiling to {_autoGeneratedLimit + 1}x after stable pacing");
                }
                return;
            }
            if (_activeFgMode == 5 && _fgSixLoadLimit < 5)
            {
                _fgSixLoadLimit++;
                App.Logger.WriteLine(LOG_IDENT, $"Frame Generation restored 6x to {_fgSixLoadLimit + 1}x after stable pacing");
            }
            else if (_fgQuality < 2)
            {
                _fgQuality++;
                App.Logger.WriteLine(LOG_IDENT, _fgQuality >= 2
                    ? "Frame Generation raised its flow quality after stable pacing"
                    : "Frame Generation raised its flow quality to improve motion detail");
            }
        }

        private void PresentWithFrameGen(int fgMode, ID3D11ShaderResourceView composited, CancellationToken token)
        {
            bool srcIsCapture = _captureSrcMs > 0;
            double srcMs = srcIsCapture ? _captureSrcMs : _freshCaptureMs;
            double dt = srcMs - _lastSrcMs;
            bool hadSrc = _lastSrcMs > 0 && srcIsCapture == _srcWasCapture;
            int fpsCap = RobloxFpsCap.Cap;
            double capMs = _wgc != null && fpsCap > 0 && fpsCap < 1000 ? 1000.0 / fpsCap : 0;
            double expectedMs = _emaIntervalMs > 4.0 ? _emaIntervalMs : capMs;
            _effectiveFgQuality = _fgQuality;
            _fg.SetQuality(_effectiveFgQuality);
            bool flowResourcesRebuilt = _fg.EnsureSize(_width, _height);
            if (flowResourcesRebuilt)
                ResetFrameGenState();
            if (!_fg.Ready)
            {
                _fgGenerating = false;
                DrawBlit(_psPass!, _backBufferRtv!, composited);
                if (Present())
                {
                    _realPresented++;
                    _hasPrev = false;
                }
                return;
            }
            long captureDropCount = _wgc?.DroppedCount ?? 0;
            long skippedSinceLastUse = Math.Max(0, captureDropCount - _lastCaptureDropCount);
            bool drainedQueuedCapture = skippedSinceLastUse > 0;
            _lastCaptureDropCount = captureDropCount;
            bool sourceStutter = hadSrc && FrameGenSettings.IsSourceStutter(dt, expectedMs);
            _lastSrcMs = srcMs;
            _srcWasCapture = srcIsCapture;
            int prev = 1 - _cur;

            if (!_hasPrev || !hadSrc || dt > 450.0 || dt < 0.5 || sourceStutter)
            {
                bool fullStop = !_hasPrev || !hadSrc || dt > 450.0 || dt < 0.5;
                if (_fgGenerating && fullStop)
                {
                    _fgGenerating = false;
                    App.Logger.WriteLine(LOG_IDENT, dt > 450.0
                        ? $"Frame Generation resync after a {dt:0}ms gap (stutter or load), showing real frames until the rate steadies"
                        : "Frame Generation resync after an irregular capture");
                }
                _fgResetCount++;
                _fruc?.Deprime();
                _fg.ResetHistory();
                DrawBlit(_psPass!, _compRtv[_cur]!, composited);
                _fg.BuildPyramid(_cur, _compSrv[_cur]!);
                DrawBlit(_psPass!, _backBufferRtv!, _compSrv[_cur]!);
                if (!Present())
                    return;
                _realPresented++;
                _hasPrev = true;
                _pairStale = false;
                _emaIntervalMs = 0;
                _paceAnchorMs = 0;
                _multErr = 0;
                _dtCount = 0;
                _dtHead = 0;
                _dtOutlierRun = 0;
                _cur = prev;
                return;
            }

            double measuredDt = Math.Clamp(dt, 2.0, 400.0);
            double normalizationMs = drainedQueuedCapture && capMs > 4.0
                ? capMs
                : _dtCount >= 3 ? _emaIntervalMs : 0;
            double clampedDt = normalizationMs > 4.0 ? FrameGenSettings.NormalizeInterval(measuredDt, normalizationMs) : measuredDt;
            bool minimumCadenceLocked = fpsCap >= 5 && fpsCap <= 15;
            if (minimumCadenceLocked)
                clampedDt = capMs;
            bool normalizedDroppedGap = drainedQueuedCapture && Math.Abs(clampedDt - measuredDt) > 0.01;
            if (normalizedDroppedGap && _emaIntervalMs > 0 && Math.Abs(_emaIntervalMs - clampedDt) > clampedDt * 0.25)
            {
                _dtCount = 0;
                _dtHead = 0;
                _dtOutlierRun = 0;
            }
            if (!minimumCadenceLocked && Math.Abs(clampedDt - measuredDt) > 0.01)
            {
                _fruc?.Deprime();
                _fg.ResetHistory();
                _frucLastFedMs = 0;
            }
            if (_dtCount >= 3 && _emaIntervalMs > 4.0)
            {
                bool outlier = clampedDt > _emaIntervalMs * 1.6 || clampedDt < _emaIntervalMs * 0.55;
                if (outlier && _dtOutlierRun < 5)
                {
                    _dtOutlierRun++;
                    clampedDt = _emaIntervalMs;
                }
                else
                    _dtOutlierRun = 0;
            }
            _dtRing[_dtHead] = clampedDt;
            _dtHead = (_dtHead + 1) % _dtRing.Length;
            if (_dtCount < _dtRing.Length)
                _dtCount++;
            double stable = StableIntervalMs();
            if (_dtCount < 2)
                _emaIntervalMs = capMs > 4.0 ? capMs : stable;
            else if (_dtCount == 2 || _emaIntervalMs <= 0)
                _emaIntervalMs = stable;
            else
                _emaIntervalMs = _emaIntervalMs * 0.85 + stable * 0.15;
            if (_emaIntervalMs <= 0.5 && _captureGapEma > 0.5)
                _emaIntervalMs = _captureGapEma;
            double baseFps = _emaIntervalMs > 0.5 ? 1000.0 / _emaIntervalMs : 0;

            double trueIntervalMs = srcIsCapture && skippedSinceLastUse > 0
                ? Math.Clamp(measuredDt / (skippedSinceLastUse + 1), 2.0, 400.0)
                : _emaIntervalMs;
            _sourceIntervalMs = _sourceIntervalMs <= 0.5 ? trueIntervalMs : _sourceIntervalMs * 0.85 + trueIntervalMs * 0.15;
            double sourceFps = _sourceIntervalMs > 0.5 ? 1000.0 / _sourceIntervalMs : baseFps;
            double consumeRatio = sourceFps > 0.5 && baseFps > 0.5 ? Math.Clamp(baseFps / sourceFps, 0.05, 1.0) : 1.0;
            _consumeRatioEma = _consumeRatioEma <= 0.0 ? consumeRatio : _consumeRatioEma * 0.8 + consumeRatio * 0.2;
            RobloxPresentTracer.ReportFrameGenerationCadence(sourceFps);

            double pacingIntervalMs = _emaIntervalMs;

            bool sourceRegressed = TrackSourceHealth(baseFps);
            int userMax = FrameGenSettings.MaxGeneratedForCadence(fgMode, pacingIntervalMs, _refreshHz, _consumeRatioEma);
            ConsiderFrameGenQuality(_lastGenCount >= 3 ? 2 : _lastGenCount >= 2 ? 1 : 0, drainedQueuedCapture || sourceRegressed);
            bool uncap = App.Settings.Prop.FrameGenUncap && _tearingActive;
            int displayBudget = pacingIntervalMs > 0.5 && _refreshHz > 1.0
                ? FrameGenSettings.TargetTotal(pacingIntervalMs, _refreshHz, fgMode, uncap, ref _multErr)
                : 1;
            int genCount = Math.Clamp(displayBudget - 1, 0, userMax);
            if (fgMode == 1)
                genCount = Math.Min(genCount, _autoGeneratedLimit);
            if (fgMode == 5)
                genCount = Math.Min(genCount, _fgSixLoadLimit);
            if (baseFps < 3.0)
                genCount = 0;
            if (_dtCount < FrameGenSettings.RequiredCadenceSamples(pacingIntervalMs))
                genCount = 0;

            if (!uncap && genCount > 0 && baseFps > 0.5)
            {
                int maxTotal = Math.Max(1, (int)Math.Ceiling(Math.Max(_refreshHz, 60.0) / baseFps - 0.001));
                if (genCount + 1 > maxTotal)
                {
                    genCount = maxTotal - 1;
                }
            }

            double nowSec = _clock.Elapsed.TotalSeconds;
            _lastDisplayBudget = displayBudget;
            _lastGenCount = genCount;
            if (Environment.TickCount64 < Volatile.Read(ref _fgHoldUntilTick))
                genCount = 0;
            int targetMult = genCount + 1;

            if (targetMult != _lastLoggedMult)
            {
                _lastLoggedMult = targetMult;
                if (nowSec - _lastMultLogSec > 10.0)
                {
                    _lastMultLogSec = nowSec;
                    if (genCount == 0)
                        App.Logger.WriteLine(LOG_IDENT, Environment.TickCount64 < Volatile.Read(ref _fgHoldUntilTick)
                            ? "Frame Generation is resting while the game loads, real frames pass through until it ends"
                            : $"Frame Generation paused, base {baseFps:0} fps against a {_refreshHz:0}Hz display leaves no room to insert frames");
                    else
                    {
                        double outFps = baseFps * (genCount + 1);
                        string modeWord = FrameGenSettings.IsAuto(fgMode) ? "Auto" : FrameGenSettings.ModeNames[fgMode] + " selected";
                        string note = outFps > _refreshHz + 1 ? $", your {_refreshHz:0}Hz display shows {_refreshHz:0} of them" : "";
                        App.Logger.WriteLine(LOG_IDENT, $"Frame Generation {modeWord}: base {baseFps:0} fps to about {outFps:0} fps presented, {genCount} generated per real frame{note} (Roblox cap {RobloxFpsCap.Describe()})");
                    }
                }
            }

            if (genCount == 0)
            {
                bool keepWarm = _fgGenerating && baseFps >= 3.0 && _compRtv[_cur] != null;
                if (!keepWarm)
                {
                    _fgGenerating = false;
                    _pairStale = true;
                    _frucLastFedMs = 0;
                    _fruc?.Deprime();
                }
                _paceAnchorMs = 0;
                _fillCount = 0;
                if (keepWarm)
                {
                    DrawBlit(_psPass!, _compRtv[_cur]!, composited);
                    _fg.BuildPyramid(_cur, _compSrv[_cur]!);
                }
                double holdNowMs = _clock.Elapsed.TotalMilliseconds;
                if (Environment.TickCount64 < Volatile.Read(ref _fgHoldUntilTick))
                {
                    if (holdNowMs < _holdNextPresentMs)
                    {
                        if (keepWarm)
                            _cur = prev;
                        token.WaitHandle.WaitOne(10);
                        return;
                    }
                    _holdNextPresentMs = holdNowMs + HoldPresentIntervalMs;
                }
                else
                    _holdNextPresentMs = 0;
                if (keepWarm)
                    DrawBlit(_psPass!, _backBufferRtv!, _compSrv[_cur]!);
                else
                    DrawBlit(_psPass!, _backBufferRtv!, composited);
                if (!Present())
                    return;
                _realPresented++;
                if (keepWarm)
                {
                    _lastPairEndMs = _clock.Elapsed.TotalMilliseconds;
                    _cur = prev;
                }
                return;
            }

            if (_pairStale)
            {
                _pairStale = false;
                _fillCount = 0;
                DrawBlit(_psPass!, _compRtv[_cur]!, composited);
                _fg.BuildPyramid(_cur, _compSrv[_cur]!);
                DrawBlit(_psPass!, _backBufferRtv!, _compSrv[_cur]!);
                if (!Present())
                    return;
                _realPresented++;
                _paceAnchorMs = 0;
                _cur = prev;
                return;
            }

            DrawBlit(_psPass!, _compRtv[_cur]!, composited);
            _fg.BuildPyramid(_cur, _compSrv[_cur]!);

            if (!_fgGenerating)
            {
                _fgGenerating = true;
                if (!_fgEverGenerated)
                {
                    _fgEverGenerated = true;
                    App.Logger.WriteLine(LOG_IDENT, "Frame Generation is live: generated frames are evenly paced between each pair of real frames");
                }
            }

            float searchRange = 12f;
            if (baseFps > 0.5)
            {
                double maxSearch = _effectiveFgQuality == 0 ? 28.0 : (_effectiveFgQuality == 1 ? 32.0 : 40.0);
                searchRange = (float)Math.Clamp(12.0 * (55.0 / baseFps), 6.0, maxSearch);
            }
            _fg.ComputeFlow(prev, _cur, searchRange);
            int total = genCount + 1;
            double slotMs = FrameGenSettings.OutputSlotMs(pacingIntervalMs, _refreshHz, total, uncap);

            EnsureFruc();
            var fruc = _fruc;
            if (fruc != null && fruc.Broken)
            {
                string why = fruc.FailReason;
                fruc.Dispose();
                _fruc = null;
                fruc = null;
                _frucLive = false;
                if (why.Contains("status") && _frucRetries < 2)
                {
                    _frucRetries++;
                    App.Logger.WriteLine(LOG_IDENT, "NVIDIA interpolation restarting after: " + why);
                }
                else
                {
                    _frucFailed = true;
                    App.Logger.WriteLine(LOG_IDENT, "NVIDIA interpolation gave up: " + why + ", staying on the built in generator");
                }
            }
            int realSlot = total;
            int firstSlot = FrameGenSettings.FirstUnshownSlot(_fillCount, realSlot);

            long frucJob = -1;
            int frucSlot = 0;
            bool frucAttemptMissed = false;
            if (fruc != null && fruc.Ready)
            {
                if (firstSlot < realSlot)
                {
                    double nowFedMs = _clock.Elapsed.TotalMilliseconds;
                    if (_frucLastFedMs > 0 && nowFedMs - _frucLastFedMs > Math.Max(_emaIntervalMs * 2.5, 60.0))
                    {
                        fruc.Deprime();
                        _frucLastFedMs = 0;
                    }
                    frucSlot = Math.Clamp(Math.Max(firstSlot, (realSlot + 1) / 2), firstSlot, realSlot - 1);
                    if (!fruc.CanAcceptSource)
                    {
                        if (fruc.Primed)
                            _frucRequestedTotal++;
                        frucAttemptMissed = true;
                    }
                    else
                    {
                        DrawBlitSized(_psPass!, fruc.InputRtv(_cur), _compSrv[_cur]!, fruc.Width, fruc.Height);
                        double previousTs = fruc.PreviousTimestamp;
                        double outputTs = previousTs > 0 ? previousTs + (srcMs - previousTs) * frucSlot / total : srcMs;
                        if (fruc.Primed)
                            _frucRequestedTotal++;
                        long submittedJob = fruc.SubmitPair(_cur, srcMs, outputTs, frucSlot, out bool producesOutput);
                        if (submittedJob >= 0)
                            _frucLastFedMs = nowFedMs;
                        else
                            frucAttemptMissed = true;
                        frucJob = producesOutput ? submittedJob : -1;
                    }
                }
            }

            double now = _clock.Elapsed.TotalMilliseconds;
            double target = _paceAnchorMs > 0 ? _paceAnchorMs + slotMs : now + slotMs;

            bool frucReady = false;
            bool frucUsed = false;
            for (int k = firstSlot; k <= total; k++)
            {
                bool missed = FrameGenSettings.IsSlotOverdue(target, _clock.Elapsed.TotalMilliseconds, slotMs);
                if (missed && k < realSlot)
                {
                    RecordPacingSlot(true);
                    if (_clock.Elapsed.TotalMilliseconds - target > slotMs * DropAfterSlotsBehind)
                    {
                        target += slotMs;
                        continue;
                    }
                    target = _clock.Elapsed.TotalMilliseconds;
                    missed = false;
                }
                if (missed)
                {
                    _lastAlignedMs = 0;
                    target = _clock.Elapsed.TotalMilliseconds + Math.Min(slotMs, 0.5);
                }
                double alignedTarget = AlignTarget(target, slotMs);
                if (!WaitForPresentReady(alignedTarget, token))
                {
                    if (k < realSlot)
                    {
                        RecordPacingSlot(true);
                        if (!WaitForPresentReady(_clock.Elapsed.TotalMilliseconds + slotMs * DropAfterSlotsBehind, token))
                        {
                            target += slotMs;
                            continue;
                        }
                        _lastAlignedMs = 0;
                        alignedTarget = _clock.Elapsed.TotalMilliseconds;
                    }
                    else
                    {
                        missed = true;
                        WaitForPresentReady(_clock.Elapsed.TotalMilliseconds + slotMs, token);
                        _lastAlignedMs = 0;
                        alignedTarget = _clock.Elapsed.TotalMilliseconds;
                    }
                }
                bool ai = false;
                var genTarget = _backBufferRtv!;
                if (k < realSlot)
                {
                    if (k == frucSlot && frucJob >= 0)
                    {
                        double readyBudgetMs = Math.Clamp(alignedTarget - _clock.Elapsed.TotalMilliseconds - 0.15, 0.0, Math.Min(slotMs, 12.0));
                        frucReady = WaitForFrucReady(fruc!, frucJob, readyBudgetMs);
                    }
                    if (frucReady && fruc!.TryGetReady(frucJob, k, out var aiSrv, out ulong aiSignal))
                    {
                        fruc.WaitOutput(aiSignal);
                        DrawBlit(_psPass!, genTarget, aiSrv!);
                        ai = true;
                        frucUsed = true;
                    }
                    else
                    {
                        float t = 1f - (float)(realSlot - k) / total;
                        _fg.Warp(_compSrv[prev]!, _compSrv[_cur]!, t, genTarget);
                    }
                }
                else
                    DrawBlit(_psPass!, genTarget, _compSrv[_cur]!);
                PaceWait(alignedTarget, token);
                if (token.IsCancellationRequested)
                    return;
                if (!Present())
                    return;
                RecordPacingResult(alignedTarget, missed);
                _paceAnchorMs = alignedTarget;
                if (k == realSlot)
                    _realPresented++;
                else
                {
                    _genPresented++;
                    if (ai)
                    {
                        if (!_frucLive)
                        {
                            _frucLive = true;
                            App.Logger.WriteLine(LOG_IDENT, "NVIDIA hardware AI interpolation is live: the optical flow engine analyzes each frame pair and predicts the frames in between");
                        }
                        _frucUsedTotal++;
                    }
                }
                target = alignedTarget + slotMs;
            }
            if ((frucJob >= 0 && !frucUsed) || frucAttemptMissed)
            {
                fruc?.Deprime();
                _frucLastFedMs = 0;
            }
            _lastSlotMs = slotMs;
            _lastTotal = total;
            _fillCount = 0;
            _lastPairEndMs = _clock.Elapsed.TotalMilliseconds;
            _cur = prev;
        }

        private bool TryStutterFill(CancellationToken token)
        {
            if (!_fgGenerating || !_hasPrev || _lastSlotMs <= 0.5 || _lastTotal <= 0)
                return false;
            int newest = 1 - _cur;
            if (_backBufferRtv == null || _compSrv[newest] == null)
                return false;
            double now = _clock.Elapsed.TotalMilliseconds;
            double slotMs = _lastSlotMs;
            double expectedMs = Math.Max(_emaIntervalMs, _captureGapEma);
            if (expectedMs > 0.5 && now - _lastFreshPresentMs < expectedMs * 1.25)
                return false;
            double target = _paceAnchorMs > 0 ? _paceAnchorMs + slotMs : now;
            if (now < target - 0.15)
                return false;
            if (now - _lastPairEndMs > 250.0)
                return false;
            bool lowRate = expectedMs >= 1000.0 / 30.0;
            double reach = (_fillCount + 1.0) / _lastTotal;
            if (lowRate || reach > 1.0)
            {
                if (_fillCount > _lastTotal)
                    return false;
                if (FrameGenSettings.IsSlotOverdue(target, now, slotMs))
                {
                    _lastAlignedMs = 0;
                    target = now + Math.Min(slotMs, 0.5);
                }
                double holdTarget = AlignTarget(target, slotMs);
                if (!WaitForPresentReady(holdTarget, token))
                {
                    _lastAlignedMs = 0;
                    holdTarget = _clock.Elapsed.TotalMilliseconds;
                }
                DrawBlit(_psPass!, _backBufferRtv!, _compSrv[newest]!);
                PaceWait(holdTarget, token);
                if (token.IsCancellationRequested || !Present())
                    return false;
                RecordPacingResult(holdTarget, false);
                _paceAnchorMs = holdTarget;
                _fillCount++;
                double holdNowSec = _clock.Elapsed.TotalSeconds;
                if (lowRate && _fillCount == 1 && holdNowSec - _lastFillLogSec > 5.0)
                {
                    _lastFillLogSec = holdNowSec;
                    App.Logger.WriteLine(LOG_IDENT, "Roblox stalled a frame, holding the newest real frame to keep low rate motion stable");
                }
                return true;
            }
            if (target > now + slotMs * 1.5)
                target = now + slotMs;

            float decayedReach = (float)(reach * Math.Exp(-reach * 0.35));
            double alignedTarget = AlignTarget(target, slotMs);
            _fillCount++;
            if (FrameGenSettings.IsSlotOverdue(alignedTarget, now, slotMs) || !WaitForPresentReady(alignedTarget, token))
            {
                RecordPacingSlot(true);
                _paceAnchorMs = alignedTarget;
                return false;
            }
            try
            {
                _fg.WarpForward(_compSrv[newest]!, decayedReach, _backBufferRtv!);
            }
            catch
            {
                return false;
            }
            PaceWait(alignedTarget, token);
            if (token.IsCancellationRequested)
                return false;
            if (!Present())
                return false;
            RecordPacingResult(alignedTarget, false);
            _genPresented++;
            _paceAnchorMs = alignedTarget;

            double nowSec = _clock.Elapsed.TotalSeconds;
            if (_fillCount == 1 && nowSec - _lastFillLogSec > 5.0)
            {
                _lastFillLogSec = nowSec;
                App.Logger.WriteLine(LOG_IDENT, "Roblox stalled a frame, predicting motion forward with decay to ride it out smoothly");
            }
            return true;
        }

        private bool WaitForFrucReady(NvFrucEngine fruc, long jobId, double budgetMs)
        {
            if (fruc.AllReady(jobId))
                return true;
            double until = _clock.Elapsed.TotalMilliseconds + Math.Max(0.0, budgetMs);
            while (_clock.Elapsed.TotalMilliseconds < until)
            {
                if (fruc.AllReady(jobId))
                    return true;

                double remaining = until - _clock.Elapsed.TotalMilliseconds;
                if (remaining > 1.0)
                    Thread.Sleep(1);
                else if (remaining > 0.2)
                    Thread.Yield();
                else
                    Thread.SpinWait(200);
            }
            return fruc.AllReady(jobId);
        }

        private void EnsureFruc()
        {
            if (_fruc != null)
            {
                _fruc.Dispose();
                _fruc = null;
            }
            _frucFailed = true;
            _frucLive = false;
        }

        private void LogChain(bool ri, bool aa, bool fg)
        {
            string chain = (ri ? "RiShade" : "") + (aa ? (ri ? "+AA" : "AA") : "") + (fg ? ((ri || aa) ? "+FrameGen" : "FrameGen") : "");
            if (chain == _lastChainLog)
                return;
            _lastChainLog = chain;
            App.Logger.WriteLine(LOG_IDENT, "Active chain: " + (chain.Length == 0 ? "passthrough" : chain));
        }

        private void UpdateHudIfDue()
        {
            bool enabled = App.Settings.Prop.FrameGenOverlayShow && FrameGenSettings.ModeIndex > 0;
            if (!enabled)
            {
                if (_hudEnabled)
                {
                    _hudEnabled = false;
                    ResetHudTelemetry();
                }
                return;
            }
            if (!_hudEnabled)
            {
                _hudEnabled = true;
                ResetHudTelemetry();
            }
            double now = _clock.Elapsed.TotalMilliseconds;
            if (_hudLastMs == 0)
            {
                _hudLastMs = now;
                _hudRealBase = _realPresented;
                _hudGenBase = _genPresented;
                return;
            }
			if (now - _hudLastMs < (_hudPainted ? 1000.0 : 500.0))
                return;
            double window = (now - _hudLastMs) / 1000.0;
            long real = _realPresented - _hudRealBase;
            long gen = _genPresented - _hudGenBase;
            _hudLastMs = now;
            _hudRealBase = _realPresented;
            _hudGenBase = _genPresented;
            if (window <= 0.0)
                return;
            double outSample = (real + gen) / window;
            double genSample = gen / window;
			double capturePresentedFps = real / window;
			double captureCadenceFps = capturePresentedFps;
            if (_dtCount >= 2 && _emaIntervalMs > 0.5)
				captureCadenceFps = 1000.0 / _emaIntervalMs;
			double actualFps = ReliableActualFps();
			bool hasActualFps = actualFps > 0.5;
			double sourceDisplayFps = hasActualFps ? actualFps : captureCadenceFps;
			double mult = captureCadenceFps > 0.5 ? outSample / captureCadenceFps : 1.0;
            string engineVal = Environment.TickCount64 < Volatile.Read(ref _fgHoldUntilTick)
                ? "Restarting"
                : _fruc != null && !_fruc.Broken
                    ? $"NVIDIA Hybrid {mult:0.0}x"
                    : !_frucFailed ? "NVIDIA preparing" : $"Shader {mult:0.0}x";
			_hudLabelsFour[1] = hasActualFps ? "Real" : "Capture";
			_hudLabelsThree[1] = hasActualFps ? "Real" : "Capture";
			string[] labels;
			string[] values;
            if (gen > 0)
            {
				labels = _hudLabelsFour;
				values = _hudValuesFour;
				values[0] = $"{outSample:0}/s";
				values[1] = $"{sourceDisplayFps:0}/s";
				values[2] = $"{genSample:0}/s";
				values[3] = engineVal;
            }
            else
            {
				labels = _hudLabelsThree;
				values = _hudValuesThree;
				values[0] = $"{outSample:0}/s";
				values[1] = $"{sourceDisplayFps:0}/s";
				values[2] = "0/s";
            }
            try
            {
                _hud.Update(_context!, labels, values);
                _hudPainted = true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::UpdateHud", ex);
            }
        }

        private void DrawHud()
        {
            if (!_hudPainted || _hud.Srv == null || _backBufferRtv == null)
                return;
            _context!.OMSetBlendState(_hudBlend);
            _context.OMSetRenderTargets(_backBufferRtv);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_psOverlay);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.RSSetViewport(new Viewport(HudX, HudY, OverlayHud.TexWidth, OverlayHud.TexHeight, 0, 1));
            _context.PSSetShaderResources(0, _nullSrvs);
            _context.PSSetShaderResource(0, _hud.Srv);
            _context.Draw(3, 0);
            _context.OMSetBlendState(null);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        private void DrawCrosshair()
        {
            if (_backBufferRtv == null || !OverlayCrosshair.IsEnabled())
                return;
            try
            {
                double nowMs = _clock.Elapsed.TotalMilliseconds;
                if (_crosshair.Srv == null || nowMs - _crosshairRefreshMs >= OverlayRefreshIntervalMs)
                {
                    _crosshairRefreshMs = nowMs;
                    _crosshair.Update(_context!);
                }
                if (_crosshair.Srv == null)
                    return;
                float x = (_width - OverlayCrosshair.TexWidth) * 0.5f;
                float y = (_height - OverlayCrosshair.TexHeight) * 0.5f;
                _context!.OMSetBlendState(_hudBlend);
                _context.OMSetRenderTargets(_backBufferRtv);
                _context.VSSetShader(_vs);
                _context.PSSetShader(_psOverlay);
                _context.PSSetSampler(0, _sampler);
                _context.IASetInputLayout(null);
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.RSSetViewport(new Viewport(x, y, OverlayCrosshair.TexWidth, OverlayCrosshair.TexHeight, 0, 1));
                _context.PSSetShaderResources(0, _nullSrvs);
                _context.PSSetShaderResource(0, _crosshair.Srv);
                _context.Draw(3, 0);
                _context.OMSetBlendState(null);
                _context.PSSetShaderResources(0, _nullSrvs);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::DrawCrosshair", ex);
            }
        }

        private string FrameGenStatusText()
        {
            if (FrameGenSettings.ModeIndex <= 0 || _fgDisabledByError)
                return "";
            if (Environment.TickCount64 < Volatile.Read(ref _fgHoldUntilTick))
                return "Frame Generation: Waiting for the game, under 1 second";
            if (!FrameGenPipeline.IsPrepared)
                return FrameGenPipeline.PrepareFailed ? "Frame Generation: Shader preparation failed" : "Frame Generation: Preparing shaders, about 2 seconds";
            if (!_fgAttached)
                return "Frame Generation: Attaching";
            if (!_hasPrev)
                return "Frame Generation: Capturing the first frame";
            if (!_fgGenerating)
                return "";
            if (_fruc == null && !_frucFailed)
                return "Frame Generation: Starting NVIDIA interpolation";
            return "";
        }

        private void DrawFrameGenStatus()
        {
            double statusNowMs = _clock.Elapsed.TotalMilliseconds;
            if (statusNowMs - _statusRefreshMs >= OverlayRefreshIntervalMs)
            {
                _statusRefreshMs = statusNowMs;
                _statusCachedText = FrameGenStatusText();
            }
            string text = _statusCachedText;
            if (text.Length == 0)
            {
                if (_fgStatusSrv != null)
                {
                    _fgStatusSrv.Dispose();
                    _fgStatusTex?.Dispose();
                    _fgStatusSrv = null;
                    _fgStatusTex = null;
                    _fgStatusText = "";
                }
                return;
            }
            if (_fgStatusSrv == null || text != _fgStatusText || _fgStatusWindowWidth != _width)
            {
                _fgStatusSrv?.Dispose();
                _fgStatusTex?.Dispose();
                (_fgStatusTex, _fgStatusSrv, _fgStatusW, _fgStatusH) = MakeStatusTexture(text);
                _fgStatusText = text;
                _fgStatusWindowWidth = _width;
            }
            if (_fgStatusSrv == null)
                return;
            int x = Math.Max(8, (_width - _fgStatusW) / 2);
            int bottom = Math.Clamp(_height / 24, 14, 42);
            int y = Math.Max(8, _height - _fgStatusH - bottom);
            _context!.OMSetBlendState(_hudBlend);
            _context.OMSetRenderTargets(_backBufferRtv);
            _context.VSSetShader(_vs);
            _context.PSSetShader(_psOverlay);
            _context.PSSetSampler(0, _sampler);
            _context.IASetInputLayout(null);
            _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _context.RSSetViewport(new Viewport(x, y, _fgStatusW, _fgStatusH, 0, 1));
            _context.PSSetShaderResources(0, _nullSrvs);
            _context.PSSetShaderResource(0, _fgStatusSrv);
            _context.Draw(3, 0);
            _context.OMSetBlendState(null);
            _context.PSSetShaderResources(0, _nullSrvs);
        }

        private (ID3D11Texture2D, ID3D11ShaderResourceView, int, int) MakeStatusTexture(string text)
        {
            float fontSize = Math.Clamp(_width / 96f, 11f, 20f);
            int maxWidth = Math.Max(80, _width - 24);
            using var probe = new System.Drawing.Bitmap(1, 1);
            using var pg = System.Drawing.Graphics.FromImage(probe);
            using var probeFont = new System.Drawing.Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            var measured = pg.MeasureString(text, probeFont);
            if (measured.Width + 28 > maxWidth)
                fontSize = Math.Max(9f, fontSize * (maxWidth - 28) / measured.Width);
            using var font = new System.Drawing.Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            var size = pg.MeasureString(text, font);
            int w = Math.Min(maxWidth, (int)Math.Ceiling(size.Width) + 28);
            int h = (int)Math.Ceiling(size.Height) + 14;
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bmp))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                graphics.Clear(System.Drawing.Color.Transparent);
                using var background = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(220, 18, 20, 25));
                using var foreground = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(250, 255, 255, 255));
                graphics.FillRectangle(background, 0, 0, w, h);
                graphics.DrawString(text, font, foreground, 14, 7);
            }
            var texture = BitmapToTexture(bmp);
            return (texture, _device!.CreateShaderResourceView(texture), w, h);
        }

        private void CreateSplitAssets()
        {
            try
            {
                (_splitLeftTex, _splitLeftSrv, _splitLeftW, _splitLeftH) = MakeLabelTexture("No Frame Gen");
                (_splitRightTex, _splitRightSrv, _splitRightW, _splitRightH) = MakeLabelTexture("Frame Gen");
                (_splitLineTex, _splitLineSrv, _, _) = MakeSolidTexture(2, 2, System.Drawing.Color.FromArgb(210, 255, 255, 255));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::CreateSplitAssets", ex);
            }
        }

        private (ID3D11Texture2D, ID3D11ShaderResourceView, int, int) MakeLabelTexture(string text)
        {
            using var probe = new System.Drawing.Bitmap(1, 1);
            using var pg = System.Drawing.Graphics.FromImage(probe);
            using var font = new System.Drawing.Font("Segoe UI", 22f, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            var sz = pg.MeasureString(text, font);
            int w = (int)sz.Width + 28;
            int h = (int)sz.Height + 14;
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                using (var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(170, 12, 13, 16)))
                    g.FillRectangle(bg, 0, 0, w, h);
                using (var wb = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(245, 255, 255, 255)))
                    g.DrawString(text, font, wb, 14, 6);
            }
            var tex = BitmapToTexture(bmp);
            return (tex, _device!.CreateShaderResourceView(tex), w, h);
        }

        private (ID3D11Texture2D, ID3D11ShaderResourceView, int, int) MakeSolidTexture(int w, int h, System.Drawing.Color color)
        {
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var b = new System.Drawing.SolidBrush(color))
                g.FillRectangle(b, 0, 0, w, h);
            var tex = BitmapToTexture(bmp);
            return (tex, _device!.CreateShaderResourceView(tex), w, h);
        }

        private ID3D11Texture2D BitmapToTexture(System.Drawing.Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var locked = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var data = new SubresourceData(locked.Scan0, (uint)locked.Stride);
                var tex = _device!.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)w,
                    Height = (uint)h,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Immutable,
                    BindFlags = BindFlags.ShaderResource,
                }, new[] { data });
                return tex;
            }
            finally
            {
                bmp.UnlockBits(locked);
            }
        }

        private void ApplySplitCompare()
        {
            if (_backBufferTex == null || _compTex[_cur] == null)
                return;
            int half = _width / 2;
            if (half < 2)
                return;
            try
            {
                var box = new Box(0, 0, 0, half, _height, 1);
                _context!.CopySubresourceRegion(_backBufferTex, 0, 0, 0, 0, _compTex[_cur], 0, box);

                _context.OMSetBlendState(_hudBlend);
                _context.OMSetRenderTargets(_backBufferRtv);
                _context.VSSetShader(_vs);
                _context.PSSetShader(_psOverlay);
                _context.PSSetSampler(0, _sampler);
                _context.IASetInputLayout(null);
                _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
                _context.PSSetShaderResources(0, _nullSrvs);

                if (_splitLineSrv != null)
                {
                    _context.RSSetViewport(new Viewport(half - 1, 0, 2, _height, 0, 1));
                    _context.PSSetShaderResource(0, _splitLineSrv);
                    _context.Draw(3, 0);
                }
                if (_splitLeftSrv != null)
                {
                    _context.RSSetViewport(new Viewport(24, _height - _splitLeftH - 24, _splitLeftW, _splitLeftH, 0, 1));
                    _context.PSSetShaderResource(0, _splitLeftSrv);
                    _context.Draw(3, 0);
                }
                if (_splitRightSrv != null)
                {
                    _context.RSSetViewport(new Viewport(_width - _splitRightW - 24, _height - _splitRightH - 24, _splitRightW, _splitRightH, 0, 1));
                    _context.PSSetShaderResource(0, _splitRightSrv);
                    _context.Draw(3, 0);
                }
                _context.OMSetBlendState(null);
                _context.PSSetShaderResources(0, _nullSrvs);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::ApplySplitCompare", ex);
            }
        }

        private bool Present(int syncInterval = 0)
        {
            if (_fgGenerating && App.Settings.Prop.FrameGenSplitCompare)
                ApplySplitCompare();
            if (App.Settings.Prop.FrameGenOverlayShow && FrameGenSettings.ModeIndex > 0)
                DrawHud();
            if (FrameGenSettings.ModeIndex > 0)
                DrawFrameGenStatus();
            DrawCrosshair();
            double before = _clock.Elapsed.TotalMilliseconds;
            var flags = syncInterval > 0 ? PresentFlags.None : _presentFlags;
            var presentResult = _swapChain!.Present((uint)syncInterval, flags);
            _presentWaitMs += _clock.Elapsed.TotalMilliseconds - before;
            _presentWaitCount++;
            if (presentResult == Vortice.DXGI.ResultCode.DeviceRemoved || presentResult == Vortice.DXGI.ResultCode.DeviceReset)
            {
                App.Logger.WriteLine(LOG_IDENT, "Graphics device was lost, the session will restart");
                _deviceLost = true;
                return false;
            }
            return true;
        }

        private double _lastSettingsCheckSec;
        private DateTime _settingsFileTimeUtc;

        private void ReloadSettingsIfChanged()
        {
            double nowSec = _clock.Elapsed.TotalSeconds;
            if (nowSec - _lastSettingsCheckSec < 2.0)
                return;
            _lastSettingsCheckSec = nowSec;
            _wgc?.SetTargetFps(CaptureTargetFps());
            try
            {
                string path = App.Settings.FileLocation;
                if (!System.IO.File.Exists(path))
                    return;
                DateTime stamp = System.IO.File.GetLastWriteTimeUtc(path);
                if (_settingsFileTimeUtc == default)
                {
                    _settingsFileTimeUtc = stamp;
                    return;
                }
                if (stamp != _settingsFileTimeUtc)
                {
                    _settingsFileTimeUtc = stamp;
                    App.Settings.Load();
                    App.Logger.WriteLine(LOG_IDENT, "Settings changed on disk, reloaded so overlay toggles apply live");
                }
            }
            catch
            {
            }
        }

        private void SyncFrameGenRuntimeServices()
        {
            bool enabled = FrameGenSettings.ModeIndex > 0;
            Thread.CurrentThread.Priority = enabled ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
            if (enabled)
            {
                RobloxFpsCap.EnsureStarted();
                if (!_timerRaised)
                {
                    Interop.timeBeginPeriod(1);
                    _timerRaised = true;
                }
				return;
            }
            if (_timerRaised)
            {
                Interop.timeEndPeriod(1);
                _timerRaised = false;
            }
        }

        private void UpdateVBlankInfo()
        {
            double nowSec = _clock.Elapsed.TotalSeconds;
            if (nowSec - _lastVbQuerySec < 1.0)
                return;
            _lastVbQuerySec = nowSec;
            try
            {
                var ti = new Interop.DWM_TIMING_INFO { cbSize = (uint)Marshal.SizeOf<Interop.DWM_TIMING_INFO>() };
                if (Interop.DwmGetCompositionTimingInfo(IntPtr.Zero, ref ti) != 0 || ti.qpcRefreshPeriod == 0)
                {
                    _vbPeriodMs = 0;
                    return;
                }
                double freq = Stopwatch.Frequency;
                double periodMs = ti.qpcRefreshPeriod * 1000.0 / freq;
                if (periodMs < 2.0 || periodMs > 25.0)
                {
                    _vbPeriodMs = 0;
                    return;
                }
                long nowQpc = Stopwatch.GetTimestamp();
                double nowMs = _clock.Elapsed.TotalMilliseconds;
                _vbPeriodMs = periodMs;
                _vbBaseMs = nowMs + ((long)ti.qpcVBlank - nowQpc) * 1000.0 / freq;
            }
            catch
            {
                _vbPeriodMs = 0;
            }
        }

        private double AlignTarget(double targetMs, double slotMs)
        {
            if (_tearingActive || _vbPeriodMs <= 0)
                return targetMs;

            // VRR detection: if the slot time does not divide evenly into the
            // reported vblank period, or if the display's nominal refresh rate
            // is a known VRR range indicator, skip grid snapping.  On VRR
            // monitors the blanking interval adapts to the GPU's actual
            // present timing, so forcing a static grid causes micro-stutters.
            double nominalSlotMs = 1000.0 / Math.Max(1.0, _refreshHz);
            double periodRatio = _vbPeriodMs / Math.Max(0.1, nominalSlotMs);
            bool likelyVrr = Math.Abs(periodRatio - Math.Round(periodRatio)) > 0.15
                          || (_refreshHz >= 48.0 && Math.Abs(_vbPeriodMs - nominalSlotMs) > 1.5);
            if (likelyVrr)
                return targetMs;

            double grid = _vbPeriodMs;
            if (slotMs < _vbPeriodMs * 0.9)
            {
                int sub = Math.Max(1, (int)Math.Round(_vbPeriodMs / Math.Max(0.1, slotMs)));
                grid = _vbPeriodMs / sub;
            }
            double lead = Math.Min(1.5, grid * 0.25);
            double aligned;
            if (_lastAlignedMs <= 0)
                aligned = _vbBaseMs + Math.Ceiling((targetMs + lead - _vbBaseMs) / grid) * grid - lead;
            else
            {
                double nearest = _vbBaseMs + Math.Round((targetMs + lead - _vbBaseMs) / grid) * grid - lead;
                aligned = targetMs + Math.Clamp(nearest - targetMs, -0.12, 0.12);
                if (aligned <= _lastAlignedMs + grid * 0.5)
                    aligned = _lastAlignedMs + grid;
            }
            _lastAlignedMs = aligned;
            return aligned;
        }

        private double StableIntervalMs()
        {
            if (_dtCount == 0)
                return _emaIntervalMs;
            return FrameGenSettings.MedianInterval(_dtRing.AsSpan(0, _dtCount));
        }

        private void PaceWait(double untilMs, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                double remaining = untilMs - _clock.Elapsed.TotalMilliseconds;
                if (remaining <= 0.05)
                    break;
                if (remaining > 0.65 && _paceTimer != IntPtr.Zero)
                {
                    long dueTime = -(long)Math.Max(1.0, (remaining - 0.25) * 10000.0);
                    if (SetWaitableTimer(_paceTimer, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        Interop.WaitForSingleObject(_paceTimer, (uint)Math.Clamp(Math.Ceiling(remaining + 1.0), 1.0, 1000.0));
                        continue;
                    }
                }
                if (remaining > 1.6)
                    token.WaitHandle.WaitOne(1);
                else if (remaining > 0.3)
                    Thread.SpinWait(400);
                else
                    Thread.SpinWait(60);
            }
        }

        private bool WaitForPresentReady(double deadlineMs, CancellationToken token)
        {
            if (_frameLatencyHandle == IntPtr.Zero)
                return true;
            while (!token.IsCancellationRequested)
            {
                uint result = Interop.WaitForSingleObject(_frameLatencyHandle, 0);
                if (result == 0)
                    return true;
                if (result == uint.MaxValue)
                {
                    CloseHandle(_frameLatencyHandle);
                    _frameLatencyHandle = IntPtr.Zero;
                    return true;
                }
                double remaining = deadlineMs - _clock.Elapsed.TotalMilliseconds;
                if (remaining <= 0.05)
                    return false;
                result = Interop.WaitForSingleObject(_frameLatencyHandle, (uint)Math.Clamp(Math.Ceiling(remaining), 1.0, 2.0));
                if (result == 0)
                    return true;
                if (result == uint.MaxValue)
                {
                    CloseHandle(_frameLatencyHandle);
                    _frameLatencyHandle = IntPtr.Zero;
                    return true;
                }
            }
            return false;
        }

        private static double Percentile95(double[] samples, int count)
        {
            if (count <= 0)
                return 0;
            count = Math.Min(count, samples.Length);
            double[] sorted = new double[count];
            Array.Copy(samples, sorted, count);
            Array.Sort(sorted);
            return sorted[Math.Max(0, (int)Math.Ceiling(count * 0.95) - 1)];
        }

        private void LogStatsIfDue()
        {
            double now = _clock.Elapsed.TotalSeconds;
            long droppedNow = _wgc?.DroppedCount ?? 0;
            if (_lastStatsLog == 0)
            {
                _lastStatsLog = now;
                _realAtLastLog = _realPresented;
                _genAtLastLog = _genPresented;
                _droppedAtLastLog = droppedNow;
                _missedAtLastLog = _missedSlotsTotal;
                _frucRequestedAtLastLog = _frucRequestedTotal;
                _frucUsedAtLastLog = _frucUsedTotal;
                return;
            }
            double window = now - _lastStatsLog;
            if (window < 5.0)
                return;
            long real = _realPresented - _realAtLastLog;
            long gen = _genPresented - _genAtLastLog;
            _lastStatsLog = now;
            _realAtLastLog = _realPresented;
            _genAtLastLog = _genPresented;
            long dropped = Math.Max(0, droppedNow - _droppedAtLastLog);
            long missed = Math.Max(0, _missedSlotsTotal - _missedAtLastLog);
            long frucRequested = Math.Max(0, _frucRequestedTotal - _frucRequestedAtLastLog);
            long frucUsed = Math.Max(0, _frucUsedTotal - _frucUsedAtLastLog);
            _droppedAtLastLog = droppedNow;
            _missedAtLastLog = _missedSlotsTotal;
            _frucRequestedAtLastLog = _frucRequestedTotal;
            _frucUsedAtLastLog = _frucUsedTotal;

            string chain = _lastChainLog.Length == 0 ? "passthrough" : _lastChainLog;
			double capturePresentedFps = real / window;
			double captureCadenceFps = _emaIntervalMs > 0.5 ? 1000.0 / _emaIntervalMs : capturePresentedFps;
			double actualFps = ReliableActualFps();
            double outFps = (real + gen) / window;
            double captureAgeP95 = Percentile95(_captureAgeSamples, _captureAgeCount);
            double pacingErrorP95 = Percentile95(_paceErrorSamples, _paceErrorCount);
            _captureAgeHead = 0;
            _captureAgeCount = 0;
            _paceErrorHead = 0;
            _paceErrorCount = 0;
			if (actualFps > 0.5 && window >= 4.0 && window <= 8.0)
				RobloxFpsCap.ReportMeasuredBase(actualFps);
            double presentAvg = _presentWaitCount > 0 ? _presentWaitMs / _presentWaitCount : 0;
            _presentWaitMs = 0;
            _presentWaitCount = 0;

            if (FrameGenSettings.ModeIndex <= 0)
            {
                App.Logger.WriteLine(LOG_IDENT, $"chain [{chain}], {outFps:0} fps presented");
                return;
            }

            double trueSourceFps = Math.Max(actualFps, (real + dropped) / window);
            if (_fgAttached && !_hiddenByFocus && gen > 0 && trueSourceFps >= 5.0)
            {
                if (outFps < trueSourceFps * DeficitOutputRatio)
                    _outputDeficitWindows++;
                else
                    _outputDeficitWindows = 0;
                if (_outputDeficitWindows >= 2)
                {
                    DisableFrameGenForSession($"Frame Generation turned off for this session, it was presenting {outFps:0} fps while Roblox produced {trueSourceFps:0} fps, so it was costing frames instead of adding them");
                    _outputDeficitWindows = 0;
                }
            }
            else
                _outputDeficitWindows = 0;
            if (_consumeRatioEma >= SustainedConsumeRatio)
                ApplyGenerationHeadroomCap();

            string mode = FrameGenSettings.ModeNames[FrameGenSettings.ModeIndex];
            double frucHit = frucRequested > 0 ? Math.Min(100.0, frucUsed * 100.0 / frucRequested) : 0;
            long frucFallbacks = Math.Max(0, frucRequested - frucUsed);
			string interpolationPolicy = captureCadenceFps > 0.5 && captureCadenceFps <= 30.0 ? "buffered low rate" : "buffered";
            string diagnostics = $", interpolation {interpolationPolicy}, flow quality {_effectiveFgQuality}, source cadence {trueSourceFps:0} fps, captures consumed {_consumeRatioEma * 100.0:0} percent, capture age p95 {captureAgeP95:0.00}ms, pacing error p95 {pacingErrorP95:0.00}ms, missed slots {missed}, queued captures dropped {dropped}, NVIDIA hit {frucHit:0.0} percent, universal fallbacks {frucFallbacks}";
            if (gen > 0)
            {
				double mult = captureCadenceFps > 0.5 ? outFps / captureCadenceFps : 0;
                string presentNote = presentAvg > 1.0 ? $", presents are blocking {presentAvg:0.0}ms each (the desktop compositor is throttling output)" : $", present cost {presentAvg:0.00}ms";
				string actualNote = actualFps > 0.5 ? $", Roblox presents {actualFps:0} fps" : ", Roblox presents unavailable";
				App.Logger.WriteLine(LOG_IDENT, $"chain [{chain}], Frame Generation {mode} ON, capture cadence {captureCadenceFps:0} fps ({_emaIntervalMs:0.0}ms) to output {outFps:0} fps ({mult:0.0}x) toward {_refreshHz:0}Hz, captured frames {real}, generated frames {gen} over {window:0.0}s{actualNote}, resyncs {_fgResetCount}, capture to use {_poolWaitEma:0.0}ms{presentNote}{diagnostics}{DisplayedFramesNote(real + gen)}");
            }
            else
            {
				string sourceNote = actualFps > 0.5 ? $", Roblox presents {actualFps:0} fps" : ", Roblox presents unavailable";
				App.Logger.WriteLine(LOG_IDENT, $"chain [{chain}], Frame Generation {mode} idle, capture cadence {captureCadenceFps:0} fps against a {_refreshHz:0}Hz display{sourceNote}, no generated frames this window (interval {_emaIntervalMs:0.00}ms, samples {_dtCount}, budget {_lastDisplayBudget}, gen {_lastGenCount}, generating {_fgGenerating}, pairStale {_pairStale}, Roblox cap {RobloxFpsCap.Describe()}), resyncs {_fgResetCount}{diagnostics}{DisplayedFramesNote(real + gen)}");
            }
            _fgResetCount = 0;
        }

        private void Cleanup()
        {
            try
            {
                if (_timerRaised)
                    Interop.timeEndPeriod(1);
                if (_hotkeyRegistered && _hwnd != IntPtr.Zero)
                    RiShadeInterop.UnregisterHotKey(_hwnd, 1);
                if (_riAttached)
                    _rishade.DisposeExternal();
                if (_aaAttached)
                    _aa.DisposeExternal();
                if (_fgAttached)
                    _fg.Dispose();
                _fruc?.Dispose();
                _fruc = null;
				ReleaseHomepageMedia();
                _wgc?.Dispose();
                _duplication?.Dispose();
                ReleaseSizedResources();
                _hud.Dispose();
                _crosshair.Dispose();
                _hudBlend?.Dispose();
                _splitLeftSrv?.Dispose();
                _splitLeftTex?.Dispose();
                _splitRightSrv?.Dispose();
                _splitRightTex?.Dispose();
                _splitLineSrv?.Dispose();
                _splitLineTex?.Dispose();
                _fgStatusSrv?.Dispose();
                _fgStatusTex?.Dispose();
                _backBufferTex?.Dispose();
                _cbuffer?.Dispose();
                _sampler?.Dispose();
                _psPass?.Dispose();
				_psHomeBackground?.Dispose();
                _psCropSrgb?.Dispose();
                _psOverlay?.Dispose();
                _vs?.Dispose();
                _dcompVisual?.Dispose();
                _dcompTarget?.Dispose();
                _dcompDevice?.Dispose();
                _backBufferRtv?.Dispose();
                if (_frameLatencyHandle != IntPtr.Zero)
                {
                    CloseHandle(_frameLatencyHandle);
                    _frameLatencyHandle = IntPtr.Zero;
                }
                if (_paceTimer != IntPtr.Zero)
                {
                    CloseHandle(_paceTimer);
                    _paceTimer = IntPtr.Zero;
                }
                _swapChain2?.Dispose();
                _swapChain?.Dispose();
                _factory?.Dispose();
                _context?.Dispose();
                _device?.Dispose();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayCompositor::Cleanup", ex);
            }
            try
            {
                if (_hwnd != IntPtr.Zero)
                    Interop.DestroyWindow(_hwnd);
                if (_classAtom != 0)
                    Interop.UnregisterClassW(new IntPtr(_classAtom), _hInstance);
            }
            catch
            {
            }
            _hwnd = IntPtr.Zero;
            _classAtom = 0;
            OverlayHub.SetCompositorLive(false);
            App.Logger.WriteLine(LOG_IDENT, "Compositor session cleaned up");
        }
    }
}
