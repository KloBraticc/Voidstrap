using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace Voidstrap;

public partial class Bootstrapper
{
    private static bool IsValidSkyboxPackDirectory(string directory)
    {
        return SkyboxImageConverter.IsValidPackDirectory(directory);
    }

    public async Task EnsureSkyboxPackDownloadedAsync(string packName)
    {
        const string LOG_IDENT = "Bootstrapper::EnsureSkyboxPackDownloaded";

        if (
            string.IsNullOrWhiteSpace(packName)
            || packName.Length > 128
            || packName == "."
            || packName == ".."
            || packName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
        )
        {
            throw new InvalidDataException("The selected skybox name is invalid.");
        }

        Directory.CreateDirectory(PackFolder);
        string packRoot = Path.GetFullPath(PackFolder) + Path.DirectorySeparatorChar;
        string packFolder = Path.GetFullPath(Path.Combine(PackFolder, packName));
        if (!packFolder.StartsWith(packRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected skybox name is invalid.");

        string commitPath = Path.Combine(packFolder, App.SkyboxPackCommitFile);
        bool present = IsValidSkyboxPackDirectory(packFolder);
        if (present && File.Exists(commitPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(commitPath) < TimeSpan.FromHours(6))
            return;

        string latest;
        try
        {
            latest = await GetLatestCommitShaAsync(_cancelTokenSource.Token);
        }
        catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Commit check failed, keeping local pack: " + ex.Message);
            if (present)
                return;
            throw;
        }

        if (present && ReadPackCommit(commitPath) == latest)
        {
            TrySetCommitTimestamp(commitPath);
            return;
        }

        string operationId = Guid.NewGuid().ToString("N");
        string stagingFolder = packFolder + ".new." + operationId;
        string backupFolder = packFolder + ".backup." + operationId;
        try
        {
            if (Directory.Exists(stagingFolder))
                Directory.Delete(stagingFolder, recursive: true);
            Directory.CreateDirectory(stagingFolder);

            await DownloadSkyboxPackFilesAsync(packName, latest, stagingFolder, _cancelTokenSource.Token);

            if (!IsValidSkyboxPackDirectory(stagingFolder))
                throw new InvalidDataException("The downloaded skybox pack is incomplete.");

            File.WriteAllText(Path.Combine(stagingFolder, App.SkyboxPackCommitFile), latest);

            if (Directory.Exists(packFolder))
                Directory.Move(packFolder, backupFolder);
            Directory.Move(stagingFolder, packFolder);
            TryDeleteDirectory(backupFolder);
        }
        catch
        {
            if (!Directory.Exists(packFolder) && Directory.Exists(backupFolder))
            {
                try
                {
                    Directory.Move(backupFolder, packFolder);
                }
                catch (Exception restoreException)
                {
                    throw new IOException(
                        "The skybox update failed and its backup could not be restored. The backup was preserved at " + backupFolder,
                        restoreException
                    );
                }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingFolder);
            if (Directory.Exists(packFolder))
                TryDeleteDirectory(backupFolder);
        }
    }

    private static string? ReadPackCommit(string commitPath)
    {
        try
        {
            if (!File.Exists(commitPath))
                return null;
            string value = File.ReadAllText(commitPath).Trim();
            return IsSkyboxCommitSha(value) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSkyboxCommitSha(string value)
    {
        if (value.Length is < 7 or > 64)
            return false;
        foreach (char current in value)
        {
            if (!Uri.IsHexDigit(current))
                return false;
        }
        return true;
    }

    private static void TrySetCommitTimestamp(string commitPath)
    {
        const string LOG_IDENT = "Bootstrapper::EnsureSkyboxPackDownloaded";

        try
        {
            File.SetLastWriteTimeUtc(commitPath, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not refresh the skybox timestamp: " + ex.Message);
        }
    }

    private async Task DownloadSkyboxPackFilesAsync(string packName, string commitSha, string stagingFolder, CancellationToken ct)
    {
        const string LOG_IDENT = "Bootstrapper::DownloadSkyboxPack";

        if (!IsSkyboxCommitSha(commitSha))
            throw new InvalidDataException("The skybox version is invalid");

        string prefix = App.SkyboxRawBaseUrl + commitSha + "/" + Uri.EscapeDataString(packName) + "/";
        string[] urls = [.. App.SkyboxFileNames.Select(name => prefix + Uri.EscapeDataString(name))];

        long[] sizes = await Task.WhenAll(urls.Select(url => GetSkyboxFileSizeAsync(url, ct)));
        long total = 0;
        foreach (long size in sizes)
        {
            if (size > App.SkyboxMaxFaceBytes)
                throw new InvalidDataException("The skybox download exceeds the size limit.");
            if (size > 0)
                total += size;
        }

        App.Logger.WriteLine(
            LOG_IDENT,
            $"Downloading {urls.Length} skybox faces ({total:N0} bytes) from the raw CDN."
        );

        int bufferSize = DownloadConfiguration.NormalizeBuffer(App.Settings.Prop.DownloadBufferKb) * 1024;
        int maxSegments = DownloadConfiguration.NormalizeSegments(App.Settings.Prop.MaxDownloadSegments);
        long downloaded = 0L;

        using CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task progressTask = RunSingleFileProgressLoopAsync(
            "Downloading Skybox",
            () => Interlocked.Read(in downloaded),
            total,
            progressCts.Token
        );
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, urls.Length),
                new ParallelOptions { MaxDegreeOfParallelism = urls.Length, CancellationToken = ct },
                async (index, token) =>
                {
                    string destination = Path.Combine(stagingFolder, App.SkyboxFileNames[index]);
                    long size = sizes[index];

                    if (size >= App.SkyboxMinSegmentBytes * 2L)
                    {
                        try
                        {
                            await DownloadFileSegmentedAsync(
                                urls[index],
                                destination,
                                size,
                                bufferSize,
                                maxSegments,
                                App.SkyboxMinSegmentBytes,
                                Add,
                                token
                            );
                            return;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            App.Logger.WriteLine(
                                LOG_IDENT,
                                "Segmented face download failed (" + ex.Message + "), using a single stream."
                            );
                        }
                    }

                    using HttpResponseMessage response = await SkyboxHttpClient.GetAsync(
                        urls[index],
                        HttpCompletionOption.ResponseHeadersRead,
                        token
                    );
                    response.EnsureSuccessStatusCode();
                    await StreamResponseToFileAsync(response, destination, bufferSize, App.SkyboxMaxFaceBytes, Add, token);
                }
            );
        }
        finally
        {
            progressCts.Cancel();
            try
            {
                await progressTask.ConfigureAwait(false);
            }
            catch { }
        }

        void Add(long n)
        {
            Interlocked.Add(ref downloaded, n);
        }
    }

    private static async Task<long> GetSkyboxFileSizeAsync(string url, CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Head, url);
            using HttpResponseMessage response = await SkyboxHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return -1;
            return response.Content.Headers.ContentLength ?? -1;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return -1;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    private static async Task StreamResponseToFileAsync(
        HttpResponseMessage response,
        string destPath,
        int bufferSize,
        long maximumBytes,
        Action<long> addBytes,
        CancellationToken ct
    )
    {
        await using Stream src = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream dst = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        byte[] buf = DownloadBufferPool.Rent(bufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                int num;
                int r = (num = await src.ReadAsync(buf.AsMemory(0, bufferSize), ct));
                if (num <= 0)
                {
                    break;
                }
                total += r;
                if (total > maximumBytes)
                    throw new InvalidDataException("The skybox download exceeds the size limit.");
                await dst.WriteAsync(buf.AsMemory(0, r), ct);
                addBytes(r);
            }
        }
        finally
        {
            DownloadBufferPool.Return(buf);
        }
    }

    private static async Task DownloadFileSegmentedAsync(
        string url,
        string destPath,
        long contentLength,
        int bufferSize,
        int maxSegments,
        long minSegment,
        Action<long> addBytes,
        CancellationToken ct
    )
    {
        int segs = (int)Math.Min(maxSegments, Math.Max(1L, contentLength / minSegment));
        if (segs <= 1)
        {
            using HttpResponseMessage resp = await SkyboxHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await StreamResponseToFileAsync(resp, destPath, bufferSize, contentLength, addBytes, ct);
            return;
        }
        long segSize = contentLength / segs;
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
            destPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            FileOptions.Asynchronous | FileOptions.RandomAccess
        );
        RandomAccess.SetLength(handle, contentLength);
        await Task.WhenAll(
            Enumerable
                .Range(0, segs)
                .Select(async i =>
                {
                    long start = i * segSize;
                    long end = i == segs - 1 ? contentLength - 1 : start + segSize - 1;
                    using HttpRequestMessage request = new(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    using HttpResponseMessage res = await SkyboxHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (res.StatusCode != HttpStatusCode.PartialContent)
                    {
                        throw new IOException($"Server ignored range request with status {(int)res.StatusCode}.");
                    }
                    long expectedLength = end - start + 1;
                    ContentRangeHeaderValue? contentRange = res.Content.Headers.ContentRange;
                    if (
                        contentRange?.From != start
                        || contentRange.To != end
                        || contentRange.Length != contentLength
                        || res.Content.Headers.ContentLength != expectedLength
                    )
                    {
                        throw new IOException("Server returned an invalid skybox byte range");
                    }
                    await using Stream net = await res.Content.ReadAsStreamAsync(ct);
                    byte[] buf = DownloadBufferPool.Rent(bufferSize);
                    long pos = start;
                    try
                    {
                        long remaining = expectedLength;
                        while (remaining > 0)
                        {
                            int requested = (int)Math.Min(bufferSize, remaining);
                            int read = await net.ReadAsync(buf.AsMemory(0, requested), ct);
                            if (read == 0)
                            {
                                throw new IOException("Skybox segment ended before the expected byte range");
                            }
                            await RandomAccess.WriteAsync(handle, buf.AsMemory(0, read), pos, ct);
                            pos += read;
                            remaining -= read;
                            addBytes(read);
                        }
                        if (await net.ReadAsync(buf.AsMemory(0, 1), ct) != 0)
                        {
                            throw new IOException("Skybox segment exceeded the expected byte range");
                        }
                    }
                    finally
                    {
                        DownloadBufferPool.Return(buf);
                    }
                })
        );
    }

    private async Task RunSingleFileProgressLoopAsync(string label, Func<long> downloadedGetter, long total, CancellationToken ct)
    {
        double emaSpeed = 0.0;
        long lastBytes = downloadedGetter();
        Stopwatch sw = Stopwatch.StartNew();
        if (Dialog != null)
        {
            if (total > 0)
            {
                SetProgressIndeterminate(false);
                SetProgressMaximum(1000);
            }
            else
            {
                SetProgressIndeterminate(true);
            }
        }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch
            {
                break;
            }
            long num = downloadedGetter();
            double num2 = sw.Elapsed.TotalSeconds;
            if (num2 < 0.05)
            {
                num2 = 0.05;
            }
            sw.Restart();
            double num3 = Math.Max(0.0, (double)(num - lastBytes) / num2);
            lastBytes = num;
            emaSpeed = ((emaSpeed <= 0.0) ? num3 : (0.25 * num3 + 0.75 * emaSpeed));
            if (total > 0)
            {
                double num4 = Math.Clamp((double)num / (double)total * 100.0, 0.0, 100.0);
                long num5 = Math.Max(0L, total - num);
                string value = ((emaSpeed > 4096.0) ? FormatEta((double)num5 / emaSpeed) : "calculating");
                SetStatus($"{label} {num4:0}%\n{FormatSpeed(emaSpeed)}, ETA {value}");
                if (Dialog != null)
                {
                    int value2 = (int)Math.Clamp(num4 * 10.0, 0.0, 1000.0);
                    InvokeOnDialog(
                        delegate
                        {
                            Dialog.ProgressValue = value2;
                        }
                    );
                }
            }
            else
            {
                SetStatus($"{label}\n{BytesToString(num)}, {FormatSpeed(emaSpeed)}");
            }
        }
    }

