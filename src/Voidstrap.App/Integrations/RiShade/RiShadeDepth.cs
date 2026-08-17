using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Voidstrap.Integrations.RiShade
{
    public static class RiShadeDepth
    {
        public const int Size = 256;

        private sealed class ModelSpec
        {
            public string Name = "";
            public string FileName = "";
            public string Url = "";
            public string Sha256 = "";
            public long Bytes;
            public int TensorSize;
            public bool ImageNetNorm;
            public int BudgetMs;
            public int MinIntervalMs;
        }

        private static readonly ModelSpec[] Models =
        [
            new()
            {
                Name = "Depth Anything V2 Small",
                FileName = "depth-anything-v2-small.onnx",
                Url = "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/main/onnx/model.onnx",
                Sha256 = "AFB6A5C28F3B6BF1618C6E43F02073EF9DFDC70E937502D51603E57B0A1DF10C",
                Bytes = 99060839L,
                TensorSize = 252,
                ImageNetNorm = true,
                BudgetMs = 120,
				MinIntervalMs = 160,
            },
            new()
            {
                Name = "MiDaS Small",
                FileName = "model-small.onnx",
                Url = "https://github.com/isl-org/MiDaS/releases/download/v2_1/model-small.onnx",
                Sha256 = "2D8C6CB8F415229DAF1EB041024208E2608C9F98E17C81CC7C6ECB449C56FD58",
                Bytes = 66764249L,
                TensorSize = 256,
                ImageNetNorm = false,
                BudgetMs = 10000,
				MinIntervalMs = 125,
            },
        ];

        private static ModelSpec _model = Models[0];
        private const string RuntimePackageUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/1.24.4/microsoft.ml.onnxruntime.directml.1.24.4.nupkg";
        private const long RuntimePackageBytes = 12458649L;
        private const string RuntimePackageSha256 = "57E9F11B73437BEF7A309496135D4C1F96B1A8E9DDBA60013FA27BFC1D788681";
        private const long RuntimeDllBytes = 17328152L;
        private const string RuntimeDllSha256 = "E7EEDEC6A6F26DC39DC948276A75EF6D2BEE3FFF944D874CEED0BBD3B97BFF40";
        private const long ProvidersDllBytes = 22040L;
        private const string ProvidersDllSha256 = "265C8DAF29637CB259CAC8BE9F08F2CD45F3883F0F0E4949CBFDDD5B4CBEC3B6";
        private const string DirectMlUrl = "https://voidstrapp.pages.dev/assets/bin/DirectML.dll";
        private const long DirectMlBytes = 18527776L;
        private const string DirectMlSha256 = "9C9E6D822561C6C41B90E6994B3E8857CF1D66DBFB1E0C4C799C7C89B4E92DA1";
        private const string LOG_IDENT = "RiShade";
        private static bool _resolverInstalled;

        private static readonly Lock _lock = new();
        private static InferenceSession? _session;
        private static string _inputName = "";
        private static Thread? _thread;
        private static CancellationTokenSource? _cts;
        private static int _state;
        private static readonly SemaphoreSlim _frameSignal = new(0, 1);
        private static byte[]? _pendingFrame;
        private static readonly Lock _frameLock = new();
        private static readonly float[] _latestDepth = new float[Size * Size];
        private static int _depthVersion;
        private static long _inferCount;
        private static double _inferMsTotal;

        public static bool IsReady => Volatile.Read(ref _state) == 2;
        public static bool IsFailed => Volatile.Read(ref _state) == 3;
        public static int DepthVersion => Volatile.Read(ref _depthVersion);

        private static string ModelPathFor(ModelSpec spec) => Path.Combine(Paths.RiShade, spec.FileName);

        public static void EnsureStarted()
        {
            lock (_lock)
            {
                if (_state != 0 || _thread != null)
                    return;
                var cts = new CancellationTokenSource();
                var thread = new Thread(() => Worker(cts))
                {
                    IsBackground = true,
                    Name = "RiShadeDepth",
					Priority = ThreadPriority.BelowNormal,
                };
                _cts = cts;
                _thread = thread;
                Volatile.Write(ref _state, 1);
                try
                {
                    thread.Start();
                }
                catch
                {
                    _cts = null;
                    _thread = null;
                    Volatile.Write(ref _state, 3);
                    cts.Dispose();
                    throw;
                }
            }
        }

        public static void Shutdown()
        {
            CancellationTokenSource? cts;
            Thread? thread;
            lock (_lock)
            {
                cts = _cts;
                thread = _thread;
                Volatile.Write(ref _state, 0);
            }
            try
            {
                cts?.Cancel();
                try
                {
                    _frameSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
                if (thread != null && !ReferenceEquals(thread, Thread.CurrentThread))
				{
					if (!thread.Join(2000))
						App.Logger.WriteLine(LOG_IDENT, "AI depth shutdown is still finishing in the background");
				}
            }
            catch
            {
            }
        }

        private static float _pendingAccumX;
        private static float _pendingAccumY;

        public static void SubmitFrame(byte[] bgra, float accumX, float accumY)
        {
            if (!IsReady)
                return;
            lock (_frameLock)
            {
                _pendingFrame = bgra;
                _pendingAccumX = accumX;
                _pendingAccumY = accumY;
            }
            try
            {
                _frameSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        private static int _activeQuality;
        private static float _latestTagX;
        private static float _latestTagY;

        public static bool TryGetDepth(ref int seenVersion, float[] destination, out float tagX, out float tagY)
        {
            tagX = 0f;
            tagY = 0f;
            int v = Volatile.Read(ref _depthVersion);
            if (v == seenVersion || v == 0)
                return false;
            lock (_frameLock)
            {
                Array.Copy(_latestDepth, destination, Size * Size);
                tagX = _latestTagX;
                tagY = _latestTagY;
            }
            seenVersion = v;
            return true;
        }

        private static void Worker(CancellationTokenSource owner)
        {
            CancellationToken token = owner.Token;
            try
            {
                if (!EnsureRuntimeFiles(token))
                {
                    Volatile.Write(ref _state, 3);
                    return;
                }
                while (!token.IsCancellationRequested)
                {
                    _activeQuality = RiShadeSettings.Current.AiQuality;
                    bool ready = false;
                    ModelSpec[] order = _activeQuality == 1
                        ? [Models[0], Models[1]]
                        : [Models[1], Models[0]];
                    App.Logger.WriteLine(LOG_IDENT, _activeQuality == 1 ? "AI quality mode requested" : "AI fast mode requested");
                    foreach (var spec in order)
                    {
                        if (token.IsCancellationRequested)
                            break;
                        if (!EnsureModelFile(spec, token))
                            continue;
                        if (!CreateSession(spec))
                            continue;
                        double ms = ProbeSession(spec);
                        if (ms > spec.BudgetMs)
                        {
                            App.Logger.WriteLine(LOG_IDENT, $"{spec.Name} measured {ms:F0}ms per frame, too slow, trying the next model");
                            _session?.Dispose();
                            _session = null;
                            continue;
                        }
                        _model = spec;
                        App.Logger.WriteLine(LOG_IDENT, $"{spec.Name} selected at {ms:F0}ms per frame");
                        ready = true;
                        break;
                    }
                    if (!ready)
                    {
                        Volatile.Write(ref _state, 3);
                        return;
                    }
                    Volatile.Write(ref _state, 2);
                    App.Logger.WriteLine(LOG_IDENT, "AI depth is ready");
                    RunLoop(token);
                    if (token.IsCancellationRequested)
                        break;
                    App.Logger.WriteLine(LOG_IDENT, "AI model setting changed, reloading live");
                    _session?.Dispose();
                    _session = null;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeDepth::Worker", ex);
                Volatile.Write(ref _state, 3);
            }
            finally
            {
                CompleteWorker(owner);
            }
        }

        private static void CompleteWorker(CancellationTokenSource owner)
        {
            InferenceSession? session = null;
            bool dispose = false;
            lock (_lock)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread) && ReferenceEquals(_cts, owner))
                {
                    _thread = null;
                    _cts = null;
                    session = _session;
                    _session = null;
                    if (owner.IsCancellationRequested)
                        Volatile.Write(ref _state, 0);
                    dispose = true;
                }
            }
            if (!dispose)
                return;
            lock (_frameLock)
            {
                _pendingFrame = null;
            }
            session?.Dispose();
            owner.Dispose();
        }

        private static string RuntimeDllPath => Path.Combine(Paths.RiShade, "onnxruntime.dll");
        private static string DirectMlDllPath => Path.Combine(Paths.RiShade, "DirectML.dll");
        private static string ProvidersDllPath => Path.Combine(Paths.RiShade, "onnxruntime_providers_shared.dll");

        private static bool EnsureRuntimeFiles(CancellationToken token)
        {
            try
            {
                if (!FileMatches(RuntimeDllPath, RuntimeDllBytes, RuntimeDllSha256) || !FileMatches(ProvidersDllPath, ProvidersDllBytes, ProvidersDllSha256))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Downloading the AI runtime");
                    string temp = Path.Combine(Paths.RiShade, "onnxruntime.nupkg");
                    string suffix = Guid.NewGuid().ToString("N");
                    string stagedRuntime = RuntimeDllPath + "." + suffix + ".tmp";
                    string stagedProviders = ProvidersDllPath + "." + suffix + ".tmp";
                    Voidstrap.Utility.ResilientDownload.DownloadAsync(App.HttpClient, [RuntimePackageUrl], temp, RuntimePackageBytes, token, RuntimePackageSha256).GetAwaiter().GetResult();
                    try
                    {
                        using (var zip = ZipFile.OpenRead(temp))
                        {
                            ExtractEntry(zip, "runtimes/win-x64/native/onnxruntime.dll", stagedRuntime, RuntimeDllBytes, RuntimeDllSha256);
                            ExtractEntry(zip, "runtimes/win-x64/native/onnxruntime_providers_shared.dll", stagedProviders, ProvidersDllBytes, ProvidersDllSha256);
                        }
                        File.Move(stagedProviders, ProvidersDllPath, true);
                        File.Move(stagedRuntime, RuntimeDllPath, true);
                    }
                    finally
                    {
                        TryDelete(temp);
                        TryDelete(stagedRuntime);
                        TryDelete(stagedProviders);
                    }
                    App.Logger.WriteLine(LOG_IDENT, "AI runtime downloaded and extracted");
                }
                if (!FileMatches(DirectMlDllPath, DirectMlBytes, DirectMlSha256))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Downloading the DirectML component, one time only");
                    Voidstrap.Utility.ResilientDownload.DownloadAsync(App.HttpClient, [DirectMlUrl], DirectMlDllPath, DirectMlBytes, token, DirectMlSha256).GetAwaiter().GetResult();
                    if (!FileMatches(DirectMlDllPath, DirectMlBytes, DirectMlSha256))
                    {
                        App.Logger.WriteLine(LOG_IDENT, "DirectML download size mismatch");
                        File.Delete(DirectMlDllPath);
                        return false;
                    }
                }
                if (NativeLibrary.TryLoad(DirectMlDllPath, out IntPtr directMlHandle))
                {
                    NativeLibrary.Free(directMlHandle);
                }
                else
                {
                    if (NativeLibrary.TryLoad("DirectML.dll", out IntPtr systemDirectMlHandle))
                    {
                        NativeLibrary.Free(systemDirectMlHandle);
                        App.Logger.WriteLine(LOG_IDENT, "Using the system DirectML component");
                    }
                    else
                        App.Logger.WriteLine(LOG_IDENT, "DirectML could not be loaded, AI depth will use the CPU");
                }
                if (File.Exists(ProvidersDllPath))
                {
                    if (NativeLibrary.TryLoad(ProvidersDllPath, out IntPtr providersHandle))
                        NativeLibrary.Free(providersHandle);
                }
                if (!_resolverInstalled)
                {
                    _resolverInstalled = true;
                    NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, ResolveOnnxRuntime);
                }
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "AI runtime setup failed: " + ex.Message);
                return false;
            }
        }

        private static void ExtractEntry(ZipArchive zip, string entryPath, string destination, long expectedBytes, string expectedSha256)
        {
            var entry = zip.GetEntry(entryPath) ?? throw new FileNotFoundException(entryPath);
            if (entry.Length != expectedBytes)
                throw new InvalidDataException("The runtime package entry has an invalid size");
            entry.ExtractToFile(destination, true);
            if (!FileMatches(destination, expectedBytes, expectedSha256))
                throw new InvalidDataException("The runtime package entry failed integrity validation");
        }

        private static bool FileMatches(string path, long expectedBytes, string expectedSha256)
        {
            try
            {
                FileInfo info = new FileInfo(path);
                if (!info.Exists || info.Length != expectedBytes)
                    return false;
                using FileStream stream = File.OpenRead(path);
                byte[] actual = SHA256.HashData(stream);
                return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256));
            }
            catch
            {
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }

        private static IntPtr ResolveOnnxRuntime(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (libraryName.StartsWith("onnxruntime", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(Paths.RiShade, libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
                    return handle;
            }
            return IntPtr.Zero;
        }

        private static bool EnsureModelFile(ModelSpec spec, CancellationToken token)
        {
            try
            {
                string path = ModelPathFor(spec);
                if (FileMatches(path, spec.Bytes, spec.Sha256))
                    return true;
                App.Logger.WriteLine(LOG_IDENT, $"Downloading {spec.Name}, about {spec.Bytes / 1048576}MB, one time only");
                Voidstrap.Utility.ResilientDownload.DownloadAsync(App.HttpClient, [spec.Url], path, spec.Bytes, token, spec.Sha256).GetAwaiter().GetResult();
                var downloaded = new FileInfo(path);
                if (!FileMatches(path, spec.Bytes, spec.Sha256))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{spec.Name} download integrity mismatch, got {downloaded.Length} bytes");
                    File.Delete(path);
                    return false;
                }
                App.Logger.WriteLine(LOG_IDENT, $"{spec.Name} downloaded and verified");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"{spec.Name} download failed: " + ex.Message);
                return false;
            }
        }

        private static bool CreateSession(ModelSpec spec)
        {
            try
            {
                var options = new SessionOptions();
                string ep = "DirectML";
                try
                {
                    options.AppendExecutionProvider_DML(0);
                }
                catch (Exception ex)
                {
                    ep = "CPU";
                    App.Logger.WriteLine(LOG_IDENT, "DirectML unavailable, AI depth will use the CPU: " + ex.Message);
                }
                _session = new InferenceSession(ModelPathFor(spec), options);
                _inputName = _session.InputMetadata.First().Key;
                App.Logger.WriteLine(LOG_IDENT, $"{spec.Name} session created on " + ep);
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeDepth::CreateSession", ex);
                return false;
            }
        }

        private static double ProbeSession(ModelSpec spec)
        {
            try
            {
                int ts = spec.TensorSize;
                var tensor = new DenseTensor<float>([1, 3, ts, ts]);
                var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
                using (var warm = _session!.Run(inputs)) { }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 3; i++)
                {
                    using var r = _session.Run(inputs);
                }
                sw.Stop();
                return sw.Elapsed.TotalMilliseconds / 3.0;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Model probe failed: " + ex.Message);
                return double.MaxValue;
            }
        }

        private static void RunLoop(CancellationToken token)
        {
            int ts = _model.TensorSize;
            var tensor = new DenseTensor<float>([1, 3, ts, ts]);
            var inputs = new NamedOnnxValue[1];
            var normalized = new float[ts * ts];
            var vbuf = new float[ts * ts];
            var smoothed = new float[ts * ts];
            var warped = new float[ts * ts];
            float lastAccX = 0f;
            float lastAccY = 0f;
            float[] meanC = [0.485f, 0.456f, 0.406f];
            float[] stdC = [0.229f, 0.224f, 0.225f];
            bool hasSmoothed = false;
            float emaMin = 0f;
            float emaMax = 1f;
            bool hasRange = false;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    _frameSignal.Wait(token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (RiShadeSettings.Current.AiQuality != _activeQuality)
                    break;
                byte[]? frame;
                float accX;
                float accY;
                lock (_frameLock)
                {
                    frame = _pendingFrame;
                    _pendingFrame = null;
                    accX = _pendingAccumX;
                    accY = _pendingAccumY;
                }
                if (frame == null || _session == null)
                    continue;

                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    float scale = (float)Size / ts;
                    for (int y = 0; y < ts; y++)
                    {
                        int sy = Math.Min((int)(y * scale), Size - 1);
                        int row = sy * Size;
                        for (int x = 0; x < ts; x++)
                        {
                            int sx = Math.Min((int)(x * scale), Size - 1);
                            int p = (row + sx) * 4;
                            float r = frame[p + 2] / 255f;
                            float g = frame[p + 1] / 255f;
                            float b = frame[p] / 255f;
                            if (_model.ImageNetNorm)
                            {
                                r = (r - meanC[0]) / stdC[0];
                                g = (g - meanC[1]) / stdC[1];
                                b = (b - meanC[2]) / stdC[2];
                            }
                            tensor[0, 0, y, x] = r;
                            tensor[0, 1, y, x] = g;
                            tensor[0, 2, y, x] = b;
                        }
                    }
                    inputs[0] = NamedOnnxValue.CreateFromTensor(_inputName, tensor);
                    float min = float.MaxValue;
                    float max = float.MinValue;
                    using (var results = _session.Run(inputs))
                    {
                        var output = results[0].AsEnumerable<float>();
                        int i = 0;
                        foreach (float v in output)
                        {
                            if (i >= normalized.Length)
                                break;
                            normalized[i++] = v;
                            if (v < min) min = v;
                            if (v > max) max = v;
                        }
                    }
                    if (!hasRange)
                    {
                        hasRange = true;
                        emaMin = min;
                        emaMax = max;
                    }
                    else
                    {
                        float span = Math.Max(emaMax - emaMin, 1e-4f);
                        if (Math.Abs(min - emaMin) > span * 0.35f || Math.Abs(max - emaMax) > span * 0.35f)
                        {
                            emaMin = min;
                            emaMax = max;
                        }
                        else
                        {
                            emaMin += (min - emaMin) * 0.08f;
                            emaMax += (max - emaMax) * 0.08f;
                        }
                    }
                    float range = Math.Max(emaMax - emaMin, 1e-4f);
                    if (!hasSmoothed)
                    {
                        hasSmoothed = true;
                        for (int i = 0; i < normalized.Length; i++)
                            smoothed[i] = Math.Clamp((normalized[i] - emaMin) / range, 0f, 1f);
                        lastAccX = accX;
                        lastAccY = accY;
                    }
                    else
                    {
                        for (int i = 0; i < normalized.Length; i++)
                            vbuf[i] = Math.Clamp((normalized[i] - emaMin) / range, 0f, 1f);
                        int wdx = (int)MathF.Round((accX - lastAccX) * ts / Size);
                        int wdy = (int)MathF.Round((accY - lastAccY) * ts / Size);
                        lastAccX = accX;
                        lastAccY = accY;
                        if (wdx != 0 || wdy != 0)
                        {
                            for (int y = 0; y < ts; y++)
                            {
                                int sy = y - wdy;
                                bool rowIn = sy >= 0 && sy < ts;
                                int dr = y * ts;
                                int sr = sy * ts;
                                for (int x = 0; x < ts; x++)
                                {
                                    int sx = x - wdx;
                                    warped[dr + x] = rowIn && sx >= 0 && sx < ts ? smoothed[sr + sx] : vbuf[dr + x];
                                }
                            }
                            (smoothed, warped) = (warped, smoothed);
                        }
                        for (int i = 0; i < normalized.Length; i++)
                        {
                            float d = Math.Abs(vbuf[i] - smoothed[i]);
                            float a = d > 0.15f ? 1f : 0.75f;
                            smoothed[i] += (vbuf[i] - smoothed[i]) * a;
                        }
                    }
                    lock (_frameLock)
                    {
                        _latestTagX = accX;
                        _latestTagY = accY;
                        if (ts == Size)
                        {
                            Array.Copy(smoothed, _latestDepth, smoothed.Length);
                        }
                        else
                        {
                            float us = (float)ts / Size;
                            for (int y = 0; y < Size; y++)
                            {
                                float fy = Math.Min(y * us, ts - 1.001f);
                                int y0 = (int)fy;
                                float wy = fy - y0;
                                int r0 = y0 * ts;
                                int r1 = Math.Min(y0 + 1, ts - 1) * ts;
                                int dr = y * Size;
                                for (int x = 0; x < Size; x++)
                                {
                                    float fx = Math.Min(x * us, ts - 1.001f);
                                    int x0 = (int)fx;
                                    float wx = fx - x0;
                                    int x1 = Math.Min(x0 + 1, ts - 1);
                                    float top = smoothed[r0 + x0] * (1f - wx) + smoothed[r0 + x1] * wx;
                                    float bot = smoothed[r1 + x0] * (1f - wx) + smoothed[r1 + x1] * wx;
                                    _latestDepth[dr + x] = top * (1f - wy) + bot * wy;
                                }
                            }
                        }
                    }
                    Interlocked.Increment(ref _depthVersion);
                    sw.Stop();
                    _inferCount++;
                    _inferMsTotal += sw.Elapsed.TotalMilliseconds;
                    if (_inferCount == 1 || _inferCount % 300 == 0)
                        App.Logger.WriteLine(LOG_IDENT, $"AI depth running, {_inferMsTotal / _inferCount:0.0} ms average over {_inferCount} frames");
                    int rest = _model.MinIntervalMs - (int)sw.Elapsed.TotalMilliseconds;
                    if (rest > 0 && !token.IsCancellationRequested)
						token.WaitHandle.WaitOne(rest);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("RiShadeDepth::Infer", ex);
                }
            }
        }
    }
}
