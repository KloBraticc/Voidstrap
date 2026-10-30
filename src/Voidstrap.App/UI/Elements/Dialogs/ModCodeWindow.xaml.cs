using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.Integrations.Plugins;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class ModCodeWindow : WpfUiWindow
{
    private const long MaxFileBytes = 512 * 1024;

    private const int MaxSearchResults = 500;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts",
        ".tsx",
        ".js",
        ".mjs",
        ".jsx",
        ".json",
        ".md",
        ".txt",
        ".svg",
    };

    private string? _currentPath;

    private string? _savedText;

    private bool _suppressDirty;

    public ModCodeWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        Directory.CreateDirectory(Paths.Plugins);
        string[] mods = ModFolders(Paths.Plugins).ToArray();
        if (mods.Length == 0)
        {
            TreeEmpty.Visibility = Visibility.Visible;
            FileTree.Visibility = Visibility.Collapsed;
            SearchBox.IsEnabled = false;
            return;
        }
        SourceHint.Text = string.Format(Strings.Ui_ModCode_Hint, mods.Sum(mod => CodeFiles(mod).Count()), mods.Length);
        foreach (string mod in mods)
            FileTree.Items.Add(MakeFolderNode(mod));
    }

    private static IEnumerable<string> ModFolders(string path) =>
        Directory
            .EnumerateDirectories(path)
            .Where(folder => (File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0 && IsVisibleFolder(folder))
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase);

    private static bool IsVisibleFolder(string folder) =>
        !(
            Path.GetFileName(folder).Equals("types", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(Path.GetDirectoryName(folder))?.Equals(PluginManifest.ManifestFolder, StringComparison.OrdinalIgnoreCase) == true
        );

    private static bool IsCodeFile(string file) =>
        TextExtensions.Contains(Path.GetExtension(file)) && new FileInfo(file).Length <= MaxFileBytes;

    private static IEnumerable<string> CodeFiles(string folder) =>
        Directory
            .EnumerateFiles(folder)
            .Where(IsCodeFile)
            .Concat(ModFolders(folder).SelectMany(CodeFiles));

    private static TreeViewItem MakeFolderNode(string path)
    {
        TreeViewItem node = new() { Header = "[DIR] " + Path.GetFileName(path), Tag = path };
        node.Items.Add(new TreeViewItem { Header = "..." });
        node.Expanded += Folder_Expanded;
        return node;
    }

    private static void Folder_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { Tag: string path } node || node.Items.Count != 1 || node.Items[0] is not TreeViewItem { Tag: null })
            return;
        node.Items.Clear();
        foreach (string folder in ModFolders(path))
            node.Items.Add(MakeFolderNode(folder));
        foreach (string file in Directory.EnumerateFiles(path).Where(IsCodeFile).OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
        {
            TreeViewItem item = new() { Header = Path.GetFileName(file), Tag = file };
            item.Selected += File_Selected;
            node.Items.Add(item);
        }
    }

    private static void File_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { Tag: string path } && Window.GetWindow((DependencyObject)sender) is ModCodeWindow window)
            window.OpenFile(path);
    }

    private static string Relative(string path) => Path.GetRelativePath(Paths.Plugins, path).Replace('\\', '/');

    private void OpenFile(string path)
    {
        try
        {
            string content = File.ReadAllText(path);
            _currentPath = path;
            _savedText = content;
            _suppressDirty = true;
            CodeEditor.Text = content;
            _suppressDirty = false;
            CodeEditor.IsReadOnly = false;
            SaveButton.IsEnabled = false;
            EditorStatus.Text = string.Format(Strings.Ui_AppCode_FileOpened, Relative(path), content.Length, "");
            FooterStatus.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditorStatus.Text = string.Format(Strings.Ui_AppCode_ReadFailed, ex.Message);
        }
    }

    private void CodeEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressDirty || _currentPath == null)
            return;
        SaveButton.IsEnabled = !string.Equals(CodeEditor.Text, _savedText, StringComparison.Ordinal);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath == null)
            return;
        try
        {
            string temporary = _currentPath + ".tmp";
            File.WriteAllText(temporary, CodeEditor.Text);
            File.Move(temporary, _currentPath, true);
            _savedText = CodeEditor.Text;
            SaveButton.IsEnabled = false;
            EditorStatus.Text = string.Format(Strings.Ui_ModCode_Saved, Relative(_currentPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            EditorStatus.Text = string.Format(Strings.Ui_AppCode_SaveFailed, ex.Message);
        }
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Search_Click(sender, e);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        string query = (SearchBox.Text ?? "").Trim();
        if (query.Length < 2)
        {
            FooterStatus.Text = Strings.Ui_AppCode_SearchShort;
            return;
        }
        try
        {
            List<string> results = [];
            foreach (string file in CodeFiles(Paths.Plugins))
            {
                if (results.Count >= MaxSearchResults)
                    break;
                string relative = Relative(file);
                if (relative.Contains(query, StringComparison.OrdinalIgnoreCase))
                    results.Add(relative);
                int number = 0;
                foreach (string line in File.ReadLines(file))
                {
                    number++;
                    if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                        results.Add(relative + ":" + number + ": " + line.Trim());
                }
            }
            SearchResults.ItemsSource = results;
            SearchResults.Visibility = Visibility.Visible;
            FileTree.Visibility = Visibility.Collapsed;
            BrowseButton.Visibility = Visibility.Visible;
            FooterStatus.Text = string.Format(Strings.Ui_AppCode_SearchResults, results.Count, query);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FooterStatus.Text = string.Format(Strings.Ui_AppCode_SearchFailed, ex.Message);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        SearchResults.Visibility = Visibility.Collapsed;
        BrowseButton.Visibility = Visibility.Collapsed;
        FileTree.Visibility = Visibility.Visible;
    }

    private void SearchResult_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (SearchResults.SelectedItem is not string line || line.Length == 0)
            return;
        int colon = line.IndexOf(':');
        OpenFile(Path.Combine(Paths.Plugins, colon > 0 ? line[..colon] : line));
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => PlatformShell.TryOpenFolder(Paths.Plugins);
}
