using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public sealed class RobloxPrivateServer
{
	public string Name { get; init; } = string.Empty;

	public string JobId { get; init; } = string.Empty;

	public string AccessCode { get; init; } = string.Empty;

	public string OwnerName { get; init; } = string.Empty;

	public int Playing { get; init; }

	public int MaxPlayers { get; init; }

	public string[] PlayerTokens { get; init; } = [];
}

public enum PrivateServersStatus
{
	Ready,
	NotSignedIn,
	Unavailable
}

// Server list extras for the server browser: player headshots from server list tokens, and private servers
public static class RobloxServers
{
	private const string LOG_IDENT = "RobloxServers";
	private const int MaxApiResponseBytes = 4 * 1024 * 1024;
	private const int TokensPerRequest = 100;
	private const int MaxCachedHeadshots = 4000;
	private static readonly HttpClient Client = CreateClient();
	private static readonly ConcurrentDictionary<string, string> Headshots = new(StringComparer.Ordinal);

	// Headshot picture addresses for server list player tokens, cached because the same players show up again
	public static async Task<Dictionary<string, string>> GetHeadshotsAsync(IEnumerable<string> tokens, CancellationToken token)
	{
		Dictionary<string, string> found = new(StringComparer.Ordinal);
		List<string> missing = new();
		foreach (string playerToken in tokens.Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.Ordinal))
		{
			if (Headshots.TryGetValue(playerToken, out string? url))
				found[playerToken] = url;
			else
				missing.Add(playerToken);
		}
		for (int i = 0; i < missing.Count; i += TokensPerRequest)
		{
			List<string> batch = missing.Skip(i).Take(TokensPerRequest).ToList();
			try
			{
				var requests = batch.Select((playerToken, index) => new
				{
					requestId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
					token = playerToken,
					type = "AvatarHeadShot",
					size = "48x48",
					format = "png",
					isCircular = true
				});
				using HttpRequestMessage request = new(HttpMethod.Post, "https://thumbnails.roblox.com/v1/batch")
				{
					Content = new StringContent(JsonSerializer.Serialize(requests), Encoding.UTF8, "application/json")
				};
				using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
				if (!response.IsSuccessStatusCode)
				{
					App.Logger.WriteLine(LOG_IDENT, $"Headshots answered {(int)response.StatusCode} {response.StatusCode}");
					continue;
				}
				using JsonDocument document = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false));
				if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
					continue;
				if (Headshots.Count > MaxCachedHeadshots)
					Headshots.Clear();
				foreach (JsonElement item in data.EnumerateArray())
				{
					if (!item.TryGetProperty("requestId", out JsonElement id) || !int.TryParse(id.GetString(), out int index) || index < 0 || index >= batch.Count)
						continue;
					if (!item.TryGetProperty("imageUrl", out JsonElement url) || url.ValueKind != JsonValueKind.String || url.GetString() is not { Length: > 0 } value)
						continue;
					found[batch[index]] = value;
					Headshots[batch[index]] = value;
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "Headshots could not be loaded: " + ex.Message);
			}
		}
		return found;
	}

	// Private servers of a place that you can join
	public static async Task<(PrivateServersStatus Status, List<RobloxPrivateServer> Servers)> GetPrivateServersAsync(long placeId, CancellationToken token)
	{
		List<RobloxPrivateServer> servers = new();
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return (PrivateServersStatus.NotSignedIn, servers);
		try
		{
			string? cursor = null;
			for (int page = 0; page < 4; page++)
			{
				string url = $"https://games.roblox.com/v1/games/{placeId}/private-servers?limit=25&sortOrder=Desc";
				if (!string.IsNullOrEmpty(cursor))
					url += "&cursor=" + Uri.EscapeDataString(cursor);
				using HttpRequestMessage request = new(HttpMethod.Get, url);
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
				using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
				if (response.StatusCode == HttpStatusCode.Unauthorized)
					return (PrivateServersStatus.NotSignedIn, servers);
				if (!response.IsSuccessStatusCode)
				{
					App.Logger.WriteLine(LOG_IDENT, $"Private servers answered {(int)response.StatusCode} {response.StatusCode}");
					return (servers.Count > 0 ? PrivateServersStatus.Ready : PrivateServersStatus.Unavailable, servers);
				}
				using JsonDocument document = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false));
				JsonElement root = document.RootElement;
				if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement item in data.EnumerateArray())
					{
						string accessCode = ReadString(item, "accessCode");
						if (accessCode.Length == 0)
							continue;
						string owner = item.TryGetProperty("owner", out JsonElement ownerElement) && ownerElement.ValueKind == JsonValueKind.Object
							? (ReadString(ownerElement, "displayName") is { Length: > 0 } display ? display : ReadString(ownerElement, "name"))
							: string.Empty;
						string[] tokens = item.TryGetProperty("playerTokens", out JsonElement list) && list.ValueKind == JsonValueKind.Array
							? list.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString() ?? string.Empty).Where(t => t.Length > 0).Take(12).ToArray()
							: [];
						servers.Add(new RobloxPrivateServer
						{
							Name = ReadString(item, "name"),
							JobId = ReadString(item, "id"),
							AccessCode = accessCode,
							OwnerName = owner,
							Playing = item.TryGetProperty("playing", out JsonElement playing) && playing.ValueKind == JsonValueKind.Number && playing.TryGetInt32(out int count) ? count : tokens.Length,
							MaxPlayers = item.TryGetProperty("maxPlayers", out JsonElement max) && max.ValueKind == JsonValueKind.Number && max.TryGetInt32(out int maximum) ? maximum : 0,
							PlayerTokens = tokens
						});
					}
				}
				cursor = ReadString(root, "nextPageCursor");
				if (cursor.Length == 0)
					break;
			}
			return (PrivateServersStatus.Ready, servers);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Private servers could not be loaded: " + ex.Message);
			return (servers.Count > 0 ? PrivateServersStatus.Ready : PrivateServersStatus.Unavailable, servers);
		}
	}

	private static string ReadString(JsonElement element, string name)
		=> element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

	private static HttpClient CreateClient()
	{
		HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(10L), handler =>
		{
			handler.UseCookies = false;
			handler.AllowAutoRedirect = false;
			handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
		});
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap/1.0");
		return client;
	}
}
