using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core;

namespace Voidstrap.Platform.Linux;

public sealed class LinuxPlatformHost : IPlatformHost
{
	public LinuxPlatformHost()
		: this(new SystemProcessService(), null, LinuxRuntimeEnvironmentInfo.Detect())
	{
	}

	public LinuxPlatformHost(IProcessService processes)
		: this(processes, null, LinuxRuntimeEnvironmentInfo.Detect())
	{
	}

	public LinuxPlatformHost(IProcessService processes, IPlatformUpdater? updater)
		: this(processes, updater, LinuxRuntimeEnvironmentInfo.Detect())
	{
	}

	public LinuxPlatformHost(IProcessService processes, IPlatformUpdater? updater, LinuxRuntimeEnvironmentInfo runtimeEnvironment)
	{
		Processes = processes ?? throw new ArgumentNullException(nameof(processes));
		ArgumentNullException.ThrowIfNull(runtimeEnvironment);
		Paths = new LinuxPaths(Processes);
		SecureStore = new LinuxSecureStore(Processes);
		LinuxSoberRuntimeProvider playerRuntime = new(Processes, runtimeEnvironment);
		LinuxVinegarStudioRuntimeProvider studioRuntime = new(Processes, runtimeEnvironment);
		PlayerRuntime = playerRuntime;
		StudioRuntime = studioRuntime;
		LinuxProtocolRegistration protocolRegistration = new(Processes, (LinuxPaths)Paths);
		ProtocolRegistration = protocolRegistration;
		Updater = updater ?? new UnavailablePlatformUpdater(CreateUpdaterCapability());
		Notifications = CreateNotifications(Processes);
		Overlay = new CapabilityOnlyPlatformFeatureService(CreateOverlayCapability());
		Input = new CapabilityOnlyPlatformFeatureService(CreateInputCapability());
		AudioSession = new CapabilityOnlyPlatformFeatureService(CreateAudioSessionCapability());
		ResourceOptimization = new UnixResourceOptimizationService(
			Processes,
			new CapabilityDescriptor(FeatureId.ResourceOptimization, CapabilityState.Unavailable, "Resource optimization requires direct access to the Sober process"),
			true);
		Capabilities = new CapabilitySet(
			PlatformId.Linux,
			CreateCapabilities(
				Updater.Capability,
				Notifications.Capability,
				Overlay.Capability,
				Input.Capability,
				AudioSession.Capability,
				playerRuntime.PrerequisiteCapability,
				studioRuntime.PrerequisiteCapability,
				protocolRegistration.Capability));
	}

	public PlatformId Id => PlatformId.Linux;

	public IPlatformCapabilities Capabilities { get; }

	public IPlatformPaths Paths { get; }

	public ISecureStore SecureStore { get; }

	public IProcessService Processes { get; }

	public IRobloxRuntimeProvider PlayerRuntime { get; }

	public IRobloxRuntimeProvider StudioRuntime { get; }

	public IProtocolRegistration ProtocolRegistration { get; }

	public IPlatformUpdater Updater { get; }

	public INotificationService Notifications { get; }

	public IOverlayService Overlay { get; }

	public IInputService Input { get; }

	public IAudioSessionService AudioSession { get; }

	public IResourceOptimizationService ResourceOptimization { get; }

	private static IEnumerable<CapabilityDescriptor> CreateCapabilities(
		CapabilityDescriptor updater,
		CapabilityDescriptor notifications,
		CapabilityDescriptor overlay,
		CapabilityDescriptor input,
		CapabilityDescriptor audioSession,
		CapabilityDescriptor playerPrerequisite,
		CapabilityDescriptor studioPrerequisite,
		CapabilityDescriptor protocolRegistration)
	{
		yield return Available(FeatureId.DesktopShell, "Native Linux desktop support is available");
		yield return CreateEmbeddedBrowserCapability();
		yield return CreateRuntimeCapability(playerPrerequisite, "Requires the Sober Flatpak runtime", "Install Sober from Flathub");
		yield return CreateRuntimeCapability(studioPrerequisite, "Roblox Studio requires Vinegar", "Install Vinegar");
		yield return new CapabilityDescriptor(FeatureId.SecureStorage, CapabilityState.RequiresExternalRuntime, "Requires a Secret Service provider", "Install and unlock a Secret Service provider");
		yield return protocolRegistration;
		yield return updater;
		yield return notifications;
		yield return new CapabilityDescriptor(FeatureId.Tray, CapabilityState.Unavailable, "The native tray adapter has not been ported to the shared desktop host");
		yield return overlay;
		yield return input;
		yield return audioSession;
		yield return new CapabilityDescriptor(FeatureId.ResourceOptimization, CapabilityState.Unavailable, "Resource optimization requires direct access to the Sober process");
		yield return CreateAssetInjectionCapability(playerPrerequisite, studioPrerequisite);
		yield return new CapabilityDescriptor(FeatureId.FrameGeneration, CapabilityState.Unavailable, "Windows frame generation is not available on Linux");
		yield return new CapabilityDescriptor(FeatureId.VirtualController, CapabilityState.Unavailable, "Windows virtual controller support is not available on Linux");
		yield return new CapabilityDescriptor(FeatureId.ExtensionNativeAssets, CapabilityState.RequiresExternalRuntime, "Extensions require Linux native assets");
	}

	private static CapabilityDescriptor CreateRuntimeCapability(CapabilityDescriptor prerequisite, string reason, string requiredAction)
	{
		return prerequisite.IsAvailable
			? new CapabilityDescriptor(prerequisite.Feature, CapabilityState.RequiresExternalRuntime, reason, requiredAction, true)
			: prerequisite;
	}

	private static CapabilityDescriptor CreateAssetInjectionCapability(CapabilityDescriptor playerPrerequisite, CapabilityDescriptor studioPrerequisite)
	{
		if (playerPrerequisite.IsAvailable)
		{
			return new CapabilityDescriptor(
				FeatureId.AssetInjection,
				CapabilityState.RequiresExternalRuntime,
				"Sober Player and Vinegar Studio asset overlays are supported after the matching runtime is installed",
				"Install Sober or Vinegar",
				true);
		}

		if (studioPrerequisite.IsAvailable)
		{
			return new CapabilityDescriptor(
				FeatureId.AssetInjection,
				CapabilityState.RequiresExternalRuntime,
				"Vinegar Studio asset overlays are supported on this system after Vinegar is installed",
				"Install Vinegar",
				true);
		}

		return new CapabilityDescriptor(
			FeatureId.AssetInjection,
			CapabilityState.Unavailable,
			"Sober Player and Vinegar Studio asset overlays require an x86_64 processor with SSE4.1 support");
	}

