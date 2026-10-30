using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Voidstrap.Resources;

namespace Voidstrap.Integrations.Mcp;

public static class McpActivity
{
    private const int MaxNameLength = 30;

    private const int MaxSubjectLength = 60;

    public static string FriendlyClientName(string? raw)
    {
        string name = (raw ?? "").Trim();
        if (name.Length == 0)
            return "AI";

        string lower = name.ToLowerInvariant();
        (string Needle, string Label)[] known =
        [
            (McpConnectInfo.DesktopClientLabel.ToLowerInvariant(), McpConnectInfo.DesktopClientLabel),
            (McpConnectInfo.CliClientLabel.ToLowerInvariant(), McpConnectInfo.CliClientLabel),
            (McpConnectInfo.OpenCodeClientLabel.ToLowerInvariant(), McpConnectInfo.OpenCodeClientLabel),
            ("cursor", "Cursor"),
            ("visual studio code", "VS Code"),
            ("vscode", "VS Code"),
            ("windsurf", "Windsurf"),
            ("lm studio", "LM Studio"),
            ("lmstudio", "LM Studio"),
        ];
        foreach ((string needle, string label) in known)
        {
            if (lower.Contains(needle, StringComparison.Ordinal))
                return label;
        }

        var clean = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
                clean.Append(c);
            else if (c is ' ' or '-' or '_' or '.')
                clean.Append(' ');
        }
        string result = string.Join(' ', clean.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (result.Length == 0)
            return "AI";
        if (result.Length > MaxNameLength)
            result = result[..MaxNameLength].Trim();
        return char.ToUpperInvariant(result[0]) + result[1..];
    }

    public static void Report(string? client, string tool, JsonObject args)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;

