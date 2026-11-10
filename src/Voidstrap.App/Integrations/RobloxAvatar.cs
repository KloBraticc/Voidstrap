using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.Integrations;

internal static partial class RobloxAvatar
{
    public const string BackgroundType = "AvatarBackground";

    public const string EmoteType = "EmoteAnimation";

    public const int EmoteSlots = 8;

    public const string PositionKind = "position";

    public const string RotationKind = "rotation";

    public const string ScaleKind = "scale";

    public static readonly string[] AdjustKinds = [PositionKind, RotationKind, ScaleKind];

    public static readonly string[] AdjustAxes = ["X", "Y", "Z"];

    public static string AdjustKey(string typeName, string kind, string axis) => typeName + "." + kind + "." + axis;

    private const int InventoryPages = 3;

    private const long MaxSceneBytes = 48L * 1024 * 1024;

    public static readonly string[] BodyTypes = ["Torso", "RightArm", "LeftArm", "LeftLeg", "RightLeg", "Head", "DynamicHead", "Face"];

    public static readonly string[] ClothingTypes =
    [
        "TShirt",
        "Shirt",
        "Pants",
        "TShirtAccessory",
        "ShirtAccessory",
        "PantsAccessory",
        "JacketAccessory",
        "SweaterAccessory",
        "ShortsAccessory",
        "LeftShoeAccessory",
        "RightShoeAccessory",
        "DressSkirtAccessory",
    ];

    public static readonly string[] AccessoryTypes =
    [
        "Hat",
        "HairAccessory",
        "FaceAccessory",
        "NeckAccessory",
        "ShoulderAccessory",
        "FrontAccessory",
        "BackAccessory",
        "WaistAccessory",
        "EyebrowAccessory",
        "EyelashAccessory",
        "FaceMakeup",
        "LipMakeup",
        "EyeMakeup",
    ];

    public static readonly string[] AnimationTypes =
    [
        "ClimbAnimation",
        "FallAnimation",
        "IdleAnimation",
        "JumpAnimation",
        "RunAnimation",
        "SwimAnimation",
        "WalkAnimation",
        "MoodAnimation",
    ];

    public static readonly string[] BackgroundTypes = [BackgroundType];

    public static readonly string[] EmoteTypes = [EmoteType];

    public static readonly string[] RecentTypes = [.. AccessoryTypes, .. ClothingTypes, .. BodyTypes, .. AnimationTypes, EmoteType, BackgroundType];

    public static readonly string[] BodyColorParts = ["headColor3", "torsoColor3", "rightArmColor3", "leftArmColor3", "rightLegColor3", "leftLegColor3"];

    private static readonly HashSet<int> RigidAccessoryTypeIds = [8, 41, 42, 43, 44, 45, 46, 47];

    private static readonly Dictionary<string, int> LayeredOrder = new(StringComparer.Ordinal)
    {
        ["LeftShoeAccessory"] = 3,
        ["RightShoeAccessory"] = 3,
        ["PantsAccessory"] = 4,
        ["ShortsAccessory"] = 5,
        ["DressSkirtAccessory"] = 6,
        ["TShirtAccessory"] = 7,
        ["ShirtAccessory"] = 8,
        ["SweaterAccessory"] = 9,
        ["JacketAccessory"] = 10,
        ["HairAccessory"] = 11,
    };

    private static readonly int[] RetryDelaysMs = [1200, 2000, 3000, 4500, 6000];

    private const int AssetCacheLimit = 256;

    private static readonly ConcurrentDictionary<long, byte[]> AssetCache = new();

    private static readonly HttpClient CdnClient = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    {
        Timeout = TimeSpan.FromSeconds(30),
        MaxResponseContentBufferSize = MaxSceneBytes,
    };

    public static bool IsRigidAccessory(AvatarAsset asset) => RigidAccessoryTypeIds.Contains(asset.TypeId);

    public static string? DefaultMeta(string typeName) =>
        LayeredOrder.TryGetValue(typeName, out int order) ? "{\"order\":" + order.ToString(CultureInfo.InvariantCulture) + ",\"version\":1}" : null;

