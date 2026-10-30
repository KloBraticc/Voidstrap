using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
    private const int SearchHardCap = 300;

    public static McpToolResult SearchEverything(JsonObject args)
    {
        string mode = McpArgs.String(args, "mode")?.Trim().ToLowerInvariant() ?? "code";
        try
        {
            return mode switch
            {
                "code" => SearchCode(args),
                "logs" => SearchLogs(args),
                "errors" => SearchErrors(args),
                "settings" => SearchSettings(args),
                "ui" => SearchUi(args),
                "at" => SearchAt(args),
                "files" => SearchFiles(args),
                "mods" => SearchMods(args),
                _ => new McpToolResult("Unknown mode '" + mode + "'. Use code, logs, errors, settings, ui, at, files or mods.", true),
            };
        }
        catch (Exception ex)
        {
            return new McpToolResult("The search failed: " + ex.Message, true);
        }
    }

    private static Regex BuildSearchRegex(string query, bool useRegex, bool caseSensitive, bool wholeWord)
    {
        string pattern = useRegex ? query : Regex.Escape(query);
        if (wholeWord)
            pattern = @"\b(?:" + pattern + @")\b";
        try
        {
            return new Regex(pattern, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException("The search pattern is not a valid regular expression: " + ex.Message);
        }
    }

    private static bool GlobMatches(string glob, string path)
    {
        if (string.IsNullOrWhiteSpace(glob))
            return true;
        string pattern = Regex
            .Escape(glob.Trim().Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", ".");
        return Regex.IsMatch(path, "^" + pattern + "$", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    }

    private static McpToolResult SearchCode(JsonObject args)
    {
        string query = McpArgs.String(args, "query")?.Trim() ?? "";
        if (query.Length < 2)
            return new McpToolResult("Give a query of at least 2 characters.", true);
        if (!AppSourceStore.Available)
            return new McpToolResult("This build does not carry the source list, mode code needs a full build.", true);
        bool useRegex = McpArgs.Flag(args, "regex", false);
        bool caseSensitive = McpArgs.Flag(args, "caseSensitive", false);
        bool wholeWord = McpArgs.Flag(args, "wholeWord", false);
        bool definitions = McpArgs.Flag(args, "definitions", false);
        int context = Math.Clamp(McpArgs.Int(args, "context", 0, 0, 3), 0, 3);
        int max = Math.Clamp(McpArgs.Int(args, "max", 60, 1, SearchHardCap), 1, SearchHardCap);
        string? glob = McpArgs.String(args, "glob")?.Trim();
        Regex regex = BuildSearchRegex(query, useRegex, caseSensitive, wholeWord);
        Regex? declarationRegex = definitions
            ? BuildSearchRegex(
                "(class|record|struct|interface|enum)\\s+\\w*"
                    + Regex.Escape(query)
                    + "\\w*\\b|(public|private|internal|protected|static).*\\b"
                    + Regex.Escape(query)
                    + "\\s*\\(",
                false,
                caseSensitive,
                true
            )
            : null;

        StringBuilder output = new();
        int hits = 0;
        int scanned = 0;
        foreach (string path in AppSourceStore.ListAll())
        {
            if (hits >= max)
                break;
            if (glob != null && !GlobMatches(glob, path))
                continue;
            string? content = AppSourceStore.Read(path);
            if (content == null)
                continue;
            scanned++;
            string[] lines = content.Split('\n');
            List<string> fileHits = [];
            for (int index = 0; index < lines.Length; index++)
            {
                string line = lines[index].TrimEnd('\r');
                if (declarationRegex != null && !declarationRegex.Match(line).Success)
                    continue;
                Match match = regex.Match(line);
                if (!match.Success)
                    continue;
                fileHits.Add(
                    "  line "
                        + (index + 1)
                        + " at column "
                        + (match.Index + 1)
                        + " ("
                        + match.Length
                        + " chars): "
                        + (line.Length > 240 ? line[..240] : line).TrimStart()
                );
                for (int after = 1; after <= context && index + after < lines.Length; after++)
                    fileHits.Add("        + " + lines[index + after].TrimEnd('\r').TrimStart());
                hits++;
                if (hits >= max)
                    break;
            }
            if (fileHits.Count > 0)
            {
                output.AppendLine(path + ":");
                foreach (string hit in fileHits)
                    output.AppendLine(hit);
            }
        }
        if (hits == 0)
            return new McpToolResult(
                "No matches for " + query + " in " + scanned + " source files. Try regex true, drop wholeWord, or widen glob."
            );
        output.AppendLine("");
        output.AppendLine(
            hits
                + " line(s) across "
                + scanned
                + " files"
                + (hits >= max ? ", stopped at the " + max + " result limit, raise max or narrow with glob" : "")
                + ". Read a file with manage_app_code action read."
        );
        return new McpToolResult(output.ToString().TrimEnd());
    }

    private static McpToolResult SearchLogs(JsonObject args)
    {
        string query = McpArgs.String(args, "query")?.Trim() ?? "";
        if (query.Length < 2)
            return new McpToolResult("Give a query of at least 2 characters.", true);
        string source = McpArgs.String(args, "source")?.Trim().ToLowerInvariant() ?? "all";
        bool errorsOnly = McpArgs.Flag(args, "errorsOnly", false);
        int max = Math.Clamp(McpArgs.Int(args, "max", 80, 1, SearchHardCap), 1, SearchHardCap);
        Regex regex = BuildSearchRegex(query, McpArgs.Flag(args, "regex", false), false, false);
        StringBuilder output = new();
        string? currentLabel = null;
        int hits = 0;

        void Emit(string label, string line)
        {
            if (currentLabel != label)
            {
                output.AppendLine(label + ":");
                currentLabel = label;
            }
            output.AppendLine("  " + (line.Length > 300 ? line[..300] : line));
            hits++;
        }

        bool MatchesErrorsFilter(string line)
        {
            return line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                || line.Contains("crash", StringComparison.OrdinalIgnoreCase)
                || line.Contains("warn", StringComparison.OrdinalIgnoreCase);
        }

        if (source is "all" or "voidstrap")
        {
            foreach (string line in App.Logger.History)
            {
                if (hits >= max)
                    break;
                if (errorsOnly && !MatchesErrorsFilter(line))
                    continue;
                if (regex.Match(line).Success)
                    Emit("Voidstrap log", line);
            }
        }
        if (source is "all" or "roblox" && hits < max && !string.IsNullOrWhiteSpace(Paths.RobloxLogs) && Directory.Exists(Paths.RobloxLogs))
        {
            FileInfo? latest = new DirectoryInfo(Paths.RobloxLogs)
                .GetFiles("*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest != null)
            {
                try
                {
                    using FileStream stream = new(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (stream.Length > 8L * 1024 * 1024)
                        stream.Seek(-8L * 1024 * 1024, SeekOrigin.End);
                    using StreamReader reader = new(stream, Encoding.UTF8);
                    while (hits < max && reader.ReadLine() is { } line)
                    {
                        if (errorsOnly && !MatchesErrorsFilter(line))
                            continue;
                        if (regex.Match(line).Success)
                            Emit("Roblox log " + latest.Name, line);
                    }
                }
                catch (Exception ex)
                {
                    output.AppendLine("The Roblox log could not be read: " + ex.Message);
                }
            }
        }
        if (
            source is "all" or "history"
            && hits < max
            && !string.IsNullOrWhiteSpace(Paths.ServerHistory)
            && File.Exists(Paths.ServerHistory)
        )
        {
            foreach (string line in File.ReadAllLines(Paths.ServerHistory))
            {
                if (hits >= max)
                    break;
                if (regex.Match(line).Success)
                    Emit("Server history", line);
            }
        }

        return hits == 0
            ? new McpToolResult(
                "No log lines match "
                    + query
                    + (errorsOnly ? " among error and warning lines" : "")
                    + ". Widen the query or drop errorsOnly."
            )
            : new McpToolResult(
                hits
                    + " matching log line(s)"
                    + (hits >= max ? ", stopped at the " + max + " limit" : "")
                    + ":\n"
                    + output.ToString().TrimEnd()
            );
    }

    private static McpToolResult SearchErrors(JsonObject args)
    {
        string? query = McpArgs.String(args, "query")?.Trim();
        Regex? filter =
            query != null && query.Length >= 2 ? BuildSearchRegex(query, McpArgs.Flag(args, "regex", false), false, false) : null;
        StringBuilder output = new();
        int found = 0;

        output.AppendLine("Recorded in app notifications (the hidden errors the UI keeps):");
        foreach (AppNotification notification in AppNotifications.Items)
        {
            string text =
                "["
                + notification.Kind
                + "] "
                + notification.Title
                + ": "
                + notification.Text
                + (notification.Count > 1 ? " (seen " + notification.Count + " times)" : "")
                + " source "
                + notification.Source;
            if (filter != null && !filter.Match(text).Success)
                continue;
            output.AppendLine("  " + (text.Length > 300 ? text[..300] : text));
            found++;
        }

        output.AppendLine("Mod problems:");
        int modProblems = 0;
        foreach (Voidstrap.Integrations.Plugins.PluginHost.ModInfo mod in Voidstrap.Integrations.Plugins.PluginHost.ListMods())
        {
            foreach (string problem in mod.Problems)
            {
                if (filter != null && !filter.Match(problem).Success)
                    continue;
                output.AppendLine("  " + mod.Name + ": " + problem);
                modProblems++;
                found++;
            }
        }
        if (modProblems == 0)
            output.AppendLine("  none");

        output.AppendLine("Managed mod scan failures:");
        int failures = 0;
        foreach (ManagedModLibraryEntry entry in ManagedModStore.ScanLibrary())
        {
            if (entry.Failure.Length == 0)
                continue;
            if (filter != null && !filter.Match(entry.Failure).Success)
                continue;
            output.AppendLine("  " + entry.Record.Name + ": " + entry.Failure);
            failures++;
            found++;
        }
        if (failures == 0)
            output.AppendLine("  none");

        output.AppendLine("Error and warning lines in the Voidstrap log:");
        int logErrors = 0;
        foreach (string line in App.Logger.History)
        {
            if (
                !(
                    line.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("fail", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("crash", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("warn", StringComparison.OrdinalIgnoreCase)
                )
            )
                continue;
            if (filter != null && !filter.Match(line).Success)
                continue;
            if (logErrors < 40)
                output.AppendLine("  " + (line.Length > 300 ? line[..300] : line));
            logErrors++;
            found++;
        }
        output.AppendLine("  " + logErrors + " total, showing the first 40");

        return found == 0
            ? new McpToolResult("No hidden errors found anywhere. Voidstrap is clean.")
            : new McpToolResult(found + " finding(s):\n" + output.ToString().TrimEnd());
    }

    private static McpToolResult SearchSettings(JsonObject args)
    {
        string query = McpArgs.String(args, "query")?.Trim() ?? "";
        if (query.Length < 2)
            return new McpToolResult("Give a query of at least 2 characters.", true);
        Regex regex = BuildSearchRegex(query, McpArgs.Flag(args, "regex", false), false, false);
        int max = Math.Clamp(McpArgs.Int(args, "max", 60, 1, SearchHardCap), 1, SearchHardCap);
        StringBuilder output = new();
        int hits = 0;

        foreach (
            System.Reflection.PropertyInfo property in typeof(Voidstrap.Models.Persistable.AppSettings).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
            )
        )
        {
            if (hits >= max)
                break;
            if (!property.CanRead)
                continue;
            object? value = property.GetValue(App.Settings.Prop);
            string valueText = value?.ToString() ?? "null";
            if (regex.Match(property.Name).Success || regex.Match(valueText).Success)
            {
                output.AppendLine("setting " + property.Name + " = " + valueText);
                hits++;
            }
        }

        foreach (KeyValuePair<string, object> flag in App.FastFlags.Prop)
        {
            if (hits >= max)
                break;
            string valueText = flag.Value?.ToString() ?? "null";
            if (regex.Match(flag.Key).Success || regex.Match(valueText).Success)
            {
                output.AppendLine("fastflag " + flag.Key + " = " + valueText);
                hits++;
            }
        }

        string global = App.GlobalSettings.FileLocation;
        if (hits < max && global.Length > 0 && File.Exists(global))
        {
            foreach (string line in File.ReadAllLines(global))
            {
                if (hits >= max)
                    break;
                if (regex.Match(line).Success)
                {
                    output.AppendLine("roblox global " + line.Trim());
                    hits++;
                }
            }
        }

        return hits == 0
            ? new McpToolResult(
                "No setting, FastFlag or Roblox global matches " + query + ". Use get_settings or list_fastflags to see everything."
            )
            : new McpToolResult(
                hits + " match(es)" + (hits >= max ? ", stopped at the " + max + " limit" : "") + ":\n" + output.ToString().TrimEnd()
            );
    }

    private static string ElementText(FrameworkElement element)
    {
        string? text = element switch
        {
            TextBlock textBlock => textBlock.Text,
            HeaderedItemsControl headered => headered.Header?.ToString(),
            ContentControl content when content.Content is string contentText => contentText,
            _ => element.ToolTip as string ?? System.Windows.Automation.AutomationProperties.GetName(element),
        };
        return text ?? "";
    }

    private static Rect ElementBounds(FrameworkElement element, Window window)
    {
        try
        {
            return element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private static IEnumerable<FrameworkElement> WalkElements(DependencyObject root)
    {
        Stack<DependencyObject> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject node = pending.Pop();
            if (node is FrameworkElement element)
                yield return element;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int index = 0; index < count; index++)
                pending.Push(VisualTreeHelper.GetChild(node, index));
            if (node is System.Windows.Controls.Primitives.Popup popup && popup.Child != null)
                pending.Push(popup.Child);
        }
    }

    private static McpToolResult SearchUi(JsonObject args)
    {
        string query = McpArgs.String(args, "query")?.Trim() ?? "";
        if (query.Length < 2)
            return new McpToolResult("Give a query of at least 2 characters, or use mode at to ask what sits at a pixel.", true);
        Regex regex = BuildSearchRegex(query, McpArgs.Flag(args, "regex", false), false, false);
        int max = Math.Clamp(McpArgs.Int(args, "max", 60, 1, SearchHardCap), 1, SearchHardCap);
        StringBuilder output = new();
        int hits = 0;
        foreach (Window window in Application.Current.Windows.OfType<Window>().ToArray())
        {
            if (hits >= max)
                break;
            foreach (FrameworkElement element in WalkElements(window))
            {
                if (hits >= max)
                    break;
                if (!element.IsVisible || element.ActualWidth <= 0)
                    continue;
                string name = element.Name ?? "";
                string type = element.GetType().Name;
                string text = ElementText(element) ?? "";
                if (!regex.Match(name).Success && !regex.Match(type).Success && !regex.Match(text).Success)
                    continue;
                Rect bounds = ElementBounds(element, window);
                FrameworkElement? parent = VisualTreeHelper.GetParent(element) as FrameworkElement;
                output.AppendLine(
                    window.Title
                        + " > "
                        + (parent?.Name ?? parent?.GetType().Name ?? "")
                        + " > "
                        + (name.Length > 0 ? name : "(unnamed)")
                        + " ["
                        + type
                        + "] text '"
                        + (text.Length > 80 ? text[..80] : text)
                        + "' rect x"
                        + Math.Round(bounds.X)
                        + " y"
                        + Math.Round(bounds.Y)
                        + " w"
                        + Math.Round(bounds.Width)
                        + " h"
                        + Math.Round(bounds.Height)
                );
                hits++;
            }
        }
        return hits == 0
            ? new McpToolResult("No visible element matches " + query + " in any open window. Coordinates use mode at with x and y.")
            : new McpToolResult(
                hits
                    + " element(s)"
                    + (hits >= max ? ", stopped at the " + max + " limit" : "")
                    + ", rects are pixels from each window's top left:\n"
                    + output.ToString().TrimEnd()
            );
    }

    private static McpToolResult SearchAt(JsonObject args)
    {
        if (
            !(args["x"] is JsonValue xNode && xNode.TryGetValue(out double x))
            || !(args["y"] is JsonValue yNode && yNode.TryGetValue(out double y))
        )
            return new McpToolResult("Give x and y, pixels from the window's top left. Use mode ui first to see the rects.", true);
        string? windowTitle = McpArgs.String(args, "window");
        Window? window =
            Application
                .Current.Windows.OfType<Window>()
                .ToArray()
                .FirstOrDefault(candidate =>
                    candidate.IsVisible
                    && (windowTitle == null || (candidate.Title ?? "").Contains(windowTitle, StringComparison.OrdinalIgnoreCase))
                )
            ?? Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate => candidate.IsVisible);
        if (window == null)
            return new McpToolResult("No visible Voidstrap window is open.", true);
        Point point = new(x, y);
        List<(FrameworkElement Element, Rect Bounds)> under = [];
        foreach (FrameworkElement element in WalkElements(window))
        {
            if (!element.IsVisible || element.ActualWidth <= 0)
                continue;
            Rect bounds = ElementBounds(element, window);
            if (bounds.IsEmpty || !bounds.Contains(point))
                continue;
            under.Add((element, bounds));
        }
        if (under.Count == 0)
            return new McpToolResult(
                "Nothing sits at x"
                    + x
                    + " y"
                    + y
                    + " in '"
                    + window.Title
                    + "'. The point is outside every visible element, use mode ui to compare rects."
            );
        under.Sort((left, right) => (left.Bounds.Width * left.Bounds.Height).CompareTo(right.Bounds.Width * right.Bounds.Height));
        StringBuilder output = new();
        output.AppendLine("From the deepest element to the window at x" + x + " y" + y + " in '" + window.Title + "':");
        foreach ((FrameworkElement element, Rect bounds) in under.Take(12))
        {
            string name = element.Name ?? "";
            string text = ElementText(element) ?? "";
            output.AppendLine(
                "  "
                    + (name.Length > 0 ? name : "(unnamed)")
                    + " ["
                    + element.GetType().Name
                    + "] text '"
                    + (text.Length > 60 ? text[..60] : text)
                    + "' rect x"
                    + Math.Round(bounds.X)
                    + " y"
                    + Math.Round(bounds.Y)
                    + " w"
                    + Math.Round(bounds.Width)
                    + " h"
                    + Math.Round(bounds.Height)
                    + (name.Length > 0 ? " anchorable by name" : "")
            );
        }
        return new McpToolResult(output.ToString().TrimEnd());
    }

    private static McpToolResult SearchFiles(JsonObject args)
    {
        string query = McpArgs.String(args, "query")?.Trim() ?? "";
        if (query.Length < 2)
            return new McpToolResult("Give a query of at least 2 characters.", true);
        Regex regex = BuildSearchRegex(query, McpArgs.Flag(args, "regex", false), false, false);
        bool content = McpArgs.Flag(args, "content", true);
        int max = Math.Clamp(McpArgs.Int(args, "max", 60, 1, SearchHardCap), 1, SearchHardCap);
        string[] roots = [Paths.Config, Paths.Mods, Paths.Plugins, Paths.Data];
        StringBuilder output = new();
        int hits = 0;
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            if (hits >= max || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;
            string fullRoot = Path.GetFullPath(root);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (hits >= max)
                    break;
                string relative = Path.GetRelativePath(fullRoot, file).Replace('\\', '/');
                if (!seen.Add(file))
                    continue;
                FileInfo info = new(file);
                if (info.Length > 4L * 1024 * 1024)
                    continue;
                bool nameHit = regex.Match(Path.GetFileName(file)).Success;
                if (nameHit)
                {
                    output.AppendLine(relative + " (file name, " + info.Length + " bytes)");
                    hits++;
                }
                if (!content || hits >= max)
                    continue;
                try
                {
                    string[] lines = File.ReadAllLines(file);
                    for (int index = 0; index < lines.Length; index++)
                    {
                        if (hits >= max)
                            break;
                        if (!regex.Match(lines[index]).Success)
                            continue;
                        output.AppendLine(
                            relative + ":" + (index + 1) + ": " + (lines[index].Length > 240 ? lines[index][..240] : lines[index]).Trim()
                        );
                        hits++;
                    }
                }
                catch (Exception) { }
            }
        }
        return hits == 0
            ? new McpToolResult("No file name or content matches " + query + " in the Voidstrap data, mods or plugin folders.")
            : new McpToolResult(
                hits + " match(es)" + (hits >= max ? ", stopped at the " + max + " limit" : "") + ":\n" + output.ToString().TrimEnd()
            );
    }

    private static McpToolResult SearchMods(JsonObject args)
    {
        string query = McpArgs.String(args, "query")?.Trim() ?? "";
        if (query.Length < 2)
            return new McpToolResult("Give a query of at least 2 characters.", true);
        Regex regex = BuildSearchRegex(query, McpArgs.Flag(args, "regex", false), false, false);
        StringBuilder output = new();
        int hits = 0;

        foreach (ManagedModLibraryEntry entry in ManagedModStore.ScanLibrary())
        {
            string summary =
                entry.Record.Name
                + " "
                + (entry.Record.Enabled ? "enabled" : "off")
                + " "
                + entry.FileCount
                + " files "
                + (entry.Pack != null ? "pack " + entry.Pack.Id : "")
                + (entry.Failure.Length > 0 ? " FAILURE " + entry.Failure : "");
            if (regex.Match(summary).Success || entry.RelativePaths.Any(path => regex.Match(path).Success))
            {
                output.AppendLine("managed mod " + summary);
                hits++;
                foreach (string path in entry.RelativePaths.Where(path => regex.Match(path).Success).Take(10))
                {
                    output.AppendLine("  file " + path);
                    hits++;
                }
            }
        }

        foreach (Voidstrap.Integrations.Plugins.PluginHost.ModInfo mod in Voidstrap.Integrations.Plugins.PluginHost.ListMods())
        {
            string summary = mod.Name + " " + mod.Version + " " + (mod.Loaded ? "loaded" : "not loaded") + " " + mod.Description;
            if (
                regex.Match(summary).Success
                || mod.Hooks.Any(hook => regex.Match(hook).Success)
                || mod.Problems.Any(problem => regex.Match(problem).Success)
            )
            {
                output.AppendLine("voidstrap mod " + summary);
                hits++;
                foreach (string problem in mod.Problems.Where(problem => regex.Match(problem).Success).Take(5))
                {
                    output.AppendLine("  problem " + problem);
                    hits++;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(Paths.Mods) && Directory.Exists(Paths.Mods))
        {
            foreach (string file in Directory.EnumerateFiles(Paths.Mods, "*", SearchOption.AllDirectories))
            {
                if (hits >= SearchHardCap)
                    break;
                string relative = Path.GetRelativePath(Paths.Mods, file);
                if (regex.Match(relative).Success)
                {
                    output.AppendLine("content mod file " + relative);
                    hits++;
                }
            }
        }

        return hits == 0
            ? new McpToolResult("No managed mod, voidstrap mod or content mod file matches " + query + ".")
            : new McpToolResult(hits + " match(es):\n" + output.ToString().TrimEnd());
    }
}
