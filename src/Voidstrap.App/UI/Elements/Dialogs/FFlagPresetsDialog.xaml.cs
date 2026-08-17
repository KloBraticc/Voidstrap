using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Voidstrap.UI.Elements.Base;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class FFlagPresetsDialog : WpfUiWindow{
	private static readonly Dictionary<string, string[]> PresetCategories = new Dictionary<string, string[]>
	{
		{
			"Boolean",
			new string[2] { "True", "False" }
		},
		{
			"Basic Numbers",
			new string[5] { "0", "1", "10", "100", "1000" }
		},
		{
			"Large Numbers",
			new string[4] { "10000", "100000", "1000000", "2147483647" }
		},
		{
			"Percentages",
			new string[5] { "0", "25", "50", "75", "100" }
		},
		{
			"FPS Values",
			new string[6] { "30", "60", "120", "144", "240", "360" }
		},
		{
			"Quality Levels",
			new string[8] { "0", "1", "2", "3", "4", "5", "10", "21" }
		},
		{
			"Special Values",
			new string[3] { "-1", "null", "\"\"" }
		},
		{
			"Memory Values",
			new string[5] { "1024", "2048", "4096", "8192", "16384" }
		}
	};

	public string? SelectedValue { get; private set; }

	public FFlagPresetsDialog()
	{
		InitializeComponent();
		LoadPresetCategories();
		base.Closed += OnClosed;
	}

	private void LoadPresetCategories()
	{
		foreach (KeyValuePair<string, string[]> presetCategory in PresetCategories)
		{
			Expander expander = new Expander
			{
				Header = presetCategory.Key,
				Margin = new Thickness(0.0, 5.0, 0.0, 5.0),
				IsExpanded = (presetCategory.Key == "Boolean")
			};
			StackPanel stackPanel = new StackPanel();
			string[] value = presetCategory.Value;
			foreach (string value2 in value)
			{
				Button button = new Button
				{
					Content = value2,
					Margin = new Thickness(2.0, 2.0, 2.0, 2.0),
					Padding = new Thickness(8.0, 4.0, 8.0, 4.0),
					HorizontalAlignment = HorizontalAlignment.Stretch,
					Background = new SolidColorBrush(Colors.Transparent),
					BorderBrush = new SolidColorBrush(Colors.Gray),
					BorderThickness = new Thickness(1.0, 1.0, 1.0, 1.0)
				};
				button.Tag = value2;
				button.Click += OnPresetClick;
				button.MouseEnter += OnPresetMouseEnter;
				button.MouseLeave += OnPresetMouseLeave;
				stackPanel.Children.Add(button);
			}
			expander.Content = stackPanel;
			PresetStackPanel.Children.Add(expander);
		}
	}

	private void OnPresetClick(object sender, RoutedEventArgs e)
	{
		if (sender is not Button { Tag: string value })
			return;
		SelectedValue = value;
		base.DialogResult = true;
		Close();
	}

	private static void OnPresetMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (sender is Button button)
			button.Background = new SolidColorBrush(Color.FromArgb(50, 100, 149, 237));
	}

	private static void OnPresetMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (sender is Button button)
			button.Background = new SolidColorBrush(Colors.Transparent);
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		foreach (Button button in PresetStackPanel.Children.OfType<Expander>().SelectMany(expander => (expander.Content as StackPanel)?.Children.OfType<Button>() ?? Enumerable.Empty<Button>()))
		{
			button.Click -= OnPresetClick;
			button.MouseEnter -= OnPresetMouseEnter;
			button.MouseLeave -= OnPresetMouseLeave;
		}
		base.Closed -= OnClosed;
	}

	private void CancelButton_Click(object sender, RoutedEventArgs e)
	{
		base.DialogResult = false;
		Close();
	}
}
