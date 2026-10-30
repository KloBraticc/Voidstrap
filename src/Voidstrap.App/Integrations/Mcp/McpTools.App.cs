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
	private static McpToolResult GetVoidstrapOverview()
	{
		var sb = new StringBuilder();
		sb.AppendLine($"Voidstrap Version: {App.Version}");
		sb.AppendLine($"Executable: {McpConnectInfo.ExecutablePath()}");
		sb.AppendLine();

		sb.AppendLine("Directories:");
		sb.AppendLine($"  Base: {Paths.Base}");
		sb.AppendLine($"  Config: {Paths.Config}");
		sb.AppendLine($"  Mods: {Paths.Mods}");
		sb.AppendLine($"  CustomThemes (Bootstrapper XML): {Paths.CustomThemes}");
		sb.AppendLine($"  CustomKeystrokeThemes: {Paths.CustomKeystrokeThemes}");
		sb.AppendLine($"  CustomCursors: {Paths.CustomCursors}");
		sb.AppendLine($"  CustomSkybox: {Paths.CustomSkybox}");
		sb.AppendLine($"  Logs: {Paths.Logs}");
		sb.AppendLine($"  RobloxLogs: {Paths.RobloxLogs}");
		sb.AppendLine($"  Versions: {Paths.Versions}");
		sb.AppendLine($"  ServerHistory: {Paths.ServerHistory}");
		sb.AppendLine();

		MainWindow? mainWin = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
		sb.AppendLine("UI State:");
		sb.AppendLine($"  MainWindow Open: {mainWin != null}");
		if (mainWin != null)
			sb.AppendLine($"  Active Page: {mainWin.CurrentPageName}");

		IReadOnlyList<KeyValuePair<string, IMcpEditor>> openEditors = McpEditorRegistry.Snapshot();
		sb.AppendLine(
			$"  Open Editors ({openEditors.Count}): {(openEditors.Count == 0 ? "none" : string.Join(", ", openEditors.Select(e => $"{e.Key} ({e.Value.DisplayName})")))}"
		);
		sb.AppendLine();

		Process[] playerProcs = Process.GetProcessesByName(App.RobloxPlayerAppName);
		Process[] studioProcs = Process.GetProcessesByName(App.RobloxStudioAppName);
		sb.AppendLine("Roblox Runtime:");
		sb.AppendLine($"  Player Running: {playerProcs.Length > 0} (instances: {playerProcs.Length})");
		sb.AppendLine($"  Studio Running: {studioProcs.Length > 0} (instances: {studioProcs.Length})");
		sb.AppendLine($"  Installed Player VersionGuid: {App.State.Prop.Player.VersionGuid}");
		sb.AppendLine($"  Installed Studio VersionGuid: {App.State.Prop.Studio.VersionGuid}");
		foreach (Process p in playerProcs)
			p.Dispose();
		foreach (Process p in studioProcs)
			p.Dispose();
		sb.AppendLine();

		AppSettings s = App.Settings.Prop;
		sb.AppendLine("Key Active Settings:");
		sb.AppendLine($"  App Theme: {s.Theme2} | Backdrop: {s.WindowBackdrop} | Accent: {s.CustomAccentColor ?? "System"}");
		sb.AppendLine(
			$"  Bootstrapper Style: {s.BootstrapperStyle} | SelectedCustomTheme: {s.SelectedCustomTheme ?? "none"} | Title: {s.BootstrapperTitle} | Icon: {s.BootstrapperIcon}"
		);
		sb.AppendLine(
			$"  Keystrokes Overlay: {s.KeystrokesOverlayEnabled} | Theme: {s.KeystrokesThemeName} | Scale: {s.KeystrokesScalePreset}"
		);
		sb.AppendLine(
			$"  RiShade: {s.RiShadeEnabled} | DepthModel: {s.RiShadeDepthModel} | AntiAliasing: {s.AntiAliasingMethodIndex} | MotionBlur: {s.MotionBlurStrengthIndex}"
		);
		sb.AppendLine($"  FrameGen Mode: {s.FrameGenModeIndex} | TargetFps: {s.FrameGenTargetFps} | FpsUnlocker: {s.FpsUnlockerEnabled}");
		sb.AppendLine($"  SnapTap: {s.SnapTapEnabled} ({s.SnapTapKeys}) | DoubleMovement: {s.DoubleMovementEnabled}");
		sb.AppendLine($"  Crosshair: {s.Crosshair} (Color: {s.CrosshairColorHex}, Size: {s.CrosshairSize}) | Clipper: {s.ClipperEnabled}");
		sb.AppendLine(
			$"  Matchmaker: {s.VoidstrapMatchmakerEnabled} | PreferredDatacenter: {s.VoidstrapMatchmakerPreferredDatacenter} | ConnectClosest: {s.ConnectCloset}"
		);
		sb.AppendLine(
			$"  Discord RPC: {s.UseDiscordRichPresence} | ShowServerDetails: {s.ShowServerDetails} | ShowAccount: {s.ShowAccountOnRichPresence}"
		);
		int totalFlags = App.FastFlags.Prop.Count;
		int allowlistedCount = App.FastFlags.Prop.Keys.Count(IsAllowlistedFlag);
		sb.AppendLine($"  Channel: {s.Channel} | Configured FastFlags: {totalFlags} ({allowlistedCount} on Roblox allowlist)");
		sb.AppendLine();

		sb.AppendLine("FastFlag References:");
		sb.AppendLine($"  Roblox Allowlist ({GetEffectiveAllowlist().Count} flags): {RobloxFastFlagAllowlist.AnnouncementUrl}");
		sb.AppendLine("  MaximumADHD FFlag Tracker: https://github.com/MaximumADHD/Roblox-FFlag-Tracker");
		sb.AppendLine("  MaximumADHD Client Tracker: https://github.com/MaximumADHD/Roblox-Client-Tracker");
		sb.AppendLine();

		List<string> bootstrapperThemes = GetInstalledBootstrapperThemes();
		IReadOnlyList<string> keystrokeThemes = KeystrokeThemeManager.GetAllThemeNames();
		sb.AppendLine(
			$"Installed Custom Bootstrapper Themes ({bootstrapperThemes.Count}): {(bootstrapperThemes.Count == 0 ? "none" : string.Join(", ", bootstrapperThemes))}"
		);
		sb.AppendLine($"Available Keystroke Themes ({keystrokeThemes.Count}): {string.Join(", ", keystrokeThemes)}");
		sb.AppendLine();

		sb.AppendLine("Navigable UI Pages (use with navigate_ui):");
		sb.AppendLine(
			"  Home, Integrations, Deployment (or Bootstrapper), Appearance, FastFlags, FastFlagEditor, Mods, More, Global (or GBSEditor), Shortcuts, News, Extensions, Manager (or Downloads), Settings (or Channel), Accounts, Library, PrivateServers"
		);

		return new McpToolResult(sb.ToString().TrimEnd());
	}

	private static McpToolResult NavigateUi(JsonObject args)
	{
		string? pageName = McpArgs.String(args, "page")?.Trim();
		string? search = McpArgs.String(args, "search")?.Trim();
		bool focusWindow = McpArgs.Flag(args, "focusWindow", true);

		MainWindow? mainWin = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
		if (mainWin == null)
			return new McpToolResult("MainWindow is not currently open.", true);

		if (string.IsNullOrWhiteSpace(pageName))
		{
			if (focusWindow)
			{
				if (mainWin.WindowState == System.Windows.WindowState.Minimized)
					mainWin.WindowState = System.Windows.WindowState.Normal;
				mainWin.Activate();
			}
			if (!string.IsNullOrWhiteSpace(search))
				mainWin.NavigateToPageByName(mainWin.CurrentPageName, search, focusWindow);
			return new McpToolResult($"Focused MainWindow on page '{mainWin.CurrentPageName}'.");
		}

		bool ok = mainWin.NavigateToPageByName(pageName, search, focusWindow);
		if (!ok)
			return new McpToolResult(
				$"Could not navigate to '{pageName}'. Valid pages: Home, Integrations, Deployment (or Bootstrapper), Appearance, FastFlags, FastFlagEditor, Mods, More, Global (or GBSEditor), Shortcuts, News, Extensions, Manager (or Downloads), Settings (or Channel), Accounts, Library, PrivateServers.",
				true
			);

		return new McpToolResult(
			$"Navigated MainWindow to '{mainWin.CurrentPageName}'{(string.IsNullOrEmpty(search) ? "" : $" and searched '{search}'")}."
		);
	}

	private static McpToolResult GetLogsAndActivity(JsonObject args)
	{
		string source = McpArgs.String(args, "source")?.Trim().ToLowerInvariant() ?? "all";
		int maxLines = Math.Clamp(McpArgs.Int(args, "maxLines", 120, int.MinValue, int.MaxValue), 10, 500);
		string? filter = McpArgs.String(args, "filter")?.Trim();

		var sb = new StringBuilder();

		if (source is "all" or "voidstrap")
		{
			sb.AppendLine("=== Voidstrap Log History ===");
			IEnumerable<string> lines = App.Logger.History;
			if (!string.IsNullOrEmpty(filter))
				lines = lines.Where(l => l.Contains(filter, StringComparison.OrdinalIgnoreCase));
			foreach (string line in lines.TakeLast(maxLines))
				sb.AppendLine(line);
			sb.AppendLine();
		}

		if (source is "all" or "roblox")
		{
			sb.AppendLine("=== Latest Roblox Log ===");
			if (!string.IsNullOrWhiteSpace(Paths.RobloxLogs) && Directory.Exists(Paths.RobloxLogs))
			{
				FileInfo? latestLog = new DirectoryInfo(Paths.RobloxLogs)
					.GetFiles("*.log")
					.OrderByDescending(f => f.LastWriteTimeUtc)
					.FirstOrDefault();

				if (latestLog != null)
				{
					sb.AppendLine($"File: {latestLog.FullName} (Modified: {latestLog.LastWriteTime:u})");
					try
					{
						using var fs = new FileStream(
							latestLog.FullName,
							FileMode.Open,
							FileAccess.Read,
							FileShare.ReadWrite | FileShare.Delete
						);
						const long tailBytes = 4L * 1024 * 1024;
						if (fs.Length > tailBytes)
						{
							fs.Seek(-tailBytes, SeekOrigin.End);
							sb.AppendLine("(Reading the last 4 MB of a large log)");
						}
						using var reader = new StreamReader(fs, Encoding.UTF8);
						var robloxLines = new List<string>();
						string? line;
						while ((line = reader.ReadLine()) != null)
						{
							if (string.IsNullOrEmpty(filter) || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
								robloxLines.Add(line);
						}
						foreach (string l in robloxLines.TakeLast(maxLines))
							sb.AppendLine(l);
					}
					catch (Exception ex)
					{
						sb.AppendLine($"Could not read Roblox log: {ex.Message}");
					}
				}
				else
				{
					sb.AppendLine("No Roblox log files found.");
				}
			}
			sb.AppendLine();
		}

		if (source is "all" or "history")
		{
			sb.AppendLine("=== Server History ===");
			if (!string.IsNullOrWhiteSpace(Paths.ServerHistory) && File.Exists(Paths.ServerHistory))
			{
				try
				{
					string hist = File.ReadAllText(Paths.ServerHistory);
					sb.AppendLine(hist.Length > 8000 ? hist[..8000] + "\n... (truncated)" : hist);
				}
				catch (Exception ex)
				{
					sb.AppendLine($"Could not read ServerHistory: {ex.Message}");
				}
			}
			else
			{
				sb.AppendLine("No ServerHistory file found.");
			}
		}

		return new McpToolResult(sb.ToString().TrimEnd());
	}
}
