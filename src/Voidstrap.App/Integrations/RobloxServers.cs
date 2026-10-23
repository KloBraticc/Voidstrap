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

	public long VipServerId { get; init; }
}

public sealed class AccountPrivateServer
{
	public long Id { get; init; }

	public string Name { get; init; } = string.Empty;

	public long UniverseId { get; init; }

	public long PlaceId { get; init; }

	public string GameName { get; init; } = string.Empty;

	public string OwnerName { get; init; } = string.Empty;

	public bool IsMine { get; init; }

	public bool Active { get; set; }

	public DateTime? Expires { get; init; }

	public bool WillRenew { get; init; }
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
	private static string _csrf = string.Empty;
	private static int _accountFieldsLogged;

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
					size = "150x150",
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
				string url = $"https://games.roblox.com/v1/games/{placeId}/private-servers?limit=25&sortOrder=Desc&excludeFriendServers=false";
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
							PlayerTokens = tokens,
							VipServerId = ReadLong(item, "vipServerId")
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

	public static async Task<(PrivateServersStatus Status, List<AccountPrivateServer> Servers)> GetAccountPrivateServersAsync(CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return (PrivateServersStatus.NotSignedIn, new List<AccountPrivateServer>());
		(PrivateServersStatus Status, List<AccountPrivateServer> Servers)[] tabs = await Task.WhenAll(
			GetAccountPrivateServersTabAsync(cookie, "MyPrivateServers", true, token),
			GetAccountPrivateServersTabAsync(cookie, "OtherPrivateServers", false, token)).ConfigureAwait(false);
		HashSet<long> seen = new();
		List<AccountPrivateServer> servers = tabs.SelectMany(tab => tab.Servers).Where(server => seen.Add(server.Id)).ToList();
		if (tabs.Any(tab => tab.Status == PrivateServersStatus.NotSignedIn))
			return (PrivateServersStatus.NotSignedIn, servers);
		if (tabs.All(tab => tab.Status == PrivateServersStatus.Ready) || servers.Count > 0)
			return (PrivateServersStatus.Ready, servers);
		return (PrivateServersStatus.Unavailable, servers);
	}

	private static async Task<(PrivateServersStatus Status, List<AccountPrivateServer> Servers)> GetAccountPrivateServersTabAsync(string cookie, string tab, bool mine, CancellationToken token)
	{
		List<AccountPrivateServer> servers = new();
		try
		{
			string? cursor = null;
			for (int page = 0; page < 10; page++)
			{
				string url = "https://games.roblox.com/v1/private-servers/my-private-servers?privateServersTab=" + tab + "&itemsPerPage=100";
				if (!string.IsNullOrEmpty(cursor))
					url += "&cursor=" + Uri.EscapeDataString(cursor);
				using HttpRequestMessage request = new(HttpMethod.Get, url);
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
				using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
				if (response.StatusCode == HttpStatusCode.Unauthorized)
					return (PrivateServersStatus.NotSignedIn, servers);
				if (!response.IsSuccessStatusCode)
				{
					App.Logger.WriteLine(LOG_IDENT, $"Account private servers ({tab}) answered {(int)response.StatusCode} {response.StatusCode}");
					return (servers.Count > 0 ? PrivateServersStatus.Ready : PrivateServersStatus.Unavailable, servers);
				}
				using JsonDocument document = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false));
				JsonElement root = document.RootElement;
				if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement item in data.EnumerateArray())
					{
						if (Interlocked.Exchange(ref _accountFieldsLogged, 1) == 0 && item.ValueKind == JsonValueKind.Object)
							App.Logger.WriteLine(LOG_IDENT, "Account private server fields: " + string.Join(", ", item.EnumerateObject().Select(property => property.Name)));
						long id = ReadLong(item, "privateServerId");
						if (id <= 0)
							id = ReadLong(item, "vipServerId");
						if (id <= 0)
							id = ReadLong(item, "id");
						if (id <= 0)
							continue;
						DateTime? expires = item.TryGetProperty("expirationDate", out JsonElement expiration) && expiration.ValueKind == JsonValueKind.String && expiration.TryGetDateTime(out DateTime parsed) ? parsed.ToUniversalTime() : null;
						servers.Add(new AccountPrivateServer
						{
							Id = id,
							Name = ReadString(item, "name"),
							UniverseId = ReadLong(item, "universeId"),
							PlaceId = ReadLong(item, "placeId"),
							GameName = ReadString(item, "universeName"),
							OwnerName = ReadString(item, "ownerName"),
							IsMine = mine,
							Active = !item.TryGetProperty("active", out JsonElement active) || active.ValueKind != JsonValueKind.False,
							Expires = expires,
							WillRenew = item.TryGetProperty("willRenew", out JsonElement renew) && renew.ValueKind == JsonValueKind.True
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
			App.Logger.WriteLine(LOG_IDENT, $"Account private servers ({tab}) could not be loaded: " + ex.Message);
			return (servers.Count > 0 ? PrivateServersStatus.Ready : PrivateServersStatus.Unavailable, servers);
		}
	}

	public static async Task<string?> GetAccessCodeAsync(long placeId, long serverId, CancellationToken token)
	{
		(PrivateServersStatus status, List<RobloxPrivateServer> servers) = await GetPrivateServersAsync(placeId, token).ConfigureAwait(false);
		if (status != PrivateServersStatus.Ready)
			return null;
		return servers.FirstOrDefault(server => server.VipServerId == serverId)?.AccessCode;
	}

	public static async Task<bool> SetPrivateServerActiveAsync(long serverId, bool active, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return false;
		string payload = JsonSerializer.Serialize(new { active });
		try
		{
			for (int attempt = 0; attempt < 2; attempt++)
			{
				using HttpRequestMessage request = new(HttpMethod.Patch, "https://games.roblox.com/v1/vip-servers/" + serverId)
				{
					Content = new StringContent(payload, Encoding.UTF8, "application/json")
				};
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
				string csrf = _csrf;
				if (csrf.Length > 0)
					request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
				using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
				if (response.StatusCode == HttpStatusCode.Forbidden
					&& response.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? values)
					&& values.FirstOrDefault() is { Length: > 0 } fresh
					&& !string.Equals(fresh, csrf, StringComparison.Ordinal))
				{
					_csrf = fresh;
					continue;
				}
				if (!response.IsSuccessStatusCode)
				{
					string body = await Utility.Http.ReadStringBoundedAsync(response.Content, 4096, token).ConfigureAwait(false);
					App.Logger.WriteLine(LOG_IDENT, $"Turning a private server {(active ? "on" : "off")} answered {(int)response.StatusCode} {response.StatusCode}: {body}");
					return false;
				}
				return true;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "A private server could not be changed: " + ex.Message);
		}
		return false;
	}

	private static long ReadLong(JsonElement element, string name)
		=> element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;

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