	private static CapabilityDescriptor Available(FeatureId feature, string reason)
	{
		return new CapabilityDescriptor(feature, CapabilityState.Available, reason);
	}

	private static CapabilityDescriptor CreateEmbeddedBrowserCapability()
	{
		return LinuxWebViewRuntimeDetector.Detect() switch
		{
			LinuxWebViewRuntime.WpeWebKit => new CapabilityDescriptor(FeatureId.EmbeddedBrowser, CapabilityState.Experimental, "WPE WebKit provides basic browser and bridge support. Full document start injection is still being migrated", null, true),
			LinuxWebViewRuntime.WebKitGtk => new CapabilityDescriptor(FeatureId.EmbeddedBrowser, CapabilityState.Experimental, "WebKitGTK fallback provides basic browser and bridge support. Full document start injection is still being migrated", null, true),
			_ => new CapabilityDescriptor(FeatureId.EmbeddedBrowser, CapabilityState.RequiresExternalRuntime, "The embedded browser requires WPE WebKit or WebKitGTK", "Install WPE WebKit or WebKitGTK")
		};
	}

	private static INotificationService CreateNotifications(IProcessService processes)
	{
		string? executable = processes.FindExecutable("notify-send");
		CapabilityDescriptor capability = executable is null
			? new CapabilityDescriptor(FeatureId.Notifications, CapabilityState.RequiresExternalRuntime, "A Linux desktop notification service is unavailable", "Install notify-send")
			: new CapabilityDescriptor(FeatureId.Notifications, CapabilityState.Available, "Linux desktop notifications are available");
		return new ProcessNotificationService(
			processes,
			capability,
			executable,
			static request => ["--app-name=Voidstrap", "--expire-time=5000", request.Title, request.Message]);
	}

	private static CapabilityDescriptor CreateUpdaterCapability()
	{
		return new CapabilityDescriptor(FeatureId.Updater, CapabilityState.Unavailable, "The Linux updater has not been ported to the shared desktop host");
	}

	private static CapabilityDescriptor CreateOverlayCapability()
	{
		return new CapabilityDescriptor(FeatureId.Overlay, CapabilityState.Unavailable, "The X11 and Wayland overlay adapters have not been ported to the shared desktop host");
	}

	private static CapabilityDescriptor CreateInputCapability()
	{
		return new CapabilityDescriptor(FeatureId.GlobalInput, CapabilityState.Unavailable, "The X11 and Wayland input adapters have not been ported to the shared desktop host");
	}

	private static CapabilityDescriptor CreateAudioSessionCapability()
	{
		return new CapabilityDescriptor(FeatureId.AudioSession, CapabilityState.Unavailable, "The PipeWire and PulseAudio adapters have not been ported to the shared desktop host");
	}
}

public sealed class LinuxPaths : PlatformPathsBase
{
	public LinuxPaths()
		: this(new SystemProcessService())
	{
	}

	public LinuxPaths(IProcessService processes)
		: base(CreateStorage(processes ?? throw new ArgumentNullException(nameof(processes))))
	{
	}

	public string ApplicationsDirectory => Path.Combine(GetXdgDirectory("XDG_DATA_HOME", ".local", "share"), "applications");

	private static PlatformStoragePaths CreateStorage(IProcessService processes)
	{
		string config = Path.Combine(GetXdgDirectory("XDG_CONFIG_HOME", ".config"), "voidstrap");
		string data = Path.Combine(GetXdgDirectory("XDG_DATA_HOME", ".local", "share"), "voidstrap");
		string cache = Path.Combine(GetXdgDirectory("XDG_CACHE_HOME", ".cache"), "voidstrap");
		string state = Path.Combine(GetXdgDirectory("XDG_STATE_HOME", ".local", "state"), "voidstrap");
		string downloads = GetDownloadsDirectory(processes);
		string runtime = GetRuntimeStorageDirectory();

		return new PlatformStoragePaths(
			data,
			config,
			data,
			cache,
			Path.Combine(state, "logs"),
			downloads,
			Path.Combine(data, "extensions"),
			runtime);
	}

	private static string GetRuntimeStorageDirectory()
	{
		string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
		if (TryGetAbsolutePath(runtime, out string? configured)
			&& LinuxRuntimeDirectory.TryCreateConfiguredDirectory(configured, out string? runtimeDirectory))
		{
			return runtimeDirectory;
		}

		return LinuxRuntimeDirectory.CreatePrivateFallbackDirectory(
			Path.GetTempPath(),
			Environment.UserName,
			Environment.ProcessId,
			Guid.NewGuid().ToString("N"));
	}

	private static string GetDownloadsDirectory(IProcessService processes)
	{
		string home = GetHomeDirectory();
		string userDirectoriesPath = Path.Combine(GetXdgDirectory("XDG_CONFIG_HOME", ".config"), "user-dirs.dirs");
		if (TryReadDownloadDirectory(userDirectoriesPath, home, out string? configured))
		{
			return configured;
		}

		string? executable = processes.FindExecutable("xdg-user-dir");
		if (executable is not null)
		{
			try
			{
				OperationResult<ProcessExecution> result = processes.ExecuteAsync(
					new ProcessCommand(executable, ["DOWNLOAD"]),
					CancellationToken.None).GetAwaiter().GetResult();
				if (result.Succeeded
					&& result.Value is not null
					&& result.Value.ExitCode == 0
					&& TryGetAbsolutePath(result.Value.StandardOutput.Trim(), out string? resolved))
				{
					return resolved;
				}
			}
			catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
			{
			}
		}

		return Path.Combine(home, "Downloads");
	}

