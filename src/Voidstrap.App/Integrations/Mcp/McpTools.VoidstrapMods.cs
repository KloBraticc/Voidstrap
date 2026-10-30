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

    private static readonly string[] ModFileExtensions =
    [
        ".ts",
        ".tsx",
        ".js",
        ".mjs",
        ".jsx",
        ".json",
        ".md",
        ".png",
        ".ico",
        ".gif",
        ".webp",
        ".jpg",
        ".jpeg",
        ".bmp",
        ".svg",
    ];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    private static partial Regex ModNamePattern { get; }

    internal const string ModLayoutGuide = """
		VOIDSTRAP MODS
		Use manage_voidstrap_mods for features inside Voidstrap: widgets, cursor sprites, pages, panels, sidebar changes and automation. This is different from manage_mods, which manages Roblox content files applied on the next launch.
		Mods run on Windows, Linux and macOS inside a sandbox. They cannot access files, processes or .NET directly. Use the $ API from action types. This is not a browser: there is no document, window, DOM, CSS, React, requestAnimationFrame, setInterval or Node.js API. JSX describes native Voidstrap controls.

		WORKFLOW, FOLLOW IT IN ORDER
		1. manage_voidstrap_mods action list: see existing mods and their problems. Edit an existing mod instead of making a near duplicate.
		2. action types: read the exact API before writing code. Never guess a $ method name; if it is not in types it does not exist.
		3. For an existing mod, action files and read_file before changing it. For UI placement, action ui_map and choose a real attachable anchor.
		4. action write_file writes full file contents. Supply mod, path and content, for example { "action": "write_file", "mod": "my-mod", "path": "hooks/hooks.json", "content": "{\"modules\":[\"./register.tsx\"]}" }. A new mod needs all three files below.
		5. action validate checks that the manifest and register module load. It does not execute every event or prove the UI renders. Fix every reported error.
		6. action test runs *.test.ts, *.test.tsx, *.test.js, *.test.jsx or *.test.mjs files in a sandbox. Test the events and render tree your feature uses.
		7. action reload applies changed mods; unchanged mods keep their runtime, timers and UI. If an edit fails to load, the last working runtime stays active and problems explains the rejected edit. Call list to confirm loaded true and state on. A new mod needs the user's approval in Voidstrap. Do not repeatedly call enable: it resets consent and asks again.
		8. Exercise the feature in the live window, then action problems with mod. No problems does not prove the mod loaded or that the UI is visible. Report separately what was saved, tested, loaded and seen.

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
		Every hook is ($, e, next). Call return next(e) once to pass the event to the next hook. Return without next to stop the chain, or return next({ ...e, x }) to pass changed data.
		A ui.render hook returns its JSX tree instead of next(e). Register hooks in register; put startup work in session.start. Do not perform network work or write state while drawing.
		on(event, matcher, hook) runs only when the fields in matcher equal the fields of e.
		Events: session.start, session.end, roblox.launch (return { ...e, cancel: true } to stop a launch), game.join, game.leave, page.navigate, settings.changed, ui.render, cursor.move, cursor.down, cursor.up, cursor.enter, cursor.leave, cursor.scroll. An unknown event name is reported by validate and in problems.

		USER SETTINGS
		Declare settings in plugin.json: "userConfig": { "speed": { "title": "Speed", "type": "number", "default": 5, "description": "Optional hint" }, "mode": { "title": "Mode", "default": "a", "options": ["a", "b"] } }. type is boolean, number or string and is inferred from default. The mod card then shows a settings button; saving restarts the mod. Read values from the second argument of register or from $.mod.options(). $.mod also has name and version. Async errors from timers, handlers and hooks are recorded in problems, so do not swallow them. A ui.render hook cannot be async.

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
			$.state is session state. $.store persists across app restarts. Keep related fast changing values in one state object so one event performs one update. A plain variable change does not request a redraw.

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
		hooks.json names one entry module. Import helper modules from that entry, rather than listing every helper in hooks.json. Ensure each imported file exists before reloading, use unique aliases for repeated export names, and declare local helpers before calling them. Tests support only the matchers listed in types, including .not. $.store works in memory during tests.
		Hooks time out after 2 seconds of running and async work is waited on for at most 10 seconds, so keep hooks short.
		A sprite moves twice a second: $.clock.every has a 500 ms minimum, even when you ask for 16 or 33 ms. Use cursor.move as described below.
		A sprite stays invisible: always return a root Canvas, even when its children are hidden. Verify the Widget requestId, WindowRoot anchor and loaded state.
		A sprite blocks clicks: set hitTestVisible={false} on the root Canvas. A decoration must let pointer input reach the app beneath it.
		Images and GIFs: use Image with src set to a mod relative path, a base64 image data URL or an HTTPS URL on an allowed network host. Redirects and paths outside the mod are refused. GIFs play automatically; animate={false} shows the first frame.
		Sprite sheets: use Sprite with src, frameWidth and frameHeight in source pixels. Cells run left to right, then top to bottom. Set frameIndex for one zero based cell, or omit it to loop; fps controls playback. Image accepts the same props. Keep movement on cursor events rather than repeat timers.
		All custom image bytes are checked locally before display, including every GIF frame and sprite cell. The fixed content checker and its license files download on first use; images are never uploaded. Checking is asynchronous, so initial renders can be blank. Failed checks, unavailable checking and unsupported or oversized media stay hidden. Limits are 16 MB encoded, 48 MB decoded and 180 frames or cells. This targets sexual imagery and can make mistakes; it is not a general harmful content classifier.

		PLACING UI ANYWHERE IN VOIDSTRAP
		To restyle, hide, move or fully redesign existing UI instead of adding to it, read get_guide topic mod_ui and use action ui_tree.
		Call manage_voidstrap_mods action ui_map first. It returns every named element in the live Voidstrap window: name, type, visible text, parent, x, y, width, height (pixels from the window's top left) and attachable.
		Pick an element near where the user wants the new UI, then attach a widget next to it in session.start:
		  $.ui.attach({ id: 'friends', anchor: 'TopNavBack', placement: 'before' });   placement is before, after, start or end
		Draw it with on('ui.render', { component: 'Widget', requestId: 'friends' }, ...). It redraws whenever the mod's state changes.
		Only elements with attachable true can take a widget; if yours is false, use a neighbour or its parent from ui_map. $.ui.anchors() returns the same list at runtime.
		Well known spots: TopNavBack and TopNavForward are the Back and Next arrows at the top right of the title bar, TopNavPanel is the top left menu (Home, Library, Servers, Voidstrap mods, News).
		Attachment and rendering are asynchronous. An isPlaced true result means the request was accepted, not that pixels are already on screen. Read reason and inspect the live result.

		CURSOR SPRITES AND TIMING
		$.clock.every(ms, fn) clamps its interval to at least 500 ms. It is for occasional refreshes, not animation. $.clock.after is a one shot callback, not an animation frame API; do not chain it into a busy animation loop. There are at most 32 timers per mod, and unloading disposes them.
		Use cursor.move for pointer tracking. The host dispatches moves at most once per 16 ms and skips dispatch while a move hook is busy. This is a rate limit, not a guaranteed 60 fps clock. Events stop when the pointer stops moving. Keep handlers short, with no network calls, logging or expensive calculations in this path.
		Use event x and y for placement in the window. These are logical window coordinates, not screen pixels. screenX and screenY are screen coordinates and must not be used for Canvas placement. $.cursor.get() returns the last captured position; polling it faster cannot create new pointer events.
		Cursor events come from Voidstrap windows. A mod cannot draw a desktop overlay or follow the pointer outside those windows. Handle cursor.leave to hide the sprite. Handle cursor.down and cursor.up for click feedback even while the pointer is stationary.
		For exact tracking, copy event coordinates into state. Easing only on cursor.move leaves a gap when the pointer stops, because no more events arrive to finish that easing.
		For a full window overlay, attach to WindowRoot with placement end and return a root Canvas with hitTestVisible false. Put left and top on its direct children. Keep that root Canvas when hidden so the widget retains its overlay layout.

		COMPLETE POINTER EXAMPLE
		Use the three file layout above, with name my-mod. This register.tsx draws a small dot and changes its color while pressed:
		  import type { Register } from 'voidstrap';
		  const pointer = atom('pointer', { x: 0, y: 0, visible: false, pressed: false });
		  export const register: Register = (on) => {
		    on('session.start', ($, e, next) => {
		      $.ui.attach({ id: 'pointer', anchor: 'WindowRoot', placement: 'end' });
		      return next(e);
		    });
		    for (const event of ['cursor.enter', 'cursor.move', 'cursor.down', 'cursor.up'] as const) {
		      on(event, ($, e, next) => {
		        $.state.set(pointer.ref, { x: e.x, y: e.y, visible: e.insideWindow, pressed: e.isDown });
		        return next(e);
		      });
		    }
		    on('cursor.leave', ($, e, next) => {
		      update($, pointer, p => ({ ...p, visible: false, pressed: false }));
		      return next(e);
		    });
		    on('ui.render', { component: 'Widget', requestId: 'pointer' }, ($) => {
		      const { Canvas, Box } = $.ui.resolve();
		      const p = read($, pointer);
		      return <Canvas hitTestVisible={false}>
		        <Box key="dot" visible={p.visible} left={p.x - 8} top={p.y - 8} width={16} height={16}
		          cornerRadius={8} background={p.pressed ? '#FF8B5CF6' : '#FF38BDF8'} />
		      </Canvas>;
		    });
		  };

		TESTING THE POINTER EXAMPLE
		Save this as hooks/pointer.test.tsx. Do not import register: the test runner loads it once for the test file. Tests in a file share state, so each test must establish its starting state. Timers do not tick in this sandbox, and a render tree test does not verify native window layout.
		  import { test, expect, mod } from 'voidstrap/testing';
		  test('pointer follows and hides without losing its canvas', () => {
		    mod.emit('cursor.move', { x: 100, y: 80, screenX: 1000, screenY: 800,
		      windowWidth: 1000, windowHeight: 700, insideWindow: true, isDown: false, button: 'none' });
		    const tree = mod.renderWidget('pointer');
		    expect(tree?.type).toBe('Canvas');
		    expect(tree?.props.hitTestVisible).toBe(false);
		    const dot = tree?.children[0];
		    if (!dot || typeof dot !== 'object') throw new Error('missing dot');
		    expect(dot.props.left).toBe(92);
		    expect(dot.props.top).toBe(72);
		    mod.emit('cursor.leave', { x: 100, y: 80, insideWindow: false, isDown: false });
		    const hidden = mod.renderWidget('pointer');
		    expect(hidden?.type).toBe('Canvas');
		    const hiddenDot = hidden?.children[0];
		    if (!hiddenDot || typeof hiddenDot !== 'object') throw new Error('missing hidden dot');
		    expect(hiddenDot.props.visible).toBe(false);
		  });

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
		To share a mod: validate and test it, raise its version for an update, run preflight and fix every X item, then call publish with mod and optional notes. Publish refuses while preflight fails, so never retry a blocked publish, fix the listed items. Voidstrap opens a window where the user signs in to GitHub and presses Publish for review; it then opens a pull request from the user's own account.
		Library rules: readable code only (no eval, Function, encoded or minified code), nothing that touches Roblox logins or cheats, every contacted host listed in plugin.json network. Only the account that first published a mod can update it. Read get_guide topic library_pr for the full red X checklist.
		""";

    public static async Task<McpToolResult> ManageVoidstrapMods(JsonObject args)
    {
        string action = McpArgs.Trimmed(args, "action", "list").ToLowerInvariant();
        string mod = McpArgs.Trimmed(args, "mod");
        switch (action)
        {
            case "list":
                {
                    JsonArray mods = new();
                    foreach (PluginHost.ModInfo info in PluginHost.ListMods())
                    {
                        mods.Add(
                            new JsonObject
                            {
                                ["name"] = info.Name,
                                ["version"] = info.Version,
                                ["description"] = info.Description,
                                ["state"] = info.Consent,
                                ["loaded"] = info.Loaded,
                                ["hooks"] = new JsonArray(info.Hooks.Select(hook => (JsonNode?)hook).ToArray()),
                                ["problems"] = new JsonArray(info.Problems.TakeLast(10).Select(problem => (JsonNode?)problem).ToArray()),
                            }
                        );
                    }
                    return new McpToolResult(new JsonObject { ["folder"] = Paths.Plugins, ["mods"] = mods }.ToJsonString(IndentedJson));
                }
            case "guide":
                return new McpToolResult(ModLayoutGuide);
            case "types":
                return new McpToolResult(Resource.GetString("ModTypes.d.ts"));
            case "reload":
                PluginHost.ReloadNow();
                return new McpToolResult(
                    "Requested a reload of changed Voidstrap mod files. Call list to see what loaded and any problems."
                );
            case "ui_tree":
                return new McpToolResult(PluginRestyle.Tree(new JsonObject { ["depth"] = 12, ["namedOnly"] = true }).ToJsonString());
            case "ui_map":
                {
                    JsonArray map = Application.Current.Dispatcher.Invoke(PluginAnchors.Map);
                    return new McpToolResult(
                        new JsonObject
                        {
                            ["note"] =
                                "Named elements in the live Voidstrap window. Attach a widget next to one whose attachable is true with $.ui.attach({ id, anchor: name, placement }).",
                            ["elements"] = map,
                        }.ToJsonString(IndentedJson)
                    );
                }
            case "library_search":
                {
                    ModLibrary.Index index = ModLibrary.GetIndexAsync(false, CancellationToken.None).GetAwaiter().GetResult();
                    string query = McpArgs.Trimmed(args, "query");
                    JsonArray results = new();
                    foreach (ModLibrary.LibraryMod entry in ModLibrarySearch.Run(index.Mods, query, null, null).Take(50))
                    {
                        results.Add(
                            new JsonObject
                            {
                                ["name"] = entry.Name,
                                ["version"] = entry.Version,
                                ["owner"] = entry.Login,
                                ["description"] = entry.Description,
                                ["verified"] = entry.Verified,
                                ["trustedAuthor"] = entry.Trusted,
                                ["tags"] = new JsonArray(entry.Tags.Select(t => (JsonNode?)t).ToArray()),
                                ["network"] = new JsonArray(entry.Network.Select(h => (JsonNode?)h).ToArray()),
                                ["state"] = ModLibrary.GetState(entry).ToString(),
                            }
                        );
                    }
                    return new McpToolResult(
                        new JsonObject { ["library"] = ModLibrary.RepositoryUrl, ["mods"] = results }.ToJsonString(IndentedJson)
                    );
                }
        }
        if (!ModNamePattern.IsMatch(mod))
            return new McpToolResult("Provide 'mod', the mod's folder name in lowercase letters, digits and hyphens.", true);
        string folder = Path.Combine(Paths.Plugins, mod);
        switch (action)
        {
            case "preflight":
                {
                    ModLibraryReview.PreflightResult result = await ModLibraryReview
                        .RunAsync(mod, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new McpToolResult(result.Report, result.Errors > 0);
                }
            case "files":
                {
                    if (!Directory.Exists(folder))
                        return new McpToolResult("That mod does not exist yet. Write its files with write_file.", true);
                    string[] files = Directory
                        .EnumerateFiles(folder, "*", SearchOption.AllDirectories)
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
                        return new McpToolResult(
                            "The path must stay inside the mod and end in " + string.Join(", ", ModFileExtensions) + ".",
                            true
                        );
                    if (content.Length > MaxModFileBytes)
                        return new McpToolResult("Mod files are limited to 512 KB.", true);
                    string? before = File.Exists(path) ? File.ReadAllText(path) : null;
                    if (before == content)
                        return new McpToolResult("The mod file already has these contents. No files were changed.");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, content);
                    PushUndo(
                        "write " + mod + "/" + relative,
                        () =>
                        {
                            if (before == null)
                                File.Delete(path);
                            else
                                File.WriteAllText(path, before);
                        }
                    );
                    return new McpToolResult("Wrote " + mod + "/" + relative.Replace('\\', '/') + ". Call validate, then test, then reload.");
                }
            case "delete_file":
                {
                    string? path = ModFilePath(folder, McpArgs.Trimmed(args, "path"));
                    if (path == null || !File.Exists(path))
                        return new McpToolResult("That file does not exist in the mod.", true);
                    string before = File.ReadAllText(path);
                    File.Delete(path);
                    PushUndo(
                        "delete " + mod + " file",
                        () =>
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                            File.WriteAllText(path, before);
                        }
                    );
                    return new McpToolResult("Deleted the file.");
                }
            case "delete_mod":
                {
                    if (!McpArgs.Flag(args, "confirm", false))
                        return new McpToolResult("Deleting a whole mod needs confirm set to true.", true);
                    if (!Directory.Exists(folder))
                        return new McpToolResult("That mod does not exist.", true);
                    PluginHost.DeleteMod(mod);
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
                return new McpToolResult(
                    "Asked the user whether to turn on " + mod + ". It loads only if they say yes; call list to see the answer."
                );
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
                        return new McpToolResult(
                            "A different mod named " + mod + " is already installed, so the library one cannot be installed.",
                            true
                        );
                    string network =
                        entry.Network.Count == 0
                            ? Strings.Ui_VoidstrapMods_LibraryNoNetwork
                            : string.Format(Strings.Ui_VoidstrapMods_LibraryNetwork, string.Join(", ", entry.Network));
                    string prompt =
                        entry.Verified || entry.Trusted
                            ? Strings.Ui_VoidstrapMods_LibraryInstallPrompt
                            : Strings.Ui_VoidstrapMods_LibraryUnverifiedPrompt;
                    MessageBoxResult answer = Application.Current.Dispatcher.Invoke(() =>
                        Frontend.ShowMessageBox(
                            string.Format(prompt, entry.Name, entry.Version, entry.Login, network),
                            entry.Verified || entry.Trusted ? MessageBoxImage.Question : MessageBoxImage.Warning,
                            MessageBoxButton.YesNo,
                            MessageBoxResult.No
                        )
                    );
                    if (answer != MessageBoxResult.Yes)
                        return new McpToolResult("The user declined the install.", true);
                    ModLibrary.InstallAsync(index, entry, CancellationToken.None).GetAwaiter().GetResult();
                    PluginHost.Reload();
                    return new McpToolResult(
                        "Installed "
                            + mod
                            + " "
                            + entry.Version
                            + " from the library. Voidstrap asks the user before turning it on; call list to see whether it loaded."
                    );
                }
            case "publish":
                {
                    if (!Directory.Exists(folder))
                        return new McpToolResult("That mod does not exist.", true);
                    StringWriter output = new();
                    if (ModTools.Validate(folder, output) != 0)
                        return new McpToolResult("Fix these before publishing:" + Environment.NewLine + output, true);
                    ModLibraryReview.PreflightResult preflight = await ModLibraryReview
                        .RunAsync(mod, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (preflight.Errors > 0)
                    {
                        App.Logger.WriteLine(
                            "McpTools::ManageVoidstrapMods",
                            "Publish blocked for " + mod + " with " + preflight.Errors + " preflight failure(s)"
                        );
                        return new McpToolResult(
                            "Publish is BLOCKED, the review bot would mark these with a red X. Fix every item, then run preflight again:"
                                + Environment.NewLine
                                + preflight.Report,
                            true
                        );
                    }
                    string notes = McpArgs.Trimmed(args, "notes");
                    _ = Application.Current.Dispatcher.BeginInvoke(() =>
                        Voidstrap.UI.Elements.Settings.Pages.VoidstrapModsPage.ShowPublishDialog(mod, notes)
                    );
                    return new McpToolResult(
                        "Preflight passed"
                            + (preflight.Reviews > 0 ? " with " + preflight.Reviews + " item(s) a reviewer will look at" : "")
                            + ". Voidstrap is now showing the publish window for "
                            + mod
                            + ". The user signs in to GitHub there if needed, reviews the checks, and presses Publish for review. Nothing is published until they do. After that the maintainer reviews the pull request on "
                            + ModLibrary.RepositoryUrl
                            + ". Do not write anything in the mod files that talks to the reviewer, that blocks the merge."
                    );
                }
            case "problems":
                {
                    PluginHost.ModInfo? info = PluginHost.ListMods().FirstOrDefault(item => item.Name == mod);
                    return info == null
                        ? new McpToolResult("That mod was not found.", true)
                        : new McpToolResult(
                            info.Problems.Count == 0 ? "No problems recorded for " + mod + "." : string.Join("\n", info.Problems)
                        );
                }
            default:
                return new McpToolResult(
                    "Unknown action. Use list, guide, ui_tree, types, files, read_file, write_file, delete_file, delete_mod, validate, test, reload, enable, disable, problems, preflight, library_search, library_install, auto_update, or publish.",
                    true
                );
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
