using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.Models.Persistable;
using Voidstrap.UI.Elements.Editor;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
	private static McpToolResult LaunchRoblox(JsonObject args)
	{
		string mode = McpArgs.String(args, "mode")?.Trim().ToLowerInvariant() ?? "player";
		string? placeId = McpArgs.String(args, "placeId")?.Trim();
		string? jobId = McpArgs.String(args, "jobId")?.Trim();

		if (!string.IsNullOrWhiteSpace(placeId) && (!long.TryParse(placeId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long parsedPlace) || parsedPlace <= 0))
			return new McpToolResult("placeId must be a positive number.", true);
		if (!string.IsNullOrWhiteSpace(jobId) && !Guid.TryParse(jobId, out _))
			return new McpToolResult("jobId must be a server id in GUID form.", true);
		if (mode is not ("player" or "place" or "studio" or "close"))
			return new McpToolResult("mode must be 'player', 'place', 'studio', or 'close'.", true);

		if (mode == "close")
		{
			int closed = 0;
			foreach (Process p in Process.GetProcessesByName(App.RobloxPlayerAppName).Concat(Process.GetProcessesByName(App.RobloxStudioAppName)))
			{
				try
				{
					p.Kill();
					closed++;
				}
				catch
				{
				}
				finally
				{
					p.Dispose();
				}
			}
			return new McpToolResult($"Closed {closed} running Roblox process(es).");
		}

		string exe = McpConnectInfo.ExecutablePath();
		var startInfo = new ProcessStartInfo
		{
			FileName = exe,
			UseShellExecute = false
		};

		if (mode == "studio")
		{
			Process[] runningStudio = Process.GetProcessesByName(App.RobloxStudioAppName);
			bool alreadyRunningStudio = runningStudio.Length > 0;
			foreach (Process p in runningStudio)
				p.Dispose();
			if (alreadyRunningStudio)
				return new McpToolResult("Roblox Studio is already running. Multi instance is blocked; close the existing Studio instance first.", true);

			startInfo.ArgumentList.Add("-studio");
			using Process? proc = Process.Start(startInfo);
			return new McpToolResult("Launched Roblox Studio via Voidstrap.");
		}

		Process[] runningPlayer = Process.GetProcessesByName(App.RobloxPlayerAppName);
		bool alreadyRunningPlayer = runningPlayer.Length > 0;
		foreach (Process p in runningPlayer)
			p.Dispose();
		if (alreadyRunningPlayer)
			return new McpToolResult("Roblox Player is already running. Multi instance is blocked; close the existing Roblox instance first or call launch_roblox with mode 'close'.", true);

		startInfo.ArgumentList.Add("-player");
		if (!string.IsNullOrWhiteSpace(placeId))
		{
			string deeplink = $"roblox://experiences/start?placeId={Uri.EscapeDataString(placeId)}";
			if (!string.IsNullOrWhiteSpace(jobId))
				deeplink += $"&gameInstanceId={Uri.EscapeDataString(jobId)}";
			startInfo.ArgumentList.Add(deeplink);
		}

		using Process? playerProc = Process.Start(startInfo);
		return new McpToolResult(string.IsNullOrWhiteSpace(placeId)
			? "Launched Roblox Player via Voidstrap."
			: $"Launched Roblox Player into placeId {placeId}{(string.IsNullOrWhiteSpace(jobId) ? "" : $" (jobId {jobId})")} via Voidstrap.");
	}
}
