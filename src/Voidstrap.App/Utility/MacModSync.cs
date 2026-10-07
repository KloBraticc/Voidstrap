using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Voidstrap.Utility;

internal static class MacModSync
{
	private const string LogIdent = "MacModSync";

	private const string MarkerName = ".voidstrap-mods";

	private static readonly string[] SkippedExtensions = [".dll", ".exe", ".dylib", ".so", ".lock"];

	private static string ManifestPath(string resources) => Path.Combine(Paths.Data, "MacMods", Key(resources) + ".json");

	private static string BackupRoot(string resources) => Path.Combine(Paths.Data, "MacMods", Key(resources));

	private static string Key(string resources) => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(resources)) ?? "Roblox.app").Replace(".app", "", StringComparison.OrdinalIgnoreCase);

	private sealed class Manifest
	{
		public Dictionary<string, bool> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
	}

	public static Dictionary<string, string> Collect(bool includeSky)
	{
		Dictionary<string, string> selected = new(StringComparer.OrdinalIgnoreCase);
		if (Directory.Exists(Paths.Mods))
		{
			foreach (string file in Directory.EnumerateFiles(Paths.Mods, "*", SearchOption.AllDirectories))
			{
				string relative = Path.GetRelativePath(Paths.Mods, file).Replace('\\', '/');
				if (Accept(relative, includeSky))
					selected[relative] = file;
			}
		}
		try
		{
			foreach (ManagedModFile file in ManagedModStore.ScanEnabledFiles().Files)
			{
				string relative = file.Relative.Replace('\\', '/');
				if (Accept(relative, includeSky))
					selected[relative] = file.Source;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "Managed mods could not be indexed: " + ex.Message);
		}
		return selected;
	}

	private static bool Accept(string relative, bool includeSky)
	{
		if (relative.Equals("README.txt", StringComparison.OrdinalIgnoreCase)
			|| relative.StartsWith("ClientSettings/", StringComparison.OrdinalIgnoreCase)
			|| relative.Split('/').Any(part => part is "" or "." or "..")
			|| SkippedExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)
			|| ModAutoFixer.IsIgnoredModFile(relative.Replace('/', Path.DirectorySeparatorChar)))
			return false;
		return includeSky || !relative.StartsWith("PlatformContent/pc/textures/sky", StringComparison.OrdinalIgnoreCase);
	}

	public static void Apply(string resources, IReadOnlyDictionary<string, string> selected)
	{
		if (!Directory.Exists(resources))
		{
			App.Logger.WriteLine(LogIdent, "The Roblox resources folder is missing: " + resources);
			return;
		}

		string manifestPath = ManifestPath(resources);
		string backupRoot = BackupRoot(resources);
		string marker = Path.Combine(resources, MarkerName);
		Manifest previous = new();
		if (File.Exists(marker) && File.Exists(manifestPath))
		{
			try
			{
				previous = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath)) ?? new();
				previous.Files = new(previous.Files, StringComparer.OrdinalIgnoreCase);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogIdent, "The previous mod list could not be read: " + ex.Message);
			}
		}
		else if (Directory.Exists(backupRoot))
		{
			Directory.Delete(backupRoot, true);
		}

		int restored = 0;
		foreach ((string relative, bool hadOriginal) in previous.Files)
		{
			if (selected.ContainsKey(relative))
				continue;
			try
			{
				string destination = Path.Combine(resources, relative);
				string backup = Path.Combine(backupRoot, relative);
				if (hadOriginal && File.Exists(backup))
					File.Copy(backup, destination, true);
				else if (!hadOriginal && File.Exists(destination))
					File.Delete(destination);
				restored++;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogIdent, "Could not restore " + relative + ": " + ex.Message);
			}
		}

		Manifest next = new();
		int applied = 0;
		foreach ((string relative, string source) in selected)
		{
			string destination = Path.Combine(resources, relative);
			try
			{
				bool hadOriginal;
				if (previous.Files.TryGetValue(relative, out bool known))
				{
					hadOriginal = known;
				}
				else
				{
					hadOriginal = File.Exists(destination);
					if (hadOriginal)
					{
						string backup = Path.Combine(backupRoot, relative);
						Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
						File.Copy(destination, backup, true);
					}
				}
				next.Files[relative] = hadOriginal;
				Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
				File.Copy(source, destination, true);
				applied++;
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine(LogIdent, "Could not apply " + relative + ": " + ex.Message);
			}
		}

		Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
		File.WriteAllText(manifestPath, JsonSerializer.Serialize(next));
		File.WriteAllText(marker, "");
		App.Logger.WriteLine(LogIdent, $"Applied {applied} mod files and restored {restored} to the Roblox bundle");
	}
}
