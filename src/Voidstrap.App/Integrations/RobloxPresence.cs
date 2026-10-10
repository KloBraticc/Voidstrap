using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public enum FriendsInServerStatus
{
	Ready,
	NotSignedIn,
	SignInExpired,
	NoFriends,
	Unavailable
}

public sealed class FriendsInServerResult
{
	public FriendsInServerStatus Status { get; init; }

	public IReadOnlyList<ServerFriend> Friends { get; init; } = [];

	public int FriendCount { get; init; }
}

public sealed class FriendPresence
{
	public long UserId { get; init; }

	public bool InGame { get; init; }

	public string GameId { get; init; } = string.Empty;

	public long PlaceId { get; init; }

	public long UniverseId { get; init; }

	public string LastLocation { get; init; } = string.Empty;
}

public static class RobloxPresence
{
	private sealed class PresenceEntry
	{
		public long UserId { get; set; }

		public string? GameId { get; set; }

		public int Type { get; set; }

		public long PlaceId { get; set; }

		public long UniverseId { get; set; }

		public string LastLocation { get; set; } = string.Empty;
	}

	private sealed class FriendList
	{
		public long UserId { get; init; }

		public DateTime FetchedUtc { get; init; }

		public List<long> Ids { get; init; } = [];
	}

	private const string LOG_IDENT = "RobloxPresence";

	private const int MaxApiResponseBytes = 4 * 1024 * 1024;

	private const int FriendsPageSize = 50;

	private const int MaxFriendPages = 40;

	private const int PresenceBatchSize = 50;

	private const int PresenceConcurrency = 4;

	private static readonly TimeSpan FriendListLifetime = TimeSpan.FromMinutes(5);

	private static readonly TimeSpan ProfileLifetime = TimeSpan.FromMinutes(30);

	private static readonly HttpClient SharedClient = CreateSharedClient();

	private static readonly SemaphoreSlim FriendListGate = new SemaphoreSlim(1, 1);

	private static readonly object ProfileLock = new object();

	private static readonly Dictionary<long, (ServerFriend Friend, DateTime FetchedUtc)> Profiles = new Dictionary<long, (ServerFriend, DateTime)>();

	private static FriendList? _friendList;

	private static (int CookieHash, bool Valid, DateTime CheckedUtc)? _signIn;

	private static string _csrf = string.Empty;

	private static HttpClient CreateSharedClient()
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

