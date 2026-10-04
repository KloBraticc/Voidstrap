using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Voidstrap.Models.Persistable;

namespace Voidstrap.Utility;

internal sealed record BootstrapperImportPlan(JsonObject Settings, Dictionary<string, object>? Flags, int Skipped, string SourceSettingsPath, string SourceFlagsPath);

internal static class BootstrapperSettingsImport
{
    private static readonly HashSet<string> BooleanSettings = new(StringComparer.Ordinal)
    {
        "CheckForUpdates", "ConfirmLaunches", "UseFastFlagManager", "WPFSoftwareRender",
        "EnableAnalytics", "BackgroundUpdatesEnabled", "EnableActivityTracking",
        "UseDiscordRichPresence", "HideRPCButtons", "ShowAccountOnRichPresence",
        "ShowServerDetails", "UseDisableAppPatch", "FakeBorderlessFullscreen"
    };

    private static readonly string[] BloxstrapStyles =
    [
        "VistaDialog", "LegacyDialog2008", "LegacyDialog2011", "ProgressDialog",
        "ClassicFluentDialog", "ByfronDialog", "FluentDialog", "FluentAeroDialog", "CustomDialog"
    ];

    private static readonly string[] FishstrapStyles =
    [
        "VistaDialog", "LegacyDialog2008", "LegacyDialog2011", "ProgressDialog",
        "ClassicFluentDialog", "TwentyFiveDialog", "TerminalDialog", "ByfronDialog",
        "FluentDialog", "FluentAeroDialog", "CustomDialog"
    ];

    private static readonly string[] BloxstrapIcons =
    [
        "IconBloxstrap", "Icon2008", "Icon2011", "IconEarly2015", "IconLate2015",
        "Icon2017", "Icon2019", "Icon2022", "IconCustom", "IconBloxstrapClassic"
    ];

    private static readonly string[] FishstrapIcons =
    [
        "IconFishstrap", "IconBloxstrap", "Icon2008", "Icon2011", "IconEarly2015",
        "IconLate2015", "Icon2017", "Icon2019", "Icon2022", "IconCustom", "IconBloxstrapClassic"
    ];

