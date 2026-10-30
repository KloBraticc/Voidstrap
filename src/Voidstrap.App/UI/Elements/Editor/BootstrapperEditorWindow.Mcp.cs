using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Voidstrap.Integrations.Mcp;
using Voidstrap.UI.ViewModels.Editor;

namespace Voidstrap.UI.Elements.Editor;

public partial class BootstrapperEditorWindow : IMcpEditor
{
    private string? _mcpEditorId;

    private string? _mcpPreviewProblem;

    string IMcpEditor.Kind => "bootstrapper";

    string IMcpEditor.DisplayName => _viewModel.Name;

    string IMcpEditor.Language => ".xaml";

    string IMcpEditor.Guidance => McpGuides.BootstrapperTheme;

    private void RegisterMcpEditor()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;

        _mcpEditorId = McpEditorRegistry.Register(this);
    }

    private void UnregisterMcpEditor()
    {
        McpEditorRegistry.Unregister(_mcpEditorId);
        _mcpEditorId = null;
    }

    private void OnConnectAiClicked(object sender, System.Windows.RoutedEventArgs e) => McpConnectInfo.Show();

    private void EnsureRootFileActive()
    {
        if (_activeFile != null && _activeFile.IsRoot)
            return;

        SaveActiveFile(silent: true);
        SelectFile("Theme.xml");
    }

    string IMcpEditor.GetCode() => CurrentThemeXml();

    McpEditResult IMcpEditor.SetCode(string code)
    {
        code = ToCRLF(code ?? string.Empty);

        if (!BootstrapperEditorWindowViewModel.LooksLikeThemeXml(code))
            return new McpEditResult(false, "That is not a custom bootstrapper theme. Keep the root theme element from get_editor_code.");

        EnsureRootFileActive();

        if (_activeFile == null || !_activeFile.IsRoot)
            return new McpEditResult(false, "Theme.xml could not be opened in the editor.");

        UIXML.Document.Replace(0, UIXML.Document.TextLength, code);
        _mcpPreviewProblem = null;
        RenderPreview(true);

        if (_mcpPreviewProblem != null)
            return new McpEditResult(false, "The theme does not build: " + _mcpPreviewProblem);

        return new McpEditResult(true, "The theme builds and the preview was updated. Call save_editor to keep it.");
    }

    McpEditResult IMcpEditor.Save()
    {
        EnsureRootFileActive();

        if (_activeFile == null || !_activeFile.IsRoot)
            return new McpEditResult(false, "Theme.xml could not be opened in the editor.");

        if (!BootstrapperEditorWindowViewModel.LooksLikeThemeXml(UIXML.Text))
            return new McpEditResult(false, "Not saved, the editor does not contain a theme.");

        _viewModel.CodeChanged = true;

        if (!SaveActiveFile(silent: true))
            return new McpEditResult(false, "Theme.xml could not be saved.");

        ThemeSavedCallback(true, "Saved Theme.xml");
        return new McpEditResult(true, "Saved Theme.xml for \"" + _viewModel.Name + "\".");
    }

    private const long McpMaxFileBytes = 2L * 1024 * 1024;

    IReadOnlyList<string> IMcpEditor.Files
    {
        get
        {
            try
            {
                if (!Directory.Exists(_viewModel.Directory))
                    return Array.Empty<string>();

                return Directory
                    .EnumerateFiles(_viewModel.Directory, "*.*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(_viewModel.Directory, path).Replace(Path.DirectorySeparatorChar, '/'))
                    .Where(rel => !rel.StartsWith('.'))
                    .OrderBy(rel => rel.Equals("Theme.xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(rel => rel, StringComparer.OrdinalIgnoreCase)
                    .Select(rel => HasExtension(rel, TextFileExtensions) ? rel : rel + " (binary)")
                    .ToList();
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("BootstrapperEditorWindow::McpFiles", ex.Message);
                return Array.Empty<string>();
            }
        }
    }

    private string? McpResolve(string relativePath, out string error)
    {
        error = "";
        string clean = (relativePath ?? "").Trim().Replace('\\', '/');
        string? full = McpArgs.ContainedPath(_viewModel.Directory, clean);
        if (full == null)
        {
            error = "The file path must stay inside the theme folder.";
            return null;
        }
        if (!HasExtension(clean, TextFileExtensions))
        {
            error = "Only text files can be edited: " + string.Join(", ", TextFileExtensions) + ".";
            return null;
        }
        return full;
    }

    private static bool McpIsRoot(string relativePath) =>
        string.Equals((relativePath ?? "").Trim().Replace('\\', '/'), "Theme.xml", StringComparison.OrdinalIgnoreCase);

    string? IMcpEditor.ReadFile(string relativePath)
    {
        if (McpIsRoot(relativePath))
            return CurrentThemeXml();

        string? full = McpResolve(relativePath, out _);
        if (full == null || !File.Exists(full))
            return null;

        if (
            _activeFile != null
            && string.Equals(_activeFile.FullPath, full, StringComparison.OrdinalIgnoreCase)
            && UIXML.Visibility == System.Windows.Visibility.Visible
        )
            return UIXML.Text;

        FileInfo info = new FileInfo(full);
        if (info.Length > McpMaxFileBytes)
            return null;

        return File.ReadAllText(full);
    }

    McpEditResult IMcpEditor.WriteFile(string relativePath, string text)
    {
        if (McpIsRoot(relativePath))
            return ((IMcpEditor)this).SetCode(text);

        string? full = McpResolve(relativePath, out string error);
        if (full == null)
            return new McpEditResult(false, error);

        text ??= string.Empty;
        if (text.Length > McpMaxFileBytes)
            return new McpEditResult(false, "The file is too large, the limit is 2 MB.");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temporary = full + ".mcp.tmp";
            File.WriteAllText(temporary, ToCRLF(text));
            File.Move(temporary, full, overwrite: true);

            if (_activeFile != null && string.Equals(_activeFile.FullPath, full, StringComparison.OrdinalIgnoreCase))
            {
                UIXML.TextChanged -= OnCodeChanged;
                UIXML.Text = ToCRLF(text);
                UIXML.TextChanged += OnCodeChanged;
                _viewModel.CodeChanged = false;
                EditorDirtyLabel.Text = "";
            }

            RefreshFiles();
            _mcpPreviewProblem = null;
            RenderPreview(true);

            string written = Path.GetRelativePath(_viewModel.Directory, full).Replace(Path.DirectorySeparatorChar, '/');
            if (_mcpPreviewProblem != null)
                return new McpEditResult(true, "Saved " + written + ", but the theme preview reports: " + _mcpPreviewProblem);

            return new McpEditResult(true, "Saved " + written + " and refreshed the preview.");
        }
        catch (Exception ex)
        {
            return new McpEditResult(false, "Could not write the file: " + ex.Message);
        }
    }

    McpEditResult IMcpEditor.DeleteFile(string relativePath)
    {
        if (McpIsRoot(relativePath))
            return new McpEditResult(false, "Theme.xml cannot be deleted.");

        string clean = (relativePath ?? "").Trim().Replace('\\', '/');
        string? full = McpArgs.ContainedPath(_viewModel.Directory, clean);
        if (full == null)
            return new McpEditResult(false, "The file path must stay inside the theme folder.");

        if (!File.Exists(full))
            return new McpEditResult(false, "The file does not exist.");

        try
        {
            if (_activeFile != null && string.Equals(_activeFile.FullPath, full, StringComparison.OrdinalIgnoreCase))
                EnsureRootFileActive();

            File.Delete(full);
            RefreshFiles();
            RenderPreview(true);
            return new McpEditResult(true, "Deleted " + clean + ".");
        }
        catch (Exception ex)
        {
            return new McpEditResult(false, "Could not delete the file: " + ex.Message);
        }
    }
}
