using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ICSharpCode.AvalonEdit;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;
using Voidstrap.Resources;
using Wpf.Ui.Common;

namespace Voidstrap.UI.Elements.Editor;

internal static class EditorAppearance
{
    private const string LOG_IDENT = "EditorAppearance";

    private static readonly List<WeakReference<TextEditor>> Editors = [];

    private static readonly List<WeakReference<FrameworkElement>> Panels = [];

    public static void Attach(TextEditor editor)
    {
        Editors.RemoveAll(entry => !entry.TryGetTarget(out TextEditor? live) || ReferenceEquals(live, editor));
        Editors.Add(new WeakReference<TextEditor>(editor));
        ApplyTo(editor);
    }

    public static void AttachPanel(FrameworkElement panel)
    {
        Panels.Add(new WeakReference<FrameworkElement>(panel));
        ApplyTo(panel);
    }

    public static void Refresh()
    {
        foreach (WeakReference<TextEditor> entry in Editors)
            if (entry.TryGetTarget(out TextEditor? editor))
                ApplyTo(editor);
        foreach (WeakReference<FrameworkElement> entry in Panels)
            if (entry.TryGetTarget(out FrameworkElement? panel))
                ApplyTo(panel);
        App.Settings.SaveDeferred();
    }

    public static Color ParseColor(string? text, Color fallback)
    {
        try
        {
            return text != null && ColorConverter.ConvertFromString(text) is Color color ? color : fallback;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static Color BackgroundColor => ParseColor(App.Settings.Prop.EditorBackground, Color.FromRgb(0x1E, 0x1E, 0x1E));

    private static void ApplyTo(FrameworkElement panel)
    {
        SolidColorBrush brush = new(BackgroundColor);
        brush.Freeze();
        panel.Resources["ControlFillColorDefaultBrush"] = brush;
        panel.Resources["VsDocumentBrush"] = brush;
    }

    private static void ApplyTo(TextEditor editor)
    {
        var settings = App.Settings.Prop;
        editor.Background = BuildBackground();
        editor.Foreground = new SolidColorBrush(ParseColor(settings.EditorForeground, Color.FromRgb(0xDC, 0xDC, 0xDC)));
        editor.FontFamily = new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(settings.EditorFontFamily) ? "Cascadia Mono, Consolas" : settings.EditorFontFamily);
        editor.FontSize = Math.Clamp(settings.EditorFontSize, 8.0, 40.0);
        editor.WordWrap = settings.EditorWordWrap;
        editor.ShowLineNumbers = settings.EditorLineNumbers;
        editor.Options.HighlightCurrentLine = settings.EditorHighlightLine;
    }

    private static Brush BuildBackground()
    {
        Color color = BackgroundColor;
        string path = App.Settings.Prop.EditorBackgroundImage ?? "";
        if (Voidstrap.Utility.Platform.IsMacOS || path.Length == 0 || !File.Exists(path))
            return new SolidColorBrush(color);
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            Rect area = new(0, 0, image.PixelWidth, image.PixelHeight);
            DrawingGroup group = new();
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(color), null, new RectangleGeometry(area)));
            DrawingGroup picture = new() { Opacity = Math.Clamp(App.Settings.Prop.EditorBackgroundImageOpacity, 0.0, 1.0) };
            picture.Children.Add(new ImageDrawing(image, area));
            group.Children.Add(picture);
            return new DrawingBrush(group) { Stretch = Stretch.UniformToFill };
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or NotSupportedException or ArgumentException)
        {
            App.Logger.WriteLine(LOG_IDENT, "The editor background image could not be loaded: " + ex.Message);
            return new SolidColorBrush(color);
        }
    }
}

public sealed class EditorSettingsButton : Wpf.Ui.Controls.Button
{
    public EditorSettingsButton()
    {
        Icon = SymbolRegular.Settings24;
        ToolTip = Strings.Ui_EditorSettings_Title;
        Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        new EditorSettingsWindow().ShowOwnedDialog();
    }
}

internal sealed class EditorSettingsWindow : WpfUiWindow
{
    private readonly StackPanel _fields = new() { Margin = new Thickness(16) };

