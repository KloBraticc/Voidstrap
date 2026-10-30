using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Enums;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.Base;
using Voidstrap.UI.Elements.Settings;
using Wpf.Ui.Controls;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginConsent
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, TaskCompletionSource<bool?>> PendingPrompts = new(StringComparer.Ordinal);

    public static string PermissionKey(string mod, string permission) => mod + "#" + permission;

    public static bool HasGrant(string mod, string permission)
    {
        Dictionary<string, bool> consent = App.Settings.Prop.PluginConsent ??= [];
        lock (Gate)
            return consent.TryGetValue(PermissionKey(mod, permission), out bool granted) && granted;
    }

    internal static KeyValuePair<string, bool>[] GetStates()
    {
        lock (Gate)
            return (App.Settings.Prop.PluginConsent ?? []).ToArray();
    }

    public static void SetEnabledState(string name, bool enabled)
    {
        string accountKey = PermissionKey(name, PluginManifest.RobloxAccountPermission);
        lock (Gate)
        {
            Dictionary<string, bool> consent = new(App.Settings.Prop.PluginConsent ?? [], StringComparer.Ordinal);
            consent[name] = enabled;
            if (!enabled)
                consent.Remove(accountKey);
            else if (consent.TryGetValue(accountKey, out bool accountAllowed) && !accountAllowed)
                consent.Remove(accountKey);
            App.Settings.Prop.PluginConsent = consent;
        }
    }

    public static void Forget(string name)
    {
        lock (Gate)
        {
            Dictionary<string, bool> consent = new(App.Settings.Prop.PluginConsent ?? [], StringComparer.Ordinal);
            consent.Remove(name);
            consent.Remove(PermissionKey(name, PluginManifest.RobloxAccountPermission));
            App.Settings.Prop.PluginConsent = consent;
        }
    }

    public static string GetState(PluginManifest manifest)
    {
        Dictionary<string, bool> consent = App.Settings.Prop.PluginConsent ??= [];
        bool needsAccount = manifest.HasPermission(PluginManifest.RobloxAccountPermission);
        string accountKey = PermissionKey(manifest.Name, PluginManifest.RobloxAccountPermission);
        lock (Gate)
        {
            if (!consent.TryGetValue(manifest.Name, out bool allowed))
                return "not asked yet";
            if (!allowed)
                return "off";
            if (needsAccount && consent.TryGetValue(accountKey, out bool accountAllowed) && !accountAllowed)
                return "off";
            return "on";
        }
    }

    public static bool IsAllowed(PluginManifest manifest)
    {
        Task<bool> task = IsAllowedAsync(manifest);
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    private static readonly HashSet<string> PendingPowerful = new(StringComparer.Ordinal);

    public static async Task<bool> IsAllowedAsync(PluginManifest manifest)
    {
        bool allowed = await IsBaseAllowedAsync(manifest).ConfigureAwait(true);
        if (!allowed || !PluginNative.IsPowerful(manifest))
            return allowed;
        if (PluginNative.RefusalReason(manifest) is { } refusal)
        {
            App.Logger.WriteLine("VoidstrapMods", manifest.Name + ": refused: " + refusal);
            PluginHost.Problem(manifest.Name, refusal);
            return false;
        }
        if (PluginNative.IsApproved(manifest))
            return true;
        return await ConfirmPowerfulAsync(manifest).ConfigureAwait(true);
    }

    private static async Task<bool> ConfirmPowerfulAsync(PluginManifest manifest)
    {
        lock (PendingPowerful)
        {
            if (!PendingPowerful.Add(manifest.Name))
                return false;
        }
        bool answer = false;
        try
        {
            System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return false;
            answer = await dispatcher
                .InvokeAsync(() =>
                {
                    Voidstrap.UI.Elements.Dialogs.PowerfulModDialog dialog = new(manifest.Name, manifest.Version, manifest.Permissions);
                    dialog.ShowOwnedDialog();
                    return dialog.Confirmed;
                })
                .Task.ConfigureAwait(true);
        }
        catch
        {
            answer = false;
        }
        finally
        {
            lock (PendingPowerful)
                PendingPowerful.Remove(manifest.Name);
        }
        lock (Gate)
        {
            Dictionary<string, bool> updated = new(App.Settings.Prop.PluginConsent ?? [], StringComparer.Ordinal);
            foreach (string key in updated.Keys.Where(key => key.StartsWith(manifest.Name + "#powerful:", StringComparison.Ordinal)).ToList())
                updated.Remove(key);
            updated[manifest.Name] = answer;
            if (answer)
                updated[PermissionKey(manifest.Name, PluginNative.GrantName(manifest))] = true;
            App.Settings.Prop.PluginConsent = updated;
        }
        App.Settings.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", manifest.Name + ": extra permissions " + (answer ? "approved" : "declined") + " by the user");
        return answer;
    }

    private static async Task<bool> IsBaseAllowedAsync(PluginManifest manifest)
    {
        Dictionary<string, bool> consent = App.Settings.Prop.PluginConsent ??= [];
        bool needsAccount = manifest.HasPermission(PluginManifest.RobloxAccountPermission);
        string accountKey = PermissionKey(manifest.Name, PluginManifest.RobloxAccountPermission);
        bool alreadyEnabled = false;
        lock (Gate)
        {
            if (consent.TryGetValue(manifest.Name, out bool allowed))
            {
                if (!allowed)
                    return false;
                if (!needsAccount)
                    return true;
                if (consent.TryGetValue(accountKey, out bool accountAllowed))
                    return accountAllowed;
                alreadyEnabled = true;
            }
        }
        TaskCompletionSource<bool?> pendingBox = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool owner;
        lock (PendingPrompts)
        {
            if (!PendingPrompts.TryGetValue(manifest.Name, out TaskCompletionSource<bool?>? existing))
            {
                PendingPrompts[manifest.Name] = pendingBox;
                owner = true;
            }
            else
            {
                pendingBox = existing;
                owner = false;
            }
        }
        if (!owner)
            return await pendingBox.Task.ConfigureAwait(true) == true;
        bool answer = false;
        try
        {
            System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted)
                return false;
            string prompt = alreadyEnabled
                ? manifest.Name + Environment.NewLine + Environment.NewLine + Strings.Plugins_RobloxAccountWarning
                : string.Format(
                    Strings.Plugins_TurnOnPrompt,
                    manifest.Name,
                    manifest.Description.Length > 0 ? manifest.Description : manifest.Name
                ) + (needsAccount ? Environment.NewLine + Environment.NewLine + Strings.Plugins_RobloxAccountWarning : "");
            answer = await dispatcher
                .InvokeAsync(() =>
                    Frontend.ShowMessageBox(
                        prompt,
                        needsAccount ? MessageBoxImage.Warning : MessageBoxImage.Question,
                        MessageBoxButton.YesNo,
                        MessageBoxResult.No
                    ) == MessageBoxResult.Yes
                )
                .Task.ConfigureAwait(true);
        }
        catch
        {
            answer = false;
        }
        finally
        {
            lock (PendingPrompts)
                PendingPrompts.Remove(manifest.Name);
            pendingBox.TrySetResult(answer);
        }
        lock (Gate)
        {
            Dictionary<string, bool> updated = new(App.Settings.Prop.PluginConsent ?? [], StringComparer.Ordinal);
            updated[manifest.Name] = answer;
            if (needsAccount)
                updated[accountKey] = answer;
            else
                updated.Remove(accountKey);
            App.Settings.Prop.PluginConsent = updated;
        }
        App.Settings.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", manifest.Name + ": " + (answer ? "turned on by the user" : "left off by the user"));
        return answer;
    }
}

