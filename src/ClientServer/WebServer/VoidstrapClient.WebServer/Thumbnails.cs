using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

internal static class Thumbnails
{
	internal sealed class ThumbnailResponse
	{
		public byte[] Data { get; init; } = Array.Empty<byte>();
		public string ContentType { get; init; } = "image/png";
	}

	private class ThumbnailsAssetsData
	{
		[JsonPropertyName("targetId")]
		public long TargetId { get; set; }

		[JsonPropertyName("state")]
		public string? State { get; set; }

		[JsonPropertyName("imageUrl")]
		public string? ImageUrl { get; set; }
	}

	private class ThumbnailsAssets
	{
		[JsonPropertyName("data")]
		public ThumbnailsAssetsData[]? Data { get; set; }
	}

	private static readonly (int Width, int Height)[] _validThumbnailSizes;

	private static readonly byte[] _deletedImageBytes;

	public static string GetClosestValidSize(int x, int y)
	{
		(int Width, int Height) closest = _validThumbnailSizes[0];
		long closestDistance = long.MaxValue;
		foreach ((int width, int height) in _validThumbnailSizes)
		{
			long distance = Math.Abs((long)width - x) + Math.Abs((long)height - y);
			if (distance < closestDistance)
			{
				closest = (width, height);
				closestDistance = distance;
			}
		}
		return $"{closest.Width}x{closest.Height}";
	}

	public static async Task<ThumbnailResponse> GetThumbnail(ulong assetId, int x, int y, string format, CancellationToken cancellationToken)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		deadline.CancelAfter(TimeSpan.FromSeconds(15));
		CancellationToken operationToken = deadline.Token;
		string closestValidSize = GetClosestValidSize(x, y);
		string content = JsonSerializer.Serialize(new[]
		{
			new
			{
				targetId = assetId,
				type = "Asset",
				size = closestValidSize,
				format = format,
				isCircular = false
			}
		});
		for (int i = 1; i <= 3; i++)
		{
			try
			{
				using var bodyContent = new StringContent(content, Encoding.UTF8, "application/json");
				using var request = new HttpRequestMessage(HttpMethod.Post, "https://thumbnails.roblox.com/v1/batch") { Content = bodyContent };
				using HttpResponseMessage httpResponseMessage = await Common.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operationToken);
				if (!httpResponseMessage.IsSuccessStatusCode)
				{
					if (httpResponseMessage.StatusCode != HttpStatusCode.TooManyRequests)
					{
						break;
					}
					await Task.Delay(500, operationToken);
					continue;
				}
				byte[] responseBody = await ReadBoundedAsync(httpResponseMessage.Content, 262144, operationToken);
				ThumbnailsAssets? thumbnailsAssets = JsonSerializer.Deserialize<ThumbnailsAssets>(responseBody);
				ThumbnailsAssetsData? thumbnailsAssetsData = thumbnailsAssets?.Data?.FirstOrDefault();
				if (!string.Equals(thumbnailsAssetsData?.State, "Completed", StringComparison.Ordinal))
				{
					if (i < 3)
					{
						await Task.Delay(500, operationToken);
					}
					continue;
				}
				if (!Uri.TryCreate(thumbnailsAssetsData.ImageUrl, UriKind.Absolute, out Uri? imageUri) ||
					imageUri.Scheme != Uri.UriSchemeHttps ||
					(!string.Equals(imageUri.Host, "rbxcdn.com", StringComparison.OrdinalIgnoreCase) && !imageUri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase)))
					continue;
				using var imageRequest = new HttpRequestMessage(HttpMethod.Get, imageUri);
				using HttpResponseMessage imageResponse = await Common.HttpClient.SendAsync(imageRequest, HttpCompletionOption.ResponseHeadersRead, operationToken);
				if (!imageResponse.IsSuccessStatusCode)
					continue;
				string contentType = imageResponse.Content.Headers.ContentType?.MediaType ?? "";
				if (!string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase) && !string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
					continue;
				byte[] image = await ReadBoundedAsync(imageResponse.Content, 10485760, operationToken);
				return new ThumbnailResponse { Data = image, ContentType = contentType.ToLowerInvariant() };
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (OperationCanceledException) when (deadline.IsCancellationRequested)
			{
				break;
			}
			catch
			{
			}
		}
		return new ThumbnailResponse { Data = _deletedImageBytes, ContentType = "image/png" };
	}

	private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
	{
		if (content.Headers.ContentLength is long length && length > maximumBytes)
			throw new InvalidDataException("Thumbnail response exceeds the size limit");
		await using Stream input = await content.ReadAsStreamAsync(cancellationToken);
		using var output = new MemoryStream(content.Headers.ContentLength is long knownLength ? (int)Math.Min(knownLength, maximumBytes) : 0);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
		try
		{
			while (true)
			{
				int remaining = maximumBytes + 1 - (int)output.Length;
				int read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
				if (read == 0)
					break;
				await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
				if (output.Length > maximumBytes)
					throw new InvalidDataException("Thumbnail response exceeds the size limit");
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
		return output.ToArray();
	}

	static Thumbnails()
	{
		_validThumbnailSizes = new (int Width, int Height)[]
		{
			(30, 30),
			(42, 42),
			(50, 50),
			(75, 75),
			(110, 110),
			(140, 140),
			(150, 150),
			(160, 100),
			(160, 600),
			(250, 250),
			(256, 144),
			(300, 250),
			(384, 216),
			(420, 420),
			(480, 270),
			(512, 512),
			(576, 324),
			(700, 700),
			(728, 90),
			(768, 432)
		};
		string path = Path.Combine(PathHelper.ThumbnailsDeprecated, "deleted.png");
		_deletedImageBytes = Config.ReadBytesFile(path, 10485760);
	}
}
