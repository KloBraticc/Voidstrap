using System;
using System.Collections.Generic;
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

	public const string ProjectHelpLink = "https://voidstrapp.pages.dev/documentation";

	public const string ProjectSupportLink = "https://github.com/KloBraticc/Voidstrap/issues/new";
	public const string ProjectFallbackSupportLink = ProjectFallbackRepository + "/issues/new";
	public const string ProjectIssuesLink = "https://github.com/KloBraticc/Voidstrap/issues";
	public const string ProjectFallbackIssuesLink = ProjectFallbackRepository + "/issues";

	public const string RobloxPlayerAppName = "RobloxPlayerBeta";

	public const string RobloxStudioAppName = "RobloxStudioBeta";

	public const string UninstallKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap";

	public const string ApisKey = "Software\\Voidstrap";

	// for dev testing new features I may add in the near future

	public static readonly bool UseLocalWebsite = false;

	public const string WebsiteProductionUrl = "https://voidstrapp.pages.dev";

	public const string WebsiteLocalUrl = "http://localhost:8788";

	private static long _localSiteCheckTicks = -60000;

	private static bool _localSiteOnline;

	private static bool IsLocalWebsiteOnline()
	{
		long tickCount = Environment.TickCount64;
		long sinceCheck = tickCount - System.Threading.Interlocked.Read(ref _localSiteCheckTicks);
		int cacheWindow = _localSiteOnline ? 30000 : 3000;

		if (sinceCheck < cacheWindow)
		{
			return _localSiteOnline;
		}

		System.Threading.Interlocked.Exchange(ref _localSiteCheckTicks, tickCount);
		bool wasOnline = _localSiteOnline;

		try
		{
			Uri uri = new(WebsiteLocalUrl);
			using System.Net.Sockets.TcpClient tcpClient = new();
			Task connectTask = tcpClient.ConnectAsync(uri.Host, uri.Port);
			bool finished = connectTask.Wait(1500);
			if (!finished)
			{
				connectTask.ContinueWith(delegate(Task t)
				{
					_ = t.Exception;
				}, TaskContinuationOptions.OnlyOnFaulted);
			}
			_localSiteOnline = finished && tcpClient.Connected;
		}
		catch
		{
			_localSiteOnline = false;
		}

		if (_localSiteOnline != wasOnline)
		{
			Logger?.WriteLine("App::IsLocalWebsiteOnline", _localSiteOnline
				? "Local website is reachable, using " + WebsiteLocalUrl
				: "Local website is not reachable, using " + WebsiteProductionUrl);
		}

		return _localSiteOnline;
	}

	public static string WebsiteBaseUrl => (UseLocalWebsite && IsLocalWebsiteOnline()) ? WebsiteLocalUrl : WebsiteProductionUrl;

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

	public static DiscordRpcClient? DiscordClient { get; set; }

	public static bool WebsiteTakeoverActive { get; set; }

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
		if (WebsiteTakeoverActive)
		{
			Logger.WriteLine("App::Terminate", "Termination blocked WebsiteTakeoverActive is true.");
			return;
		}
		Logger.WriteLine("App::Terminate", $"Terminating with exit code {(int)exitCode} ({exitCode})");
		ShutdownApplication((int)exitCode);
	}

	public static void SoftTerminate(ErrorCode exitCode = ErrorCode.ERROR_SUCCESS)
	{
		if (WebsiteTakeoverActive)
		{
			Logger.WriteLine("App::SoftTerminate", "Soft termination blocked WebsiteTakeoverActive is true.");
			return;
		}
		if (LaunchSettings?.WindowAuditFlag.Active == true)
		{
			Logger.WriteLine("App::SoftTerminate", "Soft termination blocked, window audit is running.");
			return;
		}
		int exitCodeNum = (int)exitCode;
		Logger.WriteLine("App::SoftTerminate", $"Terminating with exit code {exitCodeNum} ({exitCode})");
		ShutdownApplication(exitCodeNum);
	}

	public static bool RestartApplication(IReadOnlyList<string> arguments)
	{
		try
		{
			string? executable = Environment.ProcessPath;
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
			SoftTerminate();
			return true;
		}
		catch (Exception ex)
		{
			Logger.WriteLine("App::RestartApplication", "Restart failed: " + ex.Message);
			return false;
		}
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

	private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
	{
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
			GithubRelease githubRelease = await GitHubCache.GetJsonWithFallbackAsync<GithubRelease>(ProjectReleaseApi, ProjectFallbackReleaseApi, ReleaseCacheAge(forceRefresh));
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

	protected override async void OnStartup(StartupEventArgs e)
	{
		RegisterExceptionHandlers();
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
		TryStartup("Focus style", DisableFocusVisuals);
		TryStartup("Portable popups", EmbedPortablePopups);
		TryStartup("Portable tooltips", DisablePortableToolTips);
		TryStartup("Emoji renderer", Voidstrap.UI.EmojiTextRenderer.Install);
		TryStartup("Locale", Locale.Initialize);
		TryStartup("Icon font", Voidstrap.Utility.IconFontLoader.Install);
		TryStartup("Rounded window chrome", Voidstrap.UI.RoundedWindowChrome.Install);
		TryStartup("Progress bar motion", Voidstrap.UI.SmoothProgress.Install);

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
		bool headlessLaunch = LaunchSettings.NvApplyFlag.Active || LaunchSettings.WindowAuditFlag.Active;
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

		if (!portableLinux)
		{
			Paths.Initialize(installLocation);
		}
		if (!portableLinux && !headlessLaunch && !EnsureInstalledExecutable())
		{
			return;
		}

		Logger.Initialize(LaunchSettings.UninstallFlag.Active);
		if (!Logger.Initialized && !Logger.NoWriteMode)
		{
			Logger.WriteLine("App::OnStartup", "Possible duplicate launch detected, terminating.");
			Terminate();
			return;
		}
		LogResolvedPaths();
		if (LaunchSettings.NvApplyFlag.Active)
		{
			LaunchHandler.ProcessLaunchArgs();
			return;
		}

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
			TryStartup("Install location repair", () => InstallLocationResolver.Repair(Paths.Base));
		}
		LoadPersistentState();
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
			InitializeWatcherServices();
			InitializeLanguage();
			LaunchHandler.ProcessLaunchArgs();
			return;
		}

		InitializeServices();
		InitializeAppearance();
		InitializeLanguage();
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
		TryStartup("Theme protocol registration", WindowsRegistry.RegisterVoidstrap);
		StartInstalledThemeUpdates();
		LaunchHandler.ProcessLaunchArgs();
	}

	private static void StartInstalledThemeUpdates()
	{
		if (LaunchSettings.QuietFlag.Active || LaunchSettings.UninstallFlag.Active || LaunchSettings.WatcherFlag.Active)
		{
			return;
		}

		_ = Task.Run(async delegate
		{
			try
			{
				await Voidstrap.Integrations.BootstrapperThemes.UpdateInstalledThemesAsync().ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				Logger.WriteLine("App::StartInstalledThemeUpdates", "Could not check installed themes: " + ex.Message);
			}
		});
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
		Delegate embeddedFactory = LinqExpression.Lambda(factory.PropertyType, LinqExpression.Default(invoke.ReturnType), parameters).Compile();
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
			string? installLocation = InstallLocationResolver.Resolve() ?? Path.GetDirectoryName(Paths.Process);
			if (string.IsNullOrWhiteSpace(installLocation))
			{
				LaunchInstaller();
				return;
			}

			Paths.Initialize(installLocation);
			Voidstrap.Integrations.AssetProxy.AssetProxyServer.CleanupStaleState();
			Voidstrap.Integrations.AssetProxy.AssetProxyServer.RemoveCertificates();
			if (Voidstrap.Integrations.TelemetryBlocker.IsApplied() && !Voidstrap.Integrations.TelemetryBlocker.Set(false))
			{
				throw new InvalidOperationException("The telemetry block could not be removed");
			}
			Voidstrap.Integrations.RobloxAppStorage.Reset();
			ResetGeneratedShortcuts();
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
		Logger.WriteLine("App::OnStartup", "WindowsStartMenu path is " + Paths.WindowsStartMenu);
		Logger.WriteLine("App::OnStartup", "DLL hijack protection pinned " + Voidstrap.Utility.LoaderHardening.PinnedModuleCount + " system modules");
	}

	private static void LoadPersistentState()
	{
		DownloadStats.Load();
		State.Load();
		RobloxState.Load();
		Settings.Load();
		FastFlags.Load(alertFailure: false);
	}

	private void InitializeServices()
	{
		TryStartup("Controller service", UI.ControllerService.Initialize);
		TryStartup("Website save queue", Voidstrap.Utility.WebsiteSaveQueue.Start);
		TryStartup("Website history sync", Voidstrap.Utility.WebsiteHistorySync.Install);
		InstallEnabledOverlays();
		TryStartup("Telemetry blocker", Voidstrap.Integrations.TelemetryBlocker.SyncSettingFromState);
		if (Voidstrap.Utility.Platform.SupportsAudioDucking)
		{
			TryStartup("Audio ducking", Voidstrap.Integrations.AudioDucker.ApplyFromSettings);
			TryStartup("Headset audio", Voidstrap.Integrations.HeadsetAudio.ApplyFromSettings);
		}
		if (!LaunchSettings.WatcherFlag.Active)
		{
			TryStartup("Rojo updater", Voidstrap.Integrations.Rojo.RojoManager.AutoUpdate);
			_ = RefreshRemoteDataAsync(_lifetimeCancellation.Token);
		}
		if (!LaunchSettings.WatcherFlag.Active && !LaunchSettings.IsHelperInvocation)
		{
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
		}
		TryStartup("CPU core limiter", CpuCoreLimiter.ApplyConfiguredLimit);
		TryStartup("GPU inventory warmup", () => Task.Run(() => _ = Voidstrap.Utility.GpuInventory.HasNvidia));
		TryStartup("Custom RPC", StartCustomRpcIfEnabled);
	}

	private static void InitializeWatcherServices()
	{
		TryStartup("Website save queue", Voidstrap.Utility.WebsiteSaveQueue.Start);
		TryStartup("Website history sync", Voidstrap.Utility.WebsiteHistorySync.Install);
		InstallEnabledOverlays();
		if (Voidstrap.Utility.Platform.SupportsAudioDucking)
		{
			TryStartup("Audio ducking", Voidstrap.Integrations.AudioDucker.ApplyFromSettings);
			TryStartup("Headset audio", Voidstrap.Integrations.HeadsetAudio.ApplyFromSettings);
		}
	}

	private static void InstallEnabledOverlays()
	{
		if (!Voidstrap.Utility.Platform.SupportsOverlays)
		{
			return;
		}
		if (Settings.Prop.RiShadeEnabled)
		{
			TryStartup("RiShade", Voidstrap.Integrations.RiShade.RiShadeManager.Install);
		}
		if (Voidstrap.Integrations.AntiAliasing.AntiAliasingSettings.MethodIndex > 0)
		{
			TryStartup("Anti aliasing", Voidstrap.Integrations.AntiAliasing.AntiAliasingManager.Install);
		}
		if (Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex > 0)
		{
			TryStartup("Frame generation", Voidstrap.Integrations.FrameGeneration.FrameGenManager.Install);
		}
		TryStartup("Overlay hub", () => Voidstrap.Integrations.Overlays.OverlayHub.Refresh());
	}

	private static void InitializeAppearance()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			TryStartup("Linux scrolling", () =>
			{
				EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ScrollViewer), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyLinuxScrollViewer));
				EventManager.RegisterClassHandler(typeof(System.Windows.Controls.Image), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyLinuxImageScaling));
			});
		}
		if (Settings.Prop.ClearFont)
		{
			TryStartup("Clear font", () => EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyClearFont)));
		}
		TryStartup("Smooth scrolling", () =>
		{
			Wpf.Ui.Controls.SmoothScroll.SetGlobalEnabled(Settings.Prop.SmooothBARRyesirikikthxlucipook);
			Wpf.Ui.Controls.SmoothScroll.Register();
		});
		TryStartup("Global background", GlobalBackground.Register);
		TryStartup("Application font", AppFont.Initialize);
		TryStartup("Memory manager", Voidstrap.Utility.MemoryManager.Start);
		TryStartup("Render diagnostics", LogRenderMode);
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
				+ ", renderer stage: " + (Environment.GetEnvironmentVariable("VOIDSTRAP_GPU_RETRY") ?? "default"));
		}
	}

	private static void ApplyLinuxScrollViewer(object sender, RoutedEventArgs e)
	{
		if (sender is System.Windows.Controls.ScrollViewer viewer)
		{
			System.Windows.Controls.ScrollViewer.SetIsDeferredScrollingEnabled(viewer, true);
			RenderOptions.SetBitmapScalingMode(viewer, BitmapScalingMode.Linear);
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
			await Voidstrap.Utility.WebsiteGeoSync.PullAsync().ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::RefreshRemoteData", ex);
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
		try
		{
			action();
		}
		catch (Exception ex)
		{
			Logger.WriteException("App::OnStartup::" + component, ex);
		}
	}


	protected override void OnExit(ExitEventArgs e)
	{
		UnregisterExceptionHandlers();
		TryShutdown(VpnHttpClient.Shutdown);
		TryShutdown(_lifetimeCancellation.Cancel);
		TryShutdown(Voidstrap.Utility.ScreenColorEffect.Shutdown);
		TryShutdown(Voidstrap.UI.EmojiTextRenderer.Shutdown);
		TryShutdown(Voidstrap.Utility.WebsiteSaveQueue.Shutdown);
		TryShutdown(Voidstrap.Utility.WebsiteHistorySync.Shutdown);
		TryShutdown(Voidstrap.Utility.WebsiteGeoSync.Shutdown);
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
		TryShutdown(Voidstrap.Integrations.Rojo.RojoManager.Shutdown);
		TryShutdown(Voidstrap.Integrations.Studio.StudioIntegration.Shutdown);
		TryShutdown(AssetProxyServer.Stop);
		TryShutdown(AssetPreloadCache.Shutdown);
		TryShutdown(() => AssetCaptureStore.Shutdown());
		TryShutdown(Voidstrap.Integrations.Fullscreen.FakeExclusiveFullscreen.Shutdown);
		TryShutdown(Voidstrap.Integrations.Overlays.RobloxWindowTracker.Shutdown);
		TryShutdown(Voidstrap.UI.LiveLanguageRefresher.Shutdown);
		TryShutdown(Voidstrap.UI.Utility.WindowScaling.Shutdown);
		TryShutdown(Voidstrap.Utility.TranslationService.Shutdown);
		TryShutdown(Voidstrap.UI.GlobalBackground.ClearCache);
		TryShutdown(Voidstrap.Utility.DynamicRenderSystem.ClearCache);
		TryShutdown(Settings.FlushDeferred);
		TryShutdown(FastFlags.FlushDeferred);
		TryShutdown(StopCustomRpc);
		TryShutdown(DisposeDiscordClient);
		TryShutdown(DisposeMusicPlayer);
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

	private static void StopCustomRpc()
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
