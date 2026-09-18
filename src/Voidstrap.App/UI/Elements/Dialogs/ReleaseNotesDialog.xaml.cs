using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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

namespace Voidstrap.UI.Elements.Dialogs;

public partial class ReleaseNotesDialog : WpfUiWindow
{
	private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
		.UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
		.UseAutoLinks()
		.UseSoftlineBreakAsHardlineBreak()
		.Build();


	private static readonly System.Windows.Media.FontFamily CodeFont = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Courier New");

	private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

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
		NotesViewer.Document = BuildDocument(release.Body);
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

	private void OnGitHubButtonClick(object sender, RoutedEventArgs e)
	{
		Utilities.ShellExecute(_releaseUrl);
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
		_lifetime.Cancel();
		_lifetime.Dispose();
	}

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTags { get; }
}
