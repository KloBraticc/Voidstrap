using System;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.Integrations.Mcp;

namespace Voidstrap.UI.Elements.Editor;

public partial class KeystrokeThemeEditorWindow : IMcpEditor
{
	private string? _mcpEditorId;

	string IMcpEditor.Kind => "keystrokes";

	string IMcpEditor.DisplayName => _themeName;

	string IMcpEditor.Language => KeystrokeThemeCodeConverter.GetExtension(_currentLanguage);

	string IMcpEditor.Guidance =>
		"Keystrokes overlay theme. The code must keep the same shape the editor generates for its current language, " +
		"colors are hex strings like #RRGGBB or the word Accent. The preview on the right updates after every change.";

	private void RegisterMcpEditor()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;

		_mcpEditorId = McpEditorRegistry.Register(this);
		Closed += OnMcpWindowClosed;
	}

	private void OnMcpWindowClosed(object? sender, EventArgs e)
	{
		Closed -= OnMcpWindowClosed;
		McpEditorRegistry.Unregister(_mcpEditorId);
		_mcpEditorId = null;
	}

	private void OnConnectAiClicked(object sender, System.Windows.RoutedEventArgs e) => McpConnectInfo.Show();

	string IMcpEditor.GetCode()
	{
		if (EditorTabs.SelectedIndex != 1)
			return KeystrokeThemeCodeConverter.GenerateCode(_workingTheme, _currentLanguage);

		return CodeEditor.Text;
	}

	McpEditResult IMcpEditor.SetCode(string code)
	{
		if (EditorTabs.SelectedIndex != 1)
			EditorTabs.SelectedIndex = 1;

		CodeEditor.Document.Replace(0, CodeEditor.Document.TextLength, code ?? string.Empty);

		if (UpdateFromCurrentCode())
			return new McpEditResult(true, "Code parsed and the preview was updated. Call save_editor to keep it.");

		return new McpEditResult(false, TextCodeStatus.Text);
	}

	McpEditResult IMcpEditor.Save()
	{
		if (EditorTabs.SelectedIndex == 1 && !UpdateFromCurrentCode())
			return new McpEditResult(false, "Not saved. " + TextCodeStatus.Text);

		try
		{
			KeystrokeThemeManager.SaveCustomTheme(_workingTheme);
			return new McpEditResult(true, "Saved the theme \"" + _themeName + "\".");
		}
		catch (Exception ex)
		{
			return new McpEditResult(false, "Could not save the theme: " + ex.Message);
		}
	}
}
