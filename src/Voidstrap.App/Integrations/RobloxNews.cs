using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

internal static class RobloxNews
{
	public const string FeedPageUrl = "https://devforum.roblox.com/c/updates/announcements/36";

	private const string FeedUrl = "https://devforum.roblox.com/c/updates/announcements/36.json";

	private const string TopicUrl = "https://devforum.roblox.com/t/";

	private static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(30);

	public sealed class NewsPost
	{
		public long Id { get; init; }

		public string Title { get; init; } = string.Empty;

		public string Summary { get; init; } = string.Empty;

		public string Url { get; init; } = string.Empty;

		public string ImageUrl { get; init; } = string.Empty;

		public string Tag { get; init; } = string.Empty;

		public int Views { get; init; }

		public int Replies { get; init; }

		public DateTime Created { get; init; }
	}

	public static string Thumbnail(string? imageUrl, int width)
	{
		if (string.IsNullOrWhiteSpace(imageUrl))
			return string.Empty;
		return "https://wsrv.nl/?url=" + Uri.EscapeDataString(imageUrl) + "&w=" + width.ToString(CultureInfo.InvariantCulture) + "&output=webp&q=80&we";
	}

	private const int MaxImageBytes = 4 * 1024 * 1024;

	private const int MaxCachedImages = 80;

	private static readonly ConcurrentDictionary<string, byte> ImageDownloads = new(StringComparer.Ordinal);

	private static string ImageFolder => Path.Combine(Paths.Cache, "News");

