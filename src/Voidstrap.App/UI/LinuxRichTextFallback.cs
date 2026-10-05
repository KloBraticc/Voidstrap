using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Voidstrap.UI;

internal static class LinuxRichTextFallback
{
	private static readonly DependencyProperty FallbackProperty = DependencyProperty.RegisterAttached(
		"Fallback", typeof(TextBox), typeof(LinuxRichTextFallback), new PropertyMetadata(null));

	public static void Apply(RichTextBox rich)
	{
		if (!OperatingSystem.IsLinux() || rich is null)
			return;

		try
		{
			string text = new TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.TrimEnd('\r', '\n');
			if (rich.GetValue(FallbackProperty) is TextBox existing)
			{
				existing.Text = text;
				return;
			}

			TextBox box = new()
			{
				Text = text,
				IsReadOnly = rich.IsReadOnly,
				IsReadOnlyCaretVisible = rich.IsReadOnlyCaretVisible,
				AcceptsReturn = true,
				TextWrapping = TextWrapping.Wrap,
				VerticalScrollBarVisibility = rich.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled ? ScrollBarVisibility.Auto : rich.VerticalScrollBarVisibility,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
				Padding = rich.Padding,
				Margin = rich.Margin,
				MinHeight = rich.MinHeight,
				MaxHeight = rich.MaxHeight,
				FontFamily = rich.FontFamily,
				FontSize = rich.FontSize,
				Background = rich.Background,
				BorderThickness = rich.BorderThickness,
				HorizontalAlignment = rich.HorizontalAlignment,
				VerticalAlignment = rich.VerticalAlignment
			};
			if (!double.IsNaN(rich.Height))
				box.Height = rich.Height;
			if (rich.ReadLocalValue(Control.ForegroundProperty) != DependencyProperty.UnsetValue)
				box.SetBinding(Control.ForegroundProperty, new System.Windows.Data.Binding(nameof(Control.Foreground)) { Source = rich });
			if (rich.SelectionBrush is not null)
				box.SelectionBrush = rich.SelectionBrush;
			Grid.SetRow(box, Grid.GetRow(rich));
			Grid.SetColumn(box, Grid.GetColumn(rich));
			Grid.SetRowSpan(box, Grid.GetRowSpan(rich));
			Grid.SetColumnSpan(box, Grid.GetColumnSpan(rich));

			switch (rich.Parent)
			{
				case Panel panel:
					panel.Children.Insert(panel.Children.IndexOf(rich) + 1, box);
					rich.Visibility = Visibility.Collapsed;
					break;
				case Decorator decorator:
					decorator.Child = box;
					break;
				case ContentControl content:
					content.Content = box;
					break;
				default:
					return;
			}
			rich.SetValue(FallbackProperty, box);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("LinuxRichTextFallback", "The text could not be shown in a plain text box: " + ex.Message);
		}
	}
}
