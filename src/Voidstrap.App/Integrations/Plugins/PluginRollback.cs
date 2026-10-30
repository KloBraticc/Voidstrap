using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginRollback
{
    private const string LOG_IDENT = "PluginRollback";

    private const string SettingPrefix = "setting:";

    private const string FlagPrefix = "flag:";

    private const string TextPrefix = "text:";

    private const string BackgroundKey = "background";

    private const string BrushesKey = "brushes";

    public static readonly string[] SidebarSettings = ["SidebarOrder", "SidebarHiddenItems", "SidebarNames", "SidebarIcons", "SidebarIconImages"];

    public static readonly string[] ThemeSettings = ["Theme2", "WindowBackdrop", "UiZoomPercent", "CustomAccentColor"];

    private static readonly object Gate = new();

    private static PropertyInfo? Property(string name) =>
        typeof(AppSettings).GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    private static string? ReadSetting(PropertyInfo property) => JsonSerializer.Serialize(property.GetValue(App.Settings.Prop), property.PropertyType);

    private static void Record(string mod, string key, string? before, string? after)
    {
        lock (Gate)
        {
            Dictionary<string, Dictionary<string, ModRollbackValue>> journal = App.Settings.Prop.ModRollback ??= [];
            if (!journal.TryGetValue(mod, out Dictionary<string, ModRollbackValue>? entries))
                journal[mod] = entries = [];
            if (entries.TryGetValue(key, out ModRollbackValue? value))
                value.Last = after;
            else
                entries[key] = new ModRollbackValue { Original = before, Last = after };
        }
        App.Settings.SaveDeferred();
    }

    public static void TrackSettings(string mod, IEnumerable<string> names, Action change)
    {
        List<(PropertyInfo Property, string? Before)> tracked = [];
        foreach (string name in names)
            if (Property(name) is { } property)
                tracked.Add((property, ReadSetting(property)));
        change();
        foreach ((PropertyInfo property, string? before) in tracked)
            if (ReadSetting(property) is var after && after != before)
                Record(mod, SettingPrefix + property.Name, before, after);
    }

    public static void TrackFlags(string mod, IEnumerable<string>? names, Action change)
    {
        List<string> tracked = (names ?? App.FastFlags.Prop.Keys.ToList()).Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
        List<(string Name, string? Before)> before = tracked.Select(name => (name, PluginFastFlags.Get(name))).ToList();
        change();
        foreach ((string name, string? value) in before)
            if (PluginFastFlags.Get(name) is var after && after != value)
                Record(mod, FlagPrefix + name, value, after);
    }

    public static T TrackTexts<T>(string mod, Func<T> change)
    {
        Dictionary<string, string> before = Voidstrap.Utility.AppControl.ListText().ToDictionary(pair => pair.Key, pair => pair.Value);
        T result = change();
        Dictionary<string, string> after = Voidstrap.Utility.AppControl.ListText().ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (string key in before.Keys.Union(after.Keys))
        {
            before.TryGetValue(key, out string? old);
            after.TryGetValue(key, out string? now);
            if (old != now)
                Record(mod, TextPrefix + key, old, now);
        }
        return result;
    }

    public static T TrackBackground<T>(string mod, Func<T> change)
    {
        string before = Voidstrap.Utility.AppControl.GetBackground().ToJsonString();
        T result = change();
        string after = Voidstrap.Utility.AppControl.GetBackground().ToJsonString();
        if (after != before)
            Record(mod, BackgroundKey, before, after);
        return result;
    }

    public static void MarkBrushes(string mod) => Record(mod, BrushesKey, null, null);

    public static void RestoreMissing()
    {
        List<string> names;
        lock (Gate)
        {
            names = App.Settings.Prop.ModRollback?.Keys.ToList() ?? [];
        }
        if (names.Count == 0)
            return;
        HashSet<string> present = PluginManifest.Discover(Paths.Plugins, report: false).Select(manifest => manifest.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string name in names.Where(name => !present.Contains(name)))
            Restore(name);
    }

    public static void Restore(string mod)
    {
        Dictionary<string, ModRollbackValue>? entries;
        lock (Gate)
        {
            entries = null;
            App.Settings.Prop.ModRollback?.Remove(mod, out entries);
        }
        if (entries == null || entries.Count == 0)
            return;
        bool background = false;
        foreach (KeyValuePair<string, ModRollbackValue> entry in entries)
        {
            try
            {
                background |= RestoreEntry(mod, entry.Key, entry.Value);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or IOException)
            {
                App.Logger.WriteLine(LOG_IDENT, mod + ": could not restore " + entry.Key + ": " + ex.Message);
            }
        }
        App.Settings.SaveDeferred();
        App.FastFlags.SaveDeferred();
        App.Logger.WriteLine(LOG_IDENT, mod + ": restored " + entries.Count + " saved changes");
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => RefreshUi(background)));
    }

    private static bool RestoreEntry(string mod, string key, ModRollbackValue value)
    {
        if (key == BrushesKey)
        {
            PluginTheme.ResetBrushes(mod);
            return false;
        }
        if (key == BackgroundKey)
            return RestoreBackground(value);
        if (key.StartsWith(SettingPrefix, StringComparison.Ordinal))
            RestoreSetting(key[SettingPrefix.Length..], value);
        else if (key.StartsWith(FlagPrefix, StringComparison.Ordinal))
            RestoreFlag(mod, key[FlagPrefix.Length..], value);
        else if (key.StartsWith(TextPrefix, StringComparison.Ordinal))
            RestoreText(key[TextPrefix.Length..], value);
        return false;
    }

    private static void RestoreSetting(string name, ModRollbackValue value)
    {
        if (Property(name) is not { } property || ReadSetting(property) != value.Last || value.Original == null)
            return;
        property.SetValue(App.Settings.Prop, JsonSerializer.Deserialize(value.Original, property.PropertyType));
        PluginTheme.RefreshWindowsIfVisualSetting(property.Name);
    }

    private static void RestoreFlag(string mod, string name, ModRollbackValue value)
    {
        if (PluginFastFlags.Get(name) != value.Last)
            return;
        if (value.Original == null)
            PluginFastFlags.Remove(mod, name);
        else
            PluginFastFlags.Set(mod, name, value.Original);
    }

    private static void RestoreText(string target, ModRollbackValue value)
    {
        Voidstrap.Utility.AppControl.ListText().TryGetValue(target, out string? current);
        if (current != value.Last)
            return;
        if (value.Original == null)
            Voidstrap.Utility.AppControl.ClearText(target);
        else
            Voidstrap.Utility.AppControl.SetText(target, value.Original);
    }

    private static bool RestoreBackground(ModRollbackValue value)
    {
        if (Voidstrap.Utility.AppControl.GetBackground().ToJsonString() != value.Last || value.Original == null)
            return false;
        if (JsonNode.Parse(value.Original) is not JsonObject state)
            return false;
        Voidstrap.UI.GlobalBackground.Update(
            state["backgroundFilePath"]?.ToString(),
            state["gradientOpacity"]?.GetValue<double>() ?? 0.0,
            state["blackOverlayOpacity"]?.GetValue<double>() ?? 0.0,
            state["displayEverywhere"]?.GetValue<bool>() ?? false
        );
        return true;
    }

    private static void RefreshUi(bool background)
    {
        if (Application.Current == null)
            return;
        PluginSidebar.ApplyToWindows();
        foreach (Voidstrap.UI.Elements.Settings.MainWindow window in Application.Current.Windows.OfType<Voidstrap.UI.Elements.Settings.MainWindow>())
            window.ApplyTheme();
        Voidstrap.UI.Elements.Settings.MainWindow.ApplyUiZoomToOpenWindows();
        if (background)
            Voidstrap.UI.GlobalBackground.Reload();
    }
}
