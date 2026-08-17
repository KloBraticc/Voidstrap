using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

internal static class AssetDelivery
{
	private const int MaxResponseBytes = 1_000_000;
	private static string _csrfToken = "";

	public static IEnumerable<AssetDeliveryBatchRequest> ConstructBatchRequest(IEnumerable<ulong> ids)
	{
		return ids.Distinct().Take(100).Select(id => new AssetDeliveryBatchRequest
		{
			AssetId = id,
			RequestId = id.ToString()
		});
	}

	private static HttpRequestMessage ConstructHttpRequestMessage(string body)
	{
		HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://assetdelivery.roblox.com/v2/assets/batch");
		request.Headers.TryAddWithoutValidation("user-agent", "Roblox/WinInet");
		string csrfToken = Volatile.Read(ref _csrfToken);
		if (csrfToken.Length > 0)
		{
			request.Headers.TryAddWithoutValidation("x-csrf-token", csrfToken);
		}
		request.Content = new StringContent(body, Encoding.UTF8, "application/json");
		return request;
	}

	private static async Task<string> ReadBoundedStringAsync(HttpContent content, CancellationToken cancellationToken)
	{
		if (content.Headers.ContentLength > MaxResponseBytes)
		{
			throw new InvalidDataException("Asset information response is too large");
		}
		await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
		using MemoryStream buffer = new MemoryStream();
		byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
		try
		{
			while (true)
			{
				int read = await source.ReadAsync(chunk.AsMemory(), cancellationToken);
				if (read == 0)
				{
					return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
				}
				if (buffer.Length + read > MaxResponseBytes)
				{
					throw new InvalidDataException("Asset information response is too large");
				}
				await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(chunk);
		}
	}

	public static async Task<IEnumerable<AssetInformation>> BatchRequest(IEnumerable<AssetDeliveryBatchRequest> request, CancellationToken cancellationToken)
	{
		AssetDeliveryBatchRequest[] requests = request.Take(101).ToArray();
		if (requests.Length > 100 || requests.Any(value => value.RequestId?.Length > 32))
		{
			throw new ArgumentOutOfRangeException(nameof(request));
		}
		string body = JsonSerializer.Serialize(requests);
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(30));
		CancellationToken operationToken = deadline.Token;
		for (int attempt = 1; attempt <= 5; attempt++)
		{
			try
			{
				using HttpRequestMessage message = ConstructHttpRequestMessage(body);
				using HttpResponseMessage response = await Common.HttpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, operationToken);
				if (response.StatusCode == HttpStatusCode.Forbidden)
				{
					if (response.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? values))
					{
						string? token = values.FirstOrDefault();
						if (!string.IsNullOrEmpty(token) && token.Length <= 1024)
						{
							Interlocked.Exchange(ref _csrfToken, token);
							continue;
						}
					}
					break;
				}
				if (!response.IsSuccessStatusCode)
				{
					Logger.Instance.Error($"Got unexpected status code, try {attempt}: {(int)response.StatusCode}");
					break;
				}
				string json = await ReadBoundedStringAsync(response.Content, operationToken);
				return (JsonSerializer.Deserialize<AssetInformation[]>(json) ?? Array.Empty<AssetInformation>()).Take(100).ToArray();
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException) when (deadline.IsCancellationRequested)
			{
				break;
			}
			catch (Exception value)
			{
				Logger.Instance.Warn($"Got exception, try {attempt}: {value}");
			}
		}
		Logger.Instance.Error("Failed to get asset information, retries were exhausted");
		return Array.Empty<AssetInformation>();
	}

	public static Task<IEnumerable<AssetInformation>> BatchRequest(IEnumerable<ulong> ids, CancellationToken cancellationToken)
	{
		return BatchRequest(ConstructBatchRequest(ids), cancellationToken);
	}
}
