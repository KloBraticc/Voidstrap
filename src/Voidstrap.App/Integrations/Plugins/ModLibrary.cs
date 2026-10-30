using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Plugins;

public static partial class ModLibrary
{
    public const string Repository = "KloBraticc/voidstrap-mods-library";

    public const string RepositoryUrl = "https://github.com/" + Repository;

    private const string IndexUrl = "https://raw.githubusercontent.com/" + Repository + "/index/index.json";

    private const string RawPrefix = "https://raw.githubusercontent.com/" + Repository + "/";

    private const string LogIdent = "ModLibrary";

    private const long MaxIndexBytes = 8L * 1024 * 1024;

    private const long MaxModBytes = 20L * 1024 * 1024;

    private const long MaxFileBytes = 256L * 1024;

    private const long MaxMediaBytes = 16L * 1024 * 1024;

    private const int MaxFiles = 64;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Client = CreateClient();

    private static readonly SemaphoreSlim IndexGate = new(1, 1);

    private static Index? _cached;

    private static DateTime _cachedAt;

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex CommitPattern { get; }

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern { get; }

    [GeneratedRegex("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+){0,3}$")]
    private static partial Regex PathPattern { get; }

    [GeneratedRegex("^[A-Za-z0-9-]{1,39}$")]
    private static partial Regex LoginPattern { get; }

    public sealed record LibraryFile(string Path, long Size, string Sha256);

    public sealed record LibraryMod(
        string Id,
        string Name,
        long OwnerId,
        string Login,
        string Version,
        string Description,
        IReadOnlyList<string> Tags,
        IReadOnlyList<string> Network,
        bool HasIcon,
        DateTime? Created,
        DateTime? Updated,
        bool Verified,
        bool Trusted,
        int Featured,
        IReadOnlyList<LibraryFile> Files,
        IReadOnlyList<string> Permissions
    )
    {
        public bool UsesRobloxAccount => Permissions.Contains(PluginManifest.RobloxAccountPermission, StringComparer.Ordinal);
    }

    public sealed record Index(string Commit, IReadOnlyList<LibraryMod> Mods, IReadOnlyDictionary<string, string> Blocked);

    public enum InstallState
    {
        NotInstalled,
        Installed,
        UpdateAvailable,
        NameTaken,
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new(
            new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
        return client;
    }

    public static async Task<Index> GetIndexAsync(bool refresh, CancellationToken token)
    {
        await IndexGate.WaitAsync(token);
        try
        {
            if (!refresh && _cached != null && DateTime.UtcNow - _cachedAt < CacheLifetime)
                return _cached;
            byte[] bytes = await DownloadAsync(IndexUrl + "?t=" + DateTime.UtcNow.Ticks, MaxIndexBytes, token);
            Index index = ParseIndex(bytes);
            _cached = index;
            _cachedAt = DateTime.UtcNow;
            App.Logger.WriteLine(LogIdent, "Loaded " + index.Mods.Count + " library mods at " + index.Commit[..12]);
            return index;
        }
        finally
        {
            IndexGate.Release();
        }
    }

    private static Index ParseIndex(byte[] bytes)
    {
        JsonObject root = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("the library index is not a JSON object");
        if (root["schema"]?.GetValue<int>() is not (1 or 2))
            throw new InvalidDataException("the library index has an unknown format, update Voidstrap");
        if (!string.Equals(root["repository"]?.GetValue<string>(), Repository, StringComparison.Ordinal))
            throw new InvalidDataException("the library index belongs to another repository");
        string commit = root["commit"]?.GetValue<string>() ?? "";
        if (!CommitPattern.IsMatch(commit))
            throw new InvalidDataException("the library index has no valid commit");
        List<LibraryMod> mods = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonNode? node in root["mods"] as JsonArray ?? [])
        {
            try
            {
                LibraryMod mod = ParseMod(node as JsonObject ?? throw new InvalidDataException("not an object"));
                if (names.Add(mod.Name))
                    mods.Add(mod);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException or JsonException)
            {
                App.Logger.WriteLine(LogIdent, "Skipped an invalid library entry: " + ex.Message);
            }
        }
        Dictionary<string, string> blocked = new(StringComparer.Ordinal);
        foreach (JsonNode? node in root["blocked"] as JsonArray ?? [])
        {
            string id = node?["id"]?.GetValue<string>() ?? "";
            string reason = (node?["reason"]?.GetValue<string>() ?? "").Trim();
            if (id.Length is > 0 and < 120)
                blocked[id] = reason.Length > 300 ? reason[..300] : reason;
        }
        return new Index(
            commit,
            mods.Where(m => !blocked.ContainsKey(m.Id)).OrderBy(m => m.Name, StringComparer.Ordinal).ToList(),
            blocked
        );
    }

