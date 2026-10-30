using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.Models.Persistable;
using Voidstrap.UI.Elements.Editor;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
    private const long MaxImportBytes = 512L * 1024 * 1024;

    private const long MaxAssetBytes = 64L * 1024 * 1024;

    private const int MaxTextContentChars = 16 * 1024 * 1024;

    private static bool HasExtension(string path, params string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static McpToolResult ManageThemesAndMods(JsonObject args)
    {
        string action = McpArgs.String(args, "action")?.Trim().ToLowerInvariant() ?? "list";
        string name = McpArgs.String(args, "name")?.Trim() ?? string.Empty;

        if (action.Contains("theme", StringComparison.Ordinal) && name.Length > 0 && !McpArgs.IsSafeName(name))
            return new McpToolResult(UnsafeThemeName, true);

        switch (action)
        {
            case "list":
                {
                    var info = new JsonObject
                    {
                        ["selectedBootstrapperStyle"] = App.Settings.Prop.BootstrapperStyle.ToString(),
                        ["selectedCustomBootstrapperTheme"] = App.Settings.Prop.SelectedCustomTheme,
                        ["customBootstrapperThemes"] = new JsonArray(GetInstalledBootstrapperThemes().Select(t => (JsonNode)t).ToArray()),
                        ["selectedKeystrokeTheme"] = App.Settings.Prop.KeystrokesThemeName,
                        ["builtInKeystrokeThemes"] = new JsonArray(
                            KeystrokeThemeManager.GetBuiltInThemes().Select(t => (JsonNode)t.Name).ToArray()
                        ),
                        ["customKeystrokeThemes"] = new JsonArray(
                            KeystrokeThemeManager.GetCustomThemes().Select(t => (JsonNode)t.Name).ToArray()
                        ),
                        ["cursorType"] = App.Settings.Prop.CursorType.ToString(),
                        ["customFontInstalled"] = File.Exists(Paths.CustomFont),
                        ["customDeathSoundInstalled"] = File.Exists(Paths.CustomDeathSound),
                        ["modFiles"] = new JsonArray(GetModFiles().Select(f => (JsonNode)f).ToArray()),
                    };
                    return new McpToolResult(info.ToJsonString(IndentedJson));
                }

            case "create_bootstrapper_theme":
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return new McpToolResult("Missing 'name' for create_bootstrapper_theme.", true);

                    name = Path.GetFileName(name);
                    string? xml = McpArgs.String(args, "xml");
                    EnsureBootstrapperThemeExists(name, xml);

                    bool activate = McpArgs.Flag(args, "activate", true);
                    if (activate)
                    {
                        App.Settings.Prop.SelectedCustomTheme = name;
                        App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
                        App.Settings.Save();
                    }

                    return new McpToolResult(
                        $"Created custom Bootstrapper theme '{name}' at {Path.Combine(Paths.CustomThemes, name, "Theme.xml")}{(activate ? " and set it as active" : "")}."
                    );
                }

            case "delete_bootstrapper_theme":
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return new McpToolResult("Missing 'name' for delete_bootstrapper_theme.", true);

                    string dir = Path.Combine(Paths.CustomThemes, Path.GetFileName(name));
                    if (!Directory.Exists(dir))
                        return new McpToolResult($"Bootstrapper theme '{name}' does not exist.", true);

                    Directory.Delete(dir, recursive: true);
                    if (string.Equals(App.Settings.Prop.SelectedCustomTheme, name, StringComparison.OrdinalIgnoreCase))
                    {
                        App.Settings.Prop.SelectedCustomTheme = null;
                        App.Settings.Save();
                    }
                    return new McpToolResult($"Deleted custom Bootstrapper theme '{name}'.");
                }

            case "activate_bootstrapper_theme":
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return new McpToolResult("Missing 'name' for activate_bootstrapper_theme.", true);

                    name = Path.GetFileName(name);
                    EnsureBootstrapperThemeExists(name, null);
                    App.Settings.Prop.SelectedCustomTheme = name;
                    App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
                    App.Settings.Save();
                    return new McpToolResult($"Activated custom Bootstrapper theme '{name}'.");
                }

            case "create_keystroke_theme":
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return new McpToolResult("Missing 'name' for create_keystroke_theme.", true);

                    name = Path.GetFileName(name);
                    string? baseTheme = McpArgs.String(args, "baseTheme")?.Trim();
                    string? code = McpArgs.String(args, "code");

                    KeystrokeTheme theme = KeystrokeThemeManager.GetTheme(baseTheme).Clone(name);
                    theme.IsBuiltIn = false;

                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        try
                        {
                            using KeystrokeScriptHost host = new(code);
                            host.Render();
                        }
                        catch (Exception ex)
                        {
                            return new McpToolResult($"Invalid keystroke overlay code: {ex.Message}", true);
                        }
                        theme.ScriptSource = code;
                    }

                    if (!KeystrokeThemeManager.SaveCustomTheme(theme))
                        return new McpToolResult($"Could not save custom Keystroke theme '{name}'.", true);

                    bool activate = McpArgs.Flag(args, "activate", true);
                    if (activate)
                    {
                        App.Settings.Prop.KeystrokesThemeName = name;
                        App.Settings.Prop.SelectedCustomKeystrokeTheme = name;
                        App.Settings.Prop.KeystrokesOverlayEnabled = true;
                        App.Settings.Save();
                        KeystrokeThemeManager.NotifyThemeChanged();
                    }

                    return new McpToolResult($"Saved custom Keystroke theme '{name}'{(activate ? " and activated it" : "")}.");
                }

            case "delete_keystroke_theme":
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return new McpToolResult("Missing 'name' for delete_keystroke_theme.", true);

                    if (!KeystrokeThemeManager.DeleteCustomTheme(name))
                        return new McpToolResult($"Could not delete custom Keystroke theme '{name}'.", true);

                    return new McpToolResult($"Deleted custom Keystroke theme '{name}'.");
                }

            case "activate_keystroke_theme":
                {
                    if (string.IsNullOrWhiteSpace(name))
                        return new McpToolResult("Missing 'name' for activate_keystroke_theme.", true);

                    App.Settings.Prop.KeystrokesThemeName = name;
                    App.Settings.Prop.SelectedCustomKeystrokeTheme = name;
                    App.Settings.Prop.KeystrokesOverlayEnabled = true;
                    App.Settings.Save();
                    KeystrokeThemeManager.NotifyThemeChanged();
                    return new McpToolResult($"Activated Keystroke theme '{name}'.");
                }

            case "set_custom_font":
                {
                    string? sourcePath = McpArgs.String(args, "sourcePath")?.Trim();
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                        return new McpToolResult("Provide an existing font file path in 'sourcePath'.", true);
                    if (!HasExtension(sourcePath, ".ttf", ".otf") || new FileInfo(sourcePath).Length > MaxAssetBytes)
                        return new McpToolResult("The font must be a .ttf or .otf file under 64 MB.", true);

                    Directory.CreateDirectory(Path.GetDirectoryName(Paths.CustomFont)!);
                    File.Copy(sourcePath, Paths.CustomFont, overwrite: true);
                    App.Settings.Prop.CustomFontLocation = sourcePath;
                    App.Settings.Save();
                    return new McpToolResult($"Installed custom font from '{sourcePath}' to '{Paths.CustomFont}'.");
                }

            case "clear_custom_font":
                {
                    if (File.Exists(Paths.CustomFont))
                        File.Delete(Paths.CustomFont);
                    App.Settings.Prop.CustomFontLocation = string.Empty;
                    App.Settings.Save();
                    return new McpToolResult("Removed custom font.");
                }

            case "set_custom_death_sound":
                {
                    string? sourcePath = McpArgs.String(args, "sourcePath")?.Trim();
                    if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                        return new McpToolResult("Provide an existing audio file path in 'sourcePath'.", true);
                    if (!HasExtension(sourcePath, ".ogg", ".mp3", ".wav") || new FileInfo(sourcePath).Length > MaxAssetBytes)
                        return new McpToolResult("The death sound must be an .ogg, .mp3 or .wav file under 64 MB.", true);

                    Directory.CreateDirectory(Path.GetDirectoryName(Paths.CustomDeathSound)!);
                    File.Copy(sourcePath, Paths.CustomDeathSound, overwrite: true);
                    return new McpToolResult($"Installed custom death sound from '{sourcePath}' to '{Paths.CustomDeathSound}'.");
                }

            case "clear_custom_death_sound":
                {
                    if (File.Exists(Paths.CustomDeathSound))
                        File.Delete(Paths.CustomDeathSound);
                    return new McpToolResult("Removed custom death sound.");
                }

            case "write_mod_file":
                {
                    string? relativePath = McpArgs.String(args, "relativePath")?.Trim();
                    string? content = McpArgs.String(args, "content");
                    string? sourcePath = McpArgs.String(args, "sourcePath")?.Trim();

                    if (string.IsNullOrWhiteSpace(relativePath))
                        return new McpToolResult("Provide 'relativePath' inside VoidstrapMods.", true);

                    string? destFull = McpArgs.ContainedPath(Paths.Mods, relativePath);
                    if (destFull == null)
                        return new McpToolResult("relativePath must stay inside the VoidstrapMods directory.", true);

                    Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        if (!File.Exists(sourcePath))
                            return new McpToolResult($"sourcePath '{sourcePath}' does not exist.", true);
                        if (new FileInfo(sourcePath).Length > MaxImportBytes)
                            return new McpToolResult("sourcePath is larger than 512 MB.", true);
                        File.Copy(sourcePath, destFull, overwrite: true);
                    }
                    else if (content != null)
                    {
                        if (content.Length > MaxTextContentChars)
                            return new McpToolResult("content is larger than 16 MB, use sourcePath instead.", true);
                        File.WriteAllText(destFull, content);
                    }
                    else
                    {
                        return new McpToolResult("Provide either 'sourcePath' or 'content' for write_mod_file.", true);
                    }

                    return new McpToolResult($"Wrote mod file '{relativePath}' to '{destFull}'.");
                }

            case "delete_mod_file":
                {
                    string? relativePath = McpArgs.String(args, "relativePath")?.Trim();
                    if (string.IsNullOrWhiteSpace(relativePath))
                        return new McpToolResult("Provide 'relativePath' inside VoidstrapMods.", true);

                    string? destFull = McpArgs.ContainedPath(Paths.Mods, relativePath);
                    if (destFull == null)
                        return new McpToolResult("relativePath must stay inside the VoidstrapMods directory.", true);

                    if (File.Exists(destFull))
                        File.Delete(destFull);
                    return new McpToolResult($"Deleted mod file '{relativePath}'.");
                }

            default:
                return new McpToolResult(
                    $"Unknown action '{action}'. Valid actions: list, create_bootstrapper_theme, delete_bootstrapper_theme, activate_bootstrapper_theme, create_keystroke_theme, delete_keystroke_theme, activate_keystroke_theme, set_custom_font, clear_custom_font, set_custom_death_sound, clear_custom_death_sound, write_mod_file, delete_mod_file.",
                    true
                );
        }
    }
}
