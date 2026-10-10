using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public enum RobloxChatStatus
{
	Ready,
	NotSignedIn,
	SignInExpired,
	RateLimited,
	Unavailable
}

public sealed class RobloxChatUser
{
	public long Id { get; init; }

	public string Name { get; set; } = string.Empty;

	public string DisplayName { get; set; } = string.Empty;

	public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
}

public sealed class RobloxChatMessage
{
	public string Id { get; init; } = string.Empty;

	public long SenderId { get; init; }

	public string Content { get; init; } = string.Empty;

	public DateTimeOffset CreatedUtc { get; init; }

	public bool Moderated { get; init; }
}

public sealed class RobloxConversation
{
	public string Id { get; init; } = string.Empty;

	public string Type { get; init; } = string.Empty;

	public string Name { get; init; } = string.Empty;

	public List<long> ParticipantIds { get; init; } = new();

	public RobloxChatMessage? LastMessage { get; init; }

	public int Unread { get; init; }

	public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed class RobloxChatResult<T>
{
	public RobloxChatStatus Status { get; init; }

	public T? Value { get; init; }

	public string? Cursor { get; init; }

	public static RobloxChatResult<T> Fail(RobloxChatStatus status) => new() { Status = status };
}

// Talks to the chat service the Roblox website uses (apis.roblox.com/platform-chat-api/v1) with the signed in
// account. The endpoints are not publicly documented, so every response is read defensively and accepts both
// snake_case and camelCase field names.
public static class RobloxChat
{
	private const string LOG_IDENT = "RobloxChat";
	private const string ChatApi = "https://apis.roblox.com/platform-chat-api/v1/";
	private const int MaxResponseBytes = 4 * 1024 * 1024;
	public const int MaxMessageLength = 500;

	private static readonly HttpClient Client = CreateClient();
	private static readonly object UserLock = new();
	private static readonly Dictionary<long, RobloxChatUser> Users = new();
	private static string _csrf = string.Empty;
	private static (int CookieHash, long UserId)? _self;

	private static HttpClient CreateClient()
	{
		HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(12), handler =>
		{
			handler.UseCookies = false;
			handler.AllowAutoRedirect = false;
			handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
		});
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap/1.0");
		return client;
	}

	public static RobloxChatUser? GetCachedUser(long id)
	{
		lock (UserLock)
			return Users.TryGetValue(id, out RobloxChatUser? user) ? user : null;
	}

