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
    public enum RiShadeDepthModel
    {
        Off,
        Small,
        Base,
        Large,
        LargeV3
    }

    public sealed record RiShadeDepthModelState(RiShadeDepthModel Model, bool Busy, int Percent, string? Error);

    public static class RiShadeDepthModels
    {
        public static event EventHandler<RiShadeDepthModelState>? StateChanged;

        private static RiShadeDepthModelState? _state;

        public static RiShadeDepthModelState State => _state ?? new RiShadeDepthModelState(Selected, false, 0, null);

        private static void Publish(RiShadeDepthModelState state)
        {
            _state = state;
            StateChanged?.Invoke(null, state);
        }

        private const string LogIdent = "RiShadeDepthModels";

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

        private static readonly Dictionary<RiShadeDepthModel, Asset[]> Models = new()
        {
            [RiShadeDepthModel.Small] = [new(
                "depth-anything-v2-small.onnx",
                "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/4472b7362082ad9968fee890ca0f1e5aca36b93d/onnx/model_fp16.onnx",
                "2df6223f206b5164e21f664ace61dabeb9bb6a49b8b5a3e00510b4807d0f5b04",
                49642442)],
            [RiShadeDepthModel.Base] = [new(
                "depth-anything-v2-base.onnx",
                "https://huggingface.co/onnx-community/depth-anything-v2-base/resolve/dd4557d492cd7b563738ac8d9ccff9094620983c/onnx/model_fp16.onnx",
                "822e6c12f36cd46753dcf229ce87199310533de2d533a527756b8ef30a76d158",
                194570657)],
            [RiShadeDepthModel.Large] = [new(
                "depth-anything-v2-large.onnx",
                "https://huggingface.co/onnx-community/depth-anything-v2-large/resolve/1fa1591c7b080e98da9655827c3a33a3972a4a83/onnx/model_fp16.onnx",
                "8eefc7afb877b10d413c8e1024b012073c6e4f62735d39555b1083fee3aabf0a",
                668656405)],
            [RiShadeDepthModel.LargeV3] =
            [
                new(
                    "depth-anything-v3-large\\model.onnx",
                    "https://huggingface.co/onnx-community/depth-anything-v3-large/resolve/d72462fef85c16988145b33cb5f9a3488ce93a58/onnx/model.onnx",
                    "2c02d70b301b4b51c357e7f04c371e27b5d4a5520a0ef11b8c657627e86b4a7e",
                    1093118),
                new(
                    "depth-anything-v3-large\\model.onnx_data",
                    "https://huggingface.co/onnx-community/depth-anything-v3-large/resolve/d72462fef85c16988145b33cb5f9a3488ce93a58/onnx/model.onnx_data",
                    "3aee647d45b208cbff14f4a738737a4bd9f9e5fbce2c48e7a6db311ea807060c",
                    1381741568)
            ]
        };

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

        public static RiShadeDepthModel Selected =>
            Enum.TryParse(App.Settings.Prop.RiShadeDepthModel, true, out RiShadeDepthModel model) ? model : RiShadeDepthModel.Off;

        public static int SizeMegabytes(RiShadeDepthModel model) =>
            Models.TryGetValue(model, out Asset[]? files) ? (int)Math.Round(files.Sum(file => file.Size) / 1048576.0) : 0;

        public static string? ActiveModelFile
        {
            get
            {
                if (!Voidstrap.Utility.Platform.IsWindows || !Models.TryGetValue(Selected, out Asset[]? files))
                    return null;
                string directory = RiShadeManager.RiShadeDir;
                foreach (Asset asset in files.Prepend(DirectMl).Prepend(OnnxRuntime))
                {
                    FileInfo file = new(Path.Combine(directory, asset.FileName));
                    if (!file.Exists || file.Length != asset.Size)
                        return null;
                }
                return files[0].FileName;
            }
        }

        public static async Task<(bool Success, string Message)> SelectAsync(RiShadeDepthModel model, IProgress<double>? progress = null, CancellationToken token = default)
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return (false, "AI depth is only available on Windows");

            CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(token);
            Interlocked.Exchange(ref _current, source)?.Cancel();
            App.Settings.Prop.RiShadeDepthModel = model.ToString();
            App.Settings.SaveDeferred();
            Publish(new RiShadeDepthModelState(model, model != RiShadeDepthModel.Off, 0, null));

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

                if (model == RiShadeDepthModel.Off)
                {
                    RemoveUnused(directory, null);
                    App.Logger.WriteLine(LogIdent, "AI depth turned off, scene depth estimation is used");
                    Interlocked.Increment(ref _version);
                    Publish(new RiShadeDepthModelState(model, false, 0, null));
                    return (true, string.Empty);
                }

                Asset[] chosen = Models[model];
                List<Asset> missing = chosen.Prepend(DirectMl).Prepend(OnnxRuntime).Where(asset => !IsInstalled(directory, asset)).ToList();
                long total = missing.Sum(asset => asset.Size);
                long received = 0;
                int lastPercent = -1;
                void Advance(long bytes)
                {
                    long now = Interlocked.Add(ref received, bytes);
                    int percent = total > 0 ? (int)Math.Min(99, now * 100 / total) : 99;
                    if (Interlocked.Exchange(ref lastPercent, percent) == percent)
                        return;
                    progress?.Report(percent / 100.0);
                    Publish(new RiShadeDepthModelState(model, true, percent, null));
                }

                System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
                await Task.WhenAll(missing.Select(asset => DownloadAsync(directory, asset, Advance, source.Token)));
                if (missing.Count > 0)
                    App.Logger.WriteLine(LogIdent, $"Downloaded {total / 1048576.0:F0} MB of depth files in {watch.Elapsed.TotalSeconds:F1}s");

                RemoveUnused(directory, chosen);
                progress?.Report(1.0);
                App.Logger.WriteLine(LogIdent, $"AI depth model {model} is installed");
                Interlocked.Increment(ref _version);
                Publish(new RiShadeDepthModelState(model, false, 100, null));
                return (true, string.Empty);
            }
            catch (OperationCanceledException) when (source.IsCancellationRequested && !token.IsCancellationRequested)
            {
                App.Logger.WriteLine(LogIdent, $"The {model} download was replaced by a newer choice");
                return (false, string.Empty);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LogIdent + "::SelectAsync", ex);
                Publish(new RiShadeDepthModelState(model, false, 0, ex.Message));
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

        private static void RemoveUnused(string directory, Asset[]? keep)
        {
            foreach (Asset[] files in Models.Values)
            {
                if (ReferenceEquals(files, keep))
                    continue;
                foreach (Asset file in files)
                {
                    string path = Path.Combine(directory, file.FileName);
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
