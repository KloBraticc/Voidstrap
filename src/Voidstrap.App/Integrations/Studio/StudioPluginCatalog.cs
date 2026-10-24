using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Studio;

public sealed record StudioPluginListing(
	long Id,
	string Name,
	string Description,
	string Creator,
	bool Verified,
	int UpVotes,
	int DownVotes,
	decimal Price,
	DateTime? Created,
	DateTime? Updated,
	string Category);

public sealed record StudioPluginSearchPage(IReadOnlyList<StudioPluginListing> Plugins, string? NextPageToken);

public sealed record InstalledStudioPlugin(string Path, string Name, long? AssetId, DateTime InstalledAt, long Size);

public enum StudioPluginFailure
{
	NotSignedIn,
	NotOwned,
	Unavailable,
	InvalidFile
}

public sealed class StudioPluginException : Exception
{
	public StudioPluginException(StudioPluginFailure failure, string message) : base(message)
	{
		Failure = failure;
	}

	public StudioPluginFailure Failure { get; }
}

public static partial class StudioPluginCatalog
{
	private const string LogIdent = "StudioPluginCatalog";
	private const string SearchUrl = "https://apis.roblox.com/toolbox-service/v2/assets:search?searchCategoryType=Plugin&maxPageSize=30";
	private const string DetailsUrl = "https://apis.roblox.com/toolbox-service/v1/items/details?assetIds=";
	private const string ThumbnailUrl = "https://thumbnails.roblox.com/v1/assets?returnPolicy=PlaceHolder&size=150x150&format=Png&assetIds=";
	private const string DownloadUrl = "https://assetdelivery.roblox.com/v1/asset/?id=";
	public const string StorePageUrl = "https://create.roblox.com/store/asset/";
	private const long MaxPluginBytes = 64L * 1024 * 1024;
	private const int ThumbnailBatch = 100;
	private const int DetailsBatch = 30;
	private const int MaxNameLength = 60;
	private static readonly string[] PluginExtensions = [".rbxm", ".rbxmx", ".lua", ".luau"];
	private static readonly byte[] BinaryModelHeader = "<roblox!"u8.ToArray();

	private static readonly HttpClient DownloadClient = CreateDownloadClient();

	public static string PluginsDirectory => StudioPluginInstaller.PluginsDirectory;

