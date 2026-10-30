using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Voidstrap.Utility;

internal static class AppSourceStore
{
    private const string LogIdent = "AppSourceStore";

    private const string ResourceName = "Voidstrap.AppSource";

    private const int MaxSearchResults = 80;

    private const long MaxSearchFileBytes = 1024 * 1024;

    private static readonly object Gate = new();

    private static ZipArchive? _archive;

    private static Dictionary<string, ZipArchiveEntry>? _entries;

    private static string OverridesRoot => Path.Combine(Paths.Data, "AppCode", "Overrides");

    public static bool Available
    {
        get
        {
            lock (Gate)
            {
                return Load().Count > 0;
            }
        }
    }

    private static Dictionary<string, ZipArchiveEntry> Load()
    {
        if (_entries != null)
            return _entries;
        try
        {
            Stream? stream = typeof(AppSourceStore).Assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                string? external = new[]
                {
                    Path.Combine(Paths.Data, "AppSource.zip"),
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AppSource.zip")
                }.FirstOrDefault(File.Exists);

                if (external != null)
                    stream = File.OpenRead(external);
            }
            if (stream == null)
            {
                App.Logger?.WriteLine(LogIdent, "This build does not carry the app source list");
                _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                return _entries;
            }
            _archive = new ZipArchive(stream, ZipArchiveMode.Read);
            Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.OrdinalIgnoreCase);
            foreach (ZipArchiveEntry entry in _archive.Entries)
            {
                if (entry.FullName.Length > 0)
                    entries[entry.FullName] = entry;
            }
            _entries = entries;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The app source list could not be read: " + ex.Message);
            _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        }
        return _entries;
    }

    public static string? NormalizePath(string path)
    {
        string normalized = (path ?? "").Trim().Replace('\\', '/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized))
            return null;
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part is "." or "..")
                return null;
        }
        return parts.Length == 0 ? null : string.Join('/', parts);
    }

    public static IReadOnlyList<string> ListAll()
    {
        lock (Gate)
            return [.. Load().Keys.Order(StringComparer.OrdinalIgnoreCase)];
    }

    public static IReadOnlyList<(string Name, bool IsFolder)> ListFolder(string? folder)
    {
        string prefix = string.IsNullOrWhiteSpace(folder) ? "" : (NormalizePath(folder) ?? "").TrimEnd('/') + "/";
        if (prefix == "/")
            prefix = "";
        lock (Gate)
        {
            HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
            List<string> files = [];
            foreach (string path in Load().Keys)
            {
                if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                string rest = path[prefix.Length..];
                int slash = rest.IndexOf('/');
                if (slash < 0)
                {
                    files.Add(rest);
                }
                else
                {
                    folders.Add(rest[..slash]);
                }
            }
            List<(string, bool)> result = [.. folders.Order(StringComparer.OrdinalIgnoreCase).Select(name => (name, true))];
            result.AddRange(files.Order(StringComparer.OrdinalIgnoreCase).Select(name => (name, false)));
            return result;
        }
    }

    public static bool Exists(string path)
    {
        string? normalized = NormalizePath(path);
        if (normalized == null)
            return false;
        lock (Gate)
            return Load().ContainsKey(normalized);
    }

    public static bool HasOverride(string path)
    {
        string? normalized = NormalizePath(path);
        if (normalized == null)
            return false;
        return File.Exists(Path.Combine(OverridesRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
    }

    public static IReadOnlyList<string> ListOverrides()
    {
        try
        {
            if (!Directory.Exists(OverridesRoot))
                return [];
            return [.. Directory.EnumerateFiles(OverridesRoot, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(OverridesRoot, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The edited files could not be listed: " + ex.Message);
            return [];
        }
    }

    public static string? Read(string path)
    {
        string? normalized = NormalizePath(path);
        if (normalized == null)
            return null;
        string overrideFile = Path.Combine(OverridesRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (File.Exists(overrideFile))
                return File.ReadAllText(overrideFile);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The edited copy could not be read: " + ex.Message);
        }
        lock (Gate)
        {
            if (!Load().TryGetValue(normalized, out ZipArchiveEntry? entry))
                return null;
            using StreamReader reader = new(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }

    public static void Write(string path, string content)
    {
        string? normalized = NormalizePath(path);
        if (normalized == null)
            throw new InvalidDataException("The file path must be a relative path inside the app source");
        string overrideFile = Path.Combine(OverridesRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        string? folder = Path.GetDirectoryName(overrideFile);
        if (string.IsNullOrEmpty(folder))
            throw new InvalidDataException("The file path is invalid");
        Directory.CreateDirectory(folder);
        string temporary = overrideFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, overrideFile, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
        App.Logger?.WriteLine(LogIdent, "Saved an edited copy of " + normalized);
    }

    public static bool Reset(string path)
    {
        string? normalized = NormalizePath(path);
        if (normalized == null)
            return false;
        string overrideFile = Path.Combine(OverridesRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(overrideFile))
                return false;
            File.Delete(overrideFile);
            App.Logger?.WriteLine(LogIdent, "Removed the edited copy of " + normalized);
            return true;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The edited copy could not be removed: " + ex.Message);
            return false;
        }
    }

    public static IReadOnlyList<string> Search(string query, int max = MaxSearchResults)
    {
        string needle = (query ?? "").Trim();
        if (needle.Length < 2)
            return [];
        List<string> results = [];
        Dictionary<string, ZipArchiveEntry> entries;
        lock (Gate)
            entries = new Dictionary<string, ZipArchiveEntry>(Load(), StringComparer.OrdinalIgnoreCase);
        foreach (string path in entries.Keys.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (results.Count >= max)
                break;
            if (path.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(path + " (file name)");
            }
            if (results.Count >= max)
                continue;
            try
            {
                ZipArchiveEntry entry = entries[path];
                if (entry.Length > MaxSearchFileBytes)
                    continue;
                string[] lines;
                using (StreamReader reader = new(entry.Open(), Encoding.UTF8))
                    lines = reader.ReadToEnd().Split('\n');
                for (int index = 0; index < lines.Length; index++)
                {
                    if (lines[index].Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        string snippet = lines[index].Trim();
                        if (snippet.Length > 240)
                            snippet = snippet[..240];
                        results.Add(path + ":" + (index + 1) + ": " + snippet);
                        if (results.Count >= max)
                            break;
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        return results;
    }

    public static int Export(string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder))
            throw new InvalidDataException("Choose a folder to export the app source into");
        string fullDestination = Path.GetFullPath(destinationFolder);
        Dictionary<string, ZipArchiveEntry> entries;
        lock (Gate)
            entries = new Dictionary<string, ZipArchiveEntry>(Load(), StringComparer.OrdinalIgnoreCase);
        if (entries.Count == 0)
            throw new InvalidDataException("This build does not carry the app source list");
        int written = 0;
        foreach (string path in entries.Keys)
        {
            string target = Path.Combine(fullDestination, path.Replace('/', Path.DirectorySeparatorChar));
            string? folder = Path.GetDirectoryName(target);
            if (folder == null)
                continue;
            Directory.CreateDirectory(folder);
            string? content = Read(path);
            if (content == null)
                continue;
            File.WriteAllText(target, content);
            written++;
        }
        App.Logger?.WriteLine(LogIdent, "Exported " + written + " app source files to " + fullDestination);
        return written;
    }
}
