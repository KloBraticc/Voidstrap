using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Serialization;
using Voidstrap.Enums;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Models.Persistable
{
    /// <summary>
    /// Represents configuration settings for Voidstrap.
    /// </summary>
    public class AppSettings
    {
        // General Configuration
        public BootstrapperStyle BootstrapperStyle { get; set; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? BootstrapperStyle.FluentAeroDialog : BootstrapperStyle.FluentDialog;
        public BootstrapperScale BootstrapperScale { get; set; } = BootstrapperScale.Normal;
        public BootstrapperIcon BootstrapperIcon { get; set; } = BootstrapperIcon.IconVoidstrap;
        public BootstrapperIcon StudioBootstrapperIcon { get; set; }
        public bool RiShadeEnabled { get; set; } = false;
        public int AntiAliasingMethodIndex { get; set; } = 0;
        public int FrameGenModeIndex { get; set; } = 0;
        public bool FrameGenOverlayShow { get; set; } = false;
        public int FrameGenResumeIndex { get; set; } = 0;
        public bool FrameGenAutoElevate { get; set; } = true;
        public bool FrameGenUncap { get; set; } = false;
        public bool FrameGenSplitCompare { get; set; } = false;
        public int FrameGenTargetFps { get; set; } = 0;
        public bool RobloxApiDumpTool { get; set; } = false;
        public string LastSeenNewsKey { get; set; } = "";
        public bool ShowLaunchProfile { get; set; } = true;

        [JsonIgnore]
        public BootstrapperIcon ActiveBootstrapperIcon
        {
            get
            {
                if (App.LaunchSettings == null || (App.LaunchSettings.RobloxLaunchMode != LaunchMode.Studio && App.LaunchSettings.RobloxLaunchMode != LaunchMode.StudioAuth))
                    return BootstrapperIcon;
                return StudioBootstrapperIcon;
            }
        }

        public CleanerOptions CleanerOptions { get; set; } = CleanerOptions.Never;
        public List<string> CleanerDirectories { get; set; } = [];
        public string BootstrapperTitle { get; set; } = App.ProjectName;
        public string BootstrapperIconCustomLocation { get; set; } = "";
        public Theme Theme2 { get; set; } = Theme.Dark;
        public BackdropType WindowBackdrop { get; set; } = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? BackdropType.Mica : BackdropType.None;
        public string? SelectedCustomTheme { get; set; } = null;
        public bool CheckForUpdates { get; set; } = true;
        public bool AssetWarpEnabled { get; set; } = false;
        public bool AssetWarpCertificateApproved { get; set; }
        public bool AssetWarpDisableAllTextures { get; set; } = false;
        public bool AssetWarpDisableAllDecals { get; set; } = false;
        public bool AssetWarpDisableAllImages { get; set; } = false;
        public bool AssetWarpDisableAllAnimations { get; set; } = false;
        public bool AssetWarpDisableAllMeshes { get; set; } = false;
        public bool AssetWarpPreloadEnabled { get; set; } = false;
        public int AssetWarpPreloadCacheMb { get; set; } = 2048;
        public bool AssetWarpPreloadAvatar { get; set; } = true;
        public bool AssetWarpPreloadCrossGame { get; set; } = true;
        public bool AssetWarpPreloadFlagsOwned { get; set; }
        public Dictionary<string, string?> AssetWarpPreloadFlagBackup { get; set; } = [];
        public bool BlockRobloxTelemetry { get; set; }
        public bool RobloxMinimizeToTray { get; set; }
        public bool RobloxLaunchAtStartup { get; set; }
        public bool StudioPluginEnabled { get; set; }
        public int PresenceSpoofMode { get; set; }
        public bool DuckRobloxAudioOnUnfocus { get; set; }
        public bool EnableHeadsetLoudness { get; set; }
        public bool ResetRobloxAudioOnNextLaunch { get; set; } = true;
        public double Saturation { get; set; } = 100.0;
        public double Contrast { get; set; } = 100.0;
        public double ColorTemperature { get; set; }
        public bool ColorBlindnessEnabled { get; set; } = false;
        public int ColorBlindnessType { get; set; } = 1;
        public double ColorBlindnessSeverity { get; set; } = 100.0;
        public bool ColorBlindnessSimulate { get; set; } = false;
        public bool MultiAccount { get; set; }
		public int MaxConcurrentDownloads { get; set; } = 20;
		public int MaxDownloadSegments { get; set; } = 12;
		public int DownloadBufferKb { get; set; } = 2048;
		public int DownloadPipelineVersion { get; set; } = 3;
        public bool ConfirmLaunches { get; set; } = true;
        public string VoidstrapMatchmakerPresetUrl { get; set; } = "";
        public int LaunchSelectionIndex { get; set; }
        public bool LaunchRobloxWebsite
        {
            get => false;
            set { }
        }
        public string SelectedCpuPriority { get; set; } = "Automatic";
        public int MaxCpuCores { get; set; } = Environment.ProcessorCount;
        public int TotalLogicalCores { get; set; } = Environment.ProcessorCount;
        public int TotalPhysicalCores { get; set; } = Environment.ProcessorCount;
        public bool IsChannelEnabled { get; set; } = false;
        public bool UpdateRoblox { get; set; } = true;

        public bool ForceRobloxReinstall { get; set; } = false;

        public double CustomFontScale { get; set; } = 1.0;

        public double CustomDeathSoundVolume { get; set; } = 1.0;

        public int CleanRobloxNumber = 0;
        public bool DisableCrash { get; set; } = false;
        public int CpuCoreLimit { get; set; } = Environment.ProcessorCount;
        public string ShiftlockCursorSelectedPath { get; set; } = "";
        public string UseCustomIcon { get; set; } = "";
        public string CustomGameName { get; set; } = "";
        public string PriorityLimit { get; set; } = "Normal";
        public string SelectedStatus { get; set; } = "Gray";
        public string ArrowCursorSelectedPath { get; set; } = "";
        public string ArrowFarCursorSelectedPath { get; set; } = "";
        public string IBeamCursorSelectedPath { get; set; } = "";

        public bool DisableSplashScreen { get; set; } = true;
        public bool EnableAnalytics { get; set; } = true;
        public bool ShouldExportConfig { get; set; } = true;
        public bool ShouldExportLogs { get; set; } = true;
        public bool UseFastFlagManager { get; set; } = true;
        public bool? SoberAllowGamepadPermission { get; set; }
        public bool? SoberCloseOnLeave { get; set; }
        public bool? SoberDiscordRpcEnabled { get; set; }
        public bool? SoberDiscordRpcShowJoinButton { get; set; }
        public bool? SoberEnableGameMode { get; set; }
        public bool? SoberEnableHiDpi { get; set; }
        public bool? SoberEnableMobileHomeScreen { get; set; }
        public SoberGraphicsOptimizationMode? SoberGraphicsOptimizationMode { get; set; }
        public bool? SoberServerLocationIndicatorEnabled { get; set; }
        public SoberTouchMode? SoberTouchMode { get; set; }
        public bool? SoberUseConsoleExperience { get; set; }
        public bool? SoberUseLibsecret { get; set; }
        public bool? SoberUseOpenGl { get; set; }
        public bool WPFSoftwareRender { get; set; } = false;
        public bool SmooothBARRyesirikikthxlucipook { get; set; } = false; // wanna keep this on false so people may not be annoyed by it being on
        public bool HasLaunchedGame { get; set; } = false;
        public bool NotificationWindowShow { get; set; } = true;
        public bool BackgroundWindow { get; set; } = true;
        public bool UsePlaceId { get; set; } = false;
        public bool ClearFont { get; set; } = false;

        public bool Fleasion { get; set; } = false;
        public bool RojoEnabled { get; set; } = false;
        public string RojoProjectPath { get; set; } = "";
        public string PlaceId { get; set; } = "";
        public bool OptimizeRoblox { get; set; } = false;
        public bool BypassEmulationOverhead { get; set; } = false;

        public bool RobloxEfficiencyMode { get; set; } = false;
        public bool ReduceMemoryOutOfFocus { get; set; } = false;
        public bool BackgroundUpdatesEnabled { get; set; } = true;
        public bool VoidNotify { get; set; } = true;
        public bool ServerPingCounter { get; set; } = false;
        public bool ShowServerDetailsUI { get; set; } = false;
        public bool EnableCustomStatusDisplay { get; set; } = true;
        public bool RenameClientToEuroTrucks2 { get; set; } = false;
        public bool SnowWOWSOCOOLWpfSnowbtw { get; set; } = false;


        public string ClientPath { get; set; } = Path.Combine(Paths.Base, "Roblox", "Player");

        public string Locale { get; set; } = "nil";
        public string BufferSizeKbte { get; set; } = "1024";
        public string BufferSizeKbtes { get; set; } = "2048";
        public string SkyboxName { get; set; } = "Default";
        public string FontName { get; set; } = "Default";
        public string LastServerSave { get; set; } = "112757576021097";
        public bool SkyBoxDataSending { get; set; } = false;

        public bool FFlagRPCDisplayer { get; set; } = false;

        public bool WebsiteQuestTracking { get; set; } = true;

        public bool QuestCompleteNotifications { get; set; } = true;

        public string QuestBadgeLastDay { get; set; } = "";

        public int QuestBadgeCount { get; set; }


        public bool CurrentTimeDisplay { get; set; } = false;

        public string ClockTimeZoneId { get; set; } = "";

        public bool Clock24Hour { get; set; } = false;
        public bool ExclusiveFullscreen { get; set; } = false;
        public bool Crosshair { get; set; } = false;
        public int CrosshairShapeIndex { get; set; }
        public string CrosshairColorHex { get; set; } = "#FF00FF00";
        public string CrosshairOutlineColorHex { get; set; } = "#FF000000";
        public int CrosshairSize { get; set; } = 20;
        public int CrosshairLineThickness { get; set; } = 2;
        public int CrosshairGap { get; set; } = 4;
        public double CrosshairOpacity { get; set; } = 1.0;
        public bool GameWIP { get; set; } = false;

        // Analytics & Tracking
        public bool DarkTextures { get; set; } = false;
        public bool EnableActivityTracking { get; set; } = true;
        public bool OverClockCPU { get; set; } = false;
        public bool ExitOnDissy { get; set; } = false;
        public bool ServerUptimeBetterBLOXcuzitsbetterXD { get; set; } = true;

        public string DownloadingStringFormat { get; set; } = Strings.Bootstrapper_Status_Downloading + " {0} - {1}MB / {2}MB";
        public bool ConnectCloset { get; set; } = false;

        public bool GameChatEnabled { get; set; } = false;
        public int GameChatOffsetX { get; set; } = 2;
        public int GameChatOffsetY { get; set; } = 9;
        public int GameChatWindowWidth { get; set; } = 500;
        public int GameChatWindowHeight { get; set; } = 400;
        public string GameChatFilter { get; set; } = "default";
        public bool GameChatVerified { get; set; } = false;
        public long GameChatRobloxUserId { get; set; } = 0;
        public bool GameChatBridgeEnabled { get; set; } = true;

        public bool GameIconChecked { get; set; } = true;
        public bool ServerLocationGame { get; set; } = false;
        public bool GameNameChecked { get; set; } = true;
        public bool GameCreatorChecked { get; set; } = true;
        public bool GameStatusChecked { get; set; } = true;

        // Rich Presence (Discord Integration)
        public bool UseDiscordRichPresence { get; set; } = true;
        public bool HideRPCButtons { get; set; } = true;
        public bool ShowAccountOnRichPresence { get; set; } = true;
        public bool ShowServerDetails { get; set; } = true;

        public bool OverlaysEnabled { get; set; } = false;

        public bool HomepageBackgroundOverlayEnabled { get; set; } = false;

        public string HomepageBackgroundOverlayColor { get; set; } = "#121215";

        public string HomepageBackgroundOverlayMediaPath { get; set; } = "";

        public bool HomepageBackgroundOverlayGradientEnabled { get; set; } = false;

        public string HomepageBackgroundOverlayGradientColor { get; set; } = "#5B2EFF";

        public double HomepageBackgroundOverlayGradientAngle { get; set; } = 90;

        public string HomepageBackgroundOverlayMode { get; set; } = "Auto";

        public double Brightness { get; set; } = 50;

        // Mod Settings
        public string CustomFontLocation { get; set; } = string.Empty;
        public CursorType CursorType { get; set; } = CursorType.Default;

        // Custom Integrations
        public ObservableCollection<CustomIntegration> CustomIntegrations { get; set; } = [];

        // Mod Preset Configuration
        public bool UseDisableAppPatch { get; set; } = false;

        // Roblox Deployment Settings
        public string Channel { get; set; } = RobloxInterfaces.Deployment.DefaultChannel;
        public string ChannelHash { get; set; } = "";
        public string PreferredMirror { get; set; } = "";
        public bool AllowPreReleaseUpdates { get; set; } = true;

        public string LaunchGameID { get; set; } = "";
        public bool IsGameEnabled { get; set; } = false;
        public bool MatchUniverseId { get; set; } = true;
        public long? TargetUniverseId { get; set; }
        public bool IsBetterServersEnabled { get; set; } = false;
        public bool OverClockGPU { get; set; } = false;
        public bool GRADmentFR { get; set; } = false;
        public bool VoidRPC { get; set; } = true;


        public ResolutionSetting? InGameResolution { get; set; }

        public string AppFontPath { get; set; } = "";
        public bool AutoTranslate { get; set; } = false;
        public string AutoTranslateLanguage { get; set; } = "";
        public bool CycleTitleWithGameName { get; set; } = true;
        public string DMMouseLeft { get; set; } = "A";
        public string DMMouseMiddle { get; set; } = "";
        public string DMMouseRight { get; set; } = "B";
        public double DMMouseSensitivity { get; set; } = 3.0;
        public string DMMouseX1 { get; set; } = "";
        public string DMMouseX2 { get; set; } = "";
        public string DMScrollDown { get; set; } = "DPadDown";
        public string DMScrollUp { get; set; } = "DPadUp";
        public string DoubleMovementControllerType { get; set; } = "Xbox360";
        public string DoubleMovementCurve { get; set; } = "Linear";
        public bool DoubleMovementEnabled { get; set; } = false;
        public double DoubleMovementForwardAngle { get; set; } = 55.0;
        public List<Voidstrap.Models.DMKeybind> DoubleMovementKeybinds { get; set; } = [];
        public double DoubleMovementSensitivity { get; set; } = 1.0;
        public int DoubleMovementSocdMode { get; set; } = 0;
        public double DoubleMovementStrafeAngle { get; set; } = 55.0;
        public bool FakeBorderlessFullscreen { get; set; } = false;
        public bool FakeExclusiveFullscreen { get; set; } = false;
        public Dictionary<long, string> PerGamePreferredDatacenters { get; set; } = [];
        public int ProxyConnectorType { get; set; } = 0;
        public string? ProxyHttpConnectHost { get; set; } = null;
        public int ProxyHttpConnectPort { get; set; } = 0;
        public string? ProxySocks5Host { get; set; } = null;
        public int ProxySocks5Port { get; set; } = 0;
        public BootstrapperIcon RobloxIcon { get; set; } = BootstrapperIcon.IconVoidstrap;
        public string RobloxIconCustomLocation { get; set; } = "";
        public string RobloxTitle { get; set; } = "Voidstrap";
        public int RobloxWindowBackdropType { get; set; } = 0;
        public bool RpcAutoTranslate { get; set; } = false;
        public string? RpcIdleIcon { get; set; } = "blue";
        public int ServerMatchmakerMaxRetries { get; set; } = 3;
        public bool ShowServerInfoInTitle { get; set; } = true;
        public bool SpoofOthersApplyIngame { get; set; } = false;
        public string SpoofOthersName { get; set; } = "";
        public bool SpoofOthersVerified { get; set; } = false;
        public bool SpoofSelfApplyIngame { get; set; } = false;
        public bool SpoofSelfGameCreator { get; set; } = false;
        public string SpoofSelfName { get; set; } = "";
        public bool SpoofSelfVerified { get; set; } = false;
        public string StudioRpcClientId { get; set; } = "";
        public bool StudioRpcShowPlace { get; set; } = true;
        public bool StudioRpcShowScript { get; set; } = true;
        public bool StudioRpcShowState { get; set; } = true;
        public double SidebarWidth { get; set; } = 225;
        public int UiZoomPercent { get; set; } = 100;
        public bool VoidstrapMatchmakerAutoCandidates { get; set; } = true;
        public List<string> VoidstrapMatchmakerDisabledDatacenters { get; set; } = [];
        public bool VoidstrapMatchmakerEnabled { get; set; } = false;
        public List<string> VoidstrapMatchmakerKickDatacenters { get; set; } = [];
        public int VoidstrapMatchmakerLearningGamesPlayed { get; set; } = 0;
        public int VoidstrapMatchmakerMaxCandidates { get; set; } = 14;
        public bool VoidstrapMatchmakerPreferEmpty { get; set; } = false;
        public string VoidstrapMatchmakerPreferredDatacenter { get; set; } = "";
        public bool VoidstrapMatchmakerRejoinDisabledDatacenters { get; set; } = false;
        public int VoidstrapMatchmakerGamejoinApiVersion { get; set; } = 1;
        public bool WebAccurateContinue { get; set; } = true;
        public string PlayerInstallLocation { get; set; } = "";
        public string StudioInstallLocation { get; set; } = "";
        public string ClassicInstallLocation { get; set; } = "";
        public string ClassicSourceLocation { get; set; } = "";
        public string ClassicDownloadBaseUrl { get; set; } = "";
        public string ClassicSelectedMap { get; set; } = "";
        public string LaunchSelectedClient { get; set; } = "";
        public bool ClassicCommunityContent { get; set; } = false;
        public bool WebAvatarBorder { get; set; } = true;
        public bool WebBannedUserViewer { get; set; } = true;
        public bool WebBulkLeaveGroups { get; set; } = true;
        public bool WebCategorizeWearing { get; set; } = true;
        public bool WebCurrentlyPlayingLink { get; set; } = true;
        public Voidstrap.Enums.ModApplyTarget ModApplyTarget { get; set; } = Voidstrap.Enums.ModApplyTarget.Both;

        public bool WebCustomBackgroundBlur { get; set; } = false;
        public bool WebCustomBackgroundEnabled { get; set; } = false;
        public int WebCustomBackgroundOpacity { get; set; } = 100;
        public string? WebCustomBackgroundPath { get; set; } = null;
        public ObservableCollection<Voidstrap.Models.CustomBackground> WebCustomBackgrounds { get; set; } = [];
        public bool WebViewDevTools { get; set; } = false;
        public bool WebDevProductsTab { get; set; } = true;
        public bool WebEventsTimeline { get; set; } = true;
        public bool WebFavoriteFriends { get; set; } = true;
        public bool WebFriendRequestActions { get; set; } = true;
        public bool WebGamepassViewer { get; set; } = true;
        public bool WebGroupGameViewer { get; set; } = true;
        public bool WebGroupMembersScanner { get; set; } = true;
        public bool WebHiddenBadges { get; set; } = true;
        public bool WebHiddenGamesViewer { get; set; } = true;
        public bool WebInstantJoiner { get; set; } = true;
        public bool WebItemSales { get; set; } = true;
        public bool WebItemTrading { get; set; } = true;
        public bool WebLastPlayedTogether { get; set; } = true;
        public bool WebMutualFriends { get; set; } = true;
        public bool WebOutfitViewer { get; set; } = true;
        public bool WebPendingRobux { get; set; } = true;
        public bool WebPriceFloor { get; set; } = true;
        public bool WebPrivateGames { get; set; } = true;
        public bool WebProfileRap { get; set; } = true;
        public bool WebProfileViews { get; set; } = true;
        public bool WebQuickPlay { get; set; } = true;
        public bool WebSearchSuggestions { get; set; } = true;
        public bool WebServerList { get; set; } = true;
        public bool WebSubplacesTab { get; set; } = true;
        public bool WebTradingEnhancer { get; set; } = true;
        public bool WebTransactionHistory { get; set; } = true;
        public bool WebUsernameColor { get; set; } = true;
        public bool WebViewDevMode { get; set; } = false;

        public List<LibraryPin> LibraryPins { get; set; } = [];

        public class LibraryPin
        {
            public long PlaceId { get; set; }
            public long UniverseId { get; set; }
            public string Name { get; set; } = "";
            public string CreatorName { get; set; } = "";
            public string Description { get; set; } = "";
            public string IconUrl { get; set; } = "";
            public string ThumbnailUrl { get; set; } = "";
            public long Visits { get; set; }
            public int MaxPlayers { get; set; }
            public string Genre { get; set; } = "";
            public DateTime? Created { get; set; }
            public DateTime? Updated { get; set; }
        }

        public class ResolutionSetting
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public int RefreshRate { get; set; }
            public string? Monitor { get; set; }
        }
    }
}
