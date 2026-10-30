using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows;
#if !CROSSPLAT
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.Windows.Interop;
#endif

namespace Voidstrap.Integrations.Plugins;

internal static partial class PluginWeb
{
#if CROSSPLAT
    private const string LOG_IDENT = "PluginWeb";

    private static readonly Dictionary<string, List<IntPtr>> LinuxWindows = [];

    private static readonly List<Delegate> LinuxHandlers = [];

    private sealed class MacWebWindow : Window
    {
        private readonly string _start;

        private readonly HashSet<string> _hosts;

        private readonly string[] _scripts;

        private readonly Guid _profile;

        private Voidstrap.Platform.MacOS.MacOSWebView? _view;

        public string Mod { get; }

        public MacWebWindow(string mod, HashSet<string> hosts, string start, string[] scripts)
        {
            Mod = mod;
            _hosts = hosts;
            _start = start;
            _scripts = scripts;
            _profile = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(mod)));
            ContentRendered += OnRendered;
            SizeChanged += OnResized;
            Closed += OnClosed;
        }

        private void OnRendered(object? sender, EventArgs e)
        {
            try
            {
                nint native = Voidstrap.UI.MacWindowMode.ResolveNativeWindow(this);
                _view = new Voidstrap.Platform.MacOS.MacOSWebView(native, "", "", _scripts, 0, uri => IsAllowedFrame(uri, _hosts), _start, _profile);
                Place();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, Mod + ": the web window could not start: " + ex.Message);
                Close();
            }
        }

        private void OnResized(object sender, SizeChangedEventArgs e) => Place();

        private void Place() => _view?.SetFrame(0, 0, ActualWidth, ActualHeight, true);

        private void OnClosed(object? sender, EventArgs e)
        {
            ContentRendered -= OnRendered;
            SizeChanged -= OnResized;
            Closed -= OnClosed;
            _view?.Dispose();
        }
    }

    private const string AdShim = """
        (function () {
          const bad = /(^|\.)(nitropay\.com|doubleclick\.net|googlesyndication\.com|googletagmanager\.com|google-analytics\.com|googleadservices\.com|adnxs\.com|amazon-adsystem\.com|rubiconproject\.com|pubmatic\.com|criteo\.com)$/i;
          const blocked = u => { try { return bad.test(new URL(u, location.href).hostname); } catch { return false; } };
          for (const proto of [HTMLScriptElement.prototype, HTMLIFrameElement.prototype, HTMLImageElement.prototype]) {
            const d = Object.getOwnPropertyDescriptor(proto, 'src');
            Object.defineProperty(proto, 'src', { get: d.get, set(v) { if (!blocked(v)) d.set.call(this, v); }, configurable: true });
          }
          const attr = Element.prototype.setAttribute;
          Element.prototype.setAttribute = function (n, v) { if (String(n).toLowerCase() === 'src' && blocked(v)) return; return attr.call(this, n, v); };
          const f = window.fetch;
          window.fetch = function (u, o) { const t = typeof u === 'string' ? u : u && u.url; return blocked(t) ? Promise.reject(new TypeError('blocked')) : f.call(this, u, o); };
          window.open = function () { return null; };
        })();
        """;

    private static bool IsAllowedFrame(string uri, HashSet<string> hosts) =>
        uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        || uri.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase)
        || IsAllowedUrl(uri, hosts);

    private static string CssScript(string css) =>
        css.Length == 0
            ? ""
            : "if(window===window.top){const s=document.createElement('style');s.textContent="
                + System.Text.Json.JsonSerializer.Serialize(css)
                + ";(document.head||document.documentElement).appendChild(s);document.addEventListener('DOMContentLoaded',()=>document.head.appendChild(s));}";

    public static JsonObject Open(PluginManifest manifest, JsonObject request)
    {
        (string url, HashSet<string> hosts, string title, double width, double height, string css, bool fullscreen, JsonObject? refused) = Read(manifest, request);
        if (refused != null)
            return refused;
        string[] scripts = [AdShim, CssScript(css)];
        if (Voidstrap.Utility.Platform.IsMacOS)
        {
            Application.Current?.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    MacWebWindow window = new(manifest.Name, hosts, url, scripts)
                    {
                        Title = manifest.Name + ": " + title,
                        Width = width,
                        Height = height,
                        WindowState = fullscreen ? System.Windows.WindowState.Maximized : System.Windows.WindowState.Normal,
                    };
                    window.Show();
                })
            );
            return new JsonObject { ["ok"] = true, ["reason"] = "" };
        }
        if (!NativeLibrary.TryLoad(Voidstrap.UI.LinuxWebKitInterop.WebKit, out _) || !NativeLibrary.TryLoad(Voidstrap.UI.LinuxWebKitInterop.Gtk, out _) || !Voidstrap.UI.LinuxWebPanel.EnsureGtk())
            return new JsonObject { ["ok"] = false, ["reason"] = "install WebKitGTK 4.1 (libwebkit2gtk-4.1) to use web windows" };
        string data = PluginFiles.WebDataOf(manifest.Name);
        Directory.CreateDirectory(data);
        Voidstrap.UI.LinuxWebPanel.Post(() => OpenLinux(manifest.Name, hosts, url, title, width, height, fullscreen, scripts, data));
        App.Logger.WriteLine("VoidstrapMods", manifest.Name + ": opened a web window");
        return new JsonObject { ["ok"] = true, ["reason"] = "" };
    }

    private static void OpenLinux(string mod, HashSet<string> hosts, string url, string title, double width, double height, bool fullscreen, string[] scripts, string data)
    {
        IntPtr manager = Voidstrap.UI.LinuxWebKitInterop.webkit_website_data_manager_new("base-data-directory", data, "base-cache-directory", Path.Combine(data, "cache"), IntPtr.Zero);
        IntPtr view = Voidstrap.UI.LinuxWebKitInterop.webkit_web_view_new_with_context(Voidstrap.UI.LinuxWebKitInterop.webkit_web_context_new_with_website_data_manager(manager));
        IntPtr settings = Voidstrap.UI.LinuxWebKitInterop.webkit_web_view_get_settings(view);
        Voidstrap.UI.LinuxWebKitInterop.webkit_settings_set_enable_developer_extras(settings, false);
        Voidstrap.UI.LinuxWebKitInterop.webkit_settings_set_javascript_can_open_windows_automatically(settings, false);
        IntPtr content = Voidstrap.UI.LinuxWebKitInterop.webkit_web_view_get_user_content_manager(view);
        foreach (string source in scripts.Where(script => script.Length > 0))
            Voidstrap.UI.LinuxWebKitInterop.webkit_user_content_manager_add_script(content, Voidstrap.UI.LinuxWebKitInterop.webkit_user_script_new(source, 0, 0, IntPtr.Zero, IntPtr.Zero));
        Voidstrap.UI.LinuxWebKitInterop.DecidePolicyHandler policy = (_, decision, type, _) => DecideLinux(mod, hosts, decision, type);
        Voidstrap.UI.LinuxWebKitInterop.ContextMenuHandler menu = (_, _, _, _, _) => true;
        Voidstrap.UI.LinuxWebKitInterop.PermissionHandler permission = (_, permissionRequest, _) =>
        {
            Voidstrap.UI.LinuxWebKitInterop.webkit_permission_request_deny(permissionRequest);
            return true;
        };
        IntPtr window = Voidstrap.UI.LinuxWebKitInterop.gtk_window_new(0);
        Voidstrap.UI.LinuxWebKitInterop.DestroyHandler destroyed = (_, _) =>
        {
            if (LinuxWindows.TryGetValue(mod, out List<IntPtr>? open))
                open.Remove(window);
        };
        lock (LinuxHandlers)
            LinuxHandlers.AddRange([policy, menu, permission, destroyed]);
        Connect(view, "decide-policy", policy);
        Connect(view, "context-menu", menu);
        Connect(view, "permission-request", permission);
        Connect(window, "destroy", destroyed);
        Voidstrap.UI.LinuxWebKitInterop.gtk_window_set_title(window, mod + ": " + title);
        Voidstrap.UI.LinuxWebKitInterop.gtk_window_resize(window, (int)width, (int)height);
        Voidstrap.UI.LinuxWebKitInterop.gtk_container_add(window, view);
        if (fullscreen)
            Voidstrap.UI.LinuxWebKitInterop.gtk_window_fullscreen(window);
        Voidstrap.UI.LinuxWebKitInterop.gtk_widget_show_all(window);
        Voidstrap.UI.LinuxWebKitInterop.webkit_web_view_load_uri(view, url);
        if (!LinuxWindows.TryGetValue(mod, out List<IntPtr>? list))
            LinuxWindows[mod] = list = [];
        list.Add(window);
    }

    private static void Connect(IntPtr target, string signal, Delegate handler) =>
        Voidstrap.UI.LinuxWebKitInterop.g_signal_connect_data(target, signal, Marshal.GetFunctionPointerForDelegate(handler), IntPtr.Zero, IntPtr.Zero, 0);

    private static bool DecideLinux(string mod, HashSet<string> hosts, IntPtr decision, int type)
    {
        bool allowed = false;
        if (type == 0)
        {
            IntPtr action = Voidstrap.UI.LinuxWebKitInterop.webkit_navigation_policy_decision_get_navigation_action(decision);
            IntPtr request = action == IntPtr.Zero ? IntPtr.Zero : Voidstrap.UI.LinuxWebKitInterop.webkit_navigation_action_get_request(action);
            string? uri = request == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Voidstrap.UI.LinuxWebKitInterop.webkit_uri_request_get_uri(request));
            allowed = uri != null && IsAllowedFrame(uri, hosts);
        }
        else if (type == 2)
        {
            allowed = Voidstrap.UI.LinuxWebKitInterop.webkit_response_policy_decision_is_mime_type_supported(decision);
        }
        if (allowed)
            return false;
        App.Logger.WriteLine("VoidstrapMods", mod + ": blocked a web window navigation, popup or download");
        Voidstrap.UI.LinuxWebKitInterop.webkit_policy_decision_ignore(decision);
        return true;
    }

    public static void CloseFor(string mod)
    {
        if (Voidstrap.Utility.Platform.IsMacOS)
        {
            Application.Current?.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    foreach (MacWebWindow window in Application.Current.Windows.OfType<MacWebWindow>().Where(window => window.Mod == mod).ToList())
                        window.Close();
                })
            );
            return;
        }
        if (Voidstrap.Utility.Platform.IsLinux && LinuxWindows.Count > 0)
            Voidstrap.UI.LinuxWebPanel.Post(() =>
            {
                if (LinuxWindows.TryGetValue(mod, out List<IntPtr>? open))
                    foreach (IntPtr window in open.ToList())
                        Voidstrap.UI.LinuxWebKitInterop.gtk_widget_destroy(window);
            });
    }
