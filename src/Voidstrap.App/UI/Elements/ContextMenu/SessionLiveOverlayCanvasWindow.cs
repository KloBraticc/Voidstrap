using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Persistable;
using Voidstrap.Resources;
using Voidstrap.UI;

namespace Voidstrap.UI.Elements.Overlay;

public enum OverlayStudioLayer
{
    Readout = 0,
    Notifications = 1,
    Dock = 2,
    Crosshair = 3,
    FrameGen = 4,
    Panels = 5
}

internal sealed partial class SessionLiveOverlayCanvasWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x80;
    private static readonly IntPtr HwndTopmost = new(-1);

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

    private readonly Border _readoutBox;
    private readonly TextBlock _readoutCoordText;
    private readonly TextBlock _readoutSampleLocation;
    private readonly TextBlock _readoutSampleClock;

    private readonly Border _notificationBox;
    private readonly TextBlock _notificationCoordText;

    private readonly Border _dockBox;
    private readonly TextBlock _dockCoordText;

    private readonly Border _crosshairBox;
    private readonly TextBlock _crosshairCoordText;

    private readonly Border _frameGenBox;
    private readonly TextBlock _frameGenCoordText;

    private OverlayStudioLayer _selectedLayer;
    private OverlayStudioLayer? _draggingLayer;
    private Point _dragStartPointer;
    private Point _dragStartElementOrigin;
    private bool _magneticSnap = true;
    private bool _closed;

    public SessionLiveOverlayCanvasWindow(
        OverlayStudioLayer initialLayer,
        Action<OverlayStudioLayer> onLayerUpdated,
        Action onResetAll)
    {
        _selectedLayer = initialLayer == OverlayStudioLayer.Panels ? OverlayStudioLayer.Readout : initialLayer;
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
        Background = new SolidColorBrush(Color.FromArgb(Voidstrap.Utility.Platform.IsLinux ? (byte)215 : (byte)56, 8, 10, 16));
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        Grid root = new();
        _stage = new Canvas { ClipToBounds = true, Background = Brushes.Transparent };

        Rectangle outerBorder = new()
        {
            Stroke = new SolidColorBrush(Color.FromArgb(140, 99, 140, 255)),
            StrokeThickness = 2,
            IsHitTestVisible = false
        };
        root.Children.Add(outerBorder);

        _guideThirdLeft = CreateGuideLine(true, false);
        _guideThirdRight = CreateGuideLine(true, false);
        _guideThirdTop = CreateGuideLine(false, false);
        _guideThirdBottom = CreateGuideLine(false, false);
        _guideVerticalCenter = CreateGuideLine(true, true);
        _guideHorizontalCenter = CreateGuideLine(false, true);

        _stage.Children.Add(_guideThirdLeft);
        _stage.Children.Add(_guideThirdRight);
        _stage.Children.Add(_guideThirdTop);
        _stage.Children.Add(_guideThirdBottom);
        _stage.Children.Add(_guideVerticalCenter);
        _stage.Children.Add(_guideHorizontalCenter);

        _readoutBox = CreateReadoutHandle(out _readoutCoordText, out _readoutSampleLocation, out _readoutSampleClock);
        _notificationBox = CreateNotificationHandle(out _notificationCoordText);
        _dockBox = CreateDockHandle(out _dockCoordText);
        _crosshairBox = CreateCrosshairHandle(out _crosshairCoordText);
        _frameGenBox = CreateFrameGenHandle(out _frameGenCoordText);

        if (!Voidstrap.Utility.Platform.IsWindows)
            _frameGenBox.Visibility = Visibility.Collapsed;

        _stage.Children.Add(_readoutBox);
        _stage.Children.Add(_notificationBox);
        _stage.Children.Add(_dockBox);
        _stage.Children.Add(_frameGenBox);
        _stage.Children.Add(_crosshairBox);

        root.Children.Add(_stage);

        _toolbar = BuildTopToolbar(out _coordBadge, out _snapButton);
        root.Children.Add(_toolbar);

        Content = root;

        PreviewKeyDown += OnWindowKeyDown;
        SizeChanged += (_, _) => LayoutStage();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnWindowClosed;

        RobloxWindowTracker.Changed += OnTrackerChanged;
        _trackerLease = RobloxWindowTracker.Acquire();
    }

    private static Line CreateGuideLine(bool vertical, bool center)
    {
        return new Line
        {
            Stroke = center
                ? new SolidColorBrush(Color.FromArgb(150, 99, 179, 255))
                : new SolidColorBrush(Color.FromArgb(75, 255, 255, 255)),
            StrokeThickness = center ? 1.5 : 1.0,
            StrokeDashArray = center ? new DoubleCollection { 6, 4 } : new DoubleCollection { 4, 6 },
            IsHitTestVisible = false
        };
    }

    private Border BuildTopToolbar(out TextBlock coordBadge, out Wpf.Ui.Controls.Button snapButton)
    {
        Border bar = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 22, 0, 0),
            Padding = new Thickness(16, 10, 16, 10),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(238, 18, 21, 29)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(140, 99, 140, 255)),
            BorderThickness = new Thickness(1)
        };

        StackPanel outer = new() { Orientation = Orientation.Vertical };
        StackPanel topRow = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        TextBlock icon = new()
        {
            Text = "\uE759",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(115, 170, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        topRow.Children.Add(icon);

        StackPanel titleStack = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        titleStack.Children.Add(new TextBlock
        {
            Text = Strings.Ui_OverlayStudio_LiveScreenDragTitle,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = Strings.Ui_OverlayStudio_LiveScreenDragHint,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(178, 188, 208))
        });
        topRow.Children.Add(titleStack);

        Border coordPill = new()
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(190, 30, 36, 50)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        coordBadge = new TextBlock
        {
            Text = "X: 100%  Y: 0%",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(145, 195, 255))
        };
        coordPill.Child = coordBadge;
        topRow.Children.Add(coordPill);

        snapButton = new Wpf.Ui.Controls.Button
        {
            Content = Strings.Ui_OverlayStudio_MagneticSnap + ": ON",
            Appearance = Wpf.Ui.Common.ControlAppearance.Primary,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 6, 12, 6),
            Focusable = false
        };
        snapButton.Click += (_, _) =>
        {
            _magneticSnap = !_magneticSnap;
            _snapButton.Content = Strings.Ui_OverlayStudio_MagneticSnap + (_magneticSnap ? ": ON" : ": OFF");
            _snapButton.Appearance = _magneticSnap
                ? Wpf.Ui.Common.ControlAppearance.Primary
                : Wpf.Ui.Common.ControlAppearance.Secondary;
        };
        topRow.Children.Add(snapButton);

        Wpf.Ui.Controls.Button resetBtn = new()
        {
            Content = Strings.Ui_OverlayStudio_ResetAll,
            Appearance = Wpf.Ui.Common.ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 6, 12, 6),
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
            Appearance = Wpf.Ui.Common.ControlAppearance.Primary,
            Padding = new Thickness(14, 6, 14, 6),
            Focusable = false
        };
        doneBtn.Click += (_, _) => Close();
        topRow.Children.Add(doneBtn);

        outer.Children.Add(topRow);
        bar.Child = outer;
        return bar;
    }

    private Border CreateReadoutHandle(out TextBlock coordText, out TextBlock sampleLoc, out TextBlock sampleClock)
    {
        StackPanel body = new() { Orientation = Orientation.Vertical };
        StackPanel header = CreateHandleBadge("\uE968", Strings.Ui_OverlayStudio_LayerReadout, Color.FromRgb(92, 168, 255), out coordText);
        body.Children.Add(header);

        sampleLoc = new TextBlock
        {
            Text = "Location: Ashburn, US",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 4, 0, 1)
        };
        sampleClock = new TextBlock
        {
            Text = ClockDisplay.Format(DateTime.UtcNow),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(185, 196, 216))
        };
        body.Children.Add(sampleLoc);
        body.Children.Add(sampleClock);

        return WrapInteractiveHandle(OverlayStudioLayer.Readout, body, Color.FromRgb(92, 168, 255), new Thickness(14, 10, 14, 10));
    }

    private Border CreateNotificationHandle(out TextBlock coordText)
    {
        StackPanel body = new() { Orientation = Orientation.Vertical, Width = 290 };
        StackPanel header = CreateHandleBadge("\uEA8F", Strings.Ui_OverlayStudio_LayerNotifications, Color.FromRgb(168, 122, 255), out coordText);
        body.Children.Add(header);
        body.Children.Add(new TextBlock
        {
            Text = Strings.Ui_SessionNotify_TestNotification,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 6, 0, 2)
        });
        body.Children.Add(new TextBlock
        {
            Text = Strings.Ui_SessionNotify_ThisIsHowVoidstrapNotificationsLook,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(182, 192, 212)),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        return WrapInteractiveHandle(OverlayStudioLayer.Notifications, body, Color.FromRgb(168, 122, 255), new Thickness(14, 10, 14, 10));
    }

    private Border CreateDockHandle(out TextBlock coordText)
    {
        StackPanel body = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        StackPanel badge = CreateHandleBadge("\uE759", Strings.Ui_OverlayStudio_LayerDock, Color.FromRgb(72, 214, 152), out coordText);
        body.Children.Add(badge);
        return WrapInteractiveHandle(OverlayStudioLayer.Dock, body, Color.FromRgb(72, 214, 152), new Thickness(22, 12, 22, 12));
    }

    private Border CreateCrosshairHandle(out TextBlock coordText)
    {
        Grid grid = new() { Width = 92, Height = 92 };
        Ellipse ring = new()
        {
            Width = 34,
            Height = 34,
            Stroke = new SolidColorBrush(Color.FromRgb(255, 200, 88)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Line hLine = new()
        {
            X1 = 26,
            Y1 = 46,
            X2 = 66,
            Y2 = 46,
            Stroke = Brushes.Lime,
            StrokeThickness = 2
        };
        Line vLine = new()
        {
            X1 = 46,
            Y1 = 26,
            X2 = 46,
            Y2 = 66,
            Stroke = Brushes.Lime,
            StrokeThickness = 2
        };
        Border labelPill = new()
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Color.FromArgb(220, 18, 21, 28))
        };
        coordText = new TextBlock
        {
            Text = "0, 0 px",
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 110))
        };
        labelPill.Child = coordText;

        grid.Children.Add(ring);
        grid.Children.Add(hLine);
        grid.Children.Add(vLine);
        grid.Children.Add(labelPill);

        Border handle = WrapInteractiveHandle(OverlayStudioLayer.Crosshair, grid, Color.FromRgb(255, 200, 88), new Thickness(4));
        handle.MouseLeftButtonDown += (_, e) =>
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
        return handle;
    }

    private Border CreateFrameGenHandle(out TextBlock coordText)
    {
        StackPanel body = new() { Orientation = Orientation.Vertical, Width = 220 };
        StackPanel header = CreateHandleBadge("\uE945", Strings.Ui_OverlayStudio_LayerFrameGen, Color.FromRgb(255, 132, 92), out coordText);
        body.Children.Add(header);
        body.Children.Add(new TextBlock
        {
            Text = "FPS .......... : 240  (120 -> 240)",
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 226, 150)),
            Margin = new Thickness(0, 5, 0, 0)
        });
        return WrapInteractiveHandle(OverlayStudioLayer.FrameGen, body, Color.FromRgb(255, 132, 92), new Thickness(12, 8, 12, 8));
    }

    private static StackPanel CreateHandleBadge(string glyph, string title, Color accent, out TextBlock coordText)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        row.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        coordText = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(195, 205, 222)),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(coordText);
        return row;
    }

    private Border WrapInteractiveHandle(OverlayStudioLayer layer, UIElement child, Color accent, Thickness padding)
    {
        Border box = new()
        {
            Tag = layer,
            Cursor = Cursors.SizeAll,
            CornerRadius = new CornerRadius(10),
            Padding = padding,
            Background = new SolidColorBrush(Color.FromArgb(220, 18, 22, 31)),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(2),
            Child = child
        };

        box.MouseLeftButtonDown += OnHandleMouseDown;
        box.MouseMove += OnHandleMouseMove;
        box.MouseLeftButtonUp += OnHandleMouseUp;
        box.LostMouseCapture += OnHandleLostCapture;
        return box;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (PortableOverlay.Active)
            return;
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        nint style = GetWindowLongPtrW(hwnd, GwlExStyle);
        SetWindowLongPtrW(hwnd, GwlExStyle, style | WsExToolWindow);
        OverlayDiagnostics.RegisterOverlayHandle(hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SessionDock.ActiveInstance?.SetScreenDragDimOverride(true);
        PlaceOverGame(RobloxWindowTracker.Current);
        LayoutStage();
        UpdateSelectionBorders();
        if (PortableOverlay.Active)
            PortableOverlay.Focus(this);
        else
            Activate();
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
        if (!rect.Valid)
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

        _readoutSampleClock.Text = ClockDisplay.Format(DateTime.UtcNow);
        MeasureBox(_readoutBox);
        double rGap = layout.SafeReadoutEdgeSpacing;
        double rMinX = rGap;
        double rMaxX = Math.Max(rMinX, stageW - _readoutBox.DesiredSize.Width - rGap);
        double rMinY = rGap;
        double rMaxY = Math.Max(rMinY, stageH - _readoutBox.DesiredSize.Height - rGap);
        Canvas.SetLeft(_readoutBox, rMinX + (rMaxX - rMinX) * (layout.SafeReadoutHorizontal / 100.0));
        Canvas.SetTop(_readoutBox, rMinY + (rMaxY - rMinY) * (layout.SafeReadoutVertical / 100.0));
        _readoutCoordText.Text = $"{Math.Round(layout.SafeReadoutHorizontal)}% · {Math.Round(layout.SafeReadoutVertical)}%";

        MeasureBox(_notificationBox);
        double nGap = notif.SafeEdgeSpacing;
        double nMinX = nGap;
        double nMaxX = Math.Max(nMinX, stageW - _notificationBox.DesiredSize.Width - nGap);
        double nMinY = nGap;
        double nMaxY = Math.Max(nMinY, stageH - _notificationBox.DesiredSize.Height - nGap);
        double nHoriz = notif.SafeHorizontal;
        double nVert = notif.SafeVertical;
        Canvas.SetLeft(_notificationBox, nMinX + (nMaxX - nMinX) * (nHoriz / 100.0));
        Canvas.SetTop(_notificationBox, nMinY + (nMaxY - nMinY) * (nVert / 100.0));
        _notificationCoordText.Text = $"{Math.Round(nHoriz)}% · {Math.Round(nVert)}%";

        MeasureBox(_dockBox);
        double dSide = 12;
        double dEdge = layout.SafeDockEdgeSpacing;
        double dMinX = dSide;
        double dMaxX = Math.Max(dMinX, stageW - _dockBox.DesiredSize.Width - dSide);
        Canvas.SetLeft(_dockBox, dMinX + (dMaxX - dMinX) * (layout.SafeDockHorizontal / 100.0));
        Canvas.SetTop(_dockBox, layout.DockTopEdge ? dEdge : Math.Max(dEdge, stageH - _dockBox.DesiredSize.Height - dEdge));
        _dockCoordText.Text = (layout.DockTopEdge ? Strings.Ui_OverlayStudio_TopEdge : Strings.Ui_OverlayStudio_BottomEdge)
            + $" · {Math.Round(layout.SafeDockHorizontal)}%";

        MeasureBox(_crosshairBox);
        double cLeft = (stageW - _crosshairBox.DesiredSize.Width) * 0.5 + layout.SafeCrosshairOffsetX;
        double cTop = (stageH - _crosshairBox.DesiredSize.Height) * 0.5 + layout.SafeCrosshairOffsetY;
        Canvas.SetLeft(_crosshairBox, Math.Clamp(cLeft, 0, Math.Max(0, stageW - _crosshairBox.DesiredSize.Width)));
        Canvas.SetTop(_crosshairBox, Math.Clamp(cTop, 0, Math.Max(0, stageH - _crosshairBox.DesiredSize.Height)));
        _crosshairCoordText.Text = $"{layout.SafeCrosshairOffsetX:+#;-#;0}, {layout.SafeCrosshairOffsetY:+#;-#;0} px";

        if (Voidstrap.Utility.Platform.IsWindows)
        {
            MeasureBox(_frameGenBox);
            double fgGap = layout.SafeFrameGenHudEdgeSpacing;
            double fgMinX = fgGap;
            double fgMaxX = Math.Max(fgMinX, stageW - _frameGenBox.DesiredSize.Width - fgGap);
            double fgMinY = fgGap;
            double fgMaxY = Math.Max(fgMinY, stageH - _frameGenBox.DesiredSize.Height - fgGap);
            double fgX = layout.FrameGenHudCustomPosition
                ? fgMinX + (fgMaxX - fgMinX) * (layout.SafeFrameGenHudHorizontal / 100.0)
                : 18;
            double fgY = layout.FrameGenHudCustomPosition
                ? fgMinY + (fgMaxY - fgMinY) * (layout.SafeFrameGenHudVertical / 100.0)
                : 120;
            Canvas.SetLeft(_frameGenBox, Math.Clamp(fgX, 0, Math.Max(0, stageW - _frameGenBox.DesiredSize.Width)));
            Canvas.SetTop(_frameGenBox, Math.Clamp(fgY, 0, Math.Max(0, stageH - _frameGenBox.DesiredSize.Height)));
            _frameGenCoordText.Text = layout.FrameGenHudCustomPosition
                ? $"{Math.Round(layout.SafeFrameGenHudHorizontal)}% · {Math.Round(layout.SafeFrameGenHudVertical)}%"
                : "Default";
        }

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
        else if (layer == OverlayStudioLayer.FrameGen)
        {
            double gap = layout.SafeFrameGenHudEdgeSpacing;
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
            layout.FrameGenHudCustomPosition = true;
            layout.FrameGenHudHorizontal = Math.Round(pctX);
            layout.FrameGenHudVertical = Math.Round(pctY);
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
            OverlayWindow.Reconcile();
        else if (layer == OverlayStudioLayer.Dock)
            SessionDock.ActiveInstance?.RefreshLayoutLive();
        else if (layer == OverlayStudioLayer.Crosshair)
            Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        _onLayerUpdated(layer);
    }

    private void UpdateSelectionBorders()
    {
        SetBoxSelected(_readoutBox, _selectedLayer == OverlayStudioLayer.Readout, Color.FromRgb(92, 168, 255));
        SetBoxSelected(_notificationBox, _selectedLayer == OverlayStudioLayer.Notifications, Color.FromRgb(168, 122, 255));
        SetBoxSelected(_dockBox, _selectedLayer == OverlayStudioLayer.Dock, Color.FromRgb(72, 214, 152));
        SetBoxSelected(_crosshairBox, _selectedLayer == OverlayStudioLayer.Crosshair, Color.FromRgb(255, 200, 88));
        SetBoxSelected(_frameGenBox, _selectedLayer == OverlayStudioLayer.FrameGen, Color.FromRgb(255, 132, 92));
        UpdateActiveCoordBanner();
    }

    private static void SetBoxSelected(Border box, bool selected, Color accent)
    {
        box.BorderThickness = new Thickness(selected ? 2.5 : 1.5);
        box.BorderBrush = new SolidColorBrush(selected ? accent : Color.FromArgb(165, accent.R, accent.G, accent.B));
        box.Background = new SolidColorBrush(selected
            ? Color.FromArgb(242, 24, 30, 44)
            : Color.FromArgb(205, 16, 20, 28));
        Canvas.SetZIndex(box, selected ? 20 : 10);
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
            OverlayStudioLayer.FrameGen => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerFrameGen}: X {Math.Round(layout.SafeFrameGenHudHorizontal)}% · Y {Math.Round(layout.SafeFrameGenHudVertical)}%"),
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
