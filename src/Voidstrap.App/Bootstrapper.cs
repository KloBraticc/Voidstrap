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
    internal enum MatchmakerRewriteOutcome
    {
        Rewritten,
        Unchanged,
        Cancelled,
        Failed,
    }

    internal readonly record struct MatchmakerDispatchTarget(string Target, MatchmakerRewriteOutcome Outcome, string? Error);

    private sealed class RobloxLogWaiter : IDisposable
    {
        private readonly FileSystemWatcher? _watcher;
        private readonly HashSet<string> _existingLogs;
        private readonly DateTime _launchStartedUtc;
        private readonly TaskCompletionSource<string> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        public Task<string> Task => _completion.Task;

        public RobloxLogWaiter(string directory, HashSet<string> existingLogs, DateTime launchStartedUtc)
        {
            _existingLogs = existingLogs;
            _launchStartedUtc = launchStartedUtc;
            try
            {
                _watcher = new FileSystemWatcher(directory, "*.log")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                };
                _watcher.Created += OnCreated;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("Bootstrapper::RobloxLogWaiter", "The Roblox log folder could not be watched: " + ex.Message);
            }
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            App.Logger.WriteLine("Bootstrapper::RobloxLogWaiter", "The Roblox log watch hit an error: " + e.GetException().Message);
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            TryComplete(e.FullPath);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            TryComplete(e.FullPath);
        }

        private void TryComplete(string path)
        {
            try
            {
                if (
                    !_existingLogs.Contains(path)
                    && File.Exists(path)
                    && File.GetLastWriteTimeUtc(path) >= _launchStartedUtc.AddSeconds(-3)
                )
                    _completion.TrySetResult(path);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnCreated;
                _watcher.Renamed -= OnRenamed;
                _watcher.Dispose();
            }
            GC.SuppressFinalize(this);
        }
    }

    private const string ProcRobloxPlayer = "RobloxPlayerBeta";

    private const string ProcRobloxCrash = "RobloxCrashHandler";

    private const string ProcRobloxExe = "RobloxPlayerBeta.exe";

    private const string SkyboxCommitApiUrl = "https://api.github.com/repos/KloBraticc/SkyboxPackV2/commits/main";

    private const string SkyboxRawBaseUrl = "https://raw.githubusercontent.com/KloBraticc/SkyboxPackV2/";

    private const string SkyboxPackCommitFile = ".commit";

    private const long SkyboxMaxFaceBytes = 16777216L;

    private const long SkyboxMinSegmentBytes = 524288L;

    private bool _modSkyPatchApplied;

    private static readonly string[] SkyboxFileNames =
    [
        "sky512_bk.tex",
        "sky512_dn.tex",
        "sky512_ft.tex",
        "sky512_lf.tex",
        "sky512_rt.tex",
        "sky512_up.tex",
    ];

    private const int MaxPackageDownloadAttempts = 5;

    private const string VersionOwnershipFileName = ".voidstrap-managed";

    private const string AppSettingsXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Settings>\r\n\t<ContentFolder>content</ContentFolder>\r\n\t<BaseUrl>http://www.roblox.com</BaseUrl>\r\n</Settings>\r\n";

    private static readonly int ModApplyConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private readonly FastZipEvents _fastZipEvents = new();

    private readonly CancellationTokenSource _cancelTokenSource = new();

    internal CancellationToken CancellationToken => _cancelTokenSource.Token;

    internal bool CancellationHandledByCaller { get; init; }

    private readonly IAppData AppData;

    private IDisposable? _installLock;

    private readonly LaunchMode _launchMode;
    private string _deploymentChannel;

    private string _launchCommandLine = App.LaunchSettings.RobloxLaunchArgs;

    private string _latestVersionGuid = null!;

    public bool InstallOnly { get; set; }

    public bool InstallationSucceeded { get; private set; }

    public bool ForceReinstall { get; init; }

    public bool SkipUpdateHold { get; init; }

    public bool IsCancelled => _cancelTokenSource.IsCancellationRequested;

    private bool ReinstallRequested => ForceReinstall || App.Settings.Prop.ForceRobloxReinstall;

    private bool UseVng => AppData is RobloxPlayerData { UseVng: true };

    public sealed class DownloadProgressInfo
    {
        public double Percent { get; set; }
        public long BytesDone { get; set; }
        public long TotalBytes { get; set; }
        public double SpeedBytesPerSec { get; set; }
        public int PackagesDone { get; set; }
        public int TotalPackages { get; set; }
        public string BinaryType { get; set; } = "";
    }

    public static event Action<DownloadProgressInfo>? DownloadProgressChanged;

    private string _latestVersionDirectory = null!;

    private PackageManifest _versionPackageManifest = null!;

    private int _isInstalling;

    private readonly TaskCompletionSource _installationStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _totalDownloadedBytes;

    private int _progressDisplayFailed;

    private double _progressIncrement;

    private double _taskbarProgressIncrement;

    private double _taskbarProgressMaximum;

    private Process? _robloxProcess;

    private AsyncMutex? _mutex;

    private int _appPid;

    private DateTime _robloxLaunchUtc;

    private bool _noConnection;

    private static readonly string PackFolder = Paths.SkyboxPack;

    private const long TargetSegmentBytes = 4L * 1024L * 1024L;

    private const int MaxSegmentCount = 64;

    private const int MirrorSpread = 3;

    private const int NetworkReadBufferCap = 1024 * 1024;

    private const int SlowSegmentGraceMs = 9000;

    private const int SlowSegmentWindowMs = 6000;

    private const long SlowSegmentMinimumBytes = 128L * 1024L;

    private static readonly HttpClient SkyboxHttpClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromMinutes(30L));

    private static readonly HttpClient RobloxPackageClient = Voidstrap.Utility.VpnHttpClient.Create(
        Timeout.InfiniteTimeSpan,
        handler =>
        {
            handler.AutomaticDecompression = DecompressionMethods.None;
            handler.MaxConnectionsPerServer = 128;
            handler.EnableMultipleHttp2Connections = true;
            handler.ConnectTimeout = TimeSpan.FromSeconds(10);
            handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60);
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
        }
    );

    private static readonly ArrayPool<byte> DownloadBufferPool = ArrayPool<byte>.Shared;

    private static readonly int ExtractionConcurrency = Math.Clamp(ModApplyConcurrency, 2, 8);

    private readonly SemaphoreSlim _networkRequestSlots;

    private readonly int _networkRequestLimit;

    private string? _packageExtractionDirectory;

    private static readonly Dictionary<string, string> SkyboxPatchFolderMap = new()
    {
        { "a564ec8aeef3614e788d02f0090089d8", "a5" },
        { "7328622d2d509b95dd4dd2c721d1ca8b", "73" },
        { "a50f6563c50ca4d5dcb255ee5cfab097", "a5" },
        { "6c94b9385e52d221f0538aadaceead2d", "6c" },
        { "9244e00ff9fd6cee0bb40a262bb35d31", "92" },
        { "78cb2e93aee0cdbd79b15a866bc93a54", "78" },
    };

    private const string SkyboxPatchSha256 = "5e710f0e8ff474aa78b2b472e371cb869268c3e033626dc6bca0d33bae704eeb";

    public IBootstrapperDialog? Dialog;

    private static readonly string? _launchStatusFile = Environment.GetEnvironmentVariable("VOIDSTRAP_STATUS_FILE");

    private readonly List<int> _integrationAutoclosePids = [];

    public bool IsStudioLaunch => _launchMode != LaunchMode.Player;

    private static readonly TimeSpan LateDeliveryHold = TimeSpan.FromDays(1);

    private string ApplyUpdateDelivery(string latest, bool forceManifest)
    {
        AppState state = AppData.State;
        string installed = state.VersionGuid;
        bool late = string.Equals(App.Settings.Prop.RobloxUpdateDelivery, "Late", StringComparison.OrdinalIgnoreCase);
        if (
            !late
            || SkipUpdateHold
            || forceManifest
            || string.IsNullOrEmpty(installed)
            || installed == latest
            || !IsValidVersionGuid(installed)
            || MustUpgrade
            || ReinstallRequested
        )
        {
            if (!string.IsNullOrEmpty(state.HeldVersionGuid))
            {
                state.HeldVersionGuid = string.Empty;
                state.HeldVersionFirstSeenUtc = default;
                App.State.Save();
            }
            return latest;
        }
        if (state.HeldVersionGuid != latest)
        {
            state.HeldVersionGuid = latest;
            state.HeldVersionFirstSeenUtc = DateTime.UtcNow;
            App.State.Save();
        }
        TimeSpan remaining = state.HeldVersionFirstSeenUtc + LateDeliveryHold - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero || remaining > LateDeliveryHold)
        {
            App.Logger.WriteLine("Bootstrapper::UpdateDelivery", "Late delivery hold is over, installing " + latest);
            return latest;
        }
        App.Logger.WriteLine(
            "Bootstrapper::UpdateDelivery",
            $"Late delivery is holding {latest} for about {Math.Ceiling(remaining.TotalHours)} more hours, launching the installed {installed}"
        );
        return installed;
    }

    private bool MustUpgrade
    {
        get
        {
            if (string.IsNullOrEmpty(AppData.State.VersionGuid))
            {
                return true;
            }
            if (!File.Exists(AppData.ExecutablePath))
            {
                return true;
            }
            return GetMissingCriticalFile() != null || CountMissingInstalledFiles() > 0;
        }
    }

    private string? GetMissingCriticalFile()
    {
        IReadOnlyList<string> required = AppData.State.CriticalFiles ?? AppData.CandidateCriticalFiles;
        if (required.Count == 0)
        {
            return null;
        }
        string directory = AppData.Directory;
        foreach (string name in required)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(InvalidCriticalFileChars) >= 0)
            {
                continue;
            }
            if (!File.Exists(Path.Combine(directory, name)))
            {
                App.Logger.WriteLine("Bootstrapper::MustUpgrade", "Critical file is missing, a repair is required: " + name);
                return name;
            }
        }
        return null;
    }

    private static readonly char[] InvalidCriticalFileChars = ['/', '\\', ':'];

    private List<string> ScanCriticalFiles(string directory)
    {
        List<string> found = [];
        foreach (string name in AppData.CandidateCriticalFiles)
        {
            if (File.Exists(Path.Combine(directory, name)))
            {
                found.Add(name);
            }
        }
        return found;
    }

    public Bootstrapper(LaunchMode launchMode, bool? useVng = null)
    {
        if (DownloadConfiguration.Normalize(App.Settings.Prop))
            App.Settings.SaveDeferred();
        int requestLimit = DownloadConfiguration.ResolveSegmentRequestLimit(App.Settings.Prop);
        _networkRequestSlots = new SemaphoreSlim(requestLimit, requestLimit);
        _networkRequestLimit = requestLimit;
        _launchMode = launchMode;
        _deploymentChannel = App.Settings.Prop.Channel;
        _fastZipEvents.FileFailure = OnExtractionFailure;
        _fastZipEvents.DirectoryFailure = OnExtractionFailure;
        _fastZipEvents.ProcessFile = OnExtractionFile;
        AppData = IsStudioLaunch ? new RobloxStudioData() : new RobloxPlayerData(useVng);
        if (UseVng)
            _deploymentChannel = "production";
    }

    private static void OnExtractionFailure(object _, ScanFailureEventArgs e)
    {
        throw e.Exception;
    }

    private void OnExtractionFile(object _, ScanEventArgs e)
    {
        e.ContinueRunning = !_cancelTokenSource.IsCancellationRequested;
    }

    private void InvokeOnDialog(Action action)
    {
        if (Dialog == null)
        {
            return;
        }
        try
        {
            if (Voidstrap.Utility.Platform.IsWindows && Dialog is Control control)
            {
#if !CROSSPLAT
                if (control.IsDisposed || !control.IsHandleCreated)
                {
                    return;
                }
#endif
                if (control.InvokeRequired)
                {
                    control.Invoke(action);
                }
                else
                {
                    action();
                }
                return;
            }
            IBootstrapperDialog? dialog = Dialog;
            DependencyObject? val = (DependencyObject?)((dialog is DependencyObject) ? dialog : null);
            if (val != null)
            {
                if (!((DispatcherObject)val).Dispatcher.CheckAccess())
                {
                    ((DispatcherObject)val).Dispatcher.Invoke(action);
                }
                else
                {
                    action();
                }
            }
            else
            {
                action();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or TaskCanceledException)
        {
            App.Logger.WriteLine(
                "Bootstrapper::InvokeOnDialog",
                "The launch dialog closed before a status update was shown: " + ex.Message
            );
        }
    }

    private void SetStatus(string message)
    {
        if (!string.IsNullOrEmpty(message) && message.Contains("{product}"))
        {
            message = message.Replace("{product}", "Voidstrap");
        }
        if (App.LaunchSettings.MatchmakerRejoinFlag.Active)
        {
            _ = int.TryParse(App.LaunchSettings.MatchmakerAttemptFlag.Data, out int result);
            if (result <= 0)
            {
                result = 1;
            }
            string? data = App.LaunchSettings.MatchmakerTargetFlag.Data;
            string text = ((result > 1) ? $" (try #{result})" : "");
            if (!string.IsNullOrWhiteSpace(data))
            {
                message = $"Searching for {data}{text} {message}";
            }
            else
            {
                message = "Searching for closest server" + text + " " + message;
            }
        }
        InvokeOnDialog(
            delegate
            {
                Dialog!.Message = message;
            }
        );
        PublishLaunchStatus(message);
    }

    private static void PublishLaunchStatus(string message)
    {
        if (string.IsNullOrEmpty(_launchStatusFile))
        {
            return;
        }
        try
        {
            File.WriteAllText(_launchStatusFile, message);
        }
        catch { }
    }

    private void SetProgressMaximum(int max)
    {
        InvokeOnDialog(
            delegate
            {
                Dialog!.ProgressMaximum = max;
            }
        );
    }

    private void SetProgressIndeterminate(bool indeterminate)
    {
#if !CROSSPLAT
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
#endif
        InvokeOnDialog(
            delegate
            {
                Dialog!.ProgressStyle = indeterminate ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            }
        );
    }

    private void UpdateProgressBar()
    {
        if (Dialog == null)
        {
            return;
        }
        try
        {
            InvokeOnDialog(
                delegate
                {
                    long num = Interlocked.Read(in _totalDownloadedBytes);
                    int progressValue = (int)Math.Clamp(Math.Floor(_progressIncrement * (double)num), 0.0, 10000.0);
                    Dialog.ProgressValue = progressValue;
                    double taskbarProgressValue = Math.Clamp(_taskbarProgressIncrement * (double)num, 0.0, 100.0);
                    Dialog.TaskbarProgressValue = taskbarProgressValue;
                }
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (Interlocked.Exchange(ref _progressDisplayFailed, 1) == 0)
            {
                App.Logger.WriteException("Bootstrapper::UpdateProgressBar", ex);
            }
        }
    }

    private async Task RunDownloadProgressLoopAsync(long totalPacked, int totalPackages, Func<int> completedGetter, CancellationToken ct)
    {
        double emaSpeed = 0.0;
        long lastBytes = Interlocked.Read(in _totalDownloadedBytes);
        Stopwatch sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, ct).ConfigureAwait(continueOnCapturedContext: false);
            }
            catch
            {
                break;
            }
            long num = Interlocked.Read(in _totalDownloadedBytes);
            double num2 = sw.Elapsed.TotalSeconds;
            if (num2 < 0.05)
            {
                num2 = 0.05;
            }
            sw.Restart();
            double num3 = Math.Max(0.0, (double)(num - lastBytes) / num2);
            lastBytes = num;
            emaSpeed = ((emaSpeed <= 0.0) ? num3 : (0.25 * num3 + 0.75 * emaSpeed));
            double value = ((totalPacked > 0) ? Math.Clamp((double)num / (double)totalPacked * 100.0, 0.0, 100.0) : 0.0);
            long num4 = Math.Max(0L, totalPacked - num);
            string value2 = ((emaSpeed > 4096.0) ? FormatEta((double)num4 / emaSpeed) : "calculating");
            int value3 = completedGetter();
            SetStatus(
                $"Downloading {value:0}%\n{FormatBytes(num)} of {FormatBytes(totalPacked)}, {FormatSpeed(emaSpeed)}, ETA {value2}, {value3}/{totalPackages}"
            );
            UpdateProgressBar();
            try
            {
                DownloadProgressChanged?.Invoke(
                    new DownloadProgressInfo
                    {
                        Percent = value,
                        BytesDone = num,
                        TotalBytes = totalPacked,
                        SpeedBytesPerSec = emaSpeed,
                        PackagesDone = value3,
                        TotalPackages = totalPackages,
                        BinaryType = AppData.BinaryType,
                    }
                );
            }
            catch { }
        }
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        if (!(bytesPerSecond >= 1048576.0))
        {
            if (!(bytesPerSecond >= 1024.0))
            {
                return $"{Math.Max(0.0, bytesPerSecond):0} B/s";
            }
            return $"{bytesPerSecond / 1024.0:0} KB/s";
        }
        return $"{bytesPerSecond / 1048576.0:0.0} MB/s";
    }

    private static string FormatEta(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
        {
            return "...";
        }
        TimeSpan timeSpan = TimeSpan.FromSeconds(Math.Min(seconds, 86399.0));
        if (timeSpan.Hours <= 0)
        {
            return timeSpan.ToString("m\\:ss");
        }
        return timeSpan.ToString("h\\:mm\\:ss");
    }

    private void ApplyForcedReinstall()
    {
        if (!ReinstallRequested)
        {
            return;
        }
        App.Logger.WriteLine("Bootstrapper::ApplyForcedReinstall", "Reinstall was requested for " + AppData.ProductName);
    }

    private static void ClearForcedReinstall()
    {
        if (!App.Settings.Prop.ForceRobloxReinstall)
            return;

        App.Settings.Prop.ForceRobloxReinstall = false;
        App.Settings.Save();
        App.Logger.WriteLine("Bootstrapper::ClearForcedReinstall", "Reinstall finished, the setting has been turned back off");
    }

    public async Task Run()
    {
        if (UseVng && !Frontend.ConfirmVng())
        {
            Dialog?.CloseBootstrapper();
            return;
        }
        Stopwatch launchTimer = Stopwatch.StartNew();
        App.Logger.WriteLine("Bootstrapper::Run", "Running bootstrapper");
        _safeMode = _launchMode == LaunchMode.Player && App.State.Prop.SafeLaunchPending;
        if (_safeMode)
        {
            App.Logger.WriteLine(
                "Bootstrapper::Run",
                "Safe launch: this launch skips mods, FastFlags, the matchmaker, launch integrations and process changes to test whether one of them makes Roblox crash"
            );
        }
        Dialog?.CancelEnabled = true;
        SetStatus(Strings.Bootstrapper_Status_Connecting);
        ApplyForcedReinstall();
        bool mutexExists = false;
        try
        {
            if (Mutex.TryOpenExisting("Voidstrap-Bootstrapper", out Mutex? existing))
            {
                using (existing)
                {
                    App.Logger.WriteLine("Bootstrapper::Run", "Voidstrap-Bootstrapper mutex exists, waiting...");
                    SetStatus(Strings.Bootstrapper_Status_WaitingOtherInstances);
                    mutexExists = true;
                }
            }
        }
        catch (Exception value)
        {
            App.Logger.WriteLine("Bootstrapper::Run", $"Unexpected error checking mutex: {value}");
        }
        await using AsyncMutex mutex = new(initiallyOwned: false, "Voidstrap-Bootstrapper");
        await mutex.AcquireAsync(_cancelTokenSource.Token);
        _mutex = mutex;
        if (mutexExists)
        {
            App.Settings.Load();
            App.State.Load();
        }
        if (!string.IsNullOrEmpty(AppData.State.VersionGuid) && !IsValidVersionGuid(AppData.State.VersionGuid))
        {
            App.Logger.WriteLine("Bootstrapper::Run", "Ignoring an invalid installed version identifier");
            AppData.State.VersionGuid = string.Empty;
            App.State.Save();
        }
        using IDisposable? installLock = await PrepareCompressedInstallAsync();
        _installLock = installLock;
        if (
            Voidstrap.Utility.Platform.SupportsWindowsClient
            && !await Voidstrap.Utility.Connectivity.IsOnlineAsync(_cancelTokenSource.Token)
        )
        {
            if (MustUpgrade)
            {
                App.Logger.WriteLine(
                    "Bootstrapper::Run",
                    "The Roblox connectivity probe failed but Roblox is not installed, trying the install anyway"
                );
            }
            else
            {
                _noConnection = true;
                App.Logger.WriteLine("Bootstrapper::Run", "Offline, skipping update checks and launching the installed Roblox");
                SetStatus("You are offline, launching the installed Roblox");
            }
        }
        Task versionInfoTask =
            Voidstrap.Utility.Platform.SupportsWindowsClient && !_noConnection ? GetLatestVersionInfo(false) : Task.CompletedTask;
        Task<bool> launcherUpdateTask = _noConnection ? Task.FromResult(false) : TryUpdateLauncherAsync();
        if (await launcherUpdateTask)
        {
            Observe(versionInfoTask);
            return;
        }
        if (!Voidstrap.Utility.Platform.SupportsWindowsClient)
        {
            Observe(versionInfoTask);
            if (_launchMode == LaunchMode.Player)
            {
                await PrepareLinuxLaunchAsync(_cancelTokenSource.Token);
            }
            await mutex.ReleaseAsync();
            if (!App.LaunchSettings.NoLaunchFlag.Active && !InstallOnly && !_cancelTokenSource.IsCancellationRequested)
            {
                await StartRoblox(_cancelTokenSource.Token);
            }
            Dialog?.CloseBootstrapper();
            InstallationSucceeded = InstallOnly && !_cancelTokenSource.IsCancellationRequested;
            return;
        }
        if (!_noConnection)
        {
            Exception? versionError = await FetchVersionInfoAsync(versionInfoTask);
            if (_cancelTokenSource.IsCancellationRequested)
            {
                return;
            }
            if (versionError != null)
            {
                await HandleConnectionError(versionError);
            }
        }
        else
        {
            Observe(versionInfoTask);
        }
        App.Logger.WriteLine("Bootstrapper::Run", "Version and launcher checks completed in " + launchTimer.ElapsedMilliseconds + " ms");
        if (!_noConnection)
        {
            bool upgradeRequired = AppData.State.VersionGuid != _latestVersionGuid || MustUpgrade || ReinstallRequested;
            if (upgradeRequired)
            {
                Exception? connectivityError = await Deployment.InitializeConnectivity();
                App.Logger.WriteLine("Bootstrapper::Run", "Package connectivity completed in " + launchTimer.ElapsedMilliseconds + " ms");
                if (connectivityError != null)
                    await HandleConnectionError(connectivityError);
                if (!_noConnection)
                {
                    try
                    {
                        await GetLatestVersionInfo();
                        await UpgradeRoblox();
                    }
                    catch (Exception upgradeError)
                    {
                        await HandleConnectionError(upgradeError);
                    }
                }
            }
            if (_cancelTokenSource.IsCancellationRequested)
            {
                return;
            }
            if (_launchMode == LaunchMode.Player)
            {
                App.FastFlags.MigratePlayerLoggingPreset();
                App.FastFlags.RestoreAssetWarpPreloadFlags();
                App.FastFlags.SaveDeferred();
            }
            if (_safeMode)
            {
                RobloxLocalReset.MakeCacheWritable();
                if (App.State.Prop.CacheResetPending)
                {
                    App.State.Prop.LastCacheBackup = RobloxLocalReset.MoveCacheAside() ?? string.Empty;
                    App.State.Save();
                }
            }
            else if (!_noConnection)
                await ApplyModifications();
        }
        if (IsStudioLaunch)
        {
            WindowsRegistry.RegisterStudio();
        }
        else
        {
            WindowsRegistry.RegisterPlayer();
        }
        await mutex.ReleaseAsync();
        if (!App.LaunchSettings.NoLaunchFlag.Active && !InstallOnly && !_cancelTokenSource.IsCancellationRequested)
        {
            App.Logger.WriteLine("Bootstrapper::Run", "Prelaunch preparation completed in " + launchTimer.ElapsedMilliseconds + " ms");
            await StartRoblox(_cancelTokenSource.Token);
        }
        installLock?.Dispose();
        Dialog?.CloseBootstrapper();
        if (InstallOnly)
        {
            InstallationSucceeded =
                !_cancelTokenSource.IsCancellationRequested
                && !_noConnection
                && !MustUpgrade
                && AppData.State.VersionGuid == _latestVersionGuid;
        }
    }

    private async Task<IDisposable?> PrepareCompressedInstallAsync()
    {
        if (!RobloxInstallCompression.Supported)
        {
            return null;
        }
        IDisposable? installLock = await RobloxInstallCompression
            .AcquireLockAsync(TimeSpan.FromMinutes(2), _cancelTokenSource.Token)
            .ConfigureAwait(false);
        try
        {
            await RobloxInstallCompression.EnsureExtractedAsync(AppData, SetStatus, true, _cancelTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            installLock?.Dispose();
            throw;
        }
        catch (InsufficientSpaceException ex)
        {
            App.Logger.WriteLine("Bootstrapper::PrepareCompressedInstall", ex.Message);
            Frontend.ShowMessageBox(ex.Message + ". Free up some space, then launch again.", MessageBoxImage.Exclamation);
        }
        catch (Exception ex)
        {
            string archive = RobloxInstallCompression.ArchivePathFor(AppData.Directory);
            App.Logger.WriteLine(
                "Bootstrapper::PrepareCompressedInstall",
                "The compressed install could not be unpacked and will be reinstalled: " + ex.Message
            );
            try
            {
                if (File.Exists(archive))
                {
                    File.Delete(archive);
                }
            }
            catch (Exception deleteError) when (deleteError is IOException or UnauthorizedAccessException)
            {
                App.Logger.WriteLine(
                    "Bootstrapper::PrepareCompressedInstall",
                    "Could not remove the unreadable archive: " + deleteError.Message
                );
            }
        }
        return installLock;
    }

    private static string ReadNewLogText(string path, ref long position)
    {
        try
        {
            using FileStream fileStream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (position > fileStream.Length)
            {
                position = 0L;
            }
            fileStream.Seek(position, SeekOrigin.Begin);
            using StreamReader streamReader = new(fileStream);
            string result = streamReader.ReadToEnd();
            position = fileStream.Length;
            return result;
        }
        catch
        {
            return "";
        }
    }

    internal async Task<bool> TryUpdateLauncherAsync()
    {
        bool updateCheckFresh = DateTime.UtcNow - App.State.Prop.LastLauncherUpdateCheckUtc < TimeSpan.FromMinutes(2);
        if (!App.Settings.Prop.CheckForUpdates || App.LaunchSettings.UpgradeFlag.Active || InstallOnly || updateCheckFresh)
            return false;
        try
        {
            return await Voidstrap.Utility.Connectivity.IsOnlineAsync(_cancelTokenSource.Token)
                && await CheckAndApplyUpdate("Bootstrapper::TryUpdateLauncher");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Logger.WriteLine("Bootstrapper::TryUpdateLauncher", "The launcher update check failed: " + ex.Message);
            return false;
        }
    }

    private async Task<bool> CheckAndApplyUpdate(string logIdent)
    {
        string? text = await GithubUpdater.GetLatestVersionTagAsync();
        App.State.Prop.LastLauncherUpdateCheckUtc = DateTime.UtcNow;
        App.State.Save();
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        string text2 = text.TrimStart(['v', 'V']);
        string text3 = (App.Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");
        App.Logger.WriteLine(logIdent, "Local: " + text3 + " | Remote: " + text2);
        if (IsNewerVersion(text2))
        {
            if (
                Voidstrap.Utility.Platform.IsLinux
                && string.Equals(App.State.Prop.DeclinedLinuxUpdateTag, text, StringComparison.OrdinalIgnoreCase)
            )
            {
                App.Logger.WriteLine(
                    logIdent,
                    "Skipping " + text + " because its password prompt was cancelled, Check for updates still installs it"
                );
                return false;
            }
            if (
                Voidstrap.Utility.Platform.IsLinux
                && string.Equals(App.State.Prop.StagedLinuxUpdateTag, text, StringComparison.OrdinalIgnoreCase)
            )
            {
                App.Logger.WriteLine(
                    logIdent,
                    "Skipping " + text + " because it is already staged, restart the computer to finish installing it"
                );
                return false;
            }
            SetStatus("Updating to v" + text2 + "...");
            if (await GithubUpdater.DownloadAndInstallUpdate(text))
            {
                App.Logger.WriteLine(logIdent, "Update installed restarting Voidstrap...");
                _cancelTokenSource.Cancel();
                App.RestartApplication(App.LaunchSettings.Args);
                return true;
            }
            else
            {
                App.Logger.WriteLine(logIdent, "Update failed continuing without updating.");
            }
        }
        return false;
    }

    private static bool IsNewerVersion(string remoteTag)
    {
        if (!App.Settings.Prop.CheckForUpdates)
        {
            return false;
        }
        string text = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        remoteTag = remoteTag.TrimStart(['v', 'V']);
        if (Version.TryParse(text, out Version? result) && Version.TryParse(remoteTag, out Version? result2))
        {
            return result2 > result;
        }
        return string.Compare(remoteTag, text, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static bool IsValidVersionGuid(string? versionGuid)
    {
        return Voidstrap.AppData.CommonAppData.IsVersionGuidValid(versionGuid);
    }

    private const int ConnectAttempts = 3;

    private static readonly TimeSpan ConnectDeadline = TimeSpan.FromSeconds(40);

    private static void Observe(Task task)
    {
        if (task == null || task.IsCompletedSuccessfully)
            return;
        _ = task.ContinueWith(
            static finished => _ = finished.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default
        );
    }

    private static bool IsRetryableConnectionError(Exception? error)
    {
        if (error is HttpRequestException { StatusCode: { } status })
            return (int)status >= 500 || status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests;
        return error is HttpRequestException or IOException or TaskCanceledException or TimeoutException or SocketException;
    }

    private async Task<Exception?> FetchVersionInfoAsync(Task started)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancelTokenSource.Token);
        deadline.CancelAfter(ConnectDeadline);
        Exception? lastError = null;
        try
        {
            for (int attempt = 1; attempt <= ConnectAttempts; attempt++)
            {
                if (_cancelTokenSource.IsCancellationRequested)
                {
                    return null;
                }
                try
                {
                    await (attempt == 1 ? started.WaitAsync(deadline.Token) : GetLatestVersionInfo(false, false, deadline.Token));
                    return null;
                }
                catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception error)
                {
                    lastError = UnwrapException(error);
                    App.Logger.WriteLine("Bootstrapper::FetchVersionInfo", $"Deploy info attempt {attempt} failed: {lastError?.Message}");
                    if (deadline.IsCancellationRequested || !IsRetryableConnectionError(lastError) || attempt == ConnectAttempts)
                    {
                        break;
                    }
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), deadline.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            Observe(started);
        }
        if (_cancelTokenSource.IsCancellationRequested)
        {
            return null;
        }
        if (deadline.IsCancellationRequested)
        {
            return new TimeoutException(
                "Roblox did not answer within " + ConnectDeadline.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds."
            );
        }
        return lastError;
    }

    private async Task HandleConnectionError(Exception? exception)
    {
        if (exception == null || _cancelTokenSource.IsCancellationRequested)
        {
            return;
        }
        exception = UnwrapException(exception);
        if (exception is OperationCanceledException)
        {
            return;
        }
        App.Logger.WriteException("Bootstrapper::HandleConnectionError", exception!);
        if (Interlocked.Read(in _totalDownloadedBytes) > 0 && Volatile.Read(in _isInstalling) == 1)
        {
            App.Logger.WriteLine("Bootstrapper::HandleConnectionError", "Already upgrading, skipping retry.");
            return;
        }
        if (exception is HttpRequestException { StatusCode: { } statusCode })
        {
            switch (statusCode)
            {
                case HttpStatusCode.Unauthorized:
                case HttpStatusCode.Forbidden:
                case HttpStatusCode.NotFound:
                    if (!string.Equals(_deploymentChannel, "production", StringComparison.OrdinalIgnoreCase))
                    {
                        App.Logger.WriteLine(
                            "Bootstrapper::HandleConnectionError",
                            $"HTTP {(int)statusCode} ({statusCode}), switching to default channel '{"production"}'."
                        );
                        _deploymentChannel = "production";
                        App.Settings.Prop.Channel = "production";
                        App.Settings.Save();
                        return;
                    }
                    break;
            }
        }
        _noConnection = true;
        if (!MustUpgrade)
        {
            App.Logger.WriteLine(
                "Bootstrapper::HandleConnectionError",
                "Network/server issue, but Roblox is already installed, launching the existing version silently."
            );
            return;
        }
        string message = exception switch
        {
            TimeoutException => "Roblox did not respond in time. Check your connection, then try launching again.",
            HttpRequestException { StatusCode: { } statusCode2 } when statusCode2 >= HttpStatusCode.InternalServerError =>
                $"Roblox's servers returned HTTP {(int)statusCode2}. Please try launching again in a few minutes.",
            HttpRequestException { StatusCode: { } statusCode3 } =>
                $"Roblox returned HTTP {(int)statusCode3} for the deployment endpoint. Your channel may be unavailable, try switching it in Settings.",
            _ => "A network or server issue occurred. Try switching your channel in Settings or relaunching.",
        };
        Frontend.ShowMessageBox(message, MessageBoxImage.Exclamation);
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(ex))]
    private static Exception? UnwrapException(Exception? ex)
    {
        for (int i = 0; i < 8; i++)
        {
            if (!(ex is AggregateException { InnerException: not null } ex2))
            {
                break;
            }
            ex = ex2.InnerException;
        }
        return ex;
    }

    public void Cancel()
    {
        if (_cancelTokenSource.IsCancellationRequested)
        {
            return;
        }
        App.Logger.WriteLine("Bootstrapper::Cancel", "Cancelling launch...");
        _cancelTokenSource.Cancel();
        Dialog?.CancelEnabled = false;
        if (CancellationHandledByCaller)
        {
            return;
        }
        if (Volatile.Read(in _isInstalling) == 1)
        {
            _ = FinishInstallCancellationAsync();
            return;
        }
        if (_appPid != 0)
        {
            try
            {
                using Process process = Process.GetProcessById(_appPid);
                process.Kill();
            }
            catch { }
        }
        CompleteCancellation();
    }

    private async Task FinishInstallCancellationAsync()
    {
        await _installationStopped.Task.ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            if (!string.IsNullOrWhiteSpace(_latestVersionDirectory))
            {
                string stagingDirectory = _latestVersionDirectory + ".installing";
                if (Directory.Exists(stagingDirectory))
                {
                    await SafeDeleteDirectoryAsync(stagingDirectory, "Bootstrapper::Cancel", CancellationToken.None)
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::Cancel", "Could not fully clean up installation!");
            App.Logger.WriteException("Bootstrapper::Cancel", ex);
        }
        CompleteCancellation();
    }

    private void CompleteCancellation()
    {
        Dialog?.CloseBootstrapper();
        if (!InstallOnly)
        {
            App.SoftTerminate(ErrorCode.ERROR_CANCELLED);
        }
    }

    private async Task GetLatestVersionInfo(
        bool fetchManifest = true,
        bool forceManifest = false,
        CancellationToken cancellationToken = default
    )
    {
        CancellationToken ct = cancellationToken.CanBeCanceled ? cancellationToken : _cancelTokenSource.Token;
        ClientVersion clientVersion = await Deployment
            .GetInfo(
                _deploymentChannel,
                cancellationToken: ct,
                binaryType: AppData.BinaryType,
                resolvedChannel: channel => _deploymentChannel = channel
            )
            .ConfigureAwait(continueOnCapturedContext: false);
        if (string.IsNullOrWhiteSpace(clientVersion?.VersionGuid))
        {
            throw new Exception("VersionGuid missing from clientVersion response.");
        }
        if (!IsValidVersionGuid(clientVersion.VersionGuid))
        {
            throw new InvalidDataException("VersionGuid has an invalid format");
        }
        _latestVersionGuid = ApplyUpdateDelivery(clientVersion.VersionGuid, forceManifest);
        string installFolderName =
            App.Settings.Prop.StaticDirectory && !string.IsNullOrEmpty(AppData.BinaryType) ? AppData.BinaryType : _latestVersionGuid;
        _latestVersionDirectory = Path.GetFullPath(Path.Combine(AppData.VersionsRoot, installFolderName));
        string versionsRoot =
            Path.GetFullPath(AppData.VersionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!_latestVersionDirectory.StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("VersionGuid resolves outside the versions directory");
        }
        if (!forceManifest && AppData.State.VersionGuid == _latestVersionGuid && !MustUpgrade && !ReinstallRequested)
        {
            App.Logger.WriteLine("Bootstrapper::GetLatestVersionInfo", "Already up to date, skipping package manifest fetch.");
            _versionPackageManifest = [];
            return;
        }
        if (!fetchManifest)
        {
            _versionPackageManifest = [];
            return;
        }
        IReadOnlyList<string> manifestUrls = Deployment.GetLocations("/" + _latestVersionGuid + "-rbxPkgManifest.txt", UseVng);
        string? manifestBody = null;
        foreach (string manifestUrl in manifestUrls)
        {
            App.Logger.WriteLine("Bootstrapper::GetLatestVersionInfo", "Fetching manifest: " + manifestUrl);
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, manifestUrl)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                using HttpResponseMessage manifestResp = await SendPackageRequestAsync(request, ct)
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (!manifestResp.IsSuccessStatusCode)
                {
                    Deployment.ReportMirrorFailure(manifestUrl, $"manifest HTTP {(int)manifestResp.StatusCode}");
                    App.Logger.WriteLine(
                        "Bootstrapper::GetLatestVersionInfo",
                        $"Manifest HTTP {(int)manifestResp.StatusCode} ({manifestResp.StatusCode}) from this mirror, trying next."
                    );
                    continue;
                }
                string body = await ReadTextBoundedAsync(manifestResp.Content, 4194304, ct)
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith('<'))
                {
                    Deployment.ReportMirrorFailure(manifestUrl, "manifest returned HTML or empty content");
                    App.Logger.WriteLine(
                        "Bootstrapper::GetLatestVersionInfo",
                        "Manifest returned HTML or empty content, trying next mirror."
                    );
                    continue;
                }
                manifestBody = body;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex2) when (ex2 is HttpRequestException or IOException or TaskCanceledException)
            {
                App.Logger.WriteLine("Bootstrapper::GetLatestVersionInfo", "Manifest fetch failed: " + ex2.Message);
            }
        }
        if (manifestBody == null)
        {
            throw new HttpRequestException("Package manifest is unavailable from every deployment location.");
        }
        _versionPackageManifest = new PackageManifest(manifestBody);
        App.Logger.WriteLine("Bootstrapper::GetLatestVersionInfo", $"Manifest: {_versionPackageManifest.Count} entries.");
    }

    internal static async Task<RuntimeInstallation> EnsureSoberInstalledAsync(
        IRobloxRuntimeProvider provider,
        RuntimeInstallation installation,
        IPlatformHost host,
        Action<string>? report,
        CancellationToken cancellationToken
    )
    {
        const string logIdent = "Bootstrapper::EnsureSoberInstalled";
        if (installation.Capability.IsAvailable || !LinuxSoberInstaller.CanInstall(installation.Capability))
        {
            return installation;
        }

        App.Logger.WriteLine(logIdent, "Preparing Sober installation");
        try
        {
            string stage = "Installing Sober";
            Stopwatch elapsed = Stopwatch.StartNew();
            string describe()
            {
                string time =
                    (int)elapsed.Elapsed.TotalMinutes + ":" + elapsed.Elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
                return stage.Contains('%') ? stage + " (" + time + ")" : stage + ", this can take a few minutes (" + time + ")";
            }
            Task<OperationResult> install = new LinuxSoberInstaller(host.Processes).InstallAsync(
                cancellationToken,
                message =>
                {
                    stage = message;
                    report?.Invoke(describe());
                }
            );
            while (!install.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report?.Invoke(describe());
                await Task.WhenAny(install, Task.Delay(1000, cancellationToken));
            }

            OperationResult installed = await install;
            if (!installed.Succeeded)
            {
                App.Logger.WriteLine(logIdent, installed.Failure?.Message ?? "Sober could not be installed");
                return installation with
                {
                    Capability = installation.Capability with { Reason = installed.Failure?.Message ?? "Sober could not be installed" },
                };
            }

            App.Logger.WriteLine(logIdent, "Sober installed");
            report?.Invoke("Starting Roblox");
            return await provider.FindInstallationAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Sober could not be installed: " + ex.Message);
            return installation with
            {
                Capability = installation.Capability with { Reason = "Sober could not be installed: " + ex.Message },
            };
        }
    }

    internal static async Task<RuntimeInstallation> EnsureVinegarInstalledAsync(
        IRobloxRuntimeProvider provider,
        RuntimeInstallation installation,
        IPlatformHost host,
        Action<string>? report,
        CancellationToken cancellationToken
    )
    {
        const string logIdent = "Bootstrapper::EnsureVinegarInstalled";
        if (installation.Capability.IsAvailable || !LinuxVinegarInstaller.CanInstall(installation.Capability))
        {
            return installation;
        }

        App.Logger.WriteLine(logIdent, "Vinegar is not installed, installing it from Flathub");
        report?.Invoke("Installing Vinegar, this can take a while");
        try
        {
            OperationResult installed = await new LinuxVinegarInstaller(host.Processes).InstallAsync(cancellationToken);
            if (!installed.Succeeded)
            {
                App.Logger.WriteLine(logIdent, installed.Failure?.Message ?? "Vinegar could not be installed");
                return installation;
            }

            App.Logger.WriteLine(logIdent, "Vinegar installed");
            report?.Invoke("Starting Roblox Studio");
            return await provider.FindInstallationAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Vinegar could not be installed: " + ex.Message);
            return installation;
        }
    }

    private async Task<bool> TryLaunchNonWindowsClientAsync(
        string launchCommandLine,
        LaunchMode launchMode,
        CancellationToken cancellationToken
    )
    {
        try
        {
            RuntimeKind runtimeKind = launchMode == LaunchMode.Player ? RuntimeKind.Player : RuntimeKind.Studio;
            string launchTarget = launchCommandLine;
            if (!RobloxDeeplink.TryExtract(launchTarget, out _))
            {
                launchTarget = runtimeKind == RuntimeKind.Player ? "roblox://experiences/start" : "roblox-studio://launch";
            }

            IPlatformHost? host = Voidstrap.Utility.Platform.RuntimeHost;
            if (host == null)
            {
                return false;
            }

            if (OperatingSystem.IsLinux())
            {
                IRobloxRuntimeProvider provider = runtimeKind == RuntimeKind.Player ? host.PlayerRuntime : host.StudioRuntime;
                RuntimeInstallation installation = await provider.FindInstallationAsync(cancellationToken);
                if (runtimeKind == RuntimeKind.Player)
                {
                    installation = await EnsureSoberInstalledAsync(provider, installation, host, SetStatus, cancellationToken);
                }
                else
                {
                    installation = await EnsureVinegarInstalledAsync(provider, installation, host, SetStatus, cancellationToken);
                }
                if (!installation.Capability.IsAvailable)
                {
                    App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", installation.Capability.Reason);
                    Frontend.ShowMessageBox(installation.Capability.Reason, MessageBoxImage.Hand);
                    return false;
                }

                LinuxSoberRuntimeProvider.ForceX11Session =
                    Voidstrap.Integrations.Overlays.OverlaySettings.RequiresLinuxX11Session
                    && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY"));
                App.Logger.WriteLine(
                    "Bootstrapper::TryLaunchNonWindowsClient",
                    Voidstrap.Integrations.Overlays.OverlaySettings.DescribeLinuxSoberSession(LinuxSoberRuntimeProvider.ForceX11Session)
                );

                LinuxSoberRuntimeProvider.LauncherCommand = await Voidstrap.Utility.SoberLauncherCommand.ResolveAsync(
                    App.Settings.Prop.SoberLauncher,
                    cancellationToken
                );
                LinuxRuntimeConfiguration configuration = LinuxRuntimeConfiguration.CreateDefault(Paths.Mods, host.Processes);
                OperationResult prepared = await configuration.PrepareAsync(
                    installation,
                    Voidstrap.Utility.SoberConfigurationMapper.CreatePlayerOptions(App.Settings.Prop),
                    Voidstrap.Utility.VinegarConfigurationMapper.CreateStudioOptions(App.Settings.Prop),
                    cancellationToken
                );
                if (!prepared.Succeeded)
                {
                    string message = prepared.Failure?.Message ?? "Linux runtime preparation failed.";
                    App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", message);
                    Frontend.ShowMessageBox(message, MessageBoxImage.Hand);
                    return false;
                }

                if (configuration.SkippedAssets.Count > 0)
                {
                    App.Logger.WriteLine(
                        "Bootstrapper::TryLaunchNonWindowsClient",
                        configuration.SkippedAssets.Count
                            + " mod files have no matching asset in the installed Sober Roblox package and were not applied: "
                            + string.Join(", ", configuration.SkippedAssets.Take(20))
                    );
                }
                if (configuration.ConflictingAssets.Count > 0)
                {
                    App.Logger.WriteLine(
                        "Bootstrapper::TryLaunchNonWindowsClient",
                        configuration.ConflictingAssets.Count
                            + " mod files were skipped because an overlay path already exists: "
                            + string.Join(", ", configuration.ConflictingAssets.Take(20))
                    );
                }
            }

            SettingsDocument? settings = null;
            OperationResult<SettingsLoadResult> settingsResult = await new PortableSettingsStore(host.Paths).LoadAsync(cancellationToken);
            if (settingsResult.Succeeded)
            {
                settings = settingsResult.Value?.Document;
            }

            await LaunchCustomIntegrations("Bootstrapper::TryLaunchNonWindowsClient", preLaunch: true, cancellationToken);

            using DeploymentSessionStateMachine session = new(
                new RuntimeLaunchCoordinator(host.PlayerRuntime, host.StudioRuntime),
                host.ResourceOptimization
            );
            OperationResult<DeploymentLaunchResult> result = await session.LaunchAsync(
                runtimeKind,
                launchTarget,
                settings,
                cancellationToken
            );
            if (result.Succeeded && result.Value is not null)
            {
                App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", result.Value.Summary);
                await LaunchCustomIntegrations("Bootstrapper::TryLaunchNonWindowsClient", preLaunch: false, cancellationToken);
                return true;
            }

            App.Logger.WriteLine(
                "Bootstrapper::TryLaunchNonWindowsClient",
                result.Failure?.Message ?? "No Roblox runtime is available on this platform"
            );
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", "Launch failed: " + ex.Message);
        }
        return false;
    }

    private static void ReapplyFpsUnlocker()
    {
        if (
            Voidstrap.Utility.Platform.IsMacOS
            || App.Settings.Prop.FpsUnlockerEnabled != true
            || Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex != 0
        )
            return;
        try
        {
            int cap = App.GlobalSettings.GetInt("FramerateCap", 0);
            if (cap > 240)
                return;
            if (App.GlobalSettings.SetProperty("FramerateCap", 9999))
                App.GlobalSettings.Save();
            App.Logger.WriteLine(
                "Bootstrapper::ReapplyFpsUnlocker",
                "Roblox reset FramerateCap to " + cap + ", restored the unlimited cap"
            );
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("Bootstrapper::ReapplyFpsUnlocker", ex);
        }
    }

    private static void TrySuppressKnownBlockedFastFlags(string clientSettingsPath, string blockedFlagsPath)
    {
        try
        {
            if (!File.Exists(clientSettingsPath))
                return;
            string currentHash = MD5Hash.FromFile(clientSettingsPath);
            if (File.Exists(blockedFlagsPath))
            {
                string blockedHash = File.ReadAllText(blockedFlagsPath).Trim();
                if (string.Equals(currentHash, blockedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Filesystem.AssertReadOnly(clientSettingsPath);
                    File.Delete(clientSettingsPath);
                    App.Logger.WriteLine(
                        "Bootstrapper::WaitForLogFile",
                        "Skipped an unchanged FastFlag file that previously prevented Roblox from starting"
                    );
                    return;
                }
                File.Delete(blockedFlagsPath);
            }
            if (TryValidateFastFlagFile(clientSettingsPath, out string reason))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(blockedFlagsPath)!);
            File.WriteAllText(blockedFlagsPath, currentHash);
            Filesystem.AssertReadOnly(clientSettingsPath);
            File.Delete(clientSettingsPath);
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Skipped an invalid FastFlag file: " + reason);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Could not check the blocked FastFlag record: " + ex.Message);
        }
    }

    private static bool TryValidateFastFlagFile(string path, out string reason)
    {
        reason = string.Empty;
        try
        {
            FileInfo file = new(path);
            if (file.Length > 16L * 1024L * 1024L)
            {
                reason = "the file is larger than 16 MB";
                return false;
            }
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                reason = "the root value is not an object";
                return false;
            }
            int count = 0;
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                count++;
                if (count > 50000)
                {
                    reason = "the file contains more than 50000 entries";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(property.Name) || property.Name.Length > 256 || property.Name.Any(char.IsControl))
                {
                    reason = "an entry name is invalid";
                    return false;
                }
                if (
                    property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null
                )
                {
                    reason = "an entry contains a nested value";
                    return false;
                }
                if (property.Value.ValueKind == JsonValueKind.String && (property.Value.GetString()?.Length ?? 0) > 1048576)
                {
                    reason = "an entry value is larger than 1 MB";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static string? TryGetFileHash(string path)
    {
        try
        {
            return MD5Hash.FromFile(path);
        }
        catch
        {
            return null;
        }
    }

    private static HashSet<string> GetExistingLogFiles(string logDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(logDirectory, "*.log").ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    internal static void ExcludeLogsBeforeProcess(string logDirectory, HashSet<string> existingLogs, DateTime processStartedUtc)
    {
        foreach (string path in GetExistingLogFiles(logDirectory))
        {
            try
            {
                if (File.GetCreationTimeUtc(path) < processStartedUtc)
                    existingLogs.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string? FindNewLogFile(string logDirectory, HashSet<string> existingLogs, DateTime launchStartedUtc)
    {
        try
        {
            return Directory
                .EnumerateFiles(logDirectory, "*.log")
                .Select(path => new FileInfo(path))
                .Where(file => !existingLogs.Contains(file.FullName) && file.LastWriteTimeUtc >= launchStartedUtc.AddSeconds(-3))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Log enumeration failed: " + ex.Message);
            return null;
        }
    }

    private static void LaunchFleasion(string logIdent)
    {
        if (App.Settings.Prop?.Fleasion != true || !Voidstrap.Utility.Platform.IsWindows)
        {
            return;
        }

        string executable = Path.Combine(Paths.Fleasion, "Fleasion.exe");
        if (!File.Exists(executable))
        {
            App.Logger.WriteLine(logIdent, "Fleasion is switched on but it is not installed, skipping it");
            return;
        }

        try
        {
            Process[] running = Process.GetProcessesByName("Fleasion");
            try
            {
                if (running.Length > 0)
                {
                    App.Logger.WriteLine(logIdent, "Fleasion is already running, leaving it as it is");
                    return;
                }
            }
            finally
            {
                foreach (Process existing in running)
                {
                    existing.Dispose();
                }
            }

            ProcessStartInfo startInfo = new()
            {
                FileName = executable,
                WorkingDirectory = Paths.Fleasion,
                UseShellExecute = true,
            };
            using Process? process = Process.Start(startInfo);
            if (process != null)
            {
                App.Logger.WriteLine(logIdent, $"Launched Fleasion (pid {process.Id})");
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Failed to launch Fleasion: " + ex.Message);
        }
    }

    private async Task LaunchCustomIntegrations(string logIdent, bool preLaunch, CancellationToken ct)
    {
        if (preLaunch)
        {
            LaunchFleasion(logIdent);
        }

        if (ClosesAfterLaunch)
        {
            return;
        }
        IEnumerable<CustomIntegration>? enumerable = App.Settings.Prop?.CustomIntegrations;
        foreach (CustomIntegration integration in (enumerable ?? []).Where(i => !i.SpecifyGame && i.PreLaunch == preLaunch))
        {
            if (string.IsNullOrWhiteSpace(integration.Location) || !File.Exists(integration.Location))
            {
                App.Logger.WriteLine(logIdent, "Integration missing: " + integration.Name);
                continue;
            }
            try
            {
                ct.ThrowIfCancellationRequested();
                if (integration.Delay > 0)
                {
                    await Task.Delay(Math.Min(integration.Delay, 30000), ct);
                }
                ct.ThrowIfCancellationRequested();
                ProcessStartInfo processStartInfo = new()
                {
                    FileName = integration.Location,
                    Arguments = (integration.LaunchArgs ?? "").Replace("\r\n", " "),
                    WorkingDirectory = Path.GetDirectoryName(integration.Location),
                    UseShellExecute = true,
                };
                if (integration.RunMinimized && Voidstrap.Utility.Platform.IsWindows)
                {
                    processStartInfo.WindowStyle = ProcessWindowStyle.Minimized;
                }
                if (integration.RunAsAdmin && Voidstrap.Utility.Platform.IsWindows)
                {
                    processStartInfo.Verb = "runas";
                }
                using Process? process = Process.Start(processStartInfo);
                if (process != null)
                {
                    App.Logger.WriteLine(logIdent, $"Launched integration '{integration.Name}' (pid {process.Id})");
                    if (integration.AutoClose)
                    {
                        _integrationAutoclosePids.Add(process.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(logIdent, "Failed to launch '" + integration.Name + "': " + ex.Message);
            }
        }
    }

    private bool IsFromCurrentRobloxLaunch(Process process)
    {
        if (_robloxLaunchUtc == DateTime.MinValue)
        {
            return false;
        }
        try
        {
            return process.StartTime.ToUniversalTime() >= _robloxLaunchUtc.AddSeconds(-2);
        }
        catch
        {
            return false;
        }
    }

    private async Task RunStudioSessionAsync(CancellationToken ct)
    {
        _installLock?.Dispose();
        try
        {
            System.Windows.Application current = System.Windows.Application.Current;
            current?.Dispatcher.Invoke(
                (Action)
                    delegate
                    {
                        System.Windows.Application.Current?.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
                    }
            );
        }
        catch { }
        try
        {
            Dialog?.CloseBootstrapper();
        }
        catch { }
        try
        {
            App.Logger.WriteLine("Bootstrapper::RunStudioSessionAsync", "Keeping Voidstrap in the background for the Studio session");
            try
            {
                Voidstrap.Integrations.Studio.StudioIntegration.Start();
            }
            catch { }
            DateTime deadline = DateTime.UtcNow.AddSeconds(90.0);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                if (StudioRunning())
                {
                    break;
                }
                await Task.Delay(1000, ct).ConfigureAwait(continueOnCapturedContext: false);
            }
            while (!ct.IsCancellationRequested)
            {
                if (!StudioRunning())
                {
                    break;
                }
                await Task.Delay(2000, ct).ConfigureAwait(continueOnCapturedContext: false);
            }
            App.Logger.WriteLine("Bootstrapper::RunStudioSessionAsync", "Roblox Studio closed, ending background session");
            await CompressInstallsAfterExitAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::RunStudioSessionAsync", "Session watch failed: " + ex.Message);
        }
        finally
        {
            try
            {
                Voidstrap.Integrations.Studio.StudioIntegration.Shutdown();
            }
            catch { }
        }
    }

    private static bool StudioRunning()
    {
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            return Voidstrap.Platform.Linux.StudioProcessNames.AnyRunning();
        }

        Process[] procs = Process.GetProcessesByName(Voidstrap.Utility.Platform.RobloxStudioProcessName);
        bool any = procs.Length > 0;
        foreach (Process p in procs)
        {
            try
            {
                p.Dispose();
            }
            catch { }
        }
        return any;
    }

    private bool ClosesAfterLaunch => _launchMode == LaunchMode.Player && (App.Settings?.Prop.LaunchWithoutVoidstrap ?? false);

    private Task LaunchWatcherIfNeeded(string logFileName, CancellationToken ct)
    {
        if (ClosesAfterLaunch && !(App.LaunchSettings.TestModeFlag?.Active ?? false))
        {
            App.Logger.WriteLine(
                "Bootstrapper::LaunchWatcherIfNeeded",
                "Launch without Voidstrap is on, nothing keeps running after Roblox starts"
            );
            return Task.CompletedTask;
        }
        bool flag = !string.IsNullOrEmpty(_launchStatusFile);
        bool shouldOptimize = RobloxProcessOptimizer.ShouldRun(App.Settings?.Prop);
        bool shouldTrackOverlays = Watcher.OverlaysNeedGameState() || Voidstrap.Integrations.Overlays.OverlaySettings.GameEffectsEnabled;
        bool shouldKeepSnapTap =
            _launchMode == LaunchMode.Player
            && Voidstrap.Utility.Platform.SupportsInputHooks
            && (App.Settings?.Prop.SnapTapEnabled ?? false);
        bool shouldCompressAfterExit = RobloxInstallCompression.Supported && (App.Settings?.Prop.CompressRobloxInstalls ?? false);
        bool shouldManageFullscreen = _launchMode == LaunchMode.Player && Watcher.FullscreenModeActive;
        if (
            !(
                (App.Settings?.Prop.EnableActivityTracking ?? false)
                || flag
                || shouldOptimize
                || shouldTrackOverlays
                || shouldKeepSnapTap
                || shouldCompressAfterExit
                || shouldManageFullscreen
            ) && !(App.LaunchSettings.TestModeFlag?.Active ?? false)
        )
        {
            return Task.CompletedTask;
        }
        WatcherData watcherData = new()
        {
            ProcessId = _appPid,
            LogFile = logFileName,
            AutoclosePids = [.. _integrationAutoclosePids],
        };
        string text = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(watcherData)));
        try
        {
            ct.ThrowIfCancellationRequested();
            ProcessStartInfo watcherStartInfo = new() { FileName = Paths.LaunchExecutable, UseShellExecute = false };
            watcherStartInfo.ArgumentList.Add("-watcher");
            watcherStartInfo.ArgumentList.Add(text);
            if (App.LaunchSettings.TestModeFlag?.Active ?? false)
                watcherStartInfo.ArgumentList.Add("-testmode");
            using Process? watcherProcess = Process.Start(watcherStartInfo);
            if (watcherProcess == null)
            {
                App.Logger.WriteLine("Bootstrapper::LaunchWatcherIfNeeded", "Watcher process did not start");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::LaunchWatcherIfNeeded", "Watcher process start failed: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    private bool ShouldRunAsAdmin()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return false;
        foreach (RegistryKey root in WindowsRegistry.Roots)
        {
            using RegistryKey? registryKey = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers");
            if (registryKey != null)
            {
                if (
                    registryKey.GetValue(AppData.ExecutablePath) is string value
                    && value.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase)
                )
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void MigrateCompatibilityFlags()
    {
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
        string text = Path.Combine(AppData.VersionsRoot, AppData.State.VersionGuid, AppData.ExecutableName);
        string text2 = Path.Combine(_latestVersionDirectory, AppData.ExecutableName);
        using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(
            "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers"
        );
        if (registryKey.GetValue(text) is string value)
        {
            App.Logger.WriteLine("MigrateCompat", text + " -> " + text2);
            registryKey.SetValueSafe(text2, value);
            registryKey.DeleteValueSafe(text);
        }
    }

    private static async Task StopRobloxPlayersAsync(CancellationToken ct)
    {
        bool studioRunning = StudioRunning();
        if (studioRunning)
        {
            App.Logger.WriteLine("Bootstrapper::StopRobloxPlayers", "Studio is running, leaving crash handlers alone.");
        }

        for (int round = 1; round <= 3; round++)
        {
            Process[] processes = studioRunning
                ? Process.GetProcessesByName(ProcRobloxPlayer)
                : [.. Process.GetProcessesByName(ProcRobloxPlayer), .. Process.GetProcessesByName(ProcRobloxCrash)];
            if (processes.Length == 0)
            {
                return;
            }
            foreach (Process process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    await process
                        .WaitForExitAsync(ct)
                        .WaitAsync(TimeSpan.FromSeconds(8), ct)
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("Bootstrapper::StopRobloxPlayers", $"Could not stop process {process.Id}: {ex.Message}");
                }
                finally
                {
                    process.Dispose();
                }
            }
            await Task.Delay(500, ct).ConfigureAwait(continueOnCapturedContext: false);
        }
        Process[] remaining = studioRunning
            ? Process.GetProcessesByName(ProcRobloxPlayer)
            : [.. Process.GetProcessesByName(ProcRobloxPlayer), .. Process.GetProcessesByName(ProcRobloxCrash)];
        bool stillRunning = false;
        foreach (Process process in remaining)
        {
            try
            {
                stillRunning |= !process.HasExited;
            }
            catch
            {
                stillRunning = true;
            }
            finally
            {
                process.Dispose();
            }
        }
        if (stillRunning)
        {
            throw new UnauthorizedAccessException("Roblox is still running and its installation files are in use");
        }
    }

    internal static async Task CompressInstallsAfterExitAsync()
    {
        if (!RobloxInstallCompression.Supported || !App.Settings.Prop.CompressRobloxInstalls)
        {
            return;
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(10));
        try
        {
            await RobloxInstallCompression.CompressAllAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            App.Logger.WriteLine(
                "Bootstrapper::CompressInstallsAfterExit",
                "Compression ran out of time and was stopped, the install stays uncompressed"
            );
        }
    }

    private void CleanupVersionsFolder()
    {
        RobloxInstallCompression.DeleteStaleArchives(
            AppData.VersionsRoot,
            [App.State.Prop.Player.VersionGuid, App.State.Prop.VngPlayer.VersionGuid, App.State.Prop.Studio.VersionGuid]
        );
        string[] directories = Directory.GetDirectories(AppData.VersionsRoot);
        bool customRoot = !PathsEqual(AppData.VersionsRoot, Paths.Versions);
        foreach (string text in directories)
        {
            string fileName = Path.GetFileName(text);
            if (!IsValidVersionGuid(fileName))
            {
                continue;
            }
            if (
                !(fileName == App.State.Prop.Player.VersionGuid)
                && !(fileName == App.State.Prop.VngPlayer.VersionGuid)
                && !(fileName == App.State.Prop.Studio.VersionGuid)
            )
            {
                if (customRoot && !IsOwnedVersionDirectory(text))
                {
                    App.Logger.WriteLine("CleanupVersionsFolder", "Skipped unmanaged version directory: " + text);
                    continue;
                }
                try
                {
                    Directory.Delete(text, recursive: true);
                    App.Logger.WriteLine("CleanupVersionsFolder", "Deleted: " + text);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("CleanupVersionsFolder", "Failed to delete " + text + ": " + ex.Message);
                }
            }
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase
        );
    }

    private bool IsOwnedVersionDirectory(string directory)
    {
        try
        {
            string marker = Voidstrap.Utility.JsonFile.ReadText(Path.Combine(directory, VersionOwnershipFileName), 128).Trim();
            return string.Equals(marker, AppData.BinaryType, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static async Task WithRetryAsync(
        Func<Task> action,
        string context,
        int maxAttempts = 5,
        int baseDelayMs = 750,
        Func<Exception, bool>? isTransient = null,
        CancellationToken ct = default
    )
    {
        isTransient ??= ex2 =>
            (ex2 is TaskCanceledException || ex2 is HttpRequestException || ex2 is IOException || ex2 is SocketException);
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await action().ConfigureAwait(continueOnCapturedContext: false);
                break;
            }
            catch (Exception ex) when (isTransient(ex) && attempt < maxAttempts)
            {
                int num = baseDelayMs * (1 << attempt - 1);
                int num2 = (int)((double)num * (0.15 * (Random.Shared.NextDouble() * 2.0 - 1.0)));
                num = Math.Clamp(num + num2, 250, 10000);
                App.Logger.WriteLine(context, $"Transient error ({attempt}/{maxAttempts}): {ex.Message}. Retrying in {num}ms...");
                await Task.Delay(num, ct).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
    }

    private static async Task SafeDeleteDirectoryAsync(string path, string context, CancellationToken ct)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        await WithRetryAsync(
                delegate
                {
                    NormalizeInstallationAttributes(path);
                    Directory.Delete(path, recursive: true);
                    return Task.CompletedTask;
                },
                context,
                5,
                600,
                ex => (ex is IOException || ex is UnauthorizedAccessException),
                ct
            )
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    private static string DescribePackageFailure(Exception? failure)
    {
        if (failure == null)
        {
            return string.Empty;
        }
        Exception root = failure;
        while (root.InnerException != null)
        {
            root = root.InnerException;
        }
        string reason = root switch
        {
            UnauthorizedAccessException =>
                "Windows blocked access to the install folder. Check that antivirus or controlled folder access is not blocking Voidstrap, then try again.",
            IOException io when IsDiskFull(io) => "The drive ran out of space while unpacking Roblox.",
            IOException io => "A file could not be written: " + io.Message,
            HttpRequestException http => "Roblox could not be downloaded: " + http.Message,
            TaskCanceledException => "The download timed out. Check your connection and try again.",
            _ => root.Message,
        };
        return string.IsNullOrWhiteSpace(reason) ? string.Empty : "\n" + reason;
    }

    private static bool IsDiskFull(IOException exception)
    {
        int code = exception.HResult & 0xFFFF;
        return code == 39 || code == 112;
    }

    internal bool FastFlagsAllowedForThisLaunch()
    {
        if (!App.Settings.Prop.UseFastFlagManager)
            return false;
        Voidstrap.Enums.ModApplyTarget target = App.Settings.Prop.FastFlagApplyTarget;
        if (target == Voidstrap.Enums.ModApplyTarget.Both)
            return true;
        return IsStudioLaunch ? target == Voidstrap.Enums.ModApplyTarget.Studio : target == Voidstrap.Enums.ModApplyTarget.Player;
    }

    private bool ModsAllowedForThisLaunch()
    {
        Voidstrap.Enums.ModApplyTarget target = App.Settings.Prop.ModApplyTarget;
        if (target == Voidstrap.Enums.ModApplyTarget.Both)
            return true;
        return IsStudioLaunch ? target == Voidstrap.Enums.ModApplyTarget.Studio : target == Voidstrap.Enums.ModApplyTarget.Player;
    }

}
