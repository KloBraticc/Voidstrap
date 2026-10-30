using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Dialogs;

public partial class AppCodeWindow : WpfUiWindow
{
    private string? _currentPath;

    private string? _savedText;

    private bool _suppressDirty;

    public AppCodeWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (!AppSourceStore.Available)
        {
            TreeEmpty.Visibility = Visibility.Visible;
            FileTree.Visibility = Visibility.Collapsed;
            SearchBox.IsEnabled = false;
            EditorStatus.Text = Strings.Ui_AppCode_SourceUnavailableShort;
            return;
        }
        IReadOnlyList<string> all = AppSourceStore.ListAll();
        SourceHint.Text = string.Format(Strings.Ui_AppCode_SourceHint, all.Count, AppSourceStore.ListOverrides().Count);
        PopulateRoot();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
    }

    private void PopulateRoot()
    {
        FileTree.Items.Clear();
        foreach ((string name, bool isFolder) in AppSourceStore.ListFolder(null))
            FileTree.Items.Add(MakeNode(name, isFolder, name));
    }

    private static TreeViewItem MakeNode(string name, bool isFolder, string path)
    {
        TreeViewItem node = new() { Header = isFolder ? "[DIR] " + name : name, Tag = path };
        if (isFolder)
        {
            node.Items.Add(new TreeViewItem { Header = "..." });
            node.Expanded += Folder_Expanded;
        }
        else
        {
            node.Selected += File_Selected;
        }
        return node;
    }

    private static void Folder_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem { Tag: string path } node || HasLoadedChildren(node))
            return;
        node.Items.Clear();
        foreach ((string name, bool isFolder) in AppSourceStore.ListFolder(path))
            node.Items.Add(MakeNode(name, isFolder, path + "/" + name));
    }

    private static bool HasLoadedChildren(TreeViewItem node)
    {
        return node.Items.Count != 1 || node.Items[0] is TreeViewItem child && (child.Header as string) != "...";
    }

    private static void File_Selected(object sender, RoutedEventArgs e)
    {
        if (sender is TreeViewItem { Tag: string path } && Window.GetWindow((DependencyObject)sender) is AppCodeWindow window)
            window.OpenFile(path);
    }

    private void OpenFile(string path)
    {
        try
        {
            string? content = AppSourceStore.Read(path);
            if (content == null)
            {
                EditorStatus.Text = string.Format(Strings.Ui_AppCode_ReadFailed, path);
                return;
            }
            _currentPath = path;
            _savedText = content;
            _suppressDirty = true;
            CodeEditor.Text = content;
            _suppressDirty = false;
            CodeEditor.IsReadOnly = false;
            bool edited = AppSourceStore.HasOverride(path);
            EditedBadge.Visibility = edited ? Visibility.Visible : Visibility.Collapsed;
            ResetButton.IsEnabled = edited;
            SaveButton.IsEnabled = false;
            EditorStatus.Text = string.Format(
                Strings.Ui_AppCode_FileOpened,
                path,
                content.Length,
                edited ? Strings.Ui_AppCode_FileEdited : Strings.Ui_AppCode_FileOriginal
            );
            FooterStatus.Text = "";
        }
        catch (Exception ex)
        {
            EditorStatus.Text = ex.Message;
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
            AppSourceStore.Write(_currentPath, CodeEditor.Text);
            _savedText = CodeEditor.Text;
            SaveButton.IsEnabled = false;
            EditedBadge.Visibility = Visibility.Visible;
            ResetButton.IsEnabled = true;
            EditorStatus.Text = string.Format(Strings.Ui_AppCode_Saved, _currentPath);
            FooterStatus.Text = Strings.Ui_AppCode_SavedFooter;
        }
        catch (Exception ex)
        {
            EditorStatus.Text = string.Format(Strings.Ui_AppCode_SaveFailed, ex.Message);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPath == null)
            return;
        if (
            Frontend.ShowMessageBox(
                string.Format(Strings.Ui_AppCode_ResetConfirm, _currentPath),
                MessageBoxImage.Question,
                MessageBoxButton.YesNo,
                MessageBoxResult.No
            ) != MessageBoxResult.Yes
        )
            return;
        AppSourceStore.Reset(_currentPath);
        OpenFile(_currentPath);
    }

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Search_Click(sender, e);
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        string query = SearchBox.Text ?? "";
        if (query.Trim().Length < 2)
        {
            FooterStatus.Text = Strings.Ui_AppCode_SearchShort;
            return;
        }
        try
        {
            List<string> results = [.. AppSourceStore.Search(query)];
            SearchResults.ItemsSource = results;
            SearchResults.Visibility = Visibility.Visible;
            FileTree.Visibility = Visibility.Collapsed;
            BrowseButton.Visibility = Visibility.Visible;
            FooterStatus.Text = string.Format(Strings.Ui_AppCode_SearchResults, results.Count, query);
        }
        catch (Exception ex)
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
        int firstColon = line.IndexOf(':');
        string path = firstColon > 0 ? line[..firstColon] : line;
        path = path.Replace(" (file name)", "", StringComparison.Ordinal);
        OpenFile(path);
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new() { Title = Strings.Ui_AppCode_ExportPickerTitle };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            int count = AppSourceStore.Export(dialog.FolderName);
            FooterStatus.Text = string.Format(Strings.Ui_AppCode_Exported, count, dialog.FolderName);
            if (
                Frontend.ShowMessageBox(
                    string.Format(Strings.Ui_AppCode_OpenExportedFolder, count),
                    MessageBoxImage.Information,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.Yes
                ) == MessageBoxResult.Yes
            )
            {
                try
                {
                    Process.Start(new ProcessStartInfo(dialog.FolderName) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("AppCodeWindow", string.Format(Strings.Ui_AppCode_FolderOpenFailed, ex.Message));
                }
            }
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(string.Format(Strings.Ui_AppCode_ExportFailed, ex.Message), MessageBoxImage.Warning);
        }
    }
}