	internal static bool TryReadDownloadDirectory(string path, string home, out string directory)
	{
		directory = string.Empty;
		try
		{
			if (!File.Exists(path) || new FileInfo(path).Length > 1048576)
			{
				return false;
			}

			foreach (string line in File.ReadLines(path))
			{
				Match match = Regex.Match(
					line,
					"^\\s*XDG_DOWNLOAD_DIR\\s*=\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"\\s*$",
					RegexOptions.CultureInvariant);
				if (!match.Success)
				{
					continue;
				}

				string value = UnescapeUserDirectoryValue(match.Groups["value"].Value);
				if (TryExpandHomeDirectory(value, home, out directory))
				{
					return true;
				}
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
		{
		}

		return false;
	}

	private static string UnescapeUserDirectoryValue(string value)
	{
		StringBuilder builder = new(value.Length);
		for (int index = 0; index < value.Length; index++)
		{
			if (value[index] == '\\' && index + 1 < value.Length && value[index + 1] is '\\' or '"' or '$' or '`')
			{
				index++;
			}
			builder.Append(value[index]);
		}
		return builder.ToString();
	}

	private static bool TryExpandHomeDirectory(string value, string home, out string directory)
	{
		directory = string.Empty;
		string expanded;
		if (value.Equals("$HOME", StringComparison.Ordinal) || value.Equals("${HOME}", StringComparison.Ordinal))
		{
			expanded = home;
		}
		else if (value.StartsWith("$HOME/", StringComparison.Ordinal))
		{
			expanded = Path.Combine(home, value[6..]);
		}
		else if (value.StartsWith("${HOME}/", StringComparison.Ordinal))
		{
			expanded = Path.Combine(home, value[8..]);
		}
		else
		{
			expanded = value;
		}

		if (expanded.Contains('$', StringComparison.Ordinal))
		{
			return false;
		}

		return TryGetAbsolutePath(expanded, out directory);
	}

	private static string GetXdgDirectory(string variable, params string[] fallbackSegments)
	{
		string? configured = Environment.GetEnvironmentVariable(variable);
		if (TryGetAbsolutePath(configured, out string? path))
		{
			return path;
		}

		path = GetHomeDirectory();
		foreach (string segment in fallbackSegments)
		{
			path = Path.Combine(path, segment);
		}

		return path;
	}

	internal static string GetHomeDirectory()
	{
		if (TryGetAbsolutePath(Environment.GetEnvironmentVariable("HOME"), out string? home))
		{
			return home;
		}

		if (TryGetAbsolutePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out home))
		{
			return home;
		}

		return Path.GetFullPath(Path.GetTempPath());
	}

	internal static string GetXdgDataHome()
	{
		return GetXdgDirectory("XDG_DATA_HOME", ".local", "share");
	}

	internal static bool TryGetAbsolutePath(string? value, out string path)
	{
		path = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}

		try
		{
			if (!Path.IsPathRooted(value))
			{
				return false;
			}

			path = Path.GetFullPath(value);
			return true;
		}
		catch (Exception)
		{
			return false;
		}
	}
}

internal static class LinuxRuntimeDirectory
{
	private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
	private const UnixFileMode SharedMode = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

	public static bool TryCreateConfiguredDirectory(string configuredRoot, out string runtimeDirectory)
	{
		return TryCreateConfiguredDirectory(configuredRoot, out runtimeDirectory, IsOwnedByEffectiveUser);
	}

