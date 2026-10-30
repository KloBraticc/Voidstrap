using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Voidstrap.Integrations.Mcp;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class FastFlagEditorPage : IMcpEditor
{
    private static FastFlagEditorPage? _activeInstance;

    private string? _mcpEditorId;

    string IMcpEditor.Kind => "fastflags";

    string IMcpEditor.DisplayName => "FastFlags";

    string IMcpEditor.Language => ".json";

    string IMcpEditor.Guidance =>
        "Roblox FastFlags JSON object mapping flag names (such as FFlag, DFInt, FString) to string values. " +
        "Changes update the FastFlag Editor table right away and are saved to disk when save_editor is called.";

    public static void RefreshActivePage()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;

        _activeInstance?.ReloadList();
    }

    private void RegisterMcpEditor()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;

        _activeInstance = this;
        _mcpEditorId ??= McpEditorRegistry.Register(this);
    }

    private void UnregisterMcpEditor()
    {
        if (ReferenceEquals(_activeInstance, this))
            _activeInstance = null;

        McpEditorRegistry.Unregister(_mcpEditorId);
        _mcpEditorId = null;
    }

    string IMcpEditor.GetCode()
    {
        var obj = new JsonObject();

        foreach (var pair in App.FastFlags.Prop.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            obj[pair.Key] = pair.Value?.ToString() ?? string.Empty;

        return obj.ToJsonString(_indentedJson);
    }

    McpEditResult IMcpEditor.SetCode(string code)
    {
        JsonObject? parsed;

        try
        {
            parsed = JsonNode.Parse(code ?? string.Empty) as JsonObject;
        }
        catch (Exception ex)
        {
            return new McpEditResult(false, "Invalid JSON: " + ex.Message);
        }

        if (parsed == null)
            return new McpEditResult(false, "FastFlags code must be a JSON object mapping flag names to values.");

        var next = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, node) in parsed)
        {
            string name = key.Trim();

            if (name.Length == 0 || node == null)
                continue;

            next[name] = node is JsonValue jv && jv.TryGetValue(out string? s) ? s : node.ToJsonString();
        }

        foreach (string existing in App.FastFlags.Prop.Keys.ToList())
        {
            if (!next.ContainsKey(existing))
                App.FastFlags.SetValue(existing, null);
        }

        foreach (var (name, val) in next)
            App.FastFlags.SetValue(name, val);

        ReloadList();
        return new McpEditResult(true, "Updated " + next.Count + " FastFlags in the editor. Call save_editor to save them to disk.");
    }

    McpEditResult IMcpEditor.Save()
    {
        try
        {
            App.FastFlags.Save();
            ReloadList();
            return new McpEditResult(true, "Saved FastFlags.");
        }
        catch (Exception ex)
        {
            return new McpEditResult(false, "Could not save FastFlags: " + ex.Message);
        }
    }
}
