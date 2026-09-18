using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels.Settings;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.RiShade;

public partial class RiShadeWindow : WpfUiWindow
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
		public int Decimals = 3;
		public string[]? Items;
	}

	private sealed class Group
	{
		public string Name { get; set; } = "";
		public string Hint = "";
		public string Gate = "";
		public Row[] Rows = Array.Empty<Row>();
		public FrameworkElement? Panel;
	}

	private static readonly Brush FallbackBrush = new SolidColorBrush(Color.FromArgb(160, 128, 128, 128));

	private static Row Tg(string label, string path) => new Row { Label = label, Path = path, Kind = RowKind.Toggle };

	private static Row Sl(string label, string path, double min, double max, int decimals = 3) => new Row { Label = label, Path = path, Kind = RowKind.Slider, Min = min, Max = max, Decimals = decimals };

	private static Row Cb(string label, string path, string[] items) => new Row { Label = label, Path = path, Kind = RowKind.Combo, Items = items };

	private readonly RiShadeViewModel _vm;

	private readonly List<Group> _groups = new List<Group>();

	private Group? _selected;

	static RiShadeWindow()
	{
		FallbackBrush.Freeze();
	}

	public RiShadeWindow()
	{
		InitializeComponent();
		_vm = new RiShadeViewModel();
		DataContext = _vm;
		BuildGroups();
		GroupList.ItemsSource = _groups;
		Closed += RiShadeWindow_Closed;
		GroupList.SelectedIndex = 0;
	}

	private void BuildGroups()
	{
		Add("Colour grading", "Brightness, gamma, hue and per channel colour balance.", new[]
		{
			Tg("Enable grading", "GradeEnabled"),
			Sl("Brightness", "Brightness", -1, 1),
			Sl("Gamma", "Gamma", 0.2, 3),
			Sl("Hue shift", "HueShift", -180, 180, 0),
			Cb("Temperature", "ColorTempIndex", _vm.ColorTempNames),
			Sl("Custom temp red", "TempCustomR", 0, 2),
			Sl("Custom temp green", "TempCustomG", 0, 2),
			Sl("Custom temp blue", "TempCustomB", 0, 2),
			Sl("Balance red", "BalanceR", 0, 2),
			Sl("Balance green", "BalanceG", 0, 2),
			Sl("Balance blue", "BalanceB", 0, 2),
			Sl("Lift red", "LiftR", -0.2, 0.2),
			Sl("Lift green", "LiftG", -0.2, 0.2),
			Sl("Lift blue", "LiftB", -0.2, 0.2),
			Sl("Gain red", "GainR", 0.5, 2),
			Sl("Gain green", "GainG", 0.5, 2),
			Sl("Gain blue", "GainB", 0.5, 2),
		});
		Add("Tonemapping", "Maps bright scenes into a filmic range.", new[]
		{
			Tg("Enable tonemap", "TonemapEnabled"),
			Cb("Mode", "TonemapModeIndex", _vm.TonemapNames),
			Sl("Exposure", "TonemapExposure", 0.1, 5),
			Sl("White point", "TonemapWhitepoint", 1, 16),
		});
		Add("Vignette", "Darkens the edges of the screen.", new[]
		{
			Tg("Enable vignette", "VignetteEnabled"),
			Sl("Strength", "VignetteStrength", 0, 4),
			Sl("Feather", "VignetteFeather", 0.1, 4),
			Sl("Centre X", "VignetteCenterX", -0.5, 0.5),
			Sl("Centre Y", "VignetteCenterY", -0.5, 0.5),
		});
		Add("Sharpen", "Adds detail back after scaling and anti aliasing.", new[]
		{
			Tg("Enable sharpen", "SharpenEnabled"),
			Sl("Strength", "SharpenStrength", 0, 5),
			Sl("Radius", "SharpenRadius", 0.5, 4),
			Sl("Clamp", "SharpenClamp", 0.01, 1),
		});
		Add("Bloom", "Glow around bright lights and surfaces.", new[]
		{
			Tg("Enable bloom", "BloomEnabled"),
			Sl("Strength", "BloomStrength", 0, 5),
			Sl("Threshold", "BloomThreshold", 0, 1),
			Sl("Radius", "BloomRadius", 0.5, 4),
			Sl("Passes", "BloomPasses", 1, 6, 0),
			Sl("Tint red", "BloomTintR", 0, 2),
			Sl("Tint green", "BloomTintG", 0, 2),
			Sl("Tint blue", "BloomTintB", 0, 2),
		});
		Add("Chromatic aberration", "Splits colours slightly like a camera lens.", new[]
		{
			Tg("Enable chroma", "ChromaEnabled"),
			Sl("Strength", "ChromaStrength", 0, 0.02, 4),
			Tg("Radial", "ChromaRadial"),
		});
		Add("Film grain", "Animated noise for a filmic look.", new[]
		{
			Tg("Enable grain", "GrainEnabled"),
			Sl("Strength", "GrainStrength", 0, 0.3),
			Sl("Size", "GrainSize", 0.1, 5),
			Tg("Colored", "GrainColored"),
		});
		Add("Depth of field", "Blurs what is far from the focus point. Uses the depth estimate.", new[]
		{
			Tg("Enable DOF", "DofEnabled"),
			Sl("Blur strength", "DofStrength", 0, 1),
			Sl("Focus radius", "DofFocusRange", 0, 0.5),
			Sl("Feather", "DofFeather", 0, 1),
		});
		Add("Ambient occlusion", "Soft shadows in corners and creases. Uses the depth estimate.", new[]
		{
			Tg("Enable AO", "AoEnabled"),
			Sl("Strength", "AoStrength", 0, 3),
			Sl("Radius", "AoRadius", 0.001, 0.1, 4),
			Cb("Samples", "AoSamplesIndex", _vm.AoSampleNames),
		});
		Add("Reflections", "Screen space reflections. Still experimental and can flicker.", new[]
		{
			Tg("Enable reflections", "SsrEnabled"),
			Sl("Intensity", "SsrIntensity", 0, 1),
			Sl("Glossiness", "SsrGlossiness", 0, 1),
			Sl("Wetness", "SsrReflectivity", 0, 1),
			Sl("Reach", "SsrDistance", 0, 1),
			Sl("Surface sheen", "SsrSheen", 0, 1),
		});
		Add("Clarity", "Local contrast boost.", new[]
		{
			Sl("Strength", "ClarityStrength", 0, 1),
		});
		Add("Global illumination", "Bounced light between nearby surfaces. Uses the depth estimate.", new[]
		{
			Sl("Strength", "GiStrength", 0, 1),
			Sl("Radius", "GiRadius", 0, 1),
		});
		Add("Ambient light", "Lifts dark areas softly.", new[]
		{
			Sl("Strength", "AmbientStrength", 0, 1),
		});
		Add("Auto exposure", "Adapts brightness to the scene like an eye would.", new[]
		{
			Tg("Enable auto exposure", "EyeAdaptEnabled"),
			Sl("Strength", "EyeAdaptStrength", 0, 1),
		});
		Add("Depth fog", "Fades distant geometry into fog. Uses the depth estimate.", new[]
		{
			Sl("Strength", "FogStrength", 0, 1),
			Sl("Distance", "FogStart", 0, 0.9),
			Sl("Brightness", "FogBrightness", 0, 1),
		});
		Add("Deband", "Smooths colour banding in gradients and skies.", new[]
		{
			Tg("Enable deband", "DebandEnabled"),
			Sl("Strength", "DebandStrength", 0, 1),
		});
		Add("Performance", "Trade quality for frames. Lower render resolution helps most on weaker GPUs.", new[]
		{
			Cb("Render resolution", "RenderScaleIndex", _vm.RenderScaleNames),
			Cb("Depth model", "AiQualityIndex", _vm.AiQualityNames),
			Tg("Performance mode", "PerfMode"),
		});
		Add("Debugging", "Shows what the depth estimate sees.", new[]
		{
			Cb("View", "DebugViewIndex", _vm.DebugViewNames),
		});
	}

	private void Add(string name, string hint, Row[] rows)
	{
		string gate = "";
		foreach (Row row in rows)
		{
			if (row.Kind == RowKind.Toggle && row.Label.StartsWith("Enable", StringComparison.Ordinal))
			{
				gate = row.Path;
				break;
			}
		}
		Group group = new Group { Name = name, Hint = hint, Gate = gate, Rows = rows };
		_groups.Add(group);
	}

	private StackPanel BuildPanel(Group group)
	{
		StackPanel panel = new StackPanel();
		foreach (Row row in group.Rows)
		{
			if (row.Path == group.Gate)
				continue;
			panel.Children.Add(BuildRow(row, group.Gate));
		}
		if (panel.Children.Count == 0)
		{
			panel.Children.Add(new TextBlock
			{
				Text = "Use the switch above to turn this effect on or off.",
				Foreground = TryFindResource("TextFillColorSecondaryBrush") as Brush ?? FallbackBrush,
			});
		}
		return panel;
	}

	private Grid BuildRow(Row row, string gate)
	{
		Grid grid = new Grid { Margin = new Thickness(0, 5, 0, 5) };
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
		grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

		TextBlock label = new TextBlock
		{
			Text = row.Label,
			VerticalAlignment = VerticalAlignment.Center,
			TextTrimming = TextTrimming.CharacterEllipsis,
		};
		grid.Children.Add(label);

		Wpf.Ui.Controls.Button reset = new Wpf.Ui.Controls.Button
		{
			Icon = SymbolRegular.ArrowReset24,
			Appearance = ControlAppearance.Transparent,
			Padding = new Thickness(4),
			Width = 30,
			Height = 30,
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			ToolTip = "Reset to default",
			Tag = row.Path,
		};
		reset.Click += Reset_Click;
		Grid.SetColumn(reset, 3);

		if (row.Kind == RowKind.Toggle)
		{
			Wpf.Ui.Controls.ToggleSwitch toggle = new Wpf.Ui.Controls.ToggleSwitch { HorizontalAlignment = HorizontalAlignment.Left, Tag = gate };
			AutomationProperties.SetName(toggle, row.Label);
			toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(row.Path) { Mode = BindingMode.TwoWay });
			toggle.Checked += Gated_Checked;
			Grid.SetColumn(toggle, 1);
			Grid.SetColumnSpan(toggle, 2);
			grid.Children.Add(toggle);
		}
		else if (row.Kind == RowKind.Combo)
		{
			ComboBox combo = new ComboBox { ItemsSource = row.Items, Tag = gate };
			AutomationProperties.SetName(combo, row.Label);
			combo.SetBinding(Selector.SelectedIndexProperty, new Binding(row.Path) { Mode = BindingMode.TwoWay });
			combo.SelectionChanged += Gated_SelectionChanged;
			Grid.SetColumn(combo, 1);
			Grid.SetColumnSpan(combo, 2);
			grid.Children.Add(combo);
		}
		else
		{
			Slider slider = new Slider
			{
				Minimum = row.Min,
				Maximum = row.Max,
				VerticalAlignment = VerticalAlignment.Center,
				IsMoveToPointEnabled = true,
				IsSnapToTickEnabled = row.Decimals == 0,
				TickFrequency = row.Decimals == 0 ? 1 : 0,
				SmallChange = (row.Max - row.Min) / 100.0,
				LargeChange = (row.Max - row.Min) / 10.0,
				Margin = new Thickness(0, 0, 10, 0),
				Tag = gate,
			};
			AutomationProperties.SetName(slider, row.Label);
			slider.SetBinding(RangeBase.ValueProperty, new Binding(row.Path) { Mode = BindingMode.TwoWay });
			slider.ValueChanged += Gated_ValueChanged;
			Grid.SetColumn(slider, 1);
			grid.Children.Add(slider);

			Wpf.Ui.Controls.TextBox box = new Wpf.Ui.Controls.TextBox
			{
				VerticalAlignment = VerticalAlignment.Center,
				Padding = new Thickness(6, 4, 6, 4),
				TextAlignment = TextAlignment.Right,
				ClearButtonEnabled = false,
				Tag = row,
			};
			box.SetBinding(System.Windows.Controls.TextBox.TextProperty, new Binding(row.Path) { Mode = BindingMode.OneWay, StringFormat = Format(row.Decimals) });
			box.LostFocus += ValueBox_LostFocus;
			box.KeyDown += ValueBox_KeyDown;
			Grid.SetColumn(box, 2);
			grid.Children.Add(box);
		}
		grid.Children.Add(reset);
		return grid;
	}

	private static string Format(int decimals) => decimals <= 0 ? "0" : "0." + new string('0', decimals);

	private static bool IsUserDriven(FrameworkElement fe) => fe.IsKeyboardFocusWithin || fe.IsMouseCaptureWithin || fe.IsMouseOver;

	private void EnableGate(object sender)
	{
		if (sender is not FrameworkElement fe || fe.Tag is not string gate || gate.Length == 0 || !IsUserDriven(fe))
			return;
		var prop = typeof(RiShadeViewModel).GetProperty(gate);
		if (prop == null || prop.PropertyType != typeof(bool) || prop.GetValue(_vm) is true)
			return;
		prop.SetValue(_vm, true);
	}

	private void Gated_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => EnableGate(sender);

	private void Gated_SelectionChanged(object sender, SelectionChangedEventArgs e) => EnableGate(sender);

	private void Gated_Checked(object sender, RoutedEventArgs e) => EnableGate(sender);

	private void Reset_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement fe && fe.Tag is string path)
			_vm.Reset(path);
	}

	private void CommitValueBox(Wpf.Ui.Controls.TextBox box)
	{
		if (box.Tag is not Row row)
			return;
		if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double value) && !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
		{
			BindingOperations.GetBindingExpression(box, System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
			return;
		}
		value = Math.Clamp(value, row.Min, row.Max);
		if (row.Decimals == 0)
			value = Math.Round(value);
		var prop = typeof(RiShadeViewModel).GetProperty(row.Path);
		if (prop == null)
			return;
		prop.SetValue(_vm, value);
		if (_selected != null && _selected.Gate.Length > 0)
		{
			var gateProp = typeof(RiShadeViewModel).GetProperty(_selected.Gate);
			if (gateProp != null && gateProp.GetValue(_vm) is false)
				gateProp.SetValue(_vm, true);
		}
		BindingOperations.GetBindingExpression(box, System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
	}

	private void ValueBox_LostFocus(object sender, RoutedEventArgs e)
	{
		if (sender is Wpf.Ui.Controls.TextBox box)
			CommitValueBox(box);
	}

	private void ValueBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (sender is not Wpf.Ui.Controls.TextBox box)
			return;
		if (e.Key == Key.Enter)
		{
			CommitValueBox(box);
			e.Handled = true;
		}
		else if (e.Key == Key.Escape)
		{
			BindingOperations.GetBindingExpression(box, System.Windows.Controls.TextBox.TextProperty)?.UpdateTarget();
			e.Handled = true;
		}
	}

	private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (GroupList.SelectedItem is not Group group)
			return;
		_selected = group;
		group.Panel ??= BuildPanel(group);
		GroupTitle.Text = group.Name;
		GroupHint.Text = group.Hint;
		GroupHost.Content = group.Panel;
		BindingOperations.ClearBinding(GroupToggle, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty);
		if (group.Gate.Length > 0)
		{
			GroupToggle.Visibility = Visibility.Visible;
			GroupToggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(group.Gate) { Mode = BindingMode.TwoWay });
		}
		else
		{
			GroupToggle.Visibility = Visibility.Collapsed;
		}
	}

	private void GroupReset_Click(object sender, RoutedEventArgs e)
	{
		if (_selected == null)
			return;
		foreach (Row row in _selected.Rows)
			_vm.Reset(row.Path);
	}

	private void ResetAll_Click(object sender, RoutedEventArgs e)
	{
		_vm.ResetAll();
	}

	private void RiShadeWindow_Closed(object? sender, EventArgs e)
	{
		Closed -= RiShadeWindow_Closed;
		GroupList.SelectionChanged -= GroupList_SelectionChanged;
		foreach (Group group in _groups)
		{
			if (group.Panel is StackPanel panel)
				Detach(panel);
		}
	}

	private void Detach(StackPanel panel)
	{
		foreach (object child in panel.Children)
		{
			if (child is not Grid grid)
				continue;
			foreach (object item in grid.Children)
			{
				switch (item)
				{
					case Wpf.Ui.Controls.Button button:
						button.Click -= Reset_Click;
						break;
					case Wpf.Ui.Controls.ToggleSwitch toggle:
						toggle.Checked -= Gated_Checked;
						break;
					case ComboBox combo:
						combo.SelectionChanged -= Gated_SelectionChanged;
						break;
					case Slider slider:
						slider.ValueChanged -= Gated_ValueChanged;
						break;
					case Wpf.Ui.Controls.TextBox box:
						box.LostFocus -= ValueBox_LostFocus;
						box.KeyDown -= ValueBox_KeyDown;
						break;
				}
			}
		}
	}
}
