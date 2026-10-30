using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Shell;
using System.Windows.Threading;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;
using Voidstrap.AppData;
using Voidstrap.Core;
using Voidstrap.Enums;
using Voidstrap.Exceptions;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Integrations.AssetProxy;
using Voidstrap.Models;
using Voidstrap.Models.APIs.Roblox;
using Voidstrap.Models.Manifest;
using Voidstrap.Models.Persistable;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;
using Voidstrap.Platform.MacOS;
using Voidstrap.Resources;
using Voidstrap.RobloxInterfaces;
using Voidstrap.UI;
using Voidstrap.UI.Elements.Bootstrapper.Base;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.Utility;

namespace Voidstrap;

public partial class Bootstrapper
{
    private async Task StartRoblox(CancellationToken ct = default)
    {
        Stopwatch startTimer = Stopwatch.StartNew();
        SetStatus("Starting Roblox");
        if (_safeMode)
            _safeLaunch = true;
        else
            await MaybeApplyVoidstrapMatchmakerAsync(ct);
        if (_launchMode == LaunchMode.Player)
            ReapplyFpsUnlocker();
        if (!Voidstrap.Utility.Platform.SupportsWindowsClient)
        {
            if (await TryLaunchNonWindowsClientAsync(_launchCommandLine, _launchMode, ct))
                return;
            App.Logger.WriteLine("Bootstrapper::StartRoblox", "No Roblox client available on this platform");
            return;
        }
        try
        {
            if (!_safeMode && RobloxAssetCache.ConsumeStorageCleared())
                await RestoreStoragePatchesAsync(ct);
            if (App.Settings.Prop.BlockRobloxTelemetry && !TelemetryBlocker.IsApplied())
            {
                if (ProcessElevation.IsAdministrator())
                {
                    TelemetryBlocker.Apply();
                }
                else
                {
                    App.Logger.WriteLine(
                        "Bootstrapper::StartRoblox",
                        "Telemetry block entries are missing and Voidstrap is not elevated, skipping reassert"
                    );
                }
            }
            if (!_safeMode)
                await LaunchCustomIntegrations("Bootstrapper::StartRoblox", preLaunch: true, ct);
            if (_launchMode == LaunchMode.Player && !_noConnection)
                await WarmRobloxSettingsDnsAsync(ct);
            App.Logger.WriteLine("Bootstrapper::StartRoblox", "Process preparation completed in " + startTimer.ElapsedMilliseconds + " ms");
            ProcessStartInfo startInfo = BuildStartInfo();
            if (_launchMode == LaunchMode.Studio)
                await Voidstrap.Integrations.RiShade.StudioReShade.PrepareLaunchAsync(
                    AppData.Directory,
                    startInfo,
                    App.Settings.Prop.StudioReShadeEnabled && !_safeMode
                );
            if (_launchMode == LaunchMode.StudioAuth)
            {
                using Process? studioAuthProcess = Process.Start(startInfo);
                return;
            }
            string text = Voidstrap.Utility.Platform.IsMacOS ? Paths.RobloxLogs : Path.Combine(Paths.LocalAppData, "Roblox", "logs");
            Directory.CreateDirectory(text);
            string? logFileName = await WaitForLogFileAsync(text, startInfo, ct);
            App.Logger.WriteLine(
                "Bootstrapper::StartRoblox",
                "Process start and readiness completed in " + startTimer.ElapsedMilliseconds + " ms"
            );
            if (_safeMode && App.State.Prop.SafeLaunchPending && App.State.Prop.CacheResetPending && !string.IsNullOrEmpty(logFileName))
            {
                App.State.Prop.SafeLaunchPending = false;
                App.State.Prop.CacheResetPending = false;
                App.State.Prop.LastSafeLaunchResult = SafeLaunchCache;
                App.State.Save();
                string cacheWorked =
                    "Roblox started once Voidstrap reset Roblox's local cache in "
                    + RobloxLocalReset.Root
                    + ", so a damaged cache there made Roblox crash while starting. Your login and graphics settings were kept, and Roblox rebuilds the cache by itself."
                    + (
                        string.IsNullOrEmpty(App.State.Prop.LastCacheBackup)
                            ? ""
                            : " The old cache was saved to " + App.State.Prop.LastCacheBackup + ", you can delete it."
                    );
                App.Logger.WriteLine(
                    "Bootstrapper::StartRoblox",
                    "Safe launch with a reset Roblox cache reached the renderer, the crash came from the Roblox local cache"
                );
                Voidstrap.Utility.AppNotifications.RecordInfo("roblox:cacheresetworked", "Roblox started after a cache reset", cacheWorked);
                Frontend.ShowMessageBox(cacheWorked, MessageBoxImage.Information);
            }
            if (_safeMode && App.State.Prop.SafeLaunchPending && !string.IsNullOrEmpty(logFileName))
            {
                App.State.Prop.SafeLaunchPending = false;
                App.State.Prop.LastSafeLaunchResult = SafeLaunchWorked;
                App.State.Save();
                string safeWorked = Voidstrap.Utility.Platform.IsLinux
                    ? "Roblox started once Voidstrap launched it without your mods, FastFlags, matchmaker and launch integrations, so one of those made Roblox crash while starting. They are all back on for your next launch. If Roblox crashes again, turn your mods off in My Mods first, then reset your FastFlags, then turn off AssetWarp, until it starts."
                    : "Roblox started once Voidstrap launched it without your mods, FastFlags, matchmaker and launch integrations, so one of those made Roblox crash while starting. They are all back on for your next launch. If Roblox crashes again, turn your mods off in My Mods first, then reset your FastFlags, until it starts.";
                App.Logger.WriteLine(
                    "Bootstrapper::StartRoblox",
                    "Safe launch reached the Roblox renderer, the crash comes from something Voidstrap adds"
                );
                Voidstrap.Utility.AppNotifications.RecordInfo("roblox:safelaunchworked", "Roblox started in a safe launch", safeWorked);
                Frontend.ShowMessageBox(safeWorked, MessageBoxImage.Information);
            }
            if (string.IsNullOrEmpty(logFileName))
            {
                App.Logger.WriteLine("Bootstrapper::StartRoblox", "Unable to identify log file.");
                Frontend.ShowPlayerErrorDialog();
                return;
            }
            App.Logger.WriteLine("Bootstrapper::StartRoblox", "Log file: " + logFileName);
            if (IsStudioLaunch)
            {
                await RunStudioSessionAsync(_cancelTokenSource.Token);
                return;
            }
            await LaunchCustomIntegrations("Bootstrapper::StartRoblox", preLaunch: false, ct);
            await LaunchWatcherIfNeeded(logFileName, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception value)
        {
            App.Logger.WriteLine("Bootstrapper::StartRoblox", $"Unexpected error in StartRoblox: {value}");
            Frontend.ShowPlayerErrorDialog();
        }
    }

    private async Task WarmRobloxSettingsDnsAsync(CancellationToken ct)
    {
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                await Dns.GetHostAddressesAsync("clientsettingscdn.roblox.com", ct).ConfigureAwait(false);
                if (attempt > 1)
                    App.Logger.WriteLine("Bootstrapper::StartRoblox", "Roblox's settings server resolved on attempt " + attempt);
                return;
            }
            catch (SocketException ex)
            {
                App.Logger.WriteLine(
                    "Bootstrapper::StartRoblox",
                    $"Roblox's settings server did not resolve ({ex.SocketErrorCode}), waiting for DNS before starting Roblox"
                );
                if (attempt == 1)
                    SetStatus("Waiting for your network");
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task MaybeApplyVoidstrapMatchmakerAsync(CancellationToken ct)
    {
        string rewritten = await RewriteVoidstrapMatchmakerBeforeDispatchAsync(
            _launchCommandLine,
            _launchMode,
            ct,
            () => SetStatus("Finding closest server...")
        );
        if (!string.Equals(rewritten, _launchCommandLine, StringComparison.Ordinal))
        {
            _launchCommandLine = rewritten;
            App.LaunchSettings.RobloxLaunchArgs = rewritten;
        }
    }

    internal static async Task<string> RewriteVoidstrapMatchmakerBeforeDispatchAsync(
        string launchCommandLine,
        LaunchMode launchMode,
        CancellationToken cancellationToken,
        Action? onSearching = null
    )
    {
        if (
            launchMode != LaunchMode.Player
            || App.LaunchSettings.MatchmakerRejoinFlag.Active
            || string.IsNullOrWhiteSpace(launchCommandLine)
        )
        {
            return launchCommandLine;
        }
        long placeId = LaunchInterceptor.ExtractPlaceId(launchCommandLine);
        if (placeId == 0 || !ServerMatchmaker.IsEnabled())
        {
            return launchCommandLine;
        }
        if (ServerMatchmaker.IsExcluded(placeId))
        {
            App.Logger.WriteLine(
                "Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync",
                $"Place {placeId} is excluded from the matchmaker, launching with the original URL."
            );
            return launchCommandLine;
        }
        if (LaunchInterceptor.ContainsSpecificGameInstance(launchCommandLine))
        {
            return launchCommandLine;
        }
        App.Logger.WriteLine(
            "Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync",
            "Checking the launch for the Voidstrap Matchmaker, maximum 30 seconds"
        );
        onSearching?.Invoke();
        MatchmakerDispatchTarget result = await ResolveMatchmakerDispatchTargetAsync(
            launchCommandLine,
            token => LaunchInterceptor.MaybeRewriteForClosestAsync(launchCommandLine, token),
            TimeSpan.FromSeconds(30L),
            cancellationToken
        );
        switch (result.Outcome)
        {
            case MatchmakerRewriteOutcome.Rewritten:
                App.Logger.WriteLine(
                    "Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync",
                    "Voidstrap Matchmaker rewrote launch URL to the closest server JobId."
                );
                break;
            case MatchmakerRewriteOutcome.Unchanged:
                App.Logger.WriteLine(
                    "Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync",
                    "Voidstrap Matchmaker did not rewrite launch URL."
                );
                break;
            case MatchmakerRewriteOutcome.Cancelled:
                App.Logger.WriteLine(
                    "Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync",
                    "Voidstrap Matchmaker timed out, launching with the original URL."
                );
                break;
            case MatchmakerRewriteOutcome.Failed:
                App.Logger.WriteLine(
                    "Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync",
                    "Voidstrap Matchmaker error, launching with the original URL: " + result.Error
                );
                break;
        }
        return result.Target;
    }

    internal static async Task<MatchmakerDispatchTarget> ResolveMatchmakerDispatchTargetAsync(
        string originalTarget,
        Func<CancellationToken, Task<string?>> rewrite,
        TimeSpan timeout,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            string? rewritten = await rewrite(timeoutCts.Token).ConfigureAwait(false);
            return string.IsNullOrEmpty(rewritten)
                ? new MatchmakerDispatchTarget(originalTarget, MatchmakerRewriteOutcome.Unchanged, null)
                : new MatchmakerDispatchTarget(rewritten, MatchmakerRewriteOutcome.Rewritten, null);
        }
        catch (OperationCanceledException)
        {
            return new MatchmakerDispatchTarget(originalTarget, MatchmakerRewriteOutcome.Cancelled, null);
        }
        catch (Exception exception)
        {
            return new MatchmakerDispatchTarget(originalTarget, MatchmakerRewriteOutcome.Failed, exception.Message);
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        string args = _launchCommandLine ?? string.Empty;
        string executablePath = AppData.ExecutablePath;
        string workingDirectory = AppData.Directory;
        ProcessStartInfo processStartInfo = new()
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        if (RobloxDeeplink.TryExtract(args, out Uri? deeplink) && deeplink != null)
            processStartInfo.ArgumentList.Add(deeplink.AbsoluteUri);
        else if (_launchMode == LaunchMode.Studio && TryAddStudioLocalPlaceArguments(processStartInfo, args)) { }
        else if (!string.IsNullOrWhiteSpace(args))
            throw new InvalidOperationException("Roblox launch arguments were invalid");
        if (_launchMode == LaunchMode.Player && ShouldRunAsAdmin())
        {
            processStartInfo.Verb = "runas";
            processStartInfo.UseShellExecute = true;
        }
        Voidstrap.Utility.EmulationBypassService.RestoreCompatLayers();
        return processStartInfo;
    }

    private static bool TryAddStudioLocalPlaceArguments(ProcessStartInfo startInfo, string arguments)
    {
        const string prefix = "-task EditFile -localPlaceFile \"";
        if (!arguments.StartsWith(prefix, StringComparison.Ordinal) || !arguments.EndsWith('"'))
            return false;
        string localPlaceFile = arguments[prefix.Length..^1];
        if (
            string.IsNullOrWhiteSpace(localPlaceFile)
            || localPlaceFile.Length > 32768
            || localPlaceFile.Contains('"')
            || localPlaceFile.Any(char.IsControl)
        )
            return false;
        startInfo.ArgumentList.Add("-task");
        startInfo.ArgumentList.Add("EditFile");
        startInfo.ArgumentList.Add("-localPlaceFile");
        startInfo.ArgumentList.Add(localPlaceFile);
        return true;
    }

    private async Task<string?> WaitForLogFileAsync(string rbxLogDir, ProcessStartInfo startInfo, CancellationToken ct)
    {
        HashSet<string> existingLogs = GetExistingLogFiles(rbxLogDir);
        string clientSettingsPath = Path.Combine(AppData.Directory, "ClientSettings", "ClientAppSettings.json");
        string blockedFlagsPath = Path.Combine(Paths.Cache, "BlockedFastFlags.txt");
        TrySuppressKnownBlockedFastFlags(clientSettingsPath, blockedFlagsPath);
        (string? logFile, bool timedOut, bool startupCrash) = await LaunchAndWaitForLogFileAsync(rbxLogDir, existingLogs, startInfo, ct)
            .ConfigureAwait(false);
        if (startupCrash && !ct.IsCancellationRequested)
        {
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Roblox crashed before its renderer started, relaunching once as is");
            Voidstrap.Utility.AppNotifications.RecordInfo(
                "roblox:startupcrash",
                "Roblox crashed while starting",
                "Roblox crashed before it finished loading, so Voidstrap relaunched it automatically."
            );
            SetStatus("Roblox crashed while starting, trying again");
            existingLogs = GetExistingLogFiles(rbxLogDir);
            (logFile, timedOut, startupCrash) = await LaunchAndWaitForLogFileAsync(rbxLogDir, existingLogs, startInfo, ct)
                .ConfigureAwait(false);
        }
        if (startupCrash && !ct.IsCancellationRequested && _launchMode == LaunchMode.Player)
        {
            _safeLaunch = true;
            App.Logger.WriteLine(
                "Bootstrapper::WaitForLogFile",
                "Roblox crashed again, relaunching without the process priority, memory limit and throttling changes"
            );
            SetStatus("Roblox crashed while starting, trying a safe launch");
            existingLogs = GetExistingLogFiles(rbxLogDir);
            (logFile, timedOut, startupCrash) = await LaunchAndWaitForLogFileAsync(rbxLogDir, existingLogs, startInfo, ct)
                .ConfigureAwait(false);
            if (!startupCrash && !string.IsNullOrEmpty(logFile))
                App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Roblox started once the launch time process changes were skipped");
        }
        if (startupCrash && !ct.IsCancellationRequested && (_launchMode != LaunchMode.Player || !File.Exists(clientSettingsPath)))
        {
            ReportRepeatedStartupCrash(logFile);
            return null;
        }
        if (!startupCrash && (!timedOut || !string.IsNullOrEmpty(logFile) || ct.IsCancellationRequested))
            return logFile;
        if (ct.IsCancellationRequested)
            return null;
        if (_launchMode != LaunchMode.Player || !File.Exists(clientSettingsPath))
        {
            TerminateFailedLaunchProcess();
            return null;
        }

        string? blockedHash = TryGetFileHash(clientSettingsPath);
        string disabledPath = Path.Combine(Paths.Temp, "LaunchRecovery", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            TerminateFailedLaunchProcess();
            Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);
            File.Move(clientSettingsPath, disabledPath, overwrite: true);
            App.Logger.WriteLine(
                "Bootstrapper::WaitForLogFile",
                "Roblox did not become ready, retrying once without the installed FastFlag file"
            );
            existingLogs = GetExistingLogFiles(rbxLogDir);
            (logFile, _, bool recoveryStartupCrash) = await LaunchAndWaitForLogFileAsync(rbxLogDir, existingLogs, startInfo, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(logFile) && !recoveryStartupCrash)
            {
                if (!string.IsNullOrEmpty(blockedHash))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(blockedFlagsPath)!);
                    File.WriteAllText(blockedFlagsPath, blockedHash);
                }
                App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Roblox recovered after the installed FastFlag file was disabled");
                return logFile;
            }
            if (!File.Exists(clientSettingsPath) && File.Exists(disabledPath))
                File.Move(disabledPath, clientSettingsPath, overwrite: true);
            TerminateFailedLaunchProcess();
            if ((startupCrash || recoveryStartupCrash) && !ct.IsCancellationRequested)
                ReportRepeatedStartupCrash(logFile);
            return null;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "FastFlag launch recovery failed: " + ex.Message);
            try
            {
                if (!File.Exists(clientSettingsPath) && File.Exists(disabledPath))
                    File.Move(disabledPath, clientSettingsPath, overwrite: true);
            }
            catch { }
            TerminateFailedLaunchProcess();
            return null;
        }
        finally
        {
            try
            {
                File.Delete(disabledPath);
            }
            catch { }
        }
    }

    private async Task<(string? LogFile, bool TimedOut, bool StartupCrash)> LaunchAndWaitForLogFileAsync(
        string rbxLogDir,
        HashSet<string> existingLogs,
        ProcessStartInfo startInfo,
        CancellationToken ct
    )
    {
        bool retainForRecovery = false;
        DateTime launchStartedUtc = DateTime.UtcNow;
        try
        {
            _robloxProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Roblox process.");
            int initialProcessId = _robloxProcess.Id;
            if (_launchMode == LaunchMode.Player && FleasionBridge.ManagesPlayerLaunch)
                _robloxProcess = await FleasionBridge.FollowPlayerAsync(_robloxProcess, AppData.ExecutablePath, ct).ConfigureAwait(false);
            if (_robloxProcess.Id != initialProcessId)
            {
                launchStartedUtc = _robloxProcess.StartTime.ToUniversalTime();
                ExcludeLogsBeforeProcess(rbxLogDir, existingLogs, launchStartedUtc);
            }
            _appPid = _robloxProcess.Id;
            _robloxLaunchUtc = launchStartedUtc;
            if (_launchMode == LaunchMode.Player)
            {
                AudioDucker.NotifyRobloxLaunched(_appPid);
                RobloxAudioRouting.NotifyRobloxLaunched(_appPid);
            }
            if (!_safeLaunch)
                RobloxProcessOptimizer.ApplyLaunchProfile(_robloxProcess);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (null, false, false);
        }
        using var logWaiter = new RobloxLogWaiter(rbxLogDir, existingLogs, launchStartedUtc);
        try
        {
            DateTime deadline = launchStartedUtc.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
                string? logFileName = FindNewLogFile(rbxLogDir, existingLogs, launchStartedUtc);
                if (!string.IsNullOrEmpty(logFileName))
                    return (logFileName, false, await CrashedBeforeRendererAsync(logFileName, ct).ConfigureAwait(false));

                Task delayTask = Task.Delay(500, ct);
                Task completedTask = await Task.WhenAny(logWaiter.Task, delayTask).ConfigureAwait(false);
                if (completedTask == logWaiter.Task)
                {
                    string createdLog = await logWaiter.Task.ConfigureAwait(false);
                    return (createdLog, false, await CrashedBeforeRendererAsync(createdLog, ct).ConfigureAwait(false));
                }
                if (ct.IsCancellationRequested)
                    return (null, false, false);

                try
                {
                    if (_robloxProcess?.HasExited == true)
                    {
                        string? exitedLog = FindNewLogFile(rbxLogDir, existingLogs, launchStartedUtc);
                        return (exitedLog, false, await CrashedBeforeRendererAsync(exitedLog, ct).ConfigureAwait(false));
                    }
                }
                catch
                {
                    return (null, false, false);
                }
            }
            string? finalLog = FindNewLogFile(rbxLogDir, existingLogs, launchStartedUtc);
            retainForRecovery = string.IsNullOrEmpty(finalLog);
            return (finalLog, retainForRecovery, false);
        }
        finally
        {
            if (!retainForRecovery)
            {
                _robloxProcess?.Dispose();
                _robloxProcess = null;
            }
        }
    }

    private bool _safeLaunch;

    private bool _safeMode;

    private const string SafeLaunchWorked = "worked";

    private const string SafeLaunchCrashed = "crashed";

    private const string SafeLaunchCache = "cache";

    private static readonly TimeSpan StartupCrashReinstallCooldown = TimeSpan.FromHours(6);

    private const string RendererReadyMarker = "shaders from pack";

    private static readonly TimeSpan RendererReadyTimeout = TimeSpan.FromSeconds(8);

    private async Task<bool> CrashedBeforeRendererAsync(string? logFile, CancellationToken ct)
    {
        Process? process = _robloxProcess;
        if (process == null || _launchMode != LaunchMode.Player)
            return false;
        try
        {
            Task exitTask = process.WaitForExitAsync(ct);
            DateTime deadline = DateTime.UtcNow + RendererReadyTimeout;
            long position = 0L;
            string tail = string.Empty;
            while (true)
            {
                if (!string.IsNullOrEmpty(logFile))
                {
                    string text = tail + ReadNewLogText(logFile, ref position);
                    if (text.Contains(RendererReadyMarker, StringComparison.Ordinal))
                        return false;
                    tail = text.Length > RendererReadyMarker.Length ? text[^RendererReadyMarker.Length..] : text;
                }
                if (process.HasExited)
                {
                    int exitCode = process.ExitCode;
                    bool crashed = IsCrashExitCode(exitCode);
                    App.Logger.WriteLine(
                        "Bootstrapper::WaitForLogFile",
                        $"Roblox exited before renderer readiness, code 0x{(uint)exitCode:X8}, classified as crash: {crashed}, log: {Path.GetFileName(logFile) ?? "none"}"
                    );
                    return crashed;
                }
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    App.Logger.WriteLine(
                        "Bootstrapper::WaitForLogFile",
                        $"Roblox did not report its renderer within {RendererReadyTimeout.TotalSeconds:0} seconds, skipping the startup crash check"
                    );
                    return false;
                }
                await Task.WhenAny(
                        exitTask,
                        Task.Delay(remaining < TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500), ct)
                    )
                    .ConfigureAwait(false);
                if (ct.IsCancellationRequested)
                    return false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsCrashExitCode(int exitCode)
    {
        return (uint)exitCode is >= 0xC0000000u and < 0xFFFFFFFFu;
    }

    private static void ReportRepeatedStartupCrash(string? robloxLogFile)
    {
        App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Roblox crashed before its renderer started on every attempt, giving up");
        if (App.State.Prop.SafeLaunchPending && !App.State.Prop.CacheResetPending)
        {
            App.State.Prop.CacheResetPending = true;
            App.State.Save();
            App.Logger.WriteLine(
                "Bootstrapper::WaitForLogFile",
                "Safe launch crashed, trying once more with Roblox's local cache moved aside"
            );
            Voidstrap.Utility.AppNotifications.RecordInfo(
                "roblox:cachereset",
                "Resetting the Roblox cache",
                "Roblox crashed in a safe launch too, so Voidstrap is moving Roblox's local cache aside and trying once more. Your login and graphics settings are kept."
            );
            if (App.RestartApplication(App.LaunchSettings.Args))
                return;
            App.State.Prop.CacheResetPending = false;
            App.State.Save();
        }
        if (App.State.Prop.SafeLaunchPending)
        {
            App.State.Prop.CacheResetPending = false;
            App.State.Prop.SafeLaunchPending = false;
            App.State.Prop.LastSafeLaunchResult = SafeLaunchCrashed;
            App.State.Save();
            string outside = Voidstrap.Utility.Platform.IsLinux
                ? "Roblox still crashes while starting with a fresh install, a reset Roblox cache and everything Voidstrap adds turned off: no mods, no FastFlags, no matchmaker and no launch integrations. The crash is inside the Roblox client or this PC, not in your Voidstrap setup. Update your graphics driver, close overlays and antivirus tools that inject into games, and try launching Roblox without Voidstrap to confirm."
                : "Roblox still crashes while starting with a fresh install, a reset Roblox cache and everything Voidstrap adds turned off: no mods, no FastFlags, no matchmaker and no launch integrations. The crash is inside the Roblox client or this PC, not in your Voidstrap setup. Update your graphics driver, close overlays and antivirus tools that inject into games, and try launching Roblox without Voidstrap to confirm.";
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Safe launch also crashed, the crash is outside anything Voidstrap adds");
            Voidstrap.Utility.AppNotifications.RecordInfo("roblox:safelaunchcrashed", "Roblox crashes even in a safe launch", outside);
            Frontend.ShowMessageBox(outside, MessageBoxImage.Warning);
            return;
        }
        ModCrashGuard.BeginSession();
        IReadOnlyList<string> disabled = ModCrashGuard.HandleCrash();
        if (DateTime.UtcNow - App.State.Prop.LastSafeLaunchUtc > StartupCrashReinstallCooldown)
        {
            App.State.Prop.SafeLaunchPending = true;
            App.State.Prop.LastSafeLaunchUtc = DateTime.UtcNow;
            App.State.Prop.LastSafeLaunchResult = string.Empty;
            App.State.Save();
            App.Settings.Prop.ForceRobloxReinstall = true;
            App.Settings.Prop.UpdateRoblox = true;
            App.Settings.Save();
            App.Logger.WriteLine(
                "Bootstrapper::WaitForLogFile",
                "Installing a fresh copy of Roblox automatically after repeated startup crashes"
            );
            Voidstrap.Utility.AppNotifications.RecordInfo(
                "roblox:startupcrashreinstall",
                "Roblox kept crashing while starting",
                "Voidstrap installed a fresh copy of Roblox because the installed one kept crashing while starting."
            );
            if (App.RestartApplication(App.LaunchSettings.Args))
                return;
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "The automatic reinstall could not restart Voidstrap");
        }
        if (App.State.Prop.LastSafeLaunchResult == SafeLaunchCrashed)
        {
            const string confirmed =
                "Roblox crashed while starting again. A safe launch in the last few hours already crashed the same way with a fresh Roblox install, a reset Roblox cache and nothing from Voidstrap turned on, so this crash is inside the Roblox client or this PC, not your Voidstrap setup. Update your graphics driver from your GPU maker's website, close overlays and antivirus tools that inject into games, and check whether Roblox starts from the official Roblox launcher.";
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Skipping another reinstall, the last safe launch already crashed");
            Voidstrap.Utility.AppNotifications.RecordInfo("roblox:safelaunchcrashed", "Roblox crashes even in a safe launch", confirmed);
            Frontend.ShowMessageBox(confirmed, MessageBoxImage.Warning);
            return;
        }
        if (App.State.Prop.LastSafeLaunchResult == SafeLaunchWorked)
        {
            string modsCause = Voidstrap.Utility.Platform.IsLinux
                ? "Roblox crashed while starting again. A safe launch in the last few hours worked once your mods, FastFlags and launch integrations were turned off, so one of those makes Roblox crash. Turn your mods off in My Mods first, then reset your FastFlags, then turn off AssetWarp, until Roblox starts."
                : "Roblox crashed while starting again. A safe launch in the last few hours worked once your mods, FastFlags and launch integrations were turned off, so one of those makes Roblox crash. Turn your mods off in My Mods first, then reset your FastFlags, until Roblox starts.";
            App.Logger.WriteLine(
                "Bootstrapper::WaitForLogFile",
                "Skipping another reinstall, the last safe launch worked so the cause is a Voidstrap addition"
            );
            Voidstrap.Utility.AppNotifications.RecordInfo("roblox:safelaunchworked", "Roblox started in a safe launch", modsCause);
            Frontend.ShowMessageBox(modsCause, MessageBoxImage.Warning);
            return;
        }
        string message =
            disabled.Count == 0
                ? "Roblox kept crashing while starting, so Voidstrap exhausted every retry. You can install a fresh copy of Roblox, open the startup log, or close this message."
                : "Roblox crashed while loading your mods, so Voidstrap turned these off:\n\n"
                    + string.Join("\n", disabled.Select(name => "  " + name))
                    + "\n\nYou can turn them back on in My Mods. You can also install a fresh copy of Roblox or open the startup log.";
        Voidstrap.Utility.AppNotifications.RecordInfo("roblox:startupcrashrepeated", "Roblox kept crashing while starting", message);
        MessageBoxResult result = Frontend.ShowMessageBox(
            message,
            MessageBoxImage.Warning,
            "Uninstall Roblox and Restart",
            "Open Log File",
            "Close"
        );
        if (result == MessageBoxResult.Yes)
        {
            App.State.Prop.SafeLaunchPending = true;
            App.State.Prop.LastSafeLaunchUtc = DateTime.UtcNow;
            App.State.Save();
            App.Settings.Prop.ForceRobloxReinstall = true;
            App.Settings.Prop.UpdateRoblox = true;
            App.Settings.Save();
            App.Logger.WriteLine(
                "Bootstrapper::WaitForLogFile",
                "A fresh Roblox installation was requested after repeated startup crashes"
            );
            if (!App.RestartApplication(App.LaunchSettings.Args))
                Frontend.ShowMessageBox("Voidstrap could not restart. Launch it again to reinstall Roblox.", MessageBoxImage.Error);
        }
        else if (result == MessageBoxResult.No)
        {
            OpenStartupCrashLog(robloxLogFile);
        }
    }

    private static void OpenStartupCrashLog(string? robloxLogFile)
    {
        App.Logger.Flush();
        string? logFile = !string.IsNullOrWhiteSpace(robloxLogFile) && File.Exists(robloxLogFile) ? robloxLogFile : App.Logger.FileLocation;
        if (string.IsNullOrWhiteSpace(logFile) || !File.Exists(logFile))
        {
            Frontend.ShowMessageBox("No startup log file is available.", MessageBoxImage.Warning);
            return;
        }
        if (!Utilities.OpenTextFile(logFile))
            Frontend.ShowMessageBox("The startup log could not be opened. It is saved at:\n" + logFile, MessageBoxImage.Warning);
    }

    private void TerminateFailedLaunchProcess()
    {
        Process? process = _robloxProcess;
        _robloxProcess = null;
        if (process == null)
            return;
        try
        {
            if (!process.HasExited && IsFromCurrentRobloxLaunch(process))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Terminated the unresponsive Roblox process from this launch attempt");
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Could not terminate the failed Roblox process: " + ex.Message);
        }
        finally
        {
            process.Dispose();
        }
    }
}