    public static Task ApplySkyboxAsync(string skyboxName, string modsFolder)
    {
        if (
            string.IsNullOrWhiteSpace(skyboxName)
            || skyboxName.Length > 128
            || skyboxName == "."
            || skyboxName == ".."
            || skyboxName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
        )
            throw new InvalidDataException("The selected skybox name is invalid.");
        bool custom = skyboxName.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase);
        string packRoot = Path.GetFullPath(PackFolder) + Path.DirectorySeparatorChar;
        string sourceRoot = custom
            ? Path.GetFullPath(SkyboxImageConverter.CustomPackDirectory)
            : Path.GetFullPath(Path.Combine(PackFolder, skyboxName));
        bool validSource = custom
            ? SkyboxImageConverter.IsValidPackDirectory(sourceRoot)
            : sourceRoot.StartsWith(packRoot, StringComparison.OrdinalIgnoreCase) && IsValidSkyboxPackDirectory(sourceRoot);
        if (!validSource)
        {
            throw new DirectoryNotFoundException("Skybox '" + skyboxName + "' not found.");
        }
        InlineArray5<string> buffer = default;
        buffer[0] = modsFolder;
        buffer[1] = "PlatformContent";
        buffer[2] = "pc";
        buffer[3] = "textures";
        buffer[4] = "sky";
        string targetRoot = Path.Combine(buffer);
        string operationId = Guid.NewGuid().ToString("N");
        string stagingRoot = targetRoot + ".new." + operationId;
        string backupRoot = targetRoot + ".backup." + operationId;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            Parallel.ForEach(
                App.SkyboxFileNames,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Min(ModApplyConcurrency, App.SkyboxFileNames.Length) },
                name =>
                {
                    string sourceFile = Path.Combine(sourceRoot, name);
                    string targetFile = Path.Combine(stagingRoot, name);
                    File.Copy(sourceFile, targetFile, overwrite: false);
                }
            );
            if (!IsValidSkyboxPackDirectory(stagingRoot))
                throw new InvalidDataException("The selected skybox is incomplete.");
            if (Directory.Exists(targetRoot))
                Directory.Move(targetRoot, backupRoot);
            Directory.Move(stagingRoot, targetRoot);
            TryDeleteDirectory(backupRoot);
        }
        catch
        {
            if (!Directory.Exists(targetRoot) && Directory.Exists(backupRoot))
            {
                try
                {
                    Directory.Move(backupRoot, targetRoot);
                }
                catch (Exception restoreException)
                {
                    throw new IOException(
                        "The skybox apply failed and its backup could not be restored. The backup was preserved at " + backupRoot,
                        restoreException
                    );
                }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            if (Directory.Exists(targetRoot))
                TryDeleteDirectory(backupRoot);
        }
        return Task.CompletedTask;
    }

    private async Task RestoreStoragePatchesAsync(CancellationToken ct)
    {
        const string LOG_IDENT = "Bootstrapper::RestoreStoragePatches";

        if (!IsStudioLaunch && (_modSkyPatchApplied || App.Settings.Prop.SkyBoxDataSending))
        {
            try
            {
                await ApplySkyboxPatchToRobloxStorageAsync(ct);
                App.Logger.WriteLine(
                    LOG_IDENT,
                    "Reapplied the skybox storage patch after the Roblox asset cache was reset."
                );
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(
                    LOG_IDENT,
                    "The skybox storage patch could not be reapplied: " + ex.Message
                );
            }
        }
        ReapplyAssetCacheMods();
    }

    internal static void ReapplyAssetCacheMods()
    {
        const string LOG_IDENT = "Bootstrapper::RestoreStoragePatches";

        try
        {
            foreach (string folder in ManagedModStore.EnabledFoldersByPriority().Reverse())
            {
                string backup = Path.Combine(folder, RobloxAssetCache.BackupFolderName);
                if (RobloxAssetCache.HasBackups(backup))
                    RobloxAssetCache.Reapply(backup);
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Asset cache mods could not be reapplied: " + ex.Message);
        }
    }

    public static async Task ApplySkyboxPatchToRobloxStorageAsync(CancellationToken ct = default)
    {
        const long maxPatchBytes = 67108864L;
        string rbxStorage = Path.Combine(Voidstrap.Utility.RobloxLocalReset.Root, "rbx-storage");
        HttpClient http = App.HttpClient;
        ConcurrentQueue<string> failures = new();
        await Parallel.ForEachAsync(
            SkyboxPatchFolderMap,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (item, token) =>
            {
                item.Deconstruct(out var key, out var value);
                string hash = key;
                string path = value;
                string text = Path.Combine(rbxStorage, path);
                Directory.CreateDirectory(text);
                string dest = Path.Combine(text, hash);
                if (File.Exists(dest) && GetSkyboxPatchSha256(dest).Equals(SkyboxPatchSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.SetAttributes(dest, FileAttributes.ReadOnly);
                    return;
                }
                string temp = dest + ".download";
                try
                {
                    using HttpResponseMessage response = await http.GetAsync(
                        "https://raw.githubusercontent.com/KloBraticc/SkyboxPatch/main/assets/" + hash,
                        HttpCompletionOption.ResponseHeadersRead,
                        token
                    );
                    response.EnsureSuccessStatusCode();
                    if (
                        response.Content.Headers.ContentLength is long contentLength
                        && (contentLength <= 0 || contentLength > maxPatchBytes)
                    )
                    {
                        throw new IOException("Skybox patch asset size is invalid");
                    }
                    await using Stream source = await response.Content.ReadAsStreamAsync(token);
                    await using FileStream output = new(
                        temp,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                    byte[] buffer = DownloadBufferPool.Rent(81920);
                    long total = 0L;
                    try
                    {
                        while (true)
                        {
                            int read = await source.ReadAsync(buffer.AsMemory(0, 81920), token);
                            if (read == 0)
                            {
                                break;
                            }
                            total += read;
                            if (total > maxPatchBytes)
                            {
                                throw new IOException("Skybox patch asset exceeds the size limit");
                            }
                            await output.WriteAsync(buffer.AsMemory(0, read), token);
                        }
                    }
                    finally
                    {
                        DownloadBufferPool.Return(buffer);
                    }
                    await output.FlushAsync(token);
                    await output.DisposeAsync();
                    if (total == 0)
                    {
                        throw new IOException("Skybox patch asset is empty");
                    }
                    if (!GetSkyboxPatchSha256(temp).Equals(SkyboxPatchSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("Skybox patch integrity check failed");
                    }
                    if (File.Exists(dest))
                    {
                        File.SetAttributes(dest, FileAttributes.Normal);
                    }
                    File.Move(temp, dest, overwrite: true);
                    File.SetAttributes(dest, FileAttributes.ReadOnly);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    File.Delete(temp);
                    throw;
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.Delete(temp);
                    }
                    catch { }
                    App.Logger.WriteLine("SkyboxPatch", "Failed " + hash + ": " + ex.Message);
                    failures.Enqueue(hash);
                }
            }
        );
        if (!failures.IsEmpty)
            throw new IOException("One or more skybox patch files could not be verified");
    }

    private static string GetSkyboxPatchSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static string BytesToString(long bytes)
    {
        if (bytes == 0L)
        {
            return "0 B";
        }
        string[] array = ["B", "KB", "MB", "GB", "TB"];
        int num = (int)Math.Floor(Math.Log(Math.Abs(bytes), 1024.0));
        double value = Math.Round((double)bytes / Math.Pow(1024.0, num), 1);
        return $"{value} {array[Math.Min(num, array.Length - 1)]}";
    }
}