	public static async Task<RobloxChatResult<long>> GetSelfAsync(CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return RobloxChatResult<long>.Fail(RobloxChatStatus.NotSignedIn);
		int hash = StringComparer.Ordinal.GetHashCode(cookie);
		if (_self is { } known && known.CookieHash == hash)
			return new RobloxChatResult<long> { Status = RobloxChatStatus.Ready, Value = known.UserId };
		(RobloxChatStatus status, JsonDocument? document) = await SendAsync(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated", null, cookie, token).ConfigureAwait(false);
		using (document)
		{
			if (document == null)
				return RobloxChatResult<long>.Fail(status);
			long id = ReadLong(document.RootElement, "id");
			if (id <= 0)
				return RobloxChatResult<long>.Fail(RobloxChatStatus.Unavailable);
			RememberUser(new RobloxChatUser { Id = id, Name = ReadString(document.RootElement, "name"), DisplayName = ReadString(document.RootElement, "displayName") });
			_self = (hash, id);
			return new RobloxChatResult<long> { Status = RobloxChatStatus.Ready, Value = id };
		}
	}

	public static async Task<RobloxChatResult<List<RobloxConversation>>> GetConversationsAsync(string? cursor, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return RobloxChatResult<List<RobloxConversation>>.Fail(RobloxChatStatus.NotSignedIn);
		string url = ChatApi + "get-user-conversations?include_user_data=true&pageSize=30";
		if (!string.IsNullOrEmpty(cursor))
			url += "&cursor=" + Uri.EscapeDataString(cursor);
		(RobloxChatStatus status, JsonDocument? document) = await SendAsync(HttpMethod.Get, url, null, cookie, token).ConfigureAwait(false);
		using (document)
		{
			if (document == null)
				return RobloxChatResult<List<RobloxConversation>>.Fail(status);
			List<RobloxConversation> conversations = new();
			if (TryGetArray(document.RootElement, out JsonElement items, "conversations", "data"))
			{
				foreach (JsonElement item in items.EnumerateArray())
				{
					if (ParseConversation(item) is { } conversation)
						conversations.Add(conversation);
				}
			}
			await ResolveUsersAsync(conversations.SelectMany(c => c.ParticipantIds), token).ConfigureAwait(false);
			return new RobloxChatResult<List<RobloxConversation>>
			{
				Status = RobloxChatStatus.Ready,
				Value = conversations,
				Cursor = ReadString(document.RootElement, "next_cursor", "nextCursor", "nextPageCursor")
			};
		}
	}

	public static async Task<RobloxChatResult<List<RobloxChatMessage>>> GetMessagesAsync(string conversationId, string? cursor, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return RobloxChatResult<List<RobloxChatMessage>>.Fail(RobloxChatStatus.NotSignedIn);
		string url = ChatApi + "get-conversation-messages?conversation_id=" + Uri.EscapeDataString(conversationId) + "&pageSize=30";
		if (!string.IsNullOrEmpty(cursor))
			url += "&cursor=" + Uri.EscapeDataString(cursor);
		(RobloxChatStatus status, JsonDocument? document) = await SendAsync(HttpMethod.Get, url, null, cookie, token).ConfigureAwait(false);
		using (document)
		{
			if (document == null)
				return RobloxChatResult<List<RobloxChatMessage>>.Fail(status);
			List<RobloxChatMessage> messages = new();
			if (TryGetArray(document.RootElement, out JsonElement items, "messages", "data"))
			{
				foreach (JsonElement item in items.EnumerateArray())
				{
					if (ParseMessage(item) is { } message)
						messages.Add(message);
				}
			}
			await ResolveUsersAsync(messages.Select(m => m.SenderId), token).ConfigureAwait(false);
			return new RobloxChatResult<List<RobloxChatMessage>>
			{
				Status = RobloxChatStatus.Ready,
				Value = messages.OrderBy(m => m.CreatedUtc).ToList(),
				Cursor = ReadString(document.RootElement, "next_cursor", "nextCursor", "nextPageCursor")
			};
		}
	}

	public static async Task<RobloxChatResult<RobloxChatMessage>> SendMessageAsync(string conversationId, string text, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return RobloxChatResult<RobloxChatMessage>.Fail(RobloxChatStatus.NotSignedIn);
		string content = text.Trim();
		if (content.Length == 0)
			return RobloxChatResult<RobloxChatMessage>.Fail(RobloxChatStatus.Unavailable);
		if (content.Length > MaxMessageLength)
			content = content[..MaxMessageLength];
		string body = JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["conversation_id"] = conversationId,
			["messages"] = new[] { new Dictionary<string, string> { ["content"] = content } }
		});
		(RobloxChatStatus status, JsonDocument? document) = await SendAsync(HttpMethod.Post, ChatApi + "send-messages", body, cookie, token).ConfigureAwait(false);
		using (document)
		{
			if (document == null)
				return RobloxChatResult<RobloxChatMessage>.Fail(status);
			RobloxChatMessage? sent = null;
			if (TryGetArray(document.RootElement, out JsonElement items, "messages", "data"))
				sent = items.EnumerateArray().Select(ParseMessage).FirstOrDefault(m => m != null);
			return new RobloxChatResult<RobloxChatMessage> { Status = RobloxChatStatus.Ready, Value = sent };
		}
	}

	public static async Task<RobloxChatResult<RobloxConversation>> OpenDirectAsync(long userId, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return RobloxChatResult<RobloxConversation>.Fail(RobloxChatStatus.NotSignedIn);
		// Roblox returns the existing one to one conversation when there already is one
		string body = JsonSerializer.Serialize(new Dictionary<string, object>
		{
			["conversations"] = new[] { new Dictionary<string, object> { ["type"] = "one_to_one", ["participant_user_ids"] = new[] { userId } } },
			["include_user_data"] = true
		});
		(RobloxChatStatus status, JsonDocument? document) = await SendAsync(HttpMethod.Post, ChatApi + "create-conversations", body, cookie, token).ConfigureAwait(false);
		using (document)
		{
			if (document == null)
				return RobloxChatResult<RobloxConversation>.Fail(status);
			RobloxConversation? conversation = null;
			if (TryGetArray(document.RootElement, out JsonElement items, "conversations", "data"))
				conversation = items.EnumerateArray().Select(ParseConversation).FirstOrDefault(c => c != null);
			if (conversation == null)
				return RobloxChatResult<RobloxConversation>.Fail(RobloxChatStatus.Unavailable);
			await ResolveUsersAsync(conversation.ParticipantIds, token).ConfigureAwait(false);
			return new RobloxChatResult<RobloxConversation> { Status = RobloxChatStatus.Ready, Value = conversation };
		}
	}

	public static async Task SendTypingAsync(string conversationId, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return;
		string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["conversation_id"] = conversationId });
		(_, JsonDocument? document) = await SendAsync(HttpMethod.Post, ChatApi + "update-typing-status", body, cookie, token).ConfigureAwait(false);
		document?.Dispose();
	}

	public static async Task<RobloxChatResult<List<RobloxChatUser>>> GetFriendsAsync(long userId, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return RobloxChatResult<List<RobloxChatUser>>.Fail(RobloxChatStatus.NotSignedIn);
		(RobloxChatStatus status, JsonDocument? document) = await SendAsync(HttpMethod.Get, $"https://friends.roblox.com/v1/users/{userId}/friends", null, cookie, token).ConfigureAwait(false);
		using (document)
		{
			if (document == null)
				return RobloxChatResult<List<RobloxChatUser>>.Fail(status);
			List<long> ids = new();
			if (TryGetArray(document.RootElement, out JsonElement items, "data"))
			{
				foreach (JsonElement item in items.EnumerateArray())
				{
					long id = ReadLong(item, "id");
					if (id <= 0)
						continue;
					ids.Add(id);
					string name = ReadString(item, "name");
					if (name.Length > 0)
						RememberUser(new RobloxChatUser { Id = id, Name = name, DisplayName = ReadString(item, "displayName") });
				}
			}
			await ResolveUsersAsync(ids, token).ConfigureAwait(false);
			List<RobloxChatUser> friends = ids.Distinct().Select(id => GetCachedUser(id) ?? new RobloxChatUser { Id = id, Name = id.ToString(CultureInfo.InvariantCulture) })
				.OrderBy(user => user.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
			return new RobloxChatResult<List<RobloxChatUser>> { Status = RobloxChatStatus.Ready, Value = friends };
		}
	}

	public static async Task<Dictionary<long, string>> GetHeadshotUrlsAsync(IEnumerable<long> userIds, CancellationToken token)
	{
		Dictionary<long, string> urls = new();
		List<long> ids = userIds.Where(id => id > 0).Distinct().ToList();
		for (int i = 0; i < ids.Count; i += 100)
		{
			string url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + string.Join(",", ids.Skip(i).Take(100))
				+ "&size=48x48&format=Png&isCircular=true";
			(_, JsonDocument? document) = await SendAsync(HttpMethod.Get, url, null, null, token).ConfigureAwait(false);
			using (document)
			{
				if (document == null || !TryGetArray(document.RootElement, out JsonElement items, "data"))
					continue;
				foreach (JsonElement item in items.EnumerateArray())
				{
					long id = ReadLong(item, "targetId");
					string image = ReadString(item, "imageUrl");
					if (id > 0 && image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
						urls[id] = image;
				}
			}
		}
		return urls;
	}

	private static async Task ResolveUsersAsync(IEnumerable<long> userIds, CancellationToken token)
	{
		List<long> missing;
		lock (UserLock)
			missing = userIds.Where(id => id > 0 && !Users.ContainsKey(id)).Distinct().ToList();
		for (int i = 0; i < missing.Count; i += 100)
		{
			string body = JsonSerializer.Serialize(new Dictionary<string, object> { ["userIds"] = missing.Skip(i).Take(100), ["excludeBannedUsers"] = false });
			(_, JsonDocument? document) = await SendAsync(HttpMethod.Post, "https://users.roblox.com/v1/users", body, null, token).ConfigureAwait(false);
			using (document)
			{
				if (document == null || !TryGetArray(document.RootElement, out JsonElement items, "data"))
					continue;
				foreach (JsonElement item in items.EnumerateArray())
				{
					long id = ReadLong(item, "id");
					if (id > 0)
						RememberUser(new RobloxChatUser { Id = id, Name = ReadString(item, "name"), DisplayName = ReadString(item, "displayName") });
				}
			}
		}
	}

	private static void RememberUser(RobloxChatUser user)
	{
		if (user.Id <= 0 || user.Name.Length == 0 && user.DisplayName.Length == 0)
			return;
		lock (UserLock)
		{
			if (Users.Count > 4096)
				Users.Clear();
			Users[user.Id] = user;
		}
	}

	private static RobloxConversation? ParseConversation(JsonElement item)
	{
		if (item.ValueKind != JsonValueKind.Object)
			return null;
		string id = ReadString(item, "id", "conversation_id", "conversationId");
		if (id.Length == 0)
			return null;
		List<long> participants = new();
		if (TryGetArray(item, out JsonElement ids, "participant_user_ids", "participantUserIds"))
		{
			foreach (JsonElement value in ids.EnumerateArray())
			{
				if (value.TryGetInt64(out long participant) && participant > 0)
					participants.Add(participant);
			}
		}
		if (TryGetProperty(item, out JsonElement userData, "user_data", "userData") && userData.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty entry in userData.EnumerateObject())
			{
				if (!long.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out long userId) || entry.Value.ValueKind != JsonValueKind.Object)
					continue;
				RememberUser(new RobloxChatUser
				{
					Id = userId,
					Name = ReadString(entry.Value, "name", "username"),
					DisplayName = ReadString(entry.Value, "display_name", "displayName", "combined_name")
				});
				if (!participants.Contains(userId))
					participants.Add(userId);
			}
		}
		RobloxChatMessage? last = null;
		if (TryGetArray(item, out JsonElement messages, "messages"))
			last = messages.EnumerateArray().Select(ParseMessage).Where(m => m != null).OrderByDescending(m => m!.CreatedUtc).FirstOrDefault();
		return new RobloxConversation
		{
			Id = id,
			Type = ReadString(item, "type", "conversation_type"),
			Name = ReadString(item, "name", "title"),
			ParticipantIds = participants,
			LastMessage = last,
			Unread = (int)Math.Clamp(ReadLong(item, "unread_message_count", "unreadMessageCount"), 0, 9999),
			UpdatedUtc = ReadTime(item, "updated_at", "updatedAt", "last_updated") ?? last?.CreatedUtc ?? DateTimeOffset.MinValue
		};
	}

	private static RobloxChatMessage? ParseMessage(JsonElement item)
	{
		if (item.ValueKind != JsonValueKind.Object)
			return null;
		string id = ReadString(item, "id", "message_id", "messageId");
		string content = ReadString(item, "content", "text", "content_text");
		string status = ReadString(item, "moderation_type", "status");
		if (id.Length == 0 && content.Length == 0)
			return null;
		return new RobloxChatMessage
		{
			Id = id,
			SenderId = ReadLong(item, "sender_user_id", "senderUserId", "sender_target_id", "senderTargetId"),
			Content = content,
			CreatedUtc = ReadTime(item, "created_at", "createdAt", "sent") ?? DateTimeOffset.UtcNow,
			Moderated = status.Contains("moderat", StringComparison.OrdinalIgnoreCase)
		};
	}

	private static async Task<(RobloxChatStatus Status, JsonDocument? Document)> SendAsync(HttpMethod method, string url, string? body, string? cookie, CancellationToken token)
	{
		Uri uri = new(url);
		// The account cookie is only ever sent to Roblox over https
		if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".roblox.com", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("Refusing to send a chat request outside roblox.com");
		for (int attempt = 0; attempt < 2; attempt++)
		{
			using HttpRequestMessage request = new(method, uri);
			if (body != null)
				request.Content = new StringContent(body, Encoding.UTF8, "application/json");
			if (!string.IsNullOrEmpty(cookie))
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			string csrf = _csrf;
			if (method != HttpMethod.Get && csrf.Length > 0)
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
			if (response.StatusCode == HttpStatusCode.Unauthorized)
				return (RobloxChatStatus.SignInExpired, null);
			if ((int)response.StatusCode == 429)
				return (RobloxChatStatus.RateLimited, null);
			if (!response.IsSuccessStatusCode)
			{
				App.Logger.WriteLine(LOG_IDENT, $"{uri.Host}{uri.AbsolutePath} answered {(int)response.StatusCode} {response.StatusCode}");
				return (RobloxChatStatus.Unavailable, null);
			}
			string text = await Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);
			try
			{
				return (RobloxChatStatus.Ready, JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text));
			}
			catch (JsonException ex)
			{
				App.Logger.WriteLine(LOG_IDENT, $"{uri.AbsolutePath} returned unreadable data: " + ex.Message);
				return (RobloxChatStatus.Unavailable, null);
			}
		}
		return (RobloxChatStatus.Unavailable, null);
	}

	private static bool TryGetProperty(JsonElement element, out JsonElement value, params string[] names)
	{
		value = default;
		if (element.ValueKind != JsonValueKind.Object)
			return false;
		foreach (string name in names)
		{
			if (element.TryGetProperty(name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
				return true;
		}
		return false;
	}

	private static bool TryGetArray(JsonElement element, out JsonElement value, params string[] names)
		=> TryGetProperty(element, out value, names) && value.ValueKind == JsonValueKind.Array;

	private static string ReadString(JsonElement element, params string[] names)
	{
		if (!TryGetProperty(element, out JsonElement value, names))
			return string.Empty;
		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString() ?? string.Empty,
			JsonValueKind.Number => value.GetRawText(),
			_ => string.Empty
		};
	}

	private static long ReadLong(JsonElement element, params string[] names)
	{
		if (!TryGetProperty(element, out JsonElement value, names))
			return 0;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
			return number;
		return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : 0;
	}

	private static DateTimeOffset? ReadTime(JsonElement element, params string[] names)
	{
		if (!TryGetProperty(element, out JsonElement value, names))
			return null;
		if (value.ValueKind == JsonValueKind.String
			&& DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
			return parsed;
		if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long epoch))
			return epoch > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(epoch) : DateTimeOffset.FromUnixTimeSeconds(epoch);
		return null;
	}
}
