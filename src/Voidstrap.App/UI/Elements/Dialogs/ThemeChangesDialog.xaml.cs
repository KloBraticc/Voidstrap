using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Integrations;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Dialogs;

public sealed class DiffRow
{
    public string Marker { get; set; } = " ";

    public string Text { get; set; } = "";

    public string OldLabel { get; set; } = "";

    public string NewLabel { get; set; } = "";

    public Brush RowBrush { get; set; } = Brushes.Transparent;

    public Brush TextBrush { get; set; } = Brushes.Gainsboro;
}

public sealed class FileRow
{
    public string Path { get; set; } = "";

    public string StateLabel { get; set; } = "";

    public Brush StateBrush { get; set; } = Brushes.Gray;

    public ThemeFileChange Change { get; set; } = null!;
}

public partial class ThemeChangesDialog : WpfUiWindow
{
    private static readonly Brush AddedRow = Freeze(new SolidColorBrush(Color.FromArgb(38, 63, 185, 80)));

    private static readonly Brush RemovedRow = Freeze(new SolidColorBrush(Color.FromArgb(38, 248, 81, 73)));

    private static readonly Brush AddedText = Freeze(new SolidColorBrush(Color.FromRgb(126, 231, 135)));

    private static readonly Brush RemovedText = Freeze(new SolidColorBrush(Color.FromRgb(255, 129, 122)));

    private static readonly Brush SameText = Freeze(new SolidColorBrush(Color.FromRgb(190, 190, 190)));

    private static readonly Brush MutedText = Freeze(new SolidColorBrush(Color.FromRgb(130, 130, 130)));

    private readonly List<ThemeFileChange> _files;

    private readonly string _tempFolder;

    private List<DiffLine> _lines = new();

    private bool _expanded;

    private bool _hasChanges;

    private readonly bool _localOnly;

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public ThemeChangesDialog(string themeName, List<ThemeFileChange> files, bool localOnly = false)
    {
        InitializeComponent();

        _files = files;
        _localOnly = localOnly;
        _tempFolder = Path.Combine(Paths.Temp, "ThemeChanges", Guid.NewGuid().ToString("N"));

        base.Title = (localOnly ? "Files in " : "Changes to ") + themeName;
        RootTitleBar.Title = base.Title;
        HeadingText.Text = themeName;

        int changed = files.Count(f => f.State != ThemeFileState.Same);

        SummaryText.Text = localOnly
            ? files.Count + (files.Count == 1 ? " file in this theme." : " files in this theme.")
            : changed == 0
                ? files.Count + (files.Count == 1 ? " file, nothing changed since the last publish." : " files, nothing changed since the last publish.")
                : changed + (changed == 1 ? " file waiting to be committed" : " files waiting to be committed") +
                  " out of " + files.Count + ".";

        FileList.ItemsSource = files.Select(file => new FileRow
        {
            Path = file.Path,
            StateLabel = localOnly ? file.SizeLabel : file.StateLabel,
            StateBrush = file.State switch
            {
                ThemeFileState.Added => AddedText,
                ThemeFileState.Removed => RemovedText,
                ThemeFileState.Changed => Freeze(new SolidColorBrush(Color.FromRgb(226, 192, 141))),
                _ => MutedText
            },
            Change = file
        }).ToList();

        FileList.SelectedIndex = 0;

        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;

        try
        {
            if (Directory.Exists(_tempFolder))
                Directory.Delete(_tempFolder, true);
        }
        catch
        {
        }
    }

