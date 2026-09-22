using System;
using Voidstrap.Utility;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using DiscordRPC;
using DiscordRPC.Logging;
using DiscordRPC.Message;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Models.Persistable;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.UI;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;
using WpfAnimatedGif;

namespace Voidstrap.UI.Elements.Settings;

public partial class MainWindow : WpfUiWindow, INavigationWindow
{
    public sealed record SidebarItemDefinition(string Key, string DefaultName, string Section, SymbolRegular DefaultIcon, bool CanHide = true);

    public static IReadOnlyList<SidebarItemDefinition> SidebarCustomizationItems { get; } = new SidebarItemDefinition[]
    {
        new("HomeNavItem", "Home", "Core", SymbolRegular.Home24, false),
        new("IntegrationsNavItem", "Integrations", "Core", SymbolRegular.Add12),
        new("DeploymentNavItem", "Deployment", "Core", SymbolRegular.PlaySettings20),
        new("AppearanceNavItem", "Appearance", "Core", SymbolRegular.Color24, false),
        new("FastFlagSettingsNavItem", "FastFlag Settings", "Configuration", SymbolRegular.Settings28),
        new("FastFlagEditorNavItem", "FastFlag Editor", "Configuration", SymbolRegular.Flag28),
        new("ModsNavItem", "Mods", "Configuration", SymbolRegular.WrenchScrewdriver20),
        new("MoreNavItem", "More", "Configuration", SymbolRegular.MoreHorizontal24),
        new("GlobalNavItem", "Global", "More", SymbolRegular.GlobeLocation20),
        new("ShortcutsNavItem", "Shortcuts", "More", SymbolRegular.Apps32),
        new("NewsNavItem", "News", "More", SymbolRegular.News16),
        new("ExtensionsNavItem", "Extensions", "Footer", SymbolRegular.CubeAdd20),
        new("ManagerNavItem", "Manager", "Footer", SymbolRegular.ArrowDownload24),
        new("SettingsNavItem", "Settings", "Footer", SymbolRegular.Settings28),
        new("SoberNavItem", "Sober", "Footer", SymbolRegular.Empty),
        new("AboutNavItem", "About", "Footer", SymbolRegular.QuestionCircle32)
    };

    public static bool IsSidebarItemAvailable(string key)
    {
        return Voidstrap.Utility.Platform.IsLinux
            ? key is not "ExtensionsNavItem" and not "ManagerNavItem"
            : key != "SoberNavItem";
    }

    public static string NormalizeSidebarName(string? value, string fallback)
    {
        string normalized = Regex.Replace(value ?? "", "\\s+", " ").Trim();
        if (normalized.Length == 0)
        {
            return fallback;
        }
        return normalized.Length > 48 ? normalized[..48] : normalized;
    }

    [Conditional("DEBUG")]
    private static void VerifySidebarCustomization()
    {
        Debug.Assert(SidebarCustomizationItems.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() == SidebarCustomizationItems.Count);
        Debug.Assert(SidebarCustomizationItems.All(item => item.Section is "Core" or "Configuration" or "More" or "Footer"));
        Debug.Assert(SidebarCustomizationItems.Where(item => item.Key is "HomeNavItem" or "AppearanceNavItem").All(item => !item.CanHide));
        Debug.Assert(NormalizeSidebarName("  My\nPage  ", "Page") == "My Page");
    }

    private sealed partial class PageSearchTarget
    {
        public FrameworkElement Element { get; init; } = null!;

        public string Text { get; init; } = null!;
    }


    private sealed partial class TopSearchEntry
    {
		public string Id { get; }

        public string DisplayText { get; }

        public string SearchText { get; }

        public Type PageType { get; }

        public string? TargetText { get; }

        public IReadOnlyList<string> TargetTerms { get; }

		public List<string> ContainerTerms { get; }

		public string NormalizedSearchText { get; }

		public string NormalizedTargetText { get; }

		public bool HiddenByDefault { get; }

        public TopSearchEntry(string id, string displayText, string searchText, Type pageType, string? targetText = null, IEnumerable<string>? targetTerms = null, IEnumerable<string>? containerTerms = null, bool hiddenByDefault = false)
        {
			Id = id;
			HiddenByDefault = hiddenByDefault;
            DisplayText = displayText;
            SearchText = searchText;
            PageType = pageType;
            TargetText = targetText;
            TargetTerms = (targetTerms ?? Enumerable.Empty<string>()).Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			ContainerTerms = (containerTerms ?? Enumerable.Empty<string>()).Where(term => !string.IsNullOrWhiteSpace(term)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
			NormalizedSearchText = NormalizeSearchText(searchText);
			NormalizedTargetText = NormalizeSearchText(targetText ?? string.Empty);
        }
    }

    private sealed partial class TopBarNotificationItem
    {
        private static readonly Brush CrashBrush = FreezeBrush(Color.FromRgb(255, 77, 79));

        private static readonly Brush ErrorBrush = FreezeBrush(Color.FromRgb(245, 166, 35));

        private static readonly Brush InfoBrush = FreezeBrush(Color.FromRgb(77, 163, 255));

        public string Id { get; init; } = "";

        public string Title { get; init; } = "";

        public string Text { get; init; } = "";

        public string TimeAgo { get; init; } = "";

        public string LogPath { get; init; } = "";

        public bool IsUnread { get; init; }

        public SymbolRegular Icon { get; init; } = SymbolRegular.Info24;

        public Brush IconBrush { get; init; } = InfoBrush;

        public Visibility UnreadVisibility => IsUnread ? Visibility.Visible : Visibility.Collapsed;

        private static Brush FreezeBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        public static TopBarNotificationItem From(Voidstrap.Utility.AppNotification source)
        {
            (SymbolRegular icon, Brush brush) = source.Kind switch
            {
                Voidstrap.Utility.AppNotifications.KindCrash => (SymbolRegular.ErrorCircle24, CrashBrush),
                Voidstrap.Utility.AppNotifications.KindError => (SymbolRegular.Warning24, ErrorBrush),
                _ => (SymbolRegular.Info24, InfoBrush)
            };
            string text = source.Text.Trim();
            if (source.Count > 1)
            {
                text += (text.Length > 0 ? Environment.NewLine : "") + "Happened " + source.Count.ToString("N0") + " times";
            }
            return new TopBarNotificationItem
            {
                Id = source.Id,
                Title = source.Title,
                Text = text,
                TimeAgo = FormatAge(source.LastSeen),
                LogPath = source.LogPath,
                IsUnread = !source.Read,
                Icon = icon,
                IconBrush = brush
            };
        }

        private static string FormatAge(long timestamp)
        {
            if (timestamp <= 0)
            {
                return "";
            }
            DateTimeOffset when = timestamp > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp) : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            TimeSpan age = DateTimeOffset.UtcNow - when;
            if (age.TotalMinutes < 1)
            {
                return "Just now";
            }
            if (age.TotalHours < 1)
            {
                return $"{(int)age.TotalMinutes} minutes ago";
            }
            if (age.TotalDays < 1)
            {
                return (int)age.TotalHours == 1 ? "1 hour ago" : $"{(int)age.TotalHours} hours ago";
            }
            if (age.TotalDays < 30)
            {
                return (int)age.TotalDays == 1 ? "Yesterday" : $"{(int)age.TotalDays} days ago";
            }
            return when.LocalDateTime.ToString("MMM d, yyyy");
        }
    }

    private sealed partial class CommandPaletteRow : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Title { get; init; } = "";

        public string Detail { get; init; } = "";

        public SymbolRegular Icon { get; init; } = SymbolRegular.Document24;

        public Action? Open { get; init; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private bool _isSaveAndLaunchClicked;

    private bool _isClosed;

	private bool _restartNotificationBusy;

    private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

    private readonly DispatcherTimer _visibilityTimer = new DispatcherTimer();

    private double _appliedUiZoom = double.NaN;

    private bool _uiZoomQueued;

    private bool _bgGifPausedByDeactivate;

    private DiscordRpcClient? _discordClient;

    private bool _discordReady;

    private DateTime _discordRetryAtUtc;

    private bool _discordRpcEnabled = App.Settings.Prop.VoidRPC;

    private readonly DateTime _voidRpcSessionStart = DateTime.UtcNow;

    private DateTime _lastVoidRpcUpdate = DateTime.MinValue;

    private bool _voidRpcSuppressed;

    private string? _lastVoidRpcDetails;

    private string? _lastVoidRpcState;

    private string? _lastVoidRpcExtra;

    private DateTime _lastRobloxCheck = DateTime.MinValue;

    private volatile bool _robloxRunningCached;

    private int _robloxCheckRunning;


    private static readonly Dictionary<string, (string Details, string State)> _voidRpcPageDescriptions = new Dictionary<string, (string, string)>
    {
        ["HomePage"] = ("Home", "On the Voidstrap home screen"),
        ["GamePage"] = ("Game Details", "Looking at a game"),
        ["MobilePage"] = ("Mobile", "Roblox on mobile setup"),
        ["MobilePageExplain"] = ("Mobile", "Reading the mobile guide"),
        ["NvidaEditor"] = ("NVIDIA Editor", "GPU specific tweaks"),
        ["HistoryPage"] = ("Continue Playing", "Browsing recent games"),
        ["IntegrationsPage"] = ("Integrations", "Advanced integrations"),
        ["BehaviourPage"] = ("Deployment", "Channels, cleaner, matchmaker"),
        ["FastFlagsPage"] = ("FastFlag Settings", "Tweaking flag presets"),
        ["FastFlagEditorPage"] = ("FastFlag Editor", "Editing fast flags"),
        ["FastFlagEditorWarningPage"] = ("FastFlag Editor", "Reading the warning"),
        ["GBSEditorPage"] = ("Global Settings", "Editing GBS config"),
        ["ModsPage"] = ("Mods", "Cursors, sounds, overlays, skyboxes"),
        ["NewsPage"] = ("News", "What's new in Voidstrap"),
        ["DownloadsPage"] = ("Downloads", "Managing Roblox installs"),
        ["ExtensionPage"] = ("Extensions", "Plugins & integrations"),
        ["ShortcutsPage"] = ("Shortcuts", "Game launch shortcuts"),
        ["ChannelPage"] = ("Settings", "App settings & updates"),
        ["ReleasesPage"] = ("Releases", "Voidstrap release history"),
        ["DonoPage"] = ("Support Voidstrap", "Considering a donation"),
        ["HelpPage"] = ("Help", "Reading the help guides"),
        ["AppearancePage"] = ("Appearance", "Themes and backgrounds"),
        ["BootstrapperPage"] = ("Bootstrapper", "Launch window settings"),
        ["LibraryPage"] = ("Library", "Browsing the game library"),
        ["SoberPage"] = ("Sober", "Roblox on Linux settings"),
        ["Releases"] = ("Releases", "Voidstrap release history"),
        ["ServerBrowserPage"] = ("Server Browser", "Looking for a server"),
        ["NvidiaFastFlagsPage"] = ("NVIDIA FFlags", "GPU specific tweaks")
    };

    private AppearanceViewModel.BackgroundSettings _backgroundSettings;

    private int _notificationUnread = -1;

    private int _notificationReloadTicks;

    private string? _currentBackgroundPath;

    private DateTime _currentBackgroundWriteTimeUtc;

    private int _backgroundGeneration;

    private readonly Dictionary<UIElement, TaskCompletionSource<bool>> _backgroundAnimationWaiters = new Dictionary<UIElement, TaskCompletionSource<bool>>();


    private Vector _currentOffset;

    private Vector _targetOffset;

    private double _currentRotation;

    private double _targetRotation;

    private DispatcherTimer _searchDebounceTimer = null!;

    private readonly List<PageSearchTarget> _pageSearchTargets = new List<PageSearchTarget>();

    private readonly List<TopSearchEntry> _topSearchEntriesList = new List<TopSearchEntry>();

    private readonly Dictionary<string, TopSearchEntry> _topSearchEntries = new Dictionary<string, TopSearchEntry>(StringComparer.OrdinalIgnoreCase);

    private readonly ObservableCollection<CommandPaletteRow> _commandPaletteRows = new ObservableCollection<CommandPaletteRow>();

    private int _commandPaletteSelected = -1;

    private readonly Dictionary<string, (WeakReference<FrameworkElement> Element, bool Visible)> _searchTargetStates = new Dictionary<string, (WeakReference<FrameworkElement>, bool)>(StringComparer.Ordinal);

    private TopSearchEntry? _pendingTopSearchEntry;

	private int _topSearchNavigationGeneration;

    private bool _navigationInitialized;

    private INavigationControl[]? _defaultMainSidebarItems;

    private INavigationControl[]? _defaultFooterSidebarItems;

    private Dictionary<string, object?>? _defaultSidebarContent;

    private Page? _lastPage;

    private const double MaxOffset = 0.04;

    private const double MaxRotation = 5.0;

    private const double FollowSpeed = 0.035;

    private readonly Dictionary<NavigationItem, SymbolRegular> _defaultIcons = new Dictionary<NavigationItem, SymbolRegular>();

    private Dictionary<string, SymbolRegular>? _defaultSidebarIcons;

    private Dictionary<string, BitmapSource?>? _defaultSidebarImages;

    private readonly List<Type> _pagesToHideSearchBox = new List<Type>
    {
        typeof(HomePage),
        typeof(FastFlagEditorPage),
        typeof(NewsPage),
        typeof(DownloadsPage),
        typeof(NvidiaFFlagEditorPage),
        typeof(ReleasesPage),
        typeof(DonoPage),
        typeof(LibraryPage)
    };

    private LibraryPage? _libraryPage;

    private Pages.RobloxNewsPage? _robloxNewsPage;

    private static readonly int ProcessorCount = Environment.ProcessorCount;

    private sealed partial class NavigationHistoryEntry
    {
        public Type PageType { get; }

        public WeakReference<object> Content { get; }

        public NavigationHistoryEntry(object content)
        {
            PageType = content.GetType();
            Content = new WeakReference<object>(content);
        }
    }

    private const int MaxNavigationHistoryEntries = 16;

    private readonly List<NavigationHistoryEntry> _navHistoryBack = new List<NavigationHistoryEntry>();

    private readonly List<NavigationHistoryEntry> _navHistoryForward = new List<NavigationHistoryEntry>();

    private object? _navHistoryCurrent;

    private bool _navHistorySuppressPush;

    private double _gradientLayerOpacity;

    private bool _introPlayed;

    private bool _introFinished;

    private DispatcherTimer? _introCacheTimer;

    private DispatcherTimer? _introWatchdog;

    private Storyboard? _introWatchdogStoryboard;

    private static readonly TimeSpan IntroDuration = TimeSpan.FromMilliseconds(750.0);

    private static Voidstrap.Models.Persistable.WindowState _state => App.State.Prop.SettingsWindow;

    public double GradientLayerOpacity
    {
        get
        {
            return _gradientLayerOpacity;
        }
        set
        {
            if (_gradientLayerOpacity != value)
            {
                _gradientLayerOpacity = value;
                if (GradientLayer != null)
                {
                    GradientLayer.BeginAnimation(UIElement.OpacityProperty, null);
                    GradientLayer.Opacity = Math.Clamp(_gradientLayerOpacity, 0.0, 1.0);
                }
            }
        }
    }

    public new void ApplyTheme()
    {
        base.ApplyTheme();
        Voidstrap.UI.WindowBackdrop.ApplyMainWindow(this);
        Resources["LauncherMenuBrush"] = Voidstrap.UI.WindowBackdrop.CreateOpaqueSurfaceBrush(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Voidstrap.UI.WindowBackdrop.ApplyMainWindow(this);
    }

    private void ApplyThemeBackground()
    {
        Resources["LauncherMenuBrush"] = Voidstrap.UI.WindowBackdrop.CreateOpaqueSurfaceBrush(this);
        if (GradientLayer == null)
        {
            return;
        }
        Brush surface = Voidstrap.UI.WindowBackdrop.CreateSurfaceBrush(this);
        if (BackgroundGradientTransform != null && !Voidstrap.Utility.Platform.IsLinux)
        {
            if (surface.IsFrozen)
            {
                surface = surface.Clone();
            }
            surface.RelativeTransform = BackgroundGradientTransform;
        }
        GradientLayer.Background = surface;
    }

    public void ApplyBackdropSurface()
    {
        ApplyThemeBackground();
    }

    private MediaElement? BackgroundMedia;
    private Voidstrap.UI.Elements.Controls.HomepageMediaPreviewVideo? BackgroundPortableMedia;

    private void CreateBackgroundMedia()
    {
        if (BackgroundLayer == null)
        {
            return;
        }
        try
        {
            if (Voidstrap.Utility.Platform.IsLinux)
            {
                Voidstrap.UI.Elements.Controls.HomepageMediaPreviewVideo portable = new()
                {
                    Name = "BackgroundPortableMedia",
                    Opacity = 1.0,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                BackgroundLayer.Children.Insert(0, portable);
                BackgroundPortableMedia = portable;
                return;
            }

            MediaElement media = new MediaElement
            {
                Name = "BackgroundMedia",
                Stretch = Stretch.UniformToFill,
                Opacity = 1.0,
                IsHitTestVisible = false,
                LoadedBehavior = MediaState.Manual,
                UnloadedBehavior = MediaState.Stop,
                Visibility = Visibility.Collapsed
            };
            BackgroundLayer.Children.Insert(0, media);
            BackgroundMedia = media;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("MainWindow::CreateBackgroundMedia", "Video background unavailable: " + ex.Message);
        }
    }

    public MainWindow(bool showAlreadyRunningWarning)
    {
        //IL_0017: Unknown result type (might be due to invalid IL or missing references)
        //IL_0021: Expected O, but got Unknown
        //IL_00d0: Unknown result type (might be due to invalid IL or missing references)
        //IL_00d5: Unknown result type (might be due to invalid IL or missing references)
        //IL_0186: Unknown result type (might be due to invalid IL or missing references)
        //IL_018b: Unknown result type (might be due to invalid IL or missing references)
        //IL_019e: Expected O, but got Unknown
        //IL_01c7: Unknown result type (might be due to invalid IL or missing references)
        //IL_01cc: Unknown result type (might be due to invalid IL or missing references)
        //IL_01de: Expected O, but got Unknown
        InitializeComponent();
        CommandPaletteResultsList.ItemsSource = _commandPaletteRows;
        PrepareLinuxRestartNotificationInput();
        SoberNavItem.Visibility = Voidstrap.Utility.Platform.IsLinux ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsNavItem.Visibility = Voidstrap.Utility.Platform.IsLinux ? Visibility.Collapsed : Visibility.Visible;
        ShortcutsNavItem.Visibility = Visibility.Visible;
        ManagerNavItem.Visibility = Voidstrap.Utility.Platform.IsLinux ? Visibility.Collapsed : Visibility.Visible;
        VerifySidebarCustomization();
        ApplySidebarCustomization();
        SettingChangeNotifier.Failed += OnSettingChangeFailed;
        RestartNotificationService.Changed += OnRestartRequirementsChanged;
        if (Voidstrap.Utility.Platform.IsLinux)
            CreateBackgroundMedia();
        AllowsTransparency = false;
        ApplyThemeBackground();
        InitializeViewModel();
        InitializeWindowState();
        UpdateButtonContent();
        InitializeDiscordRPC();
        RegisterHoverIcons();
        _backgroundSettings = AppearanceViewModel.LoadSettings();
        GlobalBackground.Changed += OnGlobalBackgroundChanged;
        ApplyBackgroundSettings();
        PopulateTopSearch();
        _ = LoadCatalogOptionsAsync();
        _visibilityTimer.Interval = TimeSpan.FromSeconds(0.8);
        _visibilityTimer.Tick += VisibilityTimer_Tick;
        _visibilityTimer.Start();
        base.SizeChanged += MainWindow_SizeChanged;
        base.LocationChanged += MainWindow_LocationChanged;
        base.StateChanged += MainWindow_StateChanged;
        RootFrame.Navigating += RootFrame_Navigating;
        RootFrame.Navigated += RootFrame_Navigated;
        Wpf.Ui.Controls.Navigation.NavigationTiming.PageCreated += OnNavigationPageCreated;
        CommandPalettePopup.Closed += OverlayPopup_Closed;
        AppMenuPopup.Closing += AppMenuPopup_Closing;
        AppMenuPopup.Closed += OverlayPopup_Closed;
        App.Logger.WriteLine("MainWindow", "Initializing settings window");
        if (showAlreadyRunningWarning)
        {
            _ = ShowAlreadyRunningSnackbarAsync();
        }
        RefreshRestartNotification();
    }

    private void VisibilityTimer_Tick(object? sender, EventArgs e)
    {
        UpdateDiscordPresence();
        if (++_notificationReloadTicks >= 12)
        {
            _notificationReloadTicks = 0;
            Voidstrap.Utility.AppNotifications.Reload();
        }
    }

    private void RegisterHoverIcons()
    {
        foreach (NavigationItem item in _defaultIcons.Keys.ToArray())
        {
            item.MouseEnter -= NavigationItem_MouseEnter;
            item.MouseLeave -= NavigationItem_MouseLeave;
        }
        _defaultIcons.Clear();
        Dictionary<string, string> customIcons = App.Settings.Prop.SidebarIcons ??= new Dictionary<string, string>();
        foreach (NavigationItem item in from i in RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>())
                                        where i.Tag != null && i.Image == null && !customIcons.ContainsKey(i.Name)
                                        select i)
        {
            SymbolRegular defaultIcon = item.Icon;
            _defaultIcons[item] = defaultIcon;
            item.MouseEnter += NavigationItem_MouseEnter;
            item.MouseLeave += NavigationItem_MouseLeave;
        }
    }

    private void NavigationItem_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is NavigationItem item && Enum.TryParse<SymbolRegular>(item.Tag?.ToString(), out var result))
        {
            item.Icon = result;
        }
    }

