using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Voidstrap.Utility;

public static class ResilientDownload
{
	private const long MinSegmentBytes = 1048576;

	private const long MinSegmentedBytes = 4194304;

	private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);

	private static readonly SemaphoreSlim SegmentGate = new(32, 32);

	public static async Task DownloadAsync(HttpClient client, IReadOnlyList<string> urls, string destination, long maxBytes, string? expectedSha256 = null, Action<long, long?>? progress = null, CancellationToken token = default)
	{
		if (urls.Count == 0)
			throw new ArgumentException("At least one download URL is required", nameof(urls));
		string? directory = Path.GetDirectoryName(destination);
		if (!string.IsNullOrEmpty(directory))
			Directory.CreateDirectory(directory);
		string temporary = destination + ".part";
		Exception? last = null;
		for (int source = 0; source < urls.Count; source++)
		{
			if (source > 0)
				Delete(temporary);
			for (int attempt = 0; attempt < 4; attempt++)
			{
				token.ThrowIfCancellationRequested();
				try
				{
					(long bytes, long? total) = await DownloadAttemptAsync(client, urls[source], temporary, maxBytes, attempt == 0, progress, token).ConfigureAwait(false);
					await ValidateHashAsync(temporary, expectedSha256, token).ConfigureAwait(false);
					token.ThrowIfCancellationRequested();
					File.Move(temporary, destination, true);
					progress?.Invoke(bytes, total ?? bytes);
					return;
				}
				catch (OperationCanceledException) when (token.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or CryptographicException or FormatException)
				{
					last = ex;
					if (ex is CryptographicException or FormatException)
						Delete(temporary);
					if (attempt < 3)
						await Task.Delay(250 * (attempt + 1), token).ConfigureAwait(false);
				}
			}
		}
		throw new IOException("The download failed from every available source", last);
	}

	private static async Task<(long Bytes, long? Total)> DownloadAttemptAsync(HttpClient client, string url, string temporary, long maxBytes, bool allowSegments, Action<long, long?>? progress, CancellationToken token)
	{
		long offset = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
		if (offset < 0 || offset > maxBytes)
		{
			Delete(temporary);
			offset = 0;
		}
		using HttpRequestMessage request = new(HttpMethod.Get, url);
		if (offset > 0)
			request.Headers.Range = new RangeHeaderValue(offset, null);
		using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange?.Length == offset)
			return (offset, offset);
		bool append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From == offset;
		if (offset > 0 && !append)
		{
			offset = 0;
			Delete(temporary);
		}
		response.EnsureSuccessStatusCode();
		long? responseBytes = response.Content.Headers.ContentLength;
		long? expectedTotal = response.Content.Headers.ContentRange?.Length ?? (responseBytes.HasValue ? offset + responseBytes.Value : null);
		if (expectedTotal is <= 0 || expectedTotal > maxBytes)
			throw new IOException("The download size is invalid");
		int maxSegments = DownloadConfiguration.NormalizeSegments(App.Settings.Prop.MaxDownloadSegments);
		if (allowSegments && offset == 0 && expectedTotal is >= MinSegmentedBytes && maxSegments > 1 && response.Headers.AcceptRanges.Contains("bytes") && response.RequestMessage?.RequestUri is Uri finalUri)
		{
			response.Dispose();
			try
			{
				await DownloadSegmentedAsync(client, finalUri, temporary, expectedTotal.Value, maxSegments, progress, token).ConfigureAwait(false);
			}
			catch
			{
				Delete(temporary);
				throw;
			}
			return (expectedTotal.Value, expectedTotal.Value);
		}
		await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		int bufferSize = DownloadConfiguration.NormalizeBuffer(App.Settings.Prop.DownloadBufferKb) * 1024;
		await using FileStream output = new(temporary, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] buffer = new byte[bufferSize];
		long total = offset;
		long lastProgress = Stopwatch.GetTimestamp();
		while (true)
		{
			using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(token);
			stall.CancelAfter(StallTimeout);
			int read;
			try
			{
				read = await input.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!token.IsCancellationRequested)
			{
				throw new IOException("The download stopped receiving data");
			}
			if (read == 0)
				break;
			total += read;
			if (total > maxBytes)
				throw new IOException("The download exceeds the size limit");
			await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
			long now = Stopwatch.GetTimestamp();
			if (progress != null && Stopwatch.GetElapsedTime(lastProgress, now) >= TimeSpan.FromMilliseconds(100))
			{
				lastProgress = now;
				ReportPartial(progress, total, expectedTotal);
			}
		}
		await output.FlushAsync(token).ConfigureAwait(false);
		if (total == 0)
			throw new IOException("The download is empty");
		if (expectedTotal.HasValue && total != expectedTotal.Value)
			throw new IOException("The download ended before all bytes were received");
		return (total, expectedTotal);
	}

	private static async Task DownloadSegmentedAsync(HttpClient client, Uri uri, string temporary, long length, int maxSegments, Action<long, long?>? progress, CancellationToken token)
	{
		int count = (int)Math.Min(maxSegments, Math.Max(2, length / MinSegmentBytes));
		int bufferSize = DownloadConfiguration.NormalizeBuffer(App.Settings.Prop.DownloadBufferKb) * 1024;
		long segmentSize = length / count;
		using SafeFileHandle handle = File.OpenHandle(temporary, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous | FileOptions.RandomAccess);
		RandomAccess.SetLength(handle, length);
		using CancellationTokenSource failed = CancellationTokenSource.CreateLinkedTokenSource(token);
		Exception? failure = null;
		long completed = 0;
		long lastProgress = Stopwatch.GetTimestamp();
		void Report(long added)
		{
			long current = Interlocked.Add(ref completed, added);
			if (progress == null)
				return;
			long now = Stopwatch.GetTimestamp();
			long previous = Volatile.Read(ref lastProgress);
			if (Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMilliseconds(100) || Interlocked.CompareExchange(ref lastProgress, now, previous) != previous)
				return;
			ReportPartial(progress, current, length);
		}
		void Fail(Exception exception)
		{
			Interlocked.CompareExchange(ref failure, exception, null);
			failed.Cancel();
		}
		Task[] tasks = new Task[count];
		for (int index = 0; index < count; index++)
		{
			long start = index * segmentSize;
			long end = index == count - 1 ? length - 1 : start + segmentSize - 1;
			tasks[index] = DownloadRangeAsync(client, uri, handle, start, end, length, bufferSize, Report, Fail, failed);
		}
		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch when (failure != null)
		{
			ExceptionDispatchInfo.Capture(failure).Throw();
			throw;
		}
	}

	private static async Task DownloadRangeAsync(HttpClient client, Uri uri, SafeFileHandle handle, long start, long end, long length, int bufferSize, Action<long> progress, Action<Exception> fail, CancellationTokenSource failed)
	{
		CancellationToken token = failed.Token;
		bool entered = false;
		byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
		try
		{
			await SegmentGate.WaitAsync(token).ConfigureAwait(false);
			entered = true;
			using HttpRequestMessage request = new(HttpMethod.Get, uri);
			request.Headers.Range = new RangeHeaderValue(start, end);
			using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
			if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.To != end || range.Length != length)
				throw new IOException("The server returned an unexpected byte range");
			await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
			using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(token);
			long position = start;
			while (position <= end)
			{
				stall.CancelAfter(StallTimeout);
				int read;
				try
				{
					read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position + 1)), stall.Token).ConfigureAwait(false);
				}
				catch (OperationCanceledException) when (!token.IsCancellationRequested)
				{
					throw new IOException("The download stopped receiving data");
				}
				if (read == 0)
					throw new IOException("The download ended before all bytes were received");
				await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, token).ConfigureAwait(false);
				position += read;
				progress(read);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			fail(ex);
			throw;
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
			if (entered)
				SegmentGate.Release();
		}
	}

	private static void ReportPartial(Action<long, long?> progress, long bytes, long? total)
	{
		long visible = total is > 0 && bytes >= total.Value ? Math.Max(0, total.Value - 1) : bytes;
		progress(visible, total);
	}

	private static async Task ValidateHashAsync(string path, string? expectedSha256, CancellationToken token)
	{
		if (string.IsNullOrWhiteSpace(expectedSha256))
			return;
		string expected = expectedSha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? expectedSha256[7..] : expectedSha256;
		if (expected.Length != 64)
			throw new CryptographicException("The expected SHA256 digest is invalid");
		using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
		byte[] actual;
		try
		{
			while (true)
			{
				int read = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
				if (read == 0)
					break;
				hash.AppendData(buffer, 0, read);
			}
			actual = hash.GetHashAndReset();
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
		if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected)))
			throw new CryptographicException("The download SHA256 digest does not match");
	}

	private static void Delete(string path)
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
