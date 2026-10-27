using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ICSharpCode.AvalonEdit.Indentation;
using ICSharpCode.AvalonEdit.Search;
using Voidstrap.Integrations.Plugins;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Editor;

public partial class VoidstrapModEditorWindow : WpfUiWindow
{
	private static readonly string[] EditableExtensions = [".tsx", ".ts", ".js", ".mjs", ".jsx", ".json", ".md"];

	private static readonly string[] RequiredFiles = ["hooks/register.tsx", "hooks/hooks.json", PluginManifest.ManifestFolder + "/plugin.json"];

	private readonly string _mod;

	private readonly string _folder;

	private string? _file;

	private bool _loading;

	private bool _dirty;

	private bool _busy;

	public VoidstrapModEditorWindow(string mod)
	{
		LinuxEditorCompat.Install();
		InitializeComponent();
		_mod = mod;
		_folder = Path.Combine(Paths.Plugins, mod);
		Title = string.Format(Strings.Ui_VoidstrapMods_EditorTitle, mod);
		WindowTitleBar.Title = Title;
		CodeEditor.Options.IndentationSize = 2;
		CodeEditor.Options.ConvertTabsToSpaces = true;
		CodeEditor.Options.HighlightCurrentLine = true;
		CodeEditor.TextArea.IndentationStrategy = new DefaultIndentationStrategy();
		try
		{
			SearchPanel.Install(CodeEditor.TextArea);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("VoidstrapModEditorWindow", "Search could not be added to the editor: " + ex.Message);
		}
		CodeEditor.TextChanged += CodeEditor_TextChanged;
		CodeEditor.PreviewKeyDown += CodeEditor_PreviewKeyDown;
		Closed += Window_Closed;
		RefreshFiles(RequiredFiles[0]);
	}

	private void Window_Closed(object? sender, EventArgs e)
	{
		CodeEditor.TextChanged -= CodeEditor_TextChanged;
		CodeEditor.PreviewKeyDown -= CodeEditor_PreviewKeyDown;
		Closed -= Window_Closed;
	}

	private void Window_Closing(object? sender, CancelEventArgs e)
	{
		if (_dirty)
			SaveFile();
	}

	private void RefreshFiles(string? select)
	{
		if (!Directory.Exists(_folder))
		{
			Close();
			return;
		}
		string[] files = Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories)
			.Select(path => Path.GetRelativePath(_folder, path).Replace('\\', '/'))
			.Where(path => !path.StartsWith(PluginManifest.ManifestFolder + "/types/", StringComparison.OrdinalIgnoreCase))
			.Where(path => EditableExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
			.OrderBy(path => Array.IndexOf(RequiredFiles, path) is int index && index >= 0 ? index : RequiredFiles.Length)
			.ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
			.ToArray();
		_loading = true;
		FileList.Items.Clear();
		foreach (string file in files)
			FileList.Items.Add(file);
		_loading = false;
		string? target = files.FirstOrDefault(file => string.Equals(file, select, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault();
		if (target != null)
			FileList.SelectedItem = target;
	}

	private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading || FileList.SelectedItem is not string next || string.Equals(next, _file, StringComparison.Ordinal))
			return;
		if (_dirty)
			SaveFile();
		OpenFile(next);
	}

	private void OpenFile(string relative)
	{
		string? path = ResolvePath(relative);
		if (path == null || !File.Exists(path))
			return;
		_loading = true;
		try
		{
			CodeEditor.Text = File.ReadAllText(path);
			CodeEditor.ScrollToHome();
			CodeHighlighting.Apply(CodeEditor, relative);
			_file = relative;
			FileTitleText.Text = relative;
			DeleteFileButton.IsEnabled = !RequiredFiles.Contains(relative, StringComparer.OrdinalIgnoreCase);
			SetDirty(false);
		}
		catch (Exception ex)
		{
			OutputBox.Text = ex.Message;
		}
		finally
		{
			_loading = false;
		}
	}

	private void CodeEditor_TextChanged(object? sender, EventArgs e)
	{
		if (!_loading && _file != null)
			SetDirty(true);
	}

	private void CodeEditor_PreviewKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
		{
			e.Handled = true;
			_ = SaveAndReloadAsync();
		}
	}

	private void SetDirty(bool dirty)
	{
		_dirty = dirty;
		DirtyText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
	}

	private bool SaveFile()
	{
		string? path = _file == null ? null : ResolvePath(_file);
		if (path == null)
			return false;
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, CodeEditor.Text);
			SetDirty(false);
			return true;
		}
		catch (Exception ex)
		{
			OutputBox.Text = ex.Message;
			return false;
		}
	}

	private void Save_Click(object sender, RoutedEventArgs e) => _ = SaveAndReloadAsync();

	private async Task SaveAndReloadAsync()
	{
		if (_busy || _file == null || !SaveFile())
			return;
		_busy = true;
		try
		{
			string header = string.Format(Strings.Ui_VoidstrapMods_SavedMessage, _mod, _file);
			OutputBox.Text = header;
			PluginHost.ClearProblems(_mod);
			string folder = _folder;
			(int code, string output) = await Task.Run(() =>
			{
				StringWriter writer = new();
				writer.WriteLine(header);
				int result = ModTools.Validate(folder, writer);
				return (result, writer.ToString().Trim());
			});
			OutputBox.Text = output;
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
		if (_busy)
			return;
		if (_dirty && !SaveFile())
			return;
		_busy = true;
		try
		{
			string folder = _folder;
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
		if (relative.Length == 0)
			return;
		if (!EditableExtensions.Contains(Path.GetExtension(relative).ToLowerInvariant()))
			relative += ".ts";
		string? path = ResolvePath(relative);
		if (path == null)
			return;
		if (_dirty)
			SaveFile();
		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			if (!File.Exists(path))
				File.WriteAllText(path, SeedFor(relative));
			NewFileBox.Visibility = Visibility.Collapsed;
			RefreshFiles(relative);
		}
		catch (Exception ex)
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
		if (_file == null || RequiredFiles.Contains(_file, StringComparer.OrdinalIgnoreCase))
			return;
		string? path = ResolvePath(_file);
		if (path == null || !File.Exists(path))
			return;
		try
		{
			File.Delete(path);
			_file = null;
			SetDirty(false);
			RefreshFiles(RequiredFiles[0]);
		}
		catch (Exception ex)
		{
			OutputBox.Text = ex.Message;
		}
	}

	private string? ResolvePath(string relative)
	{
		if (string.IsNullOrWhiteSpace(relative))
			return null;
		string root = Path.GetFullPath(_folder);
		string full = Path.GetFullPath(Path.Combine(root, relative));
		return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
	}
}
