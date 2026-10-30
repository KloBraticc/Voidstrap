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
	private static McpToolResult GetSettings(JsonObject args)
	{
		string? filter = McpArgs.String(args, "filter")?.Trim();
		bool includeSchema = McpArgs.Flag(args, "includeSchema", true);

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
				["writable"] = prop.CanWrite,
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
			if (
				!string.IsNullOrEmpty(filter)
				&& !kv.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)
				&& !"globalbasicsettings".Contains(filter, StringComparison.OrdinalIgnoreCase)
			)
				continue;

			var gbsEntry = new JsonObject
			{
				["value"] = App.GlobalSettings.GetProperty(kv.Key),
				["type"] = kv.Value.Tag,
				["min"] = kv.Value.Minimum,
				["max"] = kv.Value.Maximum,
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
		bool dryRun = McpArgs.Flag(args, "dryRun", false);
		var previousSettings = new List<(PropertyInfo Property, object? Value)>();
		var previousGlobal = new List<(string Name, string? Value, bool Preset)>();

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

				if (dryRun)
				{
					updated.Add(
						$"{prop.Name}: {ToJsonNode(prop.GetValue(instance))?.ToJsonString() ?? "null"} would become {kv.Value?.ToJsonString() ?? "null"}"
					);
					continue;
				}

				try
				{
					previousSettings.Add((prop, prop.GetValue(instance)));
					prop.SetValue(instance, converted);
					updated.Add($"{prop.Name} = {kv.Value?.ToJsonString() ?? "null"}");
					if (
						prop.Name.StartsWith("Keystrokes", StringComparison.OrdinalIgnoreCase)
						|| string.Equals(prop.Name, nameof(AppSettings.SelectedCustomKeystrokeTheme), StringComparison.OrdinalIgnoreCase)
					)
						keystrokeChanged = true;
				}
				catch (Exception ex)
				{
					errors.Add($"{prop.Name}: {ex.Message}");
				}
			}

			if (!dryRun)
				App.Settings.Save();
		}

		if (gbsNode != null && gbsNode.Count > 0)
		{
			foreach (KeyValuePair<string, JsonNode?> kv in gbsNode)
			{
				object? rawVal = ExtractPrimitive(kv.Value);
				bool isPreset = App.GlobalSettings.PresetPaths.ContainsKey(kv.Key);
				string? before = isPreset ? App.GlobalSettings.GetPreset(kv.Key) : App.GlobalSettings.GetProperty(kv.Key);
				if (dryRun)
				{
					updated.Add($"GlobalBasicSettings.{kv.Key}: {before ?? "unset"} would become {rawVal ?? "null"}");
					continue;
				}
				previousGlobal.Add((kv.Key, before, isPreset));
				bool ok;
				if (isPreset)
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
				if (!dryRun)
					App.GlobalSettings.Save();
			}
			catch (Exception ex)
			{
				errors.Add($"GlobalBasicSettings.Save: {ex.Message}");
			}
		}

		if (dryRun)
			return new McpToolResult(
				"Dry run, nothing was changed."
					+ Environment.NewLine
					+ string.Join(Environment.NewLine, updated.Concat(errors.Select(err => "Error: " + err))),
				errors.Count > 0 && updated.Count == 0
			);

		if (previousSettings.Count > 0 || previousGlobal.Count > 0)
		{
			PushUndo(
				$"set_settings ({previousSettings.Count + previousGlobal.Count} values)",
				() =>
				{
					foreach ((PropertyInfo property, object? value) in previousSettings)
						property.SetValue(App.Settings.Prop, value);
					if (previousSettings.Count > 0)
						App.Settings.Save();
					foreach ((string name, string? value, bool preset) in previousGlobal)
					{
						if (preset)
							App.GlobalSettings.SetPreset(name, value);
						else
							App.GlobalSettings.SetProperty(name, value);
					}
					if (previousGlobal.Count > 0)
						App.GlobalSettings.Save();
					KeystrokeThemeManager.NotifyThemeChanged();
					Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault()?.RefreshCurrentPage();
				}
			);
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
}
