using System;
using System.Collections.Generic;
using System.Net.Http;

namespace Voidstrap;

public static class RobloxFastFlagAllowlist
{
    private const string SettingsUrl = "https://clientsettingscdn.roblox.com/v2/settings/application/PCDesktopClient";

    private const string SettingName = "DFStringAllowedPublicFlags";

    public const string AnnouncementUrl = "https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569";

    private static readonly HttpClient Client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromSeconds(12));

    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    private static volatile IReadOnlyDictionary<string, string>? _remote;

    private static IReadOnlyDictionary<string, string>? _saved;

    private static bool _savedLoaded;

    private static string CachePath => Path.Combine(Paths.Cache, "FastFlagAllowlist.json");

    public static IReadOnlyDictionary<string, string> Flags => _remote ?? LoadSaved() ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public static bool IsOnline => _remote != null;

    public static bool IsAllowed(string name) => Flags.Count == 0 || Flags.ContainsKey(name);

    public static async Task<IReadOnlyDictionary<string, string>> RefreshAsync(bool force = false, CancellationToken token = default)
    {
        const string LOG_IDENT = "RobloxFastFlagAllowlist::RefreshAsync";

        await RefreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_remote != null && !force)
                return _remote;
            string payload = await Voidstrap.Utility.Http.GetStringBoundedAsync(Client, SettingsUrl, token: token).ConfigureAwait(false);
            Dictionary<string, string>? parsed = Parse(payload);
            if (parsed == null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Roblox's allowlist setting was empty or unreadable, keeping the last known list");
                return Flags;
            }
            _remote = parsed;
            Save(parsed);
            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            App.Logger.WriteLine(LOG_IDENT, "Using the last known allowlist: " + ex.Message);
            return Flags;
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    internal static Dictionary<string, string>? Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (
                !document.RootElement.TryGetProperty("applicationSettings", out JsonElement settings)
                || !settings.TryGetProperty(SettingName, out JsonElement encoded)
                || encoded.ValueKind != JsonValueKind.String
            )
                return null;

            using JsonDocument list = JsonDocument.Parse(Convert.FromBase64String(encoded.GetString() ?? string.Empty));
            if (!list.RootElement.TryGetProperty("Allowed", out JsonElement allowed) || allowed.ValueKind != JsonValueKind.Array)
                return null;

            Dictionary<string, string> result = new(StringComparer.Ordinal);
            foreach (JsonElement flag in allowed.EnumerateArray())
            {
                if (flag.GetString() is { Length: > 0 } name)
                    result[name] = name.Contains("CSG", StringComparison.Ordinal) ? "Geometry" : "Rendering";
            }
            return result.Count == 0 ? null : result;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? LoadSaved()
    {
        if (_savedLoaded)
            return _saved;
        _savedLoaded = true;
        try
        {
            if (File.Exists(CachePath))
                _saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.Logger.WriteLine("RobloxFastFlagAllowlist::LoadSaved", "The saved allowlist could not be read: " + ex.Message);
        }
        return _saved is { Count: > 0 } ? _saved : null;
    }

    private static void Save(Dictionary<string, string> flags)
    {
        try
        {
            Directory.CreateDirectory(Paths.Cache);
            File.WriteAllText(CachePath + ".tmp", JsonSerializer.Serialize(flags));
            File.Move(CachePath + ".tmp", CachePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Logger.WriteLine("RobloxFastFlagAllowlist::Save", "The allowlist could not be saved: " + ex.Message);
        }
    }
}
