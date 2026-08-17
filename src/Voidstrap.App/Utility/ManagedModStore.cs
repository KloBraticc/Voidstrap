using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

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

internal readonly record struct ManagedModStatistics(int FileCount, long TotalBytes);

internal sealed class ManagedModScanResult
{
	public List<ManagedModFile> Files { get; } = [];

	public HashSet<string> SuccessfulModIds { get; } = new(StringComparer.OrdinalIgnoreCase);

	public Dictionary<string, string> Failures { get; } = new(StringComparer.OrdinalIgnoreCase);
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
			records.Add(record);
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
		lock (Sync)
		{
			List<ManagedModRecord> records = LoadCore();
			int removed = records.RemoveAll(record => string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase));
			if (removed == 0)
				return;
			string folder = GetFolderCore(id);
			if (Directory.Exists(folder))
			{
				FileAttributes attributes = File.GetAttributes(folder);
				Directory.Delete(folder, (attributes & FileAttributes.ReparsePoint) == 0);
			}
			SaveCore(records);
		}
	}

	public static string GetFolder(string id)
	{
		if (!IsValidId(id))
			throw new InvalidDataException("The mod identifier is invalid.");
		return GetFolderCore(id);
	}

	public static ManagedModStatistics GetStatistics(string id)
	{
		string folder = GetFolder(id);
		int count = 0;
		long total = 0;
		foreach (string file in EnumeratePackageFiles(folder))
		{
			FileInfo info = new(file);
			count++;
			total = total > long.MaxValue - info.Length ? long.MaxValue : total + info.Length;
		}
		return new ManagedModStatistics(count, total);
	}

	public static ManagedModScanResult ScanEnabledFiles()
	{
		lock (Sync)
		{
			ManagedModScanResult result = new();
			foreach (ManagedModRecord record in LoadCore().Where(record => record.Enabled))
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
					result.Files.AddRange(packageFiles);
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
