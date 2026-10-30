using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Voidstrap.Integrations.Plugins;

public sealed partial class PluginManifest
{
    public const string ManifestFolder = ".voidstrap-mod";

    public const string RobloxAccountPermission = "roblox-account";

    public const string NativePermission = "native";

    public const string FilesPermission = "files";

    public const string WebPermission = "web";

    private static readonly HashSet<string> KnownPermissions = new(StringComparer.Ordinal) { RobloxAccountPermission, NativePermission, FilesPermission, WebPermission };

    private const long MaxJsonBytes = 256 * 1024;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex NamePattern { get; }

    public string Name { get; private init; } = "";

    public string Version { get; private init; } = "";

    public string Description { get; private init; } = "";

    public string Directory { get; private init; } = "";

    public string ModulePath { get; private init; } = "";

    public JsonObject Options { get; private init; } = new();

    public JsonObject ConfigFields { get; private init; } = new();

    public IReadOnlyList<string> NetworkHosts { get; private init; } = [];

    public IReadOnlyList<string> Permissions { get; private init; } = [];

    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.Ordinal);

    public static List<PluginManifest> Discover(string root, bool report = true)
    {
        List<PluginManifest> found = [];
        if (!System.IO.Directory.Exists(root))
            return found;
        foreach (string folder in System.IO.Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!System.IO.Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    continue;
                string folderName = Path.GetFileName(folder);
                if (folderName.StartsWith('.'))
                    continue;
                if (TryRead(folder, out PluginManifest? manifest, out string? error))
                {
                    if (manifest!.Name == folderName)
                        found.Add(manifest);
                    else if (report)
                        PluginHost.Problem(folderName, "the manifest was refused: the name " + manifest.Name + " must equal the folder name");
                }
                else if (report && System.IO.Directory.Exists(folder))
                    PluginHost.Problem(folderName, "the manifest was refused: " + (error ?? ManifestFolder + "/plugin.json is missing"));
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException && !System.IO.Directory.Exists(folder))
            {
            }
            catch (Exception ex)
            {
                if (report)
                    PluginHost.Problem(Path.GetFileName(folder), "the manifest was refused: " + PluginHost.Describe(ex));
            }
        }
        return found.GroupBy(manifest => manifest.Name).Select(group => group.First()).ToList();
    }

    public static PluginManifest? Read(string folder)
    {
        if (TryRead(folder, out PluginManifest? manifest, out string? error))
            return manifest;
        return error == null ? null : throw new InvalidDataException(error);
    }

    public static bool TryRead(string folder, out PluginManifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        string manifestPath = Path.Combine(folder, ManifestFolder, "plugin.json");
        if (!File.Exists(manifestPath))
            return false;
        if (!TryReadObject(manifestPath, out JsonObject? manifestObj, out error))
            return false;
        string name = manifestObj!["name"]?.ToString() ?? "";
        if (!NamePattern.IsMatch(name))
        {
            error = "the name must be lowercase letters, digits and hyphens";
            return false;
        }
        if (!TryReadObject(Path.Combine(folder, "hooks", "hooks.json"), out JsonObject? hooks, out error))
            return false;
        string module = hooks!["modules"] is JsonArray modules && modules.Count == 1 ? modules[0]?.ToString() ?? "" : "";
        if (module.Length == 0)
        {
            error = "hooks.json must name exactly one module";
            return false;
        }
        string root = Path.GetFullPath(folder);
        string modulePath = Path.GetFullPath(Path.Combine(root, "hooks", module));
        if (!modulePath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            error = "the module must be inside the plugin folder";
            return false;
        }
        string extension = Path.GetExtension(modulePath).ToLowerInvariant();
        if (extension is not (".js" or ".mjs" or ".ts" or ".tsx" or ".jsx"))
        {
            error = "the module must be a .js, .mjs, .ts, .tsx or .jsx file";
            return false;
        }
        if (!File.Exists(modulePath))
        {
            error = "the module file is missing";
            return false;
        }
        if (!TryReadPermissions(manifestObj["permissions"] as JsonArray, out List<string> permissions, out error))
            return false;
        manifest = new PluginManifest
        {
            Name = name,
            Version = manifestObj["version"]?.ToString() ?? "0.0.0",
            Description = manifestObj["description"]?.ToString() ?? "",
            Directory = root,
            ModulePath = Path.GetRelativePath(root, modulePath),
            Options = ReadOptions(manifestObj["userConfig"] as JsonObject),
            ConfigFields = manifestObj["userConfig"]?.DeepClone() as JsonObject ?? new JsonObject(),
            NetworkHosts = ReadHosts(manifestObj["network"] as JsonArray),
            Permissions = permissions
        };
        return true;
    }

    private static List<string> ReadHosts(JsonArray? hosts)
    {
        List<string> result = [];
        if (hosts == null)
            return result;
        foreach (JsonNode? host in hosts)
        {
            string value = host?.ToString().Trim().ToLowerInvariant() ?? "";
            if (Uri.CheckHostName(value) == UriHostNameType.Dns && !result.Contains(value))
                result.Add(value);
        }
        return result;
    }

    private static bool TryReadPermissions(JsonArray? permissions, out List<string> result, out string? error)
    {
        result = [];
        error = null;
        if (permissions == null)
            return true;
        foreach (JsonNode? node in permissions)
        {
            string value = node?.ToString().Trim().ToLowerInvariant() ?? "";
            if (!KnownPermissions.Contains(value))
            {
                error = "unknown permission " + value + ", the permissions are roblox-account, native, files and web";
                return false;
            }
            if (!result.Contains(value))
                result.Add(value);
        }
        return true;
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

    private static bool TryReadObject(string path, out JsonObject? obj, out string? error)
    {
        obj = null;
        error = null;
        FileInfo info = new(path);
        if (!info.Exists)
        {
            error = Path.GetFileName(path) + " is missing";
            return false;
        }
        if (info.Length > MaxJsonBytes)
        {
            error = Path.GetFileName(path) + " is too large";
            return false;
        }
        try
        {
            obj = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            if (obj == null)
            {
                error = Path.GetFileName(path) + " is not a JSON object";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = Path.GetFileName(path) + " could not be read: " + ex.Message;
            return false;
        }
    }
}