    private ThemeFileChange? Current => (FileList.SelectedItem as FileRow)?.Change;

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _expanded = false;
        Show(Current);
    }

    private void Show(ThemeFileChange? file)
    {
        DiffScroller.Visibility = Visibility.Collapsed;
        MediaScroller.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;

        if (file == null)
        {
            EmptyText.Visibility = Visibility.Visible;
            EmptyText.Text = "Pick a file to see what changed.";
            ShowAllButton.Visibility = Visibility.Collapsed;
            FilesText.Text = "";
            return;
        }

        FilesText.Text = file.Path + "   " + file.SizeLabel;

        if (file.IsText)
        {
            ShowAllButton.Visibility = Visibility.Visible;
            ShowText(file);
            return;
        }

        ShowAllButton.Visibility = Visibility.Collapsed;
        ShowMedia(file);
    }

    private void ShowText(ThemeFileChange file)
    {
        if (_localOnly)
        {
            _lines = LineDiff.Compare("", file.LocalText);
            _hasChanges = false;
            _expanded = true;
        }
        else
        {
            _lines = LineDiff.Compare(file.PublishedText, file.LocalText);

            int added = _lines.Count(line => line.Kind == DiffKind.Added);
            int removed = _lines.Count(line => line.Kind == DiffKind.Removed);
            _hasChanges = added > 0 || removed > 0;
        }

        if (!_hasChanges && !_expanded)
        {
            EmptyText.Visibility = Visibility.Visible;
            EmptyText.Text = _lines.Count == 0
                ? "This file is empty."
                : "Nothing has changed in " + file.Path + ". Press Show file to read it anyway.";
            ShowAllButton.Content = "Show file";
            return;
        }

        DiffScroller.Visibility = Visibility.Visible;

        List<DiffLine> lines = _expanded ? _lines : LineDiff.Collapse(_lines);

        DiffList.ItemsSource = lines.Select(line => new DiffRow
        {
            Marker = line.Marker,
            Text = line.Text,
            OldLabel = line.OldLabel,
            NewLabel = line.NewLabel,
            RowBrush = line.Kind switch
            {
                DiffKind.Added => AddedRow,
                DiffKind.Removed => RemovedRow,
                _ => Brushes.Transparent
            },
            TextBrush = line.Kind switch
            {
                DiffKind.Added => AddedText,
                DiffKind.Removed => RemovedText,
                _ => SameText
            }
        }).ToList();

        ShowAllButton.Content = !_hasChanges ? "Hide file" : _expanded ? "Only changes" : "Show all lines";
    }

    private void ShowMedia(ThemeFileChange file)
    {
        MediaScroller.Visibility = Visibility.Visible;

        MediaTitle.Text = file.Path;

        if (_localOnly)
        {
            MediaSubtitle.Text = "Part of this theme on this PC.";
            PublishedPane.Visibility = Visibility.Collapsed;
            LocalPane.Visibility = Visibility.Visible;
            PublishedImage.Visibility = Visibility.Collapsed;
            LocalImage.Visibility = Visibility.Collapsed;
            PublishedFontSample.Visibility = Visibility.Collapsed;
            LocalFontSample.Visibility = Visibility.Collapsed;

            if (file.IsImage)
                LoadImage(LocalImage, LocalInfo, file.LocalPath, file.LocalSize);
            else if (file.IsFont)
                LoadFont(LocalFontSample, LocalInfo, file.LocalPath, file.LocalSize);
            else
                LocalInfo.Text = Describe(file.LocalSize);

            return;
        }

        MediaSubtitle.Text = file.State switch
        {
            ThemeFileState.Added => "This file is new and will be uploaded when you commit.",
            ThemeFileState.Removed => "This file is on the website but not on this PC. Committing removes it.",
            ThemeFileState.Changed => "This file differs from the published copy.",
            _ => "This file matches the published copy."
        };

        PublishedPane.Visibility = file.State == ThemeFileState.Added ? Visibility.Collapsed : Visibility.Visible;
        LocalPane.Visibility = file.State == ThemeFileState.Removed ? Visibility.Collapsed : Visibility.Visible;

        PublishedImage.Visibility = Visibility.Collapsed;
        LocalImage.Visibility = Visibility.Collapsed;
        PublishedFontSample.Visibility = Visibility.Collapsed;
        LocalFontSample.Visibility = Visibility.Collapsed;

        string? publishedPath = WriteTemp(file);

        if (file.IsImage)
        {
            LoadImage(PublishedImage, PublishedInfo, publishedPath, file.PublishedSize);
            LoadImage(LocalImage, LocalInfo, file.LocalPath, file.LocalSize);
            return;
        }

        if (file.IsFont)
        {
            LoadFont(PublishedFontSample, PublishedInfo, publishedPath, file.PublishedSize);
            LoadFont(LocalFontSample, LocalInfo, file.LocalPath, file.LocalSize);
            return;
        }

        PublishedInfo.Text = file.PublishedSize > 0 ? Describe(file.PublishedSize) : "Not published";
        LocalInfo.Text = file.LocalSize > 0 ? Describe(file.LocalSize) : "Not on this PC";
    }

    private string? WriteTemp(ThemeFileChange file)
    {
        if (file.PublishedBytes == null || file.PublishedBytes.Length == 0)
            return null;

        try
        {
            Directory.CreateDirectory(_tempFolder);
            string path = Path.Combine(_tempFolder, Path.GetFileName(file.Path));
            File.WriteAllBytes(path, file.PublishedBytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void LoadImage(Image target, TextBlock info, string? path, long size)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            info.Text = "Not available";
            return;
        }

        try
        {
            BitmapImage bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();

            target.Source = bitmap;
            target.Visibility = Visibility.Visible;
            info.Text = bitmap.PixelWidth + " by " + bitmap.PixelHeight + ", " + Describe(size);
        }
        catch (Exception ex)
        {
            info.Text = "Could not preview this image. " + ex.Message;
        }
    }

    private static void LoadFont(TextBlock sample, TextBlock info, string? path, long size)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            info.Text = "Not available";
            return;
        }

        string family = Path.GetFileNameWithoutExtension(path);

        try
        {
            GlyphTypeface typeface = new GlyphTypeface(new Uri(path));

            if (typeface.Win32FamilyNames.Count > 0)
                family = typeface.Win32FamilyNames.Values.First();

            sample.FontFamily = new System.Windows.Media.FontFamily(new Uri(path), "./#" + family);
        }
        catch
        {
        }

        sample.Text = "The quick brown fox 0123";
        sample.Visibility = Visibility.Visible;
        info.Text = family + ", " + Describe(size);
    }

    private static string Describe(long bytes)
    {
        if (bytes < 1024)
            return bytes + " B";

        if (bytes < 1024 * 1024)
            return (bytes / 1024.0).ToString("0.#") + " KB";

        return (bytes / (1024.0 * 1024.0)).ToString("0.#") + " MB";
    }

    private void ShowAll_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        Show(Current);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
