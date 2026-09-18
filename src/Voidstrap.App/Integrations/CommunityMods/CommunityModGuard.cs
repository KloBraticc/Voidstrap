using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Voidstrap.Integrations.CommunityMods;

public sealed record CommunityModVerdict(bool Allowed, string Reason)
{
	public static readonly CommunityModVerdict Ok = new CommunityModVerdict(true, "");

	public static CommunityModVerdict Block(string reason) => new CommunityModVerdict(false, reason);
}

public static class CommunityModGuard
{
	public const int RobloxGameId = 2879;

	public const long MaxPackageBytes = 256L * 1024 * 1024;

	public const long MaxConfigBytes = 4L * 1024 * 1024;

	public const long MaxExtractedBytes = 1024L * 1024 * 1024;

	public const int MaxArchiveEntries = 6000;

	public const int MaxCompressionRatio = 120;

	public const long ZipBombFloorBytes = 64L * 1024 * 1024;

	private const string LogIdent = "CommunityModGuard";

	private static readonly string[] RobloxContentRoots =
	{
		"content",
		"extracontent",
		"platformcontent",
		"shaders"
	};

	public static readonly HashSet<string> InstallablePackageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".zip", ".rar", ".7z", ".json"
	};

	public static readonly HashSet<string> ConvertiblePackageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".rar", ".7z"
	};

	private static readonly HashSet<string> BlockedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"ssl",
		"clientsettings",
		"webview2",
		"webview2runtimeinstaller"
	};

	private static readonly HashSet<string> AllowedAssetExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".ktx", ".webp", ".tex",
		".ogg", ".mp3", ".wav", ".flac",
		".mesh", ".rbxm", ".rbxmx", ".rbxl", ".rbxlx",
		".ttf", ".otf", ".woff", ".woff2", ".font", ".fontfamily",
		".json", ".xml", ".txt", ".csv", ".md", ".dat", ".bin", ".ini", ".cfg",
		".hlsl", ".glsl", ".fx", ".pack", ".idx", ".mp4", ".webm", ".gif"
	};

	private static readonly HashSet<string> DangerousExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		".exe", ".dll", ".sys", ".drv", ".com", ".scr", ".cpl", ".msi", ".msix", ".msp",
		".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf",
		".wsh", ".hta", ".reg", ".lnk", ".url", ".scf", ".inf", ".jar", ".py", ".pyc",
		".sh", ".apk", ".app", ".so", ".dylib", ".ocx", ".ax", ".efi", ".iso", ".img",
		".vhd", ".vhdx", ".lua", ".luau", ".rbxs", ".dmp", ".pif", ".gadget", ".application"
	};

	private static readonly string[] BlockedTerms =
	{
		"exploit", "executor", "script hub", "scripthub", "cheat", "cheats", "cheating", "aimbot",
		"auto farm", "autofarm", "autofarming", "esp", "wallhack", "wall hack", "bypass anticheat",
		"anti cheat bypass", "anticheat bypass", "injector", "dll inject", "synapse", "krnl", "fluxus",
		"delta executor", "keyless", "no key system", "robux generator", "free robux", "account generator",
		"token grabber", "cookie logger", "remote access trojan", "keylogger", "stealer",
		"keygen", "serial key", "byfron bypass", "hyperion bypass", "silent aim",
		"lag switch", "dupe glitch", "duplication glitch", "fly hack", "speed hack", "god mode script"
	};

	public static readonly HashSet<string> HiddenCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"Maps",
		"Effects",
		"Castaways"
	};

	private static readonly string[] ExternalToolTerms =
	{
		"python", "pip install", ".py", "py file", "py script",
		"browser extension", "chrome extension", "firefox extension", "edge extension", "opera extension",
		"browser addon", "browser add on", "chrome web store", "load unpacked", "unpacked extension",
		"userscript", "user script", "tampermonkey", "greasemonkey", "violentmonkey",
		".crx", ".xpi", "web extension", "webextension"
	};

	private static readonly string[] FlagListTerms =
	{
		"fflag", "dfflag", "fint", "dfint", "fstring", "dfstring", "flog",
		"fastflag", "fastflags", "fast flag", "fast flags",
		"flag list", "flags list", "flag preset", "flags preset", "clientappsettings"
	};

	private static readonly Regex BlockedTermPattern = BuildBlockedTermPattern();

	private static readonly Regex ExternalToolPattern = BuildExternalToolPattern();

	private static readonly Regex FlagListPattern = BuildTermPattern(FlagListTerms);

	private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"con", "prn", "aux", "nul",
		"com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
		"lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
	};

	private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

	private static Regex BuildExternalToolPattern()
	{
		return BuildTermPattern(ExternalToolTerms);
	}

	private static Regex BuildTermPattern(IReadOnlyList<string> terms)
	{
		string pattern = string.Join("|", terms.Select(term => string.Join("[ _-]+", term.Split(' ').Select(Regex.Escape))));
		return new Regex("(?<![a-z0-9])(" + pattern + ")(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
	}

	private static Regex BuildBlockedTermPattern()
	{
		string pattern = string.Join("|", BlockedTerms.Select(term => term.Replace(" ", "[ _-]+")));
		return new Regex("(?<![a-z0-9])(" + pattern + ")(?![a-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
	}

	public static bool IsGameBananaHost(string host)
	{
		return string.Equals(host, "gamebanana.com", StringComparison.OrdinalIgnoreCase)
			|| host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsTrustedDownloadUrl(string? url)
	{
		if (MarketplaceCatalog.IsMarketplaceUrl(url))
		{
			return true;
		}
		return TryGetHost(url, out string host) && IsGameBananaHost(host);
	}

	public static bool IsTrustedUrl(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return false;
		}
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			return false;
		}
		if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(uri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
		{
			return string.Equals(url.TrimEnd('/'), MarketplaceCatalog.SourceUrl, StringComparison.Ordinal)
				|| MarketplaceCatalog.IsMarketplaceUrl(url);
		}
		return IsGameBananaHost(uri.Host);
	}

	private static bool TryGetHost(string? url, out string host)
	{
		host = "";
		if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			return false;
		}
		if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		host = uri.Host;
		return true;
	}

	public static CommunityModVerdict InspectListing(CommunityModEntry entry)
	{
		if (entry == null)
		{
			return CommunityModVerdict.Block("The entry is missing.");
		}
		if (entry.Id <= 0 || string.IsNullOrWhiteSpace(entry.Name))
		{
			return CommunityModVerdict.Block("The entry is incomplete.");
		}
		if (!IsTrustedUrl(entry.ProfileUrl))
		{
			return CommunityModVerdict.Block("The entry does not come from a trusted community source.");
		}
		if (HiddenCategories.Contains(entry.Category) || HiddenCategories.Contains(entry.SuperCategory))
		{
			return CommunityModVerdict.Block("The entry is in a category Voidstrap does not show.");
		}
		string haystack = Flatten(entry.Name, entry.Summary, entry.Category, entry.SuperCategory, string.Join(" ", entry.Tags));
		string? term = FindBlockedTerm(haystack);
		if (term != null)
		{
			return CommunityModVerdict.Block("The entry looks like a cheat or an exploit, matched on " + term + ".");
		}
		term = FindExternalToolTerm(haystack);
		if (term != null)
		{
			return CommunityModVerdict.Block("The entry is a separate program or a browser extension, matched on " + term + ".");
		}
		return CommunityModVerdict.Ok;
	}

	public static CommunityModVerdict InspectDetail(CommunityModEntry entry)
	{
		CommunityModVerdict listing = InspectListing(entry);
		if (!listing.Allowed)
		{
			return listing;
		}
		string haystack = Flatten(entry.DescriptionHtml);
		string? term = FindBlockedTerm(haystack);
		if (term != null)
		{
			return CommunityModVerdict.Block("The description advertises cheating, matched on " + term + ".");
		}
		term = FindExternalToolTerm(haystack);
		if (term != null)
		{
			return CommunityModVerdict.Block("The description describes a separate program or a browser extension, matched on " + term + ".");
		}
		if (!entry.Files.Any(IsInstallableFile))
		{
			return CommunityModVerdict.Block("No scanned package is available for this mod.");
		}
		if (IsFlagList(entry))
		{
			return CommunityModVerdict.Block("The entry is a fast flag list, not a Roblox mod.");
		}
		return CommunityModVerdict.Ok;
	}

	public static bool IsInstallableFile(CommunityModFile file)
	{
		return InspectFile(file).Allowed;
	}

	public static CommunityModVerdict InspectFile(CommunityModFile? file)
	{
		if (file == null)
		{
			return CommunityModVerdict.Block("The download is missing.");
		}
		if (file.LacksRobloxContent)
		{
			return CommunityModVerdict.Block("The package holds no Roblox client files, so it is not a Roblox mod.");
		}
		if (file.ContainsExecutable)
		{
			return CommunityModVerdict.Block("The package contains a program or a script, so it is not a Roblox mod.");
		}
		if (!IsTrustedDownloadUrl(file.DownloadUrl))
		{
			return CommunityModVerdict.Block("The download does not come from a verified GameBanana file host over https.");
		}
		string extension = Path.GetExtension(file.FileName);
		if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) && IsFlagFileName(file.FileName))
		{
			return CommunityModVerdict.Block("The file is a fast flag list, not a mod.");
		}
		if (!InstallablePackageExtensions.Contains(extension))
		{
			return CommunityModVerdict.Block("Only zip, rar and 7z packages and Fleasion JSON configs can be installed, this one is " + extension + ".");
		}
		if (MarketplaceCatalog.IsMarketplaceUrl(file.DownloadUrl))
		{
			return CommunityModVerdict.Ok;
		}
		if (file.SizeBytes <= 0 || file.SizeBytes > MaxPackageBytes)
		{
			return CommunityModVerdict.Block("The package is larger than the " + MaxPackageBytes / 1048576 + " MB limit.");
		}
		if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) && file.SizeBytes > MaxConfigBytes)
		{
			return CommunityModVerdict.Block("The replacement config is larger than the " + MaxConfigBytes / 1048576 + " MB limit.");
		}
		if (!string.Equals(file.AntivirusState, "done", StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(file.AntivirusResult, "clean", StringComparison.OrdinalIgnoreCase))
		{
			return CommunityModVerdict.Block("The GameBanana virus scan did not report the file as clean.");
		}
		if (!string.Equals(file.AnalysisState, "done", StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(file.AnalysisResult, "ok", StringComparison.OrdinalIgnoreCase))
		{
			return CommunityModVerdict.Block("The GameBanana content analysis did not pass.");
		}
		if (file.Md5.Length != 32 || !file.Md5.All(Uri.IsHexDigit))
		{
			return CommunityModVerdict.Block("The package does not publish a usable checksum.");
		}
		return CommunityModVerdict.Ok;
	}

	public static CommunityModVerdict InspectArchive(string archivePath, CancellationToken token = default)
	{
		CommunityModVerdict verdict = InspectArchive(archivePath, out bool hasRobloxContent, token);
		if (!verdict.Allowed)
		{
			return verdict;
		}
		return hasRobloxContent
			? CommunityModVerdict.Ok
			: CommunityModVerdict.Block("Nothing in the package could be matched to a Roblox client folder, so it is not a Roblox mod.");
	}

	public static CommunityModVerdict InspectArchive(string archivePath, out bool hasRobloxContent, CancellationToken token = default)
	{
		hasRobloxContent = false;
		try
		{
			using ZipArchive archive = ZipFile.OpenRead(archivePath);
			if (archive.Entries.Count == 0)
			{
				return CommunityModVerdict.Block("The package is empty.");
			}
			if (archive.Entries.Count > MaxArchiveEntries)
			{
				return CommunityModVerdict.Block("The package contains more than " + MaxArchiveEntries + " files.");
			}
			long compressed = 0;
			long extracted = 0;
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				token.ThrowIfCancellationRequested();
				string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
				if (relative.EndsWith(Path.DirectorySeparatorChar))
				{
					continue;
				}
				CommunityModVerdict path = InspectRelativePath(relative);
				if (!path.Allowed)
				{
					return path;
				}
				compressed += entry.CompressedLength;
				extracted += entry.Length;
				if (extracted > MaxExtractedBytes)
				{
					return CommunityModVerdict.Block("The package expands to more than " + MaxExtractedBytes / 1048576 + " MB.");
				}
				if (IsInstallableAsset(relative) && RobloxContentPlacer.Resolve(relative) != null)
				{
					hasRobloxContent = true;
				}
			}
			if (compressed > 0 && extracted > ZipBombFloorBytes && extracted / compressed > MaxCompressionRatio)
			{
				return CommunityModVerdict.Block("The package expands far beyond its download size, which is how zip bombs behave.");
			}
			return CommunityModVerdict.Ok;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (InvalidDataException)
		{
			return CommunityModVerdict.Block("The package is not a readable zip archive.");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The package could not be inspected: " + ex.Message);
			return CommunityModVerdict.Block("The package could not be inspected.");
		}
	}

	public static IReadOnlyCollection<string> AssetExtensions => AllowedAssetExtensions;

	public static bool IsDangerousName(string relative)
	{
		return DangerousExtensions.Contains(Path.GetExtension(relative)) && !IsClientCopy(relative.Replace('/', Path.DirectorySeparatorChar));
	}

	public static bool IsClientCopy(string relative)
	{
		try
		{
			string? resolved = RobloxContentPlacer.Resolve(relative);
			return resolved != null
				&& string.Equals(Path.GetExtension(resolved), Path.GetExtension(relative), StringComparison.OrdinalIgnoreCase)
				&& Voidstrap.Utility.RobloxLayoutRepair.ExistsInClient(resolved);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
		{
			return false;
		}
	}

	public readonly record struct ArchiveListingSummary(bool RobloxContent, bool Config, bool Assets, bool CacheEntries);

	public static ArchiveListingSummary SummarizeListing(IReadOnlyList<string> entries)
	{
		bool roblox = false;
		bool config = false;
		bool assets = false;
		bool cache = false;
		foreach (string entry in entries)
		{
			string relative = entry.Replace('/', Path.DirectorySeparatorChar);
			string name = Path.GetFileName(relative);
			if (name.Length == 0)
			{
				continue;
			}
			if (name.Length == 32 && name.All(Uri.IsHexDigit))
			{
				if (!relative.Contains("RESTORE", StringComparison.OrdinalIgnoreCase))
				{
					cache = true;
				}
				continue;
			}
			string extension = Path.GetExtension(name);
			if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
			{
				if (!IsFlagFileName(name) && RobloxContentPlacer.Resolve(relative) == null)
				{
					config = true;
				}
				continue;
			}
			if (!IsInstallableAsset(relative))
			{
				continue;
			}
			assets = true;
			if (!roblox && RobloxContentPlacer.Resolve(relative) != null)
			{
				roblox = true;
			}
		}
		return new ArchiveListingSummary(roblox, config, assets, cache);
	}

	public static bool ArchiveListingIsRobloxMod(IReadOnlyList<string> entries, bool requiresReplacement)
	{
		foreach (string entry in entries)
		{
			string relative = entry.Replace('/', Path.DirectorySeparatorChar);
			string name = Path.GetFileName(relative);
			if (name.Length == 0)
			{
				continue;
			}
			string extension = Path.GetExtension(name);
			if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
			if (name.Length == 32 && name.All(Uri.IsHexDigit))
			{
				return true;
			}
			if (!IsInstallableAsset(relative))
			{
				continue;
			}
			if (requiresReplacement || RobloxContentPlacer.Resolve(relative) != null)
			{
				return true;
			}
		}
		return false;
	}

	private static readonly string[] FlagFileTokens = { "fflag", "fastflag", "clientappsettings", "ixpsettings" };

	public static bool IsFlagFileName(string fileName)
	{
		string compact = new string(Path.GetFileNameWithoutExtension(fileName).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
		return FlagFileTokens.Any(token => compact.Contains(token, StringComparison.Ordinal));
	}

	public static bool IsInstallableAsset(string relative)
	{
		return AllowedAssetExtensions.Contains(Path.GetExtension(relative));
	}

	public static CommunityModVerdict InspectRelativePath(string relative)
	{
		if (string.IsNullOrWhiteSpace(relative))
		{
			return CommunityModVerdict.Block("The package contains an unnamed file.");
		}
		if (relative.Contains("..", StringComparison.Ordinal))
		{
			return CommunityModVerdict.Block("The package tries to escape its folder with a relative path.");
		}
		if (Path.IsPathRooted(relative) || relative.Contains(':', StringComparison.Ordinal))
		{
			return CommunityModVerdict.Block("The package contains an absolute path.");
		}
		if (relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
		{
			return CommunityModVerdict.Block("The package contains an invalid file name.");
		}
		foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
		{
			string trimmed = segment.TrimEnd(' ', '.');
			if (trimmed.Length == 0 || trimmed.Length != segment.Length || segment.IndexOfAny(InvalidNameChars) >= 0)
			{
				return CommunityModVerdict.Block("The package contains an invalid file name.");
			}
			if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(segment)))
			{
				return CommunityModVerdict.Block("The package uses the reserved Windows name " + segment + ".");
			}
		}
		string root = GetRoot(relative);
		if (BlockedRoots.Contains(root))
		{
			return CommunityModVerdict.Block("The package writes into the protected " + root + " folder.");
		}
		string extension = Path.GetExtension(relative);
		if (DangerousExtensions.Contains(extension) && !IsClientCopy(relative))
		{
			return CommunityModVerdict.Block("The package contains a " + extension + " file, which a Roblox mod never needs.");
		}
		return CommunityModVerdict.Ok;
	}

	private static string GetRoot(string relative)
	{
		int separator = relative.IndexOf('\\', StringComparison.Ordinal);
		return separator <= 0 ? "" : relative[..separator];
	}

	private static bool IsFlagList(CommunityModEntry entry)
	{
		List<CommunityModFile> installable = entry.Files.Where(IsInstallableFile).ToList();
		if (installable.Count == 0 || !installable.All(file => string.Equals(Path.GetExtension(file.FileName), ".json", StringComparison.OrdinalIgnoreCase)))
		{
			return false;
		}
		string haystack = Flatten(entry.Name, entry.Summary, entry.DescriptionHtml);
		try
		{
			return FlagListPattern.IsMatch(haystack);
		}
		catch (RegexMatchTimeoutException)
		{
			return false;
		}
	}

	private static string? FindExternalToolTerm(string haystack)
	{
		try
		{
			Match match = ExternalToolPattern.Match(haystack);
			return match.Success ? match.Value.Trim() : null;
		}
		catch (RegexMatchTimeoutException)
		{
			return null;
		}
	}

	private static string? FindBlockedTerm(string haystack)
	{
		try
		{
			Match match = BlockedTermPattern.Match(haystack);
			return match.Success ? match.Value.Trim() : null;
		}
		catch (RegexMatchTimeoutException)
		{
			return null;
		}
	}

	private static string Flatten(params string?[] values)
	{
		return " " + string.Join(" ", values.Where(value => !string.IsNullOrEmpty(value))).ToLower(CultureInfo.InvariantCulture) + " ";
	}
}