    public static string? FindSettings(string source)
    {
        ValidateSource(source);
        if (Platform.IsWindows)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + source);
            if (key?.GetValue("InstallLocation") is string location)
            {
                string registered = Path.Combine(location, "Settings.json");
                if (File.Exists(registered))
                    return registered;
            }
        }
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), source, "Settings.json");
        return File.Exists(standard) ? standard : null;
    }

    public static async Task<BootstrapperImportPlan> ReadAsync(string source, string settingsPath, CancellationToken token)
    {
        ValidateSource(source);
        string text = await JsonFile.ReadTextAsync(settingsPath, 4 * 1024 * 1024, token).ConfigureAwait(false);
        JsonObject original = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
            ?? throw new InvalidDataException("The source settings must be a JSON object");
        JsonObject settings = new();
        int skipped = 0;
        foreach ((string name, JsonNode? value) in original)
        {
            token.ThrowIfCancellationRequested();
            if (BooleanSettings.Contains(name))
            {
                settings[name] = value?.GetValue<bool>() ?? throw new InvalidDataException(name + " must contain a boolean");
                continue;
            }
            if (name is "Locale" or "BootstrapperTitle" or "Channel" or "RobloxTitle")
            {
                string content = value?.GetValue<string>() ?? throw new InvalidDataException(name + " must contain text");
                if (name == "Locale" && !Locale.SupportedLocales.ContainsKey(content))
                {
                    skipped++;
                    continue;
                }
                if (name == "Channel")
                {
                    if (content.Length is < 1 or > 64 || content.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '.' and not '-'))
                        throw new InvalidDataException("The source channel is invalid");
                    settings["IsChannelEnabled"] = !content.Equals("LIVE", StringComparison.OrdinalIgnoreCase);
                }
                settings[name] = name == "BootstrapperTitle" && content == source ? App.ProjectName : content;
                continue;
            }
            string? choice = name switch
            {
                "Theme" => EnumName(value, ["Default", "Light", "Dark"]),
                "BootstrapperStyle" => EnumName(value, source == "Fishstrap" ? FishstrapStyles : BloxstrapStyles),
                "BootstrapperIcon" => EnumName(value, source == "Fishstrap" ? FishstrapIcons : BloxstrapIcons),
                "RobloxIcon" when source == "Fishstrap" => EnumName(value, ["IconDefault", "Icon2008", "Icon2011", "IconEarly2015", "IconLate2015", "Icon2017", "Icon2019", "Icon2022", "IconCustom"]),
                "CleanerOptions" when source == "Fishstrap" => EnumName(value, ["Never", "OneDay", "OneWeek", "TwoWeeks", "OneMonth", "TwoMonths"]),
                _ => null
            };
            if (name == "Theme" && Enum.TryParse(choice, out Enums.Theme theme))
                settings["Theme2"] = (int)theme;
            else if (name == "BootstrapperStyle" && choice != "CustomDialog" && Enum.TryParse(choice, out Enums.BootstrapperStyle style))
                settings[name] = (int)style;
            else if (name is "BootstrapperIcon" or "RobloxIcon" && choice != null)
            {
                if (choice is "IconBloxstrap" or "IconFishstrap" or "IconDefault")
                    choice = "IconVoidstrap";
                string pathName = name + "CustomLocation";
                string? customPath = original[pathName]?.GetValue<string>();
                if (Enum.TryParse(choice, out Enums.BootstrapperIcon icon) && (icon != Enums.BootstrapperIcon.IconCustom || File.Exists(customPath)))
                {
                    settings[name] = (int)icon;
                    if (icon == Enums.BootstrapperIcon.IconCustom)
                        settings[pathName] = customPath;
                }
                else
                    skipped++;
            }
            else if (name == "CleanerOptions" && Enum.TryParse(choice, out Enums.CleanerOptions cleaner))
                settings[name] = (int)cleaner;
            else if (name == "CleanerDirectories" && source == "Fishstrap")
            {
                string[] directories = value?.Deserialize<string[]>() ?? throw new InvalidDataException("Cleaner directories must contain a list");
                if (directories.Any(directory => directory is not "RobloxCache" and not "RobloxStudioCache" and not "RobloxLogs" and not "FishstrapLogs"))
                    throw new InvalidDataException("A source cleaner directory is unsupported");
                settings[name] = JsonSerializer.SerializeToNode(directories.Select(directory => directory == "FishstrapLogs" ? "VoidstrapLogs" : directory).Distinct().ToArray());
            }
            else if (name == "CustomIntegrations")
            {
                if (value is not JsonArray integrations)
                    throw new InvalidDataException("Custom integrations must contain a list");
                JsonArray importedIntegrations = new();
                foreach (JsonNode? item in integrations)
                {
                    if (item is not JsonObject integration || string.IsNullOrWhiteSpace(integration["Location"]?.GetValue<string>()))
                        throw new InvalidDataException("A source custom integration is invalid");
                    JsonObject imported = new();
                    foreach (string field in new[] { "Name", "Location", "LaunchArgs", "AutoClose", "Delay", "PreLaunch" })
                        if (integration.TryGetPropertyValue(field, out JsonNode? entry))
                            imported[field] = entry?.DeepClone();
                    var parsed = imported.Deserialize<Models.CustomIntegration>(JsonOptions.Tolerant)!;
                    if (parsed.Name == null || parsed.LaunchArgs == null || integration["Delay"] is JsonNode delay && delay.GetValue<int>() < 0)
                        throw new InvalidDataException("A source custom integration is invalid");
                    importedIntegrations.Add(JsonSerializer.SerializeToNode(parsed));
                }
                settings[name] = importedIntegrations;
            }
            else if (name is "BootstrapperIconCustomLocation" or "RobloxIconCustomLocation")
            {
                continue;
            }
            else
                skipped++;
        }

        Dictionary<string, object>? flags = null;
        string flagPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath))!, "Modifications", "ClientSettings", "ClientAppSettings.json");
        string? flagText = null;
        try
        {
            flagText = await JsonFile.ReadTextAsync(flagPath, 8 * 1024 * 1024, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        if (flagText != null)
        {
            using JsonDocument document = JsonDocument.Parse(flagText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The source FastFlags must be a JSON object");
            flags = new(StringComparer.Ordinal);
            foreach (JsonProperty flag in document.RootElement.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(flag.Name) || flag.Name.Any(char.IsWhiteSpace))
                    throw new InvalidDataException("A source FastFlag name is invalid");
                object content = flag.Value.ValueKind switch
                {
                    JsonValueKind.String => flag.Value.GetString()!,
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Number => flag.Value.GetRawText(),
                    _ => throw new InvalidDataException("A source FastFlag value is invalid: " + flag.Name)
                };
                if (!flags.TryAdd(flag.Name, content))
                    throw new InvalidDataException("A source FastFlag is duplicated: " + flag.Name);
            }
        }
        if (settings.Count == 0 && (flags == null || flags.Count == 0))
            throw new InvalidDataException("No compatible settings were found");
        return new(settings, flags, skipped, Path.GetFullPath(settingsPath), flagPath);
    }

    public static void Apply(BootstrapperImportPlan plan, JsonManager<AppSettings> settings, FastFlagManager flags)
    {
        StringComparer comparer = Platform.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (string destination in new[] { settings.FileLocation, flags.FileLocation })
            if (comparer.Equals(Path.GetFullPath(destination), plan.SourceSettingsPath) || comparer.Equals(Path.GetFullPath(destination), plan.SourceFlagsPath))
                throw new InvalidDataException("Select settings from another bootstrapper installation");
        AppSettings previous = settings.Prop;
        Dictionary<string, object> previousFlags = flags.Prop;
        JsonObject merged = JsonSerializer.SerializeToNode(previous)!.AsObject();
        foreach ((string name, JsonNode? value) in plan.Settings)
            merged[name] = value?.DeepClone();
        AppSettings imported = merged.Deserialize<AppSettings>(JsonOptions.Tolerant)!;
        Dictionary<string, object> importedFlags = previousFlags.ToDictionary(pair => pair.Key, pair => (object)(pair.Value?.ToString() ?? ""));
        if (plan.Flags != null)
            foreach ((string name, object value) in plan.Flags)
                importedFlags[name] = value;
        bool settingsSaved = false;
        try
        {
            settings.Prop = imported;
            settings.SaveChecked();
            settingsSaved = true;
            if (plan.Flags != null)
            {
                flags.Prop = importedFlags;
                flags.SaveChecked();
                flags.OriginalProp = new(importedFlags);
            }
        }
        catch (Exception failure)
        {
            settings.Prop = previous;
            flags.Prop = previousFlags;
            if (settingsSaved)
            {
                try
                {
                    settings.SaveChecked(false);
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException("The import failed and the previous settings could not be restored", failure, rollbackFailure);
                }
            }
            throw;
        }
    }

    private static string? EnumName(JsonNode? node, string[] names)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<int>(out int number))
                return number >= 0 && number < names.Length ? names[number] : null;
            if (value.TryGetValue<string>(out string? name))
                return names.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    private static void ValidateSource(string source)
    {
        if (source is not "Bloxstrap" and not "Fishstrap")
            throw new ArgumentException("Select Bloxstrap or Fishstrap", nameof(source));
    }
}
