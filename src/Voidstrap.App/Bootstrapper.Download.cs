using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Voidstrap.RobloxInterfaces;

namespace Voidstrap;

public partial class Bootstrapper
{
    private async Task DownloadPackage(Package package)
    {
        string logIdent = "Bootstrapper::DownloadPackage." + package.Name;
        bool updating = !string.IsNullOrEmpty(AppData.State.VersionGuid);
        CancellationToken ct = _cancelTokenSource.Token;
        ct.ThrowIfCancellationRequested();
        PackageProgressTracker progress = new(this);
        Directory.CreateDirectory(Paths.Downloads);
        IReadOnlyList<string> packageUrls = Deployment.GetLocations("/" + _latestVersionGuid + "-" + package.Name, UseVng);
        if (packageUrls.Count == 0)
        {
            throw new InvalidOperationException("No download location is available for package " + package.Name + ".");
        }
        int urlIndex = 0;
        string packageUrl = packageUrls[0];
        string text = Path.Combine(Paths.LocalAppData, "Roblox", "Downloads", package.Signature);
        if (File.Exists(package.DownloadPath))
        {
            if (MD5Hash.FromFile(package.DownloadPath) == package.Signature)
            {
                App.Logger.WriteLine(logIdent, "Already downloaded, skipping.");
                Voidstrap.Utility.PackageCache.Touch(package.DownloadPath);
                progress.Set(package.PackedSize);
                UpdateProgressBar();
                return;
            }
            File.Delete(package.DownloadPath);
        }
        else if (File.Exists(text))
        {
            try
            {
                if (MD5Hash.FromFile(text) != package.Signature)
                {
                    throw new ChecksumFailedException("Roblox cache package checksum does not match the manifest.");
                }
                File.Copy(text, package.DownloadPath, overwrite: true);
                progress.Set(package.PackedSize);
                UpdateProgressBar();
                return;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(logIdent, "Roblox cache copy failed: " + ex.Message);
            }
        }
        int maxParallelSegments = DownloadConfiguration.NormalizeSegments(App.Settings.Prop.MaxDownloadSegments);
        int bufferSize = DownloadConfiguration.NormalizeBuffer(App.Settings.Prop.DownloadBufferKb) * 1024;
        long minMultipartSize = 3L * 1024L * 1024L;
        string tempFile = package.DownloadPath + ".part";
        string metaFile = tempFile + ".meta";
        bool completed = false;
        for (int attempt = 1; attempt <= App.MaxPackageDownloadAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                long resumeOffset = 0L;
                if (File.Exists(tempFile) && !File.Exists(metaFile))
                {
                    try
                    {
                        resumeOffset = new FileInfo(tempFile).Length;
                        if (resumeOffset == package.PackedSize)
                        {
                            if (MD5Hash.FromFile(tempFile).Equals(package.Signature, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Move(tempFile, package.DownloadPath, overwrite: true);
                                progress.Set(package.PackedSize);
                                completed = true;
                                break;
                            }
                            File.Delete(tempFile);
                            resumeOffset = 0;
                        }
                        else if (resumeOffset > package.PackedSize)
                        {
                            File.Delete(tempFile);
                            resumeOffset = 0;
                        }
                    }
                    catch
                    {
                        resumeOffset = 0L;
                    }
                }
                using HttpRequestMessage initialRequest = new(HttpMethod.Get, packageUrl)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                bool probeRanges = resumeOffset == 0 && package.PackedSize >= minMultipartSize;
                if (resumeOffset > 0)
                {
                    initialRequest.Headers.Range = new RangeHeaderValue(resumeOffset, null);
                }
                else if (probeRanges)
                {
                    initialRequest.Headers.Range = new RangeHeaderValue(0, 0);
                }
                using HttpResponseMessage response = await SendPackageRequestAsync(initialRequest, ct)
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                    if (range?.From != resumeOffset || range.To == null || range.Length != package.PackedSize)
                    {
                        File.Delete(tempFile);
                        progress.Set(0);
                        throw new IOException("Resume response range does not match the requested package");
                    }
                    App.Logger.WriteLine(logIdent, $"Resuming single stream download at byte {resumeOffset:N0}");
                    progress.Set(resumeOffset);
                    await DownloadSingleThreadAsync(
                            response,
                            tempFile,
                            bufferSize,
                            logIdent,
                            package.PackedSize - resumeOffset,
                            progress,
                            true,
                            ct
                        )
                        .ConfigureAwait(continueOnCapturedContext: false);
                    string resumedHash = MD5Hash.FromFile(tempFile);
                    if (!resumedHash.Equals(package.Signature, StringComparison.OrdinalIgnoreCase))
                    {
                        App.Logger.WriteLine(logIdent, "Resumed file failed checksum, restarting download from scratch");
                        progress.Set(0);
                        File.Delete(tempFile);
                        continue;
                    }
                    File.Move(tempFile, package.DownloadPath, overwrite: true);
                    UpdateProgressBar();
                    completed = true;
                    break;
                }
                if (resumeOffset > 0)
                {
                    try
                    {
                        File.Delete(tempFile);
                        progress.Set(0);
                    }
                    catch { }
                }
                if (probeRanges && response.StatusCode == HttpStatusCode.PartialContent)
                {
                    if (response.Content.Headers.ContentRange?.Length != package.PackedSize)
                    {
                        throw new IOException("Package server reported a size that does not match the manifest");
                    }
                    await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
                    bool common = packageUrl.Contains("/channel/", StringComparison.Ordinal);
                    List<string> segmentUrls = [.. packageUrls.Where(url => url.Contains("/channel/", StringComparison.Ordinal) == common)];
                    await DownloadMultipartAsync(
                            segmentUrls,
                            segmentUrls.IndexOf(packageUrl),
                            tempFile,
                            package.PackedSize,
                            bufferSize,
                            maxParallelSegments,
                            updating,
                            logIdent,
                            progress,
                            ct
                        )
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
                else if (!response.IsSuccessStatusCode)
                {
                    HttpStatusCode statusCode = response.StatusCode;
                    Deployment.ReportMirrorFailure(packageUrl, $"package HTTP {(int)statusCode}");
                    App.Logger.WriteLine(
                        logIdent,
                        $"Package '{package.Name}' returned HTTP {(int)statusCode} ({statusCode}) from {packageUrl} (attempt {attempt})"
                    );
                    if (
                        (
                            statusCode == HttpStatusCode.Forbidden
                            || statusCode == HttpStatusCode.Unauthorized
                            || statusCode == HttpStatusCode.NotFound
                        )
                        && urlIndex + 1 < packageUrls.Count
                    )
                    {
                        urlIndex++;
                        packageUrl = packageUrls[urlIndex];
                        App.Logger.WriteLine(
                            logIdent,
                            $"Endpoint returned {(int)statusCode}, trying mirror {urlIndex + 1} of {packageUrls.Count}: {packageUrl}"
                        );
                        attempt--;
                        continue;
                    }
                    throw new HttpRequestException(
                        $"Package '{package.Name}' returned HTTP {(int)statusCode} ({statusCode}).",
                        null,
                        statusCode
                    );
                }
                else
                {
                    progress.Set(0);
                    await DownloadSingleThreadAsync(response, tempFile, bufferSize, logIdent, package.PackedSize, progress, false, ct)
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
                string text2 = MD5Hash.FromFile(tempFile);
                if (!text2.Equals(package.Signature, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ChecksumFailedException($"Checksum mismatch for {package.Name}: expected {package.Signature}, got {text2}");
                }
                File.Move(tempFile, package.DownloadPath, overwrite: true);
                UpdateProgressBar();
                completed = true;
                break;
            }
            catch (ChecksumFailedException exception)
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch { }
                try
                {
                    if (File.Exists(metaFile))
                    {
                        File.Delete(metaFile);
                    }
                }
                catch { }
                App.Logger.WriteLine(logIdent, $"Attempt {attempt}/{App.MaxPackageDownloadAttempts}: {exception.Message}");
                if (attempt < App.MaxPackageDownloadAttempts)
                {
                    urlIndex = (urlIndex + 1) % packageUrls.Count;
                    packageUrl = packageUrls[urlIndex];
                    progress.Set(0);
                    await Task.Delay(GetDownloadRetryDelay(attempt), ct).ConfigureAwait(continueOnCapturedContext: false);
                    continue;
                }
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                App.Logger.WriteLine(logIdent, "Download cancelled, keeping partial file for resume");
                throw;
            }
            catch (Exception ex3)
            {
                if (ex3 is AggregateException ex4)
                {
                    ex3 = ex4.Flatten().InnerException ?? ex4;
                }
                App.Logger.WriteLine(logIdent, $"Attempt {attempt}/{App.MaxPackageDownloadAttempts}: {ex3.Message}");
                if (attempt == App.MaxPackageDownloadAttempts)
                {
                    throw;
                }
                urlIndex = (urlIndex + 1) % packageUrls.Count;
                packageUrl = packageUrls[urlIndex];
                await Task.Delay(GetDownloadRetryDelay(attempt), ct).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
        ct.ThrowIfCancellationRequested();
        if (!completed)
        {
            throw new IOException("Package download did not complete: " + package.Name);
        }
    }

    private static TimeSpan GetDownloadRetryDelay(int attempt)
    {
        int milliseconds = Math.Min(250 * (1 << Math.Min(attempt - 1, 4)), 4000);
        return TimeSpan.FromMilliseconds(milliseconds + Random.Shared.Next(50, 251));
    }

    private static async Task<HttpResponseMessage> SendPackageRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await RobloxPackageClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Package server did not return response headers within 20 seconds");
        }
    }

    private static async Task<string> ReadTextBoundedAsync(HttpContent content, int maximumBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long contentLength && (contentLength < 0 || contentLength > maximumBytes))
        {
            throw new InvalidDataException("Package manifest exceeds the maximum size");
        }
        await using Stream input = await content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
        using MemoryStream output = new(content.Headers.ContentLength is long length ? (int)length : 16384);
        byte[] buffer = DownloadBufferPool.Rent(65536);
        try
        {
            while (true)
            {
                int read = await ReadWithStallTimeoutAsync(input, buffer.AsMemory(0, 65536), ct)
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (read == 0)
                {
                    return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
                }
                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException("Package manifest exceeds the maximum size");
                }
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            DownloadBufferPool.Return(buffer);
        }
    }

