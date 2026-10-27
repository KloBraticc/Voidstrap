using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Voidstrap.Integrations.Plugins;

public sealed partial class PluginManifest
{
	public const string ManifestFolder = ".voidstrap-plugin";

	private const long MaxJsonBytes = 256 * 1024;

	[GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
	private static partial Regex NamePattern { get; }

	public string Name { get; private init; } = "";

	public string Version { get; private init; } = "";

	public string Description { get; private init; } = "";

	public string Directory { get; private init; } = "";

	public string ModulePath { get; private init; } = "";

	public JsonObject Options { get; private init; } = new();

	public static List<PluginManifest> Discover(string root)
	{
		List<PluginManifest> found = [];
		if (!System.IO.Directory.Exists(root))
			return found;
		foreach (string folder in System.IO.Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
		{
			if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
				continue;
			try
			{
				if (TryRead(folder) is PluginManifest manifest)
					found.Add(manifest);
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("Plugins", Path.GetFileName(folder) + ": manifest refused: " + PluginHost.Describe(ex));
			}
		}
		return found.GroupBy(manifest => manifest.Name).Select(group => group.First()).ToList();
	}

	private static PluginManifest? TryRead(string folder)
	{
		string manifestPath = Path.Combine(folder, ManifestFolder, "plugin.json");
		if (!File.Exists(manifestPath))
			return null;
		JsonObject manifest = ReadObject(manifestPath);
		string name = manifest["name"]?.GetValue<string>() ?? "";
		if (!NamePattern.IsMatch(name))
			throw new InvalidDataException("the name must be lowercase letters, digits and hyphens");
		JsonObject hooks = ReadObject(Path.Combine(folder, "hooks", "hooks.json"));
		string module = hooks["modules"] is JsonArray modules && modules.Count == 1 ? modules[0]?.GetValue<string>() ?? "" : "";
		if (module.Length == 0)
			throw new InvalidDataException("hooks.json must name exactly one module");
		string root = Path.GetFullPath(folder);
		string modulePath = Path.GetFullPath(Path.Combine(root, "hooks", module));
		if (!modulePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("the module must be inside the plugin folder");
		string extension = Path.GetExtension(modulePath);
		if (!extension.Equals(".js", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
			throw new InvalidDataException("the module must be a .js or .mjs file");
		if (!File.Exists(modulePath))
			throw new FileNotFoundException("the module file is missing");
		return new PluginManifest
		{
			Name = name,
			Version = manifest["version"]?.GetValue<string>() ?? "0.0.0",
			Description = manifest["description"]?.GetValue<string>() ?? "",
			Directory = root,
			ModulePath = Path.GetRelativePath(root, modulePath),
			Options = ReadOptions(manifest["userConfig"] as JsonObject)
		};
	}

	private static JsonObject ReadOptions(JsonObject? config)
	{
		JsonObject options = new();
		if (config == null)
			return options;
		foreach (KeyValuePair<string, JsonNode?> field in config)
			options[field.Key] = (field.Value as JsonObject)?["default"]?.DeepClone();
		return options;
	}

	private static JsonObject ReadObject(string path)
	{
		FileInfo info = new(path);
		if (!info.Exists)
			throw new FileNotFoundException(Path.GetFileName(path) + " is missing");
		if (info.Length > MaxJsonBytes)
			throw new InvalidDataException(Path.GetFileName(path) + " is too large");
		return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException(Path.GetFileName(path) + " is not a JSON object");
	}
}
