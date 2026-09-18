using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Voidstrap.Integrations.CommunityMods;

namespace Voidstrap.Utility;

internal sealed class ModCrashGuardState
{
	public bool HasLastGood { get; set; }

	public List<string> LastGoodModIds { get; set; } = [];

	public DateTime SessionStartedUtc { get; set; }

	public bool SessionSettled { get; set; }

	public List<string> QuarantinedModIds { get; set; } = [];
}

internal static class ModCrashGuard
{
	private const string LogIdent = "ModCrashGuard";

	private static readonly object Sync = new();

	private static readonly string[] EngineFolders =
	[
		"ExtraContent\\places\\",
		"ExtraContent\\models\\",
		"content\\configs\\"
	];

	private static readonly string[] EngineScriptExtensions = [".lua", ".luau", ".rbxm", ".rbxmx", ".rbxl", ".rbxlx", ".json"];

	private static string StatePath => Path.Combine(Paths.Data, "ModCrashGuard.json");

	public static string CrashReportsFolder => Path.Combine(Paths.LocalAppData, "Roblox", "logs", "crashes", "reports");

	public static DateTime BeginSession()
	{
		lock (Sync)
		{
			ModCrashGuardState state = Load();
			state.SessionStartedUtc = DateTime.UtcNow;
			state.SessionSettled = false;
			Save(state);
			return state.SessionStartedUtc;
		}
	}

	public static void MarkHealthy()
	{
		lock (Sync)
		{
			ModCrashGuardState state = Load();
			if (state.SessionSettled)
			{
				return;
			}
			state.SessionSettled = true;
			state.HasLastGood = true;
			state.LastGoodModIds = [.. ManagedModStore.Load().Where(record => record.Enabled).Select(record => record.Id)];
			Save(state);
			App.Logger?.WriteLine(LogIdent, "Roblox settled with " + state.LastGoodModIds.Count + " mods enabled, remembering this set as working");
		}
	}

