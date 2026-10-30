using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public enum RobloxAuthedStatus
{
	Ok,
	NotSignedIn,
	NotAllowed,
	NotFound,
	RateLimited,
	Failed,
}

public readonly record struct RobloxAuthedResult(RobloxAuthedStatus Status, int HttpStatus, string Body, TimeSpan? RetryAfter)
{
	public bool Succeeded => Status == RobloxAuthedStatus.Ok;
}

public static class RobloxAuthedClient
{
	private const string LogIdent = "RobloxAuthedClient";

	private const int MaxResponseBytes = 4 * 1024 * 1024;

	private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
	{
		"apis.roblox.com",
		"publish.roblox.com",
	};

	private static readonly HttpClient Client = new(
		new SocketsHttpHandler
		{
			UseCookies = false,
			AllowAutoRedirect = false,
			AutomaticDecompression = DecompressionMethods.All,
			PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		}
	)
	{
		Timeout = TimeSpan.FromSeconds(60),
	};

	private static readonly object CsrfGate = new();

	private static string? _csrf;

	public static Task<RobloxAuthedResult> GetAsync(string url, CancellationToken token)
	{
		return SendAsync(HttpMethod.Get, url, null, token);
	}

	public static Task<RobloxAuthedResult> PostJsonAsync(string url, string json, CancellationToken token)
	{
		return SendAsync(HttpMethod.Post, url, () => new StringContent(json, Encoding.UTF8, "application/json"), token);
	}

	public static Task<RobloxAuthedResult> PostAsync(string url, CancellationToken token)
	{
		return SendAsync(HttpMethod.Post, url, null, token);
	}

	public static Task<RobloxAuthedResult> DeleteAsync(string url, CancellationToken token)
	{
		return SendAsync(HttpMethod.Delete, url, null, token);
	}

	public static Task<RobloxAuthedResult> SendMultipartAsync(
		HttpMethod method,
		string url,
		string requestJson,
		byte[] file,
		string fileName,
		CancellationToken token
	)
	{
		return SendAsync(
			method,
			url,
			() =>
			{
				MultipartFormDataContent form = new();
				form.Add(new StringContent(requestJson, Encoding.UTF8, "application/json"), "request");
				ByteArrayContent fileContent = new(file);
				fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
				form.Add(fileContent, "fileContent", fileName);
				return form;
			},
			token
		);
	}

	public static bool IsAllowedUrl(string url, out Uri? uri)
	{
		uri = null;
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
			return false;
		if (
			parsed.Scheme != Uri.UriSchemeHttps
			|| !parsed.IsDefaultPort
			|| !string.IsNullOrEmpty(parsed.UserInfo)
			|| !AllowedHosts.Contains(parsed.Host)
		)
			return false;
		uri = parsed;
		return true;
	}

	private static async Task<RobloxAuthedResult> SendAsync(
		HttpMethod method,
		string url,
		Func<HttpContent>? content,
		CancellationToken token
	)
	{
		if (!IsAllowedUrl(url, out Uri? uri) || uri == null)
			throw new ArgumentException("Only https requests to Roblox API hosts can carry the account cookie", nameof(url));

		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			return new RobloxAuthedResult(RobloxAuthedStatus.NotSignedIn, 0, string.Empty, null);

		for (int attempt = 0; attempt < 2; attempt++)
		{
			using HttpRequestMessage request = new(method, uri);
			request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			request.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
			request.Headers.TryAddWithoutValidation("Accept", "application/json");
			request.Headers.TryAddWithoutValidation("Origin", "https://create.roblox.com");
			request.Headers.TryAddWithoutValidation("Referer", "https://create.roblox.com/");
			string? csrf;
			lock (CsrfGate)
				csrf = _csrf;
			if (method != HttpMethod.Get && csrf != null)
				request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
			if (content != null)
				request.Content = content();

			using HttpResponseMessage response = await Client
				.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
				.ConfigureAwait(false);
			RobloxCookie.ObserveRotation(response, cookie);
			if (
				response.StatusCode == HttpStatusCode.Forbidden
				&& method != HttpMethod.Get
				&& response.Headers.TryGetValues("x-csrf-token", out IEnumerable<string>? values)
			)
			{
				string? fresh = null;
				foreach (string value in values)
				{
					fresh = value;
					break;
				}
				if (!string.IsNullOrEmpty(fresh) && fresh != csrf && attempt == 0)
				{
					lock (CsrfGate)
						_csrf = fresh;
					continue;
				}
			}

			string body = await Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);
			int code = (int)response.StatusCode;
			App.Logger?.WriteLine(LogIdent, method.Method + " " + uri.Host + uri.AbsolutePath + " returned " + code);
			RobloxAuthedStatus status = code switch
			{
				>= 200 and < 300 => RobloxAuthedStatus.Ok,
				401 => RobloxAuthedStatus.NotSignedIn,
				403 => RobloxAuthedStatus.NotAllowed,
				404 => RobloxAuthedStatus.NotFound,
				429 => RobloxAuthedStatus.RateLimited,
				_ => RobloxAuthedStatus.Failed,
			};
			if (status == RobloxAuthedStatus.NotSignedIn)
				RobloxCookie.InvalidateCache();
			return new RobloxAuthedResult(status, code, body, response.Headers.RetryAfter?.Delta);
		}
		return new RobloxAuthedResult(RobloxAuthedStatus.NotAllowed, 403, string.Empty, null);
	}
}