	internal static bool TryCreateConfiguredDirectory(
		string configuredRoot,
		out string runtimeDirectory,
		Func<string, bool> ownershipCheck)
	{
		ArgumentNullException.ThrowIfNull(ownershipCheck);
		runtimeDirectory = string.Empty;
		if (!Directory.Exists(configuredRoot) || IsLink(configuredRoot))
		{
			return !OperatingSystem.IsLinux() && TryCreatePortableConfiguredDirectory(configuredRoot, out runtimeDirectory);
		}

		if (OperatingSystem.IsLinux() && (!HasPrivateMode(configuredRoot) || !ownershipCheck(configuredRoot)))
		{
			return false;
		}

		string candidate = Path.Combine(configuredRoot, "voidstrap");
		if (Directory.Exists(candidate))
		{
			if (IsLink(candidate) || OperatingSystem.IsLinux() && (!HasPrivateMode(candidate) || !ownershipCheck(candidate)))
			{
				return false;
			}
			runtimeDirectory = candidate;
			return true;
		}

		if (PathExists(candidate))
		{
			return false;
		}

		try
		{
			CreateNewPrivateDirectory(candidate);
			if (OperatingSystem.IsLinux() && (!HasPrivateMode(candidate) || !ownershipCheck(candidate)))
			{
				TryDeleteDirectory(candidate);
				return false;
			}
			runtimeDirectory = candidate;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	public static string CreatePrivateFallbackDirectory(string temporaryRoot, string userName, int processId, string nonce)
	{
		if (!LinuxPaths.TryGetAbsolutePath(temporaryRoot, out string? root))
		{
			throw new IOException("The temporary directory is unavailable");
		}
		if (string.IsNullOrWhiteSpace(nonce) || nonce.Any(character => !char.IsAsciiLetterOrDigit(character)))
		{
			throw new ArgumentException("The runtime directory nonce is invalid", nameof(nonce));
		}

		string user = Regex.Replace(userName ?? string.Empty, "[^A-Za-z0-9_.]", "_");
		if (string.IsNullOrWhiteSpace(user))
		{
			user = "user";
		}

		string candidate = Path.Combine(root, $"voidstrap-{user}-{processId}-{nonce}");
		if (PathExists(candidate))
		{
			throw new IOException("The private runtime directory already exists");
		}

		CreateNewPrivateDirectory(candidate);
		if (IsLink(candidate) || OperatingSystem.IsLinux() && (!HasPrivateMode(candidate) || !IsOwnedByEffectiveUser(candidate)))
		{
			TryDeleteDirectory(candidate);
			throw new IOException("The private runtime directory is unsafe");
		}

		return candidate;
	}

	private static bool TryCreatePortableConfiguredDirectory(string configuredRoot, out string runtimeDirectory)
	{
		runtimeDirectory = string.Empty;
		try
		{
			Directory.CreateDirectory(configuredRoot);
			string candidate = Path.Combine(configuredRoot, "voidstrap");
			if (PathExists(candidate))
			{
				return false;
			}
			Directory.CreateDirectory(candidate);
			runtimeDirectory = candidate;
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static void CreateNewPrivateDirectory(string path)
	{
		if (OperatingSystem.IsLinux())
		{
			if (NativeMethods.CreateDirectory(path, (uint)PrivateMode) != 0)
			{
				throw new IOException("The private runtime directory could not be created", Marshal.GetLastPInvokeError());
			}
			File.SetUnixFileMode(path, PrivateMode);
			return;
		}

		if (PathExists(path))
		{
			throw new IOException("The private runtime directory already exists");
		}
		Directory.CreateDirectory(path);
	}

	[SupportedOSPlatform("linux")]
	private static bool HasPrivateMode(string path)
	{
		try
		{
			UnixFileMode mode = File.GetUnixFileMode(path);
			return (mode & PrivateMode) == PrivateMode && (mode & SharedMode) == 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
		{
			return false;
		}
	}

	internal static bool IsOwnedByEffectiveUser(string path)
	{
		if (!OperatingSystem.IsLinux())
		{
			return true;
		}

		IntPtr buffer = IntPtr.Zero;
		try
		{
			buffer = Marshal.AllocHGlobal(256);
			for (int offset = 0; offset < 256; offset += sizeof(long))
			{
				Marshal.WriteInt64(buffer, offset, 0);
			}

			if (NativeMethods.GetFileStatus(-100, path, 0x100, 0x7ff, buffer) != 0)
			{
				return false;
			}

			uint owner = unchecked((uint)Marshal.ReadInt32(buffer, 20));
			return IsOwnedByEffectiveUser(NativeMethods.GetEffectiveUserId(), owner);
		}
		catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
		{
			return false;
		}
		finally
		{
			if (buffer != IntPtr.Zero)
			{
				Marshal.FreeHGlobal(buffer);
			}
		}
	}

	internal static bool IsOwnedByEffectiveUser(uint effectiveUserId, uint ownerUserId)
	{
		return effectiveUserId == ownerUserId;
	}

	private static bool PathExists(string path)
	{
		if (File.Exists(path) || Directory.Exists(path))
		{
			return true;
		}

		try
		{
			return new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return true;
		}
	}

	private static bool IsLink(string path)
	{
		try
		{
			FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
			return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return true;
		}
	}

	private static void TryDeleteDirectory(string path)
	{
		try
		{
			if (Directory.Exists(path) && !IsLink(path))
			{
				Directory.Delete(path);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	private static class NativeMethods
	{
		[DllImport("libc", EntryPoint = "mkdir", SetLastError = true)]
		public static extern int CreateDirectory(string path, uint mode);

		[DllImport("libc", EntryPoint = "geteuid")]
		public static extern uint GetEffectiveUserId();

		[DllImport("libc", EntryPoint = "statx", SetLastError = true)]
		public static extern int GetFileStatus(int directoryFileDescriptor, string path, int flags, uint mask, IntPtr status);
	}
}

public sealed class LinuxSecureStore : ISecureStore
{
	private const int MaximumIdentifierLength = 256;
	private const int MaximumSecureValueBytes = 4194304;

	private readonly IProcessService _processes;

	public LinuxSecureStore(IProcessService processes)
	{
		_processes = processes;
	}

	public async Task<OperationResult> SetAsync(string service, string key, string value, CancellationToken cancellationToken = default)
	{
		if (!HasValidIdentifiers(service, key))
			return OperationResult.Fail("SecretServiceKeyInvalid", "The Secret Service service or key is invalid");
		if (value is null)
			return OperationResult.Fail("SecretServiceValueInvalid", "The Secret Service value is invalid");
		if (Encoding.UTF8.GetByteCount(value) > MaximumSecureValueBytes)
			return OperationResult.Fail("SecretServiceValueTooLarge", "The Secret Service value is too large");
		string? secretTool = _processes.FindExecutable("secret-tool");
		if (secretTool is null)
		{
			return OperationResult.Fail("SecretServiceUnavailable", "A Secret Service provider is unavailable", CapabilityState.RequiresExternalRuntime);
		}

		ProcessCommand command = new ProcessCommand(
			secretTool,
			["store", "--label=Voidstrap", "service", service, "key", key],
			value);
		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(command, cancellationToken);

		if (!result.Succeeded || result.Value is null)
		{
			return CopyFailure(result.Failure);
		}

		return result.Value.ExitCode == 0
			? OperationResult.Success()
			: OperationResult.Fail("SecretServiceWriteFailed", result.Value.StandardError, CapabilityState.RequiresExternalRuntime);
	}

	public async Task<OperationResult<SecureValueResult>> GetAsync(string service, string key, CancellationToken cancellationToken = default)
	{
		if (!HasValidIdentifiers(service, key))
			return OperationResult<SecureValueResult>.Fail("SecretServiceKeyInvalid", "The Secret Service service or key is invalid");
		string? secretTool = _processes.FindExecutable("secret-tool");
		if (secretTool is null)
		{
			return OperationResult<SecureValueResult>.Fail("SecretServiceUnavailable", "A Secret Service provider is unavailable", CapabilityState.RequiresExternalRuntime);
		}

		ProcessCommand command = new ProcessCommand(
			secretTool,
			["lookup", "service", service, "key", key]);
		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(command, cancellationToken);

		if (!result.Succeeded || result.Value is null)
		{
			return CopyFailure<SecureValueResult>(result.Failure);
		}

		if (result.Value.ExitCode == 1)
		{
			return OperationResult<SecureValueResult>.Success(new SecureValueResult(false, null));
		}
		if (result.Value.ExitCode != 0)
		{
			return OperationResult<SecureValueResult>.Fail("SecretServiceReadFailed", result.Value.StandardError, CapabilityState.RequiresExternalRuntime);
		}

		string value = TrimLineEnding(result.Value.StandardOutput);
		if (Encoding.UTF8.GetByteCount(value) > MaximumSecureValueBytes)
			return OperationResult<SecureValueResult>.Fail("SecretServiceValueTooLarge", "The Secret Service value is too large");
		return OperationResult<SecureValueResult>.Success(new SecureValueResult(true, value));
	}

	public async Task<OperationResult> DeleteAsync(string service, string key, CancellationToken cancellationToken = default)
	{
		if (!HasValidIdentifiers(service, key))
			return OperationResult.Fail("SecretServiceKeyInvalid", "The Secret Service service or key is invalid");
		string? secretTool = _processes.FindExecutable("secret-tool");
		if (secretTool is null)
		{
			return OperationResult.Fail("SecretServiceUnavailable", "A Secret Service provider is unavailable", CapabilityState.RequiresExternalRuntime);
		}

		ProcessCommand command = new ProcessCommand(
			secretTool,
			["clear", "service", service, "key", key]);
		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(command, cancellationToken);

		if (!result.Succeeded || result.Value is null)
		{
			return CopyFailure(result.Failure);
		}

		return result.Value.ExitCode is 0 or 1
			? OperationResult.Success()
			: OperationResult.Fail("SecretServiceDeleteFailed", result.Value.StandardError, CapabilityState.RequiresExternalRuntime);
	}

	private static OperationResult CopyFailure(OperationFailure? failure)
	{
		return failure is null
			? OperationResult.Fail("SecretServiceOperationFailed", "The Secret Service operation failed", CapabilityState.RequiresExternalRuntime)
			: OperationResult.Fail(failure.Code, failure.Message, failure.State);
	}

	private static OperationResult<T> CopyFailure<T>(OperationFailure? failure)
	{
		return failure is null
			? OperationResult<T>.Fail("SecretServiceOperationFailed", "The Secret Service operation failed", CapabilityState.RequiresExternalRuntime)
			: OperationResult<T>.Fail(failure.Code, failure.Message, failure.State);
	}

	private static bool HasValidIdentifiers(string service, string key)
	{
		return !string.IsNullOrWhiteSpace(service)
			&& service.Length <= MaximumIdentifierLength
			&& !string.IsNullOrWhiteSpace(key)
			&& key.Length <= MaximumIdentifierLength;
	}

	private static string TrimLineEnding(string value)
	{
		if (value.EndsWith("\r\n", StringComparison.Ordinal))
			return value[..^2];
		if (value.EndsWith("\n", StringComparison.Ordinal))
			return value[..^1];
		return value;
	}
}

public sealed record LinuxRuntimeEnvironmentInfo(
	Architecture Architecture,
	Version KernelVersion,
	bool SupportsSse41)
{
	public static LinuxRuntimeEnvironmentInfo Detect()
	{
		return new LinuxRuntimeEnvironmentInfo(
			RuntimeInformation.ProcessArchitecture,
			Environment.OSVersion.Version,
			Sse41.IsSupported);
	}
}

internal static class LinuxRuntimePrerequisites
{
	private static readonly Version MinimumSoberKernel = new(5, 11);

	public static CapabilityDescriptor EvaluateSober(LinuxRuntimeEnvironmentInfo environment)
	{
		if (environment.Architecture != Architecture.X64)
		{
			return new CapabilityDescriptor(
				FeatureId.RobloxPlayer,
				CapabilityState.Unavailable,
				"Sober production support requires an x86_64 Linux system");
		}

		if (environment.KernelVersion < MinimumSoberKernel)
		{
			return new CapabilityDescriptor(
				FeatureId.RobloxPlayer,
				CapabilityState.Unavailable,
				"Sober requires Linux kernel 5.11 or newer",
				"Update the Linux kernel");
		}

		if (!environment.SupportsSse41)
		{
			return new CapabilityDescriptor(
				FeatureId.RobloxPlayer,
				CapabilityState.Unavailable,
				"Sober requires a processor with SSE4.1 support");
		}

		return new CapabilityDescriptor(FeatureId.RobloxPlayer, CapabilityState.Available, "This system meets the Sober runtime requirements");
	}

	public static CapabilityDescriptor EvaluateVinegar(LinuxRuntimeEnvironmentInfo environment)
	{
		if (environment.Architecture != Architecture.X64)
		{
			return new CapabilityDescriptor(
				FeatureId.RobloxStudio,
				CapabilityState.Unavailable,
				"Vinegar production support requires an x86_64 Linux system");
		}

		if (!environment.SupportsSse41)
		{
			return new CapabilityDescriptor(
				FeatureId.RobloxStudio,
				CapabilityState.Unavailable,
				"Vinegar requires a processor with SSE4.1 support");
		}

		return new CapabilityDescriptor(FeatureId.RobloxStudio, CapabilityState.Available, "This system meets the Vinegar runtime requirements");
	}
}

public sealed class LinuxSoberRuntimeProvider : IRobloxRuntimeProvider
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";

	private readonly IProcessService _processes;
	private readonly CapabilityDescriptor _prerequisiteCapability;

	public LinuxSoberRuntimeProvider(IProcessService processes)
		: this(processes, LinuxRuntimeEnvironmentInfo.Detect())
	{
	}

	public LinuxSoberRuntimeProvider(IProcessService processes, LinuxRuntimeEnvironmentInfo runtimeEnvironment)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
		ArgumentNullException.ThrowIfNull(runtimeEnvironment);
		_prerequisiteCapability = LinuxRuntimePrerequisites.EvaluateSober(runtimeEnvironment);
	}

	public RuntimeKind Kind => RuntimeKind.Player;

	public CapabilityDescriptor PrerequisiteCapability => _prerequisiteCapability;

	public async Task<RuntimeInstallation> FindInstallationAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!_prerequisiteCapability.IsAvailable)
		{
			return UnsupportedInstallation(_prerequisiteCapability);
		}

		string? flatpak = _processes.FindExecutable("flatpak");
		if (flatpak is null)
		{
			return MissingInstallation("Flatpak is not installed");
		}

		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(
			new ProcessCommand(flatpak, ["info", "--show-version", SoberApplicationId]),
			cancellationToken);
		ThrowIfCanceled(result.Failure, cancellationToken);

		if (!result.Succeeded)
		{
			return MissingInstallation(result.Failure?.Message ?? "Sober discovery did not complete");
		}

		if (result.Value is null || result.Value.ExitCode != 0)
		{
			return MissingInstallation("Sober is not installed");
		}

		return new RuntimeInstallation(
			RuntimeKind.Player,
			"Sober",
			result.Value.StandardOutput.Trim(),
			flatpak,
			GetSoberDataDirectory(),
			new CapabilityDescriptor(FeatureId.RobloxPlayer, CapabilityState.Experimental, "Sober is available", null, true));
	}

	public async Task<OperationResult<LaunchSession>> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (request.Kind != RuntimeKind.Player
			|| !RobloxDeeplink.TryExtract(request.Deeplink.AbsoluteUri, out Uri? deeplink)
			|| deeplink is null
			|| RobloxDeeplink.GetRuntimeKind(deeplink.AbsoluteUri) != RuntimeKind.Player)
		{
			return OperationResult<LaunchSession>.Fail("RuntimeKindMismatch", "The requested runtime does not match the Sober provider");
		}

		RuntimeInstallation installation = await FindInstallationAsync(cancellationToken);
		if (!installation.Capability.IsAvailable || string.IsNullOrWhiteSpace(installation.Location))
		{
			return OperationResult<LaunchSession>.Fail(
				"SoberUnavailable",
				installation.Capability.Reason,
				installation.Capability.State);
		}

		OperationResult<ProcessStartResult> result = await _processes.StartAsync(
			new ProcessCommand(installation.Location, ["run", SoberApplicationId, deeplink.AbsoluteUri], CaptureOutput: false),
			cancellationToken);
		ThrowIfCanceled(result.Failure, cancellationToken);

		if (!result.Succeeded || result.Value is null)
		{
			return result.Failure is null
				? OperationResult<LaunchSession>.Fail("SoberLaunchFailed", "Sober did not start", CapabilityState.Experimental)
				: OperationResult<LaunchSession>.Fail(result.Failure.Code, result.Failure.Message, result.Failure.State);
		}

		return OperationResult<LaunchSession>.Success(new LaunchSession(
			RuntimeKind.Player,
			"Sober",
			result.Value.ProcessId,
			DateTimeOffset.UtcNow,
			installation,
			false));
	}

	private static RuntimeInstallation MissingInstallation(string reason)
	{
		return new RuntimeInstallation(
			RuntimeKind.Player,
			"Sober",
			null,
			null,
			null,
			new CapabilityDescriptor(
				FeatureId.RobloxPlayer,
				CapabilityState.RequiresExternalRuntime,
				reason,
				"Install Sober from Flathub",
				true));
	}

	private static RuntimeInstallation UnsupportedInstallation(CapabilityDescriptor capability)
	{
		return new RuntimeInstallation(RuntimeKind.Player, "Sober", null, null, null, capability);
	}

	private static void ThrowIfCanceled(OperationFailure? failure, CancellationToken cancellationToken)
	{
		if (string.Equals(failure?.Code, "OperationCanceled", StringComparison.Ordinal))
		{
			throw new OperationCanceledException(failure!.Message, null, cancellationToken);
		}

		cancellationToken.ThrowIfCancellationRequested();
	}

	private static string GetSoberDataDirectory()
	{
		return Path.Combine(
			LinuxPaths.GetHomeDirectory(),
			".var",
			"app",
			SoberApplicationId,
			"data",
			"sober");
	}
}

public sealed class LinuxVinegarStudioRuntimeProvider : IRobloxRuntimeProvider
{
	private const string VinegarApplicationId = "org.vinegarhq.Vinegar";

	private readonly IProcessService _processes;
	private readonly CapabilityDescriptor _prerequisiteCapability;

	public LinuxVinegarStudioRuntimeProvider(IProcessService processes)
		: this(processes, LinuxRuntimeEnvironmentInfo.Detect())
	{
	}

	public LinuxVinegarStudioRuntimeProvider(IProcessService processes, LinuxRuntimeEnvironmentInfo runtimeEnvironment)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
		ArgumentNullException.ThrowIfNull(runtimeEnvironment);
		_prerequisiteCapability = LinuxRuntimePrerequisites.EvaluateVinegar(runtimeEnvironment);
	}

	public RuntimeKind Kind => RuntimeKind.Studio;

	public CapabilityDescriptor PrerequisiteCapability => _prerequisiteCapability;

	public async Task<RuntimeInstallation> FindInstallationAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!_prerequisiteCapability.IsAvailable)
		{
			return UnsupportedInstallation(_prerequisiteCapability);
		}

		string? vinegar = _processes.FindExecutable("vinegar");
		if (vinegar is not null)
		{
			return CreateInstallation(
				"Vinegar Native",
				null,
				vinegar,
				GetNativeDataDirectory());
		}

		string? flatpak = _processes.FindExecutable("flatpak");
		if (flatpak is null)
		{
			return MissingInstallation("Vinegar is not installed");
		}

		OperationResult<ProcessExecution> flatpakResult = await _processes.ExecuteAsync(
			new ProcessCommand(flatpak, ["info", "--show-version", VinegarApplicationId]),
			cancellationToken);
		ThrowIfCanceled(flatpakResult.Failure, cancellationToken);
		if (!flatpakResult.Succeeded)
		{
			return MissingInstallation(flatpakResult.Failure?.Message ?? "Vinegar discovery did not complete");
		}

		if (flatpakResult.Value is null || flatpakResult.Value.ExitCode != 0)
		{
			return MissingInstallation("Vinegar is not installed");
		}

		return CreateInstallation(
			"Vinegar Flatpak",
			flatpakResult.Value.StandardOutput.Trim(),
			flatpak,
			GetFlatpakDataDirectory());
	}

