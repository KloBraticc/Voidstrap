using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.CommunityMods;

internal sealed class ModSlotOption
{
	public string Label { get; init; } = "";

	public string? SourcePath { get; init; }
}

internal sealed class ModSlot
{
	public string TargetRelative { get; init; } = "";

	public List<ModSlotOption> Options { get; } = [];

	public ModSlotOption? Selected { get; set; }
}

internal static partial class ModVariantStore
{
	private const string LogIdent = "ModVariantStore";

	private const string InstalledFolderName = "Installed";

	private static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Hash)> HashCache = new(StringComparer.OrdinalIgnoreCase);

	public const string OffLabel = "Off, use the Roblox default";

	public static readonly HashSet<string> SlotExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".ktx", ".webp", ".tex",
		".ogg", ".mp3", ".wav", ".flac", ".mesh", ".ttf", ".otf", ".gif"
	};

	public static readonly HashSet<string> PreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".png", ".jpg", ".jpeg", ".bmp", ".gif"
	};

	[GeneratedRegex(@"^(?<base>.+?)(?:\s*\(\d+\)|\s+copy(?:\s*\d+)?|[\s_-]+(?:v?\d{1,2}|alt\d*|variant\d*|version\d*))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex VariantSuffix();

	public static string GetSourcesFolder(string recordId)
	{
		string folder = ManagedModStore.GetFolder(recordId);
		return Path.Combine(Paths.ManagedMods, "Sources", Path.GetFileName(folder));
	}

	public static bool HasSources(string recordId)
	{
		try
		{
			string sources = GetSourcesFolder(recordId);
			return Directory.Exists(sources)
				&& Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories)
					.Any(file => !Path.GetRelativePath(sources, file).StartsWith(InstalledFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return false;
		}
	}

	public static bool HasSlots(string recordId)
	{
		try
		{
			if (HasSources(recordId))
			{
				return true;
			}
			string folder = ManagedModStore.GetFolder(recordId);
			return Directory.Exists(folder)
				&& Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
					.Any(file => SlotExtensions.Contains(Path.GetExtension(file)));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return false;
		}
	}

	public static string? ResolveSlot(string relative)
	{
		string normalized = relative.Replace('/', Path.DirectorySeparatorChar);
		string extension = Path.GetExtension(normalized);
		if (!SlotExtensions.Contains(extension))
		{
			return null;
		}
		string? direct = RobloxContentPlacer.Resolve(normalized);
		if (direct != null)
		{
			return direct;
		}
		Match match = VariantSuffix().Match(Path.GetFileNameWithoutExtension(normalized));
		if (!match.Success)
		{
			return null;
		}
		string directory = Path.GetDirectoryName(normalized) ?? "";
		string baseName = match.Groups["base"].Value.Trim() + extension;
		return RobloxContentPlacer.Resolve(directory.Length == 0 ? baseName : Path.Combine(directory, baseName));
	}

	public static int CaptureSources(string recordId, string archivePath, CancellationToken token = default)
	{
		string root = Path.GetFullPath(GetSourcesFolder(recordId));
		List<(int Index, string Target)> plan = [];
		using (ZipArchive archive = ZipFile.OpenRead(archivePath))
		{
			long total = 0;
			for (int index = 0; index < archive.Entries.Count; index++)
			{
				token.ThrowIfCancellationRequested();
				ZipArchiveEntry entry = archive.Entries[index];
				string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
				if (string.IsNullOrEmpty(entry.Name) || !SlotExtensions.Contains(Path.GetExtension(relative)) || !CommunityModGuard.InspectRelativePath(relative).Allowed)
				{
					continue;
				}
				total += entry.Length;
				if (total > CommunityModGuard.MaxExtractedBytes)
				{
					break;
				}
				string target = Path.GetFullPath(Path.Combine(root, relative));
				if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				plan.Add((index, target));
			}
		}
		return ParallelZipWriter.Extract(archivePath, ParallelZipWriter.LastWins(plan), token);
	}

	public static async Task<int> DownloadSourcesAsync(string recordId, ModPackInfo? pack, IProgress<string>? progress, CancellationToken token)
	{
		if (pack == null || pack.Id <= 0 || !string.Equals(pack.Source, GameBananaCatalog.SourceName, StringComparison.OrdinalIgnoreCase))
		{
			return 0;
		}
		return await GameBananaCatalog.FetchAllAssetsAsync(pack.Id, GetSourcesFolder(recordId), SlotExtensions, progress, token).ConfigureAwait(false);
	}

	public static List<ModSlot> BuildSlots(string recordId)
	{
		string modFolder = ManagedModStore.GetFolder(recordId);
		string sources = GetSourcesFolder(recordId);
		CaptureInstalled(modFolder, sources);
		if (Directory.Exists(sources))
		{
			WarmHashes(Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories));
		}

		Dictionary<string, List<string>> groups = new(StringComparer.OrdinalIgnoreCase);
		if (Directory.Exists(sources))
		{
			string installedPrefix = InstalledFolderName + Path.DirectorySeparatorChar;
			foreach (string file in Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories))
			{
				string relative = Path.GetRelativePath(sources, file);
				string? slot = relative.StartsWith(installedPrefix, StringComparison.OrdinalIgnoreCase)
					? relative[installedPrefix.Length..]
					: ResolveSlot(relative);
				if (slot == null || !SlotExtensions.Contains(Path.GetExtension(slot)))
				{
					continue;
				}
				if (!groups.TryGetValue(slot, out List<string>? files))
				{
					files = [];
					groups[slot] = files;
				}
				files.Add(file);
			}
		}

		List<ModSlot> result = [];
		foreach ((string target, List<string> files) in groups.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
		{
			ModSlot slot = new() { TargetRelative = target };
			ModSlotOption off = new() { Label = OffLabel };
			slot.Options.Add(off);

			Dictionary<string, string> seen = new(StringComparer.Ordinal);
			IEnumerable<string> ordered = files
				.OrderBy(file => IsInstalledCopy(sources, file) ? 1 : 0)
				.ThenBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);
			foreach (string file in ordered)
			{
				string hash = HashFile(file);
				if (hash.Length == 0 || seen.ContainsKey(hash))
				{
					continue;
				}
				seen[hash] = file;
				string label = IsInstalledCopy(sources, file) ? "Installed file" : Path.GetFileName(file);
				if (slot.Options.Any(option => option.Label == label))
				{
					label += " (" + Path.GetFileName(Path.GetDirectoryName(file)) + ")";
				}
				slot.Options.Add(new ModSlotOption { Label = label, SourcePath = file });
			}

			string installed = Path.Combine(modFolder, target);
			slot.Selected = off;
			if (File.Exists(installed))
			{
				string installedHash = HashFile(installed);
				slot.Selected = slot.Options.FirstOrDefault(option => option.SourcePath != null
					&& seen.TryGetValue(installedHash, out string? match)
					&& string.Equals(match, option.SourcePath, StringComparison.OrdinalIgnoreCase)) ?? off;
			}
			result.Add(slot);
		}
		return result;
	}

	public static int ApplySlots(string recordId, IEnumerable<ModSlot> slots)
	{
		string modRoot = Path.GetFullPath(ManagedModStore.GetFolder(recordId));
		int changed = 0;
		foreach (ModSlot slot in slots)
		{
			string target = Path.GetFullPath(Path.Combine(modRoot, slot.TargetRelative));
			if (!target.StartsWith(modRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			try
			{
				string? source = slot.Selected?.SourcePath;
				if (source == null)
				{
					if (File.Exists(target))
					{
						File.Delete(target);
						changed++;
					}
					continue;
				}
				if (File.Exists(target) && HashFile(target) == HashFile(source))
				{
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Copy(source, target, overwrite: true);
				changed++;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "The file " + slot.TargetRelative + " could not be changed: " + ex.Message);
			}
		}
		App.Logger?.WriteLine(LogIdent, "Applied " + changed + " option changes to managed mod " + recordId[..Math.Min(8, recordId.Length)]);
		return changed;
	}

	public static void DeleteSources(string recordId)
	{
		try
		{
			string sources = GetSourcesFolder(recordId);
			if (Directory.Exists(sources))
			{
				Directory.Delete(sources, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			App.Logger?.WriteLine(LogIdent, "The saved options could not be removed: " + ex.Message);
		}
	}

	private static void CaptureInstalled(string modFolder, string sources)
	{
		if (!Directory.Exists(modFolder))
		{
			return;
		}
		HashSet<string> known = new(StringComparer.Ordinal);
		WarmHashes(Directory.EnumerateFiles(modFolder, "*", SearchOption.AllDirectories).Where(file => SlotExtensions.Contains(Path.GetExtension(file))));
		if (Directory.Exists(sources))
		{
			WarmHashes(Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories));
			foreach (string file in Directory.EnumerateFiles(sources, "*", SearchOption.AllDirectories))
			{
				string hash = HashFile(file);
				if (hash.Length > 0)
				{
					known.Add(hash);
				}
			}
		}
		foreach (string file in Directory.EnumerateFiles(modFolder, "*", SearchOption.AllDirectories))
		{
			if (!SlotExtensions.Contains(Path.GetExtension(file)))
			{
				continue;
			}
			string hash = HashFile(file);
			if (hash.Length == 0 || known.Contains(hash))
			{
				continue;
			}
			string relative = Path.GetRelativePath(modFolder, file);
			string copy = Path.Combine(sources, InstalledFolderName, relative);
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
				File.Copy(file, copy, overwrite: true);
				known.Add(hash);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "The installed file " + relative + " could not be kept as an option: " + ex.Message);
			}
		}
	}

	private static bool IsInstalledCopy(string sources, string file)
	{
		return Path.GetRelativePath(sources, file).StartsWith(InstalledFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	private static void WarmHashes(IEnumerable<string> files)
	{
		Parallel.ForEach(files, file => HashFile(file));
	}

	private static string HashFile(string path)
	{
		try
		{
			FileInfo info = new FileInfo(path);
			long length = info.Length;
			DateTime written = info.LastWriteTimeUtc;
			if (HashCache.TryGetValue(path, out (long Length, DateTime Written, string Hash) cached) && cached.Length == length && cached.Written == written)
			{
				return cached.Hash;
			}
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
			string hash = Convert.ToHexString(SHA256.HashData(stream));
			HashCache[path] = (length, written, hash);
			return hash;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return "";
		}
	}
}