internal static partial class PluginSettings
{
    [System.Text.RegularExpressions.GeneratedRegex(
        "(Path|Url|Uri|Folder|Directory|Launcher|Command)$|^(Channel|ChannelHash|VinegarChannel|LastServerSave)$|Consent|Rollback|Token|Cookie|Password|Secret|ModRollback",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase
    )]
    private static partial System.Text.RegularExpressions.Regex SensitiveSetting();

    public static void RejectRemote(string value)
    {
        string trimmed = value.Trim();
        bool startsWithSeparators = trimmed.Length >= 2 && trimmed[0] is '/' or '\\' && trimmed[1] is '/' or '\\';
        if (startsWithSeparators || trimmed.Contains("://", StringComparison.Ordinal) || trimmed.Contains("\\\\", StringComparison.Ordinal))
            throw new InvalidOperationException("mods cannot use network paths or web addresses here");
    }

    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(Voidstrap.Models.Persistable.AppSettings.PluginConsent),
        nameof(Voidstrap.Models.Persistable.AppSettings.ModRollback),
        nameof(Voidstrap.Models.Persistable.AppSettings.CheckForUpdates),
        nameof(Voidstrap.Models.Persistable.AppSettings.BackgroundUpdatesEnabled),
        nameof(Voidstrap.Models.Persistable.AppSettings.AllowPreReleaseUpdates),
        nameof(Voidstrap.Models.Persistable.AppSettings.UpdateRoblox),
        nameof(Voidstrap.Models.Persistable.AppSettings.ConfirmLaunches),
        nameof(Voidstrap.Models.Persistable.AppSettings.LaunchWithoutVoidstrap),
        nameof(Voidstrap.Models.Persistable.AppSettings.RobloxLaunchAtStartup),
        nameof(Voidstrap.Models.Persistable.AppSettings.FrameGenAutoElevate),
    };

    private static readonly HashSet<string> AllowedText = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(Voidstrap.Models.Persistable.AppSettings.CustomAccentColor),
    };

    private static bool IsAllowedType(PropertyInfo property)
    {
        Type type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(double) || type.IsEnum || AllowedText.Contains(property.Name);
    }

    private static PropertyInfo? Find(string name)
    {
        if (Blocked.Contains(name) || SensitiveSetting().IsMatch(name))
            return null;
        PropertyInfo? property = typeof(Voidstrap.Models.Persistable.AppSettings).GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
        );
        if (property == null || !property.CanRead || !property.CanWrite || !IsAllowedType(property))
            return null;
        return property;
    }

    private static JsonNode? SerializeValue(object? value)
    {
        if (value == null)
            return null;
        return value switch
        {
            bool flag => JsonValue.Create(flag),
            int number => JsonValue.Create(number),
            long number => JsonValue.Create(number),
            double number => double.IsFinite(number) ? JsonValue.Create(number) : null,
            string text => JsonValue.Create(text),
            Enum option => JsonValue.Create(option.ToString()),
            _ => SafeSerializeComplex(value),
        };
    }

    private static JsonNode? SafeSerializeComplex(object value)
    {
        try
        {
            return JsonSerializer.SerializeToNode(value);
        }
        catch
        {
            return JsonValue.Create(value.ToString());
        }
    }

    public static string List()
    {
        JsonObject result = new();
        foreach (
            PropertyInfo prop in typeof(Voidstrap.Models.Persistable.AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
        )
        {
            if (Find(prop.Name) == null)
                continue;
            try
            {
                result[prop.Name] = SerializeValue(prop.GetValue(App.Settings.Prop));
            }
            catch { }
        }
        return result.ToJsonString();
    }

    public static string? Get(string name)
    {
        PropertyInfo? property = Find(name);
        if (property == null)
            return null;
        object? value = property.GetValue(App.Settings.Prop);
        JsonNode? node = SerializeValue(value);
        return node?.ToJsonString() ?? "null";
    }

    public static void Set(string plugin, string name, string json)
    {
        PropertyInfo property =
            Find(name) ?? throw new InvalidOperationException("the setting " + name + " does not exist or cannot be changed by mods");
        JsonNode? parsed = JsonNode.Parse(json);
        if (parsed is JsonValue textValue && textValue.TryGetValue(out string? text))
            RejectRemote(text);
        Type type = property.PropertyType;
        object? converted;
        if (
            parsed is JsonValue value
            && (
                type == typeof(bool)
                || type == typeof(int)
                || type == typeof(long)
                || type == typeof(double)
                || type == typeof(string)
                || type.IsEnum
            )
        )
        {
            converted =
                type == typeof(bool) ? value.GetValue<bool>()
                : type == typeof(int) ? (int)value.GetValue<double>()
                : type == typeof(long) ? (long)value.GetValue<double>()
                : type == typeof(double) ? value.GetValue<double>()
                : type == typeof(string) ? value.GetValue<string>()
                : Enum.Parse(type, value.GetValue<string>(), ignoreCase: true);
        }
        else
        {
            converted =
                JsonSerializer.Deserialize(json, type)
                ?? throw new InvalidOperationException("the value could not be converted for setting " + property.Name);
        }
        property.SetValue(App.Settings.Prop, converted);
        App.Settings.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", plugin + ": changed the setting " + property.Name);
        PluginTheme.RefreshWindowsIfVisualSetting(property.Name);
        JsonObject changed = new() { ["name"] = property.Name, ["plugin"] = plugin };
        _ = Task.Run(() => PluginHost.Invoke("settings.changed", changed));
    }
}

internal static class PluginFastFlags
{
    public static string GetAll()
    {
        JsonObject flags = new();
        foreach (KeyValuePair<string, object> pair in App.FastFlags.Prop.ToArray())
            flags[pair.Key] = JsonValue.Create(pair.Value?.ToString() ?? "");
        return flags.ToJsonString();
    }

    public static string? Get(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string? val =
            App.FastFlags.GetValue(name) ?? (FastFlagManager.PresetFlags.ContainsKey(name) ? App.FastFlags.GetPreset(name) : null);
        return val != null ? JsonValue.Create(val)?.ToJsonString() : null;
    }

