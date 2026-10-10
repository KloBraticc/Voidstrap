using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public sealed class RobloxProfileInfo
{
	public long UserId { get; init; }

	public string Name { get; init; } = string.Empty;

	public string DisplayName { get; init; } = string.Empty;

	public string Description { get; init; } = string.Empty;

	public DateTimeOffset? Created { get; init; }

	public bool Verified { get; init; }

	public long? Friends { get; init; }

	public long? Followers { get; init; }

	public long? Following { get; init; }

	public long? Robux { get; init; }

	public bool? Premium { get; init; }

	public string AvatarUrl { get; init; } = string.Empty;

	public string HeadshotUrl { get; init; } = string.Empty;
}

// Everything about the signed in account that the profile panel shows, fetched at the same time
public static class RobloxProfile
{
	private const string LOG_IDENT = "RobloxProfile";
	private const int MaxApiResponseBytes = 1024 * 1024;
	private static readonly HttpClient Client = CreateClient();

	public static async Task<(RobloxChatStatus Status, RobloxProfileInfo? Profile)> GetMineAsync(CancellationToken token)
	{
		RobloxChatResult<long> self = await RobloxChat.GetSelfAsync(token).ConfigureAwait(false);
		if (self.Status != RobloxChatStatus.Ready)
			return (self.Status, null);
		long id = self.Value;
		string? cookie = RobloxCookie.Get();
		Task<JsonDocument?> user = GetJsonAsync($"https://users.roblox.com/v1/users/{id}", null, token);
		Task<JsonDocument?> friends = GetJsonAsync($"https://friends.roblox.com/v1/users/{id}/friends/count", cookie, token);
		Task<JsonDocument?> followers = GetJsonAsync($"https://friends.roblox.com/v1/users/{id}/followers/count", null, token);
		Task<JsonDocument?> following = GetJsonAsync($"https://friends.roblox.com/v1/users/{id}/followings/count", null, token);
		Task<JsonDocument?> robux = GetJsonAsync($"https://economy.roblox.com/v1/users/{id}/currency", cookie, token);
		Task<JsonDocument?> premium = GetJsonAsync($"https://premiumfeatures.roblox.com/v1/users/{id}/validate-membership", cookie, token);
		Task<JsonDocument?> avatar = GetJsonAsync($"https://thumbnails.roblox.com/v1/users/avatar?userIds={id}&size=420x420&format=Png&isCircular=false", null, token);
		Task<JsonDocument?> headshot = GetJsonAsync($"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={id}&size=150x150&format=Png&isCircular=true", null, token);
		await Task.WhenAll(user, friends, followers, following, robux, premium, avatar, headshot).ConfigureAwait(false);
		using JsonDocument? userDoc = user.Result;
		using JsonDocument? friendsDoc = friends.Result;
		using JsonDocument? followersDoc = followers.Result;
		using JsonDocument? followingDoc = following.Result;
		using JsonDocument? robuxDoc = robux.Result;
		using JsonDocument? premiumDoc = premium.Result;
		using JsonDocument? avatarDoc = avatar.Result;
		using JsonDocument? headshotDoc = headshot.Result;
		RobloxChatUser? cached = RobloxChat.GetCachedUser(id);
		JsonElement? root = userDoc?.RootElement;
		return (RobloxChatStatus.Ready, new RobloxProfileInfo
		{
			UserId = id,
			Name = ReadString(root, "name") is { Length: > 0 } name ? name : cached?.Name ?? string.Empty,
			DisplayName = ReadString(root, "displayName") is { Length: > 0 } display ? display : cached?.DisplayName ?? string.Empty,
			Description = ReadString(root, "description"),
			Created = DateTimeOffset.TryParse(ReadString(root, "created"), out DateTimeOffset created) ? created : null,
			Verified = root is { } r && r.TryGetProperty("hasVerifiedBadge", out JsonElement verified) && verified.ValueKind == JsonValueKind.True,
			Friends = ReadNumber(friendsDoc?.RootElement, "count"),
			Followers = ReadNumber(followersDoc?.RootElement, "count"),
			Following = ReadNumber(followingDoc?.RootElement, "count"),
			Robux = ReadNumber(robuxDoc?.RootElement, "robux"),
			Premium = premiumDoc?.RootElement.ValueKind switch
			{
				JsonValueKind.True => true,
				JsonValueKind.False => false,
				_ => null
			},
			AvatarUrl = FirstImage(avatarDoc),
			HeadshotUrl = FirstImage(headshotDoc)
		});
	}

	private static string FirstImage(JsonDocument? document)
	{
		if (document == null || !document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
			return string.Empty;
		foreach (JsonElement item in data.EnumerateArray())
		{
			if (item.TryGetProperty("imageUrl", out JsonElement url) && url.ValueKind == JsonValueKind.String)
				return url.GetString() ?? string.Empty;
		}
		return string.Empty;
	}

	private static string ReadString(JsonElement? element, string name)
		=> element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

	private static long? ReadNumber(JsonElement? element, string name)
		=> element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number) ? number : null;

	// One failed part never fails the whole profile, it just shows as unavailable
	private static async Task<JsonDocument?> GetJsonAsync(string url, string? cookie, CancellationToken token)
	{
		try
		{
			using HttpRequestMessage request = new(HttpMethod.Get, url);
			if (!string.IsNullOrEmpty(cookie))
				request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				App.Logger.WriteLine(LOG_IDENT, $"{new Uri(url).Host} answered {(int)response.StatusCode} {response.StatusCode}");
				return null;
			}
			return JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(false));
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LOG_IDENT, "Part of your profile could not be loaded: " + ex.Message);
			return null;
		}
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
}
