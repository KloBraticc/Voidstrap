using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private enum TabOptionKind
    {
        Toggle,
        Dropdown,
        Slider
    }

    private sealed class TabOptionDefinition
    {
        public string Key = "";

        public string Title = "";

        public string Description = "";

        public TabOptionKind Kind;

        public string[] Choices = Array.Empty<string>();

        public double Min;

        public double Max = 100.0;

        public double Step = 1.0;

        public Func<bool> GetBool = () => false;

        public Action<bool> SetBool = delegate
        {
        };

        public Func<string> GetChoice = () => "";

        public Action<string> SetChoice = delegate
        {
        };

        public Func<double> GetValue = () => 0.0;

        public Action<double> SetValue = delegate
        {
        };
    }

    private sealed class PageSearchTarget
    {
        public FrameworkElement Element { get; init; }

        public string Text { get; init; }
    }

    private sealed class TopSearchEntry
    {
		public string Id { get; }

        public string DisplayText { get; }

        public string SearchText { get; }

        public Type PageType { get; }

        public string? TargetText { get; }

        public IReadOnlyList<string> TargetTerms { get; }

		public IReadOnlyList<string> ContainerTerms { get; }

		public string NormalizedSearchText { get; }

		public string NormalizedTargetText { get; }

        public TopSearchEntry(string id, string displayText, string searchText, Type pageType, string? targetText = null, IEnumerable<string>? targetTerms = null, IEnumerable<string>? containerTerms = null)
        {
			Id = id;
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

    public class TabItemViewModel : INotifyPropertyChanged
    {
        private string _title = "";

        public string Title
        {
            get
            {
                return _title;
            }
            set
            {
                if (!(_title == value))
                {
                    _title = value;
                    this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Title"));
                }
            }
        }

        public Page PageInstance { get; set; }

        public List<string> OptionKeys { get; } = new List<string>();

        public event PropertyChangedEventHandler? PropertyChanged;

        public override string ToString()
        {
            return Title;
        }
    }

    public class TabBlueprint
    {
        public string Title { get; set; } = "";

        public List<string> OptionKeys { get; set; } = new List<string>();

        public List<LegacyOptionData>? Options { get; set; }
    }

    public class LegacyOptionData
    {
        public string Header { get; set; } = "";
    }

    private bool _isSaveAndLaunchClicked;

    private bool _isClosed;

	private bool _restartNotificationBusy;

    private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

    private readonly DispatcherTimer _visibilityTimer = new DispatcherTimer();

    private bool _bgGifPausedByDeactivate;

    private DiscordRpcClient? _discordClient;

    private bool _discordReady;

    private bool _discordRpcEnabled = App.Settings.Prop.VoidRPC;

    private readonly DateTime _voidRpcSessionStart = DateTime.UtcNow;

    private DateTime _lastVoidRpcUpdate = DateTime.MinValue;

    private bool _voidRpcSuppressed;

    private string? _lastVoidRpcDetails;

    private string? _lastVoidRpcState;

    private DateTime _lastRobloxCheck = DateTime.MinValue;

    private bool _robloxRunningCached;


    private static readonly Dictionary<string, (string Details, string State)> _voidRpcPageDescriptions = new Dictionary<string, (string, string)>
    {
        ["HomePage"] = ("Home", "On the Voidstrap home screen"),
        ["GamePage"] = ("Game Details", "Looking at a game"),
        ["FriendsPage"] = ("Friends", "Checking the friends list"),
        ["QuestsPage"] = ("Quests", "Checking daily quests"),
        ["NotificationsPage"] = ("Notifications", "Checking notifications"),
        ["MobilePage"] = ("Mobile", "Roblox on mobile setup"),
        ["MobilePageExplain"] = ("Mobile", "Reading the mobile guide"),
        ["NvidaEditor"] = ("NVIDIA Editor", "GPU specific tweaks"),
        ["ReleasesPage"] = ("Releases", "Voidstrap release history"),
        ["HistoryPage"] = ("Continue Playing", "Browsing recent games"),
        ["IntegrationsPage"] = ("Integrations", "Advanced integrations"),
        ["BehaviourPage"] = ("Deployment", "Channels, cleaner, matchmaker"),
        ["AppearancePage"] = ("Appearance", "Themes, backgrounds, fonts"),
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
        ["HelpPage"] = ("Help", "why... THIS ISNT A PAGE ANYMORE"),
        ["NvidiaFastFlagsPage"] = ("NVIDIA FFlags", "GPU specific tweaks")
    };

    private AppearanceViewModel _appearanceViewModel;

    private long _lastBorderRefreshTicks;

    private long _lastNotificationRefreshTicks;

    private int _notificationRefreshRunning;

    private int _notificationRefreshRequested;

    private int _notificationUnread = -1;

    private readonly Voidstrap.Utility.WebsiteNotificationRealtime _notificationRealtime = new Voidstrap.Utility.WebsiteNotificationRealtime();

    private string? _currentBackgroundPath;

    private DateTime _currentBackgroundWriteTimeUtc;

    private int _backgroundGeneration;

    private readonly Dictionary<UIElement, TaskCompletionSource<bool>> _backgroundAnimationWaiters = new Dictionary<UIElement, TaskCompletionSource<bool>>();

    private bool _spotifyInitialized;

    private Vector _currentOffset;

    private Vector _targetOffset;

    private double _currentRotation;

    private double _targetRotation;

    private DispatcherTimer _searchDebounceTimer;

    private readonly List<PageSearchTarget> _pageSearchTargets = new List<PageSearchTarget>();

    private readonly List<TopSearchEntry> _topSearchEntriesList = new List<TopSearchEntry>();

    private readonly Dictionary<string, TopSearchEntry> _topSearchEntries = new Dictionary<string, TopSearchEntry>(StringComparer.OrdinalIgnoreCase);

    private TopSearchEntry? _pendingTopSearchEntry;

    private bool _topSearchNavigationPending;

    private bool _topSearchItemsUpdating;

	private int _topSearchNavigationGeneration;

    private bool _navigationInitialized;

    private Page _lastPage;

    private const double MaxOffset = 0.04;

    private const double MaxRotation = 5.0;

    private const double FollowSpeed = 0.035;

    private readonly Dictionary<NavigationItem, SymbolRegular> _defaultIcons = new Dictionary<NavigationItem, SymbolRegular>();

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

    private bool _pendingForumsTab;

    private static readonly int ProcessorCount = Environment.ProcessorCount;

    private static readonly List<TabOptionDefinition> TabOptionRegistry = new List<TabOptionDefinition>
    {
        Toggle("Enable Overlay", "Enable Overlay", "Enables the Overlay Mods to work over Roblox.", () => App.Settings.Prop.OverlaysEnabled, delegate(bool v)
        {
            App.Settings.Prop.OverlaysEnabled = v;
        }),
        Toggle("Crosshair", "Crosshair", "Show a crosshair on screen. (In-Game Only)", () => App.Settings.Prop.Crosshair, delegate(bool v)
        {
            App.Settings.Prop.Crosshair = v;
        }),
        Toggle("Clock", "Clock", "Displays a clock in the stats overlay.", () => App.Settings.Prop.CurrentTimeDisplay, delegate(bool v)
        {
            App.Settings.Prop.CurrentTimeDisplay = v;
        }),
        Toggle("Server Ping Counter", "Server Ping Counter", "Shows your ping to the current server on the overlay.", () => App.Settings.Prop.ServerPingCounter, delegate(bool v)
        {
            App.Settings.Prop.ServerPingCounter = v;
        }),
        Toggle("Server Location Overlay", "Server Location Overlay", "Shows the current server location on the overlay.", () => App.Settings.Prop.ShowServerDetailsUI, delegate(bool v)
        {
            App.Settings.Prop.ShowServerDetailsUI = v;
        }),
        Toggle("Join Notifications", "Join Notifications", "Shows a notification with server info when joining a game.", () => App.Settings.Prop.NotificationWindowShow, delegate(bool v)
        {
            App.Settings.Prop.NotificationWindowShow = v;
        }),
        Toggle("Activity Tracking", Strings.Menu_Integrations_EnableActivityTracking_Title, Strings.Menu_Integrations_EnableActivityTracking_Description, () => App.Settings.Prop.EnableActivityTracking, delegate(bool v)
        {
            App.Settings.Prop.EnableActivityTracking = v;
        }),
        Toggle("Query Server Location", Strings.Menu_Integrations_QueryServerLocation_Title, Strings.Menu_Integrations_QueryServerLocation_Description, () => App.Settings.Prop.ShowServerDetails, delegate(bool v)
        {
            App.Settings.Prop.ShowServerDetails = v;
        }),
        Toggle("Desktop App", Strings.Menu_Integrations_DesktopApp_Title, Strings.Menu_Integrations_DesktopApp_Description, () => App.Settings.Prop.UseDisableAppPatch, delegate(bool v)
        {
            App.Settings.Prop.UseDisableAppPatch = v;
        }),
        Toggle("Show Game Activity", Strings.Menu_Integrations_ShowGameActivity_Title, Strings.Menu_Integrations_ShowGameActivity_Description, () => App.Settings.Prop.UseDiscordRichPresence, delegate(bool v)
        {
            App.Settings.Prop.UseDiscordRichPresence = v;
        }),
        Toggle("Show Account On Profile", Strings.Menu_Integrations_ShowAccountOnProfile_Title, Strings.Menu_Integrations_ShowAccountOnProfile_Description, () => App.Settings.Prop.ShowAccountOnRichPresence, delegate(bool v)
        {
            App.Settings.Prop.ShowAccountOnRichPresence = v;
        }),
        Toggle("Confirm Launches", Strings.Menu_Behaviour_ConfirmLaunches_Title, Strings.Menu_Behaviour_ConfirmLaunches_Description, () => App.Settings.Prop.ConfirmLaunches, delegate(bool v)
        {
            App.Settings.Prop.ConfirmLaunches = v;
        }),
        Toggle("Disable Background Window", "Disable Background Window", "Disables Background Window when launching Roblox.", () => App.Settings.Prop.BackgroundWindow, delegate(bool v)
        {
            App.Settings.Prop.BackgroundWindow = v;
        }),
        Toggle("Disable RobloxCrashHandler", "Disable RobloxCrashHandler", "Disables the RobloxCrashHandler that runs on startup.", () => App.Settings.Prop.DisableCrash, delegate(bool v)
        {
            App.Settings.Prop.DisableCrash = v;
        }),
        Toggle("Background Snow", "Background Snow", "Adds snow to Voidstrap's background.", () => App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw, delegate(bool v)
        {
            App.Settings.Prop.SnowWOWSOCOOLWpfSnowbtw = v;
        }),
        Toggle("Gradient Movement", "Gradient Movement", "Adds gradient movement following the cursor.", () => App.Settings.Prop.GRADmentFR, delegate(bool v)
        {
            App.Settings.Prop.GRADmentFR = v;
        }),
        Toggle("Smooth ScrollBar", "Smooth ScrollBar", "Adds smooth scrollbar movement.", () => App.Settings.Prop.SmooothBARRyesirikikthxlucipook, delegate(bool v)
        {
            App.Settings.Prop.SmooothBARRyesirikikthxlucipook = v;
            App.Settings.Save();
            Wpf.Ui.Controls.SmoothScroll.SetGlobalEnabled(v);
        }),
        Toggle("Optimize Roblox", "Prioritize Roblox while focused", "Uses safe Above Normal scheduling while Roblox is in the foreground. A custom Roblox Priority choice takes precedence.", () => App.Settings.Prop.OptimizeRoblox, delegate(bool v)
        {
            App.Settings.Prop.OptimizeRoblox = v;
        }),
        Toggle("Trim Roblox Memory", "Trim memory when Roblox is unfocused", "After Roblox remains unfocused, unused working set is released. It does not trim memory while you are playing.", () => App.Settings.Prop.MultiAccount, delegate(bool v)
        {
            App.Settings.Prop.MultiAccount = v;
        }),
        Toggle("Update Roblox", "Update Roblox", "Automatically keeps Roblox up to date when launching.", () => App.Settings.Prop.UpdateRoblox, delegate(bool v)
        {
            App.Settings.Prop.UpdateRoblox = v;
        }),
        Dropdown("Process Priority", "Roblox Priority", "Choose a safe Windows scheduling priority for Roblox.", new string[5] { "Low", "Below Normal", "Normal", "Above Normal", "High" }, () => NormalizeRobloxPriority(App.Settings.Prop.PriorityLimit), delegate(string v)
        {
            App.Settings.Prop.PriorityLimit = v;
        }),
        Dropdown("CPU Priority", "Roblox CPU limit", "Automatic uses every available processor. Set a lower limit only for multi client use.", CreateRobloxCpuLimitChoices(), () => App.Settings.Prop.SelectedCpuPriority, delegate(string v)
        {
            App.Settings.Prop.SelectedCpuPriority = v;
        }),
        Dropdown("RPC Idle Icon", "RPC Idle Icon", "Selects the idle icon shown on Discord Rich Presence.", new string[6] { "blue", "purple", "red", "green", "white", "black" }, () => App.Settings.Prop.RpcIdleIcon, delegate(string v)
        {
            App.Settings.Prop.RpcIdleIcon = v;
        }),
        SliderOption("Brightness", "Brightness", "In-game brightness overlay. 50 is neutral.", 0.0, 100.0, 5.0, () => App.Settings.Prop.Brightness, delegate(double v)
        {
            App.Settings.Prop.Brightness = v;
        }),
        SliderOption("Saturation", "Saturation", "In-game color saturation. 100 is neutral.", 0.0, 200.0, 5.0, () => App.Settings.Prop.Saturation, delegate(double v)
        {
            App.Settings.Prop.Saturation = v;
        }),
        SliderOption("Contrast", "Contrast", "In-game contrast. 100 is neutral.", 0.0, 200.0, 5.0, () => App.Settings.Prop.Contrast, delegate(double v)
        {
            App.Settings.Prop.Contrast = v;
        }),
        SliderOption("Color Temperature", "Color Temperature", "In-game color temperature. 0 is neutral, negative is cooler, positive is warmer.", -100.0, 100.0, 5.0, () => App.Settings.Prop.ColorTemperature, delegate(double v)
        {
            App.Settings.Prop.ColorTemperature = v;
        }),
        SliderOption("CPU Core Limit", "Voidstrap CPU core limit", "Limits the processor affinity of Voidstrap itself. Leave it at the maximum unless you need to reserve cores for another app.", 1.0, ProcessorCount, 1.0, () => App.Settings.Prop.CpuCoreLimit, delegate(double v)
        {
            int normalized = Math.Clamp((int)v, 1, ProcessorCount);
            App.Settings.Prop.CpuCoreLimit = normalized;
            CpuCoreLimiter.SetCpuCoreLimit(normalized);
        }),
		SliderOption("Max Concurrent Downloads", "Max Concurrent Downloads", "How many files Voidstrap downloads at once when installing Roblox.", 1.0, 32.0, 1.0, () => App.Settings.Prop.MaxConcurrentDownloads, delegate(double v)
        {
            App.Settings.Prop.MaxConcurrentDownloads = DownloadConfiguration.NormalizeConcurrent((int)v);
        })
    };

    private static bool _syncingTabControls;

    private int _navIndexBeforeTab = -1;

    private int _friendsReturnIndex = -1;

    private object? _notificationsReturnPage;

    private Pages.NotificationsPage? _activeNotificationsPage;

    private object? _pageBeforeTab;

    private sealed class NavigationHistoryEntry
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

    private DispatcherTimer? _introCacheTimer;

    private Voidstrap.Models.Persistable.WindowState _state => App.State.Prop.SettingsWindow;

    private string TabsConfigPath => System.IO.Path.Combine(Paths.Config, "TabsConfig.json");

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
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Voidstrap.UI.WindowBackdrop.ApplyMainWindow(this);
    }

    private void ApplyThemeBackground()
    {
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

    private void LogContentDiagnostics()
    {
        try
        {
            object? content = RootFrame?.Content;
            string kind = content?.GetType().Name ?? "<null>";
            double opacity = (content as UIElement)?.Opacity ?? -1.0;
            bool visible = (content as UIElement)?.IsVisible ?? false;
            double w = RootFrame?.ActualWidth ?? -1.0;
            double h = RootFrame?.ActualHeight ?? -1.0;
            App.Logger?.WriteLine("MainWindow::Diag", $"VSDIAG frame={w:F0}x{h:F0} content={kind} opacity={opacity:F2} visible={visible} navItems={RootNavigation?.Items?.Count ?? -1}");
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("MainWindow::Diag", "VSDIAG failed: " + ex.Message);
        }
    }

    private void CreateBackgroundMedia()
    {
        if (!Voidstrap.Utility.Platform.IsWindows || BackgroundLayer == null)
        {
            return;
        }
        try
        {
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
        SoberNavItem.Visibility = Voidstrap.Utility.Platform.IsLinux ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsNavItem.Visibility = Voidstrap.Utility.Platform.IsLinux ? Visibility.Collapsed : Visibility.Visible;
        SettingChangeNotifier.Failed += OnSettingChangeFailed;
        RestartNotificationService.Changed += OnRestartRequirementsChanged;
        CreateBackgroundMedia();
        AllowsTransparency = false;
        ApplyThemeBackground();
        InitializeViewModel();
        InitializeWindowState();
        UpdateButtonContent();
        InitializeDiscordRPC();
        RegisterHoverIcons();
        _appearanceViewModel = new AppearanceViewModel();
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
        RootFrame.Navigated += RootFrame_Navigated;
        WorkspaceTabs.PreviewMouseLeftButtonDown += WorkspaceTabs_PreviewMouseLeftButtonDown;
        AccountPopup.Closed += OverlayPopup_Closed;
        LaunchTargetPopup.Closed += OverlayPopup_Closed;
        App.Logger.WriteLine("MainWindow", "Initializing settings window");
        if (base.DataContext is MainWindowViewModel { Tabs: null } mainWindowViewModel)
        {
            mainWindowViewModel.Tabs = new ObservableCollection<TabItemViewModel>();
        }
        if (showAlreadyRunningWarning)
        {
            ShowAlreadyRunningSnackbarAsync();
        }
        RefreshRestartNotification();
    }

    private void VisibilityTimer_Tick(object? sender, EventArgs e)
    {
        UpdateFastFlagEditorVisibility();
        UpdateDiscordPresence();
    }

    private void RegisterHoverIcons()
    {
        foreach (NavigationItem item in from i in RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>())
                                        where i.Tag != null
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

    private static TabOptionDefinition Toggle(string key, string title, string description, Func<bool> get, Action<bool> set)
    {
        return new TabOptionDefinition
        {
            Key = key,
            Title = title,
            Description = description,
            Kind = TabOptionKind.Toggle,
            GetBool = get,
            SetBool = delegate (bool v)
            {
                set(v);
                App.Settings.SaveDeferred();
            }
        };
    }

    private static TabOptionDefinition Dropdown(string key, string title, string description, string[] choices, Func<string> get, Action<string> set)
    {
        return new TabOptionDefinition
        {
            Key = key,
            Title = title,
            Description = description,
            Kind = TabOptionKind.Dropdown,
            Choices = choices,
            GetChoice = get,
            SetChoice = delegate (string v)
            {
                set(v);
                App.Settings.SaveDeferred();
            }
        };
    }

    private static string[] CreateRobloxCpuLimitChoices()
    {
        int processorCount = ProcessorCount;
        if (processorCount > IntPtr.Size * 8)
        {
            return new string[1] { "Automatic" };
        }
        return new string[1] { "Automatic" }
            .Concat(Enumerable.Range(1, processorCount).Select(count => count + " Core" + ((count == 1) ? string.Empty : "s")))
            .ToArray();
    }

    private static string NormalizeRobloxPriority(string? priority)
    {
        if (priority?.Equals("Realtime", StringComparison.OrdinalIgnoreCase) == true || priority?.Equals("RealTime", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "High";
        }
        if (priority?.Equals("AboveNormal", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Above Normal";
        }
        if (priority?.Equals("BelowNormal", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Below Normal";
        }
        return priority ?? "Normal";
    }

    private static TabOptionDefinition SliderOption(string key, string title, string description, double min, double max, double step, Func<double> get, Action<double> set)
    {
        return new TabOptionDefinition
        {
            Key = key,
            Title = title,
            Description = description,
            Kind = TabOptionKind.Slider,
            Min = min,
            Max = max,
            Step = step,
            GetValue = get,
            SetValue = delegate (double v)
            {
                set(v);
                App.Settings.SaveDeferred();
            }
        };
    }

    private static TabOptionDefinition? FindTabOption(string keyOrTitle)
    {
        if (string.IsNullOrWhiteSpace(keyOrTitle))
        {
            return null;
        }
        return TabOptionRegistry.FirstOrDefault((TabOptionDefinition o) => string.Equals(o.Key, keyOrTitle, StringComparison.OrdinalIgnoreCase)) ?? TabOptionRegistry.FirstOrDefault((TabOptionDefinition o) => string.Equals(o.Title, keyOrTitle, StringComparison.OrdinalIgnoreCase));
    }

    private void SaveTabsStructure()
    {
        if (!(base.DataContext is MainWindowViewModel mainWindowViewModel))
        {
            return;
        }
        try
        {
            List<TabBlueprint> tabs = mainWindowViewModel.Tabs.Select((TabItemViewModel tab) => new TabBlueprint
            {
                Title = tab.Title,
                OptionKeys = tab.OptionKeys.ToList()
            }).ToList();
            Voidstrap.Utility.JsonFile.SerializeAtomic(TabsConfigPath, tabs, Voidstrap.Utility.JsonOptions.Indented);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MainWindow::SaveTabsStructure", "Save error: " + ex.Message);
        }
    }

    private void LoadTabsStructure()
    {
        if (!File.Exists(TabsConfigPath) || !(base.DataContext is MainWindowViewModel mainWindowViewModel))
        {
            return;
        }
        try
        {
            List<TabBlueprint> list = Voidstrap.Utility.JsonFile.Deserialize<List<TabBlueprint>>(TabsConfigPath, Voidstrap.Utility.JsonOptions.Tolerant, 4194304);
            mainWindowViewModel.Tabs.Clear();
            foreach (TabBlueprint item in list)
            {
                List<string> optionKeys = item.OptionKeys;
                List<string> optionKeys2 = (from k in (optionKeys != null && optionKeys.Count > 0) ? ((IEnumerable<string>)item.OptionKeys) : ((IEnumerable<string>)(item.Options?.Select((LegacyOptionData o) => o.Header).ToList() ?? new List<string>()))
                                            where FindTabOption(k) != null
                                            select FindTabOption(k).Key).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
                mainWindowViewModel.Tabs.Add(CreateTab(item.Title, optionKeys2, mainWindowViewModel));
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("MainWindow", "Load Error: " + ex.Message);
        }
    }

    private TabItemViewModel CreateTab(string title, IEnumerable<string> optionKeys, MainWindowViewModel vm)
    {
        TabItemViewModel tab = new TabItemViewModel
        {
            Title = title
        };
        tab.OptionKeys.AddRange(optionKeys);
        Grid grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1.0, GridUnitType.Star)
        });
        StackPanel stackPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(10.0)
        };
        System.Windows.Controls.Button button = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 34.0,
            Height = 34.0,
            ToolTip = "Delete this tab",
            Margin = new Thickness(0.0, 0.0, 5.0, 0.0)
        };
        button.Click += delegate
        {
            vm.Tabs.Remove(tab);
            if (vm.SelectedTab == tab)
            {
                vm.SelectedTab = null;
                NavigateBackFromTab();
            }
            SaveTabsStructure();
        };
        stackPanel.Children.Add(button);
        System.Windows.Controls.Button button2 = new System.Windows.Controls.Button
        {
            Content = "+",
            Width = 34.0,
            Height = 34.0,
            ToolTip = "Add an option to this tab",
            Margin = new Thickness(0.0, 0.0, 5.0, 0.0)
        };
        button2.Click += delegate
        {
            OpenToolbox(tab, vm);
        };
        stackPanel.Children.Add(button2);
        System.Windows.Controls.TextBox renameBox = new System.Windows.Controls.TextBox
        {
            Width = 160.0,
            Height = 34.0,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Rename this tab",
            Text = tab.Title,
            Margin = new Thickness(0.0, 0.0, 5.0, 0.0)
        };
        renameBox.LostFocus += delegate
        {
            CommitRename();
        };
        renameBox.KeyDown += delegate (object _, KeyEventArgs e)
        {
            //IL_0001: Unknown result type (might be due to invalid IL or missing references)
            //IL_0007: Invalid comparison between Unknown and I4
            if ((int)e.Key == 6)
            {
                CommitRename();
            }
        };
        stackPanel.Children.Add(renameBox);
        Grid.SetRow(stackPanel, 0);
        grid.Children.Add(stackPanel);
        ScrollViewer scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid grid2 = new Grid
        {
            Margin = new Thickness(10.0)
        };
        for (int num = 0; num < 3; num++)
        {
            grid2.ColumnDefinitions.Add(new ColumnDefinition());
        }
        scrollViewer.Content = grid2;
        Grid.SetRow(scrollViewer, 1);
        grid.Children.Add(scrollViewer);
        tab.PageInstance = new Page
        {
            Background = Brushes.Transparent,
            Content = grid
        };
        RebuildTabOptions(tab, vm);
        return tab;
        void CommitRename()
        {
            string text = renameBox.Text?.Trim() ?? "";
            if (text.Length != 0 && !(text == tab.Title))
            {
                tab.Title = text;
                SaveTabsStructure();
            }
        }
    }

    private void RebuildTabOptions(TabItemViewModel tab, MainWindowViewModel vm)
    {
        if (!(tab.PageInstance?.Content is Grid grid) || !(grid.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content is Grid grid2))
        {
            return;
        }
        foreach (ToggleSwitch toggleSwitch in FindVisualChildren<ToggleSwitch>(grid2))
        {
            toggleSwitch.Checked -= CustomToggleChanged;
            toggleSwitch.Unchecked -= CustomToggleChanged;
        }
        grid2.Children.Clear();
        grid2.RowDefinitions.Clear();
        int num = 0;
        foreach (string item in tab.OptionKeys.ToList())
        {
            TabOptionDefinition tabOptionDefinition = FindTabOption(item);
            if (tabOptionDefinition != null)
            {
                int num2 = num / 3;
                int value = num % 3;
                while (grid2.RowDefinitions.Count <= num2)
                {
                    grid2.RowDefinitions.Add(new RowDefinition
                    {
                        Height = GridLength.Auto
                    });
                }
                FrameworkElement element = BuildOptionCard(tabOptionDefinition, tab, vm);
                Grid.SetRow(element, num2);
                Grid.SetColumn(element, value);
                grid2.Children.Add(element);
                num++;
            }
        }
    }

    private FrameworkElement BuildOptionCard(TabOptionDefinition def, TabItemViewModel tab, MainWindowViewModel vm)
    {
        Border border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 80)),
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(6.0),
            Padding = new Thickness(8.0),
            Margin = new Thickness(5.0)
        };
        StackPanel stackPanel = new StackPanel();
        Grid grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1.0, GridUnitType.Star)
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });
        TextBlock element = new TextBlock
        {
            Text = def.Title,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(element, 0);
        grid.Children.Add(element);
        System.Windows.Controls.Button button = new System.Windows.Controls.Button
        {
            Content = "✕",
            Width = 20.0,
            Height = 20.0,
            FontSize = 10.0,
            Padding = new Thickness(0.0),
            ToolTip = "Remove this option from the tab"
        };
        button.Click += delegate
        {
            tab.OptionKeys.RemoveAll((string k) => string.Equals(k, def.Key, StringComparison.OrdinalIgnoreCase));
            RebuildTabOptions(tab, vm);
            SaveTabsStructure();
        };
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        stackPanel.Children.Add(grid);
        if (!string.IsNullOrEmpty(def.Description))
        {
            stackPanel.Children.Add(new TextBlock
            {
                Text = def.Description,
                FontSize = 12.0,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0.0, 5.0, 0.0, 5.0),
                Foreground = Brushes.White
            });
        }
        switch (def.Kind)
        {
            case TabOptionKind.Toggle:
                {
                    ToggleSwitch toggleSwitch = new ToggleSwitch
                    {
                        IsChecked = def.GetBool(),
                        Margin = new Thickness(0.0, 5.0, 0.0, 0.0),
                        Tag = def.Key
                    };
                    AutomationProperties.SetName(toggleSwitch, def.Title);
                    AutomationProperties.SetHelpText(toggleSwitch, def.Description);
                    toggleSwitch.Checked += CustomToggleChanged;
                    toggleSwitch.Unchecked += CustomToggleChanged;
                    stackPanel.Children.Add(toggleSwitch);
                    break;
                }
            case TabOptionKind.Dropdown:
                {
                    ComboBox combo = new ComboBox
                    {
                        Margin = new Thickness(0.0, 5.0, 0.0, 0.0),
                        Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
                        Tag = def.Key,
                        ItemsSource = def.Choices
                    };
                    string current = def.GetChoice();
                    combo.SelectedItem = def.Choices.FirstOrDefault((string c) => string.Equals(c, current, StringComparison.OrdinalIgnoreCase)) ?? def.Choices.FirstOrDefault();
                    combo.SelectionChanged += delegate
                    {
                        if (!_syncingTabControls && combo.SelectedItem is string obj)
                        {
                            def.SetChoice(obj);
                            SyncTabControls(def);
                        }
                    };
                    stackPanel.Children.Add(combo);
                    break;
                }
            case TabOptionKind.Slider:
                {
                    Grid grid2 = new Grid
                    {
                        Margin = new Thickness(0.0, 5.0, 0.0, 0.0)
                    };
                    grid2.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(1.0, GridUnitType.Star)
                    });
                    grid2.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(44.0)
                    });
                    TextBlock valueText = new TextBlock
                    {
                        Foreground = Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Text = def.GetValue().ToString("0")
                    };
                    Slider slider = new Slider
                    {
                        Minimum = def.Min,
                        Maximum = def.Max,
                        TickFrequency = def.Step,
                        IsSnapToTickEnabled = true,
                        Value = def.GetValue(),
                        VerticalAlignment = VerticalAlignment.Center,
                        Tag = def.Key
                    };
                    slider.ValueChanged += delegate (object _, RoutedPropertyChangedEventArgs<double> e2)
                    {
                        valueText.Text = e2.NewValue.ToString("0");
                        if (!_syncingTabControls)
                        {
                            def.SetValue(e2.NewValue);
                            SyncTabControls(def);
                        }
                    };
                    Grid.SetColumn(slider, 0);
                    Grid.SetColumn(valueText, 1);
                    grid2.Children.Add(slider);
                    grid2.Children.Add(valueText);
                    stackPanel.Children.Add(grid2);
                    break;
                }
        }
        border.Child = stackPanel;
        return border;
    }

    private static void CustomToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingTabControls || sender is not ToggleSwitch { Tag: string key } toggleSwitch)
        {
            return;
        }

        TabOptionDefinition? definition = FindTabOption(key);
        if (definition is null)
        {
            return;
        }

        bool previous = definition.GetBool();
        SettingChangeResult result = SettingChangeNotifier.Try(
            "MainWindow::CustomToggleChanged",
            "The custom setting could not be changed.",
            () => definition.SetBool(toggleSwitch.IsChecked == true));
        if (!result.Success)
        {
            try
            {
                definition.SetBool(previous);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteException("MainWindow::CustomToggleRollback", ex);
            }
            _syncingTabControls = true;
            try
            {
                toggleSwitch.SetCurrentValue(ToggleSwitch.IsCheckedProperty, previous);
            }
            finally
            {
                _syncingTabControls = false;
            }
        }
        SyncTabControls(definition);
    }

    private static void SyncTabControls(TabOptionDefinition def)
    {
        if (_syncingTabControls)
        {
            return;
        }
        _syncingTabControls = true;
        try
        {
            foreach (Window window in Application.Current.Windows)
            {
                foreach (FrameworkElement item in FindVisualChildren<FrameworkElement>((DependencyObject)(object)window))
                {
                    if (!(item.Tag is string a) || !string.Equals(a, def.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    FrameworkElement frameworkElement = item;
                    if (!(frameworkElement is ToggleSwitch { IsChecked: var isChecked } toggleSwitch))
                    {
                        if (!(frameworkElement is ComboBox comboBox))
                        {
                            if (frameworkElement is Slider slider && Math.Abs(slider.Value - def.GetValue()) > 0.001)
                            {
                                slider.Value = def.GetValue();
                            }
                            continue;
                        }
                        string current2 = def.GetChoice();
                        string text = def.Choices.FirstOrDefault((string c) => string.Equals(c, current2, StringComparison.OrdinalIgnoreCase));
                        if (text != null && !object.Equals(comboBox.SelectedItem, text))
                        {
                            comboBox.SelectedItem = text;
                        }
                    }
                    else if (isChecked != def.GetBool())
                    {
                        toggleSwitch.SetCurrentValue(ToggleSwitch.IsCheckedProperty, def.GetBool());
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            _syncingTabControls = false;
        }
    }

    private void OpenToolbox(TabItemViewModel targetTab, MainWindowViewModel vm)
    {
        //IL_007a: Unknown result type (might be due to invalid IL or missing references)
        //IL_0097: Unknown result type (might be due to invalid IL or missing references)
        Window obj = new Window
        {
            Title = "Add Options",
            Width = 360.0,
            Height = 480.0,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent
        };
        LinearGradientBrush linearGradientBrush = new LinearGradientBrush
        {
            StartPoint = new Point(1.0, 1.0),
            EndPoint = new Point(0.0, 0.0)
        };
        linearGradientBrush.GradientStops.Add(new GradientStop((Color)TryFindResource("WindowBackgroundColorPrimary"), 0.0));
        linearGradientBrush.GradientStops.Add(new GradientStop((Color)TryFindResource("WindowBackgroundColorSecondary"), 0.8));
        linearGradientBrush.GradientStops.Add(new GradientStop((Color)TryFindResource("WindowBackgroundColorThird"), 1.1));
        Grid grid = new Grid
        {
            Background = linearGradientBrush
        };
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1.0, GridUnitType.Star)
        });
        System.Windows.Controls.TextBox searchBox = new System.Windows.Controls.TextBox
        {
            Margin = new Thickness(15.0, 15.0, 15.0, 0.0),
            Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
            ToolTip = "Search options"
        };
        Grid.SetRow(searchBox, 0);
        grid.Children.Add(searchBox);
        StackPanel toolboxPanel = new StackPanel
        {
            Margin = new Thickness(15.0)
        };
        ScrollViewer element = new ScrollViewer
        {
            Content = toolboxPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(element, 1);
        grid.Children.Add(element);
        obj.Content = grid;
        searchBox.TextChanged += delegate
        {
            Populate(searchBox.Text);
        };
        Populate("");
        obj.ShowDialog();
        void Populate(string filter)
        {
            toolboxPanel.Children.Clear();
            foreach (TabOptionDefinition def in TabOptionRegistry)
            {
                string searchableText = string.Join(" ", new[]
                {
                    def.Key,
                    def.Title,
                    def.Description,
                    def.Kind.ToString(),
                    string.Join(" ", def.Choices)
                });
                if (string.IsNullOrWhiteSpace(filter) || IsFuzzyMatch(searchableText, filter))
                {
                    bool flag = targetTab.OptionKeys.Contains<string>(def.Key, StringComparer.OrdinalIgnoreCase);
                    System.Windows.Controls.Button button = new System.Windows.Controls.Button
                    {
                        Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
                        Padding = new Thickness(10.0),
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        IsEnabled = !flag
                    };
                    StackPanel stackPanel = new StackPanel();
                    TextBlock textBlock = new TextBlock
                    {
                        FontWeight = FontWeights.Medium
                    };
                    textBlock.Text = (flag ? (def.Title + " (added)") : $"{def.Title} [{def.Kind}]");
                    stackPanel.Children.Add(textBlock);
                    stackPanel.Children.Add(new TextBlock
                    {
                        Text = def.Description,
                        FontSize = 12.0,
                        TextWrapping = TextWrapping.Wrap
                    });
                    button.Content = stackPanel;
                    button.Click += delegate
                    {
                        if (!targetTab.OptionKeys.Contains<string>(def.Key, StringComparer.OrdinalIgnoreCase))
                        {
                            targetTab.OptionKeys.Add(def.Key);
                            RebuildTabOptions(targetTab, vm);
                            SaveTabsStructure();
                            Populate(searchBox.Text);
                        }
                    };
                    toolboxPanel.Children.Add(button);
                }
            }
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
            T val = (T)(object)((child is T) ? child : null);
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

    private void AddTab_Click(object sender, RoutedEventArgs e)
    {
        if (!(base.DataContext is MainWindowViewModel mainWindowViewModel) || mainWindowViewModel.Tabs.Count >= 8)
        {
            return;
        }
        int value = 1;
        if (mainWindowViewModel.Tabs.Any())
        {
            List<int> source = (from t in mainWindowViewModel.Tabs
                                select Regex.Match(t.Title, "\\d+") into m
                                where m.Success
                                select int.Parse(m.Value)).ToList();
            value = (source.Any() ? (source.Max() + 1) : (mainWindowViewModel.Tabs.Count + 1));
        }
        TabItemViewModel tabItemViewModel = CreateTab($"Tab #{value}", Enumerable.Empty<string>(), mainWindowViewModel);
        mainWindowViewModel.Tabs.Add(tabItemViewModel);
        mainWindowViewModel.SelectedTab = tabItemViewModel;
        SaveTabsStructure();
    }

    private bool IsTabPage(object? content)
    {
        if (content == null || !(base.DataContext is MainWindowViewModel mainWindowViewModel))
        {
            return false;
        }
        return mainWindowViewModel.Tabs.Any((TabItemViewModel t) => t.PageInstance == content);
    }

    private void WorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceTabs.SelectedItem is TabItemViewModel { PageInstance: not null } tabItemViewModel)
        {
            if (ReferenceEquals(RootFrame.Content, tabItemViewModel.PageInstance))
            {
                return;
            }
            if (!IsTabPage(RootFrame.Content))
            {
                _navIndexBeforeTab = RootNavigation.SelectedPageIndex;
                _pageBeforeTab = RootFrame.Content;
            }
            RootNavigation.NavigateExternal(tabItemViewModel.PageInstance);
        }
    }

    private void WorkspaceTabs_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (base.DataContext is MainWindowViewModel { SelectedTab: not null } mainWindowViewModel)
        {
            object originalSource = e.OriginalSource;
            DependencyObject val = (DependencyObject)((originalSource is DependencyObject) ? originalSource : null);
            if (val != null && FindAncestor<TabItem>(val)?.DataContext is TabItemViewModel tabItemViewModel && tabItemViewModel == mainWindowViewModel.SelectedTab && RootFrame.Content == tabItemViewModel.PageInstance)
            {
                e.Handled = true;
                mainWindowViewModel.SelectedTab = null;
                NavigateBackFromTab();
            }
        }
    }

    private void NavigateBackFromTab()
    {
        if (_pageBeforeTab != null && !IsTabPage(_pageBeforeTab))
        {
            RootNavigation.NavigateExternal(_pageBeforeTab);
            return;
        }
        int num = ResolveSafeNavigationIndex((_navIndexBeforeTab >= 0) ? _navIndexBeforeTab : App.State.Prop.LastPage);
        IReadOnlyList<NavigationItem> navigationItems = GetNavigationItemsInServiceOrder();
        if (num >= 0 && num < navigationItems.Count && navigationItems[num] is NavigationItem { PageType: not null } navigationItem && !RootNavigation.Navigate(navigationItem.PageType) && RootFrame.Content != null && IsTabPage(RootFrame.Content))
        {
            FrameworkElement frameworkElement = RootNavigation.PageService?.GetPage(navigationItem.PageType);
            if (frameworkElement != null)
            {
                RootNavigation.NavigateExternal(frameworkElement);
            }
            else
            {
                RootNavigation.NavigateExternal(Activator.CreateInstance(navigationItem.PageType));
            }
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            T val = (T)(object)((current is T) ? current : null);
            if (val != null)
            {
                return val;
            }
            bool flag = ((current is Visual || current is Visual3D) ? true : false);
            current = (flag ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current));
        }
        return default(T);
    }

    private void RootFrame_Navigated(object sender, NavigationEventArgs e)
    {
        if (_activeNotificationsPage != null && !ReferenceEquals(e.Content, _activeNotificationsPage))
        {
            _activeNotificationsPage.BackRequested -= NotificationsPage_BackRequested;
            _activeNotificationsPage = null;
            _notificationsReturnPage = null;
        }
        _pageSearchTargets.Clear();
        _lastPage = null;
        TrackNavigationHistory(e.Content);
        if (base.DataContext is MainWindowViewModel mainWindowViewModel)
        {
            var owningTab = mainWindowViewModel.Tabs?.FirstOrDefault(t => ReferenceEquals(t.PageInstance, e.Content));
            if (!ReferenceEquals(mainWindowViewModel.SelectedTab, owningTab))
            {
                mainWindowViewModel.SelectedTab = owningTab;
            }
        }
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
            BreadcrumbPanel.Visibility = (content is Pages.FriendsPage || content is Pages.NotificationsPage || content is Pages.QuestsPage || content is Pages.ShopPage || content is Pages.BlackMarketPage || content is LibraryPage) ? Visibility.Collapsed : Visibility.Visible;
        }
        bool isLibrary = content is LibraryPage;
        RootNavigation.Visibility = (isLibrary ? Visibility.Collapsed : Visibility.Visible);
        SynchronizeSidebarSelection(content);
        Dispatcher.BeginInvoke(new Action(SynchronizeCurrentSidebarSelection), DispatcherPriority.Loaded);
        UpdateTopNavActive(content);
        PerformSearch(GlobalSearchBox.Text?.Trim() ?? "");
        if (content is Page)
        {
            Dispatcher.BeginInvoke(new Action(IndexLoadedPageSearchEntries), DispatcherPriority.Loaded);
        }
        else if (_pendingTopSearchEntry != null)
        {
            _pendingTopSearchEntry = null;
        }
        if (content is NewsPage newsPage && _pendingForumsTab)
        {
            _pendingForumsTab = false;
            newsPage.SelectForumsTab();
        }
        NavigationItem navigationItem = RootNavigation.Items.OfType<NavigationItem>().Concat(RootNavigation.Footer.OfType<NavigationItem>()).FirstOrDefault((NavigationItem i) => i.IsActive);
        if (navigationItem != null && _defaultIcons.TryGetValue(navigationItem, out var value))
        {
            BreadcrumbIcon.Symbol = value;
        }
    }

    private void UpdateTopNavActive(object? content)
    {
        TopNavHome.Tag = ((content is HomePage) ? "Active" : null);
        TopNavLibrary.Tag = ((content is LibraryPage) ? "Active" : null);
        TopNavCommunity.Tag = ((content is NewsPage) ? "Active" : null);
    }

    private IReadOnlyList<NavigationItem> GetNavigationItemsInServiceOrder()
    {
        return RootNavigation.Items.OfType<NavigationItem>()
            .Concat(RootNavigation.Footer.OfType<NavigationItem>())
            .ToArray();
    }

    private void SynchronizeCurrentSidebarSelection()
    {
        SynchronizeSidebarSelection(RootFrame?.Content);
    }

    private void SynchronizeSidebarSelection(object? content)
    {
        if (content == null || RootNavigation == null)
        {
            return;
        }

        IReadOnlyList<NavigationItem> navigationItems = GetNavigationItemsInServiceOrder();
        Type contentType = content.GetType();
        NavigationItem? activeItem = navigationItems.FirstOrDefault(item => item.PageType == contentType);
        if (activeItem == null)
        {
            return;
        }

        for (int i = 0; i < navigationItems.Count; i++)
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
        bool hasTarget = entry.Content.TryGetTarget(out target);
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
            Dictionary<Type, string> sidebarPages = GetSidebarPageNames();
            List<TopSearchEntry> entries = new List<TopSearchEntry>();
            foreach (KeyValuePair<Type, string> page in sidebarPages)
            {
                string displayText = "Page: " + page.Value;
				entries.Add(new TopSearchEntry("Page." + page.Key.FullName, displayText, page.Value + " " + page.Key.Name, page.Key));
            }
            foreach (SearchCatalogOption option in SearchCatalog.Options)
            {
                if (!sidebarPages.TryGetValue(option.PageType, out string pageName))
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
                entries.Add(new TopSearchEntry(option.Id, displayText, string.Join(" ", terms), option.PageType, title, terms, containers));
            }
            foreach (TabOptionDefinition option in TabOptionRegistry)
            {
                Type? pageType = GetSidebarPageForTabOption(option.Key);
                if (pageType == null || !sidebarPages.TryGetValue(pageType, out string pageName))
                {
                    continue;
                }
                string displayText = "Option: " + option.Title + " | " + pageName;
				entries.Add(new TopSearchEntry("TabOption." + option.Key, displayText, string.Join(" ", option.Key, option.Title, option.Description), pageType, option.Title, new[] { option.Key, option.Title, option.Description }));
            }
            _topSearchEntriesList.Clear();
            _topSearchEntries.Clear();
			Dictionary<string, int> displayCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (TopSearchEntry originalEntry in entries)
            {
				int occurrence = displayCounts.TryGetValue(originalEntry.DisplayText, out int count) ? count + 1 : 1;
				displayCounts[originalEntry.DisplayText] = occurrence;
				TopSearchEntry entry = occurrence == 1 ? originalEntry : new TopSearchEntry(originalEntry.Id, originalEntry.DisplayText + " | " + occurrence, originalEntry.SearchText, originalEntry.PageType, originalEntry.TargetText, originalEntry.TargetTerms, originalEntry.ContainerTerms);
                _topSearchEntriesList.Add(entry);
                _topSearchEntries.Add(entry.DisplayText, entry);
            }
            SetTopSearchItems(_topSearchEntriesList);
        }
        catch (Exception ex)
        {
			App.Logger.WriteLine("MainWindow::Search", "Could not populate settings search: " + ex.Message);
        }
    }

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
            if (!pages.ContainsKey(item.PageType))
            {
                pages.Add(item.PageType, name);
            }
        }
        return pages;
    }

    private static Type? GetSidebarPageForTabOption(string key)
    {
        return key switch
        {
            "Roblox FPS Counter" or "Enable Overlay" or "Crosshair" or "Clock" or "Brightness" or "Saturation" or "Contrast" or "Color Temperature" => typeof(ModsPage),
            "Server Ping Counter" or "Server Location Overlay" or "Join Notifications" => typeof(IntegrationsPage),
            "Activity Tracking" or "Query Server Location" or "Desktop App" or "Show Game Activity" or "Show Account On Profile" or "RPC Idle Icon" => typeof(IntegrationsPage),
            "Confirm Launches" or "Disable Background Window" or "Disable RobloxCrashHandler" or "Optimize Roblox" or "Trim Roblox Memory" or "CPU Priority" => typeof(BehaviourPage),
            "Background Snow" or "Gradient Movement" or "Smooth ScrollBar" => typeof(AppearancePage),
            "Update Roblox" or "Process Priority" or "CPU Core Limit" or "Max Concurrent Downloads" => typeof(ChannelPage),
            _ => null
        };
    }

    private void SetTopSearchItems(IEnumerable<TopSearchEntry> entries)
    {
        _topSearchItemsUpdating = true;
        try
        {
            TopSearchBox.ItemsSource = entries.Select(entry => entry.DisplayText).ToList();
        }
        finally
        {
            _topSearchItemsUpdating = false;
        }
    }

    private void IndexLoadedPageSearchEntries()
    {
        if (RootFrame.Content is not Page page)
        {
            _pendingTopSearchEntry = null;
            return;
        }
        page.UpdateLayout();
        CachePageSearchTargets(page);
        _lastPage = page;
        IndexDynamicPageSearchEntries(page);
        PerformSearch(GlobalSearchBox.Text?.Trim() ?? "");
        if (_pendingTopSearchEntry?.PageType == page.GetType())
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
        foreach (FrameworkElement element in EnumerateSearchElements(page))
        {
            if (element.Visibility != Visibility.Visible || element is OptionControl)
            {
                continue;
            }
            string text = GetSearchableElementText(element);
            if (text.Length < 2 || text.Length > 160 || text.Contains("http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string displayText = "Option: " + text + " | " + pageName;
            if (_topSearchEntries.ContainsKey(displayText))
            {
                continue;
            }
			TopSearchEntry entry = new TopSearchEntry("Dynamic." + page.GetType().Name + "." + element.Name + "." + _topSearchEntriesList.Count, displayText, text + " " + element.Name, page.GetType(), text, new[] { text, element.Name });
            _topSearchEntriesList.Add(entry);
            _topSearchEntries.Add(displayText, entry);
        }
        FilterTopSearchItems();
    }

    private void TopSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_topSearchItemsUpdating)
        {
            return;
        }
        FilterTopSearchItems();
    }

    private void FilterTopSearchItems()
    {
        string query = TopSearchBox.Text?.Trim() ?? "";
		List<TopSearchEntry> result = string.IsNullOrWhiteSpace(query)
			? _topSearchEntriesList.Take(80).ToList()
			: _topSearchEntriesList
				.Select(entry => (Entry: entry, Score: ScoreTopSearchEntry(entry, query)))
				.Where(item => item.Score < int.MaxValue)
				.OrderBy(item => item.Score)
				.ThenBy(item => item.Entry.DisplayText.Length)
				.Take(80)
				.Select(item => item.Entry)
				.ToList();
        SetTopSearchItems(result);
        if (query.Length > 0 && result.Count == 0)
        {
            TopSearchBox.IsSuggestionListOpen = false;
        }
    }

    private void TopSearchBox_SuggestionChosen(object sender, RoutedEventArgs e)
    {
        if (_topSearchNavigationPending)
        {
            return;
        }
        string chosen = TopSearchBox.Text?.Trim() ?? "";
        if (chosen.Length == 0 || !_topSearchEntries.TryGetValue(chosen, out TopSearchEntry? entry))
        {
            return;
        }
		QueueTopSearchNavigation(entry);
	}

	private void TopSearchBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter || _topSearchNavigationPending)
		{
			return;
		}
		string query = TopSearchBox.Text?.Trim() ?? string.Empty;
		if (query.Length == 0)
		{
			return;
		}
		TopSearchEntry? entry = _topSearchEntries.TryGetValue(query, out TopSearchEntry? exact)
			? exact
			: _topSearchEntriesList
				.Select(item => (Entry: item, Score: ScoreTopSearchEntry(item, query)))
				.Where(item => item.Score < int.MaxValue)
				.OrderBy(item => item.Score)
				.ThenBy(item => item.Entry.DisplayText.Length)
				.Select(item => item.Entry)
				.FirstOrDefault();
		if (entry == null)
		{
			return;
		}
		e.Handled = true;
		QueueTopSearchNavigation(entry);
	}

	private void QueueTopSearchNavigation(TopSearchEntry entry)
	{
        _pendingTopSearchEntry = entry;
        _topSearchNavigationPending = true;
        TopSearchBox.IsSuggestionListOpen = false;
        Dispatcher.BeginInvoke(new Action(CompleteTopSearchNavigation), DispatcherPriority.Input);
    }

    private void CompleteTopSearchNavigation()
    {
        TopSearchEntry? entry = _pendingTopSearchEntry;
        _pendingTopSearchEntry = null;
        try
        {
            TopSearchBox.Text = "";
            TopSearchBox.IsSuggestionListOpen = false;
            if (entry != null)
            {
                NavigateTopSearchEntry(entry);
            }
        }
        finally
        {
            _topSearchNavigationPending = false;
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

    private FrameworkElement? FindTopSearchTarget(Page page, TopSearchEntry entry)
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

    private static void RevealTopSearchTarget(FrameworkElement target, Page page)
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

	private static IReadOnlyList<FrameworkElement> EnumerateSearchElements(DependencyObject root)
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
        return Regex.Replace(name.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");
    }

    private void TopNavHome_Click(object sender, RoutedEventArgs e)
    {
        NavigateTopNav(typeof(HomePage));
    }

    private void TopNavLibrary_Click(object sender, RoutedEventArgs e)
    {
        _libraryPage ??= new LibraryPage();
        if (RootFrame.Content != _libraryPage)
        {
            RootNavigation.NavigateExternal(_libraryPage);
        }
    }

    private void TopNavCommunity_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is NewsPage newsPage)
        {
            newsPage.SelectForumsTab();
            return;
        }
        _pendingForumsTab = true;
        NavigateTopNav(typeof(NewsPage));
    }

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
        if (!(RootFrame.Content is Page page))
        {
            return;
        }
        if (page != _lastPage || _pageSearchTargets.Count == 0)
        {
            page.UpdateLayout();
            CachePageSearchTargets(page);
            _lastPage = page;
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
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

	private static void ScrollSearchTargetIntoView(FrameworkElement target)
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

    private void ScrollToClosestMatch(IReadOnlyList<PageSearchTarget> matches)
    {
        if (matches.Count == 0)
        {
            return;
        }
        PageSearchTarget fallback = matches[0];
        foreach (PageSearchTarget match in matches)
        {
            ScrollViewer scrollViewer = null;
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
        ScrollViewer fallbackScrollViewer = FindAncestor<ScrollViewer>(fallback.Element);
        if (fallbackScrollViewer != null)
        {
            Point point = fallback.Element.TransformToAncestor(fallbackScrollViewer).Transform(new Point(0.0, 0.0));
            double desiredOffset = fallbackScrollViewer.VerticalOffset + point.Y - Math.Max(0.0, (fallbackScrollViewer.ViewportHeight - fallback.Element.ActualHeight) / 2.0);
            desiredOffset = Math.Max(0.0, Math.Min(fallbackScrollViewer.ScrollableHeight, desiredOffset));
            SmoothScrollTo(fallbackScrollViewer, desiredOffset);
        }
    }

    private void SmoothScrollTo(ScrollViewer scrollViewer, double targetOffset)
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

    private void FlashHighlight(TextBlock tb)
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
		return Regex.Replace(value.Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' '), "\\s+", " ");
	}

    private static bool IsFuzzyMatch(string text, string query)
    {
		string normalizedText = NormalizeSearchText(text);
		string[] terms = NormalizeSearchText(query).Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ':', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            return true;
        }
        string[] words = normalizedText.Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ':', '/', '\\', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
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

    private void AnimateOpacity(UIElement element, double toOpacity, double durationSeconds = 0.5)
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
        ApplyBackgroundState(new GlobalBackground.State(
            _appearanceViewModel.BackgroundFilePath,
            _appearanceViewModel.GradientOpacity,
            _appearanceViewModel.BlackOverlayOpacity,
            _appearanceViewModel.BackgroundEverywhere));
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
        ImageBehavior.SetAnimatedSource(BackgroundImage, null);
        BackgroundImage.Source = null;
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
        if (element == null)
        {
            return Task.CompletedTask;
        }
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_backgroundAnimationWaiters.Remove(element, out TaskCompletionSource<bool>? previous))
            previous.TrySetResult(result: false);
        _backgroundAnimationWaiters[element] = tcs;
        DoubleAnimation doubleAnimation = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        doubleAnimation.Completed += delegate
        {
            if (_backgroundAnimationWaiters.TryGetValue(element, out TaskCompletionSource<bool>? current) && ReferenceEquals(current, tcs))
                _backgroundAnimationWaiters.Remove(element);
            if (!_isClosed)
                element.Visibility = Visibility.Collapsed;
            tcs.TrySetResult(result: true);
        };
        element.BeginAnimation(UIElement.OpacityProperty, doubleAnimation);
        return tcs.Task;
    }

    private Task FadeInElementAsync(UIElement element, double durationSeconds)
    {
        if (element == null)
        {
            return Task.CompletedTask;
        }
        TaskCompletionSource<bool> tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_backgroundAnimationWaiters.Remove(element, out TaskCompletionSource<bool>? previous))
            previous.TrySetResult(result: false);
        _backgroundAnimationWaiters[element] = tcs;
        element.Visibility = Visibility.Visible;
        DoubleAnimation doubleAnimation = new DoubleAnimation
        {
            To = 1.0,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };
        doubleAnimation.Completed += delegate
        {
            if (_backgroundAnimationWaiters.TryGetValue(element, out TaskCompletionSource<bool>? current) && ReferenceEquals(current, tcs))
                _backgroundAnimationWaiters.Remove(element);
            tcs.TrySetResult(result: true);
        };
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
        _discordClient = new DiscordRpcClient("1459679943498661910");
        _discordClient.Logger = new ConsoleLogger
        {
            Level = LogLevel.Warning
        };
        _discordClient.OnReady += DiscordClient_OnReady;
        _discordClient.OnError += DiscordClient_OnError;
        _discordClient.Initialize();
        if (RootNavigation != null)
        {
            RootNavigation.Navigated += RootNavigation_RpcNavigated;
        }
        Activated += MainWindow_ActivatedRpc;
        Closed += MainWindow_ClosedRpc;
        _ = CheckForNewNewsAsync();
        _ = FetchRpcAvatarAsync();
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
            string cookie = Voidstrap.Integrations.RobloxCookie.Get();
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
            var panel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "News", VerticalAlignment = VerticalAlignment.Center });
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 7.0,
                Height = 7.0,
                Margin = new Thickness(6.0, 1.0, 0.0, 0.0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = new SolidColorBrush(Color.FromRgb(248, 113, 113)),
            };
            panel.Children.Add(dot);
            NewsNavItem.Content = panel;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::ShowNewsBadge", ex);
        }
    }

    private void ClearNewsBadge()
    {
        try
        {
            if (NewsNavItem != null && NewsNavItem.Content is not string)
            {
                NewsNavItem.Content = "News";
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
        object obj = RootFrame?.Content;
        if (obj != null)
        {
            text = obj.GetType().Name;
        }
        string item = "";
        IReadOnlyList<NavigationItem> navigationItems = GetNavigationItemsInServiceOrder();
        if (RootNavigation.SelectedPageIndex >= 0 && RootNavigation.SelectedPageIndex < navigationItems.Count && navigationItems[RootNavigation.SelectedPageIndex] is NavigationItem { Content: var content } navigationItem)
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
        return (PageKey: text, Display: item);
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
        if ((DateTime.UtcNow - _lastRobloxCheck).TotalMilliseconds < 1000.0)
        {
            return _robloxRunningCached;
        }
        _lastRobloxCheck = DateTime.UtcNow;
        try
        {
            _robloxRunningCached = AnyProcessRunning("RobloxPlayerBeta") || AnyProcessRunning("RobloxStudioBeta");
        }
        catch
        {
            _robloxRunningCached = false;
        }
        return _robloxRunningCached;
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
        if (!string.IsNullOrEmpty(text) && _voidRpcPageDescriptions.TryGetValue(text, out (string, string) value))
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
        if (details == _lastVoidRpcDetails && state == _lastVoidRpcState)
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
        string largeImageText = (string.IsNullOrWhiteSpace(text3) ? "Voidstrap" : ("Voidstrap v" + text3));
        try
        {
            _discordClient.SetPresence(new DiscordRPC.RichPresence
            {
                Details = details,
                State = state,
                Timestamps = new Timestamps(_voidRpcSessionStart),
                Assets = new Assets
                {
                    LargeImageKey = "https://voidstrapp.pages.dev/assets/img/voidstrap.png",
                    LargeImageText = largeImageText,
                    SmallImageKey = string.IsNullOrEmpty(_voidRpcSmallImageUrl) ? null : _voidRpcSmallImageUrl,
                    SmallImageText = string.IsNullOrEmpty(_voidRpcSmallImageUrl) ? null : (_voidRpcSmallImageText.Length > 0 ? _voidRpcSmallImageText : "Roblox")
                },
                Buttons = new DiscordRPC.Button[2]
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
                }
            });
            _lastVoidRpcDetails = details;
            _lastVoidRpcState = state;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("DiscordRPC", "SetPresence failed: " + ex.Message);
        }
    }

    private void UpdateFastFlagEditorVisibility()
    {
        if (FastFlagEditorNavItem != null && FastFlagEditorNavItem.Visibility != Visibility.Visible)
        {
            FastFlagEditorNavItem.Visibility = Visibility.Visible;
        }
    }

    private void OnWebsiteAuthChanged()
    {
        if (_isClosed)
            return;
        try
        {
            ((DispatcherObject)this).Dispatcher.BeginInvoke((Action)delegate
            {
                if (!_isClosed)
                {
                    UpdateAccountButtonsEnabled();
                    UpdateNotificationsButtonState(true);
                    _notificationRealtime.Start();
                    _ = LoadAccountBorderAsync();
                    if (RootFrame?.Content is not Pages.NotificationsPage)
                        _ = RefreshNotificationBadgeAsync(true);
                }
            });
        }
        catch
        {
        }
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        LoadTabsStructure();
        InitializeNavigation();
        PopulateTopSearch();
        RefreshAccountUi();
        ApplyUiZoom();
        Dispatcher.BeginInvoke(new Action(LogContentDiagnostics), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        LoadSidebarWidth();
        SetupNavShortcuts();
        Dispatcher.BeginInvoke(new Action(ResetNavigationHistory), DispatcherPriority.ApplicationIdle);
        Voidstrap.Utility.WebsiteAuth.Changed -= OnWebsiteAuthChanged;
        Voidstrap.Utility.WebsiteAuth.Changed += OnWebsiteAuthChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Voidstrap.Utility.WebsiteNotifications.UnreadChanged -= OnNotificationsUnreadChanged;
        Voidstrap.Utility.WebsiteNotifications.UnreadChanged += OnNotificationsUnreadChanged;
        _notificationRealtime.Start();
        UpdateNotificationsButtonState(true);
        _ = RefreshNotificationBadgeAsync(true);
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

    private static readonly (Type Page, Key Key, string Label)[] NavShortcuts = new (Type, Key, string)[]
    {
        (typeof(HomePage), Key.D1, "Ctrl+1"),
        (typeof(IntegrationsPage), Key.D2, "Ctrl+2"),
        (typeof(BehaviourPage), Key.D3, "Ctrl+3"),
        (typeof(AppearancePage), Key.D4, "Ctrl+4"),
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

    private void ApplyNavToolTips(System.Collections.IEnumerable items)
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
                StackPanel panel = new StackPanel { Orientation = Orientation.Horizontal };
                panel.Children.Add(new TextBlock
                {
                    Text = item.Content?.ToString() ?? "",
                    VerticalAlignment = VerticalAlignment.Center
                });
                panel.Children.Add(new TextBlock
                {
                    Text = shortcut.Label,
                    Opacity = 0.55,
                    Margin = new Thickness(12.0, 0.0, 0.0, 0.0),
                    VerticalAlignment = VerticalAlignment.Center
                });
                item.ToolTip = new System.Windows.Controls.ToolTip
                {
                    Content = panel,
                    Placement = PlacementMode.Right
                };
                ToolTipService.SetInitialShowDelay(item, 350);
                ToolTipService.SetPlacement(item, PlacementMode.Right);
                break;
            }
        }
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }
        foreach (var shortcut in NavShortcuts)
        {
            if (e.Key == shortcut.Key)
            {
                RootNavigation.Navigate(shortcut.Page);
                e.Handled = true;
                return;
            }
        }
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
        if (Math.Abs(finalScale - 1.0) < 0.001)
        {
            RootFrame.LayoutTransform = Transform.Identity;
            return;
        }
        RootFrame.LayoutTransform = new ScaleTransform(finalScale, finalScale);
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
            if (WindowState == System.Windows.WindowState.Maximized)
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

    private double ClampSidebarWidth(double w)
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
        RootNavigation.Tag = (iconsOnly ? "icons" : "full");
        double font = (iconsOnly ? 11.0 : 11.0 + (w - SidebarIconThreshold) * 4.0 / (SidebarMaxWidth - SidebarIconThreshold));
        ApplyNavFontSize(RootNavigation.Items, font);
        ApplyNavFontSize(RootNavigation.Footer, font);
        if (_sidebarIconMode != iconsOnly)
        {
            _sidebarIconMode = iconsOnly;
            ApplyIconModeLayout(RootNavigation.Items, iconsOnly);
            ApplyIconModeLayout(RootNavigation.Footer, iconsOnly);
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
            if (obj is not Wpf.Ui.Controls.NavigationItem item)
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

    private void SidebarResizer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            if (IntroOverlay != null)
            {
                IntroOverlay.Visibility = Visibility.Collapsed;
            }
            LiftTopNav();
            return;
        }
        Storyboard storyboard = TryFindResource("IntroStoryboard") as Storyboard;
        if (storyboard == null)
        {
            if (IntroOverlay != null)
            {
                IntroOverlay.Visibility = Visibility.Collapsed;
            }
            LiftTopNav();
            return;
        }
        EventHandler onCompleted = null;
        onCompleted = delegate
        {
            storyboard.Completed -= onCompleted;
            try
            {
                storyboard.Remove(IntroOverlay);
            }
            catch
            {
            }
            IntroOverlay.Visibility = Visibility.Collapsed;
            LiftTopNav();
        };
        storyboard.Completed += onCompleted;
        if (IntroContent != null)
        {
            IntroContent.CacheMode = new System.Windows.Media.BitmapCache();
            _introCacheTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.0)
            };
            _introCacheTimer.Tick += IntroCacheTimer_Tick;
            _introCacheTimer.Start();
        }
        IntroOverlay.Visibility = Visibility.Visible;
        storyboard.Begin(IntroOverlay, isControllable: true);
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

    private void RefreshAccountUi()
    {
        LoadAccountAsync();
    }

    private async Task LoadAccountAsync()
    {
        try
        {
            RobloxAccount account = await RobloxCookie.GetAccountAsync();
            _lifetimeCts.Token.ThrowIfCancellationRequested();
            await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate
            {
                if (_isClosed)
                    return;
                if (account == null)
                {
                    if (AccountAvatarButton != null)
                    {
                        AccountAvatarButton.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    if (AccountAvatarButton != null)
                    {
                        AccountAvatarButton.Visibility = Visibility.Visible;
                    }
                    if (AccountDisplayNameText != null)
                    {
                        AccountDisplayNameText.Text = (string.IsNullOrWhiteSpace(account.DisplayName) ? account.Username : account.DisplayName);
                    }
                    if (AccountUsernameText != null)
                    {
                        AccountUsernameText.Text = "@" + account.Username;
                    }
                }
            });
            if (account != null && account.UserId > 0)
            {
                await LoadAvatarAsync(account.UserId);
            }
            await LoadAccountBorderAsync();
        }
        catch
        {
        }
    }

    private void AccountAvatarButton_Click(object sender, RoutedEventArgs e)
    {
        if (AccountPopup == null)
        {
            return;
        }
        if (AccountPopup.IsOpen || JustClosedOverlayPopup())
        {
            AccountPopup.IsOpen = false;
            return;
        }
        UpdateAccountButtonsEnabled();
        AccountPopup.IsOpen = true;
    }

    private long _overlayPopupClosedTicks;

    private bool JustClosedOverlayPopup()
    {
        long elapsed = Environment.TickCount64 - _overlayPopupClosedTicks;
        return elapsed >= 0 && elapsed < 250;
    }

    private void OverlayPopup_Closed(object? sender, EventArgs e)
    {
        _overlayPopupClosedTicks = Environment.TickCount64;
        ReleaseOrphanedCapture();
    }

    private void CloseOverlayPopups()
    {
        if (AccountPopup != null)
        {
            AccountPopup.IsOpen = false;
        }
        if (LaunchTargetPopup != null)
        {
            LaunchTargetPopup.IsOpen = false;
        }
        ReleaseOrphanedCapture();
    }

    private void ReleaseOrphanedCapture()
    {
        IInputElement? captured = Mouse.Captured;
        if (captured == null)
        {
            return;
        }
        if (AccountPopup?.IsOpen == true || LaunchTargetPopup?.IsOpen == true)
        {
            return;
        }
        if (captured is DependencyObject element && Window.GetWindow(element) == this)
        {
            return;
        }
        Mouse.Capture(null);
    }

    private object? _questsReturnPage;
    private object? _shopReturnPage;

    private int _questBadgeCount = -1;

    private void QuestsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Voidstrap.Utility.WebsiteAuth.IsSignedIn())
            return;
        if (AccountPopup != null)
            AccountPopup.IsOpen = false;
        try
        {
            if (RootFrame?.Content is Pages.QuestsPage)
                return;
            _questsReturnPage = RootFrame?.Content;
            ClearQuestBadge();
            Pages.QuestsPage page = new Pages.QuestsPage();
            page.BackRequested += QuestsPage_BackRequested;
            RootNavigation.NavigateExternal(page);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::Quests", ex);
        }
    }

    private void ShopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Voidstrap.Utility.WebsiteAuth.IsSignedIn())
            return;
        if (AccountPopup != null)
            AccountPopup.IsOpen = false;
        try
        {
            if (RootFrame?.Content is Pages.ShopPage)
                return;
            if (RootFrame?.Content is not Pages.BlackMarketPage)
                _shopReturnPage = RootFrame?.Content;
            OpenShopPage();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::Shop", ex);
        }
    }

    private void OpenShopPage()
    {
        Pages.ShopPage page = new Pages.ShopPage();
        page.BackRequested += ShopPage_BackRequested;
        page.MarketRequested += ShopPage_MarketRequested;
        RootNavigation.NavigateExternal(page);
    }

    private void OpenMarketPage()
    {
        Pages.BlackMarketPage page = new Pages.BlackMarketPage();
        page.BackRequested += MarketPage_BackRequested;
        page.ShopRequested += MarketPage_ShopRequested;
        RootNavigation.NavigateExternal(page);
    }

    private void ShopPage_MarketRequested(object? sender, EventArgs e)
    {
        try
        {
            if (sender is Pages.ShopPage page)
            {
                page.BackRequested -= ShopPage_BackRequested;
                page.MarketRequested -= ShopPage_MarketRequested;
            }
            OpenMarketPage();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::ShopToMarket", ex);
        }
    }

    private void MarketPage_ShopRequested(object? sender, EventArgs e)
    {
        try
        {
            if (sender is Pages.BlackMarketPage page)
            {
                page.BackRequested -= MarketPage_BackRequested;
                page.ShopRequested -= MarketPage_ShopRequested;
            }
            OpenShopPage();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::MarketToShop", ex);
        }
    }

    private void ShopPage_BackRequested(object? sender, EventArgs e)
    {
        if (sender is Pages.ShopPage page)
        {
            page.BackRequested -= ShopPage_BackRequested;
            page.MarketRequested -= ShopPage_MarketRequested;
        }
        ReturnFromShop();
    }

    private void MarketPage_BackRequested(object? sender, EventArgs e)
    {
        if (sender is Pages.BlackMarketPage page)
        {
            page.BackRequested -= MarketPage_BackRequested;
            page.ShopRequested -= MarketPage_ShopRequested;
        }
        ReturnFromShop();
    }

    private void ReturnFromShop()
    {
        try
        {
            if (_shopReturnPage != null)
                RootNavigation.NavigateExternal(_shopReturnPage);
            else
                RootNavigation.Navigate(typeof(Pages.HomePage));
            _shopReturnPage = null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::ShopBack", ex);
        }
    }

    private void QuestsPage_BackRequested(object? sender, EventArgs e)
    {
        try
        {
            if (sender is Pages.QuestsPage page)
                page.BackRequested -= QuestsPage_BackRequested;
            if (_questsReturnPage != null)
                RootNavigation.NavigateExternal(_questsReturnPage);
            else
                RootNavigation.Navigate(typeof(Pages.HomePage));
            _questsReturnPage = null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::QuestsBack", ex);
        }
    }

    private void NotificationsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Voidstrap.Utility.WebsiteAuth.IsSignedIn())
            return;
        if (AccountPopup != null)
            AccountPopup.IsOpen = false;
        try
        {
            if (RootFrame?.Content is Pages.NotificationsPage)
                return;
            _notificationsReturnPage = RootFrame?.Content;
            Pages.NotificationsPage page = new Pages.NotificationsPage();
            page.BackRequested += NotificationsPage_BackRequested;
            _activeNotificationsPage = page;
            RootNavigation.NavigateExternal(page);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::Notifications", ex);
        }
    }

    private void NotificationsPage_BackRequested(object? sender, EventArgs e)
    {
        try
        {
            if (sender is Pages.NotificationsPage page)
                page.BackRequested -= NotificationsPage_BackRequested;
            _activeNotificationsPage = null;
            object? returnPage = _notificationsReturnPage;
            _notificationsReturnPage = null;
            if (returnPage != null && returnPage is not Pages.NotificationsPage)
                RootNavigation.NavigateExternal(returnPage);
            else
                RootNavigation.Navigate(typeof(Pages.HomePage));
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::NotificationsBack", ex);
        }
    }

    private void UpdateNotificationsButtonState(bool resetCount = false)
    {
        bool signedIn = Voidstrap.Utility.WebsiteAuth.IsSignedIn();
        if (NotificationsButton != null)
            NotificationsButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            QuestsButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            if (ShopButton != null)
                ShopButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            if (signedIn)
                RefreshQuestBadge();
            else
                ApplyQuestBadge(0);
        if (!signedIn || resetCount)
        {
            if (resetCount)
            {
                _lastNotificationRefreshTicks = 0;
                _notificationUnread = 0;
            }
            if (NotificationsBadge != null)
                NotificationsBadge.Visibility = Visibility.Collapsed;
            if (NotificationsBadgeText != null)
                NotificationsBadgeText.Text = "0";
        }
        if (!signedIn && AccountPopup != null)
            AccountPopup.IsOpen = false;
    }

    private void OnNotificationsUnreadChanged(int unread)
    {
        if (_isClosed)
            return;
        try
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyNotificationUnread(unread)));
        }
        catch
        {
        }
    }

    private static string QuestDayKey()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd");
    }

    private void RefreshQuestBadge()
    {
        if (_isClosed || !Voidstrap.Utility.WebsiteAuth.IsSignedIn())
            return;
        try
        {
            string today = QuestDayKey();
            string last = App.Settings.Prop.QuestBadgeLastDay ?? "";
            if (last.Length == 0)
            {
                App.Settings.Prop.QuestBadgeLastDay = today;
                App.Settings.Prop.QuestBadgeCount = 1;
                App.Settings.SaveDeferred();
            }
            else if (!string.Equals(last, today, StringComparison.Ordinal))
            {
                App.Settings.Prop.QuestBadgeLastDay = today;
                App.Settings.Prop.QuestBadgeCount = Math.Min(99, App.Settings.Prop.QuestBadgeCount + 1);
                App.Settings.SaveDeferred();
            }
            ApplyQuestBadge(App.Settings.Prop.QuestBadgeCount);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::RefreshQuestBadge", ex);
        }
    }

    private void ClearQuestBadge()
    {
        try
        {
            App.Settings.Prop.QuestBadgeLastDay = QuestDayKey();
            App.Settings.Prop.QuestBadgeCount = 0;
            App.Settings.SaveDeferred();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::ClearQuestBadge", ex);
        }
        ApplyQuestBadge(0);
    }

    private void ApplyQuestBadge(int count)
    {
        if (_isClosed || QuestsBadge == null || QuestsBadgeText == null)
            return;
        count = Math.Clamp(count, 0, 100);
        bool changed = _questBadgeCount != count;
        _questBadgeCount = count;
        QuestsBadgeText.Text = count > 99 ? "99+" : count.ToString();
        QuestsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (count > 0 && changed)
        {
            CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Duration duration = new Duration(TimeSpan.FromMilliseconds(180));
            QuestsBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.72, 1.0, duration) { EasingFunction = ease });
            QuestsBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.72, 1.0, duration) { EasingFunction = ease });
            QuestsBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1.0, duration) { EasingFunction = ease });
        }
    }

    private void ApplyNotificationUnread(int unread)
    {
        if (_isClosed || !Voidstrap.Utility.WebsiteAuth.IsSignedIn())
            return;
        unread = Math.Clamp(unread, 0, 100);
        bool changed = _notificationUnread != unread;
        _notificationUnread = unread;
        NotificationsButton.Visibility = Visibility.Visible;
        QuestsButton.Visibility = Visibility.Visible;
        if (ShopButton != null)
            ShopButton.Visibility = Visibility.Visible;
        RefreshQuestBadge();
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

    private async Task RefreshNotificationBadgeAsync(bool force = false)
    {
        if (_isClosed || !Voidstrap.Utility.WebsiteAuth.IsSignedIn())
            return;
        long now = Environment.TickCount64;
        if (!force && now - _lastNotificationRefreshTicks < 30000L)
            return;
        if (Interlocked.Exchange(ref _notificationRefreshRunning, 1) != 0)
        {
            if (force)
                Interlocked.Exchange(ref _notificationRefreshRequested, 1);
            return;
        }
        _lastNotificationRefreshTicks = now;
        try
        {
            await Voidstrap.Utility.WebsiteNotifications.GetAsync(_lifetimeCts.Token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _notificationRefreshRunning, 0);
            if (!_isClosed && Interlocked.Exchange(ref _notificationRefreshRequested, 0) != 0)
                _ = RefreshNotificationBadgeAsync(true);
        }
    }

    private void UpdateAccountButtonsEnabled()
    {
        bool signedIn = Voidstrap.Utility.WebsiteAuth.IsSignedIn();
        if (AccountFriendsButton != null)
        {
            AccountFriendsButton.IsEnabled = signedIn;
        }
        if (AccountSignOutButton != null)
        {
            AccountSignOutButton.IsEnabled = signedIn;
        }
    }

    private void RepositionAccountPopup()
    {
        if (AccountPopup == null || !AccountPopup.IsOpen)
        {
            return;
        }
        double offset = AccountPopup.HorizontalOffset;
        AccountPopup.HorizontalOffset = offset + 0.5;
        AccountPopup.HorizontalOffset = offset;
    }

    private void MainWindow_LocationChanged(object? sender, EventArgs e)
    {
        if (LaunchTargetPopup != null)
        {
            LaunchTargetPopup.IsOpen = false;
        }
        RepositionAccountPopup();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (LaunchTargetPopup != null)
        {
            LaunchTargetPopup.IsOpen = false;
        }
        RepositionAccountPopup();
    }


    private void AccountFriends_Click(object sender, RoutedEventArgs e)
    {
        if (AccountPopup != null)
        {
            AccountPopup.IsOpen = false;
        }
        try
        {
            if (RootFrame?.Content is Pages.FriendsPage)
            {
                return;
            }
            _friendsReturnIndex = RootNavigation.SelectedPageIndex;
            Pages.FriendsPage page = new Pages.FriendsPage();
            page.BackRequested += FriendsPage_BackRequested;
            RootNavigation.NavigateExternal(page);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::AccountFriends", ex);
        }
    }

    private void FriendsPage_BackRequested(object? sender, EventArgs e)
    {
        try
        {
            if (sender is Pages.FriendsPage page)
            {
                page.BackRequested -= FriendsPage_BackRequested;
            }
            int num = ResolveSafeNavigationIndex((_friendsReturnIndex >= 0) ? _friendsReturnIndex : App.State.Prop.LastPage);
            IReadOnlyList<NavigationItem> navigationItems = GetNavigationItemsInServiceOrder();
            if (num >= 0 && num < navigationItems.Count && navigationItems[num] is NavigationItem { PageType: not null } navigationItem)
            {
                RootNavigation.Navigate(navigationItem.PageType);
            }
            else
            {
                RootNavigation.Navigate(typeof(Pages.HomePage));
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::FriendsBack", ex);
        }
    }

    private async void AccountSignOut_Click(object sender, RoutedEventArgs e)
    {
        if (AccountPopup != null)
        {
            AccountPopup.IsOpen = false;
        }
        try
        {
            string? token = Voidstrap.Utility.WebsiteAuth.GetToken();
            Voidstrap.Utility.WebsiteAuth.Clear();
            RefreshAccountUi();
            UpdateAccountButtonsEnabled();
            if (!string.IsNullOrEmpty(token))
            {
                try
                {
                    using System.Net.Http.HttpRequestMessage req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, App.WebsiteBaseUrl + "/api/auth/logout");
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    using System.Net.Http.HttpResponseMessage resp = await App.HttpClient.SendAsync(req, _lifetimeCts.Token).ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (Exception serverEx)
                {
                    App.Logger.WriteException("MainWindow::AccountSignOutServer", serverEx);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("MainWindow::AccountSignOut", ex);
        }
    }

    private async Task LoadAccountBorderAsync()
    {
        string activeAccount = Voidstrap.Utility.WebsiteAuth.GetActiveId() ?? "";
        try
        {
            Voidstrap.Utility.WebsiteBorderData data = await Voidstrap.Utility.WebsiteBorderRenderer.FetchActiveAsync(20.0, 30.0).ConfigureAwait(continueOnCapturedContext: false);
            if (_isClosed || activeAccount != (Voidstrap.Utility.WebsiteAuth.GetActiveId() ?? ""))
                return;
            await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate
            {
                if (_isClosed || activeAccount != (Voidstrap.Utility.WebsiteAuth.GetActiveId() ?? ""))
                    return;
                try
                {
                    if (AccountAvatarRing != null)
                    {
                        System.Windows.Media.Brush ringBrush = (data != null && !string.IsNullOrEmpty(data.GradientBorderKey))
                            ? Voidstrap.Utility.GradientProfileBorder.ParseBorder(data.GradientBorderKey)
                            : null;
                        AccountAvatarRing.Background = ringBrush ?? (System.Windows.Application.Current.TryFindResource("ControlFillColorSecondaryBrush") as System.Windows.Media.Brush);
                    }
                    if (AccountBorderImage != null)
                    {
                        if (data != null && data.ImageBorder != null)
                        {
                            AccountBorderImage.Source = data.ImageBorder.Image;
                            AccountBorderImage.Width = data.ImageBorder.Width;
                            AccountBorderImage.Height = data.ImageBorder.Height;
                            AccountBorderImage.Margin = data.ImageBorder.Margin;
                            System.Windows.Controls.Panel.SetZIndex(AccountBorderImage, data.ImageBorder.ZIndex);
                            AccountBorderImage.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            AccountBorderImage.Visibility = Visibility.Collapsed;
                        }
                    }
                }
                catch
                {
                }
            });
        }
        catch
        {
        }
    }

    private async Task LoadAvatarAsync(long userId)
    {
        try
        {
            CancellationToken token = _lifetimeCts.Token;
            string requestUri = $"https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds={userId}&size=150x150&format=Png&isCircular=false";
            using JsonDocument doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString(requestUri, token).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("data", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                return;
            }
            string text = null;
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.TryGetProperty("imageUrl", out var value2) && value2.ValueKind == JsonValueKind.String)
                {
                    text = value2.GetString();
                    break;
                }
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            BitmapSource? bitmap = await Voidstrap.Utility.AppImage.LoadAsync(text, 150, token).ConfigureAwait(false);
            if (bitmap == null)
            {
                return;
            }
            await ((DispatcherObject)this).Dispatcher.InvokeAsync((Action)delegate
            {
                if (_isClosed)
                    return;
                if (AccountAvatarBrush != null)
                {
                    AccountAvatarBrush.ImageSource = bitmap;
                }
                if (AccountPopupAvatarBrush != null)
                {
                    AccountPopupAvatarBrush.ImageSource = bitmap;
                }
            });
        }
        catch
        {
        }
    }

    private void MainWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        RepositionAccountPopup();
        ApplyUiZoom();
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
            if (Voidstrap.Utility.WebsiteAuth.IsSignedIn() && Environment.TickCount64 - _lastBorderRefreshTicks > 30000L)
            {
                _lastBorderRefreshTicks = Environment.TickCount64;
                _ = LoadAccountBorderAsync();
            }
            if (RootFrame?.Content is Pages.NotificationsPage notificationsPage)
                notificationsPage.Refresh();
            else
                _ = RefreshNotificationBadgeAsync();
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
            content = (studio ? (installed ? "Save and Launch Studio" : "Install Studio") : (installed ? "Save and Launch" : "Install"));
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
            Voidstrap.AppData.IAppData appData = (studio ? ((Voidstrap.AppData.IAppData)new Voidstrap.AppData.RobloxStudioData()) : ((Voidstrap.AppData.IAppData)new Voidstrap.AppData.RobloxPlayerData()));
            if (!string.IsNullOrEmpty(appData.State.VersionGuid) && File.Exists(appData.ExecutablePath))
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
        IReadOnlyList<NavigationItem> items = GetNavigationItemsInServiceOrder();
        if (IsUsable(requested))
        {
            return requested;
        }
        for (int i = 0; i < items.Count; i++)
        {
            if (IsUsable(i))
            {
                return i;
            }
        }
        return 0;
        bool IsUsable(int num)
        {
            if (num < 0 || num >= items.Count)
            {
                return false;
            }
            NavigationItem navigationItem = items[num];
            if (!navigationItem.IsEnabled)
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
		RestartNotificationCard.Visibility = Visibility.Visible;
		RestartNotificationCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
		});
		RestartNotificationTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28.0, 0.0, TimeSpan.FromMilliseconds(220))
		{
			EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
		});
	}

	private void HideRestartNotification()
	{
		if (RestartNotificationCard == null)
		{
			return;
		}

		RestartNotificationCard.BeginAnimation(OpacityProperty, null);
		RestartNotificationTranslate?.BeginAnimation(TranslateTransform.XProperty, null);
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

	private void RestartVoidstrapFromSettings()
	{
		string executable = "";
		try
		{
			executable = Paths.Application ?? "";
		}
		catch
		{
		}

		if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
		{
			executable = Environment.ProcessPath ?? "";
		}
		if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
		{
			throw new FileNotFoundException("The Voidstrap executable could not be found");
		}

		using Process? process = Process.Start(new ProcessStartInfo
		{
			FileName = executable,
			Arguments = "-settings",
			UseShellExecute = true,
			WorkingDirectory = System.IO.Path.GetDirectoryName(executable) ?? ""
		});
		if (process == null)
		{
			throw new InvalidOperationException("The Voidstrap restart process did not start");
		}

		App.Logger.WriteLine("MainWindow::RestartNotification", "Restarting Voidstrap from settings");
		RestartNotificationService.ClearAll();
		Application.Current.Shutdown();
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
                ((DispatcherObject)this).Dispatcher.InvokeAsync<bool?>((Func<bool?>)(() => AlreadyRunningSnackbar?.Show()));
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
        SaveTabsStructure();
        SaveWindowState();
    }

    private void WpfUiWindow_Closed(object sender, EventArgs e)
    {
        if (_isClosed)
            return;
        _isClosed = true;
		Interlocked.Increment(ref _topSearchNavigationGeneration);
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
        RootFrame.Navigated -= RootFrame_Navigated;
        WorkspaceTabs.PreviewMouseLeftButtonDown -= WorkspaceTabs_PreviewMouseLeftButtonDown;
        AccountPopup.Closed -= OverlayPopup_Closed;
        LaunchTargetPopup.Closed -= OverlayPopup_Closed;
        RootNavigation.NavigationFailed -= OnNavigationFailed;
        RootNavigation.Navigated -= SaveNavigation;
        RootNavigation.Navigated -= RootNavigation_RpcNavigated;
        GlobalBackground.Changed -= OnGlobalBackgroundChanged;
        RestartNotificationService.Changed -= OnRestartRequirementsChanged;
        try
        {
            _visibilityTimer.Stop();
            _visibilityTimer.Tick -= VisibilityTimer_Tick;
            SnowCanvas?.Dispose();
            Voidstrap.Utility.WebsiteAuth.Changed -= OnWebsiteAuthChanged;
            Voidstrap.Utility.WebsiteNotifications.UnreadChanged -= OnNotificationsUnreadChanged;
            _notificationRealtime.Dispose();
            if (_activeNotificationsPage != null)
            {
                _activeNotificationsPage.BackRequested -= NotificationsPage_BackRequested;
                _activeNotificationsPage = null;
            }
            StopIntroCacheTimer();
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
        try
        {
            if (_discordClient != null)
            {
                _discordClient.OnReady -= DiscordClient_OnReady;
                _discordClient.OnError -= DiscordClient_OnError;
            }
        }
        catch
        {
        }
        DiscordRpcClient? discordClient = _discordClient;
        _discordClient = null;
		_discordReady = false;
        try
        {
            discordClient?.Dispose();
        }
        catch
        {
        }
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
            LaunchHandler.LaunchRoblox(LaunchMode.Player);
        }
        else if (!App.WebsiteTakeoverActive)
        {
            App.SoftTerminate();
        }
    }

    private void ReleaseBackgroundResources()
    {
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
        LaunchTargetList?.Children.Clear();
        _libraryPage = null;
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
            viewModel.Tabs?.Clear();
        }
        base.DataContext = null;
    }

    private void SaveWindowState()
    {
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
            _state.WidthUpdateV2 = base.Width;
            _state.HeightUpdateV2 = base.Height;
            _state.TopUpdateV2 = base.Top;
            _state.LeftUpdateV2 = base.Left;
        }
        App.State.Save();
    }

    private void SaveNavigation(INavigation sender, RoutedNavigationEventArgs e)
    {
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
        if (LaunchTargetPopup == null)
        {
            return;
        }
        if (LaunchTargetPopup.IsOpen || JustClosedOverlayPopup())
        {
            LaunchTargetPopup.IsOpen = false;
            return;
        }
        PopulateLaunchTargets();
        LaunchTargetPopup.IsOpen = true;
    }

    private void PopulateLaunchTargets()
    {
        if (LaunchTargetList == null)
        {
            return;
        }
        LaunchTargetList.Children.Clear();
        string currentKind = "player";
        string currentCode = "";
        if (base.DataContext is MainWindowViewModel vm)
        {
            if (!string.IsNullOrEmpty(vm.SelectedLaunchClient) && Voidstrap.Utility.ClassicClients.IsClientInstalled(vm.SelectedLaunchClient))
            {
                currentKind = "client";
                currentCode = vm.SelectedLaunchClient;
            }
            else if (vm.SelectedLaunchModeIndex == 1)
            {
                currentKind = "studio";
            }
        }
        Wpf.Ui.Controls.Button selectedButton = AddLaunchTargetButton("Roblox", "player", "", currentKind == "player");
        Wpf.Ui.Controls.Button studioButton = AddLaunchTargetButton("Roblox Studio", "studio", "", currentKind == "studio");
        if (currentKind == "studio")
        {
            selectedButton = studioButton;
        }
        try
        {
            foreach (string code in Voidstrap.Utility.ClassicClients.ListInstalledClients())
            {
                var config = Voidstrap.Utility.ClassicClients.GetInstalledConfig(code);
                string name = (config != null && !string.IsNullOrWhiteSpace(config.Name)) ? config.Name : code;
                bool isSelected = currentKind == "client" && string.Equals(currentCode, code, StringComparison.OrdinalIgnoreCase);
                Wpf.Ui.Controls.Button clientButton = AddLaunchTargetButton(name + "  (" + code + ")", "client", code, isSelected);
                if (isSelected)
                {
                    selectedButton = clientButton;
                }
            }
        }
        catch
        {
        }
        if (selectedButton != null)
        {
            selectedButton.Dispatcher.BeginInvoke((Action)delegate
            {
                try
                {
                    selectedButton.BringIntoView();
                }
                catch
                {
                }
            }, DispatcherPriority.Loaded);
        }
    }

    private Wpf.Ui.Controls.Button AddLaunchTargetButton(string label, string kind, string code, bool selected)
    {
        Wpf.Ui.Controls.Button button = new Wpf.Ui.Controls.Button
        {
            Content = label,
            Appearance = (selected ? Wpf.Ui.Common.ControlAppearance.Primary : Wpf.Ui.Common.ControlAppearance.Secondary),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0.0, 0.0, 0.0, 4.0),
            Tag = kind + "|" + code
        };
        button.Click += LaunchTargetItem_Click;
        LaunchTargetList.Children.Add(button);
        return button;
    }

    private void LaunchTargetItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button button && button.Tag is string tag)
        {
            int sep = tag.IndexOf('|');
            string kind = (sep >= 0) ? tag.Substring(0, sep) : tag;
            string code = (sep >= 0) ? tag.Substring(sep + 1) : "";
            if (base.DataContext is MainWindowViewModel mainWindowViewModel)
            {
                if (kind == "client")
                {
                    mainWindowViewModel.SelectedLaunchClient = code;
                }
                else
                {
                    mainWindowViewModel.SelectedLaunchClient = "";
                    mainWindowViewModel.SelectedLaunchModeIndex = (kind == "studio") ? 1 : 0;
                }
            }
        }
        UpdateButtonContent();
        if (LaunchTargetPopup != null)
        {
            LaunchTargetPopup.IsOpen = false;
        }
    }
}
