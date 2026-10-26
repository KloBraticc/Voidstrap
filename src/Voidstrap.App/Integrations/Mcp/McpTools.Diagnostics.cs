using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using Voidstrap.Enums;
using Voidstrap.Integrations.Keystrokes;
using Voidstrap.Integrations.RiShade;
using Voidstrap.Models.Persistable;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
	private sealed record AppIssue(string Id, string Severity, string Problem, string? Fix, string? Target = null);

	private static McpToolResult DiagnoseApp(JsonObject args)
	{
		int maxErrors = McpArgs.Int(args, "maxErrors", 20, 1, 50);
		List<AppIssue> issues = CollectIssues();

		var errors = new JsonArray();
		foreach (AppNotification item in AppNotifications.Items
			.Where(item => item.Kind is AppNotifications.KindError or AppNotifications.KindCrash)
			.OrderByDescending(item => item.LastSeen)
			.Take(maxErrors))
		{
			errors.Add(new JsonObject
			{
				["kind"] = item.Kind,
				["source"] = item.Source,
				["title"] = item.Title,
				["detail"] = item.Text,
				["count"] = item.Count,
				["lastSeen"] = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, item.LastSeen)).LocalDateTime.ToString("g", CultureInfo.InvariantCulture),
				["log"] = string.IsNullOrEmpty(item.LogPath) ? null : item.LogPath
			});
		}

		var issueArray = new JsonArray();
		foreach (AppIssue issue in issues)
		{
			issueArray.Add(new JsonObject
			{
				["id"] = issue.Id,
				["severity"] = issue.Severity,
				["problem"] = issue.Problem,
				["fix"] = issue.Fix,
				["target"] = issue.Target
			});
		}

		return new McpToolResult(new JsonObject
		{
			["summary"] = issues.Count == 0 ? "No broken Voidstrap state was found." : $"{issues.Count} problem(s) found, apply one with fix_app_issue using its fix and target.",
			["issues"] = issueArray,
			["recentErrors"] = errors,
			["howToDig"] = "Use get_logs_and_activity with a filter on an error's source, or read_voidstrap_file on its log, to see the full stack trace. reset_setting, clear_error_notifications and move_roblox_cache_aside are always available fixes."
		}.ToJsonString(IndentedJson));
	}

	private static List<AppIssue> CollectIssues()
	{
		var issues = new List<AppIssue>();
		AppSettings settings = App.Settings.Prop;

		try
		{
			if (settings.Theme2 == Theme.Custom && File.Exists(Paths.CustomThemeXaml))
			{
				ThemeValidationResult result = CustomTheme.Validate(CustomTheme.ReadFile(Paths.CustomThemeXaml));
				if (!result.Ok)
					issues.Add(new AppIssue("custom_app_theme_invalid", "error", "The custom Voidstrap UI theme does not load: " + string.Join(" ", result.Errors.Take(2)), "reset_custom_app_theme"));
			}
		}
		catch (Exception ex)
		{
			issues.Add(new AppIssue("custom_app_theme_unreadable", "error", "The custom Voidstrap UI theme file cannot be read: " + ex.Message, "reset_custom_app_theme"));
		}

		if (settings.BootstrapperStyle == BootstrapperStyle.CustomDialog)
		{
			string? name = settings.SelectedCustomTheme;
			if (string.IsNullOrWhiteSpace(name) || !McpArgs.IsSafeName(name) || !File.Exists(Path.Combine(Paths.CustomThemes, name, "Theme.xml")))
				issues.Add(new AppIssue("bootstrapper_theme_missing", "error", $"The custom bootstrapper style is on but the theme '{name}' has no Theme.xml.", "reset_bootstrapper_theme"));
		}

		if (!string.IsNullOrWhiteSpace(settings.KeystrokesThemeName) && !KeystrokeThemeManager.GetAllThemeNames().Contains(settings.KeystrokesThemeName, StringComparer.OrdinalIgnoreCase))
			issues.Add(new AppIssue("keystroke_theme_missing", "warning", $"The keystroke theme '{settings.KeystrokesThemeName}' no longer exists.", "reset_keystroke_theme"));

		List<string> badFlags = App.FastFlags.Prop.Keys.Where(key => !KnownFlagPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))).ToList();
		if (badFlags.Count > 0)
			issues.Add(new AppIssue("invalid_fastflag_names", "warning", $"{badFlags.Count} FastFlag name(s) are not real FastFlags and are ignored by Roblox: {string.Join(", ", badFlags.Take(10))}", "remove_invalid_fastflags"));

		try
		{
			foreach (ManagedModLibraryEntry entry in ManagedModStore.ScanLibrary().Where(entry => entry.Record.Enabled && !string.IsNullOrEmpty(entry.Failure)))
				issues.Add(new AppIssue("mod_problem", "warning", $"The mod '{entry.Record.Name}' has a problem: {entry.Failure}", "disable_mod", entry.Record.Id));
		}
		catch (Exception ex)
		{
			issues.Add(new AppIssue("mod_library_unreadable", "warning", "The mods library could not be scanned: " + ex.Message, null));
		}

		if (Voidstrap.Utility.Platform.IsWindows && settings.RiShadeEnabled && RiShadeManager.IsRuntimePopulated && !RiShadeManager.HasLoadablePreset)
			issues.Add(new AppIssue("rishade_no_preset", "info", "RiShade is on but no loadable preset is selected, so frames stay unchanged.", "disable_rishade"));

		return issues;
	}

	private static McpToolResult FixAppIssue(JsonObject args)
	{
		string fix = McpArgs.Trimmed(args, "fix").ToLowerInvariant();
		string target = McpArgs.Trimmed(args, "target");
		AppSettings settings = App.Settings.Prop;

		switch (fix)
		{
			case "reset_custom_app_theme":
			{
				string? previous = File.Exists(Paths.CustomThemeXaml) ? SafeRead(Paths.CustomThemeXaml) : null;
				Theme previousTheme = settings.Theme2;
				if (File.Exists(Paths.CustomThemeXaml))
					File.Move(Paths.CustomThemeXaml, Paths.CustomThemeXaml + ".broken", overwrite: true);
				if (settings.Theme2 == Theme.Custom)
					settings.Theme2 = Theme.Dark;
				App.Settings.Save();
				ReapplyAppTheme();
				PushUndo("reset the custom app theme", () =>
				{
					if (previous != null)
						CustomTheme.WriteFile(Paths.CustomThemeXaml, previous);
					App.Settings.Prop.Theme2 = previousTheme;
					App.Settings.Save();
					ReapplyAppTheme();
				});
				return new McpToolResult("Moved the broken custom theme to CustomTheme.xaml.broken and switched Voidstrap to the Dark theme.");
			}
			case "reset_bootstrapper_theme":
			{
				BootstrapperStyle before = settings.BootstrapperStyle;
				settings.BootstrapperStyle = new AppSettings().BootstrapperStyle;
				App.Settings.Save();
				PushUndo("reset the bootstrapper style", () => { App.Settings.Prop.BootstrapperStyle = before; App.Settings.Save(); });
				RefreshMainWindow();
				return new McpToolResult($"Switched the bootstrapper style back to {settings.BootstrapperStyle}.");
			}
			case "reset_keystroke_theme":
			{
				string before = settings.KeystrokesThemeName;
				settings.KeystrokesThemeName = new AppSettings().KeystrokesThemeName;
				App.Settings.Save();
				KeystrokeThemeManager.NotifyThemeChanged();
				PushUndo("reset the keystroke theme", () => { App.Settings.Prop.KeystrokesThemeName = before; App.Settings.Save(); KeystrokeThemeManager.NotifyThemeChanged(); });
				return new McpToolResult($"Switched the keystroke theme to {settings.KeystrokesThemeName}.");
			}
			case "remove_invalid_fastflags":
			{
				Dictionary<string, object> snapshot = new(App.FastFlags.Prop, App.FastFlags.Prop.Comparer);
				List<string> bad = App.FastFlags.Prop.Keys.Where(key => !KnownFlagPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal))).ToList();
				foreach (string key in bad)
					App.FastFlags.Prop.Remove(key);
				App.FastFlags.Save();
				UI.Elements.Settings.Pages.FastFlagEditorPage.RefreshActivePage();
				PushUndo("remove invalid FastFlags", () =>
				{
					App.FastFlags.Prop.Clear();
					foreach (KeyValuePair<string, object> pair in snapshot)
						App.FastFlags.Prop[pair.Key] = pair.Value;
					App.FastFlags.Save();
					UI.Elements.Settings.Pages.FastFlagEditorPage.RefreshActivePage();
				});
				return new McpToolResult($"Removed {bad.Count} invalid FastFlag name(s).");
			}
			case "disable_mod":
				return ManageMods(new JsonObject { ["action"] = "disable", ["id"] = target });
			case "disable_rishade":
				return ManageRiShade(new JsonObject { ["action"] = "disable" });
			case "reset_setting":
			{
				PropertyInfo? property = typeof(AppSettings).GetProperty(target, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
				if (property == null || !property.CanWrite || IsBlockedSetting(property.Name))
					return new McpToolResult($"'{target}' is not a setting that can be reset. Use get_settings to find the name.", true);
				object? before = property.GetValue(settings);
				object? value = property.GetValue(new AppSettings());
				property.SetValue(settings, value);
				App.Settings.Save();
				PushUndo("reset " + property.Name, () => { property.SetValue(App.Settings.Prop, before); App.Settings.Save(); RefreshMainWindow(); });
				RefreshMainWindow();
				return new McpToolResult($"Reset {property.Name} to its default {ToJsonNode(value)?.ToJsonString() ?? "null"}.");
			}
			case "clear_error_notifications":
				AppNotifications.Clear();
				return new McpToolResult("Cleared Voidstrap's error notifications.");
			case "move_roblox_cache_aside":
			{
				Process[] running = Process.GetProcessesByName(App.RobloxPlayerAppName);
				bool robloxOpen = running.Length > 0;
				foreach (Process process in running)
					process.Dispose();
				if (robloxOpen)
					return new McpToolResult("Close Roblox first, its cache is in use.", true);
				string? moved = RobloxLocalReset.MoveCacheAside();
				return moved == null
					? new McpToolResult("There was no Roblox cache to move.")
					: new McpToolResult("Moved Roblox's local cache to " + moved + ". Roblox rebuilds it on the next launch and your login and settings are kept. Delete that folder once Roblox works.");
			}
			default:
				return new McpToolResult("Unknown fix. Use a fix from diagnose_app, or reset_setting (target = setting name), clear_error_notifications, move_roblox_cache_aside.", true);
		}
	}

	private static string? SafeRead(string path)
	{
		try
		{
			return File.ReadAllText(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}
