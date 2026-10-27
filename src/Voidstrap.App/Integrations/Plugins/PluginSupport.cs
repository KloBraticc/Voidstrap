using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using Voidstrap.Resources;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginConsent
{
	private static readonly object Gate = new();

	public static bool IsAllowed(PluginManifest manifest)
	{
		Dictionary<string, bool> consent = App.Settings.Prop.PluginConsent ??= [];
		lock (Gate)
		{
			if (consent.TryGetValue(manifest.Name, out bool allowed))
				return allowed;
		}
		System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.HasShutdownStarted)
			return false;
		bool answer = dispatcher.Invoke(() => Frontend.ShowMessageBox(
			string.Format(Strings.Plugins_TurnOnPrompt, manifest.Name, manifest.Description.Length > 0 ? manifest.Description : manifest.Name),
			MessageBoxImage.Question,
			MessageBoxButton.YesNo,
			MessageBoxResult.No) == MessageBoxResult.Yes);
		lock (Gate)
			consent[manifest.Name] = answer;
		App.Settings.SaveDeferred();
		App.Logger.WriteLine("VoidstrapMods", manifest.Name + ": " + (answer ? "turned on by the user" : "left off by the user"));
		return answer;
	}
}

internal static class PluginSettings
{
	private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
	{
		nameof(Voidstrap.Models.Persistable.AppSettings.PluginConsent)
	};

	private static PropertyInfo? Find(string name)
	{
		if (Blocked.Contains(name))
			return null;
		PropertyInfo? property = typeof(Voidstrap.Models.Persistable.AppSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
		if (property == null || !property.CanRead || !property.CanWrite)
			return null;
		Type type = property.PropertyType;
		return type == typeof(bool) || type == typeof(int) || type == typeof(double) || type == typeof(string) || type.IsEnum ? property : null;
	}

	public static string? Get(string name)
	{
		PropertyInfo? property = Find(name);
		if (property == null)
			return null;
		object? value = property.GetValue(App.Settings.Prop);
		JsonNode? node = value switch
		{
			bool flag => JsonValue.Create(flag),
			int number => JsonValue.Create(number),
			double number => JsonValue.Create(number),
			string text => JsonValue.Create(text),
			Enum option => JsonValue.Create(option.ToString()),
			_ => null
		};
		return node?.ToJsonString() ?? "null";
	}

	public static void Set(string plugin, string name, string json)
	{
		PropertyInfo property = Find(name) ?? throw new InvalidOperationException("the setting " + name + " does not exist or cannot be changed by mods");
		JsonValue value = JsonNode.Parse(json) as JsonValue ?? throw new InvalidOperationException("settings take a single value");
		Type type = property.PropertyType;
		object converted = type == typeof(bool) ? value.GetValue<bool>()
			: type == typeof(int) ? (int)value.GetValue<double>()
			: type == typeof(double) ? value.GetValue<double>()
			: type == typeof(string) ? value.GetValue<string>()
			: Enum.Parse(type, value.GetValue<string>(), ignoreCase: true);
		property.SetValue(App.Settings.Prop, converted);
		App.Settings.SaveDeferred();
		App.Logger.WriteLine("VoidstrapMods", plugin + ": changed the setting " + property.Name);
	}
}

internal sealed class PluginStore
{
	private const int MaxBytes = 1024 * 1024;

	private readonly object _gate = new();

	private readonly string _path;

	private JsonObject? _values;

	public PluginStore(string plugin)
	{
		string parent = Path.GetDirectoryName(Paths.Plugins) ?? Paths.Plugins;
		_path = Path.Combine(parent, "AppModData", plugin + ".json");
	}

	private JsonObject Values()
	{
		if (_values != null)
			return _values;
		try
		{
			_values = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
			_values = null;
		}
		return _values ??= new JsonObject();
	}

	public string? Get(string key)
	{
		lock (_gate)
			return Values()[key]?.ToJsonString();
	}

	public void Set(string key, string json)
	{
		lock (_gate)
		{
			JsonObject values = Values();
			values[key] = JsonNode.Parse(json);
			string text = values.ToJsonString();
			if (text.Length > MaxBytes)
			{
				values.Remove(key);
				throw new InvalidOperationException("the mod store is limited to 1 MB");
			}
			Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
			string temporary = _path + ".tmp";
			File.WriteAllText(temporary, text);
			File.Move(temporary, _path, true);
		}
	}
}

internal static class PluginSidebar
{
	public static JsonObject Get()
	{
		JsonArray items = new();
		foreach (var item in Voidstrap.UI.Elements.Settings.MainWindow.SidebarCustomizationItems)
		{
			if (!Voidstrap.UI.Elements.Settings.MainWindow.IsSidebarItemAvailable(item.Key))
				continue;
			items.Add(new JsonObject { ["key"] = item.Key, ["name"] = item.DisplayName, ["section"] = item.Section, ["canHide"] = item.CanHide });
		}
		var settings = App.Settings.Prop;
		return new JsonObject
		{
			["items"] = items,
			["order"] = new JsonArray((settings.SidebarOrder ?? []).Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
			["hidden"] = new JsonArray((settings.SidebarHiddenItems ?? []).Select(key => (JsonNode?)JsonValue.Create(key)).ToArray())
		};
	}

	public static void Set(string plugin, JsonObject layout)
	{
		var definitions = Voidstrap.UI.Elements.Settings.MainWindow.SidebarCustomizationItems.ToDictionary(item => item.Key, StringComparer.Ordinal);
		var settings = App.Settings.Prop;
		if (layout["order"] is JsonArray order)
			settings.SidebarOrder = order.Select(key => key?.ToString() ?? "").Where(definitions.ContainsKey).Distinct().ToList();
		if (layout["hidden"] is JsonArray hidden)
			settings.SidebarHiddenItems = hidden.Select(key => key?.ToString() ?? "").Where(key => definitions.TryGetValue(key, out var item) && item.CanHide).Distinct().ToList();
		App.Settings.SaveDeferred();
		App.Logger.WriteLine("VoidstrapMods", plugin + ": changed the sidebar layout");
		Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyToWindows));
	}

	private static void ApplyToWindows()
	{
		foreach (Voidstrap.UI.Elements.Settings.MainWindow window in Application.Current.Windows.OfType<Voidstrap.UI.Elements.Settings.MainWindow>())
			window.ApplySidebarCustomization();
	}
}
