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
    private static McpToolResult ListEditors()
    {
        IReadOnlyList<KeyValuePair<string, IMcpEditor>> editors = McpEditorRegistry.Snapshot();
        var sb = new StringBuilder();

        sb.AppendLine($"Open Editors ({editors.Count}):");
        if (editors.Count == 0)
        {
            sb.AppendLine("  (none currently open; you can open any editor on screen using open_editor)");
        }
        else
        {
            foreach (KeyValuePair<string, IMcpEditor> kv in editors)
                sb.AppendLine($"  id: {kv.Key} | kind: {kv.Value.Kind} | name: {kv.Value.DisplayName} | language: {kv.Value.Language}");
        }

        sb.AppendLine();
        List<string> bootstrapperThemes = GetInstalledBootstrapperThemes();
        sb.AppendLine(
            $"Installed Custom Bootstrapper Themes ({bootstrapperThemes.Count}): {(bootstrapperThemes.Count == 0 ? "none" : string.Join(", ", bootstrapperThemes))}"
        );
        IReadOnlyList<string> keystrokeThemes = KeystrokeThemeManager.GetAllThemeNames();
        sb.AppendLine($"Available Keystroke Themes ({keystrokeThemes.Count}): {string.Join(", ", keystrokeThemes)}");

        return new McpToolResult(sb.ToString().TrimEnd());
    }

    private static McpToolResult OpenEditor(JsonObject args)
    {
        string type = McpArgs.String(args, "type")?.Trim().ToLowerInvariant() ?? string.Empty;
        string name = McpArgs.String(args, "name")?.Trim() ?? string.Empty;
        bool activate = McpArgs.Flag(args, "activate", true);

        if (string.IsNullOrEmpty(type))
            return new McpToolResult("Missing 'type' argument. Use 'bootstrapper', 'keystroke', or 'fastflags'.", true);

        if (type is "fastflags" or "fastflag" or "fastflageditor")
        {
            MainWindow? mainWin = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
            if (mainWin == null)
                return new McpToolResult("MainWindow is not open.", true);

            mainWin.NavigateToPageByName("FastFlagEditor", null, focusWindow: true);
            KeyValuePair<string, IMcpEditor>? ffEntry = McpEditorRegistry.FindEntry("fastflags");
            return new McpToolResult($"Opened FastFlag Editor in MainWindow. Editor ID: {ffEntry?.Key ?? "fastflags"}");
        }

        if (type is "bootstrapper" or "customtheme" or "theme")
        {
            if (string.IsNullOrWhiteSpace(name))
                name = App.Settings.Prop.SelectedCustomTheme ?? "MyCustomTheme";

            name = name.Trim();
            if (!McpArgs.IsSafeName(name))
                return new McpToolResult(UnsafeThemeName, true);
            EnsureBootstrapperThemeExists(name, null);

            if (activate)
            {
                App.Settings.Prop.SelectedCustomTheme = name;
                App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
                App.Settings.Save();
            }

            BootstrapperEditorWindow? existing = Application
                .Current?.Windows.OfType<BootstrapperEditorWindow>()
                .FirstOrDefault(w => string.Equals(((IMcpEditor)w).DisplayName, name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (existing.WindowState == System.Windows.WindowState.Minimized)
                    existing.WindowState = System.Windows.WindowState.Normal;
                existing.Activate();
                KeyValuePair<string, IMcpEditor>? existingEntry = McpEditorRegistry.FindEntry(name);
                return new McpToolResult($"Focused existing Bootstrapper Editor for '{name}'. Editor ID: {existingEntry?.Key ?? name}");
            }

            var window = new BootstrapperEditorWindow(name);
            window.Show();
            window.Activate();
            KeyValuePair<string, IMcpEditor>? openedEntry = McpEditorRegistry.FindEntry(name);
            return new McpToolResult($"Opened Bootstrapper Editor for '{name}'. Editor ID: {openedEntry?.Key ?? name}");
        }

        if (type is "keystroke" or "keystrokes")
        {
            if (string.IsNullOrWhiteSpace(name))
                name = App.Settings.Prop.KeystrokesThemeName ?? "MyKeystrokeTheme";

            name = name.Trim();
            if (!McpArgs.IsSafeName(name))
                return new McpToolResult(UnsafeThemeName, true);
            string? baseTheme = McpArgs.String(args, "baseTheme")?.Trim();
            EnsureKeystrokeThemeExists(name, baseTheme);

            if (activate)
            {
                App.Settings.Prop.KeystrokesThemeName = name;
                App.Settings.Prop.SelectedCustomKeystrokeTheme = name;
                App.Settings.Prop.KeystrokesOverlayEnabled = true;
                App.Settings.Save();
                KeystrokeThemeManager.NotifyThemeChanged();
            }

            KeystrokeThemeEditorWindow? existing = Application
                .Current?.Windows.OfType<KeystrokeThemeEditorWindow>()
                .FirstOrDefault(w => string.Equals(((IMcpEditor)w).DisplayName, name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                if (existing.WindowState == System.Windows.WindowState.Minimized)
                    existing.WindowState = System.Windows.WindowState.Normal;
                existing.Activate();
                KeyValuePair<string, IMcpEditor>? existingEntry = McpEditorRegistry.FindEntry(name);
                return new McpToolResult($"Focused existing Keystroke Theme Editor for '{name}'. Editor ID: {existingEntry?.Key ?? name}");
            }

            var window = new KeystrokeThemeEditorWindow(name);
            window.Show();
            window.Activate();
            KeyValuePair<string, IMcpEditor>? openedEntry = McpEditorRegistry.FindEntry(name);
            return new McpToolResult($"Opened Keystroke Theme Editor for '{name}'. Editor ID: {openedEntry?.Key ?? name}");
        }

        return new McpToolResult($"Unsupported editor type '{type}'. Use 'bootstrapper', 'keystroke', or 'fastflags'.", true);
    }

    private const int EditorHistoryDepth = 30;

    private const int EditorHistoryKeys = 64;

    private static readonly object EditorHistoryGate = new();

    private static readonly Dictionary<string, List<string>> EditorHistory = new(StringComparer.OrdinalIgnoreCase);

    private static int _editorHistoryHooked;

    private static void HookEditorHistory()
    {
        if (Interlocked.Exchange(ref _editorHistoryHooked, 1) == 0)
            McpEditorRegistry.Unregistered += OnEditorUnregistered;
    }

    private static void OnEditorUnregistered(string id)
    {
        lock (EditorHistoryGate)
        {
            foreach (string key in EditorHistory.Keys.Where(key => key.StartsWith(id + "|", StringComparison.OrdinalIgnoreCase)).ToList())
                EditorHistory.Remove(key);
        }
    }

    private static string HistoryKey(string editorId, string? file) =>
        editorId + "|" + (string.IsNullOrWhiteSpace(file) ? "" : file.Trim().Replace('\\', '/').ToLowerInvariant());

    private static void PushHistory(string editorId, string? file, string text)
    {
        HookEditorHistory();
        lock (EditorHistoryGate)
        {
            string key = HistoryKey(editorId, file);
            if (!EditorHistory.TryGetValue(key, out List<string>? stack))
            {
                if (EditorHistory.Count >= EditorHistoryKeys)
                    EditorHistory.Remove(EditorHistory.Keys.First());
                stack = [];
                EditorHistory[key] = stack;
            }
            if (stack.Count > 0 && string.Equals(stack[^1], text, StringComparison.Ordinal))
                return;
            stack.Add(text);
            if (stack.Count > EditorHistoryDepth)
                stack.RemoveAt(0);
        }
    }

    private static string? PopHistory(string editorId, string? file)
    {
        lock (EditorHistoryGate)
        {
            if (!EditorHistory.TryGetValue(HistoryKey(editorId, file), out List<string>? stack) || stack.Count == 0)
                return null;
            string text = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return text;
        }
    }

    private static bool IsRootFile(string? file) =>
        string.IsNullOrWhiteSpace(file) || string.Equals(file.Trim().Replace('\\', '/'), "Theme.xml", StringComparison.OrdinalIgnoreCase);

    private static string? ReadEditorText(IMcpEditor editor, string? file) =>
        IsRootFile(file) ? editor.GetCode() : editor.ReadFile(file!.Trim());

    private static McpEditResult WriteEditorText(IMcpEditor editor, string? file, string text) =>
        IsRootFile(file) ? editor.SetCode(text) : editor.WriteFile(file!.Trim(), text);

    private static McpToolResult ApplyEditorText(
        KeyValuePair<string, IMcpEditor> entry,
        string? file,
        string original,
        string updated,
        bool revertOnError,
        string action
    )
    {
        if (string.Equals(original, updated, StringComparison.Ordinal))
            return new McpToolResult($"No change was needed in {DescribeTarget(entry, file)}.");

        McpEditResult result = WriteEditorText(entry.Value, file, updated);
        if (!result.Success)
        {
            if (revertOnError)
            {
                McpEditResult restored = WriteEditorText(entry.Value, file, original);
                return new McpToolResult(
                    $"{action} failed in {DescribeTarget(entry, file)}: {result.Message}{(restored.Success ? " The previous code was restored." : " Restoring the previous code also failed: " + restored.Message)}",
                    true
                );
            }
            PushHistory(entry.Key, file, original);
            return new McpToolResult(
                $"{action} failed in {DescribeTarget(entry, file)}: {result.Message} The change was kept so you can fix it, call undo_editor to go back.",
                true
            );
        }

        PushHistory(entry.Key, file, original);
        return new McpToolResult($"{action} in {DescribeTarget(entry, file)}: {result.Message} ({CountLines(updated)} lines)");
    }

    private static string DescribeTarget(KeyValuePair<string, IMcpEditor> entry, string? file) =>
        IsRootFile(file) ? entry.Key : entry.Key + " file " + file!.Trim();

    private static int CountLines(string text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1);

    private static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static List<string> SplitLines(string text) => text.Replace("\r\n", "\n").Split('\n').ToList();

    private static bool ResolveEditor(JsonObject args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error)
    {
        string? id = McpArgs.String(args, "editorId");
        KeyValuePair<string, IMcpEditor>? found = McpEditorRegistry.FindEntry(id);
        if (found is { } value)
        {
            entry = value;
            error = null;
            return true;
        }
        entry = default;
        error = new McpToolResult(NoEditorError(id), true);
        return false;
    }

    private static McpToolResult GetEditorCode(JsonObject args)
    {
        string? id = McpArgs.String(args, "editorId");
        string? file = McpArgs.String(args, "file");
        bool numbered = McpArgs.Flag(args, "lineNumbers", false);
        long? startArg = McpArgs.Long(args, "startLine");
        long? endArg = McpArgs.Long(args, "endLine");

        KeyValuePair<string, IMcpEditor>? found = McpEditorRegistry.FindEntry(id);
        string? text;
        string header;
        if (found is { } entry)
        {
            text = ReadEditorText(entry.Value, file);
            if (text == null)
                return new McpToolResult($"'{file}' could not be read from {entry.Key}. Call list_editor_files to see the files.", true);
            var head = new StringBuilder();
            head.AppendLine($"Editor: {entry.Key} ({entry.Value.DisplayName})");
            if (!IsRootFile(file))
                head.AppendLine($"File: {file!.Trim()}");
            head.AppendLine($"Kind: {entry.Value.Kind}");
            head.AppendLine($"Language: {(IsRootFile(file) ? entry.Value.Language : Path.GetExtension(file))}");
            if (entry.Value.Languages.Count > 0)
                head.AppendLine($"Other languages: {string.Join(", ", entry.Value.Languages)} (use set_editor_language)");
            if (entry.Value.Files.Count > 1)
                head.AppendLine($"Files: {string.Join(", ", entry.Value.Files)}");
            head.AppendLine($"Format guide: {entry.Value.Guidance}");
            header = head.ToString();
        }
        else
        {
            string clean = (id ?? "").Trim();
            if (clean.StartsWith("bootstrapper:", StringComparison.OrdinalIgnoreCase))
                clean = clean["bootstrapper:".Length..];
            if (!McpArgs.IsSafeName(clean))
                return new McpToolResult(NoEditorError(id), true);
            string xmlPath = Path.Combine(Paths.CustomThemes, clean, "Theme.xml");
            if (!File.Exists(xmlPath))
                return new McpToolResult(NoEditorError(id), true);
            text = File.ReadAllText(xmlPath);
            header = $"Theme on disk ({clean}), open it with open_editor to edit it live.\n";
        }

        List<string> lines = SplitLines(text);
        int start = (int)Math.Clamp(startArg ?? 1, 1, Math.Max(1, lines.Count));
        int end = (int)Math.Clamp(endArg ?? lines.Count, start, Math.Max(start, lines.Count));
        bool partial = start > 1 || end < lines.Count;
        int width = end.ToString(CultureInfo.InvariantCulture).Length;

        var sb = new StringBuilder(header);
        sb.AppendLine($"Lines {start} to {end} of {lines.Count}{(partial ? " (partial)" : "")}");
        sb.AppendLine("=== CODE START ===");
        for (int i = start; i <= end && i <= lines.Count; i++)
            sb.AppendLine(numbered ? i.ToString(CultureInfo.InvariantCulture).PadLeft(width) + " | " + lines[i - 1] : lines[i - 1]);
        sb.AppendLine("=== CODE END ===");
        return new McpToolResult(sb.ToString());
    }

    private static McpToolResult SetEditorCode(JsonObject args)
    {
        string? code = McpArgs.String(args, "code");
        if (code == null)
            return new McpToolResult("Missing 'code' argument.", true);
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        string? file = McpArgs.String(args, "file");
        string original = ReadEditorText(entry.Value, file) ?? string.Empty;
        return ApplyEditorText(entry, file, original, code, McpArgs.Flag(args, "revertOnError", false), "Updated the code");
    }

    private static McpToolResult ReplaceInEditor(JsonObject args)
    {
        string? oldText = McpArgs.String(args, "oldText");
        string? newText = McpArgs.String(args, "newText");
        if (string.IsNullOrEmpty(oldText) || newText == null)
            return new McpToolResult("Both 'oldText' and 'newText' arguments are required.", true);
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        string? file = McpArgs.String(args, "file");
        string? current = ReadEditorText(entry.Value, file);
        if (current == null)
            return new McpToolResult($"'{file}' could not be read from {entry.Key}.", true);

        if (!TryReplace(current, oldText, newText, McpArgs.Flag(args, "replaceAll", false), out string updated, out string problem))
            return new McpToolResult(
                $"{problem} in {DescribeTarget(entry, file)}. Call get_editor_code or find_in_editor to inspect it.",
                true
            );

        return ApplyEditorText(entry, file, current, updated, McpArgs.Flag(args, "revertOnError", false), "Replaced the snippet");
    }

    private static bool TryReplace(string current, string oldText, string newText, bool replaceAll, out string updated, out string problem)
    {
        updated = current;
        problem = "";
        string source = current;
        string needle = oldText;
        string replacement = newText;
        int count = CountOccurrences(source, needle);
        if (count == 0)
        {
            source = current.Replace("\r\n", "\n");
            needle = oldText.Replace("\r\n", "\n");
            replacement = newText.Replace("\r\n", "\n");
            count = CountOccurrences(source, needle);
        }
        if (count == 0)
        {
            problem = "oldText was not found";
            return false;
        }
        if (count > 1 && !replaceAll)
        {
            problem = $"oldText appears {count} times, add more surrounding text to make it unique or set replaceAll to true";
            return false;
        }
        if (replaceAll)
        {
            updated = source.Replace(needle, replacement, StringComparison.Ordinal);
        }
        else
        {
            int index = source.IndexOf(needle, StringComparison.Ordinal);
            updated = source[..index] + replacement + source[(index + needle.Length)..];
        }
        if (!ReferenceEquals(source, current) && current.Contains("\r\n", StringComparison.Ordinal))
            updated = updated.Replace("\n", "\r\n");
        return true;
    }

    private static int CountOccurrences(string text, string needle)
    {
        if (needle.Length == 0)
            return 0;
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static McpToolResult EditEditor(JsonObject args)
    {
        if (args["edits"] is not JsonArray edits || edits.Count == 0)
            return new McpToolResult("Provide a non empty 'edits' array.", true);
        if (edits.Count > 200)
            return new McpToolResult("At most 200 edits can be applied at once.", true);
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        string? file = McpArgs.String(args, "file");
        string? original = ReadEditorText(entry.Value, file);
        if (original == null)
            return new McpToolResult($"'{file}' could not be read from {entry.Key}.", true);

        string newline = Newline(original);
        string working = original;
        var applied = new List<string>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (edits[i] is not JsonObject edit)
                return new McpToolResult($"Edit {i + 1} is not an object. Nothing was changed.", true);

            string op = McpArgs.Trimmed(edit, "op", "replace").ToLowerInvariant();
            string text = McpArgs.String(edit, "text") ?? McpArgs.String(edit, "newText") ?? string.Empty;
            List<string> lines = SplitLines(working);
            int lineCount = lines.Count;
            switch (op)
            {
                case "replace":
                    {
                        string? oldText = McpArgs.String(edit, "oldText");
                        if (string.IsNullOrEmpty(oldText))
                            return new McpToolResult($"Edit {i + 1}: 'oldText' is required for replace. Nothing was changed.", true);
                        if (!TryReplace(working, oldText, text, McpArgs.Flag(edit, "replaceAll", false), out string next, out string problem))
                            return new McpToolResult($"Edit {i + 1}: {problem}. Nothing was changed.", true);
                        working = next;
                        applied.Add("replace");
                        break;
                    }
                case "insert":
                    {
                        int line = (int)Math.Clamp(McpArgs.Long(edit, "line") ?? lineCount + 1, 1, lineCount + 1);
                        lines.InsertRange(line - 1, SplitLines(text));
                        working = string.Join(newline, lines);
                        applied.Add($"insert at line {line}");
                        break;
                    }
                case "append":
                    {
                        working = working.Length == 0 || working.EndsWith('\n') ? working + text : working + newline + text;
                        applied.Add("append");
                        break;
                    }
                case "delete_lines":
                case "replace_lines":
                    {
                        long? startLine = McpArgs.Long(edit, "startLine");
                        if (startLine == null || startLine < 1 || startLine > lineCount)
                            return new McpToolResult(
                                $"Edit {i + 1}: 'startLine' must be between 1 and {lineCount}. Nothing was changed.",
                                true
                            );
                        int from = (int)startLine.Value;
                        int to = (int)Math.Clamp(McpArgs.Long(edit, "endLine") ?? from, from, lineCount);
                        lines.RemoveRange(from - 1, to - from + 1);
                        if (op == "replace_lines")
                            lines.InsertRange(from - 1, SplitLines(text));
                        working = string.Join(newline, lines);
                        applied.Add($"{op.Replace('_', ' ')} {from} to {to}");
                        break;
                    }
                default:
                    return new McpToolResult(
                        $"Edit {i + 1}: unknown op '{op}'. Use replace, insert, append, delete_lines, or replace_lines. Nothing was changed.",
                        true
                    );
            }
        }

        McpToolResult result = ApplyEditorText(
            entry,
            file,
            original,
            working,
            McpArgs.Flag(args, "revertOnError", true),
            $"Applied {applied.Count} edit(s) ({string.Join(", ", applied)})"
        );
        return result;
    }

    private static McpToolResult FindInEditor(JsonObject args)
    {
        string? query = McpArgs.String(args, "query");
        if (string.IsNullOrEmpty(query))
            return new McpToolResult("Provide a 'query'.", true);
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        string? file = McpArgs.String(args, "file");
        string? text = ReadEditorText(entry.Value, file);
        if (text == null)
            return new McpToolResult($"'{file}' could not be read from {entry.Key}.", true);

        Regex? pattern = null;
        if (McpArgs.Flag(args, "regex", false))
        {
            try
            {
                pattern = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            }
            catch (ArgumentException ex)
            {
                return new McpToolResult("Invalid regex: " + ex.Message, true);
            }
        }

        int context = McpArgs.Int(args, "context", 0, 0, 5);
        List<string> lines = SplitLines(text);
        var hits = new List<int>();
        try
        {
            for (int i = 0; i < lines.Count && hits.Count < 200; i++)
            {
                if (pattern != null ? pattern.IsMatch(lines[i]) : lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    hits.Add(i);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return new McpToolResult("The regex took too long, simplify it.", true);
        }

        if (hits.Count == 0)
            return new McpToolResult($"No matches for '{query}' in {DescribeTarget(entry, file)} ({lines.Count} lines).");

        var sb = new StringBuilder();
        sb.AppendLine($"{hits.Count}{(hits.Count >= 200 ? "+" : "")} matching line(s) in {DescribeTarget(entry, file)}:");
        int lastShown = -1;
        foreach (int hit in hits)
        {
            for (int i = Math.Max(0, hit - context); i <= Math.Min(lines.Count - 1, hit + context); i++)
            {
                if (i <= lastShown)
                    continue;
                if (lastShown >= 0 && i > lastShown + 1)
                    sb.AppendLine("  ...");
                sb.AppendLine($"{(i == hit ? ">" : " ")}{i + 1,5} | {lines[i]}");
                lastShown = i;
            }
        }
        return new McpToolResult(sb.ToString().TrimEnd());
    }

    private static McpToolResult UndoEditor(JsonObject args)
    {
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        string? file = McpArgs.String(args, "file");
        string? previous = PopHistory(entry.Key, file);
        if (previous == null)
            return new McpToolResult($"There is nothing to undo in {DescribeTarget(entry, file)}.", true);

        McpEditResult result = WriteEditorText(entry.Value, file, previous);
        return new McpToolResult($"Undid the last MCP change in {DescribeTarget(entry, file)}: {result.Message}", !result.Success);
    }

    private static McpToolResult ListEditorFiles(JsonObject args)
    {
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        IReadOnlyList<string> files = entry.Value.Files;
        var sb = new StringBuilder();
        sb.AppendLine($"Editor: {entry.Key} ({entry.Value.DisplayName}), language {entry.Value.Language}");
        if (files.Count == 0)
            sb.AppendLine("This editor has one document, edit it without a 'file' argument.");
        else
        {
            sb.AppendLine($"Files ({files.Count}), pass one as 'file' to read or edit it, new text files are created by writing them:");
            foreach (string file in files)
                sb.AppendLine("  " + file);
        }
        if (entry.Value.Languages.Count > 0)
            sb.AppendLine($"Languages: {string.Join(", ", entry.Value.Languages)} (switch with set_editor_language)");
        return new McpToolResult(sb.ToString().TrimEnd());
    }

    private static McpToolResult SetEditorLanguage(JsonObject args)
    {
        string language = McpArgs.Trimmed(args, "language");
        if (language.Length == 0)
            return new McpToolResult("Provide a 'language'.", true);
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        McpEditResult result = entry.Value.SetLanguage(language);
        return new McpToolResult($"{entry.Key}: {result.Message}", !result.Success);
    }

    private static McpToolResult DeleteEditorFile(JsonObject args)
    {
        string file = McpArgs.Trimmed(args, "file");
        if (file.Length == 0)
            return new McpToolResult("Provide the 'file' to delete.", true);
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        string? previous = entry.Value.ReadFile(file);
        McpEditResult result = entry.Value.DeleteFile(file);
        if (result.Success && previous != null)
            PushHistory(entry.Key, file, previous);
        return new McpToolResult(
            $"{entry.Key}: {result.Message}{(result.Success && previous != null ? " Call undo_editor with this file to restore it." : "")}",
            !result.Success
        );
    }

    private static McpToolResult SaveEditor(JsonObject args)
    {
        if (!ResolveEditor(args, out KeyValuePair<string, IMcpEditor> entry, out McpToolResult? error))
            return error!;

        McpEditResult res = entry.Value.Save();
        if (!res.Success)
            return new McpToolResult($"Save failed in {entry.Key}: {res.Message}", true);

        return new McpToolResult($"Saved {entry.Key}: {res.Message}");
    }
}