    public static void Set(string plugin, string name, string json)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("a FastFlag name is required");
        JsonNode? node = JsonNode.Parse(json);
        object? raw =
            node == null ? null
            : node is JsonValue v && v.TryGetValue(out bool b) ? (b ? "True" : "False")
            : node.ToString();
        if (FastFlagManager.PresetFlags.ContainsKey(name))
            App.FastFlags.SetPreset(name, raw);
        else
            App.FastFlags.SetValue(name, raw);
        App.FastFlags.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", plugin + ": updated FastFlag " + name);
    }

    public static void Remove(string plugin, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        if (FastFlagManager.PresetFlags.ContainsKey(name))
            App.FastFlags.SetPreset(name, null);
        else
            App.FastFlags.SetValue(name, null);
        App.FastFlags.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", plugin + ": removed FastFlag " + name);
    }

    public static void Clear(string plugin)
    {
        App.FastFlags.Prop.Clear();
        App.FastFlags.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", plugin + ": cleared all FastFlags");
    }
}

internal static class PluginOptions
{
    private static string PathFor(string plugin) =>
        Path.Combine(Path.GetDirectoryName(Paths.Plugins) ?? Paths.Plugins, "AppModData", plugin + ".options.json");

    public static JsonObject Effective(PluginManifest manifest)
    {
        JsonObject merged = (JsonObject)manifest.Options.DeepClone();
        try
        {
            string path = PathFor(manifest.Name);
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject saved)
                foreach (KeyValuePair<string, JsonNode?> field in saved)
                    if (merged.ContainsKey(field.Key))
                        merged[field.Key] = field.Value?.DeepClone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            App.Logger.WriteLine("VoidstrapMods", manifest.Name + ": the saved settings could not be read: " + ex.Message);
        }
        return merged;
    }

    public static void Save(string plugin, JsonObject values)
    {
        string path = PathFor(plugin);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", values.ToJsonString());
        File.Move(path + ".tmp", path, true);
    }

    public static void Delete(string plugin)
    {
        if (File.Exists(PathFor(plugin)))
            File.Delete(PathFor(plugin));
    }

    public static void ShowEditor(PluginManifest manifest)
    {
        JsonObject current = Effective(manifest);
        StackPanel fields = new() { Margin = new Thickness(16) };
        List<(string Key, Func<JsonNode?> Read)> readers = [];
        foreach (KeyValuePair<string, JsonNode?> field in manifest.ConfigFields)
        {
            JsonObject spec = field.Value as JsonObject ?? new JsonObject();
            string label = spec["title"]?.ToString() ?? spec["label"]?.ToString() ?? field.Key;
            JsonNode? value = current[field.Key];
            string type =
                spec["type"]?.ToString()
                ?? (
                    value is JsonValue v
                        ? (
                            v.TryGetValue(out bool _) ? "boolean"
                            : v.TryGetValue(out double _) ? "number"
                            : "string"
                        )
                        : "string"
                );
            if (type == "boolean")
            {
                CheckBox box = new()
                {
                    Content = label,
                    IsChecked = value is JsonValue b && b.TryGetValue(out bool on) && on,
                    Margin = new Thickness(0, 0, 0, 12),
                };
                fields.Children.Add(box);
                readers.Add((field.Key, () => JsonValue.Create(box.IsChecked == true)));
                continue;
            }
            fields.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
            if (spec["description"]?.ToString() is string description)
            {
                TextBlock hint = new()
                {
                    Text = description,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4),
                };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                fields.Children.Add(hint);
            }
            if (spec["options"] is JsonArray options)
            {
                ComboBox combo = new() { Margin = new Thickness(0, 0, 0, 12) };
                foreach (JsonNode? option in options)
                    combo.Items.Add(option?.ToString() ?? "");
                combo.SelectedItem = value?.ToString();
                fields.Children.Add(combo);
                readers.Add((field.Key, () => JsonValue.Create(combo.SelectedItem as string)));
                continue;
            }
            System.Windows.Controls.TextBox input = new() { Text = value?.ToString() ?? "", Margin = new Thickness(0, 0, 0, 12) };
            fields.Children.Add(input);
            JsonNode? fallback = value?.DeepClone();
            readers.Add(
                (
                    field.Key,
                    () =>
                        type != "number" ? JsonValue.Create(input.Text)
                        : double.TryParse(input.Text, System.Globalization.CultureInfo.InvariantCulture, out double number)
                        && double.IsFinite(number)
                            ? JsonValue.Create(number)
                        : fallback?.DeepClone()
                )
            );
        }
        Wpf.Ui.Controls.Button save = new()
        {
            Content = Strings.Common_Save,
            Appearance = Wpf.Ui.Common.ControlAppearance.Primary,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        fields.Children.Add(save);
        PluginDialogManager.ModDialogWindow window = new()
        {
            Title = manifest.Name,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MaxHeight = 700,
            ExtendsContentIntoTitleBar = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        window.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
        Grid root = new();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(
            new TitleBar
            {
                Title = manifest.Name,
                Padding = new Thickness(8),
                ForceShutdown = false,
                MinimizeToTray = false,
                ShowMinimize = false,
                ShowMaximize = false,
            }
        );
        ScrollViewer scroll = new() { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        window.Content = root;
        save.Click += (_, _) =>
        {
            JsonObject values = new();
            foreach ((string key, Func<JsonNode?> read) in readers)
                values[key] = read();
            try
            {
                Save(manifest.Name, values);
                _ = Task.Run(() => PluginHost.RestartMod(manifest.Name));
                window.Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
            }
        };
        window.ShowOwnedDialog();
    }
}

internal sealed class PluginStore
{
    public static void DeleteData(string plugin)
    {
        string path = Path.Combine(Path.GetDirectoryName(Paths.Plugins) ?? Paths.Plugins, "AppModData", plugin + ".json");
        if (File.Exists(path))
            File.Delete(path);
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
            return Values().Select(pair => pair.Key).ToList();
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            if (!Values().Remove(key))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", Values().ToJsonString());
            File.Move(_path + ".tmp", _path, true);
        }
    }

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

public static class PluginSidebar
{
    private static readonly object Gate = new();

    private static int _applyQueued;

    private static readonly List<ModPageInfo> ModPages = [];

    public sealed record ModPageInfo(string Plugin, string Id, string Key, string Title, string Section, string Icon);

    public static IReadOnlyList<ModPageInfo> GetModPages()
    {
        lock (Gate)
            return ModPages.ToList();
    }

    public static void RemovePluginPages(HashSet<string> activePlugins)
    {
        bool removed;
        lock (Gate)
            removed = ModPages.RemoveAll(page => !activePlugins.Contains(page.Plugin)) > 0;
        if (removed)
            Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyToWindows));
    }

    public static string MakePageKey(string plugin, string id)
    {
        string raw = "ModPage_" + plugin + "_" + id;
        char[] chars = raw.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_'))
                chars[i] = '_';
        }
        return new string(chars);
    }

    public static JsonObject AddPage(string plugin, JsonObject spec)
    {
        string id = (spec["id"]?.ToString() ?? "").Trim();
        if (id.Length == 0 || id.Length > 48)
            throw new InvalidOperationException("sidebar page id must be 1 to 48 characters");
        string title = MainWindow.NormalizeSidebarName(spec["title"]?.ToString(), id);
        string section = (spec["section"]?.ToString() ?? "Core").Trim();
        if (section is not ("Core" or "Configuration" or "More" or "Footer"))
            section = "Core";
        string icon = (spec["icon"]?.ToString() ?? "Apps24").Trim();
        if (icon.Length == 0)
            icon = "Apps24";
        string key = MakePageKey(plugin, id);
        ModPageInfo info = new(plugin, id, key, title, section, icon);
        lock (Gate)
        {
            int existing = ModPages.FindIndex(p => p.Plugin == plugin && p.Id == id);
            if (existing >= 0)
                ModPages[existing] = info;
            else
                ModPages.Add(info);
        }
        App.Logger.WriteLine("VoidstrapMods", plugin + ": added sidebar page " + id);
        Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyToWindows));
        return new JsonObject
        {
            ["key"] = key,
            ["id"] = id,
            ["title"] = title,
            ["section"] = section,
            ["icon"] = icon,
        };
    }

    public static void RemovePage(string plugin, string id)
    {
        bool changed;
        lock (Gate)
            changed = ModPages.RemoveAll(p => p.Plugin == plugin && p.Id == id) > 0;
        if (changed)
            Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyToWindows));
    }

    public static JsonObject Get()
    {
        var settings = App.Settings.Prop;
        Dictionary<string, string> customNames = settings.SidebarNames ?? [];
        Dictionary<string, string> customIcons = settings.SidebarIcons ?? [];
        JsonArray items = new();
        foreach (var item in MainWindow.SidebarCustomizationItems)
        {
            if (!MainWindow.IsSidebarItemAvailable(item.Key))
                continue;
            string currentName = customNames.TryGetValue(item.Key, out string? n) && !string.IsNullOrWhiteSpace(n) ? n : item.DisplayName;
            string currentIcon =
                customIcons.TryGetValue(item.Key, out string? ic) && !string.IsNullOrWhiteSpace(ic) ? ic : item.DefaultIcon.ToString();
            items.Add(
                new JsonObject
                {
                    ["key"] = item.Key,
                    ["name"] = currentName,
                    ["defaultName"] = item.DisplayName,
                    ["section"] = item.Section,
                    ["icon"] = currentIcon,
                    ["canHide"] = item.CanHide,
                    ["isCustom"] = false,
                }
            );
        }
        JsonArray pages = new();
        foreach (ModPageInfo modPage in GetModPages())
        {
            JsonObject pageObj = new()
            {
                ["key"] = modPage.Key,
                ["id"] = modPage.Id,
                ["plugin"] = modPage.Plugin,
                ["name"] = modPage.Title,
                ["section"] = modPage.Section,
                ["icon"] = modPage.Icon,
                ["canHide"] = true,
                ["isCustom"] = true,
            };
            items.Add(pageObj);
            pages.Add(pageObj.DeepClone());
        }
        JsonObject namesObj = new();
        foreach (KeyValuePair<string, string> pair in customNames)
            namesObj[pair.Key] = pair.Value;
        JsonObject iconsObj = new();
        foreach (KeyValuePair<string, string> pair in customIcons)
            iconsObj[pair.Key] = pair.Value;
        return new JsonObject
        {
            ["items"] = items,
            ["pages"] = pages,
            ["order"] = new JsonArray((settings.SidebarOrder ?? []).Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
            ["hidden"] = new JsonArray((settings.SidebarHiddenItems ?? []).Select(key => (JsonNode?)JsonValue.Create(key)).ToArray()),
            ["names"] = namesObj,
            ["icons"] = iconsObj,
        };
    }

    public static void Set(string plugin, JsonObject layout)
    {
        var definitions = MainWindow.SidebarCustomizationItems.ToDictionary(item => item.Key, StringComparer.Ordinal);
        HashSet<string> customKeys = GetModPages().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var settings = App.Settings.Prop;
        if (layout["order"] is JsonArray order)
            settings.SidebarOrder = order
                .Select(key => key?.ToString() ?? "")
                .Where(k => definitions.ContainsKey(k) || customKeys.Contains(k))
                .Distinct()
                .ToList();
        if (layout["hidden"] is JsonArray hidden)
            settings.SidebarHiddenItems = hidden
                .Select(key => key?.ToString() ?? "")
                .Where(key => (definitions.TryGetValue(key, out var item) && item.CanHide) || customKeys.Contains(key))
                .Distinct()
                .ToList();
        if (layout["names"] is JsonObject names)
        {
            Dictionary<string, string> target = settings.SidebarNames ??= [];
            foreach (KeyValuePair<string, JsonNode?> pair in names)
            {
                if (!definitions.ContainsKey(pair.Key) && !customKeys.Contains(pair.Key))
                    continue;
                string val = pair.Value?.ToString().Trim() ?? "";
                if (val.Length == 0)
                    target.Remove(pair.Key);
                else
                    target[pair.Key] = MainWindow.NormalizeSidebarName(val, pair.Key);
            }
        }
        if (layout["icons"] is JsonObject icons)
        {
            Dictionary<string, string> target = settings.SidebarIcons ??= [];
            foreach (KeyValuePair<string, JsonNode?> pair in icons)
            {
                if (!definitions.ContainsKey(pair.Key) && !customKeys.Contains(pair.Key))
                    continue;
                string val = pair.Value?.ToString().Trim() ?? "";
                if (val.Length == 0)
                    target.Remove(pair.Key);
                else
                    target[pair.Key] = val;
            }
        }
        App.Settings.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", plugin + ": changed the sidebar layout");
        Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyToWindows));
    }

    public static void Reset(string plugin)
    {
        var settings = App.Settings.Prop;
        settings.SidebarOrder = [];
        settings.SidebarHiddenItems = [];
        settings.SidebarNames = [];
        settings.SidebarIcons = [];
        settings.SidebarIconImages = [];
        App.Settings.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", plugin + ": reset the sidebar layout");
        Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyToWindows));
    }

    public static void ApplyToWindows()
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || System.Threading.Interlocked.Exchange(ref _applyQueued, 1) != 0)
            return;
        dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(ApplyQueued));
    }

    private static void ApplyQueued()
    {
        System.Threading.Interlocked.Exchange(ref _applyQueued, 0);
        if (Application.Current == null)
            return;
        foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
            window.ApplySidebarCustomization();
    }
}

