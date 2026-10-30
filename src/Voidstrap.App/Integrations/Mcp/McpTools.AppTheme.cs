using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using Voidstrap.Enums;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Mcp;

public static partial class McpTools
{
    private static McpToolResult ManageAppTheme(JsonObject args)
    {
        string action = McpArgs.Trimmed(args, "action", "get").ToLowerInvariant();
        switch (action)
        {
            case "schema":
                {
                    var keys = new JsonArray();
                    foreach (ThemeKeyInfo info in CustomTheme.Schema)
                    {
                        keys.Add(
                            new JsonObject
                            {
                                ["key"] = info.Key,
                                ["label"] = info.Label,
                                ["group"] = info.Group,
                                ["brush"] = info.IsBrush,
                                ["default"] = info.Fallback,
                                ["optional"] = info.Optional,
                            }
                        );
                    }
                    return new McpToolResult(
                        new JsonObject
                        {
                            ["note"] =
                                "Colors are #AARRGGBB or #RRGGBB. The window gradient takes 2 to 8 stops. XAML may only use ResourceDictionary, Color, SolidColorBrush, LinearGradientBrush, RadialGradientBrush and GradientStop.",
                            ["builtInThemes"] = new JsonArray(
                                Enum.GetNames<Theme>().Where(name => name != nameof(Theme.Custom)).Select(name => (JsonNode)name).ToArray()
                            ),
                            ["keys"] = keys,
                        }.ToJsonString(IndentedJson)
                    );
                }
            case "get":
                {
                    string xaml = CurrentAppThemeXaml(McpArgs.String(args, "baseTheme"));
                    ThemeValidationResult result = CustomTheme.Validate(xaml);
                    var colors = new JsonObject();
                    foreach (KeyValuePair<string, Color> pair in CustomTheme.ReadModel(result.Dictionary).Colors)
                        colors[pair.Key] = CustomTheme.ToHex(pair.Value);
                    return new McpToolResult(
                        new JsonObject
                        {
                            ["activeTheme"] = App.Settings.Prop.Theme2.ToString(),
                            ["colors"] = colors,
                            ["xaml"] = xaml,
                        }.ToJsonString(IndentedJson)
                    );
                }
            case "set_colors":
                {
                    string baseXaml = CurrentAppThemeXaml(McpArgs.String(args, "baseTheme"));
                    ThemeModel model = CustomTheme.ReadModel(CustomTheme.Validate(baseXaml).Dictionary);
                    var errors = new List<string>();
                    if (args["colors"] is JsonObject colors)
                    {
                        HashSet<string> known = CustomTheme.Schema.Select(info => info.Key).ToHashSet(StringComparer.Ordinal);
                        foreach (KeyValuePair<string, JsonNode?> pair in colors)
                        {
                            string? key = known.FirstOrDefault(k => string.Equals(k, pair.Key, StringComparison.OrdinalIgnoreCase));
                            if (key == null)
                                errors.Add($"Unknown key '{pair.Key}', see action schema");
                            else if (!CustomTheme.TryParseColor(McpArgs.Text(pair.Value), out Color color))
                                errors.Add($"'{pair.Key}' is not a color");
                            else
                                model.Colors[key] = color;
                        }
                    }
                    if (args["gradient"] is JsonObject gradient)
                    {
                        if (McpArgs.Flag(gradient, "remove", false))
                            model.Gradient = null;
                        else if (gradient["stops"] is JsonArray stops && stops.Count >= 2)
                        {
                            var built = new ThemeGradient
                            {
                                Radial = McpArgs.Flag(gradient, "radial", false),
                                Angle = McpArgs.Long(gradient, "angle") ?? 45,
                            };
                            for (int i = 0; i < stops.Count && i < ThemeGradient.MaximumStops; i++)
                            {
                                JsonObject? stop = stops[i] as JsonObject;
                                if (!CustomTheme.TryParseColor(McpArgs.String(stop, "color"), out Color color))
                                {
                                    errors.Add($"Gradient stop {i + 1} has no valid color");
                                    continue;
                                }
                                double offset =
                                    stop?["offset"] is JsonValue value && value.TryGetValue(out double parsed)
                                        ? parsed
                                        : i / (double)(stops.Count - 1);
                                built.Stops.Add(new ThemeGradientStop { Color = color, Offset = Math.Clamp(offset, 0, 1) });
                            }
                            if (built.Stops.Count >= 2)
                                model.Gradient = built;
                        }
                        else
                            errors.Add("A gradient needs at least 2 stops, or remove set to true");
                    }
                    if (errors.Count > 0)
                        return new McpToolResult("Nothing was changed: " + string.Join("; ", errors), true);
                    return ApplyAppTheme(CustomTheme.BuildXaml(model), "set_colors");
                }
            case "set_xaml":
                {
                    string? xaml = McpArgs.String(args, "xaml");
                    if (string.IsNullOrWhiteSpace(xaml))
                        return new McpToolResult("Provide 'xaml'.", true);
                    return ApplyAppTheme(xaml, "set_xaml");
                }
            case "use_builtin":
                {
                    string name = McpArgs.Trimmed(args, "baseTheme");
                    if (!Enum.TryParse(name, true, out Theme theme) || theme == Theme.Custom)
                        return new McpToolResult("Provide a built in 'baseTheme' from action schema.", true);
                    Theme before = App.Settings.Prop.Theme2;
                    App.Settings.Prop.Theme2 = theme;
                    App.Settings.Save();
                    ReapplyAppTheme();
                    PushUndo(
                        "switch the app theme",
                        () =>
                        {
                            App.Settings.Prop.Theme2 = before;
                            App.Settings.Save();
                            ReapplyAppTheme();
                        }
                    );
                    return new McpToolResult($"Switched Voidstrap to the {theme} theme.");
                }
            case "list_profiles":
                return new McpToolResult(
                    "Saved theme profiles: " + (CustomTheme.ListProfiles() is { Count: > 0 } list ? string.Join(", ", list) : "none")
                );
            case "save_profile":
                {
                    string name = McpArgs.Trimmed(args, "name");
                    if (!CustomTheme.IsValidProfileName(name))
                        return new McpToolResult("Provide a valid profile 'name'.", true);
                    CustomTheme.SaveProfile(name, CurrentAppThemeXaml(null));
                    return new McpToolResult($"Saved the current theme as the profile '{name}'.");
                }
            case "load_profile":
                {
                    string name = McpArgs.Trimmed(args, "name");
                    if (!CustomTheme.IsValidProfileName(name) || !CustomTheme.ListProfiles().Contains(name, StringComparer.OrdinalIgnoreCase))
                        return new McpToolResult("That profile does not exist, see list_profiles.", true);
                    return ApplyAppTheme(CustomTheme.ReadProfile(name), "load profile " + name);
                }
            case "delete_profile":
                {
                    string name = McpArgs.Trimmed(args, "name");
                    if (!CustomTheme.IsValidProfileName(name) || !CustomTheme.ListProfiles().Contains(name, StringComparer.OrdinalIgnoreCase))
                        return new McpToolResult("That profile does not exist, see list_profiles.", true);
                    string saved = CustomTheme.ReadProfile(name);
                    CustomTheme.DeleteProfile(name);
                    PushUndo("delete theme profile " + name, () => CustomTheme.SaveProfile(name, saved));
                    return new McpToolResult($"Deleted the profile '{name}'.");
                }
            default:
                return new McpToolResult(
                    "Unknown action. Use schema, get, set_colors, set_xaml, use_builtin, list_profiles, save_profile, load_profile, or delete_profile.",
                    true
                );
        }
    }

