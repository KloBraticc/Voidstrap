using System;
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

	string IMcpEditor.Guidance =>
		"Custom bootstrapper theme, the Theme.xml file. It is Voidstrap custom bootstrapper XML with a root VoidstrapCustomBootstrapper element, " +
		"keep the root element and its attributes. Every change is built into the live preview and any build error is returned.";

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
}
