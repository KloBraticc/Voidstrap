using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Integrations.AssetProxy;

namespace Voidstrap.Integrations.CommunityMods;

internal static class FleasionModInstaller
{
	public const string ResultPrefix = "fleasion:";

	public const string AssetWarpResultPrefix = "assetwarp:";

	private const long MaxConfigBytes = 4L * 1024 * 1024;

	private static readonly Regex DescribedAssetId = new Regex(@"\bids?\b[^\d<>]{0,40}?(?<id>\d{6,19})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

	public static bool IsInstalled => File.Exists(Path.Combine(Paths.Fleasion, "Fleasion.exe"));

	private static string PackageLabel => Voidstrap.Utility.Platform.IsLinux ? "replacement" : "Fleasion";

	private static string ReplacementLabel => Voidstrap.Utility.Platform.IsLinux ? "replacement" : "Fleasion replacement";

	private static readonly string[] ReplacementHints = { "fleasion", "assetwarp", "asset warp" };

	public static bool LooksRequired(CommunityModEntry entry)
	{
		if (entry.Files.Any(file => string.Equals(Path.GetExtension(file.FileName), ".json", StringComparison.OrdinalIgnoreCase)
			&& !CommunityModGuard.IsFlagFileName(file.FileName)))
		{
			return true;
		}
		string text = (entry.Name + " " + entry.Summary + " " + entry.DescriptionHtml).ToLowerInvariant();
		return ReplacementHints.Any(hint => text.Contains(hint, StringComparison.Ordinal));
	}

	public static async Task<bool> ContainsConfigAsync(string packagePath, string extension, CancellationToken token)
	{
		try
		{
			if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
			{
				await using FileStream stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
				return await ReadConfigAsync(stream, token).ConfigureAwait(false) != null;
			}
			using ZipArchive archive = ZipFile.OpenRead(packagePath);
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				token.ThrowIfCancellationRequested();
				if (!string.Equals(Path.GetExtension(entry.Name), ".json", StringComparison.OrdinalIgnoreCase) || entry.Length > MaxConfigBytes)
				{
					continue;
				}
				await using Stream stream = entry.Open();
				if (await ReadConfigAsync(stream, token).ConfigureAwait(false) != null)
				{
					return true;
				}
			}
			return false;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static async Task<string?> TryInstallAsync(CommunityModEntry entry, CommunityModFile file, string packagePath, CancellationToken token, string? packageExtension = null)
	{
		string extension = packageExtension ?? Path.GetExtension(file.FileName);
		string suffix = ConfigSuffix(entry, file);
		IReadOnlyList<string>? configs = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
			? await InstallJsonAsync(entry, packagePath, InstallTarget.Fleasion, suffix, token).ConfigureAwait(false)
			: await InstallArchiveAsync(entry, packagePath, InstallTarget.Fleasion, suffix, token).ConfigureAwait(false)
				?? await InstallAssetsAsync(entry, packagePath, InstallTarget.Fleasion, token).ConfigureAwait(false);
		if (configs == null)
		{
			return null;
		}
		await EnableConfigsAsync(configs, token).ConfigureAwait(false);
		if (IsInstalled && App.Settings.Prop is { Fleasion: false } settings)
		{
			settings.Fleasion = true;
			App.Settings.Save();
		}
		return ResultPrefix + string.Join(", ", configs);
	}

	public static async Task<string?> TryInstallAssetWarpAsync(CommunityModEntry entry, CommunityModFile file, string packagePath, CancellationToken token, string? packageExtension = null)
	{
		string extension = packageExtension ?? Path.GetExtension(file.FileName);
		string suffix = ConfigSuffix(entry, file);
		IReadOnlyList<string>? configs = string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
			? await InstallJsonAsync(entry, packagePath, InstallTarget.AssetWarp, suffix, token).ConfigureAwait(false)
			: await InstallArchiveAsync(entry, packagePath, InstallTarget.AssetWarp, suffix, token).ConfigureAwait(false)
				?? await InstallAssetsAsync(entry, packagePath, InstallTarget.AssetWarp, token).ConfigureAwait(false);
		if (configs == null)
		{
			return null;
		}
		TextureStripper.InvalidateRuntimeState();
		return AssetWarpResultPrefix + string.Join(", ", configs);
	}

	private static string ConfigSuffix(CommunityModEntry entry, CommunityModFile file)
	{
		return entry.Files.Count(CommunityModGuard.IsInstallableFile) > 1
			? Path.GetFileNameWithoutExtension(file.FileName)
			: "";
	}

	private static async Task<IReadOnlyList<string>> InstallJsonAsync(CommunityModEntry entry, string packagePath, InstallTarget target, string suffix, CancellationToken token)
	{
		await using FileStream stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
		JsonObject config = await ReadConfigAsync(stream, token).ConfigureAwait(false)
			?? throw new InvalidDataException("The JSON file is not a " + ReplacementLabel + " config.");
		RewriteLocalPaths(config, Array.Empty<string>(), "", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
		string name = BuildConfigName(entry, suffix);
		await WriteConfigAsync(name, config, GetConfigsFolder(target), token).ConfigureAwait(false);
		return new[] { name };
	}

	private static async Task<IReadOnlyList<string>?> InstallArchiveAsync(CommunityModEntry entry, string packagePath, InstallTarget target, string suffix, CancellationToken token)
	{
		using ZipArchive archive = ZipFile.OpenRead(packagePath);
		InspectArchive(archive, token);
		List<PreparedConfig> configs = new List<PreparedConfig>();
		foreach (ZipArchiveEntry zipEntry in archive.Entries)
		{
			token.ThrowIfCancellationRequested();
			if (!string.Equals(Path.GetExtension(zipEntry.Name), ".json", StringComparison.OrdinalIgnoreCase) || zipEntry.Length > MaxConfigBytes)
			{
				continue;
			}
			await using Stream stream = zipEntry.Open();
			JsonObject? config = await ReadConfigAsync(stream, token).ConfigureAwait(false);
			if (config == null)
			{
				continue;
			}
			string root = GetDirectory(zipEntry.FullName);
			Dictionary<string, ZipArchiveEntry> assets = archive.Entries
				.Where(candidate => !string.IsNullOrEmpty(candidate.Name) && candidate != zipEntry)
				.Select(candidate => (Entry: candidate, Relative: RelativeTo(candidate.FullName, root)))
				.Where(candidate => candidate.Relative != null)
				.ToDictionary(candidate => candidate.Relative!, candidate => candidate.Entry, StringComparer.OrdinalIgnoreCase);
			string name = BuildConfigName(entry, configs.Count == 0
				? suffix
				: (suffix.Length > 0 ? suffix + " " : "") + Path.GetFileNameWithoutExtension(zipEntry.Name));
			string assetRoot = (target == InstallTarget.Fleasion ? "Voidstrap Mods/" : "Assets/") + SafeName(entry.Name) + " " + entry.Id;
			HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			int wired = AutoWireRules(config["replacement_rules"] as JsonArray, assets.Keys.ToArray());
			if (wired > 0)
			{
				App.Logger?.WriteLine("FleasionModInstaller", "Matched " + wired + " unfinished replacement rules in " + entry.Name + " to their asset files");
			}
			RewriteLocalPaths(config, assets.Keys, assetRoot, used, target == InstallTarget.Fleasion);
			configs.Add(new PreparedConfig(name, config, assets, used, assetRoot));
		}
		if (configs.Count == 0)
		{
			return null;
		}

		string configsFolder = GetConfigsFolder(target);
		Directory.CreateDirectory(configsFolder);
		foreach (PreparedConfig config in configs)
		{
			foreach (string relative in config.UsedAssets)
			{
				ZipArchiveEntry source = config.Assets[relative];
				string destination = GetContainedPath(configsFolder, config.AssetRoot + "/" + relative);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				await using Stream input = source.Open();
				await using FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
				await input.CopyToAsync(output, token).ConfigureAwait(false);
			}
			await WriteConfigAsync(config.Name, config.Json, configsFolder, token).ConfigureAwait(false);
		}
		return configs.Select(config => config.Name).ToArray();
	}

	public static bool ArchiveHasAssets(string packagePath, CancellationToken token = default)
	{
		try
		{
			using ZipArchive archive = ZipFile.OpenRead(packagePath);
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				token.ThrowIfCancellationRequested();
				if (IsAssetEntry(entry))
				{
					return true;
				}
			}
			return false;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool IsAssetEntry(ZipArchiveEntry entry)
	{
		return !string.IsNullOrEmpty(entry.Name)
			&& !string.Equals(Path.GetExtension(entry.Name), ".json", StringComparison.OrdinalIgnoreCase)
			&& CommunityModGuard.IsInstallableAsset(entry.Name);
	}

	private static async Task<IReadOnlyList<string>?> InstallAssetsAsync(CommunityModEntry entry, string packagePath, InstallTarget target, CancellationToken token)
	{
		using ZipArchive archive = ZipFile.OpenRead(packagePath);
		InspectArchive(archive, token);
		ZipArchiveEntry[] assets = archive.Entries.Where(IsAssetEntry).ToArray();
		if (assets.Length == 0)
		{
			return null;
		}
		string configsFolder = GetConfigsFolder(target);
		string assetRoot = (target == InstallTarget.Fleasion ? "Voidstrap Mods/" : "Assets/") + SafeName(entry.Name) + " " + entry.Id;
		foreach (ZipArchiveEntry asset in assets)
		{
			token.ThrowIfCancellationRequested();
			string relative = asset.FullName.Replace('\\', '/');
			string destination = GetContainedPath(configsFolder, assetRoot + "/" + relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			await using Stream input = asset.Open();
			await using FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
			await input.CopyToAsync(output, token).ConfigureAwait(false);
		}
		App.Logger?.WriteLine("FleasionModInstaller", "Placed " + assets.Length + " asset files for " + entry.Name + " under " + assetRoot);
		string[] describedIds = DescribedAssetIds(entry.DescriptionHtml);
		if (assets.Length != 1 || describedIds.Length != 1)
		{
			return new[] { assetRoot };
		}
		string relativePath = assetRoot + "/" + assets[0].FullName.Replace('\\', '/');
		JsonObject config = new JsonObject
		{
			["replacement_rules"] = new JsonArray
			{
				new JsonObject
				{
					["name"] = Path.GetFileNameWithoutExtension(assets[0].Name),
					["replace_ids"] = new JsonArray(long.Parse(describedIds[0], System.Globalization.CultureInfo.InvariantCulture)),
					["mode"] = "local",
					["enabled"] = true,
					["local_path"] = target == InstallTarget.Fleasion ? "/" + relativePath : relativePath
				}
			}
		};
		string name = BuildConfigName(entry, "");
		await WriteConfigAsync(name, config, configsFolder, token).ConfigureAwait(false);
		App.Logger?.WriteLine("FleasionModInstaller", "Wired " + assets[0].Name + " to asset " + describedIds[0] + " from the description of " + entry.Name);
		return new[] { name };
	}

	internal static string[] DescribedAssetIds(string? description)
	{
		string text = Regex.Replace(description ?? "", "<[^>]*>", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
		if (!DescribedAssetId.IsMatch(text))
		{
			return Array.Empty<string>();
		}
		return Regex.Matches(text, @"(?<![\d.])\d{6,19}(?![\d.])", RegexOptions.None, TimeSpan.FromSeconds(1))
			.Select(match => match.Value)
			.Where(id => long.TryParse(id, out long value) && value > 0)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
	}

	private static void InspectArchive(ZipArchive archive, CancellationToken token)
	{
		if (archive.Entries.Count == 0 || archive.Entries.Count > CommunityModGuard.MaxArchiveEntries)
		{
			throw new InvalidDataException("The " + PackageLabel + " package has an invalid number of files.");
		}
		long compressed = 0;
		long extracted = 0;
		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			token.ThrowIfCancellationRequested();
			if (string.IsNullOrEmpty(entry.Name))
			{
				continue;
			}
			CommunityModVerdict verdict = CommunityModGuard.InspectRelativePath(entry.FullName.Replace('/', '\\'));
			if (!verdict.Allowed)
			{
				throw new InvalidDataException(verdict.Reason);
			}
			compressed += entry.CompressedLength;
			extracted += entry.Length;
			if (extracted > CommunityModGuard.MaxExtractedBytes)
			{
				throw new InvalidDataException("The " + PackageLabel + " package expands beyond the allowed size.");
			}
		}
		if (compressed > 0 && extracted > CommunityModGuard.ZipBombFloorBytes && extracted / compressed > CommunityModGuard.MaxCompressionRatio)
		{
			throw new InvalidDataException("The " + PackageLabel + " package expands far beyond its download size.");
		}
	}

	private static async Task<JsonObject?> ReadConfigAsync(Stream stream, CancellationToken token)
	{
		try
		{
			JsonNode? node = await JsonNode.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
			return node is JsonObject obj && obj["replacement_rules"] is JsonArray rules && rules.Count > 0 ? obj : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static int AutoWireRules(JsonArray? rules, IReadOnlyList<string> availableAssets)
	{
		if (rules == null)
		{
			return 0;
		}
		string[] candidates = availableAssets
			.Where(asset => !string.Equals(Path.GetExtension(asset), ".json", StringComparison.OrdinalIgnoreCase))
			.ToArray();
		if (candidates.Length == 0)
		{
			return 0;
		}
		int wired = 0;
		foreach (JsonNode? node in rules)
		{
			if (node is not JsonObject rule)
			{
				continue;
			}
			if (rule["children"] is JsonArray children)
			{
				wired += AutoWireRules(children, availableAssets);
			}
			if (rule["local_path"] == null && MatchAssetById(ReadText(rule["mode"], "id"), rule["with_id"] ?? rule["replace_with"], candidates) is string bundled)
			{
				rule.Remove("with_id");
				rule.Remove("replace_with");
				rule["mode"] = "local";
				rule["local_path"] = bundled.Replace('\\', '/');
				wired++;
				continue;
			}
			if (rule["with_id"] != null || rule["replace_with"] != null || rule["local_path"] != null || rule["cdn_url"] != null)
			{
				continue;
			}
			string mode = ReadText(rule["mode"], "id");
			if (!string.Equals(mode, "id", StringComparison.OrdinalIgnoreCase) && !string.Equals(mode, "local", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string? match = MatchAssetByName(ReadText(rule["name"], ""), candidates);
			if (match == null)
			{
				continue;
			}
			rule["mode"] = "local";
			rule["local_path"] = match.Replace('\\', '/');
			wired++;
		}
		return wired;
	}

	internal static string? MatchAssetById(string mode, JsonNode? withId, IReadOnlyList<string> candidates)
	{
		if (!string.Equals(mode, "id", StringComparison.OrdinalIgnoreCase) || withId == null)
		{
			return null;
		}
		string id = withId.ToJsonString().Trim('"').Trim();
		if (id.Length < 6 || !id.All(char.IsAsciiDigit))
		{
			return null;
		}
		string[] matches = candidates
			.Where(candidate => Regex.IsMatch(Path.GetFileNameWithoutExtension(candidate), "(?<![0-9])" + id + "(?![0-9])", RegexOptions.None, TimeSpan.FromSeconds(1)))
			.ToArray();
		return matches.Length == 1 ? matches[0] : null;
	}

	private static string ReadText(JsonNode? node, string fallback)
	{
		return node is JsonValue value && value.TryGetValue(out string? text) && text != null ? text : fallback;
	}

	internal static string? MatchAssetByName(string ruleName, IReadOnlyList<string> candidates)
	{
		string key = NormalizeToken(ruleName);
		if (key.Length == 0)
		{
			return null;
		}
		string? best = null;
		int bestLength = 0;
		bool tied = false;
		foreach (string candidate in candidates)
		{
			string token = NormalizeToken(Path.GetFileNameWithoutExtension(candidate));
			if (token.Length < 3 || !key.Contains(token, StringComparison.Ordinal))
			{
				continue;
			}
			if (token.Length > bestLength)
			{
				best = candidate;
				bestLength = token.Length;
				tied = false;
			}
			else if (token.Length == bestLength)
			{
				tied = true;
			}
		}
		return tied ? null : best;
	}

	private static string NormalizeToken(string value)
	{
		StringBuilder builder = new StringBuilder(value.Length);
		foreach (char character in value)
		{
			if (char.IsLetterOrDigit(character))
			{
				builder.Append(char.ToLowerInvariant(character));
			}
		}
		return builder.ToString();
	}

	private static void RewriteLocalPaths(JsonObject config, IEnumerable<string> availableAssets, string assetRoot, ISet<string> usedAssets, bool usePortablePaths = true)
	{
		JsonArray rules = config["replacement_rules"]!.AsArray();
		RewriteRules(rules, availableAssets.ToArray(), assetRoot, usedAssets, usePortablePaths);
	}

	private static void RewriteRules(JsonArray rules, IReadOnlyList<string> availableAssets, string assetRoot, ISet<string> usedAssets, bool usePortablePaths)
	{
		foreach (JsonNode? node in rules)
		{
			if (node is not JsonObject rule)
			{
				throw new InvalidDataException("The " + PackageLabel + " config contains an invalid replacement rule.");
			}
			if (rule["children"] is JsonArray children)
			{
				RewriteRules(children, availableAssets, assetRoot, usedAssets, usePortablePaths);
			}
			ValidateRuleUrls(rule);
			if (!string.Equals(rule["mode"]?.GetValue<string>(), "local", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string requested = rule["local_path"]?.GetValue<string>() ?? "";
			string? asset = FindAsset(requested, availableAssets);
			if (asset == null)
			{
				throw new InvalidDataException("A local " + ReplacementLabel + " file is missing from the package.");
			}
			string destinationRelative = assetRoot + "/" + asset.Replace('\\', '/');
			if (destinationRelative.Split('/', StringSplitOptions.RemoveEmptyEntries).Length - 1 > 10)
			{
				throw new InvalidDataException("The " + PackageLabel + " package nests its replacement files too deeply.");
			}
			rule["local_path"] = usePortablePaths ? "/" + destinationRelative : destinationRelative;
			usedAssets.Add(asset);
		}
	}

	private static void ValidateRuleUrls(JsonObject rule)
	{
		foreach (KeyValuePair<string, JsonNode?> property in rule)
		{
			if (property.Value is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
			{
				continue;
			}
			string text = value.GetValue<string>();
			if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.IsFile)
			{
				continue;
			}
			if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidDataException("The replacement config points at " + uri.Scheme + ", only https links are allowed.");
			}
		}
	}

	private static string? FindAsset(string requested, IReadOnlyList<string> availableAssets)
	{
		string normalized = requested.Replace('\\', '/').Trim();
		int marker = normalized.IndexOf("/configs/", StringComparison.OrdinalIgnoreCase);
		if (marker >= 0)
		{
			normalized = normalized[(marker + 9)..];
		}
		else
		{
			normalized = normalized.TrimStart('/');
		}
		string? exact = availableAssets.FirstOrDefault(asset => string.Equals(asset.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
		if (exact != null)
		{
			return exact;
		}
		string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		for (int start = 1; start < segments.Length - 1; start++)
		{
			string suffix = string.Join('/', segments, start, segments.Length - start);
			string[] suffixMatches = availableAssets
				.Where(asset =>
				{
					string candidate = asset.Replace('\\', '/');
					return string.Equals(candidate, suffix, StringComparison.OrdinalIgnoreCase)
						|| candidate.EndsWith("/" + suffix, StringComparison.OrdinalIgnoreCase);
				})
				.ToArray();
			if (suffixMatches.Length == 1)
			{
				return suffixMatches[0];
			}
			if (suffixMatches.Length > 1)
			{
				return null;
			}
		}
		string fileName = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
		string[] matches = availableAssets.Where(asset => string.Equals(Path.GetFileName(asset), fileName, StringComparison.OrdinalIgnoreCase)).ToArray();
		return matches.Length == 1 ? matches[0] : null;
	}

	private static async Task WriteConfigAsync(string name, JsonObject config, string folder, CancellationToken token)
	{
		Directory.CreateDirectory(folder);
		string path = GetContainedPath(folder, name + ".json");
		await File.WriteAllTextAsync(path, config.ToJsonString(JsonOptions), new UTF8Encoding(false), token).ConfigureAwait(false);
	}

	private static async Task EnableConfigsAsync(IReadOnlyList<string> names, CancellationToken token)
	{
		string folder = Path.GetDirectoryName(GetConfigsFolder(InstallTarget.Fleasion))!;
		Directory.CreateDirectory(folder);
		string path = Path.Combine(folder, "settings.json");
		JsonObject settings = new JsonObject();
		if (File.Exists(path))
		{
			await using FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
			settings = await JsonNode.ParseAsync(input, cancellationToken: token).ConfigureAwait(false) as JsonObject
				?? throw new InvalidDataException("Fleasion settings are not valid JSON.");
		}
		JsonArray enabled = settings["enabled_configs"] as JsonArray ?? new JsonArray();
		settings["enabled_configs"] = enabled;
		foreach (string name in names)
		{
			if (!enabled.Any(node => string.Equals(node?.GetValue<string>(), name, StringComparison.Ordinal)))
			{
				enabled.Add(name);
			}
		}
		settings["last_config"] = names[0];
		string temporary = path + ".tmp";
		try
		{
			await File.WriteAllTextAsync(temporary, settings.ToJsonString(JsonOptions), new UTF8Encoding(false), token).ConfigureAwait(false);
			File.Move(temporary, path, true);
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	internal static string ConfigsFolderFor(bool fleasion)
	{
		return GetConfigsFolder(fleasion ? InstallTarget.Fleasion : InstallTarget.AssetWarp);
	}

	private static string GetConfigsFolder(InstallTarget target)
	{
		return target == InstallTarget.Fleasion
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleasionNT", "configs")
			: Path.Combine(Paths.AssetProxy, "Configs");
	}

	private static string BuildConfigName(CommunityModEntry entry, string suffix)
	{
		string name = SafeName(entry.Name) + " " + entry.Id;
		if (!string.IsNullOrWhiteSpace(suffix))
		{
			name += " " + SafeName(suffix);
		}
		return name.Length <= 100 ? name : name[..100].Trim();
	}

	private static string SafeName(string value)
	{
		StringBuilder builder = new StringBuilder(value.Length);
		foreach (char character in value)
		{
			builder.Append(Path.GetInvalidFileNameChars().Contains(character) ? ' ' : character);
		}
		string name = string.Join(" ", builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
		return string.IsNullOrWhiteSpace(name) ? "Community mod" : name;
	}

	private static string GetDirectory(string path)
	{
		int separator = path.LastIndexOf('/');
		return separator < 0 ? "" : path[..(separator + 1)];
	}

	private static string? RelativeTo(string path, string root)
	{
		if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		string relative = path[root.Length..].Replace('\\', '/');
		return string.IsNullOrWhiteSpace(relative) ? null : relative;
	}

	private static string GetContainedPath(string root, string relative)
	{
		string fullRoot = Path.GetFullPath(root);
		string path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
		if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("The " + PackageLabel + " package tried to write outside its config folder.");
		}
		return path;
	}

	private sealed record PreparedConfig(
		string Name,
		JsonObject Json,
		IReadOnlyDictionary<string, ZipArchiveEntry> Assets,
		IReadOnlySet<string> UsedAssets,
		string AssetRoot);

	private enum InstallTarget
	{
		AssetWarp,
		Fleasion
	}
}
