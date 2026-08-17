using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

public static class ResilientDownload
{
	public static async Task DownloadAsync(HttpClient client, IReadOnlyList<string> urls, string destination, long maxBytes, CancellationToken token = default, string? expectedSha256 = null, Action<long, long?>? progress = null)
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
					await DownloadAttemptAsync(client, urls[source], temporary, maxBytes, token, progress).ConfigureAwait(false);
					ValidateHash(temporary, expectedSha256);
					File.Move(temporary, destination, true);
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

	private static async Task DownloadAttemptAsync(HttpClient client, string url, string temporary, long maxBytes, CancellationToken token, Action<long, long?>? progress)
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
			return;
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
		await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
		await using FileStream output = new(temporary, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
		byte[] buffer = new byte[131072];
		long total = offset;
		while (true)
		{
			using CancellationTokenSource stall = CancellationTokenSource.CreateLinkedTokenSource(token);
			stall.CancelAfter(TimeSpan.FromSeconds(30));
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
			progress?.Invoke(total, expectedTotal);
		}
		await output.FlushAsync(token).ConfigureAwait(false);
		if (total == 0)
			throw new IOException("The download is empty");
		if (expectedTotal.HasValue && total != expectedTotal.Value)
			throw new IOException("The download ended before all bytes were received");
	}

	private static void ValidateHash(string path, string? expectedSha256)
	{
		if (string.IsNullOrWhiteSpace(expectedSha256))
			return;
		string expected = expectedSha256.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? expectedSha256[7..] : expectedSha256;
		if (expected.Length != 64)
			throw new CryptographicException("The expected SHA256 digest is invalid");
		using FileStream stream = File.OpenRead(path);
		byte[] actual = SHA256.HashData(stream);
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