    private const int DownloadStallTimeoutMs = 12000;

    private const int SegmentStallTimeoutMs = 5000;

    private static async ValueTask<int> ReadWithStallTimeoutAsync(
        Stream net,
        Memory<byte> buffer,
        CancellationToken token,
        int timeoutMs = DownloadStallTimeoutMs
    )
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await net.ReadAsync(buffer, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException($"Download stalled: no data received for {timeoutMs / 1000} seconds.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        double num = bytes;
        if (num >= 1073741824.0)
        {
            return $"{num / 1073741824.0:0.00} GB";
        }
        if (num >= 1048576.0)
        {
            return $"{num / 1048576.0:0.0} MB";
        }
        if (num >= 1024.0)
        {
            return $"{num / 1024.0:0} KB";
        }
        return $"{Math.Max(0L, bytes)} B";
    }

    private static async Task DownloadSingleThreadAsync(
        HttpResponseMessage response,
        string tempFile,
        int bufferSize,
        string logIdent,
        long expectedLength,
        PackageProgressTracker progress,
        bool append,
        CancellationToken token
    )
    {
        await using Stream net = await response.Content.ReadAsStreamAsync(token);
        await using FileStream file = new(
            tempFile,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        int readSize = Math.Min(bufferSize, NetworkReadBufferCap);
        byte[] buf = DownloadBufferPool.Rent(readSize);
        long total = 0L;
        try
        {
            while (true)
            {
                int num;
                int read = (num = await ReadWithStallTimeoutAsync(net, buf.AsMemory(0, readSize), token));
                if (num <= 0)
                {
                    break;
                }
                if (expectedLength > 0 && total + read > expectedLength)
                {
                    throw new InvalidDataException("Package response is larger than the manifest size");
                }
                await file.WriteAsync(buf.AsMemory(0, read), token);
                total += read;
                progress.Add(read);
            }
            if (expectedLength > 0 && total != expectedLength)
            {
                throw new EndOfStreamException("Package response ended before the manifest size was reached");
            }
            await file.FlushAsync(token).ConfigureAwait(continueOnCapturedContext: false);
            App.Logger.WriteLine(logIdent, $"Downloaded {total:N0} bytes (single-thread)");
        }
        finally
        {
            DownloadBufferPool.Return(buf);
        }
    }

    internal static bool IsSlowConnection(double bytesPerSecond, double typicalBytesPerSecond)
    {
        return bytesPerSecond < SlowConnectionFloorBytesPerSecond && bytesPerSecond * SlowConnectionRatio < typicalBytesPerSecond;
    }

    private void RecordConnectionRate(string url, double bytesPerSecond)
    {
        double typical = Volatile.Read(ref _typicalConnectionRate);
        Volatile.Write(ref _typicalConnectionRate, typical <= 0 ? bytesPerSecond : typical * 0.8 + bytesPerSecond * 0.2);
        MirrorRates.AddOrUpdate(MirrorKey(url), bytesPerSecond, (_, rate) => rate * 0.8 + bytesPerSecond * 0.2);
    }

    private static void PenalizeMirror(string url)
    {
        MirrorRates.AddOrUpdate(MirrorKey(url), 0, (_, rate) => rate * 0.5);
    }

    private static string MirrorKey(string url) => new Uri(url).Host;

    private string PickSegmentUrl(List<string> urls, int preferredIndex, string? avoid)
    {
        if (!string.IsNullOrEmpty(Deployment.PreferredBaseUrl) && avoid == null)
        {
            return urls[preferredIndex];
        }
        List<string> choices = urls.Where(url => url != avoid).ToList();
        if (choices.Count == 0)
        {
            return urls[preferredIndex];
        }
        int pick = Interlocked.Increment(ref _segmentPickCount);
        string? unmeasured = choices.FirstOrDefault(url => !MirrorRates.ContainsKey(MirrorKey(url)));
        if (unmeasured != null && pick % 2 == 0)
        {
            return unmeasured;
        }
        if (pick % MirrorExploreInterval == 0)
        {
            return choices[Random.Shared.Next(choices.Count)];
        }
        return choices.OrderByDescending(url => MirrorRates.GetValueOrDefault(MirrorKey(url))).First();
    }

    private sealed class SlowConnectionException() : IOException("Package connection is moving too slowly, replacing it");

    private sealed class SegmentWork
    {
        public required int Index { get; init; }

        public required long Position { get; set; }

        public required long End { get; init; }

        public int Failures { get; set; }

        public int SlowAborts { get; set; }

        public string? LastUrl { get; set; }
    }

    private sealed class PackageProgressTracker(Bootstrapper owner)
    {
        private long _credited;

        public void Add(long bytes)
        {
            if (bytes == 0)
            {
                return;
            }
            Interlocked.Add(ref _credited, bytes);
            Interlocked.Add(ref owner._totalDownloadedBytes, bytes);
        }

        public void Set(long bytes)
        {
            long normalized = Math.Max(0, bytes);
            long previous = Interlocked.Exchange(ref _credited, normalized);
            Interlocked.Add(ref owner._totalDownloadedBytes, normalized - previous);
        }
    }

    private sealed class MultipartDownloadState
    {
        public long Length { get; set; }

        public long SegSize { get; set; }

        public bool[] Done { get; set; } = [];
    }

    private async Task DownloadMultipartAsync(
        List<string> urls,
        int initialUrlIndex,
        string tempFile,
        long contentLength,
        int bufferSize,
        int maxSegments,
        bool updating,
        string logIdent,
        PackageProgressTracker progress,
        CancellationToken token
    )
    {
        int segs = (int)Math.Clamp(contentLength / TargetSegmentBytes, 1L, MaxSegmentCount);
        if (segs < maxSegments)
        {
            segs = (int)Math.Min(maxSegments, Math.Max(1L, contentLength / 1572864));
        }
        if (segs <= 1)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, urls[initialUrlIndex])
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            using HttpResponseMessage r = await SendPackageRequestAsync(request, token).ConfigureAwait(continueOnCapturedContext: false);
            r.EnsureSuccessStatusCode();
            await DownloadSingleThreadAsync(r, tempFile, bufferSize, logIdent, contentLength, progress, false, token)
                .ConfigureAwait(continueOnCapturedContext: false);
            return;
        }
        long segSize = contentLength / segs;
        string metaFile = tempFile + ".meta";
        MultipartDownloadState? state = null;
        if (File.Exists(metaFile) && File.Exists(tempFile))
        {
            try
            {
                state = JsonFile.Deserialize<MultipartDownloadState>(metaFile, JsonOptions.Tolerant, 1048576);
            }
            catch
            {
                state = null;
            }
            if (
                state != null
                && (
                    state.Length != contentLength
                    || state.Done == null
                    || state.Done.Length == 0
                    || state.Done.Length > MaxSegmentCount
                    || state.SegSize != contentLength / state.Done.Length
                    || new FileInfo(tempFile).Length != contentLength
                )
            )
            {
                state = null;
            }
        }
        bool resuming = state != null;
        if (resuming)
        {
            segs = state!.Done.Length;
            segSize = state.SegSize;
            long doneBytes = 0L;
            for (int s = 0; s < segs; s++)
            {
                if (state.Done[s])
                {
                    doneBytes += ((s == segs - 1) ? (contentLength - s * segSize) : segSize);
                }
            }
            progress.Set(doneBytes);
            App.Logger.WriteLine(logIdent, $"Resuming multi part download, {doneBytes:N0} of {contentLength:N0} bytes already complete");
        }
        else
        {
            progress.Set(0);
            state = new MultipartDownloadState
            {
                Length = contentLength,
                SegSize = segSize,
                Done = new bool[segs],
            };
        }
        App.Logger.WriteLine(logIdent, $"Multi part: {segs} segments of ~{segSize:N0} bytes");
        object metaLock = new();
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(
            tempFile,
            resuming ? FileMode.Open : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            FileOptions.Asynchronous
        );
        if (!resuming)
        {
            RandomAccess.SetLength(handle, contentLength);
            WriteJsonAtomic(metaFile, state);
        }
        long totalRead = 0L;
        CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationTokenSource segCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken segToken = segCts.Token;
        try
        {
            Task progressTask = (
                updating
                    ? Task.Run(
                        async delegate
                        {
                            Stopwatch sw = Stopwatch.StartNew();
                            while (!progressCts.Token.IsCancellationRequested)
                            {
                                if (sw.ElapsedMilliseconds >= 500)
                                {
                                    UpdateProgressBar();
                                    sw.Restart();
                                }
                                try
                                {
                                    await Task.Delay(500, progressCts.Token);
                                }
                                catch
                                {
                                    break;
                                }
                            }
                        },
                        progressCts.Token
                    )
                    : Task.CompletedTask
            );
            ConcurrentQueue<SegmentWork> queue = new();
            for (int i = 0; i < segs; i++)
            {
                if (!state.Done[i])
                {
                    long start = i * segSize;
                    queue.Enqueue(
                        new SegmentWork
                        {
                            Index = i,
                            Position = start,
                            End = (i == segs - 1) ? (contentLength - 1) : (start + segSize - 1),
                        }
                    );
                }
            }
            int[] remaining = [queue.Count];
            int workerCount = Math.Min(maxSegments, queue.Count);
            using SemaphoreSlim workAvailable = new(queue.Count);
            List<Task> tasks = [];
            for (int i = 0; i < workerCount; i++)
            {
                tasks.Add(
                    RunSegmentWorkerAsync(
                        urls,
                        initialUrlIndex,
                        handle,
                        state,
                        queue,
                        workAvailable,
                        remaining,
                        workerCount,
                        contentLength,
                        bufferSize,
                        metaFile,
                        metaLock,
                        bytes =>
                        {
                            Interlocked.Add(ref totalRead, bytes);
                            progress.Add(bytes);
                        },
                        segCts,
                        segToken
                    )
                );
            }
            try
            {
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch
                {
                    segCts.Cancel();
                    try
                    {
                        await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
                    }
                    catch { }
                    throw;
                }
            }
            finally
            {
                progressCts.Cancel();
                try
                {
                    await progressTask;
                }
                catch { }
            }
            App.Logger.WriteLine(logIdent, $"Downloaded {totalRead:N0} bytes (multi part)");
            try
            {
                File.Delete(metaFile);
            }
            catch { }
        }
        finally
        {
            progressCts.Dispose();
            segCts.Dispose();
        }
    }

