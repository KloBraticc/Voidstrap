using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Voidstrap.UI.Elements.Settings;

public partial class MainWindow
{
    private enum HoverTagSide
    {
        Right,
        Below,
    }

    private const int HoverTagShowDelayMs = 350;

    private const int HoverTagLingerMs = 400;

    private const int HoverTagReshowWindowMs = 600;

    private const double HoverTagSideGap = 8.0;

    private const double HoverTagBelowGap = 6.0;

    private const double HoverTagEdge = 4.0;

    private const double HoverTagChromeWidth = 26.0;

    private const double HoverTagChromeHeight = 18.0;

    private const double HoverTagTitleMaxWidth = 300.0;

    private static readonly Duration HoverTagGlideDuration = new(TimeSpan.FromMilliseconds(340));

    private static readonly Duration HoverTagFadeInDuration = new(TimeSpan.FromMilliseconds(140));

    private static readonly Duration HoverTagFadeOutDuration = new(TimeSpan.FromMilliseconds(120));

    private static readonly IEasingFunction HoverTagEase = CreateHoverTagEase();

    private static bool _hoverTagClassHandlerInstalled;

    private DispatcherTimer? _hoverTagTimer;

    private bool _hoverTagTimerOpens;

    private FrameworkElement? _hoverTagHover;

    private HoverTagSide _hoverTagHoverSide;

    private FrameworkElement? _hoverTagShown;

    private long _hoverTagHiddenAt = long.MinValue;

    private bool _hoverTagReady;