    private static string CurrentAppThemeXaml(string? baseTheme)
    {
        if (
            !string.IsNullOrWhiteSpace(baseTheme)
            && Enum.TryParse(baseTheme.Trim(), true, out Theme requested)
            && requested != Theme.Custom
        )
            return CustomTheme.BuiltInXaml(requested.ToString()) ?? CustomTheme.BuildXaml(new ThemeModel());

        if (App.Settings.Prop.Theme2 == Theme.Custom && File.Exists(Paths.CustomThemeXaml))
        {
            string? text = SafeRead(Paths.CustomThemeXaml);
            if (text != null && CustomTheme.Validate(text).Ok)
                return text;
        }

        string current = App.Settings.Prop.Theme2 is Theme.Custom or Theme.Default
            ? nameof(Theme.Dark)
            : App.Settings.Prop.Theme2.ToString();
        return CustomTheme.BuiltInXaml(current) ?? CustomTheme.BuildXaml(new ThemeModel());
    }

    private static McpToolResult ApplyAppTheme(string xaml, string label)
    {
        ThemeValidationResult result = CustomTheme.Validate(xaml);
        if (!result.Ok)
            return new McpToolResult(
                (result.ErrorLine > 0 ? $"Line {result.ErrorLine}: " : "")
                    + string.Join(" ", result.Errors.Take(3))
                    + " Nothing was changed.",
                true
            );

        string? previous = File.Exists(Paths.CustomThemeXaml) ? SafeRead(Paths.CustomThemeXaml) : null;
        Theme previousTheme = App.Settings.Prop.Theme2;
        CustomTheme.WriteFile(Paths.CustomThemeXaml, xaml);
        App.Settings.Prop.Theme2 = Theme.Custom;
        App.Settings.Save();
        ReapplyAppTheme();
        PushUndo(
            "app theme " + label,
            () =>
            {
                if (previous != null)
                    CustomTheme.WriteFile(Paths.CustomThemeXaml, previous);
                else if (File.Exists(Paths.CustomThemeXaml))
                    File.Delete(Paths.CustomThemeXaml);
                App.Settings.Prop.Theme2 = previousTheme;
                App.Settings.Save();
                ReapplyAppTheme();
            }
        );
        string warnings = result.Warnings.Count > 0 ? " Warnings: " + string.Join(" ", result.Warnings.Take(3)) : "";
        return new McpToolResult("Applied the theme to every open Voidstrap window. undo_last_change reverts it." + warnings);
    }

    private static void ReapplyAppTheme()
    {
        UI.Elements.Base.WpfUiWindow.InvalidateCustomTheme();
        UI.WindowBackdrop.ApplyThemeToAllOpenWindows();
        Application.Current?.Windows.OfType<UI.Elements.Settings.MainWindow>().FirstOrDefault()?.ApplyTheme();
    }
}
