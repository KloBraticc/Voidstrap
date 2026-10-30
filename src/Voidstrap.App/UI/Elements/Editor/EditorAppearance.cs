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

    private static bool FollowsTheme(string? value, params string[] oldDefaults) =>
        string.IsNullOrWhiteSpace(value) || oldDefaults.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool BackgroundFollowsTheme => FollowsTheme(App.Settings.Prop.EditorBackground, "#1E1E1E", "#1E1F22");

    private static Color BackgroundColor =>
        BackgroundFollowsTheme ? Colors.Transparent : ParseColor(App.Settings.Prop.EditorBackground, Colors.Transparent);

    private static void ApplyTo(FrameworkElement panel)
    {
        if (BackgroundFollowsTheme)
        {
            panel.Resources.Remove("ControlFillColorDefaultBrush");
            panel.Resources.Remove("VsDocumentBrush");
            return;
        }
        SolidColorBrush brush = new(BackgroundColor);
        brush.Freeze();
        panel.Resources["ControlFillColorDefaultBrush"] = brush;
        panel.Resources["VsDocumentBrush"] = brush;
    }

    private static void ApplyTo(TextEditor editor)
    {
        var settings = App.Settings.Prop;
        editor.Background = BuildBackground();
        if (FollowsTheme(settings.EditorForeground, "#DCDCDC", "#BCBEC4"))
            editor.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        else
            editor.Foreground = new SolidColorBrush(ParseColor(settings.EditorForeground, Colors.White));
        editor.FontFamily = new System.Windows.Media.FontFamily(string.IsNullOrWhiteSpace(settings.EditorFontFamily) ? "Cascadia Mono, Consolas" : settings.EditorFontFamily);
        editor.FontSize = Math.Clamp(settings.EditorFontSize, 8.0, 40.0);
        editor.WordWrap = settings.EditorWordWrap;
        editor.ShowLineNumbers = settings.EditorLineNumbers;
        editor.Options.HighlightCurrentLine = settings.EditorHighlightLine;
    }

    public static void Apply(DataGrid grid, GridEditorAppearance appearance)
    {
        Color color = ParseColor(string.IsNullOrWhiteSpace(appearance.Background) ? null : appearance.Background, Colors.Transparent);
        grid.Background = BuildBackground(color, appearance.BackgroundImage, appearance.BackgroundImageOpacity);
        if (string.IsNullOrWhiteSpace(appearance.Foreground))
            grid.Resources.Remove("TextFillColorPrimaryBrush");
        else
            grid.Resources["TextFillColorPrimaryBrush"] = new SolidColorBrush(ParseColor(appearance.Foreground, Colors.White));
        if (string.IsNullOrWhiteSpace(appearance.FontFamily))
            grid.ClearValue(Control.FontFamilyProperty);
        else
            grid.FontFamily = new System.Windows.Media.FontFamily(appearance.FontFamily);
        grid.FontSize = Math.Clamp(appearance.FontSize, 8.0, 40.0);
        grid.RowHeight = Math.Max(32.0, grid.FontSize * 2);
    }

    private static Brush BuildBackground() =>
        BuildBackground(BackgroundColor, App.Settings.Prop.EditorBackgroundImage ?? "", App.Settings.Prop.EditorBackgroundImageOpacity);

    private static Brush BuildBackground(Color color, string path, double opacity)
    {
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
            DrawingGroup picture = new() { Opacity = Math.Clamp(opacity, 0.0, 1.0) };
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

internal static class EditorFileIcons
{
    public static (SymbolRegular Symbol, Color Color) For(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        return ext switch
        {
            ".ts" or ".tsx" => (SymbolRegular.Code24, Color.FromRgb(0x3B, 0x9B, 0xEB)),
            ".js" or ".mjs" or ".jsx" => (SymbolRegular.Code24, Color.FromRgb(0xE8, 0xD4, 0x4D)),
            ".json" => (SymbolRegular.BracesVariable24, Color.FromRgb(0xE2, 0xB9, 0x4B)),
            ".xml" or ".xaml" or ".html" or ".htm" => (SymbolRegular.Code24, Color.FromRgb(0xE3, 0x7B, 0x4A)),
            ".css" => (SymbolRegular.Color24, Color.FromRgb(0x56, 0x9C, 0xD6)),
            ".md" or ".txt" => (SymbolRegular.DocumentText24, Color.FromRgb(0x9C, 0xC4, 0xE4)),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".svg" or ".bmp" or ".webp" => (SymbolRegular.Image24, Color.FromRgb(0xB5, 0x8C, 0xE0)),
            _ => (SymbolRegular.Document24, Color.FromRgb(0xC8, 0xC8, 0xC8)),
        };
    }

    public static readonly Color Folder = Color.FromRgb(0xDC, 0xB6, 0x7A);

    public static readonly Color Project = Color.FromRgb(0x7F, 0xC8, 0x7F);
}

public sealed class EditorSettingsButton : Wpf.Ui.Controls.Button
{
    public EditorSettingsButton()
    {
        SetResourceReference(StyleProperty, typeof(Wpf.Ui.Controls.Button));
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
    private readonly StackPanel _fields = new();

    private readonly DataGrid? _grid;

    private readonly GridEditorAppearance? _appearance;

    public EditorSettingsWindow()
    {
        var settings = App.Settings.Prop;
        BuildWindow();
        AddColor(Strings.Ui_EditorSettings_Background, settings.EditorBackground, "ControlFillColorDefaultBrush", value => settings.EditorBackground = value);
        AddColor(Strings.Ui_EditorSettings_TextColor, settings.EditorForeground, "TextFillColorPrimaryBrush", value => settings.EditorForeground = value);
        AddText(Strings.Ui_EditorSettings_Font, settings.EditorFontFamily, value => settings.EditorFontFamily = value);
        AddSlider(Strings.Ui_EditorSettings_FontSize, 8, 40, settings.EditorFontSize, value => settings.EditorFontSize = value);
        AddToggle(Strings.Ui_EditorSettings_WordWrap, settings.EditorWordWrap, value => settings.EditorWordWrap = value);
        AddToggle(Strings.Ui_EditorSettings_LineNumbers, settings.EditorLineNumbers, value => settings.EditorLineNumbers = value);
        AddToggle(Strings.Ui_EditorSettings_HighlightLine, settings.EditorHighlightLine, value => settings.EditorHighlightLine = value);
        FinishWindow(settings.EditorBackgroundImageOpacity, value => settings.EditorBackgroundImageOpacity = value);
    }

    public EditorSettingsWindow(DataGrid grid, GridEditorAppearance appearance)
    {
        _grid = grid;
        _appearance = appearance;
        BuildWindow();
        AddColor(Strings.Ui_EditorSettings_Background, appearance.Background, "ControlFillColorDefaultBrush", value => appearance.Background = value);
        AddColor(Strings.Ui_EditorSettings_TextColor, appearance.Foreground, "TextFillColorPrimaryBrush", value => appearance.Foreground = value);
        AddText(Strings.Ui_EditorSettings_Font, appearance.FontFamily, value => appearance.FontFamily = value);
        AddSlider(Strings.Ui_EditorSettings_FontSize, 8, 40, appearance.FontSize, value => appearance.FontSize = value);
        FinishWindow(appearance.BackgroundImageOpacity, value => appearance.BackgroundImageOpacity = value);
    }

    private void BuildWindow()
    {
        Title = Strings.Ui_EditorSettings_Title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ExtendsContentIntoTitleBar = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        VisualStudioTheme.Apply(this);
    }

    private void FinishWindow(double imageOpacity, Action<double> setImageOpacity)
    {
        if (!Voidstrap.Utility.Platform.IsMacOS)
            AddImageRow(imageOpacity, setImageOpacity);
        Border card = new()
        {
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = _fields,
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Wpf.Ui.Controls.Button reset = new() { Content = Strings.Common_Reset, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        reset.Click += Reset_Click;
        StackPanel body = new() { Margin = new Thickness(14, 4, 14, 14) };
        body.Children.Add(card);
        body.Children.Add(reset);
        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(new Wpf.Ui.Controls.TitleBar { Title = Title, Padding = new Thickness(8), ShowMinimize = false, ShowMaximize = false, ForceShutdown = false, MinimizeToTray = false });
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;
    }

    private void Changed()
    {
        if (_grid != null && _appearance != null)
        {
            EditorAppearance.Apply(_grid, _appearance);
            App.Settings.SaveDeferred();
            return;
        }
        EditorAppearance.Refresh();
    }

    private void SetImage(string path)
    {
        if (_appearance != null)
            _appearance.BackgroundImage = path;
        else
            App.Settings.Prop.EditorBackgroundImage = path;
        Changed();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_appearance != null)
        {
            _appearance.Background = "";
            _appearance.Foreground = "";
            _appearance.FontFamily = "";
            _appearance.FontSize = 13;
            _appearance.BackgroundImage = "";
            _appearance.BackgroundImageOpacity = 0.15;
            Changed();
            Close();
            return;
        }
        var settings = App.Settings.Prop;
        settings.EditorBackground = "";
        settings.EditorForeground = "";
        settings.EditorFontFamily = "JetBrains Mono, Cascadia Mono, Consolas";
        settings.EditorFontSize = 13;
        settings.EditorWordWrap = false;
        settings.EditorLineNumbers = true;
        settings.EditorHighlightLine = true;
        settings.EditorBackgroundImage = "";
        settings.EditorBackgroundImageOpacity = 0.15;
        Changed();
        Close();
    }

    private void AddRow(string label, FrameworkElement control)
    {
        Grid row = new() { Margin = new Thickness(0, 7, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBlock text = new() { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        row.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        _fields.Children.Add(row);
    }

    private void AddText(string label, string value, Action<string> set)
    {
        TextBox box = new() { Text = value, Width = 210 };
        box.TextChanged += (_, _) =>
        {
            set(box.Text);
            Changed();
        };
        AddRow(label, box);
    }

    private static void ShowColor(Border swatch, string value, string themeKey)
    {
        if (string.IsNullOrWhiteSpace(value))
            swatch.SetResourceReference(Border.BackgroundProperty, themeKey);
        else
            swatch.Background = new SolidColorBrush(EditorAppearance.ParseColor(value, Colors.Transparent));
    }

    private void AddColor(string label, string value, string themeKey, Action<string> set)
    {
        Border swatch = new()
        {
            Width = 46,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        ShowColor(swatch, value, themeKey);
        swatch.MouseLeftButtonUp += (_, _) =>
        {
            Voidstrap.UI.Elements.Controls.RinColorPickerDialog dialog = new(EditorAppearance.ParseColor(value, Colors.Black), alphaEnabled: false) { Owner = this };
            if (dialog.ShowOwnedDialog() != true)
                return;
            Color color = dialog.SelectedColor;
            value = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            ShowColor(swatch, value, themeKey);
            set(value);
            Changed();
        };
        AddRow(label, swatch);
    }

    private void AddSlider(string label, double min, double max, double value, Action<double> set)
    {
        Slider slider = new() { Minimum = min, Maximum = max, Value = value, Width = 210 };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            Changed();
        };
        AddRow(label, slider);
    }

    private void AddToggle(string label, bool value, Action<bool> set)
    {
        Wpf.Ui.Controls.ToggleSwitch toggle = new() { IsChecked = value };
        toggle.Click += (_, _) =>
        {
            set(toggle.IsChecked == true);
            Changed();
        };
        AddRow(label, toggle);
    }

    private void AddImageRow(double imageOpacity, Action<double> setImageOpacity)
    {
        StackPanel buttons = new() { Orientation = Orientation.Horizontal };
        Wpf.Ui.Controls.Button browse = new() { Content = Strings.Common_Browse };
        browse.Click += Browse_Click;
        Wpf.Ui.Controls.Button clear = new() { Content = Strings.Ui_EditorSettings_ClearImage, Margin = new Thickness(6, 0, 0, 0) };
        clear.Click += Clear_Click;
        buttons.Children.Add(browse);
        buttons.Children.Add(clear);
        AddRow(Strings.Ui_EditorSettings_Image, buttons);
        AddSlider(Strings.Ui_EditorSettings_ImageOpacity, 0, 1, imageOpacity, setImageOpacity);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog picker = new() { Filter = Strings.Ui_EditorSettings_ImageFilter, CheckFileExists = true };
        if (picker.ShowDialog(this) != true)
            return;
        SetImage(picker.FileName);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SetImage("");
    }
}
