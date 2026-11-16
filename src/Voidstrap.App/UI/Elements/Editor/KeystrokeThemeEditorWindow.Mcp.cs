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

    string IMcpEditor.Language => ".xml";

    string IMcpEditor.Guidance => McpGuides.KeystrokeTheme + "\n" + KeystrokeXmlTemplates.Reference;

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

    string IMcpEditor.GetCode() => CodeEditor.Text;

    McpEditResult IMcpEditor.SetCode(string code)
    {
        CodeEditor.Document.Replace(0, CodeEditor.Document.TextLength, code ?? string.Empty);

        if (CompileNow() is string error)
            return new McpEditResult(false, error);

        return new McpEditResult(true, "The theme builds and the preview was updated. Call save_editor to keep it.");
    }

    McpEditResult IMcpEditor.Save()
    {
        if (CompileNow() is string error)
            return new McpEditResult(false, "Not saved. " + error);

        return SaveCode()
            ? new McpEditResult(true, "Saved the theme \"" + _themeName + "\".")
            : new McpEditResult(false, "Could not save the theme.");
    }

    IReadOnlyList<string> IMcpEditor.Languages => new[] { "xml" };

    McpEditResult IMcpEditor.SetLanguage(string language) =>
        new(false, "Keystroke themes are written in XML only, the same XML as launcher themes.");
}
