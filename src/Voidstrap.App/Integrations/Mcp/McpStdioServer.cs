using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.Mcp;

public static class McpStdioServer
{
	public const string CommandArg = "mcp";

	private const string ServerInstructions =
		"Voidstrap MCP Server gives you complete live control over Voidstrap on Windows. " +
		"Start with get_voidstrap_overview to see the app state, active settings, installed themes/mods, FastFlag count, and open windows. " +
		"Use get_settings (with optional filter like 'Keystrokes', 'RiShade', 'Crosshair', 'Clipper', 'Matchmaker', 'Web', 'DoubleMovement', 'Texture') and set_settings to inspect or modify any of the 200+ Voidstrap settings and Roblox GlobalBasicSettings_13.xml properties in real time. " +
		"Use list_fastflags, set_fastflags, and search_fastflag_tracker to read, validate, search, or modify raw Roblox FastFlags and Voidstrap FastFlag presets. " +
		"Roblox FastFlag Allowlist (https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569, JSON feed https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569.json): Roblox restricts local client configuration via ClientAppSettings.json on PC to allowlisted FastFlags. " +
		"The official allowlisted flags are: Geometry: DFIntCSGLevelOfDetailSwitchingDistance, DFIntCSGLevelOfDetailSwitchingDistanceL12, DFIntCSGLevelOfDetailSwitchingDistanceL23, DFIntCSGLevelOfDetailSwitchingDistanceL34 | " +
		"Rendering: FFlagHandleAltEnterFullscreenManually, DFFlagTextureQualityOverrideEnabled, DFIntTextureQualityOverride, FIntDebugForceMSAASamples, DFFlagDisableDPIScale, FFlagDebugGraphicsPreferD3D11, FFlagDebugSkyGray, DFFlagDebugPauseVoxelizer, DFIntDebugFRMQualityLevelOverride, FIntFRMMaxGrassDistance, FIntFRMMinGrassDistance, FFlagDebugGraphicsPreferVulkan, FFlagDebugGraphicsPreferOpenGL | " +
		"User Interface: FIntGrassMovementReducedMotionFactor. " +
		"MaximumADHD Roblox-FFlag-Tracker (https://github.com/MaximumADHD/Roblox-FFlag-Tracker) & Roblox-Client-Tracker (https://github.com/MaximumADHD/Roblox-Client-Tracker): use search_fastflag_tracker to query live FastFlags and default values across PCDesktopClient.json (https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCDesktopClient.json), PCClientBootstrapper.json (https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCClientBootstrapper.json), PCStudioApp.json (https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCStudioApp.json), FVariables.txt (https://raw.githubusercontent.com/MaximumADHD/Roblox-Client-Tracker/refs/heads/roblox/FVariables.txt), and live Roblox ClientSettings (https://clientsettings.roblox.com/v2/settings/application/PCDesktopClient), or to validate all currently configured flags in ClientAppSettings.json. " +
		"Use open_editor ('bootstrapper', 'keystroke', or 'fastflags'), get_editor_code, set_editor_code, replace_in_editor, and save_editor to open and live-edit Custom Bootstrapper XML themes, Keystroke overlay themes (C#, HTML/CSS, Rust, JSON), and FastFlags with instant on-screen preview. " +
		"Use navigate_ui to switch Voidstrap's MainWindow to any active page or highlight a setting, manage_themes_and_mods to manage custom themes/fonts/sounds/mods, launch_roblox to launch Player/Studio or join a placeId/jobId, and get_logs_and_activity to inspect Voidstrap logs, Roblox logs, and server history.";

