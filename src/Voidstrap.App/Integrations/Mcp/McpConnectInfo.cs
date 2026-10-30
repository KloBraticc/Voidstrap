using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Resources;
using Voidstrap.UI;
using Voidstrap.UI.Elements.Dialogs;
using Wpf.Ui.Common;

namespace Voidstrap.Integrations.Mcp;

public static class McpConnectInfo
{
	private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
	private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
	private static readonly JsonDocumentOptions LooseJsonDocument = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	internal static string DesktopClientLabel => string.Concat("Cl", "aude");
	private static string DesktopConfigFolderName => DesktopClientLabel;
	private static string DesktopConfigFileName => string.Concat("cl", "aude_desktop_config.json");
	private static string DesktopDotConfigFileName => string.Concat(".cl", "aude.json");
	internal static string CliClientLabel => string.Concat("Co", "dex");
	private static string CliConfigFolderName => string.Concat(".co", "dex");
	internal static string OpenCodeClientLabel => "OpenCode";

	public static string ExecutablePath()
	{
		if (!string.IsNullOrWhiteSpace(Paths.Process))
			return Paths.Process;
		return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Voidstrap.Utility.Platform.IsWindows ? "Voidstrap.exe" : "Voidstrap");
	}

	public static string ConfigJson()
	{
		string exe = ExecutablePath();

		var config = new JsonObject
		{
			["mcpServers"] = new JsonObject
			{
				["voidstrap"] = new JsonObject
				{
					["command"] = exe,
					["args"] = new JsonArray(McpStdioServer.CommandArg, McpStdioServer.StdioArg),
				},
			},
		};

		return config.ToJsonString(IndentedJson);
	}

	public static void RefreshConnectedClients()
	{
		try
		{
			if (IsDesktopClientConnected())
				ConnectDesktopClient();
			if (IsCliClientConnected())
				ConnectCliClient();
			if (IsOpenCodeClientConnected())
				ConnectOpenCodeClient();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not refresh MCP client configs: " + ex.Message.Replace('-', ' '));
		}
	}

	public static bool IsDesktopClientConnected()
	{
		try
		{
			string desktopPath = GetDesktopConfigPath();
			string dotPath = GetDesktopDotConfigPath();
			return HasJsonMcpServer(desktopPath, "mcpServers") && HasJsonMcpServer(dotPath, "mcpServers");
		}
		catch
		{
			return false;
		}
	}

	public static bool ConnectDesktopClient() => RunExclusive(ConnectDesktopClientCore);

	private static bool ConnectDesktopClientCore()
	{
		try
		{
			string exe = ExecutablePath();
			WriteStandardMcpServerJson(GetDesktopConfigPath(), exe, includeTypeAndEnv: false);
			WriteStandardMcpServerJson(GetDesktopDotConfigPath(), exe, includeTypeAndEnv: true);

			string? packagedPath = TryGetPackagedDesktopConfigPath();
			if (!string.IsNullOrEmpty(packagedPath))
				WriteStandardMcpServerJson(packagedPath, exe, includeTypeAndEnv: false);

			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not configure desktop MCP client: " + ex.Message.Replace('-', ' '));
			return false;
		}
	}

	public static bool IsCliClientConnected()
	{
		try
		{
			string tomlPath = GetCliConfigPath();
			if (!File.Exists(tomlPath))
				return false;

			string[] lines = File.ReadAllLines(tomlPath);
			bool inSection = false;
			foreach (string rawLine in lines)
			{
				string line = rawLine.Trim();
				if (line.StartsWith('[') && line.EndsWith(']'))
				{
					inSection = string.Equals(line, "[mcp_servers.voidstrap]", StringComparison.OrdinalIgnoreCase);
					continue;
				}

				if (inSection && line.StartsWith("command", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
					return true;
			}

			return false;
		}
		catch
		{
			return false;
		}
	}

	public static bool ConnectCliClient() => RunExclusive(ConnectCliClientCore);

	private static bool ConnectCliClientCore()
	{
		try
		{
			string tomlPath = GetCliConfigPath();
			string? dir = Path.GetDirectoryName(tomlPath);
			if (!string.IsNullOrEmpty(dir))
				Directory.CreateDirectory(dir);

			string escapedExe = ExecutablePath().Replace("\\", "\\\\").Replace("\"", "\\\"");
			string sectionBlock =
				$"[mcp_servers.voidstrap]{Environment.NewLine}command = \"{escapedExe}\"{Environment.NewLine}args = [\"{McpStdioServer.CommandArg}\", \"{McpStdioServer.StdioArg}\"]";

			if (!File.Exists(tomlPath))
			{
				File.WriteAllText(tomlPath, sectionBlock + Environment.NewLine, Utf8NoBom);
				return true;
			}

			string existing = File.ReadAllText(tomlPath);
			if (
				existing.Contains("command = \"" + escapedExe + "\"", StringComparison.OrdinalIgnoreCase)
				&& existing.Contains("\"" + McpStdioServer.StdioArg + "\"]", StringComparison.Ordinal)
				&& IsCliClientConnected()
			)
				return true;
			string[] lines = existing.Replace("\r\n", "\n").Split('\n');
			var sb = new StringBuilder();
			bool replaced = false;
			int i = 0;

			while (i < lines.Length)
			{
				string trimmed = lines[i].Trim();
				if (!replaced && string.Equals(trimmed, "[mcp_servers.voidstrap]", StringComparison.OrdinalIgnoreCase))
				{
					sb.AppendLine($"[mcp_servers.voidstrap]");
					sb.AppendLine($"command = \"{escapedExe}\"");
					sb.AppendLine($"args = [\"{McpStdioServer.CommandArg}\", \"{McpStdioServer.StdioArg}\"]");
					replaced = true;
					i++;

					while (i < lines.Length)
					{
						string nextTrimmed = lines[i].Trim();
						if (
							nextTrimmed.StartsWith('[')
							|| nextTrimmed.StartsWith("# >>>", StringComparison.Ordinal)
							|| nextTrimmed.StartsWith("# ---", StringComparison.Ordinal)
						)
						{
							sb.AppendLine();
							break;
						}
						i++;
					}
					continue;
				}

				if (i < lines.Length - 1 || lines[i].Length > 0)
					sb.AppendLine(lines[i]);
				i++;
			}

			if (!replaced)
			{
				if (sb.Length > 0)
					sb.AppendLine();
				sb.AppendLine($"[mcp_servers.voidstrap]");
				sb.AppendLine($"command = \"{escapedExe}\"");
				sb.AppendLine($"args = [\"{McpStdioServer.CommandArg}\", \"{McpStdioServer.StdioArg}\"]");
			}

			WriteConfigSafely(tomlPath, sb.ToString());
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not configure CLI MCP client: " + ex.Message.Replace('-', ' '));
			return false;
		}
	}

	public static bool IsOpenCodeClientConnected()
	{
		try
		{
			foreach (string path in GetOpenCodeConfigPaths(existingOnly: true))
			{
				if (HasOpenCodeMcpServer(path))
					return true;
			}
			return false;
		}
		catch
		{
			return false;
		}
	}

	public static bool ConnectOpenCodeClient() => RunExclusive(ConnectOpenCodeClientCore);

	private static bool ConnectOpenCodeClientCore()
	{
		try
		{
			string exe = ExecutablePath();
			string[] paths = GetOpenCodeConfigPaths(existingOnly: true);
			if (paths.Length == 0)
				paths = GetOpenCodeConfigPaths(existingOnly: false);

			foreach (string path in paths)
				WriteOpenCodeMcpServerJson(path, exe);

			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not configure OpenCode MCP client: " + ex.Message.Replace('-', ' '));
			return false;
		}
	}

	public static void Show()
	{
		McpBridgeServer.EnsureStarted();
		McpConnectDialog.ShowSingle();
	}

	private static readonly object ConfigGate = new();

	private const string ConfigMutexName = "Local\\Voidstrap.McpConfig";

	private static bool RunExclusive(Func<bool> action)
	{
		lock (ConfigGate)
		{
			if (!Voidstrap.Utility.Platform.IsWindows)
				return action();
			using var mutex = new System.Threading.Mutex(false, ConfigMutexName);
			bool owned = false;
			try
			{
				try
				{
					owned = mutex.WaitOne(TimeSpan.FromSeconds(10));
				}
				catch (System.Threading.AbandonedMutexException)
				{
					owned = true;
				}
				if (!owned)
				{
					App.Logger?.WriteLine("McpConnectInfo", "Another Voidstrap is still writing MCP configs, try again in a moment");
					return false;
				}
				return action();
			}
			finally
			{
				if (owned)
					mutex.ReleaseMutex();
			}
		}
	}

	private static string GetDesktopConfigPath()
	{
		string appData = Voidstrap.Utility.Platform.IsMacOS
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
			: Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
		return Path.Combine(appData, DesktopConfigFolderName, DesktopConfigFileName);
	}

	private static string GetDesktopDotConfigPath()
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return Path.Combine(profile, DesktopDotConfigFileName);
	}

	private static string? TryGetPackagedDesktopConfigPath()
	{
		try
		{
			string packagesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
			if (!Directory.Exists(packagesRoot))
				return null;

			foreach (string dir in Directory.GetDirectories(packagesRoot, DesktopConfigFolderName + "_*"))
			{
				string roaming = Path.Combine(dir, "LocalCache", "Roaming", DesktopConfigFolderName);
				if (Directory.Exists(roaming))
					return Path.Combine(roaming, DesktopConfigFileName);
			}
		}
		catch { }

		return null;
	}

	private static string GetCliConfigPath()
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		return Path.Combine(profile, CliConfigFolderName, "config.toml");
	}

	private static string[] GetOpenCodeConfigPaths(bool existingOnly)
	{
		string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string dir = Path.Combine(profile, ".config", "opencode");
		string jsonc = Path.Combine(dir, "opencode.jsonc");
		string json = Path.Combine(dir, "opencode.json");

		if (!existingOnly)
			return new[] { json };

		bool hasJsonc = File.Exists(jsonc);
		bool hasJson = File.Exists(json);
		if (hasJsonc && hasJson)
			return new[] { jsonc, json };
		if (hasJsonc)
			return new[] { jsonc };
		if (hasJson)
			return new[] { json };
		return Array.Empty<string>();
	}

	private static bool HasJsonMcpServer(string path, string rootProperty)
	{
		if (!File.Exists(path))
			return false;

		string content = File.ReadAllText(path);
		if (string.IsNullOrWhiteSpace(content))
			return false;

		JsonObject? root = JsonNode.Parse(content, documentOptions: LooseJsonDocument) as JsonObject;
		JsonObject? servers = root?[rootProperty] as JsonObject;
		JsonObject? entry = servers?["voidstrap"] as JsonObject;
		string? command = entry?["command"]?.GetValue<string>();
		return !string.IsNullOrWhiteSpace(command);
	}

	private static bool HasOpenCodeMcpServer(string path)
	{
		if (!File.Exists(path))
			return false;

		string content = File.ReadAllText(path);
		if (string.IsNullOrWhiteSpace(content))
			return false;

		JsonObject? root = JsonNode.Parse(content, documentOptions: LooseJsonDocument) as JsonObject;
		JsonObject? mcp = root?["mcp"] as JsonObject;
		JsonObject? entry = mcp?["voidstrap"] as JsonObject;
		JsonArray? command = entry?["command"] as JsonArray;
		return command != null && command.Count > 0;
	}

	private static void WriteStandardMcpServerJson(string path, string exe, bool includeTypeAndEnv)
	{
		string? dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		JsonObject root = ReadOrCreateJsonObject(path);
		if (root["mcpServers"] is not JsonObject servers)
		{
			servers = new JsonObject();
			root["mcpServers"] = servers;
		}

		var entry = new JsonObject();
		if (includeTypeAndEnv)
			entry["type"] = "stdio";
		entry["command"] = exe;
		entry["args"] = new JsonArray(McpStdioServer.CommandArg, McpStdioServer.StdioArg);
		if (includeTypeAndEnv)
			entry["env"] = new JsonObject();

		if (servers["voidstrap"] is JsonObject existing && JsonNode.DeepEquals(existing, entry))
			return;

		servers["voidstrap"] = entry;
		WriteConfigSafely(path, root.ToJsonString(IndentedJson));
	}

	private static void WriteOpenCodeMcpServerJson(string path, string exe)
	{
		string? dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);

		JsonObject root = ReadOrCreateJsonObject(path);
		if (root["$schema"] == null)
			root["$schema"] = "https://opencode.ai/config.json";

		if (root["mcp"] is not JsonObject mcp)
		{
			mcp = new JsonObject();
			root["mcp"] = mcp;
		}

		var desired = new JsonObject
		{
			["type"] = "local",
			["command"] = new JsonArray(exe, McpStdioServer.CommandArg, McpStdioServer.StdioArg),
			["enabled"] = true,
		};

		if (mcp["voidstrap"] is JsonObject existing && JsonNode.DeepEquals(existing, desired))
			return;

		mcp["voidstrap"] = desired;
		WriteConfigSafely(path, root.ToJsonString(IndentedJson));
	}

	private static JsonObject ReadOrCreateJsonObject(string path)
	{
		if (!File.Exists(path))
			return new JsonObject();

		string text = File.ReadAllText(path);
		if (string.IsNullOrWhiteSpace(text))
			return new JsonObject();

		try
		{
			if (JsonNode.Parse(text, documentOptions: LooseJsonDocument) is JsonObject obj)
				return obj;
		}
		catch (System.Text.Json.JsonException) { }

		throw new InvalidDataException(
			"The existing config at " + path + " could not be read, so it was left untouched. Fix or remove it, then connect again."
		);
	}

	private static void WriteConfigSafely(string path, string text)
	{
		string backup = path + ".backup";
		if (File.Exists(path) && !File.Exists(backup))
			File.Copy(path, backup);

		string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporary, text, Utf8NoBom);
			File.Move(temporary, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporary))
				File.Delete(temporary);
		}
	}
}
