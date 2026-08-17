using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Voidstrap.UI.Utility;

public static class Rendering
{
	private static double? _cachedDpi;

	public static double GetTextWidth(TextBlock textBlock)
	{
		if (textBlock == null)
		{
			return 0.0;
		}
		string text = textBlock.Text;
		if (string.IsNullOrEmpty(text))
		{
			return 0.0;
		}
		if (!_cachedDpi.HasValue)
		{
			_cachedDpi = VisualTreeHelper.GetDpi(textBlock).PixelsPerDip;
		}
		TextOptions.SetTextFormattingMode((DependencyObject)(object)textBlock, (TextFormattingMode)1);
		Typeface typeface = new(textBlock.FontFamily, textBlock.FontStyle, textBlock.FontWeight, textBlock.FontStretch);
		return new FormattedText(text, CultureInfo.CurrentUICulture, textBlock.FlowDirection, typeface, textBlock.FontSize, Brushes.Transparent, _cachedDpi.Value)
		{
			TextAlignment = TextAlignment.Left,
			Trimming = TextTrimming.None
		}.WidthIncludingTrailingWhitespace;
	}
}
