using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.Integrations.GameChat
{
    public class GameChatOverlay : Window
    {
        private const string LogTag = "GameChatOverlay";

        private static readonly System.Windows.Media.FontFamily UiFont = new System.Windows.Media.FontFamily("Segoe UI");
        private static readonly Color ContainerColor = Color.FromArgb(166, 24, 24, 27);
        private static readonly Color InputColor = Color.FromArgb(191, 15, 15, 17);
        private static readonly Brush HelpCommandBrush = FreezeBrush(Color.FromRgb(86, 156, 255));
        private static readonly Brush HelpDescBrush = FreezeBrush(Color.FromRgb(176, 184, 196));
        private static readonly Brush TabActiveBrush = FreezeBrush(Color.FromRgb(245, 245, 250));
        private static readonly Brush TabIdleBrush = FreezeBrush(Color.FromRgb(128, 130, 140));

        private static Brush FreezeBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private readonly Canvas _rootCanvas;
        private readonly Border _mainContainer;
        private readonly RichTextBox _chatBox;
        private readonly Border _inputBorder;
        private readonly GameChatInputBox _inputBox;
        private readonly GameChatRoundButton _toggleBtn;
        private readonly GameChatResizeGrip _grip;

        private const int TabChatIndex = 0;
        private const int TabGlobalIndex = 1;
        private const int TabBridgeIndex = 2;

        private readonly Border _tabChat;
        private readonly Border _tabGlobal;
        private readonly Border _tabBridge;
        private readonly TextBlock _tabChatText;
        private readonly TextBlock _tabGlobalText;
        private readonly TextBlock _tabBridgeText;
        private readonly Border _tabChatUnderline;
        private readonly Border _tabGlobalUnderline;
        private readonly Border _tabBridgeUnderline;
        private int _selectedTab = TabChatIndex;
        private string _jobId = "global";

        private readonly GameChatBridgeClient _bridge = new GameChatBridgeClient();
        private readonly ColumnDefinition _tabBridgeColumn;
        private FlowDocument _docBridge;
        private bool _bridgeConsentShown;
        private bool _bridgeVerifyBusy;
        private readonly HashSet<long> _bridgeBadgeLookups = [];
        private bool _bridgeAvailable;

        private readonly GameChatClient _client;
        private readonly ActivityWatcher? _activityWatcher;
        private readonly System.Collections.Generic.HashSet<string> _mutedUsers = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private GameChatProfileWindow? _profileWindow;
        private bool _ctxMenuOpen;
        private ContextMenu? _openCtxMenu;
        private IntPtr _contextMenuHandle;
		private enum MessageMenuAction
		{
			CopyMessage,
			CopyUserId,
			CopyUsername,
			ViewProfile,
			ToggleMute
		}
		private sealed record MessageMenuContext(MessageMenuAction Action, long SenderId, string SenderName, string MessageText, bool WasMuted);
        private FlowDocument _docChat;
        private FlowDocument _docGlobal;
        private long _lastReportTicks;
        private const long ReportCooldownMs = 10000;
        private Border? _suggestPanel;
        private StackPanel? _suggestList;
        private bool _isDebugConsoleOpen;
        private StreamWriter? _debugWriter;
        private bool _bugBusy;
        private DateTime _lastBugSentUtc = DateTime.MinValue;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly IDisposable _trackerLease;
        private readonly ConcurrentQueue<Action> _pendingUi = new();
        private static readonly ConcurrentDictionary<long, Task<Utility.BorderRender?>> InlineBorderCache = new();
        private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> BadgeImageCache = new(StringComparer.Ordinal);
        private int _pendingUiCount;
        private int _flushScheduled;
        private readonly object _trackerSync = new();
        private RobloxWindowRect _pendingTrackerRect;
        private int _trackerDispatchPending;
        private bool _messageBatchActive;
        private volatile bool _closed;
        private bool _wasForeground;

        private readonly DispatcherTimer _autoSaveTimer;
        private readonly DispatcherTimer _leaveTimer;
        private readonly DispatcherTimer _healthTimer;
        private bool _settingsDirty;
		private long _lastSettingsChangeMs;
        private long _lastHeartbeatMs;

        private Point _defaultOffset = new Point(2, 9);
        private Point _currentOffset;
        private bool _isUserMovingWindow;
        private bool _inGame;

        private double _baseWidth;
        private double _baseHeight;
        private double _scale = 1.0;
		private double _dpiScale = 1.0;
        private readonly ScaleTransform _rootScale = new ScaleTransform(1.0, 1.0);

        private const double ContainerLeft = 7;
        private const double ContainerTop = 54;
        private const double ContainerRightInset = 21;
        private const double ContainerBottomInset = 49;
        private const double MinBaseWidth = 200;
        private const double MinBaseHeight = 150;
        private const double DefaultBaseWidth = 500;
        private const double DefaultBaseHeight = 400;
        private const double MaxBaseWidth = 720;
        private const double MaxBaseHeight = 560;
        private const int MaxPendingUiActions = 64;
        private const int MaxUiActionsPerFlush = 8;
        private const int MaxUiFlushMilliseconds = 6;

        private double ContainerWidth => Math.Max(100, _baseWidth - ContainerLeft - ContainerRightInset);
        private double ContainerHeight => Math.Max(80, _baseHeight - ContainerTop - ContainerBottomInset);

        public event EventHandler? ChatModeRequested;
        public event EventHandler? ChatModeExited;

        private const float ChatOnOpacity = 1.0f;
        private const float ChatOffOpacity = 0.7f;
        private float _targetOpacity = ChatOffOpacity;

        private bool _isChatting;
        private string _rawInputText = "";
        private IntPtr _windowHandle;
        private const int MaxInputLength = 1000;

        public bool IsWindowHidden { get; private set; }

        private volatile bool _overlayVisible;

        public bool IsOverlayVisible => _overlayVisible;

        public IntPtr WindowHandle => _windowHandle;

        public long LastHeartbeatMs => Volatile.Read(ref _lastHeartbeatMs);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("kernel32.dll")]
        private static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

        [DllImport("user32.dll")]
        private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_NOOWNERZORDER = 0x0200;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SC_CLOSE = 0xF060;
        private const uint MF_BYCOMMAND = 0;

        public GameChatOverlay(ActivityWatcher? activityWatcher)
        {
            _activityWatcher = activityWatcher;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
			UseLayoutRounding = true;
			SnapsToDevicePixels = true;

            var prop = App.Settings.Prop;
            _baseWidth = Math.Clamp(prop.GameChatWindowWidth, (int)MinBaseWidth, (int)MaxBaseWidth);
            _baseHeight = Math.Clamp(prop.GameChatWindowHeight, (int)MinBaseHeight, (int)MaxBaseHeight);
            Width = _baseWidth;
            Height = _baseHeight;
            _currentOffset = Math.Abs(prop.GameChatOffsetX) > 10000 || Math.Abs(prop.GameChatOffsetY) > 10000
                ? _defaultOffset
                : new Point(prop.GameChatOffsetX, prop.GameChatOffsetY);

			_rootCanvas = new Canvas { RenderTransform = _rootScale, UseLayoutRounding = true, SnapsToDevicePixels = true };
            Content = _rootCanvas;

            _toggleBtn = new GameChatRoundButton(LoadRobloxIcon("ui/TopBar/chatOn.png"), LoadRobloxIcon("ui/TopBar/chatOff.png"));
            Canvas.SetLeft(_toggleBtn, 115);
            Canvas.SetTop(_toggleBtn, 2);
            _toggleBtn.Clicked += OnToggleClicked;
            _toggleBtn.TripleTapped += OnToggleTripleTapped;
            _toggleBtn.Dragged += OnToggleDragged;
            _toggleBtn.DragEnded += OnToggleDragEnded;
            _rootCanvas.Children.Add(_toggleBtn);

            _chatBox = new RichTextBox
            {
                IsReadOnly = true,
                IsUndoEnabled = false,
                IsInactiveSelectionHighlightEnabled = false,
                SelectionBrush = Brushes.Transparent,
                SelectionOpacity = 0,
                CaretBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                FontFamily = UiFont,
                FontWeight = FontWeights.Medium,
                FontSize = 13.333,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				Focusable = false,
				IsReadOnlyCaretVisible = false,
                IsTabStop = false,
                IsHitTestVisible = false,
                Cursor = Cursors.Arrow,
				IsManipulationEnabled = false,
				UseLayoutRounding = true,
				SnapsToDevicePixels = true,
            };
			TextOptions.SetTextFormattingMode(_chatBox, TextFormattingMode.Display);
			_chatBox.SetValue(ScrollViewer.PanningModeProperty, PanningMode.None);
            SpellCheck.SetIsEnabled(_chatBox, false);
            _chatBox.ContextMenu = null;
            _chatBox.ContextMenuOpening += OnSuppressContextMenu;
            _chatBox.SelectionChanged += OnChatSelectionChanged;
			_chatBox.PreviewMouseLeftButtonDown += OnChatPreviewMouseLeftButtonDown;
			_chatBox.PreviewMouseMove += OnChatPreviewMouseMove;
            _docChat = NewDoc();
            _docGlobal = NewDoc();
            _docBridge = NewDoc();
            _chatBox.Document = _docChat;

            _inputBox = new GameChatInputBox();
            _inputBox.Clicked += OnInputBoxClicked;
            _inputBox.SendRequested += OnInputSendRequested;
            _inputBorder = new Border
            {
                Height = 45,
				Background = FreezeBrush(InputColor),
                CornerRadius = new CornerRadius(8),
                Child = _inputBox,
            };
            DockPanel.SetDock(_inputBorder, Dock.Bottom);

            _suggestList = new StackPanel();
            _suggestPanel = new Border
            {
				Background = FreezeBrush(Color.FromRgb(30, 30, 34)),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(4),
                Visibility = Visibility.Collapsed,
                Child = _suggestList,
            };
            DockPanel.SetDock(_suggestPanel, Dock.Bottom);

            _tabChatText = BuildTabText("Chat", true);
            _tabChatUnderline = BuildTabUnderline(true);
            _tabChat = BuildTab(_tabChatText, _tabChatUnderline);
            _tabChat.MouseLeftButtonUp += OnTabChatClicked;

            _tabGlobalText = BuildTabText("Global", false);
            _tabGlobalUnderline = BuildTabUnderline(false);
            _tabGlobal = BuildTab(_tabGlobalText, _tabGlobalUnderline);
            _tabGlobal.MouseLeftButtonUp += OnTabGlobalClicked;

            _tabBridgeText = BuildTabText("Bootstrappers", false);
            _tabBridgeUnderline = BuildTabUnderline(false);
            _tabBridge = BuildTab(_tabBridgeText, _tabBridgeUnderline);
            _tabBridge.Visibility = Visibility.Collapsed;
            _tabBridge.MouseLeftButtonUp += OnTabBridgeClicked;
            _tabBridgeColumn = new ColumnDefinition { Width = new GridLength(0) };

            var tabRow = new Grid { Height = 40 };
            tabRow.ColumnDefinitions.Add(new ColumnDefinition());
            tabRow.ColumnDefinitions.Add(new ColumnDefinition());
            tabRow.ColumnDefinitions.Add(_tabBridgeColumn);
            Grid.SetColumn(_tabChat, 0);
            Grid.SetColumn(_tabGlobal, 1);
            Grid.SetColumn(_tabBridge, 2);
			var tabDivider = new Border { Height = 1, Background = FreezeBrush(Color.FromRgb(42, 42, 48)), VerticalAlignment = VerticalAlignment.Bottom };
            Grid.SetColumnSpan(tabDivider, 3);
            tabRow.Children.Add(_tabChat);
            tabRow.Children.Add(_tabGlobal);
            tabRow.Children.Add(_tabBridge);
            tabRow.Children.Add(tabDivider);
            DockPanel.SetDock(tabRow, Dock.Top);

            var dock = new DockPanel
            {
                Margin = new Thickness(10, 8, 30, 10),
                LastChildFill = true,
            };
            dock.Children.Add(_inputBorder);
            dock.Children.Add(_suggestPanel);
            dock.Children.Add(tabRow);
            dock.Children.Add(_chatBox);

            _grip = new GameChatResizeGrip
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            _grip.ResizeDragged += OnGripResizeDragged;

            var containerGrid = new Grid { ClipToBounds = true };
            containerGrid.Children.Add(dock);
            containerGrid.Children.Add(_grip);

            _mainContainer = new Border
            {
				Background = FreezeBrush(ContainerColor),
                CornerRadius = new CornerRadius(10),
                Width = ContainerWidth,
                Height = ContainerHeight,
                Child = containerGrid,
            };
            Canvas.SetLeft(_mainContainer, ContainerLeft);
            Canvas.SetTop(_mainContainer, ContainerTop);
            _mainContainer.AddHandler(MouseDownEvent, new MouseButtonEventHandler(OnChatSurfaceMouseDown), true);
            _rootCanvas.Children.Add(_mainContainer);

            Opacity = ChatOffOpacity;

            _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5000) };
            _autoSaveTimer.Tick += OnAutoSaveTick;

            _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _leaveTimer.Tick += OnLeaveTimerTick;

            _healthTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _healthTimer.Tick += OnHealthTimerTick;
            Volatile.Write(ref _lastHeartbeatMs, Environment.TickCount64);
            _healthTimer.Start();

            _client = new GameChatClient();
            _client.OnSystemMessage += OnClientSystemMessage;
            _client.OnMessage += OnClientMessage;
            _client.OnRejected += OnClientRejected;
            _client.OnBadgesUpdated += OnClientBadgesUpdated;
            _bridge.OnSystem += OnBridgeSystem;
            _bridge.OnMessage += OnBridgeMessage;
            _bridge.OnVerificationRequired += OnBridgeVerificationRequired;
            _ = InitOwnRobloxIdAsync();
            _ = RefreshBridgeAvailabilityAsync();

            SourceInitialized += OnSourceInitializedHandler;
            IsVisibleChanged += OnOverlayIsVisibleChanged;
            RobloxWindowTracker.Changed += OnTrackerChanged;
            _trackerLease = RobloxWindowTracker.Acquire();

            AppendText(GameChatStrings.StartupText);
        }

        public void EnterGame(string jobId)
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(() => EnterGame(jobId));
                return;
            }
            _leaveTimer.Stop();
            _inGame = true;
            string newJob = string.IsNullOrEmpty(jobId) ? "global" : jobId;
            if (_jobId != "global" && newJob != _jobId)
            {
                _docChat = NewDoc();
                if (_selectedTab == TabChatIndex)
                    _chatBox.Document = _docChat;
                _docBridge = NewDoc();
                if (_selectedTab == TabBridgeIndex)
                    _chatBox.Document = _docBridge;
                _bridge.Stop();
            }
            _jobId = newJob;
            ApplyTrackerRect(RobloxWindowTracker.Current);
            _ = GameChatRoblox.GetBlockedIdsAsync();
            _ = SwitchChannelAsync(_selectedTab == TabGlobalIndex ? "global" : _jobId);
            if (_selectedTab == TabBridgeIndex)
                StartBridge();
        }

        public void LeaveGame()
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(LeaveGame);
                return;
            }
            if (_activityWatcher?.IsTeleporting == true)
            {
                _leaveTimer.Stop();
                _leaveTimer.Start();
                return;
            }
            HideOverlay();
        }

        private void OnLeaveTimerTick(object? sender, EventArgs e)
        {
            HideOverlay();
        }

        private void OnHealthTimerTick(object? sender, EventArgs e)
        {
            Volatile.Write(ref _lastHeartbeatMs, Environment.TickCount64);
        }

        private void HideOverlay()
        {
            _leaveTimer.Stop();
            if (_activityWatcher?.InGame == true)
                return;
            _inGame = false;
            _client.Stop();
            _bridge.Stop();
            if (_isChatting)
                CancelChatMode();
            if (IsVisible)
                Hide();
        }

        public async Task SwitchChannelAsync(string channelId)
        {
            if (string.IsNullOrEmpty(channelId))
                return;
            if (_client.ChannelId == channelId && _client.Connected)
                return;
            _client.ChannelId = channelId;
            await _client.RestartAsync(false);
        }

        private void OnSourceInitializedHandler(object? sender, EventArgs e)
        {
            _windowHandle = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(_windowHandle, GWL_EXSTYLE);
            SetWindowLong(_windowHandle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            OverlayDiagnostics.RegisterOverlayHandle(_windowHandle);
            ApplyTrackerRect(RobloxWindowTracker.Current);
        }

        private void OnOverlayIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _overlayVisible = IsVisible;
        }

        private static ImageSource? LoadRobloxIcon(string relativePath)
        {
            try
            {
                string[] roots =
                {
                    Paths.Versions,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
                };
                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                        continue;
                    foreach (string dir in Directory.GetDirectories(root))
                    {
                        string full = Path.Combine(dir, "content", "textures", relativePath.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(full))
                            continue;
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(full);
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogTag, "Icon load failed: " + ex.Message);
            }
            return null;
        }

        private void OnTrackerChanged(object? sender, RobloxWindowRect rect)
        {
            if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            if (Dispatcher.CheckAccess())
            {
                ApplyTrackerRect(rect);
                return;
            }
            lock (_trackerSync)
                _pendingTrackerRect = rect;
            if (Interlocked.Exchange(ref _trackerDispatchPending, 1) != 0)
                return;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushTrackerRect));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _trackerDispatchPending, 0);
            }
        }

        private void FlushTrackerRect()
        {
            Interlocked.Exchange(ref _trackerDispatchPending, 0);
            RobloxWindowRect rect;
            lock (_trackerSync)
                rect = _pendingTrackerRect;
            ApplyTrackerRect(rect);
        }

        private void ApplyTrackerRect(RobloxWindowRect rect)
        {
            if (!rect.Valid || !_inGame || (!rect.Foreground && !_ctxMenuOpen))
            {
                _wasForeground = false;
                if (_isChatting)
                    CancelChatMode();
                if (IsVisible)
                    Hide();
                return;
            }

            if (!_wasForeground && _windowHandle != IntPtr.Zero)
                SetWindowPos(_windowHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            _wasForeground = true;

            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
			_dpiScale = dpi > 0 ? dpi : 1.0;
            UpdateScale(rect, dpi);
            ClampGeometry(rect, dpi);

            if (!_isUserMovingWindow)
            {
                Left = rect.Left / dpi + _currentOffset.X;
                Top = rect.Top / dpi + _currentOffset.Y;
            }

            if (!IsVisible)
                Show();

			RaiseContextMenu();
        }

        private void ClampGeometry(RobloxWindowRect rect, double dpi)
        {
            double availableWidth = Math.Max(MinBaseWidth, rect.Width / dpi);
            double availableHeight = Math.Max(MinBaseHeight, rect.Height / dpi);
            _baseWidth = Math.Clamp(_baseWidth, MinBaseWidth, Math.Min(MaxBaseWidth, availableWidth));
            _baseHeight = Math.Clamp(_baseHeight, MinBaseHeight, Math.Min(MaxBaseHeight, availableHeight));
            ApplyWindowSize();
            double maxX = Math.Max(0, availableWidth - Width);
            double maxY = Math.Max(0, availableHeight - Height);
            _currentOffset = new Point(Math.Clamp(_currentOffset.X, 0, maxX), Math.Clamp(_currentOffset.Y, 0, maxY));
        }

        private void UpdateScale(RobloxWindowRect rect, double dpi)
        {
            double monitorWidthPx = SystemParameters.PrimaryScreenWidth * dpi;
            if (monitorWidthPx <= 1 || rect.Width <= 1)
                return;
            ApplyScale(rect.Width / monitorWidthPx);
        }

        private void ApplyScale(double target)
        {
            if (double.IsNaN(target) || double.IsInfinity(target))
                return;
            target = Math.Clamp(target, 0.5, 1.0);
            if (Math.Abs(target - _scale) < 0.01)
                return;
            _scale = target;
            _rootScale.ScaleX = _scale;
            _rootScale.ScaleY = _scale;
            ApplyWindowSize();
        }

        private void SetTargetOpacity(float target)
        {
            if (Math.Abs(_targetOpacity - target) < 0.001f && Math.Abs(Opacity - target) < 0.01)
                return;
            _targetOpacity = target;
			Opacity = _targetOpacity;
        }

        private void OnAutoSaveTick(object? sender, EventArgs e)
        {
            _autoSaveTimer.Stop();
            if (!_settingsDirty)
                return;
			long remaining = 5000 - (Environment.TickCount64 - _lastSettingsChangeMs);
			if (remaining > 0)
			{
				_autoSaveTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, remaining));
				_autoSaveTimer.Start();
				return;
			}
            SaveSettingsToDisk(false);
            _settingsDirty = false;
			_autoSaveTimer.Interval = TimeSpan.FromMilliseconds(5000);
        }

        private void MarkSettingsDirty()
        {
            _settingsDirty = true;
			_lastSettingsChangeMs = Environment.TickCount64;
			if (!_autoSaveTimer.IsEnabled)
			{
				_autoSaveTimer.Interval = TimeSpan.FromMilliseconds(5000);
				_autoSaveTimer.Start();
			}
        }

        public Task RefreshAccountAsync()
        {
            return _client.RestartAsync(false);
        }

        private void SaveSettingsToDisk(bool immediate)
        {
            var prop = App.Settings.Prop;
            prop.GameChatOffsetX = (int)_currentOffset.X;
            prop.GameChatOffsetY = (int)_currentOffset.Y;
            prop.GameChatWindowWidth = (int)_baseWidth;
            prop.GameChatWindowHeight = (int)_baseHeight;
            if (immediate)
                App.Settings.Save();
            else
                App.Settings.SaveDeferred();
        }

        private void ApplyWindowSize()
        {
            _mainContainer.Width = ContainerWidth;
            _mainContainer.Height = ContainerHeight;
            Width = (IsWindowHidden ? 170 : _baseWidth) * _scale;
            Height = (IsWindowHidden ? 54 : _baseHeight) * _scale;
        }

        private async Task InitOwnRobloxIdAsync()
        {
            try
            {
                long saved = App.Settings.Prop.GameChatRobloxUserId;
                if (saved > 0)
                {
                    _client.OwnRobloxId = saved;
                    return;
                }
                var account = await RobloxCookie.GetAccountAsync();
                if (account != null && account.UserId > 0)
                    _client.OwnRobloxId = account.UserId;
            }
            catch
            {
            }
        }

        public void OnGlobalMouseDown(int screenX, int screenY)
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(() => OnGlobalMouseDown(screenX, screenY));
                return;
            }
            if (!_isChatting || _isUserMovingWindow)
                return;
            if (IsScreenPointInOpenMenu(screenX, screenY))
                return;
            CloseOpenContextMenu();
            try
            {
                Point tl = _mainContainer.PointToScreen(new Point(0, 0));
                Point br = _mainContainer.PointToScreen(new Point(_mainContainer.ActualWidth, _mainContainer.ActualHeight));
                if (screenX >= tl.X && screenX <= br.X && screenY >= tl.Y && screenY <= br.Y)
                    return;
            }
            catch
            {
            }
            CancelChatMode();
        }

        private void OnToggleClicked(object? sender, EventArgs e)
        {
            ToggleVisibility();
        }

        private void OnToggleTripleTapped(object? sender, EventArgs e)
        {
            ResetToDefaults();
            AppendSystemMessage(GameChatStrings.ResetToDefault);
        }

        private void ResetToDefaults()
        {
            _baseWidth = DefaultBaseWidth;
            _baseHeight = DefaultBaseHeight;
            ApplyWindowSize();
            _currentOffset = _defaultOffset;

            var rect = RobloxWindowTracker.Current;
            if (rect.Valid)
            {
				Left = rect.Left / _dpiScale + _currentOffset.X;
				Top = rect.Top / _dpiScale + _currentOffset.Y;
            }
            MarkSettingsDirty();
        }

        public void ToggleVisibility()
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(ToggleVisibility);
                return;
            }
            IsWindowHidden = !IsWindowHidden;
			if (IsWindowHidden && _isChatting)
				CancelChatMode();
            _mainContainer.Visibility = IsWindowHidden ? Visibility.Collapsed : Visibility.Visible;
            _toggleBtn.IsActive = !IsWindowHidden;
            _toggleBtn.InvalidateVisual();
            _client.SlowPoll = IsWindowHidden;
            ApplyWindowSize();
        }

        private void OnToggleDragged(object? sender, Vector deltaPx)
        {
            _isUserMovingWindow = true;
			double dx = deltaPx.X / _dpiScale;
			double dy = deltaPx.Y / _dpiScale;
            _currentOffset = new Point(_currentOffset.X + dx, _currentOffset.Y + dy);
            Left += dx;
            Top += dy;
        }

        private void OnToggleDragEnded(object? sender, EventArgs e)
        {
            if (!_isUserMovingWindow)
                return;
            _isUserMovingWindow = false;

            var rect = RobloxWindowTracker.Current;
            if (rect.Valid)
            {
				double relativeX = Left - rect.Left / _dpiScale;
				double relativeY = Top - rect.Top / _dpiScale;

                const double snapDistance = 20;
                if (Math.Abs(relativeX - _defaultOffset.X) < snapDistance &&
                    Math.Abs(relativeY - _defaultOffset.Y) < snapDistance)
                {
                    _currentOffset = _defaultOffset;
                }
                else
                {
                    _currentOffset = new Point(relativeX, relativeY);
                }

				ClampGeometry(rect, _dpiScale);
				Left = rect.Left / _dpiScale + _currentOffset.X;
				Top = rect.Top / _dpiScale + _currentOffset.Y;
            }
            MarkSettingsDirty();
        }

        private void OnGripResizeDragged(object? sender, Vector deltaPx)
        {
			double dx = deltaPx.X / _dpiScale / _scale;
			double dy = deltaPx.Y / _dpiScale / _scale;
            _baseWidth = Math.Clamp(_baseWidth + dx, MinBaseWidth, MaxBaseWidth);
            _baseHeight = Math.Clamp(_baseHeight + dy, MinBaseHeight, MaxBaseHeight);
            RobloxWindowRect rect = RobloxWindowTracker.Current;
            if (rect.Valid)
                ClampGeometry(rect, _dpiScale);
            else
                ApplyWindowSize();
            MarkSettingsDirty();
        }

        private void OnInputBoxClicked(object? sender, EventArgs e)
        {
            RequestChatMode();
        }

        private void OnChatSurfaceMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_isUserMovingWindow)
                return;
            RequestChatMode();
        }

        private void OnInputSendRequested(object? sender, EventArgs e)
        {
            _ = Send();
        }

        public void RequestChatMode()
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(RequestChatMode);
                return;
            }
            if (IsWindowHidden || _isChatting)
                return;
            _inputBox.CaretIndex = _rawInputText.Length;
            StartChatMode();
        }

        public void StartChatMode()
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(StartChatMode);
                return;
            }
            _isChatting = true;
            _chatBox.IsHitTestVisible = true;
            SetTargetOpacity(ChatOnOpacity);
            SyncInput();
            ChatModeRequested?.Invoke(this, EventArgs.Empty);
        }

        public void CancelChatMode()
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(CancelChatMode);
                return;
            }
            _isChatting = false;
            _chatBox.IsHitTestVisible = false;
            CloseOpenContextMenu();
            SetTargetOpacity(ChatOffOpacity);
            SyncInput();
            ChatModeExited?.Invoke(this, EventArgs.Empty);
        }

        public void AppendTextFromKey(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(() => AppendTextFromKey(text));
                return;
            }
            int remaining = MaxInputLength - _rawInputText.Length;
            if (remaining <= 0)
                return;
            if (text.Length > remaining)
                text = text.Substring(0, remaining);
            int caret = Math.Min(_inputBox.CaretIndex, _rawInputText.Length);
            _rawInputText = _rawInputText.Insert(caret, text);
            _inputBox.CaretIndex = caret + text.Length;
            SyncInput();
        }

        public void PasteFromClipboard()
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(PasteFromClipboard);
                return;
            }
            try
            {
                AppendTextFromKey(Clipboard.GetText());
            }
            catch
            {
            }
        }

        public void Backspace()
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(Backspace);
                return;
            }
            if (_rawInputText.Length > 0 && _inputBox.CaretIndex > 0)
            {
                _rawInputText = _rawInputText.Remove(_inputBox.CaretIndex - 1, 1);
                _inputBox.CaretIndex--;
                SyncInput();
            }
        }

        public void HandleNavigation(System.Windows.Forms.Keys key)
        {
            if (!Dispatcher.CheckAccess())
            {
                DispatchUi(() => HandleNavigation(key));
                return;
            }
            if (key == System.Windows.Forms.Keys.Left)
                _inputBox.CaretIndex = Math.Max(0, _inputBox.CaretIndex - 1);
            else if (key == System.Windows.Forms.Keys.Right)
                _inputBox.CaretIndex = Math.Min(_rawInputText.Length, _inputBox.CaretIndex + 1);
            else if (key == System.Windows.Forms.Keys.Home)
                _inputBox.CaretIndex = 0;
            else if (key == System.Windows.Forms.Keys.End)
                _inputBox.CaretIndex = _rawInputText.Length;
            _inputBox.InvalidateVisual();
        }

        private void SyncInput()
        {
            _inputBox.RawText = _rawInputText;
            _inputBox.IsChatting = _isChatting;
            if (_inputBox.CaretIndex > _rawInputText.Length)
                _inputBox.CaretIndex = _rawInputText.Length;
            _inputBox.InvalidateVisual();
            UpdateSuggestions();
        }

        private void UpdateSuggestions()
        {
            if (_suggestPanel == null || _suggestList == null)
                return;

            string text = _rawInputText.TrimStart();
            bool show = _isChatting && text.StartsWith("/") && !text.Contains(' ');
            if (!show)
            {
                if (_suggestPanel.Visibility != Visibility.Collapsed)
                    _suggestPanel.Visibility = Visibility.Collapsed;
                return;
            }

            _suggestList.Children.Clear();
            int shown = 0;
            foreach (var (token, desc) in GameChatStrings.CommandTokens)
            {
                if (!token.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                    continue;
                _suggestList.Children.Add(BuildSuggestionRow(token, desc, token.Equals(text, StringComparison.OrdinalIgnoreCase)));
                if (++shown >= 7)
                    break;
            }

            _suggestPanel.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private Border BuildSuggestionRow(string token, string desc, bool exact)
        {
            var line = new TextBlock
            {
                FontFamily = UiFont,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            line.Inlines.Add(new Run(token) { Foreground = HelpCommandBrush, FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new Run("   " + desc) { Foreground = HelpDescBrush });

            var row = new Border
            {
                Background = exact ? new SolidColorBrush(Color.FromRgb(45, 45, 52)) : Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 3, 6, 3),
                Cursor = Cursors.Hand,
                Tag = token,
                Child = line,
            };
            row.MouseLeftButtonUp += OnSuggestionClicked;
            return row;
        }

        private void OnSuggestionClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string token)
            {
                _rawInputText = token + " ";
                _inputBox.CaretIndex = _rawInputText.Length;
                if (!_isChatting)
                    StartChatMode();
                else
                    SyncInput();
            }
            e.Handled = true;
        }

        private void ExitChatUI()
        {
            _rawInputText = "";
            _isChatting = false;
            CloseOpenContextMenu();
            SetTargetOpacity(ChatOffOpacity);
            SyncInput();
            ChatModeExited?.Invoke(this, EventArgs.Empty);
        }

        public async Task Send()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_rawInputText))
                {
                    ExitChatUI();
                    return;
                }

                string userMessage = _rawInputText;
                ExitChatUI();

                bool isCommand = await HandleCommands(userMessage);
                if (isCommand)
                    return;

                if (_selectedTab == TabBridgeIndex)
                {
                    if (!_bridgeAvailable)
                    {
                        AppendSystemMessage(GameChatStrings.BridgeUnavailable);
                        return;
                    }
                    if (!App.Settings.Prop.GameChatBridgeEnabled)
                    {
                        AppendBridgeSystem(GameChatStrings.BridgeConsent);
                        return;
                    }
                    await _bridge.SendMessageAsync(userMessage);
                    return;
                }

                await _client.SendMessageAsync(userMessage);
            }
            catch (Exception ex)
            {
                AppendSystemMessage(string.Format(GameChatStrings.FailedToSendMessage, ex.Message));
            }
        }

        private const int MaxChatBlocks = 100;

        private static FlowDocument NewDoc()
        {
            return new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left, Background = Brushes.Transparent };
        }

        private Paragraph NewParagraph()
        {
            return new Paragraph { Margin = new Thickness(0), TextAlignment = TextAlignment.Left };
        }

        private void AddBlock(FlowDocument document, Paragraph p)
        {
            var blocks = document.Blocks;
            blocks.Add(p);
            if (!_messageBatchActive)
            {
                TrimDocument(document);
                if (ReferenceEquals(_chatBox.Document, document))
                    _chatBox.ScrollToEnd();
            }
        }

        private void AddBlock(Paragraph paragraph) => AddBlock(_chatBox.Document, paragraph);

        private static void TrimDocument(FlowDocument document)
        {
            var blocks = document.Blocks;
            while (blocks.Count > MaxChatBlocks && blocks.FirstBlock != null)
                blocks.Remove(blocks.FirstBlock);
        }

        private void QueueUi(Action action)
        {
            if (_closed || _lifetimeCts.IsCancellationRequested)
                return;

            _pendingUi.Enqueue(action);
            int count = Interlocked.Increment(ref _pendingUiCount);
            while (count > MaxPendingUiActions && _pendingUi.TryDequeue(out _))
                count = Interlocked.Decrement(ref _pendingUiCount);

            ScheduleUiFlush();
        }

        private void DispatchUi(Action action)
        {
            if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            try
            {
                Dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ScheduleUiFlush()
        {
            if (_closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished || Interlocked.Exchange(ref _flushScheduled, 1) != 0)
                return;

            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(FlushPendingUi));
            }
            catch (InvalidOperationException)
            {
                Interlocked.Exchange(ref _flushScheduled, 0);
            }
        }

        private void FlushPendingUi()
        {
            if (_closed)
            {
                ClearPendingUi();
                return;
            }

            bool scrollToEnd = _chatBox.VerticalOffset >= _chatBox.ExtentHeight - _chatBox.ViewportHeight - 32;
            _messageBatchActive = true;
            try
            {
                int processed = 0;
                long started = Environment.TickCount64;
                while (processed < MaxUiActionsPerFlush && Environment.TickCount64 - started < MaxUiFlushMilliseconds && _pendingUi.TryDequeue(out Action? action))
                {
                    Interlocked.Decrement(ref _pendingUiCount);
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException("GameChatOverlay::Flush", ex);
                    }
                    processed++;
                }
            }
            finally
            {
                _messageBatchActive = false;
                TrimDocument(_docChat);
                TrimDocument(_docGlobal);
                TrimDocument(_docBridge);
                if (scrollToEnd)
                    _chatBox.ScrollToEnd();
                Interlocked.Exchange(ref _flushScheduled, 0);
            }

            if (!_pendingUi.IsEmpty)
                ScheduleUiFlush();
        }

        private void ClearPendingUi()
        {
            while (_pendingUi.TryDequeue(out _))
                Interlocked.Decrement(ref _pendingUiCount);
            Interlocked.Exchange(ref _pendingUiCount, 0);
            Interlocked.Exchange(ref _flushScheduled, 0);
        }

        public void AppendText(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueUi(() => AppendText(message));
                return;
            }
            bool wasBatching = _messageBatchActive;
            _messageBatchActive = true;
            try
            {
                foreach (string line in message.Split('\n'))
                {
                    var p = NewParagraph();
                    p.Inlines.Add(new Run(line.TrimEnd('\r')));
                    AddBlock(p);
                }
            }
            finally
            {
                _messageBatchActive = wasBatching;
                if (!wasBatching)
                {
                    TrimDocument(_chatBox.Document);
                    _chatBox.ScrollToEnd();
                }
            }
        }

        public void AppendChatMessage(string sender, long senderId, string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueUi(() => AppendChatMessage(sender, senderId, message));
                return;
            }
            AppendChatMessage(_chatBox.Document, sender, senderId, message);
        }

        private void AppendChatMessage(FlowDocument document, string sender, long senderId, string message, IReadOnlyList<GameChatBadge>? badges = null)
        {
            var p = NewParagraph();
            AddAvatarInline(p, sender, senderId);
            p.Inlines.Add(new Run(sender) { Foreground = GameChatNameColor.GetNameBrush(sender) });
            AddBadgeHost(p, senderId, badges);
            p.Inlines.Add(new Run(": "));
            p.Inlines.Add(new Run(message));
            AttachMessageProfile(p, senderId, sender, message);
            AddBlock(document, p);
        }

        public void AppendWhisperMessage(string sender, long senderId, string target, string text, bool isTo)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueUi(() => AppendWhisperMessage(sender, senderId, target, text, isTo));
                return;
            }
            AppendWhisperMessage(_chatBox.Document, sender, senderId, target, text, isTo);
        }

        private void AppendWhisperMessage(FlowDocument document, string sender, long senderId, string target, string text, bool isTo, IReadOnlyList<GameChatBadge>? badges = null)
        {
            string prefix = isTo
                ? string.Format(GameChatStrings.WhisperTo, target)
                : string.Format(GameChatStrings.WhisperFrom, sender);
            var p = NewParagraph();
            AddAvatarInline(p, sender, senderId);
            p.Inlines.Add(new Run("[" + prefix + "] "));
            p.Inlines.Add(new Run(sender) { Foreground = GameChatNameColor.GetNameBrush(sender) });
            AddBadgeHost(p, senderId, badges);
            p.Inlines.Add(new Run(": " + text));
            AttachMessageProfile(p, senderId, sender, text);
            AddBlock(document, p);
        }

        private void AttachMessageProfile(Paragraph p, long senderId, string sender, string text)
        {
            if (senderId <= 0)
                return;
            p.Tag = (senderId, sender, text);
            p.MouseRightButtonUp += OnMessageRightClick;
        }

        private void OnMessageRightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkContentElement fce || fce.Tag is not ValueTuple<long, string, string> ctx || ctx.Item1 <= 0)
            {
                e.Handled = true;
                return;
            }

            long senderId = ctx.Item1;
            string senderName = ctx.Item2;
            string messageText = ctx.Item3;

            var menu = new ContextMenu
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 27)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                FontFamily = UiFont,
                FontSize = 12,
            };

            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(Colors.White)));
            style.Setters.Add(new Setter(MenuItem.PaddingProperty, new Thickness(10, 6, 10, 6)));
            style.Setters.Add(new Setter(MenuItem.BackgroundProperty, Brushes.Transparent));

            var hoverTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(MenuItem.BackgroundProperty, new SolidColorBrush(Color.FromArgb(20, 255, 255, 255))));
            style.Triggers.Add(hoverTrigger);

            var copyMsg = CreateMessageMenuItem(GameChatStrings.CtxCopyMessage, style, MessageMenuAction.CopyMessage, senderId, senderName, messageText, false);
            menu.Items.Add(copyMsg);

            var copyId = CreateMessageMenuItem(GameChatStrings.CtxCopyUserId, style, MessageMenuAction.CopyUserId, senderId, senderName, messageText, false);
            menu.Items.Add(copyId);

            var copyName = CreateMessageMenuItem(GameChatStrings.CtxCopyUsername, style, MessageMenuAction.CopyUsername, senderId, senderName, messageText, false);
            menu.Items.Add(copyName);

            menu.Items.Add(new Separator());

            var viewProfile = CreateMessageMenuItem(GameChatStrings.CtxViewProfile, style, MessageMenuAction.ViewProfile, senderId, senderName, messageText, false);
            menu.Items.Add(viewProfile);

            bool isMuted = _mutedUsers.Contains(senderName);
            var muteItem = CreateMessageMenuItem(isMuted ? GameChatStrings.CtxUnmuteUser : GameChatStrings.CtxMuteUser, style, MessageMenuAction.ToggleMute, senderId, senderName, messageText, isMuted);
            menu.Items.Add(muteItem);

            menu.Opened += OnContextMenuOpened;
            menu.Closed += OnContextMenuClosed;
            CloseOpenContextMenu();
            _openCtxMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        }

		private MenuItem CreateMessageMenuItem(string header, Style style, MessageMenuAction action, long senderId, string senderName, string messageText, bool wasMuted)
		{
			var item = new MenuItem
			{
				Header = header,
				Style = style,
				Tag = new MessageMenuContext(action, senderId, senderName, messageText, wasMuted)
			};
			item.Click += OnMessageMenuItemClick;
			return item;
		}

		private void OnMessageMenuItemClick(object sender, RoutedEventArgs e)
		{
			if (sender is not MenuItem { Tag: MessageMenuContext context })
				return;
			switch (context.Action)
			{
				case MessageMenuAction.CopyMessage:
					Clipboard.SetText(context.MessageText);
					AppendSystemMessage(GameChatStrings.CopiedMessage);
					break;
				case MessageMenuAction.CopyUserId:
					Clipboard.SetText(context.SenderId.ToString());
					AppendSystemMessage(string.Format(GameChatStrings.CopiedUserId, context.SenderId));
					break;
				case MessageMenuAction.CopyUsername:
					Clipboard.SetText(context.SenderName);
					break;
				case MessageMenuAction.ViewProfile:
					OpenProfile(context.SenderId, BuildReporter(context.SenderName, context.SenderId, context.MessageText));
					break;
				case MessageMenuAction.ToggleMute:
					if (context.WasMuted)
					{
						_mutedUsers.Remove(context.SenderName);
						AppendSystemMessage(string.Format(GameChatStrings.UnmutedSpeaker, context.SenderName));
					}
					else
					{
						_mutedUsers.Add(context.SenderName);
						AppendSystemMessage(string.Format(GameChatStrings.MutedSpeaker, context.SenderName));
					}
					break;
			}
		}

        private void CloseOpenContextMenu()
        {
            var open = _openCtxMenu;
            if (open == null)
                return;
            _openCtxMenu = null;
            try
            {
                open.IsOpen = false;
            }
            catch
            {
            }
        }

        private bool IsScreenPointInOpenMenu(int screenX, int screenY)
        {
            var open = _openCtxMenu;
            if (open == null || !open.IsOpen)
                return false;
            try
            {
                Point tl = open.PointToScreen(new Point(0, 0));
                Point br = open.PointToScreen(new Point(open.ActualWidth, open.ActualHeight));
                return screenX >= tl.X && screenX <= br.X && screenY >= tl.Y && screenY <= br.Y;
            }
            catch
            {
                return false;
            }
        }

        private Func<Task<GameChatBugResult>> BuildReporter(string target, long targetId, string text)
        {
            return async () =>
            {
                long now = Environment.TickCount64;
                if (now - _lastReportTicks < ReportCooldownMs)
                    return GameChatBugResult.RateLimited;
                _lastReportTicks = now;
                var result = await _client.SendReportAsync(target, targetId, text);
                if (result != GameChatBugResult.Ok)
                    _lastReportTicks = 0;
                return result;
            };
        }

        private const double InlineAvatarSize = 24;
        private const double InlineAvatarContainer = 28;

        private sealed class BadgeHostTag
        {
            public long UserId;
        }

        private void AddBadgeHost(Paragraph paragraph, long userId, IReadOnlyList<GameChatBadge>? badges)
        {
            var host = new Span { Tag = new BadgeHostTag { UserId = userId } };
            paragraph.Inlines.Add(host);
            AddBadgeInlines(host.Inlines, badges);
        }

        private void AddBadgeInlines(InlineCollection inlines, IReadOnlyList<GameChatBadge>? badges)
        {
            if (badges is null)
                return;
            foreach (var badge in badges)
            {
                string? badgeSource = ResolveBadgeSource(badge.Image);
                if (badgeSource is null || string.IsNullOrWhiteSpace(badge.Name))
                    continue;
                var image = new System.Windows.Controls.Image
                {
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true,
					IsHitTestVisible = false,
                };
                var border = new Border
                {
                    Width = 18,
                    Height = 18,
                    CornerRadius = new CornerRadius(4),
                    Background = FreezeBrush(Color.FromArgb(34, 255, 255, 255)),
                    Margin = new Thickness(3, 0, 0, 0),
                    Child = image,
                    ToolTip = badge.Name,
                };
                inlines.Add(new InlineUIContainer(border) { BaselineAlignment = BaselineAlignment.Center });
                _ = LoadBadgeImageAsync(new WeakReference<System.Windows.Controls.Image>(image), badge.Id, badge.Name, badgeSource, _lifetimeCts.Token);
            }
        }

        private static string? ResolveBadgeSource(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (value.Length <= 256 * 1024 && (value.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:image/gif;base64,", StringComparison.OrdinalIgnoreCase) || value.StartsWith("data:image/webp;base64,", StringComparison.OrdinalIgnoreCase)))
                return value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                Uri.TryCreate(App.WebsiteBaseUrl.TrimEnd('/') + "/" + value.TrimStart('/'), UriKind.Absolute, out uri);
            if (uri is null || !Uri.TryCreate(App.WebsiteBaseUrl, UriKind.Absolute, out var site) || !string.Equals(uri.Scheme, site.Scheme, StringComparison.OrdinalIgnoreCase) || !string.Equals(uri.Host, site.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != site.Port)
                return null;
            return uri.AbsoluteUri;
        }

        private static async Task LoadBadgeImageAsync(WeakReference<System.Windows.Controls.Image> imageReference, string badgeId, string badgeName, string badgeSource, CancellationToken token)
        {
            string key = badgeSource.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? badgeId + ":" + badgeName + ":" + badgeSource.Length + ":" + badgeSource.GetHashCode(StringComparison.Ordinal)
                : badgeSource;
            try
            {
                var task = BadgeImageCache.GetOrAdd(key, _ => LoadBadgeImageCoreAsync(badgeSource));
                TrimBadgeImageCache();
                var imageSource = await task;
                if (!token.IsCancellationRequested && imageSource != null && imageReference.TryGetTarget(out var image))
                    image.Source = imageSource;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                BadgeImageCache.TryRemove(key, out _);
            }
        }

        private static Task<BitmapSource?> LoadBadgeImageCoreAsync(string badgeSource)
        {
            if (!badgeSource.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return Utility.AppImage.LoadAsync(badgeSource, 32, CancellationToken.None);
            return Task.Run(() =>
            {
                int comma = badgeSource.IndexOf(',');
                if (comma < 0)
                    return null;
                byte[] bytes = Convert.FromBase64String(badgeSource.Substring(comma + 1));
                return bytes.Length <= 2 * 1024 * 1024 ? Utility.SafeImaging.FromBytes(bytes, 32) : null;
            });
        }

        private static void TrimBadgeImageCache()
        {
            if (BadgeImageCache.Count <= 64)
                return;
            foreach (string key in BadgeImageCache.Keys)
            {
                if (BadgeImageCache.Count <= 64)
                    break;
                BadgeImageCache.TryRemove(key, out _);
            }
        }

        private void AddAvatarInline(Paragraph p, string sender, long senderId)
        {
            if (senderId <= 0)
                return;

            var avatar = new System.Windows.Shapes.Ellipse
            {
                Width = InlineAvatarSize,
                Height = InlineAvatarSize,
                Stroke = GameChatNameColor.GetNameBrush(sender),
                StrokeThickness = 2.5,
                Fill = new ImageBrush { Stretch = Stretch.UniformToFill },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Panel.SetZIndex(avatar, 0);
            var borderImage = new System.Windows.Controls.Image
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,
            };
            Panel.SetZIndex(borderImage, 10);
            var wrap = new Grid
            {
                Width = InlineAvatarContainer,
                Height = InlineAvatarContainer,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = senderId,
                ToolTip = string.Format(GameChatStrings.ViewProfileTooltip, sender),
                Margin = new Thickness(0, 0, 5, 0),
            };
            wrap.Children.Add(avatar);
            wrap.Children.Add(borderImage);
            wrap.MouseLeftButtonUp += OnAvatarClicked;
            p.Inlines.Add(new InlineUIContainer(wrap) { BaselineAlignment = BaselineAlignment.Center });
            _ = LoadAvatarWithBorderAsync(new WeakReference<System.Windows.Shapes.Ellipse>(avatar), new WeakReference<System.Windows.Controls.Image>(borderImage), senderId, _lifetimeCts.Token);
        }

        private static async Task LoadAvatarWithBorderAsync(WeakReference<System.Windows.Shapes.Ellipse> avatarReference, WeakReference<System.Windows.Controls.Image> borderReference, long senderId, CancellationToken token)
        {
            try
            {
                var image = await GameChatRoblox.GetChatHeadshotAsync(senderId, token);
                if (token.IsCancellationRequested || !avatarReference.TryGetTarget(out var avatar))
                    return;
                if (image != null && avatar.Fill is ImageBrush brush)
                    brush.ImageSource = image;
                avatar = null!;

                var border = await GameChatRoblox.GetBorderAsync(senderId);
                if (border == null || token.IsCancellationRequested)
                    return;

                if (!string.IsNullOrEmpty(border.AvatarBorderCss))
                {
                    var ring = Utility.GradientProfileBorder.ParseBorder(border.AvatarBorderCss);
                    if (ring != null && avatarReference.TryGetTarget(out var borderAvatar))
                        borderAvatar.Stroke = ring;
                }

                if (!string.IsNullOrEmpty(border.EquippedBorderJson))
                {
                    Task<Utility.BorderRender?> renderTask = InlineBorderCache.GetOrAdd(senderId, _ => Task.Run(() => BuildInlineBorder(border.EquippedBorderJson)));
                    TrimInlineBorderCache();
                    var render = await renderTask;
                    if (render?.Image != null && !token.IsCancellationRequested && borderReference.TryGetTarget(out var borderImage))
                    {
                        borderImage.Source = render.Image;
                        borderImage.Width = render.Width;
                        borderImage.Height = render.Height;
                        borderImage.Margin = render.Margin;
                        borderImage.Visibility = Visibility.Visible;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        private static void TrimInlineBorderCache()
        {
            if (InlineBorderCache.Count <= 96)
                return;
            foreach (long key in InlineBorderCache.Keys)
            {
                if (InlineBorderCache.Count <= 96)
                    break;
                InlineBorderCache.TryRemove(key, out _);
            }
        }

        private static Utility.BorderRender? BuildInlineBorder(string equippedBorderJson)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(equippedBorderJson);
                return Utility.WebsiteBorderRenderer.Build(doc.RootElement, InlineAvatarSize, InlineAvatarContainer);
            }
            catch
            {
                return null;
            }
        }

        private void OnAvatarClicked(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is long id && id > 0)
                OpenProfile(id);
            e.Handled = true;
        }

        private void OnSuppressContextMenu(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
        }

		private void OnChatPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (!IsInteractiveChatElement(e.OriginalSource as DependencyObject))
				e.Handled = true;
		}

		private void OnChatPreviewMouseMove(object sender, MouseEventArgs e)
		{
			if (e.LeftButton == MouseButtonState.Pressed && !IsInteractiveChatElement(e.OriginalSource as DependencyObject))
				e.Handled = true;
		}

		private bool IsInteractiveChatElement(DependencyObject? source)
		{
			DependencyObject? current = source;
			while (current != null && !ReferenceEquals(current, _chatBox))
			{
				if (current is FrameworkElement element && element.Tag is long)
					return true;
				current = current switch
				{
					FrameworkElement parentElement => parentElement.Parent,
					FrameworkContentElement content => content.Parent,
					_ => null,
				};
			}
			return false;
		}

		private void OnContextMenuOpened(object sender, RoutedEventArgs e)
		{
			_ctxMenuOpen = true;
			if (sender is Visual visual && PresentationSource.FromVisual(visual) is HwndSource source)
			{
				_contextMenuHandle = source.Handle;
				OverlayDiagnostics.RegisterOverlayHandle(_contextMenuHandle);
				RaiseContextMenu();
			}
		}

		private void RaiseContextMenu()
		{
			if (_contextMenuHandle != IntPtr.Zero)
				SetWindowPos(_contextMenuHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
		}

		private void OnContextMenuClosed(object sender, RoutedEventArgs e)
		{
			_ctxMenuOpen = false;
			OverlayDiagnostics.UnregisterOverlayHandle(_contextMenuHandle);
			_contextMenuHandle = IntPtr.Zero;
			if (sender is ContextMenu menu)
			{
				foreach (MenuItem item in menu.Items.OfType<MenuItem>())
					item.Click -= OnMessageMenuItemClick;
				menu.Opened -= OnContextMenuOpened;
				menu.Closed -= OnContextMenuClosed;
				if (ReferenceEquals(_openCtxMenu, menu))
					_openCtxMenu = null;
			}
			ApplyTrackerRect(RobloxWindowTracker.Current);
		}

        private void OnChatSelectionChanged(object sender, RoutedEventArgs e)
        {
            if (_chatBox.Selection.IsEmpty)
                return;
            TextPointer end = _chatBox.Selection.End;
            _chatBox.Selection.Select(end, end);
        }

        private Border BuildTab(TextBlock text, Border underline)
        {
            var cell = new Grid { Background = Brushes.Transparent };
            cell.Children.Add(text);
            cell.Children.Add(underline);
            return new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = cell };
        }

        private TextBlock BuildTabText(string label, bool active)
        {
            return new TextBlock
            {
                Text = label,
                FontFamily = UiFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = active ? TabActiveBrush : TabIdleBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        private Border BuildTabUnderline(bool active)
        {
            return new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(2, 2, 0, 0),
                Background = TabActiveBrush,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(26, 0, 26, 0),
                Visibility = active ? Visibility.Visible : Visibility.Collapsed,
            };
        }

        private void OnTabChatClicked(object sender, MouseButtonEventArgs e) => SelectTab(TabChatIndex);

        private void OnTabGlobalClicked(object sender, MouseButtonEventArgs e) => SelectTab(TabGlobalIndex);

        private void OnTabBridgeClicked(object sender, MouseButtonEventArgs e) => SelectTab(TabBridgeIndex);

        private void SelectTab(int tab)
        {
            bool sameTab = _selectedTab == tab;
            if (sameTab && tab != TabBridgeIndex && _client.Connected)
                return;

            _selectedTab = tab;

            _tabChatText.Foreground = tab == TabChatIndex ? TabActiveBrush : TabIdleBrush;
            _tabGlobalText.Foreground = tab == TabGlobalIndex ? TabActiveBrush : TabIdleBrush;
            _tabBridgeText.Foreground = tab == TabBridgeIndex ? TabActiveBrush : TabIdleBrush;
            _tabChatUnderline.Visibility = tab == TabChatIndex ? Visibility.Visible : Visibility.Collapsed;
            _tabGlobalUnderline.Visibility = tab == TabGlobalIndex ? Visibility.Visible : Visibility.Collapsed;
            _tabBridgeUnderline.Visibility = tab == TabBridgeIndex ? Visibility.Visible : Visibility.Collapsed;

            _chatBox.Document = tab switch
            {
                TabGlobalIndex => _docGlobal,
                TabBridgeIndex => _docBridge,
                _ => _docChat,
            };
            _chatBox.ScrollToEnd();

            if (tab == TabBridgeIndex)
            {
                _ = RefreshBridgeAvailabilityAsync();
                StartBridge();
                return;
            }

            if (!sameTab)
                _ = SwitchChannelAsync(tab == TabGlobalIndex ? "global" : _jobId);
        }

        private async Task RefreshBridgeAvailabilityAsync()
        {
            bool available;
            try
            {
                available = await GameChatBridgeConfig.IsEnabledAsync(_lifetimeCts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_closed || _bridgeAvailable == available)
                return;

            _bridgeAvailable = available;
            _tabBridge.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            _tabBridgeColumn.Width = available ? new GridLength(1.4, GridUnitType.Star) : new GridLength(0);

            if (available)
                return;

            _bridge.Stop();
            if (_selectedTab == TabBridgeIndex)
                SelectTab(TabChatIndex);
        }

        private void StartBridge()
        {
            if (_closed)
                return;

            if (!_bridgeAvailable)
            {
                _ = RefreshBridgeAvailabilityAsync();
                AppendSystemMessage(GameChatStrings.BridgeUnavailable);
                return;
            }

            if (!App.Settings.Prop.GameChatBridgeEnabled)
            {
                AppendSystemMessage(GameChatStrings.BridgeDisabled);
                return;
            }

            if (!_bridgeConsentShown)
            {
                _bridgeConsentShown = true;
                AppendBridgeSystem(GameChatStrings.BridgeConsent);
            }

            if (!GameChatBridgeClient.IsJoinableServer(_jobId))
            {
                AppendSystemMessage(GameChatStrings.BridgeNoServer);
                return;
            }

            _bridge.Start(_jobId);
        }

        private void OnBridgeSystem(object? sender, string message)
        {
            QueueUi(() => AppendBridgeSystem(message));
        }

        private void OnBridgeMessage(object? sender, GameChatBridgeMessage message)
        {
            QueueUi(() => ProcessBridgeMessage(message));
        }

        private void OnBridgeVerificationRequired(object? sender, EventArgs e)
        {
            QueueUi(() =>
            {
                if (_closed || _bridgeVerifyBusy || !_bridgeAvailable || !App.Settings.Prop.GameChatBridgeEnabled)
                    return;
                AppendBridgeSystem(GameChatStrings.BridgeNeedsVerify);
                _ = HandleBridgeVerifyAsync();
            });
        }

        private void ProcessBridgeMessage(GameChatBridgeMessage message)
        {
            if (message.Kind == "system")
            {
                AppendBridgeSystem(message.Text);
                return;
            }

            if (_mutedUsers.Contains(message.Sender))
                return;
            if (message.SenderId > 0 && GameChatRoblox.BlockedSnapshot.Contains(message.SenderId))
                return;

            GameChatLog.Add(message.Sender, "Bootstrappers", message.Text);
            AppendChatMessage(_docBridge, message.Sender, message.SenderId, message.Text);
            QueueBridgeBadgeLookup(message.SenderId);
        }

        private void QueueBridgeBadgeLookup(long userId)
        {
            if (_closed || userId <= 0 || !_bridgeBadgeLookups.Add(userId))
                return;

            _ = ResolveBridgeBadgesAsync(userId);
        }

        private async Task ResolveBridgeBadgesAsync(long userId)
        {
            try
            {
                GameChatIdentity? identity = await GameChatRoblox.GetChatIdentityAsync(userId, _lifetimeCts.Token).ConfigureAwait(false);
                if (_closed || identity == null || identity.Badges.Count == 0)
                    return;

                QueueUi(() => UpdateVisibleBadges(userId, identity.Badges));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogTag, "Bridge badge lookup failed: " + ex.Message);
            }
            finally
            {
                QueueUi(() => _bridgeBadgeLookups.Remove(userId));
            }
        }

        private void AppendBridgeSystem(string message)
        {
            if (string.IsNullOrEmpty(message))
                return;
            var p = NewParagraph();
            p.Inlines.Add(new Run(GameChatStrings.System + ": ") { Foreground = Brushes.Gray });
            p.Inlines.Add(new Run(message));
            AddBlock(_docBridge, p);
        }

        private async Task HandleBridgeCommandAsync(string args)
        {
            await RefreshBridgeAvailabilityAsync().ConfigureAwait(true);
            if (!_bridgeAvailable)
            {
                AppendSystemMessage(GameChatStrings.BridgeUnavailable);
                return;
            }

            string action = args.Trim().ToLowerInvariant();

            switch (action)
            {
                case "on":
                    if (!App.Settings.Prop.GameChatBridgeEnabled)
                    {
                        App.Settings.Prop.GameChatBridgeEnabled = true;
                        App.Settings.SaveDeferred();
                    }
                    AppendSystemMessage(GameChatStrings.BridgeEnabled);
                    if (!GameChatBridgeClient.IsJoinableServer(_jobId))
                    {
                        AppendSystemMessage(GameChatStrings.BridgeNoServer);
                        return;
                    }
                    if (GameChatBridgeAuth.GetToken() == null)
                    {
                        await HandleBridgeVerifyAsync().ConfigureAwait(true);
                        return;
                    }
                    _bridge.Start(_jobId);
                    return;

                case "off":
                    App.Settings.Prop.GameChatBridgeEnabled = false;
                    App.Settings.SaveDeferred();
                    _bridge.Stop();
                    AppendSystemMessage(GameChatStrings.BridgeTurnedOff);
                    return;

                case "verify":
                    await HandleBridgeVerifyAsync().ConfigureAwait(true);
                    return;

                case "reconnect":
                case "rc":
                    if (!App.Settings.Prop.GameChatBridgeEnabled)
                    {
                        AppendSystemMessage(GameChatStrings.BridgeDisabled);
                        return;
                    }
                    _bridge.Stop();
                    StartBridge();
                    return;

                case "status":
                    AppendSystemMessage(string.Format(
                        GameChatStrings.BridgeStatus,
                        !App.Settings.Prop.GameChatBridgeEnabled
                            ? GameChatStrings.BridgeStatusOff
                            : _bridge.Connected
                                ? string.Format(GameChatStrings.BridgeStatusConnected, _bridge.Name, _bridge.RoomId)
                                : GameChatStrings.BridgeStatusOn));
                    return;

                default:
                    AppendSystemMessage(GameChatStrings.UsageBridge);
                    return;
            }
        }

        private async Task HandleBridgeVerifyAsync()
        {
            if (_bridgeVerifyBusy)
                return;

            _bridgeVerifyBusy = true;
            try
            {
                GameChatBridgeChallenge? challenge = await GameChatBridgeVerify.StartAsync(_lifetimeCts.Token).ConfigureAwait(true);
                if (challenge == null)
                {
                    AppendSystemMessage(GameChatStrings.BridgeVerifyUnavailable);
                    return;
                }

                if (!GameChatBridgeVerify.IsSafeAuthUrl(challenge.AuthUrl))
                {
                    AppendSystemMessage(GameChatStrings.BridgeVerifyUnavailable);
                    return;
                }

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = challenge.AuthUrl,
                        UseShellExecute = true,
                    });
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LogTag, "Could not open the Roblox sign in page: " + ex.Message);
                    AppendSystemMessage(GameChatStrings.BridgeVerifyUnavailable);
                    return;
                }

                AppendSystemMessage(GameChatStrings.BridgeVerifyStarted);

                bool verified = await GameChatBridgeVerify.WaitAsync(challenge, _lifetimeCts.Token).ConfigureAwait(true);
                if (_closed)
                    return;

                if (!verified)
                {
                    AppendSystemMessage(GameChatStrings.BridgeVerifyFailed);
                    return;
                }

                AppendSystemMessage(GameChatStrings.BridgeVerifySuccess);
                StartBridge();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _bridgeVerifyBusy = false;
            }
        }

        private async Task<bool> HandleVotekickAsync(string args)
        {
            if (!_bridgeAvailable)
            {
                AppendSystemMessage(GameChatStrings.BridgeUnavailable);
                return true;
            }

            if (_selectedTab != TabBridgeIndex)
            {
                AppendSystemMessage(GameChatStrings.BridgeVotekickWrongTab);
                return true;
            }

            string[] parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                AppendSystemMessage(GameChatStrings.UsageVotekick);
                return true;
            }

            if (!_bridge.Connected)
            {
                AppendSystemMessage(GameChatStrings.BridgeNotConnected);
                return true;
            }

            bool voteOnly = string.Equals(_bridge.ActiveVotekickTarget, parts[0], StringComparison.OrdinalIgnoreCase);
            await _bridge.SendVotekickAsync(parts[0], parts.Length > 1 ? parts[1] : "", voteOnly).ConfigureAwait(true);
            return true;
        }

        private void OpenProfile(long userId) => OpenProfile(userId, null);

        private void OpenProfile(long userId, Func<Task<GameChatBugResult>>? reporter)
        {
            if (_profileWindow != null)
            {
                try { _profileWindow.Close(); } catch { }
                _profileWindow = null;
            }
            _profileWindow = new GameChatProfileWindow(userId, reporter);
            _profileWindow.Owner = this;
            _profileWindow.Closed += OnProfileWindowClosed;
			RobloxWindowRect rect = RobloxWindowTracker.Current;
			if (rect.Valid)
			{
				double dpi = _dpiScale > 0 ? _dpiScale : VisualTreeHelper.GetDpi(this).DpiScaleX;
				double robloxLeft = rect.Left / dpi;
				double robloxTop = rect.Top / dpi;
				double robloxWidth = rect.Width / dpi;
				double robloxHeight = rect.Height / dpi;
				_profileWindow.Left = robloxLeft + Math.Max(0, (robloxWidth - _profileWindow.Width) / 2);
				_profileWindow.Top = robloxTop + Math.Max(0, (robloxHeight - _profileWindow.Height) / 2);
			}
            _profileWindow.Show();
        }

        private void OnProfileWindowClosed(object? sender, EventArgs e)
        {
            if (_profileWindow != null)
                _profileWindow.Closed -= OnProfileWindowClosed;
            _profileWindow = null;
        }

        public void AppendSystemMessage(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueUi(() => AppendSystemMessage(message));
                return;
            }
            var p = NewParagraph();
            p.Inlines.Add(new Run(GameChatStrings.System + ": ") { Foreground = Brushes.Gray });
            p.Inlines.Add(new Run(message));
            AddBlock(p);
        }

        public void AppendHelp()
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueUi(AppendHelp);
                return;
            }
            bool wasBatching = _messageBatchActive;
            _messageBatchActive = true;
            var header = NewParagraph();
            header.Margin = new Thickness(0, 2, 0, 4);
            header.Inlines.Add(new Run(GameChatStrings.HelpHeader) { Foreground = HelpCommandBrush, FontWeight = FontWeights.Bold });
            AddBlock(header);
            foreach (var entry in GameChatStrings.HelpEntries)
            {
                var p = NewParagraph();
                p.Inlines.Add(new Run(entry.Command) { Foreground = HelpCommandBrush, FontWeight = FontWeights.SemiBold });
                p.Inlines.Add(new Run("   " + entry.Description) { Foreground = HelpDescBrush });
                AddBlock(p);
            }
            _messageBatchActive = wasBatching;
            if (!wasBatching)
            {
                TrimDocument(_chatBox.Document);
                _chatBox.ScrollToEnd();
            }
        }

        public void AppendBroadcastMessage(string sender, string message, string? hexColor)
        {
            if (!Dispatcher.CheckAccess())
            {
                QueueUi(() => AppendBroadcastMessage(sender, message, hexColor));
                return;
            }
            AppendBroadcastMessage(_chatBox.Document, sender, message, hexColor);
        }

        private void AppendBroadcastMessage(FlowDocument document, string sender, string message, string? hexColor)
        {
            Brush nameBrush = GameChatNameColor.GetNameBrush(sender);
            if (!string.IsNullOrEmpty(hexColor))
            {
                try
                {
                    var parsed = ColorConverter.ConvertFromString(hexColor);
                    if (parsed is Color c)
                    {
                        var custom = new SolidColorBrush(c);
                        custom.Freeze();
                        nameBrush = custom;
                    }
                }
                catch
                {
                }
            }
            var p = NewParagraph();
            p.Inlines.Add(new Run(sender + ": ") { Foreground = nameBrush, FontWeight = FontWeights.Bold });
            p.Inlines.Add(new Run(message));
            AddBlock(document, p);
        }

        private void OnClientSystemMessage(object? sender, string message)
        {
            QueueUi(() => AppendSystemMessage(message));
        }

        private void OnClientMessage(object? sender, GameChatMessage m)
        {
            if (m.SenderId > 0 && GameChatRoblox.BlockedSnapshot.Contains(m.SenderId))
                return;

            string text = m.Text;
            if (m.HasScores && GameChatFilter.ShouldHideMessageByFilter(m.Scores))
                text = GameChatStrings.MessageHiddenDueToFilterSettings;

            bool global = string.Equals(_client.ChannelId, "global", StringComparison.Ordinal);
            QueueUi(() => ProcessClientMessage(m, text, global));
        }

        private void OnClientBadgesUpdated(object? sender, GameChatBadgeUpdate update)
        {
            QueueUi(() => UpdateVisibleBadges(update.UserId, update.Badges));
        }

        private void UpdateVisibleBadges(long userId, IReadOnlyList<GameChatBadge> badges)
        {
            UpdateDocumentBadges(_docChat, userId, badges);
            UpdateDocumentBadges(_docGlobal, userId, badges);
            UpdateDocumentBadges(_docBridge, userId, badges);
        }

        private void UpdateDocumentBadges(FlowDocument document, long userId, IReadOnlyList<GameChatBadge> badges)
        {
            foreach (Paragraph paragraph in document.Blocks.OfType<Paragraph>())
            {
                foreach (Span span in paragraph.Inlines.OfType<Span>())
                {
                    if (span.Tag is not BadgeHostTag tag || tag.UserId != userId)
                        continue;
                    span.Inlines.Clear();
                    AddBadgeInlines(span.Inlines, badges);
                }
            }
        }

        private void ProcessClientMessage(GameChatMessage message, string text, bool global)
        {
            FlowDocument document = global ? _docGlobal : _docChat;
            if (message.Type == "message")
            {
                if (message.IsBroadcast)
                {
                    GameChatLog.Add(message.Sender, global ? "Global" : "Game", text);
                    AppendBroadcastMessage(document, message.Sender, text, message.Color);
                }
                else if (!_mutedUsers.Contains(message.Sender))
                {
                    GameChatLog.Add(message.Sender, global ? "Global" : "Game", text);
                    AppendChatMessage(document, message.Sender, message.SenderId, text, message.Badges);
                }
            }
            else if (message.Type == "whisper" && !_mutedUsers.Contains(message.Sender))
            {
                GameChatLog.Add(message.Sender, "Whisper", text);
                AppendWhisperMessage(document, message.Sender, message.SenderId, message.Target, text, message.IsTo, message.Badges);
            }
        }

        private void OnClientRejected(object? sender, GameChatRejection rejection)
        {
            string messageText = rejection.Reason switch
            {
                "moderation" => GameChatStrings.MessageRejectedModeration,
                "queue_full" => GameChatStrings.MessageRejectedQueueFull,
                "api_error" => GameChatStrings.MessageRejectedApiError,
                "not_found" => string.Format(GameChatStrings.UserNotFoundInChannel, string.IsNullOrEmpty(rejection.Target) ? "Unknown" : rejection.Target),
                _ => GameChatStrings.MessageRejectedUnknown,
            };
            QueueUi(() => AppendSystemMessage(messageText));
        }

        private async Task<bool> HandleCommands(string input)
        {
            if (string.IsNullOrWhiteSpace(input) || !input.StartsWith("/"))
                return false;

            string[] parts = input.Split(' ', 2);
            string command = parts[0].ToLower();
            string args = parts.Length > 1 ? parts[1] : "";

            switch (command)
            {
                case "/help":
                case "/?":
                    AppendHelp();
                    return true;

                case "/about":
                case "/credits":
                    AppendText(GameChatStrings.AboutText);
                    return true;

                case "/reconnect":
                case "/rc":
                    await _client.RestartAsync();
                    return true;

                case "/echo":
                    await _client.SendEchoAsync(args);
                    return true;

                case "/clear":
                case "/cls":
                case "/c":
                    _chatBox.Document.Blocks.Clear();
                    return true;

                case "/id":
                case "/channel":
                    if (_selectedTab == TabBridgeIndex)
                        AppendSystemMessage($"{GameChatStrings.CurrentChannelID}: {(_bridge.RoomId.Length > 0 ? _bridge.RoomId : _jobId)}");
                    else
                        AppendSystemMessage($"{GameChatStrings.CurrentChannelID}: {_client.ChannelId}");
                    return true;

                case "/bridge":
                    await HandleBridgeCommandAsync(args);
                    return true;

                case "/votekick":
                case "/vk":
                    return await HandleVotekickAsync(args);

                case "/bug":
                case "/issue":
                    await HandleBugReport(args);
                    return true;

                case "/mute":
                    return HandleMute(args);

                case "/filter":
                    return HandleFilterPreferenceCommand(args);

                case "/unmute":
                    return HandleUnmute(args);

                case "/whisper":
                case "/w":
                    return await HandleWhisperAsync(args);

                case "/console":
                case "/debug":
                    HandleDebugConsole();
                    return true;

                case "/update":
                    AppendSystemMessage(GameChatStrings.CheckingForUpdates);
                    await HandleUpdateCheck();
                    return true;

                case "/login":
                    await HandleLoginAsync();
                    return true;

                case "/logout":
                    Utility.WebsiteAuth.Clear();
                    AppendSystemMessage(GameChatStrings.LoggedOut);
                    await _client.RestartAsync();
                    return true;

                case "/verify":
                    await HandleVerifyAsync();
                    return true;

                case "/unverify":
                    await GameChatEmotes.UnverifyAsync();
                    AppendSystemMessage(GameChatStrings.Unverified);
                    await _client.RestartAsync();
                    return true;

                case "/emote":
                case "/e":
                    if (string.IsNullOrWhiteSpace(args))
                    {
                        AppendSystemMessage(GameChatStrings.UsageEmote);
                    }
                    else
                    {
                        long universeId = _activityWatcher?.Data?.UniverseId ?? 0;
                        if (universeId <= 0)
                        {
                            AppendSystemMessage(GameChatStrings.EmoteNoGame);
                        }
                        else
                        {
                            string emoteName = args.Trim();
                            string? emoteError = await GameChatEmotes.SendEmoteAsync(emoteName, universeId, _client.ChannelId);
                            AppendSystemMessage(emoteError ?? string.Format(GameChatStrings.EmoteQueued, emoteName));
                        }
                    }
                    return true;

                default:
                    AppendSystemMessage(string.Format(GameChatStrings.UnknownCommand, command));
                    return true;
            }
        }

        private bool _loginBusy;
        private bool _verifyBusy;

        private async Task HandleVerifyAsync()
        {
            if (_verifyBusy)
                return;
            _verifyBusy = true;
            try
            {
                AppendSystemMessage(GameChatStrings.VerifyChecking);
                var result = await GameChatEmotes.VerifyAccountAsync();
                if (result.Error != null)
                {
                    AppendSystemMessage(string.Format(GameChatStrings.VerifyFailed, result.Error));
                    return;
                }
                AppendSystemMessage(string.Format(GameChatStrings.VerifySuccess, string.IsNullOrEmpty(result.Username) ? "your account" : result.Username));
                await _client.RestartAsync();
            }
            finally
            {
                _verifyBusy = false;
            }
        }

        private async Task HandleLoginAsync()
        {
            if (!string.IsNullOrEmpty(Utility.WebsiteAuth.GetToken()))
            {
                AppendSystemMessage(GameChatStrings.AlreadyLoggedIn);
                await _client.RestartAsync();
                return;
            }

            if (_loginBusy)
                return;
            _loginBusy = true;
            try
            {
                AppendSystemMessage(GameChatStrings.AttemptingLogin);

                byte[] sessionBytes = new byte[32];
                System.Security.Cryptography.RandomNumberGenerator.Fill(sessionBytes);
                string sessionId = Convert.ToHexString(sessionBytes).ToLowerInvariant();

                string signInUrl = App.WebsiteBaseUrl + "/pages/app-signin.html#session=" + sessionId;
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = signInUrl,
                        UseShellExecute = true,
                    });
                }
                catch
                {
                    AppendSystemMessage(GameChatStrings.LoginBrowserFailed);
                    return;
                }
                AppendSystemMessage(GameChatStrings.LoginBrowserOpened);

                string pollUrl = App.WebsiteBaseUrl + "/api/app/auth/poll";
                using var timeoutCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));
                string? vsToken = null;
                while (!timeoutCts.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(2000, timeoutCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    try
                    {
                        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, pollUrl);
                        req.Headers.TryAddWithoutValidation("x-app-session", sessionId);
                        using var resp = await App.HttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                        if (!resp.IsSuccessStatusCode)
                            continue;
                        using var doc = System.Text.Json.JsonDocument.Parse(await Utility.Http.ReadStringBoundedAsync(resp.Content, 262144, timeoutCts.Token).ConfigureAwait(false));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("ready", out var readyEl) && readyEl.ValueKind == System.Text.Json.JsonValueKind.True
                            && root.TryGetProperty("vs_token", out var tokEl) && tokEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            vsToken = tokEl.GetString();
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch
                    {
                    }
                }

                if (string.IsNullOrWhiteSpace(vsToken))
                {
                    AppendSystemMessage(GameChatStrings.LoginTimedOut);
                    return;
                }

                Utility.WebsiteAuth.Save(vsToken.Trim());
                AppendSystemMessage(GameChatStrings.LoginSuccess);
                await _client.RestartAsync();
            }
            catch (Exception)
            {
                AppendSystemMessage(GameChatStrings.LoginFailed);
            }
            finally
            {
                _loginBusy = false;
            }
        }

        private async Task HandleBugReport(string args)
        {
            string text = args?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendSystemMessage(GameChatStrings.UsageBug);
                return;
            }
            if (text.Length < 6)
            {
                AppendSystemMessage(GameChatStrings.BugTooShort);
                return;
            }
            if (_bugBusy)
                return;
            if ((DateTime.UtcNow - _lastBugSentUtc).TotalSeconds < 60)
            {
                AppendSystemMessage(GameChatStrings.BugCooldown);
                return;
            }

            _bugBusy = true;
            try
            {
                AppendSystemMessage(GameChatStrings.BugSending);
                GameChatBugResult result = await _client.SendBugAsync(text);
                switch (result)
                {
                    case GameChatBugResult.Ok:
                        _lastBugSentUtc = DateTime.UtcNow;
                        AppendSystemMessage(GameChatStrings.BugSent);
                        break;
                    case GameChatBugResult.RateLimited:
                        AppendSystemMessage(GameChatStrings.BugCooldown);
                        break;
                    case GameChatBugResult.NotConnected:
                        AppendSystemMessage(GameChatStrings.NotConnected);
                        break;
                    default:
                        AppendSystemMessage(GameChatStrings.BugFailed);
                        break;
                }
            }
            finally
            {
                _bugBusy = false;
            }
        }

        private async Task HandleUpdateCheck()
        {
            try
            {
                var release = await App.GetLatestRelease();
                string tag = release?.TagName ?? "";
                string current = App.Version;
                if (!string.IsNullOrEmpty(tag) && tag.TrimStart('v') != current.TrimStart('v'))
                {
                    AppendSystemMessage(string.Format(GameChatStrings.UpdateAvailable, tag));
                    OpenUrl(release?.Assets?.FirstOrDefault(asset => string.Equals(asset.Name, "Voidstrap.exe", StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl ?? App.ProjectFallbackDownloadLink);
                }
                else
                {
                    AppendSystemMessage(string.Format(GameChatStrings.AlreadyUpToDate, current));
                }
            }
            catch (Exception ex)
            {
                AppendSystemMessage(string.Format(GameChatStrings.UpdateCheckFailed, ex.Message));
            }
        }

        private bool HandleFilterPreferenceCommand(string args)
        {
            string raw = args?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(raw))
            {
                AppendSystemMessage(string.Format(GameChatStrings.FilterPreferenceCurrent, GameChatFilter.GetCurrentFilterPreference()));
                return true;
            }
            string next = raw.ToLowerInvariant();
            if (!GameChatFilter.ValidFilterPreferences.Contains(next))
            {
                AppendSystemMessage(GameChatStrings.UsageFilter);
                return true;
            }
            GameChatFilter.SetPreference(next);
            AppendSystemMessage(string.Format(GameChatStrings.FilterPreferenceSet, next));
            return true;
        }

        private bool HandleMute(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                AppendSystemMessage(GameChatStrings.UsageMute);
            }
            else
            {
                string speaker = args.Trim().Trim('"');
                _mutedUsers.Add(speaker);
                AppendSystemMessage(string.Format(GameChatStrings.MutedSpeaker, speaker));
            }
            return true;
        }

        private bool HandleUnmute(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                AppendSystemMessage(GameChatStrings.UsageUnmute);
                return true;
            }
            string speaker = args.Trim().Trim('"');
            if (_mutedUsers.Remove(speaker))
                AppendSystemMessage(string.Format(GameChatStrings.UnmutedSpeaker, speaker));
            else
                AppendSystemMessage(string.Format(GameChatStrings.SpeakerNotMuted, speaker));
            return true;
        }

        private async Task<bool> HandleWhisperAsync(string args)
        {
            string target = "";
            string msg = "";

            if (args.StartsWith("\""))
            {
                int endQuoteIndex = args.IndexOf("\"", 1, StringComparison.Ordinal);
                if (endQuoteIndex != -1)
                {
                    target = args.Substring(1, endQuoteIndex - 1);
                    msg = args.Substring(endQuoteIndex + 1).Trim();
                }
            }
            else
            {
                string[] whisperParts = args.Split(' ', 2);
                if (whisperParts.Length == 2)
                {
                    target = whisperParts[0];
                    msg = whisperParts[1];
                }
            }

            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(msg))
            {
                AppendSystemMessage(GameChatStrings.UsageWhisper);
                return true;
            }

            await _client.SendWhisperAsync(target, msg);
            return true;
        }

        private void HandleDebugConsole()
        {
            if (!_isDebugConsoleOpen)
            {
                if (AllocConsole())
                {
                    _isDebugConsoleOpen = true;
                    _debugWriter = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    Console.SetOut(_debugWriter);
                    Console.SetError(_debugWriter);

                    IntPtr consoleWindow = GetConsoleWindow();
                    if (consoleWindow != IntPtr.Zero)
                    {
                        IntPtr sysMenu = GetSystemMenu(consoleWindow, false);
                        if (sysMenu != IntPtr.Zero)
                            DeleteMenu(sysMenu, SC_CLOSE, MF_BYCOMMAND);
                    }

                    Console.OutputEncoding = System.Text.Encoding.UTF8;
                    Console.Title = GameChatStrings.DebugConsoleTitle;
                    Console.WriteLine(string.Format(GameChatStrings.DebugConsoleInitialized, DateTime.Now));
                    Console.WriteLine(GameChatStrings.DebugConsoleUseClose);
                }
            }
            else
            {
                CloseDebugConsole();
            }
        }

        private void CloseDebugConsole()
        {
            if (!_isDebugConsoleOpen && _debugWriter == null)
                return;
            _isDebugConsoleOpen = false;
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            _debugWriter?.Dispose();
            _debugWriter = null;
            FreeConsole();
        }

        private void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                AppendSystemMessage(string.Format(GameChatStrings.CouldNotOpenLink, ex.Message));
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            _lifetimeCts.Cancel();
            Interlocked.Exchange(ref _trackerDispatchPending, 0);
            ClearPendingUi();
            CloseOpenContextMenu();
            OverlayDiagnostics.UnregisterOverlayHandle(_contextMenuHandle);
            _contextMenuHandle = IntPtr.Zero;
            CloseDebugConsole();
            RobloxWindowTracker.Changed -= OnTrackerChanged;
            _trackerLease.Dispose();
            SourceInitialized -= OnSourceInitializedHandler;
            IsVisibleChanged -= OnOverlayIsVisibleChanged;
            _overlayVisible = false;
            OverlayDiagnostics.UnregisterOverlayHandle(_windowHandle);
            _windowHandle = IntPtr.Zero;

            _autoSaveTimer.Stop();
            _autoSaveTimer.Tick -= OnAutoSaveTick;
            _leaveTimer.Stop();
            _leaveTimer.Tick -= OnLeaveTimerTick;
            _healthTimer.Stop();
            _healthTimer.Tick -= OnHealthTimerTick;

            SaveSettingsToDisk(false);

            _toggleBtn.Clicked -= OnToggleClicked;
            _toggleBtn.TripleTapped -= OnToggleTripleTapped;
            _toggleBtn.Dragged -= OnToggleDragged;
            _toggleBtn.DragEnded -= OnToggleDragEnded;
            _toggleBtn.Shutdown();
            _grip.ResizeDragged -= OnGripResizeDragged;
			_grip.Shutdown();
            _mainContainer.RemoveHandler(MouseDownEvent, new MouseButtonEventHandler(OnChatSurfaceMouseDown));
            _inputBox.Clicked -= OnInputBoxClicked;
            _inputBox.SendRequested -= OnInputSendRequested;
            _inputBox.Shutdown();
            _chatBox.ContextMenuOpening -= OnSuppressContextMenu;
            _chatBox.SelectionChanged -= OnChatSelectionChanged;
			_chatBox.PreviewMouseLeftButtonDown -= OnChatPreviewMouseLeftButtonDown;
			_chatBox.PreviewMouseMove -= OnChatPreviewMouseMove;
            _tabChat.MouseLeftButtonUp -= OnTabChatClicked;
            _tabGlobal.MouseLeftButtonUp -= OnTabGlobalClicked;
            _tabBridge.MouseLeftButtonUp -= OnTabBridgeClicked;

            if (_profileWindow != null)
            {
                _profileWindow.Closed -= OnProfileWindowClosed;
                try { _profileWindow.Close(); } catch { }
                _profileWindow = null;
            }

            _client.OnSystemMessage -= OnClientSystemMessage;
            _client.OnMessage -= OnClientMessage;
            _client.OnRejected -= OnClientRejected;
            _client.OnBadgesUpdated -= OnClientBadgesUpdated;
            _client.Dispose();
            _bridge.OnSystem -= OnBridgeSystem;
            _bridge.OnMessage -= OnBridgeMessage;
            _bridge.OnVerificationRequired -= OnBridgeVerificationRequired;
            _bridge.Dispose();
            _docChat.Blocks.Clear();
            _docGlobal.Blocks.Clear();
            _docBridge.Blocks.Clear();
            InlineBorderCache.Clear();
            BadgeImageCache.Clear();
            GameChatRoblox.InvalidateAccountState();
            _lifetimeCts.Dispose();

            base.OnClosed(e);
        }
    }
}
