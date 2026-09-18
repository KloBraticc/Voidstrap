using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Voidstrap.Utility;

internal readonly record struct AssetCacheEntry(string Hash, string SourcePath);

internal static class RobloxAssetCache
{
	private const string LogIdent = "RobloxAssetCache";

	public const int HeaderBytes = 37;

	public const int ContentLengthOffset = 25;

	public const long MaxEntryBytes = 64L * 1024 * 1024;

	public const string BackupFolderName = "AssetCacheBackup";

	public const string ManifestName = "AssetCache.lock";

	public static string Root => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"Roblox",
		"rbx-storage");

	public static bool IsValidHash(string? value)
	{
		if (string.IsNullOrEmpty(value) || value.Length != 32)
		{
			return false;
		}
		foreach (char character in value)
		{
			bool digit = character >= '0' && character <= '9';
			bool lower = character >= 'a' && character <= 'f';
			if (!digit && !lower)
			{
				return false;
			}
		}
		return true;
	}

	public static string? ExtractHash(string fileName)
	{
		string name = Path.GetFileNameWithoutExtension(fileName);
		for (int start = name.Length - 32; start >= 0; start--)
		{
			string candidate = name.Substring(start, 32).ToLowerInvariant();
			if (IsValidHash(candidate))
			{
				return candidate;
			}
		}
		return null;
	}

	public static bool IsCacheEntry(byte[] header, long totalLength)
	{
		if (header.Length < HeaderBytes || totalLength <= HeaderBytes || totalLength > MaxEntryBytes)
		{
			return false;
		}
		if (header[0] != (byte)'R' || header[1] != (byte)'B' || header[2] != (byte)'X' || header[3] != (byte)'H')
		{
			return false;
		}
		uint declared = BitConverter.ToUInt32(header, ContentLengthOffset);
		return declared == totalLength - HeaderBytes;
	}

	public static bool IsCacheEntryFile(string path)
	{
		try
		{
			FileInfo info = new(path);
			if (info.Length <= HeaderBytes || info.Length > MaxEntryBytes)
			{
				return false;
			}
			byte[] header = new byte[HeaderBytes];
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			return stream.Read(header, 0, HeaderBytes) == HeaderBytes && IsCacheEntry(header, info.Length);
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static string GetEntryPath(string hash)
	{
		if (!IsValidHash(hash))
		{
			throw new InvalidDataException("The asset cache identifier is invalid.");
		}
		return Path.Combine(Root, hash[..2], hash);
	}

	public static IReadOnlyList<AssetCacheEntry> Collect(string folder, CancellationToken token = default)
	{
		List<AssetCacheEntry> entries = [];
		if (!Directory.Exists(folder))
		{
			return entries;
		}
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
		{
			token.ThrowIfCancellationRequested();
			string relative = Path.GetRelativePath(folder, file);
			if (relative.Contains("RESTORE", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string? hash = ExtractHash(Path.GetFileName(file));
			if (hash is null || !IsCacheEntryFile(file) || !seen.Add(hash))
			{
				continue;
			}
			entries.Add(new AssetCacheEntry(hash, file));
		}
		return entries;
	}

	public static int Install(IReadOnlyList<AssetCacheEntry> entries, string backupFolder, CancellationToken token = default)
	{
		if (entries.Count == 0)
		{
			return 0;
		}
		Directory.CreateDirectory(backupFolder);
		int installed = 0;
		List<string> manifest = [];
		foreach (AssetCacheEntry entry in entries)
		{
			token.ThrowIfCancellationRequested();
			try
			{
				string destination = GetEntryPath(entry.Hash);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				string backup = Path.Combine(backupFolder, entry.Hash + ".lock");
				if (File.Exists(destination) && !File.Exists(backup))
				{
					ClearReadOnly(destination);
					File.Copy(destination, backup, overwrite: true);
				}
				File.Copy(entry.SourcePath, Path.Combine(backupFolder, entry.Hash + ".mod.lock"), overwrite: true);
				ClearReadOnly(destination);
				File.Copy(entry.SourcePath, destination, overwrite: true);
				SetReadOnly(destination);
				token.ThrowIfCancellationRequested();
				manifest.Add(entry.Hash);
				installed++;
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger?.WriteLine(LogIdent, "Could not replace cached asset " + entry.Hash + ": " + ex.Message);
			}
		}
		if (manifest.Count > 0)
		{
			try
			{
				File.WriteAllLines(Path.Combine(backupFolder, ManifestName), manifest);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "The asset cache manifest could not be written: " + ex.Message);
			}
		}
		App.Logger?.WriteLine(LogIdent, "Replaced " + installed + " cached Roblox assets and locked them against redownload");
		return installed;
	}

	public static int Restore(string backupFolder)
	{
		string manifestPath = Path.Combine(backupFolder, ManifestName);
		if (!Directory.Exists(backupFolder))
		{
			return 0;
		}
		IEnumerable<string> hashes = File.Exists(manifestPath)
			? ReadManifest(manifestPath)
			: Directory.EnumerateFiles(backupFolder, "*.lock")
				.Select(file => Path.GetFileName(file))
				.Select(name => name.EndsWith(".mod.lock", StringComparison.OrdinalIgnoreCase)
					? name[..^9]
					: Path.GetFileNameWithoutExtension(name))
				.Where(IsValidHash)
				.Distinct(StringComparer.OrdinalIgnoreCase);
		int restored = 0;
		foreach (string hash in hashes)
		{
			try
			{
				string destination = GetEntryPath(hash);
				string backup = Path.Combine(backupFolder, hash + ".lock");
				ClearReadOnly(destination);
				if (File.Exists(backup))
				{
					File.Copy(backup, destination, overwrite: true);
				}
				else if (File.Exists(destination))
				{
					File.Delete(destination);
				}
				restored++;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Could not restore cached asset " + hash + ": " + ex.Message);
			}
		}
		App.Logger?.WriteLine(LogIdent, "Restored " + restored + " cached Roblox assets");
		return restored;
	}

	public static int Reapply(string backupFolder)
	{
		string manifestPath = Path.Combine(backupFolder, ManifestName);
		if (!File.Exists(manifestPath))
		{
			return 0;
		}
		int applied = 0;
		foreach (string hash in ReadManifest(manifestPath))
		{
			try
			{
				string modded = Path.Combine(backupFolder, hash + ".mod.lock");
				if (!File.Exists(modded))
				{
					continue;
				}
				string destination = GetEntryPath(hash);
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				ClearReadOnly(destination);
				File.Copy(modded, destination, overwrite: true);
				SetReadOnly(destination);
				applied++;
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "Could not reapply cached asset " + hash + ": " + ex.Message);
			}
		}
		App.Logger?.WriteLine(LogIdent, "Reapplied " + applied + " cached Roblox assets");
		return applied;
	}

	public static bool HasBackups(string backupFolder)
	{
		return File.Exists(Path.Combine(backupFolder, ManifestName));
	}

	private static IEnumerable<string> ReadManifest(string manifestPath)
	{
		string[] lines;
		try
		{
			lines = File.ReadAllLines(manifestPath);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The asset cache manifest could not be read: " + ex.Message);
			return Array.Empty<string>();
		}
		return lines.Select(line => line.Trim()).Where(IsValidHash);
	}

	private static void SetReadOnly(string path)
	{
		try
		{
			File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Could not lock " + Path.GetFileName(path) + ": " + ex.Message);
		}
	}

	private static void ClearReadOnly(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
			}
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "Could not unlock " + Path.GetFileName(path) + ": " + ex.Message);
		}
	}
}