	private const string AllowlistAndTrackerReference =
		"ROBLOX FASTFLAG ALLOWLIST & MAXIMUMADHD FFLAG TRACKER REFERENCE\n\n" +
		"1. Official Roblox FastFlag Allowlist\n" +
		"   Announcement: https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569\n" +
		"   Live JSON Feed: https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569.json\n" +
		"   Overview: Roblox restricts local client configuration via ClientAppSettings.json on PC so that only FastFlags on the official allowlist are applied by the standard client.\n" +
		"   Official Allowlisted Flags by Category:\n" +
		"     Geometry:\n" +
		"       * DFIntCSGLevelOfDetailSwitchingDistance\n" +
		"       * DFIntCSGLevelOfDetailSwitchingDistanceL12\n" +
		"       * DFIntCSGLevelOfDetailSwitchingDistanceL23\n" +
		"       * DFIntCSGLevelOfDetailSwitchingDistanceL34\n" +
		"     Rendering:\n" +
		"       * FFlagHandleAltEnterFullscreenManually\n" +
		"       * DFFlagTextureQualityOverrideEnabled\n" +
		"       * DFIntTextureQualityOverride\n" +
		"       * FIntDebugForceMSAASamples\n" +
		"       * DFFlagDisableDPIScale\n" +
		"       * FFlagDebugGraphicsPreferD3D11\n" +
		"       * FFlagDebugSkyGray\n" +
		"       * DFFlagDebugPauseVoxelizer\n" +
		"       * DFIntDebugFRMQualityLevelOverride\n" +
		"       * FIntFRMMaxGrassDistance\n" +
		"       * FIntFRMMinGrassDistance\n" +
		"       * FFlagDebugGraphicsPreferVulkan\n" +
		"       * FFlagDebugGraphicsPreferOpenGL\n" +
		"     User Interface:\n" +
		"       * FIntGrassMovementReducedMotionFactor\n\n" +
		"   Recommended Allowlisted Profiles in Voidstrap:\n" +
		"     Performance: DFFlagTextureQualityOverrideEnabled=True, DFIntTextureQualityOverride=0, FIntDebugForceMSAASamples=1\n" +
		"     Balanced:    DFFlagTextureQualityOverrideEnabled=True, DFIntTextureQualityOverride=2, FIntDebugForceMSAASamples=2\n" +
		"     Quality:     DFFlagTextureQualityOverrideEnabled=True, DFIntTextureQualityOverride=3, FIntDebugForceMSAASamples=4\n\n" +
		"2. MaximumADHD Roblox-FFlag-Tracker & Roblox-Client-Tracker\n" +
		"   Tracker Repository: https://github.com/MaximumADHD/Roblox-FFlag-Tracker\n" +
		"   Client Tracker Repository: https://github.com/MaximumADHD/Roblox-Client-Tracker\n" +
		"   Live Data Feeds Queried by Voidstrap (via search_fastflag_tracker):\n" +
		"     * PCDesktopClient: https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCDesktopClient.json\n" +
		"     * PCClientBootstrapper: https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCClientBootstrapper.json\n" +
		"     * PCStudioApp: https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCStudioApp.json\n" +
		"     * FVariables.txt: https://raw.githubusercontent.com/MaximumADHD/Roblox-Client-Tracker/refs/heads/roblox/FVariables.txt\n" +
		"     * Roblox ClientSettings API: https://clientsettings.roblox.com/v2/settings/application/PCDesktopClient\n\n" +
		"3. FastFlag Naming & Type Conventions\n" +
		"   * FFlag / DFFlag / SFFlag: Boolean ('True' or 'False')\n" +
		"   * FInt / DFInt / SFInt: 32-bit signed integer string (e.g. '0', '1', '144')\n" +
		"   * FString / DFString / SFString: String value\n" +
		"   * FLog / DFLog / SFLog: Verbosity log level integer\n" +
		"   * Prefix 'D' = Dynamic (can update at runtime without restart); 'S' = Synchronized with server.";

	public static bool IsRequested(string[] args) => IsMcpStartupArg(args);

