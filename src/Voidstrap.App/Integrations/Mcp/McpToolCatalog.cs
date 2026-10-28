using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.Mcp;

public static class McpToolCatalog
{
	private enum Kind
	{
		Read,
		Write,
		Destructive
	}

	private sealed record Definition(string Name, string Description, Kind Kind, JsonObject Properties, string[] Required, bool OffUiThread, int TimeoutSeconds);

	private static readonly Definition[] Definitions = Build();

	private static readonly Dictionary<string, Definition> ByName = Definitions.ToDictionary(d => d.Name, StringComparer.Ordinal);

	public static bool Exists(string name) => ByName.ContainsKey(name);

	public static bool IsReadOnly(string name) => ByName.TryGetValue(name, out Definition? d) && d.Kind == Kind.Read;

	public static bool RunsOffUiThread(string name) => ByName.TryGetValue(name, out Definition? d) && d.OffUiThread;

	public static TimeSpan Timeout(string name) => TimeSpan.FromSeconds(ByName.TryGetValue(name, out Definition? d) ? d.TimeoutSeconds : 30);

	public static TimeSpan LongestTimeout => TimeSpan.FromSeconds(Definitions.Max(d => d.TimeoutSeconds) + 15);

	public static IEnumerable<string> Names => Definitions.Select(d => d.Name);

	public static JsonArray ToJson()
	{
		var tools = new JsonArray();
		foreach (Definition d in Definitions)
		{
			var required = new JsonArray();
			foreach (string r in d.Required)
				required.Add(r);

			tools.Add(new JsonObject
			{
				["name"] = d.Name,
				["description"] = d.Description,
				["inputSchema"] = new JsonObject
				{
					["type"] = "object",
					["properties"] = d.Properties.DeepClone(),
					["required"] = required
				},
				["annotations"] = new JsonObject
				{
					["readOnlyHint"] = d.Kind == Kind.Read,
					["destructiveHint"] = d.Kind == Kind.Destructive,
					["openWorldHint"] = false
				}
			});
		}
		return tools;
	}

