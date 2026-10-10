using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Voidstrap.UI.Elements.Overlay;

public enum BrowserLayout
{
    Horizontal,
    Compact,
    Vertical
}

// Everything the browser keeps between runs: your tabs and where you were on them, recently closed tabs,
// the sites you visit for suggestions and the new tab page, and every browser setting
public sealed class BrowserState
{
    public const int MaxHistory = 400;
    public const int MaxClosed = 15;

    public int Active { get; set; }
    public List<SavedTab> Tabs { get; set; } = new();
    public List<SavedTab> Closed { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();
    public BrowserSettings Settings { get; set; } = new();

    private static string FilePath => Path.Combine(Paths.Config, "browser.json");

    public static BrowserState Load()
    {
        try
        {
            string path = FilePath;
            if (File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024)
            {
                BrowserState? state = JsonSerializer.Deserialize<BrowserState>(File.ReadAllText(path));
                if (state != null)
                {
                    state.Tabs ??= new();
                    state.Closed ??= new();
                    state.History ??= new();
                    state.Settings ??= new();
                    state.Settings.Normalize();
                    return state;
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("SessionWebBrowser", "The saved browser state could not be read: " + ex.Message);
        }
        return new BrowserState();
    }

    // Written to a side file first and swapped in, so a crash mid-save never loses your tabs
    public static async Task SaveAsync(string json)
    {
        string path = FilePath;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
        });
    }

    public void Visit(string url, string? title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;
        HistoryEntry? entry = History.FirstOrDefault(item => item.Url == url);
        if (entry == null)
        {
            entry = new HistoryEntry { Url = url };
            History.Add(entry);
        }
        entry.Visits++;
        entry.LastVisit = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(title))
            entry.Title = title;
        if (History.Count > MaxHistory)
        {
            // The least useful entries go first: visited once and longest ago
            foreach (HistoryEntry old in History.OrderBy(item => item.Score()).Take(History.Count - MaxHistory).ToList())
                History.Remove(old);
        }
    }

    public void Retitle(string url, string title)
    {
        HistoryEntry? entry = History.FirstOrDefault(item => item.Url == url);
        if (entry != null && !string.IsNullOrWhiteSpace(title))
            entry.Title = title;
    }

    // The sites you go to most, one tile per site, for the new tab page
    public List<HistoryEntry> TopSites(int count)
    {
        return History
            .Where(item => Uri.TryCreate(item.Url, UriKind.Absolute, out Uri? uri) && !Settings.HiddenSites.Contains(uri.Host))
            .GroupBy(item => new Uri(item.Url).Host, StringComparer.OrdinalIgnoreCase)
            .Select(group => new HistoryEntry
            {
                Url = new Uri(group.First().Url).GetLeftPart(UriPartial.Authority) + "/",
                Title = group.OrderByDescending(item => item.Visits).First().Title,
                Visits = group.Sum(item => item.Visits),
                LastVisit = group.Max(item => item.LastVisit)
            })
            .OrderByDescending(item => item.Score())
            .Take(count)
            .ToList();
    }
}

public sealed class BrowserSettings
{
    public BrowserLayout Layout { get; set; } = BrowserLayout.Horizontal;
    public string Engine { get; set; } = BrowserSearch.DefaultEngine;
    public bool Suggestions { get; set; } = true;
    public bool BlockAds { get; set; } = true;
    public bool MinimalAddress { get; set; }
    public bool ProgressBar { get; set; } = true;
    public bool MemorySaver { get; set; } = true;
    public bool OpenNextToCurrent { get; set; } = true;
    public bool ZenMode { get; set; }
    public List<string> AllowedSites { get; set; } = new();
    public List<string> HiddenSites { get; set; } = new();
    public Dictionary<string, double> Zoom { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        AllowedSites ??= new();
        HiddenSites ??= new();
        Zoom = new Dictionary<string, double>(Zoom ?? new(), StringComparer.OrdinalIgnoreCase);
        if (!Enum.IsDefined(Layout))
            Layout = BrowserLayout.Horizontal;
        Engine = BrowserSearch.Engine(Engine).Key;
    }
}

public sealed class SavedTab
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public double Scroll { get; set; }
    public bool Pinned { get; set; }
    public bool Muted { get; set; }
}

public sealed class HistoryEntry
{
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Visits { get; set; }
    public DateTime LastVisit { get; set; }

    // Often visited and recently visited both count, recent visits a little more
    public double Score()
    {
        double days = Math.Max(0, (DateTime.UtcNow - LastVisit).TotalDays);
        return Visits / (1 + days / 7);
    }
}
