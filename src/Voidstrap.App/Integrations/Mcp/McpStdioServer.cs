using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.Mcp;

public static class McpStdioServer
{
	public const string CommandArg = "mcp";

	private const string LatestProtocol = "2025-06-18";

	private static readonly string[] SupportedProtocols = ["2025-06-18", "2025-03-26", "2024-11-05"];

	public static bool IsRequested(string[] args)
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return false;

		for (int i = 1; i < args.Length; i++)
		{
			string arg = args[i];

			if (arg.Equals(CommandArg, StringComparison.OrdinalIgnoreCase)
				|| arg.Equals("-mcp", StringComparison.OrdinalIgnoreCase)
				|| arg.Equals("--mcp", StringComparison.OrdinalIgnoreCase)
				|| arg.Equals("/mcp", StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	public static int Run()
	{
		try
		{
			return RunAsync().GetAwaiter().GetResult();
		}
		catch
		{
			return 1;
		}
	}

	private static async Task<int> RunAsync()
	{
		var encoding = new UTF8Encoding(false);
		using var input = new StreamReader(Console.OpenStandardInput(), encoding, false);
		using var output = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true, NewLine = "\n" };

		while (true)
		{
			string? line = await input.ReadLineAsync().ConfigureAwait(false);

			if (line == null)
				return 0;

			if (string.IsNullOrWhiteSpace(line))
				continue;

			JsonNode? message;

			try
			{
				message = JsonNode.Parse(line);
			}
			catch
			{
				await output.WriteLineAsync(Error(null, -32700, "Parse error").ToJsonString()).ConfigureAwait(false);
				continue;
			}

			if (message is JsonArray batch)
			{
				var responses = new JsonArray();

				foreach (JsonNode? item in batch)
				{
					JsonObject? response = item is JsonObject obj ? await HandleAsync(obj).ConfigureAwait(false) : Error(null, -32600, "Invalid request");

					if (response != null)
						responses.Add(response);
				}

				if (responses.Count > 0)
					await output.WriteLineAsync(responses.ToJsonString()).ConfigureAwait(false);

				continue;
			}

			if (message is not JsonObject request)
			{
				await output.WriteLineAsync(Error(null, -32600, "Invalid request").ToJsonString()).ConfigureAwait(false);
				continue;
			}

			JsonObject? reply = await HandleAsync(request).ConfigureAwait(false);

			if (reply != null)
				await output.WriteLineAsync(reply.ToJsonString()).ConfigureAwait(false);
		}
	}

	private static async Task<JsonObject?> HandleAsync(JsonObject request)
	{
		JsonNode? id = request["id"];
		bool isNotification = !request.ContainsKey("id");
		string? method = request["method"] is JsonValue m && m.TryGetValue(out string? s) ? s : null;

		if (method == null)
			return isNotification ? null : Error(id, -32600, "Invalid request");

		JsonObject parameters = request["params"] as JsonObject ?? new JsonObject();

		switch (method)
		{
			case "initialize":
				return Result(id, Initialize(parameters));

			case "ping":
				return isNotification ? null : Result(id, new JsonObject());

			case "tools/list":
				return Result(id, new JsonObject { ["tools"] = McpTools.Definitions() });

			case "tools/call":
				return Result(id, await CallToolAsync(parameters).ConfigureAwait(false));

			case "resources/list":
				return Result(id, new JsonObject { ["resources"] = new JsonArray() });

			case "prompts/list":
				return Result(id, new JsonObject { ["prompts"] = new JsonArray() });

			default:
				return isNotification ? null : Error(id, -32601, "Method not found: " + method);
		}
	}

	private static JsonObject Initialize(JsonObject parameters)
	{
		string requested = parameters["protocolVersion"] is JsonValue v && v.TryGetValue(out string? s) ? s : LatestProtocol;
		string protocol = Array.IndexOf(SupportedProtocols, requested) >= 0 ? requested : LatestProtocol;
		string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

		return new JsonObject
		{
			["protocolVersion"] = protocol,
			["capabilities"] = new JsonObject
			{
				["tools"] = new JsonObject { ["listChanged"] = false }
			},
			["serverInfo"] = new JsonObject
			{
				["name"] = "voidstrap",
				["title"] = "Voidstrap",
				["version"] = version
			},
			["instructions"] = McpTools.Instructions
		};
	}

	private static async Task<JsonObject> CallToolAsync(JsonObject parameters)
	{
		string? name = parameters["name"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

		if (string.IsNullOrWhiteSpace(name))
			return ToolResult("Missing tool name.", true);

		var request = new JsonObject
		{
			["name"] = name,
			["arguments"] = parameters["arguments"] is JsonObject args ? JsonNode.Parse(args.ToJsonString()) : new JsonObject()
		};

		try
		{
			using var pipe = new NamedPipeClientStream(".", McpBridgeServer.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
			await pipe.ConnectAsync(2000).ConfigureAwait(false);

			var encoding = new UTF8Encoding(false);
			using var writer = new StreamWriter(pipe, encoding, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
			using var reader = new StreamReader(pipe, encoding, false, 4096, leaveOpen: true);

			await writer.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);

			Task<string?> readTask = reader.ReadLineAsync();
			Task finished = await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(45))).ConfigureAwait(false);

			if (finished != readTask)
				return ToolResult("Voidstrap did not answer in time.", true);

			string? line = await readTask.ConfigureAwait(false);

			if (line == null || JsonNode.Parse(line) is not JsonObject response)
				return ToolResult("Voidstrap closed the connection without an answer.", true);

			string text = response["text"] is JsonValue t && t.TryGetValue(out string? str) ? str : string.Empty;
			bool isError = response["isError"] is JsonValue e && e.TryGetValue(out bool b) && b;
			return ToolResult(text, isError);
		}
		catch (TimeoutException)
		{
			return ToolResult("Voidstrap is not open. Ask the user to open Voidstrap and the editor they want to work on, then try again.", true);
		}
		catch (Exception ex)
		{
			return ToolResult("Could not reach Voidstrap: " + ex.Message, true);
		}
	}

	private static JsonObject ToolResult(string text, bool isError)
	{
		return new JsonObject
		{
			["content"] = new JsonArray
			{
				new JsonObject { ["type"] = "text", ["text"] = text }
			},
			["isError"] = isError
		};
	}

	private static JsonObject Result(JsonNode? id, JsonObject result)
	{
		return new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id == null ? null : JsonNode.Parse(id.ToJsonString()),
			["result"] = result
		};
	}

	private static JsonObject Error(JsonNode? id, int code, string message)
	{
		return new JsonObject
		{
			["jsonrpc"] = "2.0",
			["id"] = id == null ? null : JsonNode.Parse(id.ToJsonString()),
			["error"] = new JsonObject { ["code"] = code, ["message"] = message }
		};
	}
}
