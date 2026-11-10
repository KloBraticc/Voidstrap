using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.Utility;

public static class CountUpText
{
    private const double DurationMs = 650;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value",
        typeof(double),
        typeof(CountUpText),
        new PropertyMetadata(double.NaN, OnValueChanged)
    );

    public static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
        "Format",
        typeof(string),
        typeof(CountUpText),
        new PropertyMetadata("compact")
    );

    private static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown",
        typeof(double),
        typeof(CountUpText),
        new PropertyMetadata(double.NaN)
    );

    private static readonly List<Tween> Active = new();

    private static DispatcherTimer? _timer;

    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);

    public static string GetFormat(DependencyObject element) => (string)element.GetValue(FormatProperty);

    public static void SetFormat(DependencyObject element, string value) => element.SetValue(FormatProperty, value);

    public static string Format(string format, double value)
    {
        long rounded = (long)Math.Round(value);
        if (IsTemplate(format))
            return string.Format(CultureInfo.CurrentCulture, format, rounded.ToString("N0", CultureInfo.CurrentCulture));
        return format switch
        {
            "n0" => rounded.ToString("N0", CultureInfo.CurrentCulture),
            "percent" => rounded.ToString(CultureInfo.CurrentCulture) + "%",
            "compactplus" => LibraryViewModel.FormatCount(rounded) + (rounded >= 1000 ? "+" : ""),
            _ => LibraryViewModel.FormatCount(rounded),
        };
    }

    private static bool IsTemplate(string format) => format.Contains("{0", StringComparison.Ordinal);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text)
            return;
        double to = (double)e.NewValue;
        double from = (double)text.GetValue(ShownProperty);
        Tween? running = Active.Find(t => ReferenceEquals(t.Target, text));
        if (running != null)
        {
            from = running.Current;
            Active.Remove(running);
        }
        bool template = IsTemplate(GetFormat(text));
        if (double.IsNaN(to) || double.IsInfinity(to))
        {
            text.SetValue(ShownProperty, template ? from : double.NaN);
            text.Text = template ? "" : "--";
            return;
        }
        if (template && double.IsNaN(from))
            from = 0;
        if (double.IsNaN(from) || from == to || !text.IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            Show(text, to);
            return;
        }
        Active.Add(new Tween(text, from, to, Environment.TickCount64));
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTick;
            _timer.Start();
        }
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        long now = Environment.TickCount64;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            Tween tween = Active[i];
            double t = Math.Clamp((now - tween.Start) / DurationMs, 0, 1);
            double eased = 1 - Math.Pow(1 - t, 3);
            tween.Current = tween.From + (tween.To - tween.From) * eased;
            if (t >= 1 || !tween.Target.IsLoaded)
            {
                Show(tween.Target, tween.To);
                Active.RemoveAt(i);
            }
            else
                tween.Target.Text = Format(GetFormat(tween.Target), tween.Current);
        }
        if (Active.Count == 0 && _timer != null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private static void Show(TextBlock text, double value)
    {
        text.SetValue(ShownProperty, value);
        text.Text = Format(GetFormat(text), value);
    }

    private sealed class Tween
    {
        public Tween(TextBlock target, double from, double to, long start)
        {
            Target = target;
            From = from;
            To = to;
            Start = start;
            Current = from;
        }

        public TextBlock Target { get; }

        public double From { get; }

        public double To { get; }

        public long Start { get; }

        public double Current { get; set; }
    }
}
