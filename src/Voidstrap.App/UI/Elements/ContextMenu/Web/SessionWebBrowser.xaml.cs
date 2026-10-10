using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Voidstrap.UI.Elements.Overlay;

// A small tabbed browser for the dock, built to cost as little as possible while a game runs:
// one shared browser engine with the background extras switched off, only the tabs on screen running
// (the others are suspended, and put fully to sleep after a while), and everything at idle priority while the dock is closed.
// Your tabs, the tab you were on and where you were on each page are kept, also across restarts.
// Every callback from the browser engine is fenced so a page or engine crash never takes Voidstrap down,
// and a crashed engine is rebuilt on its own.
public partial class SessionWebBrowser : Window
{
    private const string LogIdent = "SessionWebBrowser";
    private const int MaxRebuildsPerMinute = 3;
    private const int MaxTabs = 16;
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(10);

    // Background work a game overlay never needs: syncing, component updates, translate and Office viewers,
    // a spare renderer kept warm, the back forward page cache, and more than a few renderer processes.
    // Pages are drawn on the graphics card with thin overlay scrollbars, and WebRTC never reveals your local address.
    private static readonly string BrowserArguments = string.Join(" ",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-sync",
        "--disable-component-update",
        "--disable-default-apps",
        "--disable-breakpad",
        "--renderer-process-limit=4",
        "--enable-gpu-rasterization",
        "--enable-zero-copy",
        "--enable-smooth-scrolling",
        "--force-webrtc-ip-handling-policy=default_public_interface_only",
        "--enable-features=FluentOverlayScrollbar,MiddleClickAutoscroll",
        "--disable-features=TranslateUI,msWebOOUI,msPdfOOUI,msSmartScreenProtection,SpareRendererForSitePerProcess,BackForwardCache,msEdgeCollections,msShoppingExp,msEdgeSidebarV2",
        "--disable-pinch",
        "--overscroll-history-navigation=0");

    private static readonly string[] HiddenMenuItems = { "share", "webCapture", "readAloud", "addToCollections", "sendTabToSelf", "translate", "visualSearch", "copilot" };

    private static Task<CoreWebView2Environment>? _sharedEnvironment;

    public static readonly DependencyProperty TabWidthProperty =
        DependencyProperty.Register(nameof(TabWidth), typeof(double), typeof(SessionWebBrowser), new PropertyMetadata(200.0));

    private readonly ObservableCollection<BrowserTab> _tabs = new();
    private readonly Queue<DateTime> _rebuilds = new();
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _sleepTimer;
    private readonly BrowserState _state;
    private CoreWebView2Environment? _environment;
    private BrowserTab? _active;
    private BrowserTab? _splitLeft;
    private BrowserTab? _splitRight;
    private string? _accentScript;
    private bool _starting;
    private bool _closed;
    private bool _panelHidden;

