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
        const string LOG_IDENT = "RobloxOutfits::WearAsync";
        RobloxApiResponse details = await RobloxCookie.SendGetWithCookieAsync(
            "https://avatar.roblox.com/v3/outfits/" + outfitId.ToString(CultureInfo.InvariantCulture) + "/details",
            cookie,
            token
        );
        if (details.StatusCode != 200 || string.IsNullOrEmpty(details.Body))
            return details.StatusCode;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(details.Body);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return 0;
            using MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("assets");
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.Number)
                        continue;
                    writer.WriteStartObject();
                    writer.WritePropertyName("id");
                    id.WriteTo(writer);
                    foreach (string key in WearAssetKeys)
                        if (asset.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.Object)
                        {
                            writer.WritePropertyName(key);
                            value.WriteTo(writer);
                        }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            RobloxApiResponse wear = await RobloxCookie.SendPostWithCookieAsync(
                "https://avatar.roblox.com/v2/avatar/set-wearing-assets",
                Encoding.UTF8.GetString(stream.ToArray()),
                cookie,
                token
            );
            if (wear.StatusCode != 200)
                return wear.StatusCode;
            if (root.TryGetProperty("bodyColor3s", out JsonElement colors) && colors.ValueKind == JsonValueKind.Object)
                await ApplyPartAsync("v2/avatar/set-body-colors", colors.GetRawText(), cookie, LOG_IDENT, token);
            if (root.TryGetProperty("scale", out JsonElement scale) && scale.ValueKind == JsonValueKind.Object)
                await ApplyPartAsync("v1/avatar/set-scales", scale.GetRawText(), cookie, LOG_IDENT, token);
            if (root.TryGetProperty("playerAvatarType", out JsonElement type) && type.ValueKind == JsonValueKind.String && type.GetString() is "R6" or "R15")
                await ApplyPartAsync("v1/avatar/set-player-avatar-type", "{\"playerAvatarType\":\"" + type.GetString() + "\"}", cookie, LOG_IDENT, token);
            return 200;
        }
        catch (JsonException ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The outfit details could not be read: " + ex.Message);
            return 0;
        }
    }

    private static readonly string[] WearAssetKeys = ["assetType", "meta"];

    private static async Task ApplyPartAsync(string path, string body, string? cookie, string logIdent, CancellationToken token)
    {
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync("https://avatar.roblox.com/" + path, body, cookie, token);
        if (response.StatusCode != 200)
            App.Logger?.WriteLine(logIdent, "Applying " + path + " returned " + response.StatusCode);
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

    private const int BackgroundAssetType = 92;

    public static string? AskName(string title, string initial)
    {
        Voidstrap.UI.Elements.Dialogs.TextInputDialog dialog = new(title, initial);
        dialog.Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(window => window.IsActive);
        dialog.ShowOwnedDialog();
        return dialog.Confirmed ? dialog.Value : null;
    }

    public static async Task<RobloxApiResponse> CreateFromCurrentAsync(string name, string cookie, CancellationToken token)
    {
        string? body = await ReadCurrentAvatarAsync(cookie, name, token);
        if (body == null)
            return new RobloxApiResponse(0, null, null);
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync("https://avatar.roblox.com/v3/outfits/create", body, cookie, token);
        if (!NeedsV4(response))
            return response;
        return await SendV4Async(
            "https://avatar.roblox.com/v4/outfits/create",
            false,
            [Wrap("outfitDefinition", body), Wrap("outfitDefinition", Wrap("updateOutfitModel", body)), body],
            response,
            cookie,
            token
        );
    }

    public static async Task<RobloxApiResponse> UpdateFromCurrentAsync(long outfitId, string name, string cookie, CancellationToken token)
    {
        string? body = await ReadCurrentAvatarAsync(cookie, name, token);
        if (body == null)
            return new RobloxApiResponse(0, null, null);
        string id = outfitId.ToString(CultureInfo.InvariantCulture);
        RobloxApiResponse response = await RobloxCookie.SendPatchWithCookieAsync("https://avatar.roblox.com/v3/outfits/" + id, body, cookie, token);
        if (!NeedsV4(response))
            return response;
        return await SendV4Async("https://avatar.roblox.com/v4/outfits/" + id, true, V4Updates(["UpdateAssets", "UpdateName"], body), response, cookie, token);
    }

    public static async Task<RobloxApiResponse> RenameAsync(long outfitId, string name, string cookie, CancellationToken token)
    {
        string id = outfitId.ToString(CultureInfo.InvariantCulture);
        string body = "{\"name\":" + JsonSerializer.Serialize(name) + "}";
        RobloxApiResponse response = await RobloxCookie.SendPatchWithCookieAsync("https://avatar.roblox.com/v1/outfits/" + id, body, cookie, token);
        if (!NeedsV4(response))
            return response;
        return await SendV4Async("https://avatar.roblox.com/v4/outfits/" + id, true, V4Updates(["UpdateName"], body), response, cookie, token);
    }

    public static async Task<RobloxApiResponse> DeleteAsync(long outfitId, string cookie, CancellationToken token)
    {
        string id = outfitId.ToString(CultureInfo.InvariantCulture);
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync("https://avatar.roblox.com/v1/outfits/" + id + "/delete", null, cookie, token);
        if (!NeedsV4(response))
            return response;
        RobloxApiResponse fallback = await RobloxCookie.SendDeleteWithCookieAsync("https://avatar.roblox.com/v4/outfits/" + id, cookie, token);
        return fallback.StatusCode == 200 ? fallback : response;
    }

    private static bool NeedsV4(RobloxApiResponse response) => response.StatusCode is 400 or 404 or 405 or 410 or 501;

    private static string Wrap(string key, string json) => "{" + JsonSerializer.Serialize(key) + ":" + json + "}";

    private static string[] V4Updates(string[] updateTypes, string body)
    {
        string types = JsonSerializer.Serialize(updateTypes);
        return
        [
            "{\"updateTypes\":" + types + ",\"outfitDefinition\":" + Wrap("updateOutfitModel", body) + "}",
            "{\"updateTypes\":" + types + ",\"outfitDefinition\":" + body + "}",
            "{\"updateTypes\":" + types + ",\"updateOutfitModel\":" + body + "}",
        ];
    }

    private static async Task<RobloxApiResponse> SendV4Async(string url, bool patch, string[] bodies, RobloxApiResponse original, string cookie, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxOutfits::SendV4Async";
        App.Logger?.WriteLine(LOG_IDENT, "Roblox returned " + original.StatusCode + " for the older outfit route, trying the newer one");
        RobloxApiResponse last = original;
        foreach (string body in bodies)
        {
            last = patch ? await RobloxCookie.SendPatchWithCookieAsync(url, body, cookie, token) : await RobloxCookie.SendPostWithCookieAsync(url, body, cookie, token);
            if (last.StatusCode == 200)
                return last;
            App.Logger?.WriteLine(LOG_IDENT, "The newer outfit route returned " + last.StatusCode + ": " + last.Body);
            if (last.StatusCode != 400)
                break;
        }
        return last.StatusCode is 404 or 405 ? original : last;
    }

    private static async Task<string?> ReadCurrentAvatarAsync(string cookie, string name, CancellationToken token)
    {
        RobloxApiResponse avatar = await RobloxCookie.SendGetWithCookieAsync("https://avatar.roblox.com/v2/avatar/avatar", cookie, token);
        if (avatar.StatusCode != 200 || string.IsNullOrEmpty(avatar.Body))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(avatar.Body);
            JsonElement root = doc.RootElement;
            using System.IO.MemoryStream stream = new();
            using (Utf8JsonWriter writer = new(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                if (root.TryGetProperty("bodyColor3s", out JsonElement colors))
                {
                    writer.WritePropertyName("bodyColor3s");
                    colors.WriteTo(writer);
                }
                if (root.TryGetProperty("scales", out JsonElement scales))
                {
                    writer.WritePropertyName("scale");
                    scales.WriteTo(writer);
                }
                if (root.TryGetProperty("playerAvatarType", out JsonElement type))
                {
                    writer.WritePropertyName("playerAvatarType");
                    type.WriteTo(writer);
                }
                writer.WritePropertyName("assets");
                writer.WriteStartArray();
                if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement asset in assets.EnumerateArray())
                    {
                        if (!asset.TryGetProperty("id", out JsonElement id) || asset.TryGetProperty("assetType", out JsonElement kind) && kind.TryGetProperty("id", out JsonElement kindId) && kindId.TryGetInt32(out int kindValue) && kindValue == BackgroundAssetType)
                            continue;
                        writer.WriteStartObject();
                        writer.WritePropertyName("id");
                        id.WriteTo(writer);
                        if (asset.TryGetProperty("meta", out JsonElement meta) && meta.ValueKind == JsonValueKind.Object)
                        {
                            writer.WritePropertyName("meta");
                            meta.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                writer.WriteEndArray();
                writer.WriteString("outfitType", "Avatar");
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

}