    private void NavigationItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is NavigationItem item && _defaultIcons.TryGetValue(item, out SymbolRegular defaultIcon))
        {
            item.Icon = defaultIcon;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null)
        {
            yield break;
        }
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            T? val = (T?)(object?)((child is T) ? child : null);
            if (val != null)
            {
                yield return val;
            }
            foreach (T item in FindVisualChildren<T>(child))
            {
                yield return item;
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            T? val = (T?)(object?)((current is T) ? current : null);
            if (val != null)
            {
                return val;
            }
            bool flag = ((current is Visual || current is Visual3D) ? true : false);
            current = (flag ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current));
        }
        return default(T);
    }

    private long _navigationStartedTicks;

    private double _navigationCreateMs;

    private void OnNavigationPageCreated(Type pageType, double milliseconds)
    {
        _navigationStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _navigationCreateMs = milliseconds;
    }

    private FrameworkElement? _discardedPage;

    private readonly object _downloadProgressSync = new();

    private string _pendingDownloadTitle = "";

    private double _pendingDownloadFraction;

    private bool _pendingDownloadShow;

    private bool _downloadProgressScheduled;

    private void OnExtensionProgressChanged(string title, double fraction, bool show)
    {
        if (_isClosed)
        {
            return;
        }
        lock (_downloadProgressSync)
        {
            _pendingDownloadTitle = title;
            _pendingDownloadFraction = fraction;
            _pendingDownloadShow = show;
            if (_downloadProgressScheduled)
            {
                return;
            }
            _downloadProgressScheduled = true;
        }
        Dispatcher.BeginInvoke(new Action(ApplyPendingDownloadProgress));
    }

    private void ApplyPendingDownloadProgress()
    {
        string title;
        double fraction;
        bool show;
        lock (_downloadProgressSync)
        {
            title = _pendingDownloadTitle;
            fraction = _pendingDownloadFraction;
            show = _pendingDownloadShow;
            _downloadProgressScheduled = false;
        }
        if (_isClosed || DownloadCard == null)
        {
            return;
        }
        if (!show)
        {
            DownloadCard.Hide();
            return;
        }
        if (!DownloadCard.IsShown || DownloadCard.IsCompleted)
        {
            DownloadCard.Show(title, Voidstrap.UI.Elements.Settings.Pages.ExtensionViewModel.CancelActiveDownload);
        }
        DownloadCard.Update(title, fraction);
    }

    private void RootFrame_Navigating(object sender, NavigatingCancelEventArgs e)
    {
        if (_navigationStartedTicks == 0)
        {
            _navigationStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            _navigationCreateMs = 0;
        }
        if (RootFrame.Content is FrameworkElement leaving && !ReferenceEquals(leaving, e.Content) && !IsCachedPage(leaving))
        {
            _discardedPage = leaving;
        }
    }

    private static bool IsDiscardedPage(object? page)
    {
        return page is Page discarded && discarded.Content == null && discarded.DataContext == null;
    }

    private bool IsCachedPage(FrameworkElement page)
    {
        if (ReferenceEquals(page, _libraryPage) || ReferenceEquals(page, _robloxNewsPage))
        {
            return true;
        }
        Type pageType = page.GetType();
        foreach (NavigationItem item in RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>()))
        {
            if (item.PageType == pageType)
            {
                return item.Cache;
            }
        }
        return false;
    }

    private void DetachDiscardedPage()
    {
        FrameworkElement? page = _discardedPage;
        _discardedPage = null;
        if (page == null || page.IsLoaded || ReferenceEquals(page, RootFrame.Content))
        {
            return;
        }
        try
        {
            List<ItemsControl> itemsControls = EnumerateDescendants(page).OfType<ItemsControl>().ToList();
            foreach (ItemsControl itemsControl in itemsControls)
            {
                try
                {
                    System.Windows.Data.BindingOperations.ClearBinding(itemsControl, ItemsControl.ItemsSourceProperty);
                    if (itemsControl.ItemsSource != null)
                    {
                        itemsControl.ItemsSource = null;
                    }
                    else if (itemsControl.Items.Count > 0)
                    {
                        itemsControl.Items.Clear();
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("MainWindow::DetachDiscardedPage", page.GetType().Name + " " + itemsControl.GetType().Name + " " + itemsControl.Name + ": " + ex.Message);
                }
            }
            page.DataContext = null;
            if (page is ContentControl contentHost)
            {
                contentHost.Content = null;
            }
            else if (page is Page pageHost)
            {
                pageHost.Content = null;
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MainWindow::DetachDiscardedPage", ex.Message);
        }
    }

    private static IEnumerable<DependencyObject> EnumerateDescendants(DependencyObject root)
    {
        Stack<DependencyObject> pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            yield return current;
            int count = current is Visual || current is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(current) : 0;
            for (int i = 0; i < count; i++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, i));
            }
        }
    }

    private void LogPageReady(object content)
    {
        if (_navigationStartedTicks == 0)
        {
            return;
        }
        double total = _navigationCreateMs + System.Diagnostics.Stopwatch.GetElapsedTime(_navigationStartedTicks).TotalMilliseconds;
        _navigationStartedTicks = 0;
        App.Logger.WriteLine("MainWindow::Navigation", content.GetType().Name + " ready in " + (int)total + " ms, construct " + (int)_navigationCreateMs + " ms");
    }

    private void RootFrame_Navigated(object sender, NavigationEventArgs e)
    {
		if (Voidstrap.Utility.Platform.IsLinux && (e.Content is DownloadsPage or ExtensionPage))
		{
			if (e.Content is FrameworkElement hiddenPage)
			{
				hiddenPage.Visibility = Visibility.Collapsed;
			}

			Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => RootNavigation.Navigate(typeof(HomePage))));
			return;
		}

        _pageSearchTargets.Clear();
        _lastPage = null;
        TrackNavigationHistory(e.Content);
        object readyContent = e.Content;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => LogPageReady(readyContent)));
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(DetachDiscardedPage));
        object content = e.Content;
        if (content != null && _pagesToHideSearchBox.Contains(content.GetType()))
        {
            GlobalSearchBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            GlobalSearchBox.Visibility = Visibility.Visible;
        }
        if (BreadcrumbPanel != null)
        {
            BreadcrumbPanel.Visibility = content is LibraryPage ? Visibility.Collapsed : Visibility.Visible;
        }
        bool isLibrary = content is LibraryPage;
        RootNavigation.Visibility = (isLibrary ? Visibility.Collapsed : Visibility.Visible);
        SynchronizeSidebarSelection(content);
        SidebarGroup.RefreshActiveChild(MoreNavItem);
        Dispatcher.BeginInvoke(new Action(SynchronizeCurrentSidebarSelection), DispatcherPriority.Loaded);
        UpdateTopNavActive(content);
        if (content is Page)
        {
            Dispatcher.BeginInvoke(new Action(IndexLoadedPageSearchEntries), _pendingTopSearchEntry != null ? DispatcherPriority.Loaded : DispatcherPriority.ApplicationIdle);
        }
        else if (_pendingTopSearchEntry != null)
        {
            _pendingTopSearchEntry = null;
        }
        NavigationItem? navigationItem = RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>()).FirstOrDefault((NavigationItem i) => i.IsActive);
        if (navigationItem != null && _defaultIcons.TryGetValue(navigationItem, out var value))
        {
            BreadcrumbIcon.Symbol = value;
        }
    }

    private void UpdateTopNavActive(object? content)
    {
        TopNavHome.Tag = content is HomePage ? "Active" : null;
        TopNavLibrary.Tag = content is LibraryPage ? "Active" : null;
        TopNavNews.Tag = content is NewsPage ? "Active" : null;
    }

    private NavigationItem[] GetNavigationItemsInServiceOrder()
    {
        return RootNavigation.Items.OfType<NavigationItem>()
            .Concat(RootNavigation.Footer.OfType<NavigationItem>())
            .ToArray();
    }

    public void ApplySidebarCustomization()
    {
        if (RootNavigation == null)
        {
            return;
        }
        _defaultMainSidebarItems ??= RootNavigation.Items.ToArray();
        _defaultFooterSidebarItems ??= RootNavigation.Footer.ToArray();
        INavigationControl[] controls = _defaultMainSidebarItems.Concat(_defaultFooterSidebarItems).ToArray();
        Dictionary<string, NavigationItem> items = controls
            .OfType<NavigationItem>()
            .Where(item => !string.IsNullOrEmpty(item.Name))
            .ToDictionary(item => item.Name, StringComparer.Ordinal);
        _defaultSidebarContent ??= items.ToDictionary(pair => pair.Key, pair => (object?)pair.Value.Content, StringComparer.Ordinal);
        _defaultSidebarIcons ??= items.ToDictionary(pair => pair.Key, pair => pair.Value.Icon, StringComparer.Ordinal);
        _defaultSidebarImages ??= items.ToDictionary(pair => pair.Key, pair => (BitmapSource?)pair.Value.Image, StringComparer.Ordinal);
        Dictionary<string, string> names = App.Settings.Prop.SidebarNames ??= new Dictionary<string, string>();
        Dictionary<string, string> icons = App.Settings.Prop.SidebarIcons ??= new Dictionary<string, string>();
        Dictionary<string, string> iconImages = App.Settings.Prop.SidebarIconImages ??= new Dictionary<string, string>();
        List<string> hidden = App.Settings.Prop.SidebarHiddenItems ??= new List<string>();
        List<string> savedOrder = App.Settings.Prop.SidebarOrder ??= new List<string>();
        Dictionary<string, int> ranks = savedOrder
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Select((key, index) => (key, index))
            .ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
        Dictionary<string, int> defaults = SidebarCustomizationItems
            .Select((item, index) => (item.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);
        foreach (SidebarItemDefinition definition in SidebarCustomizationItems)
        {
            if (!items.TryGetValue(definition.Key, out NavigationItem? item))
            {
                continue;
            }
            item.Content = names.TryGetValue(definition.Key, out string? customName)
                ? NormalizeSidebarName(customName, definition.DefaultName)
                : _defaultSidebarContent[definition.Key];
            item.Icon = _defaultSidebarIcons[definition.Key];
            BitmapSource? defaultImage = _defaultSidebarImages[definition.Key];
            item.SetValue(NavigationItem.ImageProperty, defaultImage);
            if (iconImages.TryGetValue(definition.Key, out string? imagePath)
                && SafeImaging.FromFile(imagePath, 48) is BitmapSource customImage)
            {
                item.Icon = SymbolRegular.Empty;
                item.Image = customImage;
            }
            else if (icons.TryGetValue(definition.Key, out string? iconName)
                && Enum.TryParse(iconName, out SymbolRegular customIcon)
                && customIcon != SymbolRegular.Empty)
            {
                item.SetValue(NavigationItem.ImageProperty, null);
                item.Icon = customIcon;
            }
            bool isHidden = definition.CanHide && hidden.Contains(definition.Key, StringComparer.Ordinal);
            item.Visibility = IsSidebarItemAvailable(definition.Key) && !isHidden ? Visibility.Visible : Visibility.Collapsed;
        }
        IEnumerable<SidebarItemDefinition> Ordered(string section)
        {
            return SidebarCustomizationItems
                .Where(item => item.Section == section && items.ContainsKey(item.Key))
                .OrderBy(item => ranks.TryGetValue(item.Key, out int rank) ? rank : int.MaxValue)
                .ThenBy(item => defaults[item.Key]);
        }
        NavigationItem coreHeader = _defaultMainSidebarItems.OfType<NavigationItem>().First(item => !item.IsEnabled && string.Equals(item.Content?.ToString(), "Core", StringComparison.Ordinal));
        NavigationItem configurationHeader = _defaultMainSidebarItems.OfType<NavigationItem>().First(item => !item.IsEnabled && string.Equals(item.Content?.ToString(), "Configuration", StringComparison.Ordinal));
        Wpf.Ui.Controls.Navigation.NavigationSeparator separator = _defaultMainSidebarItems.OfType<Wpf.Ui.Controls.Navigation.NavigationSeparator>().First();
        bool hasConfigurationItems = Ordered("Configuration").Any(definition => items[definition.Key].Visibility == Visibility.Visible);
        configurationHeader.Visibility = hasConfigurationItems ? Visibility.Visible : Visibility.Collapsed;
        separator.Visibility = configurationHeader.Visibility;
        List<INavigationControl> main = new List<INavigationControl> { coreHeader };
        main.AddRange(Ordered("Core").Select(item => items[item.Key]));
        main.Add(separator);
        main.Add(configurationHeader);
        foreach (SidebarItemDefinition definition in Ordered("Configuration"))
        {
            main.Add(items[definition.Key]);
            if (definition.Key == "MoreNavItem")
            {
                main.AddRange(Ordered("More").Select(item => items[item.Key]));
            }
        }
        foreach (NavigationItem item in items.Values.Where(item => RootNavigation.Items.Contains(item)).ToArray())
        {
            RootNavigation.Items.Remove(item);
        }
        int coreIndex = RootNavigation.Items.IndexOf(coreHeader) + 1;
        foreach (INavigationControl item in main.Skip(1).TakeWhile(item => !ReferenceEquals(item, separator)))
        {
            RootNavigation.Items.Insert(coreIndex++, item);
        }
        int configurationIndex = RootNavigation.Items.IndexOf(configurationHeader) + 1;
        foreach (INavigationControl item in main.SkipWhile(item => !ReferenceEquals(item, configurationHeader)).Skip(1))
        {
            RootNavigation.Items.Insert(configurationIndex++, item);
        }
        foreach (NavigationItem item in items.Values.Where(item => RootNavigation.Footer.Contains(item)).ToArray())
        {
            RootNavigation.Footer.Remove(item);
        }
        int footerIndex = 0;
        foreach (SidebarItemDefinition definition in Ordered("Footer"))
        {
            RootNavigation.Footer.Insert(footerIndex++, items[definition.Key]);
        }
        RegisterHoverIcons();
        if (!string.IsNullOrEmpty(_latestNewsKey) && App.Settings.Prop.LastSeenNewsKey != _latestNewsKey)
        {
            ShowNewsBadge();
        }
        if (IsLoaded)
        {
            SynchronizeCurrentSidebarSelection();
            PopulateTopSearch();
        }
    }

    private string GetSidebarDisplayName(string key, string fallback)
    {
        Dictionary<string, string> names = App.Settings.Prop.SidebarNames ??= new Dictionary<string, string>();
        if (names.TryGetValue(key, out string? customName))
        {
            return NormalizeSidebarName(customName, fallback);
        }
        if (_defaultSidebarContent != null && _defaultSidebarContent.TryGetValue(key, out object? content) && content is string text)
        {
            return text;
        }
        return fallback;
    }

    private void SynchronizeCurrentSidebarSelection()
    {
        SynchronizeSidebarSelection(RootFrame?.Content);
        SidebarGroup.RefreshActiveChild(MoreNavItem);
    }

    private void SynchronizeSidebarSelection(object? content)
    {
        if (content == null || RootNavigation == null)
        {
            return;
        }

        NavigationItem[] navigationItems = GetNavigationItemsInServiceOrder();
        Type contentType = content.GetType();
        NavigationItem? activeItem = navigationItems.FirstOrDefault(item => item.PageType == contentType);
        if (activeItem == null)
        {
            return;
        }

        for (int i = 0; i < navigationItems.Length; i++)
        {
            NavigationItem item = navigationItems[i];
            bool isActive = ReferenceEquals(item, activeItem);
            if (item.IsActive != isActive)
            {
                item.IsActive = isActive;
            }
            if (isActive)
            {
                RootNavigation.SelectedPageIndex = i;
                if (App.State.Prop.LastPage != i)
                {
                    App.State.Prop.LastPage = i;
                    App.State.SaveDeferred();
                }
            }
        }
    }

    private void TrackNavigationHistory(object? content)
    {
        if (content == null || ReferenceEquals(content, _navHistoryCurrent))
        {
            return;
        }
        if (_navHistorySuppressPush)
        {
            _navHistorySuppressPush = false;
        }
        else
        {
            if (_navHistoryCurrent != null)
            {
                AddNavigationHistoryEntry(_navHistoryBack, _navHistoryCurrent);
            }
            _navHistoryForward.Clear();
        }
        _navHistoryCurrent = content;
        UpdateTopNavArrows();
    }

    private static void AddNavigationHistoryEntry(List<NavigationHistoryEntry> history, object content)
    {
        history.Add(new NavigationHistoryEntry(content));
        if (history.Count > MaxNavigationHistoryEntries)
        {
            history.RemoveRange(0, history.Count - MaxNavigationHistoryEntries);
        }
    }

    private bool CanRestoreNavigationHistoryEntry(NavigationHistoryEntry entry)
    {
        return entry.Content.TryGetTarget(out _) || GetSidebarPageNames().ContainsKey(entry.PageType);
    }

    private bool RestoreNavigationHistoryEntry(NavigationHistoryEntry entry, List<NavigationHistoryEntry> oppositeHistory)
    {
        object? target = null;
        bool hasTarget = entry.Content.TryGetTarget(out target) && !IsDiscardedPage(target);
        if (!hasTarget && !GetSidebarPageNames().ContainsKey(entry.PageType))
        {
            return false;
        }
        if (_navHistoryCurrent != null)
        {
            AddNavigationHistoryEntry(oppositeHistory, _navHistoryCurrent);
        }
        _navHistorySuppressPush = true;
        if (hasTarget)
        {
            RootNavigation.NavigateExternal(target!);
        }
        else
        {
            RootNavigation.Navigate(entry.PageType);
        }
        return true;
    }

    private void ResetNavigationHistory()
    {
        _navHistoryBack.Clear();
        _navHistoryForward.Clear();
        UpdateTopNavArrows();
    }

    private void UpdateTopNavArrows()
    {
        _navHistoryBack.RemoveAll(entry => !CanRestoreNavigationHistoryEntry(entry));
        _navHistoryForward.RemoveAll(entry => !CanRestoreNavigationHistoryEntry(entry));
        if (TopNavBack != null)
        {
            TopNavBack.IsEnabled = _navHistoryBack.Count > 0;
        }
        if (TopNavForward != null)
        {
            TopNavForward.IsEnabled = _navHistoryForward.Count > 0;
        }
    }

    private void TopNavBack_Click(object sender, RoutedEventArgs e)
    {
        while (_navHistoryBack.Count != 0)
        {
            NavigationHistoryEntry target = _navHistoryBack[_navHistoryBack.Count - 1];
            _navHistoryBack.RemoveAt(_navHistoryBack.Count - 1);
            if (RestoreNavigationHistoryEntry(target, _navHistoryForward))
            {
                break;
            }
        }
        UpdateTopNavArrows();
    }

    private void TopNavForward_Click(object sender, RoutedEventArgs e)
    {
        while (_navHistoryForward.Count != 0)
        {
            NavigationHistoryEntry target = _navHistoryForward[_navHistoryForward.Count - 1];
            _navHistoryForward.RemoveAt(_navHistoryForward.Count - 1);
            if (RestoreNavigationHistoryEntry(target, _navHistoryBack))
            {
                break;
            }
        }
        UpdateTopNavArrows();
    }

    private void PopulateTopSearch()
    {
        try
        {
            Dictionary<Type, string> navigationPages = new Dictionary<Type, string>();
            foreach (NavigationItem item in GetNavigationItemsInServiceOrder())
            {
                if (item.PageType != null)
                {
                    navigationPages.TryAdd(item.PageType, item.Content as string ?? item.PageType.Name);
                }
            }
            List<TopSearchEntry> entries = new List<TopSearchEntry>();
            foreach (SearchCatalogOption option in SearchCatalog.Options)
            {
                if (!navigationPages.TryGetValue(option.PageType, out string? pageName))
                {
                    continue;
                }
                string title = SearchCatalog.Resolve(option.TitleToken).Trim();
                if (title.Length == 0)
                {
                    continue;
                }
                string description = SearchCatalog.Resolve(option.DescriptionToken).Trim();
				string[] containers = option.Containers.Select(SearchCatalog.Resolve).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
                string displayText = "Option: " + title + " | " + pageName + (containers.Length > 0 ? " | " + string.Join(" › ", containers) : string.Empty);
                List<string> terms = new List<string> { title, option.TitleToken, description, option.TargetName, option.Id };
                terms.AddRange(option.Aliases);
				terms.AddRange(containers);
                entries.Add(new TopSearchEntry(option.Id, displayText, string.Join(" ", terms), option.PageType, title, terms, containers, option.HiddenByDefault));
            }
            _topSearchEntriesList.Clear();
            _topSearchEntries.Clear();
			Dictionary<string, int> displayCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (TopSearchEntry originalEntry in entries)
            {
				int occurrence = displayCounts.TryGetValue(originalEntry.DisplayText, out int count) ? count + 1 : 1;
				displayCounts[originalEntry.DisplayText] = occurrence;
				TopSearchEntry entry = occurrence == 1 ? originalEntry : new TopSearchEntry(originalEntry.Id, originalEntry.DisplayText + " | " + occurrence, originalEntry.SearchText, originalEntry.PageType, originalEntry.TargetText, originalEntry.TargetTerms, originalEntry.ContainerTerms, originalEntry.HiddenByDefault);
                _topSearchEntriesList.Add(entry);
                _topSearchEntries.Add(entry.DisplayText, entry);
            }
        }
        catch (Exception ex)
        {
			App.Logger.WriteLine("MainWindow::Search", "Could not populate settings search: " + ex.Message);
        }
    }

    private static readonly char[] separatorArray = new[] { ' ', '\t', '\r', '\n', ',', '.', ':', '/', '\\', '_', '-' };
    private static readonly char[] separator = new[] { ' ', '\t', '\r', '\n', ',', '.', ':', '/', '\\' };

    private async Task LoadCatalogOptionsAsync()
    {
		try
		{
			for (int attempt = 0; attempt < 3 && !_lifetimeCts.IsCancellationRequested; attempt++)
			{
				await SearchCatalog.LoadOptionsAsync().ConfigureAwait(false);
				if (SearchCatalog.Options.Count > 0)
				{
					break;
				}
				await Task.Delay(TimeSpan.FromMilliseconds(500), _lifetimeCts.Token).ConfigureAwait(false);
			}
			if (!_lifetimeCts.IsCancellationRequested)
			{
				await Dispatcher.InvokeAsync(PopulateTopSearch, DispatcherPriority.Background);
			}
		}
		catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("MainWindow::Search", "Could not load settings search: " + ex.Message);
		}
    }

    private Dictionary<Type, string> GetSidebarPageNames()
    {
        Dictionary<Type, string> pages = new Dictionary<Type, string>();
        IEnumerable<NavigationItem> items = RootNavigation.Items
            .OfType<NavigationItem>()
            .Concat(RootNavigation.Footer.OfType<NavigationItem>());
        foreach (NavigationItem item in items)
        {
            if (!item.IsEnabled || item.Visibility != Visibility.Visible || item.PageType == null)
            {
                continue;
            }
            string name = item.Content as string ?? item.PageType.Name;
            pages.TryAdd(item.PageType, name);
        }
        return pages;
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Page, object> _indexedSearchPages = new System.Runtime.CompilerServices.ConditionalWeakTable<Page, object>();

    private void IndexLoadedPageSearchEntries()
    {
        if (_isClosed || RootFrame.Content is not Page page)
        {
            _pendingTopSearchEntry = null;
            return;
        }
        bool pending = _pendingTopSearchEntry?.PageType == page.GetType();
        if (pending)
        {
            page.UpdateLayout();
        }
        if (_indexedSearchPages.TryAdd(page, page))
        {
            IndexDynamicPageSearchEntries(page);
        }
        PerformSearch(GlobalSearchBox.Text?.Trim() ?? "");
        if (pending)
        {
            ApplyPendingTopSearchEntry();
        }
        else if (_pendingTopSearchEntry != null)
        {
            _pendingTopSearchEntry = null;
        }
    }

    private void IndexDynamicPageSearchEntries(Page page)
    {
        if (!GetSidebarPageNames().TryGetValue(page.GetType(), out string? pageName))
        {
            return;
        }
        Type pageType = page.GetType();
        Dictionary<string, FrameworkElement> optionHeaders = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal);
        Dictionary<string, FrameworkElement> namedElements = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
        foreach (FrameworkElement element in EnumerateSearchElements(page))
        {
            if (!string.IsNullOrWhiteSpace(element.Name))
            {
                namedElements.TryAdd(element.Name, element);
            }
            if (element is OptionControl option)
            {
                optionHeaders.TryAdd(NormalizeSearchText(SearchCatalog.Resolve(option.Header)), option);
                continue;
            }
            if (FindAncestor<OptionControl>(element) != null)
            {
                continue;
            }
            string text = GetSearchableElementText(element);
            if (text.Length < 2 || text.Length > 160 || text.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string displayText = "Option: " + text + " | " + pageName;
            if (_topSearchEntries.TryGetValue(displayText, out TopSearchEntry? existing))
            {
                if (existing.Id.StartsWith("Dynamic.", StringComparison.Ordinal))
                {
                    TrackSearchTarget(existing.Id, element);
                }
                continue;
            }
			TopSearchEntry entry = new TopSearchEntry("Dynamic." + pageType.Name + "." + element.Name + "." + _topSearchEntriesList.Count, displayText, text + " " + element.Name, pageType, text, new[] { text, element.Name });
            _topSearchEntriesList.Add(entry);
            _topSearchEntries.Add(displayText, entry);
            TrackSearchTarget(entry.Id, element);
        }
        foreach (TopSearchEntry entry in _topSearchEntriesList)
        {
            if (entry.PageType != pageType || entry.Id.StartsWith("Dynamic.", StringComparison.Ordinal))
            {
                continue;
            }
            FrameworkElement? target = optionHeaders.GetValueOrDefault(entry.NormalizedTargetText) ?? entry.TargetTerms.Select(term => namedElements.GetValueOrDefault(term)).FirstOrDefault(element => element != null);
            if (target != null)
            {
                TrackSearchTarget(entry.Id, target);
            }
        }
    }

    private void TrackSearchTarget(string id, FrameworkElement element)
    {
        _searchTargetStates[id] = (new WeakReference<FrameworkElement>(element), IsSearchElementShown(element));
    }

    private bool IsSearchEntryShown(TopSearchEntry entry)
    {
        if (!_searchTargetStates.TryGetValue(entry.Id, out var state))
        {
            return !entry.HiddenByDefault;
        }
        if (state.Element.TryGetTarget(out FrameworkElement? element))
        {
            state.Visible = IsSearchElementShown(element);
            _searchTargetStates[entry.Id] = state;
        }
        return state.Visible;
    }

    private static bool IsSearchElementShown(FrameworkElement element)
    {
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
            current = LogicalTreeHelper.GetParent(current) ?? (current is Visual or Visual3D ? VisualTreeHelper.GetParent(current) : null);
        }
        return true;
    }

	private void QueueTopSearchNavigation(TopSearchEntry entry)
	{
        _pendingTopSearchEntry = entry;
        Dispatcher.BeginInvoke(new Action(CompleteTopSearchNavigation), DispatcherPriority.Input);
    }

    private void CompleteTopSearchNavigation()
    {
        TopSearchEntry? entry = _pendingTopSearchEntry;
        _pendingTopSearchEntry = null;
        if (entry != null)
        {
            NavigateTopSearchEntry(entry);
        }
    }

    private void NavigateTopSearchEntry(TopSearchEntry entry)
    {
        if (!GetSidebarPageNames().ContainsKey(entry.PageType))
        {
            _pendingTopSearchEntry = null;
            return;
        }
        if (!string.IsNullOrWhiteSpace(entry.TargetText))
        {
            _pendingTopSearchEntry = entry;
        }
        if (RootFrame.Content?.GetType() == entry.PageType)
        {
            Dispatcher.BeginInvoke(new Action(ApplyPendingTopSearchEntry), DispatcherPriority.Loaded);
            return;
        }
        NavigateTopNav(entry.PageType);
    }

    private void ApplyPendingTopSearchEntry()
    {
        TopSearchEntry? entry = _pendingTopSearchEntry;
        _pendingTopSearchEntry = null;
        if (entry == null || string.IsNullOrWhiteSpace(entry.TargetText) || RootFrame.Content?.GetType() != entry.PageType || RootFrame.Content is not Page page)
        {
            return;
        }
		int generation = Interlocked.Increment(ref _topSearchNavigationGeneration);
		_ = ApplyTopSearchEntryAsync(page, entry, generation);
    }

	private async Task ApplyTopSearchEntryAsync(Page page, TopSearchEntry entry, int generation)
	{
		try
		{
			FrameworkElement? target = null;
			for (int attempt = 0; attempt < 3 && generation == Volatile.Read(ref _topSearchNavigationGeneration) && !_isClosed; attempt++)
			{
				page.UpdateLayout();
				target = FindTopSearchTarget(page, entry);
				if (target != null)
				{
					RevealTopSearchTarget(target, page);
					if (!target.IsVisible)
					{
						RevealSearchContainers(page, entry);
					}
					page.UpdateLayout();
					await Dispatcher.InvokeAsync(page.UpdateLayout, attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.ContextIdle);
					if (target.IsVisible || attempt == 2)
					{
						break;
					}
				}
				else
				{
					RevealSearchContainers(page, entry);
					await Dispatcher.InvokeAsync(page.UpdateLayout, DispatcherPriority.Loaded);
				}
			}
			if (generation != Volatile.Read(ref _topSearchNavigationGeneration) || _isClosed || RootFrame.Content?.GetType() != entry.PageType)
			{
				return;
			}
			if (target == null)
			{
				PerformSearch(entry.TargetText ?? string.Empty);
				return;
			}
			CachePageSearchTargets(page);
			ScrollSearchTargetIntoView(target);
			FocusSearchTarget(target);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("MainWindow::Search", "Could not navigate to the selected setting: " + ex.Message);
			if (!_isClosed && RootFrame.Content is Page currentPage && RootFrame.Content?.GetType() == entry.PageType)
			{
				PerformSearch(entry.TargetText ?? string.Empty);
			}
		}
	}

    private static FrameworkElement? FindTopSearchTarget(Page page, TopSearchEntry entry)
    {
		return EnumerateSearchElements(page)
			.Select(element => (Element: element, Score: ScoreSearchTarget(element, entry)))
			.Where(item => item.Score < int.MaxValue)
			.OrderBy(item => item.Score)
			.ThenByDescending(item => item.Element.IsVisible)
			.Select(item => item.Element)
			.FirstOrDefault();
    }

	private static int ScoreSearchTarget(FrameworkElement element, TopSearchEntry entry)
	{
		if (!string.IsNullOrWhiteSpace(element.Name) && entry.TargetTerms.Any(term => string.Equals(element.Name, term, StringComparison.OrdinalIgnoreCase)))
		{
			return 0;
		}
		string text = GetSearchableElementText(element);
		if (element is OptionControl option)
		{
			if (entry.TargetTerms.Any(term => string.Equals(SearchCatalog.Resolve(option.Header), SearchCatalog.Resolve(term), StringComparison.OrdinalIgnoreCase)))
			{
				return 1;
			}
			if (entry.TargetTerms.Any(term => IsFuzzyMatch(option.Header + " " + option.Description, SearchCatalog.Resolve(term))))
			{
				return 10;
			}
		}
		if (text.Length > 0 && entry.TargetTerms.Any(term => string.Equals(NormalizeSearchText(text), NormalizeSearchText(SearchCatalog.Resolve(term)), StringComparison.Ordinal)))
		{
			return 20;
		}
		if (text.Length > 0 && entry.TargetTerms.Any(term => IsFuzzyMatch(text, SearchCatalog.Resolve(term))))
		{
			return 50;
		}
		return int.MaxValue;
	}

	private static void RevealSearchContainers(Page page, TopSearchEntry entry)
	{
		if (entry.ContainerTerms.Count == 0)
		{
			return;
		}
		foreach (FrameworkElement element in EnumerateSearchElements(page))
		{
			string text = GetSearchableElementText(element);
			if (!entry.ContainerTerms.Any(term => IsFuzzyMatch(text, SearchCatalog.Resolve(term))))
			{
				continue;
			}
			if (element is TabItem tabItem && tabItem.IsEnabled && tabItem.Visibility == Visibility.Visible && ItemsControl.ItemsControlFromItemContainer(tabItem) is TabControl tabControl)
			{
				tabControl.SelectedItem = tabItem;
			}
			else if (element is System.Windows.Controls.Expander nativeExpander)
			{
				nativeExpander.IsExpanded = true;
			}
			else if (element is Voidstrap.UI.Elements.Controls.Expander expander)
			{
				expander.IsExpanded = true;
			}
		}
	}

    internal static void RevealTopSearchTarget(FrameworkElement target, Page page)
    {
        for (DependencyObject? current = target; current != null && current != page; current = GetParent(current))
        {
            if (current is Voidstrap.UI.Elements.Controls.Expander expander)
            {
                expander.IsExpanded = true;
            }
            if (current is System.Windows.Controls.Expander nativeExpander)
            {
                nativeExpander.IsExpanded = true;
            }
            if (current is TabItem tabItem)
            {
                TabControl? tabControl = FindAncestor<TabControl>(tabItem);
                if (tabControl != null)
                {
                    tabControl.SelectedItem = tabItem;
                }
            }
        }
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        if (current is Visual || current is Visual3D)
        {
            DependencyObject? parent = VisualTreeHelper.GetParent(current);
            if (parent != null)
            {
                return parent;
            }
        }
        return LogicalTreeHelper.GetParent(current);
    }

	internal static IReadOnlyList<FrameworkElement> EnumerateSearchElements(DependencyObject root)
	{
		List<FrameworkElement> elements = new List<FrameworkElement>();
		Queue<DependencyObject> pending = new Queue<DependencyObject>();
		HashSet<DependencyObject> seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
		pending.Enqueue(root);
		while (pending.Count > 0)
		{
			DependencyObject current = pending.Dequeue();
			if (!seen.Add(current))
			{
				continue;
			}
			if (current is FrameworkElement element)
			{
				elements.Add(element);
			}
			try
			{
				int visualChildren = VisualTreeHelper.GetChildrenCount(current);
				for (int index = 0; index < visualChildren; index++)
				{
					pending.Enqueue(VisualTreeHelper.GetChild(current, index));
				}
			}
			catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
			{
			}
			object[] logicalChildren;
			try
			{
				logicalChildren = LogicalTreeHelper.GetChildren(current).Cast<object>().ToArray();
			}
			catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
			{
				logicalChildren = Array.Empty<object>();
			}
			foreach (object child in logicalChildren)
			{
				if (child is DependencyObject dependencyChild)
				{
					pending.Enqueue(dependencyChild);
				}
			}
			if (current is ContentControl contentControl && contentControl.Content is DependencyObject content)
			{
				pending.Enqueue(content);
			}
			if (current is ItemsControl itemsControl)
			{
				object[] items;
				try
				{
					items = itemsControl.Items.Cast<object>().ToArray();
				}
				catch (InvalidOperationException)
				{
					items = Array.Empty<object>();
				}
				foreach (object item in items)
				{
					if (item is DependencyObject dependencyItem)
					{
						pending.Enqueue(dependencyItem);
					}
				}
			}
		}
		return elements;
	}

    private static string FormatSearchName(string name)
    {
        return CamelCaseBoundaryPattern.Replace(name.Replace('_', ' '), " ");
    }

    private void TopNavHome_Click(object sender, RoutedEventArgs e)
    {
        NavigateTopNav(typeof(HomePage));
    }

    private void TopNavLibrary_Click(object sender, RoutedEventArgs e)
    {
        _libraryPage ??= new LibraryPage();
        if (!ReferenceEquals(RootFrame.Content, _libraryPage))
        {
            RootNavigation.NavigateExternal(_libraryPage);
        }
    }

    public void NavigateBack()
    {
        if (_navHistoryBack.Count == 0)
        {
            RootNavigation.Navigate(typeof(Pages.HomePage));
            return;
        }
        TopNavBack_Click(this, new RoutedEventArgs());
    }

    public void ShowRobloxNews()
    {
        _robloxNewsPage ??= new Pages.RobloxNewsPage();
        if (!ReferenceEquals(RootFrame.Content, _robloxNewsPage))
        {
            RootNavigation.NavigateExternal(_robloxNewsPage);
        }
    }

    private void TopNavNews_Click(object sender, RoutedEventArgs e)
    {
        NavigateTopNav(typeof(NewsPage));
    }

    private void TopSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCommandPalette();
    }

    private void ToggleCommandPalette()
    {
        if (CommandPalettePopup.IsOpen && !CommandPalettePopup.IsClosing)
        {
            CloseCommandPalette();
            return;
        }
        CloseOverlayPopups();
        CommandPaletteSearchBox.Text = "";
        RefreshCommandPaletteResults();
        CommandPalettePopup.IsOpen = true;
    }

    private void CommandPalettePopup_Opened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(FocusCommandPalette), DispatcherPriority.Input);
    }

    private void FocusCommandPalette()
    {
        if (!CommandPalettePopup.IsOpen)
        {
            return;
        }
        CommandPaletteSearchBox.Focus();
        Keyboard.Focus(CommandPaletteSearchBox);
    }

    private void CloseCommandPalette()
    {
        if (CommandPalettePopup != null)
        {
            CommandPalettePopup.IsOpen = false;
        }
    }

    private void RefreshCommandPaletteResults()
    {
        string query = CommandPaletteSearchBox.Text?.Trim() ?? "";
        _commandPaletteRows.Clear();
        _commandPaletteSelected = -1;
        if (query.Length > 0)
        {
            string normalizedQuery = NormalizeSearchText(query);
            Dictionary<Type, string> pageNames = GetSidebarPageNames();
            Dictionary<Type, SymbolRegular> pageIcons = new Dictionary<Type, SymbolRegular>();
            List<CommandPaletteRow> pageRows = new List<CommandPaletteRow>();
            foreach (NavigationItem item in GetNavigationItemsInServiceOrder())
            {
                if (item.PageType == null || !pageNames.TryGetValue(item.PageType, out string? pageName))
                {
                    continue;
                }
                SymbolRegular icon = item.Icon == SymbolRegular.Empty ? SymbolRegular.Document24 : item.Icon;
                pageIcons.TryAdd(item.PageType, icon);
                Type pageType = item.PageType;
                AddCommandPalettePage(pageRows, pageName, icon, query, () => NavigateTopNav(pageType));
            }
            AddCommandPalettePage(pageRows, "Library", SymbolRegular.Apps24, query, () => TopNavLibrary_Click(this, new RoutedEventArgs()));
            foreach (CommandPaletteRow row in pageRows.OrderBy(row => NormalizeSearchText(row.Title).StartsWith(normalizedQuery, StringComparison.Ordinal) ? 0 : 1))
            {
                _commandPaletteRows.Add(row);
            }
            IEnumerable<TopSearchEntry> settings = _topSearchEntriesList
                .Where(entry => pageNames.ContainsKey(entry.PageType) && !string.Equals(entry.NormalizedTargetText, NormalizeSearchText(pageNames[entry.PageType]), StringComparison.Ordinal))
                .Select(entry => (Entry: entry, Score: ScoreTopSearchEntry(entry, query)))
                .Where(item => item.Score < int.MaxValue)
                .OrderBy(item => item.Score)
                .ThenBy(item => item.Entry.DisplayText.Length)
                .Select(item => item.Entry)
                .Where(IsSearchEntryShown)
                .DistinctBy(entry => (entry.PageType, entry.NormalizedTargetText))
                .Take(30);
            foreach (TopSearchEntry entry in settings)
            {
                TopSearchEntry captured = entry;
                _commandPaletteRows.Add(new CommandPaletteRow
                {
                    Title = entry.TargetText ?? pageNames[entry.PageType],
                    Detail = pageNames[entry.PageType],
                    Icon = pageIcons.GetValueOrDefault(entry.PageType, SymbolRegular.Document24),
                    Open = () => QueueTopSearchNavigation(captured)
                });
            }
        }
        CommandPaletteResultsArea.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        CommandPaletteEmpty.Visibility = query.Length > 0 && _commandPaletteRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandPaletteResultsScroll.Visibility = _commandPaletteRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CommandPaletteResultsScroll.ScrollToTop();
        MoveCommandPaletteSelection(1);
    }

    private static void AddCommandPalettePage(List<CommandPaletteRow> rows, string title, SymbolRegular icon, string query, Action open)
    {
        if (IsFuzzyMatch(title, query))
        {
            rows.Add(new CommandPaletteRow { Title = title, Icon = icon, Open = open });
        }
    }

    private void CommandPaletteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CommandPalettePlaceholder.Visibility = string.IsNullOrEmpty(CommandPaletteSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (CommandPalettePopup.IsOpen)
        {
            RefreshCommandPaletteResults();
        }
    }

    private void CommandPaletteSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            MoveCommandPaletteSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            MoveCommandPaletteSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            OpenSelectedCommandPaletteRow();
            e.Handled = true;
        }
    }

    private void MoveCommandPaletteSelection(int direction)
    {
        int count = _commandPaletteRows.Count;
        if (count == 0)
        {
            return;
        }
        SelectCommandPaletteRow(((_commandPaletteSelected + direction) % count + count) % count);
    }

    private void SelectCommandPaletteRow(int index)
    {
        if (_commandPaletteSelected >= 0 && _commandPaletteSelected < _commandPaletteRows.Count)
        {
            _commandPaletteRows[_commandPaletteSelected].IsSelected = false;
        }
        _commandPaletteSelected = index;
        if (index < 0 || index >= _commandPaletteRows.Count)
        {
            return;
        }
        _commandPaletteRows[index].IsSelected = true;
        if (CommandPaletteResultsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.BringIntoView();
        }
    }

    private void OpenSelectedCommandPaletteRow()
    {
        if (_commandPaletteSelected < 0 || _commandPaletteSelected >= _commandPaletteRows.Count)
        {
            return;
        }
        Action? open = _commandPaletteRows[_commandPaletteSelected].Open;
        if (open == null)
        {
            return;
        }
        CloseCommandPalette();
        open();
    }

    private void CommandPaletteRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommandPaletteRow row })
        {
            int index = _commandPaletteRows.IndexOf(row);
            if (index >= 0 && index != _commandPaletteSelected)
            {
                SelectCommandPaletteRow(index);
            }
        }
    }

    private void CommandPaletteRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CommandPaletteRow row })
        {
            SelectCommandPaletteRow(_commandPaletteRows.IndexOf(row));
            OpenSelectedCommandPaletteRow();
        }
    }

    private void AppMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppMenuPopup.IsOpen && !AppMenuPopup.IsClosing)
        {
            AppMenuPopup.IsOpen = false;
            return;
        }
        CloseOverlayPopups();
        AppMenuPopup.IsOpen = true;
    }

    private void AppMenuAbout_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        Voidstrap.UI.Elements.About.MainWindow window = new Voidstrap.UI.Elements.About.MainWindow
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private async void AppMenuUpdates_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        await CheckForUpdatesAsync(manual: true);
    }

    private bool _checkingForUpdates;

    private async Task AutoCheckForUpdatesAsync()
    {
        if (!App.Settings.Prop.CheckForUpdates || App.LaunchSettings.UpgradeFlag.Active)
        {
            return;
        }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), _lifetimeCts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await CheckForUpdatesAsync(manual: false);
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates)
        {
            return;
        }
        _checkingForUpdates = true;
        try
        {
            string currentText = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
            var release = await App.GetLatestRelease(true) ?? throw new InvalidDataException("Release information is unavailable");
            if (!Version.TryParse(currentText, out Version? current) || !Version.TryParse(release.TagName.TrimStart('v', 'V'), out Version? latest))
            {
                if (manual)
                    Frontend.ShowMessageBox("Could not compare this build with the latest release.");
                return;
            }
            App.Logger.WriteLine("MainWindow::CheckForUpdates", "Local: " + currentText + " | Remote: " + release.TagName);
            if (latest <= current)
            {
                if (manual)
                    Frontend.ShowMessageBox("You are already running the latest version of Voidstrap (" + currentText + ").");
                return;
            }
            if (Frontend.ShowMessageBox("Voidstrap " + release.TagName + " is available. Install it now?", MessageBoxImage.Question, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            {
                return;
            }
            if (!await GithubUpdater.DownloadAndInstallUpdate(release.TagName, _lifetimeCts.Token))
            {
                throw new InvalidDataException("The update could not be installed");
            }
            if (!App.RestartApplication(["-settings", "-elevatedwait", Environment.ProcessId.ToString()]))
            {
                throw new InvalidOperationException("The updated application could not be restarted");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::CheckForUpdates", ex);
            if (manual)
                Frontend.ShowMessageBox("Error checking for updates:\n" + ex.Message);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    private void AppMenuReleases_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        new Voidstrap.UI.Elements.Dialogs.ReleaseNotesDialog { Owner = this }.ShowDialog();
    }

    private DispatcherTimer? _logsSubmenuCloseTimer;

    private void AppMenuItems_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (AppMenuLogsItem.IsMouseOver)
        {
            StopLogsSubmenuCloseTimer();
            if (!AppMenuLogsItem.IsSubmenuOpen)
                AppMenuLogsItem.IsSubmenuOpen = true;
        }
        else if (AppMenuLogsItem.IsSubmenuOpen)
        {
            StartLogsSubmenuCloseTimer();
        }
    }

    private void AppMenuLogsItem_MouseLeave(object sender, MouseEventArgs e)
    {
        if (AppMenuLogsItem.IsSubmenuOpen)
            StartLogsSubmenuCloseTimer();
    }

    private void StartLogsSubmenuCloseTimer()
    {
        if (_logsSubmenuCloseTimer == null)
        {
            _logsSubmenuCloseTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(250) };
            _logsSubmenuCloseTimer.Tick += LogsSubmenuCloseTimer_Tick;
        }
        if (!_logsSubmenuCloseTimer.IsEnabled)
            _logsSubmenuCloseTimer.Start();
    }

    private void StopLogsSubmenuCloseTimer()
    {
        _logsSubmenuCloseTimer?.Stop();
    }

    private void LogsSubmenuCloseTimer_Tick(object? sender, EventArgs e)
    {
        StopLogsSubmenuCloseTimer();
        if (!AppMenuLogsItem.IsMouseOver)
            AppMenuLogsItem.IsSubmenuOpen = false;
    }

    private void AppMenuVoidstrapLogs_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        OpenTopBarFolder(Paths.Logs);
    }

    private void AppMenuRobloxLogs_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        OpenTopBarFolder(Paths.RobloxLogs);
    }

    private void AppMenuApplicationFolder_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        OpenTopBarFolder(Paths.Base);
    }

    private static void OpenTopBarFolder(string path)
    {
        if (!Voidstrap.Utility.PlatformShell.TryOpenFolder(path))
        {
            Frontend.ShowMessageBox("The folder could not be opened.");
        }
    }

    private void AppMenuSupport_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        Utilities.ShellExecute(App.ProjectSupportLink);
    }

    private void AppMenuDonate_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        Utilities.ShellExecute(App.ProjectDonateLink);
    }

    private void AppMenuRestart_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        App.Settings.FlushDeferred();
        App.State.FlushDeferred();
        App.FastFlags.FlushDeferred();
        try
        {
            RestartVoidstrapFromSettings();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::AppMenuRestart", ex);
            Frontend.ShowMessageBox("Voidstrap could not restart: " + ex.Message, MessageBoxImage.Error);
        }
    }


    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void NavigateTopNav(Type pageType)
    {
        if (!GetSidebarPageNames().ContainsKey(pageType))
        {
            return;
        }
        if (RootFrame.Content?.GetType() == pageType)
        {
            return;
        }
        RootNavigation.Navigate(pageType);
    }

    private void GlobalSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchDebounceTimer == null)
        {
            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150L)
            };
            _searchDebounceTimer.Tick += SearchDebounceTimer_Tick;
        }
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void SearchDebounceTimer_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer?.Stop();
        PerformSearch(GlobalSearchBox.Text?.Trim() ?? "");
    }

    private void PerformSearch(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !(RootFrame.Content is Page page))
        {
            return;
        }
        if (page != _lastPage || _pageSearchTargets.Count == 0)
        {
            page.UpdateLayout();
            CachePageSearchTargets(page);
            _lastPage = page;
        }
        string query2 = query.Trim();
        List<PageSearchTarget> matches = new List<PageSearchTarget>();
        foreach (PageSearchTarget target in _pageSearchTargets)
        {
            if (IsFuzzyMatch(target.Text, query2))
            {
				if (target.Element.IsVisible)
				{
					matches.Add(target);
				}
            }
        }
        if (matches.Count > 0)
        {
            ScrollToClosestMatch(matches);
        }
    }

    private void CachePageSearchTargets(Page page)
    {
        _pageSearchTargets.Clear();
        foreach (FrameworkElement element in EnumerateSearchElements(page))
        {
            string text = GetSearchableElementText(element);
            if (text.Length == 0)
            {
                continue;
            }
            _pageSearchTargets.Add(new PageSearchTarget
            {
                Element = element,
				Text = text
            });
        }
    }

    private static string GetSearchableElementText(FrameworkElement element)
    {
        List<string> parts = new List<string>();
		if (element is OptionControl optionControl)
		{
			parts.Add(SearchCatalog.Resolve(optionControl.Header));
			parts.Add(SearchCatalog.Resolve(optionControl.Description));
		}
        if (element is TextBlock textBlock && !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            parts.Add(textBlock.Text);
        }
        if (element is System.Windows.Controls.TextBox textBox && !string.IsNullOrWhiteSpace(textBox.Text))
        {
            parts.Add(textBox.Text);
        }
        if (element is Wpf.Ui.Controls.TextBox uiTextBox && !string.IsNullOrWhiteSpace(uiTextBox.PlaceholderText))
        {
            parts.Add(uiTextBox.PlaceholderText);
        }
        if (element is HeaderedContentControl headeredContentControl && headeredContentControl.Header is string header && !string.IsNullOrWhiteSpace(header))
        {
            parts.Add(header);
        }
		else if (element is HeaderedContentControl complexHeaderControl && complexHeaderControl.Header is DependencyObject complexHeader)
		{
			parts.AddRange(EnumerateSearchElements(complexHeader)
				.OfType<TextBlock>()
				.Select(text => text.Text)
				.Where(text => !string.IsNullOrWhiteSpace(text)));
		}
        if (element is ContentControl contentControl && contentControl.Content is string content && !string.IsNullOrWhiteSpace(content))
        {
            parts.Add(content);
        }
        if (element.ToolTip is string toolTip && !string.IsNullOrWhiteSpace(toolTip))
        {
            parts.Add(toolTip);
        }
        if (!string.IsNullOrWhiteSpace(element.Name))
        {
            parts.Add(FormatSearchName(element.Name));
        }
        return string.Join(" ", parts.Distinct(StringComparer.OrdinalIgnoreCase));
    }

	internal static void ScrollSearchTargetIntoView(FrameworkElement target)
	{
		try
		{
			target.BringIntoView();
			target.UpdateLayout();
			ScrollViewer? scrollViewer = null;
			for (DependencyObject? current = target; current != null; current = GetParent(current))
			{
				if (current is ScrollViewer found)
				{
					scrollViewer = found;
					break;
				}
			}
			if (scrollViewer == null || !target.IsVisible)
			{
				return;
			}
			Point point = target.TransformToAncestor(scrollViewer).Transform(new Point(0.0, 0.0));
			double targetOffset = scrollViewer.VerticalOffset + point.Y - Math.Max(0.0, (scrollViewer.ViewportHeight - target.ActualHeight) / 2.0);
			scrollViewer.ScrollToVerticalOffset(Math.Clamp(targetOffset, 0.0, scrollViewer.ScrollableHeight));
		}
		catch (InvalidOperationException)
		{
			target.BringIntoView();
		}
	}

	private static void FocusSearchTarget(FrameworkElement target)
	{
		Control? control = EnumerateSearchElements(target)
			.OfType<Control>()
			.FirstOrDefault(item => item is not OptionControl && item.Focusable && item.IsEnabled && item.IsVisible);
		if (control != null)
		{
			Keyboard.Focus(control);
		}
	}

    private static void ScrollToClosestMatch(IReadOnlyList<PageSearchTarget> matches)
    {
        if (matches.Count == 0)
        {
            return;
        }
        PageSearchTarget fallback = matches[0];
        foreach (PageSearchTarget match in matches)
        {
            ScrollViewer? scrollViewer = null;
            for (DependencyObject current = match.Element; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is ScrollViewer scrollViewer2)
                {
                    scrollViewer = scrollViewer2;
                    break;
                }
            }
            if (scrollViewer != null)
            {
                Point point = match.Element.TransformToAncestor(scrollViewer).Transform(new Point(0.0, 0.0));
                double viewportHeight = scrollViewer.ViewportHeight;
                double elementHeight = match.Element.ActualHeight;
                if (point.Y + elementHeight < 0.0 || point.Y > viewportHeight)
                {
                    double desiredOffset = scrollViewer.VerticalOffset + point.Y - Math.Max(0.0, (viewportHeight - elementHeight) / 2.0);
                    desiredOffset = Math.Max(0.0, Math.Min(scrollViewer.ScrollableHeight, desiredOffset));
                    SmoothScrollTo(scrollViewer, desiredOffset);
                    return;
                }
            }
        }
        ScrollViewer? fallbackScrollViewer = FindAncestor<ScrollViewer>(fallback.Element);
        if (fallbackScrollViewer != null)
        {
            Point point = fallback.Element.TransformToAncestor(fallbackScrollViewer).Transform(new Point(0.0, 0.0));
            double desiredOffset = fallbackScrollViewer.VerticalOffset + point.Y - Math.Max(0.0, (fallbackScrollViewer.ViewportHeight - fallback.Element.ActualHeight) / 2.0);
            desiredOffset = Math.Max(0.0, Math.Min(fallbackScrollViewer.ScrollableHeight, desiredOffset));
            SmoothScrollTo(fallbackScrollViewer, desiredOffset);
        }
    }

    private static void SmoothScrollTo(ScrollViewer scrollViewer, double targetOffset)
    {
        //IL_003c: Unknown result type (might be due to invalid IL or missing references)
        //IL_0041: Unknown result type (might be due to invalid IL or missing references)
        //IL_0054: Expected O, but got Unknown
        double startOffset = scrollViewer.VerticalOffset;
        double distance = targetOffset - startOffset;
        int steps = 15;
        int currentStep = 0;
        DispatcherTimer timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(15L)
        };
        EventHandler? onTick = null;
        onTick = delegate
        {
            currentStep++;
            double num = (double)currentStep / (double)steps;
            num = num * num * (3.0 - 2.0 * num);
            scrollViewer.ScrollToVerticalOffset(startOffset + distance * num);
            if (currentStep >= steps)
            {
                timer.Stop();
                timer.Tick -= onTick;
            }
        };
        timer.Tick += onTick;
        timer.Start();
    }

    private static void FlashHighlight(TextBlock tb)
    {
        //IL_001f: Unknown result type (might be due to invalid IL or missing references)
        //IL_0024: Unknown result type (might be due to invalid IL or missing references)
        //IL_003a: Expected O, but got Unknown
        Brush originalBrush = tb.Background;
        DispatcherTimer flashTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300L)
        };
        EventHandler? onTick = null;
        onTick = delegate
        {
            flashTimer.Stop();
            flashTimer.Tick -= onTick;
            tb.Background = originalBrush;
        };
        flashTimer.Tick += onTick;
        flashTimer.Start();
    }

	private static int ScoreTopSearchEntry(TopSearchEntry entry, string query)
	{
		string normalizedQuery = NormalizeSearchText(query);
		if (normalizedQuery.Length == 0)
		{
			return 0;
		}
		if (string.Equals(entry.NormalizedTargetText, normalizedQuery, StringComparison.Ordinal))
		{
			return 0;
		}
		if (entry.NormalizedTargetText.StartsWith(normalizedQuery, StringComparison.Ordinal))
		{
			return 5;
		}
		if (entry.NormalizedSearchText.StartsWith(normalizedQuery, StringComparison.Ordinal))
		{
			return 10;
		}
		int position = entry.NormalizedSearchText.IndexOf(normalizedQuery, StringComparison.Ordinal);
		if (position >= 0)
		{
			return 20 + Math.Min(position, 20);
		}
		return IsFuzzyMatch(entry.SearchText, query) ? 100 + Math.Abs(entry.NormalizedTargetText.Length - normalizedQuery.Length) : int.MaxValue;
	}

	private static string NormalizeSearchText(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return string.Empty;
		}
		return WhitespacePattern.Replace(value.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), " ");
	}

    private static bool IsFuzzyMatch(string text, string query)
    {
		string normalizedText = NormalizeSearchText(text);
		string[] terms = NormalizeSearchText(query).Split(separator, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return true;
        }
        string[] words = normalizedText.Split(separatorArray, StringSplitOptions.RemoveEmptyEntries);
        foreach (string term in terms)
        {
            if (normalizedText.Contains(term, StringComparison.Ordinal))
            {
                continue;
            }
            int threshold = term.Length < 4 ? 0 : Math.Max(1, term.Length / 4);
            if (!words.Any(word => word.Contains(term, StringComparison.Ordinal) || (Math.Abs(word.Length - term.Length) <= threshold && LevenshteinDistance(word, term) <= threshold)))
            {
                return false;
            }
        }
        return true;
    }

    private static int LevenshteinDistance(string s, string t)
    {
		if (s.Length > t.Length)
		{
			(s, t) = (t, s);
		}
		int[] previous = new int[s.Length + 1];
		int[] current = new int[s.Length + 1];
		for (int index = 0; index <= s.Length; index++)
		{
			previous[index] = index;
		}
		for (int row = 1; row <= t.Length; row++)
		{
			current[0] = row;
			for (int column = 1; column <= s.Length; column++)
			{
				int substitution = previous[column - 1] + (s[column - 1] == t[row - 1] ? 0 : 1);
				current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1), substitution);
			}
			(previous, current) = (current, previous);
		}
		return previous[s.Length];
    }

    private static void AnimateOpacity(UIElement element, double toOpacity, double durationSeconds = 0.5)
    {
        if (element != null)
        {
            DoubleAnimation animation = new DoubleAnimation
            {
                To = toOpacity,
                Duration = TimeSpan.FromSeconds(durationSeconds),
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseInOut
                }
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation);
        }
    }

    private void OnGlobalBackgroundChanged(GlobalBackground.State state)
    {
        if (_isClosed)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)(() => ApplyBackgroundState(state)));
            return;
        }
        ApplyBackgroundState(state);
    }

    private void ApplyBackgroundState(GlobalBackground.State state)
    {
        GradientLayerOpacity = state.GradientOpacity;
        if (BackgroundBlackOverlay != null)
        {
            BackgroundBlackOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            BackgroundBlackOverlay.Opacity = state.BlackOverlayOpacity;
        }
        DateTime writeTimeUtc = !string.IsNullOrWhiteSpace(state.FilePath) && File.Exists(state.FilePath)
            ? File.GetLastWriteTimeUtc(state.FilePath)
            : default;
        if (!string.Equals(_currentBackgroundPath, state.FilePath, StringComparison.OrdinalIgnoreCase) || _currentBackgroundWriteTimeUtc != writeTimeUtc)
        {
            _ = SetBackgroundImage(state.FilePath);
        }
    }

    public void SetBackgroundOverlay(double opacity)
    {
        try
        {
            if (BackgroundBlackOverlay == null)
                return;
            BackgroundBlackOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            BackgroundBlackOverlay.Opacity = Math.Max(0.0, Math.Min(1.0, opacity));
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::SetBackgroundOverlay", ex);
        }
    }

    public void RestoreBackground()
    {
        try
        {
            ApplyBackgroundSettings();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::RestoreBackground", ex);
        }
    }

    private void ApplyBackgroundSettings()
    {
        _backgroundSettings = AppearanceViewModel.LoadSettings();
        ApplyBackgroundState(new GlobalBackground.State(
            _backgroundSettings.BackgroundFilePath,
            _backgroundSettings.GradientOpacity,
            _backgroundSettings.BlackOverlayOpacity,
            _backgroundSettings.DisplayEverywhere));
    }

    public async Task SetBackgroundImage(string? path, bool loop = true)
    {
        int generation = ++_backgroundGeneration;
        if (_isClosed)
        {
            return;
        }
        if (BackgroundImage == null || GradientLayer == null)
        {
            return;
        }
        if (BackgroundImage.Visibility == Visibility.Visible)
        {
            await FadeOutElementAsync(BackgroundImage, 0.12);
            if (!IsCurrentBackgroundOperation(generation))
                return;
        }
        if (BackgroundMedia != null && BackgroundMedia.Visibility == Visibility.Visible)
        {
            BackgroundMedia.Stop();
            BackgroundMedia.MediaEnded -= BackgroundMedia_MediaEnded;
            await FadeOutElementAsync(BackgroundMedia, 0.12);
            if (!IsCurrentBackgroundOperation(generation))
                return;
        }
        if (BackgroundPortableMedia != null && BackgroundPortableMedia.Visibility == Visibility.Visible)
        {
            await FadeOutElementAsync(BackgroundPortableMedia, 0.12);
            if (!IsCurrentBackgroundOperation(generation))
                return;
        }
        ImageBehavior.SetAnimatedSource(BackgroundImage, null);
        BackgroundImage.Source = null;
        if (BackgroundPortableMedia != null)
        {
            BackgroundPortableMedia.SourcePath = string.Empty;
            BackgroundPortableMedia.Visibility = Visibility.Collapsed;
        }
        if (BackgroundMedia != null)
        {
            BackgroundMedia.MediaEnded -= BackgroundMedia_MediaEnded;
            try
            {
                BackgroundMedia.Stop();
                BackgroundMedia.Close();
            }
            catch
            {
            }
            BackgroundMedia.Source = null;
            BackgroundMedia.Visibility = Visibility.Collapsed;
        }
        GradientLayer.BeginAnimation(UIElement.OpacityProperty, null);
        GradientLayer.Opacity = GlobalBackground.Current.GradientOpacity;
        GradientLayer.Visibility = Visibility.Visible;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _currentBackgroundPath = null;
            _currentBackgroundWriteTimeUtc = default;
            BackgroundImage.Visibility = Visibility.Collapsed;
            if (BackgroundMedia != null) { BackgroundMedia.Visibility = Visibility.Collapsed; }
            return;
        }
        _currentBackgroundPath = path;
        _currentBackgroundWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        if (BackgroundPortableMedia != null)
        {
            BackgroundPortableMedia.SourcePath = path;
            BackgroundPortableMedia.Visibility = Visibility.Visible;
            BackgroundImage.Visibility = Visibility.Collapsed;
            await FadeInElementAsync(BackgroundPortableMedia, 0.2);
            return;
        }
        string text = System.IO.Path.GetExtension(path).ToLowerInvariant();
        bool flag;
        switch (text)
        {
            case ".png":
            case ".jpg":
            case ".jpeg":
            case ".bmp":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag)
        {
            int num = GetBackgroundDecodeWidth(2560);
            BitmapSource? bitmap = await Task.Run(() => Voidstrap.Utility.SafeImaging.FromFile(path, num));
            if (!IsCurrentBackgroundOperation(generation))
            {
                return;
            }
            if (bitmap == null)
            {
                _currentBackgroundPath = null;
                BackgroundImage.Visibility = Visibility.Collapsed;
                return;
            }
            BackgroundImage.Source = bitmap;
            BackgroundImage.Visibility = Visibility.Visible;
            if (BackgroundMedia != null)
            {
                BackgroundMedia.Visibility = Visibility.Collapsed;
            }
            await FadeInElementAsync(BackgroundImage, 0.2);
            return;
        }
        switch (text)
        {
            case ".gif":
                {
                    if (Voidstrap.Utility.Platform.IsWindows)
                    {
                        if (new FileInfo(path).Length > 32L * 1024 * 1024)
                            return;
                        BitmapImage value = new BitmapImage();
                        value.BeginInit();
                        value.CacheOption = BitmapCacheOption.OnLoad;
                        value.DecodePixelWidth = GetBackgroundDecodeWidth(1280);
                        value.UriSource = new Uri(path, UriKind.Absolute);
                        value.EndInit();
                        if (value.CanFreeze)
                            value.Freeze();
                        ImageBehavior.SetAnimatedSource(BackgroundImage, value);
                        ImageBehavior.SetRepeatBehavior(BackgroundImage, loop ? RepeatBehavior.Forever : new RepeatBehavior(1.0));
                    }
                    else
                    {
                        BackgroundImage.Source = Voidstrap.Utility.SafeImaging.FromFile(path);
                    }
                    BackgroundImage.Visibility = Visibility.Visible;
                    if (BackgroundMedia != null)
                    {
                        BackgroundMedia.Visibility = Visibility.Collapsed;
                    }
                    await FadeInElementAsync(BackgroundImage, 0.2);
                    return;
                }
            case ".mp4":
            case ".webm":
            case ".avi":
            case ".mov":
                flag = true;
                break;
            default:
                flag = false;
                break;
        }
        if (flag && BackgroundMedia == null)
            CreateBackgroundMedia();
        if (flag && BackgroundMedia != null)
        {
            BackgroundMedia.Source = new Uri(path, UriKind.Absolute);
            BackgroundMedia.Visibility = Visibility.Visible;
            BackgroundImage.Visibility = Visibility.Collapsed;
            BackgroundMedia.LoadedBehavior = MediaState.Manual;
            BackgroundMedia.UnloadedBehavior = MediaState.Stop;
            BackgroundMedia.Volume = 0.0;
            if (loop)
            {
                BackgroundMedia.MediaEnded += BackgroundMedia_MediaEnded;
            }
            BackgroundMedia.Play();
            await FadeInElementAsync(BackgroundMedia, 0.2);
        }
    }

    private int GetBackgroundDecodeWidth(int maxWidth)
    {
        try
        {
            int width = (int)Math.Ceiling(((ActualWidth > 0.0) ? ActualWidth : ((Width > 0.0) ? Width : 1280.0)) * VisualTreeHelper.GetDpi(this).DpiScaleX);
            return Math.Clamp(width, 320, maxWidth);
        }
        catch
        {
            return Math.Min(1280, maxWidth);
        }
    }

    private bool IsCurrentBackgroundOperation(int generation)
    {
        return !_isClosed && generation == _backgroundGeneration;
    }

    private Task FadeOutElementAsync(UIElement element, double durationSeconds)
    {
        return FadeElementAsync(element, 0.0, durationSeconds, collapse: true);
    }

    private Task FadeInElementAsync(UIElement element, double durationSeconds)
    {
        if (element != null)
            element.Visibility = Visibility.Visible;
        return FadeElementAsync(element!, 1.0, durationSeconds, collapse: false);
    }

    private Task FadeElementAsync(UIElement element, double target, double durationSeconds, bool collapse)
    {
        if (element == null)
        {
            return Task.CompletedTask;
        }
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_backgroundAnimationWaiters.Remove(element, out TaskCompletionSource<bool>? previous))
            previous.TrySetResult(result: false);
        _backgroundAnimationWaiters[element] = tcs;

        double from = element.Opacity;
        element.Opacity = target;

        DispatcherTimer? watchdog = null;
        void settle()
        {
            if (watchdog != null)
            {
                watchdog.Stop();
                watchdog = null;
            }
            if (!_backgroundAnimationWaiters.TryGetValue(element, out TaskCompletionSource<bool>? current) || !ReferenceEquals(current, tcs))
                return;
            _backgroundAnimationWaiters.Remove(element);
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = target;
            if (collapse && !_isClosed)
                element.Visibility = Visibility.Collapsed;
            tcs.TrySetResult(result: true);
        }

        Wpf.Ui.Animations.RenderReady.Hold(this, TimeSpan.FromSeconds(durationSeconds));
        DoubleAnimation doubleAnimation = new DoubleAnimation
        {
            From = from,
            To = target,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseInOut
            },
            FillBehavior = FillBehavior.Stop
        };
        doubleAnimation.Completed += delegate
        {
            settle();
        };
        watchdog = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(durationSeconds) + TimeSpan.FromMilliseconds(750.0)
        };
        watchdog.Tick += delegate
        {
            settle();
        };
        watchdog.Start();
        element.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
        return tcs.Task;
    }

    private void BackgroundMedia_MediaEnded(object? sender, RoutedEventArgs e)
    {
        if (sender is MediaElement mediaElement)
        {
            mediaElement.Position = TimeSpan.Zero;
            mediaElement.Play();
        }
    }

    private void RootGrid_MouseMove(object sender, MouseEventArgs e)
    {
        //IL_000d: Unknown result type (might be due to invalid IL or missing references)
        //IL_0012: Unknown result type (might be due to invalid IL or missing references)
        //IL_0070: Unknown result type (might be due to invalid IL or missing references)
        //IL_0075: Unknown result type (might be due to invalid IL or missing references)
        if (sender is FrameworkElement frameworkElement)
        {
            Point position = e.GetPosition(frameworkElement);
            double num = (position.X / frameworkElement.ActualWidth - 0.5) * 2.0;
            double num2 = (position.Y / frameworkElement.ActualHeight - 0.5) * 2.0;
            _targetOffset = new Vector(num * 0.04, num2 * 0.04);
            _targetRotation = num * 5.0;
            StartGradientRendering();
        }
    }

    private void RootGrid_MouseLeave(object sender, MouseEventArgs e)
    {
        //IL_0013: Unknown result type (might be due to invalid IL or missing references)
        //IL_0018: Unknown result type (might be due to invalid IL or missing references)
        _targetOffset = new Vector(0.0, 0.0);
        _targetRotation = 0.0;
        StartGradientRendering();
    }

    private void StartGradientRendering()
    {
        if (App.Settings.Prop.GRADmentFR && base.IsActive)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        //IL_0002: Unknown result type (might be due to invalid IL or missing references)
        //IL_0008: Unknown result type (might be due to invalid IL or missing references)
        //IL_000e: Unknown result type (might be due to invalid IL or missing references)
        //IL_0013: Unknown result type (might be due to invalid IL or missing references)
        //IL_0021: Unknown result type (might be due to invalid IL or missing references)
        //IL_0026: Unknown result type (might be due to invalid IL or missing references)
        //IL_002b: Unknown result type (might be due to invalid IL or missing references)
        _currentOffset += (_targetOffset - _currentOffset) * 0.035;
        _currentRotation += (_targetRotation - _currentRotation) * 0.035;
        BackgroundGradientTranslate.X = _currentOffset.X;
        BackgroundGradientTranslate.Y = _currentOffset.Y;
        BackgroundGradientRotate.Angle = _currentRotation;
        if ((_targetOffset - _currentOffset).Length < 0.0005 && Math.Abs(_targetRotation - _currentRotation) < 0.01)
        {
            _currentOffset = _targetOffset;
            _currentRotation = _targetRotation;
            BackgroundGradientTranslate.X = _currentOffset.X;
            BackgroundGradientTranslate.Y = _currentOffset.Y;
            BackgroundGradientRotate.Angle = _currentRotation;
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
        }
    }

    private void InitializeDiscordRPC()
    {
        _ = SuperviseDiscordRpcAsync(_lifetimeCts.Token);
        if (RootNavigation != null)
        {
            RootNavigation.Navigated += RootNavigation_RpcNavigated;
        }
        Activated += MainWindow_ActivatedRpc;
        Closed += MainWindow_ClosedRpc;
        Voidstrap.UI.Elements.Settings.Pages.ExtensionViewModel.AnyProgressChanged += OnExtensionProgressChanged;
        _ = CheckForNewNewsAsync();
        _ = FetchRpcAvatarAsync();
    }

    private async Task SuperviseDiscordRpcAsync(CancellationToken token)
    {
        try
        {
            while (!_isClosed && !token.IsCancellationRequested)
            {
                if (_discordRpcEnabled && _discordClient == null && DateTime.UtcNow >= _discordRetryAtUtc && DiscordIpc.TryFindPipe(out int pipe))
                {
                    await Dispatcher.InvokeAsync(() => InitializeDiscordRpcClient(pipe), DispatcherPriority.Background, token);
                }

                await Task.Delay(DiscordIpc.PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void InitializeDiscordRpcClient(int pipe)
    {
        if (_isClosed || !_discordRpcEnabled || _discordClient != null)
            return;

        DiscordRpcLogger logger = new DiscordRpcLogger("DiscordRichPresence::App")
        {
            Level = LogLevel.Warning
        };
        DiscordRpcClient client = new DiscordRpcClient("1459679943498661910", pipe, logger, true, null);
        client.OnReady += DiscordClient_OnReady;
        client.OnError += DiscordClient_OnError;
        client.OnConnectionFailed += DiscordClient_OnConnectionFailed;
        _discordClient = client;
        try
        {
            client.Initialize();
        }
        catch
        {
            ReleaseDiscordRpcClient();
        }
    }

    private void DiscordClient_OnConnectionFailed(object sender, ConnectionFailedMessage e)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(new Action(() => RetryDiscordRpcLater(sender)));
        }
    }

    private void RetryDiscordRpcLater(object client)
    {
        if (!ReferenceEquals(client, _discordClient))
            return;

        App.Logger.WriteLine("DiscordRPC", "Discord connection failed, retrying in " + (int)DiscordIpc.RetryDelay.TotalSeconds + " seconds");
        _discordRetryAtUtc = DateTime.UtcNow + DiscordIpc.RetryDelay;
        ReleaseDiscordRpcClient();
    }

    private void ReleaseDiscordRpcClient()
    {
        DiscordRpcClient? client = _discordClient;
        _discordClient = null;
        _discordReady = false;
        if (client == null)
            return;

        client.OnReady -= DiscordClient_OnReady;
        client.OnError -= DiscordClient_OnError;
        client.OnConnectionFailed -= DiscordClient_OnConnectionFailed;
        try
        {
            client.Dispose();
        }
        catch
        {
        }
    }

    private void DiscordClient_OnReady(object sender, ReadyMessage e)
    {
		_discordReady = true;
        App.Logger.WriteLine("DiscordRPC", "Connected to Discord as " + e.User.Username);
		if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
		{
			Dispatcher.BeginInvoke(new Action(UpdateDiscordPresence));
		}
    }

    private void DiscordClient_OnError(object sender, ErrorMessage e)
    {
        App.Logger.WriteLine("DiscordRPC", "DiscordRPC Error: " + e.Message);
    }

    private void RootNavigation_RpcNavigated(INavigation sender, RoutedNavigationEventArgs e)
    {
        UpdateDiscordPresence();
        if (RootFrame?.Content is Voidstrap.UI.Elements.Settings.Pages.NewsPage)
        {
            ClearNewsBadge();
        }
    }

    private void MainWindow_ActivatedRpc(object? sender, EventArgs e)
    {
        UpdateDiscordPresence();
    }

    private void MainWindow_ClosedRpc(object? sender, EventArgs e)
    {
        Activated -= MainWindow_ActivatedRpc;
        Closed -= MainWindow_ClosedRpc;
    }

    private string _latestNewsKey = "";

    private string _voidRpcSmallImageUrl = "";

    private string _voidRpcSmallImageText = "";

    private async Task FetchRpcAvatarAsync()
    {
        try
        {
            string? cookie = Voidstrap.Integrations.RobloxCookie.Get();
            if (string.IsNullOrEmpty(cookie))
            {
                return;
            }
            long robloxId = 0;
            string userName = "";
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token))
            using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://users.roblox.com/v1/users/authenticated"))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
                using var response = await App.HttpClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }
                string text = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 200000, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    idProp.TryGetInt64(out robloxId);
                }
                if (doc.RootElement.TryGetProperty("displayName", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    userName = dn.GetString() ?? "";
                }
                if (string.IsNullOrEmpty(userName) && doc.RootElement.TryGetProperty("name", out var nm) && nm.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    userName = nm.GetString() ?? "";
                }
            }
            if (robloxId <= 0)
            {
                return;
            }
            string imageUrl = "";
            using (var cts2 = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token))
            {
                cts2.CancelAfter(TimeSpan.FromSeconds(15));
                using var thumbResponse = await App.HttpClient.GetAsync("https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + robloxId + "&size=150x150&format=Png&isCircular=true", System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts2.Token).ConfigureAwait(continueOnCapturedContext: false);
                if (!thumbResponse.IsSuccessStatusCode)
                {
                    return;
                }
                string thumbText = await Voidstrap.Utility.Http.ReadStringBoundedAsync(thumbResponse.Content, 200000, cts2.Token).ConfigureAwait(continueOnCapturedContext: false);
                using var thumbDoc = System.Text.Json.JsonDocument.Parse(thumbText);
                if (thumbDoc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == System.Text.Json.JsonValueKind.Array && dataProp.GetArrayLength() > 0)
                {
                    var first = dataProp[0];
                    if (first.TryGetProperty("imageUrl", out var urlProp) && urlProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        imageUrl = urlProp.GetString() ?? "";
                    }
                }
            }
            if (string.IsNullOrEmpty(imageUrl) || imageUrl.Length > 250)
            {
                return;
            }
            if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? imageUri) || imageUri.Scheme != Uri.UriSchemeHttps)
            {
                return;
            }
            string host = imageUri.Host.ToLowerInvariant();
            if (host != "tr.rbxcdn.com" && !host.EndsWith(".rbxcdn.com", StringComparison.Ordinal) && !host.EndsWith(".roblox.com", StringComparison.Ordinal))
            {
                return;
            }
            _voidRpcSmallImageUrl = imageUri.AbsoluteUri;
            if (!string.IsNullOrEmpty(userName))
            {
                _voidRpcSmallImageText = userName;
            }
            _lastVoidRpcDetails = null;
            _lastVoidRpcState = null;
            _lastVoidRpcExtra = null;
            await Dispatcher.InvokeAsync(UpdateDiscordPresence);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("DiscordRPC", "Avatar fetch failed: " + ex.GetType().Name);
        }
    }

    private async Task CheckForNewNewsAsync()
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, NewsViewModel.FeedUrl);
            using var response = await App.HttpClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            string text = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 2000000, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
            string key = "";
            using (var doc = System.Text.Json.JsonDocument.Parse(text))
            {
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    string title = first.TryGetProperty("Title", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.String ? tEl.GetString() ?? "" : "";
                    string date = first.TryGetProperty("Date", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String ? dEl.GetString() ?? "" : "";
                    if (title.Length > 200)
                    {
                        title = title.Substring(0, 200);
                    }
                    key = date + "|" + title;
                }
            }
            if (string.IsNullOrEmpty(key) || key == "|")
            {
                return;
            }
            _latestNewsKey = key;
            string seen = App.Settings.Prop.LastSeenNewsKey;
            if (string.IsNullOrEmpty(seen))
            {
                App.Settings.Prop.LastSeenNewsKey = key;
                App.Settings.SaveDeferred();
                return;
            }
            if (seen != key)
            {
                await Dispatcher.InvokeAsync(ShowNewsBadge);
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("NewsBadge", "Check failed: " + ex.GetType().Name);
        }
    }

    private void ShowNewsBadge()
    {
        try
        {
            if (NewsNavItem == null)
            {
                return;
            }
            NewsNavItem.Content = BuildBadgedLabel(GetSidebarDisplayName("NewsNavItem", "News"));
            if (MoreNavItem != null)
            {
                MoreNavItem.Content = BuildBadgedLabel(GetSidebarDisplayName("MoreNavItem", "More"));
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::ShowNewsBadge", ex);
        }
    }

    private static System.Windows.Controls.StackPanel BuildBadgedLabel(string text)
    {
        var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 7.0,
            Height = 7.0,
            Margin = new Thickness(6.0, 1.0, 0.0, 0.0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
        });
        return panel;
    }

    private void ClearNewsBadge()
    {
        try
        {
            if (NewsNavItem != null && NewsNavItem.Content is not string)
            {
                NewsNavItem.Content = GetSidebarDisplayName("NewsNavItem", "News");
            }
            if (MoreNavItem != null && MoreNavItem.Content is not string)
            {
                MoreNavItem.Content = GetSidebarDisplayName("MoreNavItem", "More");
            }
            if (!string.IsNullOrEmpty(_latestNewsKey) && App.Settings.Prop.LastSeenNewsKey != _latestNewsKey)
            {
                App.Settings.Prop.LastSeenNewsKey = _latestNewsKey;
                App.Settings.SaveDeferred();
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::ClearNewsBadge", ex);
        }
    }

    private (string PageKey, string Display) GetCurrentPageInfo()
    {
        string text = "";
        object? obj = RootFrame?.Content;
        if (obj != null)
        {
            text = obj.GetType().Name;
        }
        string? item = "";
        NavigationItem[] navigationItems = GetNavigationItemsInServiceOrder();
        if (RootNavigation.SelectedPageIndex >= 0 && RootNavigation.SelectedPageIndex < navigationItems.Length && navigationItems[RootNavigation.SelectedPageIndex] is NavigationItem { Content: var content } navigationItem)
        {
            if (!string.IsNullOrWhiteSpace(content?.ToString()))
            {
                item = navigationItem.Content.ToString();
            }
            if (string.IsNullOrEmpty(text) && (object)navigationItem.PageType != null)
            {
                text = navigationItem.PageType.Name;
            }
        }
        return (PageKey: text, Display: item ?? string.Empty);
    }

    public void ToggleDiscordRPC(bool enabled)
    {
        _discordRpcEnabled = enabled;
        if (_discordClient != null)
        {
            if (!_discordRpcEnabled)
            {
                _discordClient.ClearPresence();
                _lastVoidRpcDetails = null;
                _lastVoidRpcState = null;
                _lastVoidRpcExtra = null;
                App.Logger.WriteLine("DiscordRPC", "DiscordRPC disabled.");
            }
            else
            {
                UpdateDiscordPresence();
                App.Logger.WriteLine("DiscordRPC", "DiscordRPC enabled.");
            }
        }
    }

    public void ApplyGradientMovement(bool enabled)
    {
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        if (enabled)
        {
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
    }

    public void ApplySnow(bool enabled)
    {
        try
        {
            SnowCanvas?.SetActive(enabled && IsActive);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::ApplySnow", ex);
        }
    }

    public void ApplyGradientOpacityLive(double opacity)
    {
        try
        {
            GradientLayerOpacity = opacity;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::ApplyGradientOpacityLive", ex);
        }
    }

    private bool IsRobloxRunning()
    {
        if ((DateTime.UtcNow - _lastRobloxCheck).TotalMilliseconds >= 1000.0 && Interlocked.Exchange(ref _robloxCheckRunning, 1) == 0)
        {
            _lastRobloxCheck = DateTime.UtcNow;
            _ = Task.Run(RefreshRobloxRunning);
        }
        return _robloxRunningCached;
    }

    private void RefreshRobloxRunning()
    {
        try
        {
            _robloxRunningCached = AnyProcessRunning("RobloxPlayerBeta") || AnyProcessRunning("RobloxStudioBeta");
        }
        catch
        {
            _robloxRunningCached = false;
        }
        finally
        {
            Interlocked.Exchange(ref _robloxCheckRunning, 0);
        }
    }

    private static bool AnyProcessRunning(string name)
    {
        Process[] processes = Process.GetProcessesByName(name);
        bool running = processes.Length != 0;
        for (int i = 0; i < processes.Length; i++)
        {
            try
            {
                processes[i].Dispose();
            }
            catch
            {
            }
        }
        return running;
    }

    private void UpdateDiscordPresence()
    {
        if (_discordClient == null || !_discordReady || !_discordRpcEnabled)
        {
            return;
        }
        if (IsRobloxRunning())
        {
            if (!_voidRpcSuppressed)
            {
                try
                {
                    _discordClient.ClearPresence();
                }
                catch
                {
                }
                _voidRpcSuppressed = true;
                _lastVoidRpcDetails = null;
                _lastVoidRpcState = null;
                _lastVoidRpcExtra = null;
            }
            return;
        }
        _voidRpcSuppressed = false;
        if ((DateTime.UtcNow - _lastVoidRpcUpdate).TotalMilliseconds < 1500.0)
        {
            return;
        }
        _lastVoidRpcUpdate = DateTime.UtcNow;
        var (text, text2) = GetCurrentPageInfo();
        string details;
        string state;
        VoidstrapPresenceContext? context = VoidstrapPresence.Current;
        if (context != null && !string.Equals(context.Owner, text, StringComparison.Ordinal))
        {
            context = null;
        }
        if (context != null)
        {
            details = context.Details;
            state = context.State;
        }
        else if (!string.IsNullOrEmpty(text) && _voidRpcPageDescriptions.TryGetValue(text, out (string, string) value))
        {
            (details, state) = value;
        }
        else if (!string.IsNullOrWhiteSpace(text2))
        {
            details = text2;
            state = "Exploring Voidstrap";
        }
        else
        {
            details = "Idle";
            state = "Configuring Voidstrap";
        }
        details = VoidstrapPresence.Clip(details, 128);
        state = VoidstrapPresence.Clip(state, 128);
        if (details.Length < 2)
        {
            details = "Voidstrap";
        }
        if (state.Length < 2)
        {
            state = "Exploring Voidstrap";
        }
        string imageUrl = context != null && VoidstrapPresence.IsWebUrl(context.ImageUrl) ? context.ImageUrl : "";
        string buttonUrl = context != null && VoidstrapPresence.IsWebUrl(context.ButtonUrl) ? context.ButtonUrl : "";
        string extra = imageUrl + "|" + buttonUrl;
        if (details == _lastVoidRpcDetails && state == _lastVoidRpcState && extra == _lastVoidRpcExtra)
        {
            return;
        }
        string text3 = "";
        try
        {
            text3 = App.Version ?? "";
        }
        catch
        {
            text3 = "";
        }
        string versionText = (string.IsNullOrWhiteSpace(text3) ? "Voidstrap" : ("Voidstrap v" + text3));
        const string VoidstrapLogo = App.ProjectLogoUrl;
        try
        {
            Assets assets = imageUrl.Length > 0
                ? new Assets
                {
                    LargeImageKey = imageUrl,
                    LargeImageText = VoidstrapPresence.Clip(context!.ImageText.Length > 0 ? context.ImageText : details, 128),
                    SmallImageKey = VoidstrapLogo,
                    SmallImageText = versionText
                }
                : new Assets
                {
                    LargeImageKey = VoidstrapLogo,
                    LargeImageText = versionText,
                    SmallImageKey = _voidRpcSmallImageUrl,
                    SmallImageText = string.IsNullOrEmpty(_voidRpcSmallImageUrl) ? string.Empty : (_voidRpcSmallImageText.Length > 0 ? _voidRpcSmallImageText : "Roblox")
                };
            DiscordRPC.Button[] buttons = buttonUrl.Length > 0
                ? new DiscordRPC.Button[2]
                {
                    new DiscordRPC.Button
                    {
                        Label = VoidstrapPresence.Clip(context!.ButtonLabel.Length > 0 ? context.ButtonLabel : "Open", 32),
                        Url = buttonUrl
                    },
                    new DiscordRPC.Button
                    {
                        Label = "Get Voidstrap",
                        Url = App.ProjectDownloadLink
                    }
                }
                : new DiscordRPC.Button[2]
                {
                    new DiscordRPC.Button
                    {
                        Label = "Discord",
                        Url = "https://discord.gg/bzdbHHytFR"
                    },
                    new DiscordRPC.Button
                    {
                        Label = "Github",
                        Url = Voidstrap.Utility.GitHubCache.PreferredRepository
                    }
                };
            _discordClient.SetPresenceSafe(new DiscordRPC.RichPresence
            {
                Details = details,
                State = state,
                Timestamps = new Timestamps(_voidRpcSessionStart),
                Assets = assets,
                Buttons = buttons
            });
            _lastVoidRpcDetails = details;
            _lastVoidRpcState = state;
            _lastVoidRpcExtra = extra;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("DiscordRPC", "SetPresence failed: " + ex.Message);
        }
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        InitializeNavigation();
        PopulateTopSearch();
        ApplyUiZoom();
        LoadSidebarWidth();
        ApplyLinuxWindowSize();
        SetupNavShortcuts();
        _ = Dispatcher.BeginInvoke(new Action(ResetNavigationHistory), DispatcherPriority.ApplicationIdle);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Voidstrap.Utility.AppNotifications.Changed -= OnAppNotificationsChanged;
        Voidstrap.Utility.AppNotifications.Changed += OnAppNotificationsChanged;
        Voidstrap.Utility.AppNotifications.Reload();
        ApplyNotificationUnread(Voidstrap.Utility.AppNotifications.UnreadCount);
        if (App.Settings.Prop.GRADmentFR)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
        if (App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw)
        {
            SnowCanvas?.SetActive(IsActive);
        }
        else if (SnowCanvas != null)
        {
            SnowCanvas.SetActive(false);
        }
        await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate
        {
        }, (DispatcherPriority)6);
        PlayIntro();
        _ = AutoCheckForUpdatesAsync();
    }

    private bool _navShortcutsReady;

    private bool _sidebarResizing;

    private double _resizeStartX;

    private double _resizeStartWidth;

    private const double SidebarMinWidth = 64.0;

    private const double SidebarMaxWidth = 320.0;

    private const double SidebarDefaultWidth = 225.0;

    private const double SidebarIconThreshold = 150.0;

    private const double SidebarSnapTolerance = 12.0;

    private readonly Dictionary<Wpf.Ui.Controls.NavigationItem, Thickness> _navItemMargins = new Dictionary<Wpf.Ui.Controls.NavigationItem, Thickness>();

    private bool? _sidebarIconMode;

    private double _sidebarExpandedWidth = SidebarDefaultWidth;

    private const double SidebarCollapsedWidth = 72.0;

    private static readonly (Type Page, Key Key, string Label)[] NavShortcuts = new (Type, Key, string)[]
    {
        (typeof(HomePage), Key.D1, "Ctrl+1"),
        (typeof(IntegrationsPage), Key.D2, "Ctrl+2"),
        (typeof(BehaviourPage), Key.D3, "Ctrl+3"),
        (typeof(FastFlagsPage), Key.D5, "Ctrl+5"),
        (typeof(FastFlagEditorPage), Key.D6, "Ctrl+6"),
        (typeof(GBSEditorPage), Key.D7, "Ctrl+7"),
        (typeof(ModsPage), Key.D8, "Ctrl+8"),
        (typeof(NewsPage), Key.D9, "Ctrl+9"),
        (typeof(DownloadsPage), Key.D0, "Ctrl+0"),
        (typeof(ExtensionPage), Key.E, "Ctrl+E"),
        (typeof(ShortcutsPage), Key.U, "Ctrl+U"),
        (typeof(ChannelPage), Key.OemComma, "Ctrl+,")
    };

    private void SetupNavShortcuts()
    {
        if (_navShortcutsReady)
        {
            return;
        }
        _navShortcutsReady = true;
        ApplyNavToolTips(RootNavigation.Items);
        ApplyNavToolTips(RootNavigation.Footer);
        PreviewKeyDown += MainWindow_PreviewKeyDown;
    }

    private static void ApplyNavToolTips(System.Collections.IEnumerable items)
    {
        if (items == null)
        {
            return;
        }
        foreach (object obj in items)
        {
            if (!(obj is Wpf.Ui.Controls.NavigationItem item) || item.PageType == null)
            {
                continue;
            }
            foreach (var shortcut in NavShortcuts)
            {
                if (shortcut.Page != item.PageType)
                {
                    continue;
                }
                item.ToolTip = new System.Windows.Controls.ToolTip
                {
                    Content = BuildShortcutToolTip(item.Content?.ToString() ?? "", shortcut.Label),
                    Placement = PlacementMode.Right,
                    HorizontalOffset = 8,
                    VerticalOffset = 0
                };
                ToolTipService.SetInitialShowDelay(item, 350);
                ToolTipService.SetBetweenShowDelay(item, 60);
                ToolTipService.SetPlacement(item, PlacementMode.Right);
                break;
            }
        }
    }

    private static StackPanel BuildShortcutToolTip(string title, string shortcut)
    {
        StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            VerticalAlignment = VerticalAlignment.Center
        });
        Border keycap = new Border
        {
            Margin = new Thickness(10.0, 0.0, 0.0, 0.0),
            Padding = new Thickness(5.0, 1.0, 5.0, 1.0),
            CornerRadius = new CornerRadius(4.0),
            BorderThickness = new Thickness(1.0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = shortcut,
                FontSize = 11,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        keycap.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        keycap.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
        panel.Children.Add(keycap);
        return panel;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _launchTargetOverlayOpen)
        {
            CloseLaunchTargetOverlay();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && CommandPalettePopup.IsOpen)
        {
            CloseCommandPalette();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleCommandPalette();
            e.Handled = true;
            return;
        }
        if (CommandPalettePopup.IsOpen)
        {
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }
        foreach (var shortcut in NavShortcuts)
        {
            if (e.Key != shortcut.Key)
            {
                continue;
            }
            if (!IsNavigablePage(shortcut.Page))
            {
                return;
            }
            RootNavigation.Navigate(shortcut.Page);
            e.Handled = true;
            return;
        }
    }

    private bool IsNavigablePage(Type pageType)
    {
        foreach (NavigationItem item in RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>()))
        {
            if (item.PageType != pageType)
            {
                continue;
            }
            return item.IsEnabled && item.Visibility == Visibility.Visible;
        }
        return false;
    }

    private static readonly int[] ZoomSteps = new int[] { 50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200 };

    private DispatcherTimer? _zoomIndicatorTimer;

    private EventHandler? _zoomIndicatorTick;

    private void OnZoomWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control || e.Delta == 0)
        {
            return;
        }

        StepZoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void StepZoom(int direction)
    {
        int current = App.Settings.Prop.UiZoomPercent;
        int index = Array.IndexOf(ZoomSteps, current);
        if (index < 0)
        {
            index = 0;
            for (int i = 0; i < ZoomSteps.Length; i++)
            {
                if (ZoomSteps[i] <= current)
                {
                    index = i;
                }
            }
        }

        int next = Math.Max(0, Math.Min(ZoomSteps.Length - 1, index + direction));
        SetZoom(ZoomSteps[next]);
    }

    private void SetZoom(int percent)
    {
        if (App.Settings.Prop.UiZoomPercent != percent)
        {
            App.Settings.Prop.UiZoomPercent = percent;
            App.Settings.Save();
            ApplyUiZoomToOpenWindows();
        }

        ShowZoomIndicator(percent);
    }

    private void ShowZoomIndicator(int percent)
    {
        if (ZoomIndicator == null || ZoomIndicatorText == null)
        {
            return;
        }

        ZoomIndicatorText.Text = percent.ToString() + "%";
        ZoomIndicator.Visibility = Visibility.Visible;

        if (_zoomIndicatorTimer == null)
        {
            _zoomIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _zoomIndicatorTick = OnZoomIndicatorTick;
            _zoomIndicatorTimer.Tick += _zoomIndicatorTick;
        }

        _zoomIndicatorTimer.Stop();
        _zoomIndicatorTimer.Start();
    }

    private void OnZoomIndicatorTick(object? sender, EventArgs e)
    {
        _zoomIndicatorTimer?.Stop();
        if (ZoomIndicator != null)
        {
            ZoomIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void ReleaseZoomIndicator()
    {
        if (_zoomIndicatorTimer == null)
        {
            return;
        }

        _zoomIndicatorTimer.Stop();
        if (_zoomIndicatorTick != null)
        {
            _zoomIndicatorTimer.Tick -= _zoomIndicatorTick;
            _zoomIndicatorTick = null;
        }

        _zoomIndicatorTimer = null;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => StepZoom(1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(100);

    public void ApplyUiZoom()
    {
        int percent = App.Settings.Prop.UiZoomPercent;
        if (percent < 50)
        {
            percent = 50;
        }
        if (percent > 200)
        {
            percent = 200;
        }

        double userScale = percent / 100.0;
        double currentW = ActualWidth > 0 ? ActualWidth : Width;
        double currentH = ActualHeight > 0 ? ActualHeight : Height;
        double targetW = 1071.0;
        double targetH = 690.0;

        double widthScale = currentW > 0 && currentW < targetW ? (currentW / targetW) : 1.0;
        double heightScale = currentH > 0 && currentH < targetH ? (currentH / targetH) : 1.0;
        double autoFitScale = Math.Min(widthScale, heightScale);
        if (autoFitScale < 0.4)
        {
            autoFitScale = 0.4;
        }

        double finalScale = userScale * autoFitScale;
        if (Math.Abs(finalScale - _appliedUiZoom) < 0.005)
        {
            return;
        }
        _appliedUiZoom = finalScale;

        if (Math.Abs(finalScale - 1.0) < 0.005)
        {
            if (!ReferenceEquals(RootFrame.LayoutTransform, Transform.Identity))
            {
                RootFrame.LayoutTransform = Transform.Identity;
            }
            return;
        }

        if (RootFrame.LayoutTransform is ScaleTransform applied && !applied.IsFrozen)
        {
            applied.ScaleX = finalScale;
            applied.ScaleY = finalScale;
            return;
        }

        RootFrame.LayoutTransform = new ScaleTransform(finalScale, finalScale);
    }

    private void QueueUiZoom()
    {
        if (_uiZoomQueued || _isClosed || Dispatcher.HasShutdownStarted)
        {
            return;
        }
        _uiZoomQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)delegate
        {
            _uiZoomQueued = false;
            if (!_isClosed)
            {
                ApplyUiZoom();
            }
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            Dispatcher.BeginInvoke(FitToScreen, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::OnDisplaySettingsChanged", ex);
        }
    }

    private void FitToScreen()
    {
        try
        {
            if (WindowState == System.Windows.WindowState.Maximized || Voidstrap.UI.LinuxWindowMode.IsFullscreen(this))
                return;

            System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            double dpiScale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double screenW = screen.WorkingArea.Width / dpiScale;
            double screenH = screen.WorkingArea.Height / dpiScale;
            double screenL = screen.WorkingArea.Left / dpiScale;
            double screenT = screen.WorkingArea.Top / dpiScale;

            double minW = MinWidth > 0 ? MinWidth : 640;
            double minH = MinHeight > 0 ? MinHeight : 440;

            double newW = Math.Max(minW, Math.Min(1071.0, screenW * 0.95));
            double newH = Math.Max(minH, Math.Min(690.0, screenH * 0.95));

            Width = newW;
            Height = newH;

            Left = screenL + (screenW - newW) / 2.0;
            Top = screenT + (screenH - newH) / 2.0;

            ApplyUiZoom();
        }
        catch
        {
        }
    }

    public static void ApplyUiZoomToOpenWindows()
    {
        Application app = Application.Current;
        if (app == null)
        {
            return;
        }
        foreach (Window window in app.Windows)
        {
            if (window is MainWindow mainWindow)
            {
                mainWindow.ApplyUiZoom();
            }
        }
    }

    private void LoadSidebarWidth()
    {
        ApplySidebarWidth(App.Settings.Prop.SidebarWidth);
    }

    private static double ClampSidebarWidth(double w)
    {
        if (double.IsNaN(w) || w <= 0.0)
        {
            return SidebarDefaultWidth;
        }
        return Math.Max(SidebarMinWidth, Math.Min(w, SidebarMaxWidth));
    }

    private void ApplySidebarWidth(double w)
    {
        if (RootNavigation == null)
        {
            return;
        }
        w = ClampSidebarWidth(w);
        if (Math.Abs(w - SidebarDefaultWidth) <= SidebarSnapTolerance)
        {
            w = SidebarDefaultWidth;
        }
        RootNavigation.Width = w;
        bool iconsOnly = w < SidebarIconThreshold;
        if (!iconsOnly)
            _sidebarExpandedWidth = w;
        RootNavigation.Tag = (iconsOnly ? "icons" : "full");
        double font = (iconsOnly ? 11.0 : 11.0 + (w - SidebarIconThreshold) * 4.0 / (SidebarMaxWidth - SidebarIconThreshold));
        ApplyNavFontSize(RootNavigation.Items, font);
        ApplyNavFontSize(RootNavigation.Footer, font);
        if (_sidebarIconMode != iconsOnly)
        {
            _sidebarIconMode = iconsOnly;
            ApplyIconModeLayout(RootNavigation.Items, iconsOnly);
            ApplyIconModeLayout(RootNavigation.Footer, iconsOnly);
            SidebarGroup.SetIconOnly(MoreNavItem, iconsOnly);
            if (iconsOnly)
                SidebarGroup.Expand(MoreNavItem, false, animate: false);
        }
    }

    private void ApplyIconModeLayout(System.Collections.IEnumerable items, bool iconsOnly)
    {
        if (items == null)
        {
            return;
        }
        foreach (object obj in items)
        {
            if (obj is not Wpf.Ui.Controls.NavigationItem item || SidebarGroup.GetIsChild(item))
            {
                continue;
            }
            if (!_navItemMargins.TryGetValue(item, out var original))
            {
                original = item.Margin;
                _navItemMargins[item] = original;
            }
            bool iconless = item.Icon == Wpf.Ui.Common.SymbolRegular.Empty && item.Image == null;
            if (iconsOnly)
            {
                item.Margin = new Thickness(0.0, 0.0, 0.0, 4.0);
                if (iconless)
                {
                    item.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                item.Margin = original;
                if (iconless)
                {
                    item.Visibility = Visibility.Visible;
                }
            }
        }
    }

    private static void ApplyNavFontSize(System.Collections.IEnumerable items, double font)
    {
        if (items == null)
        {
            return;
        }
        foreach (object obj in items)
        {
            if (obj is Wpf.Ui.Controls.NavigationItem item)
            {
                item.FontSize = font;
            }
        }
    }

    private void SaveSidebarWidth()
    {
        App.Settings.Prop.SidebarWidth = ((RootNavigation != null) ? RootNavigation.Width : SidebarDefaultWidth);
        App.Settings.Save();
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is LibraryPage libraryPage)
        {
            libraryPage.ToggleSidebar();
            return;
        }
        if (RootNavigation == null)
            return;
        if (_sidebarResizing)
        {
            _sidebarResizing = false;
            try
            {
                SidebarResizer.ReleaseMouseCapture();
            }
            catch
            {
            }
        }
        RootNavigation.BeginAnimation(WidthProperty, null);
        double from = double.IsNaN(RootNavigation.Width) ? SidebarDefaultWidth : RootNavigation.Width;
        double to = _sidebarIconMode == true ? Math.Max(SidebarIconThreshold, _sidebarExpandedWidth) : SidebarCollapsedWidth;
        if (Math.Abs(to - from) > 1.0)
        {
            DoubleAnimation slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            slide.Completed += SidebarToggleAnimation_Completed;
            RootNavigation.Tag = "full";
            RootNavigation.BeginAnimation(WidthProperty, slide);
        }
        else
        {
            ApplySidebarWidth(to);
            SaveSidebarWidth();
        }
    }

    private void SidebarToggleAnimation_Completed(object? sender, EventArgs e)
    {
        if (RootNavigation == null)
            return;
        ApplySidebarWidth(_sidebarIconMode == true ? Math.Max(SidebarIconThreshold, _sidebarExpandedWidth) : SidebarCollapsedWidth);
        SaveSidebarWidth();
    }

    private void SidebarResizer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RootNavigation?.BeginAnimation(WidthProperty, null);
        if (e.ClickCount > 1)
        {
            _sidebarResizing = false;
            try
            {
                SidebarResizer.ReleaseMouseCapture();
            }
            catch
            {
            }
            ApplySidebarWidth(SidebarDefaultWidth);
            SaveSidebarWidth();
            e.Handled = true;
            return;
        }
        _sidebarResizing = true;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = ((RootNavigation != null && !double.IsNaN(RootNavigation.Width)) ? RootNavigation.Width : SidebarDefaultWidth);
        SidebarResizer.CaptureMouse();
        e.Handled = true;
    }

    private void SidebarResizer_MouseMove(object sender, MouseEventArgs e)
    {
        if (_sidebarResizing)
        {
            double dx = e.GetPosition(this).X - _resizeStartX;
            ApplySidebarWidth(_resizeStartWidth + dx);
        }
    }

    private void SidebarResizer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_sidebarResizing)
        {
            _sidebarResizing = false;
            try
            {
                SidebarResizer.ReleaseMouseCapture();
            }
            catch
            {
            }
            SaveSidebarWidth();
        }
    }

    private void PlayIntro()
    {
        if (_introPlayed)
        {
            return;
        }
        _introPlayed = true;
        Storyboard? storyboard = TryFindResource("IntroStoryboard") as Storyboard;
        if (storyboard == null)
        {
            if (IntroOverlay != null)
            {
                IntroOverlay.Visibility = Visibility.Collapsed;
            }
            LiftTopNav();
            StartPageWarmup();
            return;
        }
        EventHandler? onCompleted = null;
        onCompleted = delegate
        {
            storyboard.Completed -= onCompleted;
            FinishIntro(storyboard);
        };
        storyboard.Completed += onCompleted;
        IntroOverlay.Visibility = Visibility.Visible;
        Wpf.Ui.Animations.RenderReady.Run(this, delegate
        {
            if (_isClosed || _introFinished)
            {
                return;
            }
            if (IntroContent != null && !Voidstrap.Utility.Platform.IsLinux)
            {
                IntroContent.CacheMode = new System.Windows.Media.BitmapCache();
                _introCacheTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(1.0)
                };
                _introCacheTimer.Tick += IntroCacheTimer_Tick;
                _introCacheTimer.Start();
            }
            StartIntroWatchdog(storyboard);
            Wpf.Ui.Animations.RenderReady.Hold(this, IntroDuration);
            storyboard.Begin(IntroOverlay, isControllable: true);
            App.Logger?.WriteLine("MainWindow::PlayIntro", "Intro storyboard started");
        });
    }

    private void StartIntroWatchdog(Storyboard storyboard)
    {
        StopIntroWatchdog();
        _introWatchdogStoryboard = storyboard;
        _introWatchdog = new DispatcherTimer
        {
            Interval = IntroDuration + TimeSpan.FromSeconds(1.5)
        };
        _introWatchdog.Tick += IntroWatchdog_Tick;
        _introWatchdog.Start();
    }

    private void StopIntroWatchdog()
    {
        if (_introWatchdog != null)
        {
            _introWatchdog.Stop();
            _introWatchdog.Tick -= IntroWatchdog_Tick;
            _introWatchdog = null;
        }
        _introWatchdogStoryboard = null;
    }

    private void IntroWatchdog_Tick(object? sender, EventArgs e)
    {
        Storyboard? storyboard = _introWatchdogStoryboard;
        StopIntroWatchdog();
        if (!_introFinished)
        {
            App.Logger?.WriteLine("MainWindow::PlayIntro", "Intro watchdog closed the overlay");
        }
        FinishIntro(storyboard);
    }

    private void FinishIntro(Storyboard? storyboard)
    {
        if (_introFinished)
        {
            return;
        }
        _introFinished = true;
        App.Logger?.WriteLine("MainWindow::PlayIntro", "Intro finished");
        StopIntroWatchdog();
        StopIntroCacheTimer();
        if (IntroContent != null)
        {
            IntroContent.CacheMode = null;
        }
        try
        {
            storyboard?.Remove(IntroOverlay);
        }
        catch
        {
        }
        if (IntroOverlay != null)
        {
            IntroOverlay.Visibility = Visibility.Collapsed;
        }
        LiftTopNav();
        StartPageWarmup();
    }

    private void LiftTopNav()
    {
        if (TopNavPanel != null)
            System.Windows.Controls.Panel.SetZIndex(TopNavPanel, 1001);
    }

    private void IntroCacheTimer_Tick(object? sender, EventArgs e)
    {
        StopIntroCacheTimer();
        if (IntroContent != null)
        {
            IntroContent.CacheMode = null;
        }
    }

    private void StopIntroCacheTimer()
    {
        if (_introCacheTimer != null)
        {
            _introCacheTimer.Stop();
            _introCacheTimer.Tick -= IntroCacheTimer_Tick;
            _introCacheTimer = null;
        }
    }

    private long _overlayPopupClosedTicks;
    private Popup? _lastClosedOverlayPopup;

    private bool JustClosedOverlayPopup(Popup popup)
    {
        long elapsed = Environment.TickCount64 - _overlayPopupClosedTicks;
        return ReferenceEquals(_lastClosedOverlayPopup, popup) && elapsed >= 0 && elapsed < 250;
    }

    private void AppMenuPopup_Closing(object? sender, EventArgs e)
    {
        StopLogsSubmenuCloseTimer();
        AppMenuLogsItem.IsSubmenuOpen = false;
    }

    private void OverlayPopup_Closed(object? sender, EventArgs e)
    {
        _lastClosedOverlayPopup = sender as Popup;
        _overlayPopupClosedTicks = Environment.TickCount64;
        if (ReferenceEquals(sender, AppMenuPopup))
        {
            StopLogsSubmenuCloseTimer();
            AppMenuLogsItem.IsSubmenuOpen = false;
        }
        else if (ReferenceEquals(sender, CommandPalettePopup))
        {
            CommandPaletteSearchBox.Text = "";
            _commandPaletteRows.Clear();
            _commandPaletteSelected = -1;
        }
        ReleaseOrphanedCapture();
    }

    private void CloseOverlayPopups()
    {
        CloseTopBarMenus();
        CloseCommandPalette();
        CloseLaunchTargetOverlay();
        ReleaseOrphanedCapture();
    }

    private void CloseTopBarMenus()
    {
        if (AppMenuPopup != null)
        {
            AppMenuPopup.IsOpen = false;
        }
        if (NotificationPopup != null)
        {
            NotificationPopup.IsOpen = false;
        }
    }

    private void ReleaseOrphanedCapture()
    {
        IInputElement? captured = Mouse.Captured;
        if (captured == null)
        {
            return;
        }
        if (_launchTargetOverlayOpen || CommandPalettePopup?.IsOpen == true || AppMenuPopup?.IsOpen == true || NotificationPopup?.IsOpen == true)
        {
            return;
        }
        if (captured is DependencyObject element && Window.GetWindow(element) == this)
        {
            return;
        }
        Mouse.Capture(null);
    }

    private void NotificationsButton_Click(object sender, RoutedEventArgs e)
    {
        bool wasOpen = NotificationPopup.IsOpen && !NotificationPopup.IsClosing;
        CloseTopBarMenus();
        if (wasOpen)
        {
            return;
        }
        NotificationPopup.IsOpen = true;
        LoadTopBarNotifications();
    }

    private void NotificationPopup_Closed(object? sender, EventArgs e)
    {
        ReleaseOrphanedCapture();
    }

    private void LoadTopBarNotifications()
    {
        Voidstrap.Utility.AppNotifications.Reload();
        List<TopBarNotificationItem> items = Voidstrap.Utility.AppNotifications.Items.Select(TopBarNotificationItem.From).ToList();
        NotificationList.ItemsSource = items;
        bool any = items.Count > 0;
        NotificationEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        NotificationScroll.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NotificationMarkAllButton.IsEnabled = items.Exists(item => item.IsUnread);
        NotificationClearButton.IsEnabled = any;
        ApplyNotificationUnread(Voidstrap.Utility.AppNotifications.UnreadCount);
    }

    private void NotificationMarkAll_Click(object sender, RoutedEventArgs e)
    {
        Voidstrap.Utility.AppNotifications.MarkAllRead();
        LoadTopBarNotifications();
    }

    private void NotificationClear_Click(object sender, RoutedEventArgs e)
    {
        Voidstrap.Utility.AppNotifications.Clear();
        LoadTopBarNotifications();
    }

    private void NotificationItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: TopBarNotificationItem item })
        {
            return;
        }
        if (item.IsUnread)
        {
            Voidstrap.Utility.AppNotifications.MarkRead(item.Id);
        }
        if (!string.IsNullOrEmpty(item.LogPath) && File.Exists(item.LogPath))
        {
            NotificationPopup.IsOpen = false;
            Utilities.ShellExecute(item.LogPath);
            return;
        }
        LoadTopBarNotifications();
    }

    private void OnAppNotificationsChanged()
    {
        if (_isClosed)
            return;
        try
        {
            Dispatcher.BeginInvoke(new Action(RefreshNotificationBadge));
        }
        catch
        {
        }
    }

    private void RefreshNotificationBadge()
    {
        if (_isClosed)
            return;
        if (NotificationPopup.IsOpen)
        {
            LoadTopBarNotifications();
            return;
        }
        ApplyNotificationUnread(Voidstrap.Utility.AppNotifications.UnreadCount);
    }

    private void ApplyNotificationUnread(int unread)
    {
        if (_isClosed)
            return;
        unread = Math.Clamp(unread, 0, 100);
        bool changed = _notificationUnread != unread;
        _notificationUnread = unread;
        NotificationsBadgeText.Text = unread > 99 ? "99+" : unread.ToString();
        NotificationsBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (unread > 0 && changed)
        {
            CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Duration duration = new Duration(TimeSpan.FromMilliseconds(180));
            NotificationsBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.72, 1.0, duration) { EasingFunction = ease });
            NotificationsBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.72, 1.0, duration) { EasingFunction = ease });
            NotificationsBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1.0, duration) { EasingFunction = ease });
        }
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        CloseTopBarMenus();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        CloseTopBarMenus();
    }


    private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueUiZoom();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateButtonContent();
        if (App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw)
        {
            SnowCanvas?.SetActive(true);
        }
        try
        {
            _visibilityTimer.Start();
            if (App.Settings.Prop.GRADmentFR)
            {
                CompositionTarget.Rendering -= CompositionTarget_Rendering;
                CompositionTarget.Rendering += CompositionTarget_Rendering;
            }
            if (BackgroundMedia != null && BackgroundMedia.Visibility == Visibility.Visible)
            {
                BackgroundMedia?.Play();
            }
            if (_bgGifPausedByDeactivate)
            {
                _bgGifPausedByDeactivate = false;
                if (BackgroundImage != null)
                {
                    ImageBehavior.GetAnimationController(BackgroundImage)?.Play();
                }
            }
            Voidstrap.Utility.AppNotifications.Reload();
        }
        catch
        {
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        CloseOverlayPopups();
        SnowCanvas?.SetActive(false);
        try
        {
            _visibilityTimer.Stop();
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            if (BackgroundMedia != null && BackgroundMedia.Visibility == Visibility.Visible)
            {
                BackgroundMedia?.Pause();
            }
            if (BackgroundImage != null && BackgroundImage.Visibility == Visibility.Visible)
            {
                ImageAnimationController controller = ImageBehavior.GetAnimationController(BackgroundImage);
                if (controller != null && !controller.IsPaused && !controller.IsComplete)
                {
                    controller.Pause();
                    _bgGifPausedByDeactivate = true;
                }
            }
        }
        catch
        {
        }
    }

    private void OnNavigationFailed(object? sender, Wpf.Ui.Controls.Navigation.NavigationFailedEventArgs e)
    {
        Exception root = e.Exception;
        while (root.InnerException != null)
        {
            root = root.InnerException;
        }
        App.Logger.WriteException("MainWindow::OnNavigationFailed", root);
        Frontend.ShowMessageBox($"The {e.PageTag} page could not be opened on this system.\n\n{root.GetType().Name}: {root.Message.Split('\n')[0]}", MessageBoxImage.Warning);
    }

    private void InitializeViewModel()
    {
        if (RootNavigation != null)
        {
            RootNavigation.NavigationFailed -= OnNavigationFailed;
            RootNavigation.NavigationFailed += OnNavigationFailed;
        }
        _declaredWidth = base.Width;
        _declaredHeight = base.Height;
        MainWindowViewModel mainWindowViewModel = (MainWindowViewModel)(base.DataContext = new MainWindowViewModel());
        mainWindowViewModel.RequestSaveNoticeEvent = (EventHandler)Delegate.Combine(mainWindowViewModel.RequestSaveNoticeEvent, new EventHandler(OnRequestSaveNotice));
        mainWindowViewModel.RequestSaveLaunchNoticeEvent = (EventHandler)Delegate.Combine(mainWindowViewModel.RequestSaveLaunchNoticeEvent, new EventHandler(OnRequestSaveLaunchNotice));
        mainWindowViewModel.RequestCloseWindowEvent = (EventHandler)Delegate.Combine(mainWindowViewModel.RequestCloseWindowEvent, new EventHandler(OnRequestCloseWindow));
    }

    private void UpdateButtonContent()
    {
        if (InstallLaunchButton == null)
        {
            return;
        }
        string content;
        if (base.DataContext is MainWindowViewModel clientVm && !string.IsNullOrEmpty(clientVm.SelectedLaunchClient) && Voidstrap.Utility.ClassicClients.IsClientInstalled(clientVm.SelectedLaunchClient))
        {
            content = "Save and Launch";
        }
        else
        {
            bool studio = base.DataContext is MainWindowViewModel mainWindowViewModel && mainWindowViewModel.SelectedLaunchModeIndex == 1;
            bool installed = IsLaunchTargetInstalled(studio);
            if (!Voidstrap.Utility.Platform.SupportsWindowsClient)
            {
                string runtime = studio ? "Vinegar" : "Sober";
                content = installed ? "Save and Launch " + runtime : "Install " + runtime;
            }
            else
            {
                content = (studio ? (installed ? "Save and Launch Studio" : "Install Studio") : (installed ? "Save and Launch" : "Install"));
            }
        }
        if (!object.Equals(InstallLaunchButton.Content, content))
        {
            InstallLaunchButton.Content = content;
        }
    }

    private static bool IsLaunchTargetInstalled(bool studio)
    {
        try
        {
            if (!Voidstrap.Utility.Platform.SupportsWindowsClient)
            {
                return studio
                    ? Voidstrap.Platform.Linux.LinuxVinegarStudioRuntimeProvider.IsInstalled()
                    : Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.IsInstalled();
            }

            Voidstrap.AppData.IAppData appData = (studio ? ((Voidstrap.AppData.IAppData)new Voidstrap.AppData.RobloxStudioData()) : ((Voidstrap.AppData.IAppData)new Voidstrap.AppData.RobloxPlayerData()));
            if (!string.IsNullOrEmpty(appData.State.VersionGuid) && (File.Exists(appData.ExecutablePath) || Voidstrap.Utility.RobloxInstallCompression.IsCompressed(appData)))
            {
                return true;
            }
            string versionsRoot = appData.VersionsRoot;
            if (Directory.Exists(versionsRoot))
            {
                foreach (string item in Directory.EnumerateDirectories(versionsRoot, "version-*"))
                {
                    if (File.Exists(System.IO.Path.Combine(item, appData.ExecutableName)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private void InitializeWindowState()
    {
        if (_state.LeftUpdateV2 > SystemParameters.VirtualScreenWidth || _state.TopUpdateV2 > SystemParameters.VirtualScreenHeight)
        {
            _state.LeftUpdateV2 = 0.0;
            _state.TopUpdateV2 = 0.0;
        }
        if (_state.WidthUpdateV2 > 0.0)
        {
            base.Width = _state.WidthUpdateV2;
        }
        if (_state.HeightUpdateV2 > 0.0)
        {
            base.Height = _state.HeightUpdateV2;
        }
        if (_state.LeftUpdateV2 > 0.0 && _state.TopUpdateV2 > 0.0)
        {
            base.WindowStartupLocation = WindowStartupLocation.Manual;
            base.Left = _state.LeftUpdateV2;
            base.Top = _state.TopUpdateV2;
        }
        if (_state.MaximizedUpdateV2)
        {
            base.WindowState = System.Windows.WindowState.Maximized;
        }

    }

    private void InitializeNavigation()
    {
        if (_navigationInitialized || RootNavigation == null)
        {
            return;
        }
        _navigationInitialized = true;
        if (App.State.Prop.SidebarLayoutVersion < 1)
        {
            App.State.Prop.LastPage = App.State.Prop.LastPage switch
            {
                4 => 10,
                6 => 5,
                7 => 6,
                8 => 9,
                9 => 7,
                int page => page
            };
            App.State.Prop.SidebarLayoutVersion = 1;
            App.State.SaveDeferred();
        }
        if (App.State.Prop.SidebarLayoutVersion < 2)
        {
            if (App.State.Prop.LastPage >= 4)
            {
                App.State.Prop.LastPage++;
            }
            App.State.Prop.SidebarLayoutVersion = 2;
            App.State.SaveDeferred();
        }
        int lastPage = App.State.Prop.LastPage;
        int selectedPageIndex = ResolveSafeNavigationIndex(lastPage);
        RootNavigation.SelectedPageIndex = selectedPageIndex;
        RootNavigation.Navigated += SaveNavigation;
    }

    private int ResolveSafeNavigationIndex(int requested)
    {
        if (RootNavigation == null)
        {
            return 0;
        }
        NavigationItem[] items = GetNavigationItemsInServiceOrder();
        if (IsUsable(requested))
        {
            return requested;
        }
        for (int i = 0; i < items.Length; i++)
        {
            if (IsUsable(i))
            {
                return i;
            }
        }
        return 0;
		bool IsUsable(int num)
		{
            if (num < 0 || num >= items.Length)
            {
                return false;
            }
            NavigationItem navigationItem = items[num];
			if (!navigationItem.IsEnabled)
			{
				return false;
			}
			if (navigationItem.Visibility != Visibility.Visible)
			{
				return false;
			}
            if ((object)navigationItem.PageType == null)
            {
                return false;
            }
            return true;
        }
    }

    private void OnRequestSaveNotice(object? sender, EventArgs e)
    {
        if (!_isSaveAndLaunchClicked)
        {
            SettingsSavedSnackbar.Show();
        }
    }

    private void OnRequestSaveLaunchNotice(object? sender, EventArgs e)
    {
        if (!_isSaveAndLaunchClicked)
        {
            SettingsSavedLaunchSnackbar.Show();
        }
    }

    private void OnSettingChangeFailed(object? sender, SettingChangeFailedEventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnSettingChangeFailed(sender, e)));
            return;
        }

        if (!_isClosed)
        {
            SettingFailureSnackbar.Show("Setting not changed", e.Result.Message, SymbolRegular.ErrorCircle24, ControlAppearance.Danger);
        }
    }

	private void OnRestartRequirementsChanged(object? sender, RestartRequirementsChangedEventArgs e)
	{
		if (Dispatcher.CheckAccess())
		{
			RefreshRestartNotification();
		}
		else if (!_isClosed && !Dispatcher.HasShutdownStarted)
		{
			Dispatcher.BeginInvoke(new Action(RefreshRestartNotification));
		}
	}

	private void RefreshRestartNotification()
	{
		if (_isClosed || RestartNotificationCard == null)
		{
			return;
		}

		RestartRequirement? requirement = RestartNotificationService.Current;
		if (requirement == null)
		{
			HideRestartNotification();
			return;
		}

		_restartNotificationBusy = false;
		RestartNotificationTitle.Text = requirement.Title;
		RestartNotificationMessage.Text = requirement.Message;
		RestartNotificationAction.Content = requirement.ActionText;
		RestartNotificationAction.IsEnabled = true;
		RestartNotificationAction.IsHitTestVisible = true;
		RestartNotificationDismiss.IsHitTestVisible = true;
		RestartNotificationCard.IsHitTestVisible = true;
		RestartNotificationCard.Visibility = Visibility.Visible;
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			PrepareLinuxRestartNotificationInput();
		}
		RestartNotificationCard.Opacity = 1.0;
		RestartNotificationTranslate.X = 0.0;
		Wpf.Ui.Animations.RenderReady.Hold(this, TimeSpan.FromMilliseconds(260.0));
		RestartNotificationCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		});
		RestartNotificationTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28.0, 0.0, TimeSpan.FromMilliseconds(220))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
			FillBehavior = FillBehavior.Stop
		});
	}

	private void PrepareLinuxRestartNotificationInput()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || RestartNotificationCard == null)
		{
			return;
		}

		RestartNotificationTranslate.BeginAnimation(TranslateTransform.XProperty, null);
		RestartNotificationTranslate.X = 0.0;
		Panel.SetZIndex(RestartNotificationCard, 32000);
		if (RestartNotificationCard.Parent is Panel parent
			&& parent.Children.Count > 0
			&& !ReferenceEquals(parent.Children[parent.Children.Count - 1], RestartNotificationCard))
		{
			parent.Children.Remove(RestartNotificationCard);
			parent.Children.Add(RestartNotificationCard);
		}
	}

	private void HideRestartNotification()
	{
		if (RestartNotificationCard == null)
		{
			return;
		}

		RestartNotificationCard.BeginAnimation(OpacityProperty, null);
		RestartNotificationTranslate?.BeginAnimation(TranslateTransform.XProperty, null);
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			RestartNotificationCard.IsHitTestVisible = false;
		}
		RestartNotificationCard.Opacity = 0.0;
		RestartNotificationCard.Visibility = Visibility.Collapsed;
	}

	private void RestartNotificationDismiss_Click(object sender, RoutedEventArgs e)
	{
		HideRestartNotification();
	}

	private async void RestartNotificationAction_Click(object sender, RoutedEventArgs e)
	{
		if (_restartNotificationBusy)
		{
			return;
		}

		RestartRequirement? requirement = RestartNotificationService.Current;
		if (requirement == null)
		{
			HideRestartNotification();
			return;
		}

		try
		{
			requirement.Apply?.Invoke();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("MainWindow::RestartNotification", ex);
			ShowRestartNotificationFailure("Settings could not be applied. Check the logs and try again.");
			return;
		}

		if (DataContext is MainWindowViewModel viewModel && !await viewModel.TrySaveSettingsAsync(false, false))
		{
			ShowRestartNotificationFailure("Settings could not be saved. Check the logs and try again.");
			return;
		}

		_restartNotificationBusy = true;
		RestartNotificationAction.IsEnabled = false;
		RestartNotificationAction.Content = "Restarting";

		try
		{
			App.Settings.FlushDeferred();
			App.State.FlushDeferred();
			App.FastFlags.FlushDeferred();

			switch (requirement.Target)
			{
				case RestartTarget.RobloxPlayer:
					RestartNotificationService.ClearAll();
					LaunchHandler.LaunchRoblox(LaunchMode.Player);
					break;
				case RestartTarget.RobloxStudio:
					RestartNotificationService.ClearAll();
					LaunchHandler.LaunchRoblox(LaunchMode.Studio);
					break;
				default:
                    RestartVoidstrapFromSettings();
					break;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("MainWindow::RestartNotification", ex);
			ShowRestartNotificationFailure("Voidstrap could not restart. Check the logs and try again.");
		}
	}

	private static void RestartVoidstrapFromSettings()
	{
		App.Logger.WriteLine("MainWindow::RestartNotification", "Restarting Voidstrap from settings");
		RestartNotificationService.ClearAll();
		if (!App.RestartApplication(["-settings", "-elevatedwait", Environment.ProcessId.ToString()], closeRuntime: false))
		{
			throw new InvalidOperationException("The Voidstrap restart process did not start");
		}
	}

	private void ShowRestartNotificationFailure(string message)
	{
		_restartNotificationBusy = false;
		RestartNotificationTitle.Text = "Restart failed";
		RestartNotificationMessage.Text = message;
		RestartNotificationAction.Content = RestartNotificationService.Current?.ActionText ?? "Try again";
		RestartNotificationAction.IsEnabled = true;
		RestartNotificationCard.Visibility = Visibility.Visible;
		RestartNotificationCard.Opacity = 1.0;
		RestartNotificationTranslate.X = 0.0;
	}

    private async Task ShowAlreadyRunningSnackbarAsync()
    {
        try
        {
            await Task.Delay(225, _lifetimeCts.Token);
            if (!_isClosed && !((DispatcherObject)this).Dispatcher.HasShutdownStarted)
            {
                _ = ((DispatcherObject)this).Dispatcher.InvokeAsync<bool?>((Func<bool?>)(() => AlreadyRunningSnackbar?.Show()));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void OnRequestCloseWindow(object? sender, EventArgs e)
    {
        await Task.Yield();
        Close();
    }

    private void OnSaveAndLaunchButtonClick(object sender, EventArgs e)
    {
        _isSaveAndLaunchClicked = true;
    }

    private void WpfUiWindow_Closing(object sender, CancelEventArgs e)
    {
        SaveWindowState();
    }

    private void WpfUiWindow_Closed(object sender, EventArgs e)
    {
        if (_isClosed)
            return;
        _isClosed = true;
        StopPageWarmup();
        MainWindowViewModel? closingViewModel = DataContext as MainWindowViewModel;
		Interlocked.Increment(ref _topSearchNavigationGeneration);
        CloseCommandPalette();
        ReleaseZoomIndicator();
        _backgroundGeneration++;
        foreach (TaskCompletionSource<bool> waiter in _backgroundAnimationWaiters.Values)
            waiter.TrySetResult(result: false);
        _backgroundAnimationWaiters.Clear();
        _lifetimeCts.Cancel();
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        PreviewKeyDown -= MainWindow_PreviewKeyDown;
        base.SizeChanged -= MainWindow_SizeChanged;
        base.LocationChanged -= MainWindow_LocationChanged;
        base.StateChanged -= MainWindow_StateChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Activated -= MainWindow_ActivatedRpc;
        Closed -= MainWindow_ClosedRpc;
        Voidstrap.UI.Elements.Settings.Pages.ExtensionViewModel.AnyProgressChanged -= OnExtensionProgressChanged;
        RootFrame.Navigating -= RootFrame_Navigating;
        RootFrame.Navigated -= RootFrame_Navigated;
        Wpf.Ui.Controls.Navigation.NavigationTiming.PageCreated -= OnNavigationPageCreated;
        CommandPalettePopup.Closed -= OverlayPopup_Closed;
        AppMenuPopup.Closing -= AppMenuPopup_Closing;
        AppMenuPopup.Closed -= OverlayPopup_Closed;
        RootNavigation.NavigationFailed -= OnNavigationFailed;
        RootNavigation.Navigated -= SaveNavigation;
        RootNavigation.Navigated -= RootNavigation_RpcNavigated;
        GlobalBackground.Changed -= OnGlobalBackgroundChanged;
        RestartNotificationService.Changed -= OnRestartRequirementsChanged;
        try
        {
            _visibilityTimer.Stop();
            _visibilityTimer.Tick -= VisibilityTimer_Tick;
            if (_logsSubmenuCloseTimer != null)
            {
                _logsSubmenuCloseTimer.Stop();
                _logsSubmenuCloseTimer.Tick -= LogsSubmenuCloseTimer_Tick;
                _logsSubmenuCloseTimer = null;
            }
            SnowCanvas?.Dispose();
            Voidstrap.Utility.AppNotifications.Changed -= OnAppNotificationsChanged;
            StopIntroCacheTimer();
            StopIntroWatchdog();
            DispatcherTimer searchDebounceTimer = _searchDebounceTimer;
            if (searchDebounceTimer != null)
            {
                searchDebounceTimer.Stop();
                searchDebounceTimer.Tick -= SearchDebounceTimer_Tick;
            }
        }
        catch
        {
        }
        ReleaseDiscordRpcClient();
        try
        {
            (TryFindResource("IntroStoryboard") as Storyboard)?.Remove(IntroOverlay);
        }
        catch
        {
        }
        ReleaseBackgroundResources();
        ClearRetainedUiState();
        Voidstrap.Utility.DynamicRenderSystem.ClearCache();
        _lifetimeCts.Dispose();
        if (App.LaunchSettings.TestModeFlag.Active)
        {
            StartTestModeLaunch(closingViewModel);
        }
        else if (!LaunchHandler.PortableSessionActive)
        {
            App.SoftTerminate();
        }
    }

    private async void StartTestModeLaunch(MainWindowViewModel? viewModel)
    {
        if (viewModel == null)
        {
            App.Logger.WriteLine("MainWindow::StartTestModeLaunch", "The settings view model was already released, launching without saving");
            LaunchHandler.LaunchRoblox(LaunchMode.Player);
            return;
        }
        try
        {
            await viewModel.LaunchForTestModeAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::StartTestModeLaunch", ex);
        }
    }

    private double _declaredWidth;

    private double _declaredHeight;

    private void ApplyLinuxWindowSize()
    {
        if (Voidstrap.Utility.Platform.IsWindows || base.WindowState != System.Windows.WindowState.Normal || Voidstrap.UI.LinuxWindowMode.IsFullscreen(this))
        {
            return;
        }

        double width = _state.WidthUpdateV2 > 0.0 ? _state.WidthUpdateV2 : _declaredWidth;
        double height = _state.HeightUpdateV2 > 0.0 ? _state.HeightUpdateV2 : _declaredHeight;
        if (double.IsNaN(width) || double.IsNaN(height) || width < MinWidth || height < MinHeight)
        {
            return;
        }

        Voidstrap.UI.LinuxWindowSize.Apply(Title, (int)Math.Round(width), (int)Math.Round(height));
    }

    private void ReleaseBackgroundResources()
    {
        Voidstrap.UI.Elements.Controls.HomepageMediaPreviewVideo? portable = BackgroundPortableMedia;
        BackgroundPortableMedia = null;
        if (portable != null)
        {
            portable.SourcePath = string.Empty;
            portable.BeginAnimation(UIElement.OpacityProperty, null);
            BackgroundLayer?.Children.Remove(portable);
        }
        MediaElement? media = BackgroundMedia;
        BackgroundMedia = null;
        if (media != null)
        {
            media.MediaEnded -= BackgroundMedia_MediaEnded;
            try
            {
                media.Stop();
            }
            catch
            {
            }
            try
            {
                media.Close();
            }
            catch
            {
            }
            media.Source = null;
            media.BeginAnimation(UIElement.OpacityProperty, null);
            BackgroundLayer?.Children.Remove(media);
        }
        try
        {
            if (BackgroundImage != null)
            {
                ImageBehavior.SetAnimatedSource(BackgroundImage, null);
                BackgroundImage.Source = null;
                BackgroundImage.CacheMode = null;
                BackgroundImage.BeginAnimation(UIElement.OpacityProperty, null);
            }
            GradientLayer?.BeginAnimation(UIElement.OpacityProperty, null);
            if (IntroContent != null)
            {
                IntroContent.CacheMode = null;
            }
        }
        catch
        {
        }
    }

    private void ClearRetainedUiState()
    {
        SettingChangeNotifier.Failed -= OnSettingChangeFailed;
        foreach (NavigationItem item in _defaultIcons.Keys.ToArray())
        {
            item.MouseEnter -= NavigationItem_MouseEnter;
            item.MouseLeave -= NavigationItem_MouseLeave;
        }
        _defaultIcons.Clear();
        _navItemMargins.Clear();
        _navHistoryBack.Clear();
        _navHistoryForward.Clear();
        _navHistoryCurrent = null;
        _pageSearchTargets.Clear();
        _topSearchEntries.Clear();
        _topSearchEntriesList.Clear();
        if (LaunchTargetList is not null)
            LaunchTargetList.ItemsSource = null;
        _libraryPage = null;
        _robloxNewsPage = null;
        _lastPage = null;
        try
        {
            RootFrame.NavigationService?.StopLoading();
            RootFrame.Content = null;
            while (RootFrame.NavigationService?.RemoveBackEntry() != null)
            {
            }
        }
        catch
        {
        }
        if (base.DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RequestSaveNoticeEvent -= OnRequestSaveNotice;
            viewModel.RequestSaveLaunchNoticeEvent -= OnRequestSaveLaunchNotice;
            viewModel.RequestCloseWindowEvent -= OnRequestCloseWindow;
        }
        base.DataContext = null;
    }

    private void SaveWindowState()
    {
        if (Voidstrap.Utility.Platform.IsLinux && Voidstrap.UI.LinuxWindowMode.TryGetRestorePlacement(this, out bool fullscreenMaximized, out Rect fullscreenBounds))
        {
            _state.MaximizedUpdateV2 = fullscreenMaximized;
            _state.WidthUpdateV2 = fullscreenBounds.Width;
            _state.HeightUpdateV2 = fullscreenBounds.Height;
            _state.TopUpdateV2 = fullscreenBounds.Top;
            _state.LeftUpdateV2 = fullscreenBounds.Left;
            App.State.Save();
            return;
        }
        bool maximized = base.WindowState == System.Windows.WindowState.Maximized;
        _state.MaximizedUpdateV2 = maximized;
        if (maximized && !base.RestoreBounds.IsEmpty)
        {
            _state.WidthUpdateV2 = base.RestoreBounds.Width;
            _state.HeightUpdateV2 = base.RestoreBounds.Height;
            _state.TopUpdateV2 = base.RestoreBounds.Top;
            _state.LeftUpdateV2 = base.RestoreBounds.Left;
        }
        else if (!maximized)
        {
            if (Voidstrap.Utility.Platform.IsWindows)
            {
                _state.WidthUpdateV2 = base.Width;
                _state.HeightUpdateV2 = base.Height;
            }
            else if (Voidstrap.UI.LinuxWindowSize.TryGet(Title, out int nativeWidth, out int nativeHeight) && nativeWidth > 0 && nativeHeight > 0)
            {
                _state.WidthUpdateV2 = nativeWidth;
                _state.HeightUpdateV2 = nativeHeight;
            }
            else
            {
                _state.WidthUpdateV2 = base.ActualWidth;
                _state.HeightUpdateV2 = base.ActualHeight;
            }
            _state.TopUpdateV2 = base.Top;
            _state.LeftUpdateV2 = base.Left;
        }
        App.State.Save();
    }

    private void SaveNavigation(INavigation sender, RoutedNavigationEventArgs e)
    {
        SidebarGroup.RefreshActiveChild(MoreNavItem);
        App.State.Prop.LastPage = RootNavigation.SelectedPageIndex;
        UpdateDiscordPresence();
    }

    public Frame GetFrame()
    {
        return RootFrame;
    }

    public INavigation GetNavigation()
    {
        return RootNavigation;
    }

    public bool Navigate(Type pageType)
    {
		if (Voidstrap.Utility.Platform.IsLinux && (pageType == typeof(DownloadsPage) || pageType == typeof(ExtensionPage)))
		{
			return false;
		}

        return RootNavigation.Navigate(pageType);
    }

    public void SetPageService(IPageService pageService)
    {
        RootNavigation.PageService = pageService;
    }

    public void ShowWindow()
    {
        Show();
    }

    public void CloseWindow()
    {
        Close();
    }

    private void NavigationItem_Click(object sender, RoutedEventArgs e)
    {
    }

    private void NavigationItem_Click_1(object sender, RoutedEventArgs e)
    {
    }

    private void Button_Click_1(object sender, RoutedEventArgs e)
    {
    }

    private void Button_Click_2(object sender, RoutedEventArgs e)
    {
    }

    private void LaunchTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_launchTargetOverlayOpen)
        {
            CloseLaunchTargetOverlay();
            return;
        }
        OpenLaunchTargetOverlay();
    }

    private void PopulateLaunchTargets()
    {
        if (LaunchTargetList == null)
            return;

        DownloadsViewModel downloads = DownloadsViewModel.Shared;
        foreach (DownloadsViewModel.DownloadItem download in downloads.Items)
            download.Refresh();
        downloads.RefreshClassic();

        List<object> entries = [.. downloads.Items, .. downloads.ClientItems];
        object? current = null;
        if (base.DataContext is MainWindowViewModel vm)
        {
            string code = vm.SelectedLaunchClient;
            if (!string.IsNullOrEmpty(code))
                current = downloads.ClientItems.FirstOrDefault(client => client.IsInstalled && string.Equals(client.Code, code, StringComparison.OrdinalIgnoreCase));
            Voidstrap.Enums.LaunchMode mode = vm.SelectedLaunchModeIndex == 1 ? Voidstrap.Enums.LaunchMode.Studio : Voidstrap.Enums.LaunchMode.Player;
            current ??= downloads.Items.FirstOrDefault(download => download.LaunchMode == mode);
        }
        LaunchTargetList.ItemsSource = entries;
        LaunchTargetList.SelectedItem = current;
    }

    private void LaunchTargetList_ItemChosen(object? sender, object item)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            switch (item)
            {
                case DownloadsViewModel.ClientItem client when client.IsInstalled:
                    mainWindowViewModel.SelectedLaunchClient = client.Code;
                    break;
                case DownloadsViewModel.DownloadItem download:
                    mainWindowViewModel.SelectedLaunchClient = "";
                    mainWindowViewModel.SelectedLaunchModeIndex = download.LaunchMode == Voidstrap.Enums.LaunchMode.Studio ? 1 : 0;
                    break;
                default:
                    return;
            }
        }
        UpdateButtonContent();
        CloseLaunchTargetOverlay();
    }

    private void LaunchTargetClose_Click(object sender, RoutedEventArgs e)
    {
        CloseLaunchTargetOverlay();
    }

    private bool _launchTargetOverlayOpen;

    private int _launchTargetOverlayGeneration;

    private System.Windows.Media.Effects.BlurEffect? _launchTargetBlur;

    private static readonly CubicEase LaunchTargetEaseOut = CreateLaunchTargetEase(EasingMode.EaseOut);

    private static readonly CubicEase LaunchTargetEaseIn = CreateLaunchTargetEase(EasingMode.EaseIn);

    private static CubicEase CreateLaunchTargetEase(EasingMode mode)
    {
        CubicEase ease = new CubicEase { EasingMode = mode };
        ease.Freeze();
        return ease;
    }

    private UIElement[] LaunchTargetBlurTargets => [BackgroundLayer, RootGrid, StatusBarHost, TopNavPanel, RootTitleBar];

    private void OpenLaunchTargetOverlay()
    {
        CloseTopBarMenus();
        PopulateLaunchTargets();
        _launchTargetOverlayOpen = true;
        int generation = ++_launchTargetOverlayGeneration;
        LaunchTargetOverlay.Visibility = Visibility.Visible;
        LaunchTargetOverlay.IsHitTestVisible = true;
        _launchTargetBlur ??= new System.Windows.Media.Effects.BlurEffect
        {
            Radius = 0,
            KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
            RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
        };
        foreach (UIElement target in LaunchTargetBlurTargets)
        {
            if (target.Effect == null)
                target.Effect = _launchTargetBlur;
        }
        AnimateLaunchTargetOverlay(1.0, 0.0, 8.0, TimeSpan.FromMilliseconds(260), LaunchTargetEaseOut, generation, false);
        LaunchTargetList.FocusList();
    }

    private void CloseLaunchTargetOverlay()
    {
        if (!_launchTargetOverlayOpen)
            return;
        _launchTargetOverlayOpen = false;
        int generation = ++_launchTargetOverlayGeneration;
        LaunchTargetOverlay.IsHitTestVisible = false;
        AnimateLaunchTargetOverlay(0.0, 28.0, 0.0, TimeSpan.FromMilliseconds(200), LaunchTargetEaseIn, generation, true);
    }

    private void AnimateLaunchTargetOverlay(double opacity, double offsetY, double blurRadius, TimeSpan duration, IEasingFunction ease, int generation, bool finishClose)
    {
        DoubleAnimation fade = new DoubleAnimation(opacity, duration) { EasingFunction = ease };
        if (finishClose)
            fade.Completed += (_, _) => FinishLaunchTargetClose(generation);
        LaunchTargetOverlay.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        LaunchTargetPanelTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offsetY, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        _launchTargetBlur?.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, new DoubleAnimation(blurRadius, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
        LaunchTargetChevronRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(finishClose ? 0.0 : 180.0, duration) { EasingFunction = ease }, HandoffBehavior.SnapshotAndReplace);
    }

    private void FinishLaunchTargetClose(int generation)
    {
        if (generation != _launchTargetOverlayGeneration || _launchTargetOverlayOpen)
            return;
        LaunchTargetOverlay.Visibility = Visibility.Collapsed;
        foreach (UIElement target in LaunchTargetBlurTargets)
        {
            if (ReferenceEquals(target.Effect, _launchTargetBlur))
                target.Effect = null;
        }
        ReleaseOrphanedCapture();
    }

    private void LaunchTargetScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseLaunchTargetOverlay();
        e.Handled = true;
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundaryPattern { get; }
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern { get; }
}