    private static IEasingFunction CreateHoverTagEase()
    {
        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private void SetupHoverTag()
    {
        if (_hoverTagReady)
            return;
        _hoverTagReady = true;
        if (!_hoverTagClassHandlerInstalled)
        {
            _hoverTagClassHandlerInstalled = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), ToolTipOpeningEvent, new ToolTipEventHandler(OnAnyToolTipOpening));
        }
        _hoverTagTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _hoverTagTimer.Tick += HoverTagTimer_Tick;
        AddHandler(MouseMoveEvent, new MouseEventHandler(HoverTag_MouseMove), true);
        MouseLeave += HoverTag_MouseLeave;
        SizeChanged += HoverTag_LayoutChanged;
        RootNavigation.SizeChanged += HoverTag_LayoutChanged;
        PreviewMouseDown += HoverTag_PreviewMouseDown;
        PreviewMouseWheel += HoverTag_PreviewMouseWheel;
    }

    private void TeardownHoverTag()
    {
        if (!_hoverTagReady)
            return;
        _hoverTagReady = false;
        if (_hoverTagTimer != null)
        {
            _hoverTagTimer.Stop();
            _hoverTagTimer.Tick -= HoverTagTimer_Tick;
            _hoverTagTimer = null;
        }
        RemoveHandler(MouseMoveEvent, new MouseEventHandler(HoverTag_MouseMove));
        MouseLeave -= HoverTag_MouseLeave;
        SizeChanged -= HoverTag_LayoutChanged;
        RootNavigation.SizeChanged -= HoverTag_LayoutChanged;
        PreviewMouseDown -= HoverTag_PreviewMouseDown;
        PreviewMouseWheel -= HoverTag_PreviewMouseWheel;
        _hoverTagHover = null;
        _hoverTagShown = null;
    }

    private static void OnAnyToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement element || GetWindow(element) is not MainWindow window || !window._hoverTagReady)
            return;
        if (window.TryGetHoverTagZoneOf(element) && window.TryGetHoverTagRows(element, out _))
            e.Handled = true;
    }

    private void HoverTag_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            return;
        FrameworkElement? target = FindHoverTagTarget(e.OriginalSource as DependencyObject, out HoverTagSide side);
        if (ReferenceEquals(target, _hoverTagHover))
            return;
        _hoverTagHover = target;
        _hoverTagHoverSide = side;
        if (target == null)
        {
            ScheduleHoverTagHide();
            return;
        }
        if (_hoverTagShown != null)
        {
            GlideHoverTag(target, side);
            return;
        }
        if (Environment.TickCount64 - _hoverTagHiddenAt < HoverTagReshowWindowMs)
        {
            OpenHoverTag(target, side);
            return;
        }
        StartHoverTagTimer(true, HoverTagShowDelayMs);
    }

    private void HoverTag_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverTagHover = null;
        ScheduleHoverTagHide();
    }

    private void HoverTag_LayoutChanged(object sender, SizeChangedEventArgs e) => HideHoverTag(false);

    private void HoverTag_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        HideHoverTag(false);
        _hoverTagHiddenAt = long.MinValue;
    }

    private void HoverTag_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => HideHoverTag(false);

    private void HoverTagTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTagTimer?.Stop();
        if (!_hoverTagTimerOpens)
        {
            HideHoverTag(true);
            return;
        }
        if (_hoverTagHover != null)
            OpenHoverTag(_hoverTagHover, _hoverTagHoverSide);
    }

    private void StartHoverTagTimer(bool opens, int milliseconds)
    {
        if (_hoverTagTimer == null)
            return;
        _hoverTagTimer.Stop();
        _hoverTagTimerOpens = opens;
        _hoverTagTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _hoverTagTimer.Start();
    }

    private void ScheduleHoverTagHide()
    {
        if (_hoverTagShown == null)
        {
            _hoverTagTimer?.Stop();
            return;
        }
        StartHoverTagTimer(false, HoverTagLingerMs);
    }

    private bool TryGetHoverTagZone(DependencyObject element, out HoverTagSide side)
    {
        side = HoverTagSide.Below;
        if (ReferenceEquals(element, RootNavigation))
        {
            side = HoverTagSide.Right;
            return true;
        }
        return ReferenceEquals(element, RootTitleBar) || ReferenceEquals(element, TopNavPanel);
    }

    private bool TryGetHoverTagZoneOf(DependencyObject element)
    {
        for (DependencyObject? current = element; current != null; current = GetParent(current))
        {
            if (current is Popup)
                return false;
            if (TryGetHoverTagZone(current, out _))
                return true;
        }
        return false;
    }

    private FrameworkElement? FindHoverTagTarget(DependencyObject? source, out HoverTagSide side)
    {
        side = HoverTagSide.Below;
        if (source is Visual visual && PresentationSource.FromVisual(visual) is { } presentation && !ReferenceEquals(presentation.RootVisual, this))
            return null;
        FrameworkElement? candidate = null;
        for (DependencyObject? current = source; current != null; current = GetParent(current))
        {
            if (current is Popup)
                return null;
            if (TryGetHoverTagZone(current, out side))
                return candidate;
            if (candidate == null && current is FrameworkElement element && TryGetHoverTagRows(element, out _))
                candidate = element;
        }
        return null;
    }

    private bool TryGetHoverTagRows(FrameworkElement element, out List<(string Title, string? Shortcut)>? rows)
    {
        rows = null;
        if (!element.IsEnabled || !element.IsVisible || !ToolTipService.GetIsEnabled(element))
            return false;
        if (element is Wpf.Ui.Controls.NavigationItem item)
        {
            string title = item.Content as string ?? item.Content?.ToString() ?? "";
            string? shortcut = NavShortcutOf(item);
            if (string.IsNullOrWhiteSpace(title) || (_sidebarIconMode != true && shortcut == null))
                return false;
            rows = [(title, shortcut)];
            return true;
        }
        if (ReferenceEquals(element, TopSearchButton))
        {
            rows =
            [
                (Strings.ContextMenu_GameBrowser_Search, Wpf.Ui.Converters.ShortcutTextConverter.Format("Ctrl K")),
                (Strings.Ui_Main_SearchRobloxGames, Wpf.Ui.Converters.ShortcutTextConverter.Format("Ctrl L")),
            ];
            return true;
        }
        object? tip = element.ToolTip;
        if (tip is ToolTip toolTip)
            tip = toolTip.Content;
        if (tip is not string text || string.IsNullOrWhiteSpace(text))
            return false;
        rows = [(text, null)];
        return true;
    }

    private static string? NavShortcutOf(Wpf.Ui.Controls.NavigationItem item)
    {
        if (item.PageType == null)
            return null;
        foreach (var shortcut in NavShortcuts)
        {
            if (shortcut.Page == item.PageType)
                return Wpf.Ui.Converters.ShortcutTextConverter.Format(shortcut.Label);
        }
        return null;
    }

    private void FillHoverTagRows(List<(string Title, string? Shortcut)> rows)
    {
        HoverTagRows.Children.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            StackPanel row = new()
            {
                Orientation = Orientation.Horizontal,
                Margin = i == 0 ? new Thickness(0.0) : new Thickness(0.0, 6.0, 0.0, 0.0),
            };
            row.Children.Add(
                new TextBlock
                {
                    Text = rows[i].Title,
                    MaxWidth = HoverTagTitleMaxWidth,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            );
            if (rows[i].Shortcut is string shortcut)
            {
                Border keycap = new()
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
                row.Children.Add(keycap);
            }
            HoverTagRows.Children.Add(row);
        }
    }

    private bool TryLayoutHoverTag(FrameworkElement target, HoverTagSide side, out double x, out double y, out Size size)
    {
        x = y = 0;
        size = default;
        if (!TryGetHoverTagRows(target, out List<(string Title, string? Shortcut)>? rows) || rows == null)
            return false;
        if (PresentationSource.FromVisual(target) == null || PresentationSource.FromVisual(HoverTagLayer) == null)
            return false;
        Rect bounds;
        try
        {
            bounds = target.TransformToVisual(HoverTagLayer).TransformBounds(new Rect(target.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        FillHoverTagRows(rows);
        HoverTagRows.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        size = new Size(
            Math.Ceiling(HoverTagRows.DesiredSize.Width + HoverTagChromeWidth),
            Math.Ceiling(HoverTagRows.DesiredSize.Height + HoverTagChromeHeight)
        );
        if (side == HoverTagSide.Right)
        {
            x = bounds.Right + HoverTagSideGap;
            y = bounds.Top + (bounds.Height - size.Height) / 2.0;
        }
        else
        {
            x = bounds.Left + (bounds.Width - size.Width) / 2.0;
            y = bounds.Bottom + HoverTagBelowGap;
        }
        x = Math.Round(Math.Clamp(x, HoverTagEdge, Math.Max(HoverTagEdge, HoverTagLayer.ActualWidth - size.Width - HoverTagEdge)));
        y = Math.Round(Math.Clamp(y, HoverTagEdge, Math.Max(HoverTagEdge, HoverTagLayer.ActualHeight - size.Height - HoverTagEdge)));
        return true;
    }

    private void OpenHoverTag(FrameworkElement target, HoverTagSide side)
    {
        _hoverTagTimer?.Stop();
        if (!TryLayoutHoverTag(target, side, out double x, out double y, out Size size))
            return;
        SetHoverTagValue(HoverTag, WidthProperty, size.Width);
        SetHoverTagValue(HoverTag, HeightProperty, size.Height);
        SetHoverTagValue(HoverTagRows, OpacityProperty, 1.0);
        if (side == HoverTagSide.Right)
        {
            SetHoverTagValue(HoverTagShift, TranslateTransform.YProperty, y);
            AnimateHoverTag(HoverTagShift, TranslateTransform.XProperty, x, HoverTagGlideDuration, x - 4.0);
        }
        else
        {
            SetHoverTagValue(HoverTagShift, TranslateTransform.XProperty, x);
            AnimateHoverTag(HoverTagShift, TranslateTransform.YProperty, y, HoverTagGlideDuration, y - 4.0);
        }
        HoverTag.Visibility = Visibility.Visible;
        AnimateHoverTag(HoverTag, OpacityProperty, 1.0, HoverTagFadeInDuration, 0.0);
        _hoverTagShown = target;
    }

    private void GlideHoverTag(FrameworkElement target, HoverTagSide side)
    {
        _hoverTagTimer?.Stop();
        if (ReferenceEquals(target, _hoverTagShown) && HoverTag.Opacity > 0.99)
            return;
        bool changed = !ReferenceEquals(target, _hoverTagShown);
        if (!TryLayoutHoverTag(target, side, out double x, out double y, out Size size))
        {
            HideHoverTag(false);
            return;
        }
        HoverTag.Visibility = Visibility.Visible;
        AnimateHoverTag(HoverTag, OpacityProperty, 1.0, HoverTagFadeInDuration);
        AnimateHoverTag(HoverTagShift, TranslateTransform.XProperty, x, HoverTagGlideDuration);
        AnimateHoverTag(HoverTagShift, TranslateTransform.YProperty, y, HoverTagGlideDuration);
        AnimateHoverTag(HoverTag, WidthProperty, size.Width, HoverTagGlideDuration);
        AnimateHoverTag(HoverTag, HeightProperty, size.Height, HoverTagGlideDuration);
        if (changed)
            AnimateHoverTag(HoverTagRows, OpacityProperty, 1.0, HoverTagGlideDuration, 0.15);
        _hoverTagShown = target;
    }

    private void HideHoverTag(bool animated)
    {
        _hoverTagTimer?.Stop();
        if (_hoverTagShown == null)
            return;
        _hoverTagShown = null;
        _hoverTagHiddenAt = Environment.TickCount64;
        if (!animated)
        {
            SetHoverTagValue(HoverTag, OpacityProperty, 0.0);
            HoverTag.Visibility = Visibility.Collapsed;
            return;
        }
        DoubleAnimation fade = new(0.0, HoverTagFadeOutDuration) { EasingFunction = HoverTagEase };
        fade.Completed += HoverTagFade_Completed;
        HoverTag.BeginAnimation(OpacityProperty, fade);
    }

    private void HoverTagFade_Completed(object? sender, EventArgs e)
    {
        if (_hoverTagShown == null)
            HoverTag.Visibility = Visibility.Collapsed;
    }

    private static void AnimateHoverTag(IAnimatable target, DependencyProperty property, double to, Duration duration, double? from = null)
    {
        DoubleAnimation animation = new(to, duration) { EasingFunction = HoverTagEase };
        if (from is double start)
            animation.From = start;
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetHoverTagValue<T>(T target, DependencyProperty property, double value)
        where T : DependencyObject, IAnimatable
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }
}
