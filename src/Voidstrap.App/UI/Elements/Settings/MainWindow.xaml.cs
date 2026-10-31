using System;
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
using System.Windows.Data;
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
using Voidstrap.UI;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.UI.ViewModels.Settings;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using Wpf.Ui.Controls.Interfaces;
using Wpf.Ui.Mvvm.Contracts;
using WpfAnimatedGif;

namespace Voidstrap.UI.Elements.Settings;

public partial class MainWindow : WpfUiWindow, INavigationWindow
{
    public sealed record SidebarItemDefinition(
        string Key,
        string DefaultName,
        string Section,
        SymbolRegular DefaultIcon,
        bool CanHide = true,
        string? NameKey = null
    )
    {
        public string DisplayName =>
            NameKey != null && Voidstrap.Resources.LocalizedStrings.Instance[NameKey] is { Length: > 0 } localized
                ? localized
                : DefaultName;
    }

    public static IReadOnlyList<SidebarItemDefinition> SidebarCustomizationItems { get; } =
        new SidebarItemDefinition[]
        {
            new("HomeNavItem", "Home", "Core", SymbolRegular.Home24, false, NameKey: "Ui_Nav_Home"),
            new("IntegrationsNavItem", "Integrations", "Core", SymbolRegular.Add12, NameKey: "Menu_Integrations_Title"),
            new("DeploymentNavItem", "Deployment", "Core", SymbolRegular.PlaySettings20, NameKey: "Common_Deployment"),
            new("AppearanceNavItem", "Appearance", "Core", SymbolRegular.Color24, false, NameKey: "Menu_Appearance_Title"),
            new(
                "FastFlagSettingsNavItem",
                "FastFlag Settings",
                "Configuration",
                SymbolRegular.Settings28,
                NameKey: "Ui_Nav_FastFlagSettings"
            ),
            new("FastFlagEditorNavItem", "FastFlag Editor", "Configuration", SymbolRegular.Flag28, NameKey: "Ui_Nav_FastFlagEditor"),
            new("ModsNavItem", "Mods", "Configuration", SymbolRegular.WrenchScrewdriver20, NameKey: "Menu_Mods_Title"),
            new("MoreNavItem", "More", "Configuration", SymbolRegular.MoreHorizontal24, NameKey: "Ui_Nav_More"),
            new("GlobalNavItem", "Global", "More", SymbolRegular.GlobeLocation20, NameKey: "Ui_Nav_Global"),
            new("ShortcutsNavItem", "Shortcuts", "More", SymbolRegular.Apps32, NameKey: "Common_Shortcuts"),
            new("NewsNavItem", "News", "More", SymbolRegular.News16, NameKey: "Ui_Nav_News"),
            new("ExtensionsNavItem", "Extensions", "Footer", SymbolRegular.CubeAdd20, NameKey: "Ui_Nav_Extensions"),
            new("ManagerNavItem", "Manager", "Footer", SymbolRegular.ArrowDownload24, NameKey: "Ui_Nav_Manager"),
            new("SoberNavItem", "Sober", "Footer", SymbolRegular.Empty, NameKey: "Menu_Sober_Title"),
            new("RosemaryNavItem", "Rosemary", "Footer", SymbolRegular.Empty, NameKey: "Menu_Rosemary_Title"),
            new("SettingsNavItem", "Settings", "Footer", SymbolRegular.Settings28, NameKey: "Ui_Nav_Settings"),
            new("AboutNavItem", "About", "Footer", SymbolRegular.QuestionCircle32, NameKey: "Menu_About_Title"),
        };

