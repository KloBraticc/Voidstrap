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

    private static readonly Brush AddedText = Freeze(new SolidColorBrush(Color.FromRgb(126, 231, 135)));

    private static readonly Brush SameText = Freeze(new SolidColorBrush(Color.FromRgb(190, 190, 190)));

    private static readonly Brush MutedText = Freeze(new SolidColorBrush(Color.FromRgb(130, 130, 130)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public ThemeChangesDialog(string themeName, List<ThemeFileChange> files)
    {
        InitializeComponent();

        base.Title = "Files in " + themeName;
        RootTitleBar.Title = base.Title;
        HeadingText.Text = themeName;
        SummaryText.Text = files.Count + (files.Count == 1 ? " file in this theme." : " files in this theme.");

        FileList.ItemsSource = files.Select(file => new FileRow
        {
            Path = file.Path,
            StateLabel = file.SizeLabel,
            StateBrush = MutedText,
            Change = file
        }).ToList();

        FileList.SelectedIndex = 0;
    }

    private ThemeFileChange? Current => (FileList.SelectedItem as FileRow)?.Change;

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
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
            EmptyText.Text = "Pick a file to see what is inside.";
            FilesText.Text = "";
            return;
        }

        FilesText.Text = file.Path + "   " + file.SizeLabel;

        if (file.IsText)
        {
            ShowText(file);
            return;
        }

        ShowMedia(file);
    }

    private void ShowText(ThemeFileChange file)
    {
        DiffScroller.Visibility = Visibility.Visible;

        DiffList.ItemsSource = LineDiff.Compare("", file.LocalText).Select(line => new DiffRow
        {
            Marker = line.Marker,
            Text = line.Text,
            OldLabel = line.OldLabel,
            NewLabel = line.NewLabel,
            RowBrush = line.Kind == DiffKind.Added ? AddedRow : Brushes.Transparent,
            TextBrush = line.Kind == DiffKind.Added ? AddedText : SameText
        }).ToList();
    }

    private void ShowMedia(ThemeFileChange file)
    {
        MediaScroller.Visibility = Visibility.Visible;

        MediaTitle.Text = file.Path;
        MediaSubtitle.Text = "Part of this theme on this PC.";
        LocalImage.Visibility = Visibility.Collapsed;
        LocalFontSample.Visibility = Visibility.Collapsed;

        if (file.IsImage)
            LoadImage(LocalImage, LocalInfo, file.LocalPath, file.LocalSize);
        else if (file.IsFont)
            LoadFont(LocalFontSample, LocalInfo, file.LocalPath, file.LocalSize);
        else
            LocalInfo.Text = file.SizeLabel;
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
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				BitmapSource? portable = Voidstrap.Utility.SafeImaging.FromFile(path, 512);
				if (portable == null)
				{
					info.Text = "Could not preview this image.";
					return;
				}
				target.Source = portable;
				target.Visibility = Visibility.Visible;
				info.Text = portable.PixelWidth + " by " + portable.PixelHeight + ", " + Describe(size);
				return;
			}
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
