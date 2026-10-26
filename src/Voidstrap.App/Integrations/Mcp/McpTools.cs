using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Voidstrap.UI.Elements.Settings.Pages;

namespace Voidstrap.Integrations.Mcp;

public sealed record McpToolResult(string Text, bool IsError);

public static class McpTools
{
	public const string Instructions =
		"These tools control Voidstrap's open editors and its FastFlags. " +
		"Call list_editors first to get an editorId, then get_editor_code to read the current code before changing it. " +
		"set_editor_code replaces the whole document and replace_in_editor edits an exact snippet. Both apply the change to the live preview and report parse or build errors, fix any error before saving. " +
		"Changes stay unsaved until save_editor is called. Keep the language and structure the editor reports.";

	public static JsonArray Definitions()
	{
		return new JsonArray
		{
			Tool("list_editors",
				"List the Voidstrap editors that are open right now, with their editorId, kind, name and code language.",
				new JsonObject(), []),
			Tool("get_editor_code",
				"Read the full code currently shown in an open Voidstrap editor.",
				new JsonObject { ["editorId"] = Str("The editorId returned by list_editors.") },
				["editorId"]),
			Tool("set_editor_code",
				"Replace the whole code of an open Voidstrap editor, apply it to the live preview and return whether it parsed or built. Does not save.",
				new JsonObject
				{
					["editorId"] = Str("The editorId returned by list_editors."),
					["code"] = Str("The complete new code for the editor.")
				},
				["editorId", "code"]),
			Tool("replace_in_editor",
				"Replace an exact snippet in an open Voidstrap editor, apply it to the live preview and return whether it parsed or built. Does not save.",
				new JsonObject
				{
					["editorId"] = Str("The editorId returned by list_editors."),
					["find"] = Str("Exact text to find, including whitespace."),
					["replace"] = Str("Text to put in its place."),
					["replaceAll"] = new JsonObject { ["type"] = "boolean", ["description"] = "Replace every match instead of requiring exactly one." }
				},
				["editorId", "find", "replace"]),
			Tool("save_editor",
				"Save the current code of an open Voidstrap editor to disk. The editor stays open.",
				new JsonObject { ["editorId"] = Str("The editorId returned by list_editors.") },
				["editorId"]),
			Tool("list_fastflags",
				"List the FastFlags Voidstrap currently applies to Roblox.",
				new JsonObject { ["filter"] = Str("Optional case insensitive text the flag name must contain.") },
				[]),
			Tool("set_fastflags",
				"Add, change or remove Voidstrap FastFlags and save them. Pass a JSON object of flag name to value, use null to remove a flag.",
				new JsonObject
				{
					["flags"] = new JsonObject
					{
						["type"] = "object",
						["description"] = "Flag names mapped to their new value, or null to remove the flag.",
						["additionalProperties"] = true
					}
				},
				["flags"])
		};
	}

	private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

	private static JsonObject Tool(string name, string description, JsonObject properties, string[] required)
	{
		var schema = new JsonObject
		{
			["type"] = "object",
			["properties"] = properties
		};

		if (required.Length > 0)
			schema["required"] = new JsonArray(required.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray());

		return new JsonObject
		{
			["name"] = name,
			["description"] = description,
			["inputSchema"] = schema
		};
	}

	public static McpToolResult Execute(string name, JsonObject args)
	{
		try
		{
			return name switch
			{
				"list_editors" => ListEditors(),
				"get_editor_code" => WithEditor(args, editor => new McpToolResult(editor.GetCode(), false)),
				"set_editor_code" => WithEditor(args, editor => FromEdit(editor.SetCode(RequiredString(args, "code")))),
				"replace_in_editor" => WithEditor(args, editor => Replace(editor, args)),
				"save_editor" => WithEditor(args, editor => FromEdit(editor.Save())),
				"list_fastflags" => ListFastFlags(OptionalString(args, "filter")),
				"set_fastflags" => SetFastFlags(args),
				_ => new McpToolResult("Unknown tool: " + name, true)
			};
		}
		catch (ArgumentException ex)
		{
			return new McpToolResult(ex.Message, true);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("McpTools::Execute", ex);
			return new McpToolResult("Voidstrap could not run " + name + ": " + ex.Message, true);
		}
	}

	private static McpToolResult FromEdit(McpEditResult result) => new(result.Message, !result.Success);