	public static bool IsMcpStartupArg(string[] args)
	{
		if (!Voidstrap.Utility.Platform.IsWindows || args.Length == 0)
			return false;

		foreach (string arg in args)
		{
			if (string.Equals(arg, CommandArg, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(arg, "-" + CommandArg, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(arg, "--" + CommandArg, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(arg, "/" + CommandArg, StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	public static int Run()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return 1;

		Console.InputEncoding = new UTF8Encoding(false);
		Console.OutputEncoding = new UTF8Encoding(false);

		using Stream stdin = Console.OpenStandardInput();
		using Stream stdout = Console.OpenStandardOutput();
		using var reader = new StreamReader(stdin, new UTF8Encoding(false), false, 4096, leaveOpen: true);
		using var writer = new StreamWriter(stdout, new UTF8Encoding(false), 4096, leaveOpen: true)
		{
			AutoFlush = true,
			NewLine = "\n"
		};

		while (true)
		{
			string? line;
			try
			{
				line = reader.ReadLine();
			}
			catch
			{
				break;
			}

			if (line == null)
				break;

			if (string.IsNullOrWhiteSpace(line))
				continue;

			JsonObject? response = HandleMessage(line);
			if (response != null)
				writer.WriteLine(response.ToJsonString());
		}

		return 0;
	}

	private static JsonObject? HandleMessage(string raw)
	{
		JsonObject? msg;
		try
		{
			msg = JsonNode.Parse(raw) as JsonObject;
		}
		catch (Exception ex)
		{
			return Error(null, -32700, "Parse error: " + ex.Message);
		}

		if (msg == null)
			return Error(null, -32600, "Invalid Request");

		bool hasId = msg.ContainsKey("id");
		JsonNode? id = hasId ? msg["id"]?.DeepClone() : null;
		string? method = msg["method"]?.GetValue<string>();

		if (!hasId)
			return null;

		if (string.IsNullOrEmpty(method))
			return Error(id, -32600, "Missing method");

		JsonObject @params = msg["params"] as JsonObject ?? new JsonObject();

		return method switch
		{
			"initialize" => Ok(id, BuildInitializeResult(@params)),
			"ping" => Ok(id, new JsonObject()),
			"tools/list" => Ok(id, new JsonObject { ["tools"] = BuildToolSchemas() }),
			"tools/call" => Ok(id, HandleToolCall(@params)),
			"resources/list" => Ok(id, new JsonObject { ["resources"] = BuildResourceList() }),
			"resources/read" => Ok(id, HandleResourceRead(@params)),
			"prompts/list" => Ok(id, new JsonObject { ["prompts"] = new JsonArray() }),
			_ => Error(id, -32601, "Method not found: " + method)
		};
	}

	private static JsonObject BuildInitializeResult(JsonObject @params)
	{
		string clientVersion = @params["protocolVersion"]?.GetValue<string>() ?? "2024-11-05";
		string negotiated = clientVersion is "2025-03-26" or "2024-11-05" ? clientVersion : "2024-11-05";

		return new JsonObject
		{
			["protocolVersion"] = negotiated,
			["capabilities"] = new JsonObject
			{
				["tools"] = new JsonObject(),
				["resources"] = new JsonObject()
			},
			["serverInfo"] = new JsonObject
			{
				["name"] = "voidstrap",
				["version"] = "1.0.0"
			},
			["instructions"] = ServerInstructions
		};
	}

	private static JsonArray BuildResourceList()
	{
		return new JsonArray
		{
			new JsonObject
			{
				["uri"] = "voidstrap://guide/overview",
				["name"] = "Voidstrap Capabilities Guide",
				["description"] = "Complete guide to Voidstrap settings, FastFlags, editors, themes, mods, UI navigation, and MCP tools.",
				["mimeType"] = "text/plain"
			},
			new JsonObject
			{
				["uri"] = "voidstrap://fastflags/allowlist-and-tracker",
				["name"] = "Roblox FastFlag Allowlist & MaximumADHD FFlag Tracker Reference",
				["description"] = "Complete reference for Roblox's official local FastFlag allowlist (https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569) and MaximumADHD's Roblox-FFlag-Tracker (https://github.com/MaximumADHD/Roblox-FFlag-Tracker).",
				["mimeType"] = "text/plain"
			},
			new JsonObject
			{
				["uri"] = "voidstrap://state/overview",
				["name"] = "Voidstrap Live Overview",
				["description"] = "Live snapshot of Voidstrap state, paths, active settings, installed themes, and running processes.",
				["mimeType"] = "text/plain"
			}
		};
	}

	private static JsonObject HandleResourceRead(JsonObject @params)
	{
		string uri = @params["uri"]?.GetValue<string>() ?? string.Empty;
		string text;

		if (string.Equals(uri, "voidstrap://state/overview", StringComparison.OrdinalIgnoreCase))
		{
			McpToolResult live = ForwardToRunningApp("get_voidstrap_overview", new JsonObject());
			text = live.Text;
		}
		else if (string.Equals(uri, "voidstrap://fastflags/allowlist-and-tracker", StringComparison.OrdinalIgnoreCase))
		{
			text = AllowlistAndTrackerReference;
		}
		else
		{
			text = ServerInstructions + "\n\n" + AllowlistAndTrackerReference;
		}

		return new JsonObject
		{
			["contents"] = new JsonArray
			{
				new JsonObject
				{
					["uri"] = string.IsNullOrEmpty(uri) ? "voidstrap://guide/overview" : uri,
					["mimeType"] = "text/plain",
					["text"] = text
				}
			}
		};
	}

	private static JsonObject HandleToolCall(JsonObject @params)
	{
		string? name = @params["name"]?.GetValue<string>();
		if (string.IsNullOrWhiteSpace(name))
			return ToolResponse("Missing tool name.", isError: true);

		JsonObject args = @params["arguments"] as JsonObject ?? new JsonObject();
		McpToolResult result = ForwardToRunningApp(name, args);
		return ToolResponse(result.Text, result.IsError);
	}

	private static McpToolResult ForwardToRunningApp(string name, JsonObject args)
	{
		try
		{
			using var client = new NamedPipeClientStream(".", McpBridgeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
			client.Connect(3000);

			using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
			using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
			{
				AutoFlush = true,
				NewLine = "\n"
			};

			var payload = new JsonObject
			{
				["name"] = name,
				["arguments"] = args.DeepClone()
			};

			writer.WriteLine(payload.ToJsonString());
			string? reply = reader.ReadLine();

			if (string.IsNullOrEmpty(reply))
				return new McpToolResult("Voidstrap closed the connection before replying.", true);

			JsonObject? parsed = JsonNode.Parse(reply) as JsonObject;
			string text = parsed?["text"]?.GetValue<string>() ?? reply;
			bool isError = parsed?["isError"]?.GetValue<bool>() ?? false;
			return new McpToolResult(text, isError);
		}
		catch (TimeoutException)
		{
			return new McpToolResult("Voidstrap is not currently open. Open Voidstrap first so the MCP bridge is active.", true);
		}
		catch (Exception ex)
		{
			return new McpToolResult("Could not reach the running Voidstrap instance: " + ex.Message, true);
		}
	}

	private static JsonArray BuildToolSchemas()
	{
		return new JsonArray
		{
			Tool(
				"get_voidstrap_overview",
				"Get a complete snapshot of Voidstrap: version, directories, UI state (active page and open editors), Roblox Player/Studio running status and installed versions, key active settings, installed custom Bootstrapper and Keystroke themes, FastFlag count, and all navigable UI pages.",
				new JsonObject(),
				Array.Empty<string>()),

			Tool(
				"get_settings",
				"Read Voidstrap settings (AppSettings) and Roblox GlobalBasicSettings_13.xml properties, including current values, data types, and valid enum options.",
				new JsonObject
				{
					["filter"] = Prop("string", "Optional case-insensitive substring to filter setting names (e.g. 'Keystrokes', 'RiShade', 'Crosshair', 'Clipper', 'Matchmaker', 'Web', 'DoubleMovement', 'Texture', 'Bootstrapper')."),
					["includeSchema"] = Prop("boolean", "Include type and enum metadata for each setting (default true).")
				},
				Array.Empty<string>()),

			Tool(
				"set_settings",
				"Modify any Voidstrap setting(s) on AppSettings and/or Roblox GlobalBasicSettings_13.xml, save immediately, and refresh the live UI.",
				new JsonObject
				{
					["settings"] = new JsonObject
					{
						["type"] = "object",
						["description"] = "Object mapping AppSettings property names to their new values (supports bool, int, double, string, enum names, arrays, and nested objects)."
					},
					["globalSettings"] = new JsonObject
					{
						["type"] = "object",
						["description"] = "Optional object mapping Roblox GlobalBasicSettings_13.xml properties (e.g. FramerateCap, SavedQualityLevel, MouseSensitivity, MasterVolume, Fullscreen) to new values."
					}
				},
				Array.Empty<string>()),

			Tool(
				"list_fastflags",
				"List configured Roblox FastFlags in Voidstrap (with Roblox allowlist status and category), the complete official Roblox FastFlag Allowlist (https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569), MaximumADHD Roblox-FFlag-Tracker sources (https://github.com/MaximumADHD/Roblox-FFlag-Tracker), and Voidstrap FastFlag preset keys.",
				new JsonObject
				{
					["filter"] = Prop("string", "Optional case-insensitive substring filter for flag or preset names."),
					["includePresets"] = Prop("boolean", "Include Voidstrap's named FastFlag presets (default true)."),
					["includeAllowlist"] = Prop("boolean", "Include the full official Roblox FastFlag Allowlist and tracker source URLs (default true).")
				},
				Array.Empty<string>()),

			Tool(
				"set_fastflags",
				"Set or delete raw Roblox FastFlags, Voidstrap FastFlag presets, or apply a recommended allowlisted profile ('Performance', 'Balanced', 'Quality'), save ClientAppSettings.json, and refresh the FastFlag Editor UI if open.",
				new JsonObject
				{
					["flags"] = new JsonObject
					{
						["type"] = "object",
						["description"] = "Object mapping raw FastFlag names (e.g. DFFlagTextureQualityOverrideEnabled) to values, or null to delete a flag."
					},
					["presets"] = new JsonObject
					{
						["type"] = "object",
						["description"] = "Optional object mapping Voidstrap preset keys (from list_fastflags availablePresets, e.g. 'Rendering.Framerate') to values, or null to remove."
					},
					["recommendedProfile"] = Prop("string", "Optional allowlisted preset profile to apply: 'Performance', 'Balanced', or 'Quality'."),
					["replaceAll"] = Prop("boolean", "If true, clear all existing FastFlags before applying 'flags'.")
				},
				Array.Empty<string>()),

			Tool(
				"search_fastflag_tracker",
				"Search and validate FastFlags across MaximumADHD's Roblox-FFlag-Tracker (https://github.com/MaximumADHD/Roblox-FFlag-Tracker: PCDesktopClient.json, PCClientBootstrapper.json, PCStudioApp.json), MaximumADHD's Roblox-Client-Tracker (FVariables.txt), live Roblox ClientSettings (clientsettings.roblox.com), and the official Roblox FastFlag Allowlist (https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569).",
				new JsonObject
				{
					["query"] = Prop("string", "Case-insensitive search query (substring or space-separated keywords, e.g. 'grass', 'msaa', 'texture', 'sky', 'lod', 'vulkan')."),
					["source"] = Prop("string", "Optional source filter: 'all' (default), 'allowlist', 'PCDesktopClient', 'PCClientBootstrapper', 'PCStudioApp', 'FVariables.txt', or 'Roblox ClientSettings'."),
					["allowlistedOnly"] = Prop("boolean", "If true, only return flags on Roblox's official local configuration allowlist."),
					["validateConfigured"] = Prop("boolean", "If true, validate all FastFlags currently configured in Voidstrap against MaximumADHD's tracker and Roblox's official allowlist (default true when query is empty)."),
					["refreshOnline"] = Prop("boolean", "If true, re-fetch the live DevForum allowlist JSON (3966569.json) and MaximumADHD tracker datasets from GitHub."),
					["limit"] = Prop("integer", "Maximum number of matching flags to return (default 100, max 500).")
				},
				Array.Empty<string>()),

			Tool(
				"list_editors",
				"List all open Voidstrap editor windows and pages (Keystroke Theme Editor, Custom Bootstrapper Theme Editor, FastFlag Editor) as well as all installed custom themes on disk.",
				new JsonObject(),
				Array.Empty<string>()),

			Tool(
				"open_editor",
				"Open or focus a Voidstrap editor window on screen ('bootstrapper', 'keystroke', or 'fastflags'). Automatically creates the custom theme if it does not exist yet and returns its editorId for live editing.",
				new JsonObject
				{
					["type"] = Prop("string", "Editor type to open: 'bootstrapper', 'keystroke', or 'fastflags'."),
					["name"] = Prop("string", "Theme name for 'bootstrapper' or 'keystroke' editors (created automatically if new)."),
					["baseTheme"] = Prop("string", "Optional built-in or existing keystroke theme name to clone from when creating a new keystroke theme."),
					["activate"] = Prop("boolean", "Also set this theme as the active theme in Voidstrap settings (default true).")
				},
				new[] { "type" }),

			Tool(
				"get_editor_code",
				"Read the full code, language, and syntax/schema guide from an open Voidstrap editor (or a custom theme on disk).",
				new JsonObject
				{
					["editorId"] = Prop("string", "Optional editor id from list_editors or open_editor (e.g. 'keystroke:MyTheme', 'bootstrapper:MyTheme', 'fastflags'). Defaults to the most recently opened editor.")
				},
				Array.Empty<string>()),

			Tool(
				"set_editor_code",
				"Replace the entire code in an open Voidstrap editor and immediately refresh its live preview on screen without saving to disk until save_editor is called.",
				new JsonObject
				{
					["editorId"] = Prop("string", "Optional editor id from list_editors. Defaults to the most recently opened editor."),
					["code"] = Prop("string", "Complete new code/markup/JSON for the editor.")
				},
				new[] { "code" }),

			Tool(
				"replace_in_editor",
				"Replace an exact snippet in an open Voidstrap editor and immediately refresh its live preview.",
				new JsonObject
				{
					["editorId"] = Prop("string", "Optional editor id from list_editors. Defaults to the most recently opened editor."),
					["oldText"] = Prop("string", "Exact existing text in the editor to replace."),
					["newText"] = Prop("string", "Replacement text.")
				},
				new[] { "oldText", "newText" }),

			Tool(
				"save_editor",
				"Save the current state of an open Voidstrap editor to disk.",
				new JsonObject
				{
					["editorId"] = Prop("string", "Optional editor id from list_editors. Defaults to the most recently opened editor.")
				},
				Array.Empty<string>()),

			Tool(
				"navigate_ui",
				"Navigate Voidstrap's MainWindow to any active page, highlight/search for a setting on the page, or bring MainWindow to the foreground.",
				new JsonObject
				{
					["page"] = Prop("string", "Page name: Home, Integrations, Deployment (or Bootstrapper), Appearance, FastFlags, FastFlagEditor, Mods, More, Global (or GBSEditor), Shortcuts, News, Extensions, Manager (or Downloads), Settings (or Channel), Accounts, Library, PrivateServers."),
					["search"] = Prop("string", "Optional search query to filter/highlight a setting on the target page."),
					["focusWindow"] = Prop("boolean", "Bring MainWindow to the foreground (default true).")
				},
				Array.Empty<string>()),

			Tool(
				"manage_themes_and_mods",
				"Manage Voidstrap custom Bootstrapper themes, Keystroke themes, custom font, custom death sound, and files in VoidstrapMods.",
				new JsonObject
				{
					["action"] = Prop("string", "Action: 'list', 'create_bootstrapper_theme', 'delete_bootstrapper_theme', 'activate_bootstrapper_theme', 'create_keystroke_theme', 'delete_keystroke_theme', 'activate_keystroke_theme', 'set_custom_font', 'clear_custom_font', 'set_custom_death_sound', 'clear_custom_death_sound', 'write_mod_file', 'delete_mod_file'."),
					["name"] = Prop("string", "Theme name for theme actions."),
					["xml"] = Prop("string", "Optional XML content for create_bootstrapper_theme."),
					["code"] = Prop("string", "Optional C#, HTML/CSS, Rust, or JSON code for create_keystroke_theme."),
					["baseTheme"] = Prop("string", "Optional base theme name to clone for create_keystroke_theme."),
					["activate"] = Prop("boolean", "Activate the created theme immediately (default true)."),
					["sourcePath"] = Prop("string", "Source file path on disk for set_custom_font, set_custom_death_sound, or write_mod_file."),
					["relativePath"] = Prop("string", "Relative path inside VoidstrapMods for write_mod_file or delete_mod_file."),
					["content"] = Prop("string", "Text content for write_mod_file.")
				},
				new[] { "action" }),

			Tool(
				"launch_roblox",
				"Launch Roblox Player, Roblox Studio, join a specific placeId/jobId through Voidstrap, or close running Roblox processes.",
				new JsonObject
				{
					["mode"] = Prop("string", "Launch mode: 'player' (default), 'studio', 'place', or 'close'."),
					["placeId"] = Prop("string", "Optional Roblox Place ID to join when mode is 'player' or 'place'."),
					["jobId"] = Prop("string", "Optional Roblox Server Job ID (gameInstanceId) to join.")
				},
				Array.Empty<string>()),

			Tool(
				"get_logs_and_activity",
				"Read recent Voidstrap logs, latest Roblox logs, and server join history for diagnostics and activity inspection.",
				new JsonObject
				{
					["source"] = Prop("string", "Source to read: 'all' (default), 'voidstrap', 'roblox', or 'history'."),
					["maxLines"] = Prop("integer", "Maximum log lines to return per source (default 120, max 500)."),
					["filter"] = Prop("string", "Optional case-insensitive substring filter for log lines.")
				},
				Array.Empty<string>())
		};
	}

	private static JsonObject Tool(string name, string description, JsonObject properties, string[] required)
	{
		var reqArray = new JsonArray();
		foreach (string r in required)
			reqArray.Add(r);

		return new JsonObject
		{
			["name"] = name,
			["description"] = description,
			["inputSchema"] = new JsonObject
			{
				["type"] = "object",
				["properties"] = properties,
				["required"] = reqArray
			}
		};
	}

	private static JsonObject Prop(string type, string description)
	{
		return new JsonObject
		{
			["type"] = type,
			["description"] = description
		};
	}

	private static JsonObject ToolResponse(string text, bool isError)
	{
		return new JsonObject
		{
			["content"] = new JsonArray
			{
				new JsonObject
				{
					["type"] = "text",
					["text"] = text
				}
			},
			["isError"] = isError
		};
	}

	private static JsonObject Ok(JsonNode? id, JsonObject result)
	{
		return new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id,
			["result"] = result
		};
	}

	private static JsonObject Error(JsonNode? id, int code, string message)
	{
		return new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id,
			["error"] = new JsonObject
			{
				["code"] = code,
				["message"] = message
			}
		};
	}
}
