using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Voidstrap.Integrations;

public static class RobloxCookie
{
	public readonly record struct AssetMeta(string Name, string Type, string CreatorName);

	private const string LOG_IDENT = "RobloxCookie";
	private const int MaxApiResponseBytes = 4 * 1024 * 1024;

	private static readonly Regex WarningRegex = new Regex("(_\\|WARNING:-DO-NOT-SHARE[^\\s;,\"']+)", RegexOptions.Compiled);

	private static readonly Regex NamedRegex = new Regex("\\.ROBLOSECURITY[\\s=]+([^\\s;,\"']+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static string? _cached;

	private static DateTime _cachedAt = DateTime.MinValue;

	private static readonly HttpClient _authClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(12L), handler =>
	{
		handler.UseCookies = false;
		handler.AllowAutoRedirect = false;
	});

	private static DateTime _economyCooldownUntil = DateTime.MinValue;

	private static string CookiesDatPath => Path.Combine(Paths.LocalAppData, "Roblox", "LocalStorage", "RobloxCookies.dat");

	public static bool Exists
	{
		get
		{
			if ((DateTime.UtcNow - _cachedAt).TotalSeconds < 5.0)
			{
				return !string.IsNullOrEmpty(_cached);
			}
			_cached = Get();
			_cachedAt = DateTime.UtcNow;
			return !string.IsNullOrEmpty(_cached);
		}
	}