	public static bool HasCrashReportSince(DateTime sinceUtc)
	{
		try
		{
			if (!Directory.Exists(CrashReportsFolder))
			{
				return false;
			}
			return Directory.EnumerateFiles(CrashReportsFolder, "*.dmp", SearchOption.TopDirectoryOnly)
				.Any(file => File.GetLastWriteTimeUtc(file) >= sinceUtc.AddSeconds(-2));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	public static IReadOnlyList<string> HandleCrash()
	{
		lock (Sync)
		{
			ModCrashGuardState state = Load();
			if (state.SessionSettled)
			{
				return [];
			}
			state.SessionSettled = true;

			List<ManagedModRecord> enabled = [.. ManagedModStore.Load().Where(record => record.Enabled)];
			HashSet<string> lastGood = new(state.LastGoodModIds, StringComparer.OrdinalIgnoreCase);
			List<ManagedModRecord> suspects = state.HasLastGood
				? [.. enabled.Where(record => !lastGood.Contains(record.Id))]
				: enabled;

			List<ManagedModRecord> engine = [.. suspects.Where(HasEngineFiles)];
			List<ManagedModRecord> chosen;
			if (engine.Count > 0)
			{
				chosen = engine;
			}
			else if (state.HasLastGood)
			{
				List<ManagedModRecord> replacements = [.. suspects.Where(record => ExternalModConfigs.IsExternal(record.Id))];
				chosen = replacements.Count > 0 ? replacements : suspects;
			}
			else
			{
				chosen = [];
			}

			List<string> names = [];
			foreach (ManagedModRecord record in chosen)
			{
				try
				{
					ExternalModConfigs.SetEnabled(record.Id, false);
					ManagedModStore.SetEnabled(record.Id, false);
					if (!state.QuarantinedModIds.Contains(record.Id, StringComparer.OrdinalIgnoreCase))
					{
						state.QuarantinedModIds.Add(record.Id);
					}
					names.Add(record.Name);
				}
				catch (Exception ex)
				{
					App.Logger?.WriteLine(LogIdent, "The mod " + record.Name + " could not be turned off: " + ex.Message);
				}
			}
			Save(state);
			App.Logger?.WriteLine(LogIdent, names.Count > 0
				? "Roblox crashed while loading mods, turned off: " + string.Join(", ", names)
				: "Roblox crashed, but no recently added mod looks responsible");
			return names;
		}
	}

	public static bool HasEngineFiles(ManagedModRecord record)
	{
		try
		{
			string folder = ManagedModStore.GetFolder(record.Id);
			if (!Directory.Exists(folder))
			{
				return false;
			}
			return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
				.Select(file => Path.GetRelativePath(folder, file))
				.Any(IsEngineFile);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			return false;
		}
	}

	public static bool IsEngineFile(string relative)
	{
		string normalized = relative.Replace('/', '\\');
		if (EngineFolders.Any(folder => normalized.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
		{
			return true;
		}
		return normalized.StartsWith("ExtraContent\\LuaPackages\\", StringComparison.OrdinalIgnoreCase)
			&& EngineScriptExtensions.Contains(Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase);
	}

	private static ModCrashGuardState Load()
	{
		try
		{
			if (File.Exists(StatePath))
			{
				return JsonSerializer.Deserialize<ModCrashGuardState>(File.ReadAllText(StatePath)) ?? new ModCrashGuardState();
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			App.Logger?.WriteLine(LogIdent, "The crash guard state could not be read: " + ex.Message);
		}
		return new ModCrashGuardState();
	}

	private static void Save(ModCrashGuardState state)
	{
		try
		{
			Directory.CreateDirectory(Paths.Data);
			string temporary = StatePath + ".tmp";
			File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions.Indented));
			File.Move(temporary, StatePath, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			App.Logger?.WriteLine(LogIdent, "The crash guard state could not be saved: " + ex.Message);
		}
	}
}

internal sealed record ReplacementConfigFile(string Label, string Path, string ConfigsFolder, bool Fleasion, bool Parked);

internal static class ExternalModConfigs
{
	public static List<ReplacementConfigFile> GetConfigFiles(string recordId)
	{
		List<ReplacementConfigFile> files = [];
		string fleasionFolder = FleasionModInstaller.ConfigsFolderFor(fleasion: true);
		foreach ((string configsFolder, string configName) in ReadLocks(recordId))
		{
			bool fleasion = string.Equals(configsFolder, fleasionFolder, StringComparison.OrdinalIgnoreCase);
			string owner = fleasion ? "Fleasion" : "AssetWarp";
			string active = Path.Combine(configsFolder, configName + ".json");
			string parked = Path.Combine(configsFolder, DisabledFolderName, configName + ".json");
			if (File.Exists(active))
			{
				files.Add(new ReplacementConfigFile(configName + " (" + owner + ")", active, configsFolder, fleasion, false));
			}
			else if (File.Exists(parked))
			{
				files.Add(new ReplacementConfigFile(configName + " (" + owner + ", off)", parked, configsFolder, fleasion, true));
			}
		}
		return files;
	}

	private const string LogIdent = "ExternalModConfigs";

	private const string DisabledFolderName = "Disabled";

	public static bool IsExternal(string recordId)
	{
		return ReadLocks(recordId).Count > 0;
	}

	public static bool HasActiveAssetWarpConfig(string recordId)
	{
		return GetConfigFiles(recordId).Any(file => !file.Fleasion && !file.Parked);
	}

	public static void RemoveConfigs(string recordId)
	{
		List<ReplacementConfigFile> files = GetConfigFiles(recordId);
		foreach (ReplacementConfigFile file in files)
		{
			try
			{
				HashSet<string> assetFolders = file.Fleasion ? [] : ReadAssetFolders(file.Path);
				File.Delete(file.Path);
				string assetsRoot = Path.GetFullPath(Path.Combine(file.ConfigsFolder, "Assets")) + Path.DirectorySeparatorChar;
				foreach (string folder in assetFolders)
				{
					string target = Path.GetFullPath(Path.Combine(file.ConfigsFolder, "Assets", folder));
					if (target.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(target))
					{
						Directory.Delete(target, recursive: true);
					}
				}
				App.Logger?.WriteLine(LogIdent, "Removed the replacement config " + file.Label + " with its mod");
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
			{
				App.Logger?.WriteLine(LogIdent, "The config " + file.Label + " could not be removed: " + ex.Message);
			}
		}
		if (files.Count > 0)
		{
			Voidstrap.Integrations.AssetProxy.TextureStripper.InvalidateRuntimeState();
		}
	}

	private static HashSet<string> ReadAssetFolders(string configPath)
	{
		HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
		using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(configPath));
		JsonElement root = document.RootElement;
		if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("replacement_rules", out JsonElement nested))
		{
			root = nested;
		}
		CollectAssetFolders(root, folders);
		return folders;
	}

	private static void CollectAssetFolders(JsonElement rules, HashSet<string> folders)
	{
		if (rules.ValueKind != JsonValueKind.Array)
		{
			return;
		}
		foreach (JsonElement rule in rules.EnumerateArray())
		{
			if (rule.ValueKind != JsonValueKind.Object)
			{
				continue;
			}
			if (rule.TryGetProperty("local_path", out JsonElement localPath) && localPath.ValueKind == JsonValueKind.String)
			{
				string[] parts = (localPath.GetString() ?? "").Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length >= 3 && parts[0].Equals("Assets", StringComparison.OrdinalIgnoreCase) && parts[1] != "." && parts[1] != "..")
				{
					folders.Add(parts[1]);
				}
			}
			if (rule.TryGetProperty("children", out JsonElement children))
			{
				CollectAssetFolders(children, folders);
			}
		}
	}

	public static void SetEnabled(string recordId, bool enabled)
	{
		foreach ((string configsFolder, string configName) in ReadLocks(recordId))
		{
			string active = Path.Combine(configsFolder, configName + ".json");
			string parked = Path.Combine(configsFolder, DisabledFolderName, configName + ".json");
			string source = enabled ? parked : active;
			string target = enabled ? active : parked;
			try
			{
				if (!File.Exists(source))
				{
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.Move(source, target, overwrite: true);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				App.Logger?.WriteLine(LogIdent, "The config " + configName + " could not be moved: " + ex.Message);
			}
		}
		Voidstrap.Integrations.AssetProxy.TextureStripper.InvalidateRuntimeState();
	}

	private static List<(string ConfigsFolder, string ConfigName)> ReadLocks(string recordId)
	{
		List<(string, string)> result = [];
		try
		{
			string folder = ManagedModStore.GetFolder(recordId);
			AddLock(result, Path.Combine(folder, "AssetWarp.lock"), FleasionModInstaller.AssetWarpResultPrefix, FleasionModInstaller.ConfigsFolderFor(fleasion: false));
			AddLock(result, Path.Combine(folder, "Fleasion.lock"), FleasionModInstaller.ResultPrefix, FleasionModInstaller.ConfigsFolderFor(fleasion: true));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
		{
			App.Logger?.WriteLine(LogIdent, "The replacement configs could not be read: " + ex.Message);
		}
		return result;
	}

	private static void AddLock(List<(string, string)> result, string lockPath, string prefix, string configsFolder)
	{
		if (!File.Exists(lockPath))
		{
			return;
		}
		string detail = File.ReadAllText(lockPath).Trim();
		if (detail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			detail = detail[prefix.Length..];
		}
		foreach (string name in detail.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			if (name.Contains('/') || name.Contains('\\') || name.Contains(".."))
			{
				continue;
			}
			result.Add((configsFolder, name));
		}
	}
}