    public static async Task<AvatarState?> GetAsync(string? cookie, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::GetAsync";
        RobloxApiResponse response = await RobloxCookie.SendGetWithCookieAsync("https://avatar.roblox.com/v2/avatar/avatar", cookie, token);
        if (response.StatusCode != 200 || string.IsNullOrEmpty(response.Body))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            AvatarState state = new();
            if (root.TryGetProperty("playerAvatarType", out JsonElement type) && type.ValueKind == JsonValueKind.String)
                state.AvatarType = type.GetString() ?? "R15";
            if (root.TryGetProperty("scales", out JsonElement scales) && scales.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty scale in scales.EnumerateObject())
                    if (scale.Value.ValueKind == JsonValueKind.Number)
                        state.Scales[scale.Name] = scale.Value.GetDouble();
            if (root.TryGetProperty("bodyColor3s", out JsonElement colors) && colors.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty color in colors.EnumerateObject())
                    if (color.Value.ValueKind == JsonValueKind.String)
                        state.BodyColors[color.Name] = color.Value.GetString() ?? "";
            if (root.TryGetProperty("emotes", out JsonElement emotes) && emotes.ValueKind == JsonValueKind.Array)
                foreach (JsonElement emote in emotes.EnumerateArray())
                {
                    long emoteId = emote.TryGetProperty("assetId", out JsonElement emoteIdEl) && emoteIdEl.TryGetInt64(out long e) ? e : 0;
                    int position = emote.TryGetProperty("position", out JsonElement positionEl) && positionEl.TryGetInt32(out int p) ? p : 0;
                    if (emoteId != 0 && position > 0)
                        state.Emotes.Add(new AvatarEmote(emoteId, position));
                }
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    long id = asset.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt64(out long l) ? l : 0;
                    if (id == 0)
                        continue;
                    string name = asset.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
                    int typeId = 0;
                    string typeName = "";
                    if (asset.TryGetProperty("assetType", out JsonElement assetType) && assetType.ValueKind == JsonValueKind.Object)
                    {
                        typeId = assetType.TryGetProperty("id", out JsonElement typeIdEl) && typeIdEl.TryGetInt32(out int i) ? i : 0;
                        typeName = assetType.TryGetProperty("name", out JsonElement typeNameEl) ? typeNameEl.GetString() ?? "" : "";
                    }
                    string? meta = asset.TryGetProperty("meta", out JsonElement metaEl) && metaEl.ValueKind == JsonValueKind.Object ? metaEl.GetRawText() : null;
                    state.Assets.Add(new AvatarAsset(id, name, typeId, typeName, meta));
                }
            return state;
        }
        catch (JsonException ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The avatar response could not be parsed: " + ex.Message);
            return null;
        }
    }

    public static async Task<AvatarRules> LoadRulesAsync(CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::LoadRulesAsync";
        Dictionary<string, (int Id, int Max)> types = new(StringComparer.Ordinal);
        List<string> palette = new();
        Dictionary<string, (double Min, double Max)> bounds = new(StringComparer.Ordinal);
        AvatarRules rules = new(types, palette, bounds);
        RobloxApiResponse response = await RobloxCookie.SendGetWithCookieAsync("https://avatar.roblox.com/v1/avatar-rules", null, token);
        if (response.StatusCode != 200 || string.IsNullOrEmpty(response.Body))
            return rules;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.TryGetProperty("bodyColorsPalette", out JsonElement colors) && colors.ValueKind == JsonValueKind.Array)
                foreach (JsonElement color in colors.EnumerateArray())
                    if (
                        color.TryGetProperty("hexColor", out JsonElement hex)
                        && hex.ValueKind == JsonValueKind.String
                        && hex.GetString()?.TrimStart('#') is { Length: 6 } value
                        && value.All(Uri.IsHexDigit)
                    )
                        palette.Add(value.ToUpperInvariant());
            if (
                doc.RootElement.TryGetProperty("accessoryRefinementLowerBounds", out JsonElement lower)
                && doc.RootElement.TryGetProperty("accessoryRefinementUpperBounds", out JsonElement upper)
                && lower.ValueKind == JsonValueKind.Object
                && upper.ValueKind == JsonValueKind.Object
            )
                foreach (JsonProperty type in lower.EnumerateObject())
                foreach (string kind in AdjustKinds)
                foreach (string axis in AdjustAxes)
                {
                    string field = axis.ToLowerInvariant() + char.ToUpperInvariant(kind[0]) + kind[1..];
                    if (
                        type.Value.ValueKind == JsonValueKind.Object
                        && type.Value.TryGetProperty(kind, out JsonElement low)
                        && low.ValueKind == JsonValueKind.Object
                        && low.TryGetProperty(field, out JsonElement min)
                        && min.ValueKind == JsonValueKind.Number
                        && upper.TryGetProperty(type.Name, out JsonElement upperType)
                        && upperType.ValueKind == JsonValueKind.Object
                        && upperType.TryGetProperty(kind, out JsonElement high)
                        && high.ValueKind == JsonValueKind.Object
                        && high.TryGetProperty(field, out JsonElement max)
                        && max.ValueKind == JsonValueKind.Number
                        && max.GetDouble() > min.GetDouble()
                    )
                        bounds[AdjustKey(type.Name, kind, axis)] = (min.GetDouble(), max.GetDouble());
                }
            if (!doc.RootElement.TryGetProperty("wearableAssetTypes", out JsonElement wearable) || wearable.ValueKind != JsonValueKind.Array)
                return rules;
            foreach (JsonElement type in wearable.EnumerateArray())
            {
                string name = type.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
                int id = type.TryGetProperty("id", out JsonElement idEl) && idEl.TryGetInt32(out int i) ? i : 0;
                int max = type.TryGetProperty("maxNumber", out JsonElement maxEl) && maxEl.TryGetInt32(out int m) ? m : 1;
                if (name.Length > 0 && id > 0)
                    types[name.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal)] = (id, max);
            }
        }
        catch (JsonException ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The avatar rules could not be parsed: " + ex.Message);
        }
        return rules;
    }

    public static async Task<bool> SetWearingAsync(IReadOnlyList<AvatarAsset> assets, string? cookie, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::SetWearingAsync";
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("assets");
            foreach (AvatarAsset asset in assets)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", asset.Id);
                if (asset.TypeId > 0)
                {
                    writer.WriteStartObject("assetType");
                    writer.WriteNumber("id", asset.TypeId);
                    writer.WriteString("name", asset.TypeName);
                    writer.WriteEndObject();
                }
                if (asset.MetaJson != null)
                {
                    writer.WritePropertyName("meta");
                    writer.WriteRawValue(asset.MetaJson);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync(
            "https://avatar.roblox.com/v2/avatar/set-wearing-assets",
            Encoding.UTF8.GetString(stream.ToArray()),
            cookie,
            token
        );
        if (response.StatusCode != 200)
            return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body ?? "{}");
            bool failed = doc.RootElement.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False;
            if (failed)
                App.Logger?.WriteLine(LOG_IDENT, "Roblox rejected part of the avatar: " + response.Body);
            return !failed;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    public static async Task<bool> SetAvatarTypeAsync(bool r15, string? cookie, CancellationToken token)
    {
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync(
            "https://avatar.roblox.com/v1/avatar/set-player-avatar-type",
            "{\"playerAvatarType\":\"" + (r15 ? "R15" : "R6") + "\"}",
            cookie,
            token
        );
        return response.StatusCode == 200;
    }

    public static async Task<bool> SetBodyColorsAsync(string hex, string? cookie, CancellationToken token)
    {
        string body = "{" + string.Join(",", BodyColorParts.Select(part => "\"" + part + "\":\"" + hex + "\"")) + "}";
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync("https://avatar.roblox.com/v2/avatar/set-body-colors", body, cookie, token);
        return response.StatusCode == 200;
    }

    public static async Task<bool> EquipEmoteAsync(long assetId, int position, string? cookie, CancellationToken token)
    {
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync(
            "https://avatar.roblox.com/v1/emotes/" + assetId.ToString(CultureInfo.InvariantCulture) + "/" + position.ToString(CultureInfo.InvariantCulture),
            null,
            cookie,
            token
        );
        return response.StatusCode == 200;
    }

    public static async Task<bool> UnequipEmoteAsync(int position, string? cookie, CancellationToken token)
    {
        RobloxApiResponse response = await RobloxCookie.SendDeleteWithCookieAsync(
            "https://avatar.roblox.com/v1/emotes/" + position.ToString(CultureInfo.InvariantCulture),
            cookie,
            token
        );
        return response.StatusCode == 200;
    }

    public static async Task<bool> SetScalesAsync(IReadOnlyDictionary<string, double> scales, string? cookie, CancellationToken token)
    {
        string body = "{" + string.Join(",", scales.Select(s => "\"" + s.Key + "\":" + s.Value.ToString("0.###", CultureInfo.InvariantCulture))) + "}";
        RobloxApiResponse response = await RobloxCookie.SendPostWithCookieAsync("https://avatar.roblox.com/v1/avatar/set-scales", body, cookie, token);
        return response.StatusCode == 200;
    }

    public static async Task<List<AvatarItemRow>> ListInventoryAsync(
        long userId,
        string[] types,
        string? cookie,
        CancellationToken token,
        int pages = InventoryPages
    )
    {
        const string LOG_IDENT = "RobloxAvatar::ListInventoryAsync";
        List<AvatarItemRow> rows = new();
        HashSet<long> seen = new();
        string? cursor = null;
        for (int page = 0; page < pages; page++)
        {
            string url =
                "https://inventory.roblox.com/v2/users/"
                + userId.ToString(CultureInfo.InvariantCulture)
                + "/inventory?assetTypes="
                + string.Join(",", types)
                + "&limit=100&sortOrder=Desc"
                + (cursor == null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));
            RobloxApiResponse response = await RobloxCookie.SendGetWithCookieAsync(url, cookie, token);
            if (response.StatusCode != 200 || string.IsNullOrEmpty(response.Body))
                break;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(response.Body);
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement el in data.EnumerateArray())
                    {
                        long id = el.TryGetProperty("assetId", out JsonElement idEl) && idEl.TryGetInt64(out long l) ? l : 0;
                        string name = el.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() ?? "" : "";
                        string type = el.TryGetProperty("assetType", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String ? typeEl.GetString() ?? "" : "";
                        if (id != 0 && seen.Add(id))
                            rows.Add(new AvatarItemRow(id, name, type, false));
                    }
                cursor = root.TryGetProperty("nextPageCursor", out JsonElement next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
            }
            catch (JsonException ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The inventory response could not be parsed: " + ex.Message);
                break;
            }
            if (string.IsNullOrEmpty(cursor))
                break;
        }
        return rows;
    }

    public static async Task FillThumbnailsAsync(IReadOnlyList<AvatarItemRow> rows, CancellationToken token)
    {
        foreach (bool outfits in new[] { false, true })
        {
            List<AvatarItemRow> group = rows.Where(r => r.IsOutfit == outfits && r.ImageUrl == null).ToList();
            List<long> remaining = group.Select(r => r.Id).Distinct().ToList();
            for (int attempt = 0; attempt <= RetryDelaysMs.Length && remaining.Count > 0; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(RetryDelaysMs[attempt - 1], token);
                List<long> next = new();
                for (int i = 0; i < remaining.Count; i += 50)
                {
                    List<long> chunk = remaining.GetRange(i, Math.Min(50, remaining.Count - i));
                    Dictionary<long, string> found = new();
                    next.AddRange(await RobloxOutfits.RequestThumbnailBatchAsync(chunk, found, token, outfits ? "Outfit" : "Asset"));
                    if (token.IsCancellationRequested)
                        return;
                    foreach (AvatarItemRow row in group)
                        if (found.TryGetValue(row.Id, out string? url))
                            row.ImageUrl = url;
                }
                remaining = next;
            }
        }
    }

    public static async Task<string?> ThumbnailAsync(string type, long id, CancellationToken token)
    {
        List<long> ids = [id];
        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RetryDelaysMs[attempt - 1], token);
            Dictionary<long, string> found = new();
            List<long> pending = await RobloxOutfits.RequestThumbnailBatchAsync(ids, found, token, type, "420x420");
            if (found.TryGetValue(id, out string? url))
                return url;
            if (pending.Count == 0)
                return null;
        }
        return null;
    }

    public static async Task<byte[]?> DownloadAssetAsync(long id, string? cookie, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::DownloadAssetAsync";
        if (AssetCache.TryGetValue(id, out byte[]? cached))
            return cached;
        string number = id.ToString(CultureInfo.InvariantCulture);
        string? location =
            await FindAssetLocationAsync("https://assetdelivery.roblox.com/v2/assetId/" + number, cookie, token)
            ?? await FindAssetLocationAsync("https://assetdelivery.roblox.com/v1/assetId/" + number, cookie, token)
            ?? (string.IsNullOrEmpty(cookie) ? null : await FindAssetLocationAsync("https://assetdelivery.roblox.com/v2/assetId/" + number, null, token));
        if (location == null)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Asset " + number + " has no download location");
            return null;
        }
        try
        {
            byte[] bytes = await CdnClient.GetByteArrayAsync(location, token);
            if (AssetCache.Count >= AssetCacheLimit)
                AssetCache.Clear();
            AssetCache[id] = bytes;
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Asset " + number + " could not be downloaded: " + ex.Message);
            return null;
        }
    }

    private static async Task<string?> FindAssetLocationAsync(string url, string? cookie, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::FindAssetLocationAsync";
        RobloxApiResponse response = await RobloxCookie.SendGetWithCookieAsync(url, cookie, token);
        if (response.StatusCode != 200 || string.IsNullOrEmpty(response.Body))
            return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.Body);
            JsonElement root = doc.RootElement;
            List<string?> found = new();
            if (root.TryGetProperty("locations", out JsonElement locations) && locations.ValueKind == JsonValueKind.Array)
                foreach (JsonElement entry in locations.EnumerateArray())
                    if (entry.TryGetProperty("location", out JsonElement value) && value.ValueKind == JsonValueKind.String)
                        found.Add(value.GetString());
            if (root.TryGetProperty("location", out JsonElement single) && single.ValueKind == JsonValueKind.String)
                found.Add(single.GetString());
            string? location = found.FirstOrDefault(candidate => candidate != null && IsAssetHost(candidate));
            if (location == null)
            {
                string hosts = string.Join(", ", found.Select(candidate => Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) ? uri.Host : "unreadable"));
                string detail = found.Count > 0 ? "hosts " + hosts : response.Body.Length > 200 ? response.Body[..200] : response.Body;
                App.Logger?.WriteLine(LOG_IDENT, "No usable location from " + new Uri(url).AbsolutePath + (cookie == null ? " without sign in: " : ": ") + detail);
            }
            return location;
        }
        catch (JsonException ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The asset location could not be read: " + ex.Message);
            return null;
        }
    }

    private static bool IsAssetHost(string url) =>
        IsCdnUrl(url) || RobloxCookie.IsRobloxUrl(Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri : null);

    public static async Task<AvatarScene?> BuildLiveSceneAsync(AvatarState state, string? cookie, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::BuildLiveSceneAsync";
        try
        {
            string? root = await ClassicTopBar.ClassicTopBarMod.FindRobloxInstallAsync(token);
            if (root == null)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The Roblox install was not found, so the live preview is unavailable");
                return null;
            }
            Task<AvatarScene?> Build() =>
                AvatarAssembler.BuildAsync(state, root, (id, inner) => DownloadAssetAsync(id, cookie, inner), message => App.Logger?.WriteLine(LOG_IDENT, message), token);
            AvatarScene? scene = Utility.Platform.IsWindows ? await Task.Run(Build, token) : await Build();
            if (scene == null)
                App.Logger?.WriteLine(LOG_IDENT, "The live preview could not be built, so the Roblox render is shown");
            return scene;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The live preview failed: " + ex.Message);
            return null;
        }
    }

    public static async Task<AvatarScene?> LoadSceneAsync(long userId, string? cookie, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::LoadSceneAsync";
        try
        {
            string? manifest = null;
            for (int attempt = 0; attempt <= RetryDelaysMs.Length && manifest == null; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(RetryDelaysMs[attempt - 1], token);
                RobloxApiResponse response = await RobloxCookie.SendGetWithCookieAsync(
                    "https://thumbnails.roblox.com/v1/users/avatar-3d?userId=" + userId.ToString(CultureInfo.InvariantCulture),
                    cookie,
                    token
                );
                if (response.StatusCode != 200 || string.IsNullOrEmpty(response.Body))
                    return null;
                using JsonDocument doc = JsonDocument.Parse(response.Body);
                string state = doc.RootElement.TryGetProperty("state", out JsonElement stateEl) ? stateEl.GetString() ?? "" : "";
                string url = doc.RootElement.TryGetProperty("imageUrl", out JsonElement urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() ?? "" : "";
                if (state == "Completed" && url.Length > 0)
                    manifest = url;
                else if (state != "Pending")
                    return null;
            }
            if (manifest == null || !IsCdnUrl(manifest))
                return null;
            return Utility.Platform.IsWindows ? await Task.Run(() => BuildSceneAsync(manifest, token), token) : await BuildSceneAsync(manifest, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The 3D avatar could not be loaded: " + ex.Message);
            return null;
        }
    }

    private static bool IsCdnUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase);

    private static string? CdnUrl(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || !HashPattern().IsMatch(hash))
            return null;
        int bucket = 31;
        foreach (char c in hash)
            bucket ^= c;
        return "https://t" + (bucket % 8).ToString(CultureInfo.InvariantCulture) + ".rbxcdn.com/" + hash;
    }

    [GeneratedRegex("^[A-Za-z0-9]{16,128}$")]
    private static partial Regex HashPattern();

    private static async Task<AvatarScene?> BuildSceneAsync(string manifestUrl, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::BuildSceneAsync";
        using JsonDocument doc = JsonDocument.Parse(await CdnClient.GetStringAsync(manifestUrl, token));
        JsonElement root = doc.RootElement;
        string? objUrl = CdnUrl(root.TryGetProperty("obj", out JsonElement objEl) ? objEl.GetString() : null);
        string? mtlUrl = CdnUrl(root.TryGetProperty("mtl", out JsonElement mtlEl) ? mtlEl.GetString() : null);
        if (objUrl == null)
            return null;
        List<string> textures = new();
        if (root.TryGetProperty("textures", out JsonElement texEl) && texEl.ValueKind == JsonValueKind.Array)
            foreach (JsonElement tex in texEl.EnumerateArray())
                if (tex.ValueKind == JsonValueKind.String)
                    textures.Add(tex.GetString() ?? "");
        Task<string> objTask = CdnClient.GetStringAsync(objUrl, token);
        string mtl = mtlUrl == null ? "" : await CdnClient.GetStringAsync(mtlUrl, token);
        string obj = await objTask;
        Dictionary<string, (Color Color, string? Map)> materials = ParseMaterials(mtl);
        Dictionary<string, ImageSource?> images = new(StringComparer.Ordinal);
        int fallbackIndex = 0;
        foreach ((_, (_, string? map)) in materials)
        {
            if (map == null || images.ContainsKey(map))
                continue;
            string? url = textures.Contains(map) || HashPattern().IsMatch(map) ? CdnUrl(map) : null;
            if (url == null && fallbackIndex < textures.Count)
                url = CdnUrl(textures[fallbackIndex]);
            fallbackIndex++;
            images[map] = url == null ? null : await LoadImageAsync(url, token);
        }
        token.ThrowIfCancellationRequested();
        Model3DGroup group = new();
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        foreach ((string name, MeshGeometry3D mesh) in ParseMeshes(obj))
        {
            if (mesh.TriangleIndices.Count == 0)
                continue;
            foreach (Point3D p in mesh.Positions)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
                minZ = Math.Min(minZ, p.Z);
                maxX = Math.Max(maxX, p.X);
                maxY = Math.Max(maxY, p.Y);
                maxZ = Math.Max(maxZ, p.Z);
            }
            mesh.Freeze();
            Brush brush;
            if (materials.TryGetValue(name, out (Color Color, string? Map) material) && material.Map != null && images.GetValueOrDefault(material.Map) is ImageSource image)
                brush = new ImageBrush(image)
                {
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, 1, 1),
                    TileMode = TileMode.Tile,
                    Stretch = Stretch.Fill,
                };
            else
                brush = new SolidColorBrush(materials.TryGetValue(name, out material) ? material.Color : Colors.LightGray);
            brush.Freeze();
            DiffuseMaterial diffuse = new(brush);
            diffuse.Freeze();
            group.Children.Add(new GeometryModel3D(mesh, diffuse) { BackMaterial = diffuse });
        }
        if (group.Children.Count == 0)
            return null;
        group.Freeze();
        Rect3D bounds = new(minX, minY, minZ, maxX - minX, maxY - minY, maxZ - minZ);
        if (
            root.TryGetProperty("aabb", out JsonElement aabb)
            && aabb.TryGetProperty("min", out JsonElement low)
            && aabb.TryGetProperty("max", out JsonElement high)
            && ReadPoint(low) is Point3D lowPoint
            && ReadPoint(high) is Point3D highPoint
            && highPoint.X > lowPoint.X
            && highPoint.Y > lowPoint.Y
            && highPoint.Z > lowPoint.Z
        )
            bounds = new Rect3D(lowPoint.X, lowPoint.Y, lowPoint.Z, highPoint.X - lowPoint.X, highPoint.Y - lowPoint.Y, highPoint.Z - lowPoint.Z);
        App.Logger?.WriteLine(
            LOG_IDENT,
            "Roblox render loaded with " + group.Children.Count + " meshes, size " + bounds.SizeX.ToString("0.##", CultureInfo.InvariantCulture) + " by " + bounds.SizeY.ToString("0.##", CultureInfo.InvariantCulture) + " by " + bounds.SizeZ.ToString("0.##", CultureInfo.InvariantCulture) + ", mesh extent " + (maxX - minX).ToString("0.##", CultureInfo.InvariantCulture) + " by " + (maxY - minY).ToString("0.##", CultureInfo.InvariantCulture) + " by " + (maxZ - minZ).ToString("0.##", CultureInfo.InvariantCulture)
        );
        double yaw = 180.0;
        if (
            root.TryGetProperty("camera", out JsonElement camera)
            && camera.TryGetProperty("position", out JsonElement position)
            && position.TryGetProperty("x", out JsonElement px)
            && position.TryGetProperty("z", out JsonElement pz)
            && px.ValueKind == JsonValueKind.Number
            && pz.ValueKind == JsonValueKind.Number
        )
            yaw = Math.Atan2(px.GetDouble() - (minX + maxX) / 2, pz.GetDouble() - (minZ + maxZ) / 2) * 180.0 / Math.PI;
        return new AvatarScene(group, bounds, yaw);
    }

    public static async Task<ImageSource?> LoadBitmapAsync(string? url, CancellationToken token)
    {
        const string LOG_IDENT = "RobloxAvatar::LoadBitmapAsync";
        if (url == null || !IsCdnUrl(url))
            return null;
        try
        {
            return Utility.Platform.IsWindows ? await Task.Run(() => LoadImageAsync(url, token), token) : await LoadImageAsync(url, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "The picture could not be loaded: " + ex.Message);
            return null;
        }
    }

    private static Point3D? ReadPoint(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("x", out JsonElement x)
        && element.TryGetProperty("y", out JsonElement y)
        && element.TryGetProperty("z", out JsonElement z)
        && x.ValueKind == JsonValueKind.Number
        && y.ValueKind == JsonValueKind.Number
        && z.ValueKind == JsonValueKind.Number
            ? new Point3D(x.GetDouble(), y.GetDouble(), z.GetDouble())
            : null;

    private static async Task<ImageSource?> LoadImageAsync(string url, CancellationToken token)
    {
        byte[] bytes = await CdnClient.GetByteArrayAsync(url, token);
        try
        {
            using MemoryStream stream = new(bytes);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Dictionary<string, (Color Color, string? Map)> ParseMaterials(string mtl)
    {
        Dictionary<string, (Color Color, string? Map)> materials = new(StringComparer.Ordinal);
        string? current = null;
        foreach (string raw in mtl.Split('\n'))
        {
            string[] parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            if (parts[0] == "newmtl")
            {
                current = parts[1];
                materials[current] = (Colors.LightGray, null);
            }
            else if (current != null && parts[0] == "Kd" && parts.Length >= 4)
                materials[current] = (Color.FromRgb(Channel(parts[1]), Channel(parts[2]), Channel(parts[3])), materials[current].Map);
            else if (current != null && parts[0] == "map_Kd")
                materials[current] = (materials[current].Color, parts[^1]);
        }
        return materials;
    }

    private static byte Channel(string text) => (byte)Math.Clamp(Number(text) * 255.0, 0, 255);

    private static double Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : 0;

    private static Dictionary<string, MeshGeometry3D> ParseMeshes(string obj)
    {
        List<Point3D> positions = new();
        List<Point> coords = new();
        List<Vector3D> normals = new();
        Dictionary<string, MeshGeometry3D> meshes = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<(int, int, int), int>> lookups = new(StringComparer.Ordinal);
        string material = "";
        List<int> corners = new();
        foreach (string raw in obj.Split('\n'))
        {
            string[] parts = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;
            switch (parts[0])
            {
                case "v" when parts.Length >= 4:
                    positions.Add(new Point3D(Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;
                case "vt" when parts.Length >= 3:
                    coords.Add(new Point(Number(parts[1]), 1.0 - Number(parts[2])));
                    break;
                case "vn" when parts.Length >= 4:
                    normals.Add(new Vector3D(Number(parts[1]), Number(parts[2]), Number(parts[3])));
                    break;
                case "usemtl":
                    material = parts[1];
                    break;
                case "f" when parts.Length >= 4:
                    if (!meshes.TryGetValue(material, out MeshGeometry3D? mesh))
                    {
                        mesh = new MeshGeometry3D();
                        meshes[material] = mesh;
                        lookups[material] = new Dictionary<(int, int, int), int>();
                    }
                    Dictionary<(int, int, int), int> lookup = lookups[material];
                    corners.Clear();
                    for (int i = 1; i < parts.Length; i++)
                    {
                        string[] refs = parts[i].Split('/');
                        int v = Resolve(refs[0], positions.Count);
                        int t = refs.Length > 1 ? Resolve(refs[1], coords.Count) : -1;
                        int n = refs.Length > 2 ? Resolve(refs[2], normals.Count) : -1;
                        if (v < 0)
                        {
                            corners.Clear();
                            break;
                        }
                        if (!lookup.TryGetValue((v, t, n), out int index))
                        {
                            index = mesh.Positions.Count;
                            lookup[(v, t, n)] = index;
                            mesh.Positions.Add(positions[v]);
                            mesh.TextureCoordinates.Add(t >= 0 ? coords[t] : new Point(0, 0));
                            mesh.Normals.Add(n >= 0 ? normals[n] : new Vector3D(0, 1, 0));
                        }
                        corners.Add(index);
                    }
                    for (int i = 1; i + 1 < corners.Count; i++)
                    {
                        mesh.TriangleIndices.Add(corners[0]);
                        mesh.TriangleIndices.Add(corners[i]);
                        mesh.TriangleIndices.Add(corners[i + 1]);
                    }
                    break;
            }
        }
        return meshes;
    }

    private static int Resolve(string text, int count)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) || index == 0)
            return -1;
        index = index > 0 ? index - 1 : count + index;
        return index >= 0 && index < count ? index : -1;
    }
}
