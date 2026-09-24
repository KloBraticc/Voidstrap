using System.Linq;
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
		MarkdownDocument markdownDocument = Markdown.Parse(markdown, _markdownPipeline);
		markdownTextBlock.Inlines.Clear();
		Markdig.Syntax.Block? block = markdownDocument.LastOrDefault();
		foreach (Markdig.Syntax.Block item in markdownDocument)
		{
			if (!(item is ParagraphBlock { Inline: not null } paragraphBlock))
			{
				continue;
			}
			foreach (Markdig.Syntax.Inlines.Inline item2 in paragraphBlock.Inline)
			{
				markdownTextBlock.AddMarkdownInline(item2);
			}
			if (item != block)
			{
				markdownTextBlock.AddMarkdownInline(new LineBreakInline());
				markdownTextBlock.AddMarkdownInline(new LineBreakInline());
			}
		}
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			Voidstrap.UI.LinuxInlineText.Sanitize(markdownTextBlock);
			Voidstrap.UI.LinuxTextGuard.Refresh(markdownTextBlock);
		}
	}
}