public static class PluginModPages
{
    private static readonly object Gate = new();

    private static readonly List<ModPageEntry> Entries = [];

    public sealed record ModPageEntry(string Plugin, string Id, string Key, string Title, string Icon, bool IsAction = false);

    public static event Action? Changed;

    public static IReadOnlyList<ModPageEntry> GetEntries()
    {
        lock (Gate)
            return Entries.ToList();
    }

    public static void ClearAll()
    {
        bool removed;
        lock (Gate)
        {
            removed = Entries.Count > 0;
            Entries.Clear();
        }
        if (removed)
            NotifyWindows();
    }

    internal static void RemovePlugins(IReadOnlySet<string> plugins)
    {
        bool removed;
        lock (Gate)
            removed = Entries.RemoveAll(entry => plugins.Contains(entry.Plugin)) > 0;
        if (removed)
            NotifyWindows();
    }

    private const long MaxIconBytes = 2L * 1024 * 1024;

    public static FrameworkElement CreateIcon(ModPageEntry entry)
    {
        const string LOG_IDENT = "PluginModPages::CreateIcon";
        try
        {
            if (entry.Icon.StartsWith("path:", StringComparison.Ordinal))
                return CreateVectorIcon(entry.Icon["path:".Length..]);
            if (entry.Icon.Contains('.'))
                return CreateImageIcon(entry);
        }
        catch (Exception ex) when (ex is FormatException or IOException or NotSupportedException or ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            App.Logger.WriteLine(LOG_IDENT, entry.Plugin + ": the menu icon could not be loaded: " + ex.Message);
            return new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Common.SymbolRegular.PuzzlePiece24, FontSize = 16 };
        }
        return new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Enum.TryParse(entry.Icon, ignoreCase: true, out Wpf.Ui.Common.SymbolRegular symbol) ? symbol : Wpf.Ui.Common.SymbolRegular.PuzzlePiece24,
            FontSize = 16,
        };
    }

    private static FrameworkElement CreateVectorIcon(string data)
    {
        System.Windows.Shapes.Path path = new() { Data = System.Windows.Media.Geometry.Parse(data), Stretch = System.Windows.Media.Stretch.Uniform, Width = 16, Height = 16 };
        path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextFillColorPrimaryBrush");
        return path;
    }

    private static FrameworkElement CreateImageIcon(ModPageEntry entry)
    {
        string file =
            PluginMedia.ResolveLocalPath(System.IO.Path.Combine(Paths.Plugins, entry.Plugin), entry.Icon)
            ?? throw new InvalidOperationException("the icon must be a file inside the mod");
        if (new FileInfo(file).Length > MaxIconBytes)
            throw new InvalidOperationException("the icon is larger than 2 MB");
        System.Windows.Media.Imaging.BitmapImage bitmap = new();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 48;
        bitmap.UriSource = new Uri(file);
        bitmap.EndInit();
        bitmap.Freeze();
        return new System.Windows.Controls.Image { Source = bitmap, Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.Uniform };
    }

    public static JsonObject AddPage(string plugin, JsonObject spec)
    {
        string id = (spec["id"]?.ToString() ?? "").Trim();
        if (id.Length == 0 || id.Length > 48)
            throw new InvalidOperationException("mod page id must be 1 to 48 characters");
        string title = MainWindow.NormalizeSidebarName(spec["title"]?.ToString(), id);
        string icon = (spec["icon"]?.ToString() ?? "PuzzlePiece24").Trim();
        if (icon.Length == 0)
            icon = "PuzzlePiece24";
        string key = "ModMenu_" + PluginSidebar.MakePageKey(plugin, id);
        ModPageEntry entry = new(plugin, id, key, title, icon, spec["action"] is JsonValue action && action.TryGetValue(out bool isAction) && isAction);
        bool changed;
        lock (Gate)
        {
            int existing = Entries.FindIndex(p => p.Plugin == plugin && p.Id == id);
            changed = existing < 0 || Entries[existing].Title != title || Entries[existing].Icon != icon;
            if (existing >= 0)
                Entries[existing] = entry;
            else
                Entries.Add(entry);
        }
        App.Logger.WriteLine("VoidstrapMods", plugin + ": added mod page " + id);
        if (changed)
            NotifyWindows();
        return new JsonObject
        {
            ["key"] = key,
            ["id"] = id,
            ["title"] = title,
            ["icon"] = icon,
        };
    }

    public static void RemovePage(string plugin, string id)
    {
        bool changed;
        lock (Gate)
            changed = Entries.RemoveAll(p => p.Plugin == plugin && p.Id == id) > 0;
        if (changed)
            NotifyWindows();
    }

    public static string ListJson()
    {
        JsonArray list = [];
        lock (Gate)
        {
            foreach (ModPageEntry entry in Entries)
                list.Add(
                    new JsonObject
                    {
                        ["key"] = entry.Key,
                        ["id"] = entry.Id,
                        ["plugin"] = entry.Plugin,
                        ["title"] = entry.Title,
                        ["icon"] = entry.Icon,
                    }
                );
        }
        return list.ToJsonString();
    }

    private static void NotifyWindows()
    {
        Changed?.Invoke();
        Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyToWindows));
    }

    public static void ApplyToWindows()
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ApplyToWindows));
            return;
        }
        if (Application.Current == null)
            return;
        foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
            window.ApplyModPagesMenu();
    }
}

