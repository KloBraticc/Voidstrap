using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Voidstrap.UI.Elements.Overlay;

// A small browser for the dock, built to cost as little as possible while a game runs:
// one shared browser engine with the background extras switched off, its processes kept below the game's
// priority, and the whole thing suspended (no CPU, memory trimmed) whenever the dock is closed.
// Every callback from the browser engine is fenced so a page or engine crash never takes Voidstrap down,
// and a crashed engine is rebuilt on its own.
public partial class SessionWebBrowser : Window
{
    private const string LogIdent = "SessionWebBrowser";
    private const string HomePage = "https://www.google.com/";
    private const int MaxRebuildsPerMinute = 3;

    // Background work a game overlay never needs: syncing, component updates, translate and Office viewers,
    // a spare renderer kept warm, the back forward page cache, and more than a few renderer processes
    private static readonly string BrowserArguments = string.Join(" ",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-sync",
        "--disable-component-update",
        "--disable-default-apps",
        "--disable-breakpad",
        "--renderer-process-limit=3",
        // Pages are drawn on the graphics card and scroll smoothly
        "--enable-gpu-rasterization",
        "--enable-zero-copy",
        "--enable-smooth-scrolling",
        "--disable-features=TranslateUI,msWebOOUI,msPdfOOUI,msSmartScreenProtection,SpareRendererForSitePerProcess,BackForwardCache,msEdgeCollections,msShoppingExp,msEdgeSidebarV2",
        "--disable-pinch",
        "--overscroll-history-navigation=0");

    private static Task<CoreWebView2Environment>? _sharedEnvironment;

    private readonly Queue<DateTime> _rebuilds = new();
    private WebView2? _view;
    private CoreWebView2? _core;
    private bool _starting;
    private bool _closed;
    private bool _loading;
    private bool _hidden;
    private string _lastUrl = HomePage;