    private async Task RunSegmentWorkerAsync(
        List<string> urls,
        int preferredUrlIndex,
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        MultipartDownloadState state,
        ConcurrentQueue<SegmentWork> queue,
        SemaphoreSlim workAvailable,
        int[] remaining,
        int workerCount,
        long contentLength,
        int bufferSize,
        string metaFile,
        object metaLock,
        Action<long> reportProgress,
        CancellationTokenSource segCts,
        CancellationToken token
    )
    {
        try
        {
            while (true)
            {
                await workAvailable.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
                if (!queue.TryDequeue(out SegmentWork? work))
                {
                    return;
                }
                bool finished = await DownloadSegmentOnceAsync(
                        urls,
                        preferredUrlIndex,
                        handle,
                        work,
                        contentLength,
                        bufferSize,
                        reportProgress,
                        token
                    )
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (!finished)
                {
                    queue.Enqueue(work);
                    workAvailable.Release();
                    continue;
                }
                lock (metaLock)
                {
                    state.Done[work.Index] = true;
                    WriteJsonAtomic(metaFile, state);
                }
                if (Interlocked.Decrement(ref remaining[0]) == 0)
                {
                    workAvailable.Release(workerCount);
                }
            }
        }
        catch
        {
            segCts.Cancel();
            throw;
        }
    }