internal static class PluginNavigation
{
    private static readonly string[] BuiltInPages =
    [
        "Home",
        "Integrations",
        "Deployment",
        "Appearance",
        "FastFlagSettings",
        "FastFlagEditor",
        "Mods",
        "Global",
        "Shortcuts",
        "News",
        "Extensions",
        "Manager",
        "Settings",
        "VoidstrapMods",
        "Library",
        "PrivateServers",
        "Accounts",
        "More",
        "ServerBrowser",
    ];

    public static T InvokeUiBounded<T>(Func<T> action, T fallback, int timeoutMs = 200)
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return fallback;
        if (dispatcher.CheckAccess())
        {
            try
            {
                return action();
            }
            catch
            {
                return fallback;
            }
        }
        try
        {
            System.Windows.Threading.DispatcherOperation<T> op = dispatcher.InvokeAsync(action);
            if (op.Wait(TimeSpan.FromMilliseconds(timeoutMs)) == System.Windows.Threading.DispatcherOperationStatus.Completed)
                return op.Result;
        }
        catch { }
        return fallback;
    }

    public static string Current()
    {
        return InvokeUiBounded(
            () =>
            {
                MainWindow? window = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
                return window?.GetCurrentPageKey() ?? "";
            },
            ""
        );
    }

    public static string Pages()
    {
        JsonArray list = new();
        foreach (string page in BuiltInPages)
            list.Add(page);
        foreach (PluginSidebar.ModPageInfo modPage in PluginSidebar.GetModPages())
            list.Add("mod:" + modPage.Plugin + ":" + modPage.Id);
        foreach (PluginModPages.ModPageEntry modEntry in PluginModPages.GetEntries())
            list.Add("mod:" + modEntry.Plugin + ":" + modEntry.Id);
        return list.ToJsonString();
    }

    public static void Open(string plugin, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;
        App.Logger.WriteLine("VoidstrapMods", plugin + ": navigating to " + target);
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (Application.Current == null)
                    return;
                foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
                    window.NavigateFromPlugin(target, plugin);
            })
        );
    }
}