        string name = FriendlyClientName(client);
        JsonObject copy = (JsonObject)args.DeepClone();
        dispatcher.BeginInvoke(new Action(() => Show(name, tool, copy)));
    }

    private static void Show(string name, string tool, JsonObject args)
    {
        try
        {
            string? message = Describe(tool, args);
            if (message == null)
                return;
            UI.Elements.Settings.MainWindow? window = Application
                .Current?.Windows.OfType<UI.Elements.Settings.MainWindow>()
                .FirstOrDefault();
            window?.ShowAiActivity(string.Format(Strings.Ui_Mcp_Activity_Title, name), message);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("McpActivity", "The activity banner could not be shown: " + ex.Message);
        }
    }

    private static string Clip(string text) => text.Length > MaxSubjectLength ? text[..MaxSubjectLength].TrimEnd() + "..." : text;

    private static string EditorTarget(JsonObject args)
    {
        string? file = McpArgs.String(args, "file")?.Trim();
        KeyValuePair<string, IMcpEditor>? entry = McpEditorRegistry.FindEntry(McpArgs.String(args, "editorId"));
        string editor = entry?.Value.DisplayName ?? McpArgs.String(args, "editorId") ?? Strings.Ui_Mcp_Activity_Editors;
        return Clip(string.IsNullOrEmpty(file) ? editor : editor + ", " + file);
    }

    private static string? Describe(string tool, JsonObject args)
    {
        string action = McpArgs.Trimmed(args, "action").ToLowerInvariant();
        bool reading =
            action
            is ""
                or "list"
                or "status"
                or "get"
                or "schema"
                or "list_profiles"
                or "guide"
                or "types"
                or "files"
                or "read_file"
                or "problems"
                or "validate"
                or "test";
        return tool switch
        {
            "get_voidstrap_overview" => string.Format(Strings.Ui_Mcp_Activity_Reading, Strings.Ui_Mcp_Activity_Overview),
            "get_settings" => string.Format(Strings.Ui_Mcp_Activity_Reading, Strings.Ui_Mcp_Activity_Settings),
            "set_settings" => string.Format(Strings.Ui_Mcp_Activity_Changing, Strings.Ui_Mcp_Activity_Settings),
            "list_fastflags" or "search_fastflag_tracker" => string.Format(Strings.Ui_Mcp_Activity_Reading, "FastFlags"),
            "set_fastflags" => string.Format(Strings.Ui_Mcp_Activity_Changing, "FastFlags"),
            "list_editors" => string.Format(Strings.Ui_Mcp_Activity_Reading, Strings.Ui_Mcp_Activity_Editors),
            "open_editor" => string.Format(
                Strings.Ui_Mcp_Activity_Opening,
                Clip(McpArgs.Trimmed(args, "name", McpArgs.Trimmed(args, "type", Strings.Ui_Mcp_Activity_Editors)))
            ),
            "list_editor_files" or "get_editor_code" or "find_in_editor" => string.Format(
                Strings.Ui_Mcp_Activity_Reading,
                EditorTarget(args)
            ),
            "set_editor_code" or "replace_in_editor" or "edit_editor" or "delete_editor_file" or "set_editor_language" => string.Format(
                Strings.Ui_Mcp_Activity_Editing,
                EditorTarget(args)
            ),
            "save_editor" => string.Format(Strings.Ui_Mcp_Activity_Saving, EditorTarget(args)),
            "undo_editor" or "undo_last_change" => Strings.Ui_Mcp_Activity_Undoing,
            "navigate_ui" => string.Format(Strings.Ui_Mcp_Activity_Opening, Clip(McpArgs.Trimmed(args, "page", "Voidstrap"))),
            "manage_themes_and_mods" => string.Format(
                reading ? Strings.Ui_Mcp_Activity_Reading : Strings.Ui_Mcp_Activity_Changing,
                Strings.Ui_Mcp_Activity_Themes
            ),
            "manage_mods" => string.Format(
                reading ? Strings.Ui_Mcp_Activity_Reading : Strings.Ui_Mcp_Activity_Changing,
                Strings.Ui_Mcp_Activity_Mods
            ),
            "manage_rishade" => string.Format(reading ? Strings.Ui_Mcp_Activity_Reading : Strings.Ui_Mcp_Activity_Changing, "RiShade"),
            "manage_mod_generator" => string.Format(
                reading ? Strings.Ui_Mcp_Activity_Reading : Strings.Ui_Mcp_Activity_Changing,
                Strings.Ui_Mcp_Activity_ModGenerator
            ),
            "manage_app_theme" => string.Format(
                reading ? Strings.Ui_Mcp_Activity_Reading : Strings.Ui_Mcp_Activity_Changing,
                Strings.Ui_Mcp_Activity_AppTheme
            ),
            "list_accounts" => string.Format(Strings.Ui_Mcp_Activity_Reading, Strings.Ui_Mcp_Activity_Accounts),
            "manage_voidstrap_mods" => string.Format(
                reading ? Strings.Ui_Mcp_Activity_Reading : Strings.Ui_Mcp_Activity_Changing,
                Strings.Ui_Nav_VoidstrapMods
            ),
            "read_voidstrap_file" => string.Format(Strings.Ui_Mcp_Activity_Reading, Clip(McpArgs.Trimmed(args, "path", "Voidstrap"))),
            "diagnose_app" => string.Format(Strings.Ui_Mcp_Activity_Reading, Strings.Ui_Mcp_Activity_Problems),
            "fix_app_issue" => string.Format(Strings.Ui_Mcp_Activity_Fixing, Strings.Ui_Mcp_Activity_Problems),
            "test_bootstrapper_theme" => string.Format(
                Strings.Ui_Mcp_Activity_Testing,
                Clip(McpArgs.Trimmed(args, "name", App.Settings.Prop.SelectedCustomTheme ?? "Voidstrap"))
            ),
            "launch_roblox" => Strings.Ui_Mcp_Activity_Launching,
            "get_logs_and_activity" => string.Format(Strings.Ui_Mcp_Activity_Reading, Strings.Ui_Mcp_Activity_Logs),
            "get_guide" => null,
            _ => null,
        };
    }
}