	public async Task<OperationResult<LaunchSession>> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (request.Kind != RuntimeKind.Studio
			|| !RobloxDeeplink.TryExtract(request.Deeplink.AbsoluteUri, out Uri? deeplink)
			|| deeplink is null
			|| RobloxDeeplink.GetRuntimeKind(deeplink.AbsoluteUri) != RuntimeKind.Studio)
		{
			return OperationResult<LaunchSession>.Fail("RuntimeKindMismatch", "The requested runtime does not match the Vinegar provider");
		}

		RuntimeInstallation installation = await FindInstallationAsync(cancellationToken);
		if (!installation.Capability.IsAvailable || string.IsNullOrWhiteSpace(installation.Location))
		{
			return OperationResult<LaunchSession>.Fail(
				"VinegarUnavailable",
				installation.Capability.Reason,
				installation.Capability.State);
		}

		IReadOnlyList<string> arguments = string.Equals(installation.Provider, "Vinegar Native", StringComparison.Ordinal)
			? [deeplink.AbsoluteUri]
			: ["run", VinegarApplicationId, deeplink.AbsoluteUri];
		OperationResult<ProcessStartResult> result = await _processes.StartAsync(
			new ProcessCommand(installation.Location, arguments, CaptureOutput: false),
			cancellationToken);
		ThrowIfCanceled(result.Failure, cancellationToken);