internal static class PluginTheme
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, object?> OriginalResources = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ActiveBrushOverrides = new(StringComparer.Ordinal);

    private static readonly string[] CommonBrushKeys =
    [
        "ApplicationBackgroundBrush",
        "WindowBackgroundColorPrimary",
        "WindowBackgroundColorSecondary",
        "WindowBackgroundColorThird",
        "LayerFillColorDefaultBrush",
        "CardBackgroundFillColorDefaultBrush",
        "CardStrokeColorDefaultBrush",
        "SubtleFillColorSecondaryBrush",
        "ControlFillColorDefaultBrush",
        "ControlFillColorSecondaryBrush",
        "ControlStrokeColorDefaultBrush",
        "TextFillColorPrimaryBrush",
        "TextFillColorSecondaryBrush",
        "TextFillColorTertiaryBrush",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "SystemFillColorSuccessBrush",
        "SystemFillColorCautionBrush",
        "SystemFillColorCriticalBrush",
    ];

    public static string Get()
    {
        var prop = App.Settings.Prop;
        Voidstrap.UI.GlobalBackground.State bg = Voidstrap.UI.GlobalBackground.Current;
        JsonObject brushes = new();
        lock (Gate)
        {
            foreach (KeyValuePair<string, string> pair in ActiveBrushOverrides)
                brushes[pair.Key] = pair.Value;
        }
        return new JsonObject
        {
            ["theme"] = prop.Theme2.ToString(),
            ["backdrop"] = prop.WindowBackdrop.ToString(),
            ["uiZoomPercent"] = prop.UiZoomPercent,
            ["backgroundFilePath"] = bg.FilePath ?? "",
            ["gradientOpacity"] = bg.GradientOpacity,
            ["blackOverlayOpacity"] = bg.BlackOverlayOpacity,
            ["displayEverywhere"] = bg.DisplayEverywhere,
            ["customThemeColor"] = prop.CustomAccentColor ?? "",
            ["brushes"] = brushes,
        }.ToJsonString();
    }

    public static void Set(string plugin, JsonObject spec)
    {
        var prop = App.Settings.Prop;
        Voidstrap.UI.GlobalBackground.State bg = Voidstrap.UI.GlobalBackground.Current;
        string? nextBgFile = bg.FilePath;
        double nextGrad = bg.GradientOpacity;
        double nextBlack = bg.BlackOverlayOpacity;
        bool nextDisp = bg.DisplayEverywhere;
        bool changedTheme = false;
        bool changedZoom = false;
        bool changedBackground = false;
        if (
            spec["theme"]?.ToString() is { Length: > 0 } themeStr
            && Enum.TryParse(themeStr, ignoreCase: true, out Voidstrap.Enums.Theme parsedTheme)
        )
        {
            prop.Theme2 = parsedTheme;
            changedTheme = true;
        }
        if (
            spec["backdrop"]?.ToString() is { Length: > 0 } backdropStr
            && Enum.TryParse(backdropStr, ignoreCase: true, out Voidstrap.Models.BackdropType parsedBackdrop)
        )
        {
            prop.WindowBackdrop = parsedBackdrop;
            changedTheme = true;
        }
        if (spec["uiZoomPercent"] is JsonValue zoomVal && zoomVal.TryGetValue(out double zoom))
        {
            prop.UiZoomPercent = Math.Clamp((int)Math.Round(zoom), 50, 200);
            changedZoom = true;
        }
        if (spec["backgroundFilePath"] != null)
        {
            nextBgFile = spec["backgroundFilePath"]?.ToString() ?? "";
            changedBackground = true;
        }
        if (spec["gradientOpacity"] is JsonValue gradVal && gradVal.TryGetValue(out double grad))
        {
            nextGrad = Math.Clamp(grad, 0.0, 1.0);
            changedBackground = true;
        }
        if (spec["blackOverlayOpacity"] is JsonValue blackVal && blackVal.TryGetValue(out double black))
        {
            nextBlack = Math.Clamp(black, 0.0, 1.0);
            changedBackground = true;
        }
        if (spec["displayEverywhere"] is JsonValue dispVal && dispVal.TryGetValue(out bool disp))
        {
            nextDisp = disp;
            changedBackground = true;
        }
        if (spec["customThemeColor"]?.ToString() is { Length: > 0 } ctc)
        {
            prop.CustomAccentColor = ctc;
            changedTheme = true;
        }
        if (spec["accentColor"]?.ToString() is { Length: > 0 } accentHex)
        {
            prop.CustomAccentColor = accentHex;
            SetBrush(plugin, "AccentFillColorDefaultBrush", accentHex);
            SetBrush(plugin, "AccentFillColorSecondaryBrush", accentHex);
            SetBrush(plugin, "AccentFillColorTertiaryBrush", accentHex);
        }
        if (changedBackground)
            Voidstrap.UI.GlobalBackground.Update(nextBgFile, nextGrad, nextBlack, nextDisp);
        App.Settings.SaveDeferred();
        App.Logger.WriteLine("VoidstrapMods", plugin + ": updated application theme settings");
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (Application.Current == null)
                    return;
                if (changedTheme)
                {
                    foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
                        window.ApplyTheme();
                }
                if (changedZoom)
                    MainWindow.ApplyUiZoomToOpenWindows();
                if (changedBackground)
                    Voidstrap.UI.GlobalBackground.Reload();
            })
        );
    }

    public static string? GetBrush(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        lock (Gate)
        {
            if (ActiveBrushOverrides.TryGetValue(key, out string? activeHex))
                return activeHex;
        }
        return PluginNavigation.InvokeUiBounded(() => ReadResourceHex(key), null);
    }

    public static string ListBrushes()
    {
        return PluginNavigation.InvokeUiBounded(
            () =>
            {
                JsonObject obj = new();
                foreach (string key in CommonBrushKeys)
                {
                    if (ReadResourceHex(key) is string hex)
                        obj[key] = hex;
                }
                lock (Gate)
                {
                    foreach (KeyValuePair<string, string> pair in ActiveBrushOverrides)
                        obj[pair.Key] = pair.Value;
                }
                return obj.ToJsonString();
            },
            "{}"
        );
    }

    private static string? ReadResourceHex(string key)
    {
        object? res = Application.Current?.TryFindResource(key);
        if (res == null)
        {
            MainWindow? win = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
            res = win?.TryFindResource(key);
        }
        if (res is SolidColorBrush brush)
            return FormatColor(brush.Color);
        if (res is Color color)
            return FormatColor(color);
        return null;
    }

    private static string FormatColor(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static void SetBrush(string plugin, string key, string hexColor)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(hexColor))
            return;
        Color parsed;
        try
        {
            if (ColorConverter.ConvertFromString(hexColor.Trim()) is not Color c)
                throw new InvalidOperationException("invalid color " + hexColor);
            parsed = c;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("invalid color " + hexColor + ": " + ex.Message);
        }
        string formatted = FormatColor(parsed);
        lock (Gate)
            ActiveBrushOverrides[key] = formatted;
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (Application.Current == null)
                    return;
                lock (Gate)
                {
                    if (!OriginalResources.ContainsKey(key))
                        OriginalResources[key] = Application.Current.Resources.Contains(key)
                            ? Application.Current.Resources[key]
                            : Application.Current.TryFindResource(key);
                }
                object? existing = Application.Current.TryFindResource(key);
                if (
                    existing is Color
                    || (key.EndsWith("Color", StringComparison.Ordinal) && !key.EndsWith("Brush", StringComparison.Ordinal))
                    || key.Contains("WindowBackgroundColor", StringComparison.Ordinal)
                )
                {
                    Application.Current.Resources[key] = parsed;
                    foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
                    {
                        if (window.Resources.Contains(key))
                            window.Resources[key] = parsed;
                        window.ApplyBackdropSurface();
                    }
                }
                else
                {
                    SolidColorBrush brush = new(parsed);
                    brush.Freeze();
                    Application.Current.Resources[key] = brush;
                    foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
                    {
                        if (window.Resources.Contains(key))
                            window.Resources[key] = brush;
                    }
                }
            })
        );
    }

    public static void ResetBrushes(string plugin)
    {
        Dictionary<string, object?> snapshot;
        lock (Gate)
        {
            snapshot = new Dictionary<string, object?>(OriginalResources, StringComparer.Ordinal);
            OriginalResources.Clear();
            ActiveBrushOverrides.Clear();
        }
        App.Logger.WriteLine("VoidstrapMods", plugin + ": restored default theme brushes");
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (Application.Current == null)
                    return;
                foreach (KeyValuePair<string, object?> pair in snapshot)
                {
                    if (pair.Value == null)
                        Application.Current.Resources.Remove(pair.Key);
                    else
                        Application.Current.Resources[pair.Key] = pair.Value;
                }
                foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
                    window.ApplyTheme();
            })
        );
    }

    public static void ReapplyActiveBrushes()
    {
        Dictionary<string, string> snapshot;
        lock (Gate)
            snapshot = new Dictionary<string, string>(ActiveBrushOverrides, StringComparer.Ordinal);
        if (snapshot.Count == 0)
            return;
        foreach (KeyValuePair<string, string> pair in snapshot)
            SetBrush("system", pair.Key, pair.Value);
    }

    public static void RefreshWindowsIfVisualSetting(string propertyName)
    {
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (Application.Current == null)
                    return;
                if (propertyName is nameof(Voidstrap.Models.Persistable.AppSettings.UiZoomPercent))
                    MainWindow.ApplyUiZoomToOpenWindows();
                else if (
                    propertyName
                    is nameof(Voidstrap.Models.Persistable.AppSettings.Theme2)
                        or nameof(Voidstrap.Models.Persistable.AppSettings.WindowBackdrop)
                        or nameof(Voidstrap.Models.Persistable.AppSettings.CustomAccentColor)
                )
                {
                    foreach (MainWindow window in Application.Current.Windows.OfType<MainWindow>())
                        window.ApplyTheme();
                }
                else if (propertyName.StartsWith("HomepageBackgroundOverlay", StringComparison.Ordinal))
                    Voidstrap.UI.GlobalBackground.Reload();
                else if (propertyName.StartsWith("Sidebar", StringComparison.Ordinal))
                    PluginSidebar.ApplyToWindows();
            })
        );
    }
}

