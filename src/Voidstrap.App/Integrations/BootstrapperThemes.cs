using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

public sealed class PublishedTheme
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("verified")]
    public bool Verified { get; set; }
}

public sealed class PublishedThemeInfo
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public int Version { get; set; } = 1;

    public int Downloads { get; set; }

    public bool Verified { get; set; }

    public long UpdatedAt { get; set; }

    public long Size { get; set; }

    public int AssetCount { get; set; }

    public string? LocalFolder { get; set; }

    public ThemeDiff? Pending { get; set; }

    public string UpdatedText => UpdatedAt <= 0
        ? ""
        : DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAt).ToLocalTime().ToString("d MMM yyyy");

    public bool HasLocalCopy => !string.IsNullOrEmpty(LocalFolder);

    public bool HasChanges => Pending != null && Pending.Total > 0;

    public string State
    {
        get
        {
            if (!HasLocalCopy)
                return "Not on this PC";

            return HasChanges ? Pending!.Describe() : "Up to date";
        }
    }

    public string Facts
    {
        get
        {
            List<string> parts = new List<string>
            {
                "v" + Version,
                Downloads + (Downloads == 1 ? " download" : " downloads")
            };

            if (Verified)
                parts.Add("Verified");

            if (AssetCount > 0)
                parts.Add(AssetCount + (AssetCount == 1 ? " file" : " files"));

            if (!string.IsNullOrEmpty(UpdatedText))
                parts.Add(UpdatedText);

            return string.Join("   ", parts);
        }
    }
}

public sealed class ThemeDiff
{
    public int Added { get; set; }

    public int Changed { get; set; }

    public int Removed { get; set; }

    public bool LayoutChanged { get; set; }

    public int Total => Added + Changed + Removed + (LayoutChanged ? 1 : 0);

    public string Describe()
    {
        if (Total == 0)
            return "Up to date";

        List<string> parts = new List<string>();

        if (LayoutChanged)
            parts.Add("layout edited");

        if (Added > 0)
            parts.Add(Added + (Added == 1 ? " file added" : " files added"));

        if (Changed > 0)
            parts.Add(Changed + (Changed == 1 ? " file changed" : " files changed"));

        if (Removed > 0)
            parts.Add(Removed + (Removed == 1 ? " file removed" : " files removed"));

        return string.Join(", ", parts);
    }
}

public enum ThemeFileState
{
    Same,
    Added,
    Changed,
    Removed
}

public sealed class ThemeFileChange
{
    public string Path { get; set; } = "";

    public ThemeFileState State { get; set; }

    public string Kind { get; set; } = "binary";

    public string? LocalPath { get; set; }

    public byte[]? PublishedBytes { get; set; }

    public string LocalText { get; set; } = "";

    public string PublishedText { get; set; } = "";

    public long LocalSize { get; set; }

    public long PublishedSize { get; set; }

    public bool IsText => Kind == "text";

    public bool IsImage => Kind == "image";

    public bool IsFont => Kind == "font";

    public string StateLabel => State switch
    {
        ThemeFileState.Added => "added",
        ThemeFileState.Changed => "changed",
        ThemeFileState.Removed => "removed",
        _ => "unchanged"
    };

    public string SizeLabel
    {
        get
        {
            if (State == ThemeFileState.Removed)
                return Describe(PublishedSize) + " on the website";

            if (State == ThemeFileState.Added)
                return Describe(LocalSize) + " on this PC";

            if (State == ThemeFileState.Changed)
                return Describe(PublishedSize) + " published, " + Describe(LocalSize) + " here";

            return Describe(LocalSize);
        }
    }

    private static string Describe(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";

        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.#") + " KB";

        return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
    }
}

public sealed class ThemePublishRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;
}

public sealed class ThemeUpdateResult
{
    public int Version { get; set; } = 1;

    public bool ClearedVerification { get; set; }
}

public sealed class ThemeNotFoundException : InvalidOperationException
{
    public ThemeNotFoundException(string message) : base(message)
    {
    }
}

public static class BootstrapperThemes
{
    private const string PublishRecordFile = ".published.json";
    private const string InstallRecordFile = ".installed.json";

    private const string LogIdent = "BootstrapperThemes";

    private const long MaxArchiveBytes = 6 * 1024 * 1024;

    private const long MaxEntryBytes = 3 * 1024 * 1024;

    private const int MaxResponseBytes = 6 * 1024 * 1024;

    private const int MaxEntries = 24;

    private static readonly string[] AllowedExtensions =
    {
        ".xml",
        ".png", ".apng", ".jpg", ".jpeg", ".jfif", ".jpe", ".jff",
        ".webp", ".gif", ".bmp", ".dib", ".ico", ".cur", ".avif", ".avifs",
        ".ttf", ".otf", ".ttc",
        ".html", ".htm", ".css", ".js",
        ".mp4", ".webm", ".mov",
        ".mp3", ".wav", ".ogg", ".m4a", ".flac"
    };

