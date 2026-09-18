using System;
using System.Collections.Generic;
using System.Globalization;
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

	public static async Task<List<NewsPost>> GetAllAsync(CancellationToken token)
	{
		FeedResponse? response = await GitHubCache.GetJsonAsync<FeedResponse>(FeedUrl, CacheAge, token).ConfigureAwait(false);
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