internal static class PluginWindow
{
    private static readonly object Gate = new();
    private static JsonObject? _lastSpec;
    private static readonly Dictionary<string, string> Owners = new(StringComparer.Ordinal);

    public static void ClearAll()
    {
        lock (Gate)
        {
            _lastSpec = null;
            Owners.Clear();
        }
    }

    internal static void RemovePlugins(IReadOnlySet<string> plugins)
    {
        lock (Gate)
            foreach (string key in Owners.Where(pair => plugins.Contains(pair.Value)).Select(pair => pair.Key).ToArray())
            {
                _lastSpec?.Remove(key);
                Owners.Remove(key);
            }
    }

    public static void ApplyPending(MainWindow window)
    {
        JsonObject? spec;
        lock (Gate)
            spec = _lastSpec?.DeepClone() as JsonObject;
        if (spec != null)
            ApplyToWindow(window, spec);
    }

    public static string Get()
    {
        return PluginNavigation.InvokeUiBounded(
            () =>
            {
                MainWindow? window = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
                if (window == null)
                    return "{}";
                return new JsonObject
                {
                    ["title"] = window.Title ?? "",
                    ["width"] = window.ActualWidth > 0 ? window.ActualWidth : window.Width,
                    ["height"] = window.ActualHeight > 0 ? window.ActualHeight : window.Height,
                    ["left"] = window.Left,
                    ["top"] = window.Top,
                    ["state"] = window.WindowState.ToString(),
                    ["topMost"] = window.Topmost,
                    ["opacity"] = window.Opacity,
                }.ToJsonString();
            },
            "{}"
        );
    }

    public static void Set(string plugin, JsonObject spec)
    {
        lock (Gate)
        {
            _lastSpec ??= new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> kv in spec)
            {
                _lastSpec[kv.Key] = kv.Value?.DeepClone();
                Owners[kv.Key] = plugin;
            }
        }
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                MainWindow? window = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
                if (window != null)
                    ApplyToWindow(window, spec);
            })
        );
    }

    private static void ApplyToWindow(MainWindow window, JsonObject spec)
    {
        if (spec["title"]?.ToString() is { Length: > 0 } title)
            window.Title = title;
        if (spec["width"] is JsonValue wVal && wVal.TryGetValue(out double w) && w >= 640 && w <= 4000)
            window.Width = w;
        if (spec["height"] is JsonValue hVal && hVal.TryGetValue(out double h) && h >= 480 && h <= 3000)
            window.Height = h;
        if (spec["left"] is JsonValue lVal && lVal.TryGetValue(out double l) && double.IsFinite(l))
            window.Left = l;
        if (spec["top"] is JsonValue tVal && tVal.TryGetValue(out double t) && double.IsFinite(t))
            window.Top = t;
        if (
            spec["state"]?.ToString() is { Length: > 0 } stateStr
            && Enum.TryParse(stateStr, ignoreCase: true, out System.Windows.WindowState ws)
        )
            window.WindowState = ws;
        if (spec["topMost"] is JsonValue tmVal && tmVal.TryGetValue(out bool topMost))
            window.Topmost = topMost;
        if (spec["opacity"] is JsonValue opVal && opVal.TryGetValue(out double op))
            window.Opacity = Math.Clamp(op, 0.25, 1.0);
    }
}

