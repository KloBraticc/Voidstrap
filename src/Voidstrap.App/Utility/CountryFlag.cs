using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility;

internal static class CountryFlag
{
    private static readonly HttpClient _http = VpnHttpClient.Create(TimeSpan.FromSeconds(10));

    private static readonly ConcurrentDictionary<string, BitmapSource?> _images = new(StringComparer.OrdinalIgnoreCase);

    private static readonly SemaphoreSlim _downloadLock = new SemaphoreSlim(4, 4);

    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "USA", "US" }, { "U.S.", "US" }, { "U.S.A.", "US" }, { "America", "US" },
        { "UK", "GB" }, { "Great Britain", "GB" }, { "England", "GB" }, { "Scotland", "GB" }, { "Wales", "GB" },
        { "Northern Ireland", "GB" }, { "Britain", "GB" },
        { "UAE", "AE" }, { "Holland", "NL" }, { "Korea", "KR" }, { "Czechia", "CZ" }, { "Czech Republic", "CZ" },
        { "Russia", "RU" }, { "Russian Federation", "RU" }, { "Vietnam", "VN" }, { "Viet Nam", "VN" },
        { "Turkey", "TR" }, { "Turkiye", "TR" }, { "Ivory Coast", "CI" }, { "Cape Verde", "CV" },
        { "Macedonia", "MK" }, { "Swaziland", "SZ" }, { "Burma", "MM" }, { "East Timor", "TL" },
        { "Congo Kinshasa", "CD" }, { "Congo Brazzaville", "CG" }, { "Palestine", "PS" },
        { "Hong Kong SAR", "HK" }, { "Macau", "MO" }, { "Bolivia", "BO" }, { "Laos", "LA" },
        { "Syria", "SY" }, { "Iran", "IR" }, { "Tanzania", "TZ" }, { "Moldova", "MD" },
        { "Brunei", "BN" }, { "Venezuela", "VE" }, { "South Korea", "KR" }, { "North Korea", "KP" },
    };

    private static readonly Lazy<Dictionary<string, string>> _nameToIso = new(BuildNameIndex);

    private static Dictionary<string, string> BuildNameIndex()
    {
        Dictionary<string, string> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (CultureInfo culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                RegionInfo region = new RegionInfo(culture.Name);
                string iso = region.TwoLetterISORegionName.ToUpperInvariant();
                if (iso.Length != 2)
                    continue;
                map.TryAdd(region.EnglishName, iso);
                map.TryAdd(region.DisplayName, iso);
                map.TryAdd(region.NativeName, iso);
                map.TryAdd(region.ThreeLetterISORegionName, iso);
                map.TryAdd(iso, iso);
            }
            catch (ArgumentException)
            {
            }
        }
        foreach (KeyValuePair<string, string> alias in _aliases)
            map[alias.Key] = alias.Value;
        return map;
    }

    public static string ToIso2(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return string.Empty;
        string trimmed = country.Trim();
        if (_nameToIso.Value.TryGetValue(trimmed, out string? mapped))
            return mapped;
        if (trimmed.Length != 2 || !char.IsLetter(trimmed[0]) || !char.IsLetter(trimmed[1]))
            return string.Empty;
        string upper = trimmed.ToUpperInvariant();
        try
        {
            return new RegionInfo(upper).TwoLetterISORegionName.Equals(upper, StringComparison.OrdinalIgnoreCase)
                ? upper
                : string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    public static string Canonical(string? country)
    {
        if (string.IsNullOrWhiteSpace(country))
            return string.Empty;
        string trimmed = country.Trim();
        string iso = ToIso2(trimmed);
        if (iso.Length == 2)
            return ToDisplayName(iso);
        return trimmed.Length > 2 ? trimmed : string.Empty;
    }

    public static string ToDisplayName(string? country)
    {
        string iso = ToIso2(country);
        if (iso.Length != 2)
            return country?.Trim() ?? string.Empty;
        try
        {
            return new RegionInfo(iso).EnglishName;
        }
        catch (ArgumentException)
        {
            return country?.Trim() ?? string.Empty;
        }
    }

    public static async Task<BitmapSource?> GetImageAsync(string? country, CancellationToken token = default)
    {
        string iso = ToIso2(country);
        if (iso.Length != 2)
            return null;
        if (_images.TryGetValue(iso, out BitmapSource? cached))
            return cached;

        string path = Path.Combine(Paths.Cache, "Flags", iso.ToLowerInvariant() + ".png");
        BitmapSource? image = Decode(path);
        if (image == null)
        {
            await _downloadLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                image = Decode(path);
                if (image == null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using HttpResponseMessage response = await _http
                        .GetAsync("https://flagcdn.com/w40/" + iso.ToLowerInvariant() + ".png", HttpCompletionOption.ResponseHeadersRead, token)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        _images[iso] = null;
                        return null;
                    }
                    await Http.DownloadToFileBoundedAsync(response.Content, path, 256 * 1024, token).ConfigureAwait(false);
                    image = Decode(path);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("CountryFlag::GetImage", "Could not load the flag for " + iso + ": " + ex.Message);
                return null;
            }
            finally
            {
                _downloadLock.Release();
            }
        }

        _images[iso] = image;
        return image;
    }

    private static BitmapSource? Decode(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
                return null;
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("CountryFlag::Decode", "Could not decode " + path + ": " + ex.Message);
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
            return null;
        }
    }
}
