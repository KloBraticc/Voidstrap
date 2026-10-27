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
		VOIDSTRAP MODS, THE SHORT VERSION
		A Voidstrap mod is a small TypeScript program that runs inside Voidstrap in a sandbox. It cannot touch files, processes or .NET. Everything goes through the $ object.

		WORKFLOW, FOLLOW IT IN ORDER
		1. manage_voidstrap_mods action list: see existing mods and their problems. Edit an existing mod instead of making a near duplicate.
		2. action types: read the exact API before writing code. Never guess a $ method name; if it is not in types it does not exist.
		3. write_file the three files below (mod is the folder name, lowercase letters, digits and hyphens).
		4. action validate: fix every reported problem before going on.
		5. action test if the mod has *.test.tsx files.
		6. action reload, then action problems: a hook that throws is skipped and its error shows here.
		7. A brand new mod stays off until the user says yes to a prompt Voidstrap shows them. Tell the user to accept it; you cannot turn it on yourself.

		THE THREE FILES
		.voidstrap-mod/plugin.json
		  { "name": "my-mod", "version": "0.1.0", "description": "One line about it", "network": [] }
		  name must equal the folder name. network lists https hosts $.net.fetch may call, nothing else is reachable.
		hooks/hooks.json
		  { "modules": ["./register.tsx"] }
		hooks/register.tsx, a complete working example:
		  import type { Register } from 'voidstrap';
		  const clicks = atom('clicks', 0);
		  export const register: Register = (on) => {
		    on('session.start', ($, e, next) => {
		      $.ui.open({ id: 'main', title: 'My mod' });
		      return next(e);
		    });
		    on('ui.render', { component: 'Pane', requestId: 'main' }, ($) => {
		      const { Box, Text, Button } = $.ui.resolve();
		      return (
		        <Box>
		          <Text bold>Clicked {read($, clicks)} times</Text>
		          <Button key="add" label="Click" onPress={() => update($, clicks, n => n + 1)} />
		        </Box>
		      );
		    });
		  };

		HOOKS
		Every hook is ($, e, next). Call next(e) to let Voidstrap carry on, return without next to stop it, or next({ ...e, x }) to change it.
		on(event, matcher, hook) runs only when the fields in matcher equal the fields of e.
		Events: session.start, session.end, roblox.launch (return { ...e, cancel: true } to stop a launch), game.join, game.leave, page.navigate, ui.render.

		DRAWING
		Panes, sidebar pages, banners and dialogs are all drawn by ui.render hooks with the matching component and requestId:
		  Pane: $.ui.open({ id, title })
		  Page: $.sidebar.addPage({ id, title, icon, section })
		  Banner: $.ui.openBanner({ id, position: 'top' })
		  Dialog: $.ui.openDialog({ id, title, width, height })
		Elements come from $.ui.resolve(). The full list and every prop are in types.
		Give every pressable element a key, and put handlers in onPress, onChange or onSubmit.
		State: const n = atom('name', initial); read($, n) while drawing; update($, n, fn) in a handler. Updates redraw automatically.

		COMMON MISTAKES
		"Box is not defined": you used JSX without const { Box, Text, ... } = $.ui.resolve(); inside the hook.
		Nothing shows: the ui.render matcher does not match the component and id you opened, or you forgot to call next(e) in session.start.
		"the setting X does not exist": read $.settings.list() for real names.
		Network refused: the host is missing from network in plugin.json, or the url is not https.
		Tests: do not import register in a test file. The mod is already loaded; drive it with mod.emit, mod.render, mod.renderPage and mod.press from 'voidstrap/testing'.
		Hooks time out after 2 seconds of running and async work is waited on for at most 10 seconds, so keep hooks short.
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
