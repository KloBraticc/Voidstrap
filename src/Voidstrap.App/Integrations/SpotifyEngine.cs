using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using Voidstrap.Models.Entities;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

public sealed class SpotifyEngine
{
    private const string LOG_IDENT = "SpotifyEngine";

    private const string PlayerUrl = "https://open.spotify.com/";

    internal const string Arguments =
        "--autoplay-policy=no-user-gesture-required --lang=en-US --accept-lang=en-US --process-per-site --disk-cache-size=52428800 --media-cache-size=10485760 "
        + "--disable-background-networking --disable-extensions --disable-sync --disable-translate --disable-features=msWebOOUI,msPdfOOUI,TranslateUI,AutofillServerCommunication,OptimizationHints,MediaRouter";

    private const string Bridge = """
        window.__vsSpotify = {
          text: function (e) { return e ? (e.textContent || '').trim() : ''; },
          items: function (root) {
            var seen = {}, out = [];
            var links = (root || document).querySelectorAll('a[href^="/playlist/"],a[href^="/album/"],a[href^="/track/"],a[href^="/collection/tracks"]');
            for (var i = 0; i < links.length && out.length < 60; i++) {
              var a = links[i], href = a.getAttribute('href');
              if (seen[href]) continue;
              var name = this.text(a);
              if (!name) continue;
              seen[href] = 1;
              var box = a.closest('[data-testid="tracklist-row"],[role="row"],[data-encore-id="card"],[data-testid="top-result-card"],li') || a.parentElement;
              var img = box ? box.querySelector('img') : null;
              var artist = box ? this.text(box.querySelector('a[href^="/artist/"]')) : '';
              out.push({ href: href, name: name, detail: artist, image: img ? img.src : null });
            }
            return out;
          },
          library: function () {
            var out = [], seen = {}, titles = document.querySelectorAll('[id^="listrow-title-spotify:"]');
            for (var i = 0; i < titles.length && out.length < 100; i++) {
              var uri = titles[i].id.replace('listrow-title-', ''), parts = uri.split(':'), href = null;
              if (parts[1] === 'user' && parts[3] === 'collection') href = '/collection/tracks';
              else if (parts[1] === 'collection') href = '/collection/tracks';
              else if (parts.length >= 3) href = '/' + parts[1] + '/' + parts[2];
              if (!href || seen[href]) continue;
              seen[href] = 1;
              var row = titles[i], img = null;
              for (var up = 0; up < 8 && row && !img; up++) { img = row.querySelector('img'); row = row.parentElement; }
              var sub = document.getElementById('listrow-subtitle-' + uri);
              out.push({ href: href, name: this.text(titles[i]), detail: this.text(sub), image: img ? img.src : null });
            }
            return out.length ? out : this.items(document.querySelector('main') || document);
          },
          results: function () {
            return this.items(document.querySelector('main,[data-testid="grid-search-results"]') || document);
          },
          state: function () {
            if (!document.querySelector('[data-testid="global-nav-bar"]')) return null;
            var w = document.querySelector('[data-testid="now-playing-widget"]');
            var img = w ? w.querySelector('img') : null;
            var session = navigator.mediaSession || {}, meta = session.metadata || null;
            var art = meta && meta.artwork && meta.artwork.length ? meta.artwork[meta.artwork.length - 1].src : null;
            return {
              signedIn: !document.querySelector('[data-testid="login-button"]'),
              title: meta && meta.title ? meta.title : (w ? this.text(w.querySelector('[data-testid="context-item-link"]')) : ''),
              artist: meta && meta.artist ? meta.artist : (w ? this.text(w.querySelector('[data-testid="context-item-info-artist"]')) : ''),
              image: art || (img ? img.src : null),
              playing: session.playbackState === 'playing',
              position: this.text(document.querySelector('[data-testid="playback-position"]')),
              duration: this.text(document.querySelector('[data-testid="playback-duration"]')),
              progress: this.fraction('[data-testid="playback-progressbar"] input[type="range"]'),
              volume: this.fraction('[data-testid="volume-bar"] input[type="range"]'),
              shuffle: this.isOn(this.shuffleButton()),
              repeat: this.checked('control-button-repeat') || 'false',
              liked: this.isOn(this.likeButton())
            };
          },
          isOn: function (b) { return !!b && b.getAttribute('aria-checked') === 'true'; },
          shuffleButton: function () {
            return document.querySelector('[data-testid="control-button-shuffle"]') || document.querySelector('[data-testid="general-controls"] button');
          },
          likeButton: function () {
            var bar = document.querySelector('[data-testid="now-playing-bar"]');
            return bar ? bar.querySelector('[data-testid="add-button"],button[aria-label*="Liked Songs"],button[aria-label*="Add to"]') : null;
          },
          press: function (b) {
            if (!b) return false;
            var r = b.getBoundingClientRect(), o = { bubbles: true, cancelable: true, view: window, clientX: r.left + 2, clientY: r.top + 2, button: 0, pointerId: 1, isPrimary: true };
            b.dispatchEvent(new PointerEvent('pointerdown', o));
            b.dispatchEvent(new MouseEvent('mousedown', o));
            b.dispatchEvent(new PointerEvent('pointerup', o));
            b.dispatchEvent(new MouseEvent('mouseup', o));
            b.click();
            return true;
          },
          shuffle: function () { return this.press(this.shuffleButton()); },
          checked: function (id) {
            var b = document.querySelector('[data-testid="' + id + '"]');
            return b ? b.getAttribute('aria-checked') : null;
          },
          fraction: function (selector) {
            var r = document.querySelector(selector);
            if (!r) return 0;
            var min = Number(r.min || 0), max = Number(r.max || 1);
            return max > min ? (Number(r.value) - min) / (max - min) : 0;
          },
          setRange: function (selector, value) {
            var r = document.querySelector(selector);
            if (!r) return false;
            var min = Number(r.min || 0), max = Number(r.max || 1);
            Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(r, String(min + (max - min) * value));
            r.dispatchEvent(new Event('input', { bubbles: true }));
            r.dispatchEvent(new Event('change', { bubbles: true }));
            return true;
          },
          seek: function (value) { return this.setRange('[data-testid="playback-progressbar"] input[type="range"]', value); },
          volume: function (value) { return this.setRange('[data-testid="volume-bar"] input[type="range"]', value); },
          like: function () {
            return this.press(this.likeButton());
          },
          rows: function () {
            var out = [], seen = {}, rows = document.querySelectorAll('[data-testid="tracklist-row"]');
            for (var i = 0; i < rows.length; i++) {
              var a = rows[i].querySelector('a[href^="/track/"]');
              if (!a || seen[a.getAttribute('href')]) continue;
              seen[a.getAttribute('href')] = 1;
              var img = rows[i].querySelector('img');
              out.push({ href: a.getAttribute('href'), name: this.text(a), detail: this.text(rows[i].querySelector('a[href^="/artist/"]')), image: img ? img.src : null });
            }
            return out;
          },
          playRow: function (href) {
            var a = document.querySelector('[data-testid="tracklist-row"] a[href="' + href + '"]');
            var row = a ? a.closest('[data-testid="tracklist-row"]') : null;
            if (!row) return false;
            row.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true, view: window }));
            return true;
          },
          go: function (path) {
            history.pushState({}, '', path);
            dispatchEvent(new PopStateEvent('popstate', { state: {} }));
          },
          click: function (id) {
            var b = document.querySelector('[data-testid="' + id + '"]');
            return this.press(b);
          },
          playPage: function () {
            var b = document.querySelector('[data-testid="action-bar-row"] [data-testid="play-button"]') || document.querySelector('main [data-testid="play-button"]');
            if (b) b.click();
            return !!b;
          }
        };
        """;

