using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.UI.Elements.Overlay;

public sealed record SearchEngine(string Key, string Name, string SearchUrl, string SuggestUrl);

public enum BangKind
{
    Search,
    Ask
}

public sealed record Bang(string Trigger, string Name, string Url, BangKind Kind = BangKind.Search);

// Where the address bar sends what you type: the search engine, !bangs that jump straight to a site's own search,
// and the suggestions shown while typing
public static class BrowserSearch
{
    public const string DefaultEngine = "google";

    public static readonly IReadOnlyList<SearchEngine> Engines = new[]
    {
        new SearchEngine("google", "Google", "https://www.google.com/search?q={0}", "https://suggestqueries.google.com/complete/search?client=firefox&q={0}"),
        new SearchEngine("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={0}", "https://duckduckgo.com/ac/?type=list&q={0}"),
        new SearchEngine("bing", "Bing", "https://www.bing.com/search?q={0}", "https://api.bing.com/osjson.aspx?query={0}"),
        new SearchEngine("brave", "Brave Search", "https://search.brave.com/search?q={0}", "https://search.brave.com/api/suggest?q={0}"),
        new SearchEngine("startpage", "Startpage", "https://www.startpage.com/do/search?q={0}", "https://duckduckgo.com/ac/?type=list&q={0}"),
        new SearchEngine("ecosia", "Ecosia", "https://www.ecosia.org/search?q={0}", "https://duckduckgo.com/ac/?type=list&q={0}"),
        new SearchEngine("kagi", "Kagi", "https://kagi.com/search?q={0}", "https://duckduckgo.com/ac/?type=list&q={0}")
    };

    // Typed as "!yt cats" or "cats !yt"; the list is our own pick of what is useful next to a game
    public static readonly IReadOnlyList<Bang> Bangs = new[]
    {
        new Bang("g", "Google", "https://www.google.com/search?q={0}"),
        new Bang("i", "Google Images", "https://www.google.com/search?tbm=isch&q={0}"),
        new Bang("n", "Google News", "https://news.google.com/search?q={0}"),
        new Bang("m", "Google Maps", "https://www.google.com/maps/search/{0}"),
        new Bang("tr", "Google Translate", "https://translate.google.com/?sl=auto&tl=en&text={0}"),
        new Bang("ddg", "DuckDuckGo", "https://duckduckgo.com/?q={0}"),
        new Bang("b", "Bing", "https://www.bing.com/search?q={0}"),
        new Bang("brave", "Brave Search", "https://search.brave.com/search?q={0}"),
        new Bang("yt", "YouTube", "https://www.youtube.com/results?search_query={0}"),
        new Bang("w", "Wikipedia", "https://en.wikipedia.org/wiki/Special:Search?search={0}"),
        new Bang("r", "Reddit", "https://www.reddit.com/search/?q={0}"),
        new Bang("gh", "GitHub", "https://github.com/search?q={0}"),
        new Bang("so", "Stack Overflow", "https://stackoverflow.com/search?q={0}"),
        new Bang("mdn", "MDN", "https://developer.mozilla.org/search?q={0}"),
        new Bang("nuget", "NuGet", "https://www.nuget.org/packages?q={0}"),
        new Bang("npm", "npm", "https://www.npmjs.com/search?q={0}"),
        new Bang("a", "Amazon", "https://www.amazon.com/s?k={0}"),
        new Bang("imdb", "IMDb", "https://www.imdb.com/find/?q={0}"),
        new Bang("x", "X", "https://x.com/search?q={0}"),
        new Bang("tw", "Twitch", "https://www.twitch.tv/search?term={0}"),
        new Bang("sp", "Spotify", "https://open.spotify.com/search/{0}"),
        new Bang("wa", "Wolfram Alpha", "https://www.wolframalpha.com/input?i={0}"),
        new Bang("dict", "Dictionary", "https://www.merriam-webster.com/dictionary/{0}"),
        new Bang("rbx", "Roblox games", "https://www.roblox.com/discover/?Keyword={0}"),
        new Bang("rbxc", "Roblox catalog", "https://www.roblox.com/catalog?Keyword={0}"),
        new Bang("rbxu", "Roblox players", "https://www.roblox.com/search/users?keyword={0}"),
        new Bang("dev", "Roblox DevForum", "https://devforum.roblox.com/search?q={0}"),
        new Bang("fandom", "Fandom", "https://community.fandom.com/wiki/Special:Search?query={0}"),
        new Bang("chat", "ChatGPT", "https://chatgpt.com/?q={0}", BangKind.Ask),
        new Bang("claude", "Claude", "https://claude.ai/new?q={0}", BangKind.Ask),
        new Bang("p", "Perplexity", "https://www.perplexity.ai/search?q={0}", BangKind.Ask)
    };

