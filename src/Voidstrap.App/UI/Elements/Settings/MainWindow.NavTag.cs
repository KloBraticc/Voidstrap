using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings;

public partial class MainWindow
{
    private const int NavTagShowDelayMs = 350;

    private const int NavTagLingerMs = 280;

    private const int NavTagReshowWindowMs = 500;

    private const double NavTagGap = 8.0;

    private const double NavTagEdge = 4.0;

    private const double NavTagChromeWidth = 26.0;

    private const double NavTagChromeHeight = 18.0;

    private static readonly Duration NavTagGlideDuration = new(TimeSpan.FromMilliseconds(200));

    private static readonly Duration NavTagFadeInDuration = new(TimeSpan.FromMilliseconds(120));

    private static readonly Duration NavTagFadeOutDuration = new(TimeSpan.FromMilliseconds(100));

    private static readonly IEasingFunction NavTagEase = CreateNavTagEase();

    private DispatcherTimer? _navTagTimer;

    private bool _navTagTimerOpens;

    private NavigationItem? _navTagHover;

    private NavigationItem? _navTagShown;

    private long _navTagHiddenAt = long.MinValue;

    private bool _navTagReady;

    private static IEasingFunction CreateNavTagEase()
    {
        CubicEase ease = new() { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private void SetupNavTag()
    {
        if (_navTagReady)
            return;
        _navTagReady = true;
        _navTagTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _navTagTimer.Tick += NavTagTimer_Tick;
        RootNavigation.AddHandler(MouseMoveEvent, new MouseEventHandler(RootNavigation_NavTagMouseMove), true);
        RootNavigation.MouseLeave += RootNavigation_NavTagMouseLeave;
        RootNavigation.SizeChanged += RootNavigation_NavTagSizeChanged;
        PreviewMouseDown += NavTag_PreviewMouseDown;
        PreviewMouseWheel += NavTag_PreviewMouseWheel;
    }

    private void TeardownNavTag()
    {
        if (!_navTagReady)
            return;
        _navTagReady = false;
        if (_navTagTimer != null)
        {
            _navTagTimer.Stop();
            _navTagTimer.Tick -= NavTagTimer_Tick;
            _navTagTimer = null;
        }
        RootNavigation.RemoveHandler(MouseMoveEvent, new MouseEventHandler(RootNavigation_NavTagMouseMove));
        RootNavigation.MouseLeave -= RootNavigation_NavTagMouseLeave;
        RootNavigation.SizeChanged -= RootNavigation_NavTagSizeChanged;
        PreviewMouseDown -= NavTag_PreviewMouseDown;
        PreviewMouseWheel -= NavTag_PreviewMouseWheel;
        _navTagHover = null;
        _navTagShown = null;
    }

    private void RootNavigation_NavTagMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed)
            return;
        NavigationItem? item = FindNavTagItem(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(item, _navTagHover))
            return;
        _navTagHover = item;
        if (item == null || !HasNavTag(item))
        {
            ScheduleNavTagHide();
            return;
        }
        if (_navTagShown != null)
        {
            GlideNavTag(item);
            return;
        }
        if (Environment.TickCount64 - _navTagHiddenAt < NavTagReshowWindowMs)
        {
            OpenNavTag(item);
            return;
        }
        StartNavTagTimer(true, NavTagShowDelayMs);
    }

    private void RootNavigation_NavTagMouseLeave(object sender, MouseEventArgs e)
    {
        _navTagHover = null;
        ScheduleNavTagHide();
    }

    private void RootNavigation_NavTagSizeChanged(object sender, SizeChangedEventArgs e) => HideNavTag(false);

