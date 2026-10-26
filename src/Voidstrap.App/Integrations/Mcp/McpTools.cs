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

public static partial class McpTools
{
	private const int UndoDepth = 25;

	private static readonly List<(string Label, DateTime At, Action Undo)> UndoLog = [];

	private static void PushUndo(string label, Action undo)
	{
		lock (UndoLog)
		{
			UndoLog.Add((label, DateTime.Now, undo));
			if (UndoLog.Count > UndoDepth)
				UndoLog.RemoveAt(0);
		}
	}

	private static McpToolResult UndoLastChange(JsonObject args)
	{
		if (McpArgs.Flag(args, "list", false))
		{
			lock (UndoLog)
			{
				if (UndoLog.Count == 0)
					return new McpToolResult("There are no MCP changes to undo.");
				return new McpToolResult("Changes that can be undone, newest last:" + Environment.NewLine + string.Join(Environment.NewLine, UndoLog.Select(item => $"  {item.At:HH:mm:ss} {item.Label}")));
			}
		}

		(string Label, DateTime At, Action Undo) last;
		lock (UndoLog)
		{
			if (UndoLog.Count == 0)
				return new McpToolResult("There are no MCP changes to undo.", true);
			last = UndoLog[^1];
			UndoLog.RemoveAt(UndoLog.Count - 1);
		}
		last.Undo();
		return new McpToolResult($"Undid {last.Label} from {last.At:HH:mm:ss}.");
	}

	private const string UnsafeThemeName = "Theme names cannot contain slashes or colons, and cannot be . or ..";

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
				"edit_editor" => EditEditor(args),
				"find_in_editor" => FindInEditor(args),
				"undo_editor" => UndoEditor(args),
				"list_editor_files" => ListEditorFiles(args),
				"set_editor_language" => SetEditorLanguage(args),
				"delete_editor_file" => DeleteEditorFile(args),
				"undo_last_change" => UndoLastChange(args),
				"manage_mods" => ManageMods(args),
				"manage_rishade" => ManageRiShade(args),
				"read_voidstrap_file" => ReadVoidstrapFile(args),
				_ => new McpToolResult($"Unknown tool: {name}", true)
			};
		}
		catch (Exception ex)
		{
			return new McpToolResult($"Tool failed: {ex.Message}", true);
		}
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
}
