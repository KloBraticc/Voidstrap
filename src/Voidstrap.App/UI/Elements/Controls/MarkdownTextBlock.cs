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
					return new Italic(GetWpfInlineFromMarkdownInline(emphasisInline.FirstChild));
				}
				return new Bold(GetWpfInlineFromMarkdownInline(emphasisInline.FirstChild));
			case '=':
				return new Span(GetWpfInlineFromMarkdownInline(emphasisInline.FirstChild))
				{
					Background = new SolidColorBrush(Color.FromArgb(50, byte.MaxValue, byte.MaxValue, byte.MaxValue))
				};
			}
		}
		else
		{
			if (inline is LinkInline linkInline)
			{
				string url = linkInline.Url;
				Markdig.Syntax.Inlines.Inline firstChild = linkInline.FirstChild;
				if (string.IsNullOrEmpty(url))
				{
					return GetWpfInlineFromMarkdownInline(firstChild);
				}
				return new Hyperlink(GetWpfInlineFromMarkdownInline(firstChild))
				{
					Command = GlobalViewModel.OpenWebpageCommand,
					CommandParameter = url
				};
			}
			if (inline is LineBreakInline)
			{
				return new LineBreak();
			}
		}
		return null;
	}

	private void AddMarkdownInline(Markdig.Syntax.Inlines.Inline? inline)
	{
		System.Windows.Documents.Inline wpfInlineFromMarkdownInline = GetWpfInlineFromMarkdownInline(inline);
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
		MarkdownDocument markdownDocument = Markdown.Parse(markdown, _markdownPipeline);
		markdownTextBlock.Inlines.Clear();
		Markdig.Syntax.Block block = markdownDocument.Last();
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
		EmojiTextRenderer.Refresh(markdownTextBlock);
	}
}
