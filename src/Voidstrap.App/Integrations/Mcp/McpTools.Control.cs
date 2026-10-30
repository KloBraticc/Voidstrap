using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Integrations.RiShade;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
    private const long MaxReadFileBytes = 1024 * 1024;

    private static readonly string[] ReadableExtensions =
    [
        ".json",
        ".txt",
        ".log",
        ".xml",
        ".ini",
        ".cfg",
        ".md",
        ".html",
        ".htm",
        ".css",
        ".js",
        ".lua",
        ".toml",
        ".yaml",
        ".yml",
        ".fx",
        ".fxh",
    ];

    private static McpToolResult ManageMods(JsonObject args)
    {
        string action = McpArgs.Trimmed(args, "action", "list").ToLowerInvariant();
        string id = McpArgs.Trimmed(args, "id");

        if (action == "list")
        {
            var list = new JsonArray();
            foreach (ManagedModLibraryEntry entry in ManagedModStore.ScanLibrary())
            {
                list.Add(
                    new JsonObject
                    {
                        ["id"] = entry.Record.Id,
                        ["name"] = entry.Record.Name,
                        ["enabled"] = entry.Record.Enabled,
                        ["files"] = entry.FileCount,
                        ["bytes"] = entry.TotalBytes,
                        ["problem"] = string.IsNullOrEmpty(entry.Failure) ? null : entry.Failure,
                    }
                );
            }
            return new McpToolResult(
                new JsonObject
                {
                    ["enabledCount"] = ManagedModStore.GetEnabledModCount(),
                    ["note"] =
                        "Mods apply the next time Roblox launches through Voidstrap. Earlier entries win when two mods change the same file.",
                    ["mods"] = list,
                }.ToJsonString(IndentedJson)
            );
        }

        ManagedModRecord? record =
            ManagedModStore.Load().FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? ManagedModStore.Load().FirstOrDefault(item => string.Equals(item.Name, id, StringComparison.OrdinalIgnoreCase));
        if (record == null)
            return new McpToolResult($"No managed mod has the id or name '{id}'. Call manage_mods with action 'list'.", true);

        switch (action)
        {
            case "enable":
            case "disable":
                {
                    bool enabled = action == "enable";
                    bool before = record.Enabled;
                    ManagedModStore.SetEnabled(record.Id, enabled);
                    PushUndo($"{action} mod {record.Name}", () => ManagedModStore.SetEnabled(record.Id, before));
                    RefreshMainWindow();
                    return new McpToolResult(
                        $"{(enabled ? "Enabled" : "Disabled")} the mod '{record.Name}'. It applies on the next Roblox launch."
                    );
                }
            case "rename":
                {
                    string name = McpArgs.Trimmed(args, "name");
                    if (name.Length == 0 || name.Length > 100)
                        return new McpToolResult("Provide a 'name' of 1 to 100 characters.", true);
                    string before = record.Name;
                    ManagedModStore.Rename(record.Id, name);
                    PushUndo($"rename mod {before}", () => ManagedModStore.Rename(record.Id, before));
                    RefreshMainWindow();
                    return new McpToolResult($"Renamed the mod '{before}' to '{name}'.");
                }
            case "move":
                {
                    string target = McpArgs.Trimmed(args, "targetId");
                    if (!ManagedModStore.Load().Any(item => string.Equals(item.Id, target, StringComparison.OrdinalIgnoreCase)))
                        return new McpToolResult("Provide a valid 'targetId' to move next to.", true);
                    ManagedModStore.MoveRelative(record.Id, target, McpArgs.Flag(args, "after", true));
                    RefreshMainWindow();
                    return new McpToolResult($"Moved the mod '{record.Name}'.");
                }
            case "delete":
                if (!McpArgs.Flag(args, "confirm", false))
                    return new McpToolResult(
                        $"Deleting '{record.Name}' removes its files for good. Call again with confirm set to true to delete it.",
                        true
                    );
                ManagedModStore.Delete(record.Id);
                RefreshMainWindow();
                return new McpToolResult($"Deleted the mod '{record.Name}'.");
            default:
                return new McpToolResult("Unknown action. Use list, enable, disable, rename, move, or delete.", true);
        }
    }

    private static McpToolResult ManageRiShade(JsonObject args)
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return new McpToolResult("RiShade is only available on Windows.", true);

        string action = McpArgs.Trimmed(args, "action", "status").ToLowerInvariant();
        switch (action)
        {
            case "status":
                {
                    IReadOnlyList<string> presets = RiShadeManager.GetAvailablePresets();
                    return new McpToolResult(
                        new JsonObject
                        {
                            ["enabled"] = App.Settings.Prop.RiShadeEnabled,
                            ["runtimeInstalled"] = RiShadeManager.IsRuntimePopulated,
                            ["currentPreset"] = RiShadeManager.PreferredPresetPath,
                            ["presetLoadable"] = RiShadeManager.HasLoadablePreset,
                            ["presetUsesDepth"] = RiShadeManager.PresetNeedsDepth,
                            ["depthModel"] = App.Settings.Prop.RiShadeDepthModel,
                            ["weakGpuPath"] = RiShadeDepthModels.LowEndHardware,
                            ["presets"] = new JsonArray(
                                presets.Select(path => (JsonNode)Path.GetRelativePath(RiShadeManager.RiShadeDir, path)).ToArray()
                            ),
                        }.ToJsonString(IndentedJson)
                    );
                }
            case "enable":
            case "disable":
                {
                    bool enabled = action == "enable";
                    bool before = App.Settings.Prop.RiShadeEnabled;
                    RiShadeManager.SetEnabled(enabled);
                    PushUndo($"{action} RiShade", () => RiShadeManager.SetEnabled(before));
                    RefreshMainWindow();
                    return new McpToolResult($"RiShade is now {(enabled ? "on" : "off")}.");
                }
            case "select_preset":
                {
                    string preset = McpArgs.Trimmed(args, "preset");
                    if (preset.Length == 0)
                        return new McpToolResult("Provide the 'preset' path relative to the RiShade folder, from action status.", true);
                    string? full = Path.IsPathRooted(preset) ? preset : McpArgs.ContainedPath(RiShadeManager.RiShadeDir, preset);
                    string? match =
                        full == null
                            ? null
                            : RiShadeManager
                                .GetAvailablePresets()
                                .FirstOrDefault(path =>
                                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase)
                                );
                    if (match == null)
                        return new McpToolResult("That preset was not found. Call manage_rishade with action status to list presets.", true);
                    string? before = RiShadeManager.PreferredPresetPath;
                    if (!RiShadeManager.SelectPreset(match))
                        return new McpToolResult("RiShade could not switch to that preset.", true);
                    if (before != null)
                        PushUndo("select RiShade preset", () => RiShadeManager.SelectPreset(before));
                    return new McpToolResult($"Selected the RiShade preset {Path.GetFileName(match)}.");
                }
            default:
                return new McpToolResult("Unknown action. Use status, enable, disable, or select_preset.", true);
        }
    }

    public static async Task<McpToolResult> ManageModGeneratorAsync(JsonObject args, Dispatcher dispatcher, CancellationToken token)
    {
        string action = McpArgs.Trimmed(args, "action", "status").ToLowerInvariant();
        try
        {
            switch (action)
            {
                case "status":
                    {
                        string? modId = ModGenerator.FindModId();
                        ModGeneratorSettings? current = modId == null ? null : ModGenerator.LoadSettings(modId);
                        return new McpToolResult(
                            new JsonObject
                            {
                                ["generated"] = current != null,
                                ["settings"] = current == null ? null : JsonSerializer.SerializeToNode(current),
                                ["savedThemes"] = new JsonArray(ModGenerator.ListThemes().Select(name => (JsonNode)name).ToArray()),
                                ["busy"] = ModGenerator.IsGenerating,
                            }.ToJsonString(IndentedJson)
                        );
                    }
                case "generate":
                    {
                        string? modId = ModGenerator.FindModId();
                        ModGeneratorSettings settings = (modId == null ? null : ModGenerator.LoadSettings(modId)) ?? new ModGeneratorSettings();
                        if (args["settings"] is JsonObject overrides)
                        {
                            JsonObject merged = JsonSerializer.SerializeToNode(settings)!.AsObject();
                            foreach (KeyValuePair<string, JsonNode?> pair in overrides)
                            {
                                if (pair.Key is nameof(ModGeneratorSettings.RobloxVersion) or nameof(ModGeneratorSettings.GeneratedUtc))
                                    continue;
                                merged[pair.Key] = pair.Value?.DeepClone();
                            }
                            settings = merged.Deserialize<ModGeneratorSettings>() ?? settings;
                        }
                        string? theme = McpArgs.String(args, "theme");
                        if (!string.IsNullOrWhiteSpace(theme))
                            settings =
                                ModGenerator.LoadTheme(theme.Trim()) ?? throw new InvalidOperationException("That saved theme was not found.");
                        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                        limit.CancelAfter(TimeSpan.FromMinutes(5));
                        string summary = await ModGenerator.GenerateAsync(settings, null, limit.Token).ConfigureAwait(false);
                        await dispatcher.InvokeAsync(RefreshMainWindow);
                        return new McpToolResult("Generated the mod: " + summary + ". It applies on the next Roblox launch.");
                    }
                case "save_theme":
                    {
                        string name = McpArgs.Trimmed(args, "name");
                        string? modId = ModGenerator.FindModId();
                        ModGeneratorSettings? current = modId == null ? null : ModGenerator.LoadSettings(modId);
                        if (current == null)
                            return new McpToolResult("Generate a mod first, then save its settings as a theme.", true);
                        return new McpToolResult(
                            "Saved the theme as " + ModGenerator.SaveTheme(name.Length == 0 ? current.ModName : name, current) + "."
                        );
                    }
                default:
                    return new McpToolResult("Unknown action. Use status, generate, or save_theme.", true);
            }
        }
        catch (OperationCanceledException)
        {
            return new McpToolResult("The mod generator was cancelled or took longer than 5 minutes.", true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new McpToolResult("The mod generator failed: " + ex.Message, true);
        }
    }

    public static async Task<McpToolResult> ListAccountsAsync(CancellationToken token)
    {
        List<SavedAccount> accounts = await SavedAccounts.LoadAsync(token).ConfigureAwait(false);
        CurrentAccountInfo? current = SavedAccounts.Current;
        var list = new JsonArray();
        foreach (SavedAccount account in accounts)
        {
            list.Add(
                new JsonObject
                {
                    ["userId"] = account.UserId,
                    ["username"] = account.Username,
                    ["displayName"] = account.DisplayName,
                    ["current"] = current?.UserId == account.UserId,
                    ["sessionExpired"] = account.Expired,
                    ["pinned"] = account.Pinned,
                    ["lastUsed"] = account.LastUsedUtc == default ? null : account.LastUsedUtc.ToString("u", CultureInfo.InvariantCulture),
                }
            );
        }
        return new McpToolResult(
            new JsonObject
            {
                ["switchingSupported"] = SavedAccounts.SwitchingSupported,
                ["note"] = "Account switching is done by the user in the Accounts page, MCP can only read this list.",
                ["accounts"] = list,
            }.ToJsonString(IndentedJson)
        );
    }

    public static McpToolResult ReadVoidstrapFileSafe(JsonObject args)
    {
        try
        {
            return ReadVoidstrapFile(args);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new McpToolResult("The file or folder could not be read: " + ex.Message, true);
        }
    }

    private static McpToolResult ReadVoidstrapFile(JsonObject args)
    {
        string relative = McpArgs.Trimmed(args, "path");
        string root = Paths.Base;
        if (relative.Length == 0 || relative == "." || relative == "/")
            return ListVoidstrapFolder(root, root);

        string? full = McpArgs.ContainedPath(root, relative);
        if (full == null)
            return new McpToolResult("The path must be relative and stay inside the Voidstrap folder.", true);
        if (IsPrivatePath(full))
            return new McpToolResult("That location holds account logins and cannot be read over MCP.", true);
        if (Directory.Exists(full))
            return ListVoidstrapFolder(root, full);
        if (!File.Exists(full))
            return new McpToolResult("That file does not exist.", true);
        if (!ReadableExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            return new McpToolResult("Only text files can be read: " + string.Join(", ", ReadableExtensions) + ".", true);

        using FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        bool clipped = stream.Length > MaxReadFileBytes;
        if (clipped)
            stream.Seek(-MaxReadFileBytes, SeekOrigin.End);
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        string text = reader.ReadToEnd();
        return new McpToolResult(
            $"{Path.GetRelativePath(root, full)} ({stream.Length:N0} bytes{(clipped ? ", showing the last 1 MB" : "")})"
                + Environment.NewLine
                + text
        );
    }

    private static bool IsPrivatePath(string full)
    {
        string backups = Path.GetFullPath(Paths.AccountBackups);
        return full.StartsWith(Path.TrimEndingDirectorySeparator(backups), StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(full).Equals(".dat", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(full).Contains("cookie", StringComparison.OrdinalIgnoreCase);
    }

    private static McpToolResult ListVoidstrapFolder(string root, string folder)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"Folder: {(string.Equals(root, folder, StringComparison.OrdinalIgnoreCase) ? "." : Path.GetRelativePath(root, folder))}"
        );
        foreach (string dir in Directory.EnumerateDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(200))
            if (!IsPrivatePath(dir))
                sb.AppendLine("  [dir]  " + Path.GetRelativePath(root, dir));
        foreach (string file in Directory.EnumerateFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(300))
            if (!IsPrivatePath(file))
                sb.AppendLine($"  [file] {Path.GetRelativePath(root, file)} ({new FileInfo(file).Length:N0} bytes)");
        return new McpToolResult(sb.ToString().TrimEnd());
    }

    private static void RefreshMainWindow()
    {
        Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault()?.RefreshCurrentPage();
    }
}
