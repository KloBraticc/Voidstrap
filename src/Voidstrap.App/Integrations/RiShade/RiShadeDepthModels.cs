using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.RiShade
{
    public enum RiShadeDepthModel
    {
        Off,
        Small,
        Base,
        Large
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

        private static readonly Dictionary<RiShadeDepthModel, Asset> Models = new()
        {
            [RiShadeDepthModel.Small] = new(
                "depth-anything-v2-small.onnx",
                "https://huggingface.co/onnx-community/depth-anything-v2-small/resolve/4472b7362082ad9968fee890ca0f1e5aca36b93d/onnx/model_fp16.onnx",
                "2df6223f206b5164e21f664ace61dabeb9bb6a49b8b5a3e00510b4807d0f5b04",
                49642442),
            [RiShadeDepthModel.Base] = new(
                "depth-anything-v2-base.onnx",
                "https://huggingface.co/onnx-community/depth-anything-v2-base/resolve/dd4557d492cd7b563738ac8d9ccff9094620983c/onnx/model_fp16.onnx",
                "822e6c12f36cd46753dcf229ce87199310533de2d533a527756b8ef30a76d158",
                194570657),
            [RiShadeDepthModel.Large] = new(
                "depth-anything-v2-large.onnx",
                "https://huggingface.co/onnx-community/depth-anything-v2-large/resolve/1fa1591c7b080e98da9655827c3a33a3972a4a83/onnx/model_fp16.onnx",
                "8eefc7afb877b10d413c8e1024b012073c6e4f62735d39555b1083fee3aabf0a",
                668656405)
        };

        private static readonly HttpClient Client = CreateClient();
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static HttpClient CreateClient()
        {
            HttpClient client = new() { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
            return client;
        }

        public static RiShadeDepthModel Selected
        {
            get => Enum.TryParse(App.Settings.Prop.RiShadeDepthModel, true, out RiShadeDepthModel model) ? model : RiShadeDepthModel.Off;
        }

        public static int SizeMegabytes(RiShadeDepthModel model) =>
            Models.TryGetValue(model, out Asset? asset) ? (int)Math.Round(asset.Size / 1048576.0) : 0;

        public static string? ActiveModelFile
        {
            get
            {
                if (!Voidstrap.Utility.Platform.IsWindows || !Models.TryGetValue(Selected, out Asset? model))
                    return null;
                string directory = RiShadeManager.RiShadeDir;
                foreach (Asset asset in new[] { OnnxRuntime, DirectMl, model })
                {
                    if (!File.Exists(Path.Combine(directory, asset.FileName)))
                        return null;
                }
                return model.FileName;
            }
        }

        public static async Task<(bool Success, string Message)> SelectAsync(RiShadeDepthModel model, IProgress<double>? progress = null, CancellationToken token = default)
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return (false, "AI depth is only available on Windows");

            await Gate.WaitAsync(token);
            try
            {
                App.Settings.Prop.RiShadeDepthModel = model.ToString();
                App.Settings.SaveDeferred();
                Publish(new RiShadeDepthModelState(model, model != RiShadeDepthModel.Off, 0, null));
                string directory = RiShadeManager.RiShadeDir;
                Directory.CreateDirectory(directory);

                if (model == RiShadeDepthModel.Off)
                {
                    RemoveUnused(directory, null);
                    App.Logger.WriteLine(LogIdent, "AI depth turned off, scene depth estimation is used");
                    Publish(new RiShadeDepthModelState(model, false, 0, null));
                    return (true, string.Empty);
                }

                Asset chosen = Models[model];
                Asset[] needed = { OnnxRuntime, DirectMl, chosen };
                long total = 0;
                foreach (Asset asset in needed)
                {
                    if (!IsInstalled(directory, asset))
                        total += asset.Size;
                }
                long done = 0;
                int lastPercent = 0;
                void Report(double value)
                {
                    int percent = (int)(value * 100);
                    progress?.Report(value);
                    if (percent == lastPercent)
                        return;
                    lastPercent = percent;
                    Publish(new RiShadeDepthModelState(model, true, percent, null));
                }
                foreach (Asset asset in needed)
                {
                    if (IsInstalled(directory, asset))
                        continue;
                    long before = done;
                    Progress<long> bytes = new(count => Report(total > 0 ? Math.Min(1.0, (before + count) / (double)total) : 1.0));
                    await DownloadAsync(directory, asset, bytes, token);
                    done += asset.Size;
                }
                RemoveUnused(directory, chosen);
                progress?.Report(1.0);
                App.Logger.WriteLine(LogIdent, $"AI depth model {model} is installed");
                Publish(new RiShadeDepthModelState(model, false, 100, null));
                return (true, string.Empty);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LogIdent + "::SelectAsync", ex);
                Publish(new RiShadeDepthModelState(model, false, 0, ex.Message));
                return (false, ex.Message);
            }
            finally
            {
                Gate.Release();
            }
        }

        private static readonly Dictionary<string, bool> VerifiedFiles = new(StringComparer.OrdinalIgnoreCase);

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
            using (FileStream stream = File.OpenRead(path))
                valid = Convert.ToHexString(SHA256.HashData(stream)).Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase);
            lock (VerifiedFiles)
                VerifiedFiles[key] = valid;
            return valid;
        }

        private static void RemoveUnused(string directory, Asset? keep)
        {
            foreach (Asset model in Models.Values)
            {
                if (ReferenceEquals(model, keep))
                    continue;
                TryDelete(Path.Combine(directory, model.FileName));
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
                App.Logger.WriteLine(LogIdent, "Could not remove " + Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        private static async Task DownloadAsync(string directory, Asset asset, IProgress<long> progress, CancellationToken token)
        {
            string target = Path.Combine(directory, asset.FileName);
            string partial = target + ".part";
            App.Logger.WriteLine(LogIdent, "Downloading " + asset.FileName);
            try
            {
                await using (FileStream output = new(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
                {
                    if (asset.ZipEntry == null)
                        await CopyAsync(await OpenAsync(asset.Url, null, token), output, progress, token);
                    else
                        await ExtractEntryAsync(asset, output, progress, token);
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
                TryDelete(partial);
            }
        }

        private static async Task<Stream> OpenAsync(string url, RangeHeaderValue? range, CancellationToken token)
        {
            HttpRequestMessage request = new(HttpMethod.Get, url);
            if (range != null)
                request.Headers.Range = range;
            HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                request.Dispose();
                throw new HttpRequestException($"{new Uri(url).Host} answered {(int)response.StatusCode}");
            }
            if (range != null && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
            {
                response.Dispose();
                request.Dispose();
                throw new HttpRequestException(new Uri(url).Host + " does not support partial downloads");
            }
            return await response.Content.ReadAsStreamAsync(token);
        }

        private static async Task<byte[]> ReadRangeAsync(string url, long from, long length, CancellationToken token)
        {
            await using Stream stream = await OpenAsync(url, new RangeHeaderValue(from, from + length - 1), token);
            byte[] buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, token);
            return buffer;
        }

        private static async Task CopyAsync(Stream source, Stream destination, IProgress<long> progress, CancellationToken token)
        {
            await using (source)
            {
                byte[] buffer = new byte[1 << 16];
                long copied = 0;
                long nextReport = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    copied += read;
                    if (copied >= nextReport)
                    {
                        progress.Report(copied);
                        nextReport = copied + (1 << 20);
                    }
                }
                progress.Report(copied);
            }
        }

        private static async Task ExtractEntryAsync(Asset asset, Stream output, IProgress<long> progress, CancellationToken token)
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
                    Stream data = await OpenAsync(asset.Url, new RangeHeaderValue(dataStart, dataStart + compressed - 1), token);
                    Stream body = method == 8 ? new DeflateStream(data, CompressionMode.Decompress) : data;
                    await CopyAsync(body, output, progress, token);
                    return;
                }
                position += 46 + nameLength + extraLength + commentLength;
            }
            throw new InvalidDataException(asset.ZipEntry + " was not found in the package");
        }
    }
}
