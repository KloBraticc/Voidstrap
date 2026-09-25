using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using DiscordRPC;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Helpers;
using Voidstrap.Integrations;
using Voidstrap.Integrations.AssetProxy;
using Voidstrap.Models.APIs.GitHub;
using Voidstrap.Models.Attributes;
using Voidstrap.Models.Persistable;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.UI;
using Voidstrap.UI.ViewModels.ContextMenu;
using Voidstrap.Utility;
using LinqExpression = System.Linq.Expressions.Expression;
using ParameterExpression = System.Linq.Expressions.ParameterExpression;

namespace Voidstrap;

public partial class App : Application
{
	public const string ProjectName = "Voidstrap";

	public const string ProjectOwner = "Voidstrap";

	public const string ProjectRepository = "/KloBraticc/Voidstrap/";

	public const string ProjectDownloadLink = "https://github.com/KloBraticc/Voidstrap/releases";
	public const string ProjectFallbackRepository = "https://github.com/KloBraticc/Voidstrap";
	public const string ProjectFallbackDownloadLink = ProjectFallbackRepository + "/releases";
	public const string ProjectReleaseApi = "https://api.github.com/repos/KloBraticc/Voidstrap/releases/latest";
	public const string ProjectFallbackReleaseApi = "https://api.github.com/repos/KloBraticc/Voidstrap/releases/latest";

	public const string ProjectReleaseListApi = "https://api.github.com/repos/KloBraticc/Voidstrap/releases?per_page=20";

	public const string ProjectFallbackReleaseListApi = "https://api.github.com/repos/KloBraticc/Voidstrap/releases?per_page=20";

	public const string ProjectHelpLink = "https://github.com/KloBraticc/Voidstrap";

	public const string ProjectDonateLink = "https://github.com/sponsors/KloBraticc";

	public const string ProjectLogoUrl = "https://raw.githubusercontent.com/KloBraticc/Voidstrap/main/src/Voidstrap.App/Voidstrap.png";

	public const string ProjectSupportLink = "https://github.com/KloBraticc/Voidstrap/issues/new";
	public const string ProjectFallbackSupportLink = ProjectFallbackRepository + "/issues/new";
	public const string ProjectIssuesLink = "https://github.com/KloBraticc/Voidstrap/issues";
	public const string ProjectFallbackIssuesLink = ProjectFallbackRepository + "/issues";

	public const string RobloxPlayerAppName = "RobloxPlayerBeta";

	public const string RobloxStudioAppName = "RobloxStudioBeta";

	public const string UninstallKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap";

	public const string ApisKey = "Software\\Voidstrap";

	public static readonly string RobloxCookiesFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox\\LocalStorage\\RobloxCookies.dat");

	public static readonly BuildMetadataAttribute BuildMetadata = ResolveBuildMetadata();

	public static string Version { get; set; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

	public const int TaskbarProgressMaximum = 100;

	public static readonly Logger Logger = new();

	public static readonly Dictionary<string, BaseTask> PendingSettingTasks = [];

	public static readonly JsonManager<AppSettings> Settings = new();

	public static readonly JsonManager<DownloadStats> DownloadStats = new();

	public static readonly JsonManager<State> State = new();

	public static readonly JsonManager<RobloxState> RobloxState = new();

	public static readonly FastFlagManager FastFlags = new();

	public static readonly GBSEditor GlobalSettings = new();


	private static readonly HttpClient _httpClient = CreateHttpClient();

	private static int _showingExceptionDialog;
	private static bool _portableToolTipsDisabled;

	private readonly CancellationTokenSource _lifetimeCancellation = new();

	private int _linuxDeferredStarted;
	private int _linuxDeferredFallbackQueued;

	public static DiscordRpcClient? DiscordClient { get; set; }

	public static LaunchSettings LaunchSettings { get; private set; } = null!;

	public static Bootstrapper? Bootstrapper { get; set; } = null;

	public static bool IsActionBuild => !string.IsNullOrEmpty(BuildMetadata.CommitRef);

	public static bool IsProductionBuild
	{
		get
		{
			if (IsActionBuild)
			{
				return BuildMetadata.CommitRef.StartsWith("tag", StringComparison.Ordinal);
			}
			return false;
		}
	}

	public static bool IsStudioVisible => !string.IsNullOrEmpty(State.Prop.Studio.VersionGuid);

	public static HttpClient HttpClient => _httpClient;

	private static HttpClient CreateHttpClient()
	{
		HttpClient client = VpnHttpClient.Create(TimeSpan.FromSeconds(30), log: true);
		client.MaxResponseContentBufferSize = 16 * 1024 * 1024;
		return client;
	}

	public static byte[] ComputeSha256(byte[] data)
	{
		return SHA256.HashData(data);
	}

	public static byte[] ComputeSha256(Stream stream)
	{
		using SHA256 sHA = SHA256.Create();
		return sHA.ComputeHash(stream);
	}

	public static void Terminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
	{
		Logger.WriteLine("App::Terminate", $"Terminating with exit code {(int)exitCode} ({exitCode})");
		ShutdownApplication((int)exitCode);
	}

	public static void SoftTerminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
	{
		if (LaunchSettings?.WindowAuditFlag.Active == true)
		{
			Logger.WriteLine("App::SoftTerminate", "Soft termination blocked, window audit is running.");
			return;
		}
		int exitCodeNum = (int)exitCode;
		Logger.WriteLine("App::SoftTerminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");
		CloseRuntimeOnExit();
		ShutdownApplication(exitCodeNum);
	}

	private static void CloseRuntimeOnExit()
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return;
		}

		try
		{
			if (Voidstrap.Platform.Linux.LinuxSoberRuntimeProvider.TryCloseSober())
			{
				Logger.WriteLine("App::CloseRuntimeOnExit", "Closed the Roblox runtime alongside Voidstrap");
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("App::CloseRuntimeOnExit", "The Roblox runtime could not be closed: " + ex.Message);
		}
	}

	public static bool RestartApplication(IReadOnlyList<string> arguments, bool closeRuntime = true)
	{
		try
		{
			if (Voidstrap.Utility.Platform.IsLinux && Voidstrap.Platform.Linux.LinuxFlatpakHost.IsSandboxed)
			{
				List<string> flatpakArguments = ["run", Voidstrap.Platform.Linux.LinuxFlatpakHost.CurrentApplicationId];
				flatpakArguments.AddRange(arguments);
				using Process? flatpakProcess = Voidstrap.Platform.Linux.LinuxFlatpakHost.Start(flatpakArguments);
				if (flatpakProcess == null)
					return false;
				ExitForRestart(closeRuntime);
				return true;
			}

			string? executable = Voidstrap.Utility.Platform.IsLinux
				? Voidstrap.Platform.Linux.LinuxAppImageHost.ResolveApplicationPath(Environment.ProcessPath ?? string.Empty)
				: Environment.ProcessPath;
			if (string.IsNullOrWhiteSpace(executable))
			{
				return false;
			}
			ProcessStartInfo startInfo = new()
			{
				FileName = executable,
				UseShellExecute = true
			};
			foreach (string argument in arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}
			using Process? process = Process.Start(startInfo);
			if (process == null)
			{
				return false;
			}
			ExitForRestart(closeRuntime);
			return true;
		}
		catch (Exception ex)
		{
			Logger.WriteLine("App::RestartApplication", "Restart failed: " + ex.Message);
			return false;
		}
	}

	private static void ExitForRestart(bool closeRuntime)
	{
		if (closeRuntime)
		{
			SoftTerminate();
			return;
		}
		Logger.WriteLine("App::RestartApplication", "Closing the current instance for the restart");
		ShutdownApplication((int)ErrorCode.ERROR_SUCCESS);
	}

	private static void OnLinuxExitMarkRenderer(object sender, ExitEventArgs e)
	{
		StopLinuxRendererTimer();
		Voidstrap.Utility.LinuxStartup.MarkRendererHealthy(requireRenderer: false);
	}

