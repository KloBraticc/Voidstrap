using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Indentation;
using Voidstrap.Integrations.Plugins;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace Voidstrap.UI.Elements.Editor;

public partial class VoidstrapModEditorWindow : WpfUiWindow
{
    private const long MaxFileBytes = 512 * 1024;

    private static readonly string[] EditableExtensions = [".tsx", ".ts", ".js", ".mjs", ".jsx", ".json", ".md"];

    private static readonly string[] RequiredFiles =
    [
        "hooks/register.tsx",
        "hooks/hooks.json",
        PluginManifest.ManifestFolder + "/plugin.json",
    ];

    private sealed class OpenDocument(string path, TextDocument text)
    {
        public string Path { get; } = path;

        public TextDocument Text { get; } = text;

        public bool Dirty { get; set; }

        public Border Tab { get; set; } = null!;
    }

    private readonly string? _onlyMod;

    private readonly List<OpenDocument> _documents = [];

    private OpenDocument? _active;

    private bool _switching;

    private bool _busy;

    public VoidstrapModEditorWindow(string? mod = null)
    {
        LinuxEditorCompat.Install();
        InitializeComponent();
        _onlyMod = mod;
        Title = mod != null ? string.Format(Strings.Ui_VoidstrapMods_EditorTitle, mod) : Strings.Ui_ModCode_Title;
        WindowTitleBar.Title = Title;
        EditorAppearance.Attach(CodeEditor);
        CodeEditor.Options.IndentationSize = 2;
        CodeEditor.Options.ConvertTabsToSpaces = true;
        CodeEditor.Options.HighlightCurrentLine = true;
        CodeEditor.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
        CodeEditor.TextChanged += CodeEditor_TextChanged;
        CodeEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;
        PreviewKeyDown += Window_PreviewKeyDown;
        Closed += Window_Closed;
        Directory.CreateDirectory(Paths.Plugins);
        BuildTree("");
        string? first = mod == null ? null : Path.Combine(Paths.Plugins, mod, RequiredFiles[0]);
        if (first != null && File.Exists(first))
            OpenFile(first);
        UpdateActions();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        CodeEditor.TextChanged -= CodeEditor_TextChanged;
        CodeEditor.TextArea.Caret.PositionChanged -= Caret_PositionChanged;
        PreviewKeyDown -= Window_PreviewKeyDown;
        Closed -= Window_Closed;
    }

    private void Window_Closing(object? sender, CancelEventArgs e) => SaveAll();

    private IEnumerable<string> ProjectFolders()
    {
        if (_onlyMod != null)
            return Directory.Exists(Path.Combine(Paths.Plugins, _onlyMod)) ? [Path.Combine(Paths.Plugins, _onlyMod)] : [];
        return Directory
            .EnumerateDirectories(Paths.Plugins)
            .Where(folder => !Path.GetFileName(folder).StartsWith('.') && !IsLink(folder))
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsHiddenFolder(string folder) =>
        IsLink(folder)
        || (
            Path.GetFileName(folder).Equals("types", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(folder))?.Equals(PluginManifest.ManifestFolder, StringComparison.OrdinalIgnoreCase) == true
        );

    private static bool IsEditable(string file) =>
        EditableExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()) && new FileInfo(file).Length <= MaxFileBytes;

    private static IEnumerable<string> Files(string folder) =>
        Directory.EnumerateFiles(folder).Where(IsEditable).Concat(Directory.EnumerateDirectories(folder).Where(f => !IsHiddenFolder(f)).SelectMany(Files));

    private void BuildTree(string query)
    {
        FileTree.Items.Clear();
        string[] projects = ProjectFolders().ToArray();
        TreeViewItem solution = MakeItem(SymbolRegular.Apps24, Strings.Ui_Nav_VoidstrapMods + " (" + projects.Length + ")", null, EditorFileIcons.Project);
        solution.IsExpanded = true;
        FileTree.Items.Add(solution);
        foreach (string project in projects)
        {
            TreeViewItem node = MakeItem(SymbolRegular.PuzzlePiece24, Path.GetFileName(project), null, EditorFileIcons.Project);
            node.IsExpanded = query.Length > 0 || _onlyMod != null;
            AddChildren(node, project, query);
            if (query.Length == 0 || node.Items.Count > 0)
                solution.Items.Add(node);
        }
    }

