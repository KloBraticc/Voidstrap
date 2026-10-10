using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Voidstrap.UI.Elements.Overlay;

// A small tabbed browser for the dock, built to cost as little as possible while a game runs:
// one shared browser engine with the background extras switched off, only the tab you are looking at running
// (the others are suspended), and everything suspended at idle priority while the dock is closed.
// Your tabs, the tab you were on and where you were on each page are kept, also across restarts.
// Every callback from the browser engine is fenced so a page or engine crash never takes Voidstrap down,
// and a crashed engine is rebuilt on its own.
public partial class SessionWebBrowser : Window
{
    private const string LogIdent = "SessionWebBrowser";
    private const string HomePage = "https://www.google.com/";
    private const int MaxRebuildsPerMinute = 3;
    private const int MaxTabs = 12;

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

    private readonly ObservableCollection<BrowserTab> _tabs = new();
    private readonly Queue<DateTime> _rebuilds = new();
    private readonly DispatcherTimer _saveTimer;
    private CoreWebView2Environment? _environment;
    private BrowserTab? _active;
    private string? _accentScript;
    private bool _starting;
    private bool _closed;
    private bool _panelHidden;

    public SessionWebBrowser()
    {
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = SaveSessionAsync();
        };
        Loaded += OnLoaded;
        Closed += OnClosed;
        ViewHost.IsVisibleChanged += OnHostVisibleChanged;
    }

    private static string SessionPath => Path.Combine(Paths.Config, "browser.json");

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _ = StartAsync();
    }

    // ---- Starting, restoring and rebuilding ----

    private async Task StartAsync()
    {
        if (_closed || _starting || _environment != null)
            return;
        if (!IsRuntimePresent())
        {
            ShowMessage("The browser needs Microsoft Edge WebView2", "Install the WebView2 Runtime from Microsoft, then try again.", true);
            return;
        }
        _starting = true;
        ShowMessage("Starting the browser", string.Empty, false);
        try
        {
            _environment = await GetEnvironmentAsync();
            if (_closed)
                return;
            HookEnvironment(_environment);
            Message.Visibility = Visibility.Collapsed;
            // Picks up where you left off: the same tabs, the tab you were on and where you were on each page
            SavedSession saved = LoadSession();
            List<SavedTab> tabs = saved.Tabs.Where(tab => IsWebAddress(tab.Url)).Take(MaxTabs).ToList();
            if (tabs.Count == 0)
                tabs.Add(new SavedTab { Url = HomePage });
            int active = Math.Clamp(saved.Active, 0, tabs.Count - 1);
            List<BrowserTab> created = tabs.Select(tab => AddTab(tab.Url, tab.Title, tab.Scroll, ReferenceEquals(tab, tabs[active]))).ToList();
            SwitchTo(created[active]);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The browser could not start: " + ex.Message);
            // A broken shared engine is created fresh next time
            _sharedEnvironment = null;
            _environment = null;
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

    private void HookEnvironment(CoreWebView2Environment environment)
    {
        try
        {
            environment.ProcessInfosChanged += OnProcessInfosChanged;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Browser processes could not be watched: " + ex.Message);
        }
    }

    // ---- Tabs ----

    // A restored tab that is not the open one waits with its page until it is first shown,
    // so restoring many tabs costs no more than the one you are looking at
    private BrowserTab AddTab(string url, string? title = null, double scroll = 0, bool loadNow = true)
    {
        BrowserTab tab = new(url, string.IsNullOrWhiteSpace(title) ? "New tab" : title, scroll);
        _tabs.Add(tab);
        if (loadNow)
            _ = CreateViewAsync(tab);
        return tab;
    }

    private async Task CreateViewAsync(BrowserTab tab)
    {
        CoreWebView2Environment? environment = _environment;
        if (_closed || environment == null || tab.View != null)
            return;
        WebView2 view = new()
        {
            // The page area is dark from the very first frame instead of flashing white
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 32, 32, 32),
            Visibility = ReferenceEquals(tab, _active) && !_panelHidden ? Visibility.Visible : Visibility.Collapsed
        };
        tab.View = view;
        ViewHost.Children.Add(view);
        try
        {
            await view.EnsureCoreWebView2Async(environment);
            if (_closed || tab.Closed)
            {
                DisposeView(view);
                return;
            }
            CoreWebView2 core = view.CoreWebView2;
            tab.Core = core;
            Configure(tab, core);
            await AddAccentStyleAsync(core);
            ApplyProcessPriority();
            core.Navigate(tab.Url);
            if (ReferenceEquals(tab, _active))
                RefreshToolbar();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A tab could not be opened: " + ex.Message);
            tab.Title = "Could not open";
        }
    }

    private void SwitchTo(BrowserTab tab)
    {
        if (_closed)
            return;
        BrowserTab? previous = _active;
        _active = tab;
        foreach (BrowserTab each in _tabs)
            each.IsActive = ReferenceEquals(each, tab);
        if (previous != null && !ReferenceEquals(previous, tab))
        {
            _ = CaptureScrollAsync(previous);
            if (previous.View != null)
                previous.View.Visibility = Visibility.Collapsed;
            // Only the tab you are looking at runs, the one you left is put to sleep
            _ = SleepAsync(previous);
        }
        if (tab.View == null)
            _ = CreateViewAsync(tab);
        else if (!_panelHidden)
        {
            tab.View.Visibility = Visibility.Visible;
            Wake(tab);
            tab.View.Focus();
        }
        RefreshToolbar();
        BringTabIntoView(tab);
        ScheduleSave();
    }

    private void CloseTab(BrowserTab tab)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
            return;
        tab.Closed = true;
        _tabs.RemoveAt(index);
        DisposeView(tab.View);
        tab.View = null;
        tab.Core = null;
        if (_tabs.Count == 0)
        {
            // There is always a tab, closing the last one leaves a fresh Google page
            _active = null;
            SwitchTo(AddTab(HomePage));
            return;
        }
        if (ReferenceEquals(_active, tab))
        {
            _active = null;
            SwitchTo(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }
        ScheduleSave();
    }

    private void OpenInNewTab(string url)
    {
        if (_tabs.Count >= MaxTabs)
        {
            Navigate(url);
            return;
        }
        SwitchTo(AddTab(url));
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();

    private void NewTab()
    {
        if (_environment == null)
            return;
        if (_tabs.Count >= MaxTabs)
        {
            Navigate(HomePage);
            return;
        }
        SwitchTo(AddTab(HomePage));
        AddressBox.Focus();
    }

    private void Tab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BrowserTab tab } && !ReferenceEquals(tab, _active))
            SwitchTo(tab);
    }

    // Middle click closes a tab, like any browser
    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || sender is not FrameworkElement { DataContext: BrowserTab tab })
            return;
        e.Handled = true;
        CloseTab(tab);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
            CloseTab(tab);
    }

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void BringTabIntoView(BrowserTab tab)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement chip)
                chip.BringIntoView();
        }));
    }

    // Ctrl+T, Ctrl+W, Ctrl+Tab, Ctrl+Shift+Tab, Ctrl+1 to 9 and Ctrl+L, also while a page has focus
    private void BrowserRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int index = _active == null ? -1 : _tabs.IndexOf(_active);
        switch (key)
        {
            case Key.T:
                NewTab();
                break;
            case Key.W:
                if (_active != null)
                    CloseTab(_active);
                break;
            case Key.Tab when _tabs.Count > 1:
                SwitchTo(_tabs[(index + (shift ? _tabs.Count - 1 : 1)) % _tabs.Count]);
                break;
            case Key.L:
                AddressBox.Focus();
                break;
            case >= Key.D1 and <= Key.D9:
                int wanted = key == Key.D9 ? _tabs.Count - 1 : key - Key.D1;
                if (wanted >= 0 && wanted < _tabs.Count)
                    SwitchTo(_tabs[wanted]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Configure(BrowserTab tab, CoreWebView2 core)
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
        core.NavigationStarting += (_, e) => Fenced(() =>
        {
            // Web pages only: links that would start other apps from inside the game are not followed
            if (!IsWebAddress(e.Uri) && !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                e.Cancel = true;
                return;
            }
            tab.Loading = true;
            if (ReferenceEquals(tab, _active))
                RefreshToolbar();
        });
        core.NavigationCompleted += (_, _) => Fenced(() =>
        {
            tab.Loading = false;
            if (tab.PendingScroll > 0)
                _ = RestoreScrollAsync(tab);
            if (ReferenceEquals(tab, _active))
                RefreshToolbar();
            ScheduleSave();
        });
        core.SourceChanged += (_, _) => Fenced(() =>
        {
            string source = core.Source ?? string.Empty;
            if (IsWebAddress(source))
                tab.Url = source;
            if (ReferenceEquals(tab, _active) && !AddressBox.IsKeyboardFocused)
                ShowAddress(source);
        });
        core.DocumentTitleChanged += (_, _) => Fenced(() =>
        {
            string title = core.DocumentTitle;
            tab.Title = string.IsNullOrWhiteSpace(title) ? HostOf(tab.Url) : title;
        });
        core.FaviconChanged += (_, _) => Fenced(() => tab.Icon = Favicon(core.FaviconUri));
        core.HistoryChanged += (_, _) => Fenced(() =>
        {
            if (ReferenceEquals(tab, _active))
                RefreshToolbar();
        });
        // Links that ask for a new window open in a new tab instead of a window over the game
        core.NewWindowRequested += (_, e) => Fenced(() =>
        {
            e.Handled = true;
            string uri = e.Uri;
            if (IsWebAddress(uri))
                Dispatcher.BeginInvoke(new Action(() => OpenInNewTab(uri)));
        });
        core.ProcessFailed += (_, e) => Fenced(() => OnProcessFailed(tab, e));
    }

    // ---- Keeping your spot ----

    private static async Task CaptureScrollAsync(BrowserTab tab)
    {
        CoreWebView2? core = tab.Core;
        if (core == null || tab.Closed)
            return;
        try
        {
            if (core.IsSuspended)
                return;
            string result = await core.ExecuteScriptAsync("window.scrollY");
            if (double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out double scroll))
                tab.Scroll = scroll;
        }
        catch (Exception)
        {
        }
    }

    // Pages often grow as they load, so the scroll is put back once and again a moment later
    private static async Task RestoreScrollAsync(BrowserTab tab)
    {
        double scroll = tab.PendingScroll;
        tab.PendingScroll = 0;
        string script = "window.scrollTo(0, " + scroll.ToString(CultureInfo.InvariantCulture) + ")";
        try
        {
            if (tab.Core is { } core)
                await core.ExecuteScriptAsync(script);
            await Task.Delay(700);
            if (!tab.Closed && tab.Core is { } later && !later.IsSuspended)
                await later.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
        }
    }

    private void ScheduleSave()
    {
        if (_closed)
            return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveSessionAsync()
    {
        // Taken before anything is awaited, closing the panel clears the tabs right after
        List<BrowserTab> tabs = _tabs.ToList();
        BrowserTab? active = _active;
        if (tabs.Count == 0)
            return;
        if (active != null)
            await CaptureScrollAsync(active);
        SavedSession session = new()
        {
            Active = active == null ? 0 : Math.Max(0, tabs.IndexOf(active)),
            Tabs = tabs.Where(tab => IsWebAddress(tab.Url)).Select(tab => new SavedTab { Url = tab.Url, Title = tab.Title, Scroll = tab.Scroll }).ToList()
        };
        try
        {
            string json = JsonSerializer.Serialize(session);
            string path = SessionPath;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, true);
            });
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Your tabs could not be saved: " + ex.Message);
        }
    }

    private static SavedSession LoadSession()
    {
        try
        {
            string path = SessionPath;
            if (File.Exists(path) && new FileInfo(path).Length < 256 * 1024)
                return JsonSerializer.Deserialize<SavedSession>(File.ReadAllText(path)) ?? new SavedSession();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Your saved tabs could not be read: " + ex.Message);
        }
        return new SavedSession();
    }

    // ---- Crash recovery ----

    private void OnProcessFailed(BrowserTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        App.Logger?.WriteLine(LogIdent, $"A browser process failed: {e.ProcessFailedKind}, {e.Reason}");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The engine itself is gone, every tab is rebuilt on a fresh engine
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Rebuild));
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                // Only the page crashed or hung, loading it again brings it back
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Fenced(() => tab.Core?.Reload())));
                break;
            // The graphics and helper processes are restarted by the engine itself
        }
    }

    private async void Rebuild()
    {
        try
        {
            if (_closed || _environment == null)
                return;
            DateTime now = DateTime.UtcNow;
            while (_rebuilds.Count > 0 && now - _rebuilds.Peek() > TimeSpan.FromMinutes(1))
                _rebuilds.Dequeue();
            await SaveSessionAsync();
            TearDownAll();
            _sharedEnvironment = null;
            if (_rebuilds.Count >= MaxRebuildsPerMinute)
            {
                // Crashing over and over, stop trying until asked so it never loops in the background
                ShowMessage("The browser keeps crashing", "It stopped restarting on its own to keep your game smooth.", true);
                return;
            }
            _rebuilds.Enqueue(now);
            await StartAsync();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The browser could not be rebuilt: " + ex.Message);
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        _rebuilds.Clear();
        TearDownAll();
        _sharedEnvironment = null;
        _ = StartAsync();
    }

    // ---- Staying light ----

    // The whole panel hides with the dock: every tab sleeps and the engine drops to idle priority,
    // then the tab you were on wakes the moment the dock opens again
    private void OnHostVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool visible = e.NewValue is true;
        _panelHidden = !visible;
        BrowserTab? active = _active;
        if (active?.View != null)
        {
            if (visible)
            {
                active.View.Visibility = Visibility.Visible;
                Wake(active);
            }
            else
            {
                _ = SaveSessionAsync();
                active.View.Visibility = Visibility.Collapsed;
                _ = SleepAsync(active);
            }
        }
        ApplyProcessPriority();
    }

    private async Task SleepAsync(BrowserTab tab)
    {
        try
        {
            // After the view has told the engine it is hidden, which suspending needs
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            CoreWebView2? core = tab.Core;
            if (core == null || tab.Closed || (ReferenceEquals(tab, _active) && !_panelHidden))
                return;
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            await core.TrySuspendAsync();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A tab could not be put to sleep: " + ex.Message);
        }
    }

    private static void Wake(BrowserTab tab)
    {
        CoreWebView2? core = tab.Core;
        if (core == null)
            return;
        Fenced(() =>
        {
            if (core.IsSuspended)
                core.Resume();
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        });
    }

    private void OnProcessInfosChanged(object? sender, object e) => Fenced(ApplyProcessPriority);

    // Normal while you are using it so scrolling and typing never stutter, idle while it is hidden
    // so the game gets the CPU back the moment the dock closes
    private void ApplyProcessPriority()
    {
        CoreWebView2? core = _tabs.Select(tab => tab.Core).FirstOrDefault(found => found != null);
        if (core == null)
            return;
        ProcessPriorityClass priority = _panelHidden ? ProcessPriorityClass.Idle : ProcessPriorityClass.Normal;
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
        foreach (int id in ids.Distinct())
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

    // ---- Toolbar ----

    private void RefreshToolbar()
    {
        BrowserTab? tab = _active;
        CoreWebView2? core = tab?.Core;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        bool loading = tab?.Loading == true;
        LoadingBar.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ReloadGlyph.Text = loading ? "" : "";
        ReloadButton.ToolTip = loading ? "Stop" : "Reload (F5)";
        if (!AddressBox.IsKeyboardFocused)
            ShowAddress(core?.Source ?? tab?.Url ?? string.Empty);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Fenced(() => { if (_active?.Core?.CanGoBack == true) _active.Core.GoBack(); });

    private void Forward_Click(object sender, RoutedEventArgs e) => Fenced(() => { if (_active?.Core?.CanGoForward == true) _active.Core.GoForward(); });

    private void Reload_Click(object sender, RoutedEventArgs e) => Fenced(() =>
    {
        if (_active?.Loading == true)
            _active.Core?.Stop();
        else
            _active?.Core?.Reload();
    });

    private void Home_Click(object sender, RoutedEventArgs e) => Navigate(HomePage);

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
            ShowAddress(_active?.Core?.Source ?? _active?.Url ?? string.Empty);
            _active?.View?.Focus();
        }
    }

    private void Go_Click(object sender, RoutedEventArgs e) => Go();

    private void Go()
    {
        string target = ResolveInput(AddressBox.Text);
        if (target.Length == 0)
            return;
        Navigate(target);
        _active?.View?.Focus();
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
        BrowserTab? tab = _active;
        if (tab == null)
        {
            if (_environment != null)
                SwitchTo(AddTab(url));
            else
                _ = StartAsync();
            return;
        }
        tab.Url = url;
        Fenced(() => tab.Core?.Navigate(url));
    }

    private void AddressBox_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        AddressIcon.Text = "";
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(AddressBox.SelectAll));
    }

    private void AddressBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => ShowAddress(_active?.Core?.Source ?? _active?.Url ?? string.Empty);

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
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

    // Selected text, form controls and the text cursor on every page use Voidstrap's accent colour,
    // with black or white selected text depending on how bright the accent is
    private async Task AddAccentStyleAsync(CoreWebView2 core)
    {
        try
        {
            if (_accentScript == null)
            {
                if (TryFindResource("SystemAccentColorPrimaryBrush") is not SolidColorBrush accent)
                    return;
                Color color = accent.Color;
                string fill = $"rgb({color.R}, {color.G}, {color.B})";
                string text = Voidstrap.UI.Converters.ContrastForegroundConverter.For(accent) is SolidColorBrush { Color.R: > 128 } ? "#ffffff" : "#000000";
                string css = $"::selection{{background:{fill} !important;color:{text} !important}}*{{accent-color:{fill}}}input,textarea,[contenteditable]{{caret-color:{fill}}}";
                _accentScript = "(() => { const css = " + JsonSerializer.Serialize(css) + ";"
                    + " const add = () => { if (document.getElementById('voidstrap-accent')) return; const s = document.createElement('style'); s.id = 'voidstrap-accent'; s.textContent = css; (document.head || document.documentElement).appendChild(s); };"
                    + " if (document.documentElement) add(); else document.addEventListener('DOMContentLoaded', add, { once: true }); })();";
            }
            await core.AddScriptToExecuteOnDocumentCreatedAsync(_accentScript);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The accent style could not be added: " + ex.Message);
        }
    }

    // ---- Helpers and cleanup ----

    private void ShowMessage(string title, string text, bool retry)
    {
        MessageTitle.Text = title;
        MessageText.Text = text;
        MessageText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        Message.Visibility = Visibility.Visible;
        LoadingBar.Visibility = Visibility.Collapsed;
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

    private static bool IsWebAddress(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : "New tab";

    private static ImageSource? Favicon(string? url)
    {
        if (!IsWebAddress(url))
            return null;
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.UriSource = new Uri(url!);
            image.DecodePixelWidth = 32;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TearDownAll()
    {
        foreach (BrowserTab tab in _tabs.ToList())
        {
            tab.Closed = true;
            DisposeView(tab.View);
            tab.View = null;
            tab.Core = null;
        }
        _tabs.Clear();
        _active = null;
        if (_environment != null)
        {
            try
            {
                _environment.ProcessInfosChanged -= OnProcessInfosChanged;
            }
            catch (Exception)
            {
            }
        }
        _environment = null;
    }

    private void DisposeView(WebView2? view)
    {
        if (view == null)
            return;
        try
        {
            ViewHost.Children.Remove(view);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A tab could not be detached: " + ex.Message);
        }
        try
        {
            // Closing the last view lets the engine's processes exit, nothing keeps running after the panel closes
            view.Dispose();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A tab could not be closed: " + ex.Message);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _saveTimer.Stop();
        // Saved before the views go away, the tab list is copied before anything is awaited
        _ = SaveSessionAsync();
        _closed = true;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        ViewHost.IsVisibleChanged -= OnHostVisibleChanged;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(TearDownAll));
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

    private sealed class SavedSession
    {
        public int Active { get; set; }
        public List<SavedTab> Tabs { get; set; } = new();
    }

    private sealed class SavedTab
    {
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public double Scroll { get; set; }
    }
}

// One tab: its page view and what the tab strip shows for it
public sealed class BrowserTab : INotifyPropertyChanged
{
    private string _title;
    private ImageSource? _icon;
    private bool _isActive;

    public BrowserTab(string url, string title, double scroll)
    {
        Url = url;
        _title = title;
        PendingScroll = scroll;
        Scroll = scroll;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Url { get; set; }
    public WebView2? View { get; set; }
    public CoreWebView2? Core { get; set; }
    public bool Loading { get; set; }
    public bool Closed { get; set; }
    public double Scroll { get; set; }
    public double PendingScroll { get; set; }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
                return;
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
                return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }
}
