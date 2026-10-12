using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace Voidstrap.UI.Elements.Overlay;

// Voidstrap has its own ContextMenu namespace and FontFamily model, these names mean the WPF ones here
using ContextMenu = System.Windows.Controls.ContextMenu;
using FontFamily = System.Windows.Media.FontFamily;

// Everything around the page: the three layouts and zen mode, the tab strip, the toolbar buttons and menus,
// keyboard shortcuts, the loading line, the link status pill, toasts and the new tab page
public partial class SessionWebBrowser
{
    private static readonly double[] ZoomSteps = { 0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5 };

    private static readonly (string Name, string Url)[] StarterSites =
    {
        ("Roblox", "https://www.roblox.com/"),
        ("YouTube", "https://www.youtube.com/"),
        ("DevForum", "https://devforum.roblox.com/"),
        ("Google", "https://www.google.com/"),
        ("Wikipedia", "https://en.wikipedia.org/"),
        ("Reddit", "https://www.reddit.com/"),
        ("Twitch", "https://www.twitch.tv/"),
        ("GitHub", "https://github.com/")
    };

    private static readonly Color[] TileColors =
    {
        Color.FromRgb(0xD9, 0x4F, 0x4F), Color.FromRgb(0xE0, 0x8A, 0x2E), Color.FromRgb(0xC9, 0xA2, 0x27), Color.FromRgb(0x4C, 0xA8, 0x5A),
        Color.FromRgb(0x2E, 0x9C, 0x9C), Color.FromRgb(0x3D, 0x7E, 0xD6), Color.FromRgb(0x6A, 0x5A, 0xD6), Color.FromRgb(0xB0, 0x4F, 0xC2)
    };

    private DispatcherTimer? _zenTimer;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _clockTimer;
    private bool _zenRevealed;
    private bool _pageFullscreen;
    private BrowserTab? _pressTab;
    private Point _pressPoint;
    private bool _dragging;
    private ItemsControl? _dragHost;

