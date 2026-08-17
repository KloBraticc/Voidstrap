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

public class Bootstrapper
{
	internal enum MatchmakerRewriteOutcome
	{
		Rewritten,
		Unchanged,
		Cancelled,
		Failed
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
					NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite
				};
				_watcher.Created += OnCreated;
				_watcher.Renamed += OnRenamed;
				_watcher.EnableRaisingEvents = true;
			}
			catch
			{
			}
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
				if (!_existingLogs.Contains(path) && File.Exists(path) && File.GetLastWriteTimeUtc(path) >= _launchStartedUtc.AddSeconds(-3))
					_completion.TrySetResult(path);
			}
			catch
			{
			}
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

    private const int ProgressBarMaximum = 10000;

    private const double TaskbarProgressMaximumWpf = 1.0;

    private const int TaskbarProgressMaximumWinForms = 100;

    private const string ProcRobloxPlayer = "RobloxPlayerBeta";

    private const string ProcRobloxCrash = "RobloxCrashHandler";

    private const string ProcRobloxStudio = "RobloxStudioBeta";

    private const string ProcEuroTrucks = "eurotrucks2.exe";

    private const string ProcRobloxExe = "RobloxPlayerBeta.exe";

    private const string SkyboxArchiveBaseUrl = "https://github.com/KloBraticc/SkyboxPackV2/archive/";

    private const string SkyboxCommitApiUrl = "https://api.github.com/repos/KloBraticc/SkyboxPackV2/commits/main";

    private const string SkyboxVersionFile = "skybox.commit";

    private static readonly string[] SkyboxFileNames = ["sky512_bk.tex", "sky512_dn.tex", "sky512_ft.tex", "sky512_lf.tex", "sky512_rt.tex", "sky512_up.tex"];

	private const int MaxPackageDownloadAttempts = 5;

	private const string VersionOwnershipFileName = ".voidstrap-managed";

    private const string AppSettingsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Settings>\r\n\t<ContentFolder>content</ContentFolder>\r\n\t<BaseUrl>http://www.roblox.com</BaseUrl>\r\n</Settings>\r\n";

    private static readonly int ModApplyConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private readonly FastZipEvents _fastZipEvents = new();

    private readonly CancellationTokenSource _cancelTokenSource = new();

    internal CancellationToken CancellationToken => _cancelTokenSource.Token;

    private readonly IAppData AppData;

    private readonly LaunchMode _launchMode;
    private string _deploymentChannel;


    private string _launchCommandLine = App.LaunchSettings.RobloxLaunchArgs;

    private string _latestVersionGuid;

    public bool InstallOnly { get; set; }

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

    private string _latestVersionDirectory;

    private PackageManifest _versionPackageManifest;

    private int _isInstalling;

    private readonly TaskCompletionSource _installationStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private long _totalDownloadedBytes;

    private double _progressIncrement;

    private double _taskbarProgressIncrement;

    private double _taskbarProgressMaximum;

    private Process? _robloxProcess;

    private AsyncMutex? _mutex;

    private int _appPid;

    private DateTime _robloxLaunchUtc;

    private bool _noConnection;

    private static readonly string PackFolder = Paths.SkyboxPack;

    private static readonly HttpClient SkyboxHttpClient = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromMinutes(30L));

    private static readonly HttpClient RobloxPackageClient = Voidstrap.Utility.VpnHttpClient.Create(Timeout.InfiniteTimeSpan, handler =>
    {
        handler.AutomaticDecompression = DecompressionMethods.None;
        handler.MaxConnectionsPerServer = 128;
        handler.PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30);
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
    });

    private static readonly ArrayPool<byte> DownloadBufferPool = ArrayPool<byte>.Shared;

    private static readonly int ExtractionConcurrency = Math.Clamp(ModApplyConcurrency, 2, 8);

	private readonly SemaphoreSlim _networkRequestSlots;

    private string? _packageExtractionDirectory;

    private static readonly Dictionary<string, string> SkyboxPatchFolderMap = new()
    {
        { "a564ec8aeef3614e788d02f0090089d8", "a5" },
        { "7328622d2d509b95dd4dd2c721d1ca8b", "73" },
        { "a50f6563c50ca4d5dcb255ee5cfab097", "a5" },
        { "6c94b9385e52d221f0538aadaceead2d", "6c" },
        { "9244e00ff9fd6cee0bb40a262bb35d31", "92" },
        { "78cb2e93aee0cdbd79b15a866bc93a54", "78" }
    };

    private const string SkyboxPatchSha256 = "5e710f0e8ff474aa78b2b472e371cb869268c3e033626dc6bca0d33bae704eeb";

    public IBootstrapperDialog? Dialog;

    private static readonly string? _launchStatusFile = Environment.GetEnvironmentVariable("VOIDSTRAP_STATUS_FILE");

    private static readonly string[] RobloxLeaveMarkers = ["[FLog::SingleSurfaceApp] leaveUGCGameInternal", "[FLog::Network] Time to disconnect replication data:"];

    private const string RobloxRejoinMarker = "[FLog::Output] ! Joining game";

    private Watcher? _inProcessWatcher;

    private readonly List<int> _integrationAutoclosePids = [];


    public bool IsStudioLaunch => _launchMode != LaunchMode.Player;

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
            return GetMissingCriticalFile() != null;
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

    public Bootstrapper(LaunchMode launchMode)
    {
		if (DownloadConfiguration.Normalize(App.Settings.Prop))
			App.Settings.SaveDeferred();
		int requestLimit = DownloadConfiguration.ResolveSegmentRequestLimit(App.Settings.Prop);
		_networkRequestSlots = new SemaphoreSlim(requestLimit, requestLimit);
        _launchMode = launchMode;
        _deploymentChannel = App.Settings.Prop.Channel;
        _fastZipEvents.FileFailure = OnExtractionFailure;
        _fastZipEvents.DirectoryFailure = OnExtractionFailure;
        _fastZipEvents.ProcessFile = OnExtractionFile;
        AppData = IsStudioLaunch ? new RobloxStudioData() : new RobloxPlayerData();
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
        if (Dialog is Control control)
        {
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
        DependencyObject val = (DependencyObject)((dialog is DependencyObject) ? dialog : null);
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
            string data = App.LaunchSettings.MatchmakerTargetFlag.Data;
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
        InvokeOnDialog(delegate
        {
            Dialog.Message = message;
        });
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
        catch
        {
        }
    }

    private void SetProgressValue(int value)
    {
        InvokeOnDialog(delegate
        {
            Dialog.ProgressValue = value;
        });
    }

    private void SetProgressMaximum(int max)
    {
        InvokeOnDialog(delegate
        {
            Dialog.ProgressMaximum = max;
        });
    }

    private void SetProgressStyle(ProgressBarStyle style)
    {
        InvokeOnDialog(delegate
        {
            Dialog.ProgressStyle = style;
        });
    }

    private void UpdateProgressBar()
    {
        if (Dialog != null)
        {
            InvokeOnDialog(delegate
            {
                long num = Interlocked.Read(in _totalDownloadedBytes);
                int progressValue = (int)Math.Clamp(Math.Floor(_progressIncrement * (double)num), 0.0, 10000.0);
                Dialog.ProgressValue = progressValue;
                double taskbarProgressValue = Math.Clamp(_taskbarProgressIncrement * (double)num, 0.0, 100.0);
                Dialog.TaskbarProgressValue = taskbarProgressValue;
            });
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
            SetStatus($"Downloading {value:0}%\n{FormatBytes(num)} of {FormatBytes(totalPacked)} · {FormatSpeed(emaSpeed)} · ETA {value2} · {value3}/{totalPackages}");
            UpdateProgressBar();
            try
            {
                DownloadProgressChanged?.Invoke(new DownloadProgressInfo
                {
                    Percent = value,
                    BytesDone = num,
                    TotalBytes = totalPacked,
                    SpeedBytesPerSec = emaSpeed,
                    PackagesDone = value3,
                    TotalPackages = totalPackages,
                    BinaryType = AppData.BinaryType
                });
            }
            catch
            {
            }
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
        if (!App.Settings.Prop.ForceRobloxReinstall)
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
        Stopwatch launchTimer = Stopwatch.StartNew();
        App.Logger.WriteLine("Bootstrapper::Run", "Running bootstrapper");
        Dialog?.CancelEnabled = true;
        SetStatus(Strings.Bootstrapper_Status_Connecting);
        ApplyForcedReinstall();
        bool mutexExists = false;
        try
        {
            using (Mutex.OpenExisting("Voidstrap-Bootstrapper"))
            {
                App.Logger.WriteLine("Bootstrapper::Run", "Voidstrap-Bootstrapper mutex exists, waiting...");
                SetStatus(Strings.Bootstrapper_Status_WaitingOtherInstances);
                mutexExists = true;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
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
        Task versionInfoTask = Voidstrap.Utility.Platform.SupportsWindowsClient ? GetLatestVersionInfo(false) : Task.CompletedTask;
        bool updateCheckFresh = DateTime.UtcNow - App.State.Prop.LastLauncherUpdateCheckUtc < TimeSpan.FromMinutes(15);
        Task<bool> launcherUpdateTask = App.Settings.Prop.CheckForUpdates && !App.LaunchSettings.UpgradeFlag.Active && !InstallOnly && !updateCheckFresh
            ? CheckAndApplyUpdate("Bootstrapper::Run")
            : Task.FromResult(false);
        if (await launcherUpdateTask)
        {
            Observe(versionInfoTask);
            return;
        }
        if (!Voidstrap.Utility.Platform.SupportsWindowsClient)
        {
            Observe(versionInfoTask);
            await mutex.ReleaseAsync();
            if (!App.LaunchSettings.NoLaunchFlag.Active && !InstallOnly && !_cancelTokenSource.IsCancellationRequested)
            {
                await StartRoblox(_cancelTokenSource.Token);
            }
            Dialog?.CloseBootstrapper();
            return;
        }
        if (!_noConnection)
        {
            Exception versionError = await FetchVersionInfoAsync(versionInfoTask);
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
			bool upgradeRequired = AppData.State.VersionGuid != _latestVersionGuid || MustUpgrade || App.Settings.Prop.ForceRobloxReinstall;
			if (upgradeRequired)
            {
				Exception connectivityError = await Deployment.InitializeConnectivity();
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
				App.FastFlags.ApplyPreloadFlags();
				App.FastFlags.SaveDeferred();
			}
			if (!_noConnection)
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
        WindowsRegistry.RegisterVoidstrap();
        await mutex.ReleaseAsync();
        if (!App.LaunchSettings.NoLaunchFlag.Active && !InstallOnly && !_cancelTokenSource.IsCancellationRequested)
        {
            App.Logger.WriteLine("Bootstrapper::Run", "Prelaunch preparation completed in " + launchTimer.ElapsedMilliseconds + " ms");
            await StartRoblox(_cancelTokenSource.Token);
        }
        Dialog?.CloseBootstrapper();
        if (InstallOnly)
        {
            return;
        }
        if (_launchMode != LaunchMode.Player || !AssetProxyServer.IsRequired)
        {
            return;
        }
        try
        {
            await WaitForRobloxExitAsync(_cancelTokenSource.Token);
        }
        catch
        {
        }
        if (App.Settings.Prop.AssetWarpPreloadEnabled && App.Settings.Prop.AssetWarpPreloadCrossGame)
        {
            using CancellationTokenSource crossGameDeadline = CancellationTokenSource.CreateLinkedTokenSource(_cancelTokenSource.Token);
            crossGameDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await AssetPreloadProactive.DiscoverCrossGameAsync(crossGameDeadline.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
        AssetProxyServer.Stop();
        try
        {
            System.Windows.Application current = System.Windows.Application.Current;
            if (current == null)
            {
                return;
            }
            ((DispatcherObject)current).Dispatcher.Invoke((Action)delegate
            {
                try
                {
                    _inProcessWatcher?.Dispose();
                }
                catch
                {
                }
                _inProcessWatcher = null;
            });
        }
        catch
        {
        }
    }

    private async Task WaitForRobloxToFullyCloseAsync()
    {
        string processName = "RobloxPlayerBeta".Split('.')[0];
        try
        {
            for (int i = 0; i < 360; i++)
            {
                if (_cancelTokenSource.IsCancellationRequested)
                {
                    break;
                }
                if (IsProcessRunning(processName))
                {
                    break;
                }
                await Task.Delay(500, _cancelTokenSource.Token);
            }
            App.Logger.WriteLine("Bootstrapper::WaitForRobloxClose", "Watching " + processName + ".exe - exiting when every Roblox instance is closed.");
            int goneStreak = 0;
            while (!_cancelTokenSource.IsCancellationRequested)
            {
                if (IsProcessRunning(processName))
                {
                    goneStreak = 0;
                }
                else
                {
                    goneStreak++;
                    if (goneStreak >= 6)
                    {
                        App.Logger.WriteLine("Bootstrapper::WaitForRobloxClose", "Every Roblox instance is closed.");
                        break;
                    }
                }
                await Task.Delay(1000, _cancelTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex2)
        {
            App.Logger.WriteLine("Bootstrapper::WaitForRobloxClose", "Watch failed: " + ex2.Message);
        }
    }

    private async Task WaitForRobloxGameAsync()
    {
        string proc = "RobloxPlayerBeta".Split('.')[0];
        try
        {
            bool started = false;
            for (int i = 0; i < 360; i++)
            {
                if (_cancelTokenSource.IsCancellationRequested)
                {
                    break;
                }
                if (IsProcessRunning(proc))
                {
                    started = true;
                    break;
                }
                await Task.Delay(500, _cancelTokenSource.Token);
            }
            if (!started)
            {
                App.Logger.WriteLine("Bootstrapper::WaitForRobloxGame", "Roblox process never started.");
                return;
            }
            App.Logger.WriteLine("Bootstrapper::WaitForRobloxGame", "Roblox process detected - watching for leave.");
            string currentLog = null;
            long position = 0L;
            bool leaving = false;
            DateTime leaveAt = DateTime.MinValue;
            while (!_cancelTokenSource.IsCancellationRequested)
            {
                string text = FindNewestPlayerLog();
                if (!string.Equals(text, currentLog, StringComparison.OrdinalIgnoreCase))
                {
                    currentLog = text;
                    position = 0L;
                }
                string text2 = ((currentLog != null) ? ReadNewLogText(currentLog, ref position) : "");
                bool num = IsProcessRunning(proc);
                bool flag = text2.Contains("[FLog::Output] ! Joining game", StringComparison.Ordinal);
                bool flag2 = !num || RobloxLeaveMarkers.Any(m => text2.Contains(m, StringComparison.Ordinal));
                if (flag)
                {
                    if (leaving)
                    {
                        leaving = false;
                        App.Logger.WriteLine("Bootstrapper::WaitForRobloxGame", "Rejoin/teleport detected - staying with the game.");
                    }
                }
                else if (flag2 && !leaving)
                {
                    leaving = true;
                    leaveAt = DateTime.UtcNow;
                    App.Logger.WriteLine("Bootstrapper::WaitForRobloxGame", "Leave signal detected - confirming it is not a matchmaker rejoin.");
                }
                if (leaving && (DateTime.UtcNow - leaveAt).TotalMilliseconds > 6000.0)
                {
                    App.Logger.WriteLine("Bootstrapper::WaitForRobloxGame", "Confirmed leave - closing Roblox and returning to the built-in browser.");
                    break;
                }
                await Task.Delay(300, _cancelTokenSource.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex2)
        {
            App.Logger.WriteLine("Bootstrapper::WaitForRobloxGame", "Watch failed: " + ex2.Message);
        }
    }

    private static string? FindNewestPlayerLog()
    {
        try
        {
            string path = Path.Combine(Paths.LocalAppData, "Roblox", "logs");
            if (!Directory.Exists(path))
            {
                return null;
            }
            return (from f in new DirectoryInfo(path).GetFiles()
                    where f.Name.Contains("Player", StringComparison.OrdinalIgnoreCase)
                    orderby f.LastWriteTime descending
                    select f).FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
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

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            Process[] processesByName = Process.GetProcessesByName(processName);
            bool result = processesByName.Length != 0;
            Process[] array = processesByName;
            foreach (Process process in array)
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                }
            }
            return result;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureRobloxClosed()
    {
        string[] array =
        [
            "RobloxPlayerBeta".Split('.')[0],
            "RobloxCrashHandler"
        ];
        foreach (string processName in array)
        {
            try
            {
                Process[] processesByName = Process.GetProcessesByName(processName);
                foreach (Process process in processesByName)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                    }
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static async Task WaitForRobloxExitAsync(CancellationToken ct)
    {
        string procName = "RobloxPlayerBeta";
        try
        {
            procName = Path.GetFileNameWithoutExtension("RobloxPlayerBeta");
        }
        catch
        {
        }
        await Task.Delay(4000, ct).ConfigureAwait(continueOnCapturedContext: false);
        int absentSeconds = 0;
        while (!ct.IsCancellationRequested)
        {
            bool flag;
            try
            {
                Process[] processesByName = Process.GetProcessesByName(procName);
                flag = processesByName.Length != 0;
                Process[] array = processesByName;
                for (int i = 0; i < array.Length; i++)
                {
                    array[i].Dispose();
                }
            }
            catch
            {
                flag = false;
            }
            if (flag)
            {
                absentSeconds = 0;
            }
            else
            {
                absentSeconds += 3;
                if (absentSeconds >= 25)
                {
                    System.Windows.Application current = System.Windows.Application.Current;
                    break;
                }
            }
            await Task.Delay(3000, ct).ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private async Task<bool> CheckAndApplyUpdate(string logIdent)
    {
        string text = await GithubUpdater.GetLatestVersionTagAsync();
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
        if (Version.TryParse(text, out Version result) && Version.TryParse(remoteTag, out Version result2))
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
        _ = task.ContinueWith(static finished => _ = finished.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private static bool IsRetryableConnectionError(Exception error)
    {
        if (error is HttpRequestException { StatusCode: { } status })
            return (int)status >= 500 || status == HttpStatusCode.RequestTimeout || status == HttpStatusCode.TooManyRequests;
        return error is HttpRequestException or IOException or TaskCanceledException or TimeoutException or SocketException;
    }

    private async Task<Exception> FetchVersionInfoAsync(Task started)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_cancelTokenSource.Token);
        deadline.CancelAfter(ConnectDeadline);
        Exception lastError = null;
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
                    App.Logger.WriteLine("Bootstrapper::FetchVersionInfo", $"Deploy info attempt {attempt} failed: {lastError.Message}");
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
            return new TimeoutException("Roblox did not answer within " + ConnectDeadline.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds.");
        }
        return lastError;
    }

    private async Task HandleConnectionError(Exception exception)
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
        App.Logger.WriteException("Bootstrapper::HandleConnectionError", exception);
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
						App.Logger.WriteLine("Bootstrapper::HandleConnectionError", $"HTTP {(int)statusCode} ({statusCode}), switching to default channel '{"production"}'.");
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
            App.Logger.WriteLine("Bootstrapper::HandleConnectionError", "Network/server issue, but Roblox is already installed, launching the existing version silently.");
            return;
        }
        string message = exception switch
        {
            TimeoutException => "Roblox did not respond in time. Check your connection, then try launching again.",
            HttpRequestException { StatusCode: { } statusCode2 } when statusCode2 >= HttpStatusCode.InternalServerError => $"Roblox's servers returned HTTP {(int)statusCode2}. Please try launching again in a few minutes.",
            HttpRequestException { StatusCode: { } statusCode3 } => $"Roblox returned HTTP {(int)statusCode3} for the deployment endpoint. Your channel may be unavailable, try switching it in Settings.",
            _ => "A network or server issue occurred. Try switching your channel in Settings or relaunching."
        };
        Frontend.ShowMessageBox(message, MessageBoxImage.Exclamation);
    }

    private static Exception UnwrapException(Exception ex)
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
            catch
            {
            }
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
					await SafeDeleteDirectoryAsync(stagingDirectory, "Bootstrapper::Cancel", CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
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

    private async Task GetLatestVersionInfo(bool fetchManifest = true, bool forceManifest = false, CancellationToken cancellationToken = default)
    {
		CancellationToken ct = cancellationToken.CanBeCanceled ? cancellationToken : _cancelTokenSource.Token;
		ClientVersion clientVersion = await Deployment.GetInfo(_deploymentChannel, cancellationToken: ct, binaryType: AppData.BinaryType, resolvedChannel: channel => _deploymentChannel = channel).ConfigureAwait(continueOnCapturedContext: false);
        if (string.IsNullOrWhiteSpace(clientVersion?.VersionGuid))
        {
            throw new Exception("VersionGuid missing from clientVersion response.");
        }
		if (!IsValidVersionGuid(clientVersion.VersionGuid))
		{
			throw new InvalidDataException("VersionGuid has an invalid format");
		}
        _latestVersionGuid = clientVersion.VersionGuid;
		_latestVersionDirectory = Path.GetFullPath(Path.Combine(AppData.VersionsRoot, _latestVersionGuid));
		string versionsRoot = Path.GetFullPath(AppData.VersionsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!_latestVersionDirectory.StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidDataException("VersionGuid resolves outside the versions directory");
		}
		if (!forceManifest && AppData.State.VersionGuid == _latestVersionGuid && !MustUpgrade && !App.Settings.Prop.ForceRobloxReinstall)
        {
            App.Logger.WriteLine("Bootstrapper::GetLatestVersionInfo", "Already up to date - skipping package manifest fetch.");
            _versionPackageManifest = new PackageManifest();
            return;
        }
		if (!fetchManifest)
		{
			_versionPackageManifest = new PackageManifest();
			return;
		}
        IReadOnlyList<string> manifestUrls = Deployment.GetLocations("/" + _latestVersionGuid + "-rbxPkgManifest.txt", _deploymentChannel);
        string manifestBody = null;
        foreach (string manifestUrl in manifestUrls)
        {
            App.Logger.WriteLine("Bootstrapper::GetLatestVersionInfo", "Fetching manifest: " + manifestUrl);
            try
            {
				using HttpRequestMessage request = new(HttpMethod.Get, manifestUrl)
				{
					Version = HttpVersion.Version20,
					VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
				};
				using HttpResponseMessage manifestResp = await SendPackageRequestAsync(request, ct).ConfigureAwait(continueOnCapturedContext: false);
                if (!manifestResp.IsSuccessStatusCode)
                {
                    App.Logger.WriteLine("Bootstrapper::GetLatestVersionInfo", $"Manifest HTTP {(int)manifestResp.StatusCode} ({manifestResp.StatusCode}) from this mirror, trying next.");
                    continue;
                }
				string body = await ReadTextBoundedAsync(manifestResp.Content, 4194304, ct).ConfigureAwait(continueOnCapturedContext: false);
                if (string.IsNullOrWhiteSpace(body) || body.TrimStart().StartsWith('<'))
                {
                    App.Logger.WriteLine("Bootstrapper::GetLatestVersionInfo", "Manifest returned HTML or empty content, trying next mirror.");
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

    private static bool HasGameLaunchTarget(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;
        }
        if (LaunchInterceptor.ExtractPlaceId(args) != 0L)
        {
            return true;
        }
        string[] markers = ["placelauncherurl", "gameInstanceId", "experiences/start", "games/start", "accessCode"];
        foreach (string marker in markers)
        {
            if (args.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<RuntimeInstallation> InstallSoberAndRefreshAsync(
        IRobloxRuntimeProvider provider,
        RuntimeInstallation installation,
        IPlatformHost host,
        CancellationToken cancellationToken)
    {
        const string logIdent = "Bootstrapper::InstallSober";
        App.Logger.WriteLine(logIdent, "Sober is not installed, installing it from Flathub");
        SetStatus("Installing Sober, this can take a while");
        try
        {
            OperationResult installed = await new LinuxSoberInstaller(host.Processes).InstallAsync(cancellationToken);
            if (!installed.Succeeded)
            {
                App.Logger.WriteLine(logIdent, installed.Failure?.Message ?? "Sober could not be installed");
                return installation;
            }

            App.Logger.WriteLine(logIdent, "Sober installed");
            SetStatus("Starting Roblox");
            return await provider.FindInstallationAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Sober could not be installed: " + ex.Message);
            return installation;
        }
    }

    private async Task<bool> TryLaunchNonWindowsClientAsync(string launchCommandLine, LaunchMode launchMode, CancellationToken cancellationToken)
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
                if (!installation.Capability.IsAvailable
                    && runtimeKind == RuntimeKind.Player
                    && LinuxSoberInstaller.CanInstall(installation.Capability))
                {
                    installation = await InstallSoberAndRefreshAsync(provider, installation, host, cancellationToken);
                }
                if (!installation.Capability.IsAvailable)
                {
                    App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", installation.Capability.Reason);
                    Frontend.ShowMessageBox(installation.Capability.Reason, MessageBoxImage.Hand);
                    return false;
                }

                LinuxRuntimeConfiguration configuration = LinuxRuntimeConfiguration.CreateDefault(Paths.Mods, host.Processes);
                OperationResult prepared = await configuration.PrepareAsync(
                    installation,
                    Voidstrap.Utility.SoberConfigurationMapper.CreatePlayerOptions(App.Settings.Prop),
                    cancellationToken);
                if (!prepared.Succeeded)
                {
                    string message = prepared.Failure?.Message ?? "Linux runtime preparation failed.";
                    App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", message);
                    Frontend.ShowMessageBox(message, MessageBoxImage.Hand);
                    return false;
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
                host.ResourceOptimization);
            OperationResult<DeploymentLaunchResult> result = await session.LaunchAsync(runtimeKind, launchTarget, settings, cancellationToken);
            if (result.Succeeded && result.Value is not null)
            {
                App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", result.Value.Summary);
                await LaunchCustomIntegrations("Bootstrapper::TryLaunchNonWindowsClient", preLaunch: false, cancellationToken);
                return true;
            }

            App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", result.Failure?.Message ?? "No Roblox runtime is available on this platform");
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::TryLaunchNonWindowsClient", "Launch failed: " + ex.Message);
        }
        return false;
    }

    private async Task StartRoblox(CancellationToken ct = default)
    {
		Stopwatch startTimer = Stopwatch.StartNew();
        SetStatus("Starting Roblox");
        await MaybeApplyVoidstrapMatchmakerAsync(ct);
        if (!Voidstrap.Utility.Platform.SupportsWindowsClient)
        {
            if (await TryLaunchNonWindowsClientAsync(_launchCommandLine, _launchMode, ct))
                return;
            App.Logger.WriteLine("Bootstrapper::StartRoblox", "No Roblox client available on this platform");
            return;
        }
        try
        {
            long assetPreloadPlaceId = LaunchInterceptor.ExtractPlaceId(_launchCommandLine);
            if (_launchMode != LaunchMode.Player)
            {
				AssetProxyServer.Stop();
            }
            else if (!App.Settings.Prop.AssetWarpEnabled)
            {
				AssetProxyServer.Stop();
            }
            else if (App.Settings.Prop.AssetWarpPreloadEnabled && assetPreloadPlaceId > 0)
            {
				AssetPreloadCache.SwitchSession(assetPreloadPlaceId);
            }
            if (_launchMode == LaunchMode.Player)
                await StartAssetProxyIfEnabled(ct);
            if (_launchMode == LaunchMode.Player && (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled))
            {
				App.FastFlags.RemoveInstalledPreloadFlags(Path.Combine(AppData.Directory, "ClientSettings", "ClientAppSettings.json"));
            }
            if (App.Settings.Prop.BlockRobloxTelemetry && !TelemetryBlocker.IsApplied())
            {
                if (ProcessElevation.IsAdministrator())
                {
                    TelemetryBlocker.Apply();
                }
                else
                {
                    App.Logger.WriteLine("Bootstrapper::StartRoblox", "Telemetry block entries are missing and Voidstrap is not elevated, skipping reassert");
                }
            }
            await LaunchCustomIntegrations("Bootstrapper::StartRoblox", preLaunch: true, ct);
			App.Logger.WriteLine("Bootstrapper::StartRoblox", "Process preparation completed in " + startTimer.ElapsedMilliseconds + " ms");
            ProcessStartInfo startInfo = BuildStartInfo();
            if (_launchMode == LaunchMode.StudioAuth)
            {
                using Process? studioAuthProcess = Process.Start(startInfo);
                return;
            }
            string text = Path.Combine(Paths.LocalAppData, "Roblox", "logs");
            Directory.CreateDirectory(text);
            string logFileName = await WaitForLogFileAsync(text, startInfo, ct);
			App.Logger.WriteLine("Bootstrapper::StartRoblox", "Process start and readiness completed in " + startTimer.ElapsedMilliseconds + " ms");
            if (string.IsNullOrEmpty(logFileName))
            {
                App.Logger.WriteLine("Bootstrapper::StartRoblox", "Unable to identify log file.");
                Frontend.ShowPlayerErrorDialog();
                return;
            }
            App.Logger.WriteLine("Bootstrapper::StartRoblox", "Log file: " + logFileName);
            if (AssetProxyServer.IsRunning && assetPreloadPlaceId > 0)
            {
                AssetPreloadCache.StartBackgroundWarm(assetPreloadPlaceId, ct);
            }
            if (IsStudioLaunch)
            {
                await RunStudioSessionAsync(_cancelTokenSource.Token);
                return;
            }
            await LaunchCustomIntegrations("Bootstrapper::StartRoblox", preLaunch: false, ct);
            await DisableCrashHandlerIfNeeded("Bootstrapper::StartRoblox", ct);
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

    private async Task MaybeApplyVoidstrapMatchmakerAsync(CancellationToken ct)
    {
        string rewritten = await RewriteVoidstrapMatchmakerBeforeDispatchAsync(
            _launchCommandLine,
            _launchMode,
            ct,
            () => SetStatus("Finding closest server..."));
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
        Action? onSearching = null)
    {
        if (launchMode != LaunchMode.Player || App.LaunchSettings.MatchmakerRejoinFlag.Active || string.IsNullOrWhiteSpace(launchCommandLine))
        {
            return launchCommandLine;
        }
		long placeId = LaunchInterceptor.ExtractPlaceId(launchCommandLine);
		if (placeId == 0 || (!App.Settings.Prop.VoidstrapMatchmakerEnabled && !ServerMatchmaker.HasPerGamePreference(placeId)) || LaunchInterceptor.ContainsSpecificGameInstance(launchCommandLine))
		{
			return launchCommandLine;
		}
        App.Logger.WriteLine("Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync", "Checking the launch for an enabled matchmaker or game datacenter preference, maximum 30 seconds");
        onSearching?.Invoke();
        MatchmakerDispatchTarget result = await ResolveMatchmakerDispatchTargetAsync(
            launchCommandLine,
            token => LaunchInterceptor.MaybeRewriteForClosestAsync(launchCommandLine, token),
            TimeSpan.FromSeconds(30L),
            cancellationToken);
        switch (result.Outcome)
        {
            case MatchmakerRewriteOutcome.Rewritten:
                App.Logger.WriteLine("Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync", "Voidstrap Matchmaker rewrote launch URL to the closest server JobId.");
                break;
            case MatchmakerRewriteOutcome.Unchanged:
                App.Logger.WriteLine("Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync", "Voidstrap Matchmaker did not rewrite launch URL.");
                break;
            case MatchmakerRewriteOutcome.Cancelled:
                App.Logger.WriteLine("Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync", "Voidstrap Matchmaker timed out, launching with the original URL.");
                break;
            case MatchmakerRewriteOutcome.Failed:
                App.Logger.WriteLine("Bootstrapper::MaybeApplyVoidstrapMatchmakerAsync", "Voidstrap Matchmaker error, launching with the original URL: " + result.Error);
                break;
        }
        return result.Target;
    }

    internal static async Task<MatchmakerDispatchTarget> ResolveMatchmakerDispatchTargetAsync(
        string originalTarget,
        Func<CancellationToken, Task<string?>> rewrite,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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
        ProcessStartInfo processStartInfo = new()
        {
            FileName = AppData.ExecutablePath,
            WorkingDirectory = AppData.Directory,
            UseShellExecute = false
        };
		if (RobloxDeeplink.TryExtract(args, out Uri? deeplink) && deeplink != null)
			processStartInfo.ArgumentList.Add(deeplink.AbsoluteUri);
		else if (_launchMode == LaunchMode.Studio && TryAddStudioLocalPlaceArguments(processStartInfo, args))
		{
		}
		else if (!string.IsNullOrWhiteSpace(args))
			throw new InvalidOperationException("Roblox launch arguments were invalid");
        if (_launchMode == LaunchMode.Player && ShouldRunAsAdmin())
        {
            processStartInfo.Verb = "runas";
            processStartInfo.UseShellExecute = true;
        }
        if (App.Settings.Prop.BypassEmulationOverhead)
        {
            Voidstrap.Utility.EmulationBypassService.ApplyCompatLayerBypass(AppData.ExecutablePath);
            Voidstrap.Utility.EmulationBypassService.ApplyBypassEnvironment(processStartInfo);
        }
        else
        {
            Voidstrap.Utility.EmulationBypassService.RestoreCompatLayers();
            Voidstrap.Utility.EmulationBypassService.RemoveBypassEnvironment(processStartInfo);
        }
        return processStartInfo;
    }

	private static bool TryAddStudioLocalPlaceArguments(ProcessStartInfo startInfo, string arguments)
	{
		const string prefix = "-task EditFile -localPlaceFile \"";
		if (!arguments.StartsWith(prefix, StringComparison.Ordinal) || !arguments.EndsWith('"'))
			return false;
		string localPlaceFile = arguments[prefix.Length..^1];
		if (string.IsNullOrWhiteSpace(localPlaceFile) || localPlaceFile.Length > 32768 || localPlaceFile.Contains('"') || localPlaceFile.Any(char.IsControl))
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
        DateTime launchStartedUtc = DateTime.UtcNow;
		string clientSettingsPath = Path.Combine(AppData.Directory, "ClientSettings", "ClientAppSettings.json");
		string blockedFlagsPath = Path.Combine(Paths.Cache, "BlockedFastFlags.txt");
		TrySuppressKnownBlockedFastFlags(clientSettingsPath, blockedFlagsPath);
		(string? logFile, bool timedOut) = await LaunchAndWaitForLogFileAsync(rbxLogDir, existingLogs, launchStartedUtc, startInfo, ct).ConfigureAwait(false);
		if (!timedOut || !string.IsNullOrEmpty(logFile) || ct.IsCancellationRequested)
			return logFile;
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
			App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Roblox did not become ready, retrying once without the installed FastFlag file");
			existingLogs = GetExistingLogFiles(rbxLogDir);
			launchStartedUtc = DateTime.UtcNow;
			(logFile, _) = await LaunchAndWaitForLogFileAsync(rbxLogDir, existingLogs, launchStartedUtc, startInfo, ct).ConfigureAwait(false);
			if (!string.IsNullOrEmpty(logFile))
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
			catch
			{
			}
			TerminateFailedLaunchProcess();
			return null;
		}
		finally
		{
			try
			{
				File.Delete(disabledPath);
			}
			catch
			{
			}
		}
    }

	private async Task<(string? LogFile, bool TimedOut)> LaunchAndWaitForLogFileAsync(string rbxLogDir, HashSet<string> existingLogs, DateTime launchStartedUtc, ProcessStartInfo startInfo, CancellationToken ct)
	{
		bool retainForRecovery = false;
		launchStartedUtc = DateTime.UtcNow;
		using var logWaiter = new RobloxLogWaiter(rbxLogDir, existingLogs, launchStartedUtc);
        try
        {
            _robloxProcess = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Roblox process.");
            _appPid = _robloxProcess.Id;
            _robloxLaunchUtc = launchStartedUtc;
            if (_launchMode == LaunchMode.Player)
            {
                AudioDucker.NotifyRobloxLaunched(_appPid);
            }
            RobloxProcessOptimizer.ApplyLaunchProfile(_robloxProcess);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
			return (null, false);
        }
        try
        {
            DateTime deadline = launchStartedUtc.AddSeconds(45);
            while (DateTime.UtcNow < deadline)
            {
				string? logFileName = FindNewLogFile(rbxLogDir, existingLogs, launchStartedUtc);
				if (!string.IsNullOrEmpty(logFileName))
					return (logFileName, false);

				Task delayTask = Task.Delay(500, ct);
				Task completedTask = await Task.WhenAny(logWaiter.Task, delayTask).ConfigureAwait(false);
				if (completedTask == logWaiter.Task)
					return (await logWaiter.Task.ConfigureAwait(false), false);
				if (ct.IsCancellationRequested)
					return (null, false);

                try
                {
                    if (_robloxProcess?.HasExited == true)
                    {
						return (FindNewLogFile(rbxLogDir, existingLogs, launchStartedUtc), false);
                    }
                }
                catch
                {
					return (null, false);
                }
            }
			string? finalLog = FindNewLogFile(rbxLogDir, existingLogs, launchStartedUtc);
			retainForRecovery = string.IsNullOrEmpty(finalLog);
			return (finalLog, retainForRecovery);
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
					App.Logger.WriteLine("Bootstrapper::WaitForLogFile", "Skipped an unchanged FastFlag file that previously prevented Roblox from starting");
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
				if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Undefined or JsonValueKind.Null)
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
            return Directory.EnumerateFiles(logDirectory, "*.log")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? FindNewLogFile(string logDirectory, HashSet<string> existingLogs, DateTime launchStartedUtc)
    {
        try
        {
            return Directory.EnumerateFiles(logDirectory, "*.log")
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

    private async Task LaunchCustomIntegrations(string logIdent, bool preLaunch, CancellationToken ct)
    {
        IEnumerable<CustomIntegration> enumerable = App.Settings.Prop?.CustomIntegrations;
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
                    UseShellExecute = true
                };
                if (integration.RunMinimized && Voidstrap.Utility.Platform.IsWindows)
                {
                    processStartInfo.WindowStyle = ProcessWindowStyle.Minimized;
                }
                if (integration.RunAsAdmin && Voidstrap.Utility.Platform.IsWindows)
                {
                    processStartInfo.Verb = "runas";
                }
                using Process process = Process.Start(processStartInfo);
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

    private async Task DisableCrashHandlerIfNeeded(string logIdent, CancellationToken ct)
    {
        if (!App.Settings.Prop.DisableCrash)
        {
            return;
        }
        await Task.Delay(800, ct).ConfigureAwait(continueOnCapturedContext: false);
        Process[] processesByName = Process.GetProcessesByName("RobloxCrashHandler");
        foreach (Process process in processesByName)
        {
            try
            {
                if (!process.HasExited && IsFromCurrentRobloxLaunch(process))
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1000))
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    App.Logger.WriteLine(logIdent, $"CrashHandler {process.Id} terminated.");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(logIdent, $"CrashHandler kill error {process.Id}: {ex.Message}");
            }
            finally
            {
                process.Dispose();
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
        try
        {
            System.Windows.Application current = System.Windows.Application.Current;
            current?.Dispatcher.Invoke((Action)delegate
            {
                System.Windows.Application.Current?.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            });
        }
        catch
        {
        }
        try
        {
            Dialog?.CloseBootstrapper();
        }
        catch
        {
        }
        try
        {
            App.Logger.WriteLine("Bootstrapper::RunStudioSessionAsync", "Keeping Voidstrap in the background for the Studio session");
            try
            {
                Voidstrap.Integrations.Studio.StudioIntegration.Start();
            }
            catch
            {
            }
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
        }
        catch (OperationCanceledException)
        {
        }
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
            catch
            {
            }
        }
    }

    private static bool StudioRunning()
    {
        Process[] procs = Process.GetProcessesByName("RobloxStudioBeta");
        bool any = procs.Length > 0;
        foreach (Process p in procs)
        {
            try
            {
                p.Dispose();
            }
            catch
            {
            }
        }
        return any;
    }

    private Task LaunchWatcherIfNeeded(string logFileName, CancellationToken ct)
    {
        bool flag = !string.IsNullOrEmpty(_launchStatusFile);
        bool shouldOptimize = RobloxProcessOptimizer.ShouldRun(App.Settings?.Prop);
        if (!((App.Settings?.Prop.EnableActivityTracking ?? false) || flag || shouldOptimize) && !(App.LaunchSettings.TestModeFlag?.Active ?? false))
        {
            return Task.CompletedTask;
        }
        WatcherData watcherData = new()
        {
            ProcessId = _appPid,
            LogFile = logFileName,
            AutoclosePids = [.. _integrationAutoclosePids]
        };
        if (AssetProxyServer.IsRequired && _launchMode == LaunchMode.Player)
        {
            try
            {
                System.Windows.Application current = System.Windows.Application.Current;
                if (current != null)
                {
                    ((DispatcherObject)current).Dispatcher.Invoke((Action)delegate
                    {
                        _inProcessWatcher = new Watcher(watcherData);
                    });
                }
                if (_inProcessWatcher != null)
                {
                    Task.Run(() => _inProcessWatcher.Run(), ct).ContinueWith(delegate
                    {
                        try
                        {
                            _inProcessWatcher.Dispose();
                        }
                        catch
                        {
                        }
                        Watcher.ForceShutdownAfterRobloxExit("Bootstrapper::LaunchWatcherIfNeeded");
                    }, ct);
                }
                App.Logger.WriteLine("Bootstrapper::LaunchWatcherIfNeeded", "Running the watcher in-process for AssetWarp so no second Voidstrap process is needed.");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("Bootstrapper::LaunchWatcherIfNeeded", "In-process watcher failed: " + ex.Message);
                return Task.CompletedTask;
            }
        }
        string text = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(watcherData)));
        try
        {
            ct.ThrowIfCancellationRequested();
            ProcessStartInfo watcherStartInfo = new ProcessStartInfo
            {
                FileName = Paths.Process,
                UseShellExecute = false
			};
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
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::LaunchWatcherIfNeeded", "Watcher process start failed: " + ex.Message);
        }
        return Task.CompletedTask;
    }

    private static async Task StartAssetProxyIfEnabled(CancellationToken ct)
    {
        if (!AssetProxyServer.IsRequired)
        {
            AssetProxyServer.ReconcileRuntimeState();
            return;
        }
        if (!ProcessElevation.IsAdministrator())
        {
            App.Logger.WriteLine("Bootstrapper::StartAssetProxyIfEnabled", "AssetWarp requires administrator access, continuing without AssetWarp");
            return;
        }

        Exception? finalFailure = null;
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await AssetProxyServer.StartAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                finalFailure = ex;
                AssetProxyServer.Stop();
                if (attempt < 3)
                {
                    App.Logger.WriteLine("Bootstrapper::StartAssetProxyIfEnabled", "AssetWarp startup attempt " + attempt + " failed, retrying");
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
        }

        if (finalFailure != null)
        {
            App.Logger.WriteLine("Bootstrapper::StartAssetProxyIfEnabled", "AssetWarp could not start after three attempts, continuing without AssetWarp: " + finalFailure);
        }
    }

    private bool ShouldRunAsAdmin()
    {
        foreach (RegistryKey root in WindowsRegistry.Roots)
        {
            using RegistryKey registryKey = root.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers");
            if (registryKey != null)
            {
                string obj = (string)registryKey.GetValue(AppData.ExecutablePath);
                if (obj != null && obj.Contains("RUNASADMIN", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void MigrateCompatibilityFlags()
    {
        string text = Path.Combine(AppData.VersionsRoot, AppData.State.VersionGuid, AppData.ExecutableName);
        string text2 = Path.Combine(_latestVersionDirectory, AppData.ExecutableName);
        using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers");
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
                ? Process.GetProcessesByName(ProcRobloxPlayer).ToArray()
                : Process.GetProcessesByName(ProcRobloxPlayer)
                    .Concat(Process.GetProcessesByName(ProcRobloxCrash))
                    .ToArray();
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
                    await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(continueOnCapturedContext: false);
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
            ? Process.GetProcessesByName(ProcRobloxPlayer).ToArray()
            : Process.GetProcessesByName(ProcRobloxPlayer)
                .Concat(Process.GetProcessesByName(ProcRobloxCrash))
                .ToArray();
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

    private void CleanupVersionsFolder()
    {
        string[] directories = Directory.GetDirectories(AppData.VersionsRoot);
		bool customRoot = !PathsEqual(AppData.VersionsRoot, Paths.Versions);
        foreach (string text in directories)
        {
            string fileName = Path.GetFileName(text);
			if (!IsValidVersionGuid(fileName))
			{
				continue;
            }
            if (!(fileName == App.State.Prop.Player.VersionGuid) && !(fileName == App.State.Prop.Studio.VersionGuid))
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
			StringComparison.OrdinalIgnoreCase);
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

    private static async Task WithRetryAsync(Func<Task> action, string context, int maxAttempts = 5, int baseDelayMs = 750, Func<Exception, bool>? isTransient = null, CancellationToken ct = default)
    {
        isTransient ??= ex2 => (ex2 is TaskCanceledException || ex2 is HttpRequestException || ex2 is IOException || ex2 is SocketException);
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
        await WithRetryAsync(delegate
        {
            NormalizeInstallationAttributes(path);
            Directory.Delete(path, recursive: true);
            return Task.CompletedTask;
        }, context, 5, 600, ex => (ex is IOException || ex is UnauthorizedAccessException), ct).ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task UpgradeRoblox()
    {
        CancellationToken ct = _cancelTokenSource.Token;
		string stagingDirectory = _latestVersionDirectory + ".installing";
        if (Interlocked.Exchange(ref _isInstalling, 1) == 1)
        {
            App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "Upgrade already in progress - skipping.");
            return;
        }
        try
        {
            if (!App.Settings.Prop.UpdateRoblox)
            {
                SetStatus(Strings.Bootstrapper_Status_CancelUpgrade);
                await Task.Delay(250, ct).ConfigureAwait(continueOnCapturedContext: false);
                if (!Directory.Exists(_latestVersionDirectory))
                {
                    Frontend.ShowMessageBox(Strings.Bootstrapper_Dialog_NoUpgradeWithoutClient, MessageBoxImage.Exclamation);
                }
                return;
            }
            SetStatus(string.IsNullOrEmpty(AppData.State.VersionGuid) ? "Installing Packages" : "Upgrading Packages");
			ApplyOptimizedDownloadDefaults();
            Directory.CreateDirectory(Paths.Base);
            Directory.CreateDirectory(Paths.Downloads);
            Directory.CreateDirectory(AppData.VersionsRoot);
            try
            {
                foreach (string staleFile in Directory.GetFiles(Paths.Downloads))
                {
                    if ((staleFile.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || staleFile.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(staleFile)).TotalHours >= 24.0)
                    {
                        File.Delete(staleFile);
                        App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "Deleted stale partial download older than 24 hours: " + Path.GetFileName(staleFile));
                    }
                }
            }
            catch
            {
            }
            List<string?> cachedHashes = (Directory.Exists(Paths.Downloads) ? [.. Directory.GetFiles(Paths.Downloads).Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName)] : new List<string>());
            if (!IsStudioLaunch)
            {
                await StopRobloxPlayersAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
            }
			if (Directory.Exists(stagingDirectory))
            {
				await SafeDeleteDirectoryAsync(stagingDirectory, "Bootstrapper::UpgradeRoblox", ct).ConfigureAwait(continueOnCapturedContext: false);
            }
			Directory.CreateDirectory(stagingDirectory);
			_packageExtractionDirectory = stagingDirectory;
            if (_versionPackageManifest == null || !_versionPackageManifest.Any())
            {
                throw new Exception("Package manifest is null or empty.");
            }
            long totalPacked = ((IEnumerable<Package>)_versionPackageManifest).Sum((Func<Package, long>)(p => p.PackedSize));
            long num = ((IEnumerable<Package>)_versionPackageManifest).Sum((Func<Package, long>)(p => p.Size));
			long packedHeadroom = checked((long)Math.Ceiling(totalPacked * 1.1));
			long expandedHeadroom = checked((long)Math.Ceiling(num * 1.1));
			string downloadsRoot = Path.GetPathRoot(Path.GetFullPath(Paths.Downloads)) ?? Paths.Downloads;
			string versionsVolume = Path.GetPathRoot(Path.GetFullPath(AppData.VersionsRoot)) ?? AppData.VersionsRoot;
			bool sameVolume = string.Equals(downloadsRoot, versionsVolume, StringComparison.OrdinalIgnoreCase);
			bool insufficientSpace = sameVolume
				? Filesystem.GetFreeDiskSpace(Paths.Downloads) < packedHeadroom + expandedHeadroom
				: Filesystem.GetFreeDiskSpace(Paths.Downloads) < packedHeadroom || Filesystem.GetFreeDiskSpace(AppData.VersionsRoot) < expandedHeadroom;
            if (insufficientSpace)
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_NotEnoughSpace, MessageBoxImage.Hand);
                App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                return;
            }
            if (Dialog != null)
            {
                SetProgressStyle(ProgressBarStyle.Continuous);
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Normal;
                SetProgressMaximum(10000);
                _progressIncrement = 10000.0 / (double)Math.Max(1L, totalPacked);
                _taskbarProgressMaximum = ((Dialog is WinFormsDialogBase) ? 100.0 : 1.0);
                _taskbarProgressIncrement = _taskbarProgressMaximum / (double)Math.Max(1L, totalPacked);
            }
            int totalPackages = _versionPackageManifest.Count;
            int packagesComplete = 0;
            int failedPackages = 0;
            Interlocked.Exchange(ref _totalDownloadedBytes, 0L);
			using SemaphoreSlim downloadThrottler = new(DownloadConfiguration.NormalizeConcurrent(App.Settings.Prop.MaxConcurrentDownloads));
			using SemaphoreSlim extractionThrottler = new(ExtractionConcurrency);
            using CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task progressTask = RunDownloadProgressLoopAsync(totalPacked, totalPackages, () => Volatile.Read(in packagesComplete), progressCts.Token);
            try
            {
                await Task.WhenAll(((IEnumerable<Package>)_versionPackageManifest).Select((Func<Package, Task>)async delegate (Package package)
                    {
                        try
                        {
                            await downloadThrottler.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
                            try
                            {
                                await DownloadPackage(package).ConfigureAwait(continueOnCapturedContext: false);
                            }
                            finally
                            {
                                downloadThrottler.Release();
                            }
                            await extractionThrottler.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
                            try
                            {
                                await WithRetryAsync(delegate
                                {
                                    ExtractPackage(package);
                                    return Task.CompletedTask;
                                }, "Bootstrapper::UpgradeRoblox::Extract(" + package.Name + ")", 4, 800, ex7 => (ex7 is IOException || ex7 is UnauthorizedAccessException), ct).ConfigureAwait(continueOnCapturedContext: false);
                            }
                            finally
                            {
                                extractionThrottler.Release();
                            }
                            Interlocked.Increment(ref packagesComplete);
                            UpdateProgressBar();
                        }
						catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception value)
                        {
                            Interlocked.Increment(ref failedPackages);
                            App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", $"Package {package.Name} failed: {value}");
                        }
                    }).ToList()).ConfigureAwait(continueOnCapturedContext: false);
            }
            finally
            {
                progressCts.Cancel();
                try
                {
                    await progressTask.ConfigureAwait(continueOnCapturedContext: false);
                }
                catch
                {
                }
            }
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (failedPackages > 0)
            {
                throw new Exception($"{failedPackages} package(s) failed during upgrade.");
            }
            if (Dialog != null)
            {
                SetProgressStyle(ProgressBarStyle.Marquee);
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Indeterminate;
                SetStatus(Strings.Bootstrapper_Status_Configuring);
            }
			await WithRetryAsync(() => File.WriteAllTextAsync(Path.Combine(stagingDirectory, "AppSettings.xml"), AppSettingsXml, ct), "Bootstrapper::UpgradeRoblox::Write(AppSettings.xml)", 3, 600, ex6 => (ex6 is IOException || ex6 is UnauthorizedAccessException), ct).ConfigureAwait(continueOnCapturedContext: false);
			ValidateStagedInstallation(stagingDirectory);
			File.WriteAllText(Path.Combine(stagingDirectory, VersionOwnershipFileName), AppData.BinaryType, new UTF8Encoding(false));
			if (!PathsEqual(AppData.VersionsRoot, Paths.Versions) && Directory.Exists(_latestVersionDirectory) &&
				!string.Equals(AppData.State.VersionGuid, _latestVersionGuid, StringComparison.Ordinal) && !IsOwnedVersionDirectory(_latestVersionDirectory))
			{
				throw new InvalidOperationException("The custom install location contains an unmanaged version directory");
			}
			await CommitInstallationAsync(stagingDirectory, _latestVersionDirectory, AppData.VersionsRoot, ct).ConfigureAwait(continueOnCapturedContext: false);
			_packageExtractionDirectory = null;
			VerifyCommittedInstallation();
            try
            {
                MigrateCompatibilityFlags();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "MigrateCompatibilityFlags: " + ex.Message);
            }
            AppData.State.VersionGuid = _latestVersionGuid;
            AppData.State.CriticalFiles = [.. _stagedCriticalFiles];
            AppData.State.PackageHashes.Clear();
            foreach (Package item in _versionPackageManifest)
            {
                AppData.State.PackageHashes[item.Name] = item.Signature;
            }
            AppData.State.ModManifest.Clear();
            AppData.State.ModApplyCache.Clear();
            AppData.State.ManagedModManifest.Clear();
            AppData.State.ModApplyVersion = string.Empty;
            ClearForcedReinstall();
            try
            {
                CleanupVersionsFolder();
            }
            catch (Exception ex2)
            {
                App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "CleanupVersionsFolder: " + ex2.Message);
            }
            IEnumerable<string> enumerable = App.State.Prop.Player?.PackageHashes.Values;
            IEnumerable<string> first = enumerable ?? [];
            enumerable = App.State.Prop.Studio?.PackageHashes.Values;
            HashSet<string> allHashes = [.. first, .. enumerable ?? []];
            await Task.WhenAll(from h in cachedHashes
                               where h != null && !allHashes.Contains(h)
                               select WithRetryAsync(delegate
                               {
                                   string path = Path.Combine(Paths.Downloads, h);
                                   if (File.Exists(path))
                                   {
                                       File.Delete(path);
                                   }
                                   return Task.CompletedTask;
                               }, "Bootstrapper::UpgradeRoblox::DeleteCache(" + h + ")", 3, 500, ex6 => (ex6 is IOException || ex6 is UnauthorizedAccessException), ct)).ConfigureAwait(continueOnCapturedContext: false);
            try
            {
				long installedBytes = _versionPackageManifest.Sum(package => (long)package.Size + package.PackedSize);
				long installedKilobytes = installedBytes / 1024L;
				AppData.State.Size = (int)Math.Clamp(installedKilobytes, 0L, int.MaxValue);
				long combinedKilobytes = (long)(App.State.Prop.Player?.Size ?? 0) + (App.State.Prop.Studio?.Size ?? 0);
                using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap");
				registryKey?.SetValueSafe("EstimatedSize", (int)Math.Clamp(combinedKilobytes, 0L, int.MaxValue));
            }
            catch (Exception ex3)
            {
                App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "Register size failed: " + ex3.Message);
            }
            App.State.Save();
        }
        catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
        {
            App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "Upgrade cancelled.");
        }
        catch (Exception ex5)
        {
            App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", $"Upgrade error: {ex5}");
            string message = IsInstallationAccessError(ex5)
                ? "Roblox could not be installed because Windows is still using part of the installation. No incomplete replacement was activated. Close Roblox and programs that scan or modify Roblox files, then try again."
                : "Roblox could not be installed:\n" + ex5.Message;
            Frontend.ShowMessageBox(message, MessageBoxImage.Hand);
            _cancelTokenSource.Cancel();
        }
        finally
        {
			_packageExtractionDirectory = null;
            Interlocked.Exchange(ref _isInstalling, 0);
            _installationStopped.TrySetResult();
        }
    }

	private static void ApplyOptimizedDownloadDefaults()
	{
		if (DownloadConfiguration.Normalize(App.Settings.Prop))
			App.Settings.SaveDeferred();
	}

	private static async Task CommitInstallationAsync(string stagingDirectory, string targetDirectory, string installationRoot, CancellationToken ct)
	{
		string stagingPath = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar);
		string targetPath = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
		string versionsRoot = Path.GetFullPath(installationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		if (!targetPath.StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(stagingPath), Path.GetDirectoryName(targetPath), StringComparison.OrdinalIgnoreCase))
		{
			throw new InvalidOperationException("The Roblox installation paths are not valid");
		}
		if (!Directory.Exists(stagingPath))
		{
			throw new DirectoryNotFoundException("The prepared Roblox installation could not be found");
		}
		string backupDirectory = targetPath + ".previous." + Guid.NewGuid().ToString("N");
		bool movedExisting = false;
		try
		{
			if (Directory.Exists(targetPath))
			{
				await MoveDirectoryWithRetryAsync(targetPath, backupDirectory, "Bootstrapper::CommitInstallation::Backup", ct).ConfigureAwait(continueOnCapturedContext: false);
				movedExisting = true;
			}
			await MoveDirectoryWithRetryAsync(stagingPath, targetPath, "Bootstrapper::CommitInstallation::Activate", ct).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception commitException)
		{
			if (movedExisting && !Directory.Exists(targetPath) && Directory.Exists(backupDirectory))
			{
				try
				{
					await MoveDirectoryWithRetryAsync(backupDirectory, targetPath, "Bootstrapper::CommitInstallation::Restore", CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
				}
				catch (Exception rollbackException)
				{
					throw new AggregateException("The Roblox installation could not be activated or restored", commitException, rollbackException);
				}
			}
			throw;
		}
		if (Directory.Exists(backupDirectory))
		{
			try
			{
				await SafeDeleteDirectoryAsync(backupDirectory, "Bootstrapper::CommitInstallation::Cleanup", CancellationToken.None).ConfigureAwait(continueOnCapturedContext: false);
			}
			catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
			{
				App.Logger.WriteLine("Bootstrapper::CommitInstallation", "The previous installation will be cleaned later: " + ex.Message);
			}
		}
	}

	private static async Task MoveDirectoryWithRetryAsync(string source, string destination, string context, CancellationToken ct)
	{
		await WithRetryAsync(delegate
		{
			NormalizeInstallationAttributes(source);
			return Task.CompletedTask;
		}, context + "::Prepare", 3, 500, IsInstallationAccessError, ct).ConfigureAwait(continueOnCapturedContext: false);
		await WithRetryAsync(delegate
		{
			if (!Directory.Exists(source) && Directory.Exists(destination))
			{
				return Task.CompletedTask;
			}
			Directory.Move(source, destination);
			return Task.CompletedTask;
		}, context, 8, 500, IsInstallationAccessError, ct).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static void NormalizeInstallationAttributes(string root)
	{
		if (!Directory.Exists(root))
		{
			return;
		}
		Stack<string> pending = new();
		pending.Push(root);
		while (pending.Count > 0)
		{
			string directory = pending.Pop();
			foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
			{
				FileAttributes attributes = File.GetAttributes(entry);
				if ((attributes & FileAttributes.ReparsePoint) != 0)
				{
					continue;
				}
				if ((attributes & FileAttributes.Directory) != 0)
				{
					File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
					pending.Push(entry);
				}
				else if ((attributes & FileAttributes.ReadOnly) != 0)
				{
					File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
				}
			}
			FileAttributes directoryAttributes = File.GetAttributes(directory);
			if ((directoryAttributes & FileAttributes.ReadOnly) != 0)
			{
				File.SetAttributes(directory, directoryAttributes & ~FileAttributes.ReadOnly);
			}
		}
	}

	private static bool IsInstallationAccessError(Exception ex)
	{
		if (ex is UnauthorizedAccessException)
		{
			return true;
		}
		if (ex is IOException ioException)
		{
			int errorCode = ioException.HResult & 0xFFFF;
			return errorCode is 5 or 32 or 33 or 145 or 183;
		}
		if (ex is AggregateException aggregate)
		{
			return aggregate.InnerExceptions.Any(IsInstallationAccessError);
		}
		return ex.InnerException != null && IsInstallationAccessError(ex.InnerException);
	}

	private void ValidateStagedInstallation(string stagingDirectory)
	{
		string executable = Path.Combine(stagingDirectory, IsStudioLaunch ? AppData.ExecutableName : ProcRobloxExe);
		FileInfo executableInfo = new(executable);
		if (!executableInfo.Exists || executableInfo.Length < 1048576)
		{
			throw new InvalidDataException("The staged Roblox executable is missing or incomplete");
		}
		if (!File.Exists(Path.Combine(stagingDirectory, "AppSettings.xml")))
		{
			throw new InvalidDataException("The staged Roblox settings file is missing");
		}
		_stagedCriticalFiles = ScanCriticalFiles(stagingDirectory);
		foreach (string name in _stagedCriticalFiles)
		{
			FileInfo info = new(Path.Combine(stagingDirectory, name));
			if (!info.Exists || info.Length == 0)
			{
				throw new InvalidDataException("The staged Roblox client file " + name + " is missing or empty");
			}
		}
	}

	private List<string> _stagedCriticalFiles = [];

	private void VerifyCommittedInstallation()
	{
		foreach (string name in _stagedCriticalFiles)
		{
			if (File.Exists(Path.Combine(_latestVersionDirectory, name)))
			{
				continue;
			}
			App.Logger.WriteLine("Bootstrapper::VerifyCommittedInstallation", "Missing after commit: " + name);
			throw new InvalidDataException(
				"Roblox was installed but " + name + " is missing. Your antivirus most likely removed it. "
				+ "Add the Roblox versions folder to your antivirus exclusions, then launch again.");
		}
	}

    private async Task DownloadPackage(Package package)
    {
        string logIdent = "Bootstrapper::DownloadPackage." + package.Name;
        bool updating = !string.IsNullOrEmpty(AppData.State.VersionGuid);
        CancellationToken ct = _cancelTokenSource.Token;
		ct.ThrowIfCancellationRequested();
		PackageProgressTracker progress = new(this);
        Directory.CreateDirectory(Paths.Downloads);
        IReadOnlyList<string> packageUrls = Deployment.GetLocations("/" + _latestVersionGuid + "-" + package.Name, _deploymentChannel);
        if (packageUrls.Count == 0)
        {
            throw new InvalidOperationException("No download location is available for package " + package.Name + ".");
        }
        int urlIndex = 0;
        string packageUrl = packageUrls[0];
        string text = Path.Combine(Paths.LocalAppData, "Roblox", "Downloads", package.Signature);
        if (File.Exists(package.DownloadPath))
        {
            if (MD5Hash.FromFile(package.DownloadPath) == package.Signature)
            {
                App.Logger.WriteLine(logIdent, "Already downloaded, skipping.");
				progress.Set(package.PackedSize);
                UpdateProgressBar();
                return;
            }
            File.Delete(package.DownloadPath);
        }
        else if (File.Exists(text))
        {
            try
            {
                if (MD5Hash.FromFile(text) != package.Signature)
                {
                    throw new ChecksumFailedException("Roblox cache package checksum does not match the manifest.");
                }
                File.Copy(text, package.DownloadPath, overwrite: true);
				progress.Set(package.PackedSize);
                UpdateProgressBar();
                return;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(logIdent, "Roblox cache copy failed: " + ex.Message);
            }
        }
		int maxParallelSegments = DownloadConfiguration.NormalizeSegments(App.Settings.Prop.MaxDownloadSegments);
		int bufferSize = DownloadConfiguration.NormalizeBuffer(App.Settings.Prop.DownloadBufferKb) * 1024;
        long minMultipartSize = 3L * 1024L * 1024L;
        string tempFile = package.DownloadPath + ".part";
        string metaFile = tempFile + ".meta";
		bool completed = false;
		for (int attempt = 1; attempt <= MaxPackageDownloadAttempts; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                long resumeOffset = 0L;
                if (File.Exists(tempFile) && !File.Exists(metaFile))
                {
                    try
                    {
                        resumeOffset = new FileInfo(tempFile).Length;
						if (resumeOffset == package.PackedSize)
						{
							if (MD5Hash.FromFile(tempFile).Equals(package.Signature, StringComparison.OrdinalIgnoreCase))
							{
								File.Move(tempFile, package.DownloadPath, overwrite: true);
								progress.Set(package.PackedSize);
								completed = true;
								break;
							}
							File.Delete(tempFile);
							resumeOffset = 0;
						}
						else if (resumeOffset > package.PackedSize)
						{
							File.Delete(tempFile);
							resumeOffset = 0;
						}
                    }
                    catch
                    {
                        resumeOffset = 0L;
                    }
                }
				using HttpRequestMessage initialRequest = new(HttpMethod.Get, packageUrl)
				{
					Version = HttpVersion.Version20,
					VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
				};
                if (resumeOffset > 0)
                {
                    initialRequest.Headers.Range = new RangeHeaderValue(resumeOffset, null);
                }
				using HttpResponseMessage response = await SendPackageRequestAsync(initialRequest, ct).ConfigureAwait(continueOnCapturedContext: false);
                if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
                {
					ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
					if (range?.From != resumeOffset || range.To == null || range.Length != package.PackedSize)
					{
						File.Delete(tempFile);
						progress.Set(0);
						throw new IOException("Resume response range does not match the requested package");
					}
                    App.Logger.WriteLine(logIdent, $"Resuming single stream download at byte {resumeOffset:N0}");
					progress.Set(resumeOffset);
					await DownloadSingleThreadAsync(response, tempFile, bufferSize, logIdent, ct, package.PackedSize - resumeOffset, progress, append: true).ConfigureAwait(continueOnCapturedContext: false);
                    string resumedHash = MD5Hash.FromFile(tempFile);
                    if (!resumedHash.Equals(package.Signature, StringComparison.OrdinalIgnoreCase))
                    {
                        App.Logger.WriteLine(logIdent, "Resumed file failed checksum, restarting download from scratch");
						progress.Set(0);
                        File.Delete(tempFile);
                        continue;
                    }
                    File.Move(tempFile, package.DownloadPath, overwrite: true);
                    UpdateProgressBar();
					completed = true;
                    break;
                }
                if (resumeOffset > 0)
                {
                    try
                    {
                        File.Delete(tempFile);
						progress.Set(0);
                    }
                    catch
                    {
                    }
                }
                if (!response.IsSuccessStatusCode)
                {
                    HttpStatusCode statusCode = response.StatusCode;
                    App.Logger.WriteLine(logIdent, $"Package '{package.Name}' returned HTTP {(int)statusCode} ({statusCode}) from {packageUrl} (attempt {attempt})");
                    if ((statusCode == HttpStatusCode.Forbidden || statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.NotFound) && urlIndex + 1 < packageUrls.Count)
                    {
                        urlIndex++;
                        packageUrl = packageUrls[urlIndex];
                        App.Logger.WriteLine(logIdent, $"Endpoint returned {(int)statusCode}, trying mirror {urlIndex + 1} of {packageUrls.Count}: {packageUrl}");
                        attempt--;
                        continue;
                    }
                    throw new HttpRequestException($"Package '{package.Name}' returned HTTP {(int)statusCode} ({statusCode}).", null, statusCode);
                }
                long? contentLength = response.Content.Headers.ContentLength;
				if (!contentLength.HasValue || contentLength.Value != package.PackedSize || contentLength.Value < minMultipartSize || !(response.Headers.AcceptRanges?.Contains("bytes") ?? false))
                {
					progress.Set(0);
					await DownloadSingleThreadAsync(response, tempFile, bufferSize, logIdent, ct, package.PackedSize, progress).ConfigureAwait(continueOnCapturedContext: false);
                }
                else
                {
                    response.Dispose();
					await DownloadMultipartAsync(packageUrls, urlIndex, tempFile, contentLength.Value, bufferSize, maxParallelSegments, updating, logIdent, progress, ct).ConfigureAwait(continueOnCapturedContext: false);
                }
                string text2 = MD5Hash.FromFile(tempFile);
                if (!text2.Equals(package.Signature, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ChecksumFailedException($"Checksum mismatch for {package.Name}: expected {package.Signature}, got {text2}");
                }
                File.Move(tempFile, package.DownloadPath, overwrite: true);
                UpdateProgressBar();
				completed = true;
                break;
            }
            catch (ChecksumFailedException exception)
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch
                {
                }
                try
                {
                    if (File.Exists(metaFile))
                    {
                        File.Delete(metaFile);
                    }
                }
                catch
                {
                }
				App.Logger.WriteLine(logIdent, $"Attempt {attempt}/{MaxPackageDownloadAttempts}: {exception.Message}");
				if (attempt < MaxPackageDownloadAttempts)
                {
					urlIndex = (urlIndex + 1) % packageUrls.Count;
					packageUrl = packageUrls[urlIndex];
					progress.Set(0);
					await Task.Delay(GetDownloadRetryDelay(attempt), ct).ConfigureAwait(continueOnCapturedContext: false);
                    continue;
                }
				throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                App.Logger.WriteLine(logIdent, "Download cancelled, keeping partial file for resume");
				throw;
            }
            catch (Exception ex3)
            {
                if (ex3 is AggregateException ex4)
                {
                    ex3 = ex4.Flatten().InnerException ?? ex4;
                }
				App.Logger.WriteLine(logIdent, $"Attempt {attempt}/{MaxPackageDownloadAttempts}: {ex3.Message}");
				if (attempt == MaxPackageDownloadAttempts)
                {
                    throw;
                }
				urlIndex = (urlIndex + 1) % packageUrls.Count;
				packageUrl = packageUrls[urlIndex];
				await Task.Delay(GetDownloadRetryDelay(attempt), ct).ConfigureAwait(continueOnCapturedContext: false);
            }
        }
		ct.ThrowIfCancellationRequested();
		if (!completed)
		{
			throw new IOException("Package download did not complete: " + package.Name);
		}
    }

	private static TimeSpan GetDownloadRetryDelay(int attempt)
	{
		int milliseconds = Math.Min(250 * (1 << Math.Min(attempt - 1, 4)), 4000);
		return TimeSpan.FromMilliseconds(milliseconds + Random.Shared.Next(50, 251));
	}

	private static async Task<HttpResponseMessage> SendPackageRequestAsync(HttpRequestMessage request, CancellationToken ct)
	{
		using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(TimeSpan.FromSeconds(20));
		try
		{
			return await RobloxPackageClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (!ct.IsCancellationRequested)
		{
			throw new IOException("Package server did not return response headers within 20 seconds");
		}
	}

	private static async Task<string> ReadTextBoundedAsync(HttpContent content, int maximumBytes, CancellationToken ct)
	{
		if (content.Headers.ContentLength is long contentLength && (contentLength < 0 || contentLength > maximumBytes))
		{
			throw new InvalidDataException("Package manifest exceeds the maximum size");
		}
		await using Stream input = await content.ReadAsStreamAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
		using MemoryStream output = new(content.Headers.ContentLength is long length ? (int)length : 16384);
		byte[] buffer = DownloadBufferPool.Rent(65536);
		try
		{
			while (true)
			{
				int read = await ReadWithStallTimeoutAsync(input, buffer.AsMemory(0, 65536), ct).ConfigureAwait(continueOnCapturedContext: false);
				if (read == 0)
				{
					return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
				}
				if (output.Length + read > maximumBytes)
				{
					throw new InvalidDataException("Package manifest exceeds the maximum size");
				}
				output.Write(buffer, 0, read);
			}
		}
		finally
		{
			DownloadBufferPool.Return(buffer);
		}
	}

	private const int DownloadStallTimeoutMs = 20000;

    private static async ValueTask<int> ReadWithStallTimeoutAsync(Stream net, Memory<byte> buffer, CancellationToken token)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(DownloadStallTimeoutMs);
        try
        {
            return await net.ReadAsync(buffer, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException($"Download stalled: no data received for {DownloadStallTimeoutMs / 1000} seconds.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        double num = bytes;
        if (num >= 1073741824.0)
        {
            return $"{num / 1073741824.0:0.00} GB";
        }
        if (num >= 1048576.0)
        {
            return $"{num / 1048576.0:0.0} MB";
        }
        if (num >= 1024.0)
        {
            return $"{num / 1024.0:0} KB";
        }
        return $"{Math.Max(0L, bytes)} B";
    }

	private async Task DownloadSingleThreadAsync(HttpResponseMessage response, string tempFile, int bufferSize, string logIdent, CancellationToken token, long expectedLength, PackageProgressTracker progress, bool append = false)
    {
        await using Stream net = await response.Content.ReadAsStreamAsync(token);
        await using FileStream file = new(tempFile, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buf = DownloadBufferPool.Rent(bufferSize);
        long total = 0L;
        try
        {
            while (true)
            {
                int num;
                int read = (num = await ReadWithStallTimeoutAsync(net, buf.AsMemory(0, bufferSize), token));
                if (num <= 0)
                {
                    break;
                }
				if (expectedLength > 0 && total + read > expectedLength)
				{
					throw new InvalidDataException("Package response is larger than the manifest size");
				}
                await file.WriteAsync(buf.AsMemory(0, read), token);
                total += read;
				progress.Add(read);
            }
			if (expectedLength > 0 && total != expectedLength)
			{
				throw new EndOfStreamException("Package response ended before the manifest size was reached");
			}
			await file.FlushAsync(token).ConfigureAwait(continueOnCapturedContext: false);
            App.Logger.WriteLine(logIdent, $"Downloaded {total:N0} bytes (single-thread)");
        }
        finally
        {
            DownloadBufferPool.Return(buf);
        }
    }

	private sealed class PackageProgressTracker(Bootstrapper owner)
	{
		private long _credited;

		public void Add(long bytes)
		{
			if (bytes <= 0)
			{
				return;
			}
			Interlocked.Add(ref _credited, bytes);
			Interlocked.Add(ref owner._totalDownloadedBytes, bytes);
		}

		public void Set(long bytes)
		{
			long normalized = Math.Max(0, bytes);
			long previous = Interlocked.Exchange(ref _credited, normalized);
			Interlocked.Add(ref owner._totalDownloadedBytes, normalized - previous);
		}
	}

    private sealed class MultipartDownloadState
    {
        public long Length { get; set; }

        public long SegSize { get; set; }

        public bool[] Done { get; set; } = [];
    }

	private async Task DownloadMultipartAsync(IReadOnlyList<string> urls, int initialUrlIndex, string tempFile, long contentLength, int bufferSize, int maxSegments, bool updating, string logIdent, PackageProgressTracker progress, CancellationToken token)
    {
        int segs = (int)Math.Min(maxSegments, Math.Max(1L, contentLength / 1572864));
        if (segs <= 1)
        {
			using HttpRequestMessage request = new(HttpMethod.Get, urls[initialUrlIndex])
			{
				Version = HttpVersion.Version20,
				VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
			};
			using HttpResponseMessage r = await SendPackageRequestAsync(request, token).ConfigureAwait(continueOnCapturedContext: false);
			r.EnsureSuccessStatusCode();
			await DownloadSingleThreadAsync(r, tempFile, bufferSize, logIdent, token, contentLength, progress).ConfigureAwait(continueOnCapturedContext: false);
            return;
        }
        long segSize = contentLength / segs;
        string metaFile = tempFile + ".meta";
        MultipartDownloadState state = null;
        if (File.Exists(metaFile) && File.Exists(tempFile))
        {
            try
            {
				state = JsonFile.Deserialize<MultipartDownloadState>(metaFile, JsonOptions.Tolerant, 1048576);
            }
            catch
            {
                state = null;
            }
			if (state != null && (state.Length != contentLength || state.Done == null || state.Done.Length == 0 || state.Done.Length > maxSegments || state.SegSize != contentLength / state.Done.Length || new FileInfo(tempFile).Length != contentLength))
            {
                state = null;
            }
        }
        bool resuming = state != null;
        if (resuming)
        {
            segs = state.Done.Length;
            segSize = state.SegSize;
            long doneBytes = 0L;
            for (int s = 0; s < segs; s++)
            {
                if (state.Done[s])
                {
                    doneBytes += ((s == segs - 1) ? (contentLength - s * segSize) : segSize);
                }
            }
			progress.Set(doneBytes);
            App.Logger.WriteLine(logIdent, $"Resuming multi part download, {doneBytes:N0} of {contentLength:N0} bytes already complete");
        }
        else
        {
            state = new MultipartDownloadState
            {
                Length = contentLength,
                SegSize = segSize,
                Done = new bool[segs]
            };
        }
        App.Logger.WriteLine(logIdent, $"Multi part: {segs} segments of ~{segSize:N0} bytes");
        object metaLock = new();
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(tempFile, resuming ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous);
        if (!resuming)
        {
            RandomAccess.SetLength(handle, contentLength);
			WriteJsonAtomic(metaFile, state);
        }
        long totalRead = 0L;
        CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationTokenSource segCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken segToken = segCts.Token;
        try
        {
            Task progressTask = (updating ? Task.Run(async delegate
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (!progressCts.Token.IsCancellationRequested)
                {
                    if (sw.ElapsedMilliseconds >= 500)
                    {
                        UpdateProgressBar();
                        sw.Restart();
                    }
                    try
                    {
                        await Task.Delay(500, progressCts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }, progressCts.Token) : Task.CompletedTask);
			List<Task> tasks = [];
            for (int i = 0; i < segs; i++)
            {
                if (state.Done[i])
                {
                    continue;
                }
                int segIndex = i;
                long start = segIndex * segSize;
                long end = ((segIndex == segs - 1) ? (contentLength - 1) : (start + segSize - 1));
				tasks.Add(DownloadSegmentAsync(urls, initialUrlIndex, handle, state, segIndex, start, end, contentLength, bufferSize, metaFile, metaLock, bytes =>
				{
					Interlocked.Add(ref totalRead, bytes);
					progress.Add(bytes);
				}, segToken));
            }
            try
            {
                try
                {
                    await Task.WhenAll(tasks);
                }
                catch
                {
                    segCts.Cancel();
                    try
                    {
                        await Task.WhenAll(tasks).ConfigureAwait(continueOnCapturedContext: false);
                    }
                    catch
                    {
                    }
                    throw;
                }
            }
            finally
            {
                progressCts.Cancel();
                try
                {
                    await progressTask;
                }
                catch
                {
                }
            }
            App.Logger.WriteLine(logIdent, $"Downloaded {totalRead:N0} bytes (multi part)");
            try
            {
                File.Delete(metaFile);
            }
            catch
            {
            }
        }
        finally
        {
            progressCts.Dispose();
            segCts.Dispose();
        }
    }

	private async Task DownloadSegmentAsync(IReadOnlyList<string> urls, int initialUrlIndex, Microsoft.Win32.SafeHandles.SafeFileHandle handle, MultipartDownloadState state, int segmentIndex, long start, long end, long contentLength, int bufferSize, string metaFile, object metaLock, Action<long> reportCompleted, CancellationToken token)
	{
		int attempts = Math.Clamp(urls.Count, 2, 4);
		Exception? failure = null;
		for (int attempt = 0; attempt < attempts; attempt++)
		{
			token.ThrowIfCancellationRequested();
			int urlIndex = (initialUrlIndex + attempt) % urls.Count;
			await _networkRequestSlots.WaitAsync(token).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				using HttpRequestMessage request = new(HttpMethod.Get, urls[urlIndex])
				{
					Version = HttpVersion.Version20,
					VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
				};
				request.Headers.Range = new RangeHeaderValue(start, end);
				using HttpResponseMessage response = await SendPackageRequestAsync(request, token).ConfigureAwait(continueOnCapturedContext: false);
				ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
				if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.To != end || range.Length != contentLength)
				{
					throw new IOException("Package server returned an invalid range response");
				}
				long expectedLength = end - start + 1;
				if (response.Content.Headers.ContentLength is long responseLength && responseLength != expectedLength)
				{
					throw new IOException("Package segment length does not match the requested range");
				}
				await using Stream net = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(continueOnCapturedContext: false);
				byte[] buffer = DownloadBufferPool.Rent(bufferSize);
				long position = start;
				try
				{
					while (position <= end)
					{
						int requested = (int)Math.Min(bufferSize, end - position + 1);
						int read = await ReadWithStallTimeoutAsync(net, buffer.AsMemory(0, requested), token).ConfigureAwait(continueOnCapturedContext: false);
						if (read == 0)
						{
							throw new EndOfStreamException("Package segment ended before the requested range was complete");
						}
						await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, token).ConfigureAwait(continueOnCapturedContext: false);
						position += read;
					}
				}
				finally
				{
					DownloadBufferPool.Return(buffer);
				}
				lock (metaLock)
				{
					state.Done[segmentIndex] = true;
					WriteJsonAtomic(metaFile, state);
				}
				reportCompleted(expectedLength);
				return;
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				failure = ex;
			}
			finally
			{
				_networkRequestSlots.Release();
			}
			if (attempt + 1 < attempts)
			{
				await Task.Delay(GetDownloadRetryDelay(attempt + 1), token).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		throw new IOException("Package segment failed after mirror retries", failure);
	}

	private static void WriteJsonAtomic<T>(string path, T value)
	{
		JsonFile.SerializeAtomic(path, value);
	}

    private void ExtractPackage(Package package, List<string>? files = null)
    {
        string valueOrDefault = AppData.PackageDirectoryMap.GetValueOrDefault(package.Name);
        if (valueOrDefault == null)
        {
			throw new InvalidDataException("Package " + package.Name + " is not present in the extraction map");
        }
        string fileFilter = null;
        if (files != null)
        {
            IEnumerable<string> values = files.Select(f => "(?i)^" + System.Text.RegularExpressions.Regex.Escape(f).Replace("\\\\", "[\\\\/]") + "$");
            fileFilter = string.Join(';', values);
        }
        App.Logger.WriteLine("Bootstrapper::ExtractPackage", "Extracting " + package.Name + "...");
		string extractionRoot = _packageExtractionDirectory ?? _latestVersionDirectory;
		new FastZip(_fastZipEvents).ExtractZip(package.DownloadPath, Path.Combine(extractionRoot, valueOrDefault), fileFilter);
        App.Logger.WriteLine("Bootstrapper::ExtractPackage", "Done: " + package.Name);
    }

    private bool ModsAllowedForThisLaunch()
    {
        Voidstrap.Enums.ModApplyTarget target = App.Settings?.Prop?.ModApplyTarget ?? Voidstrap.Enums.ModApplyTarget.Both;
        if (target == Voidstrap.Enums.ModApplyTarget.Both)
            return true;
        return IsStudioLaunch
            ? target == Voidstrap.Enums.ModApplyTarget.Studio
            : target == Voidstrap.Enums.ModApplyTarget.Player;
    }

    private async Task ApplyModifications()
    {
        SetStatus(Strings.Bootstrapper_Status_ApplyingModifications);
        File.Delete(Path.Combine(Paths.Base, "ModManifest.txt"));
        Directory.CreateDirectory(Paths.Mods);
        string[] files;
        if (IsStudioLaunch)
        {
            App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Skipping skybox (Roblox Studio).");
        }
        else if (!App.Settings.Prop.SkyBoxDataSending)
        {
            App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Skipping skybox (disabled).");
            try
            {
                InlineArray5<string> buffer = default;
                buffer[0] = Paths.Mods;
                buffer[1] = "PlatformContent";
                buffer[2] = "pc";
                buffer[3] = "textures";
                buffer[4] = "sky";
                string path = Path.Combine(buffer);
                if (Directory.Exists(path))
                {
                    files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                    for (int i = 0; i < files.Length; i++)
                    {
                        File.SetAttributes(files[i], FileAttributes.Normal);
                    }
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Skybox cleanup failed: " + ex.Message);
            }
        }
        else
        {
            try
            {
                try
                {
                    await ApplySkyboxPatchToRobloxStorageAsync(_cancelTokenSource.Token);
                }
                catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception patchException)
                {
                    App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Skybox storage patch unavailable: " + patchException.Message);
                }
                if (!App.Settings.Prop.SkyboxName.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase))
                    await EnsureSkyboxPackDownloadedAsync();
                await ApplySkyboxAsync(App.Settings.Prop.SkyboxName, Paths.Mods);
                App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Skybox applied: " + App.Settings.Prop.SkyboxName);
            }
			catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
			{
				throw;
			}
            catch (Exception ex2)
            {
                App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Skybox failed: " + ex2.Message);
            }
        }
		_cancelTokenSource.Token.ThrowIfCancellationRequested();
        string installedFontDir = Path.Combine(_latestVersionDirectory, "content", "fonts", "families");
        CustomFontMod.Apply(installedFontDir, "Bootstrapper::ApplyModifications");
        bool modsAllowed = ModsAllowedForThisLaunch();
        if (!modsAllowed)
        {
            App.Logger.WriteLine("Bootstrapper::ApplyModifications", $"Mods are set to {App.Settings?.Prop?.ModApplyTarget}, so this {(IsStudioLaunch ? "Studio" : "Player")} launch runs unmodded. Mod files are kept on disk.");
        }

        HashSet<string> modFolderFiles = new(StringComparer.OrdinalIgnoreCase);
        int appliedModCount = 0;
        int failedModCount = 0;
        Dictionary<string, string> selectedMods = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> nextManagedManifest = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> preservedManagedPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string text in Directory.EnumerateFiles(Paths.Mods, "*", SearchOption.AllDirectories))
        {
            if (_cancelTokenSource.IsCancellationRequested)
            {
                return;
            }
            string text2 = text[(Paths.Mods.Length + 1)..];
            if (text2 == "README.txt")
            {
                try { File.Delete(text); }
                catch (Exception ex5) { App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Could not remove README.txt: " + ex5.Message); }
            }
            else if (modsAllowed && !text2.EndsWith(".lock") && (App.Settings.Prop.UseFastFlagManager || !string.Equals(text2, "ClientSettings\\ClientAppSettings.json", StringComparison.OrdinalIgnoreCase)) && (!IsStudioLaunch || !text2.StartsWith("PlatformContent\\pc\\textures\\sky", StringComparison.OrdinalIgnoreCase)))
            {
                selectedMods[text2] = text;
            }
        }
        try
        {
            ManagedModScanResult scan = ManagedModStore.ScanEnabledFiles();
            foreach (string id in scan.SuccessfulModIds)
                nextManagedManifest[id] = [];
            foreach (ManagedModFile file in scan.Files)
            {
                if (_cancelTokenSource.IsCancellationRequested)
                    return;
                if (file.Relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!App.Settings.Prop.UseFastFlagManager && string.Equals(file.Relative, "ClientSettings\\ClientAppSettings.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsStudioLaunch && file.Relative.StartsWith("PlatformContent\\pc\\textures\\sky", StringComparison.OrdinalIgnoreCase))
                    continue;
                selectedMods[file.Relative] = file.Source;
                nextManagedManifest[file.Mod.Id].Add(file.Relative);
            }
            Dictionary<string, List<string>> previousManagedManifest = GetPreviousManagedModManifest();
            foreach ((string id, string message) in scan.Failures)
            {
                App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Managed mod " + id[..8] + " could not be indexed: " + message);
                if (previousManagedManifest.TryGetValue(id, out List<string>? previousPaths))
                {
                    nextManagedManifest[id] = [.. previousPaths];
                    preservedManagedPaths.UnionWith(previousPaths);
                }
                else
                {
                    List<string> fallbackPaths = [.. GetPreviousModManifest()];
                    nextManagedManifest[id] = fallbackPaths;
                    preservedManagedPaths.UnionWith(fallbackPaths);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Managed mods could not be indexed: " + ex.Message);
            nextManagedManifest = GetPreviousManagedModManifest().ToDictionary(item => item.Key, item => new List<string>(item.Value), StringComparer.OrdinalIgnoreCase);
            preservedManagedPaths.UnionWith(GetPreviousModManifest());
        }
        List<(string Source, string Relative, string Target)> pendingMods = new(selectedMods.Count);
        foreach ((string relative, string source) in selectedMods)
        {
            modFolderFiles.Add(relative);
            pendingMods.Add((source, relative, Path.Combine(_latestVersionDirectory, relative)));
        }
        modFolderFiles.UnionWith(preservedManagedPaths);
        foreach (string directory in pendingMods.Select(item => Path.GetDirectoryName(item.Target)).Where(path => !string.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(directory);
        }
        bool cacheVersionMatches = string.Equals(AppData.State.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string> previousCache = cacheVersionMatches
            ? new Dictionary<string, string>(AppData.State.ModApplyCache ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string, string> nextCache = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentQueue<(string Relative, string Message)> applyErrors = new();
        await Parallel.ForEachAsync(pendingMods, new ParallelOptions
        {
            MaxDegreeOfParallelism = ModApplyConcurrency,
            CancellationToken = _cancelTokenSource.Token
        }, (item, token) =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                FileInfo sourceInfo = new(item.Source);
                FileInfo targetInfo = new(item.Target);
                string fingerprint = BuildModFingerprint(sourceInfo, targetInfo);
                if (targetInfo.Exists && previousCache.TryGetValue(item.Relative, out string? cached) && string.Equals(cached, fingerprint, StringComparison.Ordinal))
                {
                    nextCache[item.Relative] = fingerprint;
                    return ValueTask.CompletedTask;
                }
                bool same = cacheVersionMatches && targetInfo.Exists && sourceInfo.Length == targetInfo.Length && MD5Hash.FromFile(item.Source) == MD5Hash.FromFile(item.Target);
                if (!same)
                {
                    Filesystem.AssertReadOnly(item.Target);
                    File.Copy(item.Source, item.Target, overwrite: true);
                    Interlocked.Increment(ref appliedModCount);
                    targetInfo.Refresh();
                }
                nextCache[item.Relative] = BuildModFingerprint(sourceInfo, targetInfo);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failedModCount);
                applyErrors.Enqueue((item.Relative, ex.Message));
            }
            return ValueTask.CompletedTask;
        });
        while (applyErrors.TryDequeue(out (string Relative, string Message) error))
        {
            App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Could not apply " + error.Relative + ": " + error.Message);
        }
        App.Logger.WriteLine("Bootstrapper::ApplyModifications", $"Applied {appliedModCount} changed mod files.");
        if (failedModCount > 0)
        {
            App.Logger.WriteLine("Bootstrapper::ApplyModifications", $"{failedModCount} mod files could not be applied, the rest were still applied.");
        }
        Dictionary<string, List<string>> dictionary = [];
        KeyValuePair<string, string>[] packagePrefixes = [.. AppData.PackageDirectoryMap
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .OrderByDescending(item => item.Value.Length)];
        HashSet<string> unresolvedStalePaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> previousModManifest = new(GetPreviousModManifest(), StringComparer.OrdinalIgnoreCase);
        string installedCustomFont = Path.Combine(_latestVersionDirectory, "content", "fonts", "CustomFont.ttf");
        if (File.Exists(installedCustomFont))
        {
            previousModManifest.Add(Path.Combine("content", "fonts", "CustomFont.ttf"));
        }
        foreach (string familyName in CustomFontMod.FindGeneratedFamilyNames(installedFontDir))
        {
            previousModManifest.Add(Path.Combine("content", "fonts", "families", familyName));
        }
        foreach (string loc in previousModManifest)
        {
            if (modFolderFiles.Contains(loc))
            {
                continue;
            }
			if (!TryResolveVersionRelativePath(loc, out string safeTarget))
			{
				App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Ignored an invalid stale mod path");
				continue;
			}
            KeyValuePair<string, string> keyValuePair = packagePrefixes.FirstOrDefault(item => loc.StartsWith(item.Value, StringComparison.OrdinalIgnoreCase));
            string key = keyValuePair.Key;
            if (string.IsNullOrEmpty(key))
            {
                try
                {
					if (File.Exists(safeTarget))
                    {
						Filesystem.AssertReadOnly(safeTarget);
						File.Delete(safeTarget);
                    }
                }
                catch (Exception ex)
                {
                    unresolvedStalePaths.Add(loc);
                    App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Could not remove stale mod " + loc + ": " + ex.Message);
                }
            }
            else
            {
                if (!dictionary.TryGetValue(key, out List<string>? value))
                {
                    value = [];
                    dictionary[key] = value;
                }

                value.Add(loc[keyValuePair.Value.Length..]);
            }
        }
        bool packageManifestFetched = false;
        foreach (KeyValuePair<string, List<string>> item in dictionary)
        {
            string pkgName;
            List<string> files2;
            (pkgName, files2) = item;
            if (_cancelTokenSource.IsCancellationRequested)
            {
                return;
            }
            try
            {
                Package? pkg = ResolveInstalledPackage(pkgName);
                if (pkg == null || !IsPackageCacheValid(pkg))
                {
                    if (!packageManifestFetched)
                    {
                        await GetLatestVersionInfo(fetchManifest: true, forceManifest: true);
                        packageManifestFetched = true;
                    }
                    pkg = ResolveInstalledPackage(pkgName);
                }
                if (pkg == null)
                {
                    throw new InvalidDataException("The Roblox package could not be identified");
                }
                await DownloadPackage(pkg);
                ExtractPackage(pkg, files2);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string prefix = AppData.PackageDirectoryMap.GetValueOrDefault(pkgName) ?? string.Empty;
                foreach (string file in files2)
                {
                    unresolvedStalePaths.Add(prefix + file);
                }
                App.Logger.WriteLine("Bootstrapper::ApplyModifications", "Could not restore stale files from " + pkgName + ": " + ex.Message);
            }
        }
        modFolderFiles.UnionWith(unresolvedStalePaths);
        AppData.State.ModManifest = [.. modFolderFiles];
        AppData.State.ModApplyCache = new Dictionary<string, string>(nextCache, StringComparer.OrdinalIgnoreCase);
        AppData.State.ManagedModManifest = nextManagedManifest;
        AppData.State.ModApplyVersion = _latestVersionGuid;
        App.State.Save();
        try
        {
            bool flag = File.Exists(Path.Combine(_latestVersionDirectory, "eurotrucks2.exe"));
            if (App.Settings.Prop.RenameClientToEuroTrucks2 && !flag)
            {
                File.Move(Path.Combine(_latestVersionDirectory, "RobloxPlayerBeta.exe"), Path.Combine(_latestVersionDirectory, "eurotrucks2.exe"));
            }
            else if (!App.Settings.Prop.RenameClientToEuroTrucks2 && flag)
            {
                File.Move(Path.Combine(_latestVersionDirectory, "eurotrucks2.exe"), Path.Combine(_latestVersionDirectory, "RobloxPlayerBeta.exe"));
            }
        }
        catch (Exception ex4)
        {
            App.Logger.WriteLine("Bootstrapper::ApplyModifications", "EuroTrucks rename failed: " + ex4.Message);
        }
    }

    private static string BuildModFingerprint(FileInfo source, FileInfo target)
    {
        return string.Concat(source.Length, "|", source.LastWriteTimeUtc.Ticks, "|", target.Exists ? target.Length : -1L, "|", target.Exists ? target.LastWriteTimeUtc.Ticks : -1L);
    }

	private bool TryResolveVersionRelativePath(string relativePath, out string fullPath)
	{
		fullPath = string.Empty;
		if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
		{
			return false;
		}
		string[] segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
		{
			return false;
		}
		string root = Path.GetFullPath(_latestVersionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
		if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		fullPath = candidate;
		return true;
	}

    private IReadOnlyList<string> GetPreviousModManifest()
    {
        if (string.Equals(AppData.State.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase))
        {
            return AppData.State.ModManifest ?? [];
        }
        if (string.Equals(App.State.Prop.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase))
        {
            return App.State.Prop.ModManifest ?? [];
        }
        return [];
    }

    private Dictionary<string, List<string>> GetPreviousManagedModManifest()
    {
        if (string.Equals(AppData.State.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase))
        {
            return AppData.State.ManagedModManifest ?? new Dictionary<string, List<string>>();
        }
        if (string.Equals(App.State.Prop.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase))
        {
            return App.State.Prop.ManagedModManifest ?? new Dictionary<string, List<string>>();
        }
        return new Dictionary<string, List<string>>();
    }

    private Package? ResolveInstalledPackage(string packageName)
    {
        Package? package = _versionPackageManifest.Find(item => string.Equals(item.Name, packageName, StringComparison.OrdinalIgnoreCase));
        if (package != null)
        {
            return package;
        }
        if (!AppData.State.PackageHashes.TryGetValue(packageName, out string? signature) || string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }
        string cachedPath = Path.Combine(Paths.Downloads, signature);
        return new Package
        {
            Name = packageName,
            Signature = signature,
            PackedSize = File.Exists(cachedPath) ? checked((int)Math.Min(new FileInfo(cachedPath).Length, int.MaxValue)) : 0,
            Size = 0
        };
    }

    private static bool IsPackageCacheValid(Package package)
    {
        return File.Exists(package.DownloadPath) && string.Equals(MD5Hash.FromFile(package.DownloadPath), package.Signature, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> GetLatestCommitShaAsync(CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, SkyboxCommitApiUrl);
        req.Headers.UserAgent.ParseAdd("SkyboxInstaller");
        using HttpResponseMessage res = await SkyboxHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        using Stream stream = await res.Content.ReadAsStreamAsync(ct);
        using JsonDocument jsonDocument = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        string? sha = jsonDocument.RootElement.GetProperty("sha").GetString();
        if (sha == null || sha.Length != 40 || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException("The skybox version is invalid");
        return sha;
    }

    private static bool HasSkyboxPack()
    {
        try
        {
            return Directory.Exists(PackFolder) && Directory.EnumerateDirectories(PackFolder).Any(IsValidSkyboxPackDirectory);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidSkyboxPackDirectory(string directory)
    {
        return SkyboxImageConverter.IsValidPackDirectory(directory);
    }

    private static string? GetLocalCommit()
    {
        string path = Path.Combine(PackFolder, SkyboxVersionFile);
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length <= 0 || file.Length > 128)
                return null;
            string value = File.ReadAllText(path).Trim();
            return value.Length == 40 && value.All(Uri.IsHexDigit) ? value : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task EnsureSkyboxPackDownloadedAsync()
    {
        Directory.CreateDirectory(PackFolder);
        string versionPath = Path.Combine(PackFolder, SkyboxVersionFile);
        if (HasSkyboxPack() && File.Exists(versionPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(versionPath) < TimeSpan.FromHours(6))
        {
            return;
        }
        string latest;
        try
        {
            latest = await GetLatestCommitShaAsync(_cancelTokenSource.Token);
        }
        catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("Bootstrapper::EnsureSkyboxPackDownloaded", "Commit check failed, keeping local pack: " + ex.Message);
            if (GetLocalCommit() != null && HasSkyboxPack())
            {
                return;
            }
            throw;
        }
        if (GetLocalCommit() == latest && HasSkyboxPack())
        {
            File.SetLastWriteTimeUtc(versionPath, DateTime.UtcNow);
            return;
        }
        SetStatus("Updating Skybox Pack...");
        string operationId = Guid.NewGuid().ToString("N");
        string tempZip = Path.Combine(Path.GetTempPath(), "VoidstrapSkybox_" + operationId + ".zip");
        string stagingFolder = PackFolder + ".new." + operationId;
        string backupFolder = PackFolder + ".backup." + operationId;
        try
        {
            string archiveUrl = SkyboxArchiveBaseUrl + latest + ".zip";
            await DownloadFileWithProgressAsync(archiveUrl, tempZip, "Downloading Skybox", 536870912L, _cancelTokenSource.Token);
            if (Dialog != null)
            {
                SetProgressStyle(ProgressBarStyle.Marquee);
            }
            SetStatus("Updating Skybox Pack...");
            if (Directory.Exists(stagingFolder))
            {
                Directory.Delete(stagingFolder, recursive: true);
            }
            Directory.CreateDirectory(stagingFolder);
            string stagingRoot = Path.GetFullPath(stagingFolder) + Path.DirectorySeparatorChar;
            using ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(tempZip);
            int extractedEntries = 0;
            long extractedBytes = 0;
            long actualExtractedBytes = 0;
            byte[] extractionBuffer = new byte[81920];
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                _cancelTokenSource.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name))
                    continue;
                string[] source = entry.FullName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
                if (source.Length != 3 || !SkyboxFileNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    continue;
                if (entry.Length <= 0 || entry.Length > 16777216)
                    throw new InvalidDataException("Skybox archive contains an invalid file size.");
                extractedEntries++;
                extractedBytes += entry.Length;
                if (extractedEntries > 4096 || extractedBytes > 536870912L)
                    throw new InvalidDataException("Skybox archive exceeds the extraction limit.");
                string path = Path.GetFullPath(Path.Combine(stagingFolder, Path.Combine([.. source.Skip(1)])));
                if (!path.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Skybox archive contains an invalid path.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using Stream es = entry.Open();
                await using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                long entryBytes = 0;
                while (true)
                {
                    int read = await es.ReadAsync(extractionBuffer.AsMemory(), _cancelTokenSource.Token);
                    if (read == 0)
                        break;
                    entryBytes += read;
                    actualExtractedBytes += read;
                    if (entryBytes > entry.Length || actualExtractedBytes > 536870912L)
                        throw new InvalidDataException("Skybox archive exceeds the extraction limit.");
                    await fs.WriteAsync(extractionBuffer.AsMemory(0, read), _cancelTokenSource.Token);
                }
                if (entryBytes != entry.Length)
                    throw new InvalidDataException("Skybox archive contains an invalid file size.");
            }
            if (!Directory.EnumerateDirectories(stagingFolder).Any(IsValidSkyboxPackDirectory))
            {
                throw new InvalidDataException("Skybox archive did not contain a valid pack.");
            }
            File.WriteAllText(Path.Combine(stagingFolder, SkyboxVersionFile), latest);
            if (Directory.Exists(PackFolder))
                Directory.Move(PackFolder, backupFolder);
            Directory.Move(stagingFolder, PackFolder);
            TryDeleteDirectory(backupFolder);
        }
        catch
        {
            if (!Directory.Exists(PackFolder) && Directory.Exists(backupFolder))
            {
                try
                {
                    Directory.Move(backupFolder, PackFolder);
                }
                catch (Exception restoreException)
                {
                    throw new IOException("The skybox update failed and its backup could not be restored. The backup was preserved at " + backupFolder, restoreException);
                }
            }
            throw;
        }
        finally
        {
            try
            {
                File.Delete(tempZip);
            }
            catch
            {
            }
            TryDeleteDirectory(stagingFolder);
            if (Directory.Exists(PackFolder))
                TryDeleteDirectory(backupFolder);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private async Task DownloadFileWithProgressAsync(string url, string destPath, string label, long maximumBytes, CancellationToken ct)
    {
		int bufferSize = DownloadConfiguration.NormalizeBuffer(App.Settings.Prop.DownloadBufferKb) * 1024;
		int maxSegments = DownloadConfiguration.NormalizeSegments(App.Settings.Prop.MaxDownloadSegments);
        long downloaded = 0L;
        using HttpResponseMessage probe = await SkyboxHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        probe.EnsureSuccessStatusCode();
        long contentLength = probe.Content.Headers.ContentLength ?? (-1);
        if (contentLength > maximumBytes)
            throw new InvalidDataException("The skybox download exceeds the size limit.");
        bool flag = contentLength >= 4194304 && (probe.Headers.AcceptRanges?.Contains("bytes") ?? false);
        CancellationTokenSource pcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            Task progressTask = RunSingleFileProgressLoopAsync(label, () => Interlocked.Read(in downloaded), contentLength, pcts.Token);
            try
            {
                if (!flag)
                {
                    App.Logger.WriteLine("Bootstrapper::DownloadFileWithProgress", $"Single-stream download ({contentLength:N0} bytes, ranges unsupported).");
                    await StreamResponseToFileAsync(probe, destPath, bufferSize, maximumBytes, Add, ct);
                    return;
                }
                try
                {
                    App.Logger.WriteLine("Bootstrapper::DownloadFileWithProgress", $"Multi-segment download ({contentLength:N0} bytes).");
                    probe.Dispose();
                    await DownloadFileSegmentedAsync(url, destPath, contentLength, bufferSize, maxSegments, 2097152L, Add, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    App.Logger.WriteLine("Bootstrapper::DownloadFileWithProgress", "Segmented download failed (" + ex.Message + "); falling back to single stream.");
                    Interlocked.Exchange(ref downloaded, 0L);
                    using HttpResponseMessage fresh = await SkyboxHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                    fresh.EnsureSuccessStatusCode();
                    await StreamResponseToFileAsync(fresh, destPath, bufferSize, maximumBytes, Add, ct);
                }
            }
            finally
            {
                pcts.Cancel();
                try
                {
                    await progressTask.ConfigureAwait(continueOnCapturedContext: false);
                }
                catch
                {
                }
            }
        }
        finally
        {
            if (pcts != null)
            {
                ((IDisposable)pcts).Dispose();
            }
        }
        void Add(long n)
        {
            Interlocked.Add(ref downloaded, n);
        }
    }

    private static async Task StreamResponseToFileAsync(HttpResponseMessage response, string destPath, int bufferSize, long maximumBytes, Action<long> addBytes, CancellationToken ct)
    {
        await using Stream src = await response.Content.ReadAsStreamAsync(ct);
        await using FileStream dst = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        byte[] buf = DownloadBufferPool.Rent(bufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                int num;
                int r = (num = await src.ReadAsync(buf.AsMemory(0, bufferSize), ct));
                if (num <= 0)
                {
                    break;
                }
                total += r;
                if (total > maximumBytes)
                    throw new InvalidDataException("The skybox download exceeds the size limit.");
                await dst.WriteAsync(buf.AsMemory(0, r), ct);
                addBytes(r);
            }
        }
        finally
        {
            DownloadBufferPool.Return(buf);
        }
    }

    private static async Task DownloadFileSegmentedAsync(string url, string destPath, long contentLength, int bufferSize, int maxSegments, long minSegment, Action<long> addBytes, CancellationToken ct)
    {
        int segs = (int)Math.Min(maxSegments, Math.Max(1L, contentLength / minSegment));
        if (segs <= 1)
        {
            using HttpResponseMessage resp = await SkyboxHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await StreamResponseToFileAsync(resp, destPath, bufferSize, contentLength, addBytes, ct);
            return;
        }
        long segSize = contentLength / segs;
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = File.OpenHandle(destPath, FileMode.Create, FileAccess.Write, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        RandomAccess.SetLength(handle, contentLength);
        await Task.WhenAll(Enumerable.Range(0, segs).Select(async i =>
        {
            long start = i * segSize;
            long end = i == segs - 1 ? contentLength - 1 : start + segSize - 1;
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(start, end);
            using HttpResponseMessage res = await SkyboxHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (res.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new IOException($"Server ignored range request with status {(int)res.StatusCode}.");
            }
			long expectedLength = end - start + 1;
			ContentRangeHeaderValue? contentRange = res.Content.Headers.ContentRange;
			if (contentRange?.From != start || contentRange.To != end || contentRange.Length != contentLength || res.Content.Headers.ContentLength != expectedLength)
			{
				throw new IOException("Server returned an invalid skybox byte range");
			}
            await using Stream net = await res.Content.ReadAsStreamAsync(ct);
            byte[] buf = DownloadBufferPool.Rent(bufferSize);
            long pos = start;
            try
            {
				long remaining = expectedLength;
				while (remaining > 0)
                {
					int requested = (int)Math.Min(bufferSize, remaining);
					int read = await net.ReadAsync(buf.AsMemory(0, requested), ct);
					if (read == 0)
					{
						throw new IOException("Skybox segment ended before the expected byte range");
					}
                    await RandomAccess.WriteAsync(handle, buf.AsMemory(0, read), pos, ct);
                    pos += read;
					remaining -= read;
                    addBytes(read);
                }
				if (await net.ReadAsync(buf.AsMemory(0, 1), ct) != 0)
                {
					throw new IOException("Skybox segment exceeded the expected byte range");
                }
            }
            finally
            {
                DownloadBufferPool.Return(buf);
            }
        }));
    }

    private async Task RunSingleFileProgressLoopAsync(string label, Func<long> downloadedGetter, long total, CancellationToken ct)
    {
        double emaSpeed = 0.0;
        long lastBytes = downloadedGetter();
        Stopwatch sw = Stopwatch.StartNew();
        if (Dialog != null)
        {
            if (total > 0)
            {
                SetProgressStyle(ProgressBarStyle.Continuous);
                SetProgressMaximum(1000);
            }
            else
            {
                SetProgressStyle(ProgressBarStyle.Marquee);
            }
        }
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
            long num = downloadedGetter();
            double num2 = sw.Elapsed.TotalSeconds;
            if (num2 < 0.05)
            {
                num2 = 0.05;
            }
            sw.Restart();
            double num3 = Math.Max(0.0, (double)(num - lastBytes) / num2);
            lastBytes = num;
            emaSpeed = ((emaSpeed <= 0.0) ? num3 : (0.25 * num3 + 0.75 * emaSpeed));
            if (total > 0)
            {
                double num4 = Math.Clamp((double)num / (double)total * 100.0, 0.0, 100.0);
                long num5 = Math.Max(0L, total - num);
                string value = ((emaSpeed > 4096.0) ? FormatEta((double)num5 / emaSpeed) : "calculating");
                SetStatus($"{label} {num4:0}%\n{FormatSpeed(emaSpeed)} · ETA {value}");
                if (Dialog != null)
                {
                    int value2 = (int)Math.Clamp(num4 * 10.0, 0.0, 1000.0);
                    InvokeOnDialog(delegate
                    {
                        Dialog.ProgressValue = value2;
                    });
                }
            }
            else
            {
                SetStatus($"{label}\n{BytesToString(num)} · {FormatSpeed(emaSpeed)}");
            }
        }
    }

    public static Task ApplySkyboxAsync(string skyboxName, string modsFolder)
    {
        if (string.IsNullOrWhiteSpace(skyboxName) || skyboxName.Length > 128 || skyboxName == "." || skyboxName == ".." || skyboxName.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidDataException("The selected skybox name is invalid.");
        bool custom = skyboxName.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase);
        string packRoot = Path.GetFullPath(PackFolder) + Path.DirectorySeparatorChar;
        string sourceRoot = custom ? Path.GetFullPath(SkyboxImageConverter.CustomPackDirectory) : Path.GetFullPath(Path.Combine(PackFolder, skyboxName));
        bool validSource = custom ? SkyboxImageConverter.IsValidPackDirectory(sourceRoot) : sourceRoot.StartsWith(packRoot, StringComparison.OrdinalIgnoreCase) && IsValidSkyboxPackDirectory(sourceRoot);
        if (!validSource)
        {
            throw new DirectoryNotFoundException("Skybox '" + skyboxName + "' not found.");
        }
        InlineArray5<string> buffer = default;
        buffer[0] = modsFolder;
        buffer[1] = "PlatformContent";
        buffer[2] = "pc";
        buffer[3] = "textures";
        buffer[4] = "sky";
        string targetRoot = Path.Combine(buffer);
        string operationId = Guid.NewGuid().ToString("N");
        string stagingRoot = targetRoot + ".new." + operationId;
        string backupRoot = targetRoot + ".backup." + operationId;
        try
        {
            Directory.CreateDirectory(stagingRoot);
            Parallel.ForEach(SkyboxFileNames, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(ModApplyConcurrency, SkyboxFileNames.Length) }, name =>
            {
                string sourceFile = Path.Combine(sourceRoot, name);
                string targetFile = Path.Combine(stagingRoot, name);
                File.Copy(sourceFile, targetFile, overwrite: false);
            });
            if (!IsValidSkyboxPackDirectory(stagingRoot))
                throw new InvalidDataException("The selected skybox is incomplete.");
            if (Directory.Exists(targetRoot))
                Directory.Move(targetRoot, backupRoot);
            Directory.Move(stagingRoot, targetRoot);
            TryDeleteDirectory(backupRoot);
        }
        catch
        {
            if (!Directory.Exists(targetRoot) && Directory.Exists(backupRoot))
            {
                try
                {
                    Directory.Move(backupRoot, targetRoot);
                }
                catch (Exception restoreException)
                {
                    throw new IOException("The skybox apply failed and its backup could not be restored. The backup was preserved at " + backupRoot, restoreException);
                }
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
            if (Directory.Exists(targetRoot))
                TryDeleteDirectory(backupRoot);
        }
        return Task.CompletedTask;
    }

    public static async Task ApplySkyboxPatchToRobloxStorageAsync(CancellationToken ct = default)
    {
        const long maxPatchBytes = 67108864L;
        string rbxStorage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "rbx-storage");
        HttpClient http = App.HttpClient;
        ConcurrentQueue<string> failures = new();
        await Parallel.ForEachAsync(SkyboxPatchFolderMap, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = ct
        }, async (item, token) =>
        {
            item.Deconstruct(out var key, out var value);
            string hash = key;
            string path = value;
            string text = Path.Combine(rbxStorage, path);
            Directory.CreateDirectory(text);
            string dest = Path.Combine(text, hash);
            if (File.Exists(dest) && GetSkyboxPatchSha256(dest).Equals(SkyboxPatchSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.SetAttributes(dest, FileAttributes.ReadOnly);
                return;
            }
            string temp = dest + ".download";
            try
            {
                using HttpResponseMessage response = await http.GetAsync("https://raw.githubusercontent.com/KloBraticc/SkyboxPatch/main/assets/" + hash, HttpCompletionOption.ResponseHeadersRead, token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > maxPatchBytes))
                {
                    throw new IOException("Skybox patch asset size is invalid");
                }
                await using Stream source = await response.Content.ReadAsStreamAsync(token);
                await using FileStream output = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] buffer = DownloadBufferPool.Rent(81920);
                long total = 0L;
                try
                {
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer.AsMemory(0, 81920), token);
                        if (read == 0)
                        {
                            break;
                        }
                        total += read;
                        if (total > maxPatchBytes)
                        {
                            throw new IOException("Skybox patch asset exceeds the size limit");
                        }
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
                finally
                {
                    DownloadBufferPool.Return(buffer);
                }
                await output.FlushAsync(token);
                await output.DisposeAsync();
                if (total == 0)
                {
                    throw new IOException("Skybox patch asset is empty");
                }
                if (!GetSkyboxPatchSha256(temp).Equals(SkyboxPatchSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("Skybox patch integrity check failed");
                }
                if (File.Exists(dest))
                {
                    File.SetAttributes(dest, FileAttributes.Normal);
                }
                File.Move(temp, dest, overwrite: true);
                File.SetAttributes(dest, FileAttributes.ReadOnly);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                File.Delete(temp);
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                }
                App.Logger.WriteLine("SkyboxPatch", "Failed " + hash + ": " + ex.Message);
                failures.Enqueue(hash);
            }
        });
        if (!failures.IsEmpty)
            throw new IOException("One or more skybox patch files could not be verified");
    }

    private static string GetSkyboxPatchSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        byte[] hash = System.Security.Cryptography.SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }

    private static string BytesToString(long bytes)
    {
        if (bytes == 0L)
        {
            return "0 B";
        }
        string[] array = ["B", "KB", "MB", "GB", "TB"];
        int num = (int)Math.Floor(Math.Log(Math.Abs(bytes), 1024.0));
        double value = Math.Round((double)bytes / Math.Pow(1024.0, num), 1);
        return $"{value} {array[Math.Min(num, array.Length - 1)]}";
    }
}
