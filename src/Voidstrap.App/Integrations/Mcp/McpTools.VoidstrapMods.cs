using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using Voidstrap.Integrations.Plugins;
using Voidstrap.Resources;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
	private const int MaxModFileBytes = 512 * 1024;

	private static readonly string[] ModFileExtensions = [".ts", ".tsx", ".js", ".mjs", ".jsx", ".json", ".md", ".png", ".ico", ".gif", ".webp", ".jpg", ".jpeg", ".svg"];

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
		Events: session.start, session.end, roblox.launch (return { ...e, cancel: true } to stop a launch), game.join, game.leave, page.navigate, ui.render, cursor.move, cursor.down, cursor.up, cursor.enter, cursor.leave.

			DRAWING
			Panes, sidebar pages, mod pages, banners and dialogs are all drawn by ui.render hooks with the matching component and requestId:
			  Pane: $.ui.open({ id, title })
			  Page: $.sidebar.addPage({ id, title, icon, section })
			  Mod page: $.modpages.add({ id, title, icon })
			  Banner: $.ui.openBanner({ id, position: 'top' })
			  Dialog: $.ui.openDialog({ id, title, width, height })
			Elements come from $.ui.resolve(). The full list and every prop are in types.
			Give every pressable element a key, and put handlers in onPress, onChange or onSubmit.
			State: const n = atom('name', initial); read($, n) while drawing; update($, n, fn) in a handler. Updates redraw automatically.

			MOD PAGES IN THE TOP BAR
			A mod page is a full page that opens behind its own button in the top left of Voidstrap, next to the Pages button. The button appears only while at least one loaded mod has added a page, so an unused mod never clutters the interface.
			In session.start: $.modpages.add({ id: 'shop', title: 'My Shop', icon: 'Cart24' }); then draw it like any page:
			  on('ui.render', { component: 'Page', requestId: 'shop' }, ($) => { ... });
			$.modpages.remove('shop') takes the page away and $.modpages.list() returns the pages the mod added. Open a mod page from code with $.nav.open('mod:<modName>:<id>').
			The page title shows in the breadcrumb at the top of the window (like Home > My Shop) so the user always sees where they are and can go back to the page they came from.
			Pick icon names from the Fluent icon list, for example PuzzlePiece24, Cart24, Gauge24 or Settings24; an unknown name falls back to PuzzlePiece24.
			Prefer $.modpages for a whole page of the mod's own, and $.sidebar.addPage when the page belongs in the main sidebar sections.

		COMMON MISTAKES
		"Box is not defined": you used JSX without const { Box, Text, ... } = $.ui.resolve(); inside the hook.
		Nothing shows: the ui.render matcher does not match the component and id you opened, or you forgot to call next(e) in session.start.
		"the setting X does not exist": read $.settings.list() for real names.
		Network refused: the host is missing from network in plugin.json, or the url is not https.
		Tests: do not import register in a test file. The mod is already loaded; drive it with mod.emit, mod.render, mod.renderPage and mod.press from 'voidstrap/testing'.
		Hooks time out after 2 seconds of running and async work is waited on for at most 10 seconds, so keep hooks short.

		PLACING UI ANYWHERE IN VOIDSTRAP
		Call manage_voidstrap_mods action ui_map first. It returns every named element in the live Voidstrap window: name, type, visible text, parent, x, y, width, height (pixels from the window's top left) and attachable.
		Pick an element near where the user wants the new UI, then attach a widget next to it in session.start:
		  $.ui.attach({ id: 'friends', anchor: 'TopNavBack', placement: 'before' });   placement is before, after, start or end
		Draw it with on('ui.render', { component: 'Widget', requestId: 'friends' }, ...). It redraws whenever the mod's state changes.
		Only elements with attachable true can take a widget; if yours is false, use a neighbour or its parent from ui_map. $.ui.anchors() returns the same list at runtime.
		Well known spots: TopNavBack and TopNavForward are the Back and Next arrows at the top right of the title bar, TopNavPanel is the top left menu (Home, Library, Servers, Voidstrap mods, News).

		USING THE ROBLOX ACCOUNT SAFELY
		A mod never reads the Roblox login cookie. Add "permissions": ["roblox-account"] to plugin.json, then use:
		  const r = await $.roblox.request('https://friends.roblox.com/v1/my/friends/count');   Voidstrap adds the login itself
		  const me = await $.roblox.account();   { signedIn, userId, username, displayName }
		Only https GET to Roblox API hosts (users, friends, presence, thumbnails, games, groups, badges, catalog, avatar, inventory, economy, followings, develop) and a few read only POST lookups are allowed, at most 60 a minute. The user approves Roblox account access separately. Never put cookies, ROBLOSECURITY or tokens in mod code: the library refuses it.

		FULL EXAMPLE: Roblox friend count next to the Back and Next arrows
		plugin.json: { "name": "friend-count", "version": "1.0.0", "description": "Shows how many Roblox friends you have next to the Back and Next arrows.", "network": [], "permissions": ["roblox-account"], "userConfig": {} }
		hooks/register.tsx:
		  import type { Register } from 'voidstrap';
		  const count = atom('count', -1);
		  export const register: Register = (on) => {
		    on('session.start', ($, e, next) => {
		      const refresh = async () => {
		        const r = await $.roblox.request('https://friends.roblox.com/v1/my/friends/count');
		        if (r.ok) $.state.set('count', JSON.parse(r.text).count);
		      };
		      $.ui.attach({ id: 'friends', anchor: 'TopNavBack', placement: 'before' });
		      refresh();
		      $.clock.every(60000, refresh);
		      return next(e);
		    });
		    on('ui.render', { component: 'Widget', requestId: 'friends' }, ($) => {
		      const { Text } = $.ui.resolve();
		      const n = read($, count);
		      return <Text text={n < 0 ? '...' : n + ' friends'} bold={true} />;
		    });
		  };

		SHARING IN THE LIBRARY
		The Voidstrap mods library (https://github.com/KloBraticc/voidstrap-mods-library) holds community mods. library_search lists them, library_install asks the user to install one.
		To share a mod: validate and test it, raise its version for an update, then call publish with mod and optional notes. Voidstrap opens a window where the user signs in to GitHub and presses Publish for review; it then opens a pull request from the user's own account.
		Library rules: readable code only (no eval, Function, encoded or minified code), nothing that touches Roblox logins or cheats, every contacted host listed in plugin.json network. Only the account that first published a mod can update it.
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
			case "ui_map":
			{
				JsonArray map = Application.Current.Dispatcher.Invoke(PluginAnchors.Map);
				return new McpToolResult(new JsonObject { ["note"] = "Named elements in the live Voidstrap window. Attach a widget next to one whose attachable is true with $.ui.attach({ id, anchor: name, placement }).", ["elements"] = map }.ToJsonString(IndentedJson));
			}
			case "library_search":
			{
				ModLibrary.Index index = ModLibrary.GetIndexAsync(false, CancellationToken.None).GetAwaiter().GetResult();
				string query = McpArgs.Trimmed(args, "query");
				JsonArray results = new();
				foreach (ModLibrary.LibraryMod entry in ModLibrarySearch.Run(index.Mods, query, null, null).Take(50))
				{
					results.Add(new JsonObject
					{
						["name"] = entry.Name,
						["version"] = entry.Version,
						["owner"] = entry.Login,
						["description"] = entry.Description,
						["verified"] = entry.Verified,
						["trustedAuthor"] = entry.Trusted,
						["tags"] = new JsonArray(entry.Tags.Select(t => (JsonNode?)t).ToArray()),
						["network"] = new JsonArray(entry.Network.Select(h => (JsonNode?)h).ToArray()),
						["state"] = ModLibrary.GetState(entry).ToString()
					});
				}
				return new McpToolResult(new JsonObject { ["library"] = ModLibrary.RepositoryUrl, ["mods"] = results }.ToJsonString(IndentedJson));
			}
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
			case "auto_update":
			{
				IReadOnlyList<string> updated = ModLibrary.AutoUpdateInstalledModsAsync(CancellationToken.None).GetAwaiter().GetResult();
				return updated.Count > 0
					? new McpToolResult("Auto-updated " + updated.Count + " mod(s): " + string.Join(", ", updated))
					: new McpToolResult("All installed library mods are up to date.");
			}
			case "library_install":
			{
				ModLibrary.Index index = ModLibrary.GetIndexAsync(false, CancellationToken.None).GetAwaiter().GetResult();
				ModLibrary.LibraryMod? entry = index.Mods.FirstOrDefault(m => m.Name == mod);
				if (entry == null)
					return new McpToolResult("That mod is not in the library. Use library_search.", true);
				ModLibrary.InstallState state = ModLibrary.GetState(entry);
				if (state == ModLibrary.InstallState.Installed)
					return new McpToolResult(mod + " " + entry.Version + " is already installed.");
				if (state == ModLibrary.InstallState.NameTaken)
					return new McpToolResult("A different mod named " + mod + " is already installed, so the library one cannot be installed.", true);
				string network = entry.Network.Count == 0 ? Strings.Ui_VoidstrapMods_LibraryNoNetwork : string.Format(Strings.Ui_VoidstrapMods_LibraryNetwork, string.Join(", ", entry.Network));
				string prompt = entry.Verified || entry.Trusted ? Strings.Ui_VoidstrapMods_LibraryInstallPrompt : Strings.Ui_VoidstrapMods_LibraryUnverifiedPrompt;
				MessageBoxResult answer = Application.Current.Dispatcher.Invoke(() => Frontend.ShowMessageBox(string.Format(prompt, entry.Name, entry.Version, entry.Login, network), entry.Verified || entry.Trusted ? MessageBoxImage.Question : MessageBoxImage.Warning, MessageBoxButton.YesNo, MessageBoxResult.No));
				if (answer != MessageBoxResult.Yes)
					return new McpToolResult("The user declined the install.", true);
				ModLibrary.InstallAsync(index, entry, CancellationToken.None).GetAwaiter().GetResult();
				PluginHost.Reload();
				return new McpToolResult("Installed " + mod + " " + entry.Version + " from the library. Voidstrap asks the user before turning it on; call list to see whether it loaded.");
			}
			case "publish":
			{
				if (!Directory.Exists(folder))
					return new McpToolResult("That mod does not exist.", true);
				StringWriter output = new();
				if (ModTools.Validate(folder, output) != 0)
					return new McpToolResult("Fix these before publishing:" + Environment.NewLine + output, true);
				string notes = McpArgs.Trimmed(args, "notes");
				Application.Current.Dispatcher.BeginInvoke(() => Voidstrap.UI.Elements.Settings.Pages.VoidstrapModsPage.ShowPublishDialog(mod, notes));
				return new McpToolResult("Voidstrap is now showing the publish window for " + mod + ". The user signs in to GitHub there if needed, reviews the checks, and presses Publish for review. Nothing is published until they do. After that the maintainer reviews the pull request on " + ModLibrary.RepositoryUrl + ".");
			}
			case "problems":
			{
				PluginHost.ModInfo? info = PluginHost.ListMods().FirstOrDefault(item => item.Name == mod);
				return info == null
					? new McpToolResult("That mod was not found.", true)
					: new McpToolResult(info.Problems.Count == 0 ? "No problems recorded for " + mod + "." : string.Join("\n", info.Problems));
			}
			default:
				return new McpToolResult("Unknown action. Use list, guide, types, files, read_file, write_file, delete_file, delete_mod, validate, test, reload, enable, disable, problems, library_search, library_install, auto_update, or publish.", true);
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