	private static int _linuxRendererConfirmed;

	private static DispatcherTimer? _linuxRendererTimer;

	private static int _linuxRendererChecks;

	private static int _linuxRendererActiveTicks;

	private const int LinuxRendererSettleTicks = 8;

	private static void OnLinuxRendererTimerTick(object? sender, EventArgs e)
	{
		_linuxRendererActiveTicks = Voidstrap.Utility.LinuxStartup.IsRendererActive() ? _linuxRendererActiveTicks + 1 : 0;
		if (_linuxRendererActiveTicks >= LinuxRendererSettleTicks && Voidstrap.Utility.LinuxStartup.MarkRendererHealthy(requireRenderer: true))
		{
			Volatile.Write(ref _linuxRendererConfirmed, 1);
			StopLinuxRendererTimer();
		}
		else if (++_linuxRendererChecks >= 120)
		{
			StopLinuxRendererTimer();
		}
	}

	private static void StopLinuxRendererTimer()
	{
		DispatcherTimer? timer = _linuxRendererTimer;
		_linuxRendererTimer = null;
		if (timer != null)
		{
			timer.Stop();
			timer.Tick -= OnLinuxRendererTimerTick;
		}
	}

	private static void OnLinuxWindowLoaded(object sender, RoutedEventArgs e)
	{
		if (sender is not Window window || Volatile.Read(ref _linuxRendererConfirmed) != 0)
		{
			return;
		}
		window.ContentRendered -= OnLinuxWindowContentRendered;
		window.ContentRendered += OnLinuxWindowContentRendered;
	}

	private static void OnLinuxWindowContentRendered(object? sender, EventArgs e)
	{
		if (sender is Window window)
		{
			window.ContentRendered -= OnLinuxWindowContentRendered;
		}
		if (Current is App application && Volatile.Read(ref application._linuxDeferredStarted) == 0 && Interlocked.Exchange(ref application._linuxDeferredFallbackQueued, 1) == 0)
			_ = application.StartLinuxDeferredFallbackAsync(application._lifetimeCancellation.Token);
		if (Volatile.Read(ref _linuxRendererConfirmed) != 0 || _linuxRendererTimer != null)
		{
			return;
		}
		_linuxRendererTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
		_linuxRendererTimer.Tick += OnLinuxRendererTimerTick;
		_linuxRendererTimer.Start();
		OnLinuxRendererTimerTick(null, EventArgs.Empty);
	}

	private static void ShutdownApplication(int exitCodeNum)
	{
		Application? application = Current;
		if (application == null)
		{
			Logger.Flush();
			Environment.Exit(exitCodeNum);
			return;
		}
		void shutdown()
		{
			if (!application.Dispatcher.HasShutdownStarted)
			{
				Logger.Flush();
				application.Shutdown(exitCodeNum);
			}
		}
		try
		{
			if (application.Dispatcher.CheckAccess())
			{
				shutdown();
			}
			else
			{
				application.Dispatcher.Invoke(shutdown);
			}
		}
		catch
		{
			Logger.Flush();
			Environment.Exit(exitCodeNum);
		}
	}

