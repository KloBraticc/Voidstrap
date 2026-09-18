using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Voidstrap.UI;

internal static class LinuxInlineText
{
	private const int MaxCachedWords = 4096;

	private static readonly object CacheLock = new();

	private static readonly Dictionary<TextMeasureKey, double> WidthCache = new();

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, Hyperlink> HoveredLinks = new();

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextBlock, HyperlinkUnderlineAdorner> Underlines = new();

	private static readonly Color LinkColor = Color.FromRgb(0x6C, 0xA8, 0xF5);

	private static Brush? _linkBrush;

	private static bool _installed;

	private readonly record struct TextMeasureKey(string Font, int Style, int Weight, int Stretch, int Size, int Dpi, string Text);

	public static Brush LinkBrush
	{
		get
		{
			Brush? cached = _linkBrush;
			if (cached != null)
			{
				return cached;
			}

			Brush resolved = ResolveThemeBrush() ?? CreateFallbackBrush();
			_linkBrush = resolved;
			return resolved;
		}
	}

	private static Brush? ResolveThemeBrush()
	{
		try
		{
			Application? application = Application.Current;
			if (application == null)
			{
				return null;
			}

			if (application.TryFindResource("AccentTextFillColorPrimaryBrush") is Brush accent)
			{
				return accent;
			}

			return application.TryFindResource("SystemAccentColorSecondaryBrush") as Brush;
		}
		catch
		{
			return null;
		}
	}

	private static SolidColorBrush CreateFallbackBrush()
	{
		SolidColorBrush brush = new(LinkColor);
		brush.Freeze();
		return brush;
	}

	public static void ResetLinkBrush()
	{
		_linkBrush = null;
	}