	public static string? Get()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			return null;
		}
		string text = ReadFromDat();
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		return ReadFromRegistry();
	}

	public static void InvalidateCache()
	{
		_cached = null;
		_cachedAt = DateTime.MinValue;
	}

	public static Task<RobloxAccount?> GetAccountAsync(CancellationToken ct = default(CancellationToken))
	{
		return GetAccountAsync(Get() ?? "", ct);
	}

	public static async Task<RobloxAccount?> GetAccountAsync(string cookie, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrEmpty(cookie))
		{
			return null;
		}
		try
		{
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated");
			req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
			req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
			using HttpResponseMessage res = await _authClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
			if (!res.IsSuccessStatusCode)
			{
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(res.Content, MaxApiResponseBytes, ct).ConfigureAwait(continueOnCapturedContext: false));
			JsonElement rootElement = jsonDocument.RootElement;
			JsonElement value;
			long value2;
			long num = ((rootElement.TryGetProperty("id", out value) && value.TryGetInt64(out value2)) ? value2 : 0);
			JsonElement value3;
			string text2 = (rootElement.TryGetProperty("name", out value3) ? (value3.GetString() ?? "") : "");
			JsonElement value4;
			string displayName = (rootElement.TryGetProperty("displayName", out value4) ? (value4.GetString() ?? "") : "");
			if (num == 0L || string.IsNullOrEmpty(text2))
			{
				return null;
			}
			return new RobloxAccount
			{
				UserId = num,
				Username = text2,
				DisplayName = displayName
			};
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RobloxCookie", "GetAccountAsync failed: " + ex.Message);
			return null;
		}
	}

	public static string? ExtractCookieFromDat(string datPath)
	{
		try
		{
			if (string.IsNullOrEmpty(datPath) || !File.Exists(datPath))
			{
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(datPath));
			if (!jsonDocument.RootElement.TryGetProperty("CookiesData", out var value))
			{
				return null;
			}
			string text = value.GetString();
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser);
			return Extract(Encoding.UTF8.GetString(bytes));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RobloxCookie", "ExtractCookieFromDat failed: " + ex.Message);
			return null;
		}
	}

	public static bool SynthesizeDatWithCookie(string templateDatPath, string newCookie, string outputDatPath)
	{
		try
		{
			if (string.IsNullOrEmpty(newCookie) || string.IsNullOrEmpty(templateDatPath) || !File.Exists(templateDatPath))
			{
				return false;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(templateDatPath));
			if (!jsonDocument.RootElement.TryGetProperty("CookiesData", out var value))
			{
				return false;
			}
			string text = value.GetString();
			if (string.IsNullOrEmpty(text))
			{
				return false;
			}
			byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser);
			string blob = Encoding.UTF8.GetString(plain);
			string oldCookie = Extract(blob);
			if (string.IsNullOrEmpty(oldCookie) || !blob.Contains(oldCookie))
			{
				return false;
			}
			string newBlob = blob.Replace(oldCookie, newCookie);
			byte[] reEncrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(newBlob), null, DataProtectionScope.CurrentUser);
			string newEncoded = Convert.ToBase64String(reEncrypted);
			using MemoryStream memoryStream = new MemoryStream();
			using (Utf8JsonWriter writer = new Utf8JsonWriter(memoryStream))
			{
				writer.WriteStartObject();
				foreach (JsonProperty property in jsonDocument.RootElement.EnumerateObject())
				{
					if (property.NameEquals("CookiesData"))
					{
						writer.WriteString("CookiesData", newEncoded);
					}
					else
					{
						property.WriteTo(writer);
					}
				}
				writer.WriteEndObject();
			}
			File.WriteAllBytes(outputDatPath, memoryStream.ToArray());
			return string.Equals(ExtractCookieFromDat(outputDatPath), newCookie, StringComparison.Ordinal);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RobloxCookie", "SynthesizeDatWithCookie failed: " + ex.Message);
			return false;
		}
	}

	public static async Task<Dictionary<string, AssetMeta>> ResolveAssetNamesAsync(IReadOnlyList<string> assetIds, CancellationToken ct = default(CancellationToken))
	{
		Dictionary<string, AssetMeta> result = new Dictionary<string, AssetMeta>(StringComparer.Ordinal);
		if (assetIds == null || assetIds.Count == 0)
		{
			return result;
		}
		string text = Get();
		if (!string.IsNullOrEmpty(text))
		{
			try
			{
				string text2 = string.Join(",", assetIds);
				using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "https://develop.roblox.com/v1/assets?assetIds=" + text2);
				req.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + text);
				req.Headers.TryAddWithoutValidation("User-Agent", "Voidstrap/1.0");
				using HttpResponseMessage res = await _authClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (res.IsSuccessStatusCode)
				{
					using JsonDocument doc = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(res.Content, MaxApiResponseBytes, ct).ConfigureAwait(continueOnCapturedContext: false));
					HashSet<long> hashSet = new HashSet<long>();
					HashSet<long> groupIds = new HashSet<long>();
					Dictionary<string, (bool isGroup, long targetId)> creatorOf = new Dictionary<string, (bool, long)>(StringComparer.Ordinal);
					if (doc.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement item3 in value.EnumerateArray())
						{
							if (item3.ValueKind != JsonValueKind.Object)
							{
								continue;
							}
							JsonElement value2;
							string text3 = ((!item3.TryGetProperty("id", out value2)) ? "" : ((value2.ValueKind == JsonValueKind.Number) ? value2.GetRawText() : (value2.GetString() ?? "")));
							JsonElement value3;
							string name = (item3.TryGetProperty("name", out value3) ? (value3.GetString() ?? "") : "");
							JsonElement value4;
							string type = (item3.TryGetProperty("type", out value4) ? (value4.GetString() ?? "") : "");
							if (text3.Length == 0)
							{
								continue;
							}
							JsonElement value5;
							string text4 = (item3.TryGetProperty("creatorName", out value5) ? (value5.GetString() ?? "") : "");
							result[text3] = new AssetMeta(name, type, text4);
							if (text4.Length > 0)
							{
								continue;
							}
							bool flag = false;
							if (item3.TryGetProperty("creatorType", out var value6))
							{
								int value7;
								if (value6.ValueKind == JsonValueKind.String)
								{
									flag = (value6.GetString() ?? "").Equals("Group", StringComparison.OrdinalIgnoreCase);
								}
								else if (value6.ValueKind == JsonValueKind.Number && value6.TryGetInt32(out value7))
								{
									flag = value7 == 2;
								}
							}
							long value8 = 0L;
							if (item3.TryGetProperty("creatorTargetId", out var value9))
							{
								long result2;
								if (value9.ValueKind == JsonValueKind.Number)
								{
									value9.TryGetInt64(out value8);
								}
								else if (value9.ValueKind == JsonValueKind.String && long.TryParse(value9.GetString(), out result2))
								{
									value8 = result2;
								}
							}
							if (value8 > 0)
							{
								creatorOf[text3] = (flag, value8);
								if (flag)
								{
									groupIds.Add(value8);
								}
								else
								{
									hashSet.Add(value8);
								}
							}
						}
					}
					if (result.Count > 0)
					{
						Dictionary<long, string> userNames = await ResolveUserNamesAsync(hashSet, ct).ConfigureAwait(continueOnCapturedContext: false);
						Dictionary<long, string> dictionary = await ResolveGroupNamesAsync(groupIds, ct).ConfigureAwait(continueOnCapturedContext: false);
						foreach (KeyValuePair<string, (bool, long)> item4 in creatorOf)
						{
							(bool, long) value10 = item4.Value;
							bool item = value10.Item1;
							long item2 = value10.Item2;
							string value11;
							string value12;
							string text5 = ((!item) ? (userNames.TryGetValue(item2, out value11) ? value11 : "") : (dictionary.TryGetValue(item2, out value12) ? value12 : ""));
							if (text5.Length > 0 && result.TryGetValue(item4.Key, out var value13))
							{
								result[item4.Key] = value13 with
								{
									CreatorName = text5
								};
							}
						}
						return result;
					}
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("RobloxCookie", "Authenticated name resolve failed: " + ex.Message);
			}
		}
		if (DateTime.UtcNow < _economyCooldownUntil)
		{
			return result;
		}
		List<string> list = new List<string>();
		foreach (string assetId in assetIds)
		{
			if (!result.ContainsKey(assetId))
			{
				list.Add(assetId);
			}
		}
		int fails = 0;
		int done = 0;
		using (List<string>.Enumerator enumerator3 = list.GetEnumerator())
		{
			for (; enumerator3.MoveNext(); done++)
			{
				string id = enumerator3.Current;
				if (ct.IsCancellationRequested || fails >= 2 || done >= 4)
				{
					break;
				}
				try
				{
					using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "https://economy.roblox.com/v2/assets/" + id + "/details"))
					{
						using HttpResponseMessage res = await _authClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false);
						if (res.StatusCode == HttpStatusCode.TooManyRequests)
						{
							_economyCooldownUntil = DateTime.UtcNow.AddSeconds(120.0);
							break;
						}
						if (res.IsSuccessStatusCode)
						{
							using JsonDocument jsonDocument = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(res.Content, MaxApiResponseBytes, ct).ConfigureAwait(continueOnCapturedContext: false));
							JsonElement rootElement = jsonDocument.RootElement;
							JsonElement value14;
							string text6 = (rootElement.TryGetProperty("Name", out value14) ? (value14.GetString() ?? "") : "");
							string creatorName = "";
							if (rootElement.TryGetProperty("Creator", out var value15) && value15.ValueKind == JsonValueKind.Object && value15.TryGetProperty("Name", out var value16))
							{
								creatorName = value16.GetString() ?? "";
							}
							if (text6.Length > 0)
							{
								result[id] = new AssetMeta(text6, "", creatorName);
							}
							fails = 0;
						}
						else
						{
							fails++;
						}
					}
					continue;
				}
				catch
				{
					fails++;
					continue;
				}
			}
		}
		return result;
	}

	private static async Task<Dictionary<long, string>> ResolveUserNamesAsync(ICollection<long> ids, CancellationToken ct)
	{
		Dictionary<long, string> map = new Dictionary<long, string>();
		if (ids == null || ids.Count == 0)
		{
			return map;
		}
		try
		{
			List<long> list = new List<long>(ids);
			for (int i = 0; i < list.Count; i += 100)
			{
				List<long> range = list.GetRange(i, Math.Min(100, list.Count - i));
				string content = "{\"userIds\":[" + string.Join(",", range) + "],\"excludeBannedUsers\":false}";
				using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, "https://users.roblox.com/v1/users");
				req.Content = new StringContent(content, Encoding.UTF8, "application/json");
				using (HttpResponseMessage res = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(continueOnCapturedContext: false))
				{
					if (!res.IsSuccessStatusCode)
					{
						goto end_IL_0161;
					}
					using (JsonDocument jsonDocument = JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(res.Content, MaxApiResponseBytes, ct).ConfigureAwait(continueOnCapturedContext: false)))
					{
						if (!jsonDocument.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Array)
						{
							continue;
						}
						foreach (JsonElement item in value.EnumerateArray())
						{
							if (item.TryGetProperty("id", out var value2) && value2.TryGetInt64(out var value3) && item.TryGetProperty("name", out var value4))
							{
								string text = value4.GetString();
								if (text != null && text.Length > 0)
								{
									map[value3] = text;
								}
							}
						}
					}
					goto end_IL_00bf;
					end_IL_0161:;
				}
				end_IL_00bf:;
			}
		}
		catch
		{
		}
		return map;
	}

	private static async Task<Dictionary<long, string>> ResolveGroupNamesAsync(ICollection<long> ids, CancellationToken ct)
	{
		Dictionary<long, string> map = new Dictionary<long, string>();
		if (ids == null || ids.Count == 0)
		{
			return map;
		}
		try
		{
			List<long> list = new List<long>(ids);
			for (int i = 0; i < list.Count; i += 50)
			{
				List<long> range = list.GetRange(i, Math.Min(50, list.Count - i));
				string requestUri = "https://groups.roblox.com/v2/groups?groupIds=" + string.Join(",", range);
				using JsonDocument jsonDocument = JsonDocument.Parse(await Utility.Http.GetString(requestUri, ct).ConfigureAwait(continueOnCapturedContext: false));
				if (!jsonDocument.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Array)
				{
					continue;
				}
				foreach (JsonElement item in value.EnumerateArray())
				{
					if (item.TryGetProperty("id", out var value2) && value2.TryGetInt64(out var value3) && item.TryGetProperty("name", out var value4))
					{
						string text = value4.GetString();
						if (text != null && text.Length > 0)
						{
							map[value3] = text;
						}
					}
				}
			}
		}
		catch
		{
		}
		return map;
	}

	private static string? ReadFromDat()
	{
		try
		{
			string cookiesDatPath = CookiesDatPath;
			if (!File.Exists(cookiesDatPath))
			{
				return null;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(File.ReadAllText(cookiesDatPath));
			if (!jsonDocument.RootElement.TryGetProperty("CookiesData", out var value))
			{
				return null;
			}
			string text = value.GetString();
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(text), null, DataProtectionScope.CurrentUser);
			return Extract(Encoding.UTF8.GetString(bytes));
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RobloxCookie", "RobloxCookies.dat read failed: " + ex.Message);
			return null;
		}
	}

	private static string? ReadFromRegistry()
	{
		if (!Voidstrap.Utility.Platform.SupportsRegistry)
		{
			return null;
		}
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Roblox\\RobloxStudioBrowser\\roblox.com");
			if (registryKey == null)
			{
				return null;
			}
			string[] valueNames = registryKey.GetValueNames();
			foreach (string text in valueNames)
			{
				if (text.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase) && registryKey.GetValue(text) is string text2 && !string.IsNullOrEmpty(text2))
				{
					string text3 = Extract(text2);
					if (!string.IsNullOrEmpty(text3))
					{
						return text3;
					}
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RobloxCookie", "Registry read failed: " + ex.Message);
		}
		return null;
	}

	private static string? Extract(string blob)
	{
		if (string.IsNullOrEmpty(blob))
		{
			return null;
		}
		Match match = WarningRegex.Match(blob);
		if (match.Success)
		{
			return match.Groups[1].Value.Trim();
		}
		Match match2 = NamedRegex.Match(blob);
		if (match2.Success)
		{
			return match2.Groups[1].Value.Trim();
		}
		return null;
	}
}
