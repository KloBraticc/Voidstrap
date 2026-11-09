using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Voidstrap.UI.Elements.Controls;

public class LoadingShimmer : Border
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source),
        typeof(ImageSource),
        typeof(LoadingShimmer),
        new PropertyMetadata(null, OnStateChanged)
    );

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(LoadingShimmer),
        new PropertyMetadata(true, OnStateChanged)
    );

    private static readonly Duration SweepDuration = new Duration(TimeSpan.FromMilliseconds(1300));
    private static readonly Duration PulseDuration = new Duration(TimeSpan.FromMilliseconds(900));

    private readonly Border _sweep = new Border { IsHitTestVisible = false };
    private TranslateTransform? _sweepTransform;
    private bool _running;

    public LoadingShimmer()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        Child = _sweep;
        SetResourceReference(BackgroundProperty, "ControlFillColorSecondaryBrush");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static void OnStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((LoadingShimmer)sender).Update();

    private void OnLoaded(object sender, RoutedEventArgs e) => Update();

    private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => Update();

    private void Update()
    {
        bool waiting = IsActive && Source == null;
        Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        if (waiting && IsLoaded && IsVisible)
            Start();
        else
            Stop();
    }

    private void Start()
    {
        if (_running)
            return;
        _running = true;
        try
        {
            if (OperatingSystem.IsWindows())
                StartSweep();
            else
                StartPulse();
        }
        catch
        {
            Stop();
        }
    }

    private void StartSweep()
    {
        Color highlight = TryFindResource("ControlFillColorTertiaryBrush") is SolidColorBrush brush ? brush.Color : Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF);
        Color edge = Color.FromArgb(0, highlight.R, highlight.G, highlight.B);
        Color peak = Color.FromArgb((byte)Math.Min(255, highlight.A * 3 + 24), highlight.R, highlight.G, highlight.B);
        _sweepTransform = new TranslateTransform(-1, 0);
        LinearGradientBrush gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0.35),
            RelativeTransform = _sweepTransform,
        };
        gradient.GradientStops.Add(new GradientStop(edge, 0.25));
        gradient.GradientStops.Add(new GradientStop(peak, 0.5));
        gradient.GradientStops.Add(new GradientStop(edge, 0.75));
        _sweep.Background = gradient;
        DoubleAnimation sweep = new DoubleAnimation(-1, 1, SweepDuration) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(sweep, 30);
        _sweepTransform.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private void StartPulse()
    {
        DoubleAnimation pulse = new DoubleAnimation(1, 0.45, PulseDuration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Timeline.SetDesiredFrameRate(pulse, 30);
        BeginAnimation(OpacityProperty, pulse);
    }

    private void Stop()
    {
        if (!_running)
            return;
        _running = false;
        try
        {
            _sweepTransform?.BeginAnimation(TranslateTransform.XProperty, null);
            _sweepTransform = null;
            _sweep.Background = null;
            BeginAnimation(OpacityProperty, null);
        }
        catch { }
    }
}
