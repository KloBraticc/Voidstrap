using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginUiRenderer
{
	private const int MaxRenderedNodes = 15000;

	private static readonly Dictionary<Wpf.Ui.Controls.TextBox, (string Handler, string Sent)> PendingChangeInputs = [];

	public static UIElement? BuildTree(JsonNode? node, string? pluginDirectory = null)
	{
		int rendered = 0;
		return BuildNode(node, pluginDirectory, ref rendered);
	}

	public static void FlushPendingInputs()
	{
		foreach (KeyValuePair<Wpf.Ui.Controls.TextBox, (string Handler, string Sent)> entry in PendingChangeInputs.ToArray())
		{
			if (!entry.Key.IsLoaded)
			{
				PendingChangeInputs.Remove(entry.Key);
				continue;
			}
			string current = entry.Key.Text ?? "";
			if (!string.Equals(current, entry.Value.Sent, StringComparison.Ordinal))
			{
				PendingChangeInputs[entry.Key] = (entry.Value.Handler, current);
				PluginHost.InvokeHandler(entry.Value.Handler, JsonValue.Create(current));
			}
		}
	}

	private static UIElement? BuildNode(JsonNode? node, string? pluginDirectory, ref int rendered)
	{
		if (node == null || ++rendered > MaxRenderedNodes)
			return null;
		if (node is JsonValue value)
			return MakeText(value.ToString(), new JsonObject());
		if (node is JsonArray list)
		{
			StackPanel stack = new();
			foreach (JsonNode? child in list)
				if (BuildNode(child, pluginDirectory, ref rendered) is UIElement item)
					stack.Children.Add(item);
			return stack;
		}
		if (node is not JsonObject element)
			return null;
		string type = element["type"]?.ToString() ?? "Box";
		JsonObject props = element["props"] as JsonObject ?? new JsonObject();
		JsonArray children = element["children"] as JsonArray ?? new JsonArray();
		if (props["visible"] is JsonValue vis && vis.TryGetValue(out bool isVisible) && !isVisible)
			return null;
		UIElement? built = type switch
		{
			"Text" => MakeText(ReadLabel(props, children, "text"), props),
			"Badge" => MakeBadge(ReadLabel(props, children, "text", "label"), props),
			"Icon" => MakeIcon(props),
			"ColorSwatch" => MakeColorSwatch(props),
			"Button" => MakeButton(props, children),
			"Toggle" => MakeToggle(props, children),
			"CheckBox" => MakeCheckBox(props, children),
			"TextInput" => MakeTextInput(props),
			"NumberInput" or "Slider" => MakeSlider(props),
			"Select" => MakeSelect(props, children),
			"ProgressBar" => MakeProgressBar(props),
			"InfoBar" => MakeInfoBar(props, children),
			"Divider" => new Separator { Margin = new Thickness(0, 6, 0, 6) },
			"Spacer" => MakeSpacer(props),
			"Image" => MakeImage(props, pluginDirectory),
			"Card" => MakeCard(children, props, pluginDirectory, ref rendered),
			"Expander" => MakeExpander(children, props, pluginDirectory, ref rendered),
			"Tabs" => MakeTabs(children, props, pluginDirectory, ref rendered),
			"Table" => MakeTable(props),
			"Grid" => MakeGrid(children, props, pluginDirectory, ref rendered),
			"Scroll" => MakeScroll(children, props, pluginDirectory, ref rendered),
			"Wrap" => MakeWrap(children, props, pluginDirectory, ref rendered),
			_ => BuildChildren(children, props, pluginDirectory, ref rendered)
		};
		if (built is FrameworkElement fe)
			ApplyCommonProps(fe, props);
		return built;
	}

	private static Panel BuildChildren(JsonArray children, JsonObject props, string? pluginDirectory, ref int rendered)
	{
		bool row = string.Equals(props["direction"]?.ToString(), "row", StringComparison.OrdinalIgnoreCase);
		bool wrap = props["wrap"] is JsonValue w && w.TryGetValue(out bool shouldWrap) && shouldWrap;
		double gap = ReadDouble(props, "gap", 0);
		Panel panel = (row && wrap) ? new WrapPanel { Orientation = Orientation.Horizontal }
			: row ? new StackPanel { Orientation = Orientation.Horizontal }
			: new StackPanel { Orientation = Orientation.Vertical };
		int index = 0;
		foreach (JsonNode? child in children)
		{
			if (BuildNode(child, pluginDirectory, ref rendered) is UIElement built)
			{
				if (gap > 0 && index > 0 && built is FrameworkElement fe)
				{
					Thickness m = fe.Margin;
					fe.Margin = row
						? new Thickness(m.Left + gap, m.Top, m.Right, m.Bottom)
						: new Thickness(m.Left, m.Top + gap, m.Right, m.Bottom);
				}
				panel.Children.Add(built);
				index++;
			}
		}
		return panel;
	}

	private static Panel MakeWrap(JsonArray children, JsonObject props, string? pluginDirectory, ref int rendered)
	{
		WrapPanel wrap = new()
		{
			Orientation = string.Equals(props["direction"]?.ToString(), "column", StringComparison.OrdinalIgnoreCase)
				? Orientation.Vertical
				: Orientation.Horizontal
		};
		if (ReadDouble(props, "itemWidth", double.NaN) is double iw && !double.IsNaN(iw) && iw > 0)
			wrap.ItemWidth = iw;
		if (ReadDouble(props, "itemHeight", double.NaN) is double ih && !double.IsNaN(ih) && ih > 0)
			wrap.ItemHeight = ih;
		foreach (JsonNode? child in children)
			if (BuildNode(child, pluginDirectory, ref rendered) is UIElement built)
				wrap.Children.Add(built);
		return wrap;
	}

	private static UIElement MakeScroll(JsonArray children, JsonObject props, string? pluginDirectory, ref int rendered)
	{
		ScrollViewer viewer = new()
		{
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
			HorizontalScrollBarVisibility = props["horizontal"] is JsonValue h && h.TryGetValue(out bool horiz) && horiz
				? ScrollBarVisibility.Auto
				: ScrollBarVisibility.Disabled,
			Content = BuildChildren(children, props, pluginDirectory, ref rendered)
		};
		if (ReadDouble(props, "maxHeight", double.NaN) is double mh && !double.IsNaN(mh) && mh > 0)
			viewer.MaxHeight = mh;
		return viewer;
	}

	private static Grid MakeGrid(JsonArray children, JsonObject props, string? pluginDirectory, ref int rendered)
	{
		Grid grid = new();
		int autoColumns = 0;
		if (props["columns"] is JsonValue colVal && colVal.TryGetValue(out int colCount) && colCount > 0)
		{
			autoColumns = Math.Clamp(colCount, 1, 16);
			for (int i = 0; i < autoColumns; i++)
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		}
		else if (props["columns"] is JsonArray colArr && colArr.Count > 0)
		{
			autoColumns = colArr.Count;
			foreach (JsonNode? col in colArr)
				grid.ColumnDefinitions.Add(new ColumnDefinition { Width = ParseGridLength(col) });
		}
		if (props["rows"] is JsonValue rowVal && rowVal.TryGetValue(out int rowCount) && rowCount > 0)
		{
			for (int i = 0; i < Math.Clamp(rowCount, 1, 32); i++)
				grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		}
		else if (props["rows"] is JsonArray rowArr && rowArr.Count > 0)
		{
			foreach (JsonNode? row in rowArr)
				grid.RowDefinitions.Add(new RowDefinition { Height = ParseGridLength(row) });
		}
		double gap = ReadDouble(props, "gap", 6);
		int cellIndex = 0;
		foreach (JsonNode? child in children)
		{
			if (BuildNode(child, pluginDirectory, ref rendered) is not UIElement built)
				continue;
			JsonObject? childProps = (child as JsonObject)?["props"] as JsonObject;
			int explicitCol = ReadInt(childProps, "column", ReadInt(childProps, "gridColumn", -1));
			int explicitRow = ReadInt(childProps, "row", ReadInt(childProps, "gridRow", -1));
			if (explicitCol < 0 && explicitRow < 0 && autoColumns > 0)
			{
				explicitCol = cellIndex % autoColumns;
				explicitRow = cellIndex / autoColumns;
			}
			if (explicitRow >= 0)
			{
				while (grid.RowDefinitions.Count <= explicitRow)
					grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
				Grid.SetRow(built, explicitRow);
			}
			if (explicitCol >= 0)
			{
				while (grid.ColumnDefinitions.Count <= explicitCol)
					grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
				Grid.SetColumn(built, explicitCol);
			}
			if (ReadInt(childProps, "rowSpan", 1) is int rs && rs > 1)
				Grid.SetRowSpan(built, rs);
			if (ReadInt(childProps, "columnSpan", 1) is int cs && cs > 1)
				Grid.SetColumnSpan(built, cs);
			if (gap > 0 && built is FrameworkElement fe && explicitCol > 0)
				fe.Margin = new Thickness(fe.Margin.Left + gap, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
			grid.Children.Add(built);
			cellIndex++;
		}
		return grid;
	}

	private static GridLength ParseGridLength(JsonNode? spec)
	{
		if (spec is JsonValue val && val.TryGetValue(out double pixels) && pixels > 0)
			return new GridLength(pixels, GridUnitType.Pixel);
		string text = spec?.ToString().Trim() ?? "*";
		if (text.Equals("auto", StringComparison.OrdinalIgnoreCase))
			return GridLength.Auto;
		if (text.EndsWith('*'))
		{
			string prefix = text[..^1].Trim();
			double weight = prefix.Length == 0 ? 1 : (double.TryParse(prefix, out double w) && w > 0 ? w : 1);
			return new GridLength(weight, GridUnitType.Star);
		}
		return double.TryParse(text, out double px) && px >= 0 ? new GridLength(px, GridUnitType.Pixel) : new GridLength(1, GridUnitType.Star);
	}

	private static Border MakeCard(JsonArray children, JsonObject props, string? pluginDirectory, ref int rendered)
	{
		StackPanel stack = new();
		string title = props["title"]?.ToString() ?? "";
		string subtitle = props["subtitle"]?.ToString() ?? "";
		if (title.Length > 0 || subtitle.Length > 0)
		{
			StackPanel header = new() { Margin = new Thickness(0, 0, 0, 8) };
			if (title.Length > 0)
				header.Children.Add(MakeText(title, new JsonObject { ["bold"] = true, ["size"] = 14 }));
			if (subtitle.Length > 0)
				header.Children.Add(MakeText(subtitle, new JsonObject { ["dim"] = true, ["size"] = 12 }));
			stack.Children.Add(header);
		}
		stack.Children.Add(BuildChildren(children, props, pluginDirectory, ref rendered));
		Border card = new()
		{
			Padding = new Thickness(12),
			Margin = new Thickness(0, 0, 0, 8),
			CornerRadius = new CornerRadius(6),
			BorderThickness = new Thickness(1),
			Child = stack
		};
		card.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
		card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
		return card;
	}

	private static Expander MakeExpander(JsonArray children, JsonObject props, string? pluginDirectory, ref int rendered)
	{
		string title = ReadLabel(props, new JsonArray(), "title", "header", "label");
		bool expanded = props["expanded"] is not JsonValue exp || !exp.TryGetValue(out bool isExp) || isExp;
		Expander expander = new()
		{
			Header = title.Length > 0 ? title : "Details",
			IsExpanded = expanded,
			Margin = new Thickness(0, 0, 0, 8),
			Content = BuildChildren(children, props, pluginDirectory, ref rendered)
		};
		return expander;
	}

	private static TabControl MakeTabs(JsonArray children, JsonObject props, string? pluginDirectory, ref int rendered)
	{
		TabControl tabs = new() { Margin = new Thickness(0, 0, 0, 8) };
		int index = 0;
		foreach (JsonNode? child in children)
		{
			if (child is not JsonObject tabObj)
				continue;
			JsonObject tabProps = tabObj["props"] as JsonObject ?? new JsonObject();
			JsonArray tabChildren = tabObj["children"] as JsonArray ?? new JsonArray();
			string header = tabProps["title"]?.ToString() ?? tabProps["label"]?.ToString() ?? ("Tab " + (++index));
			TabItem item = new()
			{
				Header = header,
				Content = new Border
				{
					Padding = new Thickness(4, 10, 4, 4),
					Child = BuildChildren(tabChildren, tabProps, pluginDirectory, ref rendered)
				}
			};
			tabs.Items.Add(item);
		}
		if (ReadInt(props, "selectedIndex", 0) is int sel && sel >= 0 && sel < tabs.Items.Count)
			tabs.SelectedIndex = sel;
		return tabs;
	}

	private static Border MakeTable(JsonObject props)
	{
		JsonArray columns = props["columns"] as JsonArray ?? new JsonArray();
		JsonArray rows = props["rows"] as JsonArray ?? new JsonArray();
		int colCount = Math.Max(1, columns.Count > 0 ? columns.Count : (rows.FirstOrDefault() as JsonArray)?.Count ?? 1);
		Grid grid = new();
		for (int c = 0; c < colCount; c++)
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		int r = 0;
		if (columns.Count > 0)
		{
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			for (int c = 0; c < colCount; c++)
			{
				Border cell = new() { Padding = new Thickness(8, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
				cell.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
				cell.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
				cell.Child = MakeText(c < columns.Count ? columns[c]?.ToString() ?? "" : "", new JsonObject { ["bold"] = true, ["size"] = 12 });
				Grid.SetRow(cell, r);
				Grid.SetColumn(cell, c);
				grid.Children.Add(cell);
			}
			r++;
		}
		foreach (JsonNode? rowNode in rows)
		{
			if (rowNode is not JsonArray rowArr)
				continue;
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			for (int c = 0; c < colCount; c++)
			{
				Border cell = new() { Padding = new Thickness(8, 5, 8, 5), BorderThickness = new Thickness(0, 0, 0, 1) };
				cell.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
				cell.Child = MakeText(c < rowArr.Count ? rowArr[c]?.ToString() ?? "" : "", new JsonObject { ["size"] = 12 });
				Grid.SetRow(cell, r);
				Grid.SetColumn(cell, c);
				grid.Children.Add(cell);
			}
			r++;
		}
		Border outer = new()
		{
			Margin = new Thickness(0, 0, 0, 8),
			CornerRadius = new CornerRadius(6),
			BorderThickness = new Thickness(1),
			Child = grid
		};
		outer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
		return outer;
	}

	public static System.Windows.Controls.TextBlock MakeText(string text, JsonObject props)
	{
		System.Windows.Controls.TextBlock block = new()
		{
			Text = text,
			TextWrapping = props["wrap"] is JsonValue wrapVal && wrapVal.TryGetValue(out bool wrap) && !wrap ? TextWrapping.NoWrap : TextWrapping.Wrap,
			Margin = new Thickness(0, 0, 6, 6)
		};
		if (props["bold"] is JsonValue bold && bold.TryGetValue(out bool isBold) && isBold)
			block.FontWeight = FontWeights.SemiBold;
		if (props["italic"] is JsonValue italic && italic.TryGetValue(out bool isItalic) && isItalic)
			block.FontStyle = FontStyles.Italic;
		if (props["mono"] is JsonValue mono && mono.TryGetValue(out bool isMono) && isMono)
			block.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, monospace");
		if (props["size"] is JsonValue size && size.TryGetValue(out double points))
			block.FontSize = Math.Clamp(points, 8, 64);
		string align = props["align"]?.ToString() ?? "";
		if (align.Equals("center", StringComparison.OrdinalIgnoreCase))
			block.TextAlignment = TextAlignment.Center;
		else if (align.Equals("right", StringComparison.OrdinalIgnoreCase))
			block.TextAlignment = TextAlignment.Right;
		string? color = props["color"]?.ToString() ?? props["foreground"]?.ToString();
		if (!string.IsNullOrWhiteSpace(color) && TryResolveBrush(color, out Brush? customBrush) && customBrush != null)
		{
			block.Foreground = customBrush;
		}
		else
		{
			block.SetResourceReference(
				System.Windows.Controls.TextBlock.ForegroundProperty,
				props["dim"] is JsonValue dim && dim.TryGetValue(out bool isDim) && isDim
					? "TextFillColorTertiaryBrush"
					: "TextFillColorPrimaryBrush");
		}
		return block;
	}

	private static Border MakeBadge(string text, JsonObject props)
	{
		string variant = (props["variant"]?.ToString() ?? props["appearance"]?.ToString() ?? "neutral").ToLowerInvariant();
		(Color bg, Color fg) = variant switch
		{
			"primary" => (Color.FromArgb(45, 77, 163, 255), Color.FromRgb(116, 185, 255)),
			"success" => (Color.FromArgb(45, 46, 204, 113), Color.FromRgb(88, 214, 141)),
			"warning" or "caution" => (Color.FromArgb(45, 243, 156, 18), Color.FromRgb(248, 196, 113)),
			"danger" or "error" => (Color.FromArgb(45, 231, 76, 60), Color.FromRgb(241, 148, 138)),
			_ => (Color.FromArgb(35, 160, 168, 180), Color.FromRgb(210, 214, 220))
		};
		System.Windows.Controls.TextBlock label = new()
		{
			Text = text,
			FontSize = 11,
			FontWeight = FontWeights.SemiBold,
			Foreground = new SolidColorBrush(fg),
			VerticalAlignment = VerticalAlignment.Center
		};
		return new Border
		{
			Background = new SolidColorBrush(bg),
			CornerRadius = new CornerRadius(10),
			Padding = new Thickness(8, 2, 8, 2),
			Margin = new Thickness(0, 0, 6, 6),
			HorizontalAlignment = HorizontalAlignment.Left,
			VerticalAlignment = VerticalAlignment.Center,
			Child = label
		};
	}

	private static SymbolIcon MakeIcon(JsonObject props)
	{
		string name = props["symbol"]?.ToString() ?? props["name"]?.ToString() ?? "Apps24";
		if (!Enum.TryParse(name, ignoreCase: true, out SymbolRegular symbol))
			symbol = SymbolRegular.Apps24;
		SymbolIcon icon = new()
		{
			Symbol = symbol,
			FontSize = Math.Clamp(ReadDouble(props, "size", 18), 10, 64),
			Margin = new Thickness(0, 0, 6, 6),
			VerticalAlignment = VerticalAlignment.Center
		};
		string? color = props["color"]?.ToString() ?? props["foreground"]?.ToString();
		if (!string.IsNullOrWhiteSpace(color) && TryResolveBrush(color, out Brush? brush) && brush != null)
			icon.Foreground = brush;
		return icon;
	}

	private static FrameworkElement MakeColorSwatch(JsonObject props)
	{
		string hex = props["color"]?.ToString() ?? "#4DA3FF";
		double size = Math.Clamp(ReadDouble(props, "size", 24), 12, 120);
		Border box = new()
		{
			Width = size,
			Height = size,
			CornerRadius = new CornerRadius(5),
			BorderThickness = new Thickness(1),
			Margin = new Thickness(0, 0, 6, 6),
			ToolTip = hex
		};
		box.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
		if (TryResolveBrush(hex, out Brush? brush) && brush != null)
			box.Background = brush;
		string label = props["label"]?.ToString() ?? "";
		string? handler = Handler(props, "onPress");
		if (handler != null)
		{
			box.Cursor = Cursors.Hand;
			box.Tag = handler;
			box.MouseLeftButtonUp += Swatch_MouseLeftButtonUp;
		}
		if (label.Length == 0)
			return box;
		StackPanel row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 6) };
		box.Margin = new Thickness(0, 0, 6, 0);
		row.Children.Add(box);
		row.Children.Add(new System.Windows.Controls.TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
		if (handler != null)
		{
			row.Cursor = Cursors.Hand;
			row.Tag = handler;
			row.MouseLeftButtonUp += Swatch_MouseLeftButtonUp;
		}
		return row;
	}

	private static Wpf.Ui.Controls.Button MakeButton(JsonObject props, JsonArray children)
	{
		Wpf.Ui.Controls.Button button = new()
		{
			Content = ReadLabel(props, children, "label", "text"),
			Margin = new Thickness(0, 0, 6, 6),
			Tag = Handler(props, "onPress")
		};
		string variant = (props["variant"]?.ToString() ?? props["appearance"]?.ToString() ?? "").ToLowerInvariant();
		button.Appearance = variant switch
		{
			"primary" => ControlAppearance.Primary,
			"danger" or "error" => ControlAppearance.Danger,
			"success" => ControlAppearance.Success,
			"caution" or "warning" => ControlAppearance.Caution,
			"transparent" => ControlAppearance.Transparent,
			_ => ControlAppearance.Secondary
		};
		string iconName = props["icon"]?.ToString() ?? "";
		if (iconName.Length > 0 && Enum.TryParse(iconName, ignoreCase: true, out SymbolRegular icon))
			button.Icon = icon;
		if (props["disabled"] is JsonValue dis && dis.TryGetValue(out bool isDisabled) && isDisabled)
			button.IsEnabled = false;
		button.Click += Button_Click;
		return button;
	}

	private static ToggleSwitch MakeToggle(JsonObject props, JsonArray children)
	{
		ToggleSwitch toggle = new()
		{
			Content = ReadLabel(props, children, "label", "text"),
			IsChecked = props["checked"] is JsonValue on && on.TryGetValue(out bool isOn) && isOn,
			Margin = new Thickness(0, 0, 0, 6),
			Tag = Handler(props, "onChange")
		};
		if (props["disabled"] is JsonValue dis && dis.TryGetValue(out bool isDisabled) && isDisabled)
			toggle.IsEnabled = false;
		toggle.Click += Toggle_Click;
		return toggle;
	}

	private static CheckBox MakeCheckBox(JsonObject props, JsonArray children)
	{
		CheckBox check = new()
		{
			Content = ReadLabel(props, children, "label", "text"),
			IsChecked = props["checked"] is JsonValue on && on.TryGetValue(out bool isOn) && isOn,
			Margin = new Thickness(0, 0, 8, 6),
			Tag = Handler(props, "onChange")
		};
		if (props["disabled"] is JsonValue dis && dis.TryGetValue(out bool isDisabled) && isDisabled)
			check.IsEnabled = false;
		check.Click += CheckBox_Click;
		return check;
	}

	private static Wpf.Ui.Controls.TextBox MakeTextInput(JsonObject props)
	{
		string initial = props["value"]?.ToString() ?? "";
		string? onSubmit = Handler(props, "onSubmit");
		string? onChange = Handler(props, "onChange");
		Wpf.Ui.Controls.TextBox input = new()
		{
			Text = initial,
			PlaceholderText = props["placeholder"]?.ToString() ?? "",
			Margin = new Thickness(0, 0, 0, 6),
			Tag = onSubmit ?? onChange
		};
		if (props["multiline"] is JsonValue ml && ml.TryGetValue(out bool isMultiline) && isMultiline)
		{
			input.AcceptsReturn = true;
			input.TextWrapping = TextWrapping.Wrap;
			input.MinHeight = 72;
		}
		if (props["readOnly"] is JsonValue ro && ro.TryGetValue(out bool isReadOnly) && isReadOnly)
			input.IsReadOnly = true;
		if (props["disabled"] is JsonValue dis && dis.TryGetValue(out bool isDisabled) && isDisabled)
			input.IsEnabled = false;
		input.KeyDown += Input_KeyDown;
		if (onChange != null)
		{
			PendingChangeInputs[input] = (onChange, initial);
			input.LostFocus += Input_LostFocus;
			input.Unloaded += Input_Unloaded;
		}
		return input;
	}

	private static FrameworkElement MakeSlider(JsonObject props)
	{
		double min = ReadDouble(props, "min", 0);
		double max = ReadDouble(props, "max", 100);
		if (max <= min)
			max = min + 100;
		double step = ReadDouble(props, "step", 1);
		double val = Math.Clamp(ReadDouble(props, "value", min), min, max);
		string label = props["label"]?.ToString() ?? "";
		Slider slider = new()
		{
			Minimum = min,
			Maximum = max,
			Value = val,
			TickFrequency = step > 0 ? step : 1,
			IsSnapToTickEnabled = step > 0,
			VerticalAlignment = VerticalAlignment.Center,
			Tag = Handler(props, "onChange")
		};
		System.Windows.Controls.TextBlock readout = new()
		{
			Text = step >= 1 ? Math.Round(val).ToString() : val.ToString("0.##"),
			MinWidth = 38,
			Margin = new Thickness(8, 0, 0, 0),
			VerticalAlignment = VerticalAlignment.Center,
			TextAlignment = TextAlignment.Right
		};
		readout.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		slider.ValueChanged += (_, e) =>
		{
			readout.Text = step >= 1 ? Math.Round(e.NewValue).ToString() : e.NewValue.ToString("0.##");
		};
		slider.PreviewMouseLeftButtonUp += Slider_Released;
		slider.KeyUp += Slider_KeyUp;
		Grid row = new() { Margin = new Thickness(0, 0, 0, 6) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		if (label.Length > 0)
		{
			System.Windows.Controls.TextBlock lbl = new() { Text = label, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
			Grid.SetColumn(lbl, 0);
			row.Children.Add(lbl);
		}
		Grid.SetColumn(slider, 1);
		row.Children.Add(slider);
		Grid.SetColumn(readout, 2);
		row.Children.Add(readout);
		return row;
	}

	private static ComboBox MakeSelect(JsonObject props, JsonArray children)
	{
		ComboBox combo = new()
		{
			Margin = new Thickness(0, 0, 6, 6),
			MinWidth = 140,
			Tag = Handler(props, "onChange")
		};
		string selected = props["selected"]?.ToString() ?? props["value"]?.ToString() ?? "";
		int selectedIdx = -1;
		int idx = 0;
		JsonArray options = props["options"] as JsonArray ?? children;
		foreach (JsonNode? opt in options)
		{
			string optVal;
			string optLabel;
			if (opt is JsonObject optObj)
			{
				optVal = optObj["value"]?.ToString() ?? optObj["key"]?.ToString() ?? optObj["label"]?.ToString() ?? "";
				optLabel = optObj["label"]?.ToString() ?? optObj["name"]?.ToString() ?? optVal;
			}
			else
			{
				optVal = opt?.ToString() ?? "";
				optLabel = optVal;
			}
			ComboBoxItem item = new() { Content = optLabel, Tag = optVal };
			combo.Items.Add(item);
			if (selectedIdx < 0 && string.Equals(optVal, selected, StringComparison.Ordinal))
				selectedIdx = idx;
			idx++;
		}
		if (selectedIdx >= 0)
			combo.SelectedIndex = selectedIdx;
		else if (combo.Items.Count > 0)
			combo.SelectedIndex = 0;
		combo.SelectionChanged += Combo_SelectionChanged;
		return combo;
	}

	private static System.Windows.Controls.ProgressBar MakeProgressBar(JsonObject props)
	{
		double max = ReadDouble(props, "max", 100);
		if (max <= 0)
			max = 100;
		double val = Math.Clamp(ReadDouble(props, "value", 0), 0, max);
		return new System.Windows.Controls.ProgressBar
		{
			Minimum = 0,
			Maximum = max,
			Value = val,
			Height = ReadDouble(props, "height", 8),
			IsIndeterminate = props["indeterminate"] is JsonValue ind && ind.TryGetValue(out bool isInd) && isInd,
			Margin = new Thickness(0, 0, 0, 6)
		};
	}

	private static InfoBar MakeInfoBar(JsonObject props, JsonArray children)
	{
		string severity = (props["severity"]?.ToString() ?? props["variant"]?.ToString() ?? "info").ToLowerInvariant();
		return new InfoBar
		{
			Title = props["title"]?.ToString() ?? "",
			Message = ReadLabel(props, children, "message", "text"),
			IsOpen = props["open"] is not JsonValue op || !op.TryGetValue(out bool isOpen) || isOpen,
			IsClosable = false,
			Severity = severity switch
			{
				"success" => InfoBarSeverity.Success,
				"warning" or "caution" => InfoBarSeverity.Warning,
				"error" or "danger" => InfoBarSeverity.Error,
				_ => InfoBarSeverity.Informational
			},
			Margin = new Thickness(0, 0, 0, 8)
		};
	}

	private static Border MakeSpacer(JsonObject props)
	{
		return new Border
		{
			Width = ReadDouble(props, "width", 0),
			Height = ReadDouble(props, "height", 8),
			Background = Brushes.Transparent
		};
	}

	private static bool IsSafeRemoteImageHost(string host, string? pluginDirectory)
	{
		if (host.Equals("thumbnails.roblox.com", StringComparison.OrdinalIgnoreCase) ||
			host.Equals("www.roblox.com", StringComparison.OrdinalIgnoreCase) ||
			host.EndsWith(".rbxcdn.com", StringComparison.OrdinalIgnoreCase))
			return true;
		if (!string.IsNullOrEmpty(pluginDirectory) && PluginManifest.TryRead(pluginDirectory, out PluginManifest? manifest, out _) && manifest != null)
			return manifest.NetworkHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
		return false;
	}

	private static System.Windows.Controls.Image? MakeImage(JsonObject props, string? pluginDirectory)
	{
		string source = props["src"]?.ToString() ?? "";
		if (source.Length == 0)
			return null;
		double height = Math.Clamp(ReadDouble(props, "height", 96), 8, 800);
		System.Windows.Controls.Image image = new()
		{
			Height = height,
			Stretch = Stretch.Uniform,
			HorizontalAlignment = HorizontalAlignment.Left,
			Margin = new Thickness(0, 0, 0, 6)
		};
		if (ReadDouble(props, "width", double.NaN) is double w && !double.IsNaN(w) && w > 0)
			image.Width = Math.Clamp(w, 8, 1200);
		if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			if (!Uri.TryCreate(source, UriKind.Absolute, out Uri? remoteUri) || !IsSafeRemoteImageHost(remoteUri.IdnHost, pluginDirectory))
				return null;
			DynamicRenderSystem.SetLazyImageSource(image, source);
			return image;
		}
		if (source.StartsWith("pack://", StringComparison.OrdinalIgnoreCase))
		{
			try
			{
				image.Source = new BitmapImage(new Uri(source, UriKind.Absolute));
				return image;
			}
			catch
			{
				return null;
			}
		}
		if (!string.IsNullOrEmpty(pluginDirectory))
		{
			string root = Path.GetFullPath(pluginDirectory);
			string full = Path.GetFullPath(Path.Combine(root, source));
			if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
				full.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) &&
				File.Exists(full))
			{
				if (SafeImaging.FromFile(full, (int)Math.Max(height * 2, 128)) is BitmapSource bmp)
				{
					image.Source = bmp;
					return image;
				}
			}
		}
		return null;
	}

	private static void ApplyCommonProps(FrameworkElement element, JsonObject props)
	{
		if (props["tooltip"]?.ToString() is { Length: > 0 } tip)
			element.ToolTip = tip;
		if (ReadDouble(props, "width", double.NaN) is double w && !double.IsNaN(w) && w >= 0)
			element.Width = w;
		if (ReadDouble(props, "height", double.NaN) is double h && !double.IsNaN(h) && h >= 0)
			element.Height = h;
		if (ReadDouble(props, "minWidth", double.NaN) is double minW && !double.IsNaN(minW) && minW >= 0)
			element.MinWidth = minW;
		if (ReadDouble(props, "maxWidth", double.NaN) is double maxW && !double.IsNaN(maxW) && maxW > 0)
			element.MaxWidth = maxW;
		if (ReadDouble(props, "minHeight", double.NaN) is double minH && !double.IsNaN(minH) && minH >= 0)
			element.MinHeight = minH;
		if (ReadDouble(props, "maxHeight", double.NaN) is double maxH && !double.IsNaN(maxH) && maxH > 0)
			element.MaxHeight = maxH;
		if (ReadDouble(props, "opacity", double.NaN) is double op && !double.IsNaN(op))
			element.Opacity = Math.Clamp(op, 0.05, 1.0);
		if (TryReadThickness(props["margin"], out Thickness margin))
			element.Margin = margin;
		if (TryReadThickness(props["padding"], out Thickness padding))
		{
			if (element is Border border)
				border.Padding = padding;
			else if (element is System.Windows.Controls.Control ctrl)
				ctrl.Padding = padding;
		}
		string? hAlign = props["horizontalAlignment"]?.ToString() ?? props["align"]?.ToString();
		if (!string.IsNullOrEmpty(hAlign) && element is not System.Windows.Controls.TextBlock)
		{
			element.HorizontalAlignment = hAlign.ToLowerInvariant() switch
			{
				"left" => HorizontalAlignment.Left,
				"center" => HorizontalAlignment.Center,
				"right" => HorizontalAlignment.Right,
				"stretch" => HorizontalAlignment.Stretch,
				_ => element.HorizontalAlignment
			};
		}
		string? vAlign = props["verticalAlignment"]?.ToString();
		if (!string.IsNullOrEmpty(vAlign))
		{
			element.VerticalAlignment = vAlign.ToLowerInvariant() switch
			{
				"top" => VerticalAlignment.Top,
				"center" => VerticalAlignment.Center,
				"bottom" => VerticalAlignment.Bottom,
				"stretch" => VerticalAlignment.Stretch,
				_ => element.VerticalAlignment
			};
		}
		if (props["background"]?.ToString() is { Length: > 0 } bg && TryResolveBrush(bg, out Brush? bgBrush) && bgBrush != null)
		{
			if (element is Border b)
				b.Background = bgBrush;
			else if (element is Panel p)
				p.Background = bgBrush;
			else if (element is System.Windows.Controls.Control c)
				c.Background = bgBrush;
		}
		if (props["borderBrush"]?.ToString() is { Length: > 0 } bb && TryResolveBrush(bb, out Brush? bbBrush) && bbBrush != null)
		{
			if (element is Border b)
				b.BorderBrush = bbBrush;
			else if (element is System.Windows.Controls.Control c)
				c.BorderBrush = bbBrush;
		}
		if (TryReadThickness(props["borderThickness"], out Thickness bt))
		{
			if (element is Border b)
				b.BorderThickness = bt;
			else if (element is System.Windows.Controls.Control c)
				c.BorderThickness = bt;
		}
		if (ReadDouble(props, "cornerRadius", double.NaN) is double cr && !double.IsNaN(cr) && cr >= 0 && element is Border cornerBorder)
			cornerBorder.CornerRadius = new CornerRadius(cr);
	}

	public static bool TryResolveBrush(string value, out Brush? brush)
	{
		brush = null;
		string trimmed = value.Trim();
		if (trimmed.Length == 0)
			return false;
		if (Application.Current?.TryFindResource(trimmed) is Brush resourceBrush)
		{
			brush = resourceBrush;
			return true;
		}
		try
		{
			if (ColorConverter.ConvertFromString(trimmed) is Color color)
			{
				SolidColorBrush solid = new(color);
				solid.Freeze();
				brush = solid;
				return true;
			}
		}
		catch
		{
		}
		return false;
	}

	private static bool TryReadThickness(JsonNode? node, out Thickness thickness)
	{
		thickness = default;
		if (node == null)
			return false;
		if (node is JsonValue val && val.TryGetValue(out double uniform))
		{
			thickness = new Thickness(uniform);
			return true;
		}
		string[] parts = node.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 1 && double.TryParse(parts[0], out double u))
		{
			thickness = new Thickness(u);
			return true;
		}
		if (parts.Length == 2 && double.TryParse(parts[0], out double h) && double.TryParse(parts[1], out double v))
		{
			thickness = new Thickness(h, v, h, v);
			return true;
		}
		if (parts.Length == 4 && double.TryParse(parts[0], out double l) && double.TryParse(parts[1], out double t) && double.TryParse(parts[2], out double r) && double.TryParse(parts[3], out double b))
		{
			thickness = new Thickness(l, t, r, b);
			return true;
		}
		return false;
	}

	private static string ReadLabel(JsonObject props, JsonArray children, params string[] keys)
	{
		foreach (string key in keys)
			if (props[key]?.ToString() is { Length: > 0 } direct)
				return direct;
		return string.Concat(children.Select(child => child is JsonValue text ? text.ToString() : ""));
	}

	private static double ReadDouble(JsonObject? props, string key, double fallback)
	{
		if (props?[key] is JsonValue val)
		{
			if (val.TryGetValue(out double d) && double.IsFinite(d))
				return d;
			if (val.TryGetValue(out int i))
				return i;
			if (double.TryParse(val.ToString(), out double parsed) && double.IsFinite(parsed))
				return parsed;
		}
		return fallback;
	}

	private static int ReadInt(JsonObject? props, string key, int fallback)
	{
		if (props?[key] is JsonValue val)
		{
			if (val.TryGetValue(out int i))
				return i;
			if (val.TryGetValue(out double d) && double.IsFinite(d))
				return (int)d;
			if (int.TryParse(val.ToString(), out int parsed))
				return parsed;
		}
		return fallback;
	}

	private static string? Handler(JsonObject props, string name) => (props[name] as JsonObject)?["$handler"]?.ToString();

	private static void Button_Click(object sender, RoutedEventArgs e)
	{
		FlushPendingInputs();
		if (sender is FrameworkElement { Tag: string handler })
			PluginHost.InvokeHandler(handler, null);
	}

	private static void Swatch_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		FlushPendingInputs();
		if (sender is FrameworkElement { Tag: string handler })
			PluginHost.InvokeHandler(handler, null);
	}

	private static void Toggle_Click(object sender, RoutedEventArgs e)
	{
		FlushPendingInputs();
		if (sender is ToggleSwitch { Tag: string handler } toggle)
			PluginHost.InvokeHandler(handler, JsonValue.Create(toggle.IsChecked == true));
	}

	private static void CheckBox_Click(object sender, RoutedEventArgs e)
	{
		FlushPendingInputs();
		if (sender is CheckBox { Tag: string handler } check)
			PluginHost.InvokeHandler(handler, JsonValue.Create(check.IsChecked == true));
	}

	private static void Input_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && sender is Wpf.Ui.Controls.TextBox { AcceptsReturn: false, Tag: string handler } input)
		{
			string text = input.Text ?? "";
			if (PendingChangeInputs.ContainsKey(input))
				PendingChangeInputs[input] = (handler, text);
			PluginHost.InvokeHandler(handler, JsonValue.Create(text));
		}
	}

	private static void Input_LostFocus(object sender, RoutedEventArgs e)
	{
		if (sender is Wpf.Ui.Controls.TextBox input && PendingChangeInputs.TryGetValue(input, out (string Handler, string Sent) entry))
		{
			string current = input.Text ?? "";
			if (!string.Equals(current, entry.Sent, StringComparison.Ordinal))
			{
				PendingChangeInputs[input] = (entry.Handler, current);
				PluginHost.InvokeHandler(entry.Handler, JsonValue.Create(current));
			}
		}
	}

	private static void Input_Unloaded(object sender, RoutedEventArgs e)
	{
		if (sender is Wpf.Ui.Controls.TextBox input)
			PendingChangeInputs.Remove(input);
	}

	private static void Slider_Released(object sender, MouseButtonEventArgs e)
	{
		if (sender is Slider { Tag: string handler } slider)
			PluginHost.InvokeHandler(handler, JsonValue.Create(slider.Value));
	}

	private static void Slider_KeyUp(object sender, KeyEventArgs e)
	{
		if (sender is Slider { Tag: string handler } slider)
			PluginHost.InvokeHandler(handler, JsonValue.Create(slider.Value));
	}

	private static void Combo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (sender is ComboBox { Tag: string handler, SelectedItem: ComboBoxItem { Tag: string val } })
			PluginHost.InvokeHandler(handler, JsonValue.Create(val));
	}
}