    public static bool IsSidebarItemAvailable(string key)
    {
        return key switch
        {
            "SoberNavItem" => Voidstrap.Utility.Platform.IsLinux && !Voidstrap.Utility.LinuxRuntimeChoice.PageUsesRosemary,
            "RosemaryNavItem" => Voidstrap.Utility.Platform.IsLinux && Voidstrap.Utility.LinuxRuntimeChoice.PageUsesRosemary,
            "ExtensionsNavItem" or "ManagerNavItem" => Voidstrap.Utility.Platform.IsWindows,
            _ => true,
        };
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
        Debug.Assert(
            SidebarCustomizationItems.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() == SidebarCustomizationItems.Count
        );
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

        public TopSearchEntry(
            string id,
            string displayText,
            string searchText,
            Type pageType,
            string? targetText = null,
            IEnumerable<string>? targetTerms = null,
            IEnumerable<string>? containerTerms = null,
            bool hiddenByDefault = false
        )
        {
            Id = id;
            HiddenByDefault = hiddenByDefault;
            DisplayText = displayText;
            SearchText = searchText;
            PageType = pageType;
            TargetText = targetText;
            TargetTerms = (targetTerms ?? Enumerable.Empty<string>())
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ContainerTerms = (containerTerms ?? Enumerable.Empty<string>())
                .Where(term => !string.IsNullOrWhiteSpace(term))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
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
                _ => (SymbolRegular.Info24, InfoBrush),
            };
            string text = source.Text.Trim();
            if (source.Key.StartsWith("upgrade:", StringComparison.Ordinal))
            {
                string version = source.Key["upgrade:".Length..];
                int metadata = version.IndexOf('+');
                if (metadata > 0)
                    text = text.Replace(version, version[..metadata], StringComparison.Ordinal);
            }
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
                IconBrush = brush,
            };
        }

        private static string FormatAge(long timestamp)
        {
            if (timestamp <= 0)
            {
                return "";
            }
            DateTimeOffset when =
                timestamp > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : DateTimeOffset.FromUnixTimeSeconds(timestamp);
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

        public bool IsHeader { get; init; }

        public bool IsSetting { get; init; }

        public string TitleBefore { get; init; } = "";

        public string TitleMatch { get; init; } = "";

        public string TitleAfter { get; init; } = "";

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

    private static readonly Dictionary<string, (string Details, string State)> _voidRpcPageDescriptions = new Dictionary<
        string,
        (string, string)
    >
    {
        ["HomePage"] = ("Home", "On the home screen"),
        ["GamePage"] = ("Game Details", "Viewing a game"),
        ["NvidaEditor"] = ("NVIDIA Editor", "Tweaking NVIDIA GPU settings"),
        ["HistoryPage"] = ("Continue Playing", "Browsing recent games"),
        ["IntegrationsPage"] = ("Integrations", "Setting up integrations"),
        ["BehaviourPage"] = ("Deployment", "Configuring channels, the cleaner and the matchmaker"),
        ["FastFlagsPage"] = ("FastFlag Settings", "Tweaking FastFlag presets"),
        ["FastFlagEditorPage"] = ("FastFlag Editor", "Editing FastFlags"),
        ["FastFlagEditorWarningPage"] = ("FastFlag Editor", "Reading the FastFlag warning"),
        ["GBSEditorPage"] = ("Global Settings", "Editing the global Roblox settings"),
        ["ModsPage"] = ("Mods", "Customizing cursors, sounds, overlays and skyboxes"),
        ["NewsPage"] = ("News", "Reading what's new"),
        ["DownloadsPage"] = ("Downloads", "Managing Roblox installs"),
        ["ExtensionPage"] = ("Extensions", "Browsing plugins and integrations"),
        ["ShortcutsPage"] = ("Shortcuts", "Setting up game shortcuts"),
        ["ChannelPage"] = ("Settings", "Adjusting app settings and updates"),
        ["ReleasesPage"] = ("Releases", "Reading the release history"),
        ["DonoPage"] = ("Support Voidstrap", "Considering a donation"),
        ["HelpPage"] = ("Help", "Reading the help guides"),
        ["AppearancePage"] = ("Appearance", "Customizing themes and backgrounds"),
        ["BootstrapperPage"] = ("Bootstrapper", "Customizing the launch window"),
        ["LibraryPage"] = ("Library", "Browsing the game library"),
        ["PrivateServersPage"] = ("Private Servers", "Managing private servers"),
        ["AccountsPage"] = ("Account Manager", "Managing Roblox accounts"),
        ["SoberPage"] = ("Sober", "Setting up Roblox on Linux"),
        ["RosemaryPage"] = ("Rosemary", "Setting up Roblox on Linux"),
        ["Releases"] = ("Releases", "Reading the release history"),
        ["ServerBrowserPage"] = ("Server Browser", "Searching for a server"),
        ["NvidiaFastFlagsPage"] = ("NVIDIA FastFlags", "Tweaking NVIDIA GPU settings"),
        ["ModGeneratorPage"] = ("Mod Generator", "Recoloring the Roblox icons"),
    };

    private AppearanceViewModel.BackgroundSettings _backgroundSettings;

    private int _notificationUnread = -1;

    private int _notificationReloadTicks;

    private string? _currentBackgroundPath;

    private DateTime _currentBackgroundWriteTimeUtc;

    private int _backgroundGeneration;

    private readonly Dictionary<UIElement, TaskCompletionSource<bool>> _backgroundAnimationWaiters =
        new Dictionary<UIElement, TaskCompletionSource<bool>>();

    private Vector _currentOffset;

    private Vector _targetOffset;

    private double _currentRotation;

    private double _targetRotation;

    private DispatcherTimer _searchDebounceTimer = null!;

    private readonly List<PageSearchTarget> _pageSearchTargets = new List<PageSearchTarget>();

    private readonly List<TopSearchEntry> _topSearchEntriesList = new List<TopSearchEntry>();

    private readonly Dictionary<string, TopSearchEntry> _topSearchEntries = new Dictionary<string, TopSearchEntry>(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ObservableCollection<CommandPaletteRow> _commandPaletteRows = new ObservableCollection<CommandPaletteRow>();

    private int _commandPaletteSelected = -1;

    private const int CommandPaletteMaxSettings = 30;

    private int _commandPaletteSearchGeneration;

    private int _commandPaletteAppliedGeneration;

    private CancellationTokenSource? _commandPaletteSearchCts;

    private sealed record CommandPalettePage(
        Type PageType,
        string Name,
        SymbolRegular Icon,
        string Shortcut,
        Action Open,
        string Section = "Footer"
    );

    private sealed record CommandPaletteSnapshot(
        string Query,
        IReadOnlyList<CommandPalettePage> Pages,
        IReadOnlyDictionary<Type, string> PageNames,
        IReadOnlyDictionary<Type, SymbolRegular> PageIcons,
        TopSearchEntry[] Entries
    );

    private readonly Dictionary<string, (WeakReference<FrameworkElement> Element, bool Visible)> _searchTargetStates = new Dictionary<
        string,
        (WeakReference<FrameworkElement>, bool)
    >(StringComparer.Ordinal);

    private TopSearchEntry? _pendingTopSearchEntry;

    private int _topSearchNavigationGeneration;

    private bool _navigationInitialized;

    private INavigationControl[]? _defaultMainSidebarItems;

    private INavigationControl[]? _defaultFooterSidebarItems;

    private Dictionary<string, object?>? _defaultSidebarContent;

    private Page? _lastPage;

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
        typeof(LibraryPage),
        typeof(PrivateServersPage),
        typeof(AccountsPage),
    };

    private LibraryPage? _libraryPage;

    private PrivateServersPage? _privateServersPage;

    private AccountsPage? _accountsPage;

    private Voidstrap.UI.SettingsIconMotion? _settingsIconMotion;

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

    private static Voidstrap.Models.Persistable.WindowState _state => App.State.Prop.SettingsWindow;

    public double GradientLayerOpacity
    {
        get { return _gradientLayerOpacity; }
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
        Resources["LauncherMenuBrush"] =
            TryFindResource("SystemFillColorSolidNeutralBackgroundBrush") as Brush
            ?? Voidstrap.UI.WindowBackdrop.CreateOpaqueSurfaceBrush(this);
        SyncHomeTargetMenuBrush();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Voidstrap.UI.WindowBackdrop.ApplyMainWindow(this);
        if (OperatingSystem.IsWindows() && Voidstrap.UI.TaskbarJumpList.OpenViewMessage != 0)
        {
            nint handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            _openViewSource = System.Windows.Interop.HwndSource.FromHwnd(handle);
            _openViewSource?.AddHook(OpenViewHook);
            _publishedHandle = handle;
            Voidstrap.UI.TaskbarJumpList.PublishWindow(handle);
        }
    }

    private System.Windows.Interop.HwndSource? _openViewSource;

    private string? _startupView;

    private nint _publishedHandle;

    internal void RequestStartupView(string? view)
    {
        _startupView = Voidstrap.UI.TaskbarJumpList.ToViewId(view) != 0 ? view : null;
    }

    private nint OpenViewHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0 || (uint)msg != Voidstrap.UI.TaskbarJumpList.OpenViewMessage)
            return 0;
        handled = true;
        string? view = Voidstrap.UI.TaskbarJumpList.FromViewId(wParam);
        if (view != null)
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => OpenView(view));
        return 0;
    }

    private void OpenView(string view)
    {
        if (_isClosed)
            return;
        try
        {
            if (WindowState == System.Windows.WindowState.Minimized)
                WindowState = System.Windows.WindowState.Normal;
            Activate();
            CloseLaunchTargetOverlay();
            CloseCommandPalette();
            CloseTopBarMenus();
            switch (view)
            {
                case Voidstrap.UI.TaskbarJumpList.LibraryView:
                    TopNavLibrary_Click(this, new RoutedEventArgs());
                    break;
                case Voidstrap.UI.TaskbarJumpList.NotificationsView:
                    if (RootFrame.Content is ChannelPage channel && channel.IsLoaded)
                    {
                        channel.ShowNotificationsTab();
                    }
                    else
                    {
                        ChannelPage.PendingNotificationsTab = true;
                        RootNavigation.Navigate(typeof(ChannelPage));
                    }
                    break;
            }
            App.Logger.WriteLine("MainWindow::OpenView", "Opened " + view + " from the taskbar");
        }
        catch (Exception ex)
        {
            ChannelPage.PendingNotificationsTab = false;
            App.Logger.WriteLine("MainWindow::OpenView", "The " + view + " view could not be opened from the taskbar: " + ex.Message);
        }
    }

    private void ApplyThemeBackground()
    {
        Resources["LauncherMenuBrush"] =
            TryFindResource("SystemFillColorSolidNeutralBackgroundBrush") as Brush
            ?? Voidstrap.UI.WindowBackdrop.CreateOpaqueSurfaceBrush(this);
        SyncHomeTargetMenuBrush();
        if (GradientLayer == null)
        {
            return;
        }
        Brush surface = Voidstrap.UI.WindowBackdrop.CreateSurfaceBrush(this);
        if (BackgroundGradientTransform != null)
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
            if (Voidstrap.Utility.Platform.UsesPortableUi)
            {
                Voidstrap.UI.Elements.Controls.HomepageMediaPreviewVideo portable = new()
                {
                    Name = "BackgroundPortableMedia",
                    Opacity = 1.0,
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed,
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
                Visibility = Visibility.Collapsed,
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
        App.Logger.WriteLine("MainWindow", "Constructing the settings window");
        long constructorStarted = System.Diagnostics.Stopwatch.GetTimestamp();
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
        Wpf.Ui.Controls.StickyTabStrip.Register();
        SyncHomeTargetMenuBrush();
        InstallLaunchButton.SetBinding(
            SplitButton.DropDownOnRightProperty,
            new System.Windows.Data.Binding(nameof(MainWindowViewModel.LaunchDropdownOnRight))
            {
                Mode = System.Windows.Data.BindingMode.OneWay,
            }
        );
        InstallLaunchButton.SetBinding(
            SplitButton.DropDownToolTipProperty,
            new System.Windows.Data.Binding(nameof(MainWindowViewModel.SelectedLaunchTargetName))
            {
                Mode = System.Windows.Data.BindingMode.OneWay,
            }
        );
        InstallLaunchButton.DropDownClick += LaunchTargetButton_Click;
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            MatchLaunchButtonToFooter();
            Voidstrap.UI.LinuxUiPerformance.ReducedMotionChanged += OnLinuxReducedMotionChanged;
            PreviewMouseMove += OnLinuxGradientMouseMove;
            MouseLeave += RootGrid_MouseLeave;
            if (Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
                RootNavigation.TransitionDuration = 0;
        }
        else if (Voidstrap.Utility.Platform.IsMacOS)
        {
            PreviewMouseMove += OnLinuxGradientMouseMove;
            MouseLeave += RootGrid_MouseLeave;
        }
        else if (Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
        {
            RootNavigation.TransitionDuration = 0;
        }
        CommandPaletteResultsList.ItemsSource = _commandPaletteRows;
        PrepareLinuxRestartNotificationInput();
        SoberNavItem.Visibility = IsSidebarItemAvailable("SoberNavItem") ? Visibility.Visible : Visibility.Collapsed;
        RosemaryNavItem.Visibility = IsSidebarItemAvailable("RosemaryNavItem") ? Visibility.Visible : Visibility.Collapsed;
        Voidstrap.Utility.LinuxRuntimeChoice.Changed += OnLinuxRuntimeChanged;
        ExtensionsNavItem.Visibility = Voidstrap.Utility.Platform.IsWindows ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsNavItem.Visibility = Visibility.Visible;
        ManagerNavItem.Visibility = Voidstrap.Utility.Platform.IsWindows ? Visibility.Visible : Visibility.Collapsed;
        VerifySidebarCustomization();
        ApplySidebarCustomization();
        SettingChangeNotifier.Failed += OnSettingChangeFailed;
        RestartNotificationService.Changed += OnRestartRequirementsChanged;
        ManagedModStore.Changed += OnManagedModsChanged;

        Voidstrap.Integrations.Plugins.PluginHost.BannersChanged += OnModBannersChanged;

        Voidstrap.Integrations.Plugins.PluginHost.PaneInvalidated += OnModBannerPaneInvalidated;
        if (Voidstrap.Utility.Platform.IsLinux)
            CreateBackgroundMedia();
        AllowsTransparency = false;
        ApplyThemeBackground();
        InitializeViewModel();
        InitializeWindowState();
        UpdateButtonContent();
        RefreshManageModsStatus();
        InitializeDiscordRPC();
        RegisterHoverIcons();
        _backgroundSettings = AppearanceViewModel.LoadSettings();
        GlobalBackground.Changed += OnGlobalBackgroundChanged;
        ApplyBackgroundSettings();
        PopulateTopSearch();
        ContentRendered += OnStartupContentRendered;
        _visibilityTimer.Interval = TimeSpan.FromSeconds(0.8);
        _visibilityTimer.Tick += VisibilityTimer_Tick;
        _visibilityTimer.Start();
        base.SizeChanged += MainWindow_SizeChanged;
        base.LocationChanged += MainWindow_LocationChanged;
        base.StateChanged += MainWindow_StateChanged;
        RootFrame.Navigating += RootFrame_Navigating;
        RootFrame.Navigated += RootFrame_Navigated;
        PageSidebar.CollapsedChanged += UpdateSidebarToggleIcon;
        Wpf.Ui.Controls.Navigation.NavigationTiming.PageCreated += OnNavigationPageCreated;
        AppMenuPopup.Closing += AppMenuPopup_Closing;
        AppMenuPopup.Closed += OverlayPopup_Closed;
        Voidstrap.Integrations.Plugins.PluginModPages.Changed += OnModPagesChanged;
        ApplyModPagesMenu();
        App.Logger.WriteLine("MainWindow", "Initializing settings window");
        if (showAlreadyRunningWarning)
        {
            _ = ShowAlreadyRunningSnackbarAsync();
        }
        RefreshRestartNotification();
        RenderModBanners();
        Voidstrap.UI.LinuxUiPerformance.Duration("Settings window constructor", constructorStarted);
    }

    private void OnLinuxReducedMotionChanged(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        SnowCanvas?.SetActive(false);
        RootNavigation.TransitionDuration = 0;
    }

    private void VisibilityTimer_Tick(object? sender, EventArgs e)
    {
        CheckLinuxGradientPointer();
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
        foreach (
            NavigationItem item in from i in RootNavigation
                .Items.OfType<NavigationItem>()
                .Concat(RootNavigation.Footer.OfType<NavigationItem>())
                                   where i.Tag != null && i.Image == null && !customIcons.ContainsKey(i.Name)
                                   select i
        )
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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
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

    private static T? FindAncestor<T>(DependencyObject current)
        where T : DependencyObject
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
        if (page is Pages.ModCustomPage)
            return true;
        if (
            ReferenceEquals(page, _libraryPage)
            || ReferenceEquals(page, _privateServersPage)
            || ReferenceEquals(page, _accountsPage)
            || ReferenceEquals(page, _robloxNewsPage)
            || ReferenceEquals(page, _voidstrapModsPage)
        )
        {
            return true;
        }
        Type pageType = page.GetType();
        foreach (
            NavigationItem item in RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>())
        )
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
                    App.Logger.WriteLine(
                        "MainWindow::DetachDiscardedPage",
                        page.GetType().Name + " " + itemsControl.GetType().Name + " " + itemsControl.Name + ": " + ex.Message
                    );
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
            int count =
                current is Visual || current is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(current) : 0;
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
        App.Logger.WriteLine(
            "MainWindow::Navigation",
            content.GetType().Name + " ready in " + (int)total + " ms, construct " + (int)_navigationCreateMs + " ms"
        );
    }

    private void RootFrame_Navigated(object sender, NavigationEventArgs e)
    {
        UpdateSidebarToggleIcon();
        if (!Voidstrap.Utility.Platform.IsWindows && (e.Content is DownloadsPage or ExtensionPage))
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
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                Voidstrap.Integrations.Plugins.PluginHost.Invoke(
                    "page.navigate",
                    new System.Text.Json.Nodes.JsonObject { ["page"] = DescribePage(content) }
                );
                Voidstrap.Integrations.Plugins.PluginAnchors.ApplyAll();
            })
        );
        Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() =>
            {
                Voidstrap.Integrations.Plugins.PluginAnchors.ApplyAll();
            })
        );
        RefreshGlobalSearchBoxVisibility();
        if (BreadcrumbPanel != null)
        {
            BreadcrumbPanel.Visibility = Visibility.Visible;
        }
        bool hasOwnSidebar = content is LibraryPage or PrivateServersPage or AccountsPage;
        SetRootNavigationShown(!hasOwnSidebar);
        SynchronizeSidebarSelection(content);
        SidebarGroup.RefreshActiveChild(MoreNavItem);
        Dispatcher.BeginInvoke(new Action(SynchronizeCurrentSidebarSelection), DispatcherPriority.Loaded);
        UpdateTopNavActive(content);
        if (content is Page)
        {
            Dispatcher.BeginInvoke(
                new Action(IndexLoadedPageSearchEntries),
                _pendingTopSearchEntry != null ? DispatcherPriority.Loaded : DispatcherPriority.ApplicationIdle
            );
        }
        else if (_pendingTopSearchEntry != null)
        {
            _pendingTopSearchEntry = null;
        }
        NavigationItem? navigationItem = RootNavigation
            .Items.OfType<NavigationItem>()
            .Concat(RootNavigation.Footer.OfType<NavigationItem>())
            .FirstOrDefault((NavigationItem i) => i.IsActive);
        if (navigationItem != null && _defaultIcons.TryGetValue(navigationItem, out var value))
        {
            PageBreadcrumb.Icon = value;
        }
        else if (navigationItem != null)
        {
            PageBreadcrumb.Icon = navigationItem.Icon;
        }
        RefreshHomeTargetButton();
        AnimatePageHeader(18.0);
    }

    private static string DescribePage(object content) =>
        content is Pages.ModCustomPage modPage ? ("mod:" + modPage.PluginName + ":" + modPage.PageId) : (content?.GetType().Name ?? "");

    private void SyncHomeTargetMenuBrush()
    {
        if (HomeTargetMenu != null && Resources["LauncherMenuBrush"] is Brush brush)
            HomeTargetMenu.Resources["LauncherMenuBrush"] = brush;
    }

    private void RefreshHomeTargetButton()
    {
        if (RootFrame.Content is not HomePage homePage)
        {
            if (ReferenceEquals(HomeTargetButton.Content, PageBreadcrumb))
            {
                HomeTargetButton.Content = null;
                PageBreadcrumb.Margin = new Thickness(6, 0, 0, 0);
                PageTitleHost.Children.Insert(0, PageBreadcrumb);
            }
            HomeTargetButton.Visibility = Visibility.Collapsed;
            return;
        }
        if (ReferenceEquals(PageBreadcrumb.Parent, PageTitleHost))
        {
            PageTitleHost.Children.Remove(PageBreadcrumb);
            PageBreadcrumb.Margin = new Thickness(0);
            HomeTargetButton.Content = PageBreadcrumb;
        }
        bool studio = homePage.IsStudioTab;
        HomeTargetButton.Visibility = Visibility.Visible;
        string target = studio ? Strings.Common_Studio : "Roblox";
        HomeTargetButton.ToolTip = target;
        AutomationProperties.SetName(HomeTargetButton, target);
        HomeTargetRoblox.Tag = studio ? null : "Active";
        HomeTargetStudio.Tag = studio ? "Active" : null;
    }

    private void SetHomeTarget(bool studio)
    {
        HomeTargetButton.IsDropDownOpen = false;
        if (RootFrame.Content is HomePage homePage)
            homePage.IsStudioTab = studio;
        RefreshHomeTargetButton();
    }

    private void HomeTargetRoblox_Click(object sender, RoutedEventArgs e)
    {
        SetHomeTarget(false);
    }

    private void HomeTargetStudio_Click(object sender, RoutedEventArgs e)
    {
        SetHomeTarget(true);
    }

    private void AnimatePageHeader(double fromX)
    {
        if (BreadcrumbPanel == null || PageHeaderShift == null)
            return;
        PageHeaderShift.BeginAnimation(TranslateTransform.XProperty, GetPageHeaderSlide(fromX));
        BreadcrumbPanel.BeginAnimation(OpacityProperty, PageHeaderFade);
    }

    private static readonly CubicEase PageHeaderEase = CreatePageHeaderEase();

    private static readonly DoubleAnimation PageHeaderFade = CreateFrozen(
        new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(240)) { EasingFunction = PageHeaderEase }
    );

    private static readonly Dictionary<double, DoubleAnimation> PageHeaderSlides = new Dictionary<double, DoubleAnimation>();

    private static CubicEase CreatePageHeaderEase()
    {
        CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private static DoubleAnimation CreateFrozen(DoubleAnimation animation)
    {
        animation.Freeze();
        return animation;
    }

    private static DoubleAnimation GetPageHeaderSlide(double fromX)
    {
        if (!PageHeaderSlides.TryGetValue(fromX, out DoubleAnimation? slide))
        {
            slide = CreateFrozen(new DoubleAnimation(fromX, 0.0, TimeSpan.FromMilliseconds(280)) { EasingFunction = PageHeaderEase });
            PageHeaderSlides[fromX] = slide;
        }
        return slide;
    }

    internal void RefreshGlobalSearchBoxVisibility()
    {
        object? content = RootFrame.Content;
        bool hideSearchBox =
            content != null
            && (
                _pagesToHideSearchBox.Contains(content.GetType())
                || content is Voidstrap.UI.Elements.Settings.Pages.FastFlagsPage fastFlagsPage && fastFlagsPage.IsAdvancedTabSelected
                || content is Voidstrap.UI.Elements.Settings.Pages.ModsPage modsPage && modsPage.IsModPackSelected
            );
        if (BreadcrumbPanel != null)
        {
            Grid.SetColumnSpan(BreadcrumbPanel, hideSearchBox ? 2 : 1);
        }
        Wpf.Ui.Animations.Transitions.ApplyVisibilityTransition(GlobalSearchBox, !hideSearchBox, 160);
    }

    private void UpdateTopNavActive(object? content)
    {
        TopNavHome.Tag = content is HomePage ? "Active" : null;
        TopNavLibrary.Tag = content is LibraryPage ? "Active" : null;
        TopNavServers.Tag = content is PrivateServersPage ? "Active" : null;
        TopNavVoidstrapMods.Tag = content is VoidstrapModsPage ? "Active" : null;
        TopNavNews.Tag = content is NewsPage ? "Active" : null;
    }

    private NavigationItem[] GetNavigationItemsInServiceOrder()
    {
        return RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>()).ToArray();
    }

    private void OnLinuxRuntimeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnLinuxRuntimeChanged(sender, e)));
            return;
        }
        if (_isClosed)
            return;
        ApplySidebarCustomization();
        UpdateButtonContent();
        if (RootFrame.Content is SoberPage && !IsSidebarItemAvailable("SoberNavItem"))
            RootNavigation.Navigate(typeof(RosemaryPage));
        else if (RootFrame.Content is RosemaryPage && !IsSidebarItemAvailable("RosemaryNavItem"))
            RootNavigation.Navigate(typeof(SoberPage));
    }

    private readonly Dictionary<string, NavigationItem> _modSidebarItems = new Dictionary<string, NavigationItem>(StringComparer.Ordinal);

    private readonly Dictionary<string, Pages.ModCustomPage> _modCustomPages = new Dictionary<string, Pages.ModCustomPage>(
        StringComparer.Ordinal
    );

    private void ModSidebarItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is NavigationItem { Tag: Voidstrap.Integrations.Plugins.PluginSidebar.ModPageInfo info })
        {
            OpenModCustomPage(info);
        }
    }

    public void OpenModCustomPage(Voidstrap.Integrations.Plugins.PluginSidebar.ModPageInfo info)
    {
        if (!_modCustomPages.TryGetValue(info.Key, out Pages.ModCustomPage? page))
        {
            page = new Pages.ModCustomPage(info);
            _modCustomPages[info.Key] = page;
        }
        else
        {
            page.UpdateDefinition(info);
        }
        if (!ReferenceEquals(RootFrame.Content, page))
        {
            RootNavigation.NavigateExternal(page);
        }
        else
        {
            page.RenderPage();
            SynchronizeSidebarSelection(page);
        }
    }

    private void OnModBannersChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RenderModBanners));
            return;
        }
        RenderModBanners();
    }

    private void OnModBannerPaneInvalidated(string plugin)
    {
        if (Voidstrap.Integrations.Plugins.PluginHost.Banners.Any(b => string.Equals(b.Plugin, plugin, StringComparison.Ordinal)))
        {
            Dispatcher.BeginInvoke(new Action(RenderModBanners));
        }
    }

    private int _modBannerRenderGeneration;

    private async void RenderModBanners()
    {
        if (ModTopBannerHost == null || ModBottomBannerHost == null)
            return;
        try
        {
            await RenderModBannersCore();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::RenderModBanners", ex);
        }
    }

    private async System.Threading.Tasks.Task RenderModBannersCore()
    {
        int generation = ++_modBannerRenderGeneration;
        IReadOnlyList<Voidstrap.Integrations.Plugins.PluginHost.BannerInfo> banners = Voidstrap.Integrations.Plugins.PluginHost.Banners;
        List<(Voidstrap.Integrations.Plugins.PluginHost.BannerInfo Banner, System.Text.Json.Nodes.JsonNode? Tree)> rendered =
            await Task.Run(() =>
            {
                List<(Voidstrap.Integrations.Plugins.PluginHost.BannerInfo, System.Text.Json.Nodes.JsonNode?)> list = [];
                foreach (Voidstrap.Integrations.Plugins.PluginHost.BannerInfo banner in banners)
                {
                    System.Text.Json.Nodes.JsonNode? tree;
                    try
                    {
                        tree = Voidstrap.Integrations.Plugins.PluginHost.RenderBanner(banner);
                    }
                    catch (Exception ex)
                    {
                        Voidstrap.Integrations.Plugins.PluginHost.Problem(
                            banner.Plugin,
                            "the banner " + banner.Id + " could not be drawn: " + Voidstrap.Integrations.Plugins.PluginHost.Describe(ex)
                        );
                        tree = null;
                    }
                    list.Add((banner, tree));
                }
                return list;
            });
        if (generation != _modBannerRenderGeneration || Dispatcher.HasShutdownStarted)
            return;
        ModTopBannerHost.Children.Clear();
        ModBottomBannerHost.Children.Clear();
        foreach (var (banner, tree) in rendered)
        {
            if (tree == null)
                continue;
            try
            {
                string dir = System.IO.Path.Combine(Paths.Plugins, banner.Plugin);
                UIElement? built = Voidstrap.Integrations.Plugins.PluginUiRenderer.BuildTree(tree, dir);
                if (built != null)
                {
                    Border container = new Border
                    {
                        Padding = new Thickness(10, 6, 10, 6),
                        Margin = new Thickness(0, 0, 0, 6),
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(1),
                        Child = built,
                    };
                    container.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
                    container.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
                    if (string.Equals(banner.Position, "bottom", StringComparison.OrdinalIgnoreCase))
                        ModBottomBannerHost.Children.Add(container);
                    else
                        ModTopBannerHost.Children.Add(container);
                }
            }
            catch (Exception ex)
            {
                Voidstrap.Integrations.Plugins.PluginHost.Problem(
                    banner.Plugin,
                    "the banner " + banner.Id + " could not be drawn: " + Voidstrap.Integrations.Plugins.PluginHost.Describe(ex)
                );
            }
        }
    }

    public string GetCurrentPageKey()
    {
        object? content = RootFrame?.Content;
        if (content is Pages.ModCustomPage modPage)
            return "mod:" + modPage.PluginName + ":" + modPage.PageId;
        return content?.GetType().Name.Replace("Page", "", StringComparison.Ordinal) ?? "";
    }

    public void NavigateFromPlugin(string target, string? plugin = null)
    {
        string trimmed = target.Trim();
        if (trimmed.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
        {
            string rest = trimmed[4..];
            IReadOnlyList<Voidstrap.Integrations.Plugins.PluginSidebar.ModPageInfo> modPages =
                Voidstrap.Integrations.Plugins.PluginSidebar.GetModPages();
            Voidstrap.Integrations.Plugins.PluginSidebar.ModPageInfo? match = modPages.FirstOrDefault(p =>
                string.Equals(p.Plugin + ":" + p.Id, rest, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Id, rest, StringComparison.OrdinalIgnoreCase)
                || string.Equals(p.Key, rest, StringComparison.OrdinalIgnoreCase)
            );
            if (match == null)
            {
                Voidstrap.Integrations.Plugins.PluginModPages.ModPageEntry? menuMatch = Voidstrap
                    .Integrations.Plugins.PluginModPages.GetEntries()
                    .FirstOrDefault(p =>
                        string.Equals(p.Plugin + ":" + p.Id, rest, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Id, rest, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Key, rest, StringComparison.OrdinalIgnoreCase)
                    );
                if (menuMatch != null)
                    match = new Voidstrap.Integrations.Plugins.PluginSidebar.ModPageInfo(
                        menuMatch.Plugin,
                        menuMatch.Id,
                        menuMatch.Key,
                        menuMatch.Title,
                        "Core",
                        menuMatch.Icon
                    );
            }
            if (match != null)
            {
                OpenModCustomPage(match);
                return;
            }
        }
        if (plugin != null)
        {
            Voidstrap.Integrations.Plugins.PluginSidebar.ModPageInfo? ownMatch = Voidstrap
                .Integrations.Plugins.PluginSidebar.GetModPages()
                .FirstOrDefault(p =>
                    string.Equals(p.Plugin, plugin, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.Id, trimmed, StringComparison.OrdinalIgnoreCase)
                );
            if (ownMatch != null)
            {
                OpenModCustomPage(ownMatch);
                return;
            }
        }
        switch (trimmed.ToLowerInvariant())
        {
            case "home":
            case "homepage":
                RootNavigation.Navigate(typeof(HomePage));
                break;
            case "integrations":
            case "integrationspage":
                RootNavigation.Navigate(typeof(IntegrationsPage));
                break;
            case "deployment":
            case "behaviour":
            case "behaviourpage":
                RootNavigation.Navigate(typeof(BehaviourPage));
                break;
            case "appearance":
            case "appearancepage":
                RootNavigation.Navigate(typeof(AppearancePage));
                break;
            case "fastflagsettings":
            case "fastflags":
            case "fastflagspage":
                RootNavigation.Navigate(typeof(FastFlagsPage));
                break;
            case "fastflageditor":
            case "fastflageditorpage":
                RootNavigation.Navigate(typeof(FastFlagEditorPage));
                break;
            case "mods":
            case "modspage":
                RootNavigation.Navigate(typeof(ModsPage));
                break;
            case "global":
            case "gbseditor":
            case "gbseditorpage":
                RootNavigation.Navigate(typeof(GBSEditorPage));
                break;
            case "shortcuts":
            case "shortcutspage":
                RootNavigation.Navigate(typeof(ShortcutsPage));
                break;
            case "news":
            case "newspage":
                RootNavigation.Navigate(typeof(NewsPage));
                break;
            case "extensions":
            case "extensionpage":
                if (Voidstrap.Utility.Platform.IsWindows)
                    RootNavigation.Navigate(typeof(ExtensionPage));
                break;
            case "manager":
            case "downloads":
            case "downloadspage":
                if (Voidstrap.Utility.Platform.IsWindows)
                    RootNavigation.Navigate(typeof(DownloadsPage));
                break;
            case "settings":
            case "channel":
            case "channelpage":
                RootNavigation.Navigate(typeof(ChannelPage));
                break;
            case "voidstrapmods":
            case "voidstrapmodspage":
                TopNavVoidstrapMods_Click(this, new RoutedEventArgs());
                break;
            case "library":
            case "librarypage":
                TopNavLibrary_Click(this, new RoutedEventArgs());
                break;
            case "privateservers":
            case "privateserverspage":
            case "servers":
                TopNavServers_Click(this, new RoutedEventArgs());
                break;
            case "accounts":
            case "accountspage":
                _accountsPage ??= new AccountsPage();
                if (!ReferenceEquals(RootFrame.Content, _accountsPage))
                    RootNavigation.NavigateExternal(_accountsPage);
                break;
            case "more":
            case "morepage":
                RootNavigation.Navigate(typeof(MorePage));
                break;
            case "serverbrowser":
            case "serverbrowserpage":
                RootNavigation.Navigate(typeof(ServerBrowserPage));
                break;
            case "sober":
            case "soberpage":
                if (IsSidebarItemAvailable("SoberNavItem"))
                    RootNavigation.Navigate(typeof(SoberPage));
                break;
            case "rosemary":
            case "rosemarypage":
                if (IsSidebarItemAvailable("RosemaryNavItem"))
                    RootNavigation.Navigate(typeof(RosemaryPage));
                break;
        }
    }

    public void ApplySidebarCustomization()
    {
        if (RootNavigation == null)
        {
            return;
        }
        _defaultMainSidebarItems ??= RootNavigation.Items.ToArray();
        _defaultFooterSidebarItems ??= RootNavigation.Footer.ToArray();
        foreach (NavigationItem oldModItem in _modSidebarItems.Values.ToArray())
        {
            if (RootNavigation.Items.Contains(oldModItem))
                RootNavigation.Items.Remove(oldModItem);
            if (RootNavigation.Footer.Contains(oldModItem))
                RootNavigation.Footer.Remove(oldModItem);
        }
        INavigationControl[] controls = _defaultMainSidebarItems.Concat(_defaultFooterSidebarItems).ToArray();
        Dictionary<string, NavigationItem> items = controls
            .OfType<NavigationItem>()
            .Where(item => !string.IsNullOrEmpty(item.Name))
            .ToDictionary(item => item.Name, StringComparer.Ordinal);
        _defaultSidebarContent ??= items.ToDictionary<KeyValuePair<string, NavigationItem>, string, object?>(
            pair => pair.Key,
            pair => (object?)BindingOperations.GetBindingBase(pair.Value, ContentControl.ContentProperty) ?? (object?)pair.Value.Content,
            StringComparer.Ordinal
        );
        _defaultSidebarIcons ??= items.ToDictionary(pair => pair.Key, pair => pair.Value.Icon, StringComparer.Ordinal);
        _defaultSidebarImages ??= items.ToDictionary(pair => pair.Key, pair => (BitmapSource?)pair.Value.Image, StringComparer.Ordinal);
        Dictionary<string, string> names = App.Settings.Prop.SidebarNames ??= new Dictionary<string, string>();
        Dictionary<string, string> icons = App.Settings.Prop.SidebarIcons ??= new Dictionary<string, string>();
        Dictionary<string, string> iconImages = App.Settings.Prop.SidebarIconImages ??= new Dictionary<string, string>();
        List<string> hidden = App.Settings.Prop.SidebarHiddenItems ??= new List<string>();
        List<string> savedOrder = App.Settings.Prop.SidebarOrder ??= new List<string>();
        bool needsFullIconFont = false;
        List<SidebarItemDefinition> allSidebarDefinitions = SidebarCustomizationItems.ToList();
        foreach (
            Voidstrap.Integrations.Plugins.PluginSidebar.ModPageInfo modPage in Voidstrap.Integrations.Plugins.PluginSidebar.GetModPages()
        )
        {
            if (!_modSidebarItems.TryGetValue(modPage.Key, out NavigationItem? modNav))
            {
                modNav = new NavigationItem { Name = modPage.Key, Cache = false };
                _modSidebarItems[modPage.Key] = modNav;
            }
            SymbolRegular modDefaultIcon = Enum.TryParse(modPage.Icon, ignoreCase: true, out SymbolRegular parsedModIcon)
                ? parsedModIcon
                : SymbolRegular.Apps24;
            modNav.Tag = modPage;
            modNav.Click -= ModSidebarItem_Click;
            modNav.Click += ModSidebarItem_Click;
            modNav.Content = names.TryGetValue(modPage.Key, out string? customModTitle)
                ? NormalizeSidebarName(customModTitle, modPage.Title)
                : modPage.Title;
            if (
                icons.TryGetValue(modPage.Key, out string? customModIconName)
                && Enum.TryParse(customModIconName, ignoreCase: true, out SymbolRegular customModSym)
            )
            {
                modNav.Icon = customModSym;
                if (Voidstrap.UI.Elements.Dialogs.SidebarIconPickerDialog.CanRender(customModSym))
                    needsFullIconFont = true;
            }
            else
            {
                modNav.Icon = modDefaultIcon;
                if (Voidstrap.UI.Elements.Dialogs.SidebarIconPickerDialog.CanRender(modDefaultIcon))
                    needsFullIconFont = true;
            }
            modNav.Visibility = hidden.Contains(modPage.Key, StringComparer.Ordinal) ? Visibility.Collapsed : Visibility.Visible;
            items[modPage.Key] = modNav;
            allSidebarDefinitions.Add(new SidebarItemDefinition(modPage.Key, modPage.Title, modPage.Section, modDefaultIcon, true));
            if (_modCustomPages.TryGetValue(modPage.Key, out Pages.ModCustomPage? activePage))
                activePage.UpdateDefinition(modPage);
        }
        Dictionary<string, int> ranks = savedOrder
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Select((key, index) => (key, index))
            .ToDictionary(pair => pair.key, pair => pair.index, StringComparer.Ordinal);
        Dictionary<string, int> defaults = allSidebarDefinitions
            .Select((item, index) => (item.Key, index))
            .ToDictionary(pair => pair.Key, pair => pair.index, StringComparer.Ordinal);
        foreach (SidebarItemDefinition definition in SidebarCustomizationItems)
        {
            if (!items.TryGetValue(definition.Key, out NavigationItem? item))
            {
                continue;
            }
            if (names.TryGetValue(definition.Key, out string? customName))
            {
                item.Content = NormalizeSidebarName(customName, definition.DisplayName);
            }
            else if (_defaultSidebarContent[definition.Key] is BindingBase defaultBinding)
            {
                item.SetBinding(ContentControl.ContentProperty, defaultBinding);
            }
            else
            {
                item.Content = _defaultSidebarContent[definition.Key];
            }
            item.Icon = _defaultSidebarIcons[definition.Key];
            BitmapSource? defaultImage = _defaultSidebarImages[definition.Key];
            item.SetValue(NavigationItem.ImageProperty, defaultImage);
            if (
                iconImages.TryGetValue(definition.Key, out string? imagePath)
                && SafeImaging.FromFile(imagePath, 48) is BitmapSource customImage
            )
            {
                item.Icon = SymbolRegular.Empty;
                item.Image = customImage;
            }
            else if (
                icons.TryGetValue(definition.Key, out string? iconName)
                && Enum.TryParse(iconName, out SymbolRegular customIcon)
                && Voidstrap.UI.Elements.Dialogs.SidebarIconPickerDialog.CanRender(customIcon)
            )
            {
                item.SetValue(NavigationItem.ImageProperty, null);
                item.Icon = customIcon;
                needsFullIconFont = true;
            }
            bool isHidden = definition.CanHide && hidden.Contains(definition.Key, StringComparer.Ordinal);
            item.Visibility = IsSidebarItemAvailable(definition.Key) && !isHidden ? Visibility.Visible : Visibility.Collapsed;
        }
        if (needsFullIconFont)
        {
            RootNavigation.Resources[Voidstrap.UI.Elements.Dialogs.SidebarIconPickerDialog.IconFontResourceKey] = Voidstrap
                .UI
                .Elements
                .Dialogs
                .SidebarIconPickerDialog
                .FullIconFont;
        }
        else
        {
            RootNavigation.Resources.Remove(Voidstrap.UI.Elements.Dialogs.SidebarIconPickerDialog.IconFontResourceKey);
        }
        IEnumerable<SidebarItemDefinition> Ordered(string section)
        {
            return allSidebarDefinitions
                .Where(item => item.Section == section && items.ContainsKey(item.Key))
                .OrderBy(item => ranks.TryGetValue(item.Key, out int rank) ? rank : int.MaxValue)
                .ThenBy(item => defaults[item.Key]);
        }
        NavigationItem[] headers = _defaultMainSidebarItems.OfType<NavigationItem>().Where(item => !item.IsEnabled).Take(2).ToArray();
        NavigationItem coreHeader = headers[0];
        NavigationItem configurationHeader = headers[1];
        Wpf.Ui.Controls.Navigation.NavigationSeparator separator = _defaultMainSidebarItems
            .OfType<Wpf.Ui.Controls.Navigation.NavigationSeparator>()
            .First();
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
            if (RootFrame?.Content is Pages.ModCustomPage activeModPage && !items.ContainsKey(activeModPage.PageKey))
            {
                RootNavigation.Navigate(typeof(HomePage));
            }
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
        if (SidebarCustomizationItems.FirstOrDefault(item => item.Key == key) is { NameKey: not null } definition)
        {
            return definition.DisplayName;
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
        NavigationItem? activeItem = content is Pages.ModCustomPage modPage
            ? navigationItems.FirstOrDefault(item => string.Equals(item.Name, modPage.PageKey, StringComparison.Ordinal))
            : navigationItems.FirstOrDefault(item => item.PageType == contentType);
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
                string[] containers = option
                    .Containers.Select(SearchCatalog.Resolve)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                string displayText =
                    "Option: " + title + " | " + pageName + (containers.Length > 0 ? " | " + string.Join(" › ", containers) : string.Empty);
                List<string> terms = new List<string> { title, option.TitleToken, description, option.TargetName, option.Id };
                terms.AddRange(option.Aliases);
                terms.AddRange(containers);
                entries.Add(
                    new TopSearchEntry(
                        option.Id,
                        displayText,
                        string.Join(" ", terms),
                        option.PageType,
                        title,
                        terms,
                        containers,
                        option.HiddenByDefault
                    )
                );
            }
            _topSearchEntriesList.Clear();
            _topSearchEntries.Clear();
            Dictionary<string, int> displayCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (TopSearchEntry originalEntry in entries)
            {
                int occurrence = displayCounts.TryGetValue(originalEntry.DisplayText, out int count) ? count + 1 : 1;
                displayCounts[originalEntry.DisplayText] = occurrence;
                TopSearchEntry entry =
                    occurrence == 1
                        ? originalEntry
                        : new TopSearchEntry(
                            originalEntry.Id,
                            originalEntry.DisplayText + " | " + occurrence,
                            originalEntry.SearchText,
                            originalEntry.PageType,
                            originalEntry.TargetText,
                            originalEntry.TargetTerms,
                            originalEntry.ContainerTerms,
                            originalEntry.HiddenByDefault
                        );
                _topSearchEntriesList.Add(entry);
                _topSearchEntries.Add(entry.DisplayText, entry);
            }
            _indexedSearchPages.Clear();
            if (!_isClosed && RootFrame?.Content is Page currentPage && _indexedSearchPages.TryAdd(currentPage, currentPage))
            {
                IndexDynamicPageSearchEntries(currentPage);
            }
            if (_commandPaletteOpen)
            {
                RefreshCommandPaletteResults();
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MainWindow::Search", "Could not populate settings search: " + ex.Message);
        }
    }

    private void OnStartupContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnStartupContentRendered;
        if (!Voidstrap.UI.LinuxWindowReveal.IsPending(this))
            StartDeferredStartupWork();
    }

    private bool _startupWorkStarted;

    internal void StartDeferredStartupWork()
    {
        if (_isClosed || _startupWorkStarted)
            return;
        _startupWorkStarted = true;
        Voidstrap.Integrations.Mcp.McpBridgeServer.EnsureStarted();
        _ = LoadCatalogOptionsAsync();
        if (_introFinished)
            ScheduleLaunchTargetWarmup();
    }

    private static readonly char[] separatorArray = new[] { ' ', '\t', '\r', '\n', ',', '.', ':', '/', '\\', '_', '-' };
    private static readonly char[] separator = new[] { ' ', '\t', '\r', '\n', ',', '.', ':', '/', '\\' };

    private async Task LoadCatalogOptionsAsync()
    {
        long searchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            for (int attempt = 0; attempt < 3 && !_lifetimeCts.IsCancellationRequested; attempt++)
            {
                await SearchCatalog.LoadOptionsAsync().ConfigureAwait(false);
                if (SearchCatalog.Options.Count > 0)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), _lifetimeCts.SafeToken()).ConfigureAwait(false);
            }
            if (!_lifetimeCts.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(
                    () =>
                    {
                        PopulateTopSearch();
                        Voidstrap.UI.LinuxUiPerformance.Duration("Settings search indexing", searchStarted);
                        Voidstrap.UI.LinuxUiPerformance.Mark("Settings search ready with " + _topSearchEntriesList.Count + " entries");
                    },
                    DispatcherPriority.Background
                );
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MainWindow::Search", "Could not load settings search: " + ex.Message);
        }
    }

    private Dictionary<Type, string> GetSidebarPageNames()
    {
        Dictionary<Type, string> pages = new Dictionary<Type, string>();
        IEnumerable<NavigationItem> items = RootNavigation
            .Items.OfType<NavigationItem>()
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

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Page, object> _indexedSearchPages =
        new System.Runtime.CompilerServices.ConditionalWeakTable<Page, object>();

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
            TopSearchEntry entry = new TopSearchEntry(
                "Dynamic." + pageType.Name + "." + element.Name + "." + _topSearchEntriesList.Count,
                displayText,
                text + " " + element.Name,
                pageType,
                text,
                new[] { text, element.Name }
            );
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
            FrameworkElement? target =
                optionHeaders.GetValueOrDefault(entry.NormalizedTargetText)
                ?? entry.TargetTerms.Select(term => namedElements.GetValueOrDefault(term)).FirstOrDefault(element => element != null);
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
        if (
            entry == null
            || string.IsNullOrWhiteSpace(entry.TargetText)
            || RootFrame.Content?.GetType() != entry.PageType
            || RootFrame.Content is not Page page
        )
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
                    await Dispatcher.InvokeAsync(
                        page.UpdateLayout,
                        attempt == 0 ? DispatcherPriority.Loaded : DispatcherPriority.ContextIdle
                    );
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
            if (
                generation != Volatile.Read(ref _topSearchNavigationGeneration)
                || _isClosed
                || RootFrame.Content?.GetType() != entry.PageType
            )
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
        if (
            !string.IsNullOrWhiteSpace(element.Name)
            && entry.TargetTerms.Any(term => string.Equals(element.Name, term, StringComparison.OrdinalIgnoreCase))
        )
        {
            return 0;
        }
        string text = GetSearchableElementText(element);
        if (element is OptionControl option)
        {
            if (
                entry.TargetTerms.Any(term =>
                    string.Equals(SearchCatalog.Resolve(option.Header), SearchCatalog.Resolve(term), StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return 1;
            }
            if (entry.TargetTerms.Any(term => IsFuzzyMatch(option.Header + " " + option.Description, SearchCatalog.Resolve(term))))
            {
                return 10;
            }
        }
        if (
            text.Length > 0
            && entry.TargetTerms.Any(term =>
                string.Equals(NormalizeSearchText(text), NormalizeSearchText(SearchCatalog.Resolve(term)), StringComparison.Ordinal)
            )
        )
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
            if (
                element is TabItem tabItem
                && tabItem.IsEnabled
                && tabItem.Visibility == Visibility.Visible
                && ItemsControl.ItemsControlFromItemContainer(tabItem) is TabControl tabControl
            )
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
            if (current is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                try
                {
                    int visualChildren = VisualTreeHelper.GetChildrenCount(current);
                    for (int index = 0; index < visualChildren; index++)
                    {
                        pending.Enqueue(VisualTreeHelper.GetChild(current, index));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
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

    private void PagesMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (PagesMenuPopup.IsOpen && !PagesMenuPopup.IsClosing)
        {
            PagesMenuPopup.IsOpen = false;
            return;
        }
        CloseOverlayPopups();
        PagesMenuPopup.IsOpen = true;
    }

    private void ModPagesMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModPagesMenuPopup.IsOpen && !ModPagesMenuPopup.IsClosing)
        {
            ModPagesMenuPopup.IsOpen = false;
            return;
        }
        CloseOverlayPopups();
        ApplyModPagesMenu();
        ModPagesMenuPopup.IsOpen = true;
    }

    public void ApplyModPagesMenu()
    {
        if (ModPagesMenuButton == null || ModPagesMenuPanel == null)
            return;
        IReadOnlyList<Voidstrap.Integrations.Plugins.PluginModPages.ModPageEntry> entries =
            Voidstrap.Integrations.Plugins.PluginModPages.GetEntries();
        ModPagesMenuButton.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (ModPagesMenuButton.Visibility == Visibility.Collapsed)
        {
            ModPagesMenuPopup.IsOpen = false;
            return;
        }
        ModPagesMenuPanel.Children.Clear();
        foreach (Voidstrap.Integrations.Plugins.PluginModPages.ModPageEntry entry in entries)
        {
            Wpf.Ui.Controls.MenuItem item = new()
            {
                Header = entry.Title,
                Style = (Style)FindResource("TopBarMenuItemStyle"),
                Tag = entry,
            };
            item.Icon = Voidstrap.Integrations.Plugins.PluginModPages.CreateIcon(entry);
            item.Click += ModPageMenuItem_Click;
            ModPagesMenuPanel.Children.Add(item);
        }
    }

    private void ModPageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ModPagesMenuPopup.IsOpen = false;
        if (sender is FrameworkElement { Tag: Voidstrap.Integrations.Plugins.PluginModPages.ModPageEntry { IsAction: true } action })
        {
            System.Text.Json.Nodes.JsonObject payload = new()
            {
                ["id"] = action.Id,
                ["plugin"] = action.Plugin,
                ["event"] = "menu",
                ["name"] = action.Key,
                ["type"] = "ModPage",
                ["text"] = action.Title,
                ["window"] = "MainWindow",
            };
            _ = Task.Run(() => Voidstrap.Integrations.Plugins.PluginHost.Invoke("ui.event", payload));
            return;
        }
        if (sender is FrameworkElement { Tag: Voidstrap.Integrations.Plugins.PluginModPages.ModPageEntry entry })
            OpenModCustomPage(
                new Voidstrap.Integrations.Plugins.PluginSidebar.ModPageInfo(
                    entry.Plugin,
                    entry.Id,
                    entry.Key,
                    entry.Title,
                    "Core",
                    entry.Icon
                )
            );
    }

    private void OnModPagesChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ApplyModPagesMenu));
            return;
        }
        ApplyModPagesMenu();
    }

    private void TopNavHome_Click(object sender, RoutedEventArgs e)
    {
        PagesMenuPopup.IsOpen = false;
        NavigateTopNav(typeof(HomePage));
    }

    private void TopNavLibrary_Click(object sender, RoutedEventArgs e)
    {
        PagesMenuPopup.IsOpen = false;
        _libraryPage ??= new LibraryPage();
        if (!ReferenceEquals(RootFrame.Content, _libraryPage))
        {
            RootNavigation.NavigateExternal(_libraryPage);
        }
    }

    public void ShowRobloxNews()
    {
        _robloxNewsPage ??= new Pages.RobloxNewsPage();
        if (!ReferenceEquals(RootFrame.Content, _robloxNewsPage))
        {
            RootNavigation.NavigateExternal(_robloxNewsPage);
        }
    }

    private const string SpotifyWebUrl = "https://open.spotify.com/";

    private async void TopNavSpotify_Click(object sender, RoutedEventArgs e)
    {
        PagesMenuPopup.IsOpen = false;
        if (Voidstrap.Utility.Platform.UsesPortableUi)
        {
            Utilities.OpenWebLink(SpotifyWebUrl);
            return;
        }
        SpotifyOverlay.Visibility = Visibility.Visible;
        SpotifyOverlay.IsHitTestVisible = true;
        AnimateSpotifyPanel(true);
        SpotifyPlayer.SetShown(true);
        SpotifyPlayer.Failed -= SpotifyPlayer_Failed;
        SpotifyPlayer.Failed += SpotifyPlayer_Failed;
        await SpotifyPlayer.EnsureStartedAsync();
    }

    private void SpotifyPlayer_Failed(object? sender, EventArgs e)
    {
        AnimateSpotifyPanel(false);
        Utilities.OpenWebLink(SpotifyWebUrl);
    }

    private void SpotifyFade_Completed(object? sender, EventArgs e)
    {
        if (!SpotifyOverlay.IsHitTestVisible)
            SpotifyOverlay.Visibility = Visibility.Hidden;
    }

    private void SpotifyClose_Click(object sender, RoutedEventArgs e) => AnimateSpotifyPanel(false);

    private void SpotifyScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => AnimateSpotifyPanel(false);

    private void AnimateSpotifyPanel(bool open)
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(open ? 260 : 200);
        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };
        SpotifyOverlay.IsHitTestVisible = open;
        SpotifyPanelSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(open ? 0 : SpotifyPanel.Width, duration) { EasingFunction = ease });
        DoubleAnimation fade = new(open ? 1 : 0, duration) { EasingFunction = ease };
        fade.Completed += SpotifyFade_Completed;
        SpotifyOverlay.BeginAnimation(OpacityProperty, fade);
        if (!open)
            SpotifyPlayer.SetShown(false);
    }

    private void TopNavNews_Click(object sender, RoutedEventArgs e)
    {
        PagesMenuPopup.IsOpen = false;
        NavigateTopNav(typeof(NewsPage));
    }

    private VoidstrapModsPage? _voidstrapModsPage;

    private void TopNavVoidstrapMods_Click(object sender, RoutedEventArgs e)
    {
        PagesMenuPopup.IsOpen = false;
        _voidstrapModsPage ??= new VoidstrapModsPage();
        if (!ReferenceEquals(RootFrame.Content, _voidstrapModsPage))
        {
            RootNavigation.NavigateExternal(_voidstrapModsPage);
        }
    }

    private void TopNavServers_Click(object sender, RoutedEventArgs e)
    {
        PagesMenuPopup.IsOpen = false;
        _privateServersPage ??= new PrivateServersPage();
        if (!ReferenceEquals(RootFrame.Content, _privateServersPage))
        {
            RootNavigation.NavigateExternal(_privateServersPage);
        }
    }

    private void TopSearchButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleCommandPalette();
    }

    private void ToggleCommandPalette()
    {
        if (_commandPaletteOpen)
        {
            CloseCommandPalette();
            return;
        }
        CloseOverlayPopups();
        CommandPaletteSearchBox.Text = "";
        _commandPaletteOpen = true;
        RefreshCommandPaletteResults();
        OpenModalOverlay(CommandPalettePanel, CommandPalettePanelScale);
        Dispatcher.BeginInvoke(new Action(FocusCommandPalette), DispatcherPriority.Input);
    }

    private void FocusCommandPalette()
    {
        if (!_commandPaletteOpen)
        {
            return;
        }
        CommandPaletteSearchBox.Focus();
        Keyboard.Focus(CommandPaletteSearchBox);
    }

    private void CloseCommandPalette()
    {
        CancelCommandPaletteSearch();
        if (!_commandPaletteOpen)
        {
            return;
        }
        _commandPaletteOpen = false;
        CloseModalOverlay();
    }

    private void CommandPaletteClear_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CommandPaletteSearchBox.Text))
        {
            CloseCommandPalette();
            return;
        }
        CommandPaletteSearchBox.Clear();
        CommandPaletteSearchBox.Focus();
    }

    private void ResetCommandPalette()
    {
        CancelCommandPaletteSearch();
        CommandPaletteSearchBox.Text = "";
        _commandPaletteRows.Clear();
        _commandPaletteSelected = -1;
    }

    private void CancelCommandPaletteSearch()
    {
        Interlocked.Increment(ref _commandPaletteSearchGeneration);
        CancellationTokenSource? cts = _commandPaletteSearchCts;
        _commandPaletteSearchCts = null;
        if (cts == null)
        {
            return;
        }
        cts.Cancel();
        cts.Dispose();
    }

    private void RefreshCommandPaletteResults()
    {
        CancelCommandPaletteSearch();
        int generation = Volatile.Read(ref _commandPaletteSearchGeneration);
        CommandPaletteSnapshot snapshot = CaptureCommandPaletteSnapshot();
        List<CommandPalettePage> pages = MatchCommandPalettePages(snapshot);
        if (snapshot.Query.Length == 0)
        {
            ApplyCommandPaletteResults(generation, snapshot, pages, new List<TopSearchEntry>());
            return;
        }
        ApplyCommandPaletteResults(generation, snapshot, pages, null);
        CancellationTokenSource cts = new CancellationTokenSource();
        _commandPaletteSearchCts = cts;
        _ = RankCommandPaletteSettingsAsync(generation, snapshot, pages, cts.Token);
    }

    private CommandPaletteSnapshot CaptureCommandPaletteSnapshot()
    {
        string query = CommandPaletteSearchBox.Text?.Trim() ?? "";
        Dictionary<Type, string> pageNames = GetSidebarPageNames();
        Dictionary<Type, SymbolRegular> pageIcons = new Dictionary<Type, SymbolRegular>();
        List<CommandPalettePage> pages = new List<CommandPalettePage>();
        foreach (NavigationItem item in GetNavigationItemsInServiceOrder())
        {
            if (
                item.PageType == null
                || !pageNames.TryGetValue(item.PageType, out string? pageName)
                || pageIcons.ContainsKey(item.PageType)
            )
            {
                continue;
            }
            SymbolRegular icon = item.Icon == SymbolRegular.Empty ? SymbolRegular.Document24 : item.Icon;
            pageIcons.Add(item.PageType, icon);
            Type pageType = item.PageType;
            string section = SidebarCustomizationItems.FirstOrDefault(definition => definition.Key == item.Name)?.Section ?? "Footer";
            pages.Add(
                new CommandPalettePage(pageType, pageName, icon, GetNavShortcutLabel(pageType), () => NavigateTopNav(pageType), section)
            );
        }
        pages.Add(
            new CommandPalettePage(
                typeof(Page),
                Strings.Ui_Pages_Library,
                SymbolRegular.Apps24,
                "",
                () => TopNavLibrary_Click(this, new RoutedEventArgs())
            )
        );
        return new CommandPaletteSnapshot(query, pages, pageNames, pageIcons, _topSearchEntriesList.ToArray());
    }

    private static string GetNavShortcutLabel(Type pageType)
    {
        foreach (var shortcut in NavShortcuts)
        {
            if (shortcut.Page == pageType)
            {
                return Voidstrap.Utility.Platform.IsMacOS
                    ? Wpf.Ui.Converters.ShortcutTextConverter.Format(shortcut.Label)
                    : shortcut.Label.Replace("+", " ", StringComparison.Ordinal);
            }
        }
        return "";
    }

    private static List<CommandPalettePage> MatchCommandPalettePages(CommandPaletteSnapshot snapshot)
    {
        if (snapshot.Query.Length == 0)
        {
            return snapshot.Pages.ToList();
        }
        string normalizedQuery = NormalizeSearchText(snapshot.Query);
        return snapshot
            .Pages.Where(page => IsFuzzyMatch(page.Name, snapshot.Query))
            .OrderBy(page => NormalizeSearchText(page.Name).StartsWith(normalizedQuery, StringComparison.Ordinal) ? 0 : 1)
            .ToList();
    }

    private static List<TopSearchEntry> RankCommandPaletteSettings(CommandPaletteSnapshot snapshot, CancellationToken token)
    {
        string normalizedQuery = NormalizeSearchText(snapshot.Query);
        List<(TopSearchEntry Entry, int Score)> scored = new List<(TopSearchEntry, int)>();
        for (int index = 0; index < snapshot.Entries.Length; index++)
        {
            if ((index & 63) == 0)
            {
                token.ThrowIfCancellationRequested();
            }
            TopSearchEntry entry = snapshot.Entries[index];
            if (
                !snapshot.PageNames.TryGetValue(entry.PageType, out string? pageName)
                || string.Equals(entry.NormalizedTargetText, NormalizeSearchText(pageName), StringComparison.Ordinal)
            )
            {
                continue;
            }
            int score = ScoreTopSearchEntry(entry, normalizedQuery);
            if (score < int.MaxValue)
            {
                scored.Add((entry, score));
            }
        }
        token.ThrowIfCancellationRequested();
        return scored
            .OrderBy(item => item.Score)
            .ThenBy(item => item.Entry.DisplayText.Length)
            .Select(item => item.Entry)
            .Take(CommandPaletteMaxSettings * 4)
            .ToList();
    }

    private async Task RankCommandPaletteSettingsAsync(
        int generation,
        CommandPaletteSnapshot snapshot,
        List<CommandPalettePage> pages,
        CancellationToken token
    )
    {
        List<TopSearchEntry> ranked;
        try
        {
            ranked = await Task.Run(() => RankCommandPaletteSettings(snapshot, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MainWindow::Search", "Could not rank search results: " + ex.Message);
            return;
        }
        if (
            _isClosed
            || token.IsCancellationRequested
            || generation != Volatile.Read(ref _commandPaletteSearchGeneration)
            || !_commandPaletteOpen
        )
        {
            return;
        }
        ApplyCommandPaletteResults(generation, snapshot, pages, ranked);
    }

    private void ApplyCommandPaletteResults(
        int generation,
        CommandPaletteSnapshot snapshot,
        List<CommandPalettePage> pages,
        List<TopSearchEntry>? rankedSettings
    )
    {
        CommandPaletteRow? previous =
            _commandPaletteSelected >= 0 && _commandPaletteSelected < _commandPaletteRows.Count
                ? _commandPaletteRows[_commandPaletteSelected]
                : null;
        string? keepTitle =
            rankedSettings != null && previous != null && !previous.IsHeader ? previous.Title + "\n" + previous.Detail : null;
        _commandPaletteRows.Clear();
        _commandPaletteSelected = -1;
        int results = 0;
        if (pages.Count > 0 && snapshot.Query.Length == 0)
        {
            foreach (
                (string section, string title) in new[]
                {
                    ("Core", Strings.Ui_Nav_Core),
                    ("Configuration", Strings.Ui_Nav_Configuration),
                    ("More", Strings.Ui_Nav_More),
                    ("Footer", Strings.Ui_Appearance_TabApplication),
                }
            )
            {
                List<CommandPalettePage> sectionPages = pages.Where(page => page.Section == section).ToList();
                if (sectionPages.Count == 0)
                    continue;
                _commandPaletteRows.Add(new CommandPaletteRow { IsHeader = true, Title = title });
                foreach (CommandPalettePage page in sectionPages)
                {
                    _commandPaletteRows.Add(CreateCommandPaletteRow(page.Name, page.Shortcut, page.Icon, snapshot.Query, page.Open));
                    results++;
                }
            }
        }
        else if (pages.Count > 0)
        {
            _commandPaletteRows.Add(new CommandPaletteRow { IsHeader = true, Title = Strings.Ui_Main_PalettePages });
            foreach (CommandPalettePage page in pages)
            {
                _commandPaletteRows.Add(CreateCommandPaletteRow(page.Name, page.Shortcut, page.Icon, snapshot.Query, page.Open));
                results++;
            }
        }
        if (rankedSettings != null && rankedSettings.Count > 0)
        {
            List<TopSearchEntry> shown = rankedSettings
                .Where(IsSearchEntryShown)
                .DistinctBy(entry => (entry.PageType, entry.NormalizedTargetText))
                .Take(CommandPaletteMaxSettings)
                .ToList();
            if (shown.Count > 0)
            {
                _commandPaletteRows.Add(new CommandPaletteRow { IsHeader = true, Title = Strings.Ui_Main_PaletteSettings });
                foreach (TopSearchEntry entry in shown)
                {
                    TopSearchEntry captured = entry;
                    string pageName = snapshot.PageNames.GetValueOrDefault(entry.PageType, "");
                    _commandPaletteRows.Add(
                        CreateCommandPaletteRow(
                            entry.TargetText ?? pageName,
                            pageName,
                            snapshot.PageIcons.GetValueOrDefault(entry.PageType, SymbolRegular.Document24),
                            snapshot.Query,
                            () => QueueTopSearchNavigation(captured),
                            true
                        )
                    );
                    results++;
                }
            }
        }
        if (rankedSettings != null)
        {
            _commandPaletteAppliedGeneration = generation;
        }
        bool searching = rankedSettings == null && snapshot.Query.Length > 0;
        CommandPaletteEmpty.Visibility = !searching && results == 0 ? Visibility.Visible : Visibility.Collapsed;
        CommandPaletteEmptyText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Strings.Ui_Main_PaletteNoResults,
            snapshot.Query
        );
        CommandPaletteResultsScroll.Visibility = results == 0 ? Visibility.Collapsed : Visibility.Visible;
        CommandPaletteCount.Text =
            snapshot.Query.Length == 0 ? ""
            : searching ? Strings.Ui_Main_PaletteSearching
            : results == 1 ? Strings.Ui_Main_PaletteOneResult
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Ui_Main_PaletteResults, results);
        int keep =
            keepTitle == null
                ? -1
                : _commandPaletteRows.ToList().FindIndex(row => !row.IsHeader && row.Title + "\n" + row.Detail == keepTitle);
        if (keep >= 0)
        {
            SelectCommandPaletteRow(keep);
            return;
        }
        CommandPaletteResultsScroll.ScrollToTop();
        MoveCommandPaletteSelection(1);
    }

    private static CommandPaletteRow CreateCommandPaletteRow(
        string title,
        string detail,
        SymbolRegular icon,
        string query,
        Action open,
        bool isSetting = false
    )
    {
        (string before, string match, string after) = SplitCommandPaletteMatch(title, query);
        return new CommandPaletteRow
        {
            Title = title,
            TitleBefore = before,
            TitleMatch = match,
            TitleAfter = after,
            Detail = detail,
            Icon = icon,
            Open = open,
            IsSetting = isSetting,
        };
    }

    internal static (string Before, string Match, string After) SplitCommandPaletteMatch(string title, string query)
    {
        string trimmed = query.Trim();
        if (title.Length == 0 || trimmed.Length == 0)
        {
            return (title, "", "");
        }
        int position = FindCommandPaletteMatch(title, trimmed);
        int length = trimmed.Length;
        if (position < 0)
        {
            foreach (string term in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(term => term.Length))
            {
                position = FindCommandPaletteMatch(title, term);
                if (position >= 0)
                {
                    length = term.Length;
                    break;
                }
            }
        }
        if (position < 0)
        {
            return (title, "", "");
        }
        return (title[..position], title.Substring(position, length), title[(position + length)..]);
    }

    private static int FindCommandPaletteMatch(string title, string term)
    {
        int first = title.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        for (int position = first; position >= 0; position = title.IndexOf(term, position + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (
                position == 0
                || !char.IsLetterOrDigit(title[position - 1])
                || (char.IsUpper(title[position]) && char.IsLower(title[position - 1]))
            )
            {
                return position;
            }
        }
        return first;
    }

    private void CommandPaletteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        CommandPalettePlaceholder.Visibility = string.IsNullOrEmpty(CommandPaletteSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_commandPaletteOpen)
        {
            RefreshCommandPaletteResults();
        }
    }

    private void CommandPaletteSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveCommandPaletteSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveCommandPaletteSelection(-1);
                e.Handled = true;
                break;
            case Key.Tab:
                MoveCommandPaletteSelection(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveCommandPaletteSelection(8);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveCommandPaletteSelection(-8);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelectedCommandPaletteRow();
                e.Handled = true;
                break;
        }
    }

    private void MoveCommandPaletteSelection(int direction)
    {
        int count = _commandPaletteRows.Count;
        if (count == 0 || direction == 0)
        {
            return;
        }
        int step = Math.Sign(direction);
        if (Math.Abs(direction) == 1)
        {
            int index = _commandPaletteSelected;
            for (int tries = 0; tries < count; tries++)
            {
                index = ((index + step) % count + count) % count;
                if (!_commandPaletteRows[index].IsHeader)
                {
                    SelectCommandPaletteRow(index);
                    return;
                }
            }
            return;
        }
        int target = Math.Clamp((_commandPaletteSelected < 0 ? 0 : _commandPaletteSelected) + direction, 0, count - 1);
        while (target >= 0 && target < count && _commandPaletteRows[target].IsHeader)
        {
            target += step;
        }
        if (target < 0 || target >= count)
        {
            target = step > 0 ? LastSelectableCommandPaletteRow() : FirstSelectableCommandPaletteRow();
        }
        if (target >= 0)
        {
            SelectCommandPaletteRow(target);
        }
    }

    private int FirstSelectableCommandPaletteRow()
    {
        for (int index = 0; index < _commandPaletteRows.Count; index++)
        {
            if (!_commandPaletteRows[index].IsHeader)
            {
                return index;
            }
        }
        return -1;
    }

    private int LastSelectableCommandPaletteRow()
    {
        for (int index = _commandPaletteRows.Count - 1; index >= 0; index--)
        {
            if (!_commandPaletteRows[index].IsHeader)
            {
                return index;
            }
        }
        return -1;
    }

    private void SelectCommandPaletteRow(int index)
    {
        if (_commandPaletteSelected >= 0 && _commandPaletteSelected < _commandPaletteRows.Count)
        {
            _commandPaletteRows[_commandPaletteSelected].IsSelected = false;
        }
        _commandPaletteSelected = index;
        if (index < 0 || index >= _commandPaletteRows.Count || _commandPaletteRows[index].IsHeader)
        {
            _commandPaletteSelected = -1;
            return;
        }
        _commandPaletteRows[index].IsSelected = true;
        int reveal = index > 0 && _commandPaletteRows[index - 1].IsHeader ? index - 1 : index;
        if (CommandPaletteResultsList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.BringIntoView();
        }
        if (reveal != index && CommandPaletteResultsList.ItemContainerGenerator.ContainerFromIndex(reveal) is FrameworkElement header)
        {
            header.BringIntoView();
        }
    }

    private void OpenSelectedCommandPaletteRow()
    {
        if (
            _commandPaletteAppliedGeneration != Volatile.Read(ref _commandPaletteSearchGeneration)
            && CommandPaletteSearchBox.Text.Trim().Length > 0
        )
        {
            CancelCommandPaletteSearch();
            int generation = Volatile.Read(ref _commandPaletteSearchGeneration);
            CommandPaletteSnapshot snapshot = CaptureCommandPaletteSnapshot();
            ApplyCommandPaletteResults(
                generation,
                snapshot,
                MatchCommandPalettePages(snapshot),
                RankCommandPaletteSettings(snapshot, CancellationToken.None)
            );
        }
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
        if (sender is FrameworkElement { DataContext: CommandPaletteRow row } && !row.IsHeader)
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
        if (sender is FrameworkElement { DataContext: CommandPaletteRow row } && !row.IsHeader)
        {
            int index = _commandPaletteRows.IndexOf(row);
            if (index < 0)
            {
                return;
            }
            SelectCommandPaletteRow(index);
            Action? open = row.Open;
            if (open == null)
            {
                return;
            }
            CloseCommandPalette();
            open();
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

    private async void AppMenuAbout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CloseAppMenuBeforeDialogAsync())
                return;
            Voidstrap.UI.Elements.About.MainWindow window = new Voidstrap.UI.Elements.About.MainWindow { Owner = this };
            window.ShowOwnedDialog();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::AppMenuAbout_Click", ex);
        }
    }

    private async Task<bool> CloseAppMenuBeforeDialogAsync()
    {
        AppMenuPopup.IsOpen = false;
        await Task.Delay(180);
        return !_isClosed;
    }

    private async void AppMenuUpdates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppMenuPopup.IsOpen = false;
            await CheckForUpdatesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::AppMenuUpdates_Click", ex);
        }
    }

    private bool _checkingForUpdates;

    private async Task CheckForUpdatesAsync()
    {
        if (_checkingForUpdates)
        {
            return;
        }
        _checkingForUpdates = true;
        try
        {
            string currentText = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
            string latestTag =
                (
                    !Voidstrap.Utility.Platform.IsWindows
                        ? await GithubUpdater.GetLatestVersionTagAsync(_lifetimeCts.SafeToken())
                        : (await App.GetLatestRelease(true))?.TagName
                ) ?? throw new InvalidDataException("Release information is unavailable");
            if (
                !Version.TryParse(currentText, out Version? current)
                || !Version.TryParse(latestTag.TrimStart('v', 'V'), out Version? latest)
            )
            {
                Frontend.ShowMessageBox("Could not compare this build with the latest release.");
                return;
            }
            App.Logger.WriteLine("MainWindow::CheckForUpdates", "Local: " + currentText + " | Remote: " + latestTag);
            if (latest <= current)
            {
                Frontend.ShowMessageBox("You are already running the latest version of Voidstrap (" + currentText + ").");
                return;
            }
            App.Logger.WriteLine("MainWindow::CheckForUpdates", "Installing " + latestTag + " requested from the app menu");
            if (!await GithubUpdater.DownloadAndInstallUpdate(latestTag, _lifetimeCts.SafeToken()))
            {
                throw new InvalidDataException("The update could not be installed");
            }
            if (!App.RestartApplication(["-settings", "-elevatedwait", Environment.ProcessId.ToString()]))
            {
                throw new InvalidOperationException("The updated application could not be restarted");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::CheckForUpdates", ex);
            Frontend.ShowMessageBox("Error checking for updates:\n" + ex.Message);
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    private async void AppMenuReleases_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await CloseAppMenuBeforeDialogAsync())
                return;
            ShowReleaseNotes();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::AppMenuReleases_Click", ex);
        }
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

    private bool _creatingSupportFile;

    private async void AppMenuSupportFile_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        if (_creatingSupportFile)
            return;
        _creatingSupportFile = true;
        try
        {
            string file = await Voidstrap.Utility.SupportBundle.CreateAsync(_lifetimeCts.SafeToken());
            Voidstrap.Utility.PlatformShell.TryOpenFolder(System.IO.Path.GetDirectoryName(file) ?? Paths.Desktop);
            Frontend.ShowMessageBox(
                string.Format(Strings.Ui_Main_SupportFileCreated, System.IO.Path.GetFileName(file)),
                MessageBoxImage.Information
            );
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::CreateSupportFile", ex);
            Frontend.ShowMessageBox(string.Format(Strings.Ui_Main_SupportFileFailed, ex.Message), MessageBoxImage.Error);
        }
        finally
        {
            _creatingSupportFile = false;
        }
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

    private void AppMenuConnectAi_Click(object sender, RoutedEventArgs e)
    {
        AppMenuPopup.IsOpen = false;
        Voidstrap.Integrations.Mcp.McpConnectInfo.Show();
    }

    public string CurrentPageName => RootFrame.Content?.GetType().Name ?? "None";

    public void RefreshCurrentPage()
    {
        if (RootFrame.Content is FrameworkElement element && element.DataContext != null)
        {
            object ctx = element.DataContext;
            if (ctx is Voidstrap.UI.ViewModels.ISettingsRefreshable refreshable)
                refreshable.ReloadFromSettings();
            element.DataContext = null;
            element.DataContext = ctx;
        }
    }

    public bool NavigateToPageByName(string pageName, string? searchQuery = null, bool focusWindow = true)
    {
        if (focusWindow)
        {
            if (WindowState == System.Windows.WindowState.Minimized)
                WindowState = System.Windows.WindowState.Normal;
            Activate();
        }

        string key = pageName
            .Trim()
            .Replace(" ", "", StringComparison.Ordinal)
            .Replace("Page", "", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
        bool navigated;

        switch (key)
        {
            case "library":
                _libraryPage ??= new LibraryPage();
                if (!ReferenceEquals(RootFrame.Content, _libraryPage))
                    RootNavigation.NavigateExternal(_libraryPage);
                navigated = true;
                break;
            case "privateservers":
            case "servers":
                _privateServersPage ??= new PrivateServersPage();
                if (!ReferenceEquals(RootFrame.Content, _privateServersPage))
                    RootNavigation.NavigateExternal(_privateServersPage);
                navigated = true;
                break;
            case "accounts":
            case "accountmanager":
                _accountsPage ??= new AccountsPage();
                if (!ReferenceEquals(RootFrame.Content, _accountsPage))
                    RootNavigation.NavigateExternal(_accountsPage);
                navigated = true;
                break;
            default:
                Type? targetType = key switch
                {
                    "home" => typeof(HomePage),
                    "integrations" => typeof(IntegrationsPage),
                    "bootstrapper" or "deployment" => typeof(BehaviourPage),
                    "appearance" or "customtheme" => typeof(AppearancePage),
                    "fastflags" or "fastflagsettings" => typeof(FastFlagsPage),
                    "fastflageditor" => typeof(FastFlagEditorPage),
                    "mods" => typeof(ModsPage),
                    "more" => typeof(MorePage),
                    "global" or "gbseditor" or "globalbasicsettings" => typeof(GBSEditorPage),
                    "shortcuts" => typeof(ShortcutsPage),
                    "news" => typeof(NewsPage),
                    "extensions" or "extension" => typeof(ExtensionPage),
                    "downloads" or "manager" => typeof(DownloadsPage),
                    "settings" or "channel" => typeof(ChannelPage),
                    _ => null,
                };
                if (targetType == null)
                    return false;
                navigated = Navigate(targetType);
                break;
        }

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            GlobalSearchBox.Text = searchQuery;
            PerformSearch(searchQuery);
        }

        return navigated;
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
            _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150L) };
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
            _pageSearchTargets.Add(new PageSearchTarget { Element = element, Text = text });
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
        if (
            element is HeaderedContentControl headeredContentControl
            && headeredContentControl.Header is string header
            && !string.IsNullOrWhiteSpace(header)
        )
        {
            parts.Add(header);
        }
        else if (element is HeaderedContentControl complexHeaderControl && complexHeaderControl.Header is DependencyObject complexHeader)
        {
            parts.AddRange(
                EnumerateSearchElements(complexHeader)
                    .OfType<TextBlock>()
                    .Select(text => text.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
            );
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
            double targetOffset =
                scrollViewer.VerticalOffset + point.Y - Math.Max(0.0, (scrollViewer.ViewportHeight - target.ActualHeight) / 2.0);
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
            double desiredOffset =
                fallbackScrollViewer.VerticalOffset
                + point.Y
                - Math.Max(0.0, (fallbackScrollViewer.ViewportHeight - fallback.Element.ActualHeight) / 2.0);
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
        DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15L) };
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

    private static int ScoreTopSearchEntry(TopSearchEntry entry, string normalizedQuery)
    {
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
        return IsFuzzyMatch(entry.SearchText, normalizedQuery)
            ? 100 + Math.Abs(entry.NormalizedTargetText.Length - normalizedQuery.Length)
            : int.MaxValue;
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
            if (
                !words.Any(word =>
                    word.Contains(term, StringComparison.Ordinal)
                    || (Math.Abs(word.Length - term.Length) <= threshold && LevenshteinDistance(word, term) <= threshold)
                )
            )
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
        DateTime writeTimeUtc =
            !string.IsNullOrWhiteSpace(state.FilePath) && File.Exists(state.FilePath) ? File.GetLastWriteTimeUtc(state.FilePath) : default;
        if (
            !string.Equals(_currentBackgroundPath, state.FilePath, StringComparison.OrdinalIgnoreCase)
            || _currentBackgroundWriteTimeUtc != writeTimeUtc
        )
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
        ApplyBackgroundState(
            new GlobalBackground.State(
                _backgroundSettings.BackgroundFilePath,
                _backgroundSettings.GradientOpacity,
                _backgroundSettings.BlackOverlayOpacity,
                _backgroundSettings.DisplayEverywhere
            )
        );
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
            catch { }
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
            if (BackgroundMedia != null)
            {
                BackgroundMedia.Visibility = Visibility.Collapsed;
            }
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
            int width = (int)
                Math.Ceiling(
                    ((ActualWidth > 0.0) ? ActualWidth : ((Width > 0.0) ? Width : 1280.0)) * VisualTreeHelper.GetDpi(this).DpiScaleX
                );
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
            if (
                !_backgroundAnimationWaiters.TryGetValue(element, out TaskCompletionSource<bool>? current) || !ReferenceEquals(current, tcs)
            )
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
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop,
        };
        doubleAnimation.Completed += delegate
        {
            settle();
        };
        watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) + TimeSpan.FromMilliseconds(750.0) };
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

    private void CheckLinuxGradientPointer()
    {
        if (
            !Voidstrap.Utility.Platform.IsLinux
            || !App.Settings.Prop.GRADmentFR
            || !IsActive
            || _targetOffset.LengthSquared == 0
            || !RootGrid.IsVisible
        )
            return;
        if (!Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetPointerPosition(out int x, out int y))
            return;
        Point position = RootGrid.PointFromScreen(new Point(x, y));
        if (position.X >= 0 && position.Y >= 0 && position.X <= RootGrid.ActualWidth && position.Y <= RootGrid.ActualHeight)
            return;
        _targetOffset = new Vector(0, 0);
        _targetRotation = 0;
        StartGradientRendering();
    }

    private void OnLinuxGradientMouseMove(object sender, MouseEventArgs e)
    {
        if (RootGrid.ActualWidth <= 0 || RootGrid.ActualHeight <= 0)
            return;
        Point position = e.GetPosition(RootGrid);
        if (position.X < 0 || position.Y < 0 || position.X > RootGrid.ActualWidth || position.Y > RootGrid.ActualHeight)
        {
            RootGrid_MouseLeave(sender, e);
            return;
        }
        double x = (position.X / RootGrid.ActualWidth - 0.5) * 2;
        double y = (position.Y / RootGrid.ActualHeight - 0.5) * 2;
        _targetOffset = new Vector(x * 0.04, y * 0.04);
        _targetRotation = x * 5;
        StartGradientRendering();
    }

    private void RootGrid_MouseMove(object sender, MouseEventArgs e)
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
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
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            if (App.Settings.Prop.GRADmentFR && IsActive && IsVisible && WindowState != System.Windows.WindowState.Minimized)
                StartLinuxGradientAnimation();
            return;
        }
        if (App.Settings.Prop.GRADmentFR && base.IsActive && !Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
    }

    private void StartLinuxGradientAnimation()
    {
        TimeSpan duration = TimeSpan.FromMilliseconds(1400);
        AnimateGradientProperty(BackgroundGradientTranslate, TranslateTransform.XProperty, _targetOffset.X, duration);
        AnimateGradientProperty(BackgroundGradientTranslate, TranslateTransform.YProperty, _targetOffset.Y, duration);
        AnimateGradientProperty(BackgroundGradientRotate, RotateTransform.AngleProperty, _targetRotation, duration);
        Wpf.Ui.Animations.RenderReady.Hold(this, duration);
    }

    private static void AnimateGradientProperty(Animatable target, DependencyProperty property, double value, TimeSpan duration)
    {
        double current = (double)target.GetValue(property);
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
        if (Math.Abs(current - value) < 0.000001)
            return;
        target.BeginAnimation(
            property,
            new DoubleAnimation
            {
                From = current,
                To = value,
                Duration = duration,
                EasingFunction = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            }
        );
    }

    private void StopLinuxGradientAnimation(bool reset)
    {
        if (Voidstrap.Utility.Platform.IsWindows)
            return;
        double x = reset ? 0 : BackgroundGradientTranslate.X;
        double y = reset ? 0 : BackgroundGradientTranslate.Y;
        double angle = reset ? 0 : BackgroundGradientRotate.Angle;
        BackgroundGradientTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        BackgroundGradientTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        BackgroundGradientRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        BackgroundGradientTranslate.X = x;
        BackgroundGradientTranslate.Y = y;
        BackgroundGradientRotate.Angle = angle;
    }

    private void CompositionTarget_Rendering(object? sender, EventArgs e)
    {
        if (Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
        {
            CompositionTarget.Rendering -= CompositionTarget_Rendering;
            return;
        }
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
        _ = SuperviseDiscordRpcAsync(_lifetimeCts.SafeToken());
        if (RootNavigation != null)
        {
            RootNavigation.Navigated += RootNavigation_RpcNavigated;
        }
        Activated += MainWindow_ActivatedRpc;
        AddHandler(
            System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new System.Windows.Controls.SelectionChangedEventHandler(OnRpcTabSelectionChanged),
            true
        );
        Closed += MainWindow_ClosedRpc;
        Voidstrap.Utility.SavedAccounts.Changed += OnSavedAccountsChanged;
        _settingsIconMotion = new Voidstrap.UI.SettingsIconMotion(SettingsNavItem);
        _ = RefreshAccountButtonAsync();
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
                if (
                    _discordRpcEnabled
                    && _discordClient == null
                    && DateTime.UtcNow >= _discordRetryAtUtc
                    && DiscordIpc.TryFindPipe(out int pipe)
                )
                {
                    await Dispatcher.InvokeAsync(() => InitializeDiscordRpcClient(pipe), DispatcherPriority.Background, token);
                }

                if (_discordRpcEnabled && _discordReady)
                    await Dispatcher.InvokeAsync(UpdateDiscordPresence, DispatcherPriority.Background, token);

                await Task.Delay(DiscordIpc.PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void InitializeDiscordRpcClient(int pipe)
    {
        if (_isClosed || !_discordRpcEnabled || _discordClient != null)
            return;

        DiscordRpcLogger logger = new DiscordRpcLogger("DiscordRichPresence::App") { Level = LogLevel.Warning };
        DiscordRpcClient client = DiscordIpc.CreateClient(
            "1459679943498661910",
            pipe,
            static () => Voidstrap.Utility.Branding.Name + " " + VoidstrapPresence.PlatformName,
            logger
        );
        client.OnReady += DiscordClient_OnReady;
        client.OnError += DiscordClient_OnError;
        client.OnConnectionFailed += DiscordClient_OnConnectionFailed;
        client.OnClose += DiscordClient_OnClose;
        client.OnPresenceUpdate += DiscordClient_OnPresenceUpdate;
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

    private void DiscordClient_OnClose(object sender, CloseMessage e)
    {
        if (!Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(() => RetryDiscordRpcLater(sender)));
    }

    private void DiscordClient_OnPresenceUpdate(object sender, PresenceMessage e)
    {
        if (!_isClosed && ReferenceEquals(sender, _discordClient))
            App.Logger.WriteLine("DiscordRPC", "Discord confirmed the Voidstrap activity");
    }

    private void RetryDiscordRpcLater(object client)
    {
        if (!ReferenceEquals(client, _discordClient))
            return;

        App.Logger.WriteLine(
            "DiscordRPC",
            "Discord connection failed, retrying in " + (int)DiscordIpc.RetryDelay.TotalSeconds + " seconds"
        );
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
        client.OnClose -= DiscordClient_OnClose;
        client.OnPresenceUpdate -= DiscordClient_OnPresenceUpdate;
        DiscordIpc.Close(client);
        App.Logger.WriteLine("DiscordRPC", "Cleared the Voidstrap status and closed the connection");
    }

    private void DiscordClient_OnReady(object sender, ReadyMessage e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (_isClosed || !ReferenceEquals(sender, _discordClient))
                    return;
                _discordReady = true;
                _lastVoidRpcDetails = null;
                _lastVoidRpcState = null;
                _lastVoidRpcExtra = null;
                _lastVoidRpcUpdate = DateTime.MinValue;
                _voidRpcSuppressed = false;
                App.Logger.WriteLine(
                    "DiscordRPC",
                    "Discord RPC is ready for " + Voidstrap.Utility.Branding.Name + " " + VoidstrapPresence.PlatformName
                );
                UpdateDiscordPresence();
            })
        );
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
        RemoveHandler(
            System.Windows.Controls.Primitives.Selector.SelectionChangedEvent,
            new System.Windows.Controls.SelectionChangedEventHandler(OnRpcTabSelectionChanged)
        );
        Closed -= MainWindow_ClosedRpc;
        Voidstrap.Utility.SavedAccounts.Changed -= OnSavedAccountsChanged;
        _settingsIconMotion?.Dispose();
        _settingsIconMotion = null;
        _accountsPage?.Release();
    }

    private void OnSavedAccountsChanged()
    {
        Dispatcher.BeginInvoke(new Action(ApplyAccountButton));
    }

    private async Task RefreshAccountButtonAsync()
    {
        try
        {
            await Voidstrap.Utility.SavedAccounts.RefreshCurrentAsync(_lifetimeCts.SafeToken());
        }
        catch (OperationCanceledException) { }
        ApplyAccountButton();
    }

    private void ApplyAccountButton()
    {
        Voidstrap.Utility.CurrentAccountInfo? current = Voidstrap.Utility.SavedAccounts.Current;
        AccountButtonName.Text =
            current == null ? Strings.Accounts_NotSignedIn
            : string.IsNullOrWhiteSpace(current.DisplayName) ? current.Username
            : current.DisplayName;
        AccountButton.ToolTip = current == null ? Strings.Accounts_Title : "@" + current.Username;
        Voidstrap.Utility.DynamicRenderSystem.SetLazyImageSource(AccountButtonAvatar, current?.AvatarUrl);
    }

    private void ToggleAccountMenu()
    {
        if (AccountMenuPopup.IsOpen && !AccountMenuPopup.IsClosing)
        {
            AccountMenuPopup.IsOpen = false;
            return;
        }
        if (DataContext is MainWindowViewModel viewModel)
            TestModeMenuItem.Tag = viewModel.TestModeEnabled ? "Active" : null;
        CloseOverlayPopups();
        AccountMenuPopup.IsOpen = true;
    }

    private void AccountMenuPopup_Opened(object? sender, EventArgs e) => RotateAccountMenuChevron(180);

    private void AccountMenuPopup_Closed(object? sender, EventArgs e) => RotateAccountMenuChevron(0);

    private void RotateAccountMenuChevron(double angle)
    {
        DoubleAnimation rotation = new(angle, new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        AccountMenuChevronRotation.BeginAnimation(RotateTransform.AngleProperty, rotation);
    }

    private void TestModeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        AccountMenuPopup.IsOpen = false;
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.TestModeEnabled = !viewModel.TestModeEnabled;
    }

    private void AccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountMenuChevron.IsMouseOver)
        {
            ToggleAccountMenu();
            return;
        }
        _accountsPage ??= new AccountsPage();
        if (!ReferenceEquals(RootFrame.Content, _accountsPage))
        {
            RootNavigation.NavigateExternal(_accountsPage);
        }
    }

    private string _latestNewsKey = "";

    private string _voidRpcSmallImageUrl = "";

    private string _voidRpcSmallImageText = "";

    private bool _voidRpcAvatarLoading;

    private void RefreshRpcAvatar()
    {
        _lastVoidRpcDetails = null;
        _lastVoidRpcState = null;
        _lastVoidRpcExtra = null;
        _lastVoidRpcUpdate = DateTime.MinValue;
        UpdateDiscordPresence();
    }

    private async Task FetchRpcAvatarAsync()
    {
        try
        {
            string? cookie = Voidstrap.Integrations.RobloxCookie.Get();
            if (string.IsNullOrEmpty(cookie))
            {
                return;
            }
            _voidRpcAvatarLoading = true;
            await Dispatcher.InvokeAsync(RefreshRpcAvatar);
            long robloxId = 0;
            string userName = "";
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.SafeToken()))
            using (
                var request = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    "https://users.roblox.com/v1/users/authenticated"
                )
            )
            {
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);
                using var response = await App
                    .HttpClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token)
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }
                string text = await Voidstrap
                    .Utility.Http.ReadStringBoundedAsync(response.Content, 200000, cts.Token)
                    .ConfigureAwait(continueOnCapturedContext: false);
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    idProp.TryGetInt64(out robloxId);
                }
                if (doc.RootElement.TryGetProperty("displayName", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    userName = dn.GetString() ?? "";
                }
                if (
                    string.IsNullOrEmpty(userName)
                    && doc.RootElement.TryGetProperty("name", out var nm)
                    && nm.ValueKind == System.Text.Json.JsonValueKind.String
                )
                {
                    userName = nm.GetString() ?? "";
                }
            }
            if (robloxId <= 0)
            {
                return;
            }
            string imageUrl = "";
            using (var cts2 = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.SafeToken()))
            {
                cts2.CancelAfter(TimeSpan.FromSeconds(15));
                using var thumbResponse = await App
                    .HttpClient.GetAsync(
                        "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds="
                            + robloxId
                            + "&size=150x150&format=Png&isCircular=true",
                        System.Net.Http.HttpCompletionOption.ResponseHeadersRead,
                        cts2.Token
                    )
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (!thumbResponse.IsSuccessStatusCode)
                {
                    return;
                }
                string thumbText = await Voidstrap
                    .Utility.Http.ReadStringBoundedAsync(thumbResponse.Content, 200000, cts2.Token)
                    .ConfigureAwait(continueOnCapturedContext: false);
                using var thumbDoc = System.Text.Json.JsonDocument.Parse(thumbText);
                if (
                    thumbDoc.RootElement.TryGetProperty("data", out var dataProp)
                    && dataProp.ValueKind == System.Text.Json.JsonValueKind.Array
                    && dataProp.GetArrayLength() > 0
                )
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
            if (
                host != "tr.rbxcdn.com"
                && !host.EndsWith(".rbxcdn.com", StringComparison.Ordinal)
                && !host.EndsWith(".roblox.com", StringComparison.Ordinal)
            )
            {
                return;
            }
            _voidRpcSmallImageUrl = imageUri.AbsoluteUri;
            if (!string.IsNullOrEmpty(userName))
            {
                _voidRpcSmallImageText = userName;
            }
            _voidRpcAvatarLoading = false;
            await Dispatcher.InvokeAsync(RefreshRpcAvatar);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("DiscordRPC", "Avatar fetch failed: " + ex.GetType().Name);
        }
        finally
        {
            if (_voidRpcAvatarLoading)
            {
                _voidRpcAvatarLoading = false;
                if (!_lifetimeCts.IsCancellationRequested)
                {
                    try
                    {
                        await Dispatcher.InvokeAsync(RefreshRpcAvatar);
                    }
                    catch { }
                }
            }
        }
    }

    private async Task CheckForNewNewsAsync()
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.SafeToken());
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, NewsViewModel.FeedUrl);
            using var response = await App
                .HttpClient.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(continueOnCapturedContext: false);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }
            string text = await Voidstrap
                .Utility.Http.ReadStringBoundedAsync(response.Content, 2000000, cts.Token)
                .ConfigureAwait(continueOnCapturedContext: false);
            string key = "";
            using (var doc = System.Text.Json.JsonDocument.Parse(text))
            {
                if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var first = doc.RootElement[0];
                    string title =
                        first.TryGetProperty("Title", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? tEl.GetString() ?? ""
                            : "";
                    string date =
                        first.TryGetProperty("Date", out var dEl) && dEl.ValueKind == System.Text.Json.JsonValueKind.String
                            ? dEl.GetString() ?? ""
                            : "";
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
        panel.Children.Add(
            new System.Windows.Shapes.Ellipse
            {
                Width = 7.0,
                Height = 7.0,
                Margin = new Thickness(6.0, 1.0, 0.0, 0.0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
            }
        );
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

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, RpcTabCache> _rpcTabCaches =
        new System.Runtime.CompilerServices.ConditionalWeakTable<object, RpcTabCache>();

    private static readonly TimeSpan RpcTabRescanInterval = TimeSpan.FromSeconds(10);

    private const int RpcTabDepth = 2;

    private sealed class RpcTabCache
    {
        public WeakReference<System.Windows.Controls.TabControl>? Tabs;

        public DateTime ScannedUtc;
    }

    private string GetSelectedTabPath()
    {
        if (RootFrame?.Content is not DependencyObject page)
        {
            return string.Empty;
        }
        RpcTabCache cache = _rpcTabCaches.GetValue(page, static _ => new RpcTabCache());
        System.Windows.Controls.TabControl? tabs = null;
        if (cache.Tabs != null && cache.Tabs.TryGetTarget(out System.Windows.Controls.TabControl? cached) && cached.IsVisible)
        {
            tabs = cached;
        }
        else if (DateTime.UtcNow - cache.ScannedUtc >= RpcTabRescanInterval || cache.Tabs != null)
        {
            cache.ScannedUtc = DateTime.UtcNow;
            tabs = FindVisibleTabControl(page);
            cache.Tabs = tabs != null ? new WeakReference<System.Windows.Controls.TabControl>(tabs) : null;
        }
        List<string> names = new List<string>();
        for (int depth = 0; depth < RpcTabDepth && tabs != null; depth++)
        {
            if (tabs.SelectedItem is not System.Windows.Controls.TabItem item)
            {
                break;
            }
            string name = ReadTabHeader(item.Header);
            if (name.Length == 0)
            {
                break;
            }
            names.Add(name);
            tabs = tabs.SelectedContent is DependencyObject content ? FindVisibleTabControl(content) : null;
        }
        return string.Join(" \u00b7 ", names);
    }

    private static System.Windows.Controls.TabControl? FindVisibleTabControl(DependencyObject root)
    {
        Queue<DependencyObject> pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        int visited = 0;
        while (pending.Count > 0 && visited++ < 4000)
        {
            DependencyObject current = pending.Dequeue();
            if (current is UIElement { IsVisible: false })
            {
                continue;
            }
            if (!ReferenceEquals(current, root) && current is System.Windows.Controls.TabControl tabControl && tabControl.Items.Count > 0)
            {
                return tabControl;
            }
            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(current);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            for (int i = 0; i < count; i++)
            {
                pending.Enqueue(VisualTreeHelper.GetChild(current, i));
            }
        }
        return null;
    }

    private static string ReadTabHeader(object? header)
    {
        switch (header)
        {
            case null:
                return string.Empty;
            case string text:
                return CleanTabName(text);
            case System.Windows.Controls.TextBlock block:
                return CleanTabName(block.Text);
            case DependencyObject element:
                foreach (object child in LogicalTreeHelper.GetChildren(element))
                {
                    string name = ReadTabHeader(child);
                    if (name.Length > 0)
                    {
                        return name;
                    }
                }
                return string.Empty;
            default:
                return CleanTabName(header.ToString());
        }
    }

    private static string CleanTabName(string? text)
    {
        string name = (text ?? string.Empty).Trim();
        if (
            name.Length == 0
            || name.All(static c =>
                char.IsWhiteSpace(c) || c is >= '\uE000' and <= '\uF8FF' || char.IsSurrogate(c) && !char.IsLetterOrDigit(c)
            )
        )
        {
            return string.Empty;
        }
        return name;
    }

    private void OnRpcTabSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.TabControl)
        {
            UpdateDiscordPresence();
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
        if (
            RootNavigation.SelectedPageIndex >= 0
            && RootNavigation.SelectedPageIndex < navigationItems.Length
            && navigationItems[RootNavigation.SelectedPageIndex] is NavigationItem { Content: var content } navigationItem
        )
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
        if (
            obj is Page { Title: { Length: > 0 } pageTitle }
            && (obj is Pages.ModCustomPage || FindNavigationItemForPageType(navigationItems, obj.GetType()) == null)
        )
        {
            item = pageTitle;
        }
        return (PageKey: text, Display: item ?? string.Empty);
    }

    private static NavigationItem? FindNavigationItemForPageType(NavigationItem[] items, Type pageType)
    {
        foreach (NavigationItem navigationItem in items)
        {
            if (navigationItem.PageType == pageType)
                return navigationItem;
        }
        return null;
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
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            StopLinuxGradientAnimation(reset: !enabled);
            if (enabled)
                StartGradientRendering();
            return;
        }
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        if (enabled && !Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
        {
            CompositionTarget.Rendering += CompositionTarget_Rendering;
        }
    }

    public void ApplySnow(bool enabled)
    {
        try
        {
            SnowCanvas?.SetActive(enabled && IsActive && !Voidstrap.UI.LinuxUiPerformance.ReducedMotion);
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
        if (_lastRobloxCheck == DateTime.MinValue && Interlocked.Exchange(ref _robloxCheckRunning, 1) == 0)
        {
            _lastRobloxCheck = DateTime.UtcNow;
            RefreshRobloxRunning();
            return _robloxRunningCached;
        }
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
            _robloxRunningCached = Voidstrap.Utility.Platform.IsLinux
                ? Voidstrap.Platform.Linux.LinuxSoberProcessProbe.IsRunningNow()
                : (
                    Voidstrap.Utility.Platform.IsMacOS
                        ? Voidstrap.Utility.MacRobloxProcesses.IsGameRunning()
                        : AnyProcessRunning(Voidstrap.Utility.Platform.RobloxPlayerProcessName)
                ) || AnyProcessRunning(Voidstrap.Utility.Platform.RobloxStudioProcessName);
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

    private static bool AnyProcessRunning(string name) => Voidstrap.Utility.ProcessScan.Any(name);

    private void UpdateDiscordPresence()
    {
        if (_discordClient == null || !_discordReady || !_discordRpcEnabled || !DiscordIpc.IsReady(_discordClient))
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
                catch { }
                _voidRpcSuppressed = true;
                App.Logger.WriteLine("DiscordRPC", "Roblox is running, hid the Voidstrap status so the game status shows");
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
        else if (RootFrame?.Content is Pages.ModCustomPage modPage && !string.IsNullOrWhiteSpace(text2))
        {
            details = text2;
            state = Voidstrap.Utility.RpcText.Mark("Using a Voidstrap mod") + ": " + modPage.PluginName;
        }
        else if (!string.IsNullOrEmpty(text) && _voidRpcPageDescriptions.TryGetValue(text, out (string, string) value))
        {
            details = Voidstrap.Utility.RpcText.Mark(value.Item1);
            state = Voidstrap.Utility.RpcText.Mark(value.Item2);
            string tabPath = GetSelectedTabPath();
            if (tabPath.Length > 0)
            {
                details = details + ": " + Voidstrap.Utility.RpcText.Mark(tabPath);
            }
        }
        else if (!string.IsNullOrWhiteSpace(text2))
        {
            string tabPath = GetSelectedTabPath();
            details =
                tabPath.Length > 0
                    ? Voidstrap.Utility.RpcText.Mark(text2) + ": " + Voidstrap.Utility.RpcText.Mark(tabPath)
                    : Voidstrap.Utility.RpcText.Mark(text2);
            state = Voidstrap.Utility.RpcText.Mark("Exploring the app");
        }
        else
        {
            details = Voidstrap.Utility.RpcText.Mark("Idle");
            state = Voidstrap.Utility.RpcText.Mark("Browsing the app");
        }
        details = DiscordPresenceGuard.Text(details);
        state = DiscordPresenceGuard.Text(state);
        if (details.Length < 2)
        {
            details = Voidstrap.Utility.Branding.Name;
        }
        if (state.Length < 2)
        {
            state = Voidstrap.Utility.RpcText.Mark("Exploring the app");
        }
        string imageUrl = context != null && VoidstrapPresence.IsWebUrl(context.ImageUrl) ? DiscordPresenceGuard.Key(context.ImageUrl) : "";
        string buttonUrl = context != null && VoidstrapPresence.IsWebUrl(context.ButtonUrl) ? context.ButtonUrl : "";
        string extra =
            imageUrl
            + "|"
            + buttonUrl
            + "|"
            + Voidstrap.Utility.Branding.Name
            + "|"
            + Voidstrap.Utility.RpcText.Render(details)
            + "|"
            + Voidstrap.Utility.RpcText.Render(state);
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
        string versionText = string.IsNullOrWhiteSpace(text3)
            ? Voidstrap.Utility.Branding.Name
            : Voidstrap.Utility.Branding.Name + " v" + text3;
        string VoidstrapLogo = Voidstrap.Utility.Branding.RpcLogoUrl;
        try
        {
            Assets assets =
                imageUrl.Length > 0
                    ? new Assets
                    {
                        LargeImageKey = imageUrl,
                        LargeImageText = DiscordPresenceGuard.Text(context!.ImageText.Length > 0 ? context.ImageText : details),
                        SmallImageKey = VoidstrapLogo,
                        SmallImageText = versionText,
                    }
                    : new Assets
                    {
                        LargeImageKey = VoidstrapLogo,
                        LargeImageText = versionText,
                        SmallImageKey =
                            _voidRpcSmallImageUrl.Length == 0 && _voidRpcAvatarLoading
                                ? App.RpcLoadingImageUrl
                                : DiscordPresenceGuard.Key(_voidRpcSmallImageUrl),
                        SmallImageText = string.IsNullOrEmpty(_voidRpcSmallImageUrl)
                            ? string.Empty
                            : DiscordPresenceGuard.Text(_voidRpcSmallImageText.Length > 0 ? _voidRpcSmallImageText : "Roblox"),
                    };
            DiscordRPC.Button[] buttons =
                buttonUrl.Length > 0
                    ? new DiscordRPC.Button[2]
                    {
                        new DiscordRPC.Button { Label = DiscordPresenceGuard.Label(context!.ButtonLabel, "Open"), Url = buttonUrl },
                        new DiscordRPC.Button
                        {
                            Label = Voidstrap.Utility.RpcText.Mark("Get") + " " + Voidstrap.Utility.Branding.Name,
                            Url = App.ProjectDownloadLink,
                        },
                    }
                    : new DiscordRPC.Button[2]
                    {
                        new DiscordRPC.Button
                        {
                            Label = Voidstrap.Utility.RpcText.Mark("Get") + " " + Voidstrap.Utility.Branding.Name,
                            Url = App.ProjectDownloadLink,
                        },
                        new DiscordRPC.Button { Label = "Join the Discord", Url = "https://discord.gg/bzdbHHytFR" },
                    };
            if (
                !_discordClient.SetPresenceSafe(
                    new DiscordRPC.RichPresence
                    {
                        Details = details,
                        State = state,
                        StatusDisplay = StatusDisplayType.Name,
                        Timestamps = new Timestamps(_voidRpcSessionStart),
                        Assets = assets,
                        Buttons = buttons,
                    }
                )
            )
            {
                return;
            }
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
        try
        {
            long loadedStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            InitializeNavigation();
            Voidstrap.Integrations.Plugins.PluginTheme.ReapplyActiveBrushes();
            Voidstrap.Integrations.Plugins.PluginWindow.ApplyPending(this);
            Voidstrap.Integrations.Plugins.PluginAnchors.ApplyAll();
            ApplyUiZoom();
            LoadSidebarWidth();
            ApplyLinuxWindowSize();
            if (Voidstrap.Utility.Platform.IsMacOS)
                FitToScreen();
            else if (Voidstrap.Utility.Platform.IsWindows)
                KeepInsideWorkArea();
            SetupNavShortcuts();
            _ = Dispatcher.BeginInvoke(new Action(ResetNavigationHistory), DispatcherPriority.ApplicationIdle);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Voidstrap.Utility.AppNotifications.Changed -= OnAppNotificationsChanged;
            Voidstrap.Utility.AppNotifications.Changed += OnAppNotificationsChanged;
            Voidstrap.Utility.AppNotifications.Reload();
            ApplyNotificationUnread(Voidstrap.Utility.AppNotifications.UnreadCount);
            if (!Voidstrap.Utility.Platform.IsWindows)
                StartGradientRendering();
            else if (App.Settings.Prop.GRADmentFR && !Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
            {
                CompositionTarget.Rendering -= CompositionTarget_Rendering;
                CompositionTarget.Rendering += CompositionTarget_Rendering;
            }
            if (App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw && !Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
            {
                SnowCanvas?.SetActive(IsActive);
            }
            else if (SnowCanvas != null)
            {
                SnowCanvas.SetActive(false);
            }
            Voidstrap.UI.LinuxUiPerformance.Duration("Settings window loaded work", loadedStarted);
            await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate { }, (DispatcherPriority)6);
            PlayIntro();
            Voidstrap.Integrations.FleasionCleanup.WarnAtStartup();
            _ = Dispatcher.BeginInvoke(
                new Action(() => Voidstrap.Integrations.Plugins.PluginAnchors.ApplyAll()),
                DispatcherPriority.ContextIdle
            );
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::MainWindow_Loaded", ex);
        }
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

    private readonly Dictionary<Wpf.Ui.Controls.NavigationItem, Thickness> _navItemMargins =
        new Dictionary<Wpf.Ui.Controls.NavigationItem, Thickness>();

    private bool? _sidebarIconMode;

    private double? _sidebarToggleTarget;

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
        (typeof(ChannelPage), Key.OemComma, "Ctrl+,"),
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
                    Content = BuildShortcutToolTip(
                        item.Content?.ToString() ?? "",
                        Wpf.Ui.Converters.ShortcutTextConverter.Format(shortcut.Label)
                    ),
                    Placement = PlacementMode.Right,
                    HorizontalOffset = 8,
                    VerticalOffset = 0,
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
        panel.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
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
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        keycap.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        keycap.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
        panel.Children.Add(keycap);
        return panel;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (
            (e.Key == Key.System ? e.SystemKey : e.Key) == Key.M
            && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)
        )
        {
            Voidstrap.Integrations.Plugins.PluginRestyle.ToggleSuspended();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ModalOpen)
        {
            CloseActiveModal();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ToggleCommandPalette();
            e.Handled = true;
            return;
        }
        if (_commandPaletteOpen)
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
        foreach (
            NavigationItem item in RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>())
        )
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
            App.Settings.SaveDeferred();
            ApplyUiZoomToOpenWindows();
            if (RootFrame?.Content is Pages.AppearancePage { DataContext: Voidstrap.UI.ViewModels.Settings.AppearanceViewModel appearance })
                appearance.OnPropertyChanged(nameof(Voidstrap.UI.ViewModels.Settings.AppearanceViewModel.UiZoomPercent));
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
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            (Action)
                delegate
                {
                    _uiZoomQueued = false;
                    if (!_isClosed)
                    {
                        ApplyUiZoom();
                    }
                }
        );
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

    private void KeepInsideWorkArea()
    {
        try
        {
            if (WindowState != System.Windows.WindowState.Normal)
                return;
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
                return;
            System.Drawing.Rectangle area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            System.Windows.Media.Matrix toDevice =
                PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? System.Windows.Media.Matrix.Identity;
            double scaleX = toDevice.M11 > 0 ? toDevice.M11 : 1.0;
            double scaleY = toDevice.M22 > 0 ? toDevice.M22 : 1.0;
            Rect work = new(area.Left / scaleX, area.Top / scaleY, area.Width / scaleX, area.Height / scaleY);
            if (work.Width <= 0.0 || work.Height <= 0.0)
                return;
            double wantedWidth = ActualWidth > 0 ? ActualWidth : Width;
            double wantedHeight = ActualHeight > 0 ? ActualHeight : Height;
            bool oversized = wantedWidth > work.Width + 0.5 || wantedHeight > work.Height + 0.5;
            double width = Math.Min(wantedWidth, oversized ? work.Width * 0.9 : work.Width);
            double height = Math.Min(wantedHeight, oversized ? work.Height * 0.9 : work.Height);
            if (MinWidth > width)
                MinWidth = width;
            if (MinHeight > height)
                MinHeight = height;
            double left = Math.Clamp(Left, work.Left, work.Right - width);
            double top = Math.Clamp(Top, work.Top, work.Bottom - height);
            if (Math.Abs(width - Width) > 0.5)
                Width = width;
            if (Math.Abs(height - Height) > 0.5)
                Height = height;
            if (Math.Abs(left - Left) > 0.5)
                Left = left;
            if (Math.Abs(top - Top) > 0.5)
                Top = top;
            if (oversized)
            {
                App.Logger.WriteLine(
                    "MainWindow::KeepInsideWorkArea",
                    "The window was larger than the screen's usable area, opening it maximized so the taskbar stays visible"
                );
                WindowState = System.Windows.WindowState.Maximized;
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::KeepInsideWorkArea", ex);
        }
    }

    private void FitToScreen()
    {
        try
        {
            if (WindowState == System.Windows.WindowState.Maximized || Voidstrap.UI.LinuxWindowMode.IsFullscreen(this))
                return;

            Rect work;
            if (Voidstrap.Utility.Platform.IsMacOS)
            {
                work = Voidstrap.Utility.ScreenMetrics.WorkArea;
            }
            else
            {
                System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.FromHandle(
                    new System.Windows.Interop.WindowInteropHelper(this).Handle
                );
                double dpiScale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                work = new Rect(
                    screen.WorkingArea.Left / dpiScale,
                    screen.WorkingArea.Top / dpiScale,
                    screen.WorkingArea.Width / dpiScale,
                    screen.WorkingArea.Height / dpiScale
                );
            }
            if (work.Width <= 0.0 || work.Height <= 0.0)
                return;
            double screenW = work.Width;
            double screenH = work.Height;
            double screenL = work.Left;
            double screenT = work.Top;

            double minW = MinWidth > 0 ? MinWidth : 640;
            double minH = MinHeight > 0 ? MinHeight : 440;

            if (Voidstrap.Utility.Platform.IsMacOS)
            {
                minW = MinWidth = Math.Min(minW, screenW * 0.95);
                minH = MinHeight = Math.Min(minH, screenH * 0.95);
            }
            double newW = Math.Max(minW, Math.Min(1071.0, screenW * 0.95));
            double newH = Math.Max(minH, Math.Min(690.0, screenH * 0.95));

            Width = newW;
            Height = newH;

            Left = screenL + (screenW - newW) / 2.0;
            Top = screenT + (screenH - newH) / 2.0;

            ApplyUiZoom();
        }
        catch { }
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
            UpdateSidebarToggleIcon();
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

    private bool? _rootNavigationShown;

    private readonly TranslateTransform _navigationSlide = new TranslateTransform();

    private void SetRootNavigationShown(bool shown)
    {
        if (_rootNavigationShown == shown)
            return;
        bool firstApply = _rootNavigationShown == null;
        _rootNavigationShown = shown;
        SidebarHost.BeginAnimation(WidthProperty, null);
        RootNavigation.BeginAnimation(OpacityProperty, null);
        _navigationSlide.BeginAnimation(TranslateTransform.XProperty, null);
        ReleasePinnedPage();
        if (firstApply || !IsLoaded)
        {
            RootNavigation.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        double from = SidebarHost.ActualWidth;
        double navigationWidth = double.IsNaN(RootNavigation.Width) ? SidebarDefaultWidth : RootNavigation.Width;
        double to = shown ? Math.Max(navigationWidth, SidebarResizer.Width) : SidebarResizer.Width;
        if (shown)
            RootNavigation.Visibility = Visibility.Visible;
        CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        ease.Freeze();
        PinPageForSidebarShift(to - from, ease, 220);
        SidebarHost.ClipToBounds = true;
        DoubleAnimation slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        slide.Completed += RootNavigationSlide_Completed;
        DoubleAnimation fade = new DoubleAnimation(shown ? 0.0 : 1.0, shown ? 1.0 : 0.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        fade.Freeze();
        if (!ReferenceEquals(RootNavigation.RenderTransform, _navigationSlide))
            RootNavigation.RenderTransform = _navigationSlide;
        DoubleAnimation move = new DoubleAnimation(
            Math.Min(0.0, from - navigationWidth),
            Math.Min(0.0, to - navigationWidth),
            TimeSpan.FromMilliseconds(220)
        )
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        move.Freeze();
        _navigationSlide.BeginAnimation(TranslateTransform.XProperty, move);
        RootNavigation.BeginAnimation(OpacityProperty, fade);
        SidebarHost.BeginAnimation(WidthProperty, slide);
    }

    private void RootNavigationSlide_Completed(object? sender, EventArgs e)
    {
        if (_rootNavigationShown == false)
            RootNavigation.Visibility = Visibility.Collapsed;
        _navigationSlide.BeginAnimation(TranslateTransform.XProperty, null);
        SidebarHost.BeginAnimation(WidthProperty, null);
        SidebarHost.ClipToBounds = false;
        ReleasePinnedPage();
    }

    private void PinPageForSidebarShift(double shift, IEasingFunction ease, int durationMs)
    {
        double pageScale = RootFrame.LayoutTransform is ScaleTransform zoom && zoom.ScaleX > 0 ? zoom.ScaleX : 1.0;
        double pageWidth = RootFrame.ActualWidth * pageScale;
        if (pageWidth <= 0 || pageWidth - shift <= 0)
            return;
        RootFrame.Width = (pageWidth - shift) / pageScale;
        RootFrame.HorizontalAlignment = HorizontalAlignment.Left;
        PageSurface.ClipToBounds = true;
        ThicknessAnimation hold = new ThicknessAnimation(
            new Thickness(shift, 0, 0, 0),
            new Thickness(0),
            TimeSpan.FromMilliseconds(durationMs)
        )
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop,
        };
        hold.Freeze();
        RootFrame.BeginAnimation(MarginProperty, hold);
    }

    private void ReleasePinnedPage()
    {
        RootFrame.BeginAnimation(MarginProperty, null);
        if (double.IsNaN(RootFrame.Width))
            return;
        RootFrame.Width = double.NaN;
        RootFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
        PageSurface.ClipToBounds = false;
    }

    private void SaveSidebarWidth()
    {
        App.Settings.Prop.SidebarWidth = ((RootNavigation != null) ? RootNavigation.Width : SidebarDefaultWidth);
        App.Settings.Save();
    }

    private static readonly Geometry SidebarOpenIcon = Geometry.Parse("M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2H5a2 2 0 0 1 -2 -2V5a2 2 0 0 1 2 -2z M9 3v18 M16 9l-3 3 3 3");

    private static readonly Geometry SidebarClosedIcon = Geometry.Parse("M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1 -2 2H5a2 2 0 0 1 -2 -2V5a2 2 0 0 1 2 -2z M9 3v18 M13 9l3 3 -3 3");

    private void UpdateSidebarToggleIcon()
    {
        bool closed = RootFrame.Content switch
        {
            LibraryPage page => page.IsSidebarCollapsed,
            PrivateServersPage page => page.IsSidebarCollapsed,
            AccountsPage page => page.IsSidebarCollapsed,
            _ => _sidebarToggleTarget is double target ? target < SidebarIconThreshold : _sidebarIconMode == true,
        };
        SidebarToggleIcon.Data = closed ? SidebarClosedIcon : SidebarOpenIcon;
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is LibraryPage libraryPage)
        {
            libraryPage.ToggleSidebar();
            AnimatePageHeader(-14.0);
            return;
        }
        if (RootFrame.Content is PrivateServersPage privateServersPage)
        {
            privateServersPage.ToggleSidebar();
            AnimatePageHeader(-14.0);
            return;
        }
        if (RootFrame.Content is AccountsPage accountsPage)
        {
            accountsPage.ToggleSidebar();
            AnimatePageHeader(-14.0);
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
            catch { }
        }
        bool interrupted = _sidebarToggleTarget.HasValue;
        double from =
            interrupted && RootNavigation.ActualWidth > 0
                ? RootNavigation.ActualWidth
                : (double.IsNaN(RootNavigation.Width) ? SidebarDefaultWidth : RootNavigation.Width);
        bool collapsed = interrupted ? _sidebarToggleTarget!.Value < SidebarIconThreshold : _sidebarIconMode == true;
        double to = collapsed ? Math.Max(SidebarIconThreshold, _sidebarExpandedWidth) : SidebarCollapsedWidth;
        RootNavigation.BeginAnimation(WidthProperty, null);
        AnimatePageHeader(to > from ? -18.0 : 18.0);
        if (Math.Abs(to - from) > 1.0)
        {
            QuinticEase ease = new QuinticEase { EasingMode = EasingMode.EaseInOut };
            ease.Freeze();
            DoubleAnimation slide = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = ease,
                FillBehavior = FillBehavior.Stop,
            };
            slide.Completed += SidebarToggleAnimation_Completed;
            ReleasePinnedPage();
            if (!interrupted)
                PinPageForSidebarShift(to - from, ease, 260);
            _sidebarToggleTarget = to;
            UpdateSidebarToggleIcon();
            RootNavigation.Tag = "full";
            RootNavigation.BeginAnimation(WidthProperty, slide);
        }
        else
        {
            _sidebarToggleTarget = null;
            ReleasePinnedPage();
            ApplySidebarWidth(to);
            SaveSidebarWidth();
        }
    }

    private void SidebarToggleAnimation_Completed(object? sender, EventArgs e)
    {
        ReleasePinnedPage();
        double? target = _sidebarToggleTarget;
        _sidebarToggleTarget = null;
        if (RootNavigation == null || target == null)
            return;
        ApplySidebarWidth(target.Value);
        SaveSidebarWidth();
    }

    private void SidebarResizer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RootNavigation?.BeginAnimation(WidthProperty, null);
        _sidebarToggleTarget = null;
        ReleasePinnedPage();
        if (e.ClickCount > 1)
        {
            _sidebarResizing = false;
            try
            {
                SidebarResizer.ReleaseMouseCapture();
            }
            catch { }
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
            catch { }
            SaveSidebarWidth();
        }
    }

    private const double LinuxFooterButtonHeight = 32;

    private void MatchLaunchButtonToFooter()
    {
        FooterSaveButton.MinHeight = LinuxFooterButtonHeight;
        FooterCloseButton.MinHeight = LinuxFooterButtonHeight;
        InstallLaunchButton.MinHeight = 0;
        InstallLaunchButton.SetBinding(
            FrameworkElement.HeightProperty,
            new System.Windows.Data.Binding(nameof(FrameworkElement.ActualHeight))
            {
                Source = FooterSaveButton,
                Mode = System.Windows.Data.BindingMode.OneWay,
            }
        );
        InstallLaunchButton.VerticalAlignment = VerticalAlignment.Center;
        InstallLaunchButton.ApplyTemplate();
        if (InstallLaunchButton.Template?.FindName("PART_DropDownButton", InstallLaunchButton) is FrameworkElement dropDown)
        {
            dropDown.SetBinding(
                FrameworkElement.WidthProperty,
                new System.Windows.Data.Binding(nameof(FrameworkElement.ActualHeight))
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self),
                    Mode = System.Windows.Data.BindingMode.OneWay,
                }
            );
        }
    }

    private void PlayIntro()
    {
        if (_introPlayed)
        {
            return;
        }
        _introPlayed = true;
        FinishIntro(null);
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
        if (_startupView is string startupView)
        {
            _startupView = null;
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => OpenView(startupView));
        }
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
        catch { }
        if (IntroOverlay != null)
        {
            IntroOverlay.Visibility = Visibility.Collapsed;
        }
        LiftTopNav();
        if (_startupWorkStarted)
            ScheduleLaunchTargetWarmup();
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
        if (PagesMenuPopup != null)
        {
            PagesMenuPopup.IsOpen = false;
        }
        if (NotificationsOpen)
        {
            CloseSimpleModal();
        }
    }

    private void ReleaseOrphanedCapture()
    {
        IInputElement? captured = Mouse.Captured;
        if (captured == null)
        {
            return;
        }
        if (ModalOpen || AppMenuPopup?.IsOpen == true)
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
        bool wasOpen = NotificationsOpen;
        CloseTopBarMenus();
        if (wasOpen)
        {
            return;
        }
        LoadTopBarNotifications();
        OpenSimpleModal(NotificationsPanel, NotificationsPanelScale);
    }

    private bool NotificationsOpen => _simpleModalOpen && ReferenceEquals(_modalPanel, NotificationsPanel);

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
            CloseSimpleModal();
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
        catch { }
    }

    private void RefreshNotificationBadge()
    {
        if (_isClosed)
            return;
        if (NotificationsOpen)
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
            NotificationsBadgeScale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.72, 1.0, duration) { EasingFunction = ease }
            );
            NotificationsBadgeScale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.72, 1.0, duration) { EasingFunction = ease }
            );
            NotificationsBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1.0, duration) { EasingFunction = ease });
        }
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        CloseTopBarMenus();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == System.Windows.WindowState.Minimized)
            StopLinuxGradientAnimation(reset: false);
        CloseTopBarMenus();
        if (Voidstrap.Utility.Platform.IsWindows && WindowState != System.Windows.WindowState.Minimized && IsLoaded)
        {
            _state.MaximizedUpdateV2 = WindowState == System.Windows.WindowState.Maximized;
            App.State.SaveDeferred();
        }
    }

    private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        QueueUiZoom();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        UpdateButtonContent();
        RefreshManageModsStatus();
        if (App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw && !Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
        {
            SnowCanvas?.SetActive(true);
        }
        try
        {
            _visibilityTimer.Start();
            if (!Voidstrap.Utility.Platform.IsWindows)
                StartGradientRendering();
            else if (App.Settings.Prop.GRADmentFR && !Voidstrap.UI.LinuxUiPerformance.ReducedMotion)
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
        catch { }
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
            StopLinuxGradientAnimation(reset: false);
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
        catch { }
    }

    private void OnNavigationFailed(object? sender, Wpf.Ui.Controls.Navigation.NavigationFailedEventArgs e)
    {
        Exception root = e.Exception;
        while (root.InnerException != null)
        {
            root = root.InnerException;
        }
        App.Logger.WriteException("MainWindow::OnNavigationFailed", root);
        Frontend.ShowMessageBox(
            $"The {e.PageTag} page could not be opened on this system.\n\n{root.GetType().Name}: {root.Message.Split('\n')[0]}",
            MessageBoxImage.Warning
        );
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
        mainWindowViewModel.RequestSaveNoticeEvent = (EventHandler)
            Delegate.Combine(mainWindowViewModel.RequestSaveNoticeEvent, new EventHandler(OnRequestSaveNotice));
        mainWindowViewModel.RequestSaveLaunchNoticeEvent = (EventHandler)
            Delegate.Combine(mainWindowViewModel.RequestSaveLaunchNoticeEvent, new EventHandler(OnRequestSaveLaunchNotice));
        mainWindowViewModel.RequestCloseWindowEvent = (EventHandler)
            Delegate.Combine(mainWindowViewModel.RequestCloseWindowEvent, new EventHandler(OnRequestCloseWindow));
    }

    private void UpdateButtonContent()
    {
        if (InstallLaunchButton == null)
        {
            return;
        }
        string content;
        if (
            base.DataContext is MainWindowViewModel clientVm
            && !string.IsNullOrEmpty(clientVm.SelectedLaunchClient)
            && Voidstrap.Utility.ClassicClients.IsClientInstalled(clientVm.SelectedLaunchClient)
        )
        {
            content = "Save and Launch";
        }
        else
        {
            bool studio = base.DataContext is MainWindowViewModel mainWindowViewModel && mainWindowViewModel.SelectedLaunchModeIndex == 1;
            bool installed = IsLaunchTargetInstalled(studio);
            if (Voidstrap.Utility.Platform.IsLinux)
            {
                string runtime =
                    Voidstrap.Utility.LinuxRuntimeChoice.UsesRosemary(
                        studio ? Voidstrap.Enums.LaunchMode.Studio : Voidstrap.Enums.LaunchMode.Player
                    )
                        ? "Rosemary"
                    : studio ? "Vinegar"
                    : "Sober";
                content = installed ? "Save and Launch " + runtime : "Install " + runtime;
            }
            else
            {
                content = (
                    studio ? (installed ? "Save and Launch Studio" : "Install Studio") : (installed ? "Save and Launch" : "Install")
                );
            }
        }
        if (!object.Equals(LaunchActionText.Text, content))
        {
            LaunchActionText.Text = content;
        }
    }

    private static bool IsLaunchTargetInstalled(bool studio)
    {
        try
        {
            if (Voidstrap.Utility.Platform.IsLinux)
            {
                if (
                    Voidstrap.Utility.LinuxRuntimeChoice.UsesRosemary(
                        studio ? Voidstrap.Enums.LaunchMode.Studio : Voidstrap.Enums.LaunchMode.Player
                    )
                )
                    return Voidstrap.Platform.Linux.LinuxRosemary.IsRuntimeInstalled
                        && Voidstrap.Platform.Linux.LinuxRosemary.IsApplicationInstalled(
                            studio
                                ? Voidstrap.Platform.Linux.RosemaryApplication.Studio
                                : Voidstrap.Platform.Linux.RosemaryApplication.Player
                        );
                return studio
                    ? Voidstrap.Platform.Linux.LinuxVinegarStudioRuntimeProvider.IsInstalled()
                    : Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.IsInstalled();
            }

            if (Voidstrap.Utility.Platform.IsMacOS)
                return Voidstrap.Platform.MacOS.MacOSRobloxRuntimeProvider.IsInstalled(
                    studio ? Voidstrap.Platform.RuntimeKind.Studio : Voidstrap.Platform.RuntimeKind.Player
                );

            Voidstrap.AppData.IAppData appData = (
                studio
                    ? ((Voidstrap.AppData.IAppData)new Voidstrap.AppData.RobloxStudioData())
                    : ((Voidstrap.AppData.IAppData)new Voidstrap.AppData.RobloxPlayerData())
            );
            if (
                !string.IsNullOrEmpty(appData.State.VersionGuid)
                && (File.Exists(appData.ExecutablePath) || Voidstrap.Utility.RobloxInstallCompression.IsCompressed(appData))
            )
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
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            DiscardOversizedLinuxState();
        }
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

    private static void DiscardOversizedLinuxState()
    {
        Rect work = Voidstrap.Utility.ScreenMetrics.WorkArea;
        if (work.Width <= 0.0 || work.Height <= 0.0)
        {
            return;
        }
        double scale = Voidstrap.UI.LinuxInterfaceScale.Factor;
        if (
            _state.WidthUpdateV2 * scale < work.Width - LinuxScreenMargin
            && _state.HeightUpdateV2 * scale < work.Height - LinuxScreenMargin
        )
        {
            return;
        }
        _state.WidthUpdateV2 = 0.0;
        _state.HeightUpdateV2 = 0.0;
        _state.LeftUpdateV2 = 0.0;
        _state.TopUpdateV2 = 0.0;
    }

    private const double LinuxScreenMargin = 24.0;

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
                int page => page,
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
        SettingsSavedSnackbar.Show();
    }

    private void OnRequestSaveLaunchNotice(object? sender, EventArgs e)
    {
        SettingsSavedLaunchSnackbar.Show();
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
        RestartNotificationCard.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            }
        );
        RestartNotificationTranslate.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(28.0, 0.0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop,
            }
        );
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
        if (
            RestartNotificationCard.Parent is Panel parent
            && parent.Children.Count > 0
            && !ReferenceEquals(parent.Children[parent.Children.Count - 1], RestartNotificationCard)
        )
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

    public void ShowAiActivity(string title, string message)
    {
        if (_isClosed || AiActivitySnackbar == null)
            return;
        AiActivitySnackbar.Title = title;
        AiActivitySnackbar.Message = message;
        AiActivitySnackbar.Show();
    }

    private async Task ShowAlreadyRunningSnackbarAsync()
    {
        try
        {
            await Task.Delay(225, _lifetimeCts.SafeToken());
            if (!_isClosed && !((DispatcherObject)this).Dispatcher.HasShutdownStarted)
            {
                _ = ((DispatcherObject)this).Dispatcher.InvokeAsync<bool?>((Func<bool?>)(() => AlreadyRunningSnackbar?.Show()));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async void OnRequestCloseWindow(object? sender, EventArgs e)
    {
        try
        {
            await Task.Yield();
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::OnRequestCloseWindow", ex);
        }
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
        InstallLaunchButton.DropDownClick -= LaunchTargetButton_Click;
        StopLaunchTargetWarmupTimer();
        _openViewSource?.RemoveHook(OpenViewHook);
        _openViewSource = null;
        Voidstrap.UI.TaskbarJumpList.UnpublishWindow(_publishedHandle);
        _publishedHandle = 0;
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
        StopLinuxGradientAnimation(reset: false);
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
        PageSidebar.CollapsedChanged -= UpdateSidebarToggleIcon;
        Wpf.Ui.Controls.Navigation.NavigationTiming.PageCreated -= OnNavigationPageCreated;
        AppMenuPopup.Closing -= AppMenuPopup_Closing;
        AppMenuPopup.Closed -= OverlayPopup_Closed;
        Voidstrap.Integrations.Plugins.PluginModPages.Changed -= OnModPagesChanged;
        RootNavigation.NavigationFailed -= OnNavigationFailed;
        RootNavigation.Navigated -= SaveNavigation;
        RootNavigation.Navigated -= RootNavigation_RpcNavigated;
        GlobalBackground.Changed -= OnGlobalBackgroundChanged;
        Voidstrap.UI.LinuxUiPerformance.ReducedMotionChanged -= OnLinuxReducedMotionChanged;
        ContentRendered -= OnStartupContentRendered;
        PreviewMouseMove -= OnLinuxGradientMouseMove;
        MouseLeave -= RootGrid_MouseLeave;
        RestartNotificationService.Changed -= OnRestartRequirementsChanged;
        ManagedModStore.Changed -= OnManagedModsChanged;

        Voidstrap.Integrations.Plugins.PluginHost.BannersChanged -= OnModBannersChanged;

        Voidstrap.Integrations.Plugins.PluginHost.PaneInvalidated -= OnModBannerPaneInvalidated;
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
        catch { }
        ReleaseDiscordRpcClient();
        try
        {
            (TryFindResource("IntroStoryboard") as Storyboard)?.Remove(IntroOverlay);
        }
        catch { }
        ReleaseBackgroundResources();
        ClearRetainedUiState();
        Voidstrap.Utility.DynamicRenderSystem.ClearCache();
        SpotifyPlayer.Failed -= SpotifyPlayer_Failed;
        SpotifyPlayer.Shutdown();
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
            App.Logger.WriteLine(
                "MainWindow::StartTestModeLaunch",
                "The settings view model was already released, launching without saving"
            );
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
        if (
            Voidstrap.Utility.Platform.IsWindows
            || base.WindowState != System.Windows.WindowState.Normal
            || Voidstrap.UI.LinuxWindowMode.IsFullscreen(this)
        )
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
            catch { }
            try
            {
                media.Close();
            }
            catch { }
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
        catch { }
    }

    private void ClearRetainedUiState()
    {
        SettingChangeNotifier.Failed -= OnSettingChangeFailed;
        Voidstrap.Utility.LinuxRuntimeChoice.Changed -= OnLinuxRuntimeChanged;
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
        _privateServersPage = null;
        _accountsPage?.Release();
        _accountsPage = null;
        _robloxNewsPage = null;
        _lastPage = null;
        try
        {
            RootNavigation.ClearCache();
            RootFrame.NavigationService?.StopLoading();
            RootFrame.Content = null;
            while (RootFrame.NavigationService?.RemoveBackEntry() != null) { }
        }
        catch { }
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
        double scale = Voidstrap.UI.LinuxInterfaceScale.For(this);
        if (
            Voidstrap.Utility.Platform.IsLinux
            && Voidstrap.UI.LinuxWindowMode.TryGetRestorePlacement(this, out bool fullscreenMaximized, out Rect fullscreenBounds)
        )
        {
            _state.MaximizedUpdateV2 = fullscreenMaximized;
            _state.WidthUpdateV2 = fullscreenBounds.Width / scale;
            _state.HeightUpdateV2 = fullscreenBounds.Height / scale;
            _state.TopUpdateV2 = fullscreenBounds.Top;
            _state.LeftUpdateV2 = fullscreenBounds.Left;
            SaveSanitizedWindowState();
            return;
        }
        if (Voidstrap.Utility.Platform.IsLinux && Voidstrap.UI.LinuxWindowMode.IsCompositorMaximized(this))
        {
            _state.MaximizedUpdateV2 = true;
            SaveSanitizedWindowState();
            return;
        }
        bool maximized = base.WindowState == System.Windows.WindowState.Maximized;
        _state.MaximizedUpdateV2 = maximized;
        if (maximized && !base.RestoreBounds.IsEmpty)
        {
            _state.WidthUpdateV2 = base.RestoreBounds.Width / scale;
            _state.HeightUpdateV2 = base.RestoreBounds.Height / scale;
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
            else if (
                Voidstrap.UI.LinuxWindowSize.TryGet(Title, out int nativeWidth, out int nativeHeight)
                && nativeWidth > 0
                && nativeHeight > 0
            )
            {
                _state.WidthUpdateV2 = nativeWidth / scale;
                _state.HeightUpdateV2 = nativeHeight / scale;
            }
            else
            {
                _state.WidthUpdateV2 = base.ActualWidth / scale;
                _state.HeightUpdateV2 = base.ActualHeight / scale;
            }
            _state.TopUpdateV2 = base.Top;
            _state.LeftUpdateV2 = base.Left;
        }
        SaveSanitizedWindowState();
    }

    private static void SaveSanitizedWindowState()
    {
        _state.WidthUpdateV2 = FiniteOrZero(_state.WidthUpdateV2);
        _state.HeightUpdateV2 = FiniteOrZero(_state.HeightUpdateV2);
        _state.LeftUpdateV2 = FiniteOrZero(_state.LeftUpdateV2);
        _state.TopUpdateV2 = FiniteOrZero(_state.TopUpdateV2);
        App.State.Save();
    }

    private static double FiniteOrZero(double value)
    {
        return double.IsFinite(value) ? value : 0.0;
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
        if (!Voidstrap.Utility.Platform.IsWindows && (pageType == typeof(DownloadsPage) || pageType == typeof(ExtensionPage)))
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

    private void Button_Click_1(object sender, RoutedEventArgs e) { }

    private void Button_Click_2(object sender, RoutedEventArgs e) { }

    private int _manageModsStatusVersion;

    private void OnManagedModsChanged()
    {
        if (_isClosed)
            return;
        RefreshManageModsStatus();
    }

    private void RefreshManageModsStatus()
    {
        if (_isClosed)
            return;
        int version = Interlocked.Increment(ref _manageModsStatusVersion);
        _ = Task.Run(() =>
        {
            int enabledCount;
            try
            {
                enabledCount = ManagedModStore.GetEnabledModCount();
            }
            catch
            {
                enabledCount = 0;
            }
            if (_isClosed || version != Volatile.Read(ref _manageModsStatusVersion))
                return;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ApplyManageModsStatus(enabledCount, version)));
            }
            catch { }
        });
    }

    private void ApplyManageModsStatus(int enabledCount, int version)
    {
        if (_isClosed || version != Volatile.Read(ref _manageModsStatusVersion) || ManageModsButton == null)
            return;
        if (enabledCount > 0)
        {
            string status =
                enabledCount == 1
                    ? Strings.Ui_Main_OneModApplied
                    : string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Ui_Main_ModsApplied, enabledCount);
            ManageModsAppliedText.Text = status;
            ManageModsAppliedBadge.Visibility = Visibility.Visible;
        }
        else
        {
            ManageModsAppliedText.Text = string.Empty;
            ManageModsAppliedBadge.Visibility = Visibility.Collapsed;
        }
    }

    private void ManageModsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosed)
            return;
        CloseOverlayPopups();
        if (RootFrame.Content is ModsPage currentModsPage && currentModsPage.IsLoaded)
        {
            currentModsPage.ShowMyModsTab();
            return;
        }
        ModsPage.PendingMyModsTab = true;
        if (RootFrame.Content is OverlaysPage or ModGeneratorPage && RootFrame.CanGoBack)
        {
            RootFrame.GoBack();
            return;
        }
        if (!RootNavigation.Navigate(typeof(ModsPage)))
        {
            if (RootFrame.Content is ModsPage existing)
            {
                existing.ShowMyModsTab();
            }
            else
            {
                RootFrame.Navigate(new ModsPage());
            }
            return;
        }
        if (RootFrame.Content is ModsPage navigatedModsPage && navigatedModsPage.IsLoaded)
        {
            navigatedModsPage.ShowMyModsTab();
        }
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
        if (Voidstrap.Utility.Platform.IsWindows && downloads.ClientItems.Count == 0)
            downloads.RefreshClassic();

        List<object> entries = !Voidstrap.Utility.Platform.IsWindows
            ? [.. downloads.Items]
            : [.. downloads.Items, .. downloads.ClientItems];
        if (LaunchTargetList.ItemsSource is not IList<object> shown || !shown.SequenceEqual(entries))
            LaunchTargetList.ItemsSource = entries;
        object? current = null;
        if (base.DataContext is MainWindowViewModel vm)
        {
            string code = vm.SelectedLaunchClient;
            if (!string.IsNullOrEmpty(code))
                current = downloads.ClientItems.FirstOrDefault(client =>
                    string.Equals(client.Code, code, StringComparison.OrdinalIgnoreCase)
                );
            Voidstrap.Enums.LaunchMode mode =
                vm.SelectedLaunchModeIndex == 1 ? Voidstrap.Enums.LaunchMode.Studio : Voidstrap.Enums.LaunchMode.Player;
            if (Voidstrap.Utility.Platform.IsLinux)
            {
                bool rosemary = Voidstrap.Utility.LinuxRuntimeChoice.UsesRosemary(mode);
                current ??= downloads.Items.FirstOrDefault(download => download.LaunchMode == mode && download.IsRosemary == rosemary);
            }
            current ??= downloads.Items.FirstOrDefault(download => download.LaunchMode == mode);
        }
        if (!ReferenceEquals(LaunchTargetList.SelectedItem, current))
            LaunchTargetList.SelectedItem = current;
    }

    private void RefreshLaunchTargetStates()
    {
        if (!_launchTargetOverlayOpen || _isClosed)
            return;
        DownloadsViewModel downloads = DownloadsViewModel.Shared;
        foreach (DownloadsViewModel.DownloadItem download in downloads.Items)
            download.RefreshInstallState();
        if (Voidstrap.Utility.Platform.IsWindows)
        {
            foreach (DownloadsViewModel.ClientItem client in downloads.ClientItems)
                client.RefreshInstallState();
        }
        PopulateLaunchTargets();
    }

    private void LaunchTargetList_ItemChosen(object? sender, object item)
    {
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            switch (item)
            {
                case DownloadsViewModel.ClientItem client:
                    mainWindowViewModel.SelectedLaunchClient = client.Code;
                    break;
                case DownloadsViewModel.DownloadItem download:
                    mainWindowViewModel.SelectedLaunchClient = "";
                    if (Voidstrap.Utility.Platform.IsLinux)
                    {
                        Voidstrap.Utility.LinuxRuntimeChoice.Select(download.LaunchMode, download.RuntimeKey);
                        bool rosemary = Voidstrap.Utility.LinuxRuntimeChoice.UsesRosemary(download.LaunchMode);
                        if (rosemary && RootFrame.Content is SoberPage)
                            RootNavigation.Navigate(typeof(RosemaryPage));
                        else if (!rosemary && RootFrame.Content is RosemaryPage)
                            RootNavigation.Navigate(typeof(SoberPage));
                    }
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

    private bool _commandPaletteOpen;

    private Border? _modalPanel;

    private ScaleTransform? _modalScale;

    private bool _simpleModalOpen;

    private bool ModalOpen => _launchTargetOverlayOpen || _commandPaletteOpen || _simpleModalOpen;

    private int _launchTargetOverlayGeneration;

    private static readonly IEasingFunction ModalScaleEase = CreateLaunchTargetEase(new QuinticEase { EasingMode = EasingMode.EaseOut });

    private static readonly IEasingFunction ModalFadeEase = CreateLaunchTargetEase(new QuarticEase { EasingMode = EasingMode.EaseInOut });

    private const double ModalScaleOffset = 1.1;

    private static IEasingFunction CreateLaunchTargetEase(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }

    private static readonly TimeSpan ModalScaleDuration = TimeSpan.FromMilliseconds(667);

    private static readonly TimeSpan ModalOpenFadeDuration = TimeSpan.FromMilliseconds(187);

    private static readonly TimeSpan ModalCloseFadeDuration = TimeSpan.FromMilliseconds(100);

    private const int LaunchTargetFrameRate = 240;

    private void OpenLaunchTargetOverlay()
    {
        CloseTopBarMenus();
        PopulateLaunchTargets();
        _launchTargetOverlayOpen = true;
        InstallLaunchButton.IsDropDownOpen = true;
        OpenModalOverlay(LaunchTargetPanel, LaunchTargetPanelScale);
        LaunchTargetList.FocusList();
    }

    private Border[] ModalPanels =>
        [LaunchTargetPanel, CommandPalettePanel, NotificationsPanel, ReleaseNotesPanel, RiShadeReleasePanel, NewsItemPanel];

    private bool ModalIsSidebar => ReferenceEquals(_modalPanel, LaunchTargetPanel);

    private double SidebarOffset => Math.Max(LaunchTargetPanel.ActualWidth, LaunchTargetPanel.Width);

    private void OpenModalOverlay(Border panel, ScaleTransform scale)
    {
        int generation = ++_launchTargetOverlayGeneration;
        bool fresh =
            LaunchTargetOverlay.Visibility != Visibility.Visible
            || !LaunchTargetOverlay.IsHitTestVisible
            || !ReferenceEquals(_modalPanel, panel);
        _modalPanel = panel;
        _modalScale = scale;
        foreach (Border candidate in ModalPanels)
            candidate.Visibility = ReferenceEquals(panel, candidate) ? Visibility.Visible : Visibility.Collapsed;
        if (Voidstrap.Utility.Platform.IsWindows && panel.Effect == null && !ModalIsSidebar)
            panel.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 32,
                ShadowDepth = 8,
                Direction = 270,
                Opacity = 0.37,
                Color = Colors.Black,
            };
        if (fresh)
        {
            LaunchTargetOverlay.BeginAnimation(OpacityProperty, null);
            LaunchTargetOverlay.Opacity = 0;
            if (ModalIsSidebar)
            {
                SetModalScale(scale, 1.0);
                LaunchTargetPanelSlide.BeginAnimation(TranslateTransform.XProperty, null);
                LaunchTargetPanelSlide.X = SidebarOffset;
                LaunchTargetScrim.BeginAnimation(OpacityProperty, null);
                LaunchTargetScrim.Opacity = 0;
            }
            else
            {
                SetModalScale(scale, ModalScaleOffset);
            }
        }
        if (!Voidstrap.Utility.Platform.UsesPortableUi)
            panel.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        LaunchTargetOverlay.Visibility = Visibility.Visible;
        LaunchTargetOverlay.IsHitTestVisible = true;
        if (fresh)
        {
            LaunchTargetOverlay.UpdateLayout();
            ShowModalSnapshot(panel, scale);
        }
        AnimateLaunchTargetOverlay(1.0, 1.0, ModalOpenFadeDuration, generation, false);
    }

    private void CloseModalOverlay()
    {
        if (ModalOpen || _modalPanel == null)
            return;
        int generation = ++_launchTargetOverlayGeneration;
        LaunchTargetOverlay.IsHitTestVisible = false;
        if (Voidstrap.Utility.Platform.IsWindows)
            _modalPanel.CacheMode = new BitmapCache { SnapsToDevicePixels = true };
        if (_modalSnapshotPanel == null && _modalScale != null)
            ShowModalSnapshot(_modalPanel, _modalScale);
        if (ModalIsSidebar)
            AnimateLaunchTargetOverlay(0.0, 1.0, ModalOpenFadeDuration, generation, true);
        else
            AnimateLaunchTargetOverlay(0.0, ModalScaleOffset, ModalCloseFadeDuration, generation, true);
    }

    private bool _launchTargetsWarmed;

    private DispatcherTimer? _launchTargetWarmupTimer;

    private DispatcherTimer? _launchTargetSettleTimer;

    private void ScheduleLaunchTargetWarmup()
    {
        if (_launchTargetsWarmed || _launchTargetWarmupTimer != null)
            return;
        _launchTargetWarmupTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(2) };
        _launchTargetWarmupTimer.Tick += LaunchTargetWarmupTimer_Tick;
        _launchTargetWarmupTimer.Start();
    }

    private void LaunchTargetWarmupTimer_Tick(object? sender, EventArgs e)
    {
        StopLaunchTargetWarmupTimer();
        WarmLaunchTargets();
    }

    private void LaunchTargetSettleTimer_Tick(object? sender, EventArgs e)
    {
        StopLaunchTargetSettleTimer();
        FinishLaunchTargetWarmup();
    }

    private void StopLaunchTargetWarmupTimer()
    {
        if (_launchTargetWarmupTimer != null)
        {
            _launchTargetWarmupTimer.Stop();
            _launchTargetWarmupTimer.Tick -= LaunchTargetWarmupTimer_Tick;
            _launchTargetWarmupTimer = null;
        }
        StopLaunchTargetSettleTimer();
    }

    private void StopLaunchTargetSettleTimer()
    {
        if (_launchTargetSettleTimer == null)
            return;
        _launchTargetSettleTimer.Stop();
        _launchTargetSettleTimer.Tick -= LaunchTargetSettleTimer_Tick;
        _launchTargetSettleTimer = null;
    }

    private void WarmLaunchTargets()
    {
        if (_launchTargetsWarmed || ModalOpen || _isClosed || LaunchTargetOverlay == null)
            return;
        _launchTargetsWarmed = true;
        try
        {
            PopulateLaunchTargets();
            LaunchTargetOverlay.IsHitTestVisible = false;
            LaunchTargetOverlay.Opacity = LaunchTargetWarmOpacity;
            LaunchTargetOverlay.Visibility = Visibility.Visible;
            LaunchTargetOverlay.UpdateLayout();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MainWindow::WarmLaunchTargets", "The version picker could not be prepared: " + ex.Message);
            FinishLaunchTargetWarmup();
            return;
        }
        _launchTargetSettleTimer = new DispatcherTimer(DispatcherPriority.ContextIdle) { Interval = LaunchTargetWarmDuration };
        _launchTargetSettleTimer.Tick += LaunchTargetSettleTimer_Tick;
        _launchTargetSettleTimer.Start();
    }

    private const double LaunchTargetWarmOpacity = 0.01;

    private static readonly TimeSpan LaunchTargetWarmDuration = TimeSpan.FromMilliseconds(300);

    private void FinishLaunchTargetWarmup()
    {
        if (ModalOpen || LaunchTargetOverlay == null)
            return;
        LaunchTargetOverlay.Visibility = Visibility.Hidden;
        LaunchTargetOverlay.Opacity = 0;
    }

    private void CloseLaunchTargetOverlay()
    {
        if (!_launchTargetOverlayOpen)
            return;
        _launchTargetOverlayOpen = false;
        InstallLaunchButton.IsDropDownOpen = false;
        CloseModalOverlay();
    }

    private void AnimateLaunchTargetOverlay(double opacity, double scaleTo, TimeSpan fadeDuration, int generation, bool finishClose)
    {
        DoubleAnimation fade = CreateLaunchTargetAnimation(opacity, fadeDuration, ModalFadeEase);
        DoubleAnimation scale = CreateLaunchTargetAnimation(scaleTo, ModalScaleDuration, ModalScaleEase);
        DoubleAnimation done = finishClose ? fade : scale;
        void Completed(object? sender, EventArgs e)
        {
            done.Completed -= Completed;
            if (finishClose)
                FinishLaunchTargetClose(generation);
            else
                FinishLaunchTargetOpen(generation);
        }
        done.Completed += Completed;
        if (ModalIsSidebar)
        {
            LaunchTargetOverlay.BeginAnimation(OpacityProperty, null);
            LaunchTargetOverlay.Opacity = 1;
            LaunchTargetScrim.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
            DoubleAnimation slide = CreateLaunchTargetAnimation(finishClose ? SidebarOffset : 0.0, fadeDuration, ModalScaleEase);
            LaunchTargetPanelSlide.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            LaunchTargetOverlay.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        }
        ScaleTransform target = _modalScale ?? LaunchTargetPanelScale;
        target.BeginAnimation(ScaleTransform.ScaleXProperty, scale, HandoffBehavior.SnapshotAndReplace);
        target.BeginAnimation(ScaleTransform.ScaleYProperty, scale, HandoffBehavior.SnapshotAndReplace);
    }

    private Image? _modalSnapshot;

    private Border? _modalSnapshotPanel;

    private const double ModalSnapshotPadding = 48;

    private void ShowModalSnapshot(Border panel, ScaleTransform scale)
    {
        HideModalSnapshot();
        if (!Voidstrap.Utility.RenderAcceleration.RendersInSoftware)
            return;
        try
        {
            double startScale = scale.ScaleX;
            double startSlide = LaunchTargetPanelSlide.X;
            LaunchTargetPanelSlide.BeginAnimation(TranslateTransform.XProperty, null);
            LaunchTargetPanelSlide.X = 0;
            SetModalScale(scale, 1.0);
            LaunchTargetOverlay.UpdateLayout();
            double width = panel.ActualWidth;
            double height = panel.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                SetModalScale(scale, startScale);
                LaunchTargetPanelSlide.X = startSlide;
                return;
            }
            double pad = ModalSnapshotPadding;
            Vector offset = VisualTreeHelper.GetOffset(panel);
            Rect bounds = new Rect(offset.X - pad, offset.Y - pad, width + pad * 2, height + pad * 2);
            DpiScale dpi = VisualTreeHelper.GetDpi(panel);
            DrawingVisual drawing = new DrawingVisual();
            using (DrawingContext context = drawing.RenderOpen())
            {
                VisualBrush brush = new VisualBrush(panel)
                {
                    Viewbox = bounds,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.Fill,
                };
                context.DrawRectangle(brush, null, new Rect(0, 0, bounds.Width, bounds.Height));
            }
            RenderTargetBitmap bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(bounds.Width * dpi.DpiScaleX),
                (int)Math.Ceiling(bounds.Height * dpi.DpiScaleY),
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32
            );
            bitmap.Render(drawing);
            bitmap.Freeze();
            SetModalScale(scale, startScale);
            LaunchTargetPanelSlide.X = startSlide;
            if (_modalSnapshot == null)
            {
                _modalSnapshot = new Image
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    IsHitTestVisible = false,
                    Stretch = Stretch.Fill,
                };
                RenderOptions.SetBitmapScalingMode(_modalSnapshot, BitmapScalingMode.Linear);
                LaunchTargetOverlay.Children.Add(_modalSnapshot);
            }
            _modalSnapshot.Source = bitmap;
            _modalSnapshot.Width = bounds.Width;
            _modalSnapshot.Height = bounds.Height;
            _modalSnapshot.Margin = new Thickness(
                panel.Margin.Left - pad,
                panel.Margin.Top - pad,
                panel.Margin.Right - pad,
                panel.Margin.Bottom - pad
            );
            _modalSnapshot.HorizontalAlignment = panel.HorizontalAlignment;
            _modalSnapshot.VerticalAlignment = panel.VerticalAlignment;
            _modalSnapshot.RenderTransform = panel.RenderTransform;
            _modalSnapshot.Visibility = Visibility.Visible;
            panel.Opacity = 0;
            _modalSnapshotPanel = panel;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MainWindow::ShowModalSnapshot", "The dialog snapshot could not be prepared: " + ex.Message);
            HideModalSnapshot();
        }
    }

    private void HideModalSnapshot()
    {
        if (_modalSnapshotPanel != null)
        {
            _modalSnapshotPanel.Opacity = 1;
            _modalSnapshotPanel = null;
        }
        if (_modalSnapshot != null)
        {
            _modalSnapshot.Visibility = Visibility.Collapsed;
            _modalSnapshot.Source = null;
            _modalSnapshot.RenderTransform = Transform.Identity;
        }
    }

    private static void SetModalScale(ScaleTransform scale, double value)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = value;
        scale.ScaleY = value;
    }

    private static DoubleAnimation CreateLaunchTargetAnimation(double to, TimeSpan duration, IEasingFunction ease)
    {
        DoubleAnimation animation = new DoubleAnimation(to, duration) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(animation, LaunchTargetFrameRate);
        return animation;
    }

    private void FinishLaunchTargetOpen(int generation)
    {
        if (generation != _launchTargetOverlayGeneration || !ModalOpen)
            return;
        HideModalSnapshot();
        if (_modalPanel != null)
            _modalPanel.CacheMode = null;
        if (_launchTargetOverlayOpen)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, RefreshLaunchTargetStates);
    }

    private void FinishLaunchTargetClose(int generation)
    {
        if (generation != _launchTargetOverlayGeneration || ModalOpen)
            return;
        HideModalSnapshot();
        if (_modalPanel != null)
            _modalPanel.CacheMode = null;
        if (ReferenceEquals(_modalPanel, CommandPalettePanel))
            ResetCommandPalette();
        if (ReferenceEquals(_modalPanel, ReleaseNotesPanel))
            ReleaseReleaseNotes();
        if (ReferenceEquals(_modalPanel, NewsItemPanel))
            ReleaseNewsItem();
        if (_modalScale != null)
            SetModalScale(_modalScale, 1.0);
        LaunchTargetPanelSlide.BeginAnimation(TranslateTransform.XProperty, null);
        LaunchTargetPanelSlide.X = 0;
        LaunchTargetScrim.BeginAnimation(OpacityProperty, null);
        LaunchTargetScrim.Opacity = 1;
        LaunchTargetOverlay.BeginAnimation(OpacityProperty, null);
        LaunchTargetOverlay.Opacity = 0;
        _modalPanel = null;
        _modalScale = null;
        LaunchTargetOverlay.Visibility = Visibility.Hidden;
        ReleaseOrphanedCapture();
    }

    private void OpenSimpleModal(Border panel, ScaleTransform scale)
    {
        if (_launchTargetOverlayOpen)
        {
            _launchTargetOverlayOpen = false;
            InstallLaunchButton.IsDropDownOpen = false;
        }
        if (_commandPaletteOpen)
        {
            CancelCommandPaletteSearch();
            _commandPaletteOpen = false;
        }
        CloseTopBarMenus();
        _simpleModalOpen = true;
        OpenModalOverlay(panel, scale);
    }

    private void CloseSimpleModal()
    {
        if (!_simpleModalOpen)
            return;
        _simpleModalOpen = false;
        CloseModalOverlay();
    }

    private void CloseActiveModal()
    {
        CloseLaunchTargetOverlay();
        CloseCommandPalette();
        CloseSimpleModal();
    }

    private void ModalClose_Click(object sender, RoutedEventArgs e)
    {
        CloseSimpleModal();
    }

    public void ShowRiShadeRelease()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
        OpenSimpleModal(RiShadeReleasePanel, RiShadeReleasePanelScale);
    }

    private void RiShadeReleaseOpen_Click(object sender, RoutedEventArgs e)
    {
        CloseSimpleModal();
        ExtensionPage.RequestedExtension = "rishade";
        RootNavigation.Navigate(typeof(ExtensionPage));
    }

    private void ShowReleaseNotes()
    {
        ReleaseReleaseNotes();
        Voidstrap.UI.Elements.Dialogs.ReleaseNotesDialog view = new();
        view.CloseRequested += ReleaseNotes_CloseRequested;
        ReleaseNotesHost.Content = view;
        OpenSimpleModal(ReleaseNotesPanel, ReleaseNotesPanelScale);
    }

    public void ShowNewsItem(Voidstrap.UI.ViewModels.Settings.NewsItem item)
    {
        ReleaseNewsItem();
        Voidstrap.UI.Elements.Dialogs.NewsItemDialog view = new(item);
        view.CloseRequested += NewsItem_CloseRequested;
        NewsItemHost.Content = view;
        OpenSimpleModal(NewsItemPanel, NewsItemPanelScale);
    }

    private void NewsItem_CloseRequested(object? sender, EventArgs e)
    {
        CloseSimpleModal();
    }

    private void ReleaseNewsItem()
    {
        if (NewsItemHost.Content is not Voidstrap.UI.Elements.Dialogs.NewsItemDialog view)
            return;
        view.CloseRequested -= NewsItem_CloseRequested;
        NewsItemHost.Content = null;
    }

    private void ReleaseNotes_CloseRequested(object? sender, EventArgs e)
    {
        CloseSimpleModal();
    }

    private void ReleaseReleaseNotes()
    {
        if (ReleaseNotesHost.Content is not Voidstrap.UI.Elements.Dialogs.ReleaseNotesDialog view)
            return;
        view.CloseRequested -= ReleaseNotes_CloseRequested;
        view.Release();
        ReleaseNotesHost.Content = null;
    }

    private void LaunchTargetScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseActiveModal();
        e.Handled = true;
    }

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundaryPattern { get; }

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern { get; }
}