    private void InitializeChrome()
    {
        _zenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _zenTimer.Tick += (_, _) => TuckChromeIfIdle();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastPopup.IsOpen = false;
        };
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _clockTimer.Tick += (_, _) => NewTabClock.Text = DateTime.Now.ToString("t");
        foreach (ItemsControl host in new ItemsControl[] { TabStrip, TabList })
        {
            host.MouseMove += TabHost_MouseMove;
            host.MouseUp += TabHost_MouseUp;
            host.LostMouseCapture += (_, _) => EndDrag();
        }
        InitializeOmnibox();
        ApplyLayout();
    }

    private void StopChrome()
    {
        _zenTimer?.Stop();
        _toastTimer?.Stop();
        _clockTimer?.Stop();
        StopOmnibox();
    }

    // Popups float in their own little windows, so they are closed whenever the panel goes away
    private void CloseFloatingParts()
    {
        SuggestPopup.IsOpen = false;
        TabSearchPopup.IsOpen = false;
        StatusPopup.IsOpen = false;
        ToastPopup.IsOpen = false;
    }

    // ---- Layouts and zen mode ----

    // Tabs on top with the toolbar under them, everything in one compact row, or tabs down the side
    private void ApplyLayout()
    {
        BrowserLayout layout = Settings.Layout;
        if (TabRow.Parent is Panel parent)
            parent.Children.Remove(TabRow);
        if (layout == BrowserLayout.Compact)
        {
            Toolbar.Children.Add(TabRow);
            Grid.SetRow(TabRow, 0);
            Grid.SetColumn(TabRow, 2);
            TabRow.Margin = new Thickness(6, 0, 0, 0);
            AddressColumn.Width = new GridLength(340);
            CompactTabsColumn.Width = new GridLength(1, GridUnitType.Star);
            Toolbar.Margin = new Thickness(6, 6, 6, 6);
        }
        else
        {
            Chrome.Children.Add(TabRow);
            Grid.SetRow(TabRow, 0);
            Grid.SetColumn(TabRow, 0);
            TabRow.Margin = new Thickness(6, 6, 6, 0);
            AddressColumn.Width = new GridLength(1, GridUnitType.Star);
            CompactTabsColumn.Width = new GridLength(0);
            Toolbar.Margin = layout == BrowserLayout.Vertical ? new Thickness(6, 6, 6, 6) : new Thickness(6, 5, 6, 6);
        }
        TabRow.Visibility = layout == BrowserLayout.Vertical ? Visibility.Collapsed : Visibility.Visible;
        // Only the tab list in use builds its tabs
        TabStrip.ItemsSource = layout == BrowserLayout.Vertical ? null : _tabs;
        TabList.ItemsSource = layout == BrowserLayout.Vertical ? _tabs : null;
        ApplyChromeVisibility();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(UpdateTabWidth));
    }

    private void SetLayout(BrowserLayout layout)
    {
        Settings.Layout = layout;
        ApplyLayout();
        ScheduleSave();
    }

    // Zen mode, and a video playing full screen, hide everything but the page; touching the top edge brings it back
    private void ApplyChromeVisibility()
    {
        bool tucked = _pageFullscreen || Settings.ZenMode;
        bool show = !tucked || (_zenRevealed && !_pageFullscreen);
        Chrome.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SideBar.Visibility = show && Settings.Layout == BrowserLayout.Vertical ? Visibility.Visible : Visibility.Collapsed;
        if (ProgressRow.Parent is Grid grid)
            grid.RowDefinitions[1].Height = new GridLength(_pageFullscreen ? 0 : 3);
    }

    private void ToggleZen()
    {
        Settings.ZenMode = !Settings.ZenMode;
        _zenRevealed = false;
        ApplyChromeVisibility();
        ShowToast(Settings.ZenMode ? "\uE740" : "\uE73F", Settings.ZenMode ? "Zen mode: move to the top edge for the toolbar, F11 to leave" : "Zen mode off");
        ScheduleSave();
    }

    private void SetPageFullscreen(bool fullscreen)
    {
        if (_pageFullscreen == fullscreen)
            return;
        _pageFullscreen = fullscreen;
        ApplyChromeVisibility();
    }

    private void ProgressRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!Settings.ZenMode || _pageFullscreen || _zenRevealed)
            return;
        _zenRevealed = true;
        ApplyChromeVisibility();
    }

    private void Chrome_MouseLeave(object sender, MouseEventArgs e)
    {
        if (Settings.ZenMode && _zenRevealed)
        {
            _zenTimer?.Stop();
            _zenTimer?.Start();
        }
    }

    private void TuckChromeIfIdle()
    {
        _zenTimer?.Stop();
        if (!Settings.ZenMode || !_zenRevealed)
            return;
        // Stays while you are typing in it, choosing a suggestion or using one of its menus
        if (Chrome.IsMouseOver || SideBar.IsMouseOver || AddressBox.IsKeyboardFocused || FindBox.IsKeyboardFocused || SuggestPopup.IsOpen || TabSearchPopup.IsOpen || _menuOpen)
        {
            _zenTimer?.Start();
            return;
        }
        _zenRevealed = false;
        ApplyChromeVisibility();
    }

    // ---- The tab strip ----

    private void UpdateTabWidth()
    {
        int pinned = _tabs.Count(tab => tab.IsPinned);
        int others = Math.Max(1, _tabs.Count - pinned);
        double available = TabScroller.ActualWidth - pinned * 43;
        if (available <= 0)
            return;
        TabWidth = Math.Round(Math.Clamp(available / others - 3, 72, 220));
    }

    private void TabScroller_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidth();

    private void TabScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void BringTabIntoView(BrowserTab tab)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            ItemsControl host = Settings.Layout == BrowserLayout.Vertical ? TabList : TabStrip;
            if (host.ItemContainerGenerator.ContainerFromItem(tab) is FrameworkElement chip)
                chip.BringIntoView();
        }));
    }

    // A tab opens the moment it is pressed; dragging it moves it, middle click closes it
    private void Tab_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab } || IsInsideButton(e.OriginalSource as DependencyObject))
            return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            CloseTab(tab);
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
            return;
        if (!ReferenceEquals(tab, _active))
        {
            if (tab.IsShown)
                OnViewFocused(tab);
            else
                SwitchTo(tab);
        }
        _pressTab = tab;
        _pressPoint = e.GetPosition(this);
        _dragHost = Settings.Layout == BrowserLayout.Vertical ? TabList : TabStrip;
    }

    private void Tab_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressTab == null || _dragging || e.LeftButton != MouseButtonState.Pressed)
            return;
        Vector moved = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(moved.X) < 6 && Math.Abs(moved.Y) < 6)
            return;
        ItemsControl host = Settings.Layout == BrowserLayout.Vertical ? TabList : TabStrip;
        _dragHost = host;
        _dragging = host.CaptureMouse();
    }

    private void Tab_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            _pressTab = null;
    }

    private void TabHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _pressTab == null || _dragHost == null)
            return;
        bool vertical = ReferenceEquals(_dragHost, TabList);
        Point point = e.GetPosition(_dragHost);
        int from = _tabs.IndexOf(_pressTab);
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (i == from || _tabs[i].IsPinned != _pressTab.IsPinned)
                continue;
            if (_dragHost.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement container)
                continue;
            Rect bounds = container.TransformToAncestor(_dragHost).TransformBounds(new Rect(container.RenderSize));
            // Past the middle of the neighbour, the dragged tab takes its place
            bool past = vertical
                ? point.Y >= bounds.Top && point.Y <= bounds.Bottom && (i > from ? point.Y > bounds.Top + bounds.Height / 2 : point.Y < bounds.Bottom - bounds.Height / 2)
                : point.X >= bounds.Left && point.X <= bounds.Right && (i > from ? point.X > bounds.Left + bounds.Width / 2 : point.X < bounds.Right - bounds.Width / 2);
            if (past)
            {
                _tabs.Move(from, i);
                break;
            }
        }
    }

    private void TabHost_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
            _dragHost?.ReleaseMouseCapture();
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragging)
            ScheduleSave();
        _dragging = false;
        _pressTab = null;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        for (DependencyObject? node = source; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
                return true;
            if (node is ContentPresenter { Content: BrowserTab })
                return false;
        }
        return false;
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
            CloseTab(tab);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: BrowserTab tab })
            ToggleMute(tab);
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => NewTab();

    private void Tab_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab } chip)
            return;
        e.Handled = true;
        int index = _tabs.IndexOf(tab);
        ContextMenu menu = NewMenu(chip);
        menu.Items.Add(Item("New tab to the right", "\uE710", null, () =>
        {
            if (_tabs.Count < MaxTabs)
                SwitchTo(AddTab(BrowserTab.NewTabUrl, null, 0, false, _tabs.IndexOf(tab) + 1));
        }, _tabs.Count < MaxTabs));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reload", "\uE72C", "F5", () => Fenced(() => tab.Core?.Reload()), tab.Core != null));
        menu.Items.Add(Item("Duplicate", "\uE8C8", null, () => DuplicateTab(tab), !tab.IsNewTab && _tabs.Count < MaxTabs));
        menu.Items.Add(Item(tab.IsPinned ? "Unpin tab" : "Pin tab", tab.IsPinned ? "\uE77A" : "\uE718", null, () => TogglePin(tab)));
        menu.Items.Add(Item(tab.IsMuted ? "Unmute tab" : "Mute tab", tab.IsMuted ? "\uE767" : "\uE74F", null, () => ToggleMute(tab), !tab.IsNewTab));
        menu.Items.Add(Item("Copy link", "\uE71B", null, () => CopyLink(tab), !tab.IsNewTab));
        if (tab.IsSplit)
            menu.Items.Add(Item("Close split view", "\uE89F", "Ctrl+Shift+S", ExitSplit));
        else if (!ReferenceEquals(tab, _active))
            menu.Items.Add(Item("Open in split view", "\uE8A0", null, () => EnterSplit(tab)));
        menu.Items.Add(Item("Put tab to sleep", "\uE708", null, () =>
        {
            Hibernate(tab);
            ShowToast("\uE708", "Tab asleep, it wakes where you were when you open it");
        }, !tab.IsShown && tab.View != null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Close tab", "\uE711", "Ctrl+W", () => CloseTab(tab)));
        menu.Items.Add(Item("Close other tabs", null, null, () => CloseTabs(_tabs.Where(each => !ReferenceEquals(each, tab) && !each.IsPinned)), _tabs.Count > 1));
        menu.Items.Add(Item("Close tabs to the left", null, null, () => CloseTabs(_tabs.Take(_tabs.IndexOf(tab)).Where(each => !each.IsPinned)), index > 0));
        menu.Items.Add(Item("Close tabs to the right", null, null, () => CloseTabs(_tabs.Skip(_tabs.IndexOf(tab) + 1)), index < _tabs.Count - 1));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Reopen closed tab", "\uE7A7", "Ctrl+Shift+T", ReopenClosedTab, _state.Closed.Count > 0));
        menu.IsOpen = true;
    }

    // ---- Toolbar ----

    private void RefreshToolbar()
    {
        BrowserTab? tab = _active;
        CoreWebView2? core = tab?.Core;
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        bool loading = tab?.IsLoading == true;
        ReloadGlyph.Text = loading ? "\uE711" : "\uE72C";
        ReloadButton.ToolTip = loading ? "Stop loading" : "Reload (F5)";
        ReloadButton.IsEnabled = core != null;
        CopyLinkButton.Visibility = tab is { IsNewTab: false } ? Visibility.Visible : Visibility.Collapsed;
        SplitButton.Foreground = IsSplitShown ? TryFindResource("SystemAccentColorPrimaryBrush") as Brush : TryFindResource("TextFillColorPrimaryBrush") as Brush;
        SplitButton.ToolTip = IsSplitShown ? "Close split view (Ctrl+Shift+S)" : "Split view (Ctrl+Shift+S)";
        if (!AddressBox.IsKeyboardFocused)
            ShowAddress();
        RefreshShield();
        RefreshZoom();
    }

    private void RefreshShield()
    {
        BrowserTab? tab = _active;
        string site = tab?.Host ?? string.Empty;
        bool web = tab is { IsNewTab: false };
        bool allowed = IsAllowedSite(site);
        bool blocking = Settings.BlockAds && !allowed;
        int count = tab?.Blocked ?? 0;
        ShieldGlyph.Opacity = blocking ? 1 : 0.45;
        ShieldBadge.Visibility = blocking && count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShieldCount.Text = count > 99 ? "99+" : count.ToString();
        ShieldButton.IsEnabled = web || !Settings.BlockAds;
        ShieldButton.ToolTip = !Settings.BlockAds ? "Ad and tracker blocking is off, click to turn it on"
            : allowed ? $"Ads are allowed on {site}, click to block them again"
            : count > 0 ? $"{count} ads and trackers blocked on this page\nClick to allow them on {site}"
            : web ? $"Blocking ads and trackers\nClick to allow them on {site}" : "Blocking ads and trackers";
    }

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        if (!Settings.BlockAds)
        {
            Settings.BlockAds = true;
            ShowToast("\uEA18", "Blocking ads and trackers");
        }
        else if (_active is { IsNewTab: false } tab && tab.Host.Length > 0)
        {
            string site = tab.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? tab.Host[4..] : tab.Host;
            if (IsAllowedSite(tab.Host))
            {
                Settings.AllowedSites.RemoveAll(each => tab.Host.Equals(each, StringComparison.OrdinalIgnoreCase) || tab.Host.EndsWith("." + each, StringComparison.OrdinalIgnoreCase));
                ShowToast("\uEA18", $"Blocking ads on {site} again");
            }
            else
            {
                Settings.AllowedSites.Add(site);
                ShowToast("\uEA18", $"Ads allowed on {site}");
            }
            Fenced(() => tab.Core?.Reload());
        }
        RefreshShield();
        ScheduleSave();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Forward_Click(object sender, RoutedEventArgs e) => GoForward();

    private void GoBack() => Fenced(() =>
    {
        if (_active?.Core?.CanGoBack == true)
            _active.Core.GoBack();
    });

    private void GoForward() => Fenced(() =>
    {
        if (_active?.Core?.CanGoForward == true)
            _active.Core.GoForward();
    });

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    private void Reload() => Fenced(() =>
    {
        if (_active?.IsLoading == true)
            _active.Core?.Stop();
        else
            _active?.Core?.Reload();
    });

    private void Split_Click(object sender, RoutedEventArgs e) => ToggleSplit();

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_active != null)
            CopyLink(_active);
    }

    private void CopyLink(BrowserTab tab)
    {
        if (tab.IsNewTab)
            return;
        try
        {
            Clipboard.SetText(tab.Core?.Source is { Length: > 0 } source ? source : tab.Url);
            ShowToast("\uE8C8", "Link copied");
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The link could not be copied: " + ex.Message);
        }
    }

    // Hands the page to your normal browser, for anything that needs more than the overlay
    private void OpenOutside(BrowserTab tab)
    {
        if (tab.IsNewTab)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(tab.Core?.Source is { Length: > 0 } source ? source : tab.Url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The page could not be opened in the browser: " + ex.Message);
        }
    }

    private void Navigate(string url)
    {
        BrowserTab? tab = _active;
        if (tab == null)
        {
            SwitchTo(AddTab(url));
            return;
        }
        bool wasNewTab = tab.IsNewTab;
        tab.Url = url;
        if (wasNewTab)
            tab.Title = HostOf(url);
        if (tab.Core != null)
            Fenced(() => tab.Core.Navigate(url));
        else if (tab.View == null)
            _ = CreateViewAsync(tab);
        if (wasNewTab)
        {
            UpdateViews();
            RefreshToolbar();
        }
    }

    // ---- Zoom ----

    private void ApplyZoom(BrowserTab tab)
    {
        if (tab.View == null)
            return;
        double zoom = Settings.Zoom.TryGetValue(tab.Host, out double saved) ? saved : 1;
        if (Math.Abs(tab.View.ZoomFactor - zoom) > 0.001)
            tab.View.ZoomFactor = zoom;
    }

    // Zoom is remembered per site, like any browser
    private void OnZoomChanged(BrowserTab tab)
    {
        if (tab.View == null || tab.Host.Length == 0 || tab.IsNewTab)
            return;
        double zoom = Math.Round(tab.View.ZoomFactor, 2);
        if (Math.Abs(zoom - 1) < 0.001)
            Settings.Zoom.Remove(tab.Host);
        else
            Settings.Zoom[tab.Host] = zoom;
        if (ReferenceEquals(tab, _active))
            RefreshZoom();
        ScheduleSave();
    }

    private void RefreshZoom()
    {
        double zoom = _active?.View?.ZoomFactor ?? 1;
        bool show = _active is { IsNewTab: false } && Math.Abs(zoom - 1) > 0.001;
        ZoomPill.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";
    }

    private void Zoom(int direction)
    {
        if (_active?.View is not { } view)
            return;
        double current = view.ZoomFactor;
        double next = direction == 0 ? 1
            : direction > 0 ? ZoomSteps.FirstOrDefault(step => step > current + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(step => step < current - 0.001, ZoomSteps[0]);
        view.ZoomFactor = next;
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Zoom(0);

    // ---- Menus ----

    private bool _menuOpen;

    private ContextMenu NewMenu(UIElement target)
    {
        ContextMenu menu = new() { PlacementTarget = target, Placement = PlacementMode.Bottom, MinWidth = 230 };
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (Settings.ZenMode)
                _zenTimer?.Start();
        };
        return menu;
    }

    private static MenuItem Item(string header, string? glyph, string? gesture, Action action, bool enabled = true)
    {
        MenuItem item = new() { Header = header, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
        if (glyph != null)
            item.Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 };
        item.Click += (_, _) => action();
        return item;
    }

    private MenuItem Toggle(string header, bool value, Action<bool> set, string? gesture = null)
    {
        MenuItem item = new() { Header = header, IsCheckable = true, IsChecked = value, InputGestureText = gesture ?? string.Empty };
        item.Click += (_, _) =>
        {
            set(item.IsChecked);
            ScheduleSave();
        };
        return item;
    }

    private MenuItem Choice(string header, bool selected, Action choose)
    {
        MenuItem item = new() { Header = header, IsCheckable = true, IsChecked = selected };
        item.Click += (_, _) =>
        {
            choose();
            ScheduleSave();
        };
        return item;
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        BrowserTab? tab = _active;
        bool web = tab is { IsNewTab: false };
        ContextMenu menu = NewMenu(MenuButton);
        // Lines up with the right edge of the button, so it never hangs off the panel
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) => new[]
        {
            new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height + 4), PopupPrimaryAxis.Horizontal)
        };

        menu.Items.Add(Item("New tab", "\uE710", "Ctrl+T", NewTab, _tabs.Count < MaxTabs));
        MenuItem closed = new() { Header = "Recently closed", Icon = Glyph("\uE81C"), IsEnabled = _state.Closed.Count > 0 };
        foreach (SavedTab saved in _state.Closed.Take(10).ToList())
            closed.Items.Add(Item(string.IsNullOrWhiteSpace(saved.Title) ? HostOf(saved.Url) : saved.Title, null, null, () => ReopenTab(saved)));
        if (_state.Closed.Count > 0)
        {
            closed.Items.Add(new Separator());
            closed.Items.Add(Item("Reopen last closed tab", "\uE7A7", "Ctrl+Shift+T", ReopenClosedTab));
        }
        menu.Items.Add(closed);
        menu.Items.Add(new Separator());

        // Zoom: minus, the level, plus, all in one row that stays open while you click
        MenuItem zoom = new() { StaysOpenOnClick = true, IsEnabled = web, Icon = Glyph("\uE71E") };
        StackPanel zoomRow = new() { Orientation = Orientation.Horizontal };
        TextBlock level = new() { Width = 52, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Text = $"{Math.Round((tab?.View?.ZoomFactor ?? 1) * 100)}%" };
        zoomRow.Children.Add(new TextBlock { Text = "Zoom", Width = 96, VerticalAlignment = VerticalAlignment.Center });
        zoomRow.Children.Add(ZoomButton("\uE738", "Zoom out (Ctrl+-)", -1, level));
        zoomRow.Children.Add(level);
        zoomRow.Children.Add(ZoomButton("\uE710", "Zoom in (Ctrl+=)", 1, level));
        zoom.Header = zoomRow;
        menu.Items.Add(zoom);
        menu.Items.Add(new Separator());

        MenuItem layout = new() { Header = "Layout", Icon = Glyph("\uE8A9") };
        layout.Items.Add(Choice("Tabs on top", Settings.Layout == BrowserLayout.Horizontal, () => SetLayout(BrowserLayout.Horizontal)));
        layout.Items.Add(Choice("Compact, one row", Settings.Layout == BrowserLayout.Compact, () => SetLayout(BrowserLayout.Compact)));
        layout.Items.Add(Choice("Tabs on the side", Settings.Layout == BrowserLayout.Vertical, () => SetLayout(BrowserLayout.Vertical)));
        layout.Items.Add(new Separator());
        layout.Items.Add(Toggle("Zen mode", Settings.ZenMode, _ => ToggleZen(), "F11"));
        layout.Items.Add(Toggle("Show only the site in the address bar", Settings.MinimalAddress, value =>
        {
            Settings.MinimalAddress = value;
            ShowAddress();
        }));
        layout.Items.Add(Toggle("Loading line", Settings.ProgressBar, value =>
        {
            Settings.ProgressBar = value;
            if (!value)
                HideProgress();
        }));
        menu.Items.Add(layout);

        MenuItem search = new() { Header = "Search engine", Icon = Glyph("\uE721") };
        foreach (SearchEngine engine in BrowserSearch.Engines)
            search.Items.Add(Choice(engine.Name, Settings.Engine == engine.Key, () =>
            {
                Settings.Engine = engine.Key;
                ShowToast("\uE721", $"Searching with {engine.Name}");
            }));
        search.Items.Add(new Separator());
        search.Items.Add(Toggle("Suggestions while typing", Settings.Suggestions, value => Settings.Suggestions = value));
        menu.Items.Add(search);

        MenuItem privacy = new() { Header = "Privacy and speed", Icon = Glyph("\uEA18") };
        privacy.Items.Add(Toggle("Block ads and trackers", Settings.BlockAds, value =>
        {
            Settings.BlockAds = value;
            RefreshShield();
        }));
        privacy.Items.Add(Toggle("Memory saver, tabs unused for 10 minutes sleep", Settings.MemorySaver, value => Settings.MemorySaver = value));
        privacy.Items.Add(Toggle("Open new tabs next to the current one", Settings.OpenNextToCurrent, value => Settings.OpenNextToCurrent = value));
        if (Settings.AllowedSites.Count > 0)
            privacy.Items.Add(Item($"Block ads everywhere again ({Settings.AllowedSites.Count} allowed)", null, null, () =>
            {
                Settings.AllowedSites.Clear();
                RefreshShield();
                ScheduleSave();
            }));
        privacy.Items.Add(new Separator());
        privacy.Items.Add(Item("Clear history and cached files", "\uE74D", null, () => ClearData(CoreWebView2BrowsingDataKinds.BrowsingHistory
            | CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.DownloadHistory, true, "History and cached files cleared")));
        privacy.Items.Add(Item("Clear cookies and site data", "\uE74D", null, () => ClearData(CoreWebView2BrowsingDataKinds.AllSite,
            false, "Cookies cleared, you are signed out of sites")));
        privacy.Items.Add(Item("Clear everything", "\uE74D", null, () => ClearData(CoreWebView2BrowsingDataKinds.AllProfile, true, "Browsing data cleared")));
        menu.Items.Add(privacy);
        menu.Items.Add(new Separator());

        menu.Items.Add(Item(IsSplitShown ? "Close split view" : "Split view", "\uE8A0", "Ctrl+Shift+S", ToggleSplit));
        menu.Items.Add(Item("Search tabs", "\uE70D", "Ctrl+Shift+A", OpenTabSearch));
        menu.Items.Add(Item("Find on page", "\uE721", "Ctrl+F", OpenFind, web));
        menu.Items.Add(Item("Copy link", "\uE71B", "Ctrl+Shift+C", () => { if (tab != null) CopyLink(tab); }, web));
        menu.Items.Add(Item("Open in your browser", "\uE8A7", null, () => { if (tab != null) OpenOutside(tab); }, web));
        menu.IsOpen = true;
    }

    private static TextBlock Glyph(string glyph) => new() { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14 };

    private Button ZoomButton(string glyph, string tip, int direction, TextBlock level)
    {
        Button button = new()
        {
            Style = (Style)FindResource("NavButton"),
            Width = 28,
            Height = 28,
            ToolTip = tip,
            Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Glyph"), FontSize = 12 }
        };
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Zoom(direction);
            level.Text = $"{Math.Round((_active?.View?.ZoomFactor ?? 1) * 100)}%";
        };
        return button;
    }

    private async void ClearData(CoreWebView2BrowsingDataKinds kinds, bool history, string done)
    {
        try
        {
            if (history)
            {
                _state.History.Clear();
                _state.Closed.Clear();
                _icons.Clear();
            }
            CoreWebView2? core = _tabs.Select(tab => tab.Core).FirstOrDefault(found => found != null);
            if (core != null)
                await core.Profile.ClearBrowsingDataAsync(kinds);
            ShowToast("\uE74D", done);
            RefreshNewTabPage();
            ScheduleSave();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "Browsing data could not be cleared: " + ex.Message);
        }
    }

    // ---- Keyboard ----

    private void BrowserRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        bool control = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int index = _active == null ? -1 : _tabs.IndexOf(_active);
        bool inAddress = AddressBox.IsKeyboardFocused || TabSearchBox.IsKeyboardFocused || FindBox.IsKeyboardFocused;

        if (key == Key.F11)
            ToggleZen();
        else if (key == Key.F6 || (alt && !control && key == Key.D) || (control && !shift && key == Key.L))
            FocusAddress();
        else if (key == Key.F5 || (control && key == Key.R))
            Reload();
        else if (alt && !control && key == Key.Left && !inAddress)
            GoBack();
        else if (alt && !control && key == Key.Right && !inAddress)
            GoForward();
        else if (!control)
            return;
        else if (shift && key == Key.T)
            ReopenClosedTab();
        else if (!shift && key == Key.T)
            NewTab();
        else if (key is Key.W or Key.F4 && !shift)
        {
            if (_active != null)
                CloseTab(_active);
        }
        else if (!shift && key == Key.F)
            OpenFind();
        else if (shift && key == Key.C)
        {
            if (_active != null)
                CopyLink(_active);
        }
        else if (shift && key == Key.A)
            OpenTabSearch();
        else if (shift && key == Key.S)
            ToggleSplit();
        else if ((key == Key.Tab || key == Key.PageDown || key == Key.PageUp) && _tabs.Count > 1)
        {
            bool back = key == Key.PageUp || (key == Key.Tab && shift);
            SwitchTo(_tabs[(index + (back ? _tabs.Count - 1 : 1)) % _tabs.Count]);
        }
        else if (key is >= Key.D1 and <= Key.D9 && !shift)
        {
            int wanted = key == Key.D9 ? _tabs.Count - 1 : key - Key.D1;
            if (wanted >= 0 && wanted < _tabs.Count)
                SwitchTo(_tabs[wanted]);
        }
        else if (inAddress && key is Key.OemPlus or Key.Add)
            Zoom(1);
        else if (inAddress && key is Key.OemMinus or Key.Subtract)
            Zoom(-1);
        else if (inAddress && key is Key.D0 or Key.NumPad0)
            Zoom(0);
        else
            return;
        e.Handled = true;
    }

    // ---- The loading line ----

    private void RestartProgressFor(BrowserTab tab)
    {
        if (tab.IsLoading)
            StartProgress();
        else
            HideProgress();
    }

    // It runs ahead quickly, then creeps on while the page loads, and races to the end and fades once it is done
    private void StartProgress()
    {
        if (!Settings.ProgressBar)
            return;
        ProgressFill.BeginAnimation(OpacityProperty, null);
        ProgressFill.Opacity = 1;
        DoubleAnimationUsingKeyFrames run = new() { FillBehavior = FillBehavior.HoldEnd };
        run.KeyFrames.Add(new LinearDoubleKeyFrame(0.04, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        run.KeyFrames.Add(new EasingDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(350)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        run.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(9)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, run);
    }

    private void FinishProgress()
    {
        if (!Settings.ProgressBar || ProgressFill.Opacity == 0)
            return;
        DoubleAnimation finish = new(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, finish);
        DoubleAnimation fade = new(0, TimeSpan.FromMilliseconds(320)) { BeginTime = TimeSpan.FromMilliseconds(180) };
        fade.Completed += (_, _) =>
        {
            if (_active?.IsLoading == true)
                return;
            ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ProgressScale.ScaleX = 0;
        };
        ProgressFill.BeginAnimation(OpacityProperty, fade);
    }

    private void HideProgress()
    {
        ProgressFill.BeginAnimation(OpacityProperty, null);
        ProgressScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ProgressFill.Opacity = 0;
        ProgressScale.ScaleX = 0;
    }

    // ---- Status pill and toasts ----

    // Where a hovered link goes, in the bottom corner
    private void ShowStatus(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _panelHidden)
        {
            StatusPopup.IsOpen = false;
            return;
        }
        StatusText.Text = text;
        StatusPopup.HorizontalOffset = 6;
        StatusPopup.VerticalOffset = Math.Max(0, ViewHost.ActualHeight - 38);
        StatusPopup.IsOpen = true;
    }

    private void ShowToast(string glyph, string text)
    {
        if (_panelHidden || _closed)
            return;
        ToastGlyph.Text = glyph;
        ToastText.Text = text;
        if (ToastPopup.Child is FrameworkElement card)
        {
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            ToastPopup.HorizontalOffset = Math.Max(0, (ViewHost.ActualWidth - card.DesiredSize.Width) / 2);
            ToastPopup.VerticalOffset = Math.Max(0, ViewHost.ActualHeight - card.DesiredSize.Height - 18);
        }
        ToastPopup.IsOpen = true;
        _toastTimer?.Stop();
        _toastTimer?.Start();
    }

    // ---- New tab page ----

    private void RefreshNewTabPage()
    {
        if (NewTabPage.Visibility != Visibility.Visible)
            return;
        int hour = DateTime.Now.Hour;
        NewTabGreeting.Text = hour < 5 ? "Up late" : hour < 12 ? "Good morning" : hour < 18 ? "Good afternoon" : "Good evening";
        NewTabClock.Text = DateTime.Now.ToString("t");
        NewTabSearchText.Text = $"Search {BrowserSearch.Engine(Settings.Engine).Name} or type an address";
        List<NewTabTile> tiles = _state.TopSites(8).Select(entry => Tile(entry.Url, entry.Title)).ToList();
        // Starter sites fill the page until your own favourites take their place
        foreach ((string name, string url) in StarterSites)
        {
            if (tiles.Count >= 8)
                break;
            string host = new Uri(url).Host;
            if (Settings.HiddenSites.Contains(host) || tiles.Any(tile => tile.Host.Equals(host, StringComparison.OrdinalIgnoreCase)))
                continue;
            tiles.Add(Tile(url, name));
        }
        NewTabTiles.ItemsSource = tiles;
    }

    private NewTabTile Tile(string url, string title)
    {
        string host = HostOf(url);
        string bare = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        string name = title.Length is > 0 and <= 18 ? title : bare.Split('.')[0];
        name = name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : bare;
        int hash = 0;
        foreach (char c in bare)
            hash = unchecked(hash * 31 + c);
        _icons.TryGetValue(host, out ImageSource? icon);
        return new NewTabTile(url, host, name, bare.Length > 0 ? char.ToUpperInvariant(bare[0]).ToString() : "?",
            new SolidColorBrush(TileColors[Math.Abs(hash % TileColors.Length)]), icon);
    }

    private void UpdateNewTabClock()
    {
        if (_clockTimer == null)
            return;
        if (NewTabPage.Visibility == Visibility.Visible && !_panelHidden && !_closed)
            _clockTimer.Start();
        else
            _clockTimer.Stop();
    }

    private void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NewTabTile tile })
            Navigate(tile.Url);
    }

    private void Tile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NewTabTile tile } element)
            return;
        e.Handled = true;
        ContextMenu menu = NewMenu(element);
        menu.Items.Add(Item("Open", "\uE8A7", null, () => Navigate(tile.Url)));
        menu.Items.Add(Item("Open in new tab", "\uE710", null, () => OpenInNewTab(tile.Url), _tabs.Count < MaxTabs));
        menu.Items.Add(Item("Remove", "\uE711", null, () =>
        {
            Settings.HiddenSites.Add(tile.Host);
            RefreshNewTabPage();
            ScheduleSave();
        }));
        menu.IsOpen = true;
    }

    private void NewTabSearch_Click(object sender, MouseButtonEventArgs e) => FocusAddress();
}

// A site on the new tab page: a square monogram in its own colour, or its icon once one is known
public sealed record NewTabTile(string Url, string Host, string Name, string Letter, Brush Tint, ImageSource? Icon)
{
    public bool HasIcon => Icon != null;
}