    private static void AddChildren(TreeViewItem parent, string folder, string query)
    {
        foreach (string child in Directory.EnumerateDirectories(folder).Where(f => !IsHiddenFolder(f)).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            TreeViewItem node = MakeItem(SymbolRegular.Folder24, Path.GetFileName(child), null, EditorFileIcons.Folder);
            node.IsExpanded = query.Length > 0;
            AddChildren(node, child, query);
            if (node.Items.Count > 0)
                parent.Items.Add(node);
        }
        foreach (string file in Directory.EnumerateFiles(folder).Where(IsEditable).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (query.Length > 0 && !Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase) && !FileContains(file, query))
                continue;
            (SymbolRegular icon, System.Windows.Media.Color color) = EditorFileIcons.For(file);
            parent.Items.Add(MakeItem(icon, Path.GetFileName(file), file, color));
        }
    }

    private static bool FileContains(string file, string query)
    {
        try
        {
            return File.ReadAllText(file).Contains(query, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static TreeViewItem MakeItem(SymbolRegular icon, string text, string? path, System.Windows.Media.Color color)
    {
        StackPanel header = new() { Orientation = Orientation.Horizontal };
        header.Children.Add(new SymbolIcon { Symbol = icon, Filled = true, FontSize = 14, Margin = new Thickness(0, 0, 5, 0), Foreground = new System.Windows.Media.SolidColorBrush(color) });
        header.Children.Add(new System.Windows.Controls.TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        TreeViewItem item = new() { Header = header, Tag = path };
        if (path != null)
            item.Selected += File_Selected;
        return item;
    }

    private static void File_Selected(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is TreeViewItem { Tag: string path } && Window.GetWindow((DependencyObject)sender) is VoidstrapModEditorWindow window)
            window.OpenFile(path);
    }

    private void ExplorerSearch_TextChanged(object? sender, EventArgs e)
    {
        ExplorerSearchHint.Visibility = ExplorerSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        BuildTree(ExplorerSearch.Text.Trim());
    }

    private static string Relative(string path) => Path.GetRelativePath(Paths.Plugins, path).Replace('\\', '/');

    private static string ModOf(string path) => Relative(path).Split('/')[0];

    private static string InMod(string path) => Path.GetRelativePath(Path.Combine(Paths.Plugins, ModOf(path)), path).Replace('\\', '/');

    private void OpenFile(string path)
    {
        OpenDocument? document = _documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (document == null)
        {
            try
            {
                document = new OpenDocument(path, new TextDocument(File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                OutputBox.Text = ex.Message;
                return;
            }
            document.Tab = MakeTab(document);
            _documents.Add(document);
            DocumentTabs.Children.Add(document.Tab);
        }
        Activate(document);
    }

    private Border MakeTab(OpenDocument document)
    {
        StackPanel content = new() { Orientation = Orientation.Horizontal };
        content.Children.Add(new System.Windows.Controls.TextBlock { FontSize = 12, Foreground = System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        System.Windows.Controls.Button close = new()
        {
            Content = new SymbolIcon { Symbol = SymbolRegular.Dismiss24, FontSize = 10, Foreground = System.Windows.Media.Brushes.White },
            Style = (Style)FindResource("VsToolButton"),
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(2),
            Tag = document,
        };
        close.Click += CloseTab_Click;
        content.Children.Add(close);
        Border tab = new() { Child = content, Padding = new Thickness(8, 3, 4, 3), Tag = document, Cursor = Cursors.Hand };
        tab.MouseLeftButtonDown += Tab_MouseLeftButtonDown;
        return tab;
    }

    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: OpenDocument document })
            Activate(document);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: OpenDocument document })
            return;
        e.Handled = true;
        if (document.Dirty && !Write(document))
            return;
        CloseDocument(document);
    }

    private void CloseDocument(OpenDocument document)
    {
        int index = _documents.IndexOf(document);
        _documents.Remove(document);
        DocumentTabs.Children.Remove(document.Tab);
        if (_active != document)
            return;
        _active = null;
        if (_documents.Count > 0)
            Activate(_documents[Math.Min(index, _documents.Count - 1)]);
        else
        {
            _switching = true;
            CodeEditor.Document = new TextDocument();
            CodeEditor.IsReadOnly = true;
            _switching = false;
            UpdateActions();
        }
    }

    private void Activate(OpenDocument document)
    {
        _switching = true;
        _active = document;
        CodeEditor.Document = document.Text;
        CodeEditor.IsReadOnly = false;
        Voidstrap.UI.Elements.Controls.CodeEditorBehavior.SetLanguage(CodeEditor, Path.GetExtension(document.Path));
        CodeHighlighting.Apply(CodeEditor, document.Path);
        _switching = false;
        foreach (OpenDocument open in _documents)
            RefreshTab(open);
        UpdateActions();
        UpdateCaret();
    }

    private void RefreshTab(OpenDocument document)
    {
        document.Tab.Background = document == _active ? (System.Windows.Media.Brush)FindResource("AccentFillColorDefaultBrush") : System.Windows.Media.Brushes.Transparent;
        if (document.Tab.Child is StackPanel { Children: [System.Windows.Controls.TextBlock label, ..] })
            label.Text = Path.GetFileName(document.Path) + (document.Dirty ? "*" : "");
    }

    private void UpdateActions()
    {
        SaveMenu.IsEnabled = _active != null;
        DeleteMenu.IsEnabled = _active != null && !RequiredFiles.Contains(InMod(_active.Path), StringComparer.OrdinalIgnoreCase);
    }

    private void CodeEditor_TextChanged(object? sender, EventArgs e)
    {
        if (_switching || _active == null || _active.Dirty)
            return;
        _active.Dirty = true;
        RefreshTab(_active);
    }

    private void Caret_PositionChanged(object? sender, EventArgs e) => UpdateCaret();

    private void UpdateCaret()
    {
        if (_active == null)
        {
            CaretText.Text = "";
            return;
        }
        var caret = CodeEditor.TextArea.Caret;
        string endings = _active.Text.Text.Contains("\r\n", StringComparison.Ordinal) ? "CRLF" : "LF";
        CaretText.Text = $"Ln {caret.Line}    Col {caret.VisualColumn + 1}    Ch {caret.Column}    SPC    {endings}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.B && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = SaveAndReloadAsync();
            return;
        }
        if (e.Key != Key.S || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            SaveAll();
        else
            _ = SaveAndReloadAsync();
    }

    private bool Write(OpenDocument document)
    {
        try
        {
            string temporary = document.Path + ".tmp";
            File.WriteAllText(temporary, document.Text.Text);
            File.Move(temporary, document.Path, true);
            document.Dirty = false;
            RefreshTab(document);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OutputBox.Text = ex.Message;
            return false;
        }
    }

    private bool SaveAll() => _documents.Where(d => d.Dirty).ToList().Aggregate(true, (ok, d) => Write(d) && ok);

    private void SaveOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_active != null)
            Write(_active);
    }

    private void SaveAll_Click(object sender, RoutedEventArgs e) => SaveAll();

    private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAndReloadAsync();

    private async Task SaveAndReloadAsync()
    {
        if (_busy || _active == null || !SaveAll())
            return;
        _busy = true;
        try
        {
            string mod = ModOf(_active.Path);
            string header = string.Format(Strings.Ui_VoidstrapMods_SavedMessage, mod, InMod(_active.Path));
            OutputBox.Text = header;
            PluginHost.ClearProblems(mod);
            string folder = Path.Combine(Paths.Plugins, mod);
            (int code, string output) = await Task.Run(() =>
            {
                StringWriter writer = new();
                writer.WriteLine(header);
                int result = ModTools.Validate(folder, writer);
                return (result, writer.ToString().Trim());
            });
            OutputBox.Text = output;
            StatusText.Text = code == 0 ? Strings.Ui_EditorSettings_BuildSucceeded : Strings.Ui_EditorSettings_BuildFailed;
            if (code == 0)
                _ = Task.Run(PluginHost.Reload);
        }
        catch (Exception ex)
        {
            OutputBox.Text = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _active == null || !SaveAll())
            return;
        _busy = true;
        try
        {
            string folder = Path.Combine(Paths.Plugins, ModOf(_active.Path));
            OutputBox.Text = await Task.Run(() =>
            {
                StringWriter writer = new();
                ModTools.Test(folder, writer);
                return writer.ToString().Trim();
            });
        }
        catch (Exception ex)
        {
            OutputBox.Text = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private void NewFile_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null && _onlyMod == null)
            return;
        NewFileBox.Visibility = NewFileBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (NewFileBox.Visibility != Visibility.Visible)
            return;
        NewFileBox.Text = "hooks/helper.ts";
        NewFileBox.Focus();
        NewFileBox.SelectAll();
    }

    private void NewFileBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            NewFileBox.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        string relative = (NewFileBox.Text ?? "").Trim().Replace('\\', '/').TrimStart('/');
        string? mod = _onlyMod ?? (_active == null ? null : ModOf(_active.Path));
        if (relative.Length == 0 || mod == null)
            return;
        if (!EditableExtensions.Contains(Path.GetExtension(relative).ToLowerInvariant()))
            relative += ".ts";
        string? path = ResolvePath(mod, relative);
        if (path == null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                File.WriteAllText(path, SeedFor(relative));
            NewFileBox.Visibility = Visibility.Collapsed;
            BuildTree((ExplorerSearch.Text ?? "").Trim());
            OpenFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OutputBox.Text = ex.Message;
        }
    }

    private static string SeedFor(string relative)
    {
        if (relative.Contains(".test.", StringComparison.OrdinalIgnoreCase))
            return "import { test, expect, mod } from 'voidstrap/testing';\n\ntest('loads cleanly', () => {\n  mod.emit('session.start', { source: 'startup' });\n  expect(true).toBe(true);\n});\n";
        if (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return "{\n}\n";
        return "export const helper = () => true;\n";
    }

    private void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if (_active == null || RequiredFiles.Contains(InMod(_active.Path), StringComparer.OrdinalIgnoreCase))
            return;
        if (
            Frontend.ShowMessageBox(
                string.Format(Strings.Ui_VoidstrapMods_DeleteModPrompt, Relative(_active.Path)),
                MessageBoxImage.Warning,
                MessageBoxButton.YesNo,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        try
        {
            File.Delete(_active.Path);
            CloseDocument(_active);
            BuildTree((ExplorerSearch.Text ?? "").Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            OutputBox.Text = ex.Message;
        }
    }

    private static string? ResolvePath(string mod, string relative)
    {
        string root = Path.GetFullPath(Path.Combine(Paths.Plugins, mod));
        string full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        PlatformShell.TryOpenFolder(_onlyMod != null ? Path.Combine(Paths.Plugins, _onlyMod) : Paths.Plugins);

    private void EditorSettings_Click(object sender, RoutedEventArgs e) => new EditorSettingsWindow().ShowOwnedDialog();

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void Undo_Click(object sender, RoutedEventArgs e) => CodeEditor.Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => CodeEditor.Redo();

    private void Cut_Click(object sender, RoutedEventArgs e) => CodeEditor.Cut();

    private void Copy_Click(object sender, RoutedEventArgs e) => CodeEditor.Copy();

    private void Paste_Click(object sender, RoutedEventArgs e) => CodeEditor.Paste();

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        CodeEditor.Focus();
        ApplicationCommands.Find.Execute(null, CodeEditor.TextArea);
    }

    private void ToggleExplorer_Click(object sender, RoutedEventArgs e)
    {
        bool show = ExplorerMenu.IsChecked;
        ExplorerPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ExplorerColumn.Width = new GridLength(show ? 300 : 0);
        ExplorerSplitColumn.Width = new GridLength(show ? 5 : 0);
    }

    private void ToggleOutput_Click(object sender, RoutedEventArgs e)
    {
        bool show = OutputMenu.IsChecked;
        OutputPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        OutputRow.Height = new GridLength(show ? 130 : 0);
        OutputSplitRow.Height = new GridLength(show ? 5 : 0);
    }
}