	// Where every friend is right now, for the friend notifications. Null when it could not be checked.
	public static async Task<List<FriendPresence>?> GetFriendPresencesAsync(long localUserId, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie) || localUserId <= 0)
			return null;
		if (await IsSignInValidAsync(cookie, token).ConfigureAwait(false) == false)
			return null;
		List<long>? friendIds = await GetFriendIdsAsync(cookie, localUserId, token).ConfigureAwait(false);
		if (friendIds == null)
			return null;
		if (friendIds.Count == 0)
			return [];
		using SemaphoreSlim gate = new SemaphoreSlim(PresenceConcurrency, PresenceConcurrency);
		List<PresenceEntry>?[] answers = await Task.WhenAll(Chunk(friendIds, PresenceBatchSize).Select(async batch =>
		{
			await gate.WaitAsync(token).ConfigureAwait(false);
			try
			{
				return await GetPresencesAsync(cookie, batch, token).ConfigureAwait(false);
			}
			finally
			{
				gate.Release();
			}
		})).ConfigureAwait(false);
		if (answers.All(answer => answer == null))
			return null;
		return answers.Where(answer => answer != null).SelectMany(answer => answer!)
			.Where(entry => entry.UserId > 0 && entry.UserId != localUserId)
			.Select(entry => new FriendPresence
			{
				UserId = entry.UserId,
				InGame = entry.Type == 2,
				GameId = entry.GameId ?? string.Empty,
				PlaceId = entry.PlaceId,
				UniverseId = entry.UniverseId,
				LastLocation = entry.LastLocation
			}).ToList();
	}

	// Names and pictures for a few people, cached
	public static Task<List<ServerFriend>> GetProfilesAsync(List<long> userIds, CancellationToken token) => ResolveProfilesAsync(userIds, token);

	public static async Task<FriendsInServerResult> GetFriendsInServerAsync(long localUserId, string jobId, CancellationToken token = default(CancellationToken))
	{
		if (string.IsNullOrEmpty(jobId) || localUserId <= 0)
			return new FriendsInServerResult { Status = FriendsInServerStatus.Unavailable };

		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return new FriendsInServerResult { Status = FriendsInServerStatus.NotSignedIn };

		try
		{
			if (await IsSignInValidAsync(cookie, token).ConfigureAwait(false) == false)
				return new FriendsInServerResult { Status = FriendsInServerStatus.SignInExpired };

			List<long>? friendIds = await GetFriendIdsAsync(cookie, localUserId, token).ConfigureAwait(false);
			if (friendIds == null)
				return new FriendsInServerResult { Status = FriendsInServerStatus.Unavailable };
			if (friendIds.Count == 0)
				return new FriendsInServerResult { Status = FriendsInServerStatus.NoFriends };

			HashSet<long> present = new HashSet<long>();
			bool anyAnswered = false;
			// Presence batches run a few at a time instead of one after another, large friend lists were slow
			using SemaphoreSlim gate = new SemaphoreSlim(PresenceConcurrency, PresenceConcurrency);
			List<PresenceEntry>?[] answers = await Task.WhenAll(Chunk(friendIds, PresenceBatchSize).Select(async batch =>
			{
				await gate.WaitAsync(token).ConfigureAwait(false);
				try
				{
					return await GetPresencesAsync(cookie, batch, token).ConfigureAwait(false);
				}
				finally
				{
					gate.Release();
				}
			})).ConfigureAwait(false);
			foreach (List<PresenceEntry>? presences in answers)
			{
				if (presences == null)
					continue;
				anyAnswered = true;
				foreach (PresenceEntry presence in presences)
				{
					if (presence.UserId > 0 && presence.UserId != localUserId && string.Equals(presence.GameId, jobId, StringComparison.OrdinalIgnoreCase))
						present.Add(presence.UserId);
				}
			}

			if (!anyAnswered)
				return new FriendsInServerResult { Status = FriendsInServerStatus.Unavailable, FriendCount = friendIds.Count };

			List<ServerFriend> friends = await ResolveProfilesAsync(present.ToList(), token).ConfigureAwait(false);
			return new FriendsInServerResult
			{
				Status = FriendsInServerStatus.Ready,
				FriendCount = friendIds.Count,
				Friends = friends.OrderBy(friend => friend.Label, StringComparer.OrdinalIgnoreCase).ToList()
			};
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Friends in this server could not be checked: " + ex.Message);
			return new FriendsInServerResult { Status = FriendsInServerStatus.Unavailable };
		}
	}

	private static async Task<bool?> IsSignInValidAsync(string cookie, CancellationToken token)
	{
		int hash = StringComparer.Ordinal.GetHashCode(cookie);
		var cached = _signIn;
		if (cached is { } known && known.CookieHash == hash && DateTime.UtcNow - known.CheckedUtc < (known.Valid ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(1)))
			return known.Valid;

		try
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
			request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			using HttpResponseMessage response = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			if (response.IsSuccessStatusCode)
			{
				_signIn = (hash, true, DateTime.UtcNow);
				return true;
			}
			if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
			{
				_signIn = (hash, false, DateTime.UtcNow);
				App.Logger.WriteLine(LOG_IDENT, "The saved Roblox sign in was rejected, friends in this server need a fresh sign in");
				return false;
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "The Roblox sign in could not be checked: " + ex.Message);
		}

		return null;
	}

	private static async Task<List<long>?> GetFriendIdsAsync(string cookie, long userId, CancellationToken token)
	{
		await FriendListGate.WaitAsync(token).ConfigureAwait(false);
		try
		{
			FriendList? cached = _friendList;
			if (cached != null && cached.UserId == userId && DateTime.UtcNow - cached.FetchedUtc < FriendListLifetime)
				return cached.Ids;

			List<long>? ids = await FetchFriendIdsPagedAsync(cookie, userId, token).ConfigureAwait(false)
				?? await FetchFriendIdsLegacyAsync(cookie, userId, token).ConfigureAwait(false);
			if (ids == null)
				return cached != null && cached.UserId == userId ? cached.Ids : null;

			_friendList = new FriendList { UserId = userId, FetchedUtc = DateTime.UtcNow, Ids = ids };
			App.Logger.WriteLine(LOG_IDENT, $"Loaded {ids.Count} friends to look for in this server");
			return ids;
		}
		finally
		{
			FriendListGate.Release();
		}
	}

	private static async Task<List<long>?> FetchFriendIdsPagedAsync(string cookie, long userId, CancellationToken token)
	{
		List<long> ids = new List<long>();
		string? cursor = null;
		for (int page = 0; page < MaxFriendPages; page++)
		{
			string url = $"https://friends.roblox.com/v1/users/{userId}/friends/find?limit={FriendsPageSize}";
			if (!string.IsNullOrEmpty(cursor))
				url += "&cursor=" + Uri.EscapeDataString(cursor);

			using JsonDocument? document = await GetJsonAsync(url, cookie, token).ConfigureAwait(false);
			if (document == null || !document.RootElement.TryGetProperty("PageItems", out JsonElement items) || items.ValueKind != JsonValueKind.Array)
				return page == 0 ? null : ids;

			foreach (JsonElement item in items.EnumerateArray())
			{
				if (item.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long value) && value > 0)
					ids.Add(value);
			}

			cursor = document.RootElement.TryGetProperty("NextCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
			if (string.IsNullOrEmpty(cursor))
				break;
		}

		return ids.Distinct().ToList();
	}

	private static async Task<List<long>?> FetchFriendIdsLegacyAsync(string cookie, long userId, CancellationToken token)
	{
		using JsonDocument? document = await GetJsonAsync($"https://friends.roblox.com/v1/users/{userId}/friends", cookie, token).ConfigureAwait(false);
		if (document == null || !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
			return null;

		List<long> ids = new List<long>();
		foreach (JsonElement item in data.EnumerateArray())
		{
			if (item.TryGetProperty("id", out JsonElement id) && id.TryGetInt64(out long value) && value > 0)
				ids.Add(value);
		}

		return ids.Distinct().ToList();
	}

	private static async Task<List<ServerFriend>> ResolveProfilesAsync(List<long> userIds, CancellationToken token)
	{
		List<ServerFriend> resolved = new List<ServerFriend>();
		List<long> missing = new List<long>();
		lock (ProfileLock)
		{
			foreach (long id in userIds)
			{
				if (Profiles.TryGetValue(id, out var entry) && DateTime.UtcNow - entry.FetchedUtc < ProfileLifetime)
					resolved.Add(entry.Friend);
				else
					missing.Add(id);
			}
		}

		if (missing.Count == 0)
			return resolved;

		Dictionary<long, ServerFriend> fetched = missing.ToDictionary(id => id, id => new ServerFriend { UserId = id });
		// Names and pictures are fetched at the same time; they fill different fields of the same entries
		async Task ResolveNamesAsync()
		{
			try
			{
				string payload = JsonSerializer.Serialize(new { userIds = missing, excludeBannedUsers = false });
				using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/users")
				{
					Content = new StringContent(payload, Encoding.UTF8, "application/json")
				};
				using HttpResponseMessage response = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
				if (response.IsSuccessStatusCode)
				{
					using JsonDocument document = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false));
					if (document.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement item in data.EnumerateArray())
						{
							if (!item.TryGetProperty("id", out JsonElement id) || !id.TryGetInt64(out long value) || !fetched.TryGetValue(value, out ServerFriend? friend))
								continue;
							friend.Username = item.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty;
							friend.DisplayName = item.TryGetProperty("displayName", out JsonElement displayName) ? displayName.GetString() ?? string.Empty : string.Empty;
						}
					}
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LOG_IDENT, "Friend names could not be loaded: " + ex.Message);
			}
		}

		await Task.WhenAll(ResolveNamesAsync(), ResolveHeadshotsAsync(fetched, token)).ConfigureAwait(false);

		lock (ProfileLock)
		{
			foreach (ServerFriend friend in fetched.Values)
			{
				if (!string.IsNullOrWhiteSpace(friend.Username))
					Profiles[friend.UserId] = (friend, DateTime.UtcNow);
			}
		}

		resolved.AddRange(fetched.Values);
		return resolved;
	}

	private static async Task ResolveHeadshotsAsync(Dictionary<long, ServerFriend> friends, CancellationToken token)
	{
		if (friends.Count == 0)
			return;

		try
		{
			string ids = string.Join(",", friends.Keys);
			using JsonDocument? document = await GetJsonAsync($"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={ids}&size=48x48&format=Png&isCircular=false", null, token).ConfigureAwait(false);
			if (document == null || !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				return;

			foreach (JsonElement item in data.EnumerateArray())
			{
				if (item.TryGetProperty("targetId", out JsonElement target) && target.TryGetInt64(out long id)
					&& friends.TryGetValue(id, out ServerFriend? friend)
					&& item.TryGetProperty("imageUrl", out JsonElement url) && url.ValueKind == JsonValueKind.String)
				{
					friend.HeadshotUrl = url.GetString() ?? string.Empty;
				}
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Friend pictures could not be loaded: " + ex.Message);
		}
	}

	private static async Task<JsonDocument?> GetJsonAsync(string url, string? cookie, CancellationToken token)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
		if (!string.IsNullOrEmpty(cookie))
			request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
		using HttpResponseMessage response = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
		{
			App.Logger.WriteLine(LOG_IDENT, $"{new Uri(url).Host} answered {(int)response.StatusCode} {response.StatusCode}");
			return null;
		}

		return JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false));
	}

	private static async Task<List<PresenceEntry>?> GetPresencesAsync(string cookie, List<long> userIds, CancellationToken token)
	{
		string payload = JsonSerializer.Serialize(new { userIds });
		for (int attempt = 0; attempt < 3; attempt++)
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users")
			{
				Content = new StringContent(payload, Encoding.UTF8, "application/json")
			};
			request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			string csrf = _csrf;
			if (!string.IsNullOrEmpty(csrf))
				request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);

			using HttpResponseMessage response = await SharedClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			if (response.StatusCode == HttpStatusCode.Forbidden
				&& response.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? values)
				&& values.FirstOrDefault() is { Length: > 0 } fresh
				&& !string.Equals(fresh, csrf, StringComparison.Ordinal))
			{
				_csrf = fresh;
				continue;
			}

			if ((int)response.StatusCode == 429)
			{
				App.Logger.WriteLine(LOG_IDENT, "Roblox is rate limiting presence checks, trying again later");
				return null;
			}

			if (!response.IsSuccessStatusCode)
			{
				App.Logger.WriteLine(LOG_IDENT, $"Presence check answered {(int)response.StatusCode} {response.StatusCode}");
				return null;
			}

			using JsonDocument document = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false));
			if (!document.RootElement.TryGetProperty("userPresences", out JsonElement presences) || presences.ValueKind != JsonValueKind.Array)
				return null;

			List<PresenceEntry> list = new List<PresenceEntry>();
			foreach (JsonElement item in presences.EnumerateArray())
			{
				// Private locations come back as null, so every number is checked before it is read
				long userId = ReadNumber(item, "userId");
				string? gameId = item.TryGetProperty("gameId", out JsonElement game) && game.ValueKind == JsonValueKind.String ? game.GetString() : null;
				list.Add(new PresenceEntry
				{
					UserId = userId,
					GameId = gameId,
					Type = (int)ReadNumber(item, "userPresenceType"),
					PlaceId = ReadNumber(item, "placeId"),
					UniverseId = ReadNumber(item, "universeId"),
					LastLocation = item.TryGetProperty("lastLocation", out JsonElement location) && location.ValueKind == JsonValueKind.String ? location.GetString() ?? string.Empty : string.Empty
				});
			}

			return list;
		}

		return null;
	}

	private static long ReadNumber(JsonElement item, string name)
		=> item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : 0;

	private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
	{
		for (int i = 0; i < source.Count; i += size)
		{
			yield return source.GetRange(i, Math.Min(size, source.Count - i));
		}
	}
}
