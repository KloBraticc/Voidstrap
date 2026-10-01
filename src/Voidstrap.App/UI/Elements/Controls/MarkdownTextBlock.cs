using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Voidstrap.UI.ViewModels;

namespace Voidstrap.UI.Elements.Controls;

[ContentProperty("MarkdownText")]
[Localizability(LocalizationCategory.Text)]
internal class MarkdownTextBlock : TextBlock
{
	private static readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder().UseEmphasisExtras(EmphasisExtraOptions.Marked).UseSoftlineBreakAsHardlineBreak().Build();

	private static readonly SolidColorBrush HighlightBrush = CreateHighlightBrush();

	private static readonly System.Buffers.SearchValues<char> MarkdownSyntax = System.Buffers.SearchValues.Create("*_=[]<>`#\\&!|~");

	private static bool IsPlainText(string text)
	{
		if (text.Length == 0)
			return true;
		if (text.AsSpan().IndexOfAny(MarkdownSyntax) >= 0)
			return false;
		foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
		{
			if (line.Length > 0 && (!char.IsLetter(line[0]) || char.IsWhiteSpace(line[^1])))
				return false;
		}
		return true;
	}

	private static SolidColorBrush CreateHighlightBrush()
	{
		SolidColorBrush brush = new(Color.FromArgb(50, byte.MaxValue, byte.MaxValue, byte.MaxValue));
		brush.Freeze();
		return brush;
	}