    public SessionWebBrowser()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = StartAsync();
    }

    // ---- Starting and rebuilding the engine ----

    private async Task StartAsync()
    {
        if (_closed || _starting || _view != null)
            return;
        if (!IsRuntimePresent())
        {
            ShowMessage("The browser needs Microsoft Edge WebView2", "Install the WebView2 Runtime from Microsoft, then try again.", true);
            return;
        }
        _starting = true;
        ShowMessage("Starting the browser", string.Empty, false);
        WebView2? view = null;
        try
        {
            CoreWebView2Environment environment = await GetEnvironmentAsync();
            if (_closed)
                return;
            view = new WebView2
            {
                // The page area is dark from the very first frame instead of flashing white
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 32, 32, 32),
                Visibility = Visibility.Visible
            };
            ViewHost.Children.Add(view);
            await view.EnsureCoreWebView2Async(environment);
            if (_closed)
            {
                Discard(view);
                return;
            }
            CoreWebView2 core = view.CoreWebView2;
            Configure(core);
            await AddAccentStyleAsync(core);
            _view = view;
            _core = core;
            view = null;
            _view.IsVisibleChanged += OnViewVisibleChanged;
            Message.Visibility = Visibility.Collapsed;
            ApplyProcessPriority(false);
            core.Navigate(_lastUrl);
        }
        catch (Exception ex)
        {
            Discard(view);
            App.Logger?.WriteLine(LogIdent, "The browser could not start: " + ex.Message);
            // A broken shared engine is created fresh next time
            _sharedEnvironment = null;
            if (!_closed)
                ShowMessage("The browser could not start", "Something went wrong starting the browser engine.", true);
        }
        finally
        {
            _starting = false;
        }
    }

    // One engine for every browser panel, so opening it again costs nothing
    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        Task<CoreWebView2Environment>? existing = _sharedEnvironment;
        if (existing != null && !existing.IsFaulted && !existing.IsCanceled)
            return existing;
        CoreWebView2EnvironmentOptions options = new(Voidstrap.Utility.RenderAcceleration.ApplyToBrowserArguments(BrowserArguments))
        {
            AreBrowserExtensionsEnabled = false
        };
        Task<CoreWebView2Environment> created = CoreWebView2Environment.CreateAsync(null, GetUserDataFolder(), options);
        _sharedEnvironment = created;
        return created;
    }

    private void Configure(CoreWebView2 core)
    {
        try
        {
            CoreWebView2Settings settings = core.Settings;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsSwipeNavigationEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            settings.AreDefaultContextMenusEnabled = true;
            settings.AreBrowserAcceleratorKeysEnabled = true;
            settings.IsZoomControlEnabled = true;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Browser settings could not be applied: " + ex.Message);
        }
        try
        {
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The memory target could not be set: " + ex.Message);
        }
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.SourceChanged += OnSourceChanged;
        core.HistoryChanged += OnHistoryChanged;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;
        try
        {
            core.Environment.ProcessInfosChanged += OnProcessInfosChanged;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Browser processes could not be watched: " + ex.Message);
        }
    }

    // Selected text, form controls and the text cursor on every page use Voidstrap's accent colour,
    // with black or white selected text depending on how bright the accent is
    private async Task AddAccentStyleAsync(CoreWebView2 core)
    {
        try
        {
            if (TryFindResource("SystemAccentColorPrimaryBrush") is not System.Windows.Media.SolidColorBrush accent)
                return;
            System.Windows.Media.Color color = accent.Color;
            string fill = $"rgb({color.R}, {color.G}, {color.B})";
            string text = Voidstrap.UI.Converters.ContrastForegroundConverter.For(accent) is System.Windows.Media.SolidColorBrush { Color.R: > 128 } ? "#ffffff" : "#000000";
            string css = $"::selection{{background:{fill} !important;color:{text} !important}}*{{accent-color:{fill}}}input,textarea,[contenteditable]{{caret-color:{fill}}}";
            string script = "(() => { const css = " + System.Text.Json.JsonSerializer.Serialize(css) + ";"
                + " const add = () => { if (document.getElementById('voidstrap-accent')) return; const s = document.createElement('style'); s.id = 'voidstrap-accent'; s.textContent = css; (document.head || document.documentElement).appendChild(s); };"
                + " if (document.documentElement) add(); else document.addEventListener('DOMContentLoaded', add, { once: true }); })();";
            await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The accent style could not be added: " + ex.Message);
        }
    }

    // ---- Crash recovery ----

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Fenced(() =>
        {
            App.Logger?.WriteLine(LogIdent, $"A browser process failed: {e.ProcessFailedKind}, {e.Reason}");
            switch (e.ProcessFailedKind)
            {
                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    // The engine itself is gone, the view is rebuilt on a fresh engine
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Rebuild));
                    break;
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                    // Only the page crashed or hung, loading it again brings it back
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Fenced(() => _core?.Reload())));
                    break;
                // The graphics and helper processes are restarted by the engine itself
            }
        });
    }

    private void Rebuild()
    {
        if (_closed)
            return;
        DateTime now = DateTime.UtcNow;
        while (_rebuilds.Count > 0 && now - _rebuilds.Peek() > TimeSpan.FromMinutes(1))
            _rebuilds.Dequeue();
        TearDown();
        _sharedEnvironment = null;
        if (_rebuilds.Count >= MaxRebuildsPerMinute)
        {
            // Crashing over and over, stop trying until asked so it never loops in the background
            ShowMessage("The browser keeps crashing", "It stopped restarting on its own to keep your game smooth.", true);
            return;
        }
        _rebuilds.Enqueue(now);
        _ = StartAsync();
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _rebuilds.Clear();
        TearDown();
        _ = StartAsync();
    }

    // ---- Staying light while hidden ----

    // The panel hides with the dock: the page is suspended, its memory trimmed and its processes put at the
    // lowest priority, then all of it comes back the moment the dock opens again
    private void OnViewVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool visible = e.NewValue is true;
        _hidden = !visible;
        // After the view has told the engine it is hidden, which suspending needs
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = ApplyVisibilityAsync(visible)));
    }

    private async Task ApplyVisibilityAsync(bool visible)
    {
        CoreWebView2? core = _core;
        if (_closed || core == null || visible == _hidden)
            return;
        try
        {
            if (visible)
            {
                if (core.IsSuspended)
                    core.Resume();
                core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                ApplyProcessPriority(false);
                return;
            }
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            ApplyProcessPriority(true);
            await core.TrySuspendAsync();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The browser could not be " + (visible ? "resumed" : "suspended") + ": " + ex.Message);
        }
    }

    private void OnProcessInfosChanged(object? sender, object e) => Fenced(() => ApplyProcessPriority(_hidden));

    // Normal while you are using it so scrolling and typing never stutter, idle while it is hidden
    // so the game gets the CPU back the moment the dock closes
    private void ApplyProcessPriority(bool hidden)
    {
        CoreWebView2? core = _core;
        if (core == null)
            return;
        ProcessPriorityClass priority = hidden ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
        List<int> ids = new();
        try
        {
            ids.Add((int)core.BrowserProcessId);
            foreach (CoreWebView2ProcessInfo info in core.Environment.GetProcessInfos())
                ids.Add(info.ProcessId);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Browser processes could not be listed: " + ex.Message);
        }
        foreach (int id in ids)
        {
            try
            {
                using Process process = Process.GetProcessById(id);
                if (process.PriorityClass != priority)
                    process.PriorityClass = priority;
            }
            catch (Exception)
            {
                // The process may have just exited, or belong to another session
            }
        }
    }

    // ---- Navigation ----

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Fenced(() =>
        {
            // Web pages only: links that would start other apps from inside the game are not followed
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != "about" && uri.Scheme != "data"))
            {
                e.Cancel = true;
                return;
            }
            SetLoading(true);
        });
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        Fenced(() =>
        {
            SetLoading(false);
            UpdateHistoryButtons();
        });
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        Fenced(() =>
        {
            string source = _core?.Source ?? string.Empty;
            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                _lastUrl = source;
            if (!AddressBox.IsKeyboardFocused)
                ShowAddress(source);
        });
    }

    private void OnHistoryChanged(object? sender, object e) => Fenced(UpdateHistoryButtons);

    // Pop ups open in this same view instead of new windows over the game
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        Fenced(() =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
                _core?.Navigate(uri.AbsoluteUri);
        });
    }

    private void SetLoading(bool loading)
    {
        _loading = loading;
        LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ReloadGlyph.Text = loading ? "" : "";
        ReloadButton.ToolTip = loading ? "Stop" : "Reload (F5)";
    }

    private void UpdateHistoryButtons()
    {
        CoreWebView2? core = _core;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Fenced(() => { if (_core?.CanGoBack == true) _core.GoBack(); });

    private void Forward_Click(object sender, RoutedEventArgs e) => Fenced(() => { if (_core?.CanGoForward == true) _core.GoForward(); });

    private void Reload_Click(object sender, RoutedEventArgs e) => Fenced(() =>
    {
        if (_loading)
            _core?.Stop();
        else
            _core?.Reload();
    });

    private void Home_Click(object sender, RoutedEventArgs e) => Navigate(HomePage);

    // ---- The search box ----

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Go();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ShowAddress(_core?.Source ?? _lastUrl);
            _view?.Focus();
        }
    }

    private void Go_Click(object sender, RoutedEventArgs e) => Go();

    private void Go()
    {
        string target = ResolveInput(AddressBox.Text);
        if (target.Length == 0)
            return;
        Navigate(target);
        _view?.Focus();
    }

    // Words search Google; something that looks like an address is opened directly
    public static string ResolveInput(string? input)
    {
        string text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;
        if (Uri.TryCreate(text, UriKind.Absolute, out Uri? absolute) && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp))
            return absolute.AbsoluteUri;
        bool looksLikeAddress = !text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
            && !text.EndsWith('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out Uri? guessed) && guessed.Host.Length > 0;
        if (looksLikeAddress)
            return "https://" + text;
        return "https://www.google.com/search?q=" + Uri.EscapeDataString(text);
    }

    private void Navigate(string url)
    {
        _lastUrl = url;
        if (_core == null)
        {
            _ = StartAsync();
            return;
        }
        Fenced(() => _core.Navigate(url));
    }

    private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressIcon.Text = "";
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(AddressBox.SelectAll));
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ShowAddress(_core?.Source ?? _lastUrl);

    private void AddressBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Placeholder.Visibility = AddressBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    // A Google search shows as the words searched for, anything else as its address
    private void ShowAddress(string url)
    {
        string shown = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            if (uri.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/search"
                && System.Web.HttpUtility.ParseQueryString(uri.Query)["q"] is { Length: > 0 } query)
                shown = query;
            else if (uri.AbsoluteUri == HomePage)
                shown = string.Empty;
            AddressIcon.Text = uri.Scheme == Uri.UriSchemeHttps ? "" : "";
        }
        AddressBox.Text = shown;
    }

    // ---- Helpers and cleanup ----

    private void ShowMessage(string title, string text, bool retry)
    {
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessageText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Message.Visibility = Visibility.Visible;
        SetLoading(false);
    }

    // Nothing thrown inside a browser callback may escape into the engine, that would close Voidstrap
    private static void Fenced(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A browser event failed: " + ex.Message);
        }
    }

    private void TearDown()
    {
        WebView2? view = _view;
        CoreWebView2? core = _core;
        _view = null;
        _core = null;
        if (core != null)
        {
            try
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.SourceChanged -= OnSourceChanged;
                core.HistoryChanged -= OnHistoryChanged;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.ProcessFailed -= OnProcessFailed;
                core.Environment.ProcessInfosChanged -= OnProcessInfosChanged;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LogIdent, "Browser events could not be released: " + ex.Message);
            }
        }
        if (view != null)
            view.IsVisibleChanged -= OnViewVisibleChanged;
        Discard(view);
    }

    private void Discard(WebView2? view)
    {
        if (view == null)
            return;
        try
        {
            ViewHost.Children.Remove(view);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The browser view could not be detached: " + ex.Message);
        }
        try
        {
            // Closing the last view lets the engine's processes exit, nothing keeps running after the panel closes
            view.Dispose();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The browser view could not be closed: " + ex.Message);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        TearDown();
    }

    private static string GetUserDataFolder()
    {
        string folder;
        try
        {
            folder = Path.Combine(Paths.WebViewData, "Browser");
        }
        catch (Exception)
        {
            folder = Path.Combine(Path.GetTempPath(), "Voidstrap", "WebView2", "Browser");
        }
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The browser data folder could not be created: " + ex.Message);
        }
        return folder;
    }

    private static bool IsRuntimePresent()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            return false;
        }
    }
}
