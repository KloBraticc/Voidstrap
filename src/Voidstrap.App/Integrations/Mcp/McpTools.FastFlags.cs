using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.Models.Persistable;
using Voidstrap.UI.Elements.Editor;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
    private static McpToolResult ListFastFlags(JsonObject args)
    {
        string? filter = McpArgs.String(args, "filter")?.Trim();
        bool includePresets = McpArgs.Flag(args, "includePresets", true);
        bool includeAllowlist = McpArgs.Flag(args, "includeAllowlist", true);

        IReadOnlyDictionary<string, string> allowlist = GetEffectiveAllowlist();
        Dictionary<string, TrackerFlagEntry>? trackerCache = _trackerFlagsCache;

        Dictionary<string, object> prop = App.FastFlags.Prop;
        IEnumerable<KeyValuePair<string, object>> query = prop;
        if (!string.IsNullOrEmpty(filter))
            query = query.Where(kv => kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var root = new JsonObject();
        var flagsObj = new JsonObject();
        foreach (KeyValuePair<string, object> kv in query.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            bool isAllowed = allowlist.TryGetValue(kv.Key, out string? category);
            var entry = new JsonObject
            {
                ["value"] = kv.Value?.ToString() ?? string.Empty,
                ["isAllowlisted"] = isAllowed,
                ["allowlistCategory"] = category,
            };

            if (trackerCache != null)
            {
                bool inTracker = trackerCache.TryGetValue(kv.Key, out TrackerFlagEntry? trackerInfo);
                entry["existsInTracker"] = inTracker;
                if (inTracker && trackerInfo != null)
                {
                    entry["trackerSource"] = trackerInfo.Source;
                    entry["defaultValue"] = trackerInfo.DefaultValue;
                }
            }

            flagsObj[kv.Key] = entry;
        }

        root["configuredFlags"] = flagsObj;

        if (includeAllowlist)
        {
            var allowlistFlagsObj = new JsonObject();
            foreach (
                KeyValuePair<string, string> kv in allowlist
                    .OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            )
            {
                if (
                    !string.IsNullOrEmpty(filter)
                    && !kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !kv.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                allowlistFlagsObj[kv.Key] = kv.Value;
            }

            root["robloxAllowlist"] = new JsonObject
            {
                ["announcementUrl"] = RobloxFastFlagAllowlist.AnnouncementUrl,
                ["jsonFeedUrl"] = RobloxFastFlagAllowlist.AnnouncementUrl + ".json",
                ["count"] = allowlist.Count,
                ["flags"] = allowlistFlagsObj,
                ["recommendedProfiles"] = new JsonObject
                {
                    ["Performance"] = new JsonObject
                    {
                        ["DFFlagTextureQualityOverrideEnabled"] = "True",
                        ["DFIntTextureQualityOverride"] = "0",
                        ["FIntDebugForceMSAASamples"] = "1",
                    },
                    ["Balanced"] = new JsonObject
                    {
                        ["DFFlagTextureQualityOverrideEnabled"] = "True",
                        ["DFIntTextureQualityOverride"] = "2",
                        ["FIntDebugForceMSAASamples"] = "2",
                    },
                    ["Quality"] = new JsonObject
                    {
                        ["DFFlagTextureQualityOverrideEnabled"] = "True",
                        ["DFIntTextureQualityOverride"] = "3",
                        ["FIntDebugForceMSAASamples"] = "4",
                    },
                },
            };

            var sourcesObj = new JsonObject();
            foreach (var src in TrackerSources)
                sourcesObj[src.Name] = src.Url;

            root["maximumAdhdTracker"] = new JsonObject
            {
                ["trackerRepository"] = "https://github.com/MaximumADHD/Roblox-FFlag-Tracker",
                ["clientTrackerRepository"] = "https://github.com/MaximumADHD/Roblox-Client-Tracker",
                ["cachedFlagCount"] = trackerCache?.Count ?? 0,
                ["sources"] = sourcesObj,
            };
        }

        if (includePresets)
        {
            var presetsObj = new JsonObject();
            foreach (
                KeyValuePair<string, string> preset in FastFlagManager.PresetFlags.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            )
            {
                if (
                    !string.IsNullOrEmpty(filter)
                    && !preset.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    && !preset.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)
                )
                    continue;

                presetsObj[preset.Key] = new JsonObject
                {
                    ["flag"] = preset.Value,
                    ["isAllowlisted"] = allowlist.ContainsKey(preset.Value),
                    ["currentValue"] = App.FastFlags.GetPreset(preset.Key),
                };
            }
            root["availablePresets"] = presetsObj;
        }

        return new McpToolResult(root.ToJsonString(IndentedJson));
    }

    private static McpToolResult SetFastFlags(JsonObject args)
    {
        JsonObject? flags = args["flags"] as JsonObject;
        JsonObject? presets = args["presets"] as JsonObject;
        string? recommendedProfile = McpArgs.String(args, "recommendedProfile")?.Trim();
        bool replaceAll = McpArgs.Flag(args, "replaceAll", false);

        if (
            (flags == null || flags.Count == 0)
            && (presets == null || presets.Count == 0)
            && string.IsNullOrWhiteSpace(recommendedProfile)
            && !replaceAll
        )
            return new McpToolResult(
                "Provide 'flags', 'presets', 'recommendedProfile' ('Performance', 'Balanced', 'Quality'), or set 'replaceAll' to true.",
                true
            );

        int setCount = 0;
        int removedCount = 0;
        int allowlistedSetCount = 0;
        IReadOnlyDictionary<string, string> allowlist = GetEffectiveAllowlist();
        bool dryRun = McpArgs.Flag(args, "dryRun", false);
        Dictionary<string, object> snapshot = new(App.FastFlags.Prop, App.FastFlags.Prop.Comparer);

        if (replaceAll)
        {
            removedCount += App.FastFlags.Prop.Count;
            App.FastFlags.Prop.Clear();
        }

        if (!string.IsNullOrWhiteSpace(recommendedProfile))
        {
            Dictionary<string, string>? profileFlags = recommendedProfile.ToLowerInvariant() switch
            {
                "performance" => new Dictionary<string, string>
                {
                    ["DFFlagTextureQualityOverrideEnabled"] = "True",
                    ["DFIntTextureQualityOverride"] = "0",
                    ["FIntDebugForceMSAASamples"] = "1",
                },
                "balanced" => new Dictionary<string, string>
                {
                    ["DFFlagTextureQualityOverrideEnabled"] = "True",
                    ["DFIntTextureQualityOverride"] = "2",
                    ["FIntDebugForceMSAASamples"] = "2",
                },
                "quality" => new Dictionary<string, string>
                {
                    ["DFFlagTextureQualityOverrideEnabled"] = "True",
                    ["DFIntTextureQualityOverride"] = "3",
                    ["FIntDebugForceMSAASamples"] = "4",
                },
                _ => null,
            };

            if (profileFlags == null)
            {
                App.FastFlags.Prop.Clear();
                foreach (KeyValuePair<string, object> pair in snapshot)
                    App.FastFlags.Prop[pair.Key] = pair.Value;
                return new McpToolResult(
                    $"Unknown recommendedProfile '{recommendedProfile}'. Use 'Performance', 'Balanced', or 'Quality'.",
                    true
                );
            }

            foreach (KeyValuePair<string, string> kv in profileFlags)
            {
                App.FastFlags.Prop[kv.Key] = kv.Value;
                setCount++;
                if (allowlist.ContainsKey(kv.Key))
                    allowlistedSetCount++;
            }
        }

        if (flags != null)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in flags)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                if (kv.Value == null)
                {
                    if (App.FastFlags.Prop.Remove(kv.Key))
                        removedCount++;
                    continue;
                }

                string val = kv.Value is JsonValue jv && jv.TryGetValue<bool>(out bool b) ? (b ? "True" : "False") : kv.Value.ToString();

                App.FastFlags.Prop[kv.Key] = val;
                setCount++;
                if (allowlist.ContainsKey(kv.Key))
                    allowlistedSetCount++;
            }
        }

        if (presets != null)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in presets)
            {
                if (string.IsNullOrWhiteSpace(kv.Key))
                    continue;

                object? rawVal = ExtractPrimitive(kv.Value);
                App.FastFlags.SetPreset(kv.Key, rawVal);
                if (rawVal == null)
                {
                    removedCount++;
                }
                else
                {
                    setCount++;
                    if (FastFlagManager.PresetFlags.TryGetValue(kv.Key, out string? mappedFlag) && allowlist.ContainsKey(mappedFlag))
                        allowlistedSetCount++;
                }
            }
        }

        if (dryRun)
        {
            Dictionary<string, object> preview = new(App.FastFlags.Prop, App.FastFlags.Prop.Comparer);
            App.FastFlags.Prop.Clear();
            foreach (KeyValuePair<string, object> pair in snapshot)
                App.FastFlags.Prop[pair.Key] = pair.Value;
            var diff = new StringBuilder();
            foreach (string key in preview.Keys.Union(snapshot.Keys).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                bool had = snapshot.TryGetValue(key, out object? oldValue);
                bool has = preview.TryGetValue(key, out object? newValue);
                if (had && has && string.Equals(oldValue?.ToString(), newValue?.ToString(), StringComparison.Ordinal))
                    continue;
                diff.AppendLine(
                    had && has ? $"  {key}: {oldValue} would become {newValue}"
                    : has ? $"  {key}: would be added as {newValue}"
                    : $"  {key}: would be removed"
                );
            }
            return new McpToolResult(
                "Dry run, nothing was changed." + Environment.NewLine + (diff.Length == 0 ? "  no differences" : diff.ToString().TrimEnd())
            );
        }

        PushUndo(
            $"set_fastflags ({setCount} set, {removedCount} removed)",
            () =>
            {
                App.FastFlags.Prop.Clear();
                foreach (KeyValuePair<string, object> pair in snapshot)
                    App.FastFlags.Prop[pair.Key] = pair.Value;
                App.FastFlags.Save();
                FastFlagEditorPage.RefreshActivePage();
            }
        );

        App.FastFlags.Save();
        FastFlagEditorPage.RefreshActivePage();

        return new McpToolResult(
            $"Saved FastFlags ({setCount} set [{allowlistedSetCount} on Roblox allowlist], {removedCount} removed, {App.FastFlags.Prop.Count} total active)."
        );
    }

    private sealed record TrackerSourceDef(string Name, string Url, bool NamesOnly);

    private sealed record TrackerFlagEntry(string DefaultValue, string Source);

    private static readonly TrackerSourceDef[] TrackerSources =
    {
        new("Roblox ClientSettings", "https://clientsettings.roblox.com/v2/settings/application/PCDesktopClient", false),
        new(
            "PCDesktopClient",
            "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCDesktopClient.json",
            false
        ),
        new(
            "PCClientBootstrapper",
            "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCClientBootstrapper.json",
            false
        ),
        new("PCStudioApp", "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCStudioApp.json", false),
        new("FVariables.txt", "https://raw.githubusercontent.com/MaximumADHD/Roblox-Client-Tracker/refs/heads/roblox/FVariables.txt", true),
    };

    private static readonly string[] KnownFlagPrefixes =
    {
        "FFlag",
        "DFFlag",
        "SFFlag",
        "FInt",
        "DFInt",
        "SFInt",
        "FString",
        "DFString",
        "SFString",
        "FLog",
        "DFLog",
        "SFLog",
        "FDouble",
    };

    private static readonly HttpClient TrackerHttpClient = VpnHttpClient.Create(TimeSpan.FromSeconds(20));
    private static readonly SemaphoreSlim TrackerFetchLock = new(1, 1);

    private static volatile Dictionary<string, TrackerFlagEntry>? _trackerFlagsCache;
    private static volatile Dictionary<string, int>? _trackerSourceCountsCache;


    private static IReadOnlyDictionary<string, string> GetEffectiveAllowlist()
    {
        return RobloxFastFlagAllowlist.Flags;
    }

    private static bool IsAllowlistedFlag(string name)
    {
        return GetEffectiveAllowlist().ContainsKey(name);
    }

    public static async Task<McpToolResult> SearchFastFlagTrackerAsync(JsonObject args, Dispatcher dispatcher, CancellationToken token)
    {
        try
        {
            string? query = McpArgs.String(args, "query")?.Trim();
            string sourceFilter = McpArgs.String(args, "source")?.Trim() ?? "all";
            bool allowlistedOnly = McpArgs.Flag(args, "allowlistedOnly", false);
            bool refreshOnline = McpArgs.Flag(args, "refreshOnline", false);
            bool validateConfigured = McpArgs.Bool(args, "validateConfigured") ?? string.IsNullOrWhiteSpace(query);
            int limit = Math.Clamp(McpArgs.Int(args, "limit", 100, int.MinValue, int.MaxValue), 1, 500);

            Dictionary<string, string> configuredSnapshot = await dispatcher.InvokeAsync(() =>
                App.FastFlags.Prop.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)
            );

            await EnsureTrackerAndAllowlistLoadedAsync(refreshOnline, token).ConfigureAwait(false);

            IReadOnlyDictionary<string, string> allowlist = GetEffectiveAllowlist();
            Dictionary<string, TrackerFlagEntry> trackerFlags =
                _trackerFlagsCache ?? new Dictionary<string, TrackerFlagEntry>(StringComparer.Ordinal);
            Dictionary<string, int> sourceCounts = _trackerSourceCountsCache ?? new Dictionary<string, int>(StringComparer.Ordinal);

            var root = new JsonObject();

            var allowlistFlagsNode = new JsonObject();
            foreach (
                KeyValuePair<string, string> kv in allowlist
                    .OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            )
                allowlistFlagsNode[kv.Key] = kv.Value;

            root["robloxAllowlist"] = new JsonObject
            {
                ["announcementUrl"] = RobloxFastFlagAllowlist.AnnouncementUrl,
                ["jsonFeedUrl"] = RobloxFastFlagAllowlist.AnnouncementUrl + ".json",
                ["onlineLoaded"] = RobloxFastFlagAllowlist.IsOnline,
                ["count"] = allowlist.Count,
                ["flags"] = allowlistFlagsNode,
            };

            var sourcesNode = new JsonObject();
            foreach (TrackerSourceDef def in TrackerSources)
            {
                sourcesNode[def.Name] = new JsonObject { ["url"] = def.Url, ["flagCount"] = sourceCounts.GetValueOrDefault(def.Name, 0) };
            }

            root["maximumAdhdTracker"] = new JsonObject
            {
                ["trackerRepository"] = "https://github.com/MaximumADHD/Roblox-FFlag-Tracker",
                ["clientTrackerRepository"] = "https://github.com/MaximumADHD/Roblox-Client-Tracker",
                ["totalUniqueFlags"] = trackerFlags.Count,
                ["sources"] = sourcesNode,
            };

            if (validateConfigured)
            {
                var validationObj = new JsonObject();
                int validInTrackerCount = 0;
                int allowlistedConfigCount = 0;

                foreach (KeyValuePair<string, string> kv in configuredSnapshot.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    bool inTracker = trackerFlags.TryGetValue(kv.Key, out TrackerFlagEntry? tEntry);
                    bool isAllowed = allowlist.TryGetValue(kv.Key, out string? allowCat);
                    if (inTracker)
                        validInTrackerCount++;
                    if (isAllowed)
                        allowlistedConfigCount++;

                    var tagsArr = new JsonArray();
                    foreach (string tag in FastFlagEditorPage.FastFlagTagHelper.GetTags(kv.Key))
                        tagsArr.Add(tag);

                    validationObj[kv.Key] = new JsonObject
                    {
                        ["configuredValue"] = kv.Value,
                        ["existsInTracker"] = inTracker,
                        ["trackerSource"] = tEntry?.Source,
                        ["defaultValue"] = tEntry?.DefaultValue,
                        ["isAllowlisted"] = isAllowed,
                        ["allowlistCategory"] = allowCat,
                        ["tags"] = tagsArr,
                    };
                }

                root["configuredFlagValidation"] = new JsonObject
                {
                    ["totalConfigured"] = configuredSnapshot.Count,
                    ["validInTrackerCount"] = validInTrackerCount,
                    ["allowlistedCount"] = allowlistedConfigCount,
                    ["flags"] = validationObj,
                };
            }

            bool hasSearchCriteria =
                !string.IsNullOrWhiteSpace(query)
                || allowlistedOnly
                || !string.Equals(sourceFilter, "all", StringComparison.OrdinalIgnoreCase);

            if (hasSearchCriteria)
            {
                string[] terms = string.IsNullOrWhiteSpace(query)
                    ? Array.Empty<string>()
                    : query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var matchesArray = new JsonArray();
                int totalMatches = 0;

                IEnumerable<KeyValuePair<string, TrackerFlagEntry>> candidates;
                if (string.Equals(sourceFilter, "allowlist", StringComparison.OrdinalIgnoreCase) || allowlistedOnly)
                {
                    candidates = allowlist.Keys.Select(k =>
                        trackerFlags.TryGetValue(k, out TrackerFlagEntry? te)
                            ? new KeyValuePair<string, TrackerFlagEntry>(k, te)
                            : new KeyValuePair<string, TrackerFlagEntry>(k, new TrackerFlagEntry(string.Empty, "Roblox Allowlist"))
                    );
                }
                else
                {
                    candidates = trackerFlags;
                }

                foreach (
                    KeyValuePair<string, TrackerFlagEntry> kv in candidates
                        .OrderBy(p => !allowlist.ContainsKey(p.Key))
                        .ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                )
                {
                    if (
                        !string.Equals(sourceFilter, "all", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(sourceFilter, "allowlist", StringComparison.OrdinalIgnoreCase)
                        && !kv.Value.Source.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase)
                    )
                        continue;

                    bool isAllowed = allowlist.TryGetValue(kv.Key, out string? allowCat);
                    if (allowlistedOnly && !isAllowed)
                        continue;

                    if (terms.Length > 0)
                    {
                        bool matchedAll = true;
                        foreach (string term in terms)
                        {
                            if (
                                !kv.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                                && (allowCat == null || !allowCat.Contains(term, StringComparison.OrdinalIgnoreCase))
                            )
                            {
                                matchedAll = false;
                                break;
                            }
                        }
                        if (!matchedAll)
                            continue;
                    }

                    totalMatches++;
                    if (matchesArray.Count >= limit)
                        continue;

                    configuredSnapshot.TryGetValue(kv.Key, out string? currentVal);
                    matchesArray.Add(
                        new JsonObject
                        {
                            ["name"] = kv.Key,
                            ["defaultValue"] = kv.Value.DefaultValue,
                            ["source"] = kv.Value.Source,
                            ["isAllowlisted"] = isAllowed,
                            ["allowlistCategory"] = allowCat,
                            ["configuredValue"] = currentVal,
                        }
                    );
                }

                root["searchResults"] = new JsonObject
                {
                    ["query"] = query ?? string.Empty,
                    ["totalMatches"] = totalMatches,
                    ["returnedCount"] = matchesArray.Count,
                    ["matches"] = matchesArray,
                };
            }

            return new McpToolResult(root.ToJsonString(IndentedJson));
        }
        catch (Exception ex)
        {
            return new McpToolResult("FastFlag tracker search failed: " + ex.Message, true);
        }
    }

    private static async Task EnsureTrackerAndAllowlistLoadedAsync(bool forceRefresh, CancellationToken token)
    {
        if (!forceRefresh && _trackerFlagsCache != null && RobloxFastFlagAllowlist.IsOnline)
            return;

        await TrackerFetchLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _trackerFlagsCache != null && RobloxFastFlagAllowlist.IsOnline)
                return;

            Task<IReadOnlyDictionary<string, string>> allowlistTask = RobloxFastFlagAllowlist.RefreshAsync(forceRefresh, token);
            Task<(string Name, Dictionary<string, string> Flags)>[] sourceTasks = TrackerSources
                .Select(src => FetchTrackerSourceAsync(src, token))
                .ToArray();

            await Task.WhenAll(sourceTasks).ConfigureAwait(false);
            await allowlistTask.ConfigureAwait(false);

            var combined = new Dictionary<string, TrackerFlagEntry>(StringComparer.Ordinal);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Task<(string Name, Dictionary<string, string> Flags)> completed in sourceTasks)
            {
                (string srcName, Dictionary<string, string> flags) = await completed.ConfigureAwait(false);
                counts[srcName] = flags.Count;
                foreach (KeyValuePair<string, string> kv in flags)
                {
                    if (!combined.TryGetValue(kv.Key, out TrackerFlagEntry? existing))
                    {
                        combined[kv.Key] = new TrackerFlagEntry(kv.Value, srcName);
                    }
                    else if (string.IsNullOrEmpty(existing.DefaultValue) && !string.IsNullOrEmpty(kv.Value))
                    {
                        combined[kv.Key] = new TrackerFlagEntry(kv.Value, srcName);
                    }
                }
            }

            if (combined.Count > 0)
            {
                _trackerFlagsCache = combined;
                _trackerSourceCountsCache = counts;
            }
        }
        finally
        {
            TrackerFetchLock.Release();
        }
    }

    private static async Task<(string Name, Dictionary<string, string> Flags)> FetchTrackerSourceAsync(
        TrackerSourceDef source,
        CancellationToken token
    )
    {
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            string content = await Http.GetStringBoundedAsync(TrackerHttpClient, source.Url, token: cts.Token).ConfigureAwait(false);

            if (source.NamesOnly)
            {
                foreach (string rawLine in content.Split('\n'))
                {
                    string line = rawLine.Trim();
                    while (line.StartsWith('['))
                    {
                        int close = line.IndexOf(']');
                        if (close < 0)
                            break;
                        line = line[(close + 1)..].Trim();
                    }
                    int eq = line.IndexOf('=');
                    if (eq >= 0)
                        line = line[..eq].Trim();
                    int sp = line.IndexOf(' ');
                    if (sp >= 0)
                        line = line[..sp].Trim();

                    if (line.Length > 0 && KnownFlagPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)))
                        flags.TryAdd(line, string.Empty);
                }
            }
            else
            {
                using JsonDocument doc = JsonDocument.Parse(content);
                JsonElement root = doc.RootElement;
                if (
                    root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("applicationSettings", out JsonElement nested)
                    && nested.ValueKind == JsonValueKind.Object
                )
                    root = nested;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in root.EnumerateObject())
                    {
                        string val = prop.Value.ValueKind switch
                        {
                            JsonValueKind.True => "True",
                            JsonValueKind.False => "False",
                            JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                            _ => prop.Value.GetRawText(),
                        };
                        flags.TryAdd(prop.Name, val);
                    }
                }
            }
        }
        catch { }

        return (source.Name, flags);
    }
}
