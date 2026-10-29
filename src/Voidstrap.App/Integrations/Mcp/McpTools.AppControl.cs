using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
	public static McpToolResult GetDocs(JsonObject args)
	{
		string? topic = McpArgs.String(args, "topic");
		if (string.IsNullOrWhiteSpace(topic))
		{
			return new McpToolResult("Voidstrap documentation sections: " + string.Join(", ", McpGuides.TopicNames)
				+ ". Call get_docs again with a topic to read it. Topic architecture covers the launch pipeline, the mods systems and every major service. Topic mod_api is the full $ API reference. Topic app_control covers live text and background control. Topic app_code covers reading and editing the app source inside Voidstrap.");
		}
		string? doc = McpGuides.Get(topic);
		if (doc == null)
		{
			return new McpToolResult("Unknown docs topic '" + topic + "'. Sections: " + string.Join(", ", McpGuides.TopicNames) + ".", true);
		}
		return new McpToolResult(doc);
	}

	public static McpToolResult ManageAppControl(JsonObject args)
	{
		string action = McpArgs.String(args, "action")?.Trim().ToLowerInvariant() ?? "text_list";
		try
		{
			switch (action)
			{
				case "text_list":
				{
					IReadOnlyDictionary<string, string> overrides = AppControl.ListText();
					if (overrides.Count == 0)
						return new McpToolResult("No text overrides are active. Set one with action text_set, target 'text:<exact original text>' or 'name:<element name>', and the replacement text.");
					JsonObject list = new();
					foreach (KeyValuePair<string, string> pair in overrides)
						list[pair.Key] = pair.Value;
					return new McpToolResult("Active text overrides:\n" + list.ToJsonString(IndentedJson));
				}
				case "text_set":
				{
					string? target = McpArgs.String(args, "target");
					if (string.IsNullOrWhiteSpace(target))
						return new McpToolResult("text_set needs target 'text:<exact original text>' or 'name:<element name>'. Use text_list to see what is set, or read the source with manage_app_code to find element names.", true);
					string? text = McpArgs.String(args, "text") ?? "";
					string key = AppControl.SetText(target, text);
					return new McpToolResult(string.IsNullOrEmpty(text)
						? "Removed the override for " + key + " (an empty text clears it)."
						: "Text override saved for " + key + ", applied to every open window and persisted.");
				}
				case "text_clear":
				{
					string? target = McpArgs.String(args, "target");
					if (string.IsNullOrWhiteSpace(target))
						return new McpToolResult("text_clear needs the target key from text_list.", true);
					return AppControl.ClearText(target)
						? new McpToolResult("Text override removed, the original text is back on screen.")
						: new McpToolResult("No override existed for that target.", true);
				}
				case "text_clear_all":
				{
					int removed = AppControl.ClearAllText();
					return new McpToolResult(removed + " text override(s) removed, every original text is back.");
				}
				case "background_get":
					return new McpToolResult("Current background state:\n" + AppControl.GetBackground().ToJsonString(IndentedJson));
				case "background_set":
				{
					string? file = McpArgs.String(args, "file");
					if (string.IsNullOrWhiteSpace(file))
						return new McpToolResult("background_set needs file, the full path to an image or video. It is copied into the Voidstrap data folder.", true);
					double? gradient = args["gradientOpacity"] is JsonValue grad && grad.TryGetValue(out double gradValue) ? gradValue : null;
					double? black = args["blackOverlayOpacity"] is JsonValue blackValue && blackValue.TryGetValue(out double blackValue2) ? blackValue2 : null;
					bool? everywhere = args["displayEverywhere"] is JsonValue disp && disp.TryGetValue(out bool dispValue) ? dispValue : null;
					string stored = AppControl.SetBackground(file, gradient, black, everywhere);
					return new McpToolResult("Background applied to every open window and saved as " + stored + ". It stays after a restart. undo with action background_clear.");
				}
				case "background_clear":
				{
					AppControl.ClearBackground();
					return new McpToolResult("Background removed, Voidstrap is back to its default look.");
				}
				default:
					return new McpToolResult("Unknown action '" + action + "'. Use text_list, text_set, text_clear, text_clear_all, background_get, background_set or background_clear.", true);
			}
		}
		catch (Exception ex)
		{
			return new McpToolResult("The action failed: " + ex.Message, true);
		}
	}

	public static McpToolResult ManageAppCode(JsonObject args)
	{
		string action = McpArgs.String(args, "action")?.Trim().ToLowerInvariant() ?? "list";
		try
		{
			switch (action)
			{
				case "list":
				{
					if (!AppSourceStore.Available)
						return new McpToolResult("This Voidstrap build does not carry the source list. Run a full build of Voidstrap to embed it.", true);
					string? folder = McpArgs.String(args, "path");
					IReadOnlyList<(string Name, bool IsFolder)> children = AppSourceStore.ListFolder(folder);
					if (children.Count == 0)
						return new McpToolResult("That folder is empty or does not exist. The root holds folders like Voidstrap.App and Voidstrap.Core.", true);
					JsonObject result = new()
					{
						["folders"] = new JsonArray(children.Where(child => child.IsFolder).Select(child => JsonValue.Create(child.Name)).ToArray()),
						["files"] = new JsonArray(children.Where(child => !child.IsFolder).Select(child => JsonValue.Create(child.Name)).ToArray())
					};
					string hint = children.FirstOrDefault(child => !child.IsFolder).Name is { Length: > 0 } example
						? "\nOpen a file with action read and path '" + (string.IsNullOrWhiteSpace(folder) ? "" : folder.TrimEnd('/') + "/") + example + "'."
						: "";
					return new McpToolResult((string.IsNullOrWhiteSpace(folder) ? "Source root" : folder) + ":\n" + result.ToJsonString(IndentedJson) + hint);
				}
				case "read":
				{
					string? path = McpArgs.String(args, "path");
					if (string.IsNullOrWhiteSpace(path))
						return new McpToolResult("read needs path, for example Voidstrap.App/Bootstrapper.cs. Use action list to walk folders or action search to find code.", true);
					string? content = AppSourceStore.Read(path);
					if (content == null)
						return new McpToolResult("That file does not exist in the source list. Check the path with action list or action search.", true);
					return new McpToolResult((AppSourceStore.HasOverride(path) ? "[edited copy] " : "") + path + ":\n" + content);
				}
				case "write":
				{
					string? path = McpArgs.String(args, "path");
					string? content = McpArgs.String(args, "content");
					if (string.IsNullOrWhiteSpace(path) || content == null)
						return new McpToolResult("write needs path and content.", true);
					if (!AppSourceStore.Exists(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
						return new McpToolResult("That file does not exist in the built source, a brand new C# file also needs a matching change to the project file. Edit an existing file, or also write the .csproj with your file listed.", true);
					AppSourceStore.Write(path, content);
					PushUndo("app code " + path, () => AppSourceStore.Reset(path));
					return new McpToolResult("Saved an edited copy of " + path + ". The running app never changes, and the edit cannot crash or break it. Export all with the export action, build the exported folder, and replace Voidstrap to run the change. undo_last_change reverts the edit.");
				}
				case "reset":
				{
					string? path = McpArgs.String(args, "path");
					if (string.IsNullOrWhiteSpace(path))
						return new McpToolResult("reset needs path.", true);
					return AppSourceStore.Reset(path)
						? new McpToolResult("Removed the edited copy of " + path + ", the original from this build is back.")
						: new McpToolResult("No edited copy existed for that file.", true);
				}
				case "search":
				{
					string? query = McpArgs.String(args, "query");
					if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
						return new McpToolResult("search needs query with at least 2 characters.", true);
					int max = args["max"] is JsonValue maxNode && maxNode.TryGetValue(out int maxValue) && maxValue > 0 ? Math.Min(maxValue, 300) : 80;
					List<string> results = [.. AppSourceStore.Search(query, max)];
					return new McpToolResult(results.Count == 0
						? "No matches for " + query
						: results.Count + " matches:\n" + string.Join("\n", results));
				}
				case "overrides":
				{
					IReadOnlyList<string> overrides = AppSourceStore.ListOverrides();
					return new McpToolResult(overrides.Count == 0
						? "No files are edited yet. Write one with action write."
						: overrides.Count + " edited copy(ies):\n" + string.Join("\n", overrides));
				}
				case "export":
				{
					string? destination = McpArgs.String(args, "destination");
					if (string.IsNullOrWhiteSpace(destination))
						return new McpToolResult("export needs destination, the full path of an empty folder to write the source tree into.", true);
					int written = AppSourceStore.Export(destination);
					return new McpToolResult("Exported " + written + " files to " + destination + ", including every edited copy. Open that folder, build Voidstrap.sln inside it, and the edits run for real.");
				}
				default:
					return new McpToolResult("Unknown action '" + action + "'. Use list, read, write, reset, search, overrides or export.", true);
			}
		}
		catch (Exception ex)
		{
			return new McpToolResult("The action failed: " + ex.Message, true);
		}
	}
}
