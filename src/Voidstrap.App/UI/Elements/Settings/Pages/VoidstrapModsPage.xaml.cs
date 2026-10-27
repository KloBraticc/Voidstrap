using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Voidstrap.Integrations.Plugins;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class VoidstrapModsPage : UiPage
{
	private const int MaxRenderedNodes = 2000;

	private int _rendered;

	public VoidstrapModsPage()
	{
		InitializeComponent();
	}

	private void Page_Loaded(object sender, RoutedEventArgs e)
	{
		PluginHost.PanesChanged -= OnPanesChanged;
		PluginHost.PanesChanged += OnPanesChanged;
		PluginHost.PaneInvalidated -= OnPaneInvalidated;
		PluginHost.PaneInvalidated += OnPaneInvalidated;
		PluginHost.StatusChanged -= OnStatusChanged;
		PluginHost.StatusChanged += OnStatusChanged;
		RenderAll();
		RenderStatus();
	}

	private void Page_Unloaded(object sender, RoutedEventArgs e)
	{
		PluginHost.PanesChanged -= OnPanesChanged;
		PluginHost.PaneInvalidated -= OnPaneInvalidated;
		PluginHost.StatusChanged -= OnStatusChanged;
	}

	private void OnPanesChanged() => Dispatcher.BeginInvoke(new Action(RenderAll));

	private void OnPaneInvalidated(string plugin) => Dispatcher.BeginInvoke(new Action(RenderAll));

	private void OnStatusChanged() => Dispatcher.BeginInvoke(new Action(RenderStatus));

	private void RenderStatus()
	{
		StatusText.Text = string.Join("   ", PluginHost.StatusLines);
	}

	private void RenderAll()
	{
		PanesHost.Children.Clear();
		var panes = PluginHost.Panes;
		EmptyText.Visibility = panes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		foreach (PluginHost.PaneInfo pane in panes)
		{
			_rendered = 0;
			StackPanel body = new();
			body.Children.Add(new System.Windows.Controls.TextBlock { Text = pane.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
			UIElement? content = null;
			try
			{
				content = Build(PluginHost.RenderPane(pane));
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("VoidstrapMods", pane.Plugin + ": the pane " + pane.Id + " could not be drawn: " + PluginHost.Describe(ex));
			}
			if (content != null)
				body.Children.Add(content);
			Border card = new() { Child = body, Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
			card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
			card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
			PanesHost.Children.Add(card);
		}
	}

	private UIElement? Build(JsonNode? node)
	{
		if (node == null || ++_rendered > MaxRenderedNodes)
			return null;
		if (node is JsonValue value)
			return MakeText(value.ToString(), new JsonObject());
		if (node is JsonArray list)
		{
			StackPanel stack = new();
			foreach (JsonNode? child in list)
				if (Build(child) is UIElement item)
					stack.Children.Add(item);
			return stack;
		}
		if (node is not JsonObject element)
			return null;
		string type = element["type"]?.ToString() ?? "Box";
		JsonObject props = element["props"] as JsonObject ?? new JsonObject();
		JsonArray children = element["children"] as JsonArray ?? new JsonArray();
		switch (type)
		{
			case "Text":
				return MakeText(props["text"]?.ToString() ?? string.Concat(children.Select(child => child is JsonValue text ? text.ToString() : "")), props);
			case "Button":
			{
				Wpf.Ui.Controls.Button button = new() { Content = props["label"]?.ToString() ?? string.Concat(children.Select(child => child is JsonValue text ? text.ToString() : "")), Margin = new Thickness(0, 0, 6, 6), Tag = Handler(props, "onPress") };
				if (props["variant"]?.ToString() == "primary")
					button.Appearance = Wpf.Ui.Common.ControlAppearance.Primary;
				button.Click += Button_Click;
				return button;
			}
			case "Toggle":
			{
				ToggleSwitch toggle = new() { Content = props["label"]?.ToString() ?? "", IsChecked = props["checked"] is JsonValue on && on.TryGetValue(out bool isOn) && isOn, Margin = new Thickness(0, 0, 0, 6), Tag = Handler(props, "onChange") };
				toggle.Click += Toggle_Click;
				return toggle;
			}
			case "TextInput":
			{
				Wpf.Ui.Controls.TextBox input = new() { Text = props["value"]?.ToString() ?? "", PlaceholderText = props["placeholder"]?.ToString() ?? "", Margin = new Thickness(0, 0, 0, 6), Tag = Handler(props, "onSubmit") };
				input.KeyDown += Input_KeyDown;
				return input;
			}
			case "Divider":
				return new Separator { Margin = new Thickness(0, 6, 0, 6) };
			case "Image":
			{
				string source = props["src"]?.ToString() ?? "";
				if (!source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
					return null;
				System.Windows.Controls.Image image = new() { Height = props["height"] is JsonValue h && h.TryGetValue(out double height) ? Math.Clamp(height, 8, 600) : 96, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 6) };
				Voidstrap.Utility.DynamicRenderSystem.SetLazyImageSource(image, source);
				return image;
			}
			case "Card":
			{
				Border card = new() { Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Child = BuildChildren(children, props) };
				card.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
				card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
				return card;
			}
			default:
				return BuildChildren(children, props);
		}
	}

	private Panel BuildChildren(JsonArray children, JsonObject props)
	{
		bool row = props["direction"]?.ToString() == "row";
		Panel panel = row ? new WrapPanel() : new StackPanel();
		foreach (JsonNode? child in children)
			if (Build(child) is UIElement built)
				panel.Children.Add(built);
		return panel;
	}

	private static System.Windows.Controls.TextBlock MakeText(string text, JsonObject props)
	{
		System.Windows.Controls.TextBlock block = new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 6, 6) };
		if (props["bold"] is JsonValue bold && bold.TryGetValue(out bool isBold) && isBold)
			block.FontWeight = FontWeights.SemiBold;
		if (props["size"] is JsonValue size && size.TryGetValue(out double points))
			block.FontSize = Math.Clamp(points, 8, 40);
		block.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, props["dim"] is JsonValue dim && dim.TryGetValue(out bool isDim) && isDim ? "TextFillColorTertiaryBrush" : "TextFillColorPrimaryBrush");
		return block;
	}

	private static string? Handler(JsonObject props, string name) => (props[name] as JsonObject)?["$handler"]?.ToString();

	private void Button_Click(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string handler })
			PluginHost.InvokeHandler(handler, null);
	}

	private void Toggle_Click(object sender, RoutedEventArgs e)
	{
		if (sender is ToggleSwitch { Tag: string handler } toggle)
			PluginHost.InvokeHandler(handler, JsonValue.Create(toggle.IsChecked == true));
	}

	private void Input_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && sender is Wpf.Ui.Controls.TextBox { Tag: string handler } input)
			PluginHost.InvokeHandler(handler, JsonValue.Create(input.Text));
	}

	private void OpenFolder_Click(object sender, RoutedEventArgs e)
	{
		System.IO.Directory.CreateDirectory(Paths.Plugins);
		Voidstrap.Utility.PlatformShell.TryOpenFolder(Paths.Plugins);
	}
}
