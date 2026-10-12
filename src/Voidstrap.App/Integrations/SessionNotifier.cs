using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Entities;
using Voidstrap.UI;

namespace Voidstrap.Integrations;

public sealed class SessionNote
{
	public string Title { get; init; } = string.Empty;

	public string Text { get; init; } = string.Empty;

	public string? ImageUrl { get; init; }

	public NotificationKind Kind { get; init; }
}

// While a game is open, watches for friends starting a game or joining you, and for badges you earn.
// The first check only records how things are, so nothing that happened before the game started is announced.
public sealed class SessionNotifier : IDisposable
{
	private const string LOG_IDENT = "SessionNotifier";
	private const int MaxApiResponseBytes = 2 * 1024 * 1024;
	private const int MaxFriendNotesPerCheck = 3;
	private static readonly TimeSpan FriendsInterval = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan BadgesInterval = TimeSpan.FromSeconds(60);
	private static readonly HttpClient Client = CreateClient();

	private readonly Func<ActivityData?> _session;
	private readonly Action<SessionNote> _post;
	private readonly CancellationTokenSource _cts = new();
	private Dictionary<long, FriendPresence>? _friends;
	private HashSet<long>? _badges;
	private bool _disposed;
	// Set when Roblox refuses the badge list, it is not asked again for the rest of the game
	private bool _badgesRefused;
	private static System.Net.HttpStatusCode _lastStatus;

	public SessionNotifier(Func<ActivityData?> session, Action<SessionNote> post)
	{
		_session = session;
		_post = post;
	}

	public void Start() => _ = Task.Run(() => RunAsync(_cts.Token));

