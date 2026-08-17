using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Wpf.Ui.Controls;

public static class PopupIntro
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(PopupIntro),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty DistanceProperty =
        DependencyProperty.RegisterAttached(
            "Distance",
            typeof(double),
            typeof(PopupIntro),
            new PropertyMetadata(10.0));

    public static readonly DependencyProperty DurationProperty =
        DependencyProperty.RegisterAttached(
            "Duration",
            typeof(Duration),
            typeof(PopupIntro),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(180))));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static double GetDistance(DependencyObject element) => (double)element.GetValue(DistanceProperty);

    public static void SetDistance(DependencyObject element, double value) => element.SetValue(DistanceProperty, value);

    public static Duration GetDuration(DependencyObject element) => (Duration)element.GetValue(DurationProperty);

    public static void SetDuration(DependencyObject element, Duration value) => element.SetValue(DurationProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Popup popup)
            return;

        popup.Opened -= OnPopupOpened;

        if (e.NewValue is true)
            popup.Opened += OnPopupOpened;
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup popup || popup.Child is not FrameworkElement child)
            return;

        TranslateTransform? translate = FindTranslate(child.RenderTransform);
        if (translate == null || translate.IsFrozen)
            return;

        double distance = GetDistance(popup);
        double from = IsAboveTarget(popup, child) ? distance : -distance;

        DoubleAnimation animation = new DoubleAnimation
        {
            From = from,
            To = 0.0,
            Duration = GetDuration(popup),
            EasingFunction = new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3.0 },
            FillBehavior = FillBehavior.HoldEnd
        };

        translate.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private static TranslateTransform? FindTranslate(Transform? transform)
    {
        if (transform is TranslateTransform direct)
            return direct;

        if (transform is TransformGroup group)
            return group.Children.OfType<TranslateTransform>().FirstOrDefault();

        return null;
    }

    private static bool IsAboveTarget(Popup popup, FrameworkElement child)
    {
        try
        {
            if (popup.PlacementTarget is not FrameworkElement target || !target.IsVisible || !child.IsVisible)
                return false;

            Point childOrigin = child.PointToScreen(new Point(0.0, 0.0));
            Point targetOrigin = target.PointToScreen(new Point(0.0, 0.0));

            return childOrigin.Y + (child.ActualHeight / 2.0) < targetOrigin.Y;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
