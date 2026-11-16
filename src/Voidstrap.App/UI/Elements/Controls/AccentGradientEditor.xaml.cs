using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.UI.Elements.ContextMenu;

namespace Voidstrap.UI.Elements.Controls
{
    public partial class AccentGradientEditor : UserControl
    {
        private const int MinimumStops = 2;

        private readonly ObservableCollection<GradientStopItem> _stops = new();
        private readonly DispatcherTimer _applyTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private bool _loading;

        public AccentGradientEditor()
        {
            InitializeComponent();
            StopList.ItemsSource = _stops;
        }

        private void Editor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            _applyTimer.Tick -= ApplyTimer_Tick;
            if (e.NewValue is true)
            {
                _applyTimer.Tick += ApplyTimer_Tick;
                Reload();
            }
            else
            {
                Flush();
            }
        }

        private void Editor_Unloaded(object sender, RoutedEventArgs e)
        {
            Flush();
            _applyTimer.Tick -= ApplyTimer_Tick;
        }

        private void Flush()
        {
            if (!_applyTimer.IsEnabled)
                return;
            _applyTimer.Stop();
            Apply();
        }

        private void Reload()
        {
            _loading = true;
            ClearStops();
            ThemeGradient gradient = SystemAccent.TryGetGradient(out ThemeGradient? saved) ? saved : SystemAccent.CreateDefaultGradient();
            foreach (ThemeGradientStop stop in gradient.Stops.OrderBy(s => s.Offset))
                AddStopItem(stop.Color, stop.Offset * 100.0);
            AngleSlider.Value = Math.Clamp(gradient.Angle, 0, 360);
            _loading = false;
            UpdatePreview();
        }

        private ThemeGradient Current()
        {
            ThemeGradient gradient = new() { Angle = Math.Round(AngleSlider.Value) };
            foreach (GradientStopItem stop in _stops)
                gradient.Stops.Add(new ThemeGradientStop { Color = stop.Color, Offset = stop.Position / 100.0 });
            return gradient;
        }

        private void UpdatePreview()
        {
            AngleText.Text = Math.Round(AngleSlider.Value).ToString("0", CultureInfo.InvariantCulture) + "°";
            AddStopButton.IsEnabled = _stops.Count < ThemeGradient.MaximumStops;
            if (_stops.Count < MinimumStops)
                return;
            GradientBrush brush = Current().ToBrush();
            brush.Freeze();
            PreviewBar.Background = brush;
        }

        private void Changed()
        {
            if (_loading)
                return;
            UpdatePreview();
            _applyTimer.Stop();
            _applyTimer.Start();
        }

        private void ApplyTimer_Tick(object? sender, EventArgs e)
        {
            _applyTimer.Stop();
            Apply();
        }

        private void Apply()
        {
            if (_stops.Count < MinimumStops || App.Settings.Prop.CustomAccentGradient == null)
                return;
            SystemAccent.SaveGradient(Current());
        }

        private void AddStopItem(Color color, double position)
        {
            GradientStopItem item = new(color, position);
            item.Changed += Changed;
            _stops.Add(item);
        }

        private void ClearStops()
        {
            foreach (GradientStopItem stop in _stops)
            {
                stop.Changed -= Changed;
                stop.Detach();
            }
            _stops.Clear();
        }

        private void SortStops()
        {
            List<GradientStopItem> ordered = _stops.OrderBy(s => s.Position).ToList();
            for (int index = 0; index < ordered.Count; index++)
            {
                int current = _stops.IndexOf(ordered[index]);
                if (current != index)
                    _stops.Move(current, index);
            }
        }

        private void AngleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => Changed();

        private void StopSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: GradientStopItem stop })
                return;
            using RinColorPickerDialog dialog = new(stop.Color, title: Strings.Menu_Appearance_Accent_Picker);
            if (Window.GetWindow(this) is Window owner)
                dialog.Owner = owner;
            if (dialog.ShowDialog() == true)
                stop.SetColor(Color.FromRgb(dialog.SelectedColor.R, dialog.SelectedColor.G, dialog.SelectedColor.B));
        }

        private void AddStop_Click(object sender, RoutedEventArgs e)
        {
            if (_stops.Count >= ThemeGradient.MaximumStops)
                return;
            List<GradientStopItem> ordered = _stops.OrderBy(s => s.Position).ToList();
            GradientStopItem left = ordered[0];
            GradientStopItem right = ordered[^1];
            double widest = -1;
            for (int index = 0; index + 1 < ordered.Count; index++)
            {
                double width = ordered[index + 1].Position - ordered[index].Position;
                if (width > widest)
                {
                    widest = width;
                    left = ordered[index];
                    right = ordered[index + 1];
                }
            }
            Color middle = Color.FromRgb(
                (byte)((left.Color.R + right.Color.R) / 2),
                (byte)((left.Color.G + right.Color.G) / 2),
                (byte)((left.Color.B + right.Color.B) / 2)
            );
            AddStopItem(middle, (left.Position + right.Position) / 2.0);
            SortStops();
            Changed();
        }

        private void RemoveStop_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { DataContext: GradientStopItem stop } || _stops.Count <= MinimumStops)
                return;
            stop.Changed -= Changed;
            stop.Detach();
            _stops.Remove(stop);
            Changed();
        }

        private void ReverseStops_Click(object sender, RoutedEventArgs e)
        {
            _loading = true;
            foreach (GradientStopItem stop in _stops.ToList())
                stop.Position = 100 - stop.Position;
            _loading = false;
            SortStops();
            Changed();
        }

        private void EvenStops_Click(object sender, RoutedEventArgs e)
        {
            _loading = true;
            List<GradientStopItem> ordered = _stops.OrderBy(s => s.Position).ToList();
            for (int index = 0; index < ordered.Count; index++)
                ordered[index].Position = index * 100.0 / (ordered.Count - 1);
            _loading = false;
            SortStops();
            Changed();
        }
    }
}
