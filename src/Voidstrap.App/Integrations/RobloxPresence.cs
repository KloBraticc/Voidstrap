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

public static class RobloxPresence
{
	private sealed class PresenceEntry
	{
		public long UserId { get; set; }

		public string? GameId { get; set; }
	}

	private const string LOG_IDENT = "RobloxPresence";
	private const int MaxApiResponseBytes = 4 * 1024 * 1024;

	private static readonly HttpClient SharedClient = CreateSharedClient();

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

	public static async Task<List<ServerFriend>> GetFriendsInServerAsync(long localUserId, string jobId, CancellationToken token = default(CancellationToken))
	{
		List<ServerFriend> result = new List<ServerFriend>();
		if (string.IsNullOrEmpty(jobId))
		{
			return result;
		}
		string cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
		{
			return result;
		}
		if (localUserId <= 0)
		{
			return result;
		}
		try
		{
			HttpClient client = SharedClient;
			List<ServerFriend> friends = await GetFriendsAsync(client, cookie, localUserId, token).ConfigureAwait(continueOnCapturedContext: false);
			if (friends.Count == 0)
			{
				return result;
			}
			Dictionary<long, ServerFriend> byId = friends.ToDictionary((ServerFriend f) => f.UserId);
			string csrf = await GetCsrfTokenAsync(client, cookie, token).ConfigureAwait(continueOnCapturedContext: false);
			foreach (List<long> item in Chunk(friends.Select((ServerFriend f) => f.UserId).ToList(), 90))
			{
				if (token.IsCancellationRequested)
				{
					break;
				}
				foreach (PresenceEntry item2 in await GetPresencesAsync(client, cookie, csrf, item, token).ConfigureAwait(continueOnCapturedContext: false))
				{
					if (!string.IsNullOrEmpty(item2.GameId) && string.Equals(item2.GameId, jobId, StringComparison.OrdinalIgnoreCase) && byId.TryGetValue(item2.UserId, out var value))
					{
						result.Add(value);
					}
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxPresence", "GetFriendsInServerAsync failed: " + ex.Message);
		}
		return (from f in result
			group f by f.UserId into g
			select g.First()).OrderBy<ServerFriend, string>((ServerFriend f) => f.Label, StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static async Task<List<ServerFriend>> GetFriendsAsync(HttpClient client, string cookie, long userId, CancellationToken token)
	{
		List<ServerFriend> list = new List<ServerFriend>();
		using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, $"https://friends.roblox.com/v1/users/{userId}/friends");
		req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
		using HttpResponseMessage res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(continueOnCapturedContext: false);
		if (!res.IsSuccessStatusCode)
		{
			return list;
		}
		using JsonDocument jsonDocument = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(res.Content, MaxApiResponseBytes, token).ConfigureAwait(continueOnCapturedContext: false));
		if (!jsonDocument.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Array)
		{
			return list;
		}
		foreach (JsonElement item in value.EnumerateArray())
		{
			JsonElement value2;
			long value3;
			long num = ((item.TryGetProperty("id", out value2) && value2.TryGetInt64(out value3)) ? value3 : 0);
			if (num > 0)
			{
				list.Add(new ServerFriend
				{
					UserId = num,
					Username = (item.TryGetProperty("name", out var value4) ? (value4.GetString() ?? "") : ""),
					DisplayName = (item.TryGetProperty("displayName", out var value5) ? (value5.GetString() ?? "") : "")
				});
			}
		}
		return list;
	}

	private static async Task<string> GetCsrfTokenAsync(HttpClient client, string cookie, CancellationToken token)
	{
		try
		{
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://auth.roblox.com/v2/logout");
			req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			using HttpResponseMessage httpResponseMessage = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(continueOnCapturedContext: false);
			if (httpResponseMessage.Headers.TryGetValues("x-csrf-token", out IEnumerable<string> values))
			{
				return values.FirstOrDefault() ?? "";
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxPresence", "GetCsrfTokenAsync failed: " + ex.Message);
		}
		return "";
	}

	private static async Task<List<PresenceEntry>> GetPresencesAsync(HttpClient client, string cookie, string csrf, List<long> userIds, CancellationToken token)
	{
		List<PresenceEntry> list = new List<PresenceEntry>();
		string payload = JsonSerializer.Serialize(new { userIds });
		for (int attempt = 0; attempt < 2; attempt++)
		{
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://presence.roblox.com/v1/presence/users");
			req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			if (!string.IsNullOrEmpty(csrf))
			{
				req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
			}
			req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
			using HttpResponseMessage res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(continueOnCapturedContext: false);
			if (res.StatusCode != HttpStatusCode.Forbidden || !res.Headers.TryGetValues("x-csrf-token", out IEnumerable<string> values))
			{
				goto IL_01ae;
			}
			string text = values.FirstOrDefault();
			if (string.IsNullOrEmpty(text) || !(text != csrf))
			{
				goto IL_01ae;
			}
			csrf = text;
			goto end_IL_0156;
			IL_01ae:
			if (!res.IsSuccessStatusCode)
			{
				return list;
			}
			using (JsonDocument jsonDocument = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(res.Content, MaxApiResponseBytes, token).ConfigureAwait(continueOnCapturedContext: false)))
			{
				if (!jsonDocument.RootElement.TryGetProperty("userPresences", out var value) || value.ValueKind != JsonValueKind.Array)
				{
					return list;
				}
				foreach (JsonElement item in value.EnumerateArray())
				{
					JsonElement value2;
					long value3;
					long userId = ((item.TryGetProperty("userId", out value2) && value2.TryGetInt64(out value3)) ? value3 : 0);
					JsonElement value4;
					string gameId = ((item.TryGetProperty("gameId", out value4) && value4.ValueKind == JsonValueKind.String) ? value4.GetString() : null);
					list.Add(new PresenceEntry
					{
						UserId = userId,
						GameId = gameId
					});
				}
				return list;
			}
			end_IL_0156:;
		}
		return list;
	}

	private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
	{
		for (int i = 0; i < source.Count; i += size)
		{
			yield return source.GetRange(i, Math.Min(size, source.Count - i));
		}
	}
}
