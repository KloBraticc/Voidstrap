using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Voidstrap.Integrations.RiShade
{
    public sealed record RiShadeDepthModelState(bool Enabled, bool Busy, int Percent, string? Error);

    public static class RiShadeDepthModels
    {
        public static event EventHandler<RiShadeDepthModelState>? StateChanged;

        private static RiShadeDepthModelState? _state;

        public static RiShadeDepthModelState State => _state ?? new RiShadeDepthModelState(Enabled, false, 0, null);

        private static void Publish(RiShadeDepthModelState state)
        {
            _state = state;
            StateChanged?.Invoke(null, state);
        }

        private const string LogIdent = "RiShadeDepthModels";
        private const string OffSetting = "Off";
        private const string OnSetting = "RiDepth";

        private sealed record Asset(string FileName, string Url, string Sha256, long Size, string? ZipEntry = null);

        private static readonly Asset OnnxRuntime = new(
            "onnxruntime.dll",
            "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/1.24.4/microsoft.ml.onnxruntime.directml.1.24.4.nupkg",
            "e7eedec6a6f26dc39dc948276a75ef6d2bee3fff944d874ceed0bbd3b97bff40",
            17328152,
            "runtimes/win-x64/native/onnxruntime.dll");

        private static readonly Asset DirectMl = new(
            "DirectML.dll",
            "https://api.nuget.org/v3-flatcontainer/microsoft.ai.directml/1.15.4/microsoft.ai.directml.1.15.4.nupkg",
            "9c9e6d822561c6c41b90e6994b3e8857cf1d66dbfb1e0c4c799c7c89b4e92da1",
            18527776,
            "bin/x64-win/DirectML.dll");

        private const string ModelFile = "ridepth-large.onnx";
        private const string FullPrecisionModelFile = "ridepth-large-fp32.onnx";

        private static readonly Asset? BuiltinModel = new(
            ModelFile,
            "https://github.com/KloBraticc/RiDepth/releases/download/large-1/ridepth-large.onnx",
            "82879e2700211f112914dd67ac9ab40582586491d1f92b7ece0005b4c9cd2b66",
            49783352);

        private const string ReleasesUrl = "https://api.github.com/repos/KloBraticc/RiDepth/releases?per_page=20";
        private const string ReleaseDownloadPrefix = "https://github.com/KloBraticc/RiDepth/releases/download/";
        private const string TagPrefix = "large-";
        private const long MaxModelBytes = 256L * 1024 * 1024;
        private static readonly TimeSpan ReleaseCheckInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan ReleaseCheckTimeout = TimeSpan.FromSeconds(15);
        private static Asset? _latest;
        private static RiShadeInterop.DepthHardware? _hardware;

        private const int MaxSegments = 8;
        private const long SegmentFloor = 4L * 1024 * 1024;
        private const int Attempts = 4;

        private static readonly HttpClient Client = CreateClient();
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly Dictionary<string, bool> VerifiedFiles = new(StringComparer.OrdinalIgnoreCase);
        private static CancellationTokenSource? _current;
        private static int _version;

        private static HttpClient CreateClient()
        {
            HttpClient client = new(new SocketsHttpHandler
            {
                MaxConnectionsPerServer = MaxSegments * 3,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = System.Net.DecompressionMethods.None
            })
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
            return client;
        }

        public static int Version => Volatile.Read(ref _version);

        private static readonly string[] RetiredFiles =
        [
            "ridepth-tiny.onnx",
            "depth-anything-v2-small.onnx",
            "depth-anything-v2-base.onnx",
            "depth-anything-v2-large.onnx",
            "depth-anything-v3-large\\model.onnx",
            "depth-anything-v3-large\\model.onnx_data"
        ];

        public static bool Enabled => !string.Equals(App.Settings.Prop.RiShadeDepthModel, OffSetting, StringComparison.OrdinalIgnoreCase);

        public static bool NeedsMigration => Enabled && !string.Equals(App.Settings.Prop.RiShadeDepthModel, OnSetting, StringComparison.Ordinal);

        public static bool NeedsUpdate =>
            Voidstrap.Utility.Platform.IsWindows && Enabled && !LowEndHardware && Files is Asset[] files && files.Any(asset => !IsInstalled(RiShadeManager.RiShadeDir, asset));

        public static int SizeMegabytes => ModelAsset is Asset model ? (int)Math.Round(model.Size / 1048576.0) : 0;

        public static bool ExactDepth => Enabled;

        private const ulong LowEndDedicatedMemory = 2UL * 1024 * 1024 * 1024;

        private static readonly int CpuCount = Environment.ProcessorCount;

        private static int _lowEndLogged;

        public static bool LowEndHardware
        {
            get
            {
                ProbeHardware();
                bool weakGpu = _hardware is RiShadeInterop.DepthHardware hardware && (hardware.Integrated != 0 || hardware.DedicatedMemory < LowEndDedicatedMemory);
                bool lowEnd = weakGpu || CpuCount <= 4;
                if (lowEnd && Interlocked.Exchange(ref _lowEndLogged, 1) == 0)
                    App.Logger.WriteLine(LogIdent, $"Low end hardware detected ({(weakGpu ? "weak GPU" : "few CPU cores")}, {CpuCount} threads), RiShade uses its lightweight path");
                return lowEnd;
            }
        }

        private static Asset? ModelAsset => Volatile.Read(ref _latest) ?? BuiltinModel;

        private static Asset[]? Files => ModelAsset is Asset model ? [OnnxRuntime, DirectMl, model] : null;

        public static void ProbeHardware() => _ = FullPrecision;

        private static bool FullPrecision
        {
            get
            {
                if (_hardware is not { HalfPrecision: >= 0 } && RiShadeManager.DepthBridgeAvailable)
                {
                    try
                    {
                        bool probed;
                        RiShadeInterop.DepthHardware hardware;
                        lock (RiShadeInterop.GraphicsSetupLock)
                            probed = RiShadeInterop.DepthBridgeProbe(RiShadeManager.RiShadeDir, out hardware);
                        if (probed)
                        {
                            _hardware = hardware;
                            string half = hardware.HalfPrecision switch { 1 => "supported", 0 => "unsupported", _ => "not checked yet" };
                            App.Logger.WriteLine(LogIdent, $"Depth GPU vendor 0x{hardware.VendorId:X4}, {hardware.DedicatedMemory / 1048576} MB dedicated memory, {(hardware.Integrated != 0 ? "integrated" : "discrete")}, half precision {half}");
                        }
                    }
                    catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                    {
                        App.Logger.WriteException(LogIdent + "::Probe", ex);
                    }
                }
                return _hardware is { HalfPrecision: 0 };
            }
        }

        public static async Task RefreshAsync(CancellationToken token = default)
        {
            if (!Voidstrap.Utility.Platform.IsWindows || !Enabled)
                return;

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(ReleaseCheckTimeout);
            try
            {
                List<Voidstrap.Models.APIs.GitHub.GithubRelease>? releases = await Voidstrap.Utility.GitHubCache.GetJsonAsync<List<Voidstrap.Models.APIs.GitHub.GithubRelease>>(ReleasesUrl, ReleaseCheckInterval, timeout.Token).ConfigureAwait(false);
                Asset? latest = releases?
                    .Where(release => !release.Draft && !release.Prerelease && release.TagName?.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) == true)
                    .OrderByDescending(release => DateTimeOffset.TryParse(release.PublishedAt, out DateTimeOffset published) ? published : DateTimeOffset.MinValue)
                    .Select(ToAsset)
                    .FirstOrDefault(asset => asset != null);
                if (latest == null)
                    return;
                Asset? previous = Interlocked.Exchange(ref _latest, latest);
                if (previous == null || !previous.Sha256.Equals(latest.Sha256, StringComparison.OrdinalIgnoreCase))
                    App.Logger.WriteLine(LogIdent, "Newest RiDepth release is " + latest.Url);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                App.Logger.WriteLine(LogIdent, "Checking for a newer RiDepth release timed out, the known model stays in use");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.Logger.WriteException(LogIdent + "::RefreshAsync", ex);
            }
        }

        private static Asset? ToAsset(Voidstrap.Models.APIs.GitHub.GithubRelease release) =>
            (FullPrecision ? ToAsset(release, FullPrecisionModelFile) : null) ?? ToAsset(release, ModelFile);

        private static Asset? ToAsset(Voidstrap.Models.APIs.GitHub.GithubRelease release, string fileName)
        {
            Voidstrap.Models.APIs.GitHub.GithubReleaseAsset? file = release.Assets?.FirstOrDefault(asset => string.Equals(asset.Name, fileName, StringComparison.Ordinal));
            string digest = file?.Digest ?? "";
            if (file == null
                || file.Size <= 0
                || file.Size > MaxModelBytes
                || file.BrowserDownloadUrl?.StartsWith(ReleaseDownloadPrefix, StringComparison.Ordinal) != true
                || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                || digest.Length != 71
                || !digest[7..].All(Uri.IsHexDigit))
                return null;
            return new Asset(fileName, file.BrowserDownloadUrl, digest[7..], file.Size);
        }

        public static string? ActiveModelFile
        {
            get
            {
                if (!Voidstrap.Utility.Platform.IsWindows || !Enabled || LowEndHardware || Files is not Asset[] files)
                    return null;
                string directory = RiShadeManager.RiShadeDir;
                foreach (Asset asset in files)
                {
                    FileInfo file = new(Path.Combine(directory, asset.FileName));
                    if (!file.Exists || file.Length != asset.Size)
                        return null;
                }
                return files[^1].FileName;
            }
        }

        public static async Task<(bool Success, string Message)> SetEnabledAsync(bool enabled, IProgress<double>? progress = null, CancellationToken token = default)
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return (false, "AI depth is only available on Windows");

            CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(token);
            Interlocked.Exchange(ref _current, source)?.Cancel();
            App.Settings.Prop.RiShadeDepthModel = enabled ? OnSetting : OffSetting;
            App.Settings.SaveDeferred();
            Publish(new RiShadeDepthModelState(enabled, enabled, 0, null));

            try
            {
                await Gate.WaitAsync(source.Token);
            }
            catch (OperationCanceledException)
            {
                Interlocked.CompareExchange(ref _current, null, source);
                source.Dispose();
                return (false, string.Empty);
            }

            try
            {
                string directory = RiShadeManager.RiShadeDir;
                Directory.CreateDirectory(directory);

                if (!enabled)
                {
                    RemoveUnused(directory, []);
                    App.Logger.WriteLine(LogIdent, "AI depth turned off, scene depth estimation is used");
                    Interlocked.Increment(ref _version);
                    Publish(new RiShadeDepthModelState(false, false, 0, null));
                    return (true, string.Empty);
                }

                await RefreshAsync(source.Token);
                Asset model = ModelAsset ?? throw new InvalidOperationException("No RiDepth release is available yet");
                List<Asset> runtime = new[] { OnnxRuntime, DirectMl }.Where(asset => !IsInstalled(directory, asset)).ToList();
                long total = runtime.Sum(asset => asset.Size) + (IsInstalled(directory, model) ? 0 : model.Size);
                long received = 0;
                int lastPercent = -1;
                void Advance(long bytes)
                {
                    long now = Interlocked.Add(ref received, bytes);
                    int percent = total > 0 ? (int)Math.Min(99, now * 100 / total) : 99;
                    if (Interlocked.Exchange(ref lastPercent, percent) == percent)
                        return;
                    progress?.Report(percent / 100.0);
                    Publish(new RiShadeDepthModelState(true, true, percent, null));
                }

                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                await Task.WhenAll(runtime.Select(asset => DownloadAsync(directory, asset, Advance, source.Token)));
                if (runtime.Count > 0 && FullPrecision && model.FileName != FullPrecisionModelFile)
                {
                    await RefreshAsync(source.Token);
                    model = ModelAsset ?? model;
                }
                bool modelMissing = !IsInstalled(directory, model);
                if (modelMissing)
                    await DownloadAsync(directory, model, Advance, source.Token);
                if (runtime.Count > 0 || modelMissing)
                    App.Logger.WriteLine(LogIdent, $"Downloaded the depth files in {watch.Elapsed.TotalSeconds:F1}s");

                RemoveUnused(directory, [model]);
                progress?.Report(1.0);
                App.Logger.WriteLine(LogIdent, "RiDepth " + model.FileName + " is installed");
                Interlocked.Increment(ref _version);
                Publish(new RiShadeDepthModelState(true, false, 100, null));
                return (true, string.Empty);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested && !token.IsCancellationRequested)
            {
                App.Logger.WriteLine(LogIdent, "The depth download was replaced by a newer choice");
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LogIdent + "::SetEnabledAsync", ex);
                Publish(new RiShadeDepthModelState(true, false, 0, ex.Message));
                return (false, ex.Message);
            }
            finally
            {
                Interlocked.CompareExchange(ref _current, null, source);
                source.Dispose();
                Gate.Release();
            }
        }

        private static bool IsInstalled(string directory, Asset asset)
        {
            string path = Path.Combine(directory, asset.FileName);
            FileInfo file = new(path);
            if (!file.Exists || file.Length != asset.Size)
                return false;
            string key = path + "|" + file.LastWriteTimeUtc.Ticks;
            lock (VerifiedFiles)
            {
                if (VerifiedFiles.TryGetValue(key, out bool known))
                    return known;
            }
            bool valid;
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                valid = Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
            lock (VerifiedFiles)
                VerifiedFiles[key] = valid;
            return valid;
        }

        private static void RemoveUnused(string directory, Asset[] keep)
        {
            IEnumerable<string> unused = new[] { ModelFile, FullPrecisionModelFile }.Concat(RetiredFiles)
                .Where(name => !keep.Any(file => string.Equals(file.FileName, name, StringComparison.OrdinalIgnoreCase)));
            foreach (string name in unused)
            {
                string path = Path.Combine(directory, name);
                TryDelete(path);
                string? folder = Path.GetDirectoryName(path);
                if (folder != null && !string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.GetFullPath(directory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                            Directory.Delete(folder);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Logger.WriteLine(LogIdent, "Could not remove " + Path.GetFileName(path) + " yet, it is in use: " + ex.Message);
            }
        }

        private static async Task DownloadAsync(string directory, Asset asset, Action<long> advance, CancellationToken token)
        {
            string target = Path.Combine(directory, asset.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string raw = target + ".download";
            string partial = target + ".part";
            App.Logger.WriteLine(LogIdent, "Downloading " + asset.FileName);
            try
            {
                long start = 0;
                long length = asset.Size;
                bool deflated = false;
                if (asset.ZipEntry != null)
                    (start, length, deflated) = await LocateEntryAsync(asset, token);

                await DownloadRangeAsync(asset.Url, start, length, raw, advance, token);

                if (asset.ZipEntry != null)
                {
                    await using FileStream compressed = new(raw, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, true);
                    await using FileStream output = new(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                    if (deflated)
                    {
                        await using DeflateStream inflate = new(compressed, CompressionMode.Decompress);
                        await inflate.CopyToAsync(output, 1 << 16, token);
                    }
                    else
                    {
                        await compressed.CopyToAsync(output, 1 << 16, token);
                    }
                }
                else
                {
                    File.Move(raw, partial, true);
                }

                string hash;
                await using (FileStream check = new(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, true))
                    hash = Convert.ToHexString(await SHA256.HashDataAsync(check, token));
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(asset.FileName + " did not match its expected checksum");

                File.Move(partial, target, true);
            }
            finally
            {
                TryDelete(raw);
                TryDelete(partial);
            }
        }

        private static async Task DownloadRangeAsync(string url, long start, long length, string path, Action<long> advance, CancellationToken token)
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous, length);
            int segments = (int)Math.Clamp(length / SegmentFloor, 1, MaxSegments);
            long size = (length + segments - 1) / segments;
            List<Task> parts = new();
            for (int index = 0; index < segments; index++)
            {
                long offset = index * size;
                long count = Math.Min(size, length - offset);
                if (count > 0)
                    parts.Add(DownloadSegmentAsync(url, start, offset, count, handle, advance, token));
            }
            await Task.WhenAll(parts);
        }

        private static async Task DownloadSegmentAsync(string url, long origin, long offset, long count, SafeFileHandle handle, Action<long> advance, CancellationToken token)
        {
            long written = 0;
            byte[] buffer = new byte[1 << 17];
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await using Stream stream = await OpenAsync(url, new RangeHeaderValue(origin + offset + written, origin + offset + count - 1), token);
                    while (written < count)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count - written)), token);
                        if (read == 0)
                            throw new IOException("The connection closed early");
                        await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), offset + written, token);
                        written += read;
                        advance(read);
                    }
                    return;
                }
                catch (Exception ex) when (attempt < Attempts && !token.IsCancellationRequested && ex is IOException or HttpRequestException)
                {
                    App.Logger.WriteLine(LogIdent, $"A download part failed, resuming it (attempt {attempt + 1} of {Attempts}): {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), token);
                }
            }
        }

        private static async Task<Stream> OpenAsync(string url, RangeHeaderValue range, CancellationToken token)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Range = range;
            HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                string reason = response.IsSuccessStatusCode
                    ? new Uri(url).Host + " does not support partial downloads"
                    : $"{new Uri(url).Host} answered {(int)response.StatusCode}";
                response.Dispose();
                throw new HttpRequestException(reason);
            }
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(token));
        }

        private static async Task<byte[]> ReadRangeAsync(string url, long from, long length, CancellationToken token)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await using Stream stream = await OpenAsync(url, new RangeHeaderValue(from, from + length - 1), token);
                    byte[] buffer = new byte[length];
                    await stream.ReadExactlyAsync(buffer, token);
                    return buffer;
                }
                catch (Exception ex) when (attempt < Attempts && !token.IsCancellationRequested && ex is IOException or HttpRequestException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), token);
                }
            }
        }

        private static async Task<(long Start, long Length, bool Deflated)> LocateEntryAsync(Asset asset, CancellationToken token)
        {
            long length;
            using (HttpRequestMessage head = new(HttpMethod.Head, asset.Url))
            using (HttpResponseMessage response = await Client.SendAsync(head, token))
            {
                response.EnsureSuccessStatusCode();
                length = response.Content.Headers.ContentLength ?? throw new InvalidDataException("The package size is unknown");
            }

            int tailLength = (int)Math.Min(length, 65536 + 22);
            byte[] tail = await ReadRangeAsync(asset.Url, length - tailLength, tailLength, token);
            int end = -1;
            for (int i = tail.Length - 22; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054b50)
                {
                    end = i;
                    break;
                }
            }
            if (end < 0)
                throw new InvalidDataException("The package directory was not found");

            uint directorySize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 12));
            uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
            if (directoryOffset == uint.MaxValue || directorySize > 16 * 1024 * 1024)
                throw new InvalidDataException("The package directory is not supported");

            byte[] directory = await ReadRangeAsync(asset.Url, directoryOffset, directorySize, token);
            byte[] wanted = Encoding.UTF8.GetBytes(asset.ZipEntry!);
            int position = 0;
            while (position + 46 <= directory.Length && BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position)) == 0x02014b50)
            {
                ushort method = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 10));
                uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position + 20));
                ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 28));
                ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 30));
                ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(position + 32));
                uint localOffset = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(position + 42));
                if (directory.AsSpan(position + 46, nameLength).SequenceEqual(wanted))
                {
                    if (method != 8 && method != 0)
                        throw new InvalidDataException("The package entry uses an unsupported compression");
                    byte[] local = await ReadRangeAsync(asset.Url, localOffset, 30, token);
                    long dataStart = localOffset + 30 + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(local.AsSpan(28));
                    return (dataStart, compressed, method == 8);
                }
                position += 46 + nameLength + extraLength + commentLength;
            }
            throw new InvalidDataException(asset.ZipEntry + " was not found in the package");
        }

        private sealed class ResponseStream : Stream
        {
            private readonly HttpResponseMessage _response;
            private readonly Stream _inner;

            public ResponseStream(HttpResponseMessage response, Stream inner)
            {
                _response = response;
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    _response.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