#else
    private sealed class WebWindow : Window
    {
        private readonly HashSet<string> _hosts;

        private readonly string _start;

        private readonly string _dataFolder;

        private readonly string _css;

        private readonly WebView2 _view = new();

        private CoreWebView2? _core;

        private HwndSource? _source;

        private bool _fullscreen;

        private System.Windows.WindowState _stateBeforeFullscreen;

        private int _crashes;

        private int _blockedLogs;

        public string Mod { get; }

        public WebWindow(string mod, HashSet<string> hosts, string start, string dataFolder, string css)
        {
            Mod = mod;
            _hosts = hosts;
            _start = start;
            _dataFolder = dataFolder;
            _css = css;
            Content = _view;
            SourceInitialized += OnSourceInitialized;
            Activated += OnActivated;
            Deactivated += OnDeactivated;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_dataFolder);
                CoreWebView2EnvironmentOptions options = new(
                    "--force_high_performance_gpu --enable-gpu-rasterization --disable-renderer-backgrounding --disable-background-timer-throttling --disable-features=CalculateNativeWinOcclusion --disable-sync --disable-extensions --js-flags=--max-old-space-size=4096"
                );
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, _dataFolder, options);
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
                foreach (string host in AdHosts)
                {
                    _core.AddWebResourceRequestedFilter("*://" + host + "/*", CoreWebView2WebResourceContext.All);
                    _core.AddWebResourceRequestedFilter("*://*." + host + "/*", CoreWebView2WebResourceContext.All);
                }
                _core.ProcessFailed += OnProcessFailed;
                _core.ContainsFullScreenElementChanged += OnPageFullscreen;
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
                if (_blockedLogs++ < 5)
                    App.Logger.WriteLine("VoidstrapMods", Mod + ": blocked a web window navigation to a host outside its list");
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

        private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            App.Logger.WriteLine("VoidstrapMods", Mod + ": web window process failed: " + e.ProcessFailedKind + ", " + e.Reason + ", exit code " + e.ExitCode);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                Dispatcher.BeginInvoke(new Action(Close));
                return;
            }
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive && _crashes++ < 3)
                Dispatcher.BeginInvoke(new Action(() => _core?.Reload()));
        }

        private void OnPageFullscreen(object? sender, object e)
        {
            if (_core != null && _core.ContainsFullScreenElement != _fullscreen)
                ToggleFullscreen();
        }

        private void ToggleFullscreen()
        {
            _fullscreen = !_fullscreen;
            if (_fullscreen)
            {
                _stateBeforeFullscreen = WindowState;
                WindowState = System.Windows.WindowState.Normal;
                WindowStyle = WindowStyle.None;
                WindowState = System.Windows.WindowState.Maximized;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                WindowState = _stateBeforeFullscreen;
            }
        }

        private void OnSourceInitialized(object? sender, EventArgs e)
        {
            _source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _source?.AddHook(OnWindowMessage);
        }

        private void OnActivated(object? sender, EventArgs e) => RegisterHotKey(new WindowInteropHelper(this).Handle, FullscreenHotKey, 0, VirtualKeyF11);

        private void OnDeactivated(object? sender, EventArgs e) => UnregisterHotKey(new WindowInteropHelper(this).Handle, FullscreenHotKey);

        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WindowMessageHotKey && wParam == FullscreenHotKey)
            {
                ToggleFullscreen();
                handled = true;
            }
            return IntPtr.Zero;
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
            SourceInitialized -= OnSourceInitialized;
            Activated -= OnActivated;
            Deactivated -= OnDeactivated;
            UnregisterHotKey(new WindowInteropHelper(this).Handle, FullscreenHotKey);
            _source?.RemoveHook(OnWindowMessage);
            if (_core != null)
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.FrameNavigationStarting -= OnFrameNavigationStarting;
                _core.WebResourceRequested -= OnResourceRequested;
                _core.ProcessFailed -= OnProcessFailed;
                _core.ContainsFullScreenElementChanged -= OnPageFullscreen;
                _core.NewWindowRequested -= OnNewWindow;
                _core.DownloadStarting -= OnDownload;
                _core.PermissionRequested -= OnPermission;
            }
            _view.Dispose();
        }
    }

    private const int FullscreenHotKey = 0x5646;

    private const int WindowMessageHotKey = 0x0312;

    private const uint VirtualKeyF11 = 0x7A;

    [LibraryImport("user32.dll", EntryPoint = "RegisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll", EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);

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

#endif

    private static bool IsAllowedUrl(string url, HashSet<string> hosts) =>
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && hosts.Contains(uri.Host)
        && !uri.Host.EndsWith("roblox.com", StringComparison.OrdinalIgnoreCase);

#if !CROSSPLAT
    public static JsonObject Open(PluginManifest manifest, JsonObject request)
    {
        (string url, HashSet<string> hosts, string title, double width, double height, string css, bool fullscreen, JsonObject? refused) = Read(manifest, request);
        if (refused != null)
            return refused;
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
                    window.WindowState = System.Windows.WindowState.Maximized;
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

    private static (string Url, HashSet<string> Hosts, string Title, double Width, double Height, string Css, bool Fullscreen, JsonObject? Refused) Read(PluginManifest manifest, JsonObject request)
    {
        string url = request["url"]?.ToString() ?? "";
        HashSet<string> hosts = new(manifest.NetworkHosts, StringComparer.OrdinalIgnoreCase);
        string css = request["css"]?.ToString() ?? "";
        JsonObject? refused = null;
        if (!IsAllowedUrl(url, hosts))
            refused = new JsonObject { ["ok"] = false, ["reason"] = "the url must be https, its host listed under network in plugin.json, and never roblox.com" };
        else if (css.Length > 8000)
            refused = new JsonObject { ["ok"] = false, ["reason"] = "css can be at most 8000 characters" };
        return (
            url,
            hosts,
            request["title"]?.ToString() ?? manifest.Name,
            request["width"] is JsonValue w && w.TryGetValue(out double wide) ? Math.Clamp(wide, 240, 2400) : 900,
            request["height"] is JsonValue h && h.TryGetValue(out double tall) ? Math.Clamp(tall, 200, 1600) : 620,
            css,
            request["fullscreen"] is JsonValue full && full.TryGetValue(out bool wantFull) && wantFull,
            refused
        );
    }
}