    public SessionWebBrowser()
    {
        InitializeComponent();
        _state = BrowserState.Load();
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            _ = SaveSessionAsync();
        };
        _sleepTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _sleepTimer.Tick += (_, _) => SleepIdleTabs();
        _sleepTimer.Start();
        InitializeChrome();
        Loaded += OnLoaded;
        Closed += OnClosed;
        ViewHost.IsVisibleChanged += OnHostVisibleChanged;
    }

    public double TabWidth
    {
        get => (double)GetValue(TabWidthProperty);
        set => SetValue(TabWidthProperty, value);
    }

    private BrowserSettings Settings => _state.Settings;

    private bool IsSplitShown => _splitLeft != null && _splitRight != null && (ReferenceEquals(_active, _splitLeft) || ReferenceEquals(_active, _splitRight));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        RestoreTabs();
        _ = StartAsync();
    }

    // ---- Starting, restoring and rebuilding ----

    // The tabs come back straight away from what was saved; their pages load once the engine is up,
    // and only for the tab you are looking at
    private void RestoreTabs()
    {
        List<SavedTab> saved = _state.Tabs.Where(tab => IsWebAddress(tab.Url) || tab.Url == BrowserTab.NewTabUrl).Take(MaxTabs).ToList();
        if (saved.Count == 0)
            saved.Add(new SavedTab { Url = BrowserTab.NewTabUrl });
        int active = Math.Clamp(_state.Active, 0, saved.Count - 1);
        List<BrowserTab> created = saved.Select(tab =>
        {
            BrowserTab restored = AddTab(tab.Url, tab.Title, tab.Scroll, false);
            restored.IsPinned = tab.Pinned;
            restored.IsMuted = tab.Muted;
            if (!restored.IsNewTab)
            {
                restored.IsHibernated = true;
                restored.Icon = WebIcon(restored.Url);
            }
            return restored;
        }).ToList();
        SwitchTo(created[active]);
    }

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
        try
        {
            _environment = await GetEnvironmentAsync();
            if (_closed)
                return;
            HookEnvironment(_environment);
            Message.Visibility = Visibility.Collapsed;
            if (_tabs.Count == 0)
                RestoreTabs();
            // Whatever is on screen loads now, the other tabs wait until they are opened
            foreach (BrowserTab tab in _tabs.Where(tab => tab.IsShown && tab.View == null && !tab.IsNewTab).ToList())
                _ = CreateViewAsync(tab);
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

    private BrowserTab AddTab(string url, string? title = null, double scroll = 0, bool loadNow = true, int index = -1)
    {
        string name = !string.IsNullOrWhiteSpace(title) ? title : url == BrowserTab.NewTabUrl ? "New tab" : HostOf(url);
        BrowserTab tab = new(url, name, scroll);
        int pinned = _tabs.Count(each => each.IsPinned);
        if (index < 0 || index > _tabs.Count)
            index = _tabs.Count;
        // Pinned tabs always stay together at the start
        index = Math.Max(index, pinned);
        _tabs.Insert(index, tab);
        if (loadNow && !tab.IsNewTab)
            _ = CreateViewAsync(tab);
        UpdateTabWidth();
        return tab;
    }

    // Creates the page for a tab. A page opened by another page with window.open is handed over before it navigates,
    // so sign-in windows and the like keep working as a tab
    private async Task<CoreWebView2?> CreateViewAsync(BrowserTab tab, bool navigate = true)
    {
        CoreWebView2Environment? environment = _environment;
        if (_closed || environment == null || tab.View != null || tab.Closed)
            return null;
        WebView2 view = new()
        {
            // The page area is dark from the very first frame instead of flashing white
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 32, 32, 32)
        };
        Border frame = new() { Child = view, Visibility = Visibility.Collapsed };
        tab.View = view;
        tab.Frame = frame;
        tab.IsHibernated = false;
        ViewHost.Children.Insert(0, frame);
        UpdateViews();
        try
        {
            await view.EnsureCoreWebView2Async(environment);
            if (_closed || tab.Closed)
            {
                DisposeView(tab, view, frame);
                return null;
            }
            CoreWebView2 core = view.CoreWebView2;
            tab.Core = core;
            Configure(tab, view, core);
            AddBlocking(tab, core, environment);
            await AddAccentStyleAsync(core);
            if (tab.IsMuted)
                core.IsMuted = true;
            ApplyZoom(tab);
            ApplyProcessPriority();
            if (navigate)
                core.Navigate(tab.Url);
            if (ReferenceEquals(tab, _active))
                RefreshToolbar();
            return core;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A tab could not be opened: " + ex.Message);
            tab.Title = "Could not open";
            return null;
        }
    }

    private void SwitchTo(BrowserTab tab)
    {
        if (_closed || tab.Closed)
            return;
        List<BrowserTab> wasShown = _tabs.Where(each => each.IsShown).ToList();
        if (!ReferenceEquals(tab, _active))
            CloseFind(false);
        _active = tab;
        tab.LastShown = DateTime.UtcNow;
        foreach (BrowserTab each in _tabs)
            each.IsActive = ReferenceEquals(each, tab);
        UpdateViews();
        foreach (BrowserTab left in wasShown.Where(each => !each.IsShown))
        {
            left.LastShown = DateTime.UtcNow;
            _ = CaptureScrollAsync(left);
            // Only what is on screen runs, the tab you left is suspended
            _ = SleepAsync(left);
        }
        foreach (BrowserTab shown in _tabs.Where(each => each.IsShown))
        {
            if (shown.View == null && !shown.IsNewTab)
                _ = CreateViewAsync(shown);
            else
                Wake(shown);
        }
        if (!_panelHidden)
        {
            if (tab.IsNewTab)
                FocusAddress();
            else
                tab.View?.Focus();
        }
        RestartProgressFor(tab);
        RefreshToolbar();
        BringTabIntoView(tab);
        ScheduleSave();
    }

    // Shows the pages that are on screen: the open tab, or both halves of a split view
    private void UpdateViews()
    {
        if (_splitLeft is { Closed: true } || _splitRight is { Closed: true })
        {
            _splitLeft = null;
            _splitRight = null;
        }
        bool split = IsSplitShown;
        BrowserTab? left = split ? _splitLeft : _active;
        BrowserTab? right = split ? _splitRight : null;
        foreach (BrowserTab tab in _tabs)
        {
            bool shown = ReferenceEquals(tab, left) || ReferenceEquals(tab, right);
            tab.IsShown = shown;
            tab.IsSplit = _splitLeft != null && (ReferenceEquals(tab, _splitLeft) || ReferenceEquals(tab, _splitRight));
            if (tab.Frame is not { } frame)
                continue;
            frame.Visibility = shown && !tab.IsNewTab ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(frame, ReferenceEquals(tab, right) ? 2 : 0);
            // In split view the side you are using has a thin accent outline
            frame.BorderThickness = new Thickness(split ? 1 : 0);
            frame.BorderBrush = split && ReferenceEquals(tab, _active) ? TryFindResource("SystemAccentColorPrimaryBrush") as Brush : Brushes.Transparent;
        }
        if (split)
        {
            if (RightPane.Width.Value == 0)
            {
                LeftPane.Width = new GridLength(1, GridUnitType.Star);
                RightPane.Width = new GridLength(1, GridUnitType.Star);
            }
            SplitGap.Width = new GridLength(6);
            SplitGrip.Visibility = Visibility.Visible;
        }
        else
        {
            LeftPane.Width = new GridLength(1, GridUnitType.Star);
            SplitGap.Width = new GridLength(0);
            RightPane.Width = new GridLength(0);
            SplitGrip.Visibility = Visibility.Collapsed;
        }
        BrowserTab? newTab = left is { IsNewTab: true } ? left : right is { IsNewTab: true } ? right : null;
        if (newTab != null)
        {
            Grid.SetColumn(NewTabPage, ReferenceEquals(newTab, right) ? 2 : 0);
            Grid.SetColumnSpan(NewTabPage, split ? 1 : 3);
            NewTabPage.Visibility = Visibility.Visible;
            RefreshNewTabPage();
        }
        else
        {
            NewTabPage.Visibility = Visibility.Collapsed;
        }
        UpdateNewTabClock();
    }

    private void CloseTab(BrowserTab tab, bool remember = true)
    {
        int index = _tabs.IndexOf(tab);
        if (index < 0)
            return;
        if (remember && !tab.IsNewTab && IsWebAddress(tab.Url))
        {
            _state.Closed.Insert(0, tab.Save());
            if (_state.Closed.Count > BrowserState.MaxClosed)
                _state.Closed.RemoveRange(BrowserState.MaxClosed, _state.Closed.Count - BrowserState.MaxClosed);
        }
        if (tab.IsSplit)
        {
            _splitLeft = null;
            _splitRight = null;
        }
        tab.Closed = true;
        _tabs.RemoveAt(index);
        DisposeView(tab);
        foreach (BrowserTab each in _tabs.Where(each => ReferenceEquals(each.Opener, tab)))
            each.Opener = null;
        if (_tabs.Count == 0)
        {
            // There is always a tab, closing the last one leaves a new tab page
            _active = null;
            SwitchTo(AddTab(BrowserTab.NewTabUrl));
            return;
        }
        if (ReferenceEquals(_active, tab))
        {
            _active = null;
            // Closing a tab a page opened goes back to that page, like following a link and coming back
            BrowserTab next = tab.Opener is { Closed: false } opener && _tabs.Contains(opener) ? opener : _tabs[Math.Min(index, _tabs.Count - 1)];
            SwitchTo(next);
        }
        else
        {
            UpdateViews();
        }
        UpdateTabWidth();
        ScheduleSave();
    }

    private void CloseTabs(IEnumerable<BrowserTab> tabs)
    {
        foreach (BrowserTab tab in tabs.ToList())
            CloseTab(tab);
    }

    private void ReopenClosedTab()
    {
        if (_state.Closed.Count == 0)
            return;
        SavedTab saved = _state.Closed[0];
        _state.Closed.RemoveAt(0);
        ReopenTab(saved);
    }

    private void ReopenTab(SavedTab saved)
    {
        _state.Closed.Remove(saved);
        if (_tabs.Count >= MaxTabs)
        {
            Navigate(saved.Url);
            return;
        }
        BrowserTab tab = AddTab(saved.Url, saved.Title, saved.Scroll, true, NextIndex());
        tab.IsMuted = saved.Muted;
        SwitchTo(tab);
    }

    private void DuplicateTab(BrowserTab tab)
    {
        if (_tabs.Count >= MaxTabs)
            return;
        BrowserTab copy = AddTab(tab.Url, tab.Title, tab.Scroll, true, _tabs.IndexOf(tab) + 1);
        copy.Opener = tab;
        SwitchTo(copy);
    }

    // Links open next to the tab they came from, after the ones it already opened, when that option is on
    private int NextIndex(BrowserTab? opener = null)
    {
        BrowserTab? from = opener ?? _active;
        if (!Settings.OpenNextToCurrent || from == null)
            return -1;
        int index = _tabs.IndexOf(from) + 1;
        while (index < _tabs.Count && ReferenceEquals(_tabs[index].Opener, from))
            index++;
        return index;
    }

    private void OpenInNewTab(string url, BrowserTab? opener = null)
    {
        if (_tabs.Count >= MaxTabs)
        {
            Navigate(url);
            return;
        }
        BrowserTab tab = AddTab(url, null, 0, true, NextIndex(opener));
        tab.Opener = opener ?? _active;
        SwitchTo(tab);
    }

    private void NewTab()
    {
        if (_tabs.Count >= MaxTabs)
        {
            // Out of tabs: the current tab becomes the new tab page instead
            if (_active != null)
                ShowToast("\uE946", "That is the most tabs at once, close one to open another");
            return;
        }
        SwitchTo(AddTab(BrowserTab.NewTabUrl));
    }

    private void TogglePin(BrowserTab tab)
    {
        tab.IsPinned = !tab.IsPinned;
        int pinned = _tabs.Count(each => each.IsPinned && !ReferenceEquals(each, tab));
        int from = _tabs.IndexOf(tab);
        int to = tab.IsPinned ? pinned : Math.Max(pinned, 0);
        if (from != to)
            _tabs.Move(from, to);
        UpdateTabWidth();
        ScheduleSave();
    }

    private void ToggleMute(BrowserTab tab)
    {
        tab.IsMuted = !tab.IsMuted;
        Fenced(() =>
        {
            if (tab.Core != null)
                tab.Core.IsMuted = tab.IsMuted;
        });
        ScheduleSave();
    }

    // Closes a tab's page to free its memory; it opens again where you were the next time you go to it
    private void Hibernate(BrowserTab tab)
    {
        if (tab.IsShown || tab.View == null || tab.IsNewTab)
            return;
        tab.PendingScroll = tab.Scroll;
        DisposeView(tab);
        tab.IsHibernated = true;
        tab.IsLoading = false;
        tab.IsAudible = false;
    }

    private void SleepIdleTabs()
    {
        if (_closed || !Settings.MemorySaver)
            return;
        DateTime now = DateTime.UtcNow;
        foreach (BrowserTab tab in _tabs.Where(tab => !tab.IsShown && tab.View != null && !tab.IsAudible && now - tab.LastShown > SleepAfter).ToList())
            Hibernate(tab);
    }

    // ---- Split view ----

    private void ToggleSplit()
    {
        if (IsSplitShown)
            ExitSplit();
        else
            EnterSplit(null);
    }

    // The tab you are on goes left; on the right goes the given tab, or the one you used last, or a new tab page
    private void EnterSplit(BrowserTab? partner)
    {
        BrowserTab? current = _active;
        if (current == null)
            return;
        partner ??= _tabs.Where(tab => !ReferenceEquals(tab, current)).OrderByDescending(tab => tab.LastShown).FirstOrDefault();
        if (partner == null || ReferenceEquals(partner, current))
        {
            if (_tabs.Count >= MaxTabs)
                return;
            partner = AddTab(BrowserTab.NewTabUrl, null, 0, false, _tabs.IndexOf(current) + 1);
        }
        _splitLeft = current;
        _splitRight = partner;
        LeftPane.Width = new GridLength(1, GridUnitType.Star);
        RightPane.Width = new GridLength(1, GridUnitType.Star);
        SwitchTo(partner);
    }

    private void OpenInSplit(string url, BrowserTab from)
    {
        if (_tabs.Count >= MaxTabs)
            return;
        BrowserTab tab = AddTab(url, null, 0, true, _tabs.IndexOf(from) + 1);
        tab.Opener = from;
        _splitLeft = from;
        _splitRight = tab;
        SwitchTo(tab);
    }

    private void ExitSplit()
    {
        _splitLeft = null;
        _splitRight = null;
        if (_active != null)
            SwitchTo(_active);
    }

    // Clicking into the other half of a split view makes it the tab the toolbar works on
    private void OnViewFocused(BrowserTab tab)
    {
        if (ReferenceEquals(tab, _active) || !tab.IsShown)
            return;
        _active = tab;
        foreach (BrowserTab each in _tabs)
            each.IsActive = ReferenceEquals(each, tab);
        tab.LastShown = DateTime.UtcNow;
        UpdateViews();
        RestartProgressFor(tab);
        RefreshToolbar();
    }

    // ---- The page ----

    private void Configure(BrowserTab tab, WebView2 view, CoreWebView2 core)
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
            // Sites see a plain Chrome, so none of them push Edge-only prompts
            settings.UserAgent = Regex.Replace(settings.UserAgent, @"\s*Edg/[\d.]+", string.Empty);
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
            tab.IsLoading = true;
            tab.Blocked = 0;
            if (ReferenceEquals(tab, _active))
            {
                StartProgress();
                RefreshToolbar();
            }
        });
        core.NavigationCompleted += (_, e) => Fenced(() =>
        {
            tab.IsLoading = false;
            if (e.IsSuccess)
                _state.Visit(core.Source, core.DocumentTitle);
            if (tab.PendingScroll > 0)
                _ = RestoreScrollAsync(tab);
            if (ReferenceEquals(tab, _active))
            {
                FinishProgress();
                RefreshToolbar();
            }
            ScheduleSave();
        });
        core.SourceChanged += (_, _) => Fenced(() =>
        {
            string source = core.Source ?? string.Empty;
            string oldHost = tab.Host;
            if (IsWebAddress(source))
                tab.Url = source;
            if (!string.Equals(oldHost, tab.Host, StringComparison.OrdinalIgnoreCase))
                ApplyZoom(tab);
            if (ReferenceEquals(tab, _active))
                RefreshToolbar();
        });
        core.DocumentTitleChanged += (_, _) => Fenced(() =>
        {
            string title = core.DocumentTitle;
            tab.Title = string.IsNullOrWhiteSpace(title) ? HostOf(tab.Url) : title;
            _state.Retitle(core.Source, title);
        });
        core.FaviconChanged += (_, _) => LoadFavicon(tab, core);
        core.HistoryChanged += (_, _) => Fenced(() =>
        {
            if (ReferenceEquals(tab, _active))
                RefreshToolbar();
        });
        core.NewWindowRequested += (_, e) => Fenced(() =>
        {
            CoreWebView2Deferral deferral = e.GetDeferral();
            e.Handled = true;
            _ = AdoptNewWindowAsync(tab, e, deferral);
        });
        core.StatusBarTextChanged += (_, _) => Fenced(() =>
        {
            if (tab.IsShown)
                ShowStatus(core.StatusBarText);
        });
        core.IsDocumentPlayingAudioChanged += (_, _) => Fenced(() =>
        {
            tab.IsAudible = core.IsDocumentPlayingAudio;
            ApplyProcessPriority();
        });
        core.IsMutedChanged += (_, _) => Fenced(() => tab.IsMuted = core.IsMuted);
        core.ContainsFullScreenElementChanged += (_, _) => Fenced(() => SetPageFullscreen(tab.IsShown && core.ContainsFullScreenElement));
        core.ContextMenuRequested += (_, e) => Fenced(() => CustomizeContextMenu(tab, e));
        core.ProcessFailed += (_, e) => Fenced(() => OnProcessFailed(tab, e));
        HookFind(tab, core);
        view.ZoomFactorChanged += (_, _) => Fenced(() => OnZoomChanged(tab));
        view.GotFocus += (_, _) => Fenced(() => OnViewFocused(tab));
    }

    // window.open and target=_blank links become a tab next to the page that opened them
    private async Task AdoptNewWindowAsync(BrowserTab opener, CoreWebView2NewWindowRequestedEventArgs e, CoreWebView2Deferral deferral)
    {
        try
        {
            string uri = e.Uri;
            if (_closed)
                return;
            if (_tabs.Count >= MaxTabs || _environment == null)
            {
                if (IsWebAddress(uri))
                    Navigate(uri);
                return;
            }
            BrowserTab child = AddTab(IsWebAddress(uri) ? uri : "about:blank", null, 0, false, NextIndex(opener));
            child.Opener = opener;
            Task<CoreWebView2?> creating = CreateViewAsync(child, false);
            SwitchTo(child);
            CoreWebView2? core = await creating;
            if (core != null)
                e.NewWindow = core;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A new tab could not be opened: " + ex.Message);
        }
        finally
        {
            Fenced(deferral.Complete);
        }
    }

    // The browser engine checks the blocked addresses itself; only a request that matches comes back here
    private void AddBlocking(BrowserTab tab, CoreWebView2 core, CoreWebView2Environment environment)
    {
        try
        {
            foreach (string pattern in BrowserBlocklist.Patterns)
                core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Ad blocking could not be set up: " + ex.Message);
            return;
        }
        core.WebResourceRequested += (_, e) => Fenced(() =>
        {
            // Pages you open yourself are never blocked, only what they load
            if (!Settings.BlockAds || e.ResourceContext == CoreWebView2WebResourceContext.Document)
                return;
            string site = tab.Host;
            if (IsAllowedSite(site) || BrowserBlocklist.IsBlockedHost(site))
                return;
            e.Response = environment.CreateWebResourceResponse(null, 403, "Blocked", "Content-Type: text/plain");
            tab.Blocked++;
            if (ReferenceEquals(tab, _active))
                RefreshShield();
        });
    }

    private bool IsAllowedSite(string host)
        => host.Length > 0 && Settings.AllowedSites.Any(site => host.Equals(site, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase));

    // The page's own menu, without the extras nobody needs over a game, and with tabs and split view added for links
    private void CustomizeContextMenu(BrowserTab tab, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        CoreWebView2Environment? environment = _environment;
        if (environment == null)
            return;
        IList<CoreWebView2ContextMenuItem> items = e.MenuItems;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (HiddenMenuItems.Contains(items[i].Name, StringComparer.OrdinalIgnoreCase) || items[i].Name == "openLinkInNewWindow")
                items.RemoveAt(i);
        }
        CoreWebView2ContextMenuTarget target = e.ContextMenuTarget;
        if (target.HasLinkUri && IsWebAddress(target.LinkUri))
        {
            string link = target.LinkUri;
            CoreWebView2ContextMenuItem newTab = environment.CreateContextMenuItem("Open link in new tab", null, CoreWebView2ContextMenuItemKind.Command);
            newTab.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(new Action(() => OpenInNewTab(link, tab)));
            CoreWebView2ContextMenuItem split = environment.CreateContextMenuItem("Open link in split view", null, CoreWebView2ContextMenuItemKind.Command);
            split.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(new Action(() => OpenInSplit(link, tab)));
            items.Insert(0, newTab);
            items.Insert(1, split);
            items.Insert(2, environment.CreateContextMenuItem(string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));
        }
        else if (target.Kind == CoreWebView2ContextMenuTargetKind.Page && !target.HasSelection && !target.IsEditable)
        {
            CoreWebView2ContextMenuItem copy = environment.CreateContextMenuItem("Copy page link", null, CoreWebView2ContextMenuItemKind.Command);
            copy.CustomItemSelected += (_, _) => Dispatcher.BeginInvoke(new Action(() => CopyLink(tab)));
            items.Add(environment.CreateContextMenuItem(string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));
            items.Add(copy);
        }
        // No separators left at the ends or doubled up after the removals
        for (int i = items.Count - 1; i >= 0; i--)
        {
            bool separator = items[i].Kind == CoreWebView2ContextMenuItemKind.Separator;
            if (separator && (i == 0 || i == items.Count - 1 || items[i - 1].Kind == CoreWebView2ContextMenuItemKind.Separator))
                items.RemoveAt(i);
        }
    }

    // The tab's icon straight from the engine, so it costs no extra download
    private async void LoadFavicon(BrowserTab tab, CoreWebView2 core)
    {
        try
        {
            using Stream stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (tab.Closed || stream == null || stream.Length == 0)
                return;
            BitmapImage image = new();
            image.BeginInit();
            image.StreamSource = stream;
            image.DecodePixelWidth = 32;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            tab.Icon = image;
            if (tab.Host.Length > 0)
                _icons[tab.Host] = image;
        }
        catch (Exception)
        {
            // Keeps the icon it had, a missing icon is not worth a message
        }
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
        List<BrowserTab> tabs = _tabs.Where(tab => IsWebAddress(tab.Url) || tab.IsNewTab).ToList();
        BrowserTab? active = _active;
        if (tabs.Count == 0)
            return;
        if (active != null)
            await CaptureScrollAsync(active);
        _state.Tabs = tabs.Select(tab => tab.Save()).ToList();
        _state.Active = active == null ? 0 : Math.Max(0, tabs.IndexOf(active));
        try
        {
            await BrowserState.SaveAsync(JsonSerializer.Serialize(_state));
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Your tabs could not be saved: " + ex.Message);
        }
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

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _rebuilds.Clear();
            if (_tabs.Count > 0)
                await SaveSessionAsync();
            TearDownAll();
            _sharedEnvironment = null;
            await StartAsync();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The browser could not be restarted: " + ex.Message);
        }
    }

    // ---- Staying light ----

    // The whole panel hides with the dock: every tab sleeps and the engine drops to idle priority,
    // then what was on screen wakes the moment the dock opens again
    private void OnHostVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool visible = e.NewValue is true;
        _panelHidden = !visible;
        List<BrowserTab> shown = _tabs.Where(tab => tab.IsShown).ToList();
        if (visible)
        {
            foreach (BrowserTab tab in shown)
                Wake(tab);
        }
        else
        {
            _ = SaveSessionAsync();
            CloseFloatingParts();
            foreach (BrowserTab tab in shown)
            {
                tab.LastShown = DateTime.UtcNow;
                _ = SleepAsync(tab);
            }
        }
        UpdateNewTabClock();
        ApplyProcessPriority();
    }

    private async Task SleepAsync(BrowserTab tab)
    {
        try
        {
            // After the view has told the engine it is hidden, which suspending needs
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            CoreWebView2? core = tab.Core;
            // A tab playing music keeps playing, it is only made lighter
            if (core == null || tab.Closed || (tab.IsShown && !_panelHidden))
                return;
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
            if (!tab.IsAudible)
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

    // Normal while you are using it so scrolling and typing never stutter; while hidden it is idle so the game gets
    // the CPU back, or just below normal when music is playing so it never crackles
    private void ApplyProcessPriority()
    {
        CoreWebView2? core = _tabs.Select(tab => tab.Core).FirstOrDefault(found => found != null);
        if (core == null)
            return;
        ProcessPriorityClass priority = !_panelHidden ? ProcessPriorityClass.Normal
            : _tabs.Any(tab => tab.IsAudible && !tab.IsMuted) ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Idle;
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

    // ---- Accent ----

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
        HideProgress();
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

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0 ? uri.Host : "New tab";

    // A site's icon for tabs that have not loaded yet, fetched in the background by the image itself
    private ImageSource? WebIcon(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !IsWebAddress(url))
            return null;
        if (_icons.TryGetValue(uri.Host, out ImageSource? cached))
            return cached;
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.UriSource = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/favicon.ico");
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
            DisposeView(tab);
        }
        _tabs.Clear();
        _active = null;
        _splitLeft = null;
        _splitRight = null;
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

    private void DisposeView(BrowserTab tab)
    {
        WebView2? view = tab.View;
        Border? frame = tab.Frame;
        tab.View = null;
        tab.Frame = null;
        tab.Core = null;
        DisposeView(tab, view, frame);
    }

    private void DisposeView(BrowserTab tab, WebView2? view, Border? frame)
    {
        if (ReferenceEquals(tab.View, view))
        {
            tab.View = null;
            tab.Frame = null;
            tab.Core = null;
        }
        try
        {
            if (frame != null)
            {
                frame.Child = null;
                ViewHost.Children.Remove(frame);
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "A tab could not be detached: " + ex.Message);
        }
        try
        {
            // Closing the last view lets the engine's processes exit, nothing keeps running after the panel closes
            view?.Dispose();
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
        _sleepTimer.Stop();
        // Saved before the views go away, the tab list is copied before anything is awaited
        _ = SaveSessionAsync();
        _closed = true;
        CloseFloatingParts();
        StopChrome();
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
}
