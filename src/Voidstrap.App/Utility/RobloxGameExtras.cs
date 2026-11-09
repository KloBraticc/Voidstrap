using Voidstrap.Integrations;

namespace Voidstrap.Utility;

internal static class RobloxGameExtras
{
    public static async Task<List<GameSocialLink>> GetSocialLinksAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameExtras::GetSocialLinksAsync";
        List<GameSocialLink> links = new();
        try
        {
            string? body = await RobloxCookie
                .GetAuthenticatedStringAsync("https://games.roblox.com/v1/games/" + universeId + "/social-links/list", token)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(body))
                return links;
            using JsonDocument doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
                return links;
            foreach (JsonElement item in data.EnumerateArray())
            {
                string url = Text(item, "url");
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                    continue;
                links.Add(new GameSocialLink { Type = Text(item, "type"), Title = Text(item, "title"), Url = uri.AbsoluteUri });
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Social links could not be loaded for " + universeId + ": " + ex.Message);
        }
        return links;
    }

    public static async Task<List<GameUpdatePost>> GetUpdatesAsync(long universeId, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxGameExtras::GetUpdatesAsync";
        List<GameUpdatePost> posts = new();
        try
        {
            string since = DateTime.UtcNow.AddYears(-2).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            string? body = await RobloxCookie
                .GetAuthenticatedStringAsync(
                    "https://notifications.roblox.com/v2/stream-notifications/get-latest-game-updates?universeIds="
                        + universeId
                        + "&sinceDateTime="
                        + Uri.EscapeDataString(since),
                    token
                )
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(body))
                return posts;
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out JsonElement data))
                root = data;
            if (root.ValueKind != JsonValueKind.Array)
                return posts;
            foreach (JsonElement item in root.EnumerateArray())
            {
                string content = Text(item, "content");
                if (string.IsNullOrWhiteSpace(content))
                    continue;
                DateTime.TryParse(
                    Text(item, "createdOn"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out DateTime created
                );
                posts.Add(new GameUpdatePost { Content = content.Trim(), CreatedUtc = created });
            }
            posts.Sort(static (a, b) => b.CreatedUtc.CompareTo(a.CreatedUtc));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Game updates could not be loaded for " + universeId + ": " + ex.Message);
        }
        return posts;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
