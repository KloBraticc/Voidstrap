using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.Plugins;

internal static class ModVotes
{
    public sealed record Tally(int Likes, int Dislikes, int Mine);

    private const int MaxResponseBytes = 2 * 1024 * 1024;

    private static readonly HttpClient Client = CreateClient();

    private static readonly Dictionary<string, Tally> Tallies = new(StringComparer.Ordinal);

    private static readonly Uri Website = new(App.ProjectWebsite);

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    private static DateTime _lastRefresh = DateTime.MinValue;

    public static event Action? Changed;

    public static bool Available { get; private set; }

    private static HttpClient CreateClient()
    {
        HttpClient client = new(
            new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(App.ProjectName + "/" + App.Version);
        return client;
    }

    public static Tally Get(string modId)
    {
        lock (Tallies)
            return Tallies.TryGetValue(modId, out Tally? tally) ? tally : new Tally(0, 0, 0);
    }

    public static async Task RefreshAsync(CancellationToken token, bool force = false)
    {
        const string LOG_IDENT = "ModVotes::RefreshAsync";
        lock (Tallies)
        {
            if (!force && DateTime.UtcNow - _lastRefresh < RefreshInterval)
                return;
            _lastRefresh = DateTime.UtcNow;
        }
        try
        {
            byte[] bytes = await Client.GetByteArrayAsync(App.ProjectWebsite + "/api/mod-votes", token).ConfigureAwait(false);
            if (JsonNode.Parse(bytes) is not JsonObject root)
            {
                MarkUnavailable();
                return;
            }
            Dictionary<string, Tally> fresh = new(StringComparer.Ordinal);
            JsonObject? mine = root["votes"] as JsonObject;
            if (root["totals"] is JsonArray totals)
                foreach (JsonObject row in totals.OfType<JsonObject>())
                    if (row["mod_id"]?.GetValue<string>() is { } id)
                        fresh[id] = new Tally(Count(row["likes"]), Count(row["dislikes"]), VoteOf(mine?[id]));
            if (mine != null)
                foreach ((string id, JsonNode? vote) in mine)
                    if (!fresh.ContainsKey(id))
                        fresh[id] = new Tally(0, 0, VoteOf(vote));
            lock (Tallies)
            {
                Tallies.Clear();
                foreach ((string id, Tally tally) in fresh)
                    Tallies[id] = tally;
            }
            Available = root["siteKey"] is JsonValue siteKey && siteKey.TryGetValue(out string? key) && !string.IsNullOrEmpty(key);
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not load mod votes: " + ex.Message);
            MarkUnavailable();
        }
    }

    public static void MarkUnavailable()
    {
        if (!Available)
            return;
        Available = false;
        Changed?.Invoke();
    }

    private static int Count(JsonNode? node) => node is JsonValue value && value.TryGetValue(out int count) ? Math.Max(0, count) : 0;

    private static int VoteOf(JsonNode? node) => node is JsonValue value && value.TryGetValue(out int vote) ? Math.Clamp(vote, -1, 1) : 0;

    public static string VoteUrl(string modId, int vote) =>
        App.ProjectWebsite + "/vote?mod=" + Uri.EscapeDataString(modId) + "&v=" + Math.Clamp(vote, -1, 1);

    public static bool IsWebsite(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals(Website.Host, StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowedFrame(string url) =>
        url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase)
        || IsWebsite(url)
        || Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("challenges.cloudflare.com", StringComparison.OrdinalIgnoreCase);

    public static bool TryReadResult(string url, out Tally? tally, out int error)
    {
        tally = null;
        error = 0;
        if (!IsWebsite(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !uri.AbsolutePath.TrimEnd('/').Equals("/vote/done", StringComparison.Ordinal))
            return false;
        Dictionary<string, string> query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .GroupBy(pair => pair[0], StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Uri.UnescapeDataString(group.First()[1]), StringComparer.Ordinal);
        if (query.TryGetValue("error", out string? code))
        {
            error = int.TryParse(code, out int status) && status > 0 ? status : 503;
            return true;
        }
        if (query.TryGetValue("likes", out string? likes) && int.TryParse(likes, out int likeCount)
            && query.TryGetValue("dislikes", out string? dislikes) && int.TryParse(dislikes, out int dislikeCount)
            && query.TryGetValue("vote", out string? vote) && int.TryParse(vote, out int mine))
            tally = new Tally(Math.Max(0, likeCount), Math.Max(0, dislikeCount), Math.Clamp(mine, -1, 1));
        else
            error = 503;
        return true;
    }

    public static void Apply(string modId, Tally tally)
    {
        lock (Tallies)
            Tallies[modId] = tally;
        Changed?.Invoke();
    }
}