	private static McpToolResult ListEditors()
	{
		var editors = McpEditorRegistry.Snapshot();

		if (editors.Count == 0)
			return new McpToolResult("No Voidstrap editor is open. Ask the user to open one, such as the Keystrokes Theme Editor, the Custom Bootstrapper editor, or the FastFlag Editor.", false);

		var list = new JsonArray();

		foreach (var (id, editor) in editors)
		{
			list.Add(new JsonObject
			{
				["editorId"] = id,
				["kind"] = editor.Kind,
				["name"] = editor.DisplayName,
				["language"] = editor.Language,
				["guidance"] = editor.Guidance
			});
		}

		return new McpToolResult(list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), false);
	}

	private static McpToolResult WithEditor(JsonObject args, Func<IMcpEditor, McpToolResult> action)
	{
		string id = RequiredString(args, "editorId");
		IMcpEditor? editor = McpEditorRegistry.Find(id);

		if (editor == null)
			return new McpToolResult("No open editor has the id '" + id + "'. Call list_editors to see the open editors.", true);

		return action(editor);
	}

	private static McpToolResult Replace(IMcpEditor editor, JsonObject args)
	{
		string find = RequiredString(args, "find");
		string replace = OptionalString(args, "replace") ?? throw new ArgumentException("Missing argument: replace");
		bool all = args["replaceAll"] is JsonValue v && v.TryGetValue(out bool b) && b;

		if (find.Length == 0)
			return new McpToolResult("find must not be empty.", true);

		string code = editor.GetCode();
		string normalizedFind = find;
		int count = CountOccurrences(code, normalizedFind);

		if (count == 0 && code.Contains("\r\n", StringComparison.Ordinal) && !find.Contains("\r\n", StringComparison.Ordinal))
		{
			normalizedFind = find.Replace("\n", "\r\n");
			replace = replace.Replace("\r\n", "\n").Replace("\n", "\r\n");
			count = CountOccurrences(code, normalizedFind);
		}

		if (count == 0)
			return new McpToolResult("The text to find was not found in the editor. Call get_editor_code and copy the snippet exactly.", true);

		if (count > 1 && !all)
			return new McpToolResult("The text to find appears " + count + " times. Add more surrounding text so it is unique, or set replaceAll to true.", true);

		string updated;

		if (all)
		{
			updated = code.Replace(normalizedFind, replace, StringComparison.Ordinal);
		}
		else
		{
			int index = code.IndexOf(normalizedFind, StringComparison.Ordinal);
			updated = string.Concat(code.AsSpan(0, index), replace, code.AsSpan(index + normalizedFind.Length));
		}

		McpEditResult result = editor.SetCode(updated);
		return new McpToolResult("Replaced " + (all ? count : 1) + " match(es). " + result.Message, !result.Success);
	}

	private static int CountOccurrences(string text, string value)
	{
		int count = 0;
		int index = 0;

		while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
		{
			count++;
			index += value.Length;
		}

		return count;
	}

	private static McpToolResult ListFastFlags(string? filter)
	{
		var flags = App.FastFlags.Prop
			.Where(p => string.IsNullOrWhiteSpace(filter) || p.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
			.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (flags.Count == 0)
			return new McpToolResult(string.IsNullOrWhiteSpace(filter) ? "No FastFlags are set." : "No FastFlags match '" + filter + "'.", false);

		var obj = new JsonObject();

		foreach (var pair in flags)
			obj[pair.Key] = pair.Value?.ToString() ?? string.Empty;

		return new McpToolResult(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), false);
	}

	private static McpToolResult SetFastFlags(JsonObject args)
	{
		if (args["flags"] is not JsonObject flags || flags.Count == 0)
			return new McpToolResult("flags must be a non empty JSON object of flag name to value.", true);

		var summary = new StringBuilder();

		foreach (var (key, node) in flags)
		{
			string name = key.Trim();

			if (name.Length == 0)
				continue;

			if (node == null)
			{
				App.FastFlags.SetValue(name, null);
				summary.AppendLine("Removed " + name);
				continue;
			}

			string value = node is JsonValue jv && jv.TryGetValue(out string? s) ? s : node.ToJsonString();
			App.FastFlags.SetValue(name, value);
			summary.AppendLine(name + ": " + value);
		}

		App.FastFlags.Save();
		FastFlagEditorPage.RefreshActivePage();
		summary.Append("Saved. Roblox picks the change up the next time it launches.");
		return new McpToolResult(summary.ToString(), false);
	}

	private static string RequiredString(JsonObject args, string name)
	{
		string? value = OptionalString(args, name);

		if (string.IsNullOrEmpty(value))
			throw new ArgumentException("Missing argument: " + name);

		return value;
	}

	private static string? OptionalString(JsonObject args, string name)
	{
		return args[name] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
	}
}
