using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.RiShade
{
    public static class RiShadeManager
    {
        private const string LogIdent = "RiShade";
        private const string ReShadeSetupUrl = "https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe";
        private const string ReShadeSetupSha256 = "AFE4C8F13048306307983B8B3D41D5BF00A86820440B0E57DEA10950E1176445";
        private const long MaxSetupBytes = 512L * 1024 * 1024;
        private static readonly SemaphoreSlim SetupGate = new(1, 1);
        private static bool _installed;
        private static readonly object RuntimeLock = new();
        private static IntPtr _runtime;
        private static volatile bool _hasLoadablePreset;

        public static bool HasLoadablePreset
        {
            get
            {
                if (_hasLoadablePreset)
                    return true;
                string? presetPath = CurrentPresetPath;
                if (!string.IsNullOrWhiteSpace(presetPath))
                {
                    string checkPath = Path.IsPathRooted(presetPath)
                        ? presetPath
                        : Path.Combine(RiShadeDir, presetPath);
                    _hasLoadablePreset = RiShadeInterop.PresetFileIsLoadable(checkPath);
                    if (_hasLoadablePreset)
                        return true;
                }
                string iniPath = Path.Combine(RiShadeDir, "ReShade.ini");
                if (File.Exists(iniPath))
                {
                    try
                    {
                        foreach (string line in File.ReadLines(iniPath))
                        {
                            if (line.StartsWith("PresetPath=", StringComparison.OrdinalIgnoreCase))
                            {
                                string val = line["PresetPath=".Length..].Trim();
                                if (!string.IsNullOrEmpty(val))
                                {
                                    string checkPath = Path.IsPathRooted(val) ? val : Path.Combine(RiShadeDir, val);
                                    _hasLoadablePreset = RiShadeInterop.PresetFileIsLoadable(checkPath);
                                    return _hasLoadablePreset;
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                return false;
            }
        }

        public static string RiShadeDir => Paths.RiShade;
        public static string DxgiDllPath => Path.Combine(Paths.RiShade, "dxgi.dll");
        public static string ShadersDirPath => Path.Combine(Paths.RiShade, "reshade-shaders");

        public static bool IsRuntimePopulated =>
            File.Exists(DxgiDllPath) &&
            File.Exists(Path.Combine(RiShadeDir, "ReShade.ini"));

        public static bool IsCompilingEffects
        {
            get
            {
                lock (RuntimeLock)
                    return _runtime != IntPtr.Zero && RiShadeInterop.IsCompilingEffects();
            }
        }

        public static bool IsLoadingEffects
        {
            get
            {
                lock (RuntimeLock)
                    return _runtime != IntPtr.Zero && RiShadeInterop.IsLoadingEffects();
            }
        }

        public static bool IsOverlayOpen
        {
            get
            {
                lock (RuntimeLock)
                    return _runtime != IntPtr.Zero && RiShadeInterop.IsOverlayOpen();
            }
        }

        public static string? CurrentPresetPath
        {
            get
            {
                lock (RuntimeLock)
                {
                    if (_runtime == IntPtr.Zero)
                        return null;
                    IntPtr buffer = Marshal.AllocHGlobal(32768);
                    try
                    {
                        nuint length = RiShadeInterop.CopyCurrentPresetPath(buffer, 32768);
                        return length is 0 or >= 32768 ? null : Marshal.PtrToStringUTF8(buffer);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
        }

        public static IReadOnlyList<string> GetAvailablePresets()
        {
            string root = Path.Combine(RiShadeDir, "presets");
            var presets = new List<string>();
            if (!Directory.Exists(root))
                return presets;

            try
            {
                if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                    return presets;
                AddPresetsInFolder(root, presets);
                foreach (string folder in Directory.EnumerateDirectories(root))
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) == 0)
                        AddPresetsInFolder(folder, presets);
                }
                presets.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(
                    Path.GetFileNameWithoutExtension(left), Path.GetFileNameWithoutExtension(right)));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeManager::GetAvailablePresets", ex);
            }
            return presets;
        }

        public static bool SelectPreset(string presetPath)
        {
            if (!Voidstrap.Utility.Platform.IsWindows || string.IsNullOrWhiteSpace(presetPath))
                return false;

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(presetPath);
                if (!GetAvailablePresets().Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                    return false;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;
            }

            lock (RuntimeLock)
            {
                bool selected = _runtime != IntPtr.Zero && RiShadeInterop.RuntimeSelectPreset(_runtime, fullPath);
                if (selected)
                    _hasLoadablePreset = true;
                return selected;
            }
        }

        private static void AddPresetsInFolder(string folder, List<string> presets)
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*.ini", SearchOption.TopDirectoryOnly))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0 &&
                    RiShadeInterop.PresetFileIsLoadable(path))
                    presets.Add(Path.GetFullPath(path));
            }
        }

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;
            CleanLog();
            App.Logger.WriteLine(LogIdent, "Installed, enabled setting is " + App.Settings.Prop.RiShadeEnabled);
        }

        public static void SetEnabled(bool enabled)
        {
            Install();
            if (enabled && !App.Settings.Prop.RiShadeEnabled)
            {
                MessageBoxResult result = Frontend.ShowMessageBox(
                    "RiShade runs official ReShade with add-on shaders and presets for Roblox.\n\nEnable RiShade?",
                    MessageBoxImage.Information,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.Yes);
                if (result != MessageBoxResult.Yes)
                    return;
            }

            if (!enabled)
                RiShadePanel.Close();

            App.Settings.Prop.RiShadeEnabled = enabled;
            App.Settings.SaveDeferred();
            App.Logger.WriteLine(LogIdent, enabled ? "Enabled by user" : "Disabled by user");
            Voidstrap.Integrations.Overlays.OverlayHub.Refresh();

            if (enabled && Voidstrap.Utility.Platform.IsWindows && Voidstrap.Integrations.Overlays.OverlayHub.RobloxProcessRunning)
            {
                _ = EnsureSetupAsync();
            }
            Voidstrap.UI.Elements.ContextMenu.MenuContainer.SyncRiShadeHotkey();
        }

        public static async Task EnsureSetupAsync()
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return;

            await SetupGate.WaitAsync();
            try
            {
                CleanLog();
                Directory.CreateDirectory(RiShadeDir);
                if (!IsRuntimePopulated)
                    await ProvisionRuntimeAsync();
                await InstallEffectAssetsAsync();
                if (!RiShadeInterop.PrepareConfig(RiShadeDir))
                    throw new InvalidOperationException("The Rust RiShade library could not prepare ReShade configuration");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await InstallDepthAssetsAsync();
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException("RiShadeManager::BackgroundDepthSetup", ex);
                    }
                });

                string defaultPreset = Path.Combine(RiShadeDir, "presets", "GenericPreset1.ini");
                if (!_hasLoadablePreset && File.Exists(defaultPreset) && SelectPreset(defaultPreset))
                    App.Logger.WriteLine(LogIdent, "Loaded the bundled GenericPreset1 preset into the active ReShade runtime");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeManager::EnsureSetupAsync", ex);
            }
            finally
            {
                SetupGate.Release();
            }
        }

        public static async Task InstallDepthAssetsAsync()
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                throw new PlatformNotSupportedException("RiShade depth assets are only available on Windows.");

            await SetupGate.WaitAsync();
            try
            {
                CleanLog();
                Directory.CreateDirectory(RiShadeDir);
                await Task.Run(() =>
                {
                    IntPtr statusBuffer = Marshal.AllocHGlobal(2048);
                    try
                    {
                        bool installed = RiShadeInterop.InstallDepthAssets(RiShadeDir, statusBuffer, 2048);
                        string status = Marshal.PtrToStringUTF8(statusBuffer) ?? "RiShade depth asset setup did not return a status";
                        App.Logger.WriteLine(LogIdent, status);
                        if (!installed)
                            throw new InvalidOperationException(status);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(statusBuffer);
                    }
                });
            }
            finally
            {
                SetupGate.Release();
            }
        }

        private static async Task ProvisionRuntimeAsync()
        {
            string stagingDir = Path.Combine(Path.GetTempPath(), "Voidstrap-RiShade-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);
            try
            {
                App.Logger.WriteLine(LogIdent, "Downloading the pinned ReShade add-on runtime");
                string setupExe = Path.Combine(stagingDir, "ReShade-Setup.exe");
                using (HttpResponseMessage response = await App.HttpClient.GetAsync(ReShadeSetupUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    Uri? finalUri = response.RequestMessage?.RequestUri;
                    if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps ||
                        !finalUri.Host.Equals("reshade.me", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The ReShade setup download did not come from the official HTTPS host");
                    if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > MaxSetupBytes))
                        throw new InvalidDataException("The ReShade setup download has an invalid size");

                    await using Stream source = await response.Content.ReadAsStreamAsync();
                    await using var destination = new FileStream(setupExe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    byte[] buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer)) > 0)
                    {
                        downloaded += read;
                        if (downloaded > MaxSetupBytes)
                            throw new InvalidDataException("The ReShade setup download exceeded the size limit");
                        await destination.WriteAsync(buffer.AsMemory(0, read));
                    }
                }

                await using var setupFile = new FileStream(setupExe, FileMode.Open, FileAccess.Read, FileShare.Read);
                byte[] setupHash = await SHA256.HashDataAsync(setupFile);
                if (!Convert.ToHexString(setupHash).Equals(ReShadeSetupSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The ReShade setup download failed its pinned SHA-256 check");

                string appExe = Environment.ProcessPath ?? throw new InvalidOperationException("Voidstrap's executable path is unavailable");
                string targetExe = Path.Combine(stagingDir, "RiShadeSetupTarget.exe");
                File.Copy(appExe, targetExe);

                var psi = new ProcessStartInfo
                {
                    FileName = setupExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = stagingDir,
                };
                psi.ArgumentList.Add("--headless");
                psi.ArgumentList.Add("--api");
                psi.ArgumentList.Add("dxgi");
                psi.ArgumentList.Add(targetExe);

                App.Logger.WriteLine(LogIdent, "Installing ReShade into a temporary staging directory");
                using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("ReShade setup could not be started");
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException($"ReShade setup failed with code {proc.ExitCode}");

                string stagedRuntime = Path.Combine(stagingDir, "dxgi.dll");
                string stagedConfig = Path.Combine(stagingDir, "ReShade.ini");
                string stagedShaders = Path.Combine(stagingDir, "reshade-shaders");
                if (!File.Exists(stagedRuntime) || !File.Exists(stagedConfig))
                    throw new InvalidDataException("ReShade setup did not produce the required runtime files");

                string replacementPath = Path.Combine(RiShadeDir, "dxgi.dll.new");
                File.Copy(stagedRuntime, replacementPath, true);
                File.Move(replacementPath, DxgiDllPath, true);
                CopyIfMissing(stagedConfig, Path.Combine(RiShadeDir, "ReShade.ini"));
                CopyIfMissing(Path.Combine(stagingDir, "ReShadePreset.ini"), Path.Combine(RiShadeDir, "ReShadePreset.ini"));
                if (Directory.Exists(stagedShaders))
                    CopyMissingFiles(stagedShaders, ShadersDirPath);
                App.Logger.WriteLine(LogIdent, "ReShade runtime installed in the RiShade extension directory");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeManager::ProvisionRuntimeAsync", ex);
                throw;
            }
            finally
            {
                try
                {
                    Directory.Delete(stagingDir, true);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("RiShadeManager::CleanupSetupStaging", ex);
                }
            }
        }

        private static async Task InstallEffectAssetsAsync()
        {
            await Task.Run(() =>
            {
                IntPtr statusBuffer = Marshal.AllocHGlobal(2048);
                try
                {
                    bool complete = RiShadeInterop.InstallEffectsAndPresets(RiShadeDir, statusBuffer, 2048);
                    string status = Marshal.PtrToStringUTF8(statusBuffer) ?? "RiShade asset setup did not return a status";
                    App.Logger.WriteLine(LogIdent, status);
                    if (!complete)
                        throw new InvalidOperationException(status);
                }
                finally
                {
                    Marshal.FreeHGlobal(statusBuffer);
                }
            });
        }

        private static void CopyIfMissing(string source, string destination)
        {
            if (File.Exists(source) && !File.Exists(destination))
                File.Copy(source, destination);
        }

        private static void CopyMissingFiles(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory))
                CopyIfMissing(sourceFile, Path.Combine(destinationDirectory, Path.GetFileName(sourceFile)));
            foreach (string sourceChild in Directory.EnumerateDirectories(sourceDirectory))
            {
                if ((File.GetAttributes(sourceChild) & FileAttributes.ReparsePoint) != 0)
                    continue;
                CopyMissingFiles(sourceChild, Path.Combine(destinationDirectory, Path.GetFileName(sourceChild)));
            }
        }

        public static void OnRobloxProcessStarted()
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return;
            CleanLog();
            Voidstrap.Integrations.Overlays.OverlayHub.SetRobloxProcessRunning(true);
            if (App.Settings.Prop.RiShadeEnabled)
                _ = EnsureSetupAsync();
        }

        public static void OnRobloxProcessStopped()
        {
            CleanLog();
            Voidstrap.Integrations.Overlays.OverlayHub.SetRobloxProcessRunning(false);
        }

        public static void SetRuntime(IntPtr runtime)
        {
            lock (RuntimeLock)
            {
                _runtime = runtime;
                _hasLoadablePreset = false;
            }

            if (runtime != IntPtr.Zero)
            {
                try
                {
                    string? presetPath = CurrentPresetPath;
                    if (!string.IsNullOrWhiteSpace(presetPath))
                    {
                        string checkPath = Path.IsPathRooted(presetPath)
                            ? presetPath
                            : Path.Combine(RiShadeDir, presetPath);
                        _hasLoadablePreset = RiShadeInterop.PresetFileIsLoadable(checkPath);
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("RiShadeManager::CheckCurrentPreset", ex);
                }
            }
            if (runtime == IntPtr.Zero)
                RiShadePanel.RuntimeUnavailable();
        }

        public static bool ToggleOverlayMenu(bool open)
        {
            lock (RuntimeLock)
            {
                if (_runtime == IntPtr.Zero)
                    return false;
                return RiShadeInterop.RuntimeOpenOverlay(_runtime, open);
            }
        }

        public static void CleanLog()
        {
            try
            {
                string[] targets =
                {
                    "ReShade.log",
                    "ReShade.previous.log",
                    "RiShade.log",
                    "RiShade.old.log",
                    "RiShade.previous.log"
                };

                string[] folders =
                {
                    RiShadeDir,
                    Paths.Base,
                    Directory.GetCurrentDirectory(),
                    AppContext.BaseDirectory
                };

                foreach (string folder in folders)
                {
                    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                        continue;

                    foreach (string target in targets)
                    {
                        try
                        {
                            string path = Path.Combine(folder, target);
                            if (File.Exists(path))
                                File.Delete(path);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public static void Shutdown()
        {
            _installed = false;
            SetRuntime(IntPtr.Zero);
            CleanLog();
        }
    }
}
