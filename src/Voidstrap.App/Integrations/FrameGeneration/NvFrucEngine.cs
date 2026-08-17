using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Voidstrap.Integrations.FrameGeneration
{
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NvFrucCreateParam
    {
        public uint Width;
        public uint Height;
        public IntPtr Device;
        public int ResourceType;
        public int SurfaceFormat;
        public int CudaResourceType;
        public fixed uint Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NvFrucRegisterParam
    {
        public fixed long Resources[10];
        public IntPtr FenceObj;
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NvFrucUnregisterParam
    {
        public fixed long Resources[10];
        public uint Count;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NvFrucFrameData
    {
        public IntPtr Frame;
        public double TimeStamp;
        public IntPtr CuSurfacePitch;
        public IntPtr HasFrameRepetitionOccurred;
        public fixed uint Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NvFrucProcessInParams
    {
        public NvFrucFrameData FrameDataInput;
        public uint SkipWarp;
        public ulong FenceValueToWaitOn;
        public ulong SyncPad;
        public fixed uint Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NvFrucProcessOutParams
    {
        public NvFrucFrameData FrameDataOutput;
        public ulong FenceValueToSignalOn;
        public ulong SyncPad;
        public fixed uint Reserved[32];
    }

    internal sealed class NvFrucEngine : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CreateFn(ref NvFrucCreateParam create, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int RegisterFn(IntPtr handle, ref NvFrucRegisterParam param);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int UnregisterFn(IntPtr handle, ref NvFrucUnregisterParam param);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ProcessFn(IntPtr handle, ref NvFrucProcessInParams inParams, ref NvFrucProcessOutParams outParams);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DestroyFn(IntPtr handle);

        private static readonly Lock _loadLock = new();
        private static bool _loadTried;
        private static string _loadFail = "";
        private static CreateFn? _create;
        private static RegisterFn? _register;
        private static UnregisterFn? _unregister;
        private static ProcessFn? _process;
        private static DestroyFn? _destroy;

        private const string InstallBaseUrl = "https://voidstrapp.pages.dev/assets/bin/";
        private static readonly Lock _installLock = new();
        private static bool _installStarted;
        private static volatile bool _installReady;

        public static bool InstallReady => _installReady;

        public static void ConsumeInstall()
        {
            _installReady = false;
            lock (_loadLock)
            {
                _loadTried = false;
                _loadFail = "";
            }
        }

        public static void BeginInstall()
        {
            lock (_installLock)
            {
                if (_installStarted)
                    return;
                _installStarted = true;
            }
            System.Threading.Tasks.Task.Run(InstallWorker);
        }

        private static void InstallWorker()
        {
            try
            {
                string dir = !string.IsNullOrEmpty(Paths.Base) ? Paths.Base : AppContext.BaseDirectory;
                App.Logger.WriteLine("NvFruc", "Downloading the NVIDIA interpolation components from the Voidstrap site, one time only");
                bool ok = DownloadOne("NvOFFRUC.dll", 783416, "5a0b6701d30709e25e7e5b92ca46b18aab1459160cecd4f629872369d85c8b0a", dir)
                    && DownloadOne("cudart64_110.dll", 466488, "edc35e7d0fa3f257bbedfa7888911080c5696acdd40b6187b6dd0173f20759ad", dir);
                if (ok)
                {
                    _installReady = true;
                    App.Logger.WriteLine("NvFruc", "NVIDIA interpolation components installed, hardware interpolation will engage shortly");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("NvFruc::Install", ex);
            }
        }

        private static bool DownloadOne(string name, long size, string sha256, string dir)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path) && new FileInfo(path).Length == size && HashMatches(path, sha256))
                return true;
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
            try
            {
                Voidstrap.Utility.ResilientDownload.DownloadAsync(App.HttpClient, [InstallBaseUrl + name], path, size, cts.Token, sha256).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                App.Logger.WriteLine("NvFruc", name + " download timed out, using the built in generator");
                return false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("NvFruc", name + " download failed: " + ex.Message);
                return false;
            }
            if (new FileInfo(path).Length != size || !HashMatches(path, sha256))
            {
                App.Logger.WriteLine("NvFruc", name + " failed download verification and was not installed");
                File.Delete(path);
                return false;
            }
            return true;
        }

        private static bool HashMatches(string path, string expected)
        {
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                using var fs = File.OpenRead(path);
                return Convert.ToHexString(sha.ComputeHash(fs)).Equals(expected, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private const int OutputCount = 2;

        private sealed class FrucJob
        {
            public long Id;
            public int Slot;
            public double InTs;
            public double OutTs;
            public int OutputSlot;
            public ulong WaitValue;
            public ulong SignalValue;
            public bool PrimeOnly;
            public long Generation;
        }

        private IntPtr _handle;
        private ID3D11Device5? _device5;
        private ID3D11DeviceContext4? _context4;
        private ID3D11Fence? _fence;
        private ulong _fenceValue;
        private ulong _inFlightSignal;
        private readonly ID3D11Texture2D?[] _inTex = new ID3D11Texture2D?[2];
        private readonly ID3D11RenderTargetView?[] _inRtv = new ID3D11RenderTargetView?[2];
        private readonly ID3D11Texture2D?[] _outTex = new ID3D11Texture2D?[OutputCount];
        private readonly ID3D11ShaderResourceView?[] _outSrv = new ID3D11ShaderResourceView?[OutputCount];
        private readonly long[] _outJob = new long[OutputCount];
        private readonly int[] _outSlot = new int[OutputCount];
        private readonly ulong[] _outSignal = new ulong[OutputCount];
        private readonly int[] _outReady = new int[OutputCount];
        private readonly byte[] _repFlag = new byte[1];
        private GCHandle _repPin;
        private bool _registered;
        private bool _disposed;

        private volatile bool _released;

        private const int AbandonedReapAttempts = 12;

        private const int AbandonedReapWaitMs = 5000;
        private Thread? _worker;
        private readonly AutoResetEvent _jobEvent = new(false);
        private readonly Lock _jobLock = new();
        private FrucJob? _pendingJob;
        private bool _busy;
        private long _jobCounter;
        private long _generation;
        private double _previousTimestamp;
        private int _procFails;
        private volatile int _state;
        private volatile bool _stop;
        private volatile bool _primed;
        private IntPtr _devicePtr;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public int SourceWidth { get; private set; }
        public int SourceHeight { get; private set; }
        public double PreviousTimestamp => Volatile.Read(ref _previousTimestamp);
        public bool Ready => _state == 1;
        public bool Broken => _state == 2;
        public bool Primed => _primed;
        public string FailReason { get; private set; } = "";

        public bool CanAcceptSource
        {
            get
            {
                lock (_jobLock)
					return !_disposed && _state == 1 && _fence != null && !_busy && _pendingJob == null && (_inFlightSignal == 0 || _fence.CompletedValue >= _inFlightSignal);
            }
        }

        public ID3D11RenderTargetView InputRtv(int slot) => _inRtv[slot & 1]!;

        public static bool IsNvidiaAdapter(ID3D11Device device)
        {
            try
            {
                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                dxgiDevice.GetAdapter(out IDXGIAdapter? adapter).CheckError();
                try
                {
                    return adapter!.Description.VendorId == 0x10DE;
                }
                finally
                {
                    adapter!.Dispose();
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool EnsureLoaded(out string reason)
        {
            lock (_loadLock)
            {
                if (_create != null)
                {
                    reason = "";
                    return true;
                }
                if (_loadTried)
                {
                    reason = _loadFail;
                    return false;
                }
                _loadTried = true;
                IntPtr dll = IntPtr.Zero;
                string[] dirs;
                try
                {
                    dirs = string.IsNullOrEmpty(Paths.Base)
                        ? [AppContext.BaseDirectory]
                        : [AppContext.BaseDirectory, Paths.Base];
                }
                catch
                {
                    dirs = [AppContext.BaseDirectory];
                }
                foreach (string name in new[] { "NvOFFRUC.dll", "NvFRUC.dll" })
                {
                    foreach (string dir in dirs)
                    {
                        string full = Path.Combine(dir, name);
                        if (File.Exists(full) && NativeLibrary.TryLoad(full, out dll))
                            break;
                        dll = IntPtr.Zero;
                    }
                    if (dll != IntPtr.Zero)
                        break;
                    if (NativeLibrary.TryLoad(name, out dll))
                        break;
                    dll = IntPtr.Zero;
                }
                if (dll == IntPtr.Zero)
                {
                    _loadFail = "the NVIDIA FRUC library is not installed next to the app";
                    reason = _loadFail;
                    return false;
                }
                foreach (string prefix in new[] { "NvFRUC", "NvOFFRUC" })
                {
                    if (NativeLibrary.TryGetExport(dll, prefix + "Create", out var pCreate)
                        && NativeLibrary.TryGetExport(dll, prefix + "RegisterResource", out var pRegister)
                        && NativeLibrary.TryGetExport(dll, prefix + "UnregisterResource", out var pUnregister)
                        && NativeLibrary.TryGetExport(dll, prefix + "Process", out var pProcess)
                        && NativeLibrary.TryGetExport(dll, prefix + "Destroy", out var pDestroy))
                    {
                        _create = Marshal.GetDelegateForFunctionPointer<CreateFn>(pCreate);
                        _register = Marshal.GetDelegateForFunctionPointer<RegisterFn>(pRegister);
                        _unregister = Marshal.GetDelegateForFunctionPointer<UnregisterFn>(pUnregister);
                        _process = Marshal.GetDelegateForFunctionPointer<ProcessFn>(pProcess);
                        _destroy = Marshal.GetDelegateForFunctionPointer<DestroyFn>(pDestroy);
                        reason = "";
                        return true;
                    }
                }
                _loadFail = "the FRUC library exports were not found in the DLL";
                reason = _loadFail;
                return false;
            }
        }

        public static NvFrucEngine? TryCreate(ID3D11Device device, ID3D11DeviceContext context, int width, int height, bool fullResolution, out string reason)
        {
            if (!IsNvidiaAdapter(device))
            {
                reason = "the GPU is not NVIDIA";
                return null;
            }
            if (!EnsureLoaded(out reason))
                return null;

            var engine = new NvFrucEngine();
            try
            {
                engine._device5 = device.QueryInterfaceOrNull<ID3D11Device5>();
                engine._context4 = context.QueryInterfaceOrNull<ID3D11DeviceContext4>();
                if (engine._device5 == null || engine._context4 == null)
                {
                    reason = "this Windows build lacks D3D11 fence support";
                    engine.Dispose();
                    return null;
                }
                engine.SourceWidth = width;
                engine.SourceHeight = height;
                int divisor = fullResolution ? 1 : 2;
                engine.Width = Math.Max(16, (width / divisor) & ~1);
                engine.Height = Math.Max(16, (height / divisor) & ~1);
                engine._fence = engine._device5.CreateFence(0, FenceFlags.Shared);
                engine._repPin = GCHandle.Alloc(engine._repFlag, GCHandleType.Pinned);

                var desc = new Texture2DDescription
                {
                    Width = (uint)engine.Width,
                    Height = (uint)engine.Height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CPUAccessFlags = CpuAccessFlags.None,
                    MiscFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNTHandle,
                };
                for (int i = 0; i < 2; i++)
                {
                    engine._inTex[i] = device.CreateTexture2D(desc);
                    engine._inRtv[i] = device.CreateRenderTargetView(engine._inTex[i]);
                }
                for (int i = 0; i < OutputCount; i++)
                {
                    engine._outTex[i] = device.CreateTexture2D(desc);
                    engine._outSrv[i] = device.CreateShaderResourceView(engine._outTex[i]);
                }

                engine._devicePtr = device.NativePointer;
                engine._worker = new Thread(engine.WorkerLoop)
                {
                    IsBackground = true,
                    Name = "NvFrucWorker",
					Priority = ThreadPriority.Normal,
                };
                engine._worker.Start();
                return engine;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                engine.Dispose();
                return null;
            }
        }

        public void Deprime()
        {
            Interlocked.Increment(ref _generation);
            _primed = false;
            Volatile.Write(ref _previousTimestamp, 0);
            ulong abandonedSignal = 0;
            lock (_jobLock)
            {
                if (_pendingJob != null)
                    abandonedSignal = _pendingJob.SignalValue;
                _pendingJob = null;
            }
            if (abandonedSignal > 0 && _context4 != null && _fence != null)
            {
                try
                {
                    _context4.Signal(_fence, abandonedSignal);
                    _context4.Flush();
                }
                catch
                {
                }
            }
        }

        public long SubmitPair(int slot, double inTs, double outTs, int outputSlot, out bool producesOutput)
        {
            if (_disposed || _state != 1)
            {
                producesOutput = false;
                return -1;
            }
            lock (_jobLock)
            {
                if (_busy || _pendingJob != null || _inFlightSignal > 0 && _fence!.CompletedValue < _inFlightSignal)
                {
                    producesOutput = false;
                    return -1;
                }
                _fenceValue++;
                _context4!.Signal(_fence!, _fenceValue);
                _context4.Flush();
                bool primeOnly = !_primed;
                var job = new FrucJob
                {
                    Id = ++_jobCounter,
                    Slot = slot,
                    InTs = inTs,
                    OutTs = outTs,
                    OutputSlot = outputSlot,
                    WaitValue = _fenceValue,
                    SignalValue = _fenceValue + 1,
                    PrimeOnly = primeOnly,
                    Generation = Volatile.Read(ref _generation),
                };
                _fenceValue++;
                _pendingJob = job;
                _inFlightSignal = job.SignalValue;
                producesOutput = !primeOnly;
            }
            _jobEvent.Set();
            return _jobCounter;
        }

        public bool AllReady(long jobId)
        {
			if (_disposed || jobId < 0)
                return false;
            for (int i = 0; i < OutputCount; i++)
                if (Volatile.Read(ref _outReady[i]) == 1 && _outJob[i] == jobId)
                    return true;
            return false;
        }

        public bool TryGetReady(long jobId, int slotK, out ID3D11ShaderResourceView? srv, out ulong signal)
        {
            srv = null;
            signal = 0;
			if (_disposed || jobId < 0)
                return false;
            for (int i = 0; i < OutputCount; i++)
            {
                if (Volatile.Read(ref _outReady[i]) == 1 && _outJob[i] == jobId && _outSlot[i] == slotK)
                {
                    srv = _outSrv[i];
                    signal = _outSignal[i];
                    return srv != null;
                }
            }
            return false;
        }

        public void WaitOutput(ulong signal)
        {
            if (_disposed)
                return;
            if (_fence!.CompletedValue >= signal)
                return;
            _context4!.Wait(_fence, signal);
        }

        private bool WorkerInit()
        {
            var createParam = new NvFrucCreateParam
            {
                Width = (uint)Width,
                Height = (uint)Height,
                Device = _devicePtr,
                ResourceType = 1,
                SurfaceFormat = 1,
                CudaResourceType = 0,
            };
            int status;
            try
            {
                status = _create!(ref createParam, out _handle);
            }
            catch (Exception ex)
            {
                FailReason = "create threw " + ex.Message;
                return false;
            }
            if (status != 0 || _handle == IntPtr.Zero)
            {
                FailReason = "the FRUC instance could not be created, status " + status;
                return false;
            }
            var reg = new NvFrucRegisterParam
            {
                Count = 2 + OutputCount,
                FenceObj = _fence!.NativePointer,
            };
            unsafe
            {
                reg.Resources[0] = (long)_inTex[0]!.NativePointer;
                reg.Resources[1] = (long)_inTex[1]!.NativePointer;
                for (int i = 0; i < OutputCount; i++)
                    reg.Resources[2 + i] = (long)_outTex[i]!.NativePointer;
            }
            try
            {
                status = _register!(_handle, ref reg);
            }
            catch (Exception ex)
            {
                FailReason = "register threw " + ex.Message;
                return false;
            }
            if (status != 0)
            {
                FailReason = "the FRUC resources could not be registered, status " + status;
                return false;
            }
            _registered = true;
            return true;
        }

        private void WorkerTeardown()
        {
            if (_handle == IntPtr.Zero)
                return;
            if (_registered)
            {
                var unreg = new NvFrucUnregisterParam { Count = 2 + OutputCount };
                unsafe
                {
                    unreg.Resources[0] = (long)(_inTex[0]?.NativePointer ?? IntPtr.Zero);
                    unreg.Resources[1] = (long)(_inTex[1]?.NativePointer ?? IntPtr.Zero);
                    for (int i = 0; i < OutputCount; i++)
                        unreg.Resources[2 + i] = (long)(_outTex[i]?.NativePointer ?? IntPtr.Zero);
                }
                try { _unregister?.Invoke(_handle, ref unreg); } catch { }
                _registered = false;
            }
            try { _destroy?.Invoke(_handle); } catch { }
            _handle = IntPtr.Zero;
        }

        private void WorkerLoop()
        {
            try
            {
                WorkerLoopCore();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("NvFrucEngine::WorkerLoop", ex);
                _state = 2;
                try
                {
                    WorkerTeardown();
                }
                catch
                {
                }
            }
        }

        private void WorkerLoopCore()
        {
            if (!WorkerInit())
            {
                _state = 2;
                WorkerTeardown();
                return;
            }
            _state = 1;
            while (!_stop)
            {
                try
                {
                    _jobEvent.WaitOne(200);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                if (_stop)
                    break;
                FrucJob? job;
                lock (_jobLock)
                {
                    job = _pendingJob;
                    _pendingJob = null;
                    _busy = job != null;
                }
                if (job == null)
                    continue;
                try
                {
                    if (job.PrimeOnly)
                    {
                        if (ProcessOne(job, true, 0) == 0 && job.Generation == Volatile.Read(ref _generation))
                        {
                            Volatile.Write(ref _previousTimestamp, job.InTs);
                            _primed = true;
                        }
                        continue;
                    }
                    int idx = (int)(job.Id & 1);
                    Volatile.Write(ref _outReady[idx], 0);
                    int status = ProcessOne(job, false, idx);
                    if (status == 0 && job.Generation == Volatile.Read(ref _generation))
                    {
                        Volatile.Write(ref _previousTimestamp, job.InTs);
                        if (_repFlag[0] == 0)
                        {
                            _outJob[idx] = job.Id;
                            _outSlot[idx] = job.OutputSlot;
                            _outSignal[idx] = job.SignalValue;
                            Volatile.Write(ref _outReady[idx], 1);
                        }
                    }
                }
                finally
                {
                    lock (_jobLock)
                        _busy = false;
                }
            }
            WorkerTeardown();
        }

        private int ProcessOne(FrucJob job, bool skipWarp, int outputIdx)
        {
            if (_disposed || _handle == IntPtr.Zero)
                return -1;
            _repFlag[0] = 0;
            var inp = new NvFrucProcessInParams();
            inp.FrameDataInput.Frame = _inTex[job.Slot & 1]!.NativePointer;
            inp.FrameDataInput.TimeStamp = job.InTs;
            inp.FrameDataInput.CuSurfacePitch = (IntPtr)(Width * 4);
            inp.SkipWarp = skipWarp ? 1u : 0u;
            inp.FenceValueToWaitOn = job.WaitValue;
            var outp = new NvFrucProcessOutParams();
            outp.FrameDataOutput.Frame = _outTex[outputIdx]!.NativePointer;
            outp.FrameDataOutput.TimeStamp = skipWarp ? job.InTs : job.OutTs;
            outp.FrameDataOutput.CuSurfacePitch = (IntPtr)(Width * 4);
            outp.FrameDataOutput.HasFrameRepetitionOccurred = _repPin.AddrOfPinnedObject();
            outp.FenceValueToSignalOn = job.SignalValue;
            int status;
            try
            {
                status = _process!(_handle, ref inp, ref outp);
            }
            catch (Exception)
            {
                status = -1;
            }
            if (status != 0)
            {
                _procFails++;
                if (_procFails == 1)
                    App.Logger.WriteLine("NvFruc", "process call failed with status " + status);
                if (_procFails >= 3)
                {
                    FailReason = "process failed with status " + status;
                    _state = 2;
                }
            }
            else
                _procFails = 0;
            return status;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
			_disposed = true;
			Deprime();
            _stop = true;
            try
            {
                _jobEvent.Set();
            }
            catch (ObjectDisposedException)
            {
            }
			Thread? worker = _worker;
			try
            {
				worker?.Join(5000);
            }
            catch
            {
            }
			if (worker?.IsAlive == true)
			{
				App.Logger.WriteLine("NvFruc", "The interpolation worker did not stop in time, releasing its resources in the background once it exits");
				Task.Run(() => ReapAbandonedWorker(worker));
				GC.SuppressFinalize(this);
				return;
			}
            _worker = null;
            ReleaseResources();
            GC.SuppressFinalize(this);
        }

        private void ReapAbandonedWorker(Thread worker)
        {
            try
            {
                for (int attempt = 0; attempt < AbandonedReapAttempts; attempt++)
                {
                    try
                    {
                        _jobEvent.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    if (worker.Join(AbandonedReapWaitMs))
                        break;
                }
                if (worker.IsAlive)
                {
                    App.Logger.WriteLine("NvFruc", "The interpolation worker never exited, its graphics resources stay allocated for this session");
                    return;
                }
                _worker = null;
                ReleaseResources();
                App.Logger.WriteLine("NvFruc", "Recovered the interpolation worker resources after a late exit");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("NvFruc", "Late interpolation cleanup failed: " + ex.Message);
            }
        }

        private void ReleaseResources()
        {
            if (_released)
                return;
            _released = true;
            for (int i = 0; i < 2; i++)
            {
                _inRtv[i]?.Dispose();
                _inTex[i]?.Dispose();
                _inRtv[i] = null;
                _inTex[i] = null;
            }
            for (int i = 0; i < OutputCount; i++)
            {
                _outSrv[i]?.Dispose();
                _outTex[i]?.Dispose();
                _outSrv[i] = null;
                _outTex[i] = null;
            }
            _fence?.Dispose();
            _fence = null;
            _context4?.Dispose();
            _context4 = null;
            _device5?.Dispose();
            _device5 = null;
            if (_repPin.IsAllocated)
                _repPin.Free();
            try
            {
                _jobEvent.Dispose();
            }
            catch
            {
            }
        }
    }
}
