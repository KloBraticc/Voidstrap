using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Keystrokes;

public static class KeystrokeThemeManager
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, KeystrokeTheme> BuiltIns = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static event Action? ActiveThemeChanged;

    static KeystrokeThemeManager()
    {
        RegisterBuiltIn(new KeystrokeTheme
        {
            Name = "Voidstrap (Default)",
            IsBuiltIn = true,
            KeyBackground = "#202020",
            KeyPressedBackground = "Accent",
            KeyBorderColor = "#363638",
            KeyBorderThickness = 1.0,
            KeyCornerRadius = 8.0,
            KeyTextColor = "#FFFFFF",
            KeyPressedTextColor = "#FFFFFF",
            CpsTextColor = "#A0A0A5",
            KeyPressedCpsTextColor = "#FFFFFF",
            KeyGlowColor = "Accent",
            KeyGlowRadius = 8.0,
            KeyGlowOpacity = 0.5,
            BackdropBlur = false,
            KeyScaleOnPress = 0.96,
            FontFamily = "Segoe UI",
            FontWeight = "SemiBold"
        });

        RegisterBuiltIn(new KeystrokeTheme
        {
            Name = "Voidstrap (Glass)",
            IsBuiltIn = true,
            KeyBackground = "#2418181E",
            KeyPressedBackground = "Accent",
            KeyBorderColor = "#38FFFFFF",
            KeyBorderThickness = 1.0,
            KeyCornerRadius = 8.0,
            KeyTextColor = "#FFFFFF",
            KeyPressedTextColor = "#FFFFFF",
            CpsTextColor = "#94A3B8",
            KeyPressedCpsTextColor = "#E2E8F0",
            KeyGlowColor = "Accent",
            KeyGlowRadius = 10.0,
            KeyGlowOpacity = 0.6,
            BackdropBlur = true,
            KeyScaleOnPress = 0.96,
            FontFamily = "Segoe UI",
            FontWeight = "SemiBold"
        });

        RegisterBuiltIn(new KeystrokeTheme
        {
            Name = "Fluent Dark",
            IsBuiltIn = true,
            KeyBackground = "#2B2B2B",
            KeyPressedBackground = "Accent",
            KeyBorderColor = "#3D3D3D",
            KeyBorderThickness = 1.0,
            KeyCornerRadius = 6.0,
            KeyTextColor = "#F3F3F3",
            KeyPressedTextColor = "#FFFFFF",
            CpsTextColor = "#A6A6A6",
            KeyPressedCpsTextColor = "#E0E0E0",
            KeyGlowColor = "Accent",
            KeyGlowRadius = 6.0,
            KeyGlowOpacity = 0.4,
            BackdropBlur = false,
            KeyScaleOnPress = 0.98,
            FontFamily = "Segoe UI",
            FontWeight = "Normal"
        });

        RegisterBuiltIn(new KeystrokeTheme
        {
            Name = "Fluent Light",
            IsBuiltIn = true,
            KeyBackground = "#EBEBEB",
            KeyPressedBackground = "Accent",
            KeyBorderColor = "#D0D0D0",
            KeyBorderThickness = 1.0,
            KeyCornerRadius = 6.0,
            KeyTextColor = "#1C1C1C",
            KeyPressedTextColor = "#FFFFFF",
            CpsTextColor = "#606060",
            KeyPressedCpsTextColor = "#E0E0E0",
            KeyGlowColor = "Accent",
            KeyGlowRadius = 4.0,
            KeyGlowOpacity = 0.3,
            BackdropBlur = false,
            KeyScaleOnPress = 0.98,
            FontFamily = "Segoe UI",
            FontWeight = "Normal"
        });

        RegisterBuiltIn(new KeystrokeTheme
        {
            Name = "Clean Minimal",
            IsBuiltIn = true,
            KeyBackground = "#18181A",
            KeyPressedBackground = "Accent",
            KeyBorderColor = "#00000000",
            KeyBorderThickness = 0.0,
            KeyCornerRadius = 5.0,
            KeyTextColor = "#EDEDED",
            KeyPressedTextColor = "#FFFFFF",
            CpsTextColor = "#707078",
            KeyPressedCpsTextColor = "#FFFFFF",
            KeyGlowColor = "#00000000",
            KeyGlowRadius = 0.0,
            KeyGlowOpacity = 0.0,
            BackdropBlur = false,
            KeyScaleOnPress = 1.0,
            FontFamily = "Segoe UI",
            FontWeight = "Medium"
        });

        RegisterBuiltIn(new KeystrokeTheme
        {
            Name = "Chroma Accent",
            IsBuiltIn = true,
            KeyBackground = "#1A1028",
            KeyPressedBackground = "Accent",
            KeyBorderColor = "Accent",
            KeyBorderThickness = 1.5,
            KeyCornerRadius = 9.0,
            KeyTextColor = "#F5F3FF",
            KeyPressedTextColor = "#FFFFFF",
            CpsTextColor = "#D8B4FE",
            KeyPressedCpsTextColor = "#FFFFFF",
            KeyGlowColor = "Accent",
            KeyGlowRadius = 14.0,
            KeyGlowOpacity = 0.75,
            BackdropBlur = true,
            KeyScaleOnPress = 0.95,
            FontFamily = "Segoe UI",
            FontWeight = "Bold"
        });

        RegisterBuiltIn(new KeystrokeTheme
        {
            Name = "Neumorphic (Jax-Core)",
            IsBuiltIn = true,
            KeyBackground = "#202227",
            KeyPressedBackground = "#131417",
            KeyBorderColor = "#2F333B",
            KeyBorderThickness = 2.0,
            KeyCornerRadius = 10.0,
            KeyTextColor = "#E2E8F0",
            KeyPressedTextColor = "#38BDF8",
            CpsTextColor = "#8292A6",
            KeyPressedCpsTextColor = "#38BDF8",
            KeyGlowColor = "Accent",
            KeyGlowRadius = 8.0,
            KeyGlowOpacity = 0.5,
            BackdropBlur = false,
            KeyScaleOnPress = 0.94,
            FontFamily = "Segoe UI",
            FontWeight = "SemiBold"
        });

        RegisterBuiltIn(new KeystrokeTheme
        {
            Name = "Retro Classic",
            IsBuiltIn = true,
            KeyBackground = "#111111",
            KeyPressedBackground = "#33FF33",
            KeyBorderColor = "#33FF33",
            KeyBorderThickness = 2.0,
            KeyCornerRadius = 0.0,
            KeyTextColor = "#33FF33",
            KeyPressedTextColor = "#000000",
            CpsTextColor = "#22AA22",
            KeyPressedCpsTextColor = "#000000",
            KeyGlowColor = "#33FF33",
            KeyGlowRadius = 8.0,
            KeyGlowOpacity = 0.7,
            BackdropBlur = false,
            KeyScaleOnPress = 1.0,
            FontFamily = "Consolas",
            FontWeight = "Bold"
        });
    }

    private static void RegisterBuiltIn(KeystrokeTheme theme)
    {
        BuiltIns[theme.Name] = theme;
    }

    public static IReadOnlyList<KeystrokeTheme> GetBuiltInThemes()
    {
        lock (Gate)
        {
            return BuiltIns.Values.ToList();
        }
    }

    public static IReadOnlyList<KeystrokeTheme> GetCustomThemes()
    {
        lock (Gate)
        {
            List<KeystrokeTheme> list = new();
            string root = Paths.CustomKeystrokeThemes;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return list;

            foreach (string dir in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(dir);
                string file = Path.Combine(dir, "Theme.json");
                if (File.Exists(file))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        var theme = JsonSerializer.Deserialize<KeystrokeTheme>(json);
                        if (theme != null)
                        {
                            theme.Name = name;
                            theme.IsBuiltIn = false;
                            list.Add(theme);
                            continue;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return list;
        }
    }

    public static IReadOnlyList<string> GetAllThemeNames()
    {
        List<string> names = GetBuiltInThemes().Select(t => t.Name).ToList();
        names.AddRange(GetCustomThemes().Select(t => t.Name));
        return names;
    }

    public static KeystrokeTheme GetTheme(string? name)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (BuiltIns.TryGetValue(name, out var builtIn))
                    return builtIn;

                string root = Paths.CustomKeystrokeThemes;
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    string dir = Path.Combine(root, name);
                    string file = Path.Combine(dir, "Theme.json");
                    if (File.Exists(file))
                    {
                        try
                        {
                            string json = File.ReadAllText(file);
                            var custom = JsonSerializer.Deserialize<KeystrokeTheme>(json);
                            if (custom != null)
                            {
                                custom.Name = name;
                                custom.IsBuiltIn = false;
                                return custom;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (BuiltIns.TryGetValue("Voidstrap (Default)", out var defaultTheme))
                return defaultTheme;

            return BuiltIns.Values.First();
        }
    }

    public static KeystrokeTheme ActiveTheme => GetTheme(App.Settings.Prop.KeystrokesThemeName);

    public static void NotifyThemeChanged()
    {
        ActiveThemeChanged?.Invoke();
    }

    public static bool SaveCustomTheme(KeystrokeTheme theme)
    {
        if (string.IsNullOrWhiteSpace(theme.Name))
            return false;

        lock (Gate)
        {
            try
            {
                string root = Paths.CustomKeystrokeThemes;
                Directory.CreateDirectory(root);
                string dir = Path.Combine(root, theme.Name);
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "Theme.json");
                string json = JsonSerializer.Serialize(theme, JsonOptions);
                File.WriteAllText(file, json);

                if (string.Equals(App.Settings.Prop.KeystrokesThemeName, theme.Name, StringComparison.OrdinalIgnoreCase))
                    NotifyThemeChanged();

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool DeleteCustomTheme(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        lock (Gate)
        {
            try
            {
                string dir = Path.Combine(Paths.CustomKeystrokeThemes, name);
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }

                if (string.Equals(App.Settings.Prop.KeystrokesThemeName, name, StringComparison.OrdinalIgnoreCase))
                {
                    App.Settings.Prop.KeystrokesThemeName = "Voidstrap (Default)";
                    App.Settings.SaveDeferred();
                    NotifyThemeChanged();
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool RenameCustomTheme(string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return false;

        lock (Gate)
        {
            try
            {
                string oldDir = Path.Combine(Paths.CustomKeystrokeThemes, oldName);
                string newDir = Path.Combine(Paths.CustomKeystrokeThemes, newName);
                if (!Directory.Exists(oldDir) || Directory.Exists(newDir))
                    return false;

                Directory.Move(oldDir, newDir);

                string file = Path.Combine(newDir, "Theme.json");
                if (File.Exists(file))
                {
                    string json = File.ReadAllText(file);
                    var theme = JsonSerializer.Deserialize<KeystrokeTheme>(json);
                    if (theme != null)
                    {
                        theme.Name = newName;
                        File.WriteAllText(file, JsonSerializer.Serialize(theme, JsonOptions));
                    }
                }

                if (string.Equals(App.Settings.Prop.KeystrokesThemeName, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    App.Settings.Prop.KeystrokesThemeName = newName;
                    App.Settings.SaveDeferred();
                    NotifyThemeChanged();
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool ExportCustomTheme(string name, string targetPath)
    {
        lock (Gate)
        {
            try
            {
                string dir = Path.Combine(Paths.CustomKeystrokeThemes, name);
                string file = Path.Combine(dir, "Theme.json");
                if (!File.Exists(file))
                    return false;

                if (targetPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);

                    using var zip = ZipFile.Open(targetPath, ZipArchiveMode.Create);
                    zip.CreateEntryFromFile(file, "Theme.json");
                    return true;
                }

                File.Copy(file, targetPath, overwrite: true);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool ImportCustomTheme(string sourcePath, out string importedName)
    {
        importedName = string.Empty;
        if (!File.Exists(sourcePath))
            return false;

        lock (Gate)
        {
            try
            {
                string root = Paths.CustomKeystrokeThemes;
                Directory.CreateDirectory(root);

                string candidateName = Path.GetFileNameWithoutExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(candidateName))
                    candidateName = "Imported Theme";

                string targetDir = Path.Combine(root, candidateName);
                int counter = 1;
                while (Directory.Exists(targetDir))
                {
                    candidateName = $"{Path.GetFileNameWithoutExtension(sourcePath)} ({counter++})";
                    targetDir = Path.Combine(root, candidateName);
                }

                Directory.CreateDirectory(targetDir);
                string targetFile = Path.Combine(targetDir, "Theme.json");

                if (sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(sourcePath);
                    var entry = zip.GetEntry("Theme.json") ?? zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                        return false;

                    entry.ExtractToFile(targetFile, overwrite: true);
                }
                else
                {
                    File.Copy(sourcePath, targetFile, overwrite: true);
                }

                string content = File.ReadAllText(targetFile);
                var theme = JsonSerializer.Deserialize<KeystrokeTheme>(content);
                if (theme == null)
                    return false;

                theme.Name = candidateName;
                File.WriteAllText(targetFile, JsonSerializer.Serialize(theme, JsonOptions));

                importedName = candidateName;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
