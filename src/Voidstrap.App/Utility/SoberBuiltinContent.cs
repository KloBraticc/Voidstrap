using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Voidstrap.Utility;

internal static class SoberBuiltinContent
{
	private const string LogIdent = "SoberBuiltinContent";

	private const string ConfigName = "Voidstrap Sober Builtin Content";

	private const int CacheVersion = 1;

	private const string PathPrefix = "rbxasset://";

	private const string UrlPrefix = "https://assetdelivery.roblox.com/v1/asset/?id=";

	private const int MaximumPairDistance = 64;

	private const int MinimumPairs = 50;

	private static readonly string[] SiblingExtensions = [".ogg", ".mp3", ".wav", ".flac", ".png", ".jpg", ".jpeg", ".tga", ".dds", ".ktx2"];

	private static string ConfigPath => Path.Combine(Paths.AssetProxy, "Configs", ConfigName + ".json");

	private static string AssetFolder => Path.Combine(Paths.AssetProxy, "Configs", "Assets", ConfigName);

	private static string SignaturePath => Path.Combine(AssetFolder, "source.sig");

	private static string CacheResetPath => Path.Combine(Paths.AssetProxy, "SoberBuiltinContent.cache-reset");

	private static string CacheRepairPath => Path.Combine(Paths.AssetProxy, "SoberBuiltinContent.cache-version");

	internal static void ApplyFromSettings()
	{
		if (!Platform.IsLinux)
			return;
		if (App.Settings.Prop.ModApplyTarget is Voidstrap.Enums.ModApplyTarget.Both or Voidstrap.Enums.ModApplyTarget.Player)
			Apply([.. ManagedModStore.EnabledFoldersByPriority(), Paths.Mods]);
		else
			RemoveGenerated();
	}

	internal static void RequestCacheReset()
	{
		if (!Platform.IsLinux)
			return;
		Directory.CreateDirectory(Paths.AssetProxy);
		File.WriteAllText(CacheResetPath, "1");
	}

	internal static void PrepareLaunchCache()
	{
		if (!Platform.IsLinux)
			return;
		bool repairRequired = File.Exists(CachePath) && !File.Exists(CacheRepairPath);
		if (!repairRequired && !File.Exists(CacheResetPath))
			return;
		RequestCacheReset();
		if (!Voidstrap.Integrations.AssetProxy.AssetProxyRouting.ClearRobloxCache())
			throw new IOException("The cached Sober sound replacements could not be reset. Close Sober and try launching again.");
		Voidstrap.Integrations.AssetProxy.TextureStripper.InvalidateRuntimeState();
		Voidstrap.Integrations.AssetProxy.AssetProxyRouting.InvalidateCache();
		File.WriteAllText(CacheRepairPath, "1");
		File.Delete(CacheResetPath);
	}

	private static string CachePath => Path.Combine(Paths.Cache, "SoberBuiltinContent.json");

	private sealed class CacheFile
	{
		public int Version { get; set; }

		public string Key { get; set; } = "";

		public Dictionary<string, long> Map { get; set; } = [];
	}

	public static bool HasRedirects()
	{
		try
		{
			return Platform.IsLinux && File.Exists(ConfigPath);
		}
		catch (Exception)
		{
			return false;
		}
	}

