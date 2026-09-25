using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Voidstrap.Models.APIs.GitHub;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.ViewModels;
using DocumentInline = System.Windows.Documents.Inline;
using DocumentList = System.Windows.Documents.List;
using MarkdownBlock = Markdig.Syntax.Block;
using MarkdownInline = Markdig.Syntax.Inlines.Inline;
using WpfControls = System.Windows.Controls;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class ReleaseNotesDialog : WpfUiWindow
{
	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
		.UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
		.UseAutoLinks()
		.UseSoftlineBreakAsHardlineBreak()
		.Build();


	private static readonly System.Windows.Media.FontFamily CodeFont = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New");

	private static readonly bool UseNativeBlocks = !Voidstrap.Utility.Platform.IsWindows;

	private const int MaxLinksPerBlock = 8;

	private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

	private readonly List<WpfControls.Button> _linkButtons = new List<WpfControls.Button>();

	private bool _textFlowQueued;

	private string _releaseUrl = App.ProjectDownloadLink;

	public ReleaseNotesDialog()
	{
		InitializeComponent();
		GitHubButton.Click += OnGitHubButtonClick;
		CloseButton.Click += OnCloseButtonClick;
		Loaded += OnLoaded;
		Closed += OnClosed;
	}

	private async void OnLoaded(object sender, RoutedEventArgs e)
	{
		Loaded -= OnLoaded;
		try
		{
			(GithubRelease? release, bool isCurrent) = await ResolveReleaseAsync(_lifetime.Token);
			if (_lifetime.IsCancellationRequested)
			{
				return;
			}
			if (release == null)
			{
				ShowUnavailable();
				return;
			}
			ShowRelease(release, isCurrent);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ReleaseNotesDialog::Load", ex);
			if (!_lifetime.IsCancellationRequested)
			{
				ShowUnavailable();
			}
		}
	}

	private static async Task<(GithubRelease? Release, bool IsCurrent)> ResolveReleaseAsync(CancellationToken token)
	{
		Version? current = Normalize(Utilities.GetVersionFromString(App.Version));
		if (current != null)
		{
			List<GithubRelease>? releases = await Voidstrap.Utility.GitHubCache.GetJsonWithFallbackAsync<List<GithubRelease>>(App.ProjectReleaseListApi, App.ProjectFallbackReleaseListApi, TimeSpan.FromMinutes(15), token);
			GithubRelease? match = releases?.FirstOrDefault(release => IsReleaseFor(release, current));
			Version? oldestListed = releases?.Select(release => Normalize(Utilities.GetVersionFromString(release?.TagName))).Where(version => version != null).Min();
			if (match == null && (oldestListed == null || current < oldestListed))
			{
				foreach (string tag in new[] { "v" + current, current.ToString() })
				{
					GithubRelease? tagged = await Voidstrap.Utility.GitHubCache.GetJsonAsync<GithubRelease>("https://api.github.com/repos" + App.ProjectRepository + "releases/tags/" + tag, TimeSpan.FromMinutes(15), token);
					if (IsReleaseFor(tagged, current))
					{
						match = tagged;
						break;
					}
				}
			}
			if (match != null)
			{
				return (match, true);
			}
		}
		return (await App.GetLatestRelease(), false);
	}

	private static bool IsReleaseFor(GithubRelease? release, Version current)
	{
		return release != null && !release.Draft && Normalize(Utilities.GetVersionFromString(release.TagName)) == current;
	}

	private static Version? Normalize(Version? version)
	{
		return version == null ? null : new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
	}

	private void ShowRelease(GithubRelease release, bool isCurrent)
	{
		string tag = release.TagName ?? string.Empty;
		ReleaseTitleText.Text = string.IsNullOrWhiteSpace(release.Name) ? "Voidstrap " + tag : release.Name.Trim();
		List<string> details = new List<string>();
		if (tag.Length > 0)
		{
			details.Add(tag);
		}
		if (DateTimeOffset.TryParse(release.PublishedAt ?? release.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset published))
		{
			details.Add("Released " + published.ToLocalTime().ToString("MMMM d, yyyy", CultureInfo.CurrentCulture));
		}
		if (release.Prerelease)
		{
			details.Add("Prerelease");
		}
		details.Add(isCurrent ? "Your version" : "Latest release");
		ReleaseDetailsText.Text = string.Join("  •  ", details);
		if (!isCurrent)
		{
			FallbackNoticeText.Text = "Voidstrap " + App.Version + " has no published release notes, so the notes for the latest release are shown instead.";
			FallbackNotice.Visibility = Visibility.Visible;
		}
		if (UseNativeBlocks)
		{
			ShowNativeNotes(release.Body);
		}
		else
		{
			NotesViewer.Document = BuildDocument(release.Body);
		}
		if (!string.IsNullOrWhiteSpace(release.HtmlUrl))
		{
			_releaseUrl = release.HtmlUrl;
		}
		GitHubButton.IsEnabled = true;
	}

	private void ShowUnavailable()
	{
		ReleaseTitleText.Text = "Release notes are unavailable";
		ReleaseDetailsText.Text = "Voidstrap could not load the release notes from GitHub. Check your connection and try again.";
		GitHubButton.IsEnabled = true;
	}

	private FlowDocument BuildDocument(string? markdown)
	{
		FlowDocument document = new FlowDocument
		{
			PagePadding = new Thickness(18, 14, 18, 14),
			TextAlignment = TextAlignment.Left,
			FontFamily = FontFamily,
			FontSize = 14,
			LineHeight = 22
		};
		document.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorPrimaryBrush");
		string text = string.IsNullOrWhiteSpace(markdown) ? "This release does not include any notes." : markdown;
		foreach (MarkdownBlock block in Markdown.Parse(text, Pipeline))
		{
			AddBlock(document.Blocks, block, 0, false);
		}
		return document;
	}

	private static void AddBlock(BlockCollection target, MarkdownBlock block, int depth, bool compact)
	{
		switch (block)
		{
			case HeadingBlock heading:
			{
				Paragraph paragraph = new Paragraph
				{
					FontSize = heading.Level switch { 1 => 22.0, 2 => 19.0, 3 => 16.5, _ => 15.0 },
					FontWeight = FontWeights.SemiBold,
					LineHeight = double.NaN,
					Margin = new Thickness(0, target.Count == 0 ? 0 : 16, 0, 6)
				};
				AddInlines(paragraph.Inlines, heading.Inline);
				target.Add(paragraph);
				break;
			}
			case ParagraphBlock paragraphBlock:
			{
				Paragraph paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, compact ? 2 : 10) };
				AddInlines(paragraph.Inlines, paragraphBlock.Inline);
				target.Add(paragraph);
				break;
			}
			case ListBlock listBlock:
			{
				DocumentList list = new DocumentList
				{
					MarkerStyle = listBlock.IsOrdered ? TextMarkerStyle.Decimal : depth % 2 == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle,
					Margin = new Thickness(0, compact ? 2 : 0, 0, compact ? 2 : 10),
					Padding = new Thickness(24, 0, 0, 0)
				};
				if (listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, out int start) && start > 0)
				{
					list.StartIndex = start;
				}
				foreach (MarkdownBlock item in listBlock)
				{
					ListItem listItem = new ListItem();
					if (item is ContainerBlock itemBlocks)
					{
						foreach (MarkdownBlock child in itemBlocks)
						{
							AddBlock(listItem.Blocks, child, depth + 1, true);
						}
					}
					list.ListItems.Add(listItem);
				}
				target.Add(list);
				break;
			}
			case QuoteBlock quote:
			{
				Section section = new Section
				{
					BorderThickness = new Thickness(3, 0, 0, 0),
					Padding = new Thickness(12, 2, 0, 2),
					Margin = new Thickness(0, 0, 0, compact ? 2 : 10)
				};
				section.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
				section.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
				foreach (MarkdownBlock child in quote)
				{
					AddBlock(section.Blocks, child, depth, true);
				}
				target.Add(section);
				break;
			}
			case CodeBlock code:
			{
				Paragraph paragraph = new Paragraph(new Run(code.Lines.ToString().TrimEnd()))
				{
					FontFamily = CodeFont,
					FontSize = 13,
					LineHeight = 19,
					Padding = new Thickness(12, 9, 12, 9),
					Margin = new Thickness(0, 0, 0, compact ? 4 : 10)
				};
				paragraph.SetResourceReference(TextElement.BackgroundProperty, "ControlFillColorSecondaryBrush");
				target.Add(paragraph);
				break;
			}
			case HtmlBlock html:
			{
				string plain = WebUtility.HtmlDecode(HtmlTags.Replace(html.Lines.ToString(), string.Empty)).Trim();
				if (plain.Length > 0)
				{
					target.Add(new Paragraph(new Run(plain)) { Margin = new Thickness(0, 0, 0, compact ? 2 : 10) });
				}
				break;
			}
			case ThematicBreakBlock:
			{
				Paragraph divider = new Paragraph
				{
					BorderThickness = new Thickness(0, 0, 0, 1),
					Margin = new Thickness(0, 6, 0, 14),
					FontSize = 1,
					LineHeight = 1
				};
				divider.SetResourceReference(System.Windows.Documents.Block.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
				target.Add(divider);
				break;
			}
			case LeafBlock leaf when leaf.Inline != null:
			{
				Paragraph paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, compact ? 2 : 10) };
				AddInlines(paragraph.Inlines, leaf.Inline);
				target.Add(paragraph);
				break;
			}
			case ContainerBlock container:
			{
				foreach (MarkdownBlock child in container)
				{
					AddBlock(target, child, depth, compact);
				}
				break;
			}
		}
	}

	private static void AddInlines(InlineCollection target, ContainerInline? container)
	{
		if (container == null)
		{
			return;
		}
		foreach (MarkdownInline inline in container)
		{
			DocumentInline? converted = ConvertInline(inline);
			if (converted != null)
			{
				target.Add(converted);
			}
		}
	}

	private static DocumentInline? ConvertInline(MarkdownInline inline)
	{
		switch (inline)
		{
			case LiteralInline literal:
				return new Run(literal.Content.ToString());
			case LineBreakInline:
				return new LineBreak();
			case CodeInline code:
			{
				Run run = new Run(code.Content.ToString()) { FontFamily = CodeFont };
				run.SetResourceReference(TextElement.BackgroundProperty, "ControlFillColorSecondaryBrush");
				return run;
			}
			case HtmlEntityInline entity:
				return new Run(entity.Transcoded.ToString());
			case AutolinkInline autolink:
				return CreateLink(new Run(autolink.Url), autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url);
			case LinkInline link:
			{
				Span content = new Span();
				AddInlines(content.Inlines, link);
				if (content.Inlines.Count == 0)
				{
					content.Inlines.Add(new Run(link.IsImage ? "Image" : link.Url ?? string.Empty));
				}
				return CreateLink(content, link.Url);
			}
			case EmphasisInline emphasis:
			{
				Span span;
				if (emphasis.DelimiterChar == '~')
				{
					span = new Span { TextDecorations = TextDecorations.Strikethrough };
				}
				else if (emphasis.DelimiterCount >= 2)
				{
					span = new Bold();
				}
				else
				{
					span = new Italic();
				}
				AddInlines(span.Inlines, emphasis);
				return span;
			}
			case ContainerInline container:
			{
				Span span = new Span();
				AddInlines(span.Inlines, container);
				return span;
			}
			default:
				return null;
		}
	}

	private static DocumentInline CreateLink(DocumentInline content, string? url)
	{
		string target = (url ?? string.Empty).Trim();
		if (target.StartsWith('/'))
		{
			target = "https://github.com" + target;
		}
		if (!Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeMailto))
		{
			return content;
		}
		return new Hyperlink(content)
		{
			Command = GlobalViewModel.OpenWebpageCommand,
			CommandParameter = uri.AbsoluteUri,
			ToolTip = uri.AbsoluteUri
		};
	}

	private void ShowNativeNotes(string? markdown)
	{
		NotesViewer.Visibility = Visibility.Collapsed;
		NotesScroll.Visibility = Visibility.Visible;
		ClearLinkButtons();
		NotesPanel.Children.Clear();
		string text = string.IsNullOrWhiteSpace(markdown) ? "This release does not include any notes." : markdown;
		foreach (MarkdownBlock block in Markdown.Parse(text, Pipeline))
		{
			AddNativeBlock(NotesPanel.Children, block, 0, false, "TextFillColorPrimaryBrush");
		}
		NotesScroll.SizeChanged -= OnNotesSizeChanged;
		NotesScroll.SizeChanged += OnNotesSizeChanged;
		QueueNativeTextFlow();
	}

	private void OnNotesSizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (e.WidthChanged)
		{
			QueueNativeTextFlow();
		}
	}

	private void QueueNativeTextFlow()
	{
		if (_textFlowQueued)
		{
			return;
		}
		_textFlowQueued = true;
		Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(ApplyNativeTextFlow));
	}

	private void ApplyNativeTextFlow()
	{
		_textFlowQueued = false;
		if (_lifetime.IsCancellationRequested)
		{
			return;
		}
		Voidstrap.UI.LinuxTextGuard.CorrectOwner(NotesPanel, this);
	}

	private void AddNativeBlock(WpfControls.UIElementCollection target, MarkdownBlock block, int depth, bool compact, string brushKey)
	{
		switch (block)
		{
			case HeadingBlock heading:
			{
				List<(string Text, string Url)> links = new List<(string, string)>();
				double size = heading.Level switch { 1 => 22.0, 2 => 19.0, 3 => 16.5, _ => 15.0 };
				target.Add(CreateText(Flatten(heading.Inline, links), size, FontWeights.SemiBold, new Thickness(0, target.Count == 0 ? 0 : 16, 0, 6), brushKey, double.NaN));
				AddLinkButtons(target, links, compact);
				break;
			}
			case ParagraphBlock paragraphBlock:
			{
				List<(string Text, string Url)> links = new List<(string, string)>();
				target.Add(CreateText(Flatten(paragraphBlock.Inline, links), 14, FontWeights.Normal, new Thickness(0, 0, 0, compact ? 2 : 10), brushKey, 22));
				AddLinkButtons(target, links, compact);
				break;
			}
			case ListBlock listBlock:
			{
				WpfControls.StackPanel list = new WpfControls.StackPanel { Margin = new Thickness(0, compact ? 2 : 0, 0, compact ? 2 : 10) };
				int number = listBlock.IsOrdered && int.TryParse(listBlock.OrderedStart, out int start) && start > 0 ? start : 1;
				foreach (MarkdownBlock item in listBlock)
				{
					string marker = listBlock.IsOrdered ? number++ + "." : depth % 2 == 0 ? "•" : "◦";
					WpfControls.Grid row = new WpfControls.Grid { Margin = new Thickness(0, 0, 0, 2) };
					row.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new GridLength(24) });
					row.ColumnDefinitions.Add(new WpfControls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
					row.Children.Add(CreateText(marker, 14, FontWeights.Normal, new Thickness(0), brushKey, 22));
					WpfControls.StackPanel content = new WpfControls.StackPanel();
					WpfControls.Grid.SetColumn(content, 1);
					if (item is ContainerBlock itemBlocks)
					{
						foreach (MarkdownBlock child in itemBlocks)
						{
							AddNativeBlock(content.Children, child, depth + 1, true, brushKey);
						}
					}
					row.Children.Add(content);
					list.Children.Add(row);
				}
				target.Add(list);
				break;
			}
			case QuoteBlock quote:
			{
				WpfControls.Border border = new WpfControls.Border
				{
					BorderThickness = new Thickness(3, 0, 0, 0),
					Padding = new Thickness(12, 2, 0, 2),
					Margin = new Thickness(0, 0, 0, compact ? 2 : 10)
				};
				border.SetResourceReference(WpfControls.Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
				WpfControls.StackPanel content = new WpfControls.StackPanel();
				foreach (MarkdownBlock child in quote)
				{
					AddNativeBlock(content.Children, child, depth, true, "TextFillColorSecondaryBrush");
				}
				border.Child = content;
				target.Add(border);
				break;
			}
			case CodeBlock code:
			{
				WpfControls.Border border = new WpfControls.Border
				{
					CornerRadius = new CornerRadius(4),
					Padding = new Thickness(12, 9, 12, 9),
					Margin = new Thickness(0, 0, 0, compact ? 4 : 10)
				};
				border.SetResourceReference(WpfControls.Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
				WpfControls.TextBlock text = CreateText(code.Lines.ToString().TrimEnd(), 13, FontWeights.Normal, new Thickness(0), brushKey, 19);
				text.FontFamily = CodeFont;
				border.Child = text;
				target.Add(border);
				break;
			}
			case HtmlBlock html:
			{
				string plain = WebUtility.HtmlDecode(HtmlTags.Replace(html.Lines.ToString(), string.Empty)).Trim();
				if (plain.Length > 0)
				{
					target.Add(CreateText(plain, 14, FontWeights.Normal, new Thickness(0, 0, 0, compact ? 2 : 10), brushKey, 22));
				}
				break;
			}
			case ThematicBreakBlock:
			{
				WpfControls.Border divider = new WpfControls.Border { Height = 1, Margin = new Thickness(0, 6, 0, 14) };
				divider.SetResourceReference(WpfControls.Border.BackgroundProperty, "ControlStrokeColorDefaultBrush");
				target.Add(divider);
				break;
			}
			case LeafBlock leaf when leaf.Inline != null:
			{
				List<(string Text, string Url)> links = new List<(string, string)>();
				target.Add(CreateText(Flatten(leaf.Inline, links), 14, FontWeights.Normal, new Thickness(0, 0, 0, compact ? 2 : 10), brushKey, 22));
				AddLinkButtons(target, links, compact);
				break;
			}
			case ContainerBlock container:
			{
				foreach (MarkdownBlock child in container)
				{
					AddNativeBlock(target, child, depth, compact, brushKey);
				}
				break;
			}
		}
	}

	private static WpfControls.TextBlock CreateText(string text, double size, FontWeight weight, Thickness margin, string brushKey, double lineHeight)
	{
		WpfControls.TextBlock block = new WpfControls.TextBlock
		{
			Text = text,
			FontSize = size,
			FontWeight = weight,
			Margin = margin,
			TextWrapping = TextWrapping.Wrap,
			LineHeight = lineHeight
		};
		block.SetResourceReference(WpfControls.TextBlock.ForegroundProperty, brushKey);
		return block;
	}

	private static string Flatten(ContainerInline? container, List<(string Text, string Url)> links)
	{
		StringBuilder builder = new StringBuilder();
		AppendFlattened(builder, container, links);
		return builder.ToString().Trim();
	}

	private static void AppendFlattened(StringBuilder builder, ContainerInline? container, List<(string Text, string Url)> links)
	{
		if (container == null)
		{
			return;
		}
		foreach (MarkdownInline inline in container)
		{
			switch (inline)
			{
				case LiteralInline literal:
					builder.Append(literal.Content.ToString());
					break;
				case LineBreakInline:
					builder.Append('\n');
					break;
				case CodeInline code:
					builder.Append(code.Content.ToString());
					break;
				case HtmlEntityInline entity:
					builder.Append(entity.Transcoded.ToString());
					break;
				case AutolinkInline autolink:
					builder.Append(autolink.Url);
					if (!autolink.IsEmail)
					{
						AddLink(links, autolink.Url, autolink.Url);
					}
					break;
				case LinkInline link:
				{
					int before = builder.Length;
					AppendFlattened(builder, link, links);
					string label = builder.ToString(before, builder.Length - before).Trim();
					if (!link.IsImage)
					{
						AddLink(links, label, link.Url);
					}
					break;
				}
				case ContainerInline nested:
					AppendFlattened(builder, nested, links);
					break;
			}
		}
	}

	private static void AddLink(List<(string Text, string Url)> links, string label, string? url)
	{
		string target = (url ?? string.Empty).Trim();
		if (target.StartsWith('/'))
		{
			target = "https://github.com" + target;
		}
		if (!Utilities.IsWebLink(target) || links.Count >= MaxLinksPerBlock || links.Exists(link => string.Equals(link.Url, target, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}
		links.Add((label, target));
	}

	private void AddLinkButtons(WpfControls.UIElementCollection target, List<(string Text, string Url)> links, bool compact)
	{
		if (links.Count == 0)
		{
			return;
		}
		WpfControls.WrapPanel panel = new WpfControls.WrapPanel { Margin = new Thickness(0, -4, 0, compact ? 4 : 10) };
		foreach ((string text, string url) in links)
		{
			WpfControls.Button button = new WpfControls.Button
			{
				Content = LinkLabel(text, url),
				Tag = url,
				ToolTip = url,
				Padding = new Thickness(8, 3, 8, 3),
				Margin = new Thickness(0, 4, 6, 0),
				FontSize = 12,
				BorderThickness = new Thickness(1),
				Cursor = System.Windows.Input.Cursors.Hand
			};
			button.SetResourceReference(WpfControls.Control.ForegroundProperty, "AccentFillColorPrimaryBrush");
			button.SetResourceReference(WpfControls.Control.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
			button.SetResourceReference(WpfControls.Control.BorderBrushProperty, "AccentFillColorPrimaryBrush");
			button.Click += OnLinkButtonClick;
			_linkButtons.Add(button);
			panel.Children.Add(button);
		}
		target.Add(panel);
	}

	private static string LinkLabel(string text, string url)
	{
		if (text.Length > 0 && !string.Equals(text, url, StringComparison.OrdinalIgnoreCase))
		{
			return text.Length > 60 ? text.Substring(0, 57) + "..." : text;
		}
		if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
		{
			return url;
		}
		string[] segments = uri.AbsolutePath.Trim('/').Split('/');
		if (uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) && segments.Length >= 4 && (segments[2] == "pull" || segments[2] == "issues"))
		{
			return "#" + segments[3];
		}
		if (uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase) && segments.Length >= 4 && segments[2] == "compare")
		{
			return "Changes " + segments[3];
		}
		string shortened = uri.Host + uri.AbsolutePath.TrimEnd('/');
		return shortened.Length > 60 ? shortened.Substring(0, 57) + "..." : shortened;
	}

	private void OnLinkButtonClick(object sender, RoutedEventArgs e)
	{
		if (sender is FrameworkElement { Tag: string url })
		{
			Utilities.OpenWebLink(url);
		}
	}

	private void ClearLinkButtons()
	{
		foreach (WpfControls.Button button in _linkButtons)
		{
			button.Click -= OnLinkButtonClick;
		}
		_linkButtons.Clear();
	}

	private void OnGitHubButtonClick(object sender, RoutedEventArgs e)
	{
		Utilities.OpenWebLink(_releaseUrl);
	}

	private void OnCloseButtonClick(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		Closed -= OnClosed;
		Loaded -= OnLoaded;
		GitHubButton.Click -= OnGitHubButtonClick;
		CloseButton.Click -= OnCloseButtonClick;
		NotesScroll.SizeChanged -= OnNotesSizeChanged;
		ClearLinkButtons();
		_lifetime.Cancel();
		_lifetime.Dispose();
	}

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTags { get; }
}
