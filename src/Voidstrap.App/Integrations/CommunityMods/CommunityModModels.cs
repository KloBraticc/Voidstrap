using System;
using System.Collections.Generic;
using System.Linq;

namespace Voidstrap.Integrations.CommunityMods;

public enum CommunityModInstallTarget
{
	ManagedMods,
	AssetWarp,
	Fleasion,
	AssetCache
}

public sealed class CommunityModCategory
{
	public long Id { get; init; }

	public string Name { get; init; } = "";

	public int ItemCount { get; init; }

	public string Display => ItemCount > 0 ? Name + "  (" + ItemCount + ")" : Name;
}

public sealed class CommunityModImage
{
	public string Thumbnail { get; init; } = "";

	public string Full { get; init; } = "";

	public string Caption { get; init; } = "";
}

public sealed class CommunityModFile
{
	public long Id { get; init; }

	public string FileName { get; init; } = "";

	public string DownloadUrl { get; init; } = "";

	public string Md5 { get; init; } = "";

	public long SizeBytes { get; init; }

	public string Description { get; init; } = "";

	public string AntivirusState { get; init; } = "";

	public string AntivirusResult { get; init; } = "";

	public string AnalysisState { get; init; } = "";

	public string AnalysisResult { get; init; } = "";

	public bool ContainsExecutable { get; set; }

	public bool LacksRobloxContent { get; set; }

	public bool ContentsProbed { get; set; }

	public bool ListingKnown { get; set; }

	public bool ListingHasRobloxContent { get; set; }

	public bool ListingHasConfig { get; set; }

	public bool ListingHasAssets { get; set; }

	public bool ListingHasCacheEntries { get; set; }

	public string SizeDisplay => SizeBytes <= 0
		? "Unknown size"
		: SizeBytes >= 1048576
			? (SizeBytes / 1048576d).ToString("0.#") + " MB"
			: Math.Max(1, SizeBytes / 1024) + " KB";
}

public sealed class CommunityModPerson
{
	public string Name { get; init; } = "";

	public string AvatarUrl { get; init; } = "";

	public string ProfileUrl { get; init; } = "";

	public string Title { get; init; } = "";

	public string HonoraryTitle { get; init; } = "";

	public int Points { get; init; }

	public DateTime JoinedUtc { get; init; }

	public IReadOnlyList<string> Achievements { get; init; } = Array.Empty<string>();

	public string Detail => string.Join("   ", new[] { HonoraryTitle, Title }.Where(value => !string.IsNullOrWhiteSpace(value)));

	public string AchievementDisplay => Achievements.Count == 0 ? "" : string.Join("   ", Achievements.Take(3));
}

public sealed class CommunityModComment
{
	public long Id { get; init; }

	public CommunityModPerson Author { get; init; } = new CommunityModPerson();

	public IReadOnlyList<string> Paragraphs { get; init; } = Array.Empty<string>();

	public DateTime CreatedUtc { get; init; }

	public int Score { get; init; }

	public int ReplyCount { get; init; }

	public string DateDisplay => CreatedUtc == default ? "" : CreatedUtc.ToLocalTime().ToString("d MMM yyyy");

	public string ActivityDisplay => Score.ToString("N0") + " score   " + ReplyCount.ToString("N0") + " replies";
}

public sealed class CommunityModUpdate
{
	public long Id { get; init; }

	public string Name { get; init; } = "";

	public string Version { get; init; } = "";

	public string Summary { get; init; } = "";

	public IReadOnlyList<string> Paragraphs { get; init; } = Array.Empty<string>();

	public DateTime CreatedUtc { get; init; }

	public bool IsSignificant { get; init; }

	public string DateDisplay => CreatedUtc == default ? "" : CreatedUtc.ToLocalTime().ToString("d MMM yyyy");

	public string Detail => string.Join("   ", new[] { Version, DateDisplay, IsSignificant ? "Major update" : "" }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class CommunityModIssue
{
	public long Id { get; init; }

	public string Name { get; init; } = "";

	public string Status { get; init; } = "";

	public IReadOnlyList<string> Paragraphs { get; init; } = Array.Empty<string>();

	public DateTime CreatedUtc { get; init; }

	public string DateDisplay => CreatedUtc == default ? "" : CreatedUtc.ToLocalTime().ToString("d MMM yyyy");
}

public sealed class CommunityModLicenseRule
{
	public string Group { get; init; } = "";

	public string Text { get; init; } = "";
}

public sealed class CommunityModEntry
{
	public long Id { get; init; }

	public string Name { get; init; } = "";

	public string Summary { get; set; } = "";

	public string DescriptionHtml { get; set; } = "";

	public string Category { get; init; } = "";

	public string SuperCategory { get; init; } = "";

	public string Author { get; init; } = "";

	public string AuthorAvatar { get; init; } = "";

	public string ProfileUrl { get; init; } = "";

	public string IconUrl { get; init; } = "";

	public string Version { get; init; } = "";

	public string Slug { get; init; } = "";

	public string SourceName { get; init; } = "";

	public bool SupportsInstall { get; init; } = true;

	public bool IsSafetyChecked { get; set; }

	public bool DetailLoaded { get; set; }

	public bool RequiresFleasion { get; set; }

	public string InstallBlockedReason { get; init; } = "";

	public int LikeCount { get; set; }

	public int ViewCount { get; init; }

	public int DownloadCount { get; set; }

	public DateTime UpdatedUtc { get; init; }

	public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

	public List<CommunityModImage> Images { get; } = new List<CommunityModImage>();

	public List<CommunityModFile> Files { get; } = new List<CommunityModFile>();

	public List<string> DetailLines { get; } = new List<string>();

	public CommunityModPerson? Submitter { get; set; }

	public int CommentCount { get; set; }

	public int UpdateCount { get; set; }

	public int IssueCount { get; set; }

	public string LicenseText { get; set; } = "";

	public List<CommunityModComment> Comments { get; } = new List<CommunityModComment>();

	public List<CommunityModUpdate> Updates { get; } = new List<CommunityModUpdate>();

	public List<CommunityModIssue> Issues { get; } = new List<CommunityModIssue>();

	public List<CommunityModLicenseRule> LicenseRules { get; } = new List<CommunityModLicenseRule>();

	public string StatsDisplay => LikeCount.ToString("N0") + " likes   " + ViewCount.ToString("N0") + " views";

	public string DetailStatsDisplay => SourceName == GameBananaCatalog.SourceName
		? LikeCount.ToString("N0") + " likes   dislikes unavailable   " + CommentCount.ToString("N0") + " comments   " + DownloadCount.ToString("N0") + " downloads   " + ViewCount.ToString("N0") + " views"
		: StatsDisplay;

	public string CategoryDisplay => string.IsNullOrEmpty(SuperCategory) ? Category : SuperCategory + "   " + Category;

	public string UpdatedDisplay => UpdatedUtc == default ? "" : "Updated " + UpdatedUtc.ToLocalTime().ToString("d MMM yyyy");
}

public sealed class CommunityModPage
{
	public List<CommunityModEntry> Entries { get; } = new List<CommunityModEntry>();

	public int TotalCount { get; init; }

	public int PageNumber { get; init; }

	public int PageSize { get; init; }

	public int ReturnedCount { get; set; }

	public bool HasMore => ReturnedCount > 0
		&& ReturnedCount >= PageSize
		&& (TotalCount <= 0 || PageNumber * PageSize < TotalCount);
}