		if (!result.Succeeded || result.Value is null)
		{
			return result.Failure is null
				? OperationResult<LaunchSession>.Fail("VinegarLaunchFailed", "Vinegar did not start", CapabilityState.Experimental)
				: OperationResult<LaunchSession>.Fail(result.Failure.Code, result.Failure.Message, result.Failure.State);
		}

		return OperationResult<LaunchSession>.Success(new LaunchSession(
			RuntimeKind.Studio,
			installation.Provider,
			result.Value.ProcessId,
			DateTimeOffset.UtcNow,
			installation,
			false));
	}

	private static RuntimeInstallation CreateInstallation(string provider, string? version, string location, string dataDirectory)
	{
		return new RuntimeInstallation(
			RuntimeKind.Studio,
			provider,
			version,
			location,
			dataDirectory,
			new CapabilityDescriptor(FeatureId.RobloxStudio, CapabilityState.Experimental, "Vinegar is available", null, true));
	}

	private static RuntimeInstallation MissingInstallation(string reason)
	{
		return new RuntimeInstallation(
			RuntimeKind.Studio,
			"Vinegar",
			null,
			null,
			null,
			new CapabilityDescriptor(
				FeatureId.RobloxStudio,
				CapabilityState.RequiresExternalRuntime,
				reason,
				"Install Vinegar",
				true));
	}

	private static RuntimeInstallation UnsupportedInstallation(CapabilityDescriptor capability)
	{
		return new RuntimeInstallation(RuntimeKind.Studio, "Vinegar", null, null, null, capability);
	}

	private static void ThrowIfCanceled(OperationFailure? failure, CancellationToken cancellationToken)
	{
		if (string.Equals(failure?.Code, "OperationCanceled", StringComparison.Ordinal))
		{
			throw new OperationCanceledException(failure!.Message, null, cancellationToken);
		}

		cancellationToken.ThrowIfCancellationRequested();
	}

	private static string GetNativeDataDirectory()
	{
		return Path.Combine(LinuxPaths.GetXdgDataHome(), "vinegar");
	}

	private static string GetFlatpakDataDirectory()
	{
		return Path.Combine(LinuxPaths.GetHomeDirectory(), ".var", "app", VinegarApplicationId, "data", "vinegar");
	}
}

