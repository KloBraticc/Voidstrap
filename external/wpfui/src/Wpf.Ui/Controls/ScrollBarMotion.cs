using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Wpf.Ui.Controls;

public static class ScrollBarMotion
{
    private const double RestSize = 4;
    private const double ScrollSize = 6;
    private const double ExpandedSize = 8;
    private const double RestThumbOpacity = 0.7;
    private const int SettleMilliseconds = 600;
    private const int LoadQuietMilliseconds = 300;

    private static readonly IEasingFunction Ease = CreateEase();
    private static readonly Duration GrowDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration ShrinkDuration = new(TimeSpan.FromMilliseconds(280));

    private sealed class MotionState
    {
        public DispatcherTimer? Timer;
        public ControlTemplate? Template;
        public Track? Track;
        public Thumb? Thumb;
        public UIElement? Panel;
        public UIElement? ArrowStart;
        public UIElement? ArrowEnd;
        public UIElement? Highlight;
        public bool Attached;
        public bool Scrolling;
        public bool Dragging;
        public DateTime LoadedAt;
        public double Size = double.NaN;
        public double ThumbOpacity = double.NaN;
        public double PanelOpacity = double.NaN;
        public double HighlightOpacity = double.NaN;
    }

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled",
        typeof(bool),
        typeof(ScrollBarMotion),
        new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(MotionState),
        typeof(ScrollBarMotion),
        new PropertyMetadata(null));

    public static bool GetEnabled(DependencyObject element)
    {
        return (bool)element.GetValue(EnabledProperty);
    }

    public static void SetEnabled(DependencyObject element, bool value)
    {
        element.SetValue(EnabledProperty, value);
    }

    private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ScrollBar scrollBar)
            return;

        if ((bool)e.NewValue)
        {
            scrollBar.Loaded += OnLoaded;
            scrollBar.Unloaded += OnUnloaded;
            if (scrollBar.IsLoaded)
                Attach(scrollBar);
        }
        else
        {
            Detach(scrollBar);
            scrollBar.Loaded -= OnLoaded;
            scrollBar.Unloaded -= OnUnloaded;
            scrollBar.ClearValue(StateProperty);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollBar scrollBar)
            Attach(scrollBar);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollBar scrollBar)
            Detach(scrollBar);
    }

    private static MotionState GetState(ScrollBar scrollBar)
    {
        if (scrollBar.GetValue(StateProperty) is MotionState state)
            return state;

        state = new MotionState();
        scrollBar.SetValue(StateProperty, state);
        return state;
    }

    private static void Attach(ScrollBar scrollBar)
    {
        MotionState state = GetState(scrollBar);
        if (state.Attached)
            return;

        state.Attached = true;
        state.LoadedAt = DateTime.UtcNow;
        state.Timer = new DispatcherTimer(DispatcherPriority.Background, scrollBar.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(SettleMilliseconds),
            Tag = scrollBar
        };
        state.Timer.Tick += OnSettleTick;

        scrollBar.MouseEnter += OnPointerChanged;
        scrollBar.MouseLeave += OnPointerLeave;
        scrollBar.MouseMove += OnPointerChanged;
        scrollBar.ValueChanged += OnValueChanged;
        scrollBar.AddHandler(Thumb.DragStartedEvent, (DragStartedEventHandler)OnDragStarted, true);
        scrollBar.AddHandler(Thumb.DragCompletedEvent, (DragCompletedEventHandler)OnDragCompleted, true);

        Update(scrollBar, false);
    }

    private static void Detach(ScrollBar scrollBar)
    {
        if (scrollBar.GetValue(StateProperty) is not MotionState state || !state.Attached)
            return;

        state.Attached = false;
        if (state.Timer != null)
        {
            state.Timer.Stop();
            state.Timer.Tick -= OnSettleTick;
            state.Timer = null;
        }

        scrollBar.MouseEnter -= OnPointerChanged;
        scrollBar.MouseLeave -= OnPointerLeave;
        scrollBar.MouseMove -= OnPointerChanged;
        scrollBar.ValueChanged -= OnValueChanged;
        scrollBar.RemoveHandler(Thumb.DragStartedEvent, (DragStartedEventHandler)OnDragStarted);
        scrollBar.RemoveHandler(Thumb.DragCompletedEvent, (DragCompletedEventHandler)OnDragCompleted);

        state.Scrolling = false;
        state.Dragging = false;
        state.Template = null;
        state.Track = null;
        state.Thumb = null;
        state.Panel = null;
        state.ArrowStart = null;
        state.ArrowEnd = null;
        state.Highlight = null;
        state.Size = double.NaN;
        state.ThumbOpacity = double.NaN;
        state.PanelOpacity = double.NaN;
        state.HighlightOpacity = double.NaN;
    }

    private static void OnPointerChanged(object sender, MouseEventArgs e)
    {
        if (sender is ScrollBar scrollBar)
            Update(scrollBar, true);
    }

    private static void OnPointerLeave(object sender, MouseEventArgs e)
    {
        if (sender is not ScrollBar scrollBar)
            return;

        MotionState state = GetState(scrollBar);
        if (!state.Dragging)
            BeginSettle(state);
        Update(scrollBar, true);
    }

    private static void OnValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not ScrollBar scrollBar)
            return;

        MotionState state = GetState(scrollBar);
        if (!state.Attached || Math.Abs(e.NewValue - e.OldValue) < 0.5)
            return;

        if ((DateTime.UtcNow - state.LoadedAt).TotalMilliseconds < LoadQuietMilliseconds)
            return;

        BeginSettle(state);
        Update(scrollBar, true);
    }

    private static void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not ScrollBar scrollBar)
            return;

        GetState(scrollBar).Dragging = true;
        Update(scrollBar, true);
    }

    private static void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (sender is not ScrollBar scrollBar)
            return;

        MotionState state = GetState(scrollBar);
        state.Dragging = false;
        BeginSettle(state);
        Update(scrollBar, true);
    }

    private static void BeginSettle(MotionState state)
    {
        if (state.Timer == null)
            return;

        state.Scrolling = true;
        state.Timer.Stop();
        state.Timer.Start();
    }

    private static void OnSettleTick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer timer || timer.Tag is not ScrollBar scrollBar)
            return;

        timer.Stop();
        GetState(scrollBar).Scrolling = false;
        Update(scrollBar, true);
    }

    private static void ResolveParts(ScrollBar scrollBar, MotionState state)
    {
        ControlTemplate? template = scrollBar.Template;
        if (ReferenceEquals(state.Template, template) && state.Track != null)
            return;

        state.Template = template;
        state.Track = scrollBar.Track;
        state.Thumb = state.Track?.Thumb;
        state.Panel = template?.FindName("PART_Border", scrollBar) as UIElement;
        bool vertical = scrollBar.Orientation == Orientation.Vertical;
        state.ArrowStart = template?.FindName(vertical ? "PART_ButtonScrollUp" : "PART_ButtonScrollLeft", scrollBar) as UIElement;
        state.ArrowEnd = template?.FindName(vertical ? "PART_ButtonScrollDown" : "PART_ButtonScrollRight", scrollBar) as UIElement;
        state.Thumb?.ApplyTemplate();
        state.Highlight = state.Thumb?.Template?.FindName("ThumbHighlight", state.Thumb) as UIElement;
        state.Size = double.NaN;
        state.ThumbOpacity = double.NaN;
        state.PanelOpacity = double.NaN;
        state.HighlightOpacity = double.NaN;
    }

    private static void Update(ScrollBar scrollBar, bool animate)
    {
        MotionState state = GetState(scrollBar);
        if (!state.Attached)
            return;

        ResolveParts(scrollBar, state);
        if (state.Track == null)
            return;

        bool expanded = state.Dragging || scrollBar.IsMouseOver;
        bool active = expanded || state.Scrolling;
        bool autoHide = scrollBar is DynamicScrollBar;

        double size = expanded ? ExpandedSize : state.Scrolling ? ScrollSize : RestSize;
        double thumbOpacity = active ? 1.0 : autoHide ? 0.0 : RestThumbOpacity;
        double panelOpacity = expanded ? 1.0 : 0.0;
        double highlightOpacity = state.Dragging ? 1.0 : state.Thumb?.IsMouseOver == true ? 0.55 : 0.0;

        DependencyProperty sizeProperty = scrollBar.Orientation == Orientation.Vertical
            ? FrameworkElement.WidthProperty
            : FrameworkElement.HeightProperty;

        Animate(state.Track, sizeProperty, ref state.Size, size, animate);
        if (state.Thumb != null)
            Animate(state.Thumb, UIElement.OpacityProperty, ref state.ThumbOpacity, thumbOpacity, animate);

        double previousPanel = state.PanelOpacity;
        if (state.Panel != null)
            Animate(state.Panel, UIElement.OpacityProperty, ref state.PanelOpacity, panelOpacity, animate);
        else
            state.PanelOpacity = panelOpacity;

        if (!previousPanel.Equals(panelOpacity))
        {
            double arrowState = double.NaN;
            if (state.ArrowStart != null)
                Animate(state.ArrowStart, UIElement.OpacityProperty, ref arrowState, panelOpacity, animate);
            arrowState = double.NaN;
            if (state.ArrowEnd != null)
                Animate(state.ArrowEnd, UIElement.OpacityProperty, ref arrowState, panelOpacity, animate);
        }

        if (state.Highlight != null)
            Animate(state.Highlight, UIElement.OpacityProperty, ref state.HighlightOpacity, highlightOpacity, animate);
    }

    private static void Animate(UIElement element, DependencyProperty property, ref double current, double target, bool animate)
    {
        if (current.Equals(target))
            return;

        bool growing = double.IsNaN(current) || target > current;
        current = target;

        if (!animate)
        {
            element.BeginAnimation(property, null);
            element.SetCurrentValue(property, target);
            return;
        }

        element.BeginAnimation(
            property,
            new DoubleAnimation(target, growing ? GrowDuration : ShrinkDuration) { EasingFunction = Ease },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static PowerEase CreateEase()
    {
        PowerEase ease = new() { EasingMode = EasingMode.EaseOut, Power = 3 };
        ease.Freeze();
        return ease;
    }
}