    public EditorSettingsWindow()
    {
        Title = Strings.Ui_EditorSettings_Title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        ExtendsContentIntoTitleBar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));
        VisualStudioTheme.Apply(this);
        var settings = App.Settings.Prop;
        AddColor(Strings.Ui_EditorSettings_Background, settings.EditorBackground, value => settings.EditorBackground = value);
        AddColor(Strings.Ui_EditorSettings_TextColor, settings.EditorForeground, value => settings.EditorForeground = value);
        AddText(Strings.Ui_EditorSettings_Font, settings.EditorFontFamily, value => settings.EditorFontFamily = value);
        AddSlider(Strings.Ui_EditorSettings_FontSize, 8, 40, settings.EditorFontSize, value => settings.EditorFontSize = value);
        AddToggle(Strings.Ui_EditorSettings_WordWrap, settings.EditorWordWrap, value => settings.EditorWordWrap = value);
        AddToggle(Strings.Ui_EditorSettings_LineNumbers, settings.EditorLineNumbers, value => settings.EditorLineNumbers = value);
        AddToggle(Strings.Ui_EditorSettings_HighlightLine, settings.EditorHighlightLine, value => settings.EditorHighlightLine = value);
        if (!Voidstrap.Utility.Platform.IsMacOS)
            AddImageRow();
        Wpf.Ui.Controls.Button reset = new() { Content = Strings.Common_Reset, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        reset.Click += Reset_Click;
        _fields.Children.Add(reset);
        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new Wpf.Ui.Controls.TitleBar { Title = Title, Padding = new Thickness(8), ShowMinimize = false, ShowMaximize = false, ForceShutdown = false, MinimizeToTray = false });
        Grid.SetRow(_fields, 1);
        root.Children.Add(_fields);
        Content = root;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Settings.Prop;
        settings.EditorBackground = "#1E1E1E";
        settings.EditorForeground = "#DCDCDC";
        settings.EditorFontFamily = "Cascadia Mono, Consolas";
        settings.EditorFontSize = 13;
        settings.EditorWordWrap = false;
        settings.EditorLineNumbers = true;
        settings.EditorHighlightLine = true;
        settings.EditorBackgroundImage = "";
        settings.EditorBackgroundImageOpacity = 0.15;
        EditorAppearance.Refresh();
        Close();
    }

    private void AddLabel(string text) =>
        _fields.Children.Add(new TextBlock { Text = text, Margin = new Thickness(0, 6, 0, 3), Foreground = Brushes.White });

    private void AddText(string label, string value, Action<string> set)
    {
        AddLabel(label);
        TextBox box = new() { Text = value };
        box.TextChanged += (_, _) =>
        {
            set(box.Text);
            EditorAppearance.Refresh();
        };
        _fields.Children.Add(box);
    }

    private void AddColor(string label, string value, Action<string> set)
    {
        AddLabel(label);
        Grid row = new();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        Border swatch = new() { Margin = new Thickness(6, 2, 0, 2), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Background = new SolidColorBrush(EditorAppearance.ParseColor(value, Colors.Transparent)) };
        Grid.SetColumn(swatch, 1);
        TextBox box = new() { Text = value };
        box.TextChanged += (_, _) =>
        {
            if (ColorConverter.ConvertFromString(IsColor(box.Text) ? box.Text : "#00000000") is not Color color || !IsColor(box.Text))
                return;
            swatch.Background = new SolidColorBrush(color);
            set(box.Text);
            EditorAppearance.Refresh();
        };
        row.Children.Add(box);
        row.Children.Add(swatch);
        _fields.Children.Add(row);
    }

    private static bool IsColor(string text)
    {
        try
        {
            return ColorConverter.ConvertFromString(text) is Color;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void AddSlider(string label, double min, double max, double value, Action<double> set)
    {
        AddLabel(label);
        Slider slider = new() { Minimum = min, Maximum = max, Value = value };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            EditorAppearance.Refresh();
        };
        _fields.Children.Add(slider);
    }

    private void AddToggle(string label, bool value, Action<bool> set)
    {
        CheckBox box = new() { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 0) };
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            EditorAppearance.Refresh();
        };
        _fields.Children.Add(box);
    }

    private void AddImageRow()
    {
        AddLabel(Strings.Ui_EditorSettings_Image);
        StackPanel row = new() { Orientation = Orientation.Horizontal };
        Wpf.Ui.Controls.Button browse = new() { Content = Strings.Common_Browse };
        browse.Click += Browse_Click;
        Wpf.Ui.Controls.Button clear = new() { Content = Strings.Ui_EditorSettings_ClearImage, Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += Clear_Click;
        row.Children.Add(browse);
        row.Children.Add(clear);
        _fields.Children.Add(row);
        AddSlider(Strings.Ui_EditorSettings_ImageOpacity, 0, 1, App.Settings.Prop.EditorBackgroundImageOpacity, value => App.Settings.Prop.EditorBackgroundImageOpacity = value);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog picker = new() { Filter = Strings.Ui_EditorSettings_ImageFilter, CheckFileExists = true };
        if (picker.ShowDialog(this) != true)
            return;
        App.Settings.Prop.EditorBackgroundImage = picker.FileName;
        EditorAppearance.Refresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.Prop.EditorBackgroundImage = "";
        EditorAppearance.Refresh();
    }
}