	private void GlobalExceptionHandler(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		e.Handled = true;
		Exception ex = UnwrapException(e.Exception);
		if (Dispatcher.HasShutdownStarted && ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1400 })
		{
			return;
		}
		if (Voidstrap.Utility.RenderAcceleration.TryRecoverFromRenderFailure(ex))
		{
			Logger.WriteException("App::GlobalExceptionHandler", ex);
			return;
		}
		Logger.WriteLine("App::GlobalExceptionHandler", "An exception occurred");
		if (IsFatalException(ex))
		{
			FinalizeExceptionHandling(e.Exception);
		}
		else
		{
			Logger.WriteException("App::GlobalExceptionHandler", ex);
			Logger.Flush();
		}
	}

	private static Exception UnwrapException(Exception ex)
	{
		while (true)
		{
			if (ex is AggregateException aggregate && aggregate.InnerException != null)
			{
				ex = aggregate.InnerException;
				continue;
			}
			if (ex is System.Reflection.TargetInvocationException invocation && invocation.InnerException != null)
			{
				ex = invocation.InnerException;
				continue;
			}
			return ex;
		}
	}

	private static bool IsFatalException(Exception ex)
	{
		return ex is OutOfMemoryException || ex is AccessViolationException || ex is System.Runtime.InteropServices.SEHException || ex is BadImageFormatException || ex is System.Threading.ThreadAbortException;
	}

	private static bool IsExpectedLinuxCancellation(Exception ex)
	{
		if (!Voidstrap.Utility.Platform.IsLinux)
		{
			return false;
		}
		if (ex is AggregateException aggregate)
		{
			AggregateException flattened = aggregate.Flatten();
			return flattened.InnerExceptions.Count > 0 && flattened.InnerExceptions.All(IsExpectedLinuxCancellation);
		}
		if (ex is OperationCanceledException)
		{
			return true;
		}
		if (ex is IOException { InnerException: not null } io)
		{
			return IsExpectedLinuxCancellation(io.InnerException!);
		}
		if (ex is System.Net.Sockets.SocketException socket)
		{
			return socket.SocketErrorCode is System.Net.Sockets.SocketError.OperationAborted or System.Net.Sockets.SocketError.Interrupted
				|| socket.NativeErrorCode == 125;
		}
		return false;
	}

	private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
		if (IsExpectedLinuxCancellation(e.Exception))
		{
			e.SetObserved();
			return;
		}
		try
		{
			AggregateException flat = e.Exception.Flatten();
			foreach (Exception inner in flat.InnerExceptions)
			{
				Exception? cur = inner;
				while (cur != null)
				{
					if (cur is ArgumentException arg && arg.Message.Contains("Hwnd of zero", StringComparison.OrdinalIgnoreCase))
					{
						e.SetObserved();
						return;
					}
					if (cur.Message.Contains("Hwnd of zero", StringComparison.OrdinalIgnoreCase))
					{
						e.SetObserved();
						return;
					}
					if (cur.StackTrace != null && cur.StackTrace.Contains("WpfTap", StringComparison.OrdinalIgnoreCase) && cur.StackTrace.Contains("HitTestHelper", StringComparison.OrdinalIgnoreCase))
					{
						e.SetObserved();
						return;
					}
					cur = cur.InnerException;
				}
			}
		}
		catch { }
		try
		{
			Logger.WriteException("App::UnobservedTaskException", e.Exception);
			Logger.Flush();
		}
		catch
		{
		}
		e.SetObserved();
	}

	private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		try
		{
			if (e.ExceptionObject is Exception ex)
			{
				Logger.WriteException("App::DomainUnhandledException", ex);
				Voidstrap.Utility.RenderAcceleration.TryRecoverFromRenderFailure(ex);
				if (e.IsTerminating)
				{
					Voidstrap.Utility.AppNotifications.RecordCrash("App::DomainUnhandledException", ex);
				}
			}
			else
			{
				Logger.WriteLine("App::DomainUnhandledException", "The runtime reported a non exception failure, terminating: " + (e.IsTerminating ? "yes" : "no"));
			}
		}
		catch
		{
		}
		try
		{
			Logger.Flush();
		}
		catch
		{
		}
	}

	public static void FinalizeExceptionHandling(AggregateException ex)
	{
		foreach (Exception innerException in ex.InnerExceptions)
		{
			Logger.WriteException("App::FinalizeExceptionHandling", innerException);
		}
		FinalizeExceptionHandling(ex.GetBaseException(), log: false);
	}

	public static void FinalizeExceptionHandling(Exception ex, bool log = true)
	{
		if (log)
		{
			Logger.WriteException("App::FinalizeExceptionHandling", ex);
		}
		Voidstrap.Utility.AppNotifications.RecordCrash("App::FinalizeExceptionHandling", ex);
		Logger.Flush();
		if (Interlocked.Exchange(ref _showingExceptionDialog, 1) != 0)
		{
			return;
		}
		Application? application = Current;
		void showFailure()
		{
			SendLog();
			if (Bootstrapper?.Dialog != null)
			{
				if (Bootstrapper.Dialog.TaskbarProgressValue == 0.0)
				{
					Bootstrapper.Dialog.TaskbarProgressValue = 1.0;
				}
				Bootstrapper.Dialog.TaskbarProgressState = TaskbarItemProgressState.Error;
			}
			Frontend.ShowExceptionDialog(ex);
		}
		try
		{
			if (application != null && !application.Dispatcher.CheckAccess())
			{
				application.Dispatcher.Invoke(showFailure);
			}
			else
			{
				showFailure();
			}
		}
		catch (Exception dialogException)
		{
			try
			{
				Logger.WriteException("App::FinalizeExceptionHandling::Dialog", dialogException);
			}
			catch
			{
			}
		}
		ShutdownApplication((int)ErrorCode.ERROR_INSTALL_FAILURE);
	}

	public static bool AllowPreReleaseUpdates => Settings?.Prop?.AllowPreReleaseUpdates == true;

	public static TimeSpan ReleaseCacheAge(bool forceRefresh) => forceRefresh ? TimeSpan.Zero : TimeSpan.FromMinutes(15L);

	public static async Task<GithubRelease?> GetLatestRelease(bool forceRefresh = false)
	{
		try
		{
			if (AllowPreReleaseUpdates)
			{
				GithubRelease? newest = await GetNewestReleaseFromListAsync(forceRefresh);
				if (newest != null)
				{
					return newest;
				}
				Logger.WriteLine("App::GetLatestRelease", "Prerelease lookup found nothing, falling back to the stable release");
			}
			GithubRelease? githubRelease = await GitHubCache.GetJsonWithFallbackAsync<GithubRelease>(ProjectReleaseApi, ProjectFallbackReleaseApi, ReleaseCacheAge(forceRefresh));
			if (githubRelease == null || githubRelease.Assets == null)
			{
				Logger.WriteLine("App::GetLatestRelease", "Encountered invalid data");
				return null;
			}
			return githubRelease;
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::GetLatestRelease", ex);
		}
		return null;
	}

	public static async Task<GithubRelease?> GetNewestReleaseFromListAsync(bool forceRefresh = false)
	{
		try
		{
			List<GithubRelease>? releases = await GitHubCache.GetJsonWithFallbackAsync<List<GithubRelease>>(ProjectReleaseListApi, ProjectFallbackReleaseListApi, ReleaseCacheAge(forceRefresh));
			if (releases == null)
			{
				return null;
			}
			bool allowPre = AllowPreReleaseUpdates;
			GithubRelease? best = null;
			System.Version? bestVersion = null;
			foreach (GithubRelease release in releases)
			{
				if (release == null || release.Draft || release.Assets == null)
				{
					continue;
				}
				if (release.Prerelease && !allowPre)
				{
					continue;
				}
				if (!System.Version.TryParse((release.TagName ?? "").TrimStart('v', 'V'), out System.Version? parsed))
				{
					best ??= release;
					continue;
				}
				if (bestVersion == null || parsed > bestVersion)
				{
					best = release;
					bestVersion = parsed;
				}
			}
			return best;
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::GetNewestReleaseFromListAsync", ex);
			return null;
		}
	}

	public static async Task<GithubRelease?> FindReleaseByTagAsync(string tag)
	{
		if (string.IsNullOrWhiteSpace(tag))
		{
			return null;
		}
		try
		{
			List<GithubRelease>? releases = await GitHubCache.GetJsonWithFallbackAsync<List<GithubRelease>>(ProjectReleaseListApi, ProjectFallbackReleaseListApi, TimeSpan.FromMinutes(15L));
			if (releases == null)
			{
				return null;
			}
			foreach (GithubRelease release in releases)
			{
				if (release != null && !release.Draft && string.Equals(release.TagName, tag, StringComparison.OrdinalIgnoreCase))
				{
					return release;
				}
			}
			return null;
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::FindReleaseByTagAsync", ex);
			return null;
		}
	}

	public static void SendStat(string _, string _1)
	{
	}

	public static void SendLog()
	{
	}

	public static void AssertWindowsOSVersion()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		if (Environment.OSVersion.Version.Major < 7)
		{
			Logger.WriteLine("App::AssertWindowsOSVersion", $"Detected unsupported Windows version ({Environment.OSVersion.Version}).");
			if (!LaunchSettings.QuietFlag.Active)
			{
				Frontend.ShowMessageBox("Your Windows Version is not supported with Voidstrap!", MessageBoxImage.Hand);
			}
			Terminate(ErrorCode.ERROR_INVALID_FUNCTION);
		}
	}

	private static bool TryAutoElevateForFrameGen()
	{
		try
		{
			if (!Settings.Prop.FrameGenAutoElevate)
				return false;
			if (LaunchSettings == null || !LaunchSettings.WatcherFlag.Active)
				return false;
			if (Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex <= 0)
				return false;
			if (Voidstrap.Utility.ProcessElevation.IsAdministrator())
				return false;
			if (LaunchSettings.AdminRetriedFlag.Active)
				return false;
			Logger.WriteLine("App::OnStartup", "Restarting elevated so Frame Generation can read Roblox's real frametime");
			if (Voidstrap.Utility.ProcessElevation.TryRestartElevated(LaunchSettings.Args.Concat(["-adminretried"])))
			{
				Current?.Dispatcher.Invoke(() => Current.Shutdown());
				return true;
			}
			Logger.WriteLine("App::OnStartup", "Administrator was declined, Frame Generation continues with capture timing");
			return false;
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::TryAutoElevateForFrameGen", ex);
			return false;
		}
	}

	private static readonly System.Buffers.SearchValues<char> _spaceTabQuote = System.Buffers.SearchValues.Create([' ', '\t', '"']);

	private static string BuildElevatedArgs(string[] args)
	{
		var sb = new StringBuilder();
		foreach (string a in args)
		{
			if (sb.Length > 0)
				sb.Append(' ');
			if (a.Length == 0 || a.AsSpan().IndexOfAny(_spaceTabQuote) >= 0)
				sb.Append('"').Append(a.Replace("\"", "\\\"")).Append('"');
			else
				sb.Append(a);
		}
		if (sb.Length > 0)
			sb.Append(' ');
		sb.Append("-adminretried");
		return sb.ToString();
	}

	private const int NativeRuntimeMissingExitCode = 78;

	protected override async void OnStartup(StartupEventArgs e)
	{
		LinuxUiPerformance.Mark("Startup entered");
		RegisterExceptionHandlers();
		if (!Voidstrap.Utility.LinuxRuntimePreflight.Verify())
		{
			Shutdown(NativeRuntimeMissingExitCode);
			return;
		}

		try
		{
			VpnHttpClient.Initialize();
			base.OnStartup(e);
			await StartAsync(e.Args);
		}
		catch (Exception ex)
		{
			FinalizeExceptionHandling(ex);
		}
	}

	private async Task StartAsync(string[] args)
	{
		LinuxUiPerformance.Install();
		TryStartup("Shared GPU device", Voidstrap.UI.LinuxSharedGpuDevice.Install);
		TryStartup("Subpixel text", Voidstrap.UI.LinuxSubpixelText.Install);
		TryStartup("Font catalog order", Voidstrap.UI.LinuxFontCatalog.Install);
		TryStartup("Text fallback", Voidstrap.UI.LinuxTextFallback.Install);
		TryStartup("Focus style", DisableFocusVisuals);
		TryStartup("Portable popups", EmbedPortablePopups);
		TryStartup("Portable tooltips", DisablePortableToolTips);
		TryStartup("Locale", Locale.Initialize);
		TryStartup("Icon font", Voidstrap.Utility.IconFontLoader.Install);
		TryStartup("Rounded window chrome", Voidstrap.UI.RoundedWindowChrome.Install);
		TryStartup("Text guard", Voidstrap.UI.LinuxTextGuard.Install);
		TryStartup("Hyperlink routing", Voidstrap.UI.LinuxInlineText.Install);
		TryStartup("Dropdown lifecycle", Voidstrap.UI.LinuxComboBoxGuard.Install);
		TryStartup("Pointer hit testing", Voidstrap.UI.LinuxPointerHitTest.Install);
		TryStartup("Grid scrolling", Voidstrap.UI.LinuxDataGridScroll.Install);
		TryStartup("Image guard", Voidstrap.Utility.DynamicRenderSystem.InstallLinuxImageGuard);
		TryStartup("Progress bar motion", Voidstrap.UI.SmoothProgress.Install);
		if (OperatingSystem.IsLinux())
		{
			base.Exit += OnLinuxExitMarkRenderer;
			Voidstrap.Utility.LinuxStartup.BeginRendererProbe();
			if (Voidstrap.Utility.LinuxStartup.SafeMode)
				Logger.WriteLine("App::OnStartup", "Safe mode is on after an unstable launch, subpixel text, the shared GPU device and hidden window reveal are off");
			EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLinuxWindowLoaded));
		}

		LaunchSettings = new LaunchSettings(args);
		if (LaunchSettings.DeferredCleanupFlag.Active)
		{
			await Installer.RunDeferredCleanupAsync(LaunchSettings.DeferredCleanupFlag.Data);
			Terminate();
			return;
		}
		if (LaunchSettings.AssetWarpGuardFlag.Active)
		{
			await Voidstrap.Integrations.AssetProxy.AssetProxyRouting.RunCleanupGuardAsync(LaunchSettings.AssetWarpGuardFlag.Data);
			Terminate();
			return;
		}
		if (LaunchSettings.AssetWarpCleanupFlag.Active)
		{
			Voidstrap.Integrations.AssetProxy.AssetProxyRouting.RunScheduledCleanup();
			Terminate();
			return;
		}
		ConfigureBuildIdentity();
		if (LaunchSettings.FactoryResetFlag.Active)
		{
			RunFactoryReset(LaunchSettings.FactoryResetFlag.Data);
			return;
		}
		bool headlessLaunch = LaunchSettings.NvApplyFlag.Active || LaunchSettings.WindowAuditFlag.Active || LaunchSettings.TelemetryBlockFlag.Active;
		bool portableLinux = Voidstrap.Utility.Platform.IsLinux;
		string? installLocation;
		if (portableLinux)
		{
			Voidstrap.Platform.IPlatformHost? host = Voidstrap.Utility.Platform.RuntimeHost;
			if (host == null)
			{
				Frontend.ShowMessageBox("Voidstrap could not initialize Linux platform services.", MessageBoxImage.Hand);
				Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
				return;
			}

			Voidstrap.Platform.OperationResult directoryResult = await host.Paths.EnsureDirectoriesAsync(_lifetimeCancellation.Token);
			if (!directoryResult.Succeeded)
			{
				Frontend.ShowMessageBox("Voidstrap could not prepare its Linux data folders: " + (directoryResult.Failure?.Message ?? "Unknown error"), MessageBoxImage.Hand);
				Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
				return;
			}

			string? applicationPath = Environment.ProcessPath;
			if (string.IsNullOrWhiteSpace(applicationPath))
			{
				Frontend.ShowMessageBox("Voidstrap could not determine its application path.", MessageBoxImage.Hand);
				Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
				return;
			}

			Paths.InitializePortable(host.Paths.Storage, applicationPath);
			installLocation = Paths.Base;
		}
		else
		{
			installLocation = InstallLocationResolver.Resolve();
			if (installLocation == null && headlessLaunch)
			{
				installLocation = Path.GetDirectoryName(Paths.Process);
			}
		}

		if (installLocation == null)
		{
			LaunchInstaller();
			return;
		}
		if (portableLinux
			&& !headlessLaunch
			&& !LaunchSettings.WatcherFlag.Active
			&& !File.Exists(App.Settings.FileLocation))
		{
			LaunchInstaller();
			return;
		}

		if (!portableLinux)
		{
			Paths.Initialize(installLocation);
		}
		if (!portableLinux && !headlessLaunch && !EnsureInstalledExecutable())
		{
			return;
		}

		Logger.Initialize(LaunchSettings.UninstallFlag.Active);
		LinuxUiPerformance.Mark("Logger ready");
		if (!Logger.Initialized && !Logger.NoWriteMode)
		{
			Logger.WriteLine("App::OnStartup", "Possible duplicate launch detected, terminating.");
			Terminate();
			return;
		}
		if (LaunchSettings.NvApplyFlag.Active || LaunchSettings.TelemetryBlockFlag.Active)
		{
			LaunchHandler.ProcessLaunchArgs();
			return;
		}
		if (!LaunchSettings.WatcherFlag.Active && !headlessLaunch)
		{
			LaunchHandler.CloseOtherInstances();
		}
		LogResolvedPaths();

		if (Paths.LegacyLayoutReset && !headlessLaunch && !LaunchSettings.QuietFlag.Active && !LaunchSettings.UninstallFlag.Active && !LaunchSettings.WatcherFlag.Active)
		{
			Logger.WriteLine("App::OnStartup", "The previous install was cleared, running first time setup");
			TryStartup("Telemetry block cleanup", ClearLeftoverTelemetryBlock);
			LaunchHandler.LaunchInstaller();
			return;
		}

		Paths.EnsureDirectories();

		if (!portableLinux)
		{
			TryStartup("Cloud folder handling", PrepareCloudSyncedInstall);
		}
		long persistentStateStarted = Stopwatch.GetTimestamp();
		LoadPersistentState();
		LinuxUiPerformance.Duration("Persistent state", persistentStateStarted);
		if (!portableLinux)
		{
			TryStartup("Install location repair", () => InstallLocationResolver.Repair(Paths.Base));
		}
		TryStartup("Render acceleration", Voidstrap.Utility.RenderAcceleration.ApplyProcess);
		TryStartup("Roblox app storage", () => Voidstrap.Integrations.RobloxAppStorage.Apply());
		TryStartup("Roblox global settings repair", () => GlobalSettings.RepairFile());
		if (!LaunchSettings.WatcherFlag.Active)
		{
			TryStartup("AssetWarp route cleanup", () =>
			{
				Voidstrap.Integrations.AssetProxy.AssetProxyServer.CleanupStaleState();
			});
		}
		if (TryAutoElevateForFrameGen())
		{
			return;
		}
		if (LaunchSettings.WatcherFlag.Active)
		{
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				TryStartup("Linux animation parity", LinuxAnimationParity.Apply);
			TryStartup("Linux screen metrics", LinuxScreenMetrics.Apply);
			TryStartup("Linux window state", LinuxWindowState.Install);
			}
			InitializeWatcherServices();
			InitializeLanguage();
			LaunchHandler.ProcessLaunchArgs();
			return;
		}

		long servicesStarted = Stopwatch.GetTimestamp();
		InitializeServices();
		LinuxUiPerformance.Duration("Services", servicesStarted);
		long appearanceStarted = Stopwatch.GetTimestamp();
		InitializeAppearance();
		LinuxUiPerformance.Duration("Appearance", appearanceStarted);
		long languageStarted = Stopwatch.GetTimestamp();
		InitializeLanguage();
		LinuxUiPerformance.Duration("Language", languageStarted);
		if (!portableLinux && !LaunchSettings.BypassUpdateCheck)
		{
			try
			{
				await Installer.HandleUpgradeAsync();
			}
			catch (Exception ex)
			{
				Logger.WriteException("App::OnStartup::Upgrade", ex);
			}
		}
		TryStartup("API registration", WindowsRegistry.RegisterApis);
		TryStartup("Theme protocol cleanup", () => WindowsRegistry.Unregister("voidstrap"));
		LaunchHandler.ProcessLaunchArgs();
	}

	private void RegisterExceptionHandlers()
	{
		DispatcherUnhandledException += GlobalExceptionHandler;
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
#if !CROSSPLAT
		if (Voidstrap.Utility.Platform.IsWindows)
		{
			try
			{
				System.Windows.Forms.Application.ThreadException += OnWindowsFormsThreadException;
			}
			catch (Exception ex)
			{
				Logger.WriteLine("App::RegisterExceptionHandlers", "Could not hook the forms thread exception: " + ex.Message);
			}
		}
#endif
	}

	private void UnregisterExceptionHandlers()
	{
		DispatcherUnhandledException -= GlobalExceptionHandler;
		TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
		AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
#if !CROSSPLAT
		if (Voidstrap.Utility.Platform.IsWindows)
		{
			try
			{
				System.Windows.Forms.Application.ThreadException -= OnWindowsFormsThreadException;
			}
			catch
			{
			}
		}
#endif
	}