	public static bool Sanitize(TextBlock block)
	{
		if (block is null || block.Inlines.Count == 0)
		{
			return false;
		}

		bool changed = false;
		foreach (Inline inline in block.Inlines)
		{
			changed |= SanitizeInline(inline);
		}

		AttachLinkRouting(block);
		return changed;
	}

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		_installed = true;
		EventManager.RegisterClassHandler(
			typeof(TextBlock),
			FrameworkElement.LoadedEvent,
			new RoutedEventHandler(OnTextBlockLoaded),
			true);
	}

	private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is TextBlock block)
			AttachLinkRouting(block);
	}

	internal static void AttachLinkRouting(TextBlock block)
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		if (block.Background is null && ContainsHyperlink(block))
			block.Background = Brushes.Transparent;

		block.MouseMove -= OnBlockMouseMove;
		block.MouseMove += OnBlockMouseMove;
		block.MouseLeave -= OnBlockMouseLeave;
		block.MouseLeave += OnBlockMouseLeave;
		block.MouseLeftButtonUp -= OnBlockMouseLeftButtonUp;
		block.MouseLeftButtonUp += OnBlockMouseLeftButtonUp;
	}

	private static bool ContainsHyperlink(TextBlock block)
	{
		foreach (Inline inline in block.Inlines)
		{
			if (ContainsHyperlink(inline))
				return true;
		}

		return false;
	}

	private static bool ContainsHyperlink(Inline inline)
	{
		if (inline is Hyperlink)
			return true;

		if (inline is Span span)
		{
			foreach (Inline child in span.Inlines)
			{
				if (ContainsHyperlink(child))
					return true;
			}
		}

		return false;
	}

	private static void OnBlockMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (sender is not TextBlock block || !ContainsHyperlink(block))
			return;

		HoverAt(block, e.GetPosition(block));
	}

	internal static bool HoverAt(TextBlock block, Point point)
	{
		Hyperlink? link = ResolveHyperlink(block, point);
		SetHoveredLink(block, link);
		return link is not null;
	}

	internal static bool ActivateAt(TextBlock block, Point point)
	{
		Hyperlink? link = ResolveHyperlink(block, point);
		if (link is null)
			return false;

		try
		{
			link.DoClick();
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("LinuxInlineText::ActivateAt", ex);
			return false;
		}
	}

	private static void OnBlockMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
	{
		if (sender is TextBlock block)
			SetHoveredLink(block, null);
	}

	private static void OnBlockMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
	{
		if (sender is not TextBlock block || !ContainsHyperlink(block))
			return;

		if (ActivateAt(block, e.GetPosition(block)))
			e.Handled = true;
	}

	private static void SetHoveredLink(TextBlock block, Hyperlink? link)
	{
		if (HoveredLinks.TryGetValue(block, out Hyperlink? previous))
		{
			if (ReferenceEquals(previous, link))
				return;

			HoveredLinks.Remove(block);
		}

		if (link is null)
		{
			block.ClearValue(FrameworkElement.CursorProperty);
			HideUnderline(block);
			return;
		}

		block.Cursor = System.Windows.Input.Cursors.Hand;
		HoveredLinks.Add(block, link);
		ShowUnderline(block, link);
	}

	internal static bool IsUnderlineVisible(TextBlock block)
	{
		return Underlines.TryGetValue(block, out HyperlinkUnderlineAdorner? adorner) && adorner.HasUnderline;
	}

	private static void ShowUnderline(TextBlock block, Hyperlink link)
	{
		System.Collections.Generic.List<Rect> regions = MeasureLinkRegions(link);
		if (regions.Count == 0)
		{
			HideUnderline(block);
			return;
		}

		HyperlinkUnderlineAdorner? adorner = ResolveUnderlineAdorner(block);
		adorner?.Show(regions, link.Foreground ?? block.Foreground ?? LinkBrush);
	}

	private static void HideUnderline(TextBlock block)
	{
		if (Underlines.TryGetValue(block, out HyperlinkUnderlineAdorner? adorner))
			adorner.Hide();
	}

	private static HyperlinkUnderlineAdorner? ResolveUnderlineAdorner(TextBlock block)
	{
		if (Underlines.TryGetValue(block, out HyperlinkUnderlineAdorner? existing))
			return existing;

		System.Windows.Documents.AdornerLayer? layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(block);
		if (layer is null)
			return null;

		HyperlinkUnderlineAdorner adorner = new(block);
		layer.Add(adorner);
		Underlines.Add(block, adorner);
		return adorner;
	}

	private static System.Collections.Generic.List<Rect> MeasureLinkRegions(Hyperlink link)
	{
		System.Collections.Generic.List<Rect> regions = new();
		try
		{
			TextPointer? position = link.ElementStart;
			TextPointer end = link.ElementEnd;
			Rect line = Rect.Empty;
			for (int steps = 0; position is not null && steps < 512 && position.CompareTo(end) < 0; steps++)
			{
				Rect rect = position.GetCharacterRect(LogicalDirection.Forward);
				if (rect.IsEmpty || rect.Height <= 0d)
				{
					position = position.GetNextInsertionPosition(LogicalDirection.Forward);
					continue;
				}

				if (line.IsEmpty)
				{
					line = rect;
				}
				else if (Math.Abs(rect.Top - line.Top) < 1d)
				{
					line = Rect.Union(line, rect);
				}
				else
				{
					regions.Add(line);
					line = rect;
				}

				position = position.GetNextInsertionPosition(LogicalDirection.Forward);
			}

			if (!line.IsEmpty)
			{
				Rect tail = end.GetCharacterRect(LogicalDirection.Backward);
				if (!tail.IsEmpty && Math.Abs(tail.Top - line.Top) < 1d)
					line = Rect.Union(line, tail);
				regions.Add(line);
			}
		}
		catch (Exception)
		{
			regions.Clear();
		}

		return regions;
	}

	private sealed class HyperlinkUnderlineAdorner : System.Windows.Documents.Adorner
	{
		private readonly System.Collections.Generic.List<Rect> _regions = new();

		private Brush _brush = Brushes.Transparent;

		internal HyperlinkUnderlineAdorner(UIElement adornedElement)
			: base(adornedElement)
		{
			IsHitTestVisible = false;
		}

		internal bool HasUnderline => _regions.Count > 0;

		internal void Show(System.Collections.Generic.List<Rect> regions, Brush brush)
		{
			_regions.Clear();
			_regions.AddRange(regions);
			_brush = brush;
			InvalidateVisual();
		}

		internal void Hide()
		{
			if (_regions.Count == 0)
				return;

			_regions.Clear();
			InvalidateVisual();
		}

		protected override void OnRender(DrawingContext drawingContext)
		{
			foreach (Rect region in _regions)
			{
				double thickness = Math.Max(1d, Math.Round(region.Height / 14d));
				drawingContext.DrawRectangle(
					_brush,
					null,
					new Rect(region.Left, region.Bottom - thickness, region.Width, thickness));
			}
		}
	}

	private static Hyperlink? ResolveHyperlink(TextBlock block, Point point)
	{
		Hyperlink? link = FindHyperlink(block.InputHitTest(point) as DependencyObject);
		if (link is not null)
			return link;

		try
		{
			return FindHyperlink(block.GetPositionFromPoint(point, false)?.Parent);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static Hyperlink? FindHyperlink(DependencyObject? start)
	{
		DependencyObject? current = start;
		for (int depth = 0; current is not null && depth < 24; depth++)
		{
			if (current is Hyperlink link)
				return link;

			current = current switch
			{
				FrameworkContentElement content => content.Parent,
				FrameworkElement element => element.Parent,
				_ => null
			};
		}

		return null;
	}

	private static bool SanitizeInline(Inline inline)
	{
		bool changed = false;

		if (inline is Hyperlink hyperlink)
		{
			if (hyperlink.TextDecorations is { Count: > 0 }
				|| hyperlink.ReadLocalValue(Inline.TextDecorationsProperty) == DependencyProperty.UnsetValue)
			{
				hyperlink.TextDecorations = null;
				changed = true;
			}

			if (hyperlink.Foreground is null)
			{
				hyperlink.Foreground = LinkBrush;
				changed = true;
			}

			if (hyperlink.Cursor is null)
			{
				hyperlink.Cursor = System.Windows.Input.Cursors.Hand;
			}
		}
		else if (inline.TextDecorations is { Count: > 0 }
			|| (inline is Underline && inline.ReadLocalValue(Inline.TextDecorationsProperty) == DependencyProperty.UnsetValue))
		{
			inline.TextDecorations = null;
			changed = true;
		}

		if (inline is Span span)
		{
			foreach (Inline child in span.Inlines)
			{
				changed |= SanitizeInline(child);
			}
		}

		return changed;
	}

	public static bool HasRichInlines(TextBlock block)
	{
		if (block is null)
		{
			return false;
		}

		foreach (Inline inline in block.Inlines)
		{
			if (inline is not Run)
			{
				return true;
			}
		}

		return false;
	}

	public static string ReadInlineText(TextBlock block)
	{
		System.Text.StringBuilder builder = new();
		foreach (Inline inline in block.Inlines)
		{
			AppendInline(builder, inline);
		}

		return builder.ToString();
	}

	private static void AppendInline(System.Text.StringBuilder builder, Inline inline)
	{
		switch (inline)
		{
			case Run run:
				builder.Append(run.Text);
				break;
			case LineBreak:
				builder.Append('\n');
				break;
			case Span span:
				foreach (Inline child in span.Inlines)
				{
					AppendInline(builder, child);
				}

				break;
		}
	}

	public static double MeasureCached(TextBlock block, string text)
	{
		if (block is null || string.IsNullOrEmpty(text))
		{
			return 0.0;
		}

		double size = block.FontSize;
		if (size <= 0.0 || double.IsNaN(size))
		{
			return 0.0;
		}

		double pixelsPerDip;
		try
		{
			pixelsPerDip = VisualTreeHelper.GetDpi(block).PixelsPerDip;
		}
		catch
		{
			pixelsPerDip = 1.0;
		}

		TextMeasureKey key = new(
			block.FontFamily?.Source ?? string.Empty,
			block.FontStyle.GetHashCode(),
			block.FontWeight.ToOpenTypeWeight(),
			block.FontStretch.ToOpenTypeStretch(),
			(int)Math.Round(size * 4.0),
			(int)Math.Round(pixelsPerDip * 100.0),
			text);

		lock (CacheLock)
		{
			if (WidthCache.TryGetValue(key, out double cached))
			{
				return cached;
			}
		}

		double measured;
		try
		{
			Typeface typeface = new(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
			FormattedText formatted = new(
				text,
				CultureInfo.CurrentUICulture,
				block.FlowDirection,
				typeface,
				size,
				Brushes.Black,
				pixelsPerDip);
			measured = formatted.WidthIncludingTrailingWhitespace;
			if (!double.IsFinite(measured) || measured <= 0.0)
			{
				measured = MeasureWithLayout(block, text);
			}
			if (ContainsNonAscii(text))
			{
				measured = Math.Max(measured, EstimatePortableWidth(text, size));
			}
		}
		catch
		{
			measured = text.Length * size * 0.55;
		}

		lock (CacheLock)
		{
			if (WidthCache.Count >= MaxCachedWords)
			{
				WidthCache.Clear();
			}

			WidthCache[key] = measured;
		}

		return measured;
	}

	private static double MeasureWithLayout(TextBlock block, string text)
	{
		try
		{
			TextBlock probe = new()
			{
				Text = text,
				FontFamily = block.FontFamily,
				FontSize = block.FontSize,
				FontStyle = block.FontStyle,
				FontWeight = block.FontWeight,
				FontStretch = block.FontStretch,
				FlowDirection = block.FlowDirection,
				TextWrapping = TextWrapping.NoWrap
			};
			probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
			double width = probe.DesiredSize.Width;
			if (double.IsFinite(width) && width > 0.0)
			{
				return width;
			}
		}
		catch
		{
		}

		return EstimatePortableWidth(text, block.FontSize);
	}

	private static bool ContainsNonAscii(string text)
	{
		foreach (char character in text)
		{
			if (character > 0x7F)
			{
				return true;
			}
		}

		return false;
	}

	private static double EstimatePortableWidth(string text, double size)
	{
		double width = 0.0;
		TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
		while (elements.MoveNext())
		{
			string element = elements.GetTextElement();
			if (string.IsNullOrWhiteSpace(element))
			{
				width += size * 0.35;
				continue;
			}

			int value = char.ConvertToUtf32(element, 0);
			bool wide = value is >= 0x1100 and <= 0x11FF
				or >= 0x2E80 and <= 0xA4CF
				or >= 0xAC00 and <= 0xD7AF
				or >= 0xF900 and <= 0xFAFF
				or >= 0xFE10 and <= 0xFE6F
				or >= 0x1F000 and <= 0x1FAFF;
			width += size * (wide ? 0.95 : 0.58);
		}

		return width;
	}

}
