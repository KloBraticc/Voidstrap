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
		"Editing: open_editor ('bootstrapper', 'keystroke', or 'fastflags') opens an editor on screen. list_editor_files shows every file in it, a custom bootstrapper theme can hold Theme.xml plus HTML, CSS, JS, JSON and SVG files, and a keystroke theme can switch between C#, HTML and CSS, Rust and JSON with set_editor_language. " +
		"Read with get_editor_code (lineNumbers, startLine, endLine, file) and find_in_editor, change code with edit_editor (batched replace, insert, append, delete_lines, replace_lines that roll back if the result does not build), replace_in_editor, or set_editor_code, then save_editor. undo_editor reverts the last MCP edit of a file, delete_editor_file removes extra files. " +
		"Safety: set_settings and set_fastflags accept dryRun to preview, and undo_last_change reverts the last MCP change to settings, FastFlags, mods or RiShade. Destructive actions such as deleting a managed mod need confirm set to true. " +
		"Fixing problems: diagnose_app lists recent in app errors and broken Voidstrap state with a fix id for each, and fix_app_issue applies the fix. App look: manage_app_theme recolors Voidstrap itself live, adds window gradients, takes raw theme XAML, and saves profiles. " +
		"More control: manage_mods (managed mod library), manage_rishade (status, on or off, preset), manage_mod_generator (regenerate the UI theme mod with new colours or icon swaps), list_accounts (read only), and read_voidstrap_file (browse and read Voidstrap config and log files). " +
		"Use navigate_ui to switch Voidstrap's MainWindow to any active page or highlight a setting, manage_themes_and_mods to manage custom themes/fonts/sounds/mods, launch_roblox to launch Player/Studio or join a placeId/jobId (multi-instance is blocked; only one Roblox instance can run at a time), and get_logs_and_activity to inspect Voidstrap logs, Roblox logs, and server history.";

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

	public static bool IsMcpToken(string arg)
	{
		return string.Equals(arg, CommandArg, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(arg, "-" + CommandArg, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(arg, "--" + CommandArg, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(arg, "/" + CommandArg, StringComparison.OrdinalIgnoreCase);
	}

	public static bool IsMcpStartupArg(string[] args)
	{
		if (!Voidstrap.Utility.Platform.IsWindows || args.Length < 2)
			return false;

		if (!IsMcpToken(args[1]))
			return false;

		if (args.Length == 2)
			return Cli.IsMcpStdioPipeActive();

		if (args.Length == 3)
		{
			string sub = args[2];
			return string.Equals(sub, "stdio", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(sub, "-stdio", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(sub, "--stdio", StringComparison.OrdinalIgnoreCase);
		}

		return false;
	}

	private static readonly string[] SupportedProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

	private const int MaxRequestChars = 32 * 1024 * 1024;

	public static int Run()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return 1;

		Console.InputEncoding = new UTF8Encoding(false);
		Console.OutputEncoding = new UTF8Encoding(false);

		using Stream stdin = Console.OpenStandardInput();
		using Stream stdout = Console.OpenStandardOutput();
		using var reader = new StreamReader(stdin, new UTF8Encoding(false), false, 65536, leaveOpen: true);
		using var writer = new StreamWriter(stdout, new UTF8Encoding(false), 65536, leaveOpen: true)
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
			catch (IOException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}

			if (line == null)
				break;

			if (string.IsNullOrWhiteSpace(line))
				continue;

			JsonObject? response;
			try
			{
				response = line.Length > MaxRequestChars ? Error(null, -32600, "Request is too large") : HandleMessage(line);
			}
			catch (Exception ex)
			{
				response = Error(null, -32603, "Internal error: " + ex.Message);
			}

			if (response == null)
				continue;

			try
			{
				writer.WriteLine(response.ToJsonString());
			}
			catch (IOException)
			{
				break;
			}
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
			return Error(null, -32600, "Invalid Request, batches are not supported");

		if (!msg.ContainsKey("id"))
			return null;

		JsonNode? id = msg["id"]?.DeepClone();
		string? method = McpArgs.String(msg, "method");

		if (string.IsNullOrEmpty(method))
			return Error(id, -32600, "Missing method");

		JsonObject @params = msg["params"] as JsonObject ?? new JsonObject();

		try
		{
			return method switch
			{
				"initialize" => Ok(id, BuildInitializeResult(@params)),
				"ping" => Ok(id, new JsonObject()),
				"tools/list" => Ok(id, new JsonObject { ["tools"] = McpToolCatalog.ToJson() }),
				"tools/call" => Ok(id, HandleToolCall(@params)),
				"resources/list" => Ok(id, new JsonObject { ["resources"] = BuildResourceList() }),
				"resources/templates/list" => Ok(id, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
				"resources/read" => Ok(id, HandleResourceRead(@params)),
				"prompts/list" => Ok(id, new JsonObject { ["prompts"] = new JsonArray() }),
				_ => Error(id, -32601, "Method not found: " + method)
			};
		}
		catch (Exception ex)
		{
			return Error(id, -32603, "Internal error: " + ex.Message);
		}
	}

	private static JsonObject BuildInitializeResult(JsonObject @params)
	{
		string clientVersion = McpArgs.String(@params, "protocolVersion") ?? SupportedProtocols[^1];
		string negotiated = Array.IndexOf(SupportedProtocols, clientVersion) >= 0 ? clientVersion : SupportedProtocols[0];

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
		string uri = McpArgs.String(@params, "uri") ?? string.Empty;
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
		string? name = McpArgs.String(@params, "name")?.Trim();
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

			using var reader = new StreamReader(client, new UTF8Encoding(false), false, 65536, leaveOpen: true);
			using var writer = new StreamWriter(client, new UTF8Encoding(false), 65536, leaveOpen: true)
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

			TimeSpan wait = McpToolCatalog.Exists(name) ? McpToolCatalog.Timeout(name) + TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(45);
			using var timeout = new System.Threading.CancellationTokenSource(wait);
			string? reply;
			try
			{
				reply = reader.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult();
			}
			catch (OperationCanceledException)
			{
				return new McpToolResult($"Voidstrap did not answer within {wait.TotalSeconds:0} seconds. It may be busy or showing a dialog.", true);
			}

			if (string.IsNullOrEmpty(reply))
				return new McpToolResult("Voidstrap closed the connection before replying.", true);

			JsonObject? parsed = JsonNode.Parse(reply) as JsonObject;
			string text = McpArgs.String(parsed, "text") ?? reply;
			bool isError = McpArgs.Bool(parsed, "isError") ?? false;
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
