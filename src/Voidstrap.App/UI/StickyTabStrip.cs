using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Voidstrap.UI;

internal sealed class StickyTabStrip
{
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(120);

    private static bool _registered;

    private readonly TabControl _tabs;
    private readonly ScrollViewer _scroll;
    private readonly Panel _strip;
    private readonly TranslateTransform _pin = new();
    private LinearGradientBrush? _surface;
    private bool _pinned;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(TabControl), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnTabControlLoaded));
    }

    private static void OnTabControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TabControl tabs || !ReferenceEquals(e.OriginalSource, tabs))
            return;
        ScrollViewer? scroll = null;
        bool insidePage = false;
        for (DependencyObject? parent = VisualTreeHelper.GetParent(tabs); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is TabControl)
                return;
            scroll ??= parent as ScrollViewer;
            if (parent is Page)
            {
                insidePage = true;
                break;
            }
        }
        if (!insidePage || scroll == null || FindItemsHost(tabs) is not Panel strip)
            return;
        _ = new StickyTabStrip(tabs, scroll, strip);
    }

    private static Panel? FindItemsHost(DependencyObject root)
    {
        Queue<DependencyObject> pending = new();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            DependencyObject node = pending.Dequeue();
            if (node is Panel { IsItemsHost: true } panel)
                return panel;
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);
                if (child is not ContentPresenter)
                    pending.Enqueue(child);
            }
        }
        return null;
    }

    private StickyTabStrip(TabControl tabs, ScrollViewer scroll, Panel strip)
    {
        _tabs = tabs;
        _scroll = scroll;
        _strip = strip;
        _strip.RenderTransform = _pin;
        Panel.SetZIndex(_strip, 100);
        _scroll.ScrollChanged += OnScrollChanged;
        _tabs.Unloaded += OnTabsUnloaded;
        Update();
    }

    private void OnTabsUnloaded(object sender, RoutedEventArgs e)
    {
        _scroll.ScrollChanged -= OnScrollChanged;
        _tabs.Unloaded -= OnTabsUnloaded;
        _pin.Y = 0;
        _strip.Background = Brushes.Transparent;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => Update();

    private void Update()
    {
        if (!_scroll.IsAncestorOf(_strip))
            return;
        double top = _strip.TransformToAncestor(_scroll).Transform(new Point(0, 0)).Y - _pin.Y;
        double limit = Math.Max(0, _tabs.ActualHeight - _strip.ActualHeight);
        double offset = Math.Clamp(-top, 0, limit);
        _pin.Y = offset;
        SetPinned(offset > 0);
    }

    private void SetPinned(bool pinned)
    {
        if (pinned == _pinned)
            return;
        _pinned = pinned;
        if (_surface == null)
        {
            Color color = (_tabs.TryFindResource("SolidBackgroundFillColorBaseBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x20, 0x20, 0x20);
            _surface = new LinearGradientBrush(
                [new GradientStop(color, 0), new GradientStop(color, 0.8), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1)],
                90
            )
            {
                Opacity = 0,
            };
            _strip.Background = _surface;
        }
        _surface.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(pinned ? 1 : 0, FadeDuration));
    }
}
