using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        private const int SetupTimeoutMs = 120_000;
        private const int PresetStateIntervalMs = 1000;
        private const int OverlayRequestLifetimeMs = 15_000;
        private const int NoOverlayRequest = -1;
        private static readonly SemaphoreSlim SetupGate = new(1, 1);
        private static readonly object RuntimeLock = new();
        private static readonly Lazy<bool> DepthBridgeLibraryPresent = new(() =>
            NativeLibrary.TryLoad("rishade_depth_bridge", typeof(RiShadeManager).Assembly, null, out _));
        private static bool _installed;
        private static IntPtr _runtime;
        private static volatile bool _hasLoadablePreset;
        private static volatile bool _presetNeedsDepth;
        private static string? _presetStatePath;
        private static DateTime _presetStateWriteTime;
        private static long _nextPresetStateCheck;
        private static int _presetStateRefreshing;
        private static int _pendingOverlay = NoOverlayRequest;
        private static long _pendingOverlayExpiry;

        public static string RiShadeDir => Paths.RiShade;
        public static string DxgiDllPath => Path.Combine(Paths.RiShade, "dxgi.dll");
        public static string ShadersDirPath => Path.Combine(Paths.RiShade, "reshade-shaders");
        public static string DepthModelPath => Path.Combine(Paths.RiShade, "depth-anything-v2-small.onnx");

        private static string RuntimeMarkerPath => Path.Combine(Paths.RiShade, ".rishade-runtime");

        public static bool IsRuntimePopulated =>
            File.Exists(DxgiDllPath) &&
            File.Exists(Path.Combine(RiShadeDir, "ReShade.ini"));

        private static bool IsRuntimeCurrent
        {
            get
            {
                try
                {
                    return IsRuntimePopulated && File.Exists(RuntimeMarkerPath) &&
                        File.ReadAllText(RuntimeMarkerPath).Trim().Equals(ReShadeSetupSha256, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return false;
                }
            }
        }

        public static bool HasLoadablePreset => _hasLoadablePreset;

        public static bool PresetNeedsDepth => _presetNeedsDepth;

        public static bool DepthBridgeAvailable => DepthBridgeLibraryPresent.Value;

        public static bool HasRuntime
        {
            get
            {
                lock (RuntimeLock)
                    return _runtime != IntPtr.Zero;
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
                    nuint length = RiShadeInterop.CopyCurrentPresetPath(IntPtr.Zero, 0);
                    if (length is 0 or > 32767)
                        return null;
                    byte[] buffer = new byte[(int)length + 1];
                    unsafe
                    {
                        fixed (byte* pointer = buffer)
                        {
                            nuint copied = RiShadeInterop.CopyCurrentPresetPath((IntPtr)pointer, (nuint)buffer.Length);
                            return copied != length ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length);
                        }
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
                    Volatile.Write(ref _nextPresetStateCheck, 0);
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

        public static void RefreshPresetState()
        {
            long now = Environment.TickCount64;
            if (now < Volatile.Read(ref _nextPresetStateCheck))
                return;
            Volatile.Write(ref _nextPresetStateCheck, now + PresetStateIntervalMs);

            string? path = ResolvePresetPath(CurrentPresetPath);
            DateTime writeTime = DateTime.MinValue;
            if (path != null)
            {
                try
                {
                    writeTime = File.GetLastWriteTimeUtc(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
            if (string.Equals(path, _presetStatePath, StringComparison.OrdinalIgnoreCase) && writeTime == _presetStateWriteTime)
                return;
            if (Interlocked.Exchange(ref _presetStateRefreshing, 1) != 0)
                return;

            _presetStatePath = path;
            _presetStateWriteTime = writeTime;
            _ = Task.Run(() =>
            {
                try
                {
                    EvaluatePreset(path);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("RiShadeManager::EvaluatePreset", ex);
                }
                finally
                {
                    Volatile.Write(ref _presetStateRefreshing, 0);
                }
            });
        }

        private static string? ResolvePresetPath(string? presetPath)
        {
            if (string.IsNullOrWhiteSpace(presetPath))
                return null;
            try
            {
                return Path.GetFullPath(Path.IsPathRooted(presetPath) ? presetPath : Path.Combine(RiShadeDir, presetPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }
        }

        private static void EvaluatePreset(string? path)
        {
            bool loadable = path != null && RiShadeInterop.PresetFileIsLoadable(path);
            bool needsDepth = loadable && PresetUsesDepth(path!);
            if (loadable != _hasLoadablePreset || needsDepth != _presetNeedsDepth)
            {
                App.Logger.WriteLine(LogIdent, loadable
                    ? $"Active preset {Path.GetFileName(path)} is loaded, depth estimation {(needsDepth ? "on" : "off")}"
                    : "No loadable preset is active, the captured game image is shown unmodified");
            }
            _hasLoadablePreset = loadable;
            _presetNeedsDepth = needsDepth;
        }

        private static bool PresetUsesDepth(string presetPath)
        {
            string? techniques = File.ReadLines(presetPath)
                .TakeWhile(line => !line.TrimStart().StartsWith('['))
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.StartsWith("Techniques=", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(techniques))
                return false;

            var effectFiles = techniques["Techniques=".Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.IndexOf('@') is int at && at >= 0 ? entry[(at + 1)..] : "")
                .Where(file => file.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (effectFiles.Count == 0)
                return false;

            var located = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(ShadersDirPath))
            {
                foreach (string file in Directory.EnumerateFiles(ShadersDirPath, "*.fx", SearchOption.AllDirectories))
                    located.TryAdd(Path.GetFileName(file), file);
            }

            foreach (string effectFile in effectFiles)
            {
                if (!located.TryGetValue(effectFile, out string? source))
                    return true;
                if (File.ReadAllText(source).Contains("depth", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static bool RequestOverlayToggle()
        {
            int pending = Volatile.Read(ref _pendingOverlay);
            bool open = pending == NoOverlayRequest ? !IsOverlayOpen : pending == 0;
            Volatile.Write(ref _pendingOverlayExpiry, Environment.TickCount64 + OverlayRequestLifetimeMs);
            Volatile.Write(ref _pendingOverlay, open ? 1 : 0);
            return open;
        }

        public static void RequestOverlayClose()
        {
            Volatile.Write(ref _pendingOverlayExpiry, Environment.TickCount64 + OverlayRequestLifetimeMs);
            Volatile.Write(ref _pendingOverlay, 0);
        }

        public static void ApplyPendingOverlay()
        {
            int request = Interlocked.Exchange(ref _pendingOverlay, NoOverlayRequest);
            if (request == NoOverlayRequest)
                return;
            if (Environment.TickCount64 > Volatile.Read(ref _pendingOverlayExpiry))
            {
                App.Logger.WriteLine(LogIdent, "An overlay request expired before the ReShade runtime was ready");
                return;
            }

            bool open = request == 1;
            bool applied;
            lock (RuntimeLock)
                applied = _runtime != IntPtr.Zero && RiShadeInterop.RuntimeOpenOverlay(_runtime, open);
            App.Logger.WriteLine(LogIdent, applied
                ? (open ? "ReShade overlay opened" : "ReShade overlay closed")
                : "ReShade did not accept the overlay request");
            RiShadePanel.NotifyOpenChanged(IsOverlayOpen);
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
                    "RiShade runs official ReShade as an overlay on top of Roblox. It never injects anything into the Roblox client.\n\nEnable RiShade?",
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
                Directory.CreateDirectory(RiShadeDir);
                if (!IsRuntimeCurrent)
                {
                    try
                    {
                        await ProvisionRuntimeAsync();
                    }
                    catch (Exception) when (IsRuntimePopulated)
                    {
                        App.Logger.WriteLine(LogIdent, "The ReShade runtime update could not be applied yet, the installed runtime stays in use until the next launch");
                    }
                }
                await InstallEffectAssetsAsync();
                if (!RiShadeInterop.PrepareConfig(RiShadeDir))
                    throw new InvalidOperationException("The Rust RiShade library could not prepare ReShade configuration");

                if (DepthBridgeAvailable)
                {
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
                }
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
                Directory.CreateDirectory(RiShadeDir);
                await Task.Run(() =>
                {
                    string status = CallWithStatus(RiShadeInterop.InstallDepthAssets, "RiShade depth asset setup did not return a status", out bool installed);
                    App.Logger.WriteLine(LogIdent, status);
                    if (!installed)
                        throw new InvalidOperationException(status);
                });
            }
            finally
            {
                SetupGate.Release();
            }
        }

        private static string CallWithStatus(Func<string, IntPtr, nuint, bool> call, string fallback, out bool success)
        {
            IntPtr statusBuffer = Marshal.AllocHGlobal(2048);
            try
            {
                Marshal.WriteByte(statusBuffer, 0);
                success = call(RiShadeDir, statusBuffer, 2048);
                string? status = Marshal.PtrToStringUTF8(statusBuffer);
                return string.IsNullOrWhiteSpace(status) ? fallback : status;
            }
            finally
            {
                Marshal.FreeHGlobal(statusBuffer);
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

                await using (var setupFile = new FileStream(setupExe, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] setupHash = await SHA256.HashDataAsync(setupFile);
                    if (!Convert.ToHexString(setupHash).Equals(ReShadeSetupSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The ReShade setup download failed its pinned SHA 256 check");
                }

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
                using (Process proc = Process.Start(psi) ?? throw new InvalidOperationException("ReShade setup could not be started"))
                {
                    using var timeout = new CancellationTokenSource(SetupTimeoutMs);
                    try
                    {
                        await proc.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        try
                        {
                            proc.Kill(true);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                        }
                        throw new TimeoutException("ReShade setup did not finish in time");
                    }
                    if (proc.ExitCode != 0)
                        throw new InvalidOperationException($"ReShade setup failed with code {proc.ExitCode}");
                }

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
                File.WriteAllText(RuntimeMarkerPath, ReShadeSetupSha256);
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
                string status = CallWithStatus(RiShadeInterop.InstallEffectsAndPresets, "RiShade asset setup did not return a status", out bool complete);
                App.Logger.WriteLine(LogIdent, status);
                if (!complete)
                    throw new InvalidOperationException(status);
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
            Voidstrap.Integrations.Overlays.OverlayHub.SetRobloxProcessRunning(true);
            if (App.Settings.Prop.RiShadeEnabled)
                _ = EnsureSetupAsync();
        }

        public static void OnRobloxProcessStopped()
        {
            Voidstrap.Integrations.Overlays.OverlayHub.SetRobloxProcessRunning(false);
        }

        public static void SetRuntime(IntPtr runtime)
        {
            lock (RuntimeLock)
                _runtime = runtime;

            _presetStatePath = null;
            _presetStateWriteTime = DateTime.MinValue;
            _hasLoadablePreset = false;
            _presetNeedsDepth = false;
            Volatile.Write(ref _nextPresetStateCheck, 0);
            if (runtime == IntPtr.Zero)
                RiShadePanel.NotifyOpenChanged(false);
        }

        private static void CleanLog()
        {
            string[] targets =
            {
                "ReShade.log",
                "ReShade.previous.log",
                "RiShade.log",
                "RiShade.old.log",
                "RiShade.previous.log"
            };

            foreach (string folder in new[] { RiShadeDir, Paths.Base })
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
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        public static void Shutdown()
        {
            _installed = false;
            Interlocked.Exchange(ref _pendingOverlay, NoOverlayRequest);
            SetRuntime(IntPtr.Zero);
        }
    }
}
