using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Resources;
using Voidstrap.Resources;

namespace Voidstrap;

internal static class LanguageMetadata
{
    public const string NativeIdentifier = "en-US";

    private const string FlagUriFormat = "pack://application:,,,/Resources/Flags/{0}.png";

    private static readonly ResourceManager _resources = new Voidstrap.Utility.CompressedResourceManager("Voidstrap.Resources.Strings", typeof(Strings).Assembly);

    private static readonly object _gate = new object();

    private static readonly Dictionary<string, string?> _flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> _coverage = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string>? _neutralKeys;

    public static string? GetFlagSource(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier == Locale.DefaultLocale)
        {
            return null;
        }
        lock (_gate)
        {
            if (_flags.TryGetValue(identifier, out string? cached))
            {
                return cached;
            }
            string? source = ResolveFlagSource(identifier);
            _flags[identifier] = source;
            return source;
        }
    }

    public static int GetCompletionPercent(string identifier)
    {
        if (string.IsNullOrEmpty(identifier) || identifier == Locale.DefaultLocale)
        {
            return -1;
        }
        lock (_gate)
        {
            if (_coverage.TryGetValue(identifier, out int cached))
            {
                return cached;
            }
            int percent = MeasureCompletion(identifier);
            _coverage[identifier] = percent;
            return percent;
        }
    }

    public static string GetStatusText(string identifier)
    {
        if (string.Equals(identifier, NativeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return Voidstrap.Resources.Strings.Menu_Appearance_Language_NativeStatus;
        }
        int percent = GetCompletionPercent(identifier);
        if (percent < 0)
        {
            return string.Empty;
        }
        return string.Format(CultureInfo.CurrentCulture, Voidstrap.Resources.Strings.Menu_Appearance_Language_CompletionStatus, percent);
    }

    private static readonly Dictionary<string, string> FlagCountries = new(StringComparer.OrdinalIgnoreCase) { ["af"] = "za", ["am"] = "et", ["ar"] = "sa", ["az"] = "az", ["be"] = "by", ["bg"] = "bg", ["bn"] = "bd", ["bs"] = "ba", ["ca"] = "ad", ["cs"] = "cz", ["cy"] = "gb", ["da"] = "dk", ["de"] = "de", ["el"] = "gr", ["en-US"] = "us", ["eo"] = "eu", ["es-ES"] = "es", ["et"] = "ee", ["eu"] = "es", ["fa"] = "ir", ["fi"] = "fi", ["fil"] = "ph", ["fr"] = "fr", ["ga"] = "ie", ["gl"] = "es", ["gu"] = "in", ["ha"] = "ng", ["he"] = "il", ["hi"] = "in", ["hr"] = "hr", ["hu"] = "hu", ["hy"] = "am", ["id"] = "id", ["ig"] = "ng", ["is"] = "is", ["it"] = "it", ["ja"] = "jp", ["ka"] = "ge", ["kk"] = "kz", ["km"] = "kh", ["kn"] = "in", ["ko"] = "kr", ["ky"] = "kg", ["lb"] = "lu", ["lo"] = "la", ["lt"] = "lt", ["lv"] = "lv", ["mg"] = "mg", ["mi"] = "nz", ["mk"] = "mk", ["ml"] = "in", ["mn"] = "mn", ["mr"] = "in", ["ms"] = "my", ["mt"] = "mt", ["my"] = "mm", ["ne"] = "np", ["nl"] = "nl", ["no"] = "no", ["or"] = "in", ["pa"] = "in", ["pl"] = "pl", ["ps"] = "af", ["pt-BR"] = "br", ["ro"] = "ro", ["ru"] = "ru", ["rw"] = "rw", ["sd"] = "pk", ["si"] = "lk", ["sk"] = "sk", ["sl"] = "si", ["so"] = "so", ["sq"] = "al", ["sr"] = "rs", ["st"] = "ls", ["sv-SE"] = "se", ["sw"] = "ke", ["ta"] = "in", ["te"] = "in", ["tg"] = "tj", ["th"] = "th", ["tk"] = "tm", ["tr"] = "tr", ["ug"] = "cn", ["uk"] = "ua", ["ur"] = "pk", ["uz"] = "uz", ["vi"] = "vn", ["xh"] = "za", ["yi"] = "il", ["yo"] = "ng", ["zh-CN"] = "cn", ["zh-TW"] = "tw", ["zu"] = "za" };

    private static string? ResolveFlagSource(string identifier)
    {
        if (FlagCountries.TryGetValue(identifier, out string? country))
            return string.Format(CultureInfo.InvariantCulture, FlagUriFormat, country);
        try
        {
            CultureInfo specific = CultureInfo.CreateSpecificCulture(identifier);
            RegionInfo region = new RegionInfo(specific.Name);
            return string.Format(CultureInfo.InvariantCulture, FlagUriFormat, region.TwoLetterISORegionName.ToLowerInvariant());
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("LanguageMetadata::ResolveFlagSource", "No region for " + identifier + ": " + ex.Message);
            return null;
        }
    }

    private static int MeasureCompletion(string identifier)
    {
        try
        {
            HashSet<string> neutral = GetNeutralKeys();
            if (neutral.Count == 0)
            {
                return -1;
            }
            HashSet<string> translated = GetKeys(new CultureInfo(identifier));
            if (translated.Count == 0)
            {
                return -1;
            }
            int covered = 0;
            foreach (string key in neutral)
            {
                if (translated.Contains(key))
                {
                    covered++;
                }
            }
            return (int)Math.Round(100.0 * covered / neutral.Count, MidpointRounding.AwayFromZero);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("LanguageMetadata::MeasureCompletion", "Could not measure " + identifier + ": " + ex.Message);
            return -1;
        }
    }

    private static HashSet<string> GetNeutralKeys()
    {
        return _neutralKeys ??= GetKeys(CultureInfo.InvariantCulture);
    }

    private static HashSet<string> GetKeys(CultureInfo culture)
    {
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            ResourceSet? set = _resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
            if (set == null)
            {
                return keys;
            }
            foreach (DictionaryEntry entry in set)
            {
                if (entry.Key is string key && entry.Value is string value && !string.IsNullOrWhiteSpace(value))
                {
                    keys.Add(key);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("LanguageMetadata::GetKeys", "Could not read resources for " + culture.Name + ": " + ex.Message);
        }
        return keys;
    }
}
