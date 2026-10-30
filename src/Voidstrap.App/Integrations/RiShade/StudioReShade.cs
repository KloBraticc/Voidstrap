using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Voidstrap.Integrations.RiShade
{
    public static class StudioReShade
    {
        private const string LogIdent = "StudioReShade";
        private const string RuntimeFile = "dxgi.dll";

        public static string BaseDir => Path.Combine(Paths.RiShade, "Studio");

        private static string ConfigPath => Path.Combine(BaseDir, "ReShade.ini");

        private static string MarkerPath => Path.Combine(BaseDir, "installed.txt");

        public static void SetEnabled(bool enabled)
        {
            if (App.Settings.Prop.StudioReShadeEnabled == enabled)
                return;
            App.Settings.Prop.StudioReShadeEnabled = enabled;
            App.Settings.SaveDeferred();
            App.Logger.WriteLine(LogIdent, enabled ? "Enabled by user" : "Disabled by user");
            if (!Voidstrap.Utility.Platform.IsWindows)
                return;
            if (enabled)
                _ = RiShadeManager.EnsureSetupAsync();
            else
                RemoveInstalledCopies();
        }

        public static async Task PrepareLaunchAsync(string studioDirectory, ProcessStartInfo startInfo, bool enabled)
        {
            if (!Voidstrap.Utility.Platform.IsWindows || string.IsNullOrWhiteSpace(studioDirectory))
                return;
            try
            {
                if (!enabled)
                {
                    RemoveFrom(studioDirectory);
                    return;
                }

                await RiShadeManager.EnsureSetupAsync();
                string source = RiShadeManager.DxgiDllPath;
                if (!File.Exists(source))
                {
                    App.Logger.WriteLine(LogIdent, "The ReShade runtime is not installed yet, Studio starts without ReShade");
                    return;
                }

                string target = Path.Combine(studioDirectory, RuntimeFile);
                string sourceHash = Hash(source);
                if (File.Exists(target))
                {
                    string targetHash = Hash(target);
                    if (!targetHash.Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!OwnedHashes(studioDirectory).Contains(targetHash, StringComparer.OrdinalIgnoreCase))
                        {
                            App.Logger.WriteLine(
                                LogIdent,
                                "Studio already has a dxgi.dll that Voidstrap did not install, it is left untouched"
                            );
                            return;
                        }
                        File.Copy(source, target, true);
                    }
                }
                else
                {
                    File.Copy(source, target);
                }

                Directory.CreateDirectory(BaseDir);
                WriteConfig();
                RecordInstall(studioDirectory, sourceHash);
                startInfo.Environment["RESHADE_BASE_PATH_OVERRIDE"] = BaseDir;
                App.Logger.WriteLine(LogIdent, "ReShade is installed for this Studio launch, press Home in Studio to open its menu");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("StudioReShade::PrepareLaunchAsync", ex);
            }
        }

        private static void WriteConfig()
        {
            string shaders = Path.Combine(RiShadeManager.ShadersDirPath, "Shaders") + "\\**";
            string textures = Path.Combine(RiShadeManager.ShadersDirPath, "Textures") + "\\**";
            string cache = Path.Combine(BaseDir, "Cache");
            if (!File.Exists(ConfigPath))
            {
                string preset = Path.Combine(RiShadeManager.RiShadeDir, "presets", "RiShade Natural.ini");
                File.WriteAllText(
                    ConfigPath,
                    string.Join(
                        "\r\n",
                        "[GENERAL]",
                        "EffectSearchPaths=" + shaders,
                        "TextureSearchPaths=" + textures,
                        "IntermediateCachePath=" + cache,
                        "PresetPath=" + preset,
                        "NoEffectCache=0",
                        "",
                        "[INPUT]",
                        "KeyOverlay=36,0,0,0",
                        "",
                        "[OVERLAY]",
                        "TutorialProgress=4",
                        ""
                    )
                );
                return;
            }

            string text = File.ReadAllText(ConfigPath);
            string updated = SetValue(
                SetValue(SetValue(text, "EffectSearchPaths", shaders), "TextureSearchPaths", textures),
                "IntermediateCachePath",
                cache
            );
            if (updated != text)
                File.WriteAllText(ConfigPath, updated);
        }

        private static string SetValue(string text, string key, string value)
        {
            var pattern = new Regex("^" + Regex.Escape(key) + "=.*$", RegexOptions.Multiline);
            return pattern.IsMatch(text)
                ? pattern.Replace(text, (key + "=" + value).Replace("$", "$$"), 1)
                : text.Replace("[GENERAL]", "[GENERAL]\r\n" + key + "=" + value);
        }

        private static void RemoveInstalledCopies()
        {
            foreach (string directory in ReadMarker().Select(entry => entry.Directory).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                RemoveFrom(directory);
        }

        private static void RemoveFrom(string studioDirectory)
        {
            string target = Path.Combine(studioDirectory, RuntimeFile);
            try
            {
                if (File.Exists(target))
                {
                    string targetHash = Hash(target);
                    bool owned =
                        OwnedHashes(studioDirectory).Contains(targetHash, StringComparer.OrdinalIgnoreCase)
                        || (
                            File.Exists(RiShadeManager.DxgiDllPath)
                            && Hash(RiShadeManager.DxgiDllPath).Equals(targetHash, StringComparison.OrdinalIgnoreCase)
                        );
                    if (!owned)
                        return;
                    File.Delete(target);
                    App.Logger.WriteLine(LogIdent, "Removed ReShade from Roblox Studio");
                }
                WriteMarker(ReadMarker().Where(entry => !entry.Directory.Equals(studioDirectory, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Logger.WriteLine(
                    LogIdent,
                    "ReShade could not be removed from Studio while it is running, it is removed on the next Studio launch"
                );
            }
        }

        private static IEnumerable<string> OwnedHashes(string studioDirectory) =>
            ReadMarker()
                .Where(entry => entry.Directory.Equals(studioDirectory, StringComparison.OrdinalIgnoreCase))
                .Select(entry => entry.Hash);

        private static void RecordInstall(string studioDirectory, string hash)
        {
            var entries = ReadMarker()
                .Where(entry =>
                    !(
                        entry.Directory.Equals(studioDirectory, StringComparison.OrdinalIgnoreCase)
                        && entry.Hash.Equals(hash, StringComparison.OrdinalIgnoreCase)
                    )
                )
                .ToList();
            entries.Add((studioDirectory, hash));
            WriteMarker(entries);
        }

        private static List<(string Directory, string Hash)> ReadMarker()
        {
            try
            {
                if (!File.Exists(MarkerPath))
                    return [];
                return File.ReadAllLines(MarkerPath)
                    .Select(line => line.Split('\t'))
                    .Where(parts => parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                    .Select(parts => (parts[0], parts[1]))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }

        private static void WriteMarker(IEnumerable<(string Directory, string Hash)> entries)
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllLines(MarkerPath, entries.Select(entry => entry.Directory + "\t" + entry.Hash));
        }

        private static string Hash(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
