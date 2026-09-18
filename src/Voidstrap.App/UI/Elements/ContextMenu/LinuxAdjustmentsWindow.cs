using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;

namespace Voidstrap.UI.Elements.ContextMenu
{
    public sealed class LinuxAdjustmentsWindow : Window
    {
        private const string WindowTitle = "Voidstrap Adjustments";

        public LinuxAdjustmentsWindow(object dataContext)
        {
            DataContext = dataContext;
            Title = WindowTitle;
            Width = 420;
            Height = 300;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));

            StackPanel root = new() { Margin = new Thickness(18) };

            root.Children.Add(BuildRow("Brightness", "Brightness", 0, 100));
            root.Children.Add(BuildRow("Saturation", "Saturation", 0, 200));
            root.Children.Add(BuildRow("Contrast", "Contrast", 0, 200));
            root.Children.Add(BuildRow("Color temperature", "ColorTemperature", -100, 100));

            Button close = new()
            {
                Content = "Close",
                Width = 96,
                Height = 30,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 6, 0, 0)
            };

            close.Click += OnCloseClicked;
            root.Children.Add(close);

            Content = root;
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
                button.Click -= OnCloseClicked;

            Dismiss();
        }

        private void Dismiss()
        {
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                Voidstrap.App.Logger?.WriteLine("LinuxAdjustmentsWindow", "The window could not be closed: " + ex.Message);
            }

            if (Voidstrap.Utility.Platform.IsWindows)
                return;

            try
            {
                nint handle = Voidstrap.Platform.Linux.LinuxWindowInterop.FindOwnWindowByTitle(WindowTitle);

                if (handle != 0)
                    Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetInvisible(handle);
            }
            catch (Exception ex)
            {
                Voidstrap.App.Logger?.WriteLine("LinuxAdjustmentsWindow", "The window could not be dismissed: " + ex.Message);
            }
        }

        private static StackPanel BuildRow(string label, string path, double minimum, double maximum)
        {
            StackPanel panel = new() { Margin = new Thickness(0, 0, 0, 14) };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = Brushes.White
            });

            Grid row = new();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Slider slider = new()
            {
                Minimum = minimum,
                Maximum = maximum,
                TickFrequency = 5,
                IsSnapToTickEnabled = true,
                VerticalAlignment = VerticalAlignment.Center
            };

            Binding binding = new(path)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };

            slider.SetBinding(RangeBase.ValueProperty, binding);

            TextBlock readout = new()
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
                MinWidth = 46,
                TextAlignment = TextAlignment.Right,
                Foreground = Brushes.White
            };

            readout.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay, StringFormat = "{0:F0}" });

            Grid.SetColumn(slider, 0);
            Grid.SetColumn(readout, 1);
            row.Children.Add(slider);
            row.Children.Add(readout);
            panel.Children.Add(row);

            return panel;
        }
    }
}
