using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class CustomThemeEditor : WpfUiWindow
{
    private const string LOG_IDENT = "CustomThemeEditor";

    private readonly string _path = Paths.CustomThemeXaml;

    private readonly ObservableCollection<ThemeColorItem> _items = new();

    private readonly DispatcherTimer _previewTimer;

    private ResourceDictionary? _previewDict;

    private string _currentXaml = "";

    private bool _saved;

    private bool _suppressPreview;

    private FileSystemWatcher? _watcher;

    private ExternalEditorInfo? _external;

    public CustomThemeEditor()
    {
        InitializeComponent();


        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _previewTimer.Tick += PreviewTimer_Tick;

        BuildItems();

        CollectionViewSource view = new CollectionViewSource { Source = _items };
        view.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
        ColorList.ItemsSource = view.View;

        _currentXaml = LoadInitialXaml();
        CodeEditor.Text = _currentXaml;
        CodeEditor.TextChanged += CodeEditor_TextChanged;

        ApplyPreview(_currentXaml, quiet: true);
        Closed += CustomThemeEditor_Closed;
    }

    private string LoadInitialXaml()
    {
        try
        {
            if (File.Exists(_path))
            {
                string text = CustomTheme.ReadFile(_path);
                if (CustomTheme.Validate(text).Ok)
                    return text;
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not read the saved theme: " + ex.Message);
        }
        return CustomTheme.BuildXaml(_items.Select(i => new KeyValuePair<string, Color>(i.Key, i.Color)));
    }

    private void BuildItems()
    {
        _suppressPreview = true;
        Dictionary<string, Color> existing = LoadExistingColors();

        foreach (ThemeKeyInfo info in CustomTheme.Schema)
        {
            Color color = existing.TryGetValue(info.Key, out Color found)
                ? found
                : (CustomTheme.TryParseColor(info.Fallback, out Color fb) ? fb : Colors.Black);

            ThemeColorItem item = new ThemeColorItem(info.Key, info.Label, info.IsBrush, color, info.Group);
            item.Changed += SchedulePreview;
            _items.Add(item);
        }
        _suppressPreview = false;
    }

    private Dictionary<string, Color> LoadExistingColors()
    {
        Dictionary<string, Color> map = new(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
                return map;
            ThemeValidationResult result = CustomTheme.Validate(CustomTheme.ReadFile(_path));
            if (result.Dictionary == null)
                return map;
            ReadColors(result.Dictionary, map);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not read saved colours: " + ex.Message);
        }
        return map;
    }

    private static void ReadColors(ResourceDictionary dict, Dictionary<string, Color> map)
    {
        foreach (ThemeKeyInfo info in CustomTheme.Schema)
        {
            if (!dict.Contains(info.Key))
                continue;
            object value = dict[info.Key];
            if (value is Color c)
                map[info.Key] = c;
            else if (value is SolidColorBrush b)
                map[info.Key] = b.Color;
        }
    }

    private void SchedulePreview()
    {
        if (_suppressPreview)
            return;
        _previewTimer.Stop();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(120);
        _previewTimer.Start();
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        _previewTimer.Stop();

        if (EditorTabs.SelectedItem == CodeTab)
        {
            ApplyPreview(CodeEditor.Text, quiet: false);
            return;
        }

        _currentXaml = CustomTheme.BuildXaml(_items.Select(i => new KeyValuePair<string, Color>(i.Key, i.Color)));
        ApplyPreview(_currentXaml, quiet: true);
    }

    private void CodeEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressPreview || EditorTabs.SelectedItem != CodeTab)
            return;
        _previewTimer.Stop();
        _previewTimer.Interval = TimeSpan.FromMilliseconds(400);
        _previewTimer.Start();
    }

    private bool ApplyPreview(string xaml, bool quiet)
    {
        ThemeValidationResult result = CustomTheme.Validate(xaml);

        if (!result.Ok)
        {
            string first = result.Errors.FirstOrDefault() ?? "That theme is not valid.";
            ShowStatus(result.ErrorLine > 0 ? $"Line {result.ErrorLine}: {first}" : first, isError: true);
            return false;
        }

        _currentXaml = xaml;
        ApplyPreviewDict(CustomTheme.Merge(result.Dictionary));

        if (result.Warnings.Count > 0)
            ShowStatus(string.Join(" ", result.Warnings.Take(2)), isError: false);
        else if (!quiet)
            ShowStatus("Theme looks good", isError: false);
        else
            ClearStatus();

        SyncSwatches(result.Dictionary);
        return true;
    }

    private void ApplyPreviewDict(ResourceDictionary dict)
    {
        try
        {
            Collection<ResourceDictionary> merged = Application.Current.Resources.MergedDictionaries;
            if (_previewDict != null)
                merged.Remove(_previewDict);
            _previewDict = dict;
            merged.Add(dict);
            WindowBackdrop.ApplyThemeToAllOpenWindows();
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Preview failed: " + ex.Message);
        }
    }

    private void RemovePreviewDict()
    {
        try
        {
            if (_previewDict != null)
                Application.Current.Resources.MergedDictionaries.Remove(_previewDict);
        }
        catch
        {
        }
        _previewDict = null;
    }

    private void SyncSwatches(ResourceDictionary? dict)
    {
        if (dict == null)
            return;
        Dictionary<string, Color> map = new(StringComparer.Ordinal);
        ReadColors(dict, map);
        _suppressPreview = true;
        foreach (ThemeColorItem item in _items)
        {
            if (map.TryGetValue(item.Key, out Color c))
                item.SetColor(c);
        }
        _suppressPreview = false;
    }

    private void ShowStatus(string message, bool isError)
    {
        StatusText.Text = message;
        StatusIcon.Symbol = isError ? Wpf.Ui.Common.SymbolRegular.ErrorCircle24 : Wpf.Ui.Common.SymbolRegular.CheckmarkCircle24;
        StatusBorder.Background = new SolidColorBrush(isError ? Color.FromArgb(0x33, 0xFF, 0x3B, 0x3B) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        StatusBorder.BorderBrush = new SolidColorBrush(isError ? Color.FromArgb(0x80, 0xFF, 0x3B, 0x3B) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        StatusText.Foreground = new SolidColorBrush(isError ? Color.FromRgb(0xFF, 0xC9, 0xC9) : Color.FromRgb(0xDD, 0xDD, 0xDD));
        StatusBorder.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = !isError;
    }

    private void ClearStatus()
    {
        StatusBorder.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = true;
    }

    private void EditorTabs_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.OriginalSource != EditorTabs)
            return;

        if (EditorTabs.SelectedItem == CodeTab)
        {
            _suppressPreview = true;
            CodeEditor.Text = _currentXaml;
            _suppressPreview = false;
        }
        else
        {
            ApplyPreview(CodeEditor.Text, quiet: true);
        }

        UpdatePreviewPane();
    }

    private void PreviewToggle_Changed(object sender, RoutedEventArgs e) => UpdatePreviewPane();

    private void UpdatePreviewPane()
    {
        if (PreviewPane == null || PreviewColumn == null)
            return;

        bool show = EditorTabs.SelectedItem != CodeTab || PreviewToggle.IsChecked == true;
        PreviewPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.Width = show ? new GridLength(300) : new GridLength(0);
        PreviewGap.Width = show ? new GridLength(14) : new GridLength(0);
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ExternalEditorInfo> editors = ExternalEditor.Detect();
        if (editors.Count == 0)
            return;

        ExternalEditorPickerDialog dialog = new(editors) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedEditor == null)
            return;

        _external = dialog.SelectedEditor;
        LaunchExternal(_external);
    }

    private void LaunchExternal(ExternalEditorInfo editor)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            CustomTheme.WriteFile(_path, EditorTabs.SelectedItem == CodeTab ? CodeEditor.Text : _currentXaml);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not write the theme file: " + ex.Message, isError: true);
            return;
        }

        if (!ExternalEditor.Open(editor, _path))
        {
            ShowStatus("Could not start " + editor.Name, isError: true);
            return;
        }

        StartWatching();
        ShowStatus("Editing in " + editor.Name + ", changes appear here as you save", isError: false);
    }

    private void StartWatching()
    {
        if (_watcher != null)
            return;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(dir))
                return;
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };
            _watcher.Changed += Watcher_Changed;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not watch the theme file: " + ex.Message);
        }
    }

    private void Watcher_Changed(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(ReloadFromDisk, DispatcherPriority.Background);
    }

    private void ReloadFromDisk()
    {
        string text;
        try
        {
            text = CustomTheme.ReadFile(_path);
        }
        catch
        {
            return;
        }

        if (text == CodeEditor.Text)
            return;

        _suppressPreview = true;
        int caret = Math.Min(CodeEditor.CaretOffset, text.Length);
        CodeEditor.Text = text;
        CodeEditor.CaretOffset = caret;
        _suppressPreview = false;

        ApplyPreview(text, quiet: false);
    }

    private void Format_Click(object sender, RoutedEventArgs e)
    {
        ThemeValidationResult result = CustomTheme.Validate(CodeEditor.Text);
        if (!result.Ok)
        {
            ShowStatus(result.Errors.FirstOrDefault() ?? "Cannot format an invalid theme", isError: true);
            return;
        }
        Dictionary<string, Color> map = new(StringComparer.Ordinal);
        ReadColors(result.Dictionary!, map);
        _suppressPreview = true;
        CodeEditor.Text = CustomTheme.BuildXaml(map);
        _suppressPreview = false;
        ApplyPreview(CodeEditor.Text, quiet: false);
    }

    private void PickColor_Click(object sender, RoutedEventArgs e)
    {
        if (TryPickColor(Colors.White, out Color picked))
            CodeEditor.Document.Insert(CodeEditor.CaretOffset, CustomTheme.ToHex(picked));
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ThemeColorItem item)
            return;
        if (TryPickColor(item.Color, out Color picked))
            item.SetColor(picked);
    }

    private bool TryPickColor(Color initial, out Color picked)
    {
        picked = initial;
        try
        {
            RinColorPickerDialog dialog = new(initial, alphaEnabled: true) { Owner = this };
            if (dialog.ShowDialog() != true)
                return false;
            picked = dialog.SelectedColor;
            return true;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Colour picker failed: " + ex.Message);
            return false;
        }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(CodeEditor.Text);
            ShowStatus("Copied to the clipboard", isError: false);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Copy failed: " + ex.Message);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _suppressPreview = true;
        foreach (ThemeColorItem item in _items)
        {
            ThemeKeyInfo info = CustomTheme.Schema.First(s => s.Key == item.Key);
            if (CustomTheme.TryParseColor(info.Fallback, out Color c))
                item.SetColor(c);
        }
        _suppressPreview = false;
        _currentXaml = CustomTheme.BuildXaml(_items.Select(i => new KeyValuePair<string, Color>(i.Key, i.Color)));
        CodeEditor.Text = _currentXaml;
        ApplyPreview(_currentXaml, quiet: false);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string xaml = EditorTabs.SelectedItem == CodeTab ? CodeEditor.Text : _currentXaml;

        ThemeValidationResult result = CustomTheme.Validate(xaml);
        if (!result.Ok)
        {
            string first = result.Errors.FirstOrDefault() ?? "That theme is not valid.";
            ShowStatus(result.ErrorLine > 0 ? $"Line {result.ErrorLine}: {first}" : first, isError: true);
            return;
        }

        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            CustomTheme.WriteFile(_path, xaml);
        }
        catch (Exception ex)
        {
            ShowStatus("Could not save the theme: " + ex.Message, isError: true);
            return;
        }

        App.Settings.Prop.Theme2 = Theme.Custom;
        App.Settings.Save();
        _saved = true;

        RemovePreviewDict();
        WindowBackdrop.ApplyThemeToAllOpenWindows();
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CustomThemeEditor_Closed(object? sender, EventArgs e)
    {
        Closed -= CustomThemeEditor_Closed;
        CodeEditor.TextChanged -= CodeEditor_TextChanged;
        _previewTimer.Stop();
        _previewTimer.Tick -= PreviewTimer_Tick;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= Watcher_Changed;
            _watcher.Dispose();
            _watcher = null;
        }

        foreach (ThemeColorItem item in _items)
        {
            item.Changed -= SchedulePreview;
            item.Detach();
        }

        if (!_saved)
            RemovePreviewDict();

        WindowBackdrop.ApplyThemeToAllOpenWindows();
    }
}
