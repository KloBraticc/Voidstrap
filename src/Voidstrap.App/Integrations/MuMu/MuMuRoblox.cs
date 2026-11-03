using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Voidstrap.Integrations.RiShade;

namespace Voidstrap.Integrations.MuMu
{
    public static class MuMuRoblox
    {
        private const string AndroidVersion = "15.0";
        private const string InstanceName = "Voidstrap";
        private const string LayerName = "VK_LAYER_reshade";
        private const string LayerKey = @"Software\Khronos\Vulkan\ImplicitLayers";
        private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MuMuPlayerGlobal";
        private const string EnableVariable = "VOIDSTRAP_MUMU_DEPTH";

        private static readonly string[] InstallerHosts = ["api.mumuplayer.com", "a11.gdl.netease.com"];

        public static string DepthDir => Path.Combine(Paths.Extensions, "MuMuDepth");

        private static string LayerManifest => Path.Combine(DepthDir, "ReShade64.json");

        public static string? FindInstallDir()
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return null;
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(UninstallKey);
            string? dir = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "nx_main", "MuMuManager.exe")))
                return null;
            return dir;
        }

        public static bool IsInstalled => FindInstallDir() != null;

        private static string ManagerPath(string dir) => Path.Combine(dir, "nx_main", "MuMuManager.exe");

        private static string AdbPath(string dir) => Path.Combine(dir, "nx_main", "adb.exe");

        private static string ShellDir(string dir) => Path.Combine(dir, "nx_device", AndroidVersion, "shell");

        private static string VmName(int index) => $"MuMuPlayerGlobal-{AndroidVersion}-{index}";

        private static string ConfigDir(string dir, int index) => Path.Combine(dir, "vms", VmName(index), "configs");

        private static async Task<string> RunAsync(string exe, string arguments, CancellationToken token)
        {
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process process = Process.Start(info) ?? throw new InvalidOperationException($"{Path.GetFileName(exe)} could not be started");
            Task<string> output = process.StandardOutput.ReadToEndAsync(token);
            Task<string> error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            return (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false));
        }

        private static Task<string> ManagerAsync(string dir, string arguments, CancellationToken token) =>
            RunAsync(ManagerPath(dir), arguments, token);

        private static async Task<JsonObject?> InfoAsync(string dir, int index, CancellationToken token)
        {
            string text = await ManagerAsync(dir, $"info -v {index}", token).ConfigureAwait(false);
            int start = text.IndexOf('{');
            if (start < 0)
                return null;
            try
            {
                return JsonNode.Parse(text[start..]) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static async Task<string> InstallMuMuAsync(Action<string> status, CancellationToken token)
        {
            const string LOG_IDENT = "MuMuRoblox::InstallMuMuAsync";

            status(Strings.MuMu_Status_Installing);
            string installer = Path.Combine(Paths.Temp, "MuMuPlayerSetup.exe");
            Directory.CreateDirectory(Paths.Temp);
            using (HttpResponseMessage response = await App.HttpClient.GetAsync(App.MuMuInstallerUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                string host = response.RequestMessage?.RequestUri?.Host ?? "";
                if (!InstallerHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"the MuMu Player download came from an unexpected host {host}");
                await using FileStream file = File.Create(installer);
                await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
            }
            App.Logger.WriteLine(LOG_IDENT, "Running the official MuMu Player installer");
            using (Process process = Process.Start(new ProcessStartInfo(installer) { UseShellExecute = true }) ?? throw new InvalidOperationException("the MuMu Player installer could not be started"))
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            try
            {
                File.Delete(installer);
            }
            catch (IOException)
            {
            }
            return FindInstallDir() ?? throw new InvalidOperationException(Strings.MuMu_Error_Setup);
        }

        private static async Task<int> EnsureInstanceAsync(string dir, Action<string> status, CancellationToken token)
        {
            const string LOG_IDENT = "MuMuRoblox::EnsureInstanceAsync";

            int index = App.Settings.Prop.MuMuInstanceIndex;
            if (index > 0 && Directory.Exists(Path.Combine(dir, "vms", VmName(index))))
                return index;

            status(Strings.MuMu_Status_Preparing);
            var before = Directory.GetDirectories(Path.Combine(dir, "vms")).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await ManagerAsync(dir, "create --version 15", token).ConfigureAwait(false);
            string? created = Directory.GetDirectories(Path.Combine(dir, "vms"))
                .Select(Path.GetFileName)
                .FirstOrDefault(name => name != null && !before.Contains(name) && name.StartsWith($"MuMuPlayerGlobal-{AndroidVersion}-", StringComparison.OrdinalIgnoreCase));
            if (created == null || !int.TryParse(created[(created.LastIndexOf('-') + 1)..], out index))
                throw new InvalidOperationException(Strings.MuMu_Error_Setup);
            await ManagerAsync(dir, $"rename -v {index} --name {InstanceName}", token).ConfigureAwait(false);
            App.Settings.Prop.MuMuInstanceIndex = index;
            App.Settings.Save();
            App.Logger.WriteLine(LOG_IDENT, $"Created MuMu instance {index}");
            return index;
        }

        private static void SetNested(JsonObject root, string value, params string[] path)
        {
            JsonObject node = root;
            for (int i = 0; i < path.Length - 1; i++)
            {
                if (node[path[i]] is not JsonObject child)
                {
                    child = new JsonObject();
                    node[path[i]] = child;
                }
                node = child;
            }
            node[path[^1]] = value;
        }

        private static void EditJson(string file, Action<JsonObject> edit)
        {
            JsonObject root = File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject existing ? existing : new JsonObject();
            edit(root);
            File.WriteAllText(file, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        private static void ConfigureInstance(string dir, int index)
        {
            string configs = ConfigDir(dir, index);
            Directory.CreateDirectory(configs);
            EditJson(Path.Combine(configs, "customer_config.json"), root =>
            {
                SetNested(root, "false", "customer", "keymap_tips_show");
                SetNested(root, "false", "customer", "gamepad_tips_show");
                SetNested(root, "false", "customer", "auto_activate_intelligent_keymap_switch");
                SetNested(root, "0", "setting", "other_setting", "auto_activate_intelligent_keymap_switch");
                foreach (string shortcut in new[] { "go_home", "screenshot", "keymap_tip" })
                    SetNested(root, "null", "setting", "shortcut", "keys", shortcut, "text");
                SetNested(root, "9999", "customer", "desired_framerate");
            });
            EditJson(Path.Combine(configs, "shell_config.json"), root =>
            {
                root["fps_limit"] = "9999";
                root["fps_limit_real"] = "9999";
            });
        }

        private static void EnsureLayer(string dir)
        {
            Directory.CreateDirectory(DepthDir);
            string runtime = RiShadeManager.DxgiDllPath;
            if (!File.Exists(runtime))
                throw new InvalidOperationException(Strings.MuMu_Error_Setup);
            string layerDll = Path.Combine(DepthDir, "ReShade64.dll");
            if (!File.Exists(layerDll) || new FileInfo(layerDll).Length != new FileInfo(runtime).Length)
                File.Copy(runtime, layerDll, true);
            string addon = Path.Combine(AppContext.BaseDirectory, "mumu_depth.addon64");
            if (File.Exists(addon))
                File.Copy(addon, Path.Combine(DepthDir, "mumu_depth.addon64"), true);

            var manifest = new JsonObject
            {
                ["file_format_version"] = "1.0.0",
                ["layer"] = new JsonObject
                {
                    ["name"] = LayerName,
                    ["type"] = "GLOBAL",
                    ["library_path"] = ".\\ReShade64.dll",
                    ["api_version"] = "1.3.268",
                    ["implementation_version"] = "1",
                    ["description"] = "Voidstrap MuMu RiShade",
                    ["enable_environment"] = new JsonObject { [EnableVariable] = "1" },
                    ["disable_environment"] = new JsonObject { ["DISABLE_VK_LAYER_reshade_1"] = "1" },
                },
            };
            File.WriteAllText(LayerManifest, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(LayerKey))
                key.SetValue(LayerManifest, 0, RegistryValueKind.DWord);

            string preset = Path.Combine(DepthDir, "MuMuDepth.ini");
            if (!File.Exists(preset))
                File.WriteAllText(preset, "");
            string effects = Path.Combine(Paths.RiShade, "reshade-shaders");
            string ini = string.Join("\r\n",
                "[ADDON]",
                $"AddonPath={DepthDir}",
                "DisabledAddons=Generic Depth",
                "",
                "[GENERAL]",
                $"EffectSearchPaths={Path.Combine(effects, "Shaders")}\\**",
                $"TextureSearchPaths={Path.Combine(effects, "Textures")}\\**",
                $"PresetPath={preset}",
                "SkipLoadingDisabledEffects=1",
                "",
                "[INPUT]",
                "KeyOverlay=122,0,0,0",
                "",
                "[OVERLAY]",
                "TutorialProgress=4",
                "");
            File.WriteAllText(Path.Combine(ShellDir(dir), "ReShade.ini"), ini);
        }

        private static async Task StartInstanceAsync(string dir, int index, Action<string> status, CancellationToken token)
        {
            const string LOG_IDENT = "MuMuRoblox::StartInstanceAsync";

            JsonObject? info = await InfoAsync(dir, index, token).ConfigureAwait(false);
            if (info?["player_state"]?.GetValue<string>() == "start_finished")
                return;

            status(Strings.MuMu_Status_Starting);
            ConfigureInstance(dir, index);
            if (Process.GetProcessesByName("MuMuNxMain").Length == 0)
            {
                var main = new ProcessStartInfo(Path.Combine(dir, "nx_main", "MuMuNxMain.exe"))
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.Combine(dir, "nx_main"),
                };
                main.Environment[EnableVariable] = "1";
                main.Environment["VK_LOADER_LAYERS_ENABLE"] = LayerName;
                Process.Start(main)?.Dispose();
                await Task.Delay(2000, token).ConfigureAwait(false);
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, "MuMu Player was already open, RiShade effects need MuMu Player to be started by Voidstrap");
            }
            await ManagerAsync(dir, $"control -v {index} launch", token).ConfigureAwait(false);

            for (int i = 0; i < 240; i++)
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                info = await InfoAsync(dir, index, token).ConfigureAwait(false);
                if (info?["player_state"]?.GetValue<string>() == "start_finished")
                    return;
            }
            throw new TimeoutException(Strings.MuMu_Error_Setup);
        }

        private static async Task<string> AdbAsync(string dir, string serial, string arguments, CancellationToken token) =>
            await RunAsync(AdbPath(dir), $"-s {serial} {arguments}", token).ConfigureAwait(false);

        private static async Task<string> ConnectAsync(string dir, int index, CancellationToken token)
        {
            JsonObject? info = await InfoAsync(dir, index, token).ConfigureAwait(false);
            int port = 0;
            if (info?["adb_port"] is JsonNode portNode)
                int.TryParse(portNode.ToString(), out port);
            if (port <= 0)
                throw new InvalidOperationException(Strings.MuMu_Error_Setup);
            string serial = $"127.0.0.1:{port}";
            for (int i = 0; i < 60; i++)
            {
                await RunAsync(AdbPath(dir), $"connect {serial}", token).ConfigureAwait(false);
                string state = await AdbAsync(dir, serial, "get-state", token).ConfigureAwait(false);
                if (state.Contains("device", StringComparison.Ordinal))
                    return serial;
                await Task.Delay(1000, token).ConfigureAwait(false);
            }
            throw new TimeoutException(Strings.MuMu_Error_Setup);
        }

        private static async Task<bool> RobloxInstalledAsync(string dir, string serial, CancellationToken token) =>
            (await AdbAsync(dir, serial, $"shell pm path {App.MuMuRobloxPackage}", token).ConfigureAwait(false)).Contains("package:", StringComparison.Ordinal);

        private static async Task EnsureRobloxAsync(string dir, string serial, Action<string> status, CancellationToken token)
        {
            if (await RobloxInstalledAsync(dir, serial, token).ConfigureAwait(false))
                return;
            status(Strings.MuMu_Status_WaitingRoblox);
            await AdbAsync(dir, serial, $"shell am start -a android.intent.action.VIEW -d market://details?id={App.MuMuRobloxPackage}", token).ConfigureAwait(false);
            while (!await RobloxInstalledAsync(dir, serial, token).ConfigureAwait(false))
                await Task.Delay(3000, token).ConfigureAwait(false);
        }

        public static async Task LaunchAsync(string? deeplink, Action<string> status, CancellationToken token)
        {
            const string LOG_IDENT = "MuMuRoblox::LaunchAsync";

            string dir = FindInstallDir() ?? await InstallMuMuAsync(status, token).ConfigureAwait(false);
            await RiShadeManager.EnsureSetupAsync().ConfigureAwait(false);
            int index = await EnsureInstanceAsync(dir, status, token).ConfigureAwait(false);
            EnsureLayer(dir);
            await StartInstanceAsync(dir, index, status, token).ConfigureAwait(false);
            string serial = await ConnectAsync(dir, index, token).ConfigureAwait(false);
            await EnsureRobloxAsync(dir, serial, status, token).ConfigureAwait(false);

            status(Strings.MuMu_Status_Launching);
            string target = string.IsNullOrWhiteSpace(deeplink) || !deeplink.StartsWith("roblox", StringComparison.OrdinalIgnoreCase)
                ? $"shell monkey -p {App.MuMuRobloxPackage} -c android.intent.category.LAUNCHER 1"
                : $"shell am start -a android.intent.action.VIEW -d \"{deeplink.Replace("\"", "", StringComparison.Ordinal)}\"";
            await AdbAsync(dir, serial, target, token).ConfigureAwait(false);
            App.Logger.WriteLine(LOG_IDENT, $"Roblox launched in MuMu instance {index}");
        }

        public static void Uninstall()
        {
            const string LOG_IDENT = "MuMuRoblox::Uninstall";

            try
            {
                string? dir = FindInstallDir();
                int index = App.Settings.Prop.MuMuInstanceIndex;
                if (dir != null && index > 0)
                {
                    RunAsync(ManagerPath(dir), $"control -v {index} shutdown", CancellationToken.None).GetAwaiter().GetResult();
                    RunAsync(ManagerPath(dir), $"delete -v {index}", CancellationToken.None).GetAwaiter().GetResult();
                }
                if (dir != null)
                {
                    string ini = Path.Combine(ShellDir(dir), "ReShade.ini");
                    if (File.Exists(ini))
                        File.Delete(ini);
                }
                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(LayerKey, true))
                    key?.DeleteValue(LayerManifest, false);
                if (Directory.Exists(DepthDir))
                    Directory.Delete(DepthDir, true);
                App.Settings.Prop.MuMuInstanceIndex = 0;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }
    }
}
