using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Persistable;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.UI.Elements.ContextMenu;

namespace Voidstrap.UI.Elements.Overlay;

public enum OverlayStudioLayer
{
    Readout = 0,
    Notifications = 1,
    Dock = 2,
    Crosshair = 3,
    Panels = 4
}

internal sealed partial class SessionLiveOverlayCanvasWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x80;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static readonly string[] DockActionGlyphs =
    [
        "\uE8BD",
        "\uE753",
        "\uE7FC",
        "\uE81C",
        "\uE8D6",
        "\uE968",
        "\uE734",
        "\uE774",
        "\uE759",
        "\uE713"
    ];

    private readonly ImageSource? _gameIcon;
    private readonly Action<OverlayStudioLayer> _onLayerUpdated;
    private readonly Action _onResetAll;
    private readonly IDisposable _trackerLease;
    private readonly Canvas _stage;
    private readonly Line _guideVerticalCenter;
    private readonly Line _guideHorizontalCenter;
    private readonly Line _guideThirdLeft;
    private readonly Line _guideThirdRight;
    private readonly Line _guideThirdTop;
    private readonly Line _guideThirdBottom;
    private readonly Border _toolbar;
    private readonly TextBlock _coordBadge;
    private readonly Wpf.Ui.Controls.Button _snapButton;

    private readonly Border _readoutWrapper;
    private readonly Border _readoutBorder;
    private readonly StackPanel _readoutPanel;
    private readonly TextBlock _readoutLocationText;
    private readonly TextBlock _readoutSeparatorText;
    private readonly TextBlock _readoutTimeText;

    private readonly Border _notificationWrapper;
    private readonly Border _notificationHost;
    private readonly Grid _notificationShadow;
    private readonly Border _notificationCard;
    private readonly Image _notificationIcon;
    private readonly StackPanel _notificationHeader;
    private readonly TextBlock _notificationHeaderText;
    private readonly TextBlock _notificationTitle;
    private readonly TextBlock _notificationText;

    private readonly Border _dockWrapper;
    private readonly Grid _dockRoot;
    private readonly Image _dockProfileImage;
    private readonly TextBlock _dockProfileName;
    private readonly TextBlock _dockProfileHandle;
    private readonly Image _dockGameImage;
    private readonly TextBlock _dockGameTitle;
    private readonly Run _dockSessionClockRun;
    private readonly Run _dockServerClockRun;

    private readonly Border _crosshairWrapper;
    private readonly Image _crosshairImage;

    private OverlayStudioLayer _selectedLayer;
    private OverlayStudioLayer? _draggingLayer;
    private Point _dragStartPointer;
    private Point _dragStartElementOrigin;
    private bool _magneticSnap = true;
    private bool _closed;

    public SessionLiveOverlayCanvasWindow(
        OverlayStudioLayer initialLayer,
        ImageSource? gameIcon,
        Action<OverlayStudioLayer> onLayerUpdated,
        Action onResetAll)
    {
        _selectedLayer = initialLayer == OverlayStudioLayer.Panels ? OverlayStudioLayer.Readout : initialLayer;
        _gameIcon = gameIcon;
        _onLayerUpdated = onLayerUpdated;
        _onResetAll = onResetAll;

        Title = Strings.Ui_OverlayStudio_LiveScreenDragTitle;
        PortableOverlay.Prepare(this);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = !Voidstrap.Utility.Platform.IsLinux;
        Background = new SolidColorBrush(Color.FromArgb(Voidstrap.Utility.Platform.IsLinux ? (byte)210 : (byte)96, 18, 18, 18));
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        Grid root = new();
        _stage = new Canvas { ClipToBounds = true, Background = Brushes.Transparent };

        Rectangle outerBorder = new()
        {
            Stroke = new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        root.Children.Add(outerBorder);

        _guideThirdLeft = CreateGuideLine(false);
        _guideThirdRight = CreateGuideLine(false);
        _guideThirdTop = CreateGuideLine(false);
        _guideThirdBottom = CreateGuideLine(false);
        _guideVerticalCenter = CreateGuideLine(true);
        _guideHorizontalCenter = CreateGuideLine(true);

        _stage.Children.Add(_guideThirdLeft);
        _stage.Children.Add(_guideThirdRight);
        _stage.Children.Add(_guideThirdTop);
        _stage.Children.Add(_guideThirdBottom);
        _stage.Children.Add(_guideVerticalCenter);
        _stage.Children.Add(_guideHorizontalCenter);

        _readoutBorder = BuildExactReadoutElement(
            out _readoutPanel,
            out _readoutLocationText,
            out _readoutSeparatorText,
            out _readoutTimeText);
        _readoutWrapper = WrapExactElement(OverlayStudioLayer.Readout, _readoutBorder);

        _notificationHost = BuildExactNotificationElement(
            _gameIcon,
            out _notificationShadow,
            out _notificationCard,
            out _notificationIcon,
            out _notificationHeader,
            out _notificationHeaderText,
            out _notificationTitle,
            out _notificationText);
        _notificationWrapper = WrapExactElement(OverlayStudioLayer.Notifications, _notificationHost);

        _dockRoot = BuildExactDockElement(
            _gameIcon,
            out _dockProfileImage,
            out _dockProfileName,
            out _dockProfileHandle,
            out _dockGameImage,
            out _dockGameTitle,
            out _dockSessionClockRun,
            out _dockServerClockRun);
        _dockWrapper = WrapExactElement(OverlayStudioLayer.Dock, _dockRoot);

        _crosshairImage = BuildExactCrosshairElement(128);
        _crosshairWrapper = WrapExactElement(OverlayStudioLayer.Crosshair, _crosshairImage);
        _crosshairWrapper.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
                layout.ResetCrosshairOffset();
                ApplyLayerLive(OverlayStudioLayer.Crosshair);
                LayoutStage();
                e.Handled = true;
            }
        };

        _stage.Children.Add(_readoutWrapper);
        _stage.Children.Add(_notificationWrapper);
        _stage.Children.Add(_dockWrapper);
        _stage.Children.Add(_crosshairWrapper);

        root.Children.Add(_stage);

        _toolbar = BuildTopToolbar(out _coordBadge, out _snapButton);
        root.Children.Add(_toolbar);

        Content = root;

        PreviewKeyDown += OnWindowKeyDown;
        SizeChanged += (_, _) => LayoutStage();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += OnWindowDeactivated;
        Closed += OnWindowClosed;

        RobloxWindowTracker.Changed += OnTrackerChanged;
        _trackerLease = RobloxWindowTracker.Acquire();
    }

    internal static Border BuildExactReadoutElement(
        out StackPanel panel,
        out TextBlock locationText,
        out TextBlock separatorText,
        out TextBlock timeText)
    {
        panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(0)
        };

        locationText = CreateExactReadoutTextBlock(new SolidColorBrush(Color.FromRgb(245, 245, 245)));
        locationText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        locationText.Text = OverlayWindow.CurrentLocationDisplay;

        separatorText = CreateExactReadoutTextBlock(new SolidColorBrush(Color.FromRgb(135, 135, 135)));
        separatorText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        separatorText.Text = "  ·  ";
        separatorText.Visibility = Visibility.Collapsed;

        timeText = CreateExactReadoutTextBlock(new SolidColorBrush(Color.FromRgb(185, 185, 185)));
        timeText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        timeText.Text = ClockDisplay.Format(DateTime.UtcNow);

        panel.Children.Add(locationText);
        panel.Children.Add(separatorText);
        panel.Children.Add(timeText);

        Border border = new()
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 58)),
            BorderThickness = new Thickness(1),
            Child = panel
        };
        border.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
        return border;
    }

    private static TextBlock CreateExactReadoutTextBlock(Brush fallback) => new()
    {
        FontSize = 17,
        FontWeight = FontWeights.SemiBold,
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text"),
        Foreground = fallback,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.None,
        LineHeight = 23,
        Margin = new Thickness(0, 1, 0, 1)
    };

    internal static void ApplyExactReadoutConfiguration(
        Border readoutBorder,
        StackPanel readoutPanel,
        TextBlock locationText,
        TextBlock separatorText,
        TextBlock timeText,
        double stageScale)
    {
        AppSettings settings = App.Settings.Prop;
        OverlayLayoutSettings layout = settings.OverlayLayout ?? new OverlayLayoutSettings();
        bool showLocation = settings.ShowServerDetailsUI;
        bool showTime = settings.CurrentTimeDisplay;
        if (!showLocation && !showTime)
        {
            showLocation = true;
            showTime = true;
        }

        bool horizontal = layout.ReadoutHorizontalLayout;
        readoutPanel.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
        locationText.Text = OverlayWindow.CurrentLocationDisplay;
        locationText.Visibility = showLocation ? Visibility.Visible : Visibility.Collapsed;
        timeText.Text = ClockDisplay.Format(DateTime.UtcNow);
        timeText.Visibility = showTime ? Visibility.Visible : Visibility.Collapsed;
        separatorText.Visibility = horizontal && showLocation && showTime ? Visibility.Visible : Visibility.Collapsed;

        int radius = (int)Math.Round(layout.SafeReadoutCornerRadius);
        readoutBorder.CornerRadius = new CornerRadius(radius);
        readoutBorder.Opacity = layout.SafeReadoutOpacity;

        double totalScale = layout.SafeReadoutScale * stageScale;
        readoutBorder.LayoutTransform = Math.Abs(totalScale - 1.0) > 0.005
            ? new ScaleTransform(totalScale, totalScale)
            : Transform.Identity;
    }

    internal static Border BuildExactNotificationElement(
        ImageSource? gameIcon,
        out Grid shadowGrid,
        out Border card,
        out Image iconImage,
        out StackPanel headerPanel,
        out TextBlock headerText,
        out TextBlock titleText,
        out TextBlock bodyText)
    {
        shadowGrid = new Grid { IsHitTestVisible = false };

        Grid contentGrid = new();
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        ImageSource? fallbackIcon = gameIcon;
        if (fallbackIcon == null && SessionDock.ActiveInstance != null)
            fallbackIcon = SessionDock.ActiveInstance.GetLiveSnapshot().GameIcon;
        if (fallbackIcon == null)
        {
            try
            {
                fallbackIcon = new BitmapImage(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute));
            }
            catch
            {
            }
        }

        iconImage = new Image
        {
            Width = 44,
            Height = 44,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Stretch = Stretch.Uniform,
            Source = fallbackIcon,
            Visibility = fallbackIcon == null ? Visibility.Collapsed : Visibility.Visible
        };
        RenderOptions.SetBitmapScalingMode(iconImage, BitmapScalingMode.HighQuality);
        contentGrid.Children.Add(iconImage);

        StackPanel textStack = new() { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(textStack, 1);

        headerPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 5),
            Visibility = Visibility.Collapsed
        };
        try
        {
            Image brandIcon = new()
            {
                Source = new BitmapImage(new Uri("pack://application:,,,/Voidstrap.png", UriKind.Absolute)),
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 6, 0)
            };
            RenderOptions.SetBitmapScalingMode(brandIcon, BitmapScalingMode.HighQuality);
            headerPanel.Children.Add(brandIcon);
        }
        catch
        {
        }

        headerText = new TextBlock
        {
            Text = Strings.Enums_Theme_Voidstrap,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170))
        };
        headerText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        headerPanel.Children.Add(headerText);
        textStack.Children.Add(headerPanel);

        titleText = new TextBlock
        {
            Text = Strings.Ui_SessionNotify_TestNotification,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 14, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.White
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        textStack.Children.Add(titleText);

        bodyText = new TextBlock
        {
            Text = Strings.Ui_SessionNotify_ThisIsHowVoidstrapNotificationsLook,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 64,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180))
        };
        bodyText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        textStack.Children.Add(bodyText);

        contentGrid.Children.Add(textStack);

        card = new Border
        {
            Padding = new Thickness(14, 12, 16, 12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            Child = contentGrid
        };
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        Grid layer = new() { RenderTransformOrigin = new Point(0.5, 0.5) };
        layer.Children.Add(shadowGrid);
        layer.Children.Add(card);

        return new Border
        {
            ClipToBounds = true,
            Child = layer
        };
    }

    internal static void ApplyExactNotificationConfiguration(
        FrameworkElement resourceContext,
        Border host,
        Grid shadow,
        Border card,
        StackPanel header,
        TextBlock headerText,
        TextBlock titleText,
        TextBlock bodyText,
        double stageScale)
    {
        NotificationAppearance appearance = NotificationStyle.Current;
        card.Width = appearance.SafeCardWidth;
        double scale = appearance.SafeScale;
        card.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        card.CornerRadius = NotificationStyle.Corners(appearance);
        card.BorderThickness = NotificationStyle.Borders(appearance);
        card.Background = NotificationStyle.Background(resourceContext, appearance);
        NotificationStyle.FillShadow(shadow, card.CornerRadius);
        double textScale = appearance.SafeTextScale;
        headerText.FontSize = 11 * textScale;
        titleText.FontSize = 14 * textScale;
        bodyText.FontSize = 12 * textScale;
        bodyText.MaxHeight = 64 * textScale;
        header.Visibility = appearance.ShowsHeader(false) ? Visibility.Visible : Visibility.Collapsed;
        host.Padding = NotificationStyle.ShadowMargins(appearance);
        host.LayoutTransform = Math.Abs(stageScale - 1.0) > 0.005
            ? new ScaleTransform(stageScale, stageScale)
            : Transform.Identity;
    }

    internal static Grid BuildExactDockElement(
        ImageSource? fallbackGameIcon,
        out Image profileImage,
        out TextBlock profileName,
        out TextBlock profileHandle,
        out Image gameImage,
        out TextBlock gameTitle,
        out Run sessionClockRun,
        out Run serverClockRun)
    {
        Grid root = new()
        {
            Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        root.Children.Add(new Border { Margin = new Thickness(-2, -1, -2, -3), CornerRadius = new CornerRadius(18), Background = new SolidColorBrush(Color.FromArgb(0x12, 0, 0, 0)), IsHitTestVisible = false });
        root.Children.Add(new Border { Margin = new Thickness(-4, -2, -4, -6), CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.FromArgb(0x0F, 0, 0, 0)), IsHitTestVisible = false });
        root.Children.Add(new Border { Margin = new Thickness(-6, -3, -6, -8), CornerRadius = new CornerRadius(22), Background = new SolidColorBrush(Color.FromArgb(0x0C, 0, 0, 0)), IsHitTestVisible = false });
        root.Children.Add(new Border { Margin = new Thickness(-9, -5, -9, -11), CornerRadius = new CornerRadius(25), Background = new SolidColorBrush(Color.FromArgb(0x09, 0, 0, 0)), IsHitTestVisible = false });
        root.Children.Add(new Border { Margin = new Thickness(-12, -7, -12, -14), CornerRadius = new CornerRadius(28), Background = new SolidColorBrush(Color.FromArgb(0x06, 0, 0, 0)), IsHitTestVisible = false });

        Border pill = new()
        {
            Padding = new Thickness(14, 0, 12, 0),
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true
        };
        pill.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        Border profileCard = new()
        {
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center
        };
        StackPanel profileRow = new() { Orientation = Orientation.Horizontal };
        Grid avatarGrid = new()
        {
            Width = 40,
            Height = 40,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Ellipse avatarBg = new() { Fill = new SolidColorBrush(Color.FromRgb(45, 45, 45)) };
        avatarBg.SetResourceReference(Shape.FillProperty, "ControlFillColorSecondaryBrush");
        avatarGrid.Children.Add(avatarBg);

        TextBlock avatarFallback = new()
        {
            Text = "\uE77B",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170))
        };
        avatarFallback.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        avatarGrid.Children.Add(avatarFallback);

        profileImage = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.UniformToFill,
            Clip = new EllipseGeometry(new Point(20, 20), 20, 20)
        };
        RenderOptions.SetBitmapScalingMode(profileImage, BitmapScalingMode.HighQuality);
        avatarGrid.Children.Add(profileImage);
        profileRow.Children.Add(avatarGrid);

        StackPanel profileTextStack = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 150
        };
        profileName = new TextBlock
        {
            Text = "Roblox",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.White
        };
        profileName.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        profileHandle = new TextBlock
        {
            Text = Strings.Ui_Session_SignedOut,
            Margin = new Thickness(0, 1, 0, 0),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170))
        };
        profileHandle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        profileTextStack.Children.Add(profileName);
        profileTextStack.Children.Add(profileHandle);
        profileRow.Children.Add(profileTextStack);
        profileRow.Children.Add(CreateDockChevron());
        profileCard.Child = profileRow;
        row.Children.Add(profileCard);

        row.Children.Add(CreateDockDivider());

        Border gameCard = new()
        {
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center
        };
        StackPanel gameRow = new() { Orientation = Orientation.Horizontal };
        Border gameIconHost = new()
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(42, 42, 42))
        };
        gameIconHost.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
        gameImage = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.UniformToFill,
            Source = fallbackGameIcon,
            Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 8, 8)
        };
        RenderOptions.SetBitmapScalingMode(gameImage, BitmapScalingMode.HighQuality);
        gameIconHost.Child = gameImage;
        gameRow.Children.Add(gameIconHost);

        StackPanel gameTextStack = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 250
        };
        gameTitle = new TextBlock
        {
            Text = "Roblox",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Brushes.White
        };
        gameTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        gameTextStack.Children.Add(gameTitle);

        StackPanel timerRow = new()
        {
            Margin = new Thickness(0, 1, 0, 0),
            Orientation = Orientation.Horizontal
        };
        Ellipse pulseDot = new()
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.FromRgb(108, 203, 95))
        };
        pulseDot.SetResourceReference(Shape.FillProperty, "SystemFillColorSuccessBrush");
        timerRow.Children.Add(pulseDot);

        TextBlock sessionTimes = new()
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170))
        };
        sessionTimes.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        sessionTimes.Inlines.Add(new Run(Strings.Ui_Session_Session));
        sessionClockRun = new Run("0:00")
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 203, 95))
        };
        sessionClockRun.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorSuccessBrush");
        sessionTimes.Inlines.Add(sessionClockRun);
        sessionTimes.Inlines.Add(new Run(Strings.Ui_Session_Server));
        serverClockRun = new Run("--")
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(108, 203, 95))
        };
        serverClockRun.SetResourceReference(TextElement.ForegroundProperty, "SystemFillColorSuccessBrush");
        sessionTimes.Inlines.Add(serverClockRun);
        timerRow.Children.Add(sessionTimes);
        gameTextStack.Children.Add(timerRow);
        gameRow.Children.Add(gameTextStack);
        gameRow.Children.Add(CreateDockChevron());
        gameCard.Child = gameRow;
        row.Children.Add(gameCard);

        row.Children.Add(CreateDockDivider());

        StackPanel actionsPanel = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        foreach (string glyph in DockActionGlyphs)
        {
            Border iconSlot = new()
            {
                Width = 36,
                Height = 36,
                Margin = new Thickness(2, 0, 2, 0),
                CornerRadius = new CornerRadius(18),
                Background = Brushes.Transparent
            };
            TextBlock glyphBlock = new()
            {
                Text = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White
            };
            glyphBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            iconSlot.Child = glyphBlock;
            actionsPanel.Children.Add(iconSlot);
        }
        row.Children.Add(actionsPanel);

        row.Children.Add(CreateDockDivider());

        StackPanel trailingPanel = new()
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        trailingPanel.Children.Add(CreateDockRoundButton("\uE71B", 16, new Thickness(0)));
        trailingPanel.Children.Add(CreateDockRoundButton("\uE711", 14, new Thickness(8, 0, 0, 0)));
        row.Children.Add(trailingPanel);

        pill.Child = row;
        root.Children.Add(pill);
        return root;
    }

    private static TextBlock CreateDockChevron()
    {
        TextBlock chevron = new()
        {
            Text = "\uE70E",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9,
            Margin = new Thickness(12, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170))
        };
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        return chevron;
    }

    private static Rectangle CreateDockDivider() => new()
    {
        Width = 1,
        Height = 30,
        Margin = new Thickness(10, 0, 10, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Fill = new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255))
    };

    private static Border CreateDockRoundButton(string glyph, double fontSize, Thickness margin)
    {
        Border circle = new()
        {
            Width = 40,
            Height = 40,
            Margin = margin,
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromRgb(45, 45, 45)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        circle.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        circle.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
        TextBlock text = new()
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        circle.Child = text;
        return circle;
    }

    internal static void ApplyExactDockConfiguration(
        Grid dockRoot,
        Image profileImage,
        TextBlock profileName,
        TextBlock profileHandle,
        Image gameImage,
        TextBlock gameTitle,
        Run sessionClockRun,
        Run serverClockRun,
        ImageSource? fallbackGameIcon,
        double stageScale)
    {
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings();
        if (SessionDock.ActiveInstance is SessionDock active)
        {
            var snap = active.GetLiveSnapshot();
            profileName.Text = snap.ProfileName;
            profileHandle.Text = snap.ProfileHandle;
            profileImage.Source = snap.ProfileAvatar;
            gameTitle.Text = snap.GameTitle;
            gameImage.Source = snap.GameIcon ?? fallbackGameIcon;
            sessionClockRun.Text = snap.SessionTime;
            serverClockRun.Text = snap.ServerTime;
        }
        else if (fallbackGameIcon != null)
        {
            gameImage.Source = fallbackGameIcon;
        }

        double totalScale = layout.SafeDockScale * stageScale;
        dockRoot.LayoutTransform = Math.Abs(totalScale - 1.0) > 0.005
            ? new ScaleTransform(totalScale, totalScale)
            : Transform.Identity;
    }

    internal static Image BuildExactCrosshairElement(double size)
    {
        Image img = new()
        {
            Width = size,
            Height = size,
            Stretch = Stretch.Fill,
            UseLayoutRounding = true
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        return img;
    }

    internal static void ApplyExactCrosshairConfiguration(Visual dpiVisual, Image crosshairImage, double dimensionLogical)
    {
        AppSettings settings = App.Settings.Prop;
        int shape = Math.Clamp(settings.CrosshairShapeIndex, 0, 2);
        double opacity = Math.Clamp(settings.CrosshairOpacity, 0.1, 1.0);
        double dpi = Math.Max(1.0, VisualTreeHelper.GetDpi(dpiVisual).DpiScaleX);
        int dimension = Math.Max(32, (int)Math.Round(dimensionLogical * dpi));
        crosshairImage.Width = dimensionLogical;
        crosshairImage.Height = dimensionLogical;
        crosshairImage.Source = CrosshairRenderer.Render(
            dimension,
            OverlayCrosshair.RuntimeScale * dpi,
            shape,
            settings.CrosshairColorHex,
            settings.CrosshairOutlineColorHex,
            settings.CrosshairSize,
            settings.CrosshairGap,
            settings.CrosshairLineThickness,
            opacity);
    }

    private Border WrapExactElement(OverlayStudioLayer layer, UIElement child)
    {
        Border wrapper = new()
        {
            Tag = layer,
            Cursor = Cursors.SizeAll,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(2),
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(1.5),
            Child = child
        };

        wrapper.MouseLeftButtonDown += OnHandleMouseDown;
        wrapper.MouseMove += OnHandleMouseMove;
        wrapper.MouseLeftButtonUp += OnHandleMouseUp;
        wrapper.LostMouseCapture += OnHandleLostCapture;
        return wrapper;
    }

    private static Line CreateGuideLine(bool center)
    {
        return new Line
        {
            Stroke = center
                ? new SolidColorBrush(Color.FromArgb(68, 255, 255, 255))
                : new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            StrokeThickness = 1.0,
            StrokeDashArray = center ? new DoubleCollection { 5, 4 } : new DoubleCollection { 3, 5 },
            IsHitTestVisible = false
        };
    }

    private Border BuildTopToolbar(out TextBlock coordBadge, out Wpf.Ui.Controls.Button snapButton)
    {
        Border bar = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 20, 0, 0),
            Padding = new Thickness(14, 9, 14, 9),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(245, 32, 32, 32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        bar.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        bar.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        StackPanel topRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        TextBlock icon = new()
        {
            Text = "\uE759",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(220, 220, 220)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        topRow.Children.Add(icon);

        StackPanel titleStack = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        TextBlock titleText = new()
        {
            Text = Strings.Ui_OverlayStudio_LiveScreenDragTitle,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };
        titleText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        titleStack.Children.Add(titleText);

        TextBlock hintText = new()
        {
            Text = Strings.Ui_OverlayStudio_LiveScreenDragHint,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160))
        };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        titleStack.Children.Add(hintText);
        topRow.Children.Add(titleStack);

        Border coordPill = new()
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(235, 43, 43, 43)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        coordPill.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        coordPill.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        coordBadge = new TextBlock
        {
            Text = "X: 100%  Y: 0%",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(224, 224, 224))
        };
        coordBadge.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        coordPill.Child = coordBadge;
        topRow.Children.Add(coordPill);

        snapButton = new Wpf.Ui.Controls.Button
        {
            Content = Strings.Ui_OverlayStudio_MagneticSnap + ": ON",
            Appearance = Wpf.Ui.Common.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(11, 6, 11, 6),
            Focusable = false
        };
        snapButton.Click += (_, _) =>
        {
            _magneticSnap = !_magneticSnap;
            _snapButton.Content = Strings.Ui_OverlayStudio_MagneticSnap + (_magneticSnap ? ": ON" : ": OFF");
        };
        topRow.Children.Add(snapButton);

        Wpf.Ui.Controls.Button resetBtn = new()
        {
            Content = Strings.Ui_OverlayStudio_ResetAll,
            Appearance = Wpf.Ui.Common.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(11, 6, 11, 6),
            Focusable = false
        };
        resetBtn.Click += (_, _) =>
        {
            _onResetAll();
            LayoutStage();
        };
        topRow.Children.Add(resetBtn);

        Wpf.Ui.Controls.Button doneBtn = new()
        {
            Content = Strings.Ui_OverlayStudio_Done + " (Esc)",
            Appearance = Wpf.Ui.Common.ControlAppearance.Secondary,
            Padding = new Thickness(13, 6, 13, 6),
            Focusable = false
        };
        doneBtn.Click += (_, _) => Close();
        topRow.Children.Add(doneBtn);

        bar.Child = topRow;
        return bar;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PortableOverlay.Active)
        {
            _ = PortableOverlay.Handle(this);
            return;
        }
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        nint style = GetWindowLongPtrW(hwnd, GwlExStyle);
        SetWindowLongPtrW(hwnd, GwlExStyle, style | WsExToolWindow);
        OverlayDiagnostics.RegisterOverlayHandle(hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (PortableOverlay.Active)
            _ = PortableOverlay.Handle(this);
        RobloxWindowRect current = RobloxWindowTracker.Current;
        if (current.Valid && !current.Foreground)
        {
            Close();
            return;
        }
        SessionDock.ActiveInstance?.SetScreenDragDimOverride(true);
        OverlayWindow.SetScreenDragHidden(true);
        Voidstrap.UI.Elements.Crosshair.CrosshairWindow.SetScreenDragHidden(true);
        PlaceOverGame(current);
        LayoutStage();
        UpdateSelectionBorders();
        if (PortableOverlay.Active)
            PortableOverlay.Focus(this);
        else
            Activate();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (_closed)
                return;
            RobloxWindowRect current = RobloxWindowTracker.Current;
            if (!current.Valid || !current.Foreground)
                Close();
        }));
    }

    private void OnTrackerChanged(object? sender, RobloxWindowRect rect)
    {
        if (_closed)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnTrackerChanged(sender, rect)));
            return;
        }
        if (!rect.Valid || !rect.Foreground)
        {
            Close();
            return;
        }
        PlaceOverGame(rect);
    }

    private void PlaceOverGame(RobloxWindowRect rect)
    {
        if (!rect.Valid)
        {
            Width = 1280;
            Height = 720;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
            return;
        }

        if (PortableOverlay.Active)
        {
            PortableOverlay.Place(this, rect.Left, rect.Top, rect.Width, rect.Height);
            return;
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Left = rect.Left / dpi.DpiScaleX;
        Top = rect.Top / dpi.DpiScaleY;
        Width = Math.Max(320, rect.Width / dpi.DpiScaleX);
        Height = Math.Max(240, rect.Height / dpi.DpiScaleY);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HwndTopmost, rect.Left, rect.Top, rect.Width, rect.Height, 0x0040);
    }

    public void RefreshFromEditor(OverlayStudioLayer selectedLayer)
    {
        if (_closed)
            return;
        if (selectedLayer != OverlayStudioLayer.Panels)
            _selectedLayer = selectedLayer;
        LayoutStage();
        UpdateSelectionBorders();
    }

    private void LayoutStage()
    {
        double stageW = Math.Max(320, _stage.ActualWidth > 0 ? _stage.ActualWidth : Width);
        double stageH = Math.Max(240, _stage.ActualHeight > 0 ? _stage.ActualHeight : Height);

        _guideVerticalCenter.X1 = stageW * 0.5;
        _guideVerticalCenter.X2 = stageW * 0.5;
        _guideVerticalCenter.Y1 = 0;
        _guideVerticalCenter.Y2 = stageH;

        _guideHorizontalCenter.X1 = 0;
        _guideHorizontalCenter.X2 = stageW;
        _guideHorizontalCenter.Y1 = stageH * 0.5;
        _guideHorizontalCenter.Y2 = stageH * 0.5;

        _guideThirdLeft.X1 = stageW / 3.0;
        _guideThirdLeft.X2 = stageW / 3.0;
        _guideThirdLeft.Y1 = 0;
        _guideThirdLeft.Y2 = stageH;

        _guideThirdRight.X1 = stageW * 2.0 / 3.0;
        _guideThirdRight.X2 = stageW * 2.0 / 3.0;
        _guideThirdRight.Y1 = 0;
        _guideThirdRight.Y2 = stageH;

        _guideThirdTop.X1 = 0;
        _guideThirdTop.X2 = stageW;
        _guideThirdTop.Y1 = stageH / 3.0;
        _guideThirdTop.Y2 = stageH / 3.0;

        _guideThirdBottom.X1 = 0;
        _guideThirdBottom.X2 = stageW;
        _guideThirdBottom.Y1 = stageH * 2.0 / 3.0;
        _guideThirdBottom.Y2 = stageH * 2.0 / 3.0;

        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        NotificationAppearance notif = NotificationStyle.Current;

        ApplyExactReadoutConfiguration(
            _readoutBorder,
            _readoutPanel,
            _readoutLocationText,
            _readoutSeparatorText,
            _readoutTimeText,
            1.0);
        MeasureBox(_readoutWrapper);
        double rGap = layout.SafeReadoutEdgeSpacing;
        double rMinX = rGap;
        double rMaxX = Math.Max(rMinX, stageW - _readoutWrapper.DesiredSize.Width - rGap);
        double rMinY = rGap;
        double rMaxY = Math.Max(rMinY, stageH - _readoutWrapper.DesiredSize.Height - rGap);
        Canvas.SetLeft(_readoutWrapper, rMinX + (rMaxX - rMinX) * (layout.SafeReadoutHorizontal / 100.0));
        Canvas.SetTop(_readoutWrapper, rMinY + (rMaxY - rMinY) * (layout.SafeReadoutVertical / 100.0));

        ApplyExactNotificationConfiguration(
            this,
            _notificationHost,
            _notificationShadow,
            _notificationCard,
            _notificationHeader,
            _notificationHeaderText,
            _notificationTitle,
            _notificationText,
            1.0);
        _notificationCard.InvalidateMeasure();
        _notificationHost.InvalidateMeasure();
        MeasureBox(_notificationWrapper);
        Thickness notifPad = _notificationHost.Padding;
        double cardW = _notificationWrapper.DesiredSize.Width - (notifPad.Left + notifPad.Right);
        double cardH = _notificationWrapper.DesiredSize.Height - (notifPad.Top + notifPad.Bottom);
        Point notifSpot = NotificationStyle.Place(notif, stageW, stageH, cardW, cardH, notif.SafeEdgeSpacing);
        Canvas.SetLeft(_notificationWrapper, Math.Round(notifSpot.X - notifPad.Left));
        Canvas.SetTop(_notificationWrapper, Math.Round(notifSpot.Y - notifPad.Top));

        ApplyExactDockConfiguration(
            _dockRoot,
            _dockProfileImage,
            _dockProfileName,
            _dockProfileHandle,
            _dockGameImage,
            _dockGameTitle,
            _dockSessionClockRun,
            _dockServerClockRun,
            _gameIcon,
            1.0);
        MeasureBox(_dockWrapper);
        double dSide = 12;
        double dEdge = layout.SafeDockEdgeSpacing;
        double dMinX = dSide;
        double dMaxX = Math.Max(dMinX, stageW - _dockWrapper.DesiredSize.Width - dSide);
        Canvas.SetLeft(_dockWrapper, dMinX + (dMaxX - dMinX) * (layout.SafeDockHorizontal / 100.0));
        Canvas.SetTop(_dockWrapper, layout.DockTopEdge ? dEdge : Math.Max(dEdge, stageH - _dockWrapper.DesiredSize.Height - dEdge));

        ApplyExactCrosshairConfiguration(this, _crosshairImage, 128);
        MeasureBox(_crosshairWrapper);
        double cLeft = (stageW - _crosshairWrapper.DesiredSize.Width) * 0.5 + layout.SafeCrosshairOffsetX;
        double cTop = (stageH - _crosshairWrapper.DesiredSize.Height) * 0.5 + layout.SafeCrosshairOffsetY;
        Canvas.SetLeft(_crosshairWrapper, Math.Clamp(cLeft, 0, Math.Max(0, stageW - _crosshairWrapper.DesiredSize.Width)));
        Canvas.SetTop(_crosshairWrapper, Math.Clamp(cTop, 0, Math.Max(0, stageH - _crosshairWrapper.DesiredSize.Height)));

        UpdateActiveCoordBanner();
    }

    private static void MeasureBox(FrameworkElement box)
    {
        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
    }

    private void OnHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: OverlayStudioLayer layer } box)
            return;
        _selectedLayer = layer;
        _draggingLayer = layer;
        _dragStartPointer = e.GetPosition(_stage);
        double left = Canvas.GetLeft(box);
        double top = Canvas.GetTop(box);
        _dragStartElementOrigin = new Point(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top);
        UpdateSelectionBorders();
        _onLayerUpdated(layer);
        box.CaptureMouse();
        e.Handled = true;
    }

    private void OnHandleMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingLayer is not OverlayStudioLayer layer || sender is not Border box || !box.IsMouseCaptured)
            return;

        double stageW = Math.Max(320, _stage.ActualWidth);
        double stageH = Math.Max(240, _stage.ActualHeight);
        Point pointer = e.GetPosition(_stage);
        double nextLeft = _dragStartElementOrigin.X + (pointer.X - _dragStartPointer.X);
        double nextTop = _dragStartElementOrigin.Y + (pointer.Y - _dragStartPointer.Y);

        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        NotificationAppearance notif = NotificationStyle.Current;

        MeasureBox(box);
        double elemW = box.DesiredSize.Width;
        double elemH = box.DesiredSize.Height;

        if (layer == OverlayStudioLayer.Readout)
        {
            double gap = layout.SafeReadoutEdgeSpacing;
            double minX = gap;
            double maxX = Math.Max(minX + 1, stageW - elemW - gap);
            double minY = gap;
            double maxY = Math.Max(minY + 1, stageH - elemH - gap);
            double pctX = Math.Clamp((nextLeft - minX) / (maxX - minX) * 100.0, 0, 100);
            double pctY = Math.Clamp((nextTop - minY) / (maxY - minY) * 100.0, 0, 100);
            if (_magneticSnap)
            {
                pctX = SnapPercent(pctX);
                pctY = SnapPercent(pctY);
            }
            layout.ReadoutHorizontal = Math.Round(pctX);
            layout.ReadoutVertical = Math.Round(pctY);
        }
        else if (layer == OverlayStudioLayer.Notifications)
        {
            double gap = notif.SafeEdgeSpacing;
            double minX = gap;
            double maxX = Math.Max(minX + 1, stageW - elemW - gap);
            double minY = gap;
            double maxY = Math.Max(minY + 1, stageH - elemH - gap);
            double pctX = Math.Clamp((nextLeft - minX) / (maxX - minX) * 100.0, 0, 100);
            double pctY = Math.Clamp((nextTop - minY) / (maxY - minY) * 100.0, 0, 100);
            if (_magneticSnap)
            {
                pctX = SnapPercent(pctX);
                pctY = SnapPercent(pctY);
            }
            notif.PositionMigrated = true;
            notif.Horizontal = Math.Round(pctX);
            notif.Vertical = Math.Round(pctY);
            App.Settings.Prop.NotificationAppearance = notif.Copy();
        }
        else if (layer == OverlayStudioLayer.Dock)
        {
            double minX = 12;
            double maxX = Math.Max(minX + 1, stageW - elemW - 12);
            double pctX = Math.Clamp((nextLeft - minX) / (maxX - minX) * 100.0, 0, 100);
            if (_magneticSnap)
                pctX = SnapPercent(pctX);
            layout.DockHorizontal = Math.Round(pctX);
            layout.DockTopEdge = (nextTop + elemH * 0.5) < stageH * 0.45;
        }
        else if (layer == OverlayStudioLayer.Crosshair)
        {
            double centerLeft = (stageW - elemW) * 0.5;
            double centerTop = (stageH - elemH) * 0.5;
            int offsetX = (int)Math.Round(nextLeft - centerLeft);
            int offsetY = (int)Math.Round(nextTop - centerTop);
            if (_magneticSnap)
            {
                if (Math.Abs(offsetX) <= 6) offsetX = 0;
                if (Math.Abs(offsetY) <= 6) offsetY = 0;
            }
            layout.CrosshairOffsetX = Math.Clamp(offsetX, -200, 200);
            layout.CrosshairOffsetY = Math.Clamp(offsetY, -200, 200);
        }

        ApplyLayerLive(layer);
        LayoutStage();
        e.Handled = true;
    }

    private void OnHandleMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border box && box.IsMouseCaptured)
            box.ReleaseMouseCapture();
        _draggingLayer = null;
        App.Settings.SaveDeferred();
        e.Handled = true;
    }

    private void OnHandleLostCapture(object sender, MouseEventArgs e)
    {
        _draggingLayer = null;
    }

    private static double SnapPercent(double value)
    {
        ReadOnlySpan<double> targets = [0, 33, 50, 67, 100];
        foreach (double target in targets)
        {
            if (Math.Abs(value - target) <= 3.5)
                return target;
        }
        return value;
    }

    private void ApplyLayerLive(OverlayStudioLayer layer)
    {
        if (layer == OverlayStudioLayer.Readout)
        {
            OverlayWindow.Reconcile();
            OverlayWindow.SetScreenDragHidden(true);
        }
        else if (layer == OverlayStudioLayer.Dock)
        {
            SessionDock.ActiveInstance?.RefreshLayoutLive();
        }
        else if (layer == OverlayStudioLayer.Crosshair)
        {
            Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
            Voidstrap.UI.Elements.Crosshair.CrosshairWindow.SetScreenDragHidden(true);
        }
        _onLayerUpdated(layer);
    }

    private void UpdateSelectionBorders()
    {
        SetBoxSelected(_readoutWrapper, _selectedLayer == OverlayStudioLayer.Readout);
        SetBoxSelected(_notificationWrapper, _selectedLayer == OverlayStudioLayer.Notifications);
        SetBoxSelected(_dockWrapper, _selectedLayer == OverlayStudioLayer.Dock);
        SetBoxSelected(_crosshairWrapper, _selectedLayer == OverlayStudioLayer.Crosshair);
        UpdateActiveCoordBanner();
    }

    private static void SetBoxSelected(Border wrapper, bool selected)
    {
        wrapper.BorderThickness = new Thickness(1.5);
        wrapper.BorderBrush = selected
            ? new SolidColorBrush(Color.FromArgb(150, 255, 255, 255))
            : Brushes.Transparent;
        Canvas.SetZIndex(wrapper, selected ? 20 : 10);
    }

    private void UpdateActiveCoordBanner()
    {
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings();
        NotificationAppearance notif = NotificationStyle.Current;
        _coordBadge.Text = _selectedLayer switch
        {
            OverlayStudioLayer.Readout => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerReadout}: X {Math.Round(layout.SafeReadoutHorizontal)}% · Y {Math.Round(layout.SafeReadoutVertical)}% · {Math.Round(layout.SafeReadoutEdgeSpacing)} px"),
            OverlayStudioLayer.Notifications => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerNotifications}: X {Math.Round(notif.SafeHorizontal)}% · Y {Math.Round(notif.SafeVertical)}% · {Math.Round(notif.SafeEdgeSpacing)} px"),
            OverlayStudioLayer.Dock => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerDock}: {(layout.DockTopEdge ? Strings.Ui_OverlayStudio_TopEdge : Strings.Ui_OverlayStudio_BottomEdge)} · X {Math.Round(layout.SafeDockHorizontal)}%"),
            OverlayStudioLayer.Crosshair => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerCrosshair}: {layout.SafeCrosshairOffsetX:+#;-#;0}, {layout.SafeCrosshairOffsetY:+#;-#;0} px"),
            _ => Strings.Ui_OverlayStudio_Title
        };
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        Deactivated -= OnWindowDeactivated;
        RobloxWindowTracker.Changed -= OnTrackerChanged;
        _trackerLease.Dispose();
        if (!PortableOverlay.Active)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                OverlayDiagnostics.UnregisterOverlayHandle(hwnd);
        }
        else
        {
            PortableOverlay.Release(this);
        }
        OverlayWindow.SetScreenDragHidden(false);
        Voidstrap.UI.Elements.Crosshair.CrosshairWindow.SetScreenDragHidden(false);
        SessionDock.ActiveInstance?.SetScreenDragDimOverride(false);
        App.Settings.SaveDeferred();
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(IntPtr hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
