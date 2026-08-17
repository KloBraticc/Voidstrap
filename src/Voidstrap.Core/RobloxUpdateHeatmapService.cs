using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed record RobloxUpdateHeatmap(
	string Created,
	string Updated,
	IReadOnlyDictionary<string, IReadOnlyCollection<string>> Days);

public sealed class RobloxUpdateHeatmapService
{
	private const int MaxResponseBytes = 1048576;
	private static readonly HttpClient SharedHttpClient = CreateSharedClient();

	private static HttpClient CreateSharedClient()
	{
		SocketsHttpHandler handler = new()
		{
			AutomaticDecompression = DecompressionMethods.All,
			UseProxy = true,
			Proxy = null,
			DefaultProxyCredentials = CredentialCache.DefaultCredentials,
			ConnectTimeout = TimeSpan.FromSeconds(10),
			PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
			PooledConnectionLifetime = TimeSpan.FromMinutes(2)
		};
		return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
	}

	private readonly HttpClient _httpClient;

	public RobloxUpdateHeatmapService(HttpClient? httpClient = null)
	{
		_httpClient = httpClient ?? SharedHttpClient;
	}

	public async Task<OperationResult<RobloxUpdateHeatmap>> GetAsync(long universeId, CancellationToken cancellationToken = default)
	{
		if (universeId <= 0)
		{
			return OperationResult<RobloxUpdateHeatmap>.Fail("UniverseIdInvalid", "A valid Roblox universe is required");
		}

		Dictionary<string, List<string>> days = new(StringComparer.Ordinal);
		string created = string.Empty;
		string updated = string.Empty;
		List<string> failures = new();
		bool fetched = false;

		try
		{
			using JsonDocument gameDocument = JsonDocument.Parse(await GetStringAsync($"https://games.roblox.com/v1/games?universeIds={universeId}", cancellationToken));
			if (gameDocument.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
			{
				JsonElement game = data[0];
				created = GetString(game, "created");
				updated = GetString(game, "updated");
				fetched = true;
			}
		}
		catch (OperationCanceledException)
		{
			return OperationResult<RobloxUpdateHeatmap>.Fail("OperationCanceled", "Update heatmap loading was canceled");
		}
		catch (Exception exception)
		{
			failures.Add(exception.Message);
		}

		try
		{
			string cursor = string.Empty;
			for (int page = 0; page < 6; page++)
			{
				string url = $"https://badges.roblox.com/v1/universes/{universeId}/badges?limit=100&sortOrder=Asc";
				if (!string.IsNullOrWhiteSpace(cursor))
				{
					url += "&cursor=" + Uri.EscapeDataString(cursor);
				}

				using JsonDocument badgeDocument = JsonDocument.Parse(await GetStringAsync(url, cancellationToken));
				if (badgeDocument.RootElement.TryGetProperty("data", out JsonElement badges) && badges.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement badge in badges.EnumerateArray())
					{
						AddDay(days, GetString(badge, "created"), "Added badge: " + GetString(badge, "name", "Badge"));
					}
					fetched = true;
				}

				cursor = GetString(badgeDocument.RootElement, "nextPageCursor");
				if (string.IsNullOrWhiteSpace(cursor))
				{
					break;
				}
			}
		}

		catch (OperationCanceledException)
		{
			return OperationResult<RobloxUpdateHeatmap>.Fail("OperationCanceled", "Update heatmap loading was canceled");
		}
		catch (Exception exception)
		{
			failures.Add(exception.Message);
		}

		if (!string.IsNullOrWhiteSpace(updated))
		{
			AddDay(days, updated, "Game updated");
		}

		if (!fetched)
		{
			string message = failures.Count == 0 ? "Roblox update data is unavailable" : string.Join(" ", failures);
			return OperationResult<RobloxUpdateHeatmap>.Fail("UpdateHeatmapUnavailable", message, CapabilityState.RequiresExternalRuntime);
		}

		Dictionary<string, IReadOnlyCollection<string>> readonlyDays = new(StringComparer.Ordinal);
		foreach ((string day, List<string> labels) in days)
		{
			readonlyDays[day] = labels.ToArray();
		}

		return OperationResult<RobloxUpdateHeatmap>.Success(new RobloxUpdateHeatmap(created, updated, readonlyDays));
	}

	private async Task<string> GetStringAsync(string url, CancellationToken token)
	{
		Exception? last = null;
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
				response.EnsureSuccessStatusCode();
				return await ReadStringBoundedAsync(response.Content, token);
			}
			catch (HttpRequestException exception) when (!token.IsCancellationRequested && IsTransient(exception.StatusCode))
			{
				last = exception;
				if (attempt + 1 < 3)
				{
					await Task.Delay(150 * (attempt + 1), token);
				}
			}
			catch (TaskCanceledException exception) when (!token.IsCancellationRequested)
			{
				last = exception;
				if (attempt + 1 < 3)
				{
					await Task.Delay(150 * (attempt + 1), token);
				}
			}
		}
		throw last ?? new HttpRequestException("The request failed");
	}

	private static async Task<string> ReadStringBoundedAsync(HttpContent content, CancellationToken token)
	{
		if (content.Headers.ContentLength is long length && length > MaxResponseBytes)
			throw new InvalidDataException("The response is too large");
		await using Stream input = await content.ReadAsStreamAsync(token);
		using MemoryStream output = new MemoryStream(content.Headers.ContentLength is long knownLength ? (int)knownLength : 0);
		byte[] buffer = new byte[65536];
		while (true)
		{
			int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token);
			if (read == 0)
				return Encoding.UTF8.GetString(output.ToArray());
			if (output.Length + read > MaxResponseBytes)
				throw new InvalidDataException("The response is too large");
			await output.WriteAsync(buffer.AsMemory(0, read), token);
		}
	}

	private static bool IsTransient(HttpStatusCode? statusCode)
	{
		return statusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || statusCode >= HttpStatusCode.InternalServerError;
	}

	public static string BuildCallbackScript(RobloxUpdateHeatmap heatmap)
	{
		string json = JsonSerializer.Serialize(new
		{
			created = heatmap.Created,
			updated = heatmap.Updated,
			days = heatmap.Days
		});
		return "if(window.__vsUpdateData)window.__vsUpdateData(" + json + ");";
	}

	private static string GetString(JsonElement element, string name, string fallback = "")
	{
		return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
	}

	private static void AddDay(Dictionary<string, List<string>> days, string isoDate, string label)
	{
		if (!DateTime.TryParse(isoDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime date))
		{
			return;
		}

		string key = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
		if (!days.TryGetValue(key, out List<string>? labels))
		{
			labels = new List<string>();
			days[key] = labels;
		}

		if (labels.Count < 12 && !labels.Contains(label, StringComparer.Ordinal))
		{
			labels.Add(label);
		}
	}
}
