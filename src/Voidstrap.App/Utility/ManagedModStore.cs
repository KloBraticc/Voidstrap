using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal sealed class ManagedModRecord
{
	public string Id { get; set; } = string.Empty;

	public string Name { get; set; } = string.Empty;

	public bool Enabled { get; set; } = true;

	public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

internal sealed class ManagedModIndex
{
	public int Version { get; set; } = 1;

	public List<ManagedModRecord> Mods { get; set; } = [];
}

internal readonly record struct ManagedModFile(ManagedModRecord Mod, string Source, string Relative);

internal sealed class ManagedModLibraryEntry
{
	public ManagedModLibraryEntry(ManagedModRecord record)
	{
		Record = record;
	}

	public ManagedModRecord Record { get; }

	public int FileCount { get; set; }

	public long TotalBytes { get; set; }

	public HashSet<string> RelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

	public string Failure { get; set; } = string.Empty;

	public bool AppliedOnTop { get; set; }

	public ModPackInfo? Pack { get; set; }
}

internal sealed class ManagedModScanResult
{
	public List<ManagedModFile> Files { get; } = [];

	public HashSet<string> SuccessfulModIds { get; } = new(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);

	public int IgnoredSkipped { get; set; }
}

internal static class ManagedModStore
{
	private const long MaxIndexBytes = 2 * 1024 * 1024;
	private const int MaxFilesPerMod = 100000;
	private static readonly object Sync = new();
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNameCaseInsensitive = true
	};

	public static IReadOnlyList<ManagedModRecord> Load()
	{
		lock (Sync)
		{
			return LoadCore().Select(Clone).ToArray();
		}
	}

	public static ManagedModRecord Create(string name)
	{
		lock (Sync)
		{
			List<ManagedModRecord> records = LoadCore();
			ManagedModRecord record = new()
			{
				Id = Guid.NewGuid().ToString("N"),
				Name = NormalizeName(name),
				Enabled = true,
				CreatedUtc = DateTime.UtcNow
			};
			Directory.CreateDirectory(GetFolderCore(record.Id));
			records.Insert(0, record);
			SaveCore(records);
			return Clone(record);
		}
	}

	public static void Rename(string id, string name)
	{
		Mutate(id, record => record.Name = NormalizeName(name));
	}

	public static void SetEnabled(string id, bool enabled)
	{
		Mutate(id, record => record.Enabled = enabled);
	}

	public static void MoveRelative(string id, string targetId, bool insertAfter)
	{
		lock (Sync)
		{
			List<ManagedModRecord> records = LoadCore();
			int sourceIndex = records.FindIndex(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));
			int targetIndex = records.FindIndex(record => string.Equals(record.Id, targetId, StringComparison.OrdinalIgnoreCase));
			if (sourceIndex < 0 || targetIndex < 0)
				throw new InvalidOperationException("The selected mod no longer exists.");
			if (sourceIndex == targetIndex)
				return;
			ManagedModRecord record = records[sourceIndex];
			records.RemoveAt(sourceIndex);
			targetIndex = records.FindIndex(item => string.Equals(item.Id, targetId, StringComparison.OrdinalIgnoreCase));
			int insertionIndex = insertAfter ? targetIndex + 1 : targetIndex;
			records.Insert(Math.Clamp(insertionIndex, 0, records.Count), record);
			SaveCore(records);
		}
	}

	public static void Delete(string id)
	{
		if (IsValidId(id))
		{
			ExternalModConfigs.RemoveConfigs(id);
		}
		bool cleanup = false;
		lock (Sync)
		{
			List<ManagedModRecord> records = LoadCore();
			int removed = records.RemoveAll(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));
			if (removed == 0)
				return;
			string folder = GetFolderCore(id);
			if (Directory.Exists(folder))
			{
				MoveToTrashOrDelete(folder);
				cleanup = true;
			}
			string sources = Path.Combine(Paths.ManagedMods, "Sources", Path.GetFileName(folder));
			if (Directory.Exists(sources))
			{
				MoveToTrashOrDelete(sources);
				cleanup = true;
			}
			SaveCore(records);
		}
		if (cleanup)
		{
			_ = Task.Run(CleanupTrash);
		}
	}

	public static void Discard(string path)
	{
		if (!Directory.Exists(path))
			return;
		MoveToTrashOrDelete(path);
		_ = Task.Run(CleanupTrash);
	}

	private static void MoveToTrashOrDelete(string path)
	{
		string trash = Path.Combine(Paths.ManagedMods, "Trash");
		Directory.CreateDirectory(trash);
		string destination = Path.Combine(trash, Guid.NewGuid().ToString("N"));
		try
		{
			Directory.Move(path, destination);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			DeleteDirectory(path);
		}
	}

	private static void CleanupTrash()
	{
		string trash = Path.Combine(Paths.ManagedMods, "Trash");
		try
		{
			if (!Directory.Exists(trash))
				return;
			foreach (string folder in Directory.EnumerateDirectories(trash))
			{
				try
				{
					DeleteDirectory(folder);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}
			if (!Directory.EnumerateFileSystemEntries(trash).Any())
			{
				Directory.Delete(trash, false);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static void DeleteDirectory(string path)
	{
		FileAttributes attributes = File.GetAttributes(path);
		bool recursive = (attributes & FileAttributes.ReparsePoint) == 0;
		try
		{
			Directory.Delete(path, recursive);
		}
		catch (UnauthorizedAccessException) when (recursive)
		{
			ClearReadOnly(path);
			Directory.Delete(path, recursive);
		}
	}

	internal static void ClearReadOnly(string path)
	{
		EnumerationOptions options = new EnumerationOptions
		{
			RecurseSubdirectories = true,
			IgnoreInaccessible = true,
			AttributesToSkip = FileAttributes.ReparsePoint
		};
		foreach (string file in Directory.EnumerateFiles(path, "*", options))
		{
			ClearReadOnlyFile(file);
		}
	}

	internal static void ClearReadOnlyFile(string file)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(file);
			if ((attributes & FileAttributes.ReadOnly) != 0)
			{
				File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	public static string GetFolder(string id)
	{
		if (!IsValidId(id))
			throw new InvalidDataException("The mod identifier is invalid.");
		return GetFolderCore(id);
	}

	public static IReadOnlyList<ManagedModLibraryEntry> ScanLibrary()
	{
		lock (Sync)
		{
			List<ManagedModLibraryEntry> entries = [];
			HashSet<string> onTop = Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.FeatureModIds();
			foreach (ManagedModRecord record in LoadCore())
			{
				ManagedModLibraryEntry entry = new(Clone(record)) { AppliedOnTop = onTop.Contains(record.Id) };
				string folder = GetFolderCore(record.Id);
				entry.Pack = ModPackInfo.Read(folder);
				try
				{
					foreach (string file in EnumeratePackageFiles(folder))
					{
						FileInfo info = new(file);
						entry.FileCount++;
						entry.TotalBytes = entry.TotalBytes > long.MaxValue - info.Length ? long.MaxValue : entry.TotalBytes + info.Length;
						string relative = Path.GetRelativePath(folder, file);
						if (IsSafeRelativePath(relative) && !ModAutoFixer.IsIgnoredModFile(relative))
							entry.RelativePaths.Add(relative);
					}
				}
				catch (Exception ex)
				{
					entry.Failure = ex.Message;
				}
				entries.Add(entry);
			}
			return entries;
		}
	}

	public static IReadOnlyList<string> EnabledFoldersByPriority()
	{
		lock (Sync)
		{
			HashSet<string> onTop = Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.FeatureModIds();
			return [.. LoadCore().Where(record => record.Enabled).OrderByDescending(record => onTop.Contains(record.Id)).Select(record => GetFolderCore(record.Id))];
		}
	}

	public static ManagedModScanResult ScanEnabledFiles()
	{
		lock (Sync)
		{
			ManagedModScanResult result = new();
			HashSet<string> claimed = new(StringComparer.OrdinalIgnoreCase);
			HashSet<string> onTop = Voidstrap.Integrations.ClassicTopBar.ClassicTopBarMod.FeatureModIds();
			foreach (ManagedModRecord record in LoadCore().Where(record => record.Enabled).OrderByDescending(record => onTop.Contains(record.Id)))
			{
				string folder = GetFolderCore(record.Id);
				List<ManagedModFile> packageFiles = [];
				try
				{
					foreach (string file in EnumeratePackageFiles(folder))
					{
						string relative = Path.GetRelativePath(folder, file);
						if (!IsSafeRelativePath(relative))
							continue;
						packageFiles.Add(new ManagedModFile(Clone(record), file, relative));
					}
					foreach (ManagedModFile file in packageFiles)
					{
						if (ModAutoFixer.IsIgnoredModFile(file.Relative))
							result.IgnoredSkipped++;
						else if (claimed.Add(file.Relative))
							result.Files.Add(file);
					}
					result.SuccessfulModIds.Add(record.Id);
				}
				catch (Exception ex)
				{
					result.Failures[record.Id] = ex.Message;
				}
			}
			return result;
		}
	}

	private static void Mutate(string id, Action<ManagedModRecord> mutation)
	{
		lock (Sync)
		{
			List<ManagedModRecord> records = LoadCore();
			ManagedModRecord? record = records.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
			if (record is null)
				throw new InvalidOperationException("The selected mod no longer exists.");
			mutation(record);
			SaveCore(records);
		}
	}

	private static List<ManagedModRecord> LoadCore()
	{
		Directory.CreateDirectory(Paths.ManagedModPackages);
		List<ManagedModRecord> records = ReadIndex();
		bool changed = false;
		HashSet<string> indexed = new(records.Select(record => record.Id), StringComparer.OrdinalIgnoreCase);
		foreach (string folder in Directory.EnumerateDirectories(Paths.ManagedModPackages))
		{
			string folderName = Path.GetFileName(folder);
			if (IsValidId(folderName))
			{
				if (indexed.Add(folderName))
				{
					records.Add(new ManagedModRecord
					{
						Id = folderName.ToLowerInvariant(),
						Name = "Mod " + folderName[..8],
						Enabled = true,
						CreatedUtc = Directory.GetCreationTimeUtc(folder)
					});
					changed = true;
				}
				continue;
			}
			string id = Guid.NewGuid().ToString("N");
			string destination = GetFolderCore(id);
			Directory.Move(folder, destination);
			records.Add(new ManagedModRecord
			{
				Id = id,
				Name = NormalizeName(folderName, "Mod " + id[..8]),
				Enabled = true,
				CreatedUtc = DateTime.UtcNow
			});
			indexed.Add(id);
			changed = true;
		}
		int removed = records.RemoveAll(record => !Directory.Exists(GetFolderCore(record.Id)));
		changed |= removed > 0;
		if (changed || !File.Exists(Paths.ManagedModIndex))
			SaveCore(records);
		return records;
	}

	private static List<ManagedModRecord> ReadIndex()
	{
		if (!File.Exists(Paths.ManagedModIndex))
			return [];
		try
		{
			FileInfo info = new(Paths.ManagedModIndex);
			if (info.Length <= 0 || info.Length > MaxIndexBytes)
				return [];
			using FileStream stream = new(Paths.ManagedModIndex, FileMode.Open, FileAccess.Read, FileShare.Read);
			ManagedModIndex? index = JsonSerializer.Deserialize<ManagedModIndex>(stream, JsonOptions);
			List<ManagedModRecord> records = [];
			HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
			foreach (ManagedModRecord record in index?.Mods ?? [])
			{
				if (!IsValidId(record.Id) || !ids.Add(record.Id))
					continue;
				record.Id = record.Id.ToLowerInvariant();
				record.Name = NormalizeName(record.Name, "Mod " + record.Id[..8]);
				if (record.CreatedUtc == default)
					record.CreatedUtc = DateTime.UtcNow;
				records.Add(record);
			}
			return records;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ManagedModStore::ReadIndex", "Could not read the managed mod index: " + ex.Message);
			return [];
		}
	}

	private static void SaveCore(List<ManagedModRecord> records)
	{
		Directory.CreateDirectory(Paths.ManagedMods);
		string temporary = Paths.ManagedModIndex + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
				JsonSerializer.Serialize(stream, new ManagedModIndex { Mods = records }, JsonOptions);
			File.Move(temporary, Paths.ManagedModIndex, true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}

	private static IEnumerable<string> EnumeratePackageFiles(string root)
	{
		if (!Directory.Exists(root))
			yield break;
		if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
			yield break;
		Stack<string> pending = new();
		pending.Push(root);
		int count = 0;
		while (pending.Count > 0)
		{
			string directory = pending.Pop();
			foreach (string child in Directory.EnumerateDirectories(directory))
			{
				FileAttributes attributes = File.GetAttributes(child);
				if ((attributes & FileAttributes.ReparsePoint) == 0)
					pending.Push(child);
			}
			foreach (string file in Directory.EnumerateFiles(directory))
			{
				FileAttributes attributes = File.GetAttributes(file);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
					continue;
				count++;
				if (count > MaxFilesPerMod)
					throw new InvalidDataException("A managed mod contains too many files.");
				yield return file;
			}
		}
	}

	private static bool IsSafeRelativePath(string relative)
	{
		if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
			return false;
		return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is ".." or "." or "");
	}

	private static bool IsValidId(string id)
	{
		return id.Length == 32 && Guid.TryParseExact(id, "N", out _);
	}

	private static string GetFolderCore(string id)
	{
		if (!IsValidId(id))
			throw new InvalidDataException("The mod identifier is invalid.");
		return Path.Combine(Paths.ManagedModPackages, id.ToLowerInvariant());
	}

	private static string NormalizeName(string name, string? fallback = null)
	{
		string normalized = string.Join(" ", (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		normalized = new string(normalized.Where(character => !char.IsControl(character)).Take(80).ToArray()).Trim();
		if (normalized.Length == 0)
			normalized = fallback ?? throw new ArgumentException("Enter a name for the mod.", nameof(name));
		return normalized;
	}

	private static ManagedModRecord Clone(ManagedModRecord record)
	{
		return new ManagedModRecord
		{
			Id = record.Id,
			Name = record.Name,
			Enabled = record.Enabled,
			CreatedUtc = record.CreatedUtc
		};
	}
}
