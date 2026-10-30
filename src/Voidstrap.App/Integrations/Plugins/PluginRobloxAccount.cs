using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginRobloxAccount
{
	private static readonly HashSet<string> ReadHosts = new(StringComparer.OrdinalIgnoreCase)
	{
		"users.roblox.com",
		"friends.roblox.com",
		"presence.roblox.com",
		"thumbnails.roblox.com",
		"games.roblox.com",
		"groups.roblox.com",
		"badges.roblox.com",
		"catalog.roblox.com",
		"avatar.roblox.com",
		"inventory.roblox.com",
		"economy.roblox.com",
		"followings.roblox.com",
		"develop.roblox.com",
	};

	private static readonly HashSet<string> ReadOnlyPosts = new(StringComparer.OrdinalIgnoreCase)
	{
		"presence.roblox.com/v1/presence/users",
		"thumbnails.roblox.com/v1/batch",
		"users.roblox.com/v1/users",
		"users.roblox.com/v1/usernames/users",
		"catalog.roblox.com/v1/catalog/items/details",
		"games.roblox.com/v1/games/multiget-playability-status",
	};

	private static readonly HttpClient Client = new(
		new SocketsHttpHandler
		{
			UseCookies = false,
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(8),
		}
	)
	{
		Timeout = TimeSpan.FromSeconds(15),
	};

	private static readonly Dictionary<string, Queue<DateTime>> Calls = new(StringComparer.Ordinal);

	private const int MaxCallsPerMinute = 60;

	private const int MaxResponseBytes = 1024 * 1024;

	private const int MaxBodyBytes = 64 * 1024;

	public static void EnsureAllowed(string mod)
	{
		PluginManifest? manifest = PluginManifest.Discover(Paths.Plugins, report: false).FirstOrDefault(m => m.Name == mod);
		if (manifest == null || !manifest.HasPermission(PluginManifest.RobloxAccountPermission))
			throw new InvalidOperationException(
				"this mod must list \"roblox-account\" under permissions in plugin.json to use the Roblox account"
			);
		if (!PluginConsent.HasGrant(mod, PluginManifest.RobloxAccountPermission))
			throw new InvalidOperationException("the user has not allowed this mod to use their Roblox account");
		lock (Calls)
		{
			if (!Calls.TryGetValue(mod, out Queue<DateTime>? queue))
				Calls[mod] = queue = new Queue<DateTime>();
			DateTime now = DateTime.UtcNow;
			while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1))
				queue.Dequeue();
			if (queue.Count >= MaxCallsPerMinute)
				throw new InvalidOperationException("too many Roblox account requests, at most " + MaxCallsPerMinute + " a minute");
			queue.Enqueue(now);
		}
	}

	public static (Uri Uri, string Method, string? Body) Validate(JsonObject request)
	{
		string url = request["url"]?.ToString() ?? "";
		if (
			!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
			|| uri.Scheme != Uri.UriSchemeHttps
			|| !uri.IsDefaultPort
			|| uri.UserInfo.Length > 0
			|| !ReadHosts.Contains(uri.IdnHost)
		)
			throw new InvalidOperationException(
				"Roblox account requests may only go to these https hosts: " + string.Join(", ", ReadHosts.Order())
			);
		string method = (request["method"]?.ToString() ?? "GET").ToUpperInvariant();
		string key = uri.IdnHost + uri.AbsolutePath.TrimEnd('/');
		if (method == "POST" && !ReadOnlyPosts.Contains(key))
			throw new InvalidOperationException(
				"Roblox account requests are read only, POST is allowed only for lookups: " + string.Join(", ", ReadOnlyPosts.Order())
			);
		if (method is not ("GET" or "POST"))
			throw new InvalidOperationException("Roblox account requests are read only, use GET");
		string? body = request["body"]?.ToString();
		if (body != null && Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
			throw new InvalidOperationException("the request body is too large");
		return (uri, method, body);
	}

	public static async Task<JsonObject> SendAsync(Uri uri, string method, string? body, CancellationToken token)
	{
		string cookie = RobloxCookie.Get() ?? throw new InvalidOperationException("no Roblox account is signed in");
		string? csrf = null;
		for (int attempt = 0; attempt < 2; attempt++)
		{
			using HttpRequestMessage message = new(new HttpMethod(method), uri);
			message.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			message.Headers.TryAddWithoutValidation("Accept", "application/json");
			if (csrf != null)
				message.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
			if (body != null)
				message.Content = new StringContent(body, Encoding.UTF8, "application/json");
			using HttpResponseMessage response = await Client
				.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token)
				.ConfigureAwait(false);
			if (
				response.StatusCode == HttpStatusCode.Forbidden
				&& method == "POST"
				&& csrf == null
				&& response.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? tokens)
			)
			{
				csrf = tokens.FirstOrDefault();
				continue;
			}
			string text = await Voidstrap
				.Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token)
				.ConfigureAwait(false);
			if (text.Contains(cookie, StringComparison.Ordinal))
				text = text.Replace(cookie, "[removed]", StringComparison.Ordinal);
			return new JsonObject
			{
				["status"] = (int)response.StatusCode,
				["ok"] = response.IsSuccessStatusCode,
				["text"] = text,
			};
		}
		return new JsonObject
		{
			["status"] = 403,
			["ok"] = false,
			["text"] = "",
		};
	}

	public static async Task<JsonObject> AccountAsync(CancellationToken token)
	{
		RobloxAccount? account = await RobloxCookie.GetAccountAsync(token).ConfigureAwait(false);
		return account == null
			? new JsonObject { ["signedIn"] = false }
			: new JsonObject
			{
				["signedIn"] = true,
				["userId"] = account.UserId,
				["username"] = account.Username,
				["displayName"] = account.DisplayName,
			};
	}
}