	private static HttpClient CreateDownloadClient()
	{
		HttpClient client = new(new SocketsHttpHandler
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
			PooledConnectionLifetime = TimeSpan.FromMinutes(5)
		})
		{
			Timeout = TimeSpan.FromSeconds(60)
		};
		client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
		return client;
	}

	public static async Task<StudioPluginSearchPage> SearchAsync(string query, string? pageToken, CancellationToken token)
	{
		StringBuilder url = new(SearchUrl);
		if (!string.IsNullOrWhiteSpace(query))
			url.Append("&query=").Append(Uri.EscapeDataString(query.Trim()));
		if (!string.IsNullOrEmpty(pageToken))
			url.Append("&pageToken=").Append(Uri.EscapeDataString(pageToken));

		using JsonDocument document = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString(url.ToString(), token).ConfigureAwait(false));
		JsonElement root = document.RootElement;
		List<StudioPluginListing> plugins = [];
		if (root.TryGetProperty("creatorStoreAssets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
		{
			foreach (JsonElement item in assets.EnumerateArray())
			{
				if (!item.TryGetProperty("asset", out JsonElement asset))
					continue;
				item.TryGetProperty("creator", out JsonElement creator);
				item.TryGetProperty("voting", out JsonElement voting);
				JsonElement price = item.TryGetProperty("creatorStoreProduct", out JsonElement product) && product.TryGetProperty("purchasePrice", out JsonElement purchase) ? purchase : default;
				StudioPluginListing? listing = ReadListing(asset, creator, voting, price, "verified", "createTime", "updateTime");
				if (listing != null)
					plugins.Add(listing);
			}
		}
		string? next = root.TryGetProperty("nextPageToken", out JsonElement nextToken) && nextToken.ValueKind == JsonValueKind.String ? nextToken.GetString() : null;
		return new StudioPluginSearchPage(plugins, string.IsNullOrEmpty(next) ? null : next);
	}

	public static async Task<IReadOnlyDictionary<long, StudioPluginListing>> GetDetailsAsync(IEnumerable<long> ids, CancellationToken token)
	{
		Dictionary<long, StudioPluginListing> found = [];
		foreach (long[] chunk in ids.Distinct().Chunk(DetailsBatch))
		{
			using JsonDocument document = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString(DetailsUrl + string.Join(",", chunk), token).ConfigureAwait(false));
			if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				continue;
			foreach (JsonElement item in data.EnumerateArray())
			{
				if (!item.TryGetProperty("asset", out JsonElement asset))
					continue;
				item.TryGetProperty("creator", out JsonElement creator);
				item.TryGetProperty("voting", out JsonElement voting);
				JsonElement price = item.TryGetProperty("fiatProduct", out JsonElement product) && product.TryGetProperty("purchasePrice", out JsonElement purchase) ? purchase : default;
				StudioPluginListing? listing = ReadListing(asset, creator, voting, price, "isVerifiedCreator", "createdUtc", "updatedUtc");
				if (listing != null)
					found[listing.Id] = listing;
			}
		}
		return found;
	}

	public static async Task<IReadOnlyDictionary<long, string>> GetIconsAsync(IEnumerable<long> ids, CancellationToken token)
	{
		Dictionary<long, string> icons = [];
		foreach (long[] chunk in ids.Distinct().Chunk(ThumbnailBatch))
		{
			using JsonDocument document = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString(ThumbnailUrl + string.Join(",", chunk), token).ConfigureAwait(false));
			if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
				continue;
			foreach (JsonElement item in data.EnumerateArray())
			{
				if (item.TryGetProperty("targetId", out JsonElement id) && id.TryGetInt64(out long assetId)
					&& item.TryGetProperty("imageUrl", out JsonElement url) && url.GetString() is string imageUrl
					&& Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps)
					icons[assetId] = imageUrl;
			}
		}
		return icons;
	}

	private static StudioPluginListing? ReadListing(JsonElement asset, JsonElement creator, JsonElement voting, JsonElement price, string verifiedProperty, string createdProperty, string updatedProperty)
	{
		if (!asset.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt64(out long id) || id <= 0)
			return null;
		return new StudioPluginListing(
			id,
			Text(asset, "name"),
			Text(asset, "description"),
			creator.ValueKind == JsonValueKind.Object ? Text(creator, "name") : "",
			creator.ValueKind == JsonValueKind.Object && creator.TryGetProperty(verifiedProperty, out JsonElement verified) && verified.ValueKind == JsonValueKind.True,
			Number(voting, "upVotes"),
			Number(voting, "downVotes"),
			Amount(price),
			Date(asset, createdProperty),
			Date(asset, updatedProperty),
			Text(asset, "categoryPath"));
	}

	private static string Text(JsonElement element, string property) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

	private static int Number(JsonElement element, string property) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number) ? number : 0;

	private static DateTime? Date(JsonElement element, string property) =>
		DateTime.TryParse(Text(element, property), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime date) ? date : null;

	private static decimal Amount(JsonElement price)
	{
		if (price.ValueKind != JsonValueKind.Object || !price.TryGetProperty("quantity", out JsonElement quantity)
			|| !quantity.TryGetProperty("significand", out JsonElement significand) || !significand.TryGetInt64(out long value) || value <= 0)
			return 0;
		int exponent = quantity.TryGetProperty("exponent", out JsonElement exponentElement) && exponentElement.TryGetInt32(out int parsed) ? parsed : 0;
		decimal amount = value;
		for (; exponent < 0; exponent++)
			amount /= 10;
		for (; exponent > 0; exponent--)
			amount *= 10;
		return amount;
	}

	public static IReadOnlyList<InstalledStudioPlugin> ListInstalled()
	{
		try
		{
			if (!Directory.Exists(PluginsDirectory))
				return [];
			return new DirectoryInfo(PluginsDirectory).EnumerateFiles()
				.Where(file => PluginExtensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
				.Select(file =>
				{
					string stem = System.IO.Path.GetFileNameWithoutExtension(file.Name);
					Match match = AssetIdSuffix().Match(stem);
					long? id = match.Success && long.TryParse(match.Groups[1].Value, out long parsed) ? parsed : null;
					string name = match.Success ? stem[..match.Index].TrimEnd() : stem;
					return new InstalledStudioPlugin(file.FullName, string.IsNullOrEmpty(name) ? stem : name, id, file.LastWriteTimeUtc, file.Length);
				})
				.OrderBy(plugin => plugin.Name, StringComparer.CurrentCultureIgnoreCase)
				.ToList();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteException(LogIdent + "::ListInstalled", ex);
			return [];
		}
	}

	public static async Task<string> InstallAsync(StudioPluginListing plugin, CancellationToken token)
	{
		string? cookie = RobloxCookie.Get();
		if (string.IsNullOrEmpty(cookie))
			throw new StudioPluginException(StudioPluginFailure.NotSignedIn, "No Roblox login was found");

		using HttpRequestMessage request = new(HttpMethod.Get, DownloadUrl + plugin.Id.ToString(CultureInfo.InvariantCulture));
		request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
		request.Headers.TryAddWithoutValidation("Roblox-Browser-Asset-Request", "false");
		request.Headers.TryAddWithoutValidation("Accept", "*/*");
		using HttpResponseMessage response = await DownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
		if (response.StatusCode == HttpStatusCode.Unauthorized)
			throw new StudioPluginException(StudioPluginFailure.NotSignedIn, "Roblox did not accept the saved login");
		if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden)
			throw new StudioPluginException(StudioPluginFailure.NotOwned, "This plugin has to be bought on the Creator Store first");
		if (!response.IsSuccessStatusCode)
			throw new StudioPluginException(StudioPluginFailure.Unavailable, "Roblox answered " + (int)response.StatusCode);

		byte[] data = await ReadBoundedAsync(response.Content, token).ConfigureAwait(false);
		bool binary = data.AsSpan().StartsWith(BinaryModelHeader);
		if (!binary && !IsXmlModel(data))
			throw new StudioPluginException(StudioPluginFailure.InvalidFile, "The download is not a Roblox model");

		Directory.CreateDirectory(PluginsDirectory);
		string target = System.IO.Path.Combine(PluginsDirectory, SafeName(plugin.Name) + " [" + plugin.Id.ToString(CultureInfo.InvariantCulture) + "]" + (binary ? ".rbxm" : ".rbxmx"));
		await WriteAtomicallyAsync(target, data, token).ConfigureAwait(false);
		foreach (InstalledStudioPlugin old in ListInstalled().Where(installed => installed.AssetId == plugin.Id && !string.Equals(installed.Path, target, StringComparison.OrdinalIgnoreCase)))
			TryDelete(old.Path);
		App.Logger.WriteLine(LogIdent, $"Installed Studio plugin {plugin.Id} as {System.IO.Path.GetFileName(target)}");
		return target;
	}

	public static string Import(string sourcePath)
	{
		FileInfo source = new(sourcePath);
		string extension = source.Extension.ToLowerInvariant();
		if (!source.Exists || !PluginExtensions.Contains(extension))
			throw new StudioPluginException(StudioPluginFailure.InvalidFile, "Only .rbxm, .rbxmx, .lua and .luau files can be imported");
		if (source.Length == 0 || source.Length > MaxPluginBytes)
			throw new StudioPluginException(StudioPluginFailure.InvalidFile, "The file is empty or too large");
		byte[] data = File.ReadAllBytes(source.FullName);
		if ((extension == ".rbxm" && !data.AsSpan().StartsWith(BinaryModelHeader)) || (extension == ".rbxmx" && !IsXmlModel(data)))
			throw new StudioPluginException(StudioPluginFailure.InvalidFile, "The file is not a Roblox model");

		Directory.CreateDirectory(PluginsDirectory);
		string stem = SafeName(System.IO.Path.GetFileNameWithoutExtension(source.Name));
		string target = System.IO.Path.Combine(PluginsDirectory, stem + extension);
		for (int copy = 2; File.Exists(target); copy++)
			target = System.IO.Path.Combine(PluginsDirectory, $"{stem} ({copy}){extension}");
		File.Copy(source.FullName, target);
		App.Logger.WriteLine(LogIdent, "Imported Studio plugin " + System.IO.Path.GetFileName(target));
		return target;
	}

	public static void Remove(InstalledStudioPlugin plugin)
	{
		string folder = System.IO.Path.GetFullPath(PluginsDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
		if (!System.IO.Path.GetFullPath(plugin.Path).StartsWith(folder, StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("The plugin is not inside the Studio plugins folder");
		File.Delete(plugin.Path);
		App.Logger.WriteLine(LogIdent, "Removed Studio plugin " + System.IO.Path.GetFileName(plugin.Path));
	}

	private static bool IsXmlModel(byte[] data)
	{
		string head = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 512)).TrimStart('﻿', ' ', '\t', '\r', '\n');
		if (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
		{
			int end = head.IndexOf("?>", StringComparison.Ordinal);
			head = end < 0 ? "" : head[(end + 2)..].TrimStart();
		}
		return head.StartsWith("<roblox", StringComparison.OrdinalIgnoreCase);
	}

	private static string SafeName(string name)
	{
		char[] invalid = System.IO.Path.GetInvalidFileNameChars();
		string cleaned = new(name.Select(character => invalid.Contains(character) || character == '[' || character == ']' ? ' ' : character).ToArray());
		cleaned = Whitespace().Replace(cleaned, " ").Trim().TrimEnd('.');
		if (cleaned.Length > MaxNameLength)
			cleaned = cleaned[..MaxNameLength].TrimEnd();
		return string.IsNullOrEmpty(cleaned) ? "Plugin" : cleaned;
	}

	private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
	{
		if (content.Headers.ContentLength is long length && length > MaxPluginBytes)
			throw new StudioPluginException(StudioPluginFailure.InvalidFile, "The plugin is too large");
		await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
		using MemoryStream buffer = new();
		byte[] chunk = new byte[81920];
		int read;
		while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
		{
			if (buffer.Length + read > MaxPluginBytes)
				throw new StudioPluginException(StudioPluginFailure.InvalidFile, "The plugin is too large");
			buffer.Write(chunk, 0, read);
		}
		return buffer.ToArray();
	}

	private static async Task WriteAtomicallyAsync(string target, byte[] data, CancellationToken token)
	{
		string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false);
			File.Move(temporary, target, true);
		}
		finally
		{
			TryDelete(temporary);
		}
	}

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger.WriteLine(LogIdent, "Could not delete " + System.IO.Path.GetFileName(path) + ": " + ex.Message);
		}
	}

	[GeneratedRegex(@"\[(\d{1,19})\]$")]
	private static partial Regex AssetIdSuffix();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();
}