	public static readonly DependencyProperty MarkdownTextProperty = DependencyProperty.Register("MarkdownText", typeof(string), typeof(MarkdownTextBlock), (PropertyMetadata)(object)new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, new PropertyChangedCallback(OnTextMarkdownChanged)));

	[Localizability(LocalizationCategory.Text)]
	public string MarkdownText
	{
		get
		{
			return (string)((DependencyObject)this).GetValue(MarkdownTextProperty);
		}
		set
		{
			((DependencyObject)this).SetValue(MarkdownTextProperty, (object)value);
		}
	}

	private static System.Windows.Documents.Inline? GetWpfInlineFromMarkdownInline(Markdig.Syntax.Inlines.Inline? inline)
	{
		if (inline is LiteralInline literalInline)
		{
			return new Run(literalInline.ToString());
		}
		if (inline is EmphasisInline { DelimiterChar: var delimiterChar } emphasisInline)
		{
			switch (delimiterChar)
			{
			case '*':
			case '_':
				if (emphasisInline.DelimiterCount == 1)
				{
					return AddChildren(new Italic(), emphasisInline);
				}
				return AddChildren(new Bold(), emphasisInline);
			case '=':
				return AddChildren(new Span
				{
					Background = HighlightBrush
				}, emphasisInline);
			}
		}
		else
		{
			if (inline is LinkInline linkInline)
			{
				string? url = linkInline.Url;
				if (string.IsNullOrEmpty(url))
				{
					return AddChildren(new Span(), linkInline);
				}
				return AddChildren(new Hyperlink
				{
					Command = GlobalViewModel.OpenWebpageCommand,
					CommandParameter = url
				}, linkInline);
			}
			if (inline is LineBreakInline)
			{
				return new LineBreak();
			}
		}
		return null;
	}

	private static Span AddChildren(Span span, ContainerInline container)
	{
		foreach (Markdig.Syntax.Inlines.Inline child in container)
		{
			System.Windows.Documents.Inline? wpfInline = GetWpfInlineFromMarkdownInline(child);
			if (wpfInline != null)
			{
				span.Inlines.Add(wpfInline);
			}
		}
		return span;
	}

	private void AddMarkdownInline(Markdig.Syntax.Inlines.Inline? inline)
	{
		System.Windows.Documents.Inline? wpfInlineFromMarkdownInline = GetWpfInlineFromMarkdownInline(inline);
		if (wpfInlineFromMarkdownInline != null)
		{
			base.Inlines.Add(wpfInlineFromMarkdownInline);
		}
	}

	private static void OnTextMarkdownChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs dependencyPropertyChangedEventArgs)
	{
		if (!(dependencyObject is MarkdownTextBlock markdownTextBlock) || !(dependencyPropertyChangedEventArgs.NewValue is string markdown))
		{
			return;
		}
		if (IsPlainText(markdown))
		{
			markdownTextBlock._portableRich = false;
			markdownTextBlock.Inlines.Clear();
			if (markdown.Length > 0)
			{
				markdownTextBlock.Inlines.Add(new Run(markdown));
			}
			if (!Voidstrap.Utility.Platform.IsWindows)
			{
				Voidstrap.UI.LinuxInlineText.Sanitize(markdownTextBlock);
				Voidstrap.UI.LinuxTextGuard.Refresh(markdownTextBlock);
			}
			return;
		}
		markdownTextBlock._portableRich = !Voidstrap.Utility.Platform.IsWindows;
		markdownTextBlock._portableWrapWidth = double.NaN;
		markdownTextBlock.BuildMarkdownInlines(markdown);
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			Voidstrap.UI.LinuxInlineText.Sanitize(markdownTextBlock);
			Voidstrap.UI.LinuxTextGuard.Refresh(markdownTextBlock);
			double limit = markdownTextBlock.MaxWidth;
			if (!double.IsInfinity(limit) && !double.IsNaN(limit) && limit > PortableMinimumWrapWidth)
			{
				markdownTextBlock._portableWrapWidth = limit;
				markdownTextBlock.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => markdownTextBlock.WrapPortableText(limit)));
			}
		}
	}

	private void BuildMarkdownInlines(string markdown)
	{
		MarkdownDocument markdownDocument = Markdown.Parse(markdown, _markdownPipeline);
		Inlines.Clear();
		Markdig.Syntax.Block? block = markdownDocument.LastOrDefault();
		foreach (Markdig.Syntax.Block item in markdownDocument)
		{
			if (!(item is ParagraphBlock { Inline: not null } paragraphBlock))
			{
				continue;
			}
			foreach (Markdig.Syntax.Inlines.Inline item2 in paragraphBlock.Inline)
			{
				AddMarkdownInline(item2);
			}
			if (item != block)
			{
				AddMarkdownInline(new LineBreakInline());
				AddMarkdownInline(new LineBreakInline());
			}
		}
	}

	private const double PortableWrapSafety = 6.0;

	private const double PortableMinimumWrapWidth = 40.0;

	private bool _portableRich;

	private double _portableWrapWidth = double.NaN;

	static MarkdownTextBlock()
	{
		MaxWidthProperty.OverrideMetadata(typeof(MarkdownTextBlock), new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure, OnPortableMaxWidthChanged));
	}

	private static void OnPortableMaxWidthChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
	{
		if (dependencyObject is not MarkdownTextBlock block || !block._portableRich || e.NewValue is not double limit)
			return;
		if (double.IsInfinity(limit) || double.IsNaN(limit) || limit <= PortableMinimumWrapWidth || Math.Abs(limit - block._portableWrapWidth) < 1.0)
			return;
		block._portableWrapWidth = limit;
		block.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => block.WrapPortableText(limit)));
	}

	private void WrapPortableText(double limit)
	{
		if (!_portableRich || TextWrapping == TextWrapping.NoWrap || Math.Abs(limit - _portableWrapWidth) >= 1.0)
			return;
		try
		{
			BuildMarkdownInlines(MarkdownText);
			double width = limit - Padding.Left - Padding.Right;
			BreakPortableLines(Math.Max(PortableMinimumWrapWidth, width - Math.Max(PortableWrapSafety, width * 0.03)));
			Voidstrap.UI.LinuxInlineText.Sanitize(this);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("MarkdownTextBlock::WrapPortableText", "Could not wrap the text: " + ex.Message);
		}
	}

	private void BreakPortableLines(double width)
	{
		double pixelsPerDip;
		try
		{
			pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
		}
		catch
		{
			pixelsPerDip = 1.0;
		}
		double x = 0.0;
		BreakPortableLines(Inlines, width, ref x, pixelsPerDip);
	}

	private static void BreakPortableLines(InlineCollection inlines, double width, ref double x, double pixelsPerDip)
	{
		foreach (System.Windows.Documents.Inline inline in inlines.ToList())
		{
			switch (inline)
			{
			case LineBreak:
				x = 0.0;
				break;
			case Span span:
				BreakPortableLines(span.Inlines, width, ref x, pixelsPerDip);
				break;
			case Run run:
				BreakPortableRun(inlines, run, width, ref x, pixelsPerDip);
				break;
			default:
				x += Math.Max(0.0, inline is InlineUIContainer { Child: FrameworkElement child } ? child.ActualWidth : 0.0);
				break;
			}
		}
	}

	private static void BreakPortableRun(InlineCollection owner, Run run, double width, ref double x, double pixelsPerDip)
	{
		string text = run.Text;
		if (string.IsNullOrEmpty(text))
			return;

		Typeface typeface = new(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch);
		double size = run.FontSize;
		List<string> lines = [];
		StringBuilder line = new();
		foreach (string token in Regex.Split(text, @"(\s+)"))
		{
			if (token.Length == 0)
				continue;
			bool space = char.IsWhiteSpace(token[0]);
			if (space && x <= 0.0)
				continue;
			double tokenWidth = new FormattedText(token, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, pixelsPerDip).WidthIncludingTrailingWhitespace;
			if (!space && x > 0.0 && x + tokenWidth > width)
			{
				lines.Add(line.ToString().TrimEnd());
				line.Clear();
				x = 0.0;
			}
			line.Append(token);
			x += tokenWidth;
		}
		lines.Add(line.ToString());

		if (lines.Count == 1)
		{
			if (!string.Equals(lines[0], text, StringComparison.Ordinal))
				run.Text = lines[0];
			return;
		}

		run.Text = lines[0];
		System.Windows.Documents.Inline anchor = run;
		for (int index = 1; index < lines.Count; index++)
		{
			LineBreak lineBreak = new();
			owner.InsertAfter(anchor, lineBreak);
			Run next = new(lines[index]);
			owner.InsertAfter(lineBreak, next);
			anchor = next;
		}
	}
}