public sealed class LinuxStudioRuntimeProvider : IRobloxRuntimeProvider
{
	private readonly LinuxVinegarStudioRuntimeProvider _provider;

	public LinuxStudioRuntimeProvider()
		: this(new SystemProcessService())
	{
	}

	public LinuxStudioRuntimeProvider(IProcessService processes)
	{
		_provider = new LinuxVinegarStudioRuntimeProvider(processes);
	}

	public RuntimeKind Kind => _provider.Kind;

	public Task<RuntimeInstallation> FindInstallationAsync(CancellationToken cancellationToken = default)
	{
		return _provider.FindInstallationAsync(cancellationToken);
	}

	public Task<OperationResult<LaunchSession>> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
	{
		return _provider.LaunchAsync(request, cancellationToken);
	}
}

public sealed class LinuxProtocolRegistration : IProtocolRegistration
{
	private static readonly Regex SchemeExpression = new Regex("^[a-z][a-z0-9+.-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
	private static readonly HashSet<string> SupportedSchemes = new HashSet<string>(StringComparer.Ordinal)
	{
		"roblox",
		"roblox-player",
		"roblox-studio",
		"roblox-studio-auth"
	};

	private readonly IProcessService _processes;
	private readonly LinuxPaths _paths;

	public LinuxProtocolRegistration(IProcessService processes, LinuxPaths paths)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
		Capability = CreateCapability(processes);
	}

	public CapabilityDescriptor Capability { get; }

	public Task<CapabilityDescriptor> GetCapabilityAsync(CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return Task.FromCanceled<CapabilityDescriptor>(cancellationToken);
		}