	private async Task RunAsync(CancellationToken token)
	{
		try
		{
			RobloxChatResult<long> self = await RobloxChat.GetSelfAsync(token).ConfigureAwait(false);
			if (self.Status != RobloxChatStatus.Ready)
			{
				App.Logger.WriteLine(LOG_IDENT, "Friend and badge notifications are off because Roblox is not signed in (" + self.Status + ")");
				return;
			}
			long userId = self.Value;
			DateTime nextFriends = DateTime.MinValue;
			DateTime nextBadges = DateTime.MinValue;
			while (!token.IsCancellationRequested)
			{
				DateTime now = DateTime.UtcNow;
				if (now >= nextFriends)
				{
					nextFriends = now + FriendsInterval;
					if (App.Settings.Prop.NotifyFriends)
						await CheckFriendsAsync(userId, token).ConfigureAwait(false);
					else
						_friends = null;
				}
				if (now >= nextBadges)
				{
					nextBadges = now + BadgesInterval;
					if (App.Settings.Prop.NotifyBadges && !_badgesRefused)
						await CheckBadgesAsync(userId, token).ConfigureAwait(false);
					else
						_badges = null;
				}
				await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Stopped watching friends and badges: " + ex.Message);
		}
	}

	private async Task CheckFriendsAsync(long userId, CancellationToken token)
	{
		try
		{
			List<FriendPresence>? presences = await RobloxPresence.GetFriendPresencesAsync(userId, token).ConfigureAwait(false);
			if (presences == null)
				return;
			Dictionary<long, FriendPresence> current = presences.GroupBy(presence => presence.UserId).ToDictionary(group => group.Key, group => group.First());
			Dictionary<long, FriendPresence>? previous = _friends;
			_friends = current;
			if (previous == null || _session() is not { } session)
				return;

			List<(long UserId, string Text)> changes = new();
			foreach (FriendPresence presence in current.Values)
			{
				if (!presence.InGame)
					continue;
				previous.TryGetValue(presence.UserId, out FriendPresence? before);
				bool wasInGame = before?.InGame == true;
				bool inYourServer = presence.GameId.Length > 0 && string.Equals(presence.GameId, session.JobId, StringComparison.OrdinalIgnoreCase);
				bool wasInYourServer = before != null && before.GameId.Length > 0 && string.Equals(before.GameId, session.JobId, StringComparison.OrdinalIgnoreCase);
				bool inYourGame = (session.UniverseId > 0 && presence.UniverseId == session.UniverseId) || (session.PlaceId > 0 && presence.PlaceId == session.PlaceId);
				bool wasInYourGame = before != null && ((session.UniverseId > 0 && before.UniverseId == session.UniverseId) || (session.PlaceId > 0 && before.PlaceId == session.PlaceId));
				if (inYourServer && !wasInYourServer)
					changes.Add((presence.UserId, "Joined your server"));
				else if (inYourGame && !wasInYourGame)
					changes.Add((presence.UserId, "Joined your game"));
				else if (!wasInGame)
					changes.Add((presence.UserId, "Started playing " + (presence.LastLocation.Length > 0 ? presence.LastLocation : "a game")));
			}
			if (changes.Count == 0)
				return;
			if (changes.Count > MaxFriendNotesPerCheck)
				changes = changes.Take(MaxFriendNotesPerCheck).ToList();
			List<ServerFriend> profiles = await RobloxPresence.GetProfilesAsync(changes.Select(change => change.UserId).ToList(), token).ConfigureAwait(false);
			foreach ((long friendId, string text) in changes)
			{
				ServerFriend? profile = profiles.FirstOrDefault(friend => friend.UserId == friendId);
				Post(new SessionNote
				{
					Title = profile?.Label is { Length: > 0 } label ? label : "A friend",
					Text = text,
					ImageUrl = profile?.HeadshotUrl,
					Kind = NotificationKind.Friends
				});
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Friends could not be checked: " + ex.Message);
		}
	}

	private async Task CheckBadgesAsync(long userId, CancellationToken token)
	{
		try
		{
			using JsonDocument? document = await GetJsonAsync($"https://badges.roblox.com/v1/users/{userId}/badges?limit=10&sortOrder=Desc", token, signedIn: true).ConfigureAwait(false);
			if (document == null && _lastStatus is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
			{
				_badgesRefused = true;
				App.Logger.WriteLine(LOG_IDENT, "Roblox does not share your badge list, badge notifications are off for this game");
				return;
			}
			if (document == null || !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				return;
			List<(long Id, string Name)> badges = new();
			foreach (JsonElement item in data.EnumerateArray())
			{
				if (!item.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long badgeId) || badgeId <= 0)
					continue;
				string name = item.TryGetProperty("displayName", out JsonElement display) && display.ValueKind == JsonValueKind.String ? display.GetString() ?? string.Empty : string.Empty;
				if (name.Length == 0 && item.TryGetProperty("name", out JsonElement plain) && plain.ValueKind == JsonValueKind.String)
					name = plain.GetString() ?? string.Empty;
				badges.Add((badgeId, name));
			}
			HashSet<long>? known = _badges;
			if (known == null)
			{
				_badges = badges.Select(badge => badge.Id).ToHashSet();
				return;
			}
			List<(long Id, string Name)> earned = badges.Where(badge => !known.Contains(badge.Id)).ToList();
			foreach ((long id, _) in earned)
				known.Add(id);
			if (earned.Count == 0)
				return;
			Dictionary<long, string> icons = await GetBadgeIconsAsync(earned.Select(badge => badge.Id), token).ConfigureAwait(false);
			foreach ((long id, string name) in earned.Take(3))
			{
				Post(new SessionNote
				{
					Title = name.Length > 0 ? name : "New badge",
					Text = "You earned a badge",
					ImageUrl = icons.TryGetValue(id, out string? url) ? url : null,
					Kind = NotificationKind.Server
				});
			}
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Badges could not be checked: " + ex.Message);
		}
	}

	private static async Task<Dictionary<long, string>> GetBadgeIconsAsync(IEnumerable<long> badgeIds, CancellationToken token)
	{
		Dictionary<long, string> icons = new();
		using JsonDocument? document = await GetJsonAsync("https://thumbnails.roblox.com/v1/badges/icons?badgeIds=" + string.Join(",", badgeIds) + "&size=150x150&format=Png&isCircular=false", token).ConfigureAwait(false);
		if (document == null || !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
			return icons;
		foreach (JsonElement item in data.EnumerateArray())
		{
			if (item.TryGetProperty("targetId", out JsonElement target) && target.ValueKind == JsonValueKind.Number && target.TryGetInt64(out long id)
				&& item.TryGetProperty("imageUrl", out JsonElement url) && url.ValueKind == JsonValueKind.String && url.GetString() is { Length: > 0 } value)
				icons[id] = value;
		}
		return icons;
	}

	private void Post(SessionNote note)
	{
		if (_disposed)
			return;
		try
		{
			_post(note);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "A notification could not be shown: " + ex.Message);
		}
	}

	private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken token, bool signedIn = false)
	{
		using HttpRequestMessage request = new(HttpMethod.Get, url);
		// The badge list needs the Roblox sign in now, it answered 401 without it
		if (signedIn && RobloxCookie.Get() is { Length: > 0 } cookie)
			request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
		using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		_lastStatus = response.StatusCode;
		if (!response.IsSuccessStatusCode)
		{
			App.Logger.WriteLine(LOG_IDENT, $"{new Uri(url).Host} answered {(int)response.StatusCode} {response.StatusCode}");
			return null;
		}
		return JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false));
	}

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

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		// Not disposed, the loop may still be reading the token as it winds down
		_cts.Cancel();
	}
}