    private static LibraryMod ParseMod(JsonObject node)
    {
        string name = node["name"]?.GetValue<string>() ?? "";
        long ownerId = node["ownerId"]?.GetValue<long>() ?? 0;
        string login = node["login"]?.GetValue<string>() ?? "";
        string version = node["version"]?.GetValue<string>() ?? "";
        if (!ModFiles.IsValidName(name) || ownerId <= 0 || !LoginPattern.IsMatch(login) || !Version.TryParse(version, out _))
            throw new InvalidDataException("bad identity for " + name);
        if (!string.Equals(node["id"]?.GetValue<string>(), ownerId + "/" + name, StringComparison.Ordinal))
            throw new InvalidDataException("bad id for " + name);
        List<LibraryFile> files = [];
        long total = 0;
        foreach (JsonNode? fileNode in node["files"] as JsonArray ?? [])
        {
            string path = fileNode?["path"]?.GetValue<string>() ?? "";
            long size = fileNode?["size"]?.GetValue<long>() ?? -1;
            string hash = fileNode?["sha256"]?.GetValue<string>() ?? "";
            long limit = Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp"
                ? MaxMediaBytes
                : MaxFileBytes;
            if (
                !PathPattern.IsMatch(path)
                || path.Split('/').Any(p => p is "." or "..")
                || size < 0
                || size > limit
                || !HashPattern.IsMatch(hash)
            )
                throw new InvalidDataException("bad file entry in " + name);
            total += size;
            files.Add(new LibraryFile(path, size, hash));
        }
        if (files.Count == 0 || files.Count > MaxFiles || total > MaxModBytes)
            throw new InvalidDataException(name + " is outside the size limits");
        if (!files.Any(f => f.Path == PluginManifest.ManifestFolder + "/plugin.json") || !files.Any(f => f.Path == "hooks/hooks.json"))
            throw new InvalidDataException(name + " is missing required files");
        List<string> network = (node["network"] as JsonArray ?? [])
            .Select(h => h?.GetValue<string>() ?? "")
            .Where(h => Uri.CheckHostName(h) == UriHostNameType.Dns)
            .ToList();
        string description = node["description"]?.GetValue<string>() ?? "";
        if (description.Length > 300)
            description = description[..300];
        List<string> tags = (node["tags"] as JsonArray ?? [])
            .Select(t => t?.GetValue<string>() ?? "")
            .Where(t => t.Length is > 0 and <= 32 && t.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            .Distinct()
            .Take(5)
            .ToList();
        int featured = Math.Clamp(node["featured"]?.GetValue<int>() ?? 0, 0, 100);
        return new LibraryMod(
            ownerId + "/" + name,
            name,
            ownerId,
            login,
            version,
            description,
            tags,
            network,
            files.Any(f => f.Path == ModFiles.IconFile),
            ParseDate(node["created"]),
            ParseDate(node["updated"]),
            node["verified"]?.GetValue<bool>() == true,
            node["trusted"]?.GetValue<bool>() == true,
            featured,
            files,
            (node["permissions"] as JsonArray ?? [])
                .Select(x => x?.GetValue<string>() ?? "")
                .Where(x => x == PluginManifest.RobloxAccountPermission)
                .Distinct()
                .ToList()
        );
    }

    private static DateTime? ParseDate(JsonNode? node)
    {
        return
            node is JsonValue value
            && value.TryGetValue(out string? text)
            && DateTime.TryParse(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime date
            )
            ? date
            : null;
    }

    public static string? GetInstalledLibraryId(string name)
    {
        try
        {
            string manifestPath = Path.Combine(Paths.Plugins, name, PluginManifest.ManifestFolder, "plugin.json");
            return File.Exists(manifestPath)
                ? ((JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject)?["library"] as JsonObject)?["id"]?.GetValue<string>()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyList<(string Name, string Reason)> DisableTakenDown(Index index)
    {
        List<(string, string)> disabled = [];
        foreach (PluginHost.ModInfo mod in PluginHost.ListMods())
        {
            if (GetInstalledLibraryId(mod.Name) is not { } id || !index.Blocked.TryGetValue(id, out string? reason))
                continue;
            if (mod.Consent == "on")
            {
                PluginHost.SetEnabled(mod.Name, false);
                disabled.Add((mod.Name, reason));
                App.Logger.WriteLine(LogIdent, "Turned off " + mod.Name + " because the library took it down");
            }
        }
        return disabled;
    }

    public static (long OwnerId, string Version)? GetInstalled(string name)
    {
        try
        {
            string manifestPath = Path.Combine(Paths.Plugins, name, PluginManifest.ManifestFolder, "plugin.json");
            if (!File.Exists(manifestPath))
                return null;
            JsonObject? manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            long ownerId = (manifest?["library"] as JsonObject)?["ownerId"]?.GetValue<long>() ?? 0;
            return (ownerId, manifest?["version"]?.GetValue<string>() ?? "0.0.0");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return (0, "0.0.0");
        }
    }

    public static InstallState GetState(LibraryMod mod)
    {
        if (GetInstalled(mod.Name) is not { } installed)
            return InstallState.NotInstalled;
        if (installed.OwnerId != mod.OwnerId)
            return InstallState.NameTaken;
        return
            Version.TryParse(installed.Version, out Version? local) && Version.TryParse(mod.Version, out Version? remote) && remote > local
            ? InstallState.UpdateAvailable
            : InstallState.Installed;
    }

    public static async Task<byte[]?> GetIconAsync(Index index, LibraryMod mod, CancellationToken token)
    {
        LibraryFile? icon = mod.Files.FirstOrDefault(f => f.Path == ModFiles.IconFile);
        if (icon == null)
            return null;
        string cached = Path.Combine(Paths.Cache, "ModLibraryIcons", icon.Sha256 + ".ico");
        try
        {
            if (File.Exists(cached))
            {
                byte[] saved = await File.ReadAllBytesAsync(cached, token);
                if (string.Equals(Convert.ToHexStringLower(SHA256.HashData(saved)), icon.Sha256, StringComparison.Ordinal))
                    return saved;
            }
            byte[] bytes = await DownloadVerifiedAsync(index, mod, icon, token);
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            await File.WriteAllBytesAsync(cached + ".tmp", bytes, token);
            File.Move(cached + ".tmp", cached, true);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            return null;
        }
    }

    public static async Task InstallAsync(Index index, LibraryMod mod, CancellationToken token)
    {
        InstallState state = GetState(mod);
        if (state == InstallState.NameTaken)
            throw new InvalidOperationException("a different mod named " + mod.Name + " is already installed");
        string staging = Path.Combine(Paths.Temp, "ModLibrary", Guid.NewGuid().ToString("N"));
        string root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        try
        {
            foreach (LibraryFile file in mod.Files)
            {
                byte[] bytes = await DownloadVerifiedAsync(index, mod, file, token);
                string target = Path.GetFullPath(Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("unsafe path " + file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllBytesAsync(target, bytes, token);
            }
            PluginManifest manifest = PluginManifest.Read(staging) ?? throw new InvalidDataException("the mod has no manifest");
            if (!string.Equals(manifest.Name, mod.Name, StringComparison.Ordinal))
                throw new InvalidDataException("the mod manifest name does not match the library");
            StampLibrary(staging, mod);
            ModFiles.Install(new ModFiles.Package(manifest, staging, staging));
            App.Logger.WriteLine(
                LogIdent,
                (state == InstallState.UpdateAvailable ? "Updated " : "Installed ") + mod.Id + " " + mod.Version
            );
        }
        finally
        {
            ModFiles.TryDelete(staging);
        }
    }

    private static Timer? _autoUpdateTimer;

    private static int _autoUpdateRunning;

    public static void StartAutoUpdater()
    {
        _autoUpdateTimer?.Dispose();
        _autoUpdateTimer = new Timer(
            static _ =>
            {
                if (App.Settings.Prop.AutoUpdateLibraryMods)
                    _ = AutoUpdateInstalledModsAsync();
            },
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromHours(4)
        );
    }

    public static void StopAutoUpdater()
    {
        _autoUpdateTimer?.Dispose();
        _autoUpdateTimer = null;
    }

    public static async Task<IReadOnlyList<string>> AutoUpdateInstalledModsAsync(CancellationToken token = default)
    {
        List<string> updated = [];
        if (Interlocked.Exchange(ref _autoUpdateRunning, 1) != 0)
            return updated;
        try
        {
            Index index = await GetIndexAsync(true, token);
            DisableTakenDown(index);
            List<PluginManifest> installed = PluginManifest.Discover(Paths.Plugins, report: false);
            foreach (PluginManifest manifest in installed)
            {
                string? libraryId = GetInstalledLibraryId(manifest.Name);
                if (libraryId == null)
                    continue;
                LibraryMod? entry = index.Mods.FirstOrDefault(m =>
                    m.Name == manifest.Name && string.Equals(m.Id, libraryId, StringComparison.Ordinal)
                );
                if (entry == null)
                    continue;
                if (GetState(entry) != InstallState.UpdateAvailable)
                    continue;
                if (
                    entry.Network.Any(host => !manifest.NetworkHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                    || (entry.UsesRobloxAccount && !manifest.HasPermission(PluginManifest.RobloxAccountPermission))
                )
                {
                    App.Logger.WriteLine(
                        LogIdent,
                        "Left " + entry.Name + " for a manual update because " + entry.Version + " asks for new access"
                    );
                    continue;
                }
                try
                {
                    await InstallAsync(index, entry, token);
                    updated.Add(entry.Name + " " + entry.Version);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    App.Logger.WriteLine(LogIdent, "Could not update " + entry.Name + ": " + ex.Message);
                }
            }
            if (updated.Count > 0)
            {
                PluginHost.Reload();
                App.Logger.WriteLine(LogIdent, "Auto-updated " + updated.Count + " mod(s): " + string.Join(", ", updated));
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "Auto-update skipped: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _autoUpdateRunning, 0);
        }
        return updated;
    }

    private static void StampLibrary(string folder, LibraryMod mod)
    {
        string path = Path.Combine(folder, PluginManifest.ManifestFolder, "plugin.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException("bad manifest");
        manifest["library"] = new JsonObject
        {
            ["id"] = mod.Id,
            ["ownerId"] = mod.OwnerId,
            ["login"] = mod.Login,
        };
        File.WriteAllText(path, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<byte[]> DownloadVerifiedAsync(Index index, LibraryMod mod, LibraryFile file, CancellationToken token)
    {
        string url =
            RawPrefix
            + index.Commit
            + "/mods/"
            + Uri.EscapeDataString(mod.Name)
            + "/"
            + string.Join('/', file.Path.Split('/').Select(Uri.EscapeDataString));
        byte[] bytes = await DownloadAsync(url, file.Size, token);
        if (
            bytes.LongLength != file.Size
            || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), file.Sha256, StringComparison.Ordinal)
        )
            throw new InvalidDataException(file.Path + " of " + mod.Name + " does not match the library index, the download was refused");
        return bytes;
    }

    private static async Task<byte[]> DownloadAsync(string url, long maxBytes, CancellationToken token)
    {
        using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidDataException("the download is larger than expected");
        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new InvalidDataException("the download is larger than expected");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}

public static class ModLibrarySearch
{
    public enum Sort
    {
        Relevance,
        Featured,
        Newest,
        Updated,
        Name,
    }

    public sealed record Query(
        List<string> Words,
        List<string> Excluded,
        HashSet<string> Tags,
        string? Author,
        HashSet<string> Flags,
        Sort? Sort
    );

    public static Query Parse(string text)
    {
        Query query = new(
            [],
            [],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            null
        );
        foreach (string raw in Tokenize(text ?? ""))
        {
            string token = raw.Trim();
            if (token.Length == 0)
                continue;
            int colon = token.IndexOf(':');
            string key = colon > 0 ? token[..colon].ToLowerInvariant() : "";
            string value = colon > 0 ? token[(colon + 1)..] : token;
            if (key is "tag" or "tags" or "t" && value.Length > 0)
            {
                foreach (string tag in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    query.Tags.Add(tag);
            }
            else if (key is "by" or "author" or "user" && value.Length > 0)
            {
                query = query with { Author = value.TrimStart('@') };
            }
            else if (key is "is" or "has" && value.Length > 0)
            {
                query.Flags.Add(value.ToLowerInvariant());
            }
            else if (key is "sort" or "order" && value.Length > 0)
            {
                query = query with { Sort = ParseSort(value) };
            }
            else if (token.StartsWith('-') && token.Length > 1)
            {
                query.Excluded.Add(token[1..]);
            }
            else
            {
                query.Words.Add(token);
            }
        }
        return query;
    }

    private static Sort ParseSort(string value) =>
        value.ToLowerInvariant() switch
        {
            "new" or "newest" or "created" => Sort.Newest,
            "updated" or "recent" => Sort.Updated,
            "name" or "az" => Sort.Name,
            "featured" or "popular" or "top" => Sort.Featured,
            _ => Sort.Relevance,
        };

    private static IEnumerable<string> Tokenize(string text)
    {
        System.Text.StringBuilder current = new();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    public static List<ModLibrary.LibraryMod> Run(IEnumerable<ModLibrary.LibraryMod> mods, string text, string? tag, Sort? chosenSort)
    {
        Query query = Parse(text);
        if (!string.IsNullOrEmpty(tag))
            query.Tags.Add(tag);
        List<(ModLibrary.LibraryMod Mod, double Score)> results = [];
        foreach (ModLibrary.LibraryMod mod in mods)
        {
            if (query.Tags.Count > 0 && !query.Tags.All(t => mod.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)))
                continue;
            if (query.Author != null && !mod.Login.Equals(query.Author, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!MatchesFlags(mod, query.Flags))
                continue;
            string haystack = mod.Name + " " + mod.Description + " " + string.Join(' ', mod.Tags) + " " + mod.Login;
            if (query.Excluded.Any(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase)))
                continue;
            double score = 0;
            bool all = true;
            foreach (string word in query.Words)
            {
                double best = ScoreWord(mod, word);
                if (best <= 0)
                {
                    all = false;
                    break;
                }
                score += best;
            }
            if (!all)
                continue;
            score += (mod.Verified ? 25 : 0) + (mod.Trusted ? 10 : 0) + mod.Featured / 2.0;
            results.Add((mod, score));
        }
        Sort sort = chosenSort ?? query.Sort ?? (query.Words.Count > 0 ? Sort.Relevance : Sort.Featured);
        IEnumerable<(ModLibrary.LibraryMod Mod, double Score)> ordered = sort switch
        {
            Sort.Newest => results.OrderByDescending(r => r.Mod.Created ?? DateTime.MinValue),
            Sort.Updated => results.OrderByDescending(r => r.Mod.Updated ?? DateTime.MinValue),
            Sort.Name => results.OrderBy(r => r.Mod.Name, StringComparer.OrdinalIgnoreCase),
            Sort.Featured => results
                .OrderByDescending(r => r.Mod.Featured)
                .ThenByDescending(r => r.Mod.Verified)
                .ThenByDescending(r => r.Mod.Trusted)
                .ThenByDescending(r => r.Mod.Updated ?? DateTime.MinValue),
            _ => results.OrderByDescending(r => r.Score).ThenBy(r => r.Mod.Name, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.Select(r => r.Mod).ToList();
    }

    private static bool MatchesFlags(ModLibrary.LibraryMod mod, HashSet<string> flags)
    {
        foreach (string flag in flags)
        {
            bool ok = flag switch
            {
                "verified" => mod.Verified,
                "unverified" => !mod.Verified,
                "trusted" => mod.Trusted,
                "featured" => mod.Featured > 0,
                "installed" => ModLibrary.GetState(mod) is ModLibrary.InstallState.Installed or ModLibrary.InstallState.UpdateAvailable,
                "update" or "updates" => ModLibrary.GetState(mod) == ModLibrary.InstallState.UpdateAvailable,
                "offline" => mod.Network.Count == 0,
                "online" or "network" => mod.Network.Count > 0,
                "icon" => mod.HasIcon,
                _ => true,
            };
            if (!ok)
                return false;
        }
        return true;
    }

    private static double ScoreWord(ModLibrary.LibraryMod mod, string word)
    {
        double score = 0;
        if (mod.Name.Equals(word, StringComparison.OrdinalIgnoreCase))
            score = 100;
        else if (mod.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            score = 60;
        else if (mod.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            score = 40;
        if (mod.Tags.Any(t => t.Equals(word, StringComparison.OrdinalIgnoreCase)))
            score = Math.Max(score, 35);
        else if (mod.Tags.Any(t => t.StartsWith(word, StringComparison.OrdinalIgnoreCase)))
            score = Math.Max(score, 20);
        if (mod.Login.Contains(word, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 20);
        if (mod.Description.Contains(word, StringComparison.OrdinalIgnoreCase))
            score = Math.Max(score, 12);
        if (score == 0 && word.Length >= 4 && mod.Name.Split('-').Any(part => Distance(part, word) <= 1 + word.Length / 6))
            score = 8;
        if (score == 0 && word.Length >= 3 && IsSubsequence(word, mod.Name.Replace("-", "")))
            score = 6;
        return score;
    }

    private static bool IsSubsequence(string needle, string haystack)
    {
        int i = 0;
        foreach (char c in haystack)
        {
            if (i < needle.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(needle[i]))
                i++;
        }
        return i == needle.Length;
    }

    private static int Distance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
        int[] current = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
