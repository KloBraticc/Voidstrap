using Microsoft.Win32;

namespace Voidstrap.Integrations;

public static partial class SteamVr
{
    public const string AppId = "250820";
    private const string RuntimeVariable = "XR_RUNTIME_JSON";
    private const string VulkanFlag = "FFlagDebugGraphicsPreferVulkan";

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPathPattern { get; }

    [GeneratedRegex("\"(SizeOnDisk|buildid)\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ManifestValuePattern { get; }

    public static bool IsSupported => Voidstrap.Utility.Platform.IsWindows;

    public static string? SteamRoot
    {
        get
        {
            if (!IsSupported)
                return null;
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                string? path = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    return Path.GetFullPath(path);
            }
            catch { }
            return null;
        }
    }

    public static bool IsSteamInstalled => SteamRoot != null;

    private static IEnumerable<string> Libraries(string steamRoot)
    {
        yield return steamRoot;
        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        string text;
        try
        {
            text = File.Exists(vdf) ? File.ReadAllText(vdf) : "";
        }
        catch
        {
            yield break;
        }
        foreach (Match match in LibraryPathPattern.Matches(text))
            yield return match.Groups[1].Value.Replace(@"\\", @"\");
    }

    public sealed record Install(string Folder, string Manifest, long SizeOnDisk, string BuildId);

    public static Install? Find()
    {
        string? root = SteamRoot;
        if (root == null)
            return null;
        foreach (string library in Libraries(root).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string folder = Path.Combine(library, "steamapps", "common", "SteamVR");
                string manifest = Path.Combine(library, "steamapps", $"appmanifest_{AppId}.acf");
                if (!File.Exists(Path.Combine(folder, "bin", "win64", "vrstartup.exe")))
                    continue;
                long size = 0;
                string build = "";
                if (File.Exists(manifest))
                {
                    foreach (Match match in ManifestValuePattern.Matches(File.ReadAllText(manifest)))
                    {
                        if (match.Groups[1].Value.Equals("SizeOnDisk", StringComparison.OrdinalIgnoreCase))
                            long.TryParse(match.Groups[2].Value, out size);
                        else
                            build = match.Groups[2].Value;
                    }
                }
                return new Install(folder, manifest, size, build);
            }
            catch { }
        }
        return null;
    }

    public static bool RequestInstall() => OpenSteamUri("steam://install/" + AppId);

    public static bool RequestUninstall() => OpenSteamUri("steam://uninstall/" + AppId);

    private static bool OpenSteamUri(string uri)
    {
        const string LOG_IDENT = "SteamVr::OpenSteamUri";
        if (!IsSteamInstalled)
            return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
            App.Logger.WriteLine(LOG_IDENT, "Asked Steam to open " + uri);
            return true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Steam could not be opened: " + ex.Message);
            return false;
        }
    }

    public static bool PrefersVulkan()
    {
        return string.Equals(App.FastFlags.GetValue(VulkanFlag), "True", StringComparison.OrdinalIgnoreCase);
    }

    public static bool PrepareLaunch(Install install)
    {
        const string LOG_IDENT = "SteamVr::PrepareLaunch";
        string runtime = Path.Combine(install.Folder, "steamxr_win64.json");
        if (File.Exists(runtime))
            Environment.SetEnvironmentVariable(RuntimeVariable, runtime);
        else
            App.Logger.WriteLine(LOG_IDENT, "SteamVR OpenXR runtime manifest is missing, using the system default runtime");

        if (!App.GlobalSettings.GetBool("VREnabled"))
        {
            App.GlobalSettings.SetProperty("VREnabled", true);
            if (App.GlobalSettings.Save())
            {
                App.Settings.Prop.SteamVrEnabledVr = true;
                App.Settings.Save();
            }
        }

        try
        {
            if (Process.GetProcessesByName("vrserver").Length == 0)
            {
                Process.Start(
                        new ProcessStartInfo(Path.Combine(install.Folder, "bin", "win64", "vrstartup.exe"))
                        {
                            UseShellExecute = false,
                            WorkingDirectory = Path.Combine(install.Folder, "bin", "win64"),
                        }
                    )
                    ?.Dispose();
                App.Logger.WriteLine(LOG_IDENT, "Started SteamVR");
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "SteamVR could not be started: " + ex.Message);
            return false;
        }
        return true;
    }

    public static void RestoreDesktop()
    {
        const string LOG_IDENT = "SteamVr::RestoreDesktop";
        if (!IsSupported)
            return;
        Environment.SetEnvironmentVariable(RuntimeVariable, null);
        if (!App.Settings.Prop.SteamVrEnabledVr)
            return;
        App.GlobalSettings.SetProperty("VREnabled", false);
        if (App.GlobalSettings.Save())
        {
            App.Settings.Prop.SteamVrEnabledVr = false;
            App.Settings.Save();
            App.Logger.WriteLine(LOG_IDENT, "Turned Roblox VR back off for a desktop launch");
        }
    }
}
