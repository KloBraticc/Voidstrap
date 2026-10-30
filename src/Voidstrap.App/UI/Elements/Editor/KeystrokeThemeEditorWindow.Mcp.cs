using System;
using System.Collections.Generic;
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
        McpGuides.KeystrokeTheme
        + " "
        + "Keystrokes overlay theme. The code must keep the same shape the editor generates for its current language, "
        + "colors are hex strings like #RRGGBB or the word Accent. The preview on the right updates after every change.";

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

    IReadOnlyList<string> IMcpEditor.Languages => new[] { "csharp", "html", "rust", "json" };

    McpEditResult IMcpEditor.SetLanguage(string language)
    {
        int index = (language ?? "").Trim().ToLowerInvariant() switch
        {
            "csharp" or "c#" or "cs" => 0,
            "html" or "htmlcss" or "css" or "web" => 1,
            "rust" or "rs" => 2,
            "json" => 3,
            _ => -1,
        };

        if (index < 0)
            return new McpEditResult(false, "Unknown language. Use csharp, html, rust, or json.");

        if (EditorTabs.SelectedIndex == 1 && !UpdateFromCurrentCode())
            return new McpEditResult(false, "The current code does not parse, fix it before switching language. " + TextCodeStatus.Text);

        ComboLanguage.SelectedIndex = index;
        if (EditorTabs.SelectedIndex != 1)
            EditorTabs.SelectedIndex = 1;

        return new McpEditResult(
            true,
            "Switched the code view to " + KeystrokeThemeCodeConverter.GetExtension(_currentLanguage) + ". Call get_editor_code to read it."
        );
    }
}
