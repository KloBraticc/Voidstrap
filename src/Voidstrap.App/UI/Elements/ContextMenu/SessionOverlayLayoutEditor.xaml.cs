using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Voidstrap.Models.Persistable;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.ContextMenu;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionOverlayLayoutEditor : Window
{
    private readonly Action? _sendTestNotification;
    private OverlayStudioLayer _selectedLayer = OverlayStudioLayer.Readout;
    private OverlayStudioLayer? _stageDraggingLayer;
    private Point _stageDragStartPointer;
    private Point _stageDragStartOrigin;
    private SessionLiveOverlayCanvasWindow? _screenDragWindow;
    private bool _ready;
    private bool _loading;
    private bool _closed;

    internal bool IsScreenDragActive => _screenDragWindow is { IsVisible: true };

    private static readonly (string Key, Func<string> Label, string Glyph)[] ManagedPanels =
    [
        ("server", () => Strings.ContextMenu_ServerInformation_Title, "\uE946"),
        ("browser", () => Strings.Ui_Session_Servers, "\uE753"),
        ("games", () => Strings.Ui_Session_Games, "\uE7FC"),
        ("chat", () => Strings.Ui_Session_ChatWithFriends, "\uE8BD"),
        ("history", () => Strings.ContextMenu_GameHistory_Title, "\uE81C"),
        ("music", () => Strings.Ui_Session_Music, "\uE8D6"),
        ("reviews", () => Strings.Ui_Session_CommunityRating, "\uE734"),
        ("notifications", () => Strings.Ui_Nav_Settings, "\uE713"),
        ("layout", () => Strings.Ui_OverlayStudio_Title, "\uE759")
    ];

    public SessionOverlayLayoutEditor(ImageSource? gameIcon = null, Action? sendTestNotification = null)
    {
        _sendTestNotification = sendTestNotification;
        InitializeComponent();
        StageBackgroundBrush.ImageSource = gameIcon;
        Voidstrap.Utility.DynamicRenderSystem.SetPortableBrushImage(StageImageHost, gameIcon);
        _ready = true;
        LoadAllControls();
        SelectLayer(OverlayStudioLayer.Readout);
        Loaded += OnEditorLoaded;
        Closed += OnEditorClosed;
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (_closed)
            return;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_closed)
            {
                LayoutStagePreview();
                RebuildPanelsList();
            }
        }));
    }

    private void OnEditorClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        Loaded -= OnEditorLoaded;
        Closed -= OnEditorClosed;
        CloseScreenDragWindow();
        App.Settings.SaveDeferred();
        GC.SuppressFinalize(this);
    }

    private void CloseScreenDragWindow()
    {
        if (_screenDragWindow == null)
            return;
        SessionLiveOverlayCanvasWindow window = _screenDragWindow;
        _screenDragWindow = null;
        try
        {
            window.Close();
        }
        catch
        {
        }
    }

    private void ScreenDragButton_Click(object sender, RoutedEventArgs e)
    {
        if (_closed)
            return;
        if (_screenDragWindow is { IsVisible: true })
        {
            CloseScreenDragWindow();
            return;
        }
        CloseScreenDragWindow();
        _screenDragWindow = new SessionLiveOverlayCanvasWindow(
            _selectedLayer,
            layer =>
            {
                if (_closed)
                    return;
                if (layer != _selectedLayer && layer != OverlayStudioLayer.Panels)
                    SelectLayer(layer);
                LoadAllControls();
                LayoutStagePreview();
            },
            () =>
            {
                if (_closed)
                    return;
                ResetAllCore();
            });
        _screenDragWindow.Closed += (_, _) =>
        {
            _screenDragWindow = null;
            if (!_closed)
            {
                LoadAllControls();
                LayoutStagePreview();
            }
        };
        _screenDragWindow.Show();
    }

    private void LoadAllControls()
    {
        if (!_ready || _closed)
            return;
        _loading = true;
        try
        {
            AppSettings prop = App.Settings.Prop;
            OverlayLayoutSettings layout = prop.OverlayLayout ??= new OverlayLayoutSettings();
            NotificationAppearance notif = NotificationStyle.Current;

            ReadoutLocationToggle.IsChecked = prop.ShowServerDetailsUI;
            ReadoutClockToggle.IsChecked = prop.CurrentTimeDisplay;
            Readout24HourToggle.IsChecked = prop.Clock24Hour;
            ReadoutPillToggle.IsChecked = layout.ReadoutHorizontalLayout;
            ReadoutHorizontalSlider.Value = Math.Round(layout.SafeReadoutHorizontal);
            ReadoutVerticalSlider.Value = Math.Round(layout.SafeReadoutVertical);
            ReadoutSpacingSlider.Value = Math.Round(layout.SafeReadoutEdgeSpacing);
            ReadoutScaleSlider.Value = Math.Round(layout.SafeReadoutScale * 100);
            ReadoutOpacitySlider.Value = Math.Round(layout.SafeReadoutOpacity * 100);
            ReadoutCornerSlider.Value = Math.Round(layout.SafeReadoutCornerRadius);

            NotifCornerMode.IsChecked = notif.Layout == NotificationLayout.Corner;
            NotifFloatingMode.IsChecked = notif.Layout == NotificationLayout.Floating;
            NotifHorizontalSlider.Value = Math.Round(notif.SafeHorizontal);
            NotifVerticalSlider.Value = Math.Round(notif.SafeVertical);
            double spacingVal = double.IsFinite(notif.EdgeSpacing) && notif.EdgeSpacing > 0
                ? Math.Clamp(notif.EdgeSpacing, 4, 48)
                : NotificationAppearance.FloatingGap;
            NotifSpacingSlider.Value = Math.Round(spacingVal);
            NotifScaleSlider.Value = Math.Round(notif.SafeScale * 100);
            NotifTextScaleSlider.Value = Math.Round(notif.SafeTextScale * 100);
            NotifWidthSlider.Value = Math.Round(notif.SafeCardWidth);
            NotifOpacitySlider.Value = Math.Round(notif.SafeBackgroundOpacity * 100);
            NotifCornerSlider.Value = Math.Round(notif.SafeCornerRadius);

            DockBottomEdgeRadio.IsChecked = !layout.DockTopEdge;
            DockTopEdgeRadio.IsChecked = layout.DockTopEdge;
            DockHorizontalSlider.Value = Math.Round(layout.SafeDockHorizontal);
            DockSpacingSlider.Value = Math.Round(layout.SafeDockEdgeSpacing);
            DockScaleSlider.Value = Math.Round(layout.SafeDockScale * 100);
            double defaultDimmer = Voidstrap.Utility.Platform.IsWindows ? 0.45 : 0.22;
            DockDimmerSlider.Value = Math.Round(layout.ResolveDockBackdropOpacity(defaultDimmer) * 100);

            CrosshairEnableToggle.IsChecked = prop.Crosshair;
            int shape = Math.Clamp(prop.CrosshairShapeIndex, 0, 2);
            CrosshairShapeCross.IsChecked = shape == 0;
            CrosshairShapeDot.IsChecked = shape == 1;
            CrosshairShapeCircle.IsChecked = shape == 2;
            CrosshairSizeSlider.Value = Math.Clamp(prop.CrosshairSize, 4, 60);
            CrosshairThicknessSlider.Value = Math.Clamp(prop.CrosshairLineThickness, 1, 12);
            CrosshairGapSlider.Value = Math.Clamp(prop.CrosshairGap, 0, 30);
            CrosshairOpacitySlider.Value = Math.Round(Math.Clamp(prop.CrosshairOpacity, 0.1, 1.0) * 100);
            CrosshairOffsetXSlider.Value = layout.SafeCrosshairOffsetX;
            CrosshairOffsetYSlider.Value = layout.SafeCrosshairOffsetY;

            FrameGenShowToggle.IsChecked = prop.FrameGenOverlayShow;
            FrameGenCustomPosToggle.IsChecked = layout.FrameGenHudCustomPosition;
            FrameGenHorizontalSlider.Value = Math.Round(layout.SafeFrameGenHudHorizontal);
            FrameGenVerticalSlider.Value = Math.Round(layout.SafeFrameGenHudVertical);
            FrameGenSpacingSlider.Value = layout.SafeFrameGenHudEdgeSpacing;

            UpdateValueLabels();
        }
        finally
        {
            _loading = false;
        }
    }

    private void UpdateValueLabels()
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        ReadoutHorizontalText.Text = ((int)Math.Round(ReadoutHorizontalSlider.Value)).ToString(inv) + "%";
        ReadoutVerticalText.Text = ((int)Math.Round(ReadoutVerticalSlider.Value)).ToString(inv) + "%";
        ReadoutSpacingText.Text = ((int)Math.Round(ReadoutSpacingSlider.Value)).ToString(inv) + " px";
        ReadoutScaleText.Text = ((int)Math.Round(ReadoutScaleSlider.Value)).ToString(inv) + "%";
        ReadoutOpacityText.Text = ((int)Math.Round(ReadoutOpacitySlider.Value)).ToString(inv) + "%";
        ReadoutCornerText.Text = ((int)Math.Round(ReadoutCornerSlider.Value)).ToString(inv) + " px";

        NotifHorizontalText.Text = ((int)Math.Round(NotifHorizontalSlider.Value)).ToString(inv) + "%";
        NotifVerticalText.Text = ((int)Math.Round(NotifVerticalSlider.Value)).ToString(inv) + "%";
        NotifSpacingText.Text = ((int)Math.Round(NotifSpacingSlider.Value)).ToString(inv) + " px";
        NotifScaleText.Text = ((int)Math.Round(NotifScaleSlider.Value)).ToString(inv) + "%";
        NotifTextScaleText.Text = ((int)Math.Round(NotifTextScaleSlider.Value)).ToString(inv) + "%";
        NotifWidthText.Text = ((int)Math.Round(NotifWidthSlider.Value)).ToString(inv) + " px";
        NotifOpacityText.Text = ((int)Math.Round(NotifOpacitySlider.Value)).ToString(inv) + "%";
        NotifCornerText.Text = ((int)Math.Round(NotifCornerSlider.Value)).ToString(inv) + " px";

        DockHorizontalText.Text = ((int)Math.Round(DockHorizontalSlider.Value)).ToString(inv) + "%";
        DockSpacingText.Text = ((int)Math.Round(DockSpacingSlider.Value)).ToString(inv) + " px";
        DockScaleText.Text = ((int)Math.Round(DockScaleSlider.Value)).ToString(inv) + "%";
        DockDimmerText.Text = ((int)Math.Round(DockDimmerSlider.Value)).ToString(inv) + "%";

        CrosshairSizeText.Text = ((int)Math.Round(CrosshairSizeSlider.Value)).ToString(inv) + " px";
        CrosshairThicknessText.Text = ((int)Math.Round(CrosshairThicknessSlider.Value)).ToString(inv) + " px";
        CrosshairGapText.Text = ((int)Math.Round(CrosshairGapSlider.Value)).ToString(inv) + " px";
        CrosshairOpacityText.Text = ((int)Math.Round(CrosshairOpacitySlider.Value)).ToString(inv) + "%";
        int cx = (int)Math.Round(CrosshairOffsetXSlider.Value);
        int cy = (int)Math.Round(CrosshairOffsetYSlider.Value);
        CrosshairOffsetXText.Text = cx.ToString("+#;-#;0", inv) + " px";
        CrosshairOffsetYText.Text = cy.ToString("+#;-#;0", inv) + " px";

        FrameGenHorizontalText.Text = ((int)Math.Round(FrameGenHorizontalSlider.Value)).ToString(inv) + "%";
        FrameGenVerticalText.Text = ((int)Math.Round(FrameGenVerticalSlider.Value)).ToString(inv) + "%";
        FrameGenSpacingText.Text = ((int)Math.Round(FrameGenSpacingSlider.Value)).ToString(inv) + " px";
    }

    private void SelectLayer(OverlayStudioLayer layer)
    {
        _selectedLayer = layer;
        SetTabButtonState(TabReadout, layer == OverlayStudioLayer.Readout);
        SetTabButtonState(TabNotifications, layer == OverlayStudioLayer.Notifications);
        SetTabButtonState(TabDock, layer == OverlayStudioLayer.Dock);
        SetTabButtonState(TabCrosshair, layer == OverlayStudioLayer.Crosshair);
        SetTabButtonState(TabFrameGen, layer == OverlayStudioLayer.FrameGen);
        SetTabButtonState(TabPanels, layer == OverlayStudioLayer.Panels);

        ReadoutInspector.Visibility = layer == OverlayStudioLayer.Readout ? Visibility.Visible : Visibility.Collapsed;
        NotificationsInspector.Visibility = layer == OverlayStudioLayer.Notifications ? Visibility.Visible : Visibility.Collapsed;
        DockInspector.Visibility = layer == OverlayStudioLayer.Dock ? Visibility.Visible : Visibility.Collapsed;
        CrosshairInspector.Visibility = layer == OverlayStudioLayer.Crosshair ? Visibility.Visible : Visibility.Collapsed;
        FrameGenInspector.Visibility = layer == OverlayStudioLayer.FrameGen ? Visibility.Visible : Visibility.Collapsed;
        PanelsInspector.Visibility = layer == OverlayStudioLayer.Panels ? Visibility.Visible : Visibility.Collapsed;

        if (layer == OverlayStudioLayer.Panels)
            RebuildPanelsList();

        UpdateStageSelectionVisuals();
        _screenDragWindow?.RefreshFromEditor(layer);
    }

    private static void SetTabButtonState(Wpf.Ui.Controls.Button button, bool active)
    {
        button.Appearance = active
            ? Wpf.Ui.Common.ControlAppearance.Primary
            : Wpf.Ui.Common.ControlAppearance.Transparent;
    }

    private void LayerTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out OverlayStudioLayer layer))
            return;
        SelectLayer(layer);
    }

    private void StageCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutStagePreview();

    private void LayoutStagePreview()
    {
        if (!_ready || _closed)
            return;

        double stageW = StageCanvas.ActualWidth > 10 ? StageCanvas.ActualWidth : 440;
        double stageH = StageCanvas.ActualHeight > 10 ? StageCanvas.ActualHeight : 230;

        StageGuideVThird1.X1 = stageW / 3.0;
        StageGuideVThird1.X2 = stageW / 3.0;
        StageGuideVThird1.Y1 = 0;
        StageGuideVThird1.Y2 = stageH;

        StageGuideVThird2.X1 = stageW * 2.0 / 3.0;
        StageGuideVThird2.X2 = stageW * 2.0 / 3.0;
        StageGuideVThird2.Y1 = 0;
        StageGuideVThird2.Y2 = stageH;

        StageGuideHThird1.X1 = 0;
        StageGuideHThird1.X2 = stageW;
        StageGuideHThird1.Y1 = stageH / 3.0;
        StageGuideHThird1.Y2 = stageH / 3.0;

        StageGuideHThird2.X1 = 0;
        StageGuideHThird2.X2 = stageW;
        StageGuideHThird2.Y1 = stageH * 2.0 / 3.0;
        StageGuideHThird2.Y2 = stageH * 2.0 / 3.0;

        StageGuideVCenter.X1 = stageW * 0.5;
        StageGuideVCenter.X2 = stageW * 0.5;
        StageGuideVCenter.Y1 = 0;
        StageGuideVCenter.Y2 = stageH;

        StageGuideHCenter.X1 = 0;
        StageGuideHCenter.X2 = stageW;
        StageGuideHCenter.Y1 = stageH * 0.5;
        StageGuideHCenter.Y2 = stageH * 0.5;

        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        NotificationAppearance notif = NotificationStyle.Current;

        StageReadoutStack.Orientation = layout.ReadoutHorizontalLayout ? Orientation.Horizontal : Orientation.Vertical;
        StageReadoutSep.Visibility = layout.ReadoutHorizontalLayout ? Visibility.Visible : Visibility.Collapsed;
        StageReadoutTime.Text = ClockDisplay.Format(DateTime.UtcNow);
        StageReadoutBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double rGap = Math.Max(4, layout.SafeReadoutEdgeSpacing * 0.35);
        double rMinX = rGap;
        double rMaxX = Math.Max(rMinX, stageW - StageReadoutBox.DesiredSize.Width - rGap);
        double rMinY = rGap;
        double rMaxY = Math.Max(rMinY, stageH - StageReadoutBox.DesiredSize.Height - rGap);
        Canvas.SetLeft(StageReadoutBox, rMinX + (rMaxX - rMinX) * (layout.SafeReadoutHorizontal / 100.0));
        Canvas.SetTop(StageReadoutBox, rMinY + (rMaxY - rMinY) * (layout.SafeReadoutVertical / 100.0));

        StageNotificationBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double nGap = Math.Max(2, notif.SafeEdgeSpacing * 0.35);
        double nMinX = nGap;
        double nMaxX = Math.Max(nMinX, stageW - StageNotificationBox.DesiredSize.Width - nGap);
        double nMinY = nGap;
        double nMaxY = Math.Max(nMinY, stageH - StageNotificationBox.DesiredSize.Height - nGap);
        double nHoriz = notif.SafeHorizontal;
        double nVert = notif.SafeVertical;
        Canvas.SetLeft(StageNotificationBox, nMinX + (nMaxX - nMinX) * (nHoriz / 100.0));
        Canvas.SetTop(StageNotificationBox, nMinY + (nMaxY - nMinY) * (nVert / 100.0));

        StageDockBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double dGap = Math.Max(4, layout.SafeDockEdgeSpacing * 0.4);
        double dMinX = 6;
        double dMaxX = Math.Max(dMinX, stageW - StageDockBox.DesiredSize.Width - 6);
        Canvas.SetLeft(StageDockBox, dMinX + (dMaxX - dMinX) * (layout.SafeDockHorizontal / 100.0));
        Canvas.SetTop(StageDockBox, layout.DockTopEdge ? dGap : Math.Max(dGap, stageH - StageDockBox.DesiredSize.Height - dGap));

        StageCrosshairBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double cScale = stageW / 1280.0;
        double cLeft = (stageW - StageCrosshairBox.DesiredSize.Width) * 0.5 + layout.SafeCrosshairOffsetX * cScale;
        double cTop = (stageH - StageCrosshairBox.DesiredSize.Height) * 0.5 + layout.SafeCrosshairOffsetY * cScale;
        Canvas.SetLeft(StageCrosshairBox, Math.Clamp(cLeft, 0, Math.Max(0, stageW - StageCrosshairBox.DesiredSize.Width)));
        Canvas.SetTop(StageCrosshairBox, Math.Clamp(cTop, 0, Math.Max(0, stageH - StageCrosshairBox.DesiredSize.Height)));

        if (Voidstrap.Utility.Platform.IsWindows)
        {
            StageFrameGenBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double fgGap = Math.Max(4, layout.SafeFrameGenHudEdgeSpacing * 0.35);
            double fgMinX = fgGap;
            double fgMaxX = Math.Max(fgMinX, stageW - StageFrameGenBox.DesiredSize.Width - fgGap);
            double fgMinY = fgGap;
            double fgMaxY = Math.Max(fgMinY, stageH - StageFrameGenBox.DesiredSize.Height - fgGap);
            double fgX = layout.FrameGenHudCustomPosition
                ? fgMinX + (fgMaxX - fgMinX) * (layout.SafeFrameGenHudHorizontal / 100.0)
                : 8;
            double fgY = layout.FrameGenHudCustomPosition
                ? fgMinY + (fgMaxY - fgMinY) * (layout.SafeFrameGenHudVertical / 100.0)
                : 32;
            Canvas.SetLeft(StageFrameGenBox, Math.Clamp(fgX, 0, Math.Max(0, stageW - StageFrameGenBox.DesiredSize.Width)));
            Canvas.SetTop(StageFrameGenBox, Math.Clamp(fgY, 0, Math.Max(0, stageH - StageFrameGenBox.DesiredSize.Height)));
        }

        UpdateStageSelectionVisuals();
    }

    private void UpdateStageSelectionVisuals()
    {
        HighlightStageBox(StageReadoutBox, _selectedLayer == OverlayStudioLayer.Readout, Color.FromRgb(92, 168, 255));
        HighlightStageBox(StageNotificationBox, _selectedLayer == OverlayStudioLayer.Notifications, Color.FromRgb(168, 122, 255));
        HighlightStageBox(StageDockBox, _selectedLayer == OverlayStudioLayer.Dock, Color.FromRgb(72, 214, 152));
        HighlightStageBox(StageCrosshairBox, _selectedLayer == OverlayStudioLayer.Crosshair, Color.FromRgb(255, 200, 88));
        HighlightStageBox(StageFrameGenBox, _selectedLayer == OverlayStudioLayer.FrameGen, Color.FromRgb(255, 132, 92));

        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings();
        NotificationAppearance notif = NotificationStyle.Current;
        StageCoordBadge.Text = _selectedLayer switch
        {
            OverlayStudioLayer.Readout => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerReadout}: X {Math.Round(layout.SafeReadoutHorizontal)}% · Y {Math.Round(layout.SafeReadoutVertical)}%"),
            OverlayStudioLayer.Notifications => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerNotifications}: X {Math.Round(notif.SafeHorizontal)}% · Y {Math.Round(notif.SafeVertical)}%"),
            OverlayStudioLayer.Dock => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerDock}: {(layout.DockTopEdge ? Strings.Ui_OverlayStudio_TopEdge : Strings.Ui_OverlayStudio_BottomEdge)} · X {Math.Round(layout.SafeDockHorizontal)}%"),
            OverlayStudioLayer.Crosshair => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerCrosshair}: {layout.SafeCrosshairOffsetX:+#;-#;0}, {layout.SafeCrosshairOffsetY:+#;-#;0} px"),
            OverlayStudioLayer.FrameGen => string.Create(CultureInfo.InvariantCulture, $"{Strings.Ui_OverlayStudio_LayerFrameGen}: X {Math.Round(layout.SafeFrameGenHudHorizontal)}% · Y {Math.Round(layout.SafeFrameGenHudVertical)}%"),
            _ => Strings.Ui_OverlayStudio_LayerPanels
        };
    }

    private static void HighlightStageBox(Border box, bool selected, Color accent)
    {
        box.BorderThickness = new Thickness(selected ? 2.2 : 1.2);
        box.BorderBrush = new SolidColorBrush(selected ? accent : Color.FromArgb(145, accent.R, accent.G, accent.B));
        Panel.SetZIndex(box, selected ? 20 : 10);
    }

    private void StageItem_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string tag } box || !Enum.TryParse(tag, out OverlayStudioLayer layer))
            return;
        SelectLayer(layer);
        _stageDraggingLayer = layer;
        _stageDragStartPointer = e.GetPosition(StageCanvas);
        double left = Canvas.GetLeft(box);
        double top = Canvas.GetTop(box);
        _stageDragStartOrigin = new Point(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top);
        box.CaptureMouse();
        e.Handled = true;
    }

    private void StageItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (_stageDraggingLayer is not OverlayStudioLayer layer || sender is not Border box || !box.IsMouseCaptured)
            return;

        double stageW = Math.Max(100, StageCanvas.ActualWidth);
        double stageH = Math.Max(80, StageCanvas.ActualHeight);
        Point pointer = e.GetPosition(StageCanvas);
        double nextLeft = _stageDragStartOrigin.X + (pointer.X - _stageDragStartPointer.X);
        double nextTop = _stageDragStartOrigin.Y + (pointer.Y - _stageDragStartPointer.Y);

        box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double elemW = box.DesiredSize.Width;
        double elemH = box.DesiredSize.Height;

        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        NotificationAppearance notif = NotificationStyle.Current;

        if (layer == OverlayStudioLayer.Readout)
        {
            double gap = Math.Max(4, layout.SafeReadoutEdgeSpacing * 0.35);
            double minX = gap;
            double maxX = Math.Max(minX + 1, stageW - elemW - gap);
            double minY = gap;
            double maxY = Math.Max(minY + 1, stageH - elemH - gap);
            layout.ReadoutHorizontal = Math.Round(SnapStagePercent(Math.Clamp((nextLeft - minX) / (maxX - minX) * 100.0, 0, 100)));
            layout.ReadoutVertical = Math.Round(SnapStagePercent(Math.Clamp((nextTop - minY) / (maxY - minY) * 100.0, 0, 100)));
            OverlayWindow.Reconcile();
        }
        else if (layer == OverlayStudioLayer.Notifications)
        {
            double gap = Math.Max(2, notif.SafeEdgeSpacing * 0.35);
            double minX = gap;
            double maxX = Math.Max(minX + 1, stageW - elemW - gap);
            double minY = gap;
            double maxY = Math.Max(minY + 1, stageH - elemH - gap);
            notif.PositionMigrated = true;
            notif.Horizontal = Math.Round(SnapStagePercent(Math.Clamp((nextLeft - minX) / (maxX - minX) * 100.0, 0, 100)));
            notif.Vertical = Math.Round(SnapStagePercent(Math.Clamp((nextTop - minY) / (maxY - minY) * 100.0, 0, 100)));
            App.Settings.Prop.NotificationAppearance = notif.Copy();
        }
        else if (layer == OverlayStudioLayer.Dock)
        {
            double minX = 6;
            double maxX = Math.Max(minX + 1, stageW - elemW - 6);
            layout.DockHorizontal = Math.Round(SnapStagePercent(Math.Clamp((nextLeft - minX) / (maxX - minX) * 100.0, 0, 100)));
            layout.DockTopEdge = (nextTop + elemH * 0.5) < stageH * 0.45;
            SessionDock.ActiveInstance?.RefreshLayoutLive();
        }
        else if (layer == OverlayStudioLayer.Crosshair)
        {
            double cScale = Math.Max(0.1, stageW / 1280.0);
            double centerLeft = (stageW - elemW) * 0.5;
            double centerTop = (stageH - elemH) * 0.5;
            int offsetX = (int)Math.Round((nextLeft - centerLeft) / cScale);
            int offsetY = (int)Math.Round((nextTop - centerTop) / cScale);
            if (Math.Abs(offsetX) <= 6) offsetX = 0;
            if (Math.Abs(offsetY) <= 6) offsetY = 0;
            layout.CrosshairOffsetX = Math.Clamp(offsetX, -200, 200);
            layout.CrosshairOffsetY = Math.Clamp(offsetY, -200, 200);
            Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        }
        else if (layer == OverlayStudioLayer.FrameGen)
        {
            double gap = Math.Max(4, layout.SafeFrameGenHudEdgeSpacing * 0.35);
            double minX = gap;
            double maxX = Math.Max(minX + 1, stageW - elemW - gap);
            double minY = gap;
            double maxY = Math.Max(minY + 1, stageH - elemH - gap);
            layout.FrameGenHudCustomPosition = true;
            layout.FrameGenHudHorizontal = Math.Round(SnapStagePercent(Math.Clamp((nextLeft - minX) / (maxX - minX) * 100.0, 0, 100)));
            layout.FrameGenHudVertical = Math.Round(SnapStagePercent(Math.Clamp((nextTop - minY) / (maxY - minY) * 100.0, 0, 100)));
        }

        LoadAllControls();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
        e.Handled = true;
    }

    private void StageItem_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border box && box.IsMouseCaptured)
            box.ReleaseMouseCapture();
        _stageDraggingLayer = null;
        App.Settings.SaveDeferred();
        e.Handled = true;
    }

    private static double SnapStagePercent(double value)
    {
        ReadOnlySpan<double> targets = [0, 50, 100];
        foreach (double target in targets)
        {
            if (Math.Abs(value - target) <= 4.0)
                return target;
        }
        return value;
    }

    private void QuickAnchor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string raw })
            return;
        string[] parts = raw.Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double anchorX)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double anchorY))
            return;

        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        NotificationAppearance notif = NotificationStyle.Current;

        if (_selectedLayer == OverlayStudioLayer.Readout)
        {
            layout.ReadoutHorizontal = anchorX;
            layout.ReadoutVertical = anchorY;
            OverlayWindow.Reconcile();
        }
        else if (_selectedLayer == OverlayStudioLayer.Notifications)
        {
            notif.PositionMigrated = true;
            notif.Horizontal = anchorX;
            notif.Vertical = anchorY;
            App.Settings.Prop.NotificationAppearance = notif.Copy();
        }
        else if (_selectedLayer == OverlayStudioLayer.Dock)
        {
            layout.DockHorizontal = anchorX;
            layout.DockTopEdge = anchorY < 50;
            SessionDock.ActiveInstance?.RefreshLayoutLive();
        }
        else if (_selectedLayer == OverlayStudioLayer.Crosshair)
        {
            layout.CrosshairOffsetX = anchorX < 50 ? -60 : anchorX > 50 ? 60 : 0;
            layout.CrosshairOffsetY = anchorY < 50 ? -60 : anchorY > 50 ? 60 : 0;
            Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        }
        else if (_selectedLayer == OverlayStudioLayer.FrameGen)
        {
            layout.FrameGenHudCustomPosition = true;
            layout.FrameGenHudHorizontal = anchorX;
            layout.FrameGenHudVertical = anchorY;
        }

        App.Settings.SaveDeferred();
        LoadAllControls();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void ReadoutControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        AppSettings prop = App.Settings.Prop;
        OverlayLayoutSettings layout = prop.OverlayLayout ??= new OverlayLayoutSettings();
        prop.ShowServerDetailsUI = ReadoutLocationToggle.IsChecked == true;
        prop.CurrentTimeDisplay = ReadoutClockToggle.IsChecked == true;
        prop.Clock24Hour = Readout24HourToggle.IsChecked == true;
        layout.ReadoutHorizontalLayout = ReadoutPillToggle.IsChecked == true;
        OverlayWindow.Reconcile();
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void ReadoutSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _loading)
            return;
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        layout.ReadoutHorizontal = Math.Round(ReadoutHorizontalSlider.Value);
        layout.ReadoutVertical = Math.Round(ReadoutVerticalSlider.Value);
        layout.ReadoutEdgeSpacing = Math.Round(ReadoutSpacingSlider.Value);
        layout.ReadoutScale = Math.Round(ReadoutScaleSlider.Value) / 100.0;
        layout.ReadoutOpacity = Math.Round(ReadoutOpacitySlider.Value) / 100.0;
        layout.ReadoutCornerRadius = Math.Round(ReadoutCornerSlider.Value);
        UpdateValueLabels();
        OverlayWindow.Reconcile();
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void NotifMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        NotificationAppearance notif = NotificationStyle.Current;
        notif.PositionMigrated = true;
        notif.Layout = NotifFloatingMode.IsChecked == true ? NotificationLayout.Floating : NotificationLayout.Corner;
        App.Settings.Prop.NotificationAppearance = notif.Copy();
        App.Settings.SaveDeferred();
        LoadAllControls();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void NotifSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _loading)
            return;
        NotificationAppearance notif = NotificationStyle.Current;
        notif.PositionMigrated = true;
        notif.Horizontal = Math.Round(NotifHorizontalSlider.Value);
        notif.Vertical = Math.Round(NotifVerticalSlider.Value);
        notif.EdgeSpacing = Math.Round(NotifSpacingSlider.Value);
        notif.Size = Math.Round(NotifScaleSlider.Value);
        notif.TextSize = Math.Round(NotifTextScaleSlider.Value);
        notif.Width = Math.Round(NotifWidthSlider.Value);
        notif.BackgroundOpacity = Math.Round(NotifOpacitySlider.Value) / 100.0;
        notif.CornerRadius = Math.Round(NotifCornerSlider.Value);
        App.Settings.Prop.NotificationAppearance = notif.Copy();
        UpdateValueLabels();
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void SendTestNotification_Click(object sender, RoutedEventArgs e)
    {
        _sendTestNotification?.Invoke();
    }

    private void DockEdge_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        layout.DockTopEdge = DockTopEdgeRadio.IsChecked == true;
        SessionDock.ActiveInstance?.RefreshLayoutLive();
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void DockSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _loading)
            return;
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        layout.DockHorizontal = Math.Round(DockHorizontalSlider.Value);
        layout.DockEdgeSpacing = Math.Round(DockSpacingSlider.Value);
        layout.DockScale = Math.Round(DockScaleSlider.Value) / 100.0;
        layout.DockBackdropOpacity = Math.Round(DockDimmerSlider.Value) / 100.0;
        UpdateValueLabels();
        SessionDock.ActiveInstance?.RefreshLayoutLive();
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void CrosshairControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        App.Settings.Prop.Crosshair = CrosshairEnableToggle.IsChecked == true;
        Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void CrosshairShape_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        App.Settings.Prop.CrosshairShapeIndex = CrosshairShapeDot.IsChecked == true ? 1 : CrosshairShapeCircle.IsChecked == true ? 2 : 0;
        Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        App.Settings.SaveDeferred();
    }

    private void CrosshairColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex })
            return;
        App.Settings.Prop.CrosshairColorHex = hex;
        Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        App.Settings.SaveDeferred();
    }

    private void CrosshairSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _loading)
            return;
        AppSettings prop = App.Settings.Prop;
        OverlayLayoutSettings layout = prop.OverlayLayout ??= new OverlayLayoutSettings();
        prop.CrosshairSize = (int)Math.Round(CrosshairSizeSlider.Value);
        prop.CrosshairLineThickness = (int)Math.Round(CrosshairThicknessSlider.Value);
        prop.CrosshairGap = (int)Math.Round(CrosshairGapSlider.Value);
        prop.CrosshairOpacity = Math.Round(CrosshairOpacitySlider.Value) / 100.0;
        layout.CrosshairOffsetX = (int)Math.Round(CrosshairOffsetXSlider.Value);
        layout.CrosshairOffsetY = (int)Math.Round(CrosshairOffsetYSlider.Value);
        UpdateValueLabels();
        Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void RecenterCrosshair_Click(object sender, RoutedEventArgs e)
    {
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        layout.ResetCrosshairOffset();
        Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        App.Settings.SaveDeferred();
        LoadAllControls();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void FrameGenControl_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        AppSettings prop = App.Settings.Prop;
        OverlayLayoutSettings layout = prop.OverlayLayout ??= new OverlayLayoutSettings();
        prop.FrameGenOverlayShow = FrameGenShowToggle.IsChecked == true;
        layout.FrameGenHudCustomPosition = FrameGenCustomPosToggle.IsChecked == true;
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void FrameGenSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _loading)
            return;
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        layout.FrameGenHudCustomPosition = true;
        layout.FrameGenHudHorizontal = Math.Round(FrameGenHorizontalSlider.Value);
        layout.FrameGenHudVertical = Math.Round(FrameGenVerticalSlider.Value);
        layout.FrameGenHudEdgeSpacing = (int)Math.Round(FrameGenSpacingSlider.Value);
        UpdateValueLabels();
        if (! _loading && FrameGenCustomPosToggle.IsChecked != true)
        {
            _loading = true;
            FrameGenCustomPosToggle.IsChecked = true;
            _loading = false;
        }
        App.Settings.SaveDeferred();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string preset })
            return;
        ApplyPreset(preset);
    }

    private void ApplyPreset(string preset)
    {
        AppSettings prop = App.Settings.Prop;
        OverlayLayoutSettings layout = prop.OverlayLayout ??= new OverlayLayoutSettings();
        NotificationAppearance notif = NotificationStyle.Current;
        notif.PositionMigrated = true;

        switch (preset)
        {
            case "tophud":
                prop.ShowServerDetailsUI = true;
                prop.CurrentTimeDisplay = true;
                layout.ReadoutHorizontal = 0;
                layout.ReadoutVertical = 0;
                layout.ReadoutEdgeSpacing = 12;
                layout.ReadoutScale = 0.90;
                layout.ReadoutOpacity = 0.90;
                layout.ReadoutHorizontalLayout = true;
                notif.Layout = NotificationLayout.Corner;
                notif.Horizontal = 100;
                notif.Vertical = 0;
                layout.DockTopEdge = false;
                layout.DockHorizontal = 50;
                layout.DockScale = 0.95;
                break;

            case "competitive":
                prop.ShowServerDetailsUI = true;
                prop.CurrentTimeDisplay = true;
                layout.ReadoutHorizontal = 0;
                layout.ReadoutVertical = 100;
                layout.ReadoutEdgeSpacing = 10;
                layout.ReadoutScale = 0.85;
                layout.ReadoutOpacity = 0.80;
                layout.ReadoutHorizontalLayout = true;
                notif.Layout = NotificationLayout.Corner;
                notif.Horizontal = 0;
                notif.Vertical = 0;
                notif.Size = 85;
                layout.DockTopEdge = true;
                layout.DockHorizontal = 50;
                layout.DockScale = 0.90;
                layout.DockBackdropOpacity = 0.20;
                prop.Crosshair = true;
                layout.ResetCrosshairOffset();
                break;

            case "streamer":
                prop.ShowServerDetailsUI = false;
                prop.CurrentTimeDisplay = true;
                layout.ReadoutHorizontal = 50;
                layout.ReadoutVertical = 0;
                layout.ReadoutEdgeSpacing = 8;
                layout.ReadoutScale = 0.85;
                layout.ReadoutOpacity = 0.85;
                layout.ReadoutHorizontalLayout = true;
                notif.Layout = NotificationLayout.Corner;
                notif.Horizontal = 0;
                notif.Vertical = 100;
                layout.DockTopEdge = false;
                layout.DockHorizontal = 100;
                layout.DockScale = 0.95;
                break;

            case "topdock":
                layout.DockTopEdge = true;
                layout.DockHorizontal = 50;
                layout.DockEdgeSpacing = 10;
                layout.DockScale = 1.0;
                layout.ReadoutHorizontal = 100;
                layout.ReadoutVertical = 100;
                layout.ReadoutEdgeSpacing = 12;
                notif.Layout = NotificationLayout.Corner;
                notif.Horizontal = 0;
                notif.Vertical = 100;
                break;

            default:
                layout.ResetAll();
                notif.Layout = NotificationLayout.Corner;
                notif.Horizontal = 100;
                notif.Vertical = 100;
                notif.EdgeSpacing = NotificationAppearance.FloatingGap;
                notif.Size = 100;
                notif.TextSize = 100;
                break;
        }

        App.Settings.Prop.NotificationAppearance = notif.Copy();

        RefreshAllLiveOverlays();
        App.Settings.SaveDeferred();
        LoadAllControls();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void ResetLayer_Click(object sender, RoutedEventArgs e)
    {
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        if (_selectedLayer == OverlayStudioLayer.Readout)
        {
            layout.ResetReadout();
            OverlayWindow.Reconcile();
        }
        else if (_selectedLayer == OverlayStudioLayer.Notifications)
        {
            NotificationAppearance fresh = new();
            App.Settings.Prop.NotificationAppearance = fresh;
        }
        else if (_selectedLayer == OverlayStudioLayer.Dock)
        {
            layout.ResetDock();
            SessionDock.ActiveInstance?.RefreshLayoutLive();
        }
        else if (_selectedLayer == OverlayStudioLayer.Crosshair)
        {
            layout.ResetCrosshairOffset();
            Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        }
        else if (_selectedLayer == OverlayStudioLayer.FrameGen)
        {
            layout.ResetFrameGenHud();
        }
        else if (_selectedLayer == OverlayStudioLayer.Panels)
        {
            SessionDock.ActiveInstance?.ResetAllPanelLayoutsLive();
            RebuildPanelsList();
        }

        App.Settings.SaveDeferred();
        LoadAllControls();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e) => ResetAllCore();

    private void ResetAllCore()
    {
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ??= new OverlayLayoutSettings();
        layout.ResetAll();
        App.Settings.Prop.NotificationAppearance = new NotificationAppearance();
        RefreshAllLiveOverlays();
        App.Settings.SaveDeferred();
        LoadAllControls();
        LayoutStagePreview();
        _screenDragWindow?.RefreshFromEditor(_selectedLayer);
    }

    private static void RefreshAllLiveOverlays()
    {
        OverlayWindow.Reconcile();
        Voidstrap.UI.Elements.Crosshair.CrosshairWindow.Reconcile();
        SessionDock.ActiveInstance?.RefreshLayoutLive();
    }

    private void ResetAllPanels_Click(object sender, RoutedEventArgs e)
    {
        SessionDock.ActiveInstance?.ResetAllPanelLayoutsLive();
        RebuildPanelsList();
    }

    private void RebuildPanelsList()
    {
        if (PanelRowsHost == null)
            return;
        PanelRowsHost.Children.Clear();
        Dictionary<string, SessionPanelLayout>? saved = App.State.Prop.SessionPanels;

        for (int i = 0; i < ManagedPanels.Length; i++)
        {
            (string key, Func<string> labelFn, string glyph) = ManagedPanels[i];
            bool last = i == ManagedPanels.Length - 1;
            SessionPanelLayout? entry = saved != null && saved.TryGetValue(key, out SessionPanelLayout? found) ? found : null;
            int pctX = (int)Math.Round(Math.Clamp(entry?.X ?? 0.15, 0, 1) * 100);
            int pctY = (int)Math.Round(Math.Clamp(entry?.Y ?? 0.12, 0, 1) * 100);
            bool pinned = entry?.Pinned == true;

            Border row = new()
            {
                Style = (Style)FindResource(last ? "LastRow" : "Row")
            };

            Grid grid = new();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel left = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = glyph,
                Style = (Style)FindResource("Glyph"),
                Margin = new Thickness(0, 0, 10, 0),
                Foreground = (Brush)FindResource("TextFillColorSecondaryBrush")
            });

            StackPanel titles = new() { VerticalAlignment = VerticalAlignment.Center };
            titles.Children.Add(new TextBlock
            {
                Text = labelFn(),
                Style = (Style)FindResource("Label")
            });
            titles.Children.Add(new TextBlock
            {
                Text = string.Create(CultureInfo.InvariantCulture, $"X: {pctX}% · Y: {pctY}%"),
                Style = (Style)FindResource("Hint")
            });
            left.Children.Add(titles);
            grid.Children.Add(left);

            StackPanel actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(actions, 1);

            Wpf.Ui.Controls.Button pinBtn = new()
            {
                Content = pinned ? Strings.Ui_OverlayStudio_PinPanel : Strings.Ui_OverlayStudio_UnpinnedPanel,
                Appearance = pinned ? Wpf.Ui.Common.ControlAppearance.Primary : Wpf.Ui.Common.ControlAppearance.Secondary,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Focusable = false
            };
            string capturedKey = key;
            bool nextPinned = !pinned;
            pinBtn.Click += (_, _) =>
            {
                SessionDock.ActiveInstance?.SetPanelPinnedLive(capturedKey, nextPinned);
                RebuildPanelsList();
            };
            actions.Children.Add(pinBtn);

            Wpf.Ui.Controls.Button centerBtn = new()
            {
                Content = Strings.Ui_OverlayStudio_CenterPanel,
                Appearance = Wpf.Ui.Common.ControlAppearance.Secondary,
                Padding = new Thickness(10, 5, 10, 5),
                Focusable = false
            };
            centerBtn.Click += (_, _) =>
            {
                SessionDock.ActiveInstance?.CenterPanelLive(capturedKey);
                RebuildPanelsList();
            };
            actions.Children.Add(centerBtn);

            grid.Children.Add(actions);
            row.Child = grid;
            PanelRowsHost.Children.Add(row);
        }
    }
}
