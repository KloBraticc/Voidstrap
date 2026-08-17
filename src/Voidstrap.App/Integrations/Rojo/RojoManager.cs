using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Rojo
{
    public static class RojoManager
    {
        private const string LogTag = "Rojo";
        private const int MaxCommandOutputChars = 1_000_000;
        private const long MaxReleaseBytes = 536870912L;
        private const int MaxReleaseMetadataBytes = 4194304;

        private static readonly HttpClient Http = CreateClient();
        private static readonly SemaphoreSlim InstallGate = new SemaphoreSlim(1, 1);

        private static readonly string RojoDir = Paths.Rojo;

        public static string RojoExe => Path.Combine(RojoDir, "rojo.exe");

        private static readonly object _serveLock = new object();
        private static Process? _serveProcess;

        public static bool IsInstalled => File.Exists(RojoExe);

        public static bool IsServing
        {
            get
            {
                lock (_serveLock)
                {
                    try
                    {
                        return _serveProcess != null && !_serveProcess.HasExited;
                    }
                    catch
                    {
                        return false;
                    }
                }
            }
        }

        private static HttpClient CreateClient()
        {
            var http = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromMinutes(10));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
            return http;
        }

        private static string VersionFile => Path.Combine(RojoDir, "version.txt");

        public static async Task<bool> EnsureInstalledAsync(Action<string, double, bool>? progress, CancellationToken ct)
        {
            if (IsInstalled)
            {
                await TryUpdateAsync(progress, ct);
                return true;
            }

            Directory.CreateDirectory(RojoDir);
            progress?.Invoke("Finding the latest Rojo release", -1.0, true);

            var (tag, zipUrl, digest, size) = await ResolveLatestAsync(ct);
            if (string.IsNullOrEmpty(zipUrl))
                throw new InvalidOperationException("Could not find a Windows Rojo release asset.");

            await InstallReleaseAsync(tag, zipUrl, digest, size, "Downloading Rojo", progress, ct);

            if (!IsInstalled)
                throw new InvalidOperationException("rojo.exe was not found in the downloaded archive.");

            App.Logger.WriteLine(LogTag, "Rojo installed at " + RojoExe);
            return true;
        }

        public static void AutoUpdate()
        {
            if (!App.Settings.Prop.RojoEnabled || !IsInstalled)
                return;
            _ = TryUpdateAsync(null, CancellationToken.None);
        }

        private static async Task TryUpdateAsync(Action<string, double, bool>? progress, CancellationToken ct)
        {
            try
            {
                if (IsServing)
                    return;

                var (tag, zipUrl, digest, size) = await ResolveLatestAsync(ct);
                if (string.IsNullOrEmpty(tag) || string.IsNullOrEmpty(zipUrl))
                    return;

                string current = File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : "";
                if (current == tag)
                    return;

                App.Logger.WriteLine(LogTag, $"Updating Rojo from {(current.Length > 0 ? current : "unknown")} to {tag}");
                await InstallReleaseAsync(tag, zipUrl, digest, size, "Updating Rojo", progress, ct);
                App.Logger.WriteLine(LogTag, "Rojo updated to " + tag);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogTag, "Update check failed: " + ex.Message);
            }
        }

        private static async Task InstallReleaseAsync(string tag, string zipUrl, string digest, long size, string label, Action<string, double, bool>? progress, CancellationToken ct)
        {
            await InstallGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsInstalled && File.Exists(VersionFile) && string.Equals(File.ReadAllText(VersionFile).Trim(), tag, StringComparison.Ordinal))
                    return;
                Directory.CreateDirectory(RojoDir);
                string suffix = Guid.NewGuid().ToString("N");
                string zipPath = Path.Combine(RojoDir, "rojo." + suffix + ".zip");
                string stagedExe = Path.Combine(RojoDir, "rojo." + suffix + ".exe");
                try
                {
                    await DownloadAsync(zipUrl, zipPath, digest, size, label, progress, ct).ConfigureAwait(false);
                    progress?.Invoke("Extracting Rojo", -1.0, true);
                    ExtractRojoExe(zipPath, stagedExe);
                    if (new FileInfo(stagedExe).Length <= 0)
                        throw new InvalidDataException("The Rojo executable is empty");
                    File.Move(stagedExe, RojoExe, true);
                    File.WriteAllText(VersionFile, tag);
                }
                finally
                {
                    TryDelete(zipPath);
                    TryDelete(stagedExe);
                }
            }
            finally
            {
                InstallGate.Release();
            }
        }

        private static async Task<(string Tag, string Url, string Digest, long Size)> ResolveLatestAsync(CancellationToken ct)
        {
            using HttpResponseMessage response = await Http.GetAsync("https://api.github.com/repos/rojo-rbx/rojo/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            string metadata = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, MaxReleaseMetadataBytes, ct).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(metadata);

            string tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement tagEl) && tagEl.ValueKind == JsonValueKind.String
                ? (tagEl.GetString() ?? "")
                : "";

            (string Url, string Digest, long Size)? fallback = null;
            foreach (JsonElement asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (name.IndexOf("win", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string? url = asset.GetProperty("browser_download_url").GetString();
                string digest = asset.TryGetProperty("digest", out JsonElement digestElement) ? digestElement.GetString() ?? "" : "";
                long size = asset.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize) ? parsedSize : 0;
                string state = asset.TryGetProperty("state", out JsonElement stateElement) ? stateElement.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url) || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71 ||
                    size <= 0 || size > MaxReleaseBytes || !string.Equals(state, "uploaded", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(url, UriKind.Absolute, out Uri? assetUri) || assetUri.Scheme != Uri.UriSchemeHttps || !assetUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (name.IndexOf("x86_64", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("win64", StringComparison.OrdinalIgnoreCase) >= 0)
                    return (tag, url, digest, size);

                fallback ??= (url, digest, size);
            }
            return fallback.HasValue ? (tag, fallback.Value.Url, fallback.Value.Digest, fallback.Value.Size) : (tag, "", "", 0);
        }

        private static void ExtractRojoExe(string zipPath, string destination)
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
                e => e.Name.Equals("rojo.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                throw new InvalidOperationException("rojo.exe missing from archive.");
            if (entry.Length <= 0 || entry.Length > MaxReleaseBytes)
                throw new InvalidDataException("rojo.exe has an invalid size");
            entry.ExtractToFile(destination, true);
        }

        private static async Task DownloadAsync(string url, string outputPath, string digest, long size, string label, Action<string, double, bool>? progress, CancellationToken ct)
        {
            await Voidstrap.Utility.ResilientDownload.DownloadAsync(Http, [url], outputPath, size, ct, digest,
                progress: (read, total) =>
                {
                    double fraction = total is > 0 ? (double)read / total.Value : -1.0;
                    progress?.Invoke(fraction >= 0 ? $"{label} {fraction * 100:0}%" : label, fraction, true);
                });
        }

        public static async Task UninstallAsync()
        {
            await InstallGate.WaitAsync().ConfigureAwait(false);
            try
            {
                StopServe();
                await Task.Delay(150).ConfigureAwait(false);

                for (int i = 0; i < 5; i++)
                {
                    try
                    {
                        if (Directory.Exists(RojoDir))
                            Directory.Delete(RojoDir, true);
                        return;
                    }
                    catch
                    {
                        await Task.Delay(300).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                InstallGate.Release();
            }
        }

        public static async Task<(bool Ok, string Output)> RunAsync(string arguments, string? workingDir, CancellationToken ct)
        {
            if (!IsInstalled)
                return (false, "Rojo is not installed.");

            var psi = new ProcessStartInfo
            {
                FileName = RojoExe,
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? RojoDir : workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = new Process { StartInfo = psi };
            var sb = new StringBuilder();
            var outputLock = new object();

            void OnOutput(object sender, DataReceivedEventArgs e)
            {
                if (e.Data == null)
                    return;
                lock (outputLock)
                {
                    if (sb.Length >= MaxCommandOutputChars)
                        return;
                    int remaining = MaxCommandOutputChars - sb.Length;
                    string value = e.Data.Length <= remaining ? e.Data : e.Data.Substring(0, remaining);
                    sb.AppendLine(value);
                }
            }

            proc.OutputDataReceived += OnOutput;
            proc.ErrorDataReceived += OnOutput;
            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(ct);
                lock (outputLock)
                {
                    return (proc.ExitCode == 0, sb.ToString().Trim());
                }
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(true);
                }
                catch
                {
                }
                try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
                throw;
            }
            finally
            {
                proc.OutputDataReceived -= OnOutput;
                proc.ErrorDataReceived -= OnOutput;
            }
        }

        public static bool StartServe(string workingDir)
        {
            if (!IsInstalled)
                return false;

            StopServe();

            lock (_serveLock)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = RojoExe,
                    Arguments = "serve",
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                _serveProcess = Process.Start(psi);
                App.Logger.WriteLine(LogTag, "rojo serve started in " + workingDir);
                return _serveProcess != null;
            }
        }

        public static void StopServe()
        {
            lock (_serveLock)
            {
                if (_serveProcess == null)
                    return;

                try
                {
                    if (!_serveProcess.HasExited)
                        _serveProcess.Kill(true);
                }
                catch
                {
                }
                try
                {
                    _serveProcess.Dispose();
                }
                catch
                {
                }
                _serveProcess = null;
                App.Logger.WriteLine(LogTag, "rojo serve stopped");
            }
        }

        public static void Shutdown() => StopServe();

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
    }
}
