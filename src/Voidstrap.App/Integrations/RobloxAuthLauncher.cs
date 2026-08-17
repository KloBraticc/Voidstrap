using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Voidstrap.Integrations;

public static class RobloxAuthLauncher
{
	private const string LOG_IDENT = "RobloxAuthLauncher";
	private const int MaxApiResponseBytes = 4 * 1024 * 1024;
	private static readonly HttpClient ServerClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(10L));
	private static readonly HttpClient AuthClient = CreateAuthClient();

	private static HttpClient CreateAuthClient()
	{
		HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(15L), handler =>
		{
			handler.UseCookies = false;
			handler.AllowAutoRedirect = false;
		});
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Roblox/WinInet");
		return client;
	}

	public static string? TryGetRobloxSecurityCookie()
	{
		return RobloxCookie.Get();
	}

	public static async Task<string?> BuildRobloxPlayerUriAsync(long placeId, string? specificJobId = null, CancellationToken token = default)
	{
		string text = TryGetRobloxSecurityCookie();
		if (string.IsNullOrEmpty(text))
		{
			App.Logger.WriteLine("RobloxAuthLauncher", "No .ROBLOSECURITY cookie available; cannot build auth-ticket URI.");
			return null;
		}
		string ticket = null;
		try
		{
			ticket = await FetchAuthTicketAsync(text, token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxAuthLauncher", "Ticket fetch failed: " + ex.Message);
		}
		if (string.IsNullOrEmpty(ticket))
		{
			return null;
		}
		long value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string value2 = HttpUtility.UrlEncode((!string.IsNullOrEmpty(specificJobId)) ? $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGameJob&browserTrackerId=0&placeId={placeId}&gameId={specificJobId}&isPlayTogetherGame=false" : $"https://assetgame.roblox.com/game/PlaceLauncher.ashx?request=RequestGame&placeId={placeId}&isPlayTogetherGame=false");
		string value3 = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % int.MaxValue).ToString();
		return $"roblox-player:1+launchmode:play+gameinfo:{ticket}+launchtime:{value}+placelauncherurl:{value2}+browsertrackerid:{value3}+robloxLocale:en_us+gameLocale:en_us+channel:";
	}

	public static async Task<List<string>> ListPublicServerJobIdsAsync(long placeId, CancellationToken token = default)
	{
		List<string> ids = [];
		try
		{
			string requestUri = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?limit=100&sortOrder=Desc";
			using HttpResponseMessage response = await ServerClient.GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(continueOnCapturedContext: false);
			response.EnsureSuccessStatusCode();
			using JsonDocument jsonDocument = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(response.Content, MaxApiResponseBytes, token).ConfigureAwait(continueOnCapturedContext: false));
			if (jsonDocument.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in value.EnumerateArray())
				{
					if (item.TryGetProperty("id", out var value2) && value2.ValueKind == JsonValueKind.String)
					{
						string text = value2.GetString();
						if (!string.IsNullOrEmpty(text))
						{
							ids.Add(text);
						}
					}
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("RobloxAuthLauncher", "ListPublicServerJobIdsAsync failed: " + ex.Message);
		}
		return ids;
	}

	private static async Task<string?> FetchAuthTicketAsync(string cookie, CancellationToken token)
	{
		string value = null;
		for (int attempt = 0; attempt < 2; attempt++)
		{
			using HttpRequestMessage req = new(HttpMethod.Post, "https://auth.roblox.com/v1/authentication-ticket/");
			req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			req.Headers.Referrer = new Uri("https://www.roblox.com/home");
			if (!string.IsNullOrEmpty(value))
			{
				req.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", value);
			}
			req.Content = new StringContent("", Encoding.UTF8, "application/json");
			using HttpResponseMessage httpResponseMessage = await AuthClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(continueOnCapturedContext: false);
			if (httpResponseMessage.StatusCode == HttpStatusCode.Forbidden && httpResponseMessage.Headers.TryGetValues("x-csrf-token", out IEnumerable<string> values))
			{
				value = values.FirstOrDefault();
				if (string.IsNullOrEmpty(value))
				{
					return null;
				}
				continue;
			}
			if (!httpResponseMessage.IsSuccessStatusCode)
			{
				App.Logger.WriteLine("RobloxAuthLauncher", $"Ticket endpoint returned {(int)httpResponseMessage.StatusCode}.");
				return null;
			}
			if (httpResponseMessage.Headers.TryGetValues("rbx-authentication-ticket", out IEnumerable<string> values2))
			{
				return values2.FirstOrDefault();
			}
			return null;
		}
		return null;
	}
}