    private static readonly string[] AllowedFolders = { "fonts", "images", "assets" };

    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromMinutes(3));
        client.MaxResponseContentBufferSize = MaxArchiveBytes;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap/" + App.Version);
        return client;
    }

    private static string ApiBase => App.WebsiteBaseUrl.TrimEnd('/');

    private static bool ConfirmActiveContent(string name, bool verified, List<string> activeFiles)
    {
        string listed = string.Join(", ", activeFiles.Take(8));
        if (activeFiles.Count > 8)
            listed += " and " + (activeFiles.Count - 8) + " more";

        string message = name + " includes HTML files, which can run code when the theme is used."
            + (verified
                ? " A staff member has verified this theme."
                : " This theme has not been verified, so it could be malicious.")
            + " Only install it if you trust the author."
            + "\n\nFiles: " + listed;

        System.Windows.MessageBoxResult result = Voidstrap.UI.Frontend.ShowMessageBox(
            message,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxResult.No);

        return result == System.Windows.MessageBoxResult.Yes;
    }

    public static async Task<string> InstallFromWebsiteAsync(string themeId, CancellationToken token = default, string? replaceFolder = null)
    {
        if (string.IsNullOrWhiteSpace(themeId) || !IsSafeId(themeId))
            throw new InvalidDataException("That theme link is not valid.");

        App.Logger.WriteLine(LogIdent, "Installing theme " + themeId);

        string metaUrl = ApiBase + "/api/bootstrappers/" + Uri.EscapeDataString(themeId);
        string name = themeId;
        int version = 1;
        bool verified = false;
        List<string> activeFiles = new();

        try
        {
            using HttpResponseMessage metaResponse = await _http.GetAsync(metaUrl, token).ConfigureAwait(false);
            if (metaResponse.IsSuccessStatusCode)
            {
                string metaJson = await Http.ReadStringBoundedAsync(metaResponse.Content, MaxResponseBytes, token).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(metaJson);
                if (document.RootElement.TryGetProperty("theme", out JsonElement theme))
                {
                    if (theme.TryGetProperty("name", out JsonElement themeName))
                        name = themeName.GetString() ?? themeId;

                    if (theme.TryGetProperty("version", out JsonElement themeVersion) && themeVersion.TryGetInt32(out int parsed))
                        version = parsed;

                    if (theme.TryGetProperty("verified", out JsonElement themeVerified) && themeVerified.ValueKind == JsonValueKind.True)
                        verified = true;

                    if (theme.TryGetProperty("activeFiles", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement file in files.EnumerateArray())
                        {
                            string? entry = file.GetString();
                            if (!string.IsNullOrWhiteSpace(entry))
                                activeFiles.Add(entry);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not read the theme details: " + ex.Message);
        }

        string zipUrl = ApiBase + "/api/bootstrappers/" + Uri.EscapeDataString(themeId) + "/download?count=0";

        byte[] archive;
        using (HttpResponseMessage response = await _http.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            archive = await Http.ReadBytesBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);
        }

        foreach (string activeFile in FindActiveFiles(archive))
        {
            if (!activeFiles.Contains(activeFile, StringComparer.OrdinalIgnoreCase))
                activeFiles.Add(activeFile);
        }
        if (activeFiles.Count > 0 && !ConfirmActiveContent(name, verified, activeFiles))
        {
            App.Logger.WriteLine(LogIdent, "The user cancelled installing " + themeId + " after the HTML warning");
            throw new OperationCanceledException("Install cancelled.");
        }

        string folder = Extract(archive, SafeFolderName(name), replaceFolder);
        string folderName = Path.GetFileName(folder);

        WriteInstallRecord(folderName, new ThemePublishRecord
        {
            Id = themeId,
            Name = name,
            Version = version
        });

        App.Logger.WriteLine(LogIdent, "Installed theme into " + folder + " at version " + version);
        return folderName;
    }

    public static ThemePublishRecord? ReadInstallRecord(string themeFolderName)
    {
        try
        {
            string path = Path.Combine(Paths.CustomThemes, themeFolderName, InstallRecordFile);

            if (!File.Exists(path))
                return null;

			return Voidstrap.Utility.JsonFile.Deserialize<ThemePublishRecord>(path, Voidstrap.Utility.JsonOptions.Tolerant, 1048576);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteInstallRecord(string themeFolderName, ThemePublishRecord record)
    {
        try
        {
            string path = Path.Combine(Paths.CustomThemes, themeFolderName, InstallRecordFile);
			Voidstrap.Utility.JsonFile.SerializeAtomic(path, record, Voidstrap.Utility.JsonOptions.Indented);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not save the install record: " + ex.Message);
        }
    }

    private static async Task<int> FetchRemoteVersionAsync(string themeId, CancellationToken token)
    {
        try
        {
            string url = ApiBase + "/api/bootstrappers/" + Uri.EscapeDataString(themeId);
            using HttpResponseMessage response = await _http.GetAsync(url, token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return 0;

            string body = await Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("theme", out JsonElement theme) &&
                theme.TryGetProperty("version", out JsonElement version) &&
                version.TryGetInt32(out int value))
            {
                return value;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not check " + themeId + ": " + ex.Message);
        }

        return 0;
    }

    public static async Task<int> UpdateInstalledThemesAsync(CancellationToken token = default)
    {
        if (!Directory.Exists(Paths.CustomThemes))
            return 0;

        int updated = 0;

        foreach (string folder in Directory.EnumerateDirectories(Paths.CustomThemes))
        {
            token.ThrowIfCancellationRequested();

            string folderName = Path.GetFileName(folder);
            ThemePublishRecord? record = ReadInstallRecord(folderName);

            if (record == null || string.IsNullOrEmpty(record.Id))
                continue;

            if (ReadPublishRecord(folderName) != null)
                continue;

            int remote = await FetchRemoteVersionAsync(record.Id, token).ConfigureAwait(false);

            if (remote <= record.Version)
                continue;

            try
            {
                await InstallFromWebsiteAsync(record.Id, token, folderName).ConfigureAwait(false);
                updated++;
                App.Logger.WriteLine(LogIdent, "Updated " + folderName + " from v" + record.Version + " to v" + remote);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogIdent, "Could not update " + folderName + ": " + ex.Message);
            }
        }

        if (updated > 0)
            App.Logger.WriteLine(LogIdent, "Updated " + updated + " installed themes");

        return updated;
    }

    private static bool IsSafeId(string id)
    {
        foreach (char c in id)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        }
        return id.Length <= 64;
    }

    private static string SafeFolderName(string name)
    {
        StringBuilder builder = new StringBuilder();

        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_')
                builder.Append(c);
        }

        string cleaned = builder.ToString().Trim();
        if (cleaned.Length == 0)
            cleaned = "Downloaded theme";

        return cleaned.Length > 48 ? cleaned.Substring(0, 48).Trim() : cleaned;
    }

    private static string Extract(byte[] archive, string preferredName, string? replaceFolder = null)
    {
        Directory.CreateDirectory(Paths.CustomThemes);

        string target = Path.Combine(Paths.CustomThemes, preferredName);

        if (!string.IsNullOrEmpty(replaceFolder))
        {
            target = Path.Combine(Paths.CustomThemes, replaceFolder);
        }
        else
        {
            int suffix = 2;

            while (Directory.Exists(target))
            {
                target = Path.Combine(Paths.CustomThemes, preferredName + " " + suffix);
                suffix++;
            }
        }

        string themesRoot = Path.GetFullPath(Paths.CustomThemes);
        string root = Path.GetFullPath(target);

        if (!string.Equals(Path.GetDirectoryName(root), themesRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("That theme folder is not valid.");

        string staging = Path.Combine(themesRoot, ".theme install " + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(themesRoot, ".theme backup " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        bool foundTheme = false;

        try
        {
            using MemoryStream stream = new MemoryStream(archive, writable: false);
            using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read);
            HashSet<string> extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (zip.Entries.Count > MaxEntries)
                throw new InvalidDataException("That theme contains too many files.");

            long total = 0;
            long actualTotal = 0;
            byte[] copyBuffer = new byte[65536];

            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                string relative = entry.FullName.Replace('\\', '/');

                if (relative.Contains("..", StringComparison.Ordinal) || relative.StartsWith("/", StringComparison.Ordinal) ||
                    (relative.Length > 1 && relative[1] == ':'))
                {
                    throw new InvalidDataException("That theme tried to write outside its own folder.");
                }

                string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length > 2)
                    throw new InvalidDataException("That theme has files nested too deeply.");

                if (segments.Length == 2 && !AllowedFolders.Contains(segments[0].ToLowerInvariant()))
                    throw new InvalidDataException("That theme uses an unexpected folder: " + segments[0]);

                if (!extracted.Add(relative))
                    throw new InvalidDataException("That theme contains the same file more than once: " + relative);

                string extension = Path.GetExtension(entry.Name).ToLowerInvariant();

                if (!AllowedExtensions.Contains(extension))
                    throw new InvalidDataException("That theme contains an unsupported file: " + entry.Name);

                if (entry.Length > MaxEntryBytes)
                    throw new InvalidDataException("A file in that theme is too large: " + entry.Name);

                total += entry.Length;

                if (total > MaxArchiveBytes)
                    throw new InvalidDataException("That theme is too large to install.");

                string destination = Path.GetFullPath(Path.Combine(staging, relative));

                if (!destination.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(destination, staging, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("That theme tried to write outside its own folder.");
                }

                string? parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);

                long entryBytes = 0;
                using (Stream input = entry.Open())
                using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    while (true)
                    {
                        int read = input.Read(copyBuffer, 0, copyBuffer.Length);
                        if (read == 0)
                            break;
                        entryBytes += read;
                        actualTotal += read;
                        if (entryBytes > entry.Length || actualTotal > MaxArchiveBytes)
                            throw new InvalidDataException("That theme expands beyond the size limit.");
                        output.Write(copyBuffer, 0, read);
                    }
                }
                if (entryBytes != entry.Length)
                    throw new InvalidDataException("A file in that theme has an invalid size.");

                if (string.Equals(relative, "Theme.xml", StringComparison.OrdinalIgnoreCase))
                    foundTheme = true;
            }

            if (!foundTheme)
                throw new InvalidDataException("That theme has no Theme.xml.");

            if (Directory.Exists(root))
            {
                Directory.Move(root, backup);

                try
                {
                    Directory.Move(staging, root);
                }
                catch
                {
                    Directory.Move(backup, root);
                    throw;
                }

                TryDelete(backup);
            }
            else
            {
                Directory.Move(staging, root);
            }
        }
        catch
        {
            TryDelete(staging);

            if (Directory.Exists(backup) && !Directory.Exists(root))
            {
                try
                {
                    Directory.Move(backup, root);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LogIdent, "Could not restore " + root + ": " + ex.Message);
                }
            }

            throw;
        }

        return root;
    }

    private static List<string> FindActiveFiles(byte[] archive)
    {
        using MemoryStream stream = new MemoryStream(archive, writable: false);
        using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("That theme contains too many files.");
        return zip.Entries
            .Where(entry => !string.IsNullOrEmpty(entry.Name))
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".js")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not clean up " + folder + ": " + ex.Message);
        }
    }

    public static ThemePublishRecord? ReadPublishRecord(string themeFolderName)
    {
        try
        {
            string path = Path.Combine(Paths.CustomThemes, themeFolderName, PublishRecordFile);

            if (!File.Exists(path))
                return null;

			return Voidstrap.Utility.JsonFile.Deserialize<ThemePublishRecord>(path, Voidstrap.Utility.JsonOptions.Tolerant, 1048576);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not read the publish record: " + ex.Message);
            return null;
        }
    }


    public static void ClearPublishRecord(string themeFolderName)
    {
        try
        {
            string path = Path.Combine(Paths.CustomThemes, themeFolderName, PublishRecordFile);

            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not clear the publish record: " + ex.Message);
        }
    }

    private static void WritePublishRecord(string themeFolderName, ThemePublishRecord record)
    {
        try
        {
            string path = Path.Combine(Paths.CustomThemes, themeFolderName, PublishRecordFile);
			Voidstrap.Utility.JsonFile.SerializeAtomic(path, record);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not save the publish record: " + ex.Message);
        }
    }


    private static readonly Regex _attributePath = new Regex(
        "(?<name>[A-Za-z_][\\w.:-]*)\\s*=\\s*(?<quote>[\"'])(?<value>[^\"']*)\\k<quote>",
        RegexOptions.Compiled);

    public static string NormalizeThemePaths(string xml, string themeFolder, out List<string> unresolved)
    {
        unresolved = new List<string>();

        if (string.IsNullOrEmpty(xml))
            return xml;

        string root;

        try
        {
            root = Path.GetFullPath(themeFolder).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return xml;
        }

        List<string> misses = unresolved;

        return _attributePath.Replace(xml, match =>
        {
            string value = match.Groups["value"].Value;

            if (string.IsNullOrWhiteSpace(value))
                return match.Value;

            string converted = ToThemePath(value, root, misses);

            if (converted == value)
                return match.Value;

            return match.Groups["name"].Value + "=\"" + converted + "\"";
        });
    }

    private static string ToThemePath(string value, string root, List<string> unresolved)
    {
        string candidate = value.Trim();

        if (candidate.StartsWith("theme://", StringComparison.OrdinalIgnoreCase))
            return "theme://" + candidate.Substring(8).Replace('\\', '/').TrimStart('/');

        if (candidate.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            try { candidate = Uri.UnescapeDataString(candidate.Substring(8)).Replace('/', Path.DirectorySeparatorChar); }
            catch { return value; }
        }
        else if (candidate.StartsWith("pack://application:,,,/", StringComparison.OrdinalIgnoreCase))
        {
            return "theme://" + candidate.Substring(23).Replace('\\', '/').TrimStart('/');
        }

        bool rooted;

        try { rooted = Path.IsPathRooted(candidate); }
        catch { return value; }

        if (!rooted)
            return value;

        string full;

        try { full = Path.GetFullPath(candidate); }
        catch { return value; }

        if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string relative = full.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/');
            return "theme://" + relative;
        }

        if (File.Exists(full))
            unresolved.Add(full);

        return value;
    }

    private static async Task<string> ImportOutsideFileAsync(string themeFolder, string sourceFile, CancellationToken token)
    {
        string extension = Path.GetExtension(sourceFile).ToLowerInvariant();
        string subFolder = extension is ".ttf" or ".otf" or ".ttc" ? "Fonts" : "Images";
        string targetFolder = Path.Combine(themeFolder, subFolder);

        Directory.CreateDirectory(targetFolder);

        string fileName = Path.GetFileName(sourceFile);
        string target = Path.Combine(targetFolder, fileName);
        int suffix = 2;

        while (File.Exists(target) && !FilesMatch(sourceFile, target))
        {
            target = Path.Combine(targetFolder, Path.GetFileNameWithoutExtension(fileName) + " " + suffix + extension);
            suffix++;
        }

        if (!File.Exists(target))
        {
            using FileStream source = File.OpenRead(sourceFile);
            using FileStream destination = File.Create(target);
            await source.CopyToAsync(destination, token).ConfigureAwait(false);
        }

        return subFolder + "/" + Path.GetFileName(target);
    }

    private static bool FilesMatch(string left, string right)
    {
        try
        {
            FileInfo a = new FileInfo(left);
            FileInfo b = new FileInfo(right);
            return a.Length == b.Length;
        }
        catch
        {
            return false;
        }
    }


    private static readonly Regex _safeAssetName = new Regex(
        @"^[A-Za-z0-9][A-Za-z0-9 _.()\[\]+-]{0,58}[A-Za-z0-9)\]]$",
        RegexOptions.Compiled);

    private static bool IsPublishableName(string fileName)
    {
        return _safeAssetName.IsMatch(fileName);
    }

    private static string MakePublishableName(string fileName)
    {
        string extension = Path.GetExtension(fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);

        StringBuilder builder = new StringBuilder(stem.Length);

        foreach (char character in stem)
        {
            if (char.IsLetterOrDigit(character) && character < 128)
                builder.Append(character);
            else if (character is ' ' or '_' or '.' or '(' or ')' or '[' or ']' or '+' or '-')
                builder.Append(character);
            else
                builder.Append('-');
        }

        string cleaned = builder.ToString();

        while (cleaned.Contains("--"))
            cleaned = cleaned.Replace("--", "-");

        cleaned = cleaned.Trim(' ', '.', '-', '_');

        if (cleaned.Length > 50)
            cleaned = cleaned.Substring(0, 50).Trim(' ', '.', '-', '_');

        if (cleaned.Length == 0 || !char.IsLetterOrDigit(cleaned[0]))
            cleaned = "asset" + cleaned;

        if (cleaned.Length == 0 || !char.IsLetterOrDigit(cleaned[cleaned.Length - 1]))
            cleaned += "1";

        return cleaned + extension.ToLowerInvariant();
    }

    private static string ReplacePath(string xml, string oldRelative, string newRelative)
    {
        string oldForward = oldRelative.Replace('\\', '/');
        string oldBack = oldRelative.Replace('/', '\\');
        string newForward = newRelative.Replace('\\', '/');

        xml = xml.Replace("theme://" + oldForward, "theme://" + newForward, StringComparison.OrdinalIgnoreCase);
        xml = xml.Replace("theme://" + oldBack, "theme://" + newForward, StringComparison.OrdinalIgnoreCase);
        xml = xml.Replace("\"" + oldForward + "\"", "\"" + newForward + "\"", StringComparison.OrdinalIgnoreCase);
        xml = xml.Replace("\"" + oldBack + "\"", "\"" + newForward + "\"", StringComparison.OrdinalIgnoreCase);

        return xml;
    }

    private static readonly string[] PortableImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };

    private static bool LooksLikeImage(string extension)
    {
        return extension is ".png" or ".apng" or ".jpg" or ".jpeg" or ".jfif" or ".jpe" or ".jff"
            or ".webp" or ".gif" or ".bmp" or ".dib" or ".ico" or ".cur" or ".avif" or ".avifs"
            or ".tif" or ".tiff" or ".heic" or ".heif";
    }

    private static bool TryConvertToPng(string sourceFile, string targetFile)
    {
        try
        {
            System.Windows.Media.Imaging.BitmapFrame frame;

            using (FileStream input = File.OpenRead(sourceFile))
            {
                System.Windows.Media.Imaging.BitmapDecoder decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(
                    input,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);

                if (decoder.Frames.Count == 0)
                    return false;

                frame = decoder.Frames[0];
            }

            System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));

            using FileStream output = File.Create(targetFile);
            encoder.Save(output);

            return true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Could not convert " + Path.GetFileName(sourceFile) + ": " + ex.Message);
            return false;
        }
    }

    private static string NormalizeImageFormats(string themeFolder, string xml)
    {
        if (!Directory.Exists(themeFolder))
            return xml;

        foreach (string file in Directory.EnumerateFiles(themeFolder, "*.*", SearchOption.AllDirectories).ToList())
        {
            string extension = Path.GetExtension(file).ToLowerInvariant();

            if (!LooksLikeImage(extension))
                continue;

            if (PortableImageExtensions.Contains(extension))
                continue;

            string relative = Path.GetRelativePath(themeFolder, file).Replace(Path.DirectorySeparatorChar, '/');
            string directory = Path.GetDirectoryName(file)!;
            string stem = Path.GetFileNameWithoutExtension(file);
            string target = Path.Combine(directory, stem + ".png");
            int suffix = 2;

            while (File.Exists(target))
            {
                target = Path.Combine(directory, stem + " " + suffix + ".png");
                suffix++;
            }

            if (!TryConvertToPng(file, target))
                continue;

            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogIdent, "Converted but could not remove " + relative + ": " + ex.Message);
            }

            string newRelative = Path.GetRelativePath(themeFolder, target).Replace(Path.DirectorySeparatorChar, '/');
            xml = ReplacePath(xml, relative, newRelative);

            App.Logger.WriteLine(LogIdent, "Converted " + relative + " to " + newRelative + " so every machine can read it");
        }

        return xml;
    }

    private static string RelocateStrayAssets(string themeFolder, string xml)
    {
        if (!Directory.Exists(themeFolder))
            return xml;

        foreach (string file in Directory.EnumerateFiles(themeFolder, "*.*", SearchOption.AllDirectories).ToList())
        {
            string relative = Path.GetRelativePath(themeFolder, file).Replace(Path.DirectorySeparatorChar, '/');
            string extension = Path.GetExtension(file).ToLowerInvariant();

            if (!AllowedExtensions.Contains(extension) || extension == ".xml")
                continue;

            string[] segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);

            bool tooDeep = segments.Length > 2;
            bool wrongFolder = segments.Length == 2 && !AllowedFolders.Contains(segments[0].ToLowerInvariant());

            if (!tooDeep && !wrongFolder)
                continue;

            string subFolder = extension is ".ttf" or ".otf" or ".ttc" ? "Fonts" : "Images";
            string targetFolder = Path.Combine(themeFolder, subFolder);

            try
            {
                Directory.CreateDirectory(targetFolder);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogIdent, "Could not create " + subFolder + ": " + ex.Message);
                continue;
            }

            string fileName = Path.GetFileName(file);
            string target = Path.Combine(targetFolder, fileName);
            int suffix = 2;

            while (File.Exists(target) && !string.Equals(target, file, StringComparison.OrdinalIgnoreCase))
            {
                target = Path.Combine(targetFolder, Path.GetFileNameWithoutExtension(fileName) + " " + suffix + extension);
                suffix++;
            }

            try
            {
                if (!string.Equals(target, file, StringComparison.OrdinalIgnoreCase))
                    File.Move(file, target);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogIdent, "Could not move " + relative + ": " + ex.Message);
                continue;
            }

            string newRelative = subFolder + "/" + Path.GetFileName(target);
            xml = ReplacePath(xml, relative, newRelative);

            App.Logger.WriteLine(LogIdent, "Moved " + relative + " to " + newRelative + " so it ships with the theme");
        }

        return xml;
    }

    private static string SanitizeAssetNames(string themeFolder, string xml)
    {
        if (!Directory.Exists(themeFolder))
            return xml;

        foreach (string file in Directory.EnumerateFiles(themeFolder, "*.*", SearchOption.AllDirectories).ToList())
        {
            string relative = Path.GetRelativePath(themeFolder, file).Replace(Path.DirectorySeparatorChar, '/');
            string fileName = Path.GetFileName(file);

            if (string.Equals(fileName, "Theme.xml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(fileName, PublishRecordFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, InstallRecordFile, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!AllowedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                continue;

            if (IsPublishableName(fileName))
                continue;

            string safeName = MakePublishableName(fileName);
            string directory = Path.GetDirectoryName(file)!;
            string target = Path.Combine(directory, safeName);
            int suffix = 2;

            while (File.Exists(target) && !string.Equals(target, file, StringComparison.OrdinalIgnoreCase))
            {
                safeName = Path.GetFileNameWithoutExtension(MakePublishableName(fileName)) + " " + suffix + Path.GetExtension(file).ToLowerInvariant();
                target = Path.Combine(directory, safeName);
                suffix++;
            }

            try
            {
                File.Move(file, target);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogIdent, "Could not rename " + relative + ": " + ex.Message);
                continue;
            }

            string newRelative = Path.GetRelativePath(themeFolder, target).Replace(Path.DirectorySeparatorChar, '/');
            xml = ReplacePath(xml, relative, newRelative);

            App.Logger.WriteLine(LogIdent, "Renamed " + relative + " to " + newRelative + " so it can be published");
        }

        return xml;
    }

    public static async Task<string> PrepareThemeXmlAsync(string themeFolderName, string xml, CancellationToken token)
    {
        string folder = Path.Combine(Paths.CustomThemes, themeFolderName);

        string normalized = NormalizeThemePaths(xml, folder, out List<string> outside);

        foreach (string file in outside.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string relative = await ImportOutsideFileAsync(folder, file, token).ConfigureAwait(false);
                normalized = normalized.Replace(file, "theme://" + relative, StringComparison.OrdinalIgnoreCase);

                string uri = "file:///" + file.Replace('\\', '/');
                normalized = normalized.Replace(uri, "theme://" + relative, StringComparison.OrdinalIgnoreCase);

                App.Logger.WriteLine(LogIdent, "Imported " + file + " into the theme as " + relative);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogIdent, "Could not import " + file + ": " + ex.Message);
            }
        }

        normalized = RelocateStrayAssets(folder, normalized);
        normalized = NormalizeImageFormats(folder, normalized);
        normalized = SanitizeAssetNames(folder, normalized);

        if (!string.Equals(normalized, xml, StringComparison.Ordinal))
        {
            try
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "Theme.xml"), normalized, token).ConfigureAwait(false);
                App.Logger.WriteLine(LogIdent, "Rewrote absolute paths in " + themeFolderName + " as theme:// paths");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogIdent, "Could not save the tidied theme: " + ex.Message);
            }
        }

        return normalized;
    }

    private static async Task<(string xml, List<object> assets)> ReadThemeAsync(string themeFolderName, CancellationToken token)
    {
        string folder = Path.Combine(Paths.CustomThemes, themeFolderName);
        string themeFile = Path.Combine(folder, "Theme.xml");

        if (!File.Exists(themeFile))
            throw new FileNotFoundException("That theme has no Theme.xml to publish.");

        string xml = await File.ReadAllTextAsync(themeFile, token).ConfigureAwait(false);
        xml = await PrepareThemeXmlAsync(themeFolderName, xml, token).ConfigureAwait(false);

        List<object> assets = new List<object>();
        long total = Encoding.UTF8.GetByteCount(xml);

        foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');

            if (string.Equals(relative, "Theme.xml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(relative, PublishRecordFile, StringComparison.OrdinalIgnoreCase))
                continue;

            string extension = Path.GetExtension(file).ToLowerInvariant();

            if (!AllowedExtensions.Contains(extension) || extension == ".xml")
                continue;

            string[] segments = relative.Split('/');

            if (segments.Length > 2 || (segments.Length == 2 && !AllowedFolders.Contains(segments[0].ToLowerInvariant())))
                continue;

            FileInfo info = new FileInfo(file);

            if (info.Length > MaxEntryBytes)
                throw new InvalidDataException("The file " + relative + " is larger than 1MB.");

            total += info.Length;

            if (total > MaxArchiveBytes)
                throw new InvalidDataException("That theme is larger than 2MB in total.");

            byte[] bytes = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false);
            assets.Add(new { path = relative, data = Convert.ToBase64String(bytes) });
        }

        return (xml, assets);
    }


    public static async Task<List<PublishedThemeInfo>> GetMineAsync(CancellationToken token = default)
    {
        string authToken = RequireToken();

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/api/bootstrappers/mine");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        using JsonDocument document = await SendAsync(request, token).ConfigureAwait(false);

        List<PublishedThemeInfo> list = new List<PublishedThemeInfo>();

        if (!document.RootElement.TryGetProperty("themes", out JsonElement themes) || themes.ValueKind != JsonValueKind.Array)
            return list;

        foreach (JsonElement entry in themes.EnumerateArray())
        {
            PublishedThemeInfo info = new PublishedThemeInfo
            {
                Id = entry.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "" : "",
                Name = entry.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "",
                Description = entry.TryGetProperty("description", out JsonElement description) ? description.GetString() ?? "" : "",
                Version = entry.TryGetProperty("version", out JsonElement version) && version.TryGetInt32(out int v) ? v : 1,
                Downloads = entry.TryGetProperty("downloads", out JsonElement downloads) && downloads.TryGetInt32(out int d) ? d : 0,
                Verified = entry.TryGetProperty("verified", out JsonElement verified) && verified.ValueKind == JsonValueKind.True,
                UpdatedAt = entry.TryGetProperty("updatedAt", out JsonElement updated) && updated.TryGetInt64(out long u) ? u : 0,
                Size = entry.TryGetProperty("size", out JsonElement size) && size.TryGetInt64(out long sz) ? sz : 0,
                AssetCount = entry.TryGetProperty("assetCount", out JsonElement assets) && assets.TryGetInt32(out int a) ? a : 0
            };

            info.LocalFolder = FindLocalFolder(info.Id);

            if (!string.IsNullOrEmpty(info.LocalFolder))
            {
                try
                {
                    info.Pending = await CompareWithPublishedAsync(info.LocalFolder!, info.Id, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LogIdent, "Could not compare " + info.Id + ": " + ex.Message);
                }
            }

            list.Add(info);
        }

        App.Logger.WriteLine(LogIdent, "Loaded " + list.Count + " published themes");
        return list;
    }

    public static string? FindLocalFolder(string themeId)
    {
        if (string.IsNullOrEmpty(themeId) || !Directory.Exists(Paths.CustomThemes))
            return null;

        foreach (string folder in Directory.EnumerateDirectories(Paths.CustomThemes))
        {
            string folderName = Path.GetFileName(folder);

            ThemePublishRecord? published = ReadPublishRecord(folderName);
            if (published != null && string.Equals(published.Id, themeId, StringComparison.OrdinalIgnoreCase))
                return folderName;

            ThemePublishRecord? installed = ReadInstallRecord(folderName);
            if (installed != null && string.Equals(installed.Id, themeId, StringComparison.OrdinalIgnoreCase))
                return folderName;
        }

        return null;
    }

    public static async Task<ThemeDiff> CompareWithPublishedAsync(string themeFolderName, string themeId, CancellationToken token = default)
    {
        ThemeDiff diff = new ThemeDiff();

        string url = ApiBase + "/api/bootstrappers/" + Uri.EscapeDataString(themeId) + "/files";

        using HttpResponseMessage response = await _http.GetAsync(url, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return diff;

        string body = await Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(body);

        Dictionary<string, string> remote = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string remoteXmlHash = "";

        if (document.RootElement.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement file in files.EnumerateArray())
            {
                string path = file.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";
                string sha = file.TryGetProperty("sha", out JsonElement h) ? h.GetString() ?? "" : "";

                if (string.IsNullOrEmpty(path))
                    continue;

                if (string.Equals(path, "Theme.xml", StringComparison.OrdinalIgnoreCase))
                {
                    remoteXmlHash = sha;
                    continue;
                }

                remote[path] = sha;
            }
        }

        string folder = Path.Combine(Paths.CustomThemes, themeFolderName);

        if (!Directory.Exists(folder))
            return diff;

        string themeFile = Path.Combine(folder, "Theme.xml");

        if (File.Exists(themeFile))
        {
            string localXml = await File.ReadAllTextAsync(themeFile, token).ConfigureAwait(false);
            diff.LayoutChanged = !string.Equals(HashText(localXml), remoteXmlHash, StringComparison.OrdinalIgnoreCase);
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');

            if (string.Equals(relative, "Theme.xml", StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(relative, PublishRecordFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relative, InstallRecordFile, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!AllowedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                continue;

            relative = PublishedRelativePath(relative);
            seen.Add(relative);

            if (!remote.TryGetValue(relative, out string? remoteHash))
            {
                diff.Added++;
                continue;
            }

            string localHash = await HashFileAsync(file, token).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(remoteHash) && !string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                diff.Changed++;
        }

        foreach (string path in remote.Keys)
        {
            if (!seen.Contains(path))
                diff.Removed++;
        }

        return diff;
    }


    private static string KindFor(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".xml")
            return "text";

        if (extension is ".png" or ".apng" or ".jpg" or ".jpeg" or ".jfif" or ".jpe" or ".jff"
            or ".webp" or ".gif" or ".bmp" or ".dib" or ".ico" or ".cur" or ".avif" or ".avifs")
            return "image";

        if (extension is ".ttf" or ".otf" or ".ttc")
            return "font";

        return "binary";
    }

    public static async Task<List<ThemeFileChange>> LoadChangesAsync(string themeFolderName, string themeId, CancellationToken token = default)
    {
        Dictionary<string, ThemeFileChange> map = new Dictionary<string, ThemeFileChange>(StringComparer.OrdinalIgnoreCase);

        string url = ApiBase + "/api/bootstrappers/" + Uri.EscapeDataString(themeId) + "/files?data=1";

        using (HttpResponseMessage response = await _http.GetAsync(url, token).ConfigureAwait(false))
        {
            if (response.IsSuccessStatusCode)
            {
                string body = await Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(body);

                if (document.RootElement.TryGetProperty("files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement file in files.EnumerateArray())
                    {
                        string path = file.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "" : "";

                        if (string.IsNullOrEmpty(path))
                            continue;

                        ThemeFileChange entry = new ThemeFileChange
                        {
                            Path = path,
                            Kind = KindFor(path),
                            State = ThemeFileState.Removed,
                            PublishedSize = file.TryGetProperty("size", out JsonElement sz) && sz.TryGetInt64(out long size) ? size : 0
                        };

                        if (file.TryGetProperty("content", out JsonElement content))
                            entry.PublishedText = content.GetString() ?? "";

                        if (file.TryGetProperty("data", out JsonElement data))
                        {
                            try { entry.PublishedBytes = Convert.FromBase64String(data.GetString() ?? ""); }
                            catch { entry.PublishedBytes = null; }
                        }

                        map[path] = entry;
                    }
                }
            }
        }

        string folder = Path.Combine(Paths.CustomThemes, themeFolderName);

        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');

                if (string.Equals(relative, PublishRecordFile, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relative, InstallRecordFile, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!AllowedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                    continue;

                string publishedName = PublishedRelativePath(relative);

                if (!map.TryGetValue(publishedName, out ThemeFileChange? entry))
                {
                    entry = new ThemeFileChange
                    {
                        Path = publishedName,
                        Kind = KindFor(publishedName),
                        State = ThemeFileState.Added
                    };
                    map[publishedName] = entry;
                }

                entry.LocalPath = file;
                entry.LocalSize = new FileInfo(file).Length;

                if (entry.IsText)
                {
                    entry.LocalText = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);

                    if (entry.State != ThemeFileState.Added)
                    {
                        entry.State = string.Equals(
                            entry.LocalText.Replace("\r\n", "\n").TrimEnd(),
                            entry.PublishedText.Replace("\r\n", "\n").TrimEnd(),
                            StringComparison.Ordinal)
                            ? ThemeFileState.Same
                            : ThemeFileState.Changed;
                    }
                }
                else if (entry.State != ThemeFileState.Added)
                {
                    string localHash = await HashFileAsync(file, token).ConfigureAwait(false);
                    string publishedHash = entry.PublishedBytes != null ? HashBytes(entry.PublishedBytes) : "";

                    entry.State = !string.IsNullOrEmpty(publishedHash) && string.Equals(localHash, publishedHash, StringComparison.OrdinalIgnoreCase)
                        ? ThemeFileState.Same
                        : ThemeFileState.Changed;
                }
            }
        }

        List<ThemeFileChange> result = map.Values.ToList();

        result.Sort((left, right) =>
        {
            int rank(ThemeFileChange item) => item.State == ThemeFileState.Same ? 1 : 0;
            int byRank = rank(left).CompareTo(rank(right));
            return byRank != 0 ? byRank : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    public static async Task<List<ThemeFileChange>> LoadLocalFilesAsync(string themeFolderName, CancellationToken token = default)
    {
        List<ThemeFileChange> result = new List<ThemeFileChange>();
        string folder = Path.Combine(Paths.CustomThemes, themeFolderName);

        if (!Directory.Exists(folder))
            return result;

        foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');

            if (string.Equals(relative, PublishRecordFile, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(relative, InstallRecordFile, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!AllowedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                continue;

            ThemeFileChange entry = new ThemeFileChange
            {
                Path = relative,
                Kind = KindFor(relative),
                State = ThemeFileState.Same,
                LocalPath = file,
                LocalSize = new FileInfo(file).Length
            };

            if (entry.IsText)
            {
                entry.LocalText = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
                entry.PublishedText = entry.LocalText;
            }

            result.Add(entry);
        }

        result.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static string PublishedRelativePath(string relative)
    {
        int slash = relative.LastIndexOf('/');
        string folder = slash < 0 ? "" : relative.Substring(0, slash + 1);
        string fileName = slash < 0 ? relative : relative.Substring(slash + 1);

        if (IsPublishableName(fileName))
            return relative;

        return folder + MakePublishableName(fileName);
    }

    private static string HashText(string text)
    {
        return HashBytes(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
            return HashBytes(bytes);
        }
        catch
        {
            return "";
        }
    }

    private static string HashBytes(byte[] bytes)
    {
        byte[] digest = System.Security.Cryptography.SHA256.HashData(bytes);
        StringBuilder builder = new StringBuilder(32);

        for (int i = 0; i < 16; i++)
            builder.Append(digest[i].ToString("x2"));

        return builder.ToString();
    }

    private static string RequireToken()
    {
        string? authToken = WebsiteAuth.GetToken();

        if (string.IsNullOrWhiteSpace(authToken))
            throw new InvalidOperationException("Sign in to your Voidstrap account first, from the Home page");

        return authToken;
    }

    private static async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        App.Logger.WriteLine(LogIdent, request.Method + " " + request.RequestUri);

        HttpResponseMessage response;

        try
        {
            response = await _http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("The website took too long to answer. Themes with images take longer because every image is screened before it goes live. Try again in a moment");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Could not reach " + ApiBase + ". " + ex.Message);
        }

        using (response)
        {
        string body = await Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch
        {
            throw new InvalidOperationException("The website returned an unexpected response.");
        }

        if (response.IsSuccessStatusCode)
            return document;

        string message = document.RootElement.TryGetProperty("error", out JsonElement error)
            ? error.GetString() ?? "The request failed."
            : "The request failed.";

        document.Dispose();

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new ThemeNotFoundException("This theme is no longer on the website.");

        throw new InvalidOperationException(message);
        }
    }

    public static async Task<ThemeUpdateResult> UpdateAsync(string themeFolderName, string themeId, string displayName, string description, string note, CancellationToken token = default)
    {
        string authToken = RequireToken();
        var (xml, assets) = await ReadThemeAsync(themeFolderName, token).ConfigureAwait(false);

        string payload = JsonSerializer.Serialize(new
        {
            name = displayName,
            description = description ?? "",
            note = note ?? "",
            xml = xml,
            assets = assets
        });

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Patch, ApiBase + "/api/bootstrappers/" + Uri.EscapeDataString(themeId))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        using JsonDocument document = await SendAsync(request, token).ConfigureAwait(false);

        ThemeUpdateResult result = new ThemeUpdateResult();

        if (document.RootElement.TryGetProperty("clearedVerification", out JsonElement cleared))
            result.ClearedVerification = cleared.GetBoolean();

        if (document.RootElement.TryGetProperty("theme", out JsonElement theme) &&
            theme.TryGetProperty("version", out JsonElement version))
        {
            result.Version = version.GetInt32();
        }

        WritePublishRecord(themeFolderName, new ThemePublishRecord
        {
            Id = themeId,
            Name = displayName,
            Description = description,
            Version = result.Version
        });

        App.Logger.WriteLine(LogIdent, "Updated theme " + themeId + " to version " + result.Version);
        return result;
    }

    public static async Task<PublishedTheme> PublishAsync(string themeFolderName, string displayName, string description, CancellationToken token = default)
    {
        string authToken = RequireToken();
        var (xml, assets) = await ReadThemeAsync(themeFolderName, token).ConfigureAwait(false);

        string payload = JsonSerializer.Serialize(new
        {
            name = displayName,
            description = description ?? "",
            xml = xml,
            assets = assets
        });

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/api/bootstrappers")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        using JsonDocument document = await SendAsync(request, token).ConfigureAwait(false);

        PublishedTheme published = new PublishedTheme();

        if (document.RootElement.TryGetProperty("theme", out JsonElement theme))
        {
            published.Id = theme.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? "" : "";
            published.Name = theme.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "" : "";
            published.Verified = theme.TryGetProperty("verified", out JsonElement verified) && verified.GetBoolean();
        }

        WritePublishRecord(themeFolderName, new ThemePublishRecord
        {
            Id = published.Id,
            Name = displayName,
            Description = description,
            Version = 1
        });

        App.Logger.WriteLine(LogIdent, "Published theme " + published.Id);
        return published;
    }
}
