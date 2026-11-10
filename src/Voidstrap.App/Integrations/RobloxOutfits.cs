using System.Collections.Concurrent;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.Integrations;

internal static class RobloxOutfits
{
    private static readonly ConcurrentDictionary<long, string> ThumbnailCache = new();

    private static readonly int[] ThumbnailRetryDelaysMs = [1000, 1500, 2500, 4000, 6000, 8000, 10000, 15000];

    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(8);

    public static string? CurrentCookie() =>
        SavedAccounts.SwitchingSupported ? RobloxCookie.ExtractCookieFromDat(SavedAccounts.LiveCookiePath) : RobloxCookie.Get();

    public static long GetSaved(long userId, long placeId) =>
        App.Settings.Prop.GameOutfits.TryGetValue(Key(userId, placeId), out long outfitId) ? outfitId : 0;

    public static void SetSaved(long userId, long placeId, long outfitId)
    {
        if (userId <= 0 || placeId <= 0 || GetSaved(userId, placeId) == outfitId)
            return;
        if (outfitId > 0)
            App.Settings.Prop.GameOutfits[Key(userId, placeId)] = outfitId;
        else
            App.Settings.Prop.GameOutfits.Remove(Key(userId, placeId));
        App.Settings.Save();
    }

    public static void ForgetThumbnail(long outfitId) => ThumbnailCache.TryRemove(outfitId, out _);

    public static async Task<List<OutfitRow>> ListAsync(long userId, string? cookie, CancellationToken token)
    {
        List<OutfitRow> loaded = new();
        RobloxApiResponse list = await RobloxCookie.SendGetWithCookieAsync(
            "https://avatar.roblox.com/v1/users/" + userId + "/outfits?page=1&itemsPerPage=50",
            cookie,
            token
        );
        if (list.StatusCode != 200 || list.Body == null)
            return loaded;
        using JsonDocument doc = JsonDocument.Parse(list.Body);
        if (!doc.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
            return loaded;
        foreach (JsonElement el in data.EnumerateArray())
        {
            long id = el.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long l) ? l : 0;
            string name = el.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
            bool editable = !el.TryGetProperty("isEditable", out JsonElement editEl) || editEl.ValueKind != JsonValueKind.False;
            if (id != 0 && editable)
                loaded.Add(new OutfitRow(id, name));
        }
        return loaded;
    }

    public static async Task<int> WearAsync(long outfitId, string? cookie, CancellationToken token)
    {
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync(
            "https://avatar.roblox.com/v1/outfits/" + outfitId + "/wear",
            null,
            cookie,
            token
        );
        return response.StatusCode;
    }

    public static async Task ApplySavedForLaunchAsync(string launchCommandLine, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxOutfits::ApplySavedForLaunchAsync";
        Dictionary<string, long> saved = App.Settings.Prop.GameOutfits;
        if (saved.Count == 0)
            return;
        long placeId = LaunchInterceptor.ExtractPlaceId(launchCommandLine);
        if (placeId <= 0)
            return;
        string suffix = ":" + placeId.ToString(CultureInfo.InvariantCulture);
        if (!saved.Keys.Any(key => key.EndsWith(suffix, StringComparison.Ordinal)))
            return;
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(LaunchTimeout);
            string? cookie = CurrentCookie();
            if (string.IsNullOrEmpty(cookie))
                return;
            RobloxAccount? account = await RobloxCookie.GetAccountAsync(cookie, timeout.Token);
            if (account == null || !saved.TryGetValue(Key(account.UserId, placeId), out long outfitId))
                return;
            int status = await WearAsync(outfitId, cookie, timeout.Token);
            App.Logger.WriteLine(LOG_IDENT, "Wearing outfit " + outfitId + " for place " + placeId + " returned " + status);
        }
        catch (OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            App.Logger.WriteLine(LOG_IDENT, "The game outfit was not applied in time");
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The game outfit could not be applied: " + ex.Message);
        }
    }

    public static async Task FillThumbnailsAsync(List<OutfitRow> outfits, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxOutfits::FillThumbnailsAsync";
        Dictionary<long, OutfitRow> byId = outfits.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
        List<long> remaining = new();
        foreach ((long id, OutfitRow row) in byId)
        {
            if (ThumbnailCache.TryGetValue(id, out string? cached))
                row.ImageUrl = cached;
            else
                remaining.Add(id);
        }
        for (int attempt = 0; attempt <= ThumbnailRetryDelaysMs.Length && remaining.Count > 0; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(ThumbnailRetryDelaysMs[attempt - 1], token);
            List<long> next = new();
            for (int i = 0; i < remaining.Count; i += 50)
            {
                List<long> chunk = remaining.GetRange(i, Math.Min(50, remaining.Count - i));
                Dictionary<long, string> found = new();
                next.AddRange(await RequestThumbnailBatchAsync(chunk, found, token));
                if (token.IsCancellationRequested)
                    return;
                foreach ((long id, string url) in found)
                {
                    ThumbnailCache[id] = url;
                    if (byId.TryGetValue(id, out OutfitRow? row))
                        row.ImageUrl = url;
                }
            }
            remaining = next;
        }
        if (remaining.Count > 0)
            App.Logger?.WriteLine(LOG_IDENT, remaining.Count + " outfit thumbnails never finished rendering");
    }

    private static string Key(long userId, long placeId) =>
        userId.ToString(CultureInfo.InvariantCulture) + ":" + placeId.ToString(CultureInfo.InvariantCulture);

    internal static async Task<List<long>> RequestThumbnailBatchAsync(
        List<long> ids,
        Dictionary<long, string> result,
        CancellationToken token,
        string type = "Outfit",
        string size = "150x150"
    )
    {
        const string LOG_IDENT = "RobloxOutfits::RequestThumbnailBatchAsync";
        List<long> pending = new();
        string body = "[" + string.Join(",", ids.Select(id => "{\"type\":\"" + type + "\",\"targetId\":" + id + ",\"format\":\"Png\",\"size\":\"" + size + "\",\"isCircular\":false}")) + "]";
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync(
            "https://thumbnails.roblox.com/v1/batch",
            body,
            null,
            token
        );
        if (response.StatusCode != 200 || response.Body == null)
            return ids;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array ? data : default;
            if (items.ValueKind != JsonValueKind.Array)
                return ids;
            HashSet<long> seen = new();
            foreach (JsonElement el in items.EnumerateArray())
            {
                long id = el.TryGetProperty("targetId", out JsonElement t) && t.TryGetInt64(out long l) ? l : 0;
                string img = el.TryGetProperty("imageUrl", out JsonElement iu) ? iu.GetString() ?? "" : "";
                string state = el.TryGetProperty("state", out JsonElement st) ? st.GetString() ?? "" : "";
                if (id == 0 || !seen.Add(id))
                    continue;
                if (state == "Completed" && img.Length > 0)
                    result[id] = img;
                else if (state is "Pending" or "InReview" or "Error")
                    pending.Add(id);
            }
        }
        catch (JsonException ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The outfit thumbnail response could not be parsed: " + ex.Message);
        }
        return pending;
    }
}