    private void NavTag_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        HideNavTag(false);
        _navTagHiddenAt = long.MinValue;
    }

    private void NavTag_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => HideNavTag(false);

    private void NavTagTimer_Tick(object? sender, EventArgs e)
    {
        _navTagTimer?.Stop();
        if (!_navTagTimerOpens)
        {
            HideNavTag(true);
            return;
        }
        if (_navTagHover != null && IsActive)
            OpenNavTag(_navTagHover);
    }

    private void StartNavTagTimer(bool opens, int milliseconds)
    {
        if (_navTagTimer == null)
            return;
        _navTagTimer.Stop();
        _navTagTimerOpens = opens;
        _navTagTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _navTagTimer.Start();
    }

    private void ScheduleNavTagHide()
    {
        if (_navTagShown == null)
        {
            _navTagTimer?.Stop();
            return;
        }
        StartNavTagTimer(false, NavTagLingerMs);
    }

    private NavigationItem? FindNavTagItem(DependencyObject? source)
    {
        for (DependencyObject? current = source; current != null && !ReferenceEquals(current, RootNavigation); current = GetParent(current))
        {
            if (current is NavigationItem item)
                return item;
        }
        return null;
    }

    private bool HasNavTag(NavigationItem item) =>
        item.IsEnabled && item.IsVisible && !string.IsNullOrWhiteSpace(NavTagTitleOf(item)) && (_sidebarIconMode == true || NavTagShortcutOf(item) != null);

    private static string NavTagTitleOf(NavigationItem item) => item.Content as string ?? item.Content?.ToString() ?? "";

    private static string? NavTagShortcutOf(NavigationItem item)
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

    private bool TryLayoutNavTag(NavigationItem item, out double x, out double y, out Size size)
    {
        x = y = 0;
        size = default;
        string? shortcut = NavTagShortcutOf(item);
        NavTagTitle.Text = NavTagTitleOf(item);
        NavTagKey.Text = shortcut ?? "";
        NavTagKeycap.Visibility = shortcut == null ? Visibility.Collapsed : Visibility.Visible;
        if (!item.IsVisible || PresentationSource.FromVisual(item) == null || PresentationSource.FromVisual(NavTagLayer) == null)
            return false;
        Rect bounds;
        try
        {
            bounds = item.TransformToVisual(NavTagLayer).TransformBounds(new Rect(item.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        NavTagContent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        size = new Size(Math.Ceiling(NavTagContent.DesiredSize.Width + NavTagChromeWidth), Math.Ceiling(NavTagContent.DesiredSize.Height + NavTagChromeHeight));
        x = Math.Round(bounds.Right + NavTagGap);
        y = Math.Round(bounds.Top + (bounds.Height - size.Height) / 2.0);
        double maxY = NavTagLayer.ActualHeight - size.Height - NavTagEdge;
        y = Math.Clamp(y, NavTagEdge, Math.Max(NavTagEdge, maxY));
        double maxX = NavTagLayer.ActualWidth - size.Width - NavTagEdge;
        x = Math.Clamp(x, NavTagEdge, Math.Max(NavTagEdge, maxX));
        return true;
    }

    private void OpenNavTag(NavigationItem item)
    {
        _navTagTimer?.Stop();
        if (!HasNavTag(item) || !TryLayoutNavTag(item, out double x, out double y, out Size size))
            return;
        SetNavTagValue(NavTag, WidthProperty, size.Width);
        SetNavTagValue(NavTag, HeightProperty, size.Height);
        SetNavTagValue(NavTagShift, TranslateTransform.YProperty, y);
        SetNavTagValue(NavTagContent, OpacityProperty, 1.0);
        NavTag.Visibility = Visibility.Visible;
        AnimateNavTag(NavTagShift, TranslateTransform.XProperty, x, NavTagGlideDuration, x - 4.0);
        AnimateNavTag(NavTag, OpacityProperty, 1.0, NavTagFadeInDuration, 0.0);
        _navTagShown = item;
    }

    private void GlideNavTag(NavigationItem item)
    {
        _navTagTimer?.Stop();
        if (ReferenceEquals(item, _navTagShown) && NavTag.Opacity > 0.99)
            return;
        if (!TryLayoutNavTag(item, out double x, out double y, out Size size))
        {
            HideNavTag(false);
            return;
        }
        NavTag.Visibility = Visibility.Visible;
        AnimateNavTag(NavTag, OpacityProperty, 1.0, NavTagFadeInDuration);
        AnimateNavTag(NavTagShift, TranslateTransform.XProperty, x, NavTagGlideDuration);
        AnimateNavTag(NavTagShift, TranslateTransform.YProperty, y, NavTagGlideDuration);
        AnimateNavTag(NavTag, WidthProperty, size.Width, NavTagGlideDuration);
        AnimateNavTag(NavTag, HeightProperty, size.Height, NavTagGlideDuration);
        if (!ReferenceEquals(item, _navTagShown))
            AnimateNavTag(NavTagContent, OpacityProperty, 1.0, NavTagGlideDuration, 0.2);
        _navTagShown = item;
    }

    private void HideNavTag(bool animated)
    {
        _navTagTimer?.Stop();
        if (_navTagShown == null)
            return;
        _navTagShown = null;
        _navTagHiddenAt = Environment.TickCount64;
        if (!animated)
        {
            SetNavTagValue(NavTag, OpacityProperty, 0.0);
            NavTag.Visibility = Visibility.Collapsed;
            return;
        }
        DoubleAnimation fade = new(0.0, NavTagFadeOutDuration) { EasingFunction = NavTagEase };
        fade.Completed += NavTagFade_Completed;
        NavTag.BeginAnimation(OpacityProperty, fade);
    }

    private void NavTagFade_Completed(object? sender, EventArgs e)
    {
        if (_navTagShown == null)
            NavTag.Visibility = Visibility.Collapsed;
    }

    private static void AnimateNavTag(IAnimatable target, DependencyProperty property, double to, Duration duration, double? from = null)
    {
        DoubleAnimation animation = new(to, duration) { EasingFunction = NavTagEase };
        if (from is double start)
            animation.From = start;
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetNavTagValue<T>(T target, DependencyProperty property, double value)
        where T : DependencyObject, IAnimatable
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, value);
    }
}
