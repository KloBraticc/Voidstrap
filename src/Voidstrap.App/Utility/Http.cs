using System;
using System.Buffers;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Exceptions;

namespace Voidstrap.Utility;

internal static class Http
{
	private const int DefaultMaxAttempts = 3;
	private const int DefaultMaxResponseBytes = 8 * 1024 * 1024;

	private static readonly TimeSpan DefaultPerCallTimeout = TimeSpan.FromSeconds(20L);

	private static readonly JsonSerializerOptions DefaultJsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		AllowTrailingCommas = true,
		ReadCommentHandling = JsonCommentHandling.Skip
	};

	public static Task<T> GetJson<T>(string url)
	{
		return GetJson<T>(url, CancellationToken.None);
	}

	public static async Task<T> GetJson<T>(string url, CancellationToken token)
	{
		T val = JsonSerializer.Deserialize<T>(await GetString(url, token).ConfigureAwait(continueOnCapturedContext: false), DefaultJsonOptions);
		if (val != null)
		{
			return val;
		}
		throw new InvalidHTTPResponseException("GET " + url + " returned null after deserialization.");
	}

	public static async Task<T?> PostJson<T>(string url, object body, CancellationToken token = default(CancellationToken))
	{
		string jsonBody = JsonSerializer.Serialize(body);
		return await WithRetryAsync(async delegate(CancellationToken ct)
		{
			using StringContent content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
			using CancellationTokenSource perCallCts = LinkedTimeout(ct);
			using HttpResponseMessage response = await App.HttpClient.PostAsync(url, content, perCallCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			ThrowIfShouldNotRetry(response);
			response.EnsureSuccessStatusCode();
			return JsonSerializer.Deserialize<T>(await ReadStringBoundedAsync(response.Content, DefaultMaxResponseBytes, perCallCts.Token).ConfigureAwait(continueOnCapturedContext: false), DefaultJsonOptions);
		}, token).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<string> GetString(string url, CancellationToken token = default(CancellationToken))
	{
		return await WithRetryAsync(async delegate(CancellationToken ct)
		{
			using CancellationTokenSource perCallCts = LinkedTimeout(ct);
			using HttpResponseMessage response = await App.HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, perCallCts.Token).ConfigureAwait(continueOnCapturedContext: false);
			ThrowIfShouldNotRetry(response);
			response.EnsureSuccessStatusCode();
			return await ReadStringBoundedAsync(response.Content, DefaultMaxResponseBytes, perCallCts.Token).ConfigureAwait(continueOnCapturedContext: false);
		}, token).ConfigureAwait(continueOnCapturedContext: false);
	}

	public static async Task<string> GetStringBoundedAsync(HttpClient client, string url, CancellationToken token = default, int maxBytes = DefaultMaxResponseBytes)
	{
		using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		return await ReadStringBoundedAsync(response.Content, maxBytes, token).ConfigureAwait(false);
	}

	public static async Task<byte[]> ReadBytesBoundedAsync(HttpContent content, int maxBytes, CancellationToken token)
	{
		if (maxBytes <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		}
		if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
		{
			throw new InvalidDataException("HTTP response is too large");
		}
		await using Stream input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
		int capacity = content.Headers.ContentLength is long length && length > 0 ? checked((int)length) : Math.Min(81920, maxBytes);
		using MemoryStream output = new MemoryStream(capacity);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(81920, maxBytes));
		try
		{
			while (true)
			{
				int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
				if (read == 0)
				{
					return output.ToArray();
				}
				if (output.Length + read > maxBytes)
				{
					throw new InvalidDataException("HTTP response is too large");
				}
				await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	public static async Task<string> ReadStringBoundedAsync(HttpContent content, int maxBytes, CancellationToken token)
	{
		byte[] bytes = await ReadBytesBoundedAsync(content, maxBytes, token).ConfigureAwait(false);
		return Encoding.UTF8.GetString(bytes);
	}

	public static async Task DownloadToFileBoundedAsync(HttpContent content, string path, long maxBytes, CancellationToken token)
	{
		if (maxBytes <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		}
		if (content.Headers.ContentLength is long contentLength && contentLength > maxBytes)
		{
			throw new InvalidDataException("HTTP response is too large");
		}
		try
		{
			await using Stream input = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
			await using FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
			byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
			long total = 0;
			try
			{
				while (true)
				{
					int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
					if (read == 0)
					{
						break;
					}
					total += read;
					if (total > maxBytes)
					{
						throw new InvalidDataException("HTTP response is too large");
					}
					await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
				}
				await output.FlushAsync(token).ConfigureAwait(false);
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
		catch
		{
			try
			{
				File.Delete(path);
			}
			catch
			{
			}
			throw;
		}
	}

	private static CancellationTokenSource LinkedTimeout(CancellationToken outer)
	{
		CancellationTokenSource cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(outer);
		cancellationTokenSource.CancelAfter(DefaultPerCallTimeout);
		return cancellationTokenSource;
	}

	private static void ThrowIfShouldNotRetry(HttpResponseMessage response)
	{
		int statusCode = (int)response.StatusCode;
		if (statusCode == 429)
		{
			throw new RateLimitedException(ReadRetryAfter(response));
		}
		if (((uint)(statusCode - 400) <= 1u || (uint)(statusCode - 403) <= 1u || statusCode == 410) ? true : false)
		{
			response.EnsureSuccessStatusCode();
		}
	}

	private static TimeSpan ReadRetryAfter(HttpResponseMessage response)
	{
		TimeSpan? delta = response.Headers.RetryAfter?.Delta;
		if (!delta.HasValue && response.Headers.RetryAfter?.Date is DateTimeOffset date)
		{
			delta = date - DateTimeOffset.UtcNow;
		}
		if (!delta.HasValue || delta.Value <= TimeSpan.Zero)
		{
			return TimeSpan.Zero;
		}
		return delta.Value > MaxRetryAfter ? MaxRetryAfter : delta.Value;
	}

	private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(10L);

	private sealed class RateLimitedException : Exception
	{
		public TimeSpan Delay { get; }

		public RateLimitedException(TimeSpan delay)
			: base("The server returned HTTP 429.")
		{
			Delay = delay;
		}
	}

	private static async Task<T> WithRetryAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken outer, int maxAttempts = 3)
	{
		Exception last = null;
		int baseDelayMs = 400;
		TimeSpan retryAfter = TimeSpan.Zero;
		for (int attempt = 1; attempt <= maxAttempts; attempt++)
		{
			outer.ThrowIfCancellationRequested();
			try
			{
				return await action(outer).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException) when (outer.IsCancellationRequested)
			{
				throw;
			}
			catch (HttpRequestException ex2) when (IsTransient(ex2) && attempt < maxAttempts)
			{
				last = ex2;
			}
			catch (TaskCanceledException ex3) when (!outer.IsCancellationRequested && attempt < maxAttempts)
			{
				last = ex3;
			}
			catch (IOException ex4) when (attempt < maxAttempts)
			{
				last = ex4;
			}
			catch (SocketException ex5) when (attempt < maxAttempts)
			{
				last = ex5;
			}
			catch (RateLimitedException ex6) when (attempt < maxAttempts)
			{
				last = ex6;
				retryAfter = ex6.Delay;
			}
			int millisecondsDelay = baseDelayMs * (1 << attempt - 1);
			if (retryAfter > TimeSpan.Zero)
			{
				millisecondsDelay = Math.Max(millisecondsDelay, (int)retryAfter.TotalMilliseconds);
				retryAfter = TimeSpan.Zero;
			}
			try
			{
				await Task.Delay(millisecondsDelay, outer).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
		}
		throw last ?? new InvalidOperationException("HTTP retry exhausted with no exception captured.");
	}

	private static bool IsTransient(HttpRequestException ex)
	{
		if (!ex.StatusCode.HasValue)
		{
			return true;
		}
		switch ((int)ex.StatusCode.Value)
		{
		case 408:
		case 425:
		case 429:
		case 500:
		case 502:
		case 503:
		case 504:
			return true;
		default:
			return false;
		}
	}
}