    private static readonly Dictionary<string, Bang> BangsByTrigger = Bangs.ToDictionary(bang => bang.Trigger, StringComparer.OrdinalIgnoreCase);

    private static readonly HttpClient SuggestClient = CreateSuggestClient();

    public static SearchEngine Engine(string? key)
        => Engines.FirstOrDefault(engine => string.Equals(engine.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Engines[0];

    public static Bang? FindBang(string? trigger)
        => trigger != null && BangsByTrigger.TryGetValue(trigger.TrimStart('!'), out Bang? bang) ? bang : null;

    public static IEnumerable<Bang> MatchBangs(string prefix)
    {
        string wanted = prefix.TrimStart('!');
        return Bangs.Where(bang => bang.Trigger.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
                || bang.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase))
            .OrderBy(bang => bang.Trigger.Length);
    }

    public static string SearchUrl(string query, string? engineKey) => Format(Engine(engineKey).SearchUrl, query);

    public static string BangUrl(Bang bang, string query)
    {
        if (query.Length == 0)
            return Uri.TryCreate(bang.Url.Replace("{0}", string.Empty), UriKind.Absolute, out Uri? site) ? site.GetLeftPart(UriPartial.Authority) + "/" : bang.Url;
        return Format(bang.Url, query);
    }

    // Splits "!yt cats" or "cats !yt" into the bang and the words, when the bang is one we know
    public static (Bang? Bang, string Query) SplitBang(string text)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return (null, string.Empty);
        if (words[0].StartsWith('!') && FindBang(words[0]) is { } first)
            return (first, string.Join(' ', words.Skip(1)));
        if (words.Length > 1 && words[^1].StartsWith('!') && FindBang(words[^1]) is { } last)
            return (last, string.Join(' ', words.Take(words.Length - 1)));
        return (null, text);
    }

    // Words search; something that looks like an address opens directly, always over https unless http was typed
    public static string Resolve(string? input, string? engineKey, Bang? keyword = null)
    {
        string text = (input ?? string.Empty).Trim();
        if (keyword != null)
            return BangUrl(keyword, text);
        if (text.Length == 0)
            return string.Empty;
        (Bang? bang, string query) = SplitBang(text);
        if (bang != null)
            return BangUrl(bang, query);
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
            return absolute.AbsoluteUri;
        if (LooksLikeAddress(text))
            return "https://" + text;
        return SearchUrl(text, engineKey);
    }

    public static bool LooksLikeAddress(string text)
    {
        if (text.Contains(' ') || text.EndsWith('.') || text.StartsWith('.'))
            return false;
        bool shaped = text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase);
        return shaped && Uri.TryCreate("https://" + text, UriKind.Absolute, out Uri? guessed) && guessed.Host.Length > 0;
    }

    // The words of a search results page, so the address bar can show what was searched for instead of a long address
    public static string? SearchedFor(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            return null;
        foreach (SearchEngine engine in Engines)
        {
            if (!Uri.TryCreate(Format(engine.SearchUrl, "x"), UriKind.Absolute, out Uri? sample))
                continue;
            if (!string.Equals(uri.Host, sample.Host, StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath != sample.AbsolutePath)
                continue;
            string? query = System.Web.HttpUtility.ParseQueryString(uri.Query)["q"];
            if (!string.IsNullOrWhiteSpace(query))
                return query;
        }
        return null;
    }

    // Every engine here answers in the same small format: ["what you typed", ["suggestion", ...]]
    public static async Task<IReadOnlyList<string>> SuggestAsync(string query, string? engineKey, CancellationToken token)
    {
        string url = Format(Engine(engineKey).SuggestUrl, query);
        try
        {
            using HttpResponseMessage response = await SuggestClient.GetAsync(url, token);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();
            await using System.IO.Stream stream = await response.Content.ReadAsStreamAsync(token);
            using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() < 2)
                return Array.Empty<string>();
            JsonElement list = document.RootElement[1];
            if (list.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();
            return list.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(item => item.Length > 0 && !string.Equals(item, query, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
        }
        catch (Exception)
        {
            // Suggestions are a nicety: offline, slow or cancelled simply shows none
            return Array.Empty<string>();
        }
    }

    private static string Format(string template, string query) => template.Replace("{0}", Uri.EscapeDataString(query));

    private static HttpClient CreateSuggestClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }
}