	internal static bool HasUserRedirects(IReadOnlyList<string> modFoldersByPriority)
	{
		if (!HasRedirects())
			return false;
		string? package = LegacyMaterialTextures.FindSoberNativePackage();
		if (package == null)
			return false;
		Dictionary<string, long> map = LoadMap(package);
		Dictionary<string, string> sources = CollectContentFiles(modFoldersByPriority)
			.ToDictionary(file => file.Relative, file => file.Source, StringComparer.OrdinalIgnoreCase);
		return Voidstrap.Integrations.AssetProxy.TextureStripper.HasVerifiedReplacement(ConfigPath, (rule, local) =>
		{
			if (local == null || !Path.GetFullPath(local).StartsWith(Path.GetFullPath(AssetFolder) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
				|| !rule.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
				return false;
			string name = nameElement.GetString()!;
			int separator = name.LastIndexOf(" for ", StringComparison.Ordinal);
			if (separator <= 0)
				return false;
			string relative = name[..separator], key = name[(separator + 5)..];
			if (!sources.TryGetValue(relative, out string? source) || VoidstrapDefaultCursor.IsImplicitDefaultFile(relative, source)
				|| !map.TryGetValue(key, out long mappedId) || mappedId <= 0)
				return false;
			string expected = relative["content/".Length..];
			if (!string.Equals(key, expected, StringComparison.OrdinalIgnoreCase)
				&& (!SiblingExtensions.Contains(Path.GetExtension(expected), StringComparer.OrdinalIgnoreCase)
					|| !string.Equals(StripExtension(key), StripExtension(expected), StringComparison.OrdinalIgnoreCase)
					|| map.Keys.Count(candidate => string.Equals(StripExtension(candidate), StripExtension(expected), StringComparison.OrdinalIgnoreCase)) != 1))
				return false;
			JsonElement ids = rule.GetProperty("replace_ids");
			if (ids.GetArrayLength() != 1 || !ids[0].TryGetInt64(out long configuredId) || configuredId != mappedId)
				return false;
			FileInfo original = new(source), replacement = new(local);
			if (original.Length != replacement.Length)
				return false;
			using FileStream originalStream = File.OpenRead(source), replacementStream = File.OpenRead(local);
			return System.Security.Cryptography.SHA256.HashData(originalStream).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(replacementStream));
		});
	}

	public static void RemoveGenerated()
	{
		try
		{
			bool changed = File.Exists(ConfigPath) || Directory.Exists(AssetFolder);
			if (changed)
				RequestCacheReset();
			if (File.Exists(ConfigPath))
			{
				File.Delete(ConfigPath);
			}
			if (Directory.Exists(AssetFolder))
			{
				Directory.Delete(AssetFolder, recursive: true);
			}
			if (changed)
				Voidstrap.Integrations.AssetProxy.TextureStripper.InvalidateRuntimeState();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The previous Sober content redirects could not be removed: " + ex.Message);
			throw;
		}
	}

	public static void Apply(IReadOnlyList<string> modFoldersByPriority)
	{
		if (!Platform.IsLinux)
		{
			return;
		}

		string? package = LegacyMaterialTextures.FindSoberNativePackage();
		if (package == null)
		{
			RemoveGenerated();
			return;
		}

		List<(string Relative, string Source)> files = CollectContentFiles(modFoldersByPriority);
		if (files.Count == 0)
		{
			RemoveGenerated();
			return;
		}

		Dictionary<string, long> map = LoadMap(package);
		if (map.Count == 0)
		{
			RemoveGenerated();
			return;
		}

		Dictionary<string, List<string>> siblings = new(StringComparer.OrdinalIgnoreCase);
		foreach (string key in map.Keys)
		{
			string stem = StripExtension(key);
			if (!siblings.TryGetValue(stem, out List<string>? list))
			{
				siblings[stem] = list = [];
			}
			list.Add(key);
		}

		List<(string Relative, string Source, string Key, long Id)> matches = [];
		foreach ((string relative, string source) in files)
		{
			string key = relative["content/".Length..];
			if (!map.TryGetValue(key, out long id))
			{
				if (!SiblingExtensions.Contains(Path.GetExtension(key), StringComparer.OrdinalIgnoreCase)
					|| !siblings.TryGetValue(StripExtension(key), out List<string>? candidates)
					|| candidates.Count != 1)
				{
					continue;
				}
				key = candidates[0];
				id = map[key];
			}
			matches.Add((relative, source, key, id));
		}

		if (matches.Count == 0)
		{
			RemoveGenerated();
			return;
		}

		string signature = BuildSignature(package, matches);
		if (File.Exists(ConfigPath) && File.Exists(SignaturePath) && string.Equals(File.ReadAllText(SignaturePath), signature, StringComparison.Ordinal))
		{
			LogState(matches.Count, matches.Select(match => match.Relative));
			return;
		}

		RequestCacheReset();
		string staging = AssetFolder + ".new";
		if (Directory.Exists(staging))
		{
			Directory.Delete(staging, recursive: true);
		}
		Directory.CreateDirectory(staging);

		JsonArray rules = [];
		int index = 0;
		foreach ((string relative, string source, string key, long id) in matches)
		{
			string file = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + "_" + Path.GetFileName(relative);
			File.Copy(source, Path.Combine(staging, file), overwrite: true);
			rules.Add(new JsonObject
			{
				["name"] = relative + " for " + key,
				["replace_ids"] = new JsonArray(id),
				["mode"] = "local",
				["enabled"] = true,
				["local_path"] = "Assets/" + ConfigName + "/" + file
			});
			index++;
		}

		File.WriteAllText(Path.Combine(staging, "source.sig"), signature);
		if (Directory.Exists(AssetFolder))
		{
			Directory.Delete(AssetFolder, recursive: true);
		}
		Directory.CreateDirectory(Path.GetDirectoryName(AssetFolder)!);
		Directory.Move(staging, AssetFolder);
		JsonObject config = new() { ["replacement_rules"] = rules };
		string temporary = ConfigPath + ".tmp";
		File.WriteAllText(temporary, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		File.Move(temporary, ConfigPath, overwrite: true);
		Voidstrap.Integrations.AssetProxy.TextureStripper.InvalidateRuntimeState();
		LogState(matches.Count, matches.Select(match => match.Relative));
	}

	private static void LogState(int count, IEnumerable<string> relatives)
	{
		App.Logger?.WriteLine(LogIdent, count + " mod files replace content Sober downloads instead of reading from its package, they apply through AssetWarp: " + string.Join(", ", relatives.Take(12)));
		if (!App.Settings.Prop.AssetWarpEnabled)
		{
			App.Logger?.WriteLine(LogIdent, "AssetWarp is off, so these mods cannot reach Roblox until it is turned on");
		}
	}

	private static List<(string Relative, string Source)> CollectContentFiles(IReadOnlyList<string> modFoldersByPriority)
	{
		Dictionary<string, (string Relative, string Source)> found = new(StringComparer.OrdinalIgnoreCase);
		foreach (string folder in modFoldersByPriority)
		{
			string root = Path.Combine(folder, "content");
			if (!Directory.Exists(root))
			{
				continue;
			}
			try
			{
				foreach (string source in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
				{
					string relative = Path.GetRelativePath(folder, source).Replace('\\', '/');
					if (ModAutoFixer.IsIgnoredModFile(relative) || found.ContainsKey(relative))
					{
						continue;
					}
					FileInfo info = new(source);
					if (info.LinkTarget != null || info.Length == 0)
					{
						continue;
					}
					found[relative] = (relative, source);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "A mod folder could not be read: " + ex.Message);
			}
		}
		return [.. found.Values.OrderBy(entry => entry.Relative, StringComparer.Ordinal)];
	}

	private static string StripExtension(string key)
	{
		string extension = Path.GetExtension(key);
		return extension.Length == 0 ? key : key[..^extension.Length];
	}

	private static string BuildSignature(string package, List<(string Relative, string Source, string Key, long Id)> matches)
	{
		StringBuilder builder = new();
		builder.Append(CacheVersion).Append('\n').Append(BuildPackageKey(package)).Append('\n');
		foreach ((string relative, string source, string key, long id) in matches)
		{
			FileInfo info = new(source);
			builder.Append(relative).Append('|').Append(source).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(key).Append('|').Append(id).Append('\n');
		}
		return builder.ToString();
	}

	private static string BuildPackageKey(string package)
	{
		FileInfo info = new(package);
		return package + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
	}

	private static Dictionary<string, long> LoadMap(string package)
	{
		string key = BuildPackageKey(package);
		try
		{
			if (File.Exists(CachePath))
			{
				CacheFile? cached = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
				if (cached != null && cached.Version == CacheVersion && string.Equals(cached.Key, key, StringComparison.Ordinal))
				{
					return new Dictionary<string, long>(cached.Map, StringComparer.OrdinalIgnoreCase);
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			App.Logger?.WriteLine(LogIdent, "The cached Sober content map could not be read: " + ex.Message);
		}

		Dictionary<string, long> map;
		try
		{
			map = ReadMap(LegacyMaterialTextures.ReadClientBinary(package));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			App.Logger?.WriteLine(LogIdent, "The Sober Roblox client library could not be read: " + ex.Message);
			return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		}

		if (map.Count < MinimumPairs)
		{
			App.Logger?.WriteLine(LogIdent, "This Sober Roblox version has no downloadable content table Voidstrap understands, found " + map.Count + " entries");
			return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		}

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
			File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheFile { Version = CacheVersion, Key = key, Map = map }));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The Sober content map could not be cached: " + ex.Message);
		}
		App.Logger?.WriteLine(LogIdent, "Read " + map.Count + " downloadable content entries from the Sober Roblox client");
		return map;
	}

	internal static Dictionary<string, long> ReadMap(byte[] binary)
	{
		Dictionary<string, long> map = new(StringComparer.OrdinalIgnoreCase);
		ReadOnlySpan<byte> data = binary;
		if (data.Length < 64 || data[0] != 0x7F || data[1] != (byte)'E' || data[2] != (byte)'L' || data[3] != (byte)'F' || data[4] != 2 || data[5] != 1)
		{
			return map;
		}
		if (BinaryPrimitives.ReadUInt16LittleEndian(data[18..]) != 62)
		{
			return map;
		}

		long sectionOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(data[0x28..]);
		int sectionSize = BinaryPrimitives.ReadUInt16LittleEndian(data[0x3A..]);
		int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(data[0x3C..]);
		int namesIndex = BinaryPrimitives.ReadUInt16LittleEndian(data[0x3E..]);
		if (sectionSize < 64 || namesIndex >= sectionCount || sectionOffset + (long)sectionSize * sectionCount > data.Length)
		{
			return map;
		}

		(ulong Address, long Offset, long Size) ReadSection(int index)
		{
			ReadOnlySpan<byte> header = binary.AsSpan((int)(sectionOffset + (long)index * sectionSize), 64);
			return (BinaryPrimitives.ReadUInt64LittleEndian(header[16..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(header[24..]), (long)BinaryPrimitives.ReadUInt64LittleEndian(header[32..]));
		}

		(ulong _, long namesOffset, long namesSize) = ReadSection(namesIndex);
		(ulong Address, long Offset, long Size)? rodata = null;
		(ulong Address, long Offset, long Size)? text = null;
		for (int index = 0; index < sectionCount; index++)
		{
			int nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(binary.AsSpan((int)(sectionOffset + (long)index * sectionSize), 4));
			if (nameOffset < 0 || nameOffset >= namesSize)
			{
				continue;
			}
			ReadOnlySpan<byte> name = data.Slice((int)(namesOffset + nameOffset));
			int end = name.IndexOf((byte)0);
			string sectionName = end < 0 ? "" : Encoding.ASCII.GetString(name[..end]);
			if (sectionName == ".rodata")
			{
				rodata = ReadSection(index);
			}
			else if (sectionName == ".text")
			{
				text = ReadSection(index);
			}
		}
		if (rodata is not { } constants || text is not { } code || constants.Offset + constants.Size > data.Length || code.Offset + code.Size > data.Length)
		{
			return map;
		}

		Dictionary<ulong, string> strings = [];
		ReadOnlySpan<byte> constantBytes = data.Slice((int)constants.Offset, (int)constants.Size);
		CollectStrings(constantBytes, constants.Address, Encoding.ASCII.GetBytes(PathPrefix), strings, digitsOnly: false);
		CollectStrings(constantBytes, constants.Address, Encoding.ASCII.GetBytes(UrlPrefix), strings, digitsOnly: true);
		if (strings.Count == 0)
		{
			return map;
		}

		ReadOnlySpan<byte> codeBytes = data.Slice((int)code.Offset, (int)code.Size);
		long previousPosition = long.MinValue;
		string? previousPath = null;
		for (int position = 0; position + 7 <= codeBytes.Length; position++)
		{
			byte prefix = codeBytes[position];
			if ((prefix != 0x48 && prefix != 0x4C) || codeBytes[position + 1] != 0x8D || (codeBytes[position + 2] & 0xC7) != 0x05)
			{
				continue;
			}
			int displacement = BinaryPrimitives.ReadInt32LittleEndian(codeBytes[(position + 3)..]);
			ulong target = (ulong)((long)code.Address + position + 7 + displacement);
			if (!strings.TryGetValue(target, out string? value))
			{
				continue;
			}
			if (value.StartsWith(UrlPrefix, StringComparison.Ordinal))
			{
				if (previousPath != null && position - previousPosition < MaximumPairDistance && long.TryParse(value.AsSpan(UrlPrefix.Length), out long id))
				{
					map.TryAdd(previousPath, id);
				}
				previousPath = null;
			}
			else
			{
				previousPath = value[PathPrefix.Length..];
			}
			previousPosition = position;
		}
		return map;
	}

	private static void CollectStrings(ReadOnlySpan<byte> section, ulong address, byte[] prefix, Dictionary<ulong, string> strings, bool digitsOnly)
	{
		int start = 0;
		while (start < section.Length)
		{
			int found = section[start..].IndexOf(prefix);
			if (found < 0)
			{
				return;
			}
			int at = start + found;
			start = at + prefix.Length;
			if (at > 0 && section[at - 1] != 0)
			{
				continue;
			}
			int end = section[at..].IndexOf((byte)0);
			if (end <= prefix.Length || end > 512)
			{
				continue;
			}
			ReadOnlySpan<byte> value = section.Slice(at, end);
			bool valid = true;
			for (int index = prefix.Length; index < value.Length; index++)
			{
				byte character = value[index];
				if (digitsOnly ? character is < (byte)'0' or > (byte)'9' : character is < 0x20 or > 0x7E)
				{
					valid = false;
					break;
				}
			}
			if (valid)
			{
				strings[address + (ulong)at] = Encoding.ASCII.GetString(value);
			}
		}
	}
}
