using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Controls;

public static class CodeEditorBehavior
{
    private const string LOG_IDENT = "CodeEditorBehavior";

    public static readonly DependencyProperty LanguageProperty = DependencyProperty.RegisterAttached(
        "Language",
        typeof(string),
        typeof(CodeEditorBehavior),
        new PropertyMetadata(null, OnLanguageChanged));

    private static readonly DependencyProperty HighlighterProperty = DependencyProperty.RegisterAttached(
        "Highlighter",
        typeof(CurrentLineHighlighter),
        typeof(CodeEditorBehavior),
        new PropertyMetadata(null));

    public static void SetLanguage(DependencyObject element, string value) => element.SetValue(LanguageProperty, value);

    public static string GetLanguage(DependencyObject element) => (string)element.GetValue(LanguageProperty);

    private static void OnLanguageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor editor)
            return;

        editor.Loaded -= Editor_Loaded;
        editor.Unloaded -= Editor_Unloaded;
        editor.Loaded += Editor_Loaded;
        editor.Unloaded += Editor_Unloaded;

        if (editor.IsLoaded)
            Attach(editor);
    }

    private static void Editor_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextEditor editor)
            Attach(editor);
    }

    private static void Editor_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextEditor editor)
            Detach(editor);
    }

    private static void Attach(TextEditor editor)
    {
        try
        {
            Detach(editor);

            CodeHighlighting.Apply(editor, GetLanguage(editor) ?? ".txt");
            ApplyChrome(editor);
            InstallEditingCommands(editor);

            editor.SetValue(HighlighterProperty, new CurrentLineHighlighter(editor.TextArea));
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not set up the editor: " + ex.Message);
        }
    }

    private static readonly DependencyProperty CommandsProperty = DependencyProperty.RegisterAttached(
        "Commands",
        typeof(bool),
        typeof(CodeEditorBehavior),
        new PropertyMetadata(false));

    private static readonly DependencyProperty SearchProperty = DependencyProperty.RegisterAttached(
        "Search",
        typeof(ICSharpCode.AvalonEdit.Search.SearchPanel),
        typeof(CodeEditorBehavior),
        new PropertyMetadata(null));

    private static void InstallEditingCommands(TextEditor editor)
    {
        if (editor.GetValue(CommandsProperty) is bool done && done)
            return;

        editor.SetValue(CommandsProperty, true);

        try
        {
            var panel = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(editor);
            editor.SetValue(SearchProperty, panel);

            panel.Loaded += SearchPanel_Loaded;
            panel.IsVisibleChanged += SearchPanel_VisibleChanged;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Search is unavailable: " + ex.Message);
        }

        Bind(editor, Key.F, ModifierKeys.Control, () => OpenSearch(editor, replace: false));
        Bind(editor, Key.H, ModifierKeys.Control, () => OpenSearch(editor, replace: true));
        Bind(editor, Key.G, ModifierKeys.Control, () => GoToLine(editor));
        Bind(editor, Key.D, ModifierKeys.Control, () => DuplicateLine(editor));
        Bind(editor, Key.L, ModifierKeys.Control | ModifierKeys.Shift, () => DeleteLine(editor));
        Bind(editor, Key.OemQuestion, ModifierKeys.Control, () => ToggleComment(editor));
        Bind(editor, Key.Up, ModifierKeys.Alt, () => MoveLine(editor, -1));
        Bind(editor, Key.Down, ModifierKeys.Alt, () => MoveLine(editor, 1));
    }

    private static void Bind(TextEditor editor, Key key, ModifierKeys modifiers, Action action)
    {
        editor.InputBindings.Add(new KeyBinding(new CommunityToolkit.Mvvm.Input.RelayCommand(action), key, modifiers));
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush PanelSurface = Frozen(0x2B, 0x2B, 0x2E);
    private static readonly SolidColorBrush PanelField = Frozen(0x1E, 0x1E, 0x1E);
    private static readonly SolidColorBrush PanelStroke = Frozen(0x3F, 0x3F, 0x46);
    private static readonly SolidColorBrush PanelText = Frozen(0xE6, 0xE6, 0xE6);
    private static readonly SolidColorBrush PanelButton = Frozen(0x3A, 0x3A, 0x3F);

    private static bool IsLight(Brush? brush)
    {
        if (brush is not SolidColorBrush solid || solid.Color.A == 0)
            return false;

        double luma = (0.299 * solid.Color.R + 0.587 * solid.Color.G + 0.114 * solid.Color.B) / 255.0;
        return luma > 0.5;
    }

    private static void ThemeSearchPanel(DependencyObject root, int depth = 0)
    {
        if (depth > 12)
            return;

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            switch (child)
            {
                case System.Windows.Controls.TextBox box:
                    box.Background = PanelField;
                    box.Foreground = PanelText;
                    box.BorderBrush = PanelStroke;
                    box.CaretBrush = PanelText;
                    break;

                case System.Windows.Controls.Primitives.ButtonBase button:
                    button.Background = PanelButton;
                    button.Foreground = PanelText;
                    button.BorderBrush = PanelStroke;
                    break;

                case System.Windows.Controls.Border border:
                    if (IsLight(border.Background))
                        border.Background = depth <= 1 ? PanelSurface : PanelButton;
                    if (IsLight(border.BorderBrush) || border.BorderBrush is SolidColorBrush { Color.R: 0, Color.G: 0, Color.B: 0 })
                        border.BorderBrush = PanelStroke;
                    break;

                case System.Windows.Shapes.Path path:
                    path.Fill = PanelText;
                    if (path.Stroke != null)
                        path.Stroke = PanelText;
                    break;

                case System.Windows.Controls.Image image:
                    TintIcon(image);
                    break;
            }

            if (child is System.Windows.Controls.Control control && IsLight(control.Background))
                control.Background = PanelSurface;

            ThemeSearchPanel(child, depth + 1);
        }

        foreach (object logical in System.Windows.LogicalTreeHelper.GetChildren(root))
        {
            if (logical is System.Windows.Controls.Primitives.Popup { Child: DependencyObject popupChild })
            {
                if (popupChild is System.Windows.Controls.Border popupBorder)
                {
                    popupBorder.Background = PanelSurface;
                    popupBorder.BorderBrush = PanelStroke;
                }

                ThemeSearchPanel(popupChild, depth + 1);
            }
        }
    }

    private static void TintIcon(System.Windows.Controls.Image image)
    {
        if (image.Source == null || image.Parent is not System.Windows.Controls.Decorator && image.Tag is string)
            return;

        image.Tag = "tinted";
        image.OpacityMask = new ImageBrush(image.Source);
        image.Source = null;

        if (image.Parent is System.Windows.Controls.Decorator decorator)
        {
            decorator.Child = new System.Windows.Shapes.Rectangle
            {
                Fill = PanelText,
                Width = image.Width > 0 ? image.Width : 16,
                Height = image.Height > 0 ? image.Height : 16,
                OpacityMask = image.OpacityMask
            };
        }
    }

    private static void SearchPanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ICSharpCode.AvalonEdit.Search.SearchPanel panel)
            ThemeSearchPanelLater(panel);
    }

    private static void SearchPanel_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is ICSharpCode.AvalonEdit.Search.SearchPanel panel && panel.IsVisible)
            ThemeSearchPanelLater(panel);
    }

    private static void ThemeSearchPanelLater(ICSharpCode.AvalonEdit.Search.SearchPanel panel)
    {
        panel.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                try { ThemeSearchPanel(panel); }
                catch (Exception ex) { App.Logger?.WriteLine(LOG_IDENT, "Could not theme search: " + ex.Message); }
            }),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void OpenSearch(TextEditor editor, bool replace)
    {
        if (editor.GetValue(SearchProperty) is not ICSharpCode.AvalonEdit.Search.SearchPanel panel)
            return;

        try
        {
            if (!editor.TextArea.Selection.IsEmpty)
            {
                string selected = editor.TextArea.Selection.GetText();
                if (!selected.Contains('\n'))
                    panel.SearchPattern = selected;
            }

            if (replace)
            {
                panel.Close();
                ReplaceInDocument(editor);
                return;
            }

            panel.Open();
            panel.Reactivate();
            ThemeSearchPanelLater(panel);
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not open search: " + ex.Message);
        }
    }

    private static void ReplaceInDocument(TextEditor editor)
    {
        string preset = "";

        if (!editor.TextArea.Selection.IsEmpty)
        {
            string selected = editor.TextArea.Selection.GetText();
            if (!selected.Contains('\n'))
                preset = selected;
        }

        var dialog = new Voidstrap.UI.Elements.Dialogs.TextInputDialog("Find", preset, "Replace with", "");
        var owner = Window.GetWindow(editor);

        if (owner != null)
            dialog.Owner = owner;

        dialog.ShowDialog();

        if (!dialog.Confirmed || dialog.Value.Length == 0)
            return;

        string find = dialog.Value;
        string swap = dialog.SecondValue;
        string text = editor.Document.Text;

        int count = 0;
        int index = text.IndexOf(find, StringComparison.Ordinal);

        while (index != -1)
        {
            count++;
            index = text.IndexOf(find, index + find.Length, StringComparison.Ordinal);
        }

        if (count == 0)
        {
            Frontend.ShowMessageBox("Nothing matched " + find, System.Windows.MessageBoxImage.Information);
            return;
        }

        editor.Document.BeginUpdate();
        editor.Document.Text = text.Replace(find, swap, StringComparison.Ordinal);
        editor.Document.EndUpdate();

        Frontend.ShowMessageBox("Replaced " + count + (count == 1 ? " match." : " matches."), System.Windows.MessageBoxImage.Information);
    }

    private static void GoToLine(TextEditor editor)
    {
        var owner = Window.GetWindow(editor);
        var dialog = new Voidstrap.UI.Elements.Dialogs.TextInputDialog(
            "Go to line, 1 to " + editor.Document.LineCount,
            editor.TextArea.Caret.Line.ToString());

        if (owner != null)
            dialog.Owner = owner;

        dialog.ShowDialog();

        if (!dialog.Confirmed || !int.TryParse(dialog.Value.Trim(), out int line))
            return;

        line = Math.Clamp(line, 1, editor.Document.LineCount);
        var target = editor.Document.GetLineByNumber(line);
        editor.Select(target.Offset, target.Length);
        editor.ScrollToLine(line);
        editor.Focus();
    }

    private static void DuplicateLine(TextEditor editor)
    {
        var line = editor.Document.GetLineByNumber(editor.TextArea.Caret.Line);
        string text = editor.Document.GetText(line.Offset, line.Length);
        editor.Document.Insert(line.EndOffset, Environment.NewLine + text);
    }

    private static void DeleteLine(TextEditor editor)
    {
        var line = editor.Document.GetLineByNumber(editor.TextArea.Caret.Line);
        editor.Document.Remove(line.Offset, line.TotalLength);
    }

    private static void MoveLine(TextEditor editor, int direction)
    {
        int number = editor.TextArea.Caret.Line;
        int target = number + direction;

        if (target < 1 || target > editor.Document.LineCount)
            return;

        var current = editor.Document.GetLineByNumber(number);
        var other = editor.Document.GetLineByNumber(target);

        string currentText = editor.Document.GetText(current.Offset, current.Length);
        string otherText = editor.Document.GetText(other.Offset, other.Length);

        editor.Document.BeginUpdate();

        if (direction < 0)
        {
            editor.Document.Replace(other.Offset, other.Length, currentText);
            editor.Document.Replace(editor.Document.GetLineByNumber(number).Offset, currentText.Length, otherText);
        }
        else
        {
            editor.Document.Replace(current.Offset, current.Length, otherText);
            editor.Document.Replace(editor.Document.GetLineByNumber(target).Offset, otherText.Length, currentText);
        }

        editor.Document.EndUpdate();
        editor.TextArea.Caret.Line = target;
        editor.ScrollToLine(target);
    }

    private static void ToggleComment(TextEditor editor)
    {
        string language = (GetLanguage(editor) ?? ".txt").ToLowerInvariant();

        string open;
        string close;

        if (language is ".css" or ".scss")
        {
            open = "/* ";
            close = " */";
        }
        else if (language is ".js" or ".mjs" or ".ts" or ".json")
        {
            open = "// ";
            close = "";
        }
        else
        {
            open = "<!-- ";
            close = " -->";
        }

        int first = editor.TextArea.Selection.IsEmpty
            ? editor.TextArea.Caret.Line
            : editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber;

        int last = editor.TextArea.Selection.IsEmpty
            ? editor.TextArea.Caret.Line
            : editor.Document.GetLineByOffset(editor.SelectionStart + editor.SelectionLength).LineNumber;

        editor.Document.BeginUpdate();

        for (int number = last; number >= first; number--)
        {
            var line = editor.Document.GetLineByNumber(number);
            string text = editor.Document.GetText(line.Offset, line.Length);
            string trimmed = text.TrimStart();

            if (trimmed.Length == 0)
                continue;

            int indent = text.Length - trimmed.Length;

            if (trimmed.StartsWith(open.TrimEnd(), StringComparison.Ordinal))
            {
                string body = trimmed[open.TrimEnd().Length..];
                if (body.StartsWith(' ')) body = body[1..];
                if (close.Length > 0 && body.EndsWith(close.TrimStart(), StringComparison.Ordinal))
                {
                    body = body[..^close.TrimStart().Length];
                    body = body.TrimEnd();
                }
                editor.Document.Replace(line.Offset, line.Length, text[..indent] + body);
            }
            else
            {
                editor.Document.Replace(line.Offset, line.Length, text[..indent] + open + trimmed + close);
            }
        }

        editor.Document.EndUpdate();
    }

    private static void Detach(TextEditor editor)
    {
        if (editor.GetValue(HighlighterProperty) is CurrentLineHighlighter existing)
        {
            existing.Dispose();
            editor.SetValue(HighlighterProperty, null);
        }
    }

    private static void ApplyChrome(TextEditor editor)
    {
        SolidColorBrush background = new(Color.FromRgb(0x1E, 0x1E, 0x1E));
        background.Freeze();

        SolidColorBrush foreground = new(Color.FromRgb(0xD4, 0xD4, 0xD4));
        foreground.Freeze();

        SolidColorBrush lineNumbers = new(Color.FromRgb(0x6E, 0x76, 0x81));
        lineNumbers.Freeze();

        SolidColorBrush selection = new(Color.FromArgb(0x66, 0x26, 0x4F, 0x78));
        selection.Freeze();

        SolidColorBrush caret = new(Color.FromRgb(0xE0, 0xE0, 0xE0));
        caret.Freeze();

        SolidColorBrush link = new(Color.FromRgb(0x4E, 0xC9, 0xB0));
        link.Freeze();

        editor.Background = background;
        editor.Foreground = foreground;
        editor.LineNumbersForeground = lineNumbers;

        editor.TextArea.SelectionBrush = selection;
        editor.TextArea.SelectionBorder = null;
        editor.TextArea.SelectionForeground = null;
        editor.TextArea.SelectionCornerRadius = 2;
        editor.TextArea.Caret.CaretBrush = caret;
        editor.TextArea.TextView.LinkTextForegroundBrush = link;

        editor.Options.HighlightCurrentLine = false;
        editor.Options.EnableHyperlinks = false;
        editor.Options.EnableEmailHyperlinks = false;
        editor.Options.ConvertTabsToSpaces = true;
        editor.Options.IndentationSize = 2;
        editor.Options.ShowBoxForControlCharacters = false;

        if (editor.ReadLocalValue(Control.FontFamilyProperty) == DependencyProperty.UnsetValue)
            editor.FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas");

        if (editor.ReadLocalValue(Control.FontSizeProperty) == DependencyProperty.UnsetValue)
            editor.FontSize = 13;
    }
}
