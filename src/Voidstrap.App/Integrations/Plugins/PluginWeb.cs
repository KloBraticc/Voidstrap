using System.Text.Json.Nodes;
using System.Windows;
#if !CROSSPLAT
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
#endif

namespace Voidstrap.Integrations.Plugins;

internal static class PluginWeb
{
#if CROSSPLAT
    public static JsonObject Open(PluginManifest manifest, JsonObject request) =>
        new() { ["ok"] = false, ["reason"] = "web windows are only available on Windows" };

    public static void CloseFor(string mod) { }
#else
    private sealed class WebWindow : Window
    {
        private readonly HashSet<string> _hosts;

        private readonly string _start;

        private readonly string _dataFolder;

        private readonly string _css;

        private readonly WebView2 _view = new();

        private CoreWebView2? _core;

        public string Mod { get; }

        public WebWindow(string mod, HashSet<string> hosts, string start, string dataFolder, string css)
        {
            Mod = mod;
            _hosts = hosts;
            _start = start;
            _dataFolder = dataFolder;
            _css = css;
            Content = _view;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_dataFolder);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _dataFolder);
                await _view.EnsureCoreWebView2Async(environment);
                _core = _view.CoreWebView2;
                CoreWebView2Settings settings = _core.Settings;
                settings.AreDevToolsEnabled = false;
                settings.AreHostObjectsAllowed = false;
                settings.IsWebMessageEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                _core.NavigationStarting += OnNavigationStarting;
                _core.FrameNavigationStarting += OnFrameNavigationStarting;
                _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                _core.WebResourceRequested += OnResourceRequested;
                if (_css.Length > 0)
                    await _core.AddScriptToExecuteOnDocumentCreatedAsync(
                        "if(window===window.top){const s=document.createElement('style');s.textContent="
                            + System.Text.Json.JsonSerializer.Serialize(_css)
                            + ";(document.head||document.documentElement).appendChild(s);document.addEventListener('DOMContentLoaded',()=>document.head.appendChild(s));}"
                    );
                _core.NewWindowRequested += OnNewWindow;
                _core.DownloadStarting += OnDownload;
                _core.PermissionRequested += OnPermission;
                _core.Navigate(_start);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("VoidstrapMods", Mod + ": the web window could not start: " + ex.Message);
                Close();
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!IsAllowedUrl(e.Uri, _hosts))
            {
                e.Cancel = true;
                App.Logger.WriteLine("VoidstrapMods", Mod + ": blocked a web window navigation outside its listed hosts");
            }
        }

        private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase))
                return;
            OnNavigationStarting(sender, e);
        }

        private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri? uri) && AdHosts.Any(host => uri.Host == host || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
                e.Response = _core!.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
        }

        private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

        private void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            e.Cancel = true;
            App.Logger.WriteLine("VoidstrapMods", Mod + ": blocked a download in a web window");
        }

        private void OnPermission(object? sender, CoreWebView2PermissionRequestedEventArgs e) => e.State = CoreWebView2PermissionState.Deny;

        private void OnClosed(object? sender, EventArgs e)
        {
            Loaded -= OnLoaded;
            Closed -= OnClosed;
            if (_core != null)
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.FrameNavigationStarting -= OnFrameNavigationStarting;
                _core.WebResourceRequested -= OnResourceRequested;
                _core.NewWindowRequested -= OnNewWindow;
                _core.DownloadStarting -= OnDownload;
                _core.PermissionRequested -= OnPermission;
            }
            _view.Dispose();
        }
    }

    private static readonly string[] AdHosts =
    [
        "nitropay.com",
        "doubleclick.net",
        "googlesyndication.com",
        "googletagmanager.com",
        "google-analytics.com",
        "googleadservices.com",
        "adnxs.com",
        "amazon-adsystem.com",
        "rubiconproject.com",
        "pubmatic.com",
        "criteo.com",
    ];

    private static bool IsAllowedUrl(string url, HashSet<string> hosts) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && hosts.Contains(uri.Host)
        && !uri.Host.EndsWith("roblox.com", StringComparison.OrdinalIgnoreCase);

    public static JsonObject Open(PluginManifest manifest, JsonObject request)
    {
        string url = request["url"]?.ToString() ?? "";
        HashSet<string> hosts = new(manifest.NetworkHosts, StringComparer.OrdinalIgnoreCase);
        if (!IsAllowedUrl(url, hosts))
            return new JsonObject { ["ok"] = false, ["reason"] = "the url must be https, its host listed under network in plugin.json, and never roblox.com" };
        string title = request["title"]?.ToString() ?? manifest.Name;
        double width = request["width"] is JsonValue w && w.TryGetValue(out double wide) ? Math.Clamp(wide, 240, 2400) : 900;
        double height = request["height"] is JsonValue h && h.TryGetValue(out double tall) ? Math.Clamp(tall, 200, 1600) : 620;
        string css = request["css"]?.ToString() ?? "";
        if (css.Length > 8000)
            return new JsonObject { ["ok"] = false, ["reason"] = "css can be at most 8000 characters" };
        bool fullscreen = request["fullscreen"] is JsonValue full && full.TryGetValue(out bool wantFull) && wantFull;
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                WebWindow window = new(manifest.Name, hosts, url, PluginFiles.WebDataOf(manifest.Name), css)
                {
                    Title = manifest.Name + ": " + title,
                    Width = width,
                    Height = height,
                };
                if (fullscreen)
                {
                    window.WindowStyle = WindowStyle.None;
                    window.WindowState = WindowState.Maximized;
                }
                window.Show();
            })
        );
        App.Logger.WriteLine("VoidstrapMods", manifest.Name + ": opened a web window");
        return new JsonObject { ["ok"] = true, ["reason"] = "" };
    }

    public static void CloseFor(string mod) =>
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                foreach (WebWindow window in Application.Current.Windows.OfType<WebWindow>().Where(window => window.Mod == mod).ToList())
                    window.Close();
            })
        );
#endif
}
