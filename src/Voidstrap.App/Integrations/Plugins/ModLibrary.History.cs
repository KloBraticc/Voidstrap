using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.Plugins;

public static partial class ModLibrary
{
    public sealed record Change(string Sha, string Title, string Body, DateTime Date);

    public sealed record ModVersion(string Version, DateTime Date, string Sha);

    public sealed record History(IReadOnlyList<Change> Changes, IReadOnlyList<ModVersion> Versions);

    private const int MaxHistoryPages = 5;

    private const int MaxVersionProbes = 100;

    private const int VersionProbeBatch = 10;

    private static readonly Dictionary<string, Task<History>> Histories = new(StringComparer.Ordinal);

    [GeneratedRegex("^Co-authored-by:.*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CoAuthorPattern { get; }

    [GeneratedRegex(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailPattern { get; }

    private const int MaxPresenceIconName = 40;

    public static string IconImageUrl(Index index, LibraryMod mod) =>
        mod.HasIcon && CommitPattern.IsMatch(index.Commit) && mod.Name.Length <= MaxPresenceIconName
            ? "https://images.weserv.nl/?url=" + Uri.EscapeDataString("cdn.jsdelivr.net/gh/" + Repository + "@" + index.Commit[..12] + "/mods/" + mod.Name + "/" + ModFiles.IconFile) + "&output=png"
            : "";

    public static Task<History> GetHistoryAsync(Index index, LibraryMod mod)
    {
        string key = index.Commit + "/" + mod.Name;
        lock (Histories)
        {
            if (Histories.TryGetValue(key, out Task<History>? task) && !task.IsFaulted && !task.IsCanceled)
                return task;
            task = LoadHistoryAsync(index.Commit, mod.Name);
            Histories[key] = task;
            return task;
        }
    }

    private static string CleanMessage(string message) => EmailPattern.Replace(CoAuthorPattern.Replace(message, ""), "[email removed]").Trim();

    private static async Task<History> LoadHistoryAsync(string commit, string name)
    {
        if (!CommitPattern.IsMatch(commit))
            throw new InvalidDataException("the library commit is not valid");
        string path = Uri.EscapeDataString("mods/" + name);
        List<Change> changes = [];
        for (int page = 1; page <= MaxHistoryPages; page++)
        {
            byte[] bytes = await DownloadAsync(
                "https://api.github.com/repos/" + Repository + "/commits?path=" + path + "&sha=" + commit + "&per_page=100&page=" + page,
                MaxIndexBytes,
                CancellationToken.None
            ).ConfigureAwait(false);
            if (JsonNode.Parse(bytes) is not JsonArray entries)
                break;
            foreach (JsonObject entry in entries.OfType<JsonObject>())
            {
                if (entry["sha"] is not JsonValue shaValue || !shaValue.TryGetValue(out string? sha) || !CommitPattern.IsMatch(sha))
                    continue;
                if (entry["commit"]?["author"]?["date"] is not JsonValue dateValue || !dateValue.TryGetValue(out DateTime date))
                    continue;
                string message = entry["commit"]?["message"] is JsonValue messageValue && messageValue.TryGetValue(out string? text) ? text : "";
                int split = message.IndexOf('\n');
                changes.Add(new Change(
                    sha,
                    CleanMessage(split < 0 ? message : message[..split]),
                    split < 0 ? "" : CleanMessage(message[(split + 1)..]),
                    date
                ));
            }
            if (entries.Count < 100)
                break;
        }
        List<ModVersion> versions = [];
        Dictionary<string, int> seen = new(StringComparer.Ordinal);
        Change[] probes = changes.Take(MaxVersionProbes).ToArray();
        for (int i = 0; i < probes.Length; i += VersionProbeBatch)
        {
            ModVersion?[] batch = await Task.WhenAll(probes.Skip(i).Take(VersionProbeBatch).Select(change => ReadVersionAsync(change, name))).ConfigureAwait(false);
            foreach (ModVersion? version in batch)
            {
                if (version == null)
                    continue;
                if (seen.TryGetValue(version.Version, out int at))
                    versions[at] = version;
                else
                {
                    seen[version.Version] = versions.Count;
                    versions.Add(version);
                }
            }
        }
        return new History(changes, versions);
    }

    private static async Task<ModVersion?> ReadVersionAsync(Change change, string name)
    {
        using HttpResponseMessage response = await Client.GetAsync(
            RawPrefix + change.Sha + "/mods/" + Uri.EscapeDataString(name) + "/.voidstrap-mod/plugin.json",
            HttpCompletionOption.ResponseHeadersRead
        ).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > MaxFileBytes)
            return null;
        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (json.Length > MaxFileBytes || JsonNode.Parse(json) is not JsonObject manifest)
            return null;
        return manifest["version"] is JsonValue value && value.TryGetValue(out string? version) && version.Length is > 0 and <= 40
            ? new ModVersion(version, change.Date, change.Sha)
            : null;
    }
}
