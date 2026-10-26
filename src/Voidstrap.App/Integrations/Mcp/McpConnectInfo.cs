using System;
using System.Text.Json.Nodes;
using System.Windows;
using Voidstrap.Resources;

namespace Voidstrap.Integrations.Mcp;

public static class McpConnectInfo
{
	public static string ConfigJson()
	{
		string exe = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Voidstrap.exe");

		var config = new JsonObject
		{
			["mcpServers"] = new JsonObject
			{
				["voidstrap"] = new JsonObject
				{
					["command"] = exe,
					["args"] = new JsonArray(McpStdioServer.CommandArg)
				}
			}
		};

		return config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
	}

	public static void Show()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
			return;

		McpBridgeServer.EnsureStarted();

		string json = ConfigJson();
		bool copied = false;

		try
		{
			Clipboard.SetText(json);
			copied = true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("McpConnectInfo", "Could not copy the MCP config: " + ex.Message.Replace('-', ' '));
		}

		string template = copied ? Strings.Ui_Mcp_ConnectMessageCopied : Strings.Ui_Mcp_ConnectMessage;
		string message = string.Format(template, json);
		Frontend.ShowMessageBox(message, MessageBoxImage.Information);
	}
}