    private async Task<bool> DownloadSegmentOnceAsync(
        List<string> urls,
        int preferredUrlIndex,
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        SegmentWork work,
        long contentLength,
        int bufferSize,
        Action<long> reportProgress,
        CancellationToken token
    )
    {
        string url = PickSegmentUrl(urls, preferredUrlIndex, work.LastUrl);
        work.LastUrl = null;
        await _networkRequestSlots.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };
            request.Headers.Range = new RangeHeaderValue(work.Position, work.End);
            using HttpResponseMessage response = await SendPackageRequestAsync(request, token)
                .ConfigureAwait(continueOnCapturedContext: false);
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (
                response.StatusCode != HttpStatusCode.PartialContent
                || range?.From != work.Position
                || range.To != work.End
                || range.Length != contentLength
            )
            {
                throw new IOException("Package server returned an invalid range response");
            }
            long expectedLength = work.End - work.Position + 1;
            if (response.Content.Headers.ContentLength is long responseLength && responseLength != expectedLength)
            {
                throw new IOException("Package segment length does not match the requested range");
            }
            await using Stream net = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(continueOnCapturedContext: false);
            int readSize = Math.Min(bufferSize, NetworkReadBufferCap);
            byte[] buffer = DownloadBufferPool.Rent(readSize);
            Stopwatch clock = Stopwatch.StartNew();
            long windowBytes = 0L;
            long windowStartMs = 0L;
            long nextCheckMs = SlowConnectionGraceMs;
            try
            {
                while (work.Position <= work.End)
                {
                    int requested = (int)Math.Min(readSize, work.End - work.Position + 1);
                    int read = await ReadWithStallTimeoutAsync(net, buffer.AsMemory(0, requested), token, SegmentStallTimeoutMs)
                        .ConfigureAwait(continueOnCapturedContext: false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Package segment ended before the requested range was complete");
                    }
                    await RandomAccess
                        .WriteAsync(handle, buffer.AsMemory(0, read), work.Position, token)
                        .ConfigureAwait(continueOnCapturedContext: false);
                    work.Position += read;
                    windowBytes += read;
                    reportProgress(read);
                    long elapsedMs = clock.ElapsedMilliseconds;
                    if (elapsedMs < nextCheckMs || work.Position > work.End)
                    {
                        continue;
                    }
                    double rate = windowBytes * 1000.0 / Math.Max(1L, elapsedMs - windowStartMs);
                    if (work.SlowAborts < MaxSlowConnectionAborts && IsSlowConnection(rate, Volatile.Read(ref _typicalConnectionRate)))
                    {
                        throw new SlowConnectionException();
                    }
                    windowBytes = 0L;
                    windowStartMs = elapsedMs;
                    nextCheckMs = elapsedMs + SlowConnectionWindowMs;
                }
            }
            finally
            {
                DownloadBufferPool.Return(buffer);
            }
            RecordConnectionRate(url, expectedLength * 1000.0 / Math.Max(1L, clock.ElapsedMilliseconds));
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (SlowConnectionException)
        {
            work.SlowAborts++;
            work.LastUrl = url;
            PenalizeMirror(url);
            return false;
        }
        catch (Exception ex)
        {
            work.LastUrl = url;
            PenalizeMirror(url);
            Deployment.ReportMirrorFailure(url, "segment failed: " + ex.Message);
            if (++work.Failures >= Math.Clamp(urls.Count, 2, 4))
            {
                throw new IOException("Package segment failed after mirror retries", ex);
            }
        }
        finally
        {
            _networkRequestSlots.Release();
        }
        await Task.Delay(GetDownloadRetryDelay(work.Failures), token).ConfigureAwait(continueOnCapturedContext: false);
        return false;
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        JsonFile.SerializeAtomic(path, value, createBackup: false);
    }
}
