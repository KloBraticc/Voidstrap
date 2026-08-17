using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Controls
{
    public sealed class ForumRichContent : StackPanel
    {
        private static readonly Regex YoutubeRegex = new Regex(@"(?:https?://)?(?:www\.)?(?:youtube\.com/watch\?(?:[^\s""']*&)?v=|youtu\.be/|youtube\.com/embed/|youtube\.com/shorts/)([a-zA-Z0-9_-]{11})(?:[^\s""']*)?", RegexOptions.Compiled);
        private static readonly Regex ImgTokenRegex = new Regex(@"\[\[img:(\d+)\]\]", RegexOptions.Compiled);
        private static readonly Regex CodeFenceRegex = new Regex(@"```(\w*)\s*\n?([\s\S]*?)```", RegexOptions.Compiled);
        private static readonly Regex LinkRegex = new Regex(@"\[([^\]\n]+)\]\((https?://[^\s)]+)\)", RegexOptions.Compiled);

        private static readonly Regex SpoilerRegex = new Regex(@"\|\|([^|]+?)\|\|", RegexOptions.Compiled);

        private static readonly Regex BoldRegex = new Regex(@"\*\*([^*]+?)\*\*", RegexOptions.Compiled);

        private static readonly Regex UnderlineRegex = new Regex(@"__([^_]+?)__", RegexOptions.Compiled);

        private static readonly Regex StrikeRegex = new Regex(@"~~([^~]+?)~~", RegexOptions.Compiled);

        private static readonly Regex InlineCodeRegex = new Regex(@"`([^`\n]+)`", RegexOptions.Compiled);

        private static readonly Regex ItalicStarRegex = new Regex(@"(^|[^*])\*([^*\n]+?)\*(?!\*)", RegexOptions.Compiled);

        private static readonly Regex ItalicUnderscoreRegex = new Regex(@"(^|[^_\w])_([^_\n]+?)_(?![_\w])", RegexOptions.Compiled);

        private static readonly Brush BorderGray = Frozen(0x3F, 0x3F, 0x46);
        private static readonly Brush CodeBackground = Frozen(0x26, 0x26, 0x2B);
        private static readonly Brush CodeBlockBackground = Frozen(0x1B, 0x1B, 0x1F);
        private static readonly Brush SpoilerBackground = Frozen(0x2E, 0x2E, 0x33);
        private static readonly Brush QuoteBar = Frozen(0x52, 0x52, 0x5B);
        private static readonly Brush LinkBlue = Frozen(0x38, 0xBD, 0xF8);
        private static readonly Brush MentionBlue = Frozen(0x81, 0x8C, 0xF8);
        private static readonly Brush HeadingWhite = Frozen(0xF5, 0xF5, 0xF5);
        private static readonly Brush BodyGray = Frozen(0xD4, 0xD4, 0xD8);
        private static readonly Brush FaintGray = Frozen(0x8A, 0x8A, 0x93);

        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        public static readonly DependencyProperty PostProperty = DependencyProperty.Register(
            nameof(Post), typeof(ForumPost), typeof(ForumRichContent), new PropertyMetadata(null, OnPostChanged));

        public ForumPost? Post
        {
            get => (ForumPost?)GetValue(PostProperty);
            set => SetValue(PostProperty, value);
        }

        private static void OnPostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((ForumRichContent)d).Rebuild();
        }

        private void Rebuild()
        {
            Children.Clear();
            var post = Post;
            if (post == null)
                return;
            try
            {
                var usedImages = new HashSet<int>();
                RenderContent(post, usedImages);
                var leftover = new List<int>();
                for (int i = 0; i < post.ImageSlots.Count; i++)
                {
                    if (!usedImages.Contains(i))
                        leftover.Add(i);
                }
                if (leftover.Count > 0)
                {
                    var wrap = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
                    foreach (int idx in leftover)
                        wrap.Children.Add(BuildImageFigure(post, idx, new Thickness(0, 4, 8, 0)));
                    Children.Add(wrap);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("ForumRichContent", "Render failed: " + ex.GetType().Name);
                Children.Clear();
                Children.Add(new TextBlock
                {
                    Text = post.Content,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = BodyGray,
                });
            }
        }

        private void RenderContent(ForumPost post, HashSet<int> usedImages)
        {
            string text = post.Content ?? "";
            var tokens = new List<(int Start, int End, string Type, string Value)>();
            int ytCount = 0;
            foreach (Match m in YoutubeRegex.Matches(text))
                tokens.Add((m.Index, m.Index + m.Length, "yt", m.Groups[1].Value));
            foreach (Match m in ImgTokenRegex.Matches(text))
                tokens.Add((m.Index, m.Index + m.Length, "img", m.Groups[1].Value));
            tokens.Sort((a, b) => a.Start.CompareTo(b.Start));

            int last = 0;
            bool renderedAny = false;
            foreach (var token in tokens)
            {
                if (token.Start < last)
                    continue;
                string before = text.Substring(last, token.Start - last);
                if (before.Length > 0)
                {
                    RenderMarkdown(post, before);
                    renderedAny = true;
                }
                if (token.Type == "yt")
                {
                    if (ytCount < 3)
                    {
                        Children.Add(BuildYoutubeCard(token.Value));
                        ytCount++;
                    }
                    else
                    {
                        RenderMarkdown(post, text.Substring(token.Start, token.End - token.Start));
                    }
                    renderedAny = true;
                }
                else if (int.TryParse(token.Value, out int idx) && idx >= 0 && idx < post.ImageSlots.Count)
                {
                    Children.Add(BuildImageFigure(post, idx, new Thickness(0, 8, 0, 0)));
                    usedImages.Add(idx);
                    renderedAny = true;
                }
                last = token.End;
            }
            string rest = text.Substring(last);
            if (rest.Length > 0 || !renderedAny)
                RenderMarkdown(post, rest);
        }

        private static bool LooksStructured(string text)
        {
            string t = text.Trim();
            if (t.Length == 0)
                return false;
            string[] lines = t.Split('\n');
            if (lines.Length < 2)
                return false;
            char f = t[0];
            char lc = t[t.Length - 1];
            bool braced = (f == '{' && lc == '}') || (f == '[' && lc == ']');
            int kvCount = 0;
            int dataLines = 0;
            foreach (string raw in lines)
            {
                string ln = raw.Trim();
                if (ln.Length == 0)
                    continue;
                if (Regex.IsMatch(ln, @"^[{}\[\]],?\s*$"))
                    continue;
                dataLines++;
                if (Regex.IsMatch(ln, "^\"[^\"]+\"\\s*:\\s*") || Regex.IsMatch(ln, @"^[a-zA-Z_]\w+:\s"))
                    kvCount++;
            }
            if (braced)
                return kvCount > 0;
            return dataLines > 0 && kvCount * 2 >= dataLines && kvCount >= 2;
        }

        private void RenderMarkdown(ForumPost post, string raw)
        {
            string[] paras = Regex.Split(raw, @"\n\n+");
            for (int pi = 0; pi < paras.Length; pi++)
            {
                if (LooksStructured(paras[pi]) && !paras[pi].Contains("```"))
                    paras[pi] = "```\n" + paras[pi] + "\n```";
            }
            raw = string.Join("\n\n", paras);

            var codeBlocks = new List<(string Lang, string Code)>();
            raw = CodeFenceRegex.Replace(raw, m =>
            {
                codeBlocks.Add((m.Groups[1].Value, m.Groups[2].Value));
                return "B" + (codeBlocks.Count - 1) + "\n";
            });

            string[] lines = raw.Split('\n');
            var para = new List<string>();

            void Flush()
            {
                if (para.Count == 0)
                    return;
                var tb = NewBodyText();
                for (int j = 0; j < para.Count; j++)
                {
                    if (j > 0)
                        tb.Inlines.Add(new LineBreak());
                    AddInlines(tb.Inlines, para[j], post);
                }
                Children.Add(tb);
                para.Clear();
            }

            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i];
                Match cb = Regex.Match(line, "^B(\\d+)\\s*$");
                if (cb.Success)
                {
                    Flush();
                    int bi = int.Parse(cb.Groups[1].Value);
                    if (bi >= 0 && bi < codeBlocks.Count)
                        Children.Add(BuildCodeBlock(codeBlocks[bi].Code, codeBlocks[bi].Lang));
                    i++;
                    continue;
                }
                Match dm = Regex.Match(line, @"^(\-{4,})\s*$");
                if (dm.Success)
                {
                    Flush();
                    if (dm.Groups[1].Value.Length == 4)
                        Children.Add(new Border { Height = 8 });
                    else
                        Children.Add(new Border { Height = 1, Background = BorderGray, Margin = new Thickness(0, 14, 0, 14) });
                    i++;
                    continue;
                }
                Match h = Regex.Match(line, @"^(#{1,3})\s+(.+)$");
                if (h.Success)
                {
                    Flush();
                    int lvl = h.Groups[1].Value.Length;
                    var tb = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        FontWeight = FontWeights.Bold,
                        FontSize = lvl == 1 ? 20 : lvl == 2 ? 17 : 15,
                        Foreground = HeadingWhite,
                        Margin = new Thickness(0, lvl == 3 ? 8 : 12, 0, 4),
                    };
                    AddInlines(tb.Inlines, h.Groups[2].Value, post);
                    Children.Add(tb);
                    i++;
                    continue;
                }
                if (Regex.IsMatch(line, @"^>\s?"))
                {
                    Flush();
                    var quote = NewBodyText();
                    bool first = true;
                    while (i < lines.Length && Regex.IsMatch(lines[i], @"^>\s?"))
                    {
                        if (!first)
                            quote.Inlines.Add(new LineBreak());
                        AddInlines(quote.Inlines, Regex.Replace(lines[i], @"^>\s?", ""), post);
                        first = false;
                        i++;
                    }
                    Children.Add(new Border
                    {
                        BorderBrush = QuoteBar,
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        Padding = new Thickness(12, 2, 0, 2),
                        Margin = new Thickness(0, 6, 0, 6),
                        Child = quote,
                    });
                    continue;
                }
                if (Regex.IsMatch(line, @"^[-*]\s+"))
                {
                    Flush();
                    var listPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
                    while (i < lines.Length && Regex.IsMatch(lines[i], @"^[-*]\s+"))
                    {
                        listPanel.Children.Add(BuildListRow("•", Regex.Replace(lines[i], @"^[-*]\s+", ""), post));
                        i++;
                    }
                    Children.Add(listPanel);
                    continue;
                }
                if (Regex.IsMatch(line, @"^\d+\.\s+"))
                {
                    Flush();
                    var listPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 6) };
                    int number = 1;
                    while (i < lines.Length && Regex.IsMatch(lines[i], @"^\d+\.\s+"))
                    {
                        listPanel.Children.Add(BuildListRow(number + ".", Regex.Replace(lines[i], @"^\d+\.\s+", ""), post));
                        number++;
                        i++;
                    }
                    Children.Add(listPanel);
                    continue;
                }
                if (line.Trim().Length == 0)
                {
                    Flush();
                    i++;
                    continue;
                }
                para.Add(line);
                i++;
            }
            Flush();
        }

        private static TextBlock NewBodyText()
        {
            return new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = BodyGray,
                Margin = new Thickness(0, 3, 0, 3),
            };
        }

        private Grid BuildListRow(string bullet, string content, ForumPost post)
        {
            var grid = new Grid { Margin = new Thickness(6, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var marker = new TextBlock
            {
                Text = bullet,
                FontSize = 13,
                Foreground = BodyGray,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            var body = NewBodyText();
            body.Margin = new Thickness(0);
            AddInlines(body.Inlines, content, post);
            Grid.SetColumn(marker, 0);
            Grid.SetColumn(body, 1);
            grid.Children.Add(marker);
            grid.Children.Add(body);
            return grid;
        }

        private sealed class Segment
        {
            public string Text = "";
            public bool Bold;
            public bool Italic;
            public bool Underline;
            public bool Strike;
            public bool Code;
            public bool Spoiler;
            public string LinkUrl = "";
            public bool Mention;
        }

        private void AddInlines(InlineCollection inlines, string text, ForumPost post)
        {
            foreach (Segment segment in ParseInline(text, post))
            {
                Inline inline;
                if (segment.Code)
                {
                    inline = new Run(segment.Text)
                    {
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        Background = CodeBackground,
                    };
                }
                else if (!string.IsNullOrEmpty(segment.LinkUrl))
                {
                    var link = new Hyperlink(new Run(segment.Text)) { Foreground = LinkBlue, Cursor = Cursors.Hand };
                    string url = segment.LinkUrl;
                    link.Click += (_, _) => OpenLink(url);
                    inline = link;
                }
                else
                {
                    var run = new Run(segment.Text);
                    if (segment.Bold)
                        run.FontWeight = FontWeights.Bold;
                    if (segment.Italic)
                        run.FontStyle = FontStyles.Italic;
                    var decorations = new TextDecorationCollection();
                    if (segment.Underline)
                        decorations.Add(TextDecorations.Underline);
                    if (segment.Strike)
                        decorations.Add(TextDecorations.Strikethrough);
                    if (decorations.Count > 0)
                        run.TextDecorations = decorations;
                    if (segment.Mention)
                    {
                        run.Foreground = MentionBlue;
                        run.FontWeight = FontWeights.SemiBold;
                    }
                    if (segment.Spoiler)
                    {
                        run.Background = SpoilerBackground;
                        run.Foreground = SpoilerBackground;
                        run.Cursor = Cursors.Hand;
                        Brush restore = segment.Mention ? MentionBlue : BodyGray;
                        run.MouseLeftButtonDown += (_, _) => run.Foreground = restore;
                    }
                    inline = run;
                }
                inlines.Add(inline);
            }
        }

        private static List<Segment> ParseInline(string text, ForumPost post)
        {
            var result = new List<Segment>();
            var codes = new List<string>();
            text = InlineCodeRegex.Replace(text, m =>
            {
                codes.Add(m.Groups[1].Value);
                return "C" + (codes.Count - 1) + "";
            });

            var markers = new List<(int Index, int Length, string Kind, string A, string B)>();
            void Collect(Regex re, string kind, int textGroup, int extraGroup)
            {
                foreach (Match m in re.Matches(text))
                    markers.Add((m.Index, m.Length, kind, m.Groups[textGroup].Value, extraGroup > 0 ? m.Groups[extraGroup].Value : ""));
            }
            Collect(SpoilerRegex, "spoiler", 1, 0);
            Collect(BoldRegex, "bold", 1, 0);
            Collect(UnderlineRegex, "underline", 1, 0);
            Collect(StrikeRegex, "strike", 1, 0);
            Collect(LinkRegex, "link", 1, 2);
            foreach (Match m in ItalicStarRegex.Matches(text))
                markers.Add((m.Index + m.Groups[1].Length, m.Length - m.Groups[1].Length, "italic", m.Groups[2].Value, ""));
            foreach (Match m in ItalicUnderscoreRegex.Matches(text))
                markers.Add((m.Index + m.Groups[1].Length, m.Length - m.Groups[1].Length, "italic", m.Groups[2].Value, ""));
            foreach (string name in post.MentionNames)
            {
                if (string.IsNullOrEmpty(name))
                    continue;
                foreach (Match m in Regex.Matches(text, "@" + Regex.Escape(name), RegexOptions.IgnoreCase))
                    markers.Add((m.Index, m.Length, "mention", m.Value, ""));
            }
            markers.Sort((a, b) => a.Index.CompareTo(b.Index));

            int pos = 0;
            void EmitPlain(string chunk, bool spoiler)
            {
                if (chunk.Length == 0)
                    return;
                var parts = Regex.Split(chunk, "(C\\d+)");
                foreach (string part in parts)
                {
                    if (part.Length == 0)
                        continue;
                    Match cm = Regex.Match(part, "^C(\\d+)$");
                    if (cm.Success)
                    {
                        int ci = int.Parse(cm.Groups[1].Value);
                        if (ci >= 0 && ci < codes.Count)
                            result.Add(new Segment { Text = codes[ci], Code = true });
                    }
                    else
                    {
                        result.Add(new Segment { Text = part, Spoiler = spoiler });
                    }
                }
            }

            foreach (var marker in markers)
            {
                if (marker.Index < pos)
                    continue;
                EmitPlain(text.Substring(pos, marker.Index - pos), false);
                switch (marker.Kind)
                {
                    case "spoiler":
                        EmitPlain(marker.A, true);
                        break;
                    case "bold":
                        result.Add(new Segment { Text = marker.A, Bold = true });
                        break;
                    case "underline":
                        result.Add(new Segment { Text = marker.A, Underline = true });
                        break;
                    case "strike":
                        result.Add(new Segment { Text = marker.A, Strike = true });
                        break;
                    case "italic":
                        result.Add(new Segment { Text = marker.A, Italic = true });
                        break;
                    case "link":
                        result.Add(new Segment { Text = marker.A, LinkUrl = marker.B });
                        break;
                    case "mention":
                        result.Add(new Segment { Text = marker.A, Mention = true });
                        break;
                }
                pos = marker.Index + marker.Length;
            }
            EmitPlain(text.Substring(pos), false);
            return result;
        }

        private static void OpenLink(string url)
        {
            try
            {
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return;
                if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
                    return;
                Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ForumRichContent::OpenLink", ex);
            }
        }

        private FrameworkElement BuildCodeBlock(string code, string lang)
        {
            code = code.TrimStart('\n').TrimEnd('\n');
            var header = new Grid { Margin = new Thickness(10, 6, 8, 4) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var langLabel = new TextBlock
            {
                Text = string.IsNullOrEmpty(lang) ? "code" : lang.ToLowerInvariant(),
                FontSize = 11,
                Foreground = FaintGray,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var copyButton = new Button
            {
                Content = "Copy",
                FontSize = 11,
                Padding = new Thickness(8, 2, 8, 2),
                Background = Brushes.Transparent,
                Foreground = FaintGray,
                BorderBrush = BorderGray,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            string codeCopy = code;
            copyButton.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(codeCopy);
                    copyButton.Content = "Copied";
                }
                catch
                {
                }
            };
            Grid.SetColumn(langLabel, 0);
            Grid.SetColumn(copyButton, 1);
            header.Children.Add(langLabel);
            header.Children.Add(copyButton);

            var codeText = new TextBlock
            {
                Text = code,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                Foreground = BodyGray,
                TextWrapping = TextWrapping.NoWrap,
                Margin = new Thickness(10, 0, 10, 8),
            };
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = codeText,
            };
            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(new Border { Height = 1, Background = BorderGray });
            body.Children.Add(scroll);
            return new Border
            {
                Background = CodeBlockBackground,
                BorderBrush = BorderGray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 8, 0, 8),
                Child = body,
            };
        }

        private FrameworkElement BuildImageFigure(ForumPost post, int index, Thickness margin)
        {
            ImageSource? source = index < post.ImageSlots.Count ? post.ImageSlots[index] : null;
            string meta = index < post.ImageMetas.Count ? post.ImageMetas[index] : "image";
            var panel = new StackPanel();
            if (source != null)
            {
                var image = new Image
                {
                    Source = source,
                    MaxHeight = 288,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    SnapsToDevicePixels = true,
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                };
                byte[]? fullBytes = index < post.ImageBytes.Count ? post.ImageBytes[index] : null;
                ImageSource inlineSource = source;
                image.MouseLeftButtonUp += (_, args) =>
                {
                    args.Handled = true;
                    OpenLightbox(inlineSource, fullBytes, meta);
                };
                panel.Children.Add(image);
            }
            else
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "image could not be displayed",
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Foreground = FaintGray,
                    Margin = new Thickness(10, 8, 10, 4),
                });
            }
            panel.Children.Add(new TextBlock
            {
                Text = meta,
                FontSize = 10,
                Foreground = FaintGray,
                Margin = new Thickness(8, 2, 8, 4),
            });
            return new Border
            {
                Margin = margin,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = BorderGray,
                Background = CodeBlockBackground,
                Child = panel,
            };
        }

        private void OpenLightbox(ImageSource inlineSource, byte[]? fullBytes, string meta)
        {
            try
            {
                ImageSource display = inlineSource;
                if (fullBytes != null)
                {
					ImageSource? full = Voidstrap.Utility.SafeImaging.FromBytes(fullBytes, 2560);
					if (full != null)
						display = full;
                }

                var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x00, 0x00, 0x00)) };
                root.Children.Add(new Image
                {
                    Source = display,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    Margin = new Thickness(48),
                    SnapsToDevicePixels = true,
                });
                var closeButton = new Button
                {
                    Content = "X",
                    FontSize = 18,
                    Width = 40,
                    Height = 40,
                    Background = Brushes.Transparent,
                    Foreground = Brushes.White,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 12, 16, 0),
                };
                root.Children.Add(closeButton);
                root.Children.Add(new TextBlock
                {
                    Text = meta,
                    FontSize = 12,
                    Foreground = FaintGray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 14),
                });

                var window = new Window
                {
                    WindowStyle = WindowStyle.None,
					AllowsTransparency = Voidstrap.Utility.Platform.IsWindows,
					Background = Voidstrap.Utility.Platform.IsWindows ? Brushes.Transparent : Brushes.Black,
                    WindowState = System.Windows.WindowState.Maximized,
                    ShowInTaskbar = false,
                    Content = root,
                };
                Window? owner = Window.GetWindow(this);
                if (owner != null)
                    window.Owner = owner;
                closeButton.Click += (_, _) => window.Close();
                root.MouseLeftButtonUp += (_, _) => window.Close();
                window.KeyDown += (_, keyArgs) =>
                {
                    if (keyArgs.Key == Key.Escape)
                        window.Close();
                };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("ForumRichContent::OpenLightbox", ex);
            }
        }

        private FrameworkElement BuildYoutubeCard(string videoId)
        {
            var text = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new Run("▶ ") { Foreground = Frozen(0xF8, 0x71, 0x71) });
            var link = new Hyperlink(new Run("YouTube video")) { Foreground = LinkBlue, Cursor = Cursors.Hand };
            string url = "https://www.youtube.com/watch?v=" + videoId;
            link.Click += (_, _) => OpenLink(url);
            text.Inlines.Add(link);
            text.Inlines.Add(new Run("  youtu.be/" + videoId) { Foreground = FaintGray, FontSize = 11 });
            return new Border
            {
                Margin = new Thickness(0, 8, 0, 8),
                Padding = new Thickness(12, 8, 12, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = BorderGray,
                Background = CodeBlockBackground,
                Child = text,
            };
        }
    }
}