	public static string Image(string? imageUrl, int width)
	{
		string remote = Thumbnail(imageUrl, width);
		if (remote.Length == 0)
			return remote;
		string path = Path.Combine(ImageFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(remote)))[..32] + ".img");
		try
		{
			if (File.Exists(path) && new FileInfo(path).Length > 0)
				return new Uri(path).AbsoluteUri;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
		if (ImageDownloads.TryAdd(path, 0))
			_ = Task.Run(() => SaveImageAsync(remote, path));
		return remote;
	}

	private static async Task SaveImageAsync(string remote, string path)
	{
		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
			using HttpResponseMessage response = await App.HttpClient.GetAsync(remote, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
				return;
			byte[] data = await Http.ReadBytesBoundedAsync(response.Content, MaxImageBytes, cts.Token).ConfigureAwait(false);
			if (data.Length == 0)
				return;
			Directory.CreateDirectory(ImageFolder);
			await File.WriteAllBytesAsync(temporary, data, cts.Token).ConfigureAwait(false);
			File.Move(temporary, path, true);
			PruneImages();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("RobloxNews", "A news image could not be saved: " + ex.Message);
		}
		finally
		{
			ImageDownloads.TryRemove(path, out _);
			try
			{
				if (File.Exists(temporary))
					File.Delete(temporary);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
			}
		}
	}

	private static void PruneImages()
	{
		try
		{
			FileInfo[] files = new DirectoryInfo(ImageFolder).GetFiles("*.img");
			if (files.Length <= MaxCachedImages)
				return;
			foreach (FileInfo file in files.OrderByDescending(file => file.LastWriteTimeUtc).Skip(MaxCachedImages))
				file.Delete();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	public static Task<List<NewsPost>> GetAllAsync(CancellationToken token) => GetAllAsync(CacheAge, token);

	public static async Task<List<NewsPost>> GetLatestSavedAsync(int count, CancellationToken token)
	{
		List<NewsPost> posts = await GetAllAsync(TimeSpan.MaxValue, token).ConfigureAwait(false);
		return PickLatest(posts, count);
	}

	private static async Task<List<NewsPost>> GetAllAsync(TimeSpan maxAge, CancellationToken token)
	{
		FeedResponse? response = await GitHubCache.GetJsonAsync<FeedResponse>(FeedUrl, maxAge, token).ConfigureAwait(false);
		List<FeedTopic> topics = response?.TopicList?.Topics ?? [];
		return topics
			.Where(topic => topic.Id > 0 && !topic.Pinned && !string.IsNullOrWhiteSpace(topic.Title))
			.Select(Build)
			.OrderByDescending(post => post.Created)
			.ToList();
	}

	public static async Task<List<NewsPost>> GetLatestAsync(int count, CancellationToken token)
	{
		List<NewsPost> posts = await GetAllAsync(token).ConfigureAwait(false);
		return PickLatest(posts, count);
	}

	private static List<NewsPost> PickLatest(List<NewsPost> posts, int count)
	{
		List<NewsPost> illustrated = posts.Where(post => post.ImageUrl.Length > 0).ToList();
		return illustrated.Count >= count
			? illustrated.Take(count).ToList()
			: illustrated.Concat(posts.Where(post => post.ImageUrl.Length == 0)).Take(count).ToList();
	}

	private static NewsPost Build(FeedTopic topic)
	{
		return new NewsPost
		{
			Id = topic.Id,
			Title = CleanTitle(topic.Title),
			Summary = CleanSummary(topic.Excerpt),
			Url = TopicUrl + (string.IsNullOrWhiteSpace(topic.Slug) ? "topic" : topic.Slug) + "/" + topic.Id.ToString(CultureInfo.InvariantCulture),
			ImageUrl = ResolveImage(topic.ImageUrl),
			Tag = ResolveTag(topic.Tags),
			Views = topic.Views,
			Replies = Math.Max(topic.PostsCount - 1, 0),
			Created = topic.CreatedAt ?? DateTime.MinValue
		};
	}

	private static string CleanTitle(string? title)
	{
		string value = (title ?? string.Empty).Trim();
		return value.Length <= 90 ? value : value[..89].TrimEnd() + "...";
	}

	private static string CleanSummary(string? excerpt)
	{
		string value = (excerpt ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
		while (value.Contains("  ", StringComparison.Ordinal))
			value = value.Replace("  ", " ", StringComparison.Ordinal);
		return value.Length <= 150 ? value : value[..149].TrimEnd() + "...";
	}

	private static string ResolveImage(string? image)
	{
		string value = (image ?? string.Empty).Trim();
		if (value.Length == 0)
			return string.Empty;
		if (value.StartsWith("//", StringComparison.Ordinal))
			return "https:" + value;
		return value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value : string.Empty;
	}

	private static readonly string[] Acronyms = ["rdc", "ip", "ugc", "api", "sdk", "ai", "ui", "ux", "vr", "ar", "os", "fps", "cdn", "npc", "id"];

	private static string ResolveTag(List<string>? tags)
	{
		string? tag = tags?.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry) && !entry.Equals("featured", StringComparison.OrdinalIgnoreCase));
		if (string.IsNullOrWhiteSpace(tag))
			return string.Empty;
		string[] words = tag.Replace('-', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		return string.Join(' ', words.Select(word => Acronyms.Contains(word, StringComparer.OrdinalIgnoreCase)
			? word.ToUpperInvariant()
			: char.ToUpper(word[0], CultureInfo.InvariantCulture) + word[1..]));
	}

	private sealed class FeedResponse
	{
		[JsonPropertyName("topic_list")]
		public FeedTopicList? TopicList { get; set; }
	}

	private sealed class FeedTopicList
	{
		[JsonPropertyName("topics")]
		public List<FeedTopic> Topics { get; set; } = [];
	}

	private sealed class FeedTopic
	{
		[JsonPropertyName("id")]
		public long Id { get; set; }

		[JsonPropertyName("title")]
		public string? Title { get; set; }

		[JsonPropertyName("slug")]
		public string? Slug { get; set; }

		[JsonPropertyName("excerpt")]
		public string? Excerpt { get; set; }

		[JsonPropertyName("image_url")]
		public string? ImageUrl { get; set; }

		[JsonPropertyName("pinned")]
		public bool Pinned { get; set; }

		[JsonPropertyName("views")]
		public int Views { get; set; }

		[JsonPropertyName("posts_count")]
		public int PostsCount { get; set; }

		[JsonPropertyName("tags")]
		public List<string>? Tags { get; set; }

		[JsonPropertyName("created_at")]
		public DateTime? CreatedAt { get; set; }
	}
}
