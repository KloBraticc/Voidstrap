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

public sealed record McpToolResult(string Text, bool IsError = false);

public static class McpTools
{
	private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

	public static McpToolResult Execute(string name, JsonObject args)
	{
		try
		{
			return name switch
			{
				"get_voidstrap_overview" => GetVoidstrapOverview(),
				"get_settings" => GetSettings(args),
				"set_settings" => SetSettings(args),
				"list_fastflags" => ListFastFlags(args),
				"set_fastflags" => SetFastFlags(args),
				"list_editors" => ListEditors(),
				"open_editor" => OpenEditor(args),
				"get_editor_code" => GetEditorCode(args),
				"set_editor_code" => SetEditorCode(args),
				"replace_in_editor" => ReplaceInEditor(args),
				"save_editor" => SaveEditor(args),
				"navigate_ui" => NavigateUi(args),
				"manage_themes_and_mods" => ManageThemesAndMods(args),
				"launch_roblox" => LaunchRoblox(args),
				"get_logs_and_activity" => GetLogsAndActivity(args),
				_ => new McpToolResult($"Unknown tool: {name}", true)
			};
		}
		catch (Exception ex)
		{
			return new McpToolResult($"Tool failed: {ex.Message}", true);
		}
	}

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
		sb.AppendLine($"  Open Editors ({openEditors.Count}): {(openEditors.Count == 0 ? "none" : string.Join(", ", openEditors.Select(e => $"{e.Key} ({e.Value.DisplayName})")))}");
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
		sb.AppendLine($"  Bootstrapper Style: {s.BootstrapperStyle} | SelectedCustomTheme: {s.SelectedCustomTheme ?? "none"} | Title: {s.BootstrapperTitle} | Icon: {s.BootstrapperIcon}");
		sb.AppendLine($"  Keystrokes Overlay: {s.KeystrokesOverlayEnabled} | Theme: {s.KeystrokesThemeName} | Scale: {s.KeystrokesScalePreset}");
		sb.AppendLine($"  RiShade: {s.RiShadeEnabled} | DepthModel: {s.RiShadeDepthModel} | AntiAliasing: {s.AntiAliasingMethodIndex} | MotionBlur: {s.MotionBlurStrengthIndex}");
		sb.AppendLine($"  FrameGen Mode: {s.FrameGenModeIndex} | TargetFps: {s.FrameGenTargetFps} | FpsUnlocker: {s.FpsUnlockerEnabled}");
		sb.AppendLine($"  SnapTap: {s.SnapTapEnabled} ({s.SnapTapKeys}) | DoubleMovement: {s.DoubleMovementEnabled}");
		sb.AppendLine($"  Crosshair: {s.Crosshair} (Color: {s.CrosshairColorHex}, Size: {s.CrosshairSize}) | Clipper: {s.ClipperEnabled}");
		sb.AppendLine($"  Matchmaker: {s.VoidstrapMatchmakerEnabled} | PreferredDatacenter: {s.VoidstrapMatchmakerPreferredDatacenter} | ConnectClosest: {s.ConnectCloset}");
		sb.AppendLine($"  Discord RPC: {s.UseDiscordRichPresence} | ShowServerDetails: {s.ShowServerDetails} | ShowAccount: {s.ShowAccountOnRichPresence}");
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
		sb.AppendLine($"Installed Custom Bootstrapper Themes ({bootstrapperThemes.Count}): {(bootstrapperThemes.Count == 0 ? "none" : string.Join(", ", bootstrapperThemes))}");
		sb.AppendLine($"Available Keystroke Themes ({keystrokeThemes.Count}): {string.Join(", ", keystrokeThemes)}");
		sb.AppendLine();

		sb.AppendLine("Navigable UI Pages (use with navigate_ui):");
		sb.AppendLine("  Home, Integrations, Deployment (or Bootstrapper), Appearance, FastFlags, FastFlagEditor, Mods, More, Global (or GBSEditor), Shortcuts, News, Extensions, Manager (or Downloads), Settings (or Channel), Accounts, Library, PrivateServers");

