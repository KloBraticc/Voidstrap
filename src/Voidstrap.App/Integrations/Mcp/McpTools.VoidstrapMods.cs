using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Voidstrap.Integrations.Plugins;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
	private const int MaxModFileBytes = 512 * 1024;

	private static readonly string[] ModFileExtensions = [".ts", ".tsx", ".js", ".mjs", ".jsx", ".json", ".md"];

	[GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
	private static partial Regex ModNamePattern { get; }

	private const string ModLayoutGuide = """
		A Voidstrap mod is a folder in the mods folder named after the mod (lowercase letters, digits and hyphens).
		.voidstrap-mod/plugin.json: { "name": "<folder name>", "version": "0.1.0", "description": "<one line>", "network": ["api.example.com"], "userConfig": { "field": { "default": 1 } } }
		hooks/hooks.json: { "modules": ["./register.tsx"] }
		hooks/register.tsx: export const register: Register = (on, options) => { ... }, with import type { Register } from 'voidstrap'.
		Every hook is ($, e, next): return without next to answer alone, next({ ...e, x }) to rewrite, await next(e) to react. on(event, matcher, hook) filters by matching fields of e.
		UI Surfaces:
		- Panes: $.ui.open({ id, title }) in session.start, rendered by on('ui.render', { component: 'Pane', requestId: id }, ($) => tree).
		- Custom Sidebar Pages: $.sidebar.addPage({ id, title, icon, section }) in session.start, rendered by on('ui.render', { component: 'Page', requestId: id }, ($) => tree).
		- Window Banners: $.ui.openBanner({ id, position: 'top' | 'bottom' }), rendered by on('ui.render', { component: 'Banner', requestId: id }, ($) => tree).
		- Modal Dialogs: $.ui.openDialog({ id, title, width, height }), rendered by on('ui.render', { component: 'Dialog', requestId: id }, ($) => tree).
		UI Elements: Box, Stack, Wrap, Grid, Scroll, Text, Badge, Icon, ColorSwatch, Button, Toggle, CheckBox, TextInput, NumberInput, Slider, Select, ProgressBar, InfoBar, Divider, Spacer, Image, Card, Expander, Tabs, Table.
		Full App Control via $:
		- $.sidebar: get(), set({ order, hidden, names, icons }), reset(), addPage({ id, title, icon, section }), removePage(id).
		- $.nav: current(), pages(), open(page).
		- $.theme: get(), set({ theme, backdrop, uiZoomPercent, accentColor, ... }), getBrush(key), setBrush(key, hexColor), resetBrushes(), listBrushes().
		- $.window: get(), set({ title, width, height, left, top, state, topMost, opacity }).
		- $.settings: list(), get(name), set(name, value).
		- $.fastFlags: getAll(), get(name), set(name, value), remove(name), clear().
		- $.roblox: status(), launch({ mode, placeId, jobId }).
		- $.clipboard: get(), set(text).
		State: const n = atom('count', 0); read($, n); update($, n, v => v + 1). Writes redraw active panes, pages, banners and dialogs.
		Tests: name files like pane.test.tsx, import { test, expect, mod } from 'voidstrap/testing', and use mod.emit, mod.render, mod.renderPage and mod.press.
		After writing, call validate, then test, then reload.
		The exact types are in the action types output.
		""";

	public static McpToolResult ManageVoidstrapMods(JsonObject args)
	{
		if (!PluginHost.IsSupported)
			return new McpToolResult("Voidstrap mods are not available on macOS.", true);
		string action = McpArgs.Trimmed(args, "action", "list").ToLowerInvariant();
		string mod = McpArgs.Trimmed(args, "mod");
		switch (action)
		{
			case "list":
			{
				JsonArray mods = new();
				foreach (PluginHost.ModInfo info in PluginHost.ListMods())
				{
					mods.Add(new JsonObject
					{
						["name"] = info.Name,
						["version"] = info.Version,
						["description"] = info.Description,
						["state"] = info.Consent,
						["loaded"] = info.Loaded,
						["hooks"] = new JsonArray(info.Hooks.Select(hook => (JsonNode?)hook).ToArray()),
						["problems"] = new JsonArray(info.Problems.TakeLast(10).Select(problem => (JsonNode?)problem).ToArray())
					});
				}
				return new McpToolResult(new JsonObject { ["folder"] = Paths.Plugins, ["mods"] = mods }.ToJsonString(IndentedJson));
			}
			case "guide":
				return new McpToolResult(ModLayoutGuide);
			case "types":
				return new McpToolResult(Resource.GetString("ModTypes.d.ts"));
			case "reload":
				PluginHost.Reload();
				return new McpToolResult("Reloaded the Voidstrap mods. Call list to see what loaded and any problems.");
		}
		if (!ModNamePattern.IsMatch(mod))
			return new McpToolResult("Provide 'mod', the mod's folder name in lowercase letters, digits and hyphens.", true);
		string folder = Path.Combine(Paths.Plugins, mod);
		switch (action)
		{
			case "files":
			{
				if (!Directory.Exists(folder))
					return new McpToolResult("That mod does not exist yet. Write its files with write_file.", true);
				string[] files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
					.Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/'))
					.Where(path => !path.StartsWith(PluginManifest.ManifestFolder + "/types/", StringComparison.OrdinalIgnoreCase))
					.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
					.ToArray();
				return new McpToolResult(string.Join("\n", files));
			}
			case "read_file":
			{
				string? path = ModFilePath(folder, McpArgs.Trimmed(args, "path"));
				if (path == null || !File.Exists(path))
					return new McpToolResult("That file does not exist in the mod.", true);
				return new McpToolResult(File.ReadAllText(path));
			}
			case "write_file":
			{
				string relative = McpArgs.Trimmed(args, "path");
				string? path = ModFilePath(folder, relative);
				string content = McpArgs.String(args, "content") ?? "";
				if (path == null || !ModFileExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
					return new McpToolResult("The path must stay inside the mod and end in " + string.Join(", ", ModFileExtensions) + ".", true);
				if (content.Length > MaxModFileBytes)
					return new McpToolResult("Mod files are limited to 512 KB.", true);
				string? before = File.Exists(path) ? File.ReadAllText(path) : null;
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, content);
				PushUndo("write " + mod + "/" + relative, () =>
				{
					if (before == null)
						File.Delete(path);
					else
						File.WriteAllText(path, before);
				});
				return new McpToolResult("Wrote " + mod + "/" + relative.Replace('\\', '/') + ". Call validate, then test, then reload.");
			}
			case "delete_file":
			{
				string? path = ModFilePath(folder, McpArgs.Trimmed(args, "path"));
				if (path == null || !File.Exists(path))
					return new McpToolResult("That file does not exist in the mod.", true);
				string before = File.ReadAllText(path);
				File.Delete(path);
				PushUndo("delete " + mod + " file", () =>
				{
					Directory.CreateDirectory(Path.GetDirectoryName(path)!);
					File.WriteAllText(path, before);
				});
				return new McpToolResult("Deleted the file.");
			}
			case "delete_mod":
			{
				if (!McpArgs.Flag(args, "confirm", false))
					return new McpToolResult("Deleting a whole mod needs confirm set to true.", true);
				if (!Directory.Exists(folder))
					return new McpToolResult("That mod does not exist.", true);
				Directory.Delete(folder, true);
				PluginHost.ForgetConsent(mod);
				PluginHost.Reload();
				return new McpToolResult("Deleted the mod " + mod + ".");
			}
			case "validate":
			{
				StringWriter output = new();
				int code = ModTools.Validate(folder, output);
				return new McpToolResult(output.ToString(), code != 0);
			}
			case "test":
			{
				StringWriter output = new();
				int code = ModTools.Test(folder, output);
				return new McpToolResult(output.ToString(), code != 0);
			}
			case "disable":
				PluginHost.SetEnabled(mod, false);
				return new McpToolResult("Turned off the mod " + mod + ".");
			case "enable":
				PluginHost.ForgetConsent(mod);
				PluginHost.Reload();
				return new McpToolResult("Asked the user whether to turn on " + mod + ". It loads only if they say yes; call list to see the answer.");
			case "problems":
			{
				PluginHost.ModInfo? info = PluginHost.ListMods().FirstOrDefault(item => item.Name == mod);
				return info == null
					? new McpToolResult("That mod was not found.", true)
					: new McpToolResult(info.Problems.Count == 0 ? "No problems recorded for " + mod + "." : string.Join("\n", info.Problems));
			}
			default:
				return new McpToolResult("Unknown action. Use list, guide, types, files, read_file, write_file, delete_file, delete_mod, validate, test, reload, enable, disable, or problems.", true);
		}
	}

	private static string? ModFilePath(string folder, string relative)
	{
		if (relative.Length == 0)
			return null;
		string root = Path.GetFullPath(folder);
		string full = Path.GetFullPath(Path.Combine(root, relative));
		return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
	}
}