#if !CROSSPLAT
	private void OnWindowsFormsThreadException(object? sender, System.Threading.ThreadExceptionEventArgs e)
	{
		try
		{
			Logger.WriteException("App::WindowsFormsThreadException", UnwrapException(e.Exception));
		}
		catch
		{
		}
	}
#endif

	private static bool _focusVisualsHandled;

	private static void DisableFocusVisuals()
	{
		if (_focusVisualsHandled || !Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		_focusVisualsHandled = true;
		if (FrameworkElement.FocusVisualStyleProperty.GetMetadata(typeof(System.Windows.Controls.Control)).DefaultValue == null)
		{
			return;
		}
		try
		{
			FrameworkElement.FocusVisualStyleProperty.OverrideMetadata(typeof(System.Windows.Controls.Control), new FrameworkPropertyMetadata(null));
		}
		catch (ArgumentException)
		{
			Logger.WriteLine("App::DisableFocusVisuals", "Focus visual metadata was already overridden, leaving it alone.");
		}
	}

	private static void DisablePortableToolTips()
	{
		if (Voidstrap.Utility.Platform.IsWindows || _portableToolTipsDisabled)
		{
			return;
		}
		EventManager.RegisterClassHandler(
			typeof(FrameworkElement),
			System.Windows.Controls.ToolTipService.ToolTipOpeningEvent,
			new System.Windows.Controls.ToolTipEventHandler(OnPortableToolTipOpening),
			true);
		_portableToolTipsDisabled = true;
	}

	private static void EmbedPortablePopups()
	{
		if (Voidstrap.Utility.Platform.IsWindows)
		{
			return;
		}
		Type? bridgeType = Type.GetType("System.Windows.Media.ProGPU.WpfPortablePopupBridge, ProGPU.Wpf", false);
		PropertyInfo? factory = bridgeType?.GetProperty("NativePopupHostFactory", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo? invoke = factory?.PropertyType.GetMethod("Invoke");
		if (factory == null || invoke == null)
		{
			throw new InvalidOperationException("Portable popup integration is unavailable");
		}
		ParameterExpression[] parameters = [.. invoke.GetParameters()
			.Select(parameter => LinqExpression.Parameter(parameter.ParameterType, parameter.Name))];
		MethodInfo release = typeof(Voidstrap.UI.LinuxPointerHitTest).GetMethod(nameof(Voidstrap.UI.LinuxPointerHitTest.ReleasePopupSource))!;
		LinqExpression body = LinqExpression.Default(invoke.ReturnType);
		ParameterExpression? popupSource = parameters.FirstOrDefault(parameter => parameter.Type.Name == "IPortablePresentationSourceHost");
		if (popupSource != null)
			body = LinqExpression.Block(LinqExpression.Call(release, LinqExpression.Convert(popupSource, typeof(object))), body);
		Delegate embeddedFactory = LinqExpression.Lambda(factory.PropertyType, body, parameters).Compile();
		factory.SetValue(null, embeddedFactory);
	}

	private static void OnPortableToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e)
	{
		e.Handled = true;
	}

	private static BuildMetadataAttribute ResolveBuildMetadata()
	{
		BuildMetadataAttribute metadata = Assembly.GetExecutingAssembly().GetCustomAttribute<BuildMetadataAttribute>()
			?? new BuildMetadataAttribute(DateTime.UnixEpoch.ToString("o"), Environment.MachineName, "", "");
		if (metadata.Timestamp.ToUniversalTime() > DateTime.UnixEpoch)
		{
			return metadata;
		}
		try
		{
			string? executable = Environment.ProcessPath;
			if (!string.IsNullOrEmpty(executable) && File.Exists(executable))
			{
				metadata.Timestamp = File.GetLastWriteTime(executable);
			}
		}
		catch (Exception ex)
		{
			Logger?.WriteLine("App::ResolveBuildMetadata", "Could not resolve the build timestamp: " + ex.Message);
		}
		return metadata;
	}

	private static void ConfigureBuildIdentity()
	{
		Logger.WriteLine("App::OnStartup", "Starting Voidstrap v" + Version);
		string userAgent = "Voidstrap/" + Version;
		if (IsActionBuild)
		{
			Logger.WriteLine("App::OnStartup", $"Compiled {BuildMetadata.Timestamp.ToFriendlyString()} from commit {BuildMetadata.CommitHash} ({BuildMetadata.CommitRef})");
			userAgent += IsProductionBuild ? " (Production)" : $" (Artifact {BuildMetadata.CommitHash}, {BuildMetadata.CommitRef})";
		}
		else
		{
			string machine = BuildMetadata.Machine ?? "";
			Logger.WriteLine("App::OnStartup", string.IsNullOrWhiteSpace(machine)
				? "Compiled " + BuildMetadata.Timestamp.ToFriendlyString()
				: "Compiled " + BuildMetadata.Timestamp.ToFriendlyString() + " from " + machine);
			userAgent += string.IsNullOrWhiteSpace(machine)
				? " (Build)"
				: " (Build " + Convert.ToBase64String(Encoding.UTF8.GetBytes(machine)) + ")";
		}
		HttpClient.DefaultRequestHeaders.Remove("User-Agent");
		HttpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
	}

	private static void LaunchInstaller()
	{
		Logger.Initialize(useTempDir: true);
		LaunchHandler.LaunchInstaller();
	}

	private static void RunFactoryReset(string? previousProcessId)
	{
		try
		{
			if (int.TryParse(previousProcessId, out int processId) && processId > 0 && processId != Environment.ProcessId)
			{
				using Process previousProcess = Process.GetProcessById(processId);
				if (!previousProcess.WaitForExit(15000))
				{
					throw new InvalidOperationException("The previous Voidstrap process did not close in time");
				}
			}
		}
		catch (ArgumentException)
		{
		}
		catch (Exception ex)
		{
			Logger.Initialize(useTempDir: true);
			Logger.WriteException("App::RunFactoryReset", ex);
			Frontend.ShowMessageBox("Factory reset could not start: " + ex.Message, MessageBoxImage.Hand);
			Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
			return;
		}

		try
		{
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				Voidstrap.Platform.IPlatformHost? host = Voidstrap.Utility.Platform.RuntimeHost;
				string? applicationPath = Environment.ProcessPath;
				if (host == null || string.IsNullOrWhiteSpace(applicationPath))
				{
					throw new InvalidOperationException("Linux platform storage is unavailable");
				}
				Paths.InitializePortable(host.Paths.Storage, applicationPath);
			}
			else
			{
				string? installLocation = InstallLocationResolver.Resolve() ?? Path.GetDirectoryName(Paths.Process);
				if (string.IsNullOrWhiteSpace(installLocation))
				{
					LaunchInstaller();
					return;
				}
				Paths.Initialize(installLocation);
			}

			Voidstrap.Integrations.AssetProxy.AssetProxyServer.CleanupStaleState();
			Voidstrap.Integrations.AssetProxy.AssetProxyServer.RemoveCertificates();
			if (Voidstrap.Integrations.TelemetryBlocker.IsApplied() && !Voidstrap.Integrations.TelemetryBlocker.Set(false))
			{
				throw new InvalidOperationException("The telemetry block could not be removed");
			}
			Voidstrap.Integrations.RobloxAppStorage.Reset();
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				Voidstrap.Utility.LinuxDesktopEntry.Remove();
			}
			else
			{
				ResetGeneratedShortcuts();
			}
			Paths.ResetUserData();
		}
		catch (Exception ex)
		{
			Logger.Initialize(useTempDir: true);
			Logger.WriteException("App::RunFactoryReset", ex);
			Frontend.ShowMessageBox("Factory reset could not finish: " + ex.Message, MessageBoxImage.Hand);
			Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
			return;
		}

		LaunchInstaller();
	}

	private static void ResetGeneratedShortcuts()
	{
		string[] shortcuts =
		[
			Path.Combine(Paths.Desktop, "Voidstrap.lnk"),
			Path.Combine(Paths.Desktop, Strings.LaunchMenu_LaunchRoblox + ".lnk"),
			Path.Combine(Paths.Desktop, Strings.LaunchMenu_LaunchRobloxStudio + ".lnk"),
			Path.Combine(Paths.Desktop, Strings.Menu_Title + ".lnk"),
			Path.Combine(Paths.WindowsStartMenu, "Voidstrap.lnk")
		];

		foreach (string shortcut in shortcuts.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (File.Exists(shortcut))
			{
				File.Delete(shortcut);
			}
		}
	}

	private static bool EnsureInstalledExecutable()
	{
		if (string.Equals(Paths.Process, Paths.Application, StringComparison.OrdinalIgnoreCase) || File.Exists(Paths.Application))
		{
			return true;
		}
		try
		{
			Directory.CreateDirectory(Paths.Base);
			File.Copy(Paths.Process, Paths.Application);
			return true;
		}
		catch (Exception ex)
		{
			Logger.Initialize(useTempDir: true);
			Logger.WriteException("App::EnsureInstalledExecutable", ex);
			LaunchHandler.LaunchInstaller();
			return false;
		}
	}

	private static void ClearLeftoverTelemetryBlock()
	{
		if (!Voidstrap.Integrations.TelemetryBlocker.IsApplied())
		{
			return;
		}
		if (!Voidstrap.Utility.ProcessElevation.IsAdministrator())
		{
			Logger.WriteLine("App::OnStartup", "The hosts telemetry block is still active from the previous install, run Voidstrap as administrator to clear it");
			return;
		}
		if (Voidstrap.Integrations.TelemetryBlocker.Remove())
		{
			Settings.Prop.BlockRobloxTelemetry = false;
			Logger.WriteLine("App::OnStartup", "Cleared the hosts telemetry block left over from the previous install");
		}
	}

	private static void PrepareCloudSyncedInstall()
	{
		if (!Paths.CloudSynced)
		{
			return;
		}
		Logger.WriteLine("App::OnStartup", "The install folder is synced by a cloud provider, Roblox data is stored under " + Paths.RobloxBase);
		Voidstrap.Utility.CloudFiles.Hydrate(Paths.Application);
		Voidstrap.Utility.CloudFiles.PinInstallRoot();
	}

	private static void LogResolvedPaths()
	{
		Logger.WriteLine("App::OnStartup", "Loaded from " + Paths.Process);
		Logger.WriteLine("App::OnStartup", "Temp path is " + Paths.Temp);

		if (!Voidstrap.Utility.Platform.IsWindows)
			return;

		Logger.WriteLine("App::OnStartup", "WindowsStartMenu path is " + Paths.WindowsStartMenu);
		Logger.WriteLine("App::OnStartup", "DLL hijack protection pinned " + Voidstrap.Utility.LoaderHardening.PinnedModuleCount + " system modules");
	}

	private static void LoadPersistentState()
	{
		DownloadStats.Load();
		State.Load();
		RobloxState.Load();
		Settings.Load();
		ResetWindowBackdropOnce();
		FastFlags.Load(alertFailure: false);
	}

	private const int WindowBackdropResetVersion = 1;

	private static void ResetWindowBackdropOnce()
	{
		if (Settings.Prop.WindowBackdropResetVersion >= WindowBackdropResetVersion)
			return;
		Settings.Prop.WindowBackdrop = Voidstrap.Models.Persistable.AppSettings.DefaultWindowBackdrop;
		Settings.Prop.WindowBackdropResetVersion = WindowBackdropResetVersion;
		Settings.Save();
		Logger.WriteLine("App::ResetWindowBackdropOnce", "Window backdrop reset to the default " + Settings.Prop.WindowBackdrop);
	}

	private void InitializeServices()
	{
		TryStartup("Controller service", UI.ControllerService.Initialize);
		InstallEnabledOverlays();
		TryStartup("Telemetry blocker", Voidstrap.Integrations.TelemetryBlocker.SyncSettingFromState);
		if (Voidstrap.Utility.Platform.SupportsAudioDucking)
		{
			TryStartup("Audio ducking", Voidstrap.Integrations.AudioDucker.ApplyFromSettings);
			TryStartup("Headset audio", Voidstrap.Integrations.HeadsetAudio.ApplyFromSettings);
		}
		if (!LaunchSettings.IsHelperInvocation)
			TryStartup("Snap Tap", Voidstrap.KeyRouting.SnapTapHook.ApplyFromSettings);
		if (!LaunchSettings.WatcherFlag.Active)
		{
			TryStartup("Rojo updater", Voidstrap.Integrations.Rojo.RojoManager.AutoUpdate);
			if (!Voidstrap.Utility.Platform.IsLinux)
				_ = RefreshRemoteDataAsync(_lifetimeCancellation.Token);
		}
		if (!LaunchSettings.WatcherFlag.Active && !LaunchSettings.IsHelperInvocation)
		{
			if (!Voidstrap.Utility.Platform.IsLinux)
			{
				_ = Voidstrap.Utility.SavedAccounts.EnsureCurrentAccountSavedAsync(_lifetimeCancellation.Token);
				TryStartup("ORC updater", () => _ = Task.Run(async delegate
				{
					Voidstrap.Integrations.ClassicHostRedirect.CleanStaleRedirect();
					try
					{
						await Voidstrap.Utility.ClassicClients.AutoUpdateAllAsync(_lifetimeCancellation.Token).ConfigureAwait(false);
					}
					catch (Exception ex)
					{
						Logger?.WriteLine("App::OrcAutoUpdate", "Auto update failed: " + ex.Message);
					}
				}));
				_ = CleanupTempAsync(_lifetimeCancellation.Token);
			}
			if (LaunchSettings.RobloxLaunchMode == LaunchMode.None && Settings.Prop.CompressRobloxInstalls && Voidstrap.Utility.RobloxInstallCompression.Supported)
				_ = CompressIdleInstallsAsync(_lifetimeCancellation.Token);
		}
		TryStartup("CPU core limiter", CpuCoreLimiter.ApplyConfiguredLimit);
		if (!Voidstrap.Utility.Platform.IsLinux)
			TryStartup("GPU inventory warmup", () => Task.Run(() => _ = Voidstrap.Utility.GpuInventory.HasNvidia));
		TryStartup("Custom RPC", StartCustomRpcIfEnabled);
	}

	internal void StartLinuxDeferredServices()
	{
		if (!Voidstrap.Utility.Platform.IsLinux || LaunchSettings.WindowAuditFlag.Active || Interlocked.Exchange(ref _linuxDeferredStarted, 1) != 0)
			return;
		_ = RunLinuxDeferredServicesAsync(_lifetimeCancellation.Token);
	}

	private async Task StartLinuxDeferredFallbackAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(1500, token).ConfigureAwait(false);
			StartLinuxDeferredServices();
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
	}

	private async Task RunLinuxDeferredServicesAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(750, token).ConfigureAwait(false);
			_ = TextFontInstaller.RefreshPendingAsync(token);
			if (!Voidstrap.Platform.Linux.LinuxFlatpakHost.IsSandboxed && !LaunchSettings.UninstallFlag.Active)
				_ = Task.Run(() => TryStartup("Linux desktop integration", () => Voidstrap.Utility.LinuxDesktopEntry.EnsureInstalled(Paths.Application)), token);
			if (LaunchSettings.WatcherFlag.Active)
				return;
			_ = RefreshRemoteDataAsync(token);
			if (LaunchSettings.IsHelperInvocation)
				return;
			_ = Voidstrap.Utility.SavedAccounts.EnsureCurrentAccountSavedAsync(token);
			_ = CleanupTempAsync(token);
			_ = Task.Run(() => _ = Voidstrap.Utility.GpuInventory.HasNvidia, token);
			_ = RunLinuxOrcUpdaterAsync(token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Logger.WriteLine("App::LinuxDeferredServices", "Background startup work failed: " + ex.Message);
		}
	}

	private static async Task RunLinuxOrcUpdaterAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(3000, token).ConfigureAwait(false);
			await Task.Run(Voidstrap.Integrations.ClassicHostRedirect.CleanStaleRedirect, token).ConfigureAwait(false);
			await Voidstrap.Utility.ClassicClients.AutoUpdateAllAsync(token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Logger.WriteLine("App::OrcAutoUpdate", "Auto update failed: " + ex.Message);
		}
	}

	private static async Task CompressIdleInstallsAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(TimeSpan.FromSeconds(45), token).ConfigureAwait(false);
			await Voidstrap.UI.ViewModels.Settings.ChannelViewModel.RunInstallCompressionAsync(true, false, token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Logger?.WriteLine("App::CompressIdleInstalls", "Background compression failed: " + ex.Message);
		}
	}

	private static void InitializeWatcherServices()
	{
		TryStartup("Memory manager", Voidstrap.Utility.MemoryManager.Start);
		Voidstrap.Integrations.Overlays.OverlayHub.MarkHostProcess();
		InstallEnabledOverlays();
		if (Voidstrap.Utility.Platform.SupportsAudioDucking)
		{
			TryStartup("Audio ducking", Voidstrap.Integrations.AudioDucker.ApplyFromSettings);
			TryStartup("Headset audio", Voidstrap.Integrations.HeadsetAudio.ApplyFromSettings);
		}
		TryStartup("Snap Tap", Voidstrap.KeyRouting.SnapTapHook.ApplyFromSettings);
	}

	private static void InstallEnabledOverlays()
	{
		bool nativeOverlays = Voidstrap.Utility.Platform.SupportsOverlays;
		bool linuxHomepage = Voidstrap.Utility.Platform.IsLinux
			&& Voidstrap.Integrations.Overlays.OverlaySettings.HomepageBackgroundEnabled;
		if (!nativeOverlays && !linuxHomepage)
		{
			return;
		}
		if (nativeOverlays && Settings.Prop.RiShadeEnabled)
		{
			TryStartup("RiShade", Voidstrap.Integrations.RiShade.RiShadeManager.Install);
		}
		if (nativeOverlays && Voidstrap.Integrations.AntiAliasing.AntiAliasingSettings.MethodIndex > 0)
		{
			TryStartup("Anti aliasing", Voidstrap.Integrations.AntiAliasing.AntiAliasingManager.Install);
		}
		if (nativeOverlays && Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0)
		{
			TryStartup("Frame generation", Voidstrap.Integrations.FrameGeneration.FrameGenManager.Install);
		}
		TryStartup("Overlay hub", () => Voidstrap.Integrations.Overlays.OverlayHub.Refresh());
	}

	private static void InitializeAppearance()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			TryStartup("Linux animation parity", LinuxAnimationParity.Apply);
			TryStartup("Linux screen metrics", LinuxScreenMetrics.Apply);
			TryStartup("Linux window state", LinuxWindowState.Install);
			TryStartup("Render loop warm up", () =>
			{
				EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(WarmRenderLoop));
			});
			TryStartup("Linux image scaling", () =>
			{
				EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Image), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyLinuxImageScaling));
			});
		}
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			TryStartup("Smooth font edges", () => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplySmoothFontEdges)));
		}
		if (Settings.Prop.ClearFont)
		{
			TryStartup("Clear font", () => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyClearFont)));
		}
		TryStartup("Linux file dialogs", Voidstrap.UI.LinuxFileDialog.Install);
		TryStartup("Linux editor compatibility", Voidstrap.UI.LinuxEditorCompat.Install);
		TryStartup("Smooth scrolling", () =>
		{
			Wpf.Ui.Controls.SmoothScroll.SetGlobalEnabled(!Voidstrap.Utility.Platform.IsLinux && Settings.Prop.SmooothBARRyesirikikthxlucipook);
			Wpf.Ui.Controls.SmoothScroll.Register();
		});
		TryStartup("Global background", GlobalBackground.Register);
		TryStartup("Application font", AppFont.Initialize);
		TryStartup("Memory manager", Voidstrap.Utility.MemoryManager.Start);
		TryStartup("Render diagnostics", LogRenderMode);
	}

	private static void WarmRenderLoop(object sender, RoutedEventArgs e)
	{
		if (sender is Window window)
		{
			Wpf.Ui.Animations.RenderReady.Warm(window);
		}
	}

	private static void ApplySmoothFontEdges(object sender, RoutedEventArgs e)
	{
		if (sender is not Window window)
			return;
		try
		{
			TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
			RenderOptions.SetClearTypeHint(window, ClearTypeHint.Enabled);
		}
		catch (Exception ex)
		{
			Logger.WriteLine("App::ApplySmoothFontEdges", "Smooth font edges could not be applied: " + ex.Message);
		}
	}

	private static void ApplyClearFont(object sender, RoutedEventArgs e)
	{
		if (sender is not Window window)
		{
			return;
		}
		TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
		TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
		window.UseLayoutRounding = true;
		window.SnapsToDevicePixels = true;
		RenderOptions.SetClearTypeHint(window, ClearTypeHint.Enabled);
	}

	private static void LogRenderMode()
	{
		int renderTier = RenderCapability.Tier >> 16;
		bool environmentSoftware = Environment.GetEnvironmentVariable("LIBGL_ALWAYS_SOFTWARE") == "1";
		string renderMode = Settings.Prop.WPFSoftwareRender || LaunchSettings.NoGPUFlag.Active || environmentSoftware ? "software" : renderTier == 0 ? "software, GPU tier 0" : "hardware";
		Logger.WriteLine("App::OnStartup", $"WPF render tier {renderTier}, rendering mode: {renderMode}");
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			Logger.WriteLine("App::OnStartup", "Linux session: " + (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "unknown")
				+ ", windowing: " + (Environment.GetEnvironmentVariable("PROGPU_WPF_LINUX_WINDOWING") ?? "auto")
				+ ", renderer stage: " + (Environment.GetEnvironmentVariable("VOIDSTRAP_GPU_RETRY") ?? "default")
				+ ", backend: " + (Environment.GetEnvironmentVariable("VOIDSTRAP_RENDER_BACKEND") ?? "Auto"));
		}
	}

	private static void ApplyLinuxImageScaling(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.Image image)
			RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
	}

	private static void InitializeLanguage()
	{
		if (!Locale.SupportedLocales.ContainsKey(Settings.Prop.Locale))
		{
			Settings.Prop.Locale = "nil";
			Settings.Save();
		}
		Locale.Set(Settings.Prop.Locale);
		TryStartup("Resource proxy", ResourceProxy.Inject);
		TryStartup("Translation service", TranslationService.Initialize);
		TryStartup("Language refresher", Voidstrap.UI.LiveLanguageRefresher.Initialize);
	}

	private static async Task RefreshRemoteDataAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Run(RemoveWebsiteLeftovers, cancellationToken).ConfigureAwait(false);
			await Voidstrap.Utility.RemoteData.RefreshAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::RefreshRemoteData", ex);
		}
	}

	private static void RemoveWebsiteLeftovers()
	{
		string documents = Paths.DocumentsData;
		string[] files =
		[
			Path.Combine(documents, "WebsiteAuth.json"),
			Path.Combine(documents, "WebsiteAuth.json.bak"),
			Path.Combine(documents, "WebsiteAuth.key"),
			Path.Combine(documents, "BootstrapperChat.json"),
			Path.Combine(Paths.Config, "WebsiteSaveQueue.dat"),
			Path.Combine(Paths.Config, "WebsiteHistorySync.json"),
			Path.Combine(Paths.Config, "QuestSession.json"),
			Path.Combine(Paths.Cache, "WebsiteProfile.json"),
			Path.Combine(Paths.Cache, "TranslationsRemote.json"),
			Path.Combine(Paths.Temp, "blackmarket-bg.png")
		];
		foreach (string file in files)
		{
			TryDeleteLeftover(file);
		}
		try
		{
			if (Directory.Exists(Paths.Config))
			{
				foreach (string file in Directory.EnumerateFiles(Paths.Config, "WebsiteCache_*.json"))
				{
					TryDeleteLeftover(file);
				}
			}
			foreach (string folder in new[] { Path.Combine(Paths.Cache, "Forums"), Path.Combine(Paths.Cache, "Banners") })
			{
				if (Directory.Exists(folder))
				{
					Directory.Delete(folder, recursive: true);
				}
			}
			if (Directory.Exists(documents) && !Directory.EnumerateFileSystemEntries(documents).Any())
			{
				Directory.Delete(documents);
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("App::RemoveWebsiteLeftovers", "Could not remove old website data: " + ex.Message);
		}
	}

	private static void TryDeleteLeftover(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
				Logger.WriteLine("App::RemoveWebsiteLeftovers", "Removed " + Path.GetFileName(path));
			}
		}
		catch (Exception ex)
		{
			Logger.WriteLine("App::RemoveWebsiteLeftovers", "Could not remove " + Path.GetFileName(path) + ": " + ex.Message);
		}
	}

	private static async Task CleanupTempAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
			Installer.CleanupStaleBundleExtractions();
			Installer.CleanupUpdateBackups();
			Installer.CleanupOldHelpers();
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::CleanupTemp", ex);
		}
	}

	private static void StartCustomRpcIfEnabled()
	{
		string configPath = Path.Combine(Paths.UserData, "discord-rpc.json");
		if (!File.Exists(configPath))
		{
			return;
		}
		if (JsonFile.TryLoad<JsonElement>(configPath, JsonOptions.Tolerant, out JsonElement config, out bool recovered, out Exception? failure, 4194304) && config.TryGetProperty("AutoStartRpc", out JsonElement autoStart) && autoStart.ValueKind == JsonValueKind.True)
		{
			_ = RPCCustomizerViewModel.Shared;
		}
		if (recovered)
			Logger.WriteLine("App::StartCustomRpcIfEnabled", "Recovered the last valid RPC configuration backup");
		if (failure != null && config.ValueKind == JsonValueKind.Undefined)
			Logger.WriteLine("App::StartCustomRpcIfEnabled", "RPC configuration is invalid: " + failure.Message);
	}

	private static void TryStartup(string component, Action action)
	{
		long started = Stopwatch.GetTimestamp();
		try
		{
			action();
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::OnStartup::" + component, ex);
		}
		finally
		{
			LinuxUiPerformance.Duration(component, started);
		}
	}


	protected override void OnExit(ExitEventArgs e)
	{
		LinuxUiPerformance.Shutdown();
		UnregisterExceptionHandlers();
		TryShutdown(VpnHttpClient.Shutdown);
		TryShutdown(_lifetimeCancellation.Cancel);
		TryShutdown(Voidstrap.Utility.ScreenColorEffect.Shutdown);
		TryShutdown(Voidstrap.Integrations.ServerFetchStore.Shutdown);
		TryShutdown(Voidstrap.Utility.MemoryManager.Shutdown);
		TryShutdown(Voidstrap.Utility.LinuxStartup.Shutdown);
		TryShutdown(Voidstrap.UI.ControllerService.Shutdown);
		TryShutdown(Voidstrap.Utility.ClassicIntegrations.Stop);
		TryShutdown(Voidstrap.Integrations.ClassicHostRedirect.RemoveIfStale);
		TryShutdown(Voidstrap.Integrations.RiShade.RiShadeManager.Shutdown);
		TryShutdown(Voidstrap.Integrations.AntiAliasing.AntiAliasingManager.Shutdown);
		TryShutdown(Voidstrap.Integrations.FrameGeneration.FrameGenManager.Shutdown);
		TryShutdown(Voidstrap.Integrations.HeadsetAudio.Shutdown);
		TryShutdown(Voidstrap.Integrations.AudioDucker.Shutdown);
		TryShutdown(Voidstrap.KeyRouting.SnapTapHook.Shutdown);
		TryShutdown(Voidstrap.Integrations.Rojo.RojoManager.Shutdown);
		TryShutdown(Voidstrap.Integrations.Studio.StudioIntegration.Shutdown);
		TryShutdown(AssetProxyServer.Stop);
		TryShutdown(AssetPreloadCache.Shutdown);
		TryShutdown(() => AssetCaptureStore.Shutdown());
		TryShutdown(Voidstrap.Integrations.Fullscreen.FakeExclusiveFullscreen.Shutdown);
		if (Voidstrap.Utility.Platform.IsLinux)
			TryShutdown(Voidstrap.Integrations.Overlays.OverlayHub.Shutdown);
		TryShutdown(Voidstrap.Integrations.Overlays.RobloxWindowTracker.Shutdown);
		TryShutdown(Voidstrap.UI.LiveLanguageRefresher.Shutdown);
		TryShutdown(Voidstrap.UI.Utility.WindowScaling.Shutdown);
		TryShutdown(Voidstrap.Utility.SystemAccent.Shutdown);
		TryShutdown(Voidstrap.Utility.TranslationService.Shutdown);
		TryShutdown(Voidstrap.UI.GlobalBackground.ClearCache);
		TryShutdown(Voidstrap.Utility.DynamicRenderSystem.ClearCache);
		TryShutdown(Settings.FlushDeferred);
		TryShutdown(FastFlags.FlushDeferred);
		TryShutdown(StopCustomRpc);
		TryShutdown(DisposeDiscordClient);
		TryShutdown(DisposeMusicPlayer);
		TryShutdown(Voidstrap.Utility.AppNotifications.Shutdown);
		TryShutdown(_httpClient.Dispose);
		TryShutdown(_lifetimeCancellation.Dispose);
		try
		{
			Logger.Dispose();
		}
		catch
		{
		}
		base.OnExit(e);
	}

	private static void TryShutdown(Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			try
			{
				Logger.WriteException("App::OnExit", ex);
			}
			catch
			{
			}
		}
	}

	internal static void StopCustomRpc()
	{
		RPCCustomizerViewModel.SharedOrNull?.Dispose();
	}

	private static void DisposeDiscordClient()
	{
		DiscordClient?.Dispose();
		DiscordClient = null;
	}

	private static void DisposeMusicPlayer()
	{
		if (Application.Current.MainWindow?.DataContext is MusicPlayerViewModel musicPlayerViewModel)
		{
			musicPlayerViewModel.Dispose();
		}
	}
}