		return new McpToolResult(sb.ToString().TrimEnd());
	}

	private static McpToolResult GetSettings(JsonObject args)
	{
		string? filter = args["filter"]?.GetValue<string>()?.Trim();
		bool includeSchema = args["includeSchema"]?.GetValue<bool>() ?? true;

		var result = new JsonObject();
		var settingsObj = new JsonObject();
		AppSettings instance = App.Settings.Prop;

		PropertyInfo[] props = typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
		foreach (PropertyInfo prop in props.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
		{
			if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || IsBlockedSetting(prop.Name))
				continue;

			if (!string.IsNullOrEmpty(filter) && !prop.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
				continue;

			object? val;
			try
			{
				val = prop.GetValue(instance);
			}
			catch
			{
				continue;
			}

			if (!includeSchema)
			{
				settingsObj[prop.Name] = ToJsonNode(val);
				continue;
			}

			var entry = new JsonObject
			{
				["value"] = ToJsonNode(val),
				["type"] = DescribeType(prop.PropertyType),
				["writable"] = prop.CanWrite
			};

			Type coreType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
			if (coreType.IsEnum)
			{
				var enumVals = new JsonArray();
				foreach (string name in Enum.GetNames(coreType))
					enumVals.Add(name);
				entry["enumValues"] = enumVals;
			}

			settingsObj[prop.Name] = entry;
		}

		result["settings"] = settingsObj;

		var gbsObj = new JsonObject();
		foreach (KeyValuePair<string, GBSProperty> kv in GBSEditor.KnownProperties)
		{
			if (!string.IsNullOrEmpty(filter) && !kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) && !"globalbasicsettings".Contains(filter, StringComparison.OrdinalIgnoreCase))
				continue;

			var gbsEntry = new JsonObject
			{
				["value"] = App.GlobalSettings.GetProperty(kv.Key),
				["type"] = kv.Value.Tag,
				["min"] = kv.Value.Minimum,
				["max"] = kv.Value.Maximum
			};
			gbsObj[kv.Key] = gbsEntry;
		}

		result["globalBasicSettings"] = gbsObj;
		return new McpToolResult(result.ToJsonString(IndentedJson));
	}

	private static McpToolResult SetSettings(JsonObject args)
	{
		JsonObject? settingsNode = args["settings"] as JsonObject;
		JsonObject? gbsNode = args["globalSettings"] as JsonObject;

		if ((settingsNode == null || settingsNode.Count == 0) && (gbsNode == null || gbsNode.Count == 0))
			return new McpToolResult("Provide a non-empty 'settings' and/or 'globalSettings' object.", true);

		var updated = new List<string>();
		var errors = new List<string>();
		bool keystrokeChanged = false;

		if (settingsNode != null && settingsNode.Count > 0)
		{
			AppSettings instance = App.Settings.Prop;
			Dictionary<string, PropertyInfo> propMap = typeof(AppSettings)
				.GetProperties(BindingFlags.Public | BindingFlags.Instance)
				.Where(p => p.CanWrite && p.GetIndexParameters().Length == 0 && !IsBlockedSetting(p.Name))
				.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

			foreach (KeyValuePair<string, JsonNode?> kv in settingsNode)
			{
				if (IsBlockedSetting(kv.Key))
				{
					errors.Add($"Setting '{kv.Key}' is blocked because multi instance is disabled in Voidstrap");
					continue;
				}

				if (!propMap.TryGetValue(kv.Key, out PropertyInfo? prop))
				{
					errors.Add($"Unknown or read-only setting '{kv.Key}'");
					continue;
				}

				if (!TryConvertJsonNode(kv.Value, prop.PropertyType, out object? converted, out string? err))
				{
					errors.Add($"{prop.Name}: {err}");
					continue;
				}

				try
				{
					prop.SetValue(instance, converted);
					updated.Add($"{prop.Name} = {kv.Value?.ToJsonString() ?? "null"}");
					if (prop.Name.StartsWith("Keystrokes", StringComparison.OrdinalIgnoreCase) || string.Equals(prop.Name, nameof(AppSettings.SelectedCustomKeystrokeTheme), StringComparison.OrdinalIgnoreCase))
						keystrokeChanged = true;
				}
				catch (Exception ex)
				{
					errors.Add($"{prop.Name}: {ex.Message}");
				}
			}

			App.Settings.Save();
		}

		if (gbsNode != null && gbsNode.Count > 0)
		{
			foreach (KeyValuePair<string, JsonNode?> kv in gbsNode)
			{
				object? rawVal = ExtractPrimitive(kv.Value);
				bool ok;
				if (App.GlobalSettings.PresetPaths.ContainsKey(kv.Key))
					ok = App.GlobalSettings.SetPreset(kv.Key, rawVal);
				else
					ok = App.GlobalSettings.SetProperty(kv.Key, rawVal);

				if (ok)
					updated.Add($"GlobalBasicSettings.{kv.Key} = {rawVal ?? "null"}");
				else
					errors.Add($"GlobalBasicSettings.{kv.Key}: invalid property name or out-of-range value");
			}

			try
			{
				App.GlobalSettings.Save();
			}
			catch (Exception ex)
			{
				errors.Add($"GlobalBasicSettings.Save: {ex.Message}");
			}
		}

		if (keystrokeChanged)
			KeystrokeThemeManager.NotifyThemeChanged();

		MainWindow? mainWin = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
		mainWin?.RefreshCurrentPage();

		var sb = new StringBuilder();
		if (updated.Count > 0)
		{
			sb.AppendLine($"Updated and saved {updated.Count} setting(s):");
			foreach (string item in updated)
				sb.AppendLine($"  {item}");
		}
		if (errors.Count > 0)
		{
			sb.AppendLine($"Errors ({errors.Count}):");
			foreach (string err in errors)
				sb.AppendLine($"  {err}");
		}

		return new McpToolResult(sb.ToString().TrimEnd(), errors.Count > 0 && updated.Count == 0);
	}

	private static McpToolResult ListEditors()
	{
		IReadOnlyList<KeyValuePair<string, IMcpEditor>> editors = McpEditorRegistry.Snapshot();
		var sb = new StringBuilder();

		sb.AppendLine($"Open Editors ({editors.Count}):");
		if (editors.Count == 0)
		{
			sb.AppendLine("  (none currently open; you can open any editor on screen using open_editor)");
		}
		else
		{
			foreach (KeyValuePair<string, IMcpEditor> kv in editors)
				sb.AppendLine($"  id: {kv.Key} | kind: {kv.Value.Kind} | name: {kv.Value.DisplayName} | language: {kv.Value.Language}");
		}

		sb.AppendLine();
		List<string> bootstrapperThemes = GetInstalledBootstrapperThemes();
		sb.AppendLine($"Installed Custom Bootstrapper Themes ({bootstrapperThemes.Count}): {(bootstrapperThemes.Count == 0 ? "none" : string.Join(", ", bootstrapperThemes))}");
		IReadOnlyList<string> keystrokeThemes = KeystrokeThemeManager.GetAllThemeNames();
		sb.AppendLine($"Available Keystroke Themes ({keystrokeThemes.Count}): {string.Join(", ", keystrokeThemes)}");

		return new McpToolResult(sb.ToString().TrimEnd());
	}

	private static McpToolResult OpenEditor(JsonObject args)
	{
		string type = args["type"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? string.Empty;
		string name = args["name"]?.GetValue<string>()?.Trim() ?? string.Empty;
		bool activate = args["activate"]?.GetValue<bool>() ?? true;

		if (string.IsNullOrEmpty(type))
			return new McpToolResult("Missing 'type' argument. Use 'bootstrapper', 'keystroke', or 'fastflags'.", true);

		if (type is "fastflags" or "fastflag" or "fastflageditor")
		{
			MainWindow? mainWin = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
			if (mainWin == null)
				return new McpToolResult("MainWindow is not open.", true);

			mainWin.NavigateToPageByName("FastFlagEditor", null, focusWindow: true);
			KeyValuePair<string, IMcpEditor>? ffEntry = McpEditorRegistry.FindEntry("fastflags");
			return new McpToolResult($"Opened FastFlag Editor in MainWindow. Editor ID: {ffEntry?.Key ?? "fastflags"}");
		}

		if (type is "bootstrapper" or "customtheme" or "theme")
		{
			if (string.IsNullOrWhiteSpace(name))
				name = App.Settings.Prop.SelectedCustomTheme ?? "MyCustomTheme";

			name = Path.GetFileName(name.Trim());
			EnsureBootstrapperThemeExists(name, null);

			if (activate)
			{
				App.Settings.Prop.SelectedCustomTheme = name;
				App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
				App.Settings.Save();
			}

			BootstrapperEditorWindow? existing = Application.Current?.Windows.OfType<BootstrapperEditorWindow>()
				.FirstOrDefault(w => string.Equals(((IMcpEditor)w).DisplayName, name, StringComparison.OrdinalIgnoreCase));

			if (existing != null)
			{
				if (existing.WindowState == System.Windows.WindowState.Minimized)
					existing.WindowState = System.Windows.WindowState.Normal;
				existing.Activate();
				KeyValuePair<string, IMcpEditor>? existingEntry = McpEditorRegistry.FindEntry(name);
				return new McpToolResult($"Focused existing Bootstrapper Editor for '{name}'. Editor ID: {existingEntry?.Key ?? name}");
			}

			var window = new BootstrapperEditorWindow(name);
			window.Show();
			window.Activate();
			KeyValuePair<string, IMcpEditor>? openedEntry = McpEditorRegistry.FindEntry(name);
			return new McpToolResult($"Opened Bootstrapper Editor for '{name}'. Editor ID: {openedEntry?.Key ?? name}");
		}

		if (type is "keystroke" or "keystrokes")
		{
			if (string.IsNullOrWhiteSpace(name))
				name = App.Settings.Prop.KeystrokesThemeName ?? "MyKeystrokeTheme";

			string? baseTheme = args["baseTheme"]?.GetValue<string>()?.Trim();
			EnsureKeystrokeThemeExists(name, baseTheme);

			if (activate)
			{
				App.Settings.Prop.KeystrokesThemeName = name;
				App.Settings.Prop.SelectedCustomKeystrokeTheme = name;
				App.Settings.Prop.KeystrokesOverlayEnabled = true;
				App.Settings.Save();
				KeystrokeThemeManager.NotifyThemeChanged();
			}

			KeystrokeThemeEditorWindow? existing = Application.Current?.Windows.OfType<KeystrokeThemeEditorWindow>()
				.FirstOrDefault(w => string.Equals(((IMcpEditor)w).DisplayName, name, StringComparison.OrdinalIgnoreCase));

			if (existing != null)
			{
				if (existing.WindowState == System.Windows.WindowState.Minimized)
					existing.WindowState = System.Windows.WindowState.Normal;
				existing.Activate();
				KeyValuePair<string, IMcpEditor>? existingEntry = McpEditorRegistry.FindEntry(name);
				return new McpToolResult($"Focused existing Keystroke Theme Editor for '{name}'. Editor ID: {existingEntry?.Key ?? name}");
			}

			var window = new KeystrokeThemeEditorWindow(name);
			window.Show();
			window.Activate();
			KeyValuePair<string, IMcpEditor>? openedEntry = McpEditorRegistry.FindEntry(name);
			return new McpToolResult($"Opened Keystroke Theme Editor for '{name}'. Editor ID: {openedEntry?.Key ?? name}");
		}

		return new McpToolResult($"Unsupported editor type '{type}'. Use 'bootstrapper', 'keystroke', or 'fastflags'.", true);
	}

	private static McpToolResult GetEditorCode(JsonObject args)
	{
		string? id = args["editorId"]?.GetValue<string>();
		KeyValuePair<string, IMcpEditor>? found = McpEditorRegistry.FindEntry(id);
		if (found is { } entry)
		{
			IMcpEditor editor = entry.Value;
			var sb = new StringBuilder();
			sb.AppendLine($"Editor: {entry.Key} ({editor.DisplayName})");
			sb.AppendLine($"Kind: {editor.Kind}");
			sb.AppendLine($"Language: {editor.Language}");
			sb.AppendLine($"Format guide: {editor.Guidance}");
			sb.AppendLine("=== CODE START ===");
			sb.AppendLine(editor.GetCode());
			sb.AppendLine("=== CODE END ===");
			return new McpToolResult(sb.ToString());
		}

		if (!string.IsNullOrWhiteSpace(id))
		{
			string clean = id.Trim();
			if (clean.StartsWith("bootstrapper:", StringComparison.OrdinalIgnoreCase))
				clean = clean["bootstrapper:".Length..];
			string xmlPath = Path.Combine(Paths.CustomThemes, clean, "Theme.xml");
			if (File.Exists(xmlPath))
				return new McpToolResult($"Theme on disk ({clean}):\n=== CODE START ===\n{File.ReadAllText(xmlPath)}\n=== CODE END ===");
		}

		return new McpToolResult(NoEditorError(id), true);
	}

	private static McpToolResult SetEditorCode(JsonObject args)
	{
		string? id = args["editorId"]?.GetValue<string>();
		string? code = args["code"]?.GetValue<string>();
		if (code == null)
			return new McpToolResult("Missing 'code' argument.", true);

		KeyValuePair<string, IMcpEditor>? found = McpEditorRegistry.FindEntry(id);
		if (found is not { } entry)
			return new McpToolResult(NoEditorError(id), true);

		McpEditResult res = entry.Value.SetCode(code);
		if (!res.Success)
			return new McpToolResult($"Failed to apply code in {entry.Key}: {res.Message}", true);

		return new McpToolResult($"Updated {entry.Key}: {res.Message}");
	}

	private static McpToolResult ReplaceInEditor(JsonObject args)
	{
		string? id = args["editorId"]?.GetValue<string>();
		string? oldText = args["oldText"]?.GetValue<string>();
		string? newText = args["newText"]?.GetValue<string>();
		if (string.IsNullOrEmpty(oldText) || newText == null)
			return new McpToolResult("Both 'oldText' and 'newText' arguments are required.", true);

		KeyValuePair<string, IMcpEditor>? found = McpEditorRegistry.FindEntry(id);
		if (found is not { } entry)
			return new McpToolResult(NoEditorError(id), true);

		string current = entry.Value.GetCode();
		int index = current.IndexOf(oldText, StringComparison.Ordinal);
		if (index < 0)
		{
			string normalizedCurrent = current.Replace("\r\n", "\n");
			string normalizedOld = oldText.Replace("\r\n", "\n");
			int normIndex = normalizedCurrent.IndexOf(normalizedOld, StringComparison.Ordinal);
			if (normIndex < 0)
				return new McpToolResult($"oldText was not found in {entry.Key}. Call get_editor_code to inspect current contents.", true);

			string updatedNorm = normalizedCurrent[..normIndex] + newText.Replace("\r\n", "\n") + normalizedCurrent[(normIndex + normalizedOld.Length)..];
			McpEditResult normRes = entry.Value.SetCode(updatedNorm);
			if (!normRes.Success)
				return new McpToolResult($"Failed to apply replacement in {entry.Key}: {normRes.Message}", true);
			return new McpToolResult($"Replaced snippet in {entry.Key}: {normRes.Message}");
		}

		string updated = current[..index] + newText + current[(index + oldText.Length)..];
		McpEditResult res = entry.Value.SetCode(updated);
		if (!res.Success)
			return new McpToolResult($"Failed to apply replacement in {entry.Key}: {res.Message}", true);

		return new McpToolResult($"Replaced snippet in {entry.Key}: {res.Message}");
	}

	private static McpToolResult SaveEditor(JsonObject args)
	{
		string? id = args["editorId"]?.GetValue<string>();
		KeyValuePair<string, IMcpEditor>? found = McpEditorRegistry.FindEntry(id);
		if (found is not { } entry)
			return new McpToolResult(NoEditorError(id), true);

		McpEditResult res = entry.Value.Save();
		if (!res.Success)
			return new McpToolResult($"Save failed in {entry.Key}: {res.Message}", true);

		return new McpToolResult($"Saved {entry.Key}: {res.Message}");
	}

	private static McpToolResult ListFastFlags(JsonObject args)
	{
		string? filter = args["filter"]?.GetValue<string>()?.Trim();
		bool includePresets = args["includePresets"]?.GetValue<bool>() ?? true;
		bool includeAllowlist = args["includeAllowlist"]?.GetValue<bool>() ?? true;

		IReadOnlyDictionary<string, string> allowlist = GetEffectiveAllowlist();
		Dictionary<string, TrackerFlagEntry>? trackerCache = _trackerFlagsCache;

		Dictionary<string, object> prop = App.FastFlags.Prop;
		IEnumerable<KeyValuePair<string, object>> query = prop;
		if (!string.IsNullOrEmpty(filter))
			query = query.Where(kv => kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase));

		var root = new JsonObject();
		var flagsObj = new JsonObject();
		foreach (KeyValuePair<string, object> kv in query.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
		{
			bool isAllowed = allowlist.TryGetValue(kv.Key, out string? category);
			var entry = new JsonObject
			{
				["value"] = kv.Value?.ToString() ?? string.Empty,
				["isAllowlisted"] = isAllowed,
				["allowlistCategory"] = category
			};

			if (trackerCache != null)
			{
				bool inTracker = trackerCache.TryGetValue(kv.Key, out TrackerFlagEntry? trackerInfo);
				entry["existsInTracker"] = inTracker;
				if (inTracker && trackerInfo != null)
				{
					entry["trackerSource"] = trackerInfo.Source;
					entry["defaultValue"] = trackerInfo.DefaultValue;
				}
			}

			flagsObj[kv.Key] = entry;
		}

		root["configuredFlags"] = flagsObj;

		if (includeAllowlist)
		{
			var allowlistFlagsObj = new JsonObject();
			foreach (KeyValuePair<string, string> kv in allowlist.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrEmpty(filter) && !kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) && !kv.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
					continue;
				allowlistFlagsObj[kv.Key] = kv.Value;
			}

			root["robloxAllowlist"] = new JsonObject
			{
				["announcementUrl"] = RobloxFastFlagAllowlist.AnnouncementUrl,
				["jsonFeedUrl"] = RobloxFastFlagAllowlist.AnnouncementUrl + ".json",
				["count"] = allowlist.Count,
				["flags"] = allowlistFlagsObj,
				["recommendedProfiles"] = new JsonObject
				{
					["Performance"] = new JsonObject
					{
						["DFFlagTextureQualityOverrideEnabled"] = "True",
						["DFIntTextureQualityOverride"] = "0",
						["FIntDebugForceMSAASamples"] = "1"
					},
					["Balanced"] = new JsonObject
					{
						["DFFlagTextureQualityOverrideEnabled"] = "True",
						["DFIntTextureQualityOverride"] = "2",
						["FIntDebugForceMSAASamples"] = "2"
					},
					["Quality"] = new JsonObject
					{
						["DFFlagTextureQualityOverrideEnabled"] = "True",
						["DFIntTextureQualityOverride"] = "3",
						["FIntDebugForceMSAASamples"] = "4"
					}
				}
			};

			var sourcesObj = new JsonObject();
			foreach (var src in TrackerSources)
				sourcesObj[src.Name] = src.Url;

			root["maximumAdhdTracker"] = new JsonObject
			{
				["trackerRepository"] = "https://github.com/MaximumADHD/Roblox-FFlag-Tracker",
				["clientTrackerRepository"] = "https://github.com/MaximumADHD/Roblox-Client-Tracker",
				["cachedFlagCount"] = trackerCache?.Count ?? 0,
				["sources"] = sourcesObj
			};
		}

		if (includePresets)
		{
			var presetsObj = new JsonObject();
			foreach (KeyValuePair<string, string> preset in FastFlagManager.PresetFlags.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
			{
				if (!string.IsNullOrEmpty(filter) && !preset.Key.Contains(filter, StringComparison.OrdinalIgnoreCase) && !preset.Value.Contains(filter, StringComparison.OrdinalIgnoreCase))
					continue;

				presetsObj[preset.Key] = new JsonObject
				{
					["flag"] = preset.Value,
					["isAllowlisted"] = allowlist.ContainsKey(preset.Value),
					["currentValue"] = App.FastFlags.GetPreset(preset.Key)
				};
			}
			root["availablePresets"] = presetsObj;
		}

		return new McpToolResult(root.ToJsonString(IndentedJson));
	}

	private static McpToolResult SetFastFlags(JsonObject args)
	{
		JsonObject? flags = args["flags"] as JsonObject;
		JsonObject? presets = args["presets"] as JsonObject;
		string? recommendedProfile = args["recommendedProfile"]?.GetValue<string>()?.Trim();
		bool replaceAll = args["replaceAll"]?.GetValue<bool>() ?? false;

		if ((flags == null || flags.Count == 0) && (presets == null || presets.Count == 0) && string.IsNullOrWhiteSpace(recommendedProfile) && !replaceAll)
			return new McpToolResult("Provide 'flags', 'presets', 'recommendedProfile' ('Performance', 'Balanced', 'Quality'), or set 'replaceAll' to true.", true);

		int setCount = 0;
		int removedCount = 0;
		int allowlistedSetCount = 0;
		IReadOnlyDictionary<string, string> allowlist = GetEffectiveAllowlist();

		if (replaceAll)
		{
			removedCount += App.FastFlags.Prop.Count;
			App.FastFlags.Prop.Clear();
		}

		if (!string.IsNullOrWhiteSpace(recommendedProfile))
		{
			Dictionary<string, string>? profileFlags = recommendedProfile.ToLowerInvariant() switch
			{
				"performance" => new Dictionary<string, string>
				{
					["DFFlagTextureQualityOverrideEnabled"] = "True",
					["DFIntTextureQualityOverride"] = "0",
					["FIntDebugForceMSAASamples"] = "1"
				},
				"balanced" => new Dictionary<string, string>
				{
					["DFFlagTextureQualityOverrideEnabled"] = "True",
					["DFIntTextureQualityOverride"] = "2",
					["FIntDebugForceMSAASamples"] = "2"
				},
				"quality" => new Dictionary<string, string>
				{
					["DFFlagTextureQualityOverrideEnabled"] = "True",
					["DFIntTextureQualityOverride"] = "3",
					["FIntDebugForceMSAASamples"] = "4"
				},
				_ => null
			};

			if (profileFlags == null)
				return new McpToolResult($"Unknown recommendedProfile '{recommendedProfile}'. Use 'Performance', 'Balanced', or 'Quality'.", true);

			foreach (KeyValuePair<string, string> kv in profileFlags)
			{
				App.FastFlags.Prop[kv.Key] = kv.Value;
				setCount++;
				if (allowlist.ContainsKey(kv.Key))
					allowlistedSetCount++;
			}
		}

		if (flags != null)
		{
			foreach (KeyValuePair<string, JsonNode?> kv in flags)
			{
				if (string.IsNullOrWhiteSpace(kv.Key))
					continue;

				if (kv.Value == null)
				{
					if (App.FastFlags.Prop.Remove(kv.Key))
						removedCount++;
					continue;
				}

				string val = kv.Value is JsonValue jv && jv.TryGetValue<bool>(out bool b)
					? (b ? "True" : "False")
					: kv.Value.ToString();

				App.FastFlags.Prop[kv.Key] = val;
				setCount++;
				if (allowlist.ContainsKey(kv.Key))
					allowlistedSetCount++;
			}
		}

		if (presets != null)
		{
			foreach (KeyValuePair<string, JsonNode?> kv in presets)
			{
				if (string.IsNullOrWhiteSpace(kv.Key))
					continue;

				object? rawVal = ExtractPrimitive(kv.Value);
				App.FastFlags.SetPreset(kv.Key, rawVal);
				if (rawVal == null)
				{
					removedCount++;
				}
				else
				{
					setCount++;
					if (FastFlagManager.PresetFlags.TryGetValue(kv.Key, out string? mappedFlag) && allowlist.ContainsKey(mappedFlag))
						allowlistedSetCount++;
				}
			}
		}

		App.FastFlags.Save();
		FastFlagEditorPage.RefreshActivePage();

		return new McpToolResult($"Saved FastFlags ({setCount} set [{allowlistedSetCount} on Roblox allowlist], {removedCount} removed, {App.FastFlags.Prop.Count} total active).");
	}

	private static McpToolResult NavigateUi(JsonObject args)
	{
		string? pageName = args["page"]?.GetValue<string>()?.Trim();
		string? search = args["search"]?.GetValue<string>()?.Trim();
		bool focusWindow = args["focusWindow"]?.GetValue<bool>() ?? true;

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
			return new McpToolResult($"Could not navigate to '{pageName}'. Valid pages: Home, Integrations, Deployment (or Bootstrapper), Appearance, FastFlags, FastFlagEditor, Mods, More, Global (or GBSEditor), Shortcuts, News, Extensions, Manager (or Downloads), Settings (or Channel), Accounts, Library, PrivateServers.", true);

		return new McpToolResult($"Navigated MainWindow to '{mainWin.CurrentPageName}'{(string.IsNullOrEmpty(search) ? "" : $" and searched '{search}'")}.");
	}

	private static McpToolResult ManageThemesAndMods(JsonObject args)
	{
		string action = args["action"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "list";
		string name = args["name"]?.GetValue<string>()?.Trim() ?? string.Empty;

		switch (action)
		{
			case "list":
			{
				var info = new JsonObject
				{
					["selectedBootstrapperStyle"] = App.Settings.Prop.BootstrapperStyle.ToString(),
					["selectedCustomBootstrapperTheme"] = App.Settings.Prop.SelectedCustomTheme,
					["customBootstrapperThemes"] = new JsonArray(GetInstalledBootstrapperThemes().Select(t => (JsonNode)t).ToArray()),
					["selectedKeystrokeTheme"] = App.Settings.Prop.KeystrokesThemeName,
					["builtInKeystrokeThemes"] = new JsonArray(KeystrokeThemeManager.GetBuiltInThemes().Select(t => (JsonNode)t.Name).ToArray()),
					["customKeystrokeThemes"] = new JsonArray(KeystrokeThemeManager.GetCustomThemes().Select(t => (JsonNode)t.Name).ToArray()),
					["cursorType"] = App.Settings.Prop.CursorType.ToString(),
					["customFontInstalled"] = File.Exists(Paths.CustomFont),
					["customDeathSoundInstalled"] = File.Exists(Paths.CustomDeathSound),
					["modFiles"] = new JsonArray(GetModFiles().Select(f => (JsonNode)f).ToArray())
				};
				return new McpToolResult(info.ToJsonString(IndentedJson));
			}

			case "create_bootstrapper_theme":
			{
				if (string.IsNullOrWhiteSpace(name))
					return new McpToolResult("Missing 'name' for create_bootstrapper_theme.", true);

				name = Path.GetFileName(name);
				string? xml = args["xml"]?.GetValue<string>();
				EnsureBootstrapperThemeExists(name, xml);

				bool activate = args["activate"]?.GetValue<bool>() ?? true;
				if (activate)
				{
					App.Settings.Prop.SelectedCustomTheme = name;
					App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
					App.Settings.Save();
				}

				return new McpToolResult($"Created custom Bootstrapper theme '{name}' at {Path.Combine(Paths.CustomThemes, name, "Theme.xml")}{(activate ? " and set it as active" : "")}.");
			}

			case "delete_bootstrapper_theme":
			{
				if (string.IsNullOrWhiteSpace(name))
					return new McpToolResult("Missing 'name' for delete_bootstrapper_theme.", true);

				string dir = Path.Combine(Paths.CustomThemes, Path.GetFileName(name));
				if (!Directory.Exists(dir))
					return new McpToolResult($"Bootstrapper theme '{name}' does not exist.", true);

				Directory.Delete(dir, recursive: true);
				if (string.Equals(App.Settings.Prop.SelectedCustomTheme, name, StringComparison.OrdinalIgnoreCase))
				{
					App.Settings.Prop.SelectedCustomTheme = null;
					App.Settings.Save();
				}
				return new McpToolResult($"Deleted custom Bootstrapper theme '{name}'.");
			}

			case "activate_bootstrapper_theme":
			{
				if (string.IsNullOrWhiteSpace(name))
					return new McpToolResult("Missing 'name' for activate_bootstrapper_theme.", true);

				name = Path.GetFileName(name);
				EnsureBootstrapperThemeExists(name, null);
				App.Settings.Prop.SelectedCustomTheme = name;
				App.Settings.Prop.BootstrapperStyle = BootstrapperStyle.CustomDialog;
				App.Settings.Save();
				return new McpToolResult($"Activated custom Bootstrapper theme '{name}'.");
			}

			case "create_keystroke_theme":
			{
				if (string.IsNullOrWhiteSpace(name))
					return new McpToolResult("Missing 'name' for create_keystroke_theme.", true);

				name = Path.GetFileName(name);
				string? baseTheme = args["baseTheme"]?.GetValue<string>()?.Trim();
				string? code = args["code"]?.GetValue<string>();

				KeystrokeTheme theme = KeystrokeThemeManager.GetTheme(baseTheme).Clone(name);
				theme.IsBuiltIn = false;

				if (!string.IsNullOrWhiteSpace(code))
				{
					ThemeCodeLanguage lang = DetectKeystrokeCodeLanguage(code);
					if (!KeystrokeThemeCodeConverter.TryParseCode(code, lang, theme, out string? parseError))
						return new McpToolResult($"Invalid keystroke theme code: {parseError}", true);
				}

				if (!KeystrokeThemeManager.SaveCustomTheme(theme))
					return new McpToolResult($"Could not save custom Keystroke theme '{name}'.", true);

				bool activate = args["activate"]?.GetValue<bool>() ?? true;
				if (activate)
				{
					App.Settings.Prop.KeystrokesThemeName = name;
					App.Settings.Prop.SelectedCustomKeystrokeTheme = name;
					App.Settings.Prop.KeystrokesOverlayEnabled = true;
					App.Settings.Save();
					KeystrokeThemeManager.NotifyThemeChanged();
				}

				return new McpToolResult($"Saved custom Keystroke theme '{name}'{(activate ? " and activated it" : "")}.");
			}

			case "delete_keystroke_theme":
			{
				if (string.IsNullOrWhiteSpace(name))
					return new McpToolResult("Missing 'name' for delete_keystroke_theme.", true);

				if (!KeystrokeThemeManager.DeleteCustomTheme(name))
					return new McpToolResult($"Could not delete custom Keystroke theme '{name}'.", true);

				return new McpToolResult($"Deleted custom Keystroke theme '{name}'.");
			}

			case "activate_keystroke_theme":
			{
				if (string.IsNullOrWhiteSpace(name))
					return new McpToolResult("Missing 'name' for activate_keystroke_theme.", true);

				App.Settings.Prop.KeystrokesThemeName = name;
				App.Settings.Prop.SelectedCustomKeystrokeTheme = name;
				App.Settings.Prop.KeystrokesOverlayEnabled = true;
				App.Settings.Save();
				KeystrokeThemeManager.NotifyThemeChanged();
				return new McpToolResult($"Activated Keystroke theme '{name}'.");
			}

			case "set_custom_font":
			{
				string? sourcePath = args["sourcePath"]?.GetValue<string>()?.Trim();
				if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
					return new McpToolResult("Provide an existing font file path in 'sourcePath'.", true);

				Directory.CreateDirectory(Path.GetDirectoryName(Paths.CustomFont)!);
				File.Copy(sourcePath, Paths.CustomFont, overwrite: true);
				App.Settings.Prop.CustomFontLocation = sourcePath;
				App.Settings.Save();
				return new McpToolResult($"Installed custom font from '{sourcePath}' to '{Paths.CustomFont}'.");
			}

			case "clear_custom_font":
			{
				if (File.Exists(Paths.CustomFont))
					File.Delete(Paths.CustomFont);
				App.Settings.Prop.CustomFontLocation = string.Empty;
				App.Settings.Save();
				return new McpToolResult("Removed custom font.");
			}

			case "set_custom_death_sound":
			{
				string? sourcePath = args["sourcePath"]?.GetValue<string>()?.Trim();
				if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
					return new McpToolResult("Provide an existing audio file path in 'sourcePath'.", true);

				Directory.CreateDirectory(Path.GetDirectoryName(Paths.CustomDeathSound)!);
				File.Copy(sourcePath, Paths.CustomDeathSound, overwrite: true);
				return new McpToolResult($"Installed custom death sound from '{sourcePath}' to '{Paths.CustomDeathSound}'.");
			}

			case "clear_custom_death_sound":
			{
				if (File.Exists(Paths.CustomDeathSound))
					File.Delete(Paths.CustomDeathSound);
				return new McpToolResult("Removed custom death sound.");
			}

			case "write_mod_file":
			{
				string? relativePath = args["relativePath"]?.GetValue<string>()?.Trim();
				string? content = args["content"]?.GetValue<string>();
				string? sourcePath = args["sourcePath"]?.GetValue<string>()?.Trim();

				if (string.IsNullOrWhiteSpace(relativePath))
					return new McpToolResult("Provide 'relativePath' inside VoidstrapMods.", true);

				string rootFull = Path.GetFullPath(Paths.Mods);
				string destFull = Path.GetFullPath(Path.Combine(rootFull, relativePath));
				if (!destFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
					return new McpToolResult("relativePath must stay inside the VoidstrapMods directory.", true);

				Directory.CreateDirectory(Path.GetDirectoryName(destFull)!);
				if (!string.IsNullOrWhiteSpace(sourcePath))
				{
					if (!File.Exists(sourcePath))
						return new McpToolResult($"sourcePath '{sourcePath}' does not exist.", true);
					File.Copy(sourcePath, destFull, overwrite: true);
				}
				else if (content != null)
				{
					File.WriteAllText(destFull, content);
				}
				else
				{
					return new McpToolResult("Provide either 'sourcePath' or 'content' for write_mod_file.", true);
				}

				return new McpToolResult($"Wrote mod file '{relativePath}' to '{destFull}'.");
			}

			case "delete_mod_file":
			{
				string? relativePath = args["relativePath"]?.GetValue<string>()?.Trim();
				if (string.IsNullOrWhiteSpace(relativePath))
					return new McpToolResult("Provide 'relativePath' inside VoidstrapMods.", true);

				string rootFull = Path.GetFullPath(Paths.Mods);
				string destFull = Path.GetFullPath(Path.Combine(rootFull, relativePath));
				if (!destFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
					return new McpToolResult("relativePath must stay inside the VoidstrapMods directory.", true);

				if (File.Exists(destFull))
					File.Delete(destFull);
				return new McpToolResult($"Deleted mod file '{relativePath}'.");
			}

			default:
				return new McpToolResult($"Unknown action '{action}'. Valid actions: list, create_bootstrapper_theme, delete_bootstrapper_theme, activate_bootstrapper_theme, create_keystroke_theme, delete_keystroke_theme, activate_keystroke_theme, set_custom_font, clear_custom_font, set_custom_death_sound, clear_custom_death_sound, write_mod_file, delete_mod_file.", true);
		}
	}

	private static McpToolResult LaunchRoblox(JsonObject args)
	{
		string mode = args["mode"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "player";
		string? placeId = args["placeId"]?.ToString()?.Trim();
		string? jobId = args["jobId"]?.GetValue<string>()?.Trim();

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

	private static McpToolResult GetLogsAndActivity(JsonObject args)
	{
		string source = args["source"]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? "all";
		int maxLines = Math.Clamp(args["maxLines"]?.GetValue<int>() ?? 120, 10, 500);
		string? filter = args["filter"]?.GetValue<string>()?.Trim();

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
						using var fs = new FileStream(latestLog.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

	private static List<string> GetInstalledBootstrapperThemes()
	{
		var list = new List<string>();
		if (string.IsNullOrWhiteSpace(Paths.CustomThemes) || !Directory.Exists(Paths.CustomThemes))
			return list;

		foreach (string dir in Directory.GetDirectories(Paths.CustomThemes))
		{
			if (File.Exists(Path.Combine(dir, "Theme.xml")))
				list.Add(Path.GetFileName(dir));
		}
		return list;
	}

	private static List<string> GetModFiles()
	{
		var list = new List<string>();
		if (string.IsNullOrWhiteSpace(Paths.Mods) || !Directory.Exists(Paths.Mods))
			return list;

		foreach (string file in Directory.EnumerateFiles(Paths.Mods, "*.*", SearchOption.AllDirectories).Take(200))
			list.Add(Path.GetRelativePath(Paths.Mods, file));
		return list;
	}

	private static void EnsureBootstrapperThemeExists(string name, string? customXml)
	{
		Directory.CreateDirectory(Paths.CustomThemes);
		string dir = Path.Combine(Paths.CustomThemes, name);
		Directory.CreateDirectory(dir);
		string xmlPath = Path.Combine(dir, "Theme.xml");

		if (!string.IsNullOrWhiteSpace(customXml))
		{
			File.WriteAllText(xmlPath, customXml);
		}
		else if (!File.Exists(xmlPath))
		{
			string template = Resource.GetString("CustomBootstrapperTemplate_Simple.xml");
			File.WriteAllText(xmlPath, template);
		}
	}

	private static void EnsureKeystrokeThemeExists(string name, string? baseTheme)
	{
		IReadOnlyList<string> allNames = KeystrokeThemeManager.GetAllThemeNames();
		if (allNames.Contains(name, StringComparer.OrdinalIgnoreCase))
			return;

		KeystrokeTheme theme = KeystrokeThemeManager.GetTheme(baseTheme).Clone(name);
		theme.IsBuiltIn = false;
		KeystrokeThemeManager.SaveCustomTheme(theme);
	}

	private static ThemeCodeLanguage DetectKeystrokeCodeLanguage(string text)
	{
		string trimmed = text.TrimStart();
		if (trimmed.StartsWith('{'))
			return ThemeCodeLanguage.Json;
		if (trimmed.StartsWith('<') || trimmed.Contains(":root", StringComparison.OrdinalIgnoreCase))
			return ThemeCodeLanguage.HtmlCss;
		if (trimmed.Contains("pub fn configure", StringComparison.Ordinal) || trimmed.Contains("let mut theme", StringComparison.Ordinal))
			return ThemeCodeLanguage.Rust;
		return ThemeCodeLanguage.CSharp;
	}

	private static string DescribeType(Type t)
	{
		Type? nullable = Nullable.GetUnderlyingType(t);
		if (nullable != null)
			return DescribeType(nullable) + "?";
		if (t.IsEnum)
			return $"enum({t.Name})";
		if (t.IsGenericType)
		{
			string baseName = t.Name.Split('`')[0];
			string args = string.Join(", ", t.GetGenericArguments().Select(DescribeType));
			return $"{baseName}<{args}>";
		}
		return t.Name;
	}

	private static JsonNode? ToJsonNode(object? val)
	{
		if (val == null)
			return null;

		return val switch
		{
			bool b => JsonValue.Create(b),
			int i => JsonValue.Create(i),
			long l => JsonValue.Create(l),
			double d => JsonValue.Create(d),
			float f => JsonValue.Create(f),
			string s => JsonValue.Create(s),
			Enum e => JsonValue.Create(e.ToString()),
			_ => SafeSerializeToNode(val)
		};
	}

	private static JsonNode? SafeSerializeToNode(object val)
	{
		try
		{
			return JsonSerializer.SerializeToNode(val);
		}
		catch
		{
			return JsonValue.Create(val.ToString());
		}
	}

	private static object? ExtractPrimitive(JsonNode? node)
	{
		if (node == null)
			return null;
		if (node is JsonValue jv)
		{
			if (jv.TryGetValue<bool>(out bool b))
				return b;
			if (jv.TryGetValue<int>(out int i))
				return i;
			if (jv.TryGetValue<long>(out long l))
				return l;
			if (jv.TryGetValue<double>(out double d))
				return d;
			if (jv.TryGetValue<string>(out string? s))
				return s;
		}
		return node.ToString();
	}

	private static bool TryConvertJsonNode(JsonNode? node, Type targetType, out object? result, out string? error)
	{
		result = null;
		error = null;

		Type? underlying = Nullable.GetUnderlyingType(targetType);
		Type coreType = underlying ?? targetType;

		if (node == null)
		{
			if (!targetType.IsValueType || underlying != null)
				return true;
			error = $"Cannot assign null to non-nullable {targetType.Name}";
			return false;
		}

		try
		{
			if (coreType == typeof(string))
			{
				result = node is JsonValue jv && jv.TryGetValue<string>(out string? s) ? s : node.ToString();
				return true;
			}

			if (coreType == typeof(bool))
			{
				if (node is JsonValue jv && jv.TryGetValue<bool>(out bool b))
				{
					result = b;
					return true;
				}
				if (bool.TryParse(node.ToString(), out bool parsedBool))
				{
					result = parsedBool;
					return true;
				}
				error = "Expected boolean";
				return false;
			}

			if (coreType == typeof(int))
			{
				if (node is JsonValue jv && jv.TryGetValue<int>(out int i))
				{
					result = i;
					return true;
				}
				if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedInt))
				{
					result = parsedInt;
					return true;
				}
				error = "Expected integer";
				return false;
			}

			if (coreType == typeof(long))
			{
				if (node is JsonValue jv && jv.TryGetValue<long>(out long l))
				{
					result = l;
					return true;
				}
				if (long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLong))
				{
					result = parsedLong;
					return true;
				}
				error = "Expected 64-bit integer";
				return false;
			}

			if (coreType == typeof(double))
			{
				if (node is JsonValue jv && jv.TryGetValue<double>(out double d))
				{
					result = d;
					return true;
				}
				if (double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDouble))
				{
					result = parsedDouble;
					return true;
				}
				error = "Expected number";
				return false;
			}

			if (coreType == typeof(float))
			{
				if (node is JsonValue jv && jv.TryGetValue<float>(out float f))
				{
					result = f;
					return true;
				}
				if (float.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedFloat))
				{
					result = parsedFloat;
					return true;
				}
				error = "Expected float";
				return false;
			}

			if (coreType.IsEnum)
			{
				if (node is JsonValue jv && jv.TryGetValue<int>(out int enumInt))
				{
					result = Enum.ToObject(coreType, enumInt);
					return true;
				}
				string enumStr = node is JsonValue jvs && jvs.TryGetValue<string>(out string? s) ? s : node.ToString();
				if (Enum.TryParse(coreType, enumStr, ignoreCase: true, out object? parsedEnum))
				{
					result = parsedEnum;
					return true;
				}
				error = $"Invalid {coreType.Name} value '{enumStr}'. Valid values: {string.Join(", ", Enum.GetNames(coreType))}";
				return false;
			}

			result = JsonSerializer.Deserialize(node.ToJsonString(), targetType);
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
	}

	private static string NoEditorError(string? requested)
	{
		IReadOnlyList<KeyValuePair<string, IMcpEditor>> all = McpEditorRegistry.Snapshot();
		if (all.Count == 0)
			return "No Voidstrap editor window or page is currently open. Call open_editor with type 'bootstrapper', 'keystroke', or 'fastflags' to open one automatically.";

		string ids = string.Join(", ", all.Select(e => $"{e.Key} ({e.Value.DisplayName})"));
		return $"Editor '{requested}' is not open. Open editors: {ids}. Or call open_editor to open it.";
	}

	private static bool IsBlockedSetting(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
			return true;

		return string.Equals(name, nameof(AppSettings.MultiAccount), StringComparison.OrdinalIgnoreCase)
			|| name.Contains("MultiInstance", StringComparison.OrdinalIgnoreCase)
			|| name.Contains("MultiAccount", StringComparison.OrdinalIgnoreCase);
	}

	private sealed record TrackerSourceDef(string Name, string Url, bool NamesOnly);
	private sealed record TrackerFlagEntry(string DefaultValue, string Source);

	private static readonly TrackerSourceDef[] TrackerSources =
	{
		new("Roblox ClientSettings", "https://clientsettings.roblox.com/v2/settings/application/PCDesktopClient", false),
		new("PCDesktopClient", "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCDesktopClient.json", false),
		new("PCClientBootstrapper", "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCClientBootstrapper.json", false),
		new("PCStudioApp", "https://raw.githubusercontent.com/MaximumADHD/Roblox-FFlag-Tracker/refs/heads/main/PCStudioApp.json", false),
		new("FVariables.txt", "https://raw.githubusercontent.com/MaximumADHD/Roblox-Client-Tracker/refs/heads/roblox/FVariables.txt", true)
	};

	private static readonly string[] KnownFlagPrefixes = { "FFlag", "DFFlag", "SFFlag", "FInt", "DFInt", "SFInt", "FString", "DFString", "SFString", "FLog", "DFLog", "SFLog", "FDouble" };

	private static readonly HttpClient TrackerHttpClient = VpnHttpClient.Create(TimeSpan.FromSeconds(20));
	private static readonly SemaphoreSlim TrackerFetchLock = new(1, 1);

	private static volatile Dictionary<string, string>? _onlineAllowlistCache;
	private static volatile Dictionary<string, TrackerFlagEntry>? _trackerFlagsCache;
	private static volatile Dictionary<string, int>? _trackerSourceCountsCache;

	private static readonly Regex AllowlistCategoryRegex = new("<p><strong>(?<category>[^<]+)</strong>:</p>\\s*<ul>(?<items>.*?)</ul>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
	private static readonly Regex AllowlistItemRegex = new("<li>(?<flag>[A-Za-z][A-Za-z0-9_]*)</li>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private static IReadOnlyDictionary<string, string> GetEffectiveAllowlist()
	{
		return _onlineAllowlistCache ?? RobloxFastFlagAllowlist.Flags;
	}

	private static bool IsAllowlistedFlag(string name)
	{
		return GetEffectiveAllowlist().ContainsKey(name);
	}

	public static async Task<McpToolResult> SearchFastFlagTrackerAsync(JsonObject args, Dispatcher dispatcher, CancellationToken token)
	{
		try
		{
			string? query = args["query"]?.GetValue<string>()?.Trim();
			string sourceFilter = args["source"]?.GetValue<string>()?.Trim() ?? "all";
			bool allowlistedOnly = args["allowlistedOnly"]?.GetValue<bool>() ?? false;
			bool refreshOnline = args["refreshOnline"]?.GetValue<bool>() ?? false;
			bool validateConfigured = args["validateConfigured"]?.GetValue<bool>() ?? string.IsNullOrWhiteSpace(query);
			int limit = Math.Clamp(args["limit"]?.GetValue<int>() ?? 100, 1, 500);

			Dictionary<string, string> configuredSnapshot = await dispatcher.InvokeAsync(() =>
				App.FastFlags.Prop.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.Ordinal));

			await EnsureTrackerAndAllowlistLoadedAsync(refreshOnline, token).ConfigureAwait(false);

			IReadOnlyDictionary<string, string> allowlist = GetEffectiveAllowlist();
			Dictionary<string, TrackerFlagEntry> trackerFlags = _trackerFlagsCache ?? new Dictionary<string, TrackerFlagEntry>(StringComparer.Ordinal);
			Dictionary<string, int> sourceCounts = _trackerSourceCountsCache ?? new Dictionary<string, int>(StringComparer.Ordinal);

			var root = new JsonObject();

			var allowlistFlagsNode = new JsonObject();
			foreach (KeyValuePair<string, string> kv in allowlist.OrderBy(p => p.Value, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
				allowlistFlagsNode[kv.Key] = kv.Value;

			root["robloxAllowlist"] = new JsonObject
			{
				["announcementUrl"] = RobloxFastFlagAllowlist.AnnouncementUrl,
				["jsonFeedUrl"] = RobloxFastFlagAllowlist.AnnouncementUrl + ".json",
				["onlineLoaded"] = _onlineAllowlistCache != null,
				["count"] = allowlist.Count,
				["flags"] = allowlistFlagsNode
			};

			var sourcesNode = new JsonObject();
			foreach (TrackerSourceDef def in TrackerSources)
			{
				sourcesNode[def.Name] = new JsonObject
				{
					["url"] = def.Url,
					["flagCount"] = sourceCounts.GetValueOrDefault(def.Name, 0)
				};
			}

			root["maximumAdhdTracker"] = new JsonObject
			{
				["trackerRepository"] = "https://github.com/MaximumADHD/Roblox-FFlag-Tracker",
				["clientTrackerRepository"] = "https://github.com/MaximumADHD/Roblox-Client-Tracker",
				["totalUniqueFlags"] = trackerFlags.Count,
				["sources"] = sourcesNode
			};

			if (validateConfigured)
			{
				var validationObj = new JsonObject();
				int validInTrackerCount = 0;
				int allowlistedConfigCount = 0;

				foreach (KeyValuePair<string, string> kv in configuredSnapshot.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
				{
					bool inTracker = trackerFlags.TryGetValue(kv.Key, out TrackerFlagEntry? tEntry);
					bool isAllowed = allowlist.TryGetValue(kv.Key, out string? allowCat);
					if (inTracker)
						validInTrackerCount++;
					if (isAllowed)
						allowlistedConfigCount++;

					var tagsArr = new JsonArray();
					foreach (string tag in FastFlagEditorPage.FastFlagTagHelper.GetTags(kv.Key))
						tagsArr.Add(tag);

					validationObj[kv.Key] = new JsonObject
					{
						["configuredValue"] = kv.Value,
						["existsInTracker"] = inTracker,
						["trackerSource"] = tEntry?.Source,
						["defaultValue"] = tEntry?.DefaultValue,
						["isAllowlisted"] = isAllowed,
						["allowlistCategory"] = allowCat,
						["tags"] = tagsArr
					};
				}

				root["configuredFlagValidation"] = new JsonObject
				{
					["totalConfigured"] = configuredSnapshot.Count,
					["validInTrackerCount"] = validInTrackerCount,
					["allowlistedCount"] = allowlistedConfigCount,
					["flags"] = validationObj
				};
			}

			bool hasSearchCriteria = !string.IsNullOrWhiteSpace(query)
				|| allowlistedOnly
				|| !string.Equals(sourceFilter, "all", StringComparison.OrdinalIgnoreCase);

			if (hasSearchCriteria)
			{
				string[] terms = string.IsNullOrWhiteSpace(query)
					? Array.Empty<string>()
					: query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

				var matchesArray = new JsonArray();
				int totalMatches = 0;

				IEnumerable<KeyValuePair<string, TrackerFlagEntry>> candidates;
				if (string.Equals(sourceFilter, "allowlist", StringComparison.OrdinalIgnoreCase) || allowlistedOnly)
				{
					candidates = allowlist.Keys.Select(k =>
						trackerFlags.TryGetValue(k, out TrackerFlagEntry? te)
							? new KeyValuePair<string, TrackerFlagEntry>(k, te)
							: new KeyValuePair<string, TrackerFlagEntry>(k, new TrackerFlagEntry(string.Empty, "Roblox Allowlist")));
				}
				else
				{
					candidates = trackerFlags;
				}

				foreach (KeyValuePair<string, TrackerFlagEntry> kv in candidates.OrderBy(p => !allowlist.ContainsKey(p.Key)).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
				{
					if (!string.Equals(sourceFilter, "all", StringComparison.OrdinalIgnoreCase)
						&& !string.Equals(sourceFilter, "allowlist", StringComparison.OrdinalIgnoreCase)
						&& !kv.Value.Source.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase))
						continue;

					bool isAllowed = allowlist.TryGetValue(kv.Key, out string? allowCat);
					if (allowlistedOnly && !isAllowed)
						continue;

					if (terms.Length > 0)
					{
						bool matchedAll = true;
						foreach (string term in terms)
						{
							if (!kv.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
								&& (allowCat == null || !allowCat.Contains(term, StringComparison.OrdinalIgnoreCase)))
							{
								matchedAll = false;
								break;
							}
						}
						if (!matchedAll)
							continue;
					}

					totalMatches++;
					if (matchesArray.Count >= limit)
						continue;

					configuredSnapshot.TryGetValue(kv.Key, out string? currentVal);
					matchesArray.Add(new JsonObject
					{
						["name"] = kv.Key,
						["defaultValue"] = kv.Value.DefaultValue,
						["source"] = kv.Value.Source,
						["isAllowlisted"] = isAllowed,
						["allowlistCategory"] = allowCat,
						["configuredValue"] = currentVal
					});
				}

				root["searchResults"] = new JsonObject
				{
					["query"] = query ?? string.Empty,
					["totalMatches"] = totalMatches,
					["returnedCount"] = matchesArray.Count,
					["matches"] = matchesArray
				};
			}

			return new McpToolResult(root.ToJsonString(IndentedJson));
		}
		catch (Exception ex)
		{
			return new McpToolResult("FastFlag tracker search failed: " + ex.Message, true);
		}
	}

	private static async Task EnsureTrackerAndAllowlistLoadedAsync(bool forceRefresh, CancellationToken token)
	{
		if (!forceRefresh && _trackerFlagsCache != null && _onlineAllowlistCache != null)
			return;

		await TrackerFetchLock.WaitAsync(token).ConfigureAwait(false);
		try
		{
			if (!forceRefresh && _trackerFlagsCache != null && _onlineAllowlistCache != null)
				return;

			Task<Dictionary<string, string>?> allowlistTask = FetchOnlineAllowlistAsync(token);
			Task<(string Name, Dictionary<string, string> Flags)>[] sourceTasks = TrackerSources
				.Select(src => FetchTrackerSourceAsync(src, token))
				.ToArray();

			await Task.WhenAll(sourceTasks).ConfigureAwait(false);
			Dictionary<string, string>? onlineAllowlist = await allowlistTask.ConfigureAwait(false);

			if (onlineAllowlist != null && onlineAllowlist.Count > 0)
			{
				foreach (KeyValuePair<string, string> builtIn in RobloxFastFlagAllowlist.Flags)
					onlineAllowlist.TryAdd(builtIn.Key, builtIn.Value);
				_onlineAllowlistCache = onlineAllowlist;
			}

			var combined = new Dictionary<string, TrackerFlagEntry>(StringComparer.Ordinal);
			var counts = new Dictionary<string, int>(StringComparer.Ordinal);

			foreach (Task<(string Name, Dictionary<string, string> Flags)> completed in sourceTasks)
			{
				(string srcName, Dictionary<string, string> flags) = await completed.ConfigureAwait(false);
				counts[srcName] = flags.Count;
				foreach (KeyValuePair<string, string> kv in flags)
				{
					if (!combined.TryGetValue(kv.Key, out TrackerFlagEntry? existing))
					{
						combined[kv.Key] = new TrackerFlagEntry(kv.Value, srcName);
					}
					else if (string.IsNullOrEmpty(existing.DefaultValue) && !string.IsNullOrEmpty(kv.Value))
					{
						combined[kv.Key] = new TrackerFlagEntry(kv.Value, srcName);
					}
				}
			}

			if (combined.Count > 0)
			{
				_trackerFlagsCache = combined;
				_trackerSourceCountsCache = counts;
			}
		}
		finally
		{
			TrackerFetchLock.Release();
		}
	}

	private static async Task<Dictionary<string, string>?> FetchOnlineAllowlistAsync(CancellationToken token)
	{
		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
			cts.CancelAfter(TimeSpan.FromSeconds(12));
			string payload = await Http.GetStringBoundedAsync(TrackerHttpClient, RobloxFastFlagAllowlist.AnnouncementUrl + ".json", token: cts.Token).ConfigureAwait(false);
			if (string.IsNullOrWhiteSpace(payload))
				return null;

			using JsonDocument doc = JsonDocument.Parse(payload);
			if (!doc.RootElement.TryGetProperty("post_stream", out JsonElement stream)
				|| !stream.TryGetProperty("posts", out JsonElement posts)
				|| posts.ValueKind != JsonValueKind.Array
				|| posts.GetArrayLength() == 0
				|| !posts[0].TryGetProperty("cooked", out JsonElement cookedEl))
				return null;

			string cooked = cookedEl.GetString() ?? string.Empty;
			int start = cooked.IndexOf("What Fast Flags are currently on the allowlist?", StringComparison.OrdinalIgnoreCase);
			int end = cooked.IndexOf("How have the Fast Flags on the Allowlist been chosen?", StringComparison.OrdinalIgnoreCase);
			if (start < 0 || end <= start)
				return null;

			string section = cooked[start..end];
			var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (Match catMatch in AllowlistCategoryRegex.Matches(section))
			{
				string category = WebUtility.HtmlDecode(catMatch.Groups["category"].Value).Trim();
				foreach (Match itemMatch in AllowlistItemRegex.Matches(catMatch.Groups["items"].Value))
				{
					string flag = itemMatch.Groups["flag"].Value;
					parsed[flag] = category;
				}
			}

			return parsed.Count > 0 ? parsed : null;
		}
		catch
		{
			return null;
		}
	}

	private static async Task<(string Name, Dictionary<string, string> Flags)> FetchTrackerSourceAsync(TrackerSourceDef source, CancellationToken token)
	{
		var flags = new Dictionary<string, string>(StringComparer.Ordinal);
		try
		{
			using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
			cts.CancelAfter(TimeSpan.FromSeconds(15));
			string content = await Http.GetStringBoundedAsync(TrackerHttpClient, source.Url, token: cts.Token).ConfigureAwait(false);

			if (source.NamesOnly)
			{
				foreach (string rawLine in content.Split('\n'))
				{
					string line = rawLine.Trim();
					while (line.StartsWith('['))
					{
						int close = line.IndexOf(']');
						if (close < 0)
							break;
						line = line[(close + 1)..].Trim();
					}
					int eq = line.IndexOf('=');
					if (eq >= 0)
						line = line[..eq].Trim();
					int sp = line.IndexOf(' ');
					if (sp >= 0)
						line = line[..sp].Trim();

					if (line.Length > 0 && KnownFlagPrefixes.Any(p => line.StartsWith(p, StringComparison.Ordinal)))
						flags.TryAdd(line, string.Empty);
				}
			}
			else
			{
				using JsonDocument doc = JsonDocument.Parse(content);
				JsonElement root = doc.RootElement;
				if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("applicationSettings", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
					root = nested;

				if (root.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty prop in root.EnumerateObject())
					{
						string val = prop.Value.ValueKind switch
						{
							JsonValueKind.True => "True",
							JsonValueKind.False => "False",
							JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
							_ => prop.Value.GetRawText()
						};
						flags.TryAdd(prop.Name, val);
					}
				}
			}
		}
		catch
		{
		}

		return (source.Name, flags);
	}
}
