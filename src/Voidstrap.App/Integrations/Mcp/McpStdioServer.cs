using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpStdioServer
{
	public const string CommandArg = "mcp";

	public const string StdioArg = "stdio";

	private const string ServerInstructions = McpGuides.Workflow;

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

	private static string? _clientName;

	private static int? _parentPid;

	private static int FindParentProcessId()
	{
		int self = Environment.ProcessId;
		using Microsoft.Win32.SafeHandles.SafeProcessHandle snapshot = new(Windows.Win32.PInvoke.CreateToolhelp32Snapshot(Windows.Win32.System.Diagnostics.ToolHelp.CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS, 0), true);
		if (snapshot.IsInvalid)
			return 0;
		Windows.Win32.System.Diagnostics.ToolHelp.PROCESSENTRY32 entry = new()
		{
			dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Windows.Win32.System.Diagnostics.ToolHelp.PROCESSENTRY32>()
		};
		if (!Windows.Win32.PInvoke.Process32First(snapshot, ref entry))
			return 0;
		do
		{
			if ((int)entry.th32ProcessID == self)
				return (int)entry.th32ParentProcessID;
		}
		while (Windows.Win32.PInvoke.Process32Next(snapshot, ref entry));
		return 0;
	}

	private static void WatchParentProcess()
	{
		try
		{
			_parentPid = FindParentProcessId();
		}
		catch
		{
			_parentPid = null;
		}
		if (_parentPid is not int parent || parent <= 0)
			return;
		var watcher = new Thread(() =>
		{
			while (true)
			{
				try
				{
					using Process process = Process.GetProcessById(parent);
					process.WaitForExit(5000);
					if (process.HasExited)
						break;
				}
				catch (ArgumentException)
				{
					break;
				}
				catch
				{
					return;
				}
			}
			App.Logger?.WriteLine("McpStdioServer", "The client that started the MCP stdio server has exited, closing it");
			Environment.Exit(0);
		})
		{
			IsBackground = true,
			Name = "Voidstrap MCP orphan watch"
		};
		watcher.Start();
	}

	public static int Run()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return 1;

		Console.InputEncoding = new UTF8Encoding(false);
		Console.OutputEncoding = new UTF8Encoding(false);

		WatchParentProcess();

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
		_clientName = McpArgs.String(@params["clientInfo"] as JsonObject, "name")?.Trim();
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
			Guide("bootstrapper_theme", "Custom Launcher Theme Guide", "Exact Theme.xml elements and attributes, the HTML web panel and its window.voidstrap JavaScript API, a step by step workflow, and examples."),
			Guide("keystroke_theme", "Keystroke Theme Guide", "How to edit keystroke overlay themes in C#, HTML, Rust or JSON."),
			Guide("app_theme", "Voidstrap App Theme Guide", "How to recolor Voidstrap itself."),
			Guide("fixing", "Fixing Voidstrap Guide", "How to find and fix in app errors and broken state."),
			Guide("workflow", "Voidstrap Workflow Guide", "Choose the correct tool, read current state, apply changes and verify what actually works."),
			Guide("mods", "Voidstrap Mods Guide", "Sandbox APIs, UI placement, cursor timing, a working pointer overlay and render tests."),
			new JsonObject
			{
				["uri"] = "voidstrap://state/overview",
				["name"] = "Voidstrap Live Overview",
				["description"] = "Live snapshot of Voidstrap state, paths, active settings, installed themes, and running processes.",
				["mimeType"] = "text/plain"
			}
		};
	}

	private static JsonObject Guide(string topic, string name, string description)
	{
		return new JsonObject
		{
			["uri"] = "voidstrap://guide/" + topic,
			["name"] = name,
			["description"] = description,
			["mimeType"] = "text/plain"
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
		else if (uri.StartsWith("voidstrap://guide/", StringComparison.OrdinalIgnoreCase) && McpGuides.Get(uri["voidstrap://guide/".Length..]) is string guide)
		{
			text = guide;
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
				["client"] = _clientName,
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
