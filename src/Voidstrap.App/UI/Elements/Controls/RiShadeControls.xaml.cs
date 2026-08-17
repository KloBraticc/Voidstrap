using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Controls
{
    public partial class RiShadeControls : UserControl
    {
        private enum RowKind
        {
            Toggle,
            Slider,
            Combo,
        }

        private sealed class Row
        {
            public string Label = "";
            public string Path = "";
            public RowKind Kind;
            public double Min;
            public double Max;
            public string Format = "0.###";
            public string[]? Items;
        }

        private static Row Tg(string label, string path) => new Row { Label = label, Path = path, Kind = RowKind.Toggle };
        private static Row Sl(string label, string path, double min, double max, string fmt = "0.###") => new Row { Label = label, Path = path, Kind = RowKind.Slider, Min = min, Max = max, Format = fmt };
        private static Row Cb(string label, string path, string[] items) => new Row { Label = label, Path = path, Kind = RowKind.Combo, Items = items };

        private readonly RiShadeViewModel _vm;
        private bool _building = true;

        public RiShadeControls()
        {
            InitializeComponent();
            _vm = new RiShadeViewModel();
            DataContext = _vm;
            BuildGroups(_vm);
            _building = false;
        }

        private void EnableGroup(object sender)
        {
            if (_building || sender is not FrameworkElement fe || fe.Tag is not string path || path.Length == 0)
                return;
            var prop = typeof(RiShadeViewModel).GetProperty(path);
            if (prop == null || prop.PropertyType != typeof(bool))
                return;
            if (prop.GetValue(_vm) is bool current && current)
                return;
            prop.SetValue(_vm, true);
        }

        private void OnGatedSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => EnableGroup(sender);

        private void OnGatedComboChanged(object sender, SelectionChangedEventArgs e) => EnableGroup(sender);

        private void OnGatedToggleChecked(object sender, RoutedEventArgs e) => EnableGroup(sender);

        private void BuildGroups(RiShadeViewModel vm)
        {
            AddGroup("Colour grading", new[]
            {
                Tg("Enable grading", "GradeEnabled"),
                Sl("Brightness", "Brightness", -1, 1),
                Sl("Gamma", "Gamma", 0.2, 3),
                Sl("Hue shift", "HueShift", -180, 180, "0"),
                Cb("Temperature", "ColorTempIndex", vm.ColorTempNames),
                Sl("Custom temp R", "TempCustomR", 0, 2),
                Sl("Custom temp G", "TempCustomG", 0, 2),
                Sl("Custom temp B", "TempCustomB", 0, 2),
                Sl("Balance R", "BalanceR", 0, 2),
                Sl("Balance G", "BalanceG", 0, 2),
                Sl("Balance B", "BalanceB", 0, 2),
                Sl("Lift R", "LiftR", -0.2, 0.2),
                Sl("Lift G", "LiftG", -0.2, 0.2),
                Sl("Lift B", "LiftB", -0.2, 0.2),
                Sl("Gain R", "GainR", 0.5, 2),
                Sl("Gain G", "GainG", 0.5, 2),
                Sl("Gain B", "GainB", 0.5, 2),
            });

            AddGroup("Tonemapping", new[]
            {
                Tg("Enable tonemap", "TonemapEnabled"),
                Cb("Mode", "TonemapModeIndex", vm.TonemapNames),
                Sl("Exposure", "TonemapExposure", 0.1, 5),
                Sl("White point", "TonemapWhitepoint", 1, 16),
            });

            AddGroup("Vignette", new[]
            {
                Tg("Enable vignette", "VignetteEnabled"),
                Sl("Strength", "VignetteStrength", 0, 4),
                Sl("Feather", "VignetteFeather", 0.1, 4),
                Sl("Centre X", "VignetteCenterX", -0.5, 0.5),
                Sl("Centre Y", "VignetteCenterY", -0.5, 0.5),
            });

            AddGroup("Sharpen", new[]
            {
                Tg("Enable sharpen", "SharpenEnabled"),
                Sl("Strength", "SharpenStrength", 0, 5),
                Sl("Radius", "SharpenRadius", 0.5, 4),
                Sl("Clamp", "SharpenClamp", 0.01, 1),
            });

            AddGroup("Bloom", new[]
            {
                Tg("Enable bloom", "BloomEnabled"),
                Sl("Strength", "BloomStrength", 0, 5),
                Sl("Threshold", "BloomThreshold", 0, 1),
                Sl("Radius", "BloomRadius", 0.5, 4),
                Sl("Passes", "BloomPasses", 1, 6, "0"),
                Sl("Tint R", "BloomTintR", 0, 2),
                Sl("Tint G", "BloomTintG", 0, 2),
                Sl("Tint B", "BloomTintB", 0, 2),
            });

            AddGroup("Chromatic aberration", new[]
            {
                Tg("Enable chroma", "ChromaEnabled"),
                Sl("Strength", "ChromaStrength", 0, 0.02, "0.####"),
                Tg("Radial", "ChromaRadial"),
            });

            AddGroup("Film grain", new[]
            {
                Tg("Enable grain", "GrainEnabled"),
                Sl("Strength", "GrainStrength", 0, 0.3),
                Sl("Size", "GrainSize", 0.1, 5),
                Tg("Colored", "GrainColored"),
            });

            AddGroup("Depth of field", new[]
            {
                Tg("Enable DOF", "DofEnabled"),
                Sl("Blur strength", "DofStrength", 0, 1),
                Sl("Focus radius", "DofFocusRange", 0, 0.5),
                Sl("Feather", "DofFeather", 0, 1),
            });

            AddGroup("Ambient occlusion", new[]
            {
                Tg("Enable AO", "AoEnabled"),
                Sl("Strength", "AoStrength", 0, 3),
                Sl("Radius", "AoRadius", 0.001, 0.1, "0.####"),
                Cb("Samples", "AoSamplesIndex", vm.AoSampleNames),
            });

            AddGroup("SSR (buggy, WIP)", new[]
            {
                Tg("Enable SSR", "SsrEnabled"),
                Sl("Intensity", "SsrIntensity", 0, 1),
                Sl("Glossiness", "SsrGlossiness", 0, 1),
                Sl("Wetness", "SsrReflectivity", 0, 1),
                Sl("Reach", "SsrDistance", 0, 1),
                Sl("Surface sheen", "SsrSheen", 0, 1),
            });

            AddGroup("Clarity", new[]
            {
                Sl("Strength", "ClarityStrength", 0, 1),
            });

            AddGroup("Global illumination", new[]
            {
                Sl("Strength", "GiStrength", 0, 1),
                Sl("Radius", "GiRadius", 0, 1),
            });

            AddGroup("Ambient light", new[]
            {
                Sl("Strength", "AmbientStrength", 0, 1),
            });

            AddGroup("Auto exposure", new[]
            {
                Tg("Enable auto exposure", "EyeAdaptEnabled"),
                Sl("Strength", "EyeAdaptStrength", 0, 1),
            });

            AddGroup("Depth fog", new[]
            {
                Sl("Strength", "FogStrength", 0, 1),
                Sl("Distance", "FogStart", 0, 0.9),
                Sl("Brightness", "FogBrightness", 0, 1),
            });

            AddGroup("Deband", new[]
            {
                Tg("Enable deband", "DebandEnabled"),
                Sl("Strength", "DebandStrength", 0, 1),
            });

            AddGroup("Debugging", new[]
            {
                Cb("View", "DebugViewIndex", vm.DebugViewNames),
                Cb("AI model", "AiQualityIndex", vm.AiQualityNames),
                Cb("Render resolution", "RenderScaleIndex", vm.RenderScaleNames),
            });
        }

        private void AddGroup(string header, Row[] rows)
        {
            string gate = "";
            foreach (var row in rows)
            {
                if (row.Kind == RowKind.Toggle && row.Label.StartsWith("Enable", StringComparison.Ordinal))
                {
                    gate = row.Path;
                    break;
                }
            }
            var panel = new StackPanel { Margin = new Thickness(8, 6, 8, 8) };
            foreach (var row in rows)
                panel.Children.Add(BuildRow(row, gate));
            var expander = new System.Windows.Controls.Expander
            {
                Header = header,
                Content = panel,
                Margin = new Thickness(0, 4, 0, 0),
                IsExpanded = false,
            };
            GroupsHost.Children.Add(expander);
        }

        private FrameworkElement BuildRow(Row row, string gate)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

            var label = new TextBlock
            {
                Text = row.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            if (row.Kind == RowKind.Toggle)
            {
                var toggle = new Wpf.Ui.Controls.ToggleSwitch { HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(toggle, row.Label);
                toggle.SetBinding(Wpf.Ui.Controls.ToggleSwitch.IsCheckedProperty, new Binding(row.Path) { Mode = BindingMode.TwoWay });
                if (gate.Length > 0 && row.Path != gate)
                {
                    toggle.Tag = gate;
                    toggle.Checked += OnGatedToggleChecked;
                }
                Grid.SetColumn(toggle, 1);
                Grid.SetColumnSpan(toggle, 2);
                grid.Children.Add(toggle);
            }
            else if (row.Kind == RowKind.Combo)
            {
                var combo = new ComboBox { ItemsSource = row.Items };
                combo.SetBinding(ComboBox.SelectedIndexProperty, new Binding(row.Path) { Mode = BindingMode.TwoWay });
                if (gate.Length > 0)
                {
                    combo.Tag = gate;
                    combo.SelectionChanged += OnGatedComboChanged;
                }
                Grid.SetColumn(combo, 1);
                Grid.SetColumnSpan(combo, 2);
                grid.Children.Add(combo);
            }
            else
            {
                var slider = new Slider
                {
                    Minimum = row.Min,
                    Maximum = row.Max,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsSnapToTickEnabled = false,
                };
                slider.SetBinding(Slider.ValueProperty, new Binding(row.Path) { Mode = BindingMode.TwoWay });
                if (gate.Length > 0)
                {
                    slider.Tag = gate;
                    slider.ValueChanged += OnGatedSliderChanged;
                }
                Grid.SetColumn(slider, 1);
                grid.Children.Add(slider);

                var value = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                value.SetBinding(TextBlock.TextProperty, new Binding(row.Path) { Mode = BindingMode.OneWay, StringFormat = row.Format });
                Grid.SetColumn(value, 2);
                grid.Children.Add(value);
            }
            return grid;
        }
    }
}