	private static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };

	private static JsonObject Obj(string description) => new() { ["type"] = "object", ["description"] = description };

	private static Definition T(string name, string description, Kind kind, JsonObject properties, params string[] required) => new(name, description, kind, properties, required, false, 30);

	private static JsonObject EditorTarget(JsonObject extra)
	{
		var props = new JsonObject
		{
			["editorId"] = P("string", "Editor id from list_editors (for example bootstrapper_3, keystrokes_2, fastflags_1), a kind like 'bootstrapper', or a theme name. Defaults to the most recently opened editor."),
			["file"] = P("string", "Optional file inside a multi file editor, such as 'index.html', 'style.css' or 'scripts/app.js' in a custom bootstrapper theme. Omit for the main document (Theme.xml). Writing a new path creates the file.")
		};
		foreach (KeyValuePair<string, JsonNode?> pair in extra)
			props[pair.Key] = pair.Value?.DeepClone();
		return props;
	}

	private static Definition[] Build()
	{
		return
		[
			T("get_guide",
				"Read the workflow to choose the correct tool, then the relevant guide before editing. Topics: workflow, mods (Voidstrap widgets, cursor sprites, pages, sandbox APIs and tests), bootstrapper_theme (launcher XML or HTML and window.voidstrap), keystroke_theme, app_theme, fixing. Read mods before writing a Voidstrap mod and bootstrapper_theme before writing a launcher theme.",
				Kind.Read, new JsonObject
				{
					["topic"] = P("string", "workflow (default), mods, bootstrapper_theme, keystroke_theme, app_theme, or fixing.")
				}),

			T("test_bootstrapper_theme",
				"Build a custom launcher theme with the same code a real Roblox launch uses and report any error, plus whether it is actually the active launcher. show puts it on screen with a simulated install so the user can see it. activate makes it the active launcher first. Run this before telling the user a launcher theme is done.",
				Kind.Write, new JsonObject
				{
					["name"] = P("string", "Theme name, defaults to the selected custom theme."),
					["show"] = P("boolean", "Show the launcher on screen with a simulated install."),
					["seconds"] = P("integer", "How long to show it (3 to 30, default 8)."),
					["activate"] = P("boolean", "Set the launcher style to Custom and select this theme first.")
				}),

			T("get_voidstrap_overview",
				"Snapshot of Voidstrap: version, folders, open window and page, open editors, Roblox running state and versions, key settings, installed themes, and FastFlag counts. Call this first.",
				Kind.Read, new JsonObject()),

			T("get_settings",
				"Read Voidstrap settings (AppSettings) and Roblox GlobalBasicSettings_13.xml values with their types and enum options.",
				Kind.Read, new JsonObject
				{
					["filter"] = P("string", "Case insensitive name filter such as 'Keystrokes', 'RiShade', 'Crosshair', 'Matchmaker', 'Discord'."),
					["includeSchema"] = P("boolean", "Include type and enum metadata (default true).")
				}),

			T("set_settings",
				"Change Voidstrap settings and/or Roblox GlobalBasicSettings, save them, and refresh the UI. Use dryRun to preview, and undo_last_change to revert.",
				Kind.Write, new JsonObject
				{
					["settings"] = Obj("Map of AppSettings property names to new values (bool, number, string, enum name, array, or object)."),
					["globalSettings"] = Obj("Map of GlobalBasicSettings properties (FramerateCap, SavedQualityLevel, MouseSensitivity, MasterVolume, Fullscreen and so on) to new values."),
					["dryRun"] = P("boolean", "Show what would change without saving anything.")
				}),

			T("list_fastflags",
				"List configured FastFlags with allowlist status, the Roblox FastFlag allowlist, tracker sources, and Voidstrap FastFlag presets.",
				Kind.Read, new JsonObject
				{
					["filter"] = P("string", "Case insensitive filter for flag or preset names."),
					["includePresets"] = P("boolean", "Include Voidstrap FastFlag presets (default true)."),
					["includeAllowlist"] = P("boolean", "Include the Roblox allowlist and tracker sources (default true).")
				}),

			T("set_fastflags",
				"Set or delete FastFlags or Voidstrap presets, or apply 'Performance', 'Balanced' or 'Quality'. Saves ClientAppSettings.json. Use dryRun to preview the exact diff and undo_last_change to revert.",
				Kind.Write, new JsonObject
				{
					["flags"] = Obj("Map of FastFlag names to values, or null to delete a flag."),
					["presets"] = Obj("Map of Voidstrap preset keys (from list_fastflags) to values, or null to remove."),
					["recommendedProfile"] = P("string", "'Performance', 'Balanced', or 'Quality'."),
					["replaceAll"] = P("boolean", "Clear every existing FastFlag before applying 'flags'."),
					["dryRun"] = P("boolean", "Show the diff without saving anything.")
				}),

			T("search_fastflag_tracker",
				"Search and validate FastFlags against the MaximumADHD trackers, live Roblox ClientSettings, and the Roblox allowlist.",
				Kind.Read, new JsonObject
				{
					["query"] = P("string", "Search words such as 'grass', 'msaa', 'texture', 'vulkan'."),
					["source"] = P("string", "'all' (default), 'allowlist', 'PCDesktopClient', 'PCClientBootstrapper', 'PCStudioApp', 'FVariables.txt', or 'Roblox ClientSettings'."),
					["allowlistedOnly"] = P("boolean", "Only return allowlisted flags."),
					["validateConfigured"] = P("boolean", "Validate the configured FastFlags (default true when query is empty)."),
					["refreshOnline"] = P("boolean", "Download fresh tracker and allowlist data."),
					["limit"] = P("integer", "Maximum results (default 100, max 500).")
				}) with { OffUiThread = true, TimeoutSeconds = 90 },

			T("list_editors",
				"List open Voidstrap editors (Custom Bootstrapper theme editor, Keystroke theme editor, FastFlag editor) and installed themes.",
				Kind.Read, new JsonObject()),

			T("open_editor",
				"Open or focus an editor on screen: 'bootstrapper', 'keystroke', or 'fastflags'. Creates the theme if it does not exist and returns its editorId.",
				Kind.Write, new JsonObject
				{
					["type"] = P("string", "'bootstrapper', 'keystroke', or 'fastflags'."),
					["name"] = P("string", "Theme name for bootstrapper or keystroke editors."),
					["baseTheme"] = P("string", "Keystroke theme to clone when creating a new one."),
					["activate"] = P("boolean", "Make the theme active (default true).")
				}, "type"),

			T("list_editor_files",
				"List every file in an editor (custom bootstrapper themes can hold HTML, CSS, JS, JSON, SVG and images next to Theme.xml) and the languages it can switch between.",
				Kind.Read, EditorTarget(new JsonObject())),

			T("get_editor_code",
				"Read code from an editor or one of its files, optionally with line numbers or just a line range. Includes the format guide.",
				Kind.Read, EditorTarget(new JsonObject
				{
					["lineNumbers"] = P("boolean", "Prefix every line with its number (use this before line based edits)."),
					["startLine"] = P("integer", "First line to return (1 based)."),
					["endLine"] = P("integer", "Last line to return.")
				})),

			T("find_in_editor",
				"Search an editor or file for text or a regex and return matching line numbers with optional context.",
				Kind.Read, EditorTarget(new JsonObject
				{
					["query"] = P("string", "Text or regex to search for (case insensitive)."),
					["regex"] = P("boolean", "Treat query as a regular expression."),
					["context"] = P("integer", "Lines of context around each match (0 to 5).")
				}), "query"),

			T("set_editor_code",
				"Replace the whole code of an editor or file and refresh the live preview. Not written to the theme until save_editor, except extra files which are written right away. undo_editor reverts.",
				Kind.Write, EditorTarget(new JsonObject
				{
					["code"] = P("string", "Complete new contents."),
					["revertOnError"] = P("boolean", "Restore the previous code if the new code does not build (default false).")
				}), "code"),

			T("replace_in_editor",
				"Replace an exact snippet. Fails if the snippet is missing or appears more than once unless replaceAll is true.",
				Kind.Write, EditorTarget(new JsonObject
				{
					["oldText"] = P("string", "Exact existing text."),
					["newText"] = P("string", "Replacement text."),
					["replaceAll"] = P("boolean", "Replace every occurrence."),
					["revertOnError"] = P("boolean", "Restore the previous code if the result does not build (default false).")
				}), "oldText", "newText"),

			T("edit_editor",
				"Apply many edits to one editor or file in a single step. Nothing changes if any edit is invalid, and by default the code is restored if the result does not build. Ops: replace (oldText, text, replaceAll), insert (line, text), append (text), delete_lines (startLine, endLine), replace_lines (startLine, endLine, text). Line numbers refer to the code as it is after the previous edits.",
				Kind.Write, EditorTarget(new JsonObject
				{
					["edits"] = new JsonObject
					{
						["type"] = "array",
						["description"] = "Ordered list of edit objects, each with an 'op' field.",
						["items"] = new JsonObject { ["type"] = "object" }
					},
					["revertOnError"] = P("boolean", "Restore the previous code if the result does not build (default true).")
				}), "edits"),

			T("undo_editor",
				"Undo the last MCP change to an editor or file (keeps 30 steps per file, including deleted files).",
				Kind.Write, EditorTarget(new JsonObject())),

			T("set_editor_language",
				"Switch the code language of an editor that supports several, such as the Keystroke theme editor: csharp, html (HTML and CSS), rust, or json.",
				Kind.Write, EditorTarget(new JsonObject
				{
					["language"] = P("string", "csharp, html, rust, or json.")
				}), "language"),

			T("delete_editor_file",
				"Delete an extra file from a multi file editor such as a custom bootstrapper theme. Theme.xml cannot be deleted. undo_editor restores text files.",
				Kind.Destructive, EditorTarget(new JsonObject()), "file"),

			T("save_editor",
				"Save the editor to disk.",
				Kind.Write, EditorTarget(new JsonObject())),

			T("navigate_ui",
				"Move Voidstrap's main window to a page, search for a setting on it, or bring it to the front.",
				Kind.Write, new JsonObject
				{
					["page"] = P("string", "Home, Integrations, Deployment, Appearance, FastFlags, FastFlagEditor, Mods, More, Global, Shortcuts, News, Extensions, Manager, Settings, Accounts, Library, PrivateServers."),
					["search"] = P("string", "Setting to search for and highlight."),
					["focusWindow"] = P("boolean", "Bring the window to the front (default true).")
				}),

			T("manage_themes_and_mods",
				"Manage custom Bootstrapper themes, Keystroke themes, the custom font, the custom death sound, and files in VoidstrapMods.",
				Kind.Destructive, new JsonObject
				{
					["action"] = P("string", "list, create_bootstrapper_theme, delete_bootstrapper_theme, activate_bootstrapper_theme, create_keystroke_theme, delete_keystroke_theme, activate_keystroke_theme, set_custom_font, clear_custom_font, set_custom_death_sound, clear_custom_death_sound, write_mod_file, delete_mod_file."),
					["name"] = P("string", "Theme name."),
					["xml"] = P("string", "Theme.xml content for create_bootstrapper_theme."),
					["code"] = P("string", "C#, HTML and CSS, Rust, or JSON for create_keystroke_theme."),
					["baseTheme"] = P("string", "Keystroke theme to clone."),
					["activate"] = P("boolean", "Activate the created theme (default true)."),
					["sourcePath"] = P("string", "Source file for set_custom_font (.ttf, .otf), set_custom_death_sound (.ogg, .mp3, .wav), or write_mod_file."),
					["relativePath"] = P("string", "Path inside VoidstrapMods for write_mod_file or delete_mod_file."),
					["content"] = P("string", "Text content for write_mod_file.")
				}, "action"),

			T("manage_mods",
				"List, enable, disable, rename, reorder, or delete managed mods in the Mods library. Changes apply on the next Roblox launch.",
				Kind.Destructive, new JsonObject
				{
					["action"] = P("string", "list (default), enable, disable, rename, move, or delete."),
					["id"] = P("string", "Mod id or name from list."),
					["name"] = P("string", "New name for rename."),
					["targetId"] = P("string", "Mod to move next to for move."),
					["after"] = P("boolean", "Place after targetId (default true)."),
					["confirm"] = P("boolean", "Required true for delete.")
				}),

			T("manage_rishade",
				"Read RiShade status and presets, turn RiShade on or off, or switch the active ReShade preset.",
				Kind.Write, new JsonObject
				{
					["action"] = P("string", "status (default), enable, disable, or select_preset."),
					["preset"] = P("string", "Preset path relative to the RiShade folder, from status.")
				}),

			T("manage_mod_generator",
				"Read the generated UI theme mod, regenerate it with changed settings (fill, colours, gradient, icon swaps and more) or from a saved theme, or save its settings as a theme.",
				Kind.Write, new JsonObject
				{
					["action"] = P("string", "status (default), generate, or save_theme."),
					["settings"] = Obj("Generator settings to change before generating, using the field names from status."),
					["theme"] = P("string", "Saved theme name to generate from."),
					["name"] = P("string", "Theme name for save_theme.")
				}) with { OffUiThread = true, TimeoutSeconds = 330 },

			T("manage_voidstrap_mods",
				"Manage features inside Voidstrap with sandboxed TypeScript or JavaScript mods: widgets, cursor sprites, pages, panels, sidebar changes and automation. This differs from manage_mods, which manages Roblox content files. Read action guide, list and types first; for an existing mod read files and read_file before write_file. Use ui_map for real anchors and $.ui.attach for widgets. Cursor sprites use cursor.move events; $.clock.every has a 500 ms minimum and cannot animate at 16 or 33 ms. Validate, test, reload, then list to confirm loaded true and problems for runtime errors. New mods require the user's approval in Voidstrap. library_search finds community mods; library_install asks before installing. publish opens a window for the user to sign in and confirm publication. Read the guide for examples, sandbox limits and the full workflow.",
				Kind.Write, new JsonObject
				{
					["action"] = P("string", "list (default), guide, types, files, read_file, write_file, delete_file, delete_mod, validate, test, reload, enable, disable, problems, library_search, library_install, auto_update, publish, or ui_map."),
					["mod"] = P("string", "The mod's folder name: lowercase letters, digits and hyphens."),
					["path"] = P("string", "File path inside the mod, for example hooks/register.tsx or .voidstrap-mod/plugin.json."),
					["content"] = P("string", "Full file contents for write_file."),
					["confirm"] = P("boolean", "Required true for delete_mod."),
					["query"] = P("string", "Search for library_search. Plain words plus filters: tag:theme, by:login, is:verified, is:trusted, is:featured, is:offline, sort:new, -word."),
					["notes"] = P("string", "What changed, prefilled in the publish window for publish.")
				}) with { OffUiThread = true, TimeoutSeconds = 120 },

			T("list_accounts",
				"List the Roblox accounts saved in Voidstrap and which one is signed in. Read only, no cookies are ever returned.",
				Kind.Read, new JsonObject()) with { OffUiThread = true, TimeoutSeconds = 30 },

			T("read_voidstrap_file",
				"List folders or read text files (json, txt, log, xml, ini, html, css, js and more) inside the Voidstrap folder. Account logins are never readable.",
				Kind.Read, new JsonObject
				{
					["path"] = P("string", "Relative path inside the Voidstrap folder, empty to list the root.")
				}) with { OffUiThread = true, TimeoutSeconds = 30 },

			T("diagnose_app",
				"Find what is wrong with Voidstrap: recent in app errors and crashes (source, message, count, log path) plus broken state such as an invalid custom UI theme, a missing bootstrapper or keystroke theme, invalid FastFlag names, failing mods, or RiShade without a preset. Every problem comes with a fix id for fix_app_issue.",
				Kind.Read, new JsonObject
				{
					["maxErrors"] = P("integer", "Recent errors to include (default 20, max 50).")
				}),

			T("fix_app_issue",
				"Apply a fix from diagnose_app. Fixes: reset_custom_app_theme, reset_bootstrapper_theme, reset_keystroke_theme, remove_invalid_fastflags, disable_mod (target = mod id), disable_rishade, reset_setting (target = setting name, restores its default), clear_error_notifications, move_roblox_cache_aside (fixes Roblox crashing on start from a damaged cache, keeps login and settings). Most fixes can be reverted with undo_last_change.",
				Kind.Write, new JsonObject
				{
					["fix"] = P("string", "Fix id from diagnose_app."),
					["target"] = P("string", "Mod id or setting name when the fix needs one.")
				}, "fix"),

			T("manage_app_theme",
				"Create and apply Voidstrap's own UI theme live: read every color key, recolor any of them, add a linear or radial window gradient, write raw theme XAML, switch to a built in theme, and save or load theme profiles. Changes show at once in every open window and undo_last_change reverts them.",
				Kind.Write, new JsonObject
				{
					["action"] = P("string", "schema, get (default), set_colors, set_xaml, use_builtin, list_profiles, save_profile, load_profile, or delete_profile."),
					["colors"] = Obj("For set_colors: map of theme keys (from schema) to #AARRGGBB or #RRGGBB colors. Keys you leave out keep their current color."),
					["gradient"] = Obj("For set_colors: window gradient as { stops: [{ color, offset }], angle, radial } with 2 to 8 stops, or { remove: true }."),
					["baseTheme"] = P("string", "Built in theme to start from for get or set_colors, or to switch to with use_builtin (Dark, Light, Voidstrap, Berry, Blue and so on)."),
					["xaml"] = P("string", "Full theme ResourceDictionary for set_xaml."),
					["name"] = P("string", "Profile name.")
				}),

			T("undo_last_change",
				"Undo the most recent MCP change to settings, FastFlags, mods, or RiShade (keeps 25 steps). Set list to true to see what can be undone.",
				Kind.Write, new JsonObject
				{
					["list"] = P("boolean", "Only list the undo history.")
				}),

			T("launch_roblox",
				"Launch Roblox Player or Studio through Voidstrap, join a placeId and optional jobId, or close Roblox. Only one Roblox instance can run.",
				Kind.Destructive, new JsonObject
				{
					["mode"] = P("string", "'player' (default), 'place', 'studio', or 'close'."),
					["placeId"] = P("string", "Place id to join."),
					["jobId"] = P("string", "Server job id (GUID) to join.")
				}),

			T("get_logs_and_activity",
				"Read recent Voidstrap logs, the latest Roblox log, and server history.",
				Kind.Read, new JsonObject
				{
					["source"] = P("string", "'all' (default), 'voidstrap', 'roblox', or 'history'."),
					["maxLines"] = P("integer", "Lines per source (default 120, max 500)."),
					["filter"] = P("string", "Case insensitive line filter.")
				})
		];
	}
}