internal static class PluginRoblox
{
    public static string Status()
    {
        try
        {
            List<Process> players = Voidstrap.Utility.RobloxProcesses.Player();
            bool running = players.Count > 0;
            int pid = running ? players[0].Id : 0;
            foreach (Process p in players)
                p.Dispose();
            return new JsonObject { ["isRunning"] = running, ["pid"] = pid }.ToJsonString();
        }
        catch
        {
            return new JsonObject { ["isRunning"] = false, ["pid"] = 0 }.ToJsonString();
        }
    }

    public static void Launch(string plugin, JsonObject spec)
    {
        string mode = (spec["mode"]?.ToString() ?? "player").ToLowerInvariant();
        long placeId = 0;
        if (spec["placeId"] is JsonValue pVal)
        {
            if (pVal.TryGetValue(out long pl))
                placeId = pl;
            else if (pVal.TryGetValue(out double pd) && pd > 0)
                placeId = (long)pd;
            else
                long.TryParse(pVal.ToString(), out placeId);
        }
        string jobId = spec["jobId"]?.ToString().Trim() ?? "";
        App.Logger.WriteLine(
            "VoidstrapMods",
            plugin + ": requested Roblox launch (" + mode + (placeId > 0 ? ", place " + placeId : "") + ")"
        );
        try
        {
            string exe = Paths.Application;
            if (!File.Exists(exe))
                exe = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exe))
                return;
            string args;
            if (mode == "studio")
            {
                args = "-studio";
            }
            else if (placeId > 0)
            {
                string deeplink =
                    "roblox://experiences/start?placeId="
                    + placeId
                    + (jobId.Length > 0 ? "&gameInstanceId=" + Uri.EscapeDataString(jobId) : "");
                args = "-player \"" + deeplink + "\"";
            }
            else
            {
                args = "-player";
            }
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                }
            );
        }
        catch (Exception ex)
        {
            PluginHost.Problem(plugin, "Roblox launch failed: " + PluginHost.Describe(ex));
        }
    }
}

internal static class PluginClipboard
{
    public static string Get()
    {
        return PluginNavigation.InvokeUiBounded(
            () =>
            {
                try
                {
                    return Clipboard.ContainsText() ? Clipboard.GetText() : "";
                }
                catch
                {
                    return "";
                }
            },
            ""
        );
    }

    public static void Set(string text)
    {
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                try
                {
                    Clipboard.SetText(text ?? "");
                }
                catch { }
            })
        );
    }
}

internal static class PluginDialogManager
{
    internal sealed class ModDialogWindow : WpfUiWindow { }

    private static readonly Dictionary<string, WpfUiWindow> OpenDialogs = new(StringComparer.Ordinal);

    public static void OpenDialog(string plugin, JsonObject spec)
    {
        string id = (spec["id"]?.ToString() ?? "dialog").Trim();
        if (id.Length == 0 || id.Length > 64)
            throw new InvalidOperationException("dialog id must be 1 to 64 characters");
        string title = spec["title"]?.ToString() ?? id;
        double width = spec["width"] is JsonValue w && w.TryGetValue(out double dw) ? Math.Clamp(dw, 280, 1400) : 520;
        double height = spec["height"] is JsonValue h && h.TryGetValue(out double dh) ? Math.Clamp(dh, 200, 1000) : 420;
        string key = plugin + "|" + id;
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (OpenDialogs.TryGetValue(key, out WpfUiWindow? existing))
                {
                    existing.Title = title;
                    existing.Activate();
                    RenderDialogContent(existing, plugin, id);
                    return;
                }
                ModDialogWindow win = new()
                {
                    Title = title,
                    Width = width,
                    Height = height,
                    MinWidth = 280,
                    MinHeight = 200,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    ExtendsContentIntoTitleBar = true,
                    Owner = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(),
                };
                win.SetResourceReference(Control.BackgroundProperty, "ApplicationBackgroundBrush");
                Grid root = new();
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                TitleBar titleBar = new()
                {
                    Title = title,
                    Padding = new Thickness(8),
                    ForceShutdown = false,
                    MinimizeToTray = false,
                };
                Grid.SetRow(titleBar, 0);
                root.Children.Add(titleBar);
                Border bodyHost = new() { Padding = new Thickness(16) };
                ScrollViewer scroll = new()
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = bodyHost,
                };
                Grid.SetRow(scroll, 1);
                root.Children.Add(scroll);
                win.Content = root;
                win.Tag = bodyHost;
                void OnInvalidated(string invalidatedPlugin)
                {
                    if (string.Equals(invalidatedPlugin, plugin, StringComparison.Ordinal))
                        win.Dispatcher.BeginInvoke(new Action(() => RenderDialogContent(win, plugin, id)));
                }
                PluginHost.PaneInvalidated += OnInvalidated;
                win.Closed += (_, _) =>
                {
                    PluginHost.PaneInvalidated -= OnInvalidated;
                    OpenDialogs.Remove(key);
                };
                OpenDialogs[key] = win;
                RenderDialogContent(win, plugin, id);
                win.Show();
            })
        );
    }

    private static void RenderDialogContent(WpfUiWindow window, string plugin, string id)
    {
        if (window.Tag is not Border host)
            return;
        try
        {
            string dir = Path.Combine(Paths.Plugins, plugin);
            host.Child = PluginUiRenderer.BuildTree(PluginHost.RenderDialog(plugin, id), dir);
        }
        catch (Exception ex)
        {
            PluginHost.Problem(plugin, "the dialog " + id + " could not be drawn: " + PluginHost.Describe(ex));
        }
    }

    public static void CloseDialog(string plugin, string id)
    {
        string key = plugin + "|" + id;
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (OpenDialogs.Remove(key, out WpfUiWindow? win))
                    win.Close();
            })
        );
    }

    public static void ClosePluginDialogs(HashSet<string> activePlugins)
    {
        Application.Current?.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                foreach (string key in OpenDialogs.Keys.ToList())
                {
                    int sep = key.IndexOf('|');
                    string plugin = sep > 0 ? key[..sep] : key;
                    if (!activePlugins.Contains(plugin) && OpenDialogs.Remove(key, out WpfUiWindow? win))
                        win.Close();
                }
            })
        );
    }

    public static void ShowMessage(string plugin, string text, string title)
    {
        System.Windows.Threading.Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;
        dispatcher.BeginInvoke(
            new Action(() =>
            {
                Frontend.ShowMessageBox((!string.IsNullOrWhiteSpace(title) ? title + "\n\n" : "") + text, MessageBoxImage.Information);
            })
        );
    }

    public static bool ShowConfirm(string plugin, string text, string title)
    {
        return PluginNavigation.InvokeUiBounded(
            () =>
                Frontend.ShowMessageBox(
                    (!string.IsNullOrWhiteSpace(title) ? title + "\n\n" : "") + text,
                    MessageBoxImage.Question,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.No
                ) == MessageBoxResult.Yes,
            false,
            30000
        );
    }
}