		return Task.FromResult(Capability);
	}

	private static CapabilityDescriptor CreateCapability(IProcessService processes)
	{
		return processes.FindExecutable("xdg-mime") is null
			? new CapabilityDescriptor(FeatureId.ProtocolRegistration, CapabilityState.RequiresExternalRuntime, "The XDG MIME utility is unavailable", "Install xdg utils")
			: new CapabilityDescriptor(FeatureId.ProtocolRegistration, CapabilityState.Available, "Freedesktop protocol registration is available");
	}

	public async Task<OperationResult> RegisterAsync(ProtocolRegistrationRequest request, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return OperationResult.Fail("OperationCanceled", "Protocol registration was canceled");
		}
		if (string.IsNullOrWhiteSpace(request.Scheme) || request.Scheme.Length > 64 || !SchemeExpression.IsMatch(request.Scheme) || !SupportedSchemes.Contains(request.Scheme))
		{
			return OperationResult.Fail("InvalidProtocolScheme", "The protocol scheme is invalid");
		}

		if (!TryValidateApplicationPath(request.ApplicationPath, out string? applicationPath, out OperationResult? pathFailure))
		{
			return pathFailure!;
		}
		if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 128 || request.DisplayName.Any(char.IsControl))
			return OperationResult.Fail("ApplicationNameInvalid", "The protocol handler application name is invalid");
		string? xdgMime = _processes.FindExecutable("xdg-mime");
		if (xdgMime is null)
		{
			return OperationResult.Fail("XdgMimeUnavailable", "The XDG MIME utility is unavailable", CapabilityState.RequiresExternalRuntime);
		}

		string? temporary = null;
		string? desktopFilePath = null;
		byte[]? previousContents = null;
		UnixFileMode? previousMode = null;
		bool desktopFileChanged = false;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			Directory.CreateDirectory(_paths.ApplicationsDirectory);
			string desktopFileName = $"voidstrap-{request.Scheme}.desktop";
			desktopFilePath = Path.Combine(_paths.ApplicationsDirectory, desktopFileName);
			if (File.Exists(desktopFilePath))
			{
				previousContents = await File.ReadAllBytesAsync(desktopFilePath, cancellationToken);
				if (OperatingSystem.IsLinux())
				{
					previousMode = File.GetUnixFileMode(desktopFilePath);
				}
			}
			temporary = desktopFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			ProtocolRegistrationRequest normalizedRequest = request with { ApplicationPath = applicationPath };
			await File.WriteAllTextAsync(temporary, BuildDesktopEntry(normalizedRequest), cancellationToken);
			File.Move(temporary, desktopFilePath, true);
			temporary = null;
			desktopFileChanged = true;

			string? updateDatabase = _processes.FindExecutable("update-desktop-database");
			if (updateDatabase is not null)
			{
				OperationResult<ProcessExecution> databaseResult = await _processes.ExecuteAsync(
					new ProcessCommand(updateDatabase, [_paths.ApplicationsDirectory]),
					cancellationToken);
				if (!databaseResult.Succeeded || databaseResult.Value is null)
				{
					OperationResult failure = databaseResult.Failure is null
						? OperationResult.Fail("DesktopDatabaseUpdateFailed", "Desktop application database update failed")
						: OperationResult.Fail(databaseResult.Failure.Code, databaseResult.Failure.Message, databaseResult.Failure.State);
					return RollBack(desktopFilePath, previousContents, previousMode, failure);
				}
				if (databaseResult.Value.ExitCode != 0)
				{
					string message = string.IsNullOrWhiteSpace(databaseResult.Value.StandardError)
						? "Desktop application database update failed"
						: databaseResult.Value.StandardError;
					return RollBack(desktopFilePath, previousContents, previousMode, OperationResult.Fail("DesktopDatabaseUpdateFailed", message));
				}
			}

			cancellationToken.ThrowIfCancellationRequested();
			OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(
				new ProcessCommand(xdgMime, ["default", desktopFileName, $"x-scheme-handler/{request.Scheme}"]),
				cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();

			if (!result.Succeeded || result.Value is null)
			{
				OperationResult failure = result.Failure is null
					? OperationResult.Fail("ProtocolRegistrationFailed", "Protocol registration failed")
					: OperationResult.Fail(result.Failure.Code, result.Failure.Message, result.Failure.State);
				return RollBack(desktopFilePath, previousContents, previousMode, failure);
			}

			if (result.Value.ExitCode != 0)
			{
				string message = string.IsNullOrWhiteSpace(result.Value.StandardError)
					? "Protocol registration failed"
					: result.Value.StandardError;
				return RollBack(desktopFilePath, previousContents, previousMode, OperationResult.Fail("ProtocolRegistrationFailed", message));
			}

			desktopFileChanged = false;
			return OperationResult.Success();
		}
		catch (OperationCanceledException)
		{
			OperationResult failure = OperationResult.Fail("OperationCanceled", "Protocol registration was canceled");
			return desktopFileChanged && desktopFilePath is not null
				? RollBack(desktopFilePath, previousContents, previousMode, failure)
				: failure;
		}
		catch (IOException exception)
		{
			OperationResult failure = OperationResult.Fail("ProtocolRegistrationFailed", exception.Message);
			return desktopFileChanged && desktopFilePath is not null
				? RollBack(desktopFilePath, previousContents, previousMode, failure)
				: failure;
		}
		catch (UnauthorizedAccessException exception)
		{
			OperationResult failure = OperationResult.Fail("ProtocolRegistrationDenied", exception.Message, CapabilityState.RequiresPermission);
			return desktopFileChanged && desktopFilePath is not null
				? RollBack(desktopFilePath, previousContents, previousMode, failure)
				: failure;
		}
		finally
		{
			if (temporary is not null)
			{
				try
				{
					File.Delete(temporary);
				}
				catch (IOException)
				{
				}
				catch (UnauthorizedAccessException)
				{
				}
			}
		}
	}

	private static bool TryValidateApplicationPath(string value, out string path, out OperationResult? failure)
	{
		path = string.Empty;
		failure = null;
		if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
		{
			failure = OperationResult.Fail("ApplicationPathInvalid", "The protocol handler application path must be absolute");
			return false;
		}

		try
		{
			path = Path.GetFullPath(value);
			FileInfo file = new(path);
			if (!file.Exists || (file.Attributes & FileAttributes.Directory) != 0)
			{
				failure = OperationResult.Fail("ApplicationPathMissing", "The protocol handler application path does not exist");
				return false;
			}
			if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
			{
				failure = OperationResult.Fail("ApplicationPathInvalid", "The protocol handler application must be a regular file");
				return false;
			}

			if (OperatingSystem.IsLinux())
			{
				UnixFileMode mode = File.GetUnixFileMode(path);
				UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
				if ((mode & execute) == 0)
				{
					failure = OperationResult.Fail("ApplicationNotExecutable", "The protocol handler application is not executable", CapabilityState.RequiresPermission);
					return false;
				}
			}

			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			failure = OperationResult.Fail("ApplicationPathInvalid", exception.Message);
			return false;
		}
	}

	private static OperationResult RollBack(string desktopFilePath, byte[]? previousContents, UnixFileMode? previousMode, OperationResult failure)
	{
		try
		{
			if (previousContents is null)
			{
				File.Delete(desktopFilePath);
			}
			else
			{
				string rollback = desktopFilePath + "." + Guid.NewGuid().ToString("N") + ".rollback";
				try
				{
					File.WriteAllBytes(rollback, previousContents);
					if (OperatingSystem.IsLinux() && previousMode is not null)
					{
						File.SetUnixFileMode(rollback, previousMode.Value);
					}
					File.Move(rollback, desktopFilePath, true);
				}
				finally
				{
					File.Delete(rollback);
				}
			}
			return failure;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return OperationResult.Fail("ProtocolRollbackFailed", exception.Message, CapabilityState.RequiresPermission);
		}
	}

	public static string BuildDesktopEntry(ProtocolRegistrationRequest request)
	{
		string escapedPath = EscapeExecQuotedArgument(request.ApplicationPath);
		string escapedName = request.DisplayName.Replace("\\", "\\\\").Replace("\n", " ").Replace("\r", " ");

		return $"[Desktop Entry]\nType=Application\nName={escapedName}\nExec=\"{escapedPath}\" %u\nMimeType=x-scheme-handler/{request.Scheme};\nNoDisplay=true\n";
	}

	private static string EscapeExecQuotedArgument(string value)
	{
		StringBuilder builder = new(value.Length + 16);
		foreach (char character in value)
		{
			switch (character)
			{
				case '\\':
					builder.Append('\\', 4);
					break;
				case '"':
					builder.Append('\\', 3);
					builder.Append(character);
					break;
				case '$':
				case '`':
					builder.Append('\\', 2);
					builder.Append(character);
					break;
				case '%':
					builder.Append("%%");
					break;
				default:
					builder.Append(character);
					break;
			}
		}

		return builder.ToString();
	}
}