    private static readonly HttpClient HttpClient = VpnHttpClient.Create(TimeSpan.FromSeconds(15));

    private CoreWebView2Controller? _controller;

    private CoreWebView2Environment? _environment;

    private bool _starting;

    private TaskCompletionSource? _ready;

    public static string UserDataFolder => Path.Combine(Paths.UserData, "SpotifyWebView");

    public bool IsRunning => _controller != null;

    public bool IsPlayingAudio => _controller?.CoreWebView2.IsDocumentPlayingAudio == true;

    public async Task<bool> EnsureStartedAsync(IntPtr window)
    {
        if (_controller != null)
            return true;
        if (_starting)
            return false;
        _starting = true;
        try
        {
            _environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder, new CoreWebView2EnvironmentOptions(Arguments));
            CoreWebView2Controller controller = await _environment.CreateCoreWebView2ControllerAsync(window);
            controller.Bounds = new Rectangle(-6000, -6000, 1280, 900);
            controller.IsVisible = true;
            CoreWebView2 core = controller.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Bridge);
            _ready = new TaskCompletionSource();
            core.NavigationCompleted += Core_NavigationCompleted;
            core.Navigate(PlayerUrl);
            _controller = controller;
            await Task.WhenAny(_ready.Task, Task.Delay(15000));
            return true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The hidden player could not start: " + ex.GetType().Name);
            Shutdown();
            return false;
        }
        finally
        {
            _starting = false;
        }
    }

    private void Core_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        _ready?.TrySetResult();
    }

    public void Reload()
    {
        _controller?.CoreWebView2.Navigate(PlayerUrl);
    }

    public void SetLowMemory(bool low)
    {
        if (_controller != null)
            _controller.CoreWebView2.MemoryUsageTargetLevel = low ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
    }

    private async Task<JsonDocument?> RunAsync(string expression)
    {
        if (_controller == null)
            return null;
        try
        {
            string json = await _controller.CoreWebView2.ExecuteScriptAsync("window.__vsSpotify ? JSON.stringify(window.__vsSpotify." + expression + ") : null");
            if (string.IsNullOrEmpty(json) || json == "null")
                return null;
            string? inner = JsonSerializer.Deserialize<string>(json);
            return inner is null or "null" ? null : JsonDocument.Parse(inner);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            App.Logger.WriteLine(LOG_IDENT, "A player request failed: " + ex.GetType().Name);
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async Task<SpotifyState?> GetStateAsync()
    {
        using JsonDocument? document = await RunAsync("state()");
        if (document == null)
            return null;
        JsonElement root = document.RootElement;
        return new SpotifyState(root.GetProperty("signedIn").GetBoolean(), Text(root, "title") ?? "", Text(root, "artist") ?? "", Text(root, "image"), IsPlayingAudio || root.GetProperty("playing").GetBoolean(), Text(root, "position") ?? "", Text(root, "duration") ?? "", root.GetProperty("progress").GetDouble(), root.GetProperty("volume").GetDouble(), root.GetProperty("shuffle").GetBoolean(), Text(root, "repeat") ?? "false", root.GetProperty("liked").GetBoolean());
    }

    private async Task<List<SpotifyItem>> ReadItemsAsync(string expression)
    {
        using JsonDocument? document = await RunAsync(expression);
        if (document == null)
            return new List<SpotifyItem>();
        return document
            .RootElement.EnumerateArray()
            .Select(static item => new SpotifyItem(Text(item, "href") ?? "", Text(item, "name") ?? "", Text(item, "detail") ?? "", Text(item, "image")))
            .ToList();
    }

    private async Task<List<SpotifyItem>> WaitForItemsAsync(string expression)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            List<SpotifyItem> items = await ReadItemsAsync(expression);
            if (items.Count > 0)
                return items;
            await Task.Delay(500);
        }
        return new List<SpotifyItem>();
    }

    public Task<List<SpotifyItem>> GetLibraryAsync() => WaitForItemsAsync("library()");

    public async Task<List<SpotifyItem>> SearchAsync(string query)
    {
        (await RunAsync("go(" + JsonSerializer.Serialize("/search/" + Uri.EscapeDataString(query)) + ")"))?.Dispose();
        await Task.Delay(700);
        return await WaitForItemsAsync("results()");
    }

    public async Task PlayAsync(SpotifyItem item)
    {
        if (!item.Href.StartsWith('/'))
            return;
        (await RunAsync("go(" + JsonSerializer.Serialize(item.Href) + ")"))?.Dispose();
        for (int attempt = 0; attempt < 16; attempt++)
        {
            await Task.Delay(500);
            using JsonDocument? clicked = await RunAsync("playPage()");
            if (clicked?.RootElement.ValueKind == JsonValueKind.True)
                return;
        }
    }

    public async Task<List<SpotifyItem>> OpenAsync(string href)
    {
        (await RunAsync("go(" + JsonSerializer.Serialize(href) + ")"))?.Dispose();
        await Task.Delay(700);
        return await WaitForItemsAsync("rows()");
    }

    public Task<List<SpotifyItem>> GetQueueAsync() => OpenAsync("/queue");

    public async Task PlayRowAsync(SpotifyItem item)
    {
        (await RunAsync("playRow(" + JsonSerializer.Serialize(item.Href) + ")"))?.Dispose();
    }

    public async Task PressAsync(string control)
    {
        (await RunAsync("click(" + JsonSerializer.Serialize(control) + ")"))?.Dispose();
    }

    public static async Task<List<SpotifyItem>> FillImagesAsync(List<SpotifyItem> items)
    {
        using SemaphoreSlim gate = new(6);
        SpotifyItem[] filled = await Task.WhenAll(items.Select(item => FillImageAsync(item, gate)));
        return filled.ToList();
    }

    private static async Task<SpotifyItem> FillImageAsync(SpotifyItem item, SemaphoreSlim gate)
    {
        if (!string.IsNullOrEmpty(item.Image) || item.Href.StartsWith("/collection", StringComparison.Ordinal))
            return item;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string url = "https://open.spotify.com/oembed?url=" + Uri.EscapeDataString(PlayerUrl.TrimEnd('/') + item.Href);
            using JsonDocument document = JsonDocument.Parse(await Http.GetStringBoundedAsync(HttpClient, url).ConfigureAwait(false));
            return item with { Image = Text(document.RootElement, "thumbnail_url") };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return item;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task SeekAsync(double value)
    {
        (await RunAsync("seek(" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"))?.Dispose();
    }

    public async Task SetVolumeAsync(double value)
    {
        (await RunAsync("volume(" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"))?.Dispose();
    }

    public async Task ToggleShuffleAsync()
    {
        (await RunAsync("shuffle()"))?.Dispose();
    }

    public async Task ToggleLikeAsync()
    {
        (await RunAsync("like()"))?.Dispose();
    }

    public void Shutdown()
    {
        CoreWebView2Controller? controller = _controller;
        _controller = null;
        if (controller == null)
            return;
        try
        {
            controller.CoreWebView2.NavigationCompleted -= Core_NavigationCompleted;
            controller.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException)
        {
            App.Logger.WriteLine(LOG_IDENT, "The hidden player was already closed: " + ex.GetType().Name);
        }
    }
}
