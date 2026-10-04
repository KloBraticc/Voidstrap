using System;
using System.Collections.Generic;
using System.Globalization;
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

public sealed partial class LinuxPlatformHost : IPlatformHost
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
			CreateResourceOptimizationCapability(),
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
		yield return new CapabilityDescriptor(FeatureId.Tray, CapabilityState.Experimental, "The tray icon uses StatusNotifierItem when the desktop provides a status notifier host", null, true);
		yield return overlay;
		yield return input;
		yield return audioSession;
		yield return CreateResourceOptimizationCapability();
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

	private static ProcessNotificationService CreateNotifications(IProcessService processes)
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
		return LinuxWindowInterop.IsAvailable
			? new CapabilityDescriptor(FeatureId.Overlay, CapabilityState.Experimental, "The X11 overlay adapter can track and cover the Sober window", null, true)
			: new CapabilityDescriptor(FeatureId.Overlay, CapabilityState.RequiresExternalRuntime, "Overlays require an X11 or XWayland session", "Start the desktop session on X11, or run Voidstrap with DISPLAY set");
	}

	private static CapabilityDescriptor CreateResourceOptimizationCapability()
	{
		return LinuxSoberResources.IsSupported
			? new CapabilityDescriptor(FeatureId.ResourceOptimization, CapabilityState.Experimental, "Resource controls adjust the Sober sandbox through systemd and CPU affinity", null, true)
			: new CapabilityDescriptor(FeatureId.ResourceOptimization, CapabilityState.Unavailable, "Resource controls need systemd and cgroup v2 outside a Flatpak sandbox");
	}

	private static CapabilityDescriptor CreateInputCapability()
	{
		return LinuxKeyboardInterceptor.CheckAccess() == LinuxInputAccessState.Ready
			? new CapabilityDescriptor(FeatureId.GlobalInput, CapabilityState.Experimental, "Snap Tap reads the keyboard through evdev and replays it through a uinput keyboard while Sober is focused", null, true)
			: new CapabilityDescriptor(FeatureId.GlobalInput, CapabilityState.RequiresPermission, "Snap Tap needs access to the keyboard and to /dev/uinput", "Use Grant keyboard access in Voidstrap, or add your user to the input group");
	}

	private static CapabilityDescriptor CreateAudioSessionCapability()
	{
		return LinuxAudioSessions.IsAvailable
			? new CapabilityDescriptor(FeatureId.AudioSession, CapabilityState.Experimental, "Sober audio is lowered through PipeWire while Sober is not focused", null, true)
			: new CapabilityDescriptor(FeatureId.AudioSession, CapabilityState.Unavailable, "Lowering Sober audio needs the PipeWire pw-dump and wpctl tools");
	}
}

public sealed partial class LinuxPaths : PlatformPathsBase
{
	public LinuxPaths()
		: this(new SystemProcessService())
	{
	}

	private readonly IProcessService _processes;

	public LinuxPaths(IProcessService processes)
		: base(CreateStorage(processes ?? throw new ArgumentNullException(nameof(processes))))
	{
		_processes = processes;
	}

	public static string DefaultDataDirectory => Path.Combine(GetXdgDirectory("XDG_DATA_HOME", ".local", "share"), "voidstrap");

	public static string DataLocationFile => Path.Combine(GetXdgDirectory("XDG_CONFIG_HOME", ".config"), "voidstrap", "DataLocation");

	public void RelocateData(string dataDirectory)
	{
		if (!TryGetAbsolutePath(dataDirectory, out string target))
			throw new ArgumentException("The data folder must be an absolute path", nameof(dataDirectory));

		target = Path.TrimEndingDirectorySeparator(target);
		string file = DataLocationFile;
		if (string.Equals(target, Path.TrimEndingDirectorySeparator(DefaultDataDirectory), StringComparison.Ordinal))
		{
			if (File.Exists(file))
				File.Delete(file);
		}
		else
		{
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, target + "\n");
		}

		Storage = CreateStorage(_processes);
	}

	private static string ResolveDataDirectory()
	{
		string fallback = DefaultDataDirectory;
		try
		{
			string file = DataLocationFile;
			if (!File.Exists(file))
				return fallback;

			string configured = File.ReadAllText(file).Trim();
			if (configured.IndexOfAny(['\r', '\n', '\0']) >= 0 || !TryGetAbsolutePath(configured, out string path))
				return fallback;

			path = Path.TrimEndingDirectorySeparator(path);
			string? parent = Path.GetDirectoryName(path);
			if (string.IsNullOrEmpty(parent) || (!Directory.Exists(path) && !Directory.Exists(parent)))
				return fallback;

			return path;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return fallback;
		}
	}

	public string ApplicationsDirectory => Path.Combine(GetXdgDirectory("XDG_DATA_HOME", ".local", "share"), "applications");

	private static PlatformStoragePaths CreateStorage(IProcessService processes)
	{
		string config = Path.Combine(GetXdgDirectory("XDG_CONFIG_HOME", ".config"), "voidstrap");
		string data = ResolveDataDirectory();
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
				Match match = XdgDownloadDirPattern.Match(line);
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

	[GeneratedRegex("^\\s*XDG_DOWNLOAD_DIR\\s*=\\s*\"(?<value>(?:[^\"\\\\]|\\\\.)*)\"\\s*$", RegexOptions.CultureInvariant)]
	private static partial Regex XdgDownloadDirPattern { get; }
}

internal static partial class LinuxRuntimeDirectory
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

		string user = UnsafeUserNameCharacterPattern.Replace(userName ?? string.Empty, "_");
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

	private static partial class NativeMethods
	{
		[LibraryImport("libc", EntryPoint = "mkdir", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
		public static partial int CreateDirectory(string path, uint mode);

		[LibraryImport("libc", EntryPoint = "geteuid")]
		public static partial uint GetEffectiveUserId();

		[LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
		public static partial int GetFileStatus(int directoryFileDescriptor, string path, int flags, uint mask, IntPtr status);
	}

	[GeneratedRegex("[^A-Za-z0-9_.]")]
	private static partial Regex UnsafeUserNameCharacterPattern { get; }
}

public sealed partial class LinuxSecureStore : ISecureStore
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
		if (value.EndsWith('\n'))
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

internal static partial class LinuxRuntimePrerequisites
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

public sealed partial class LinuxSoberRuntimeProvider : IRobloxRuntimeProvider
{
	private const string SoberApplicationId = "org.vinegarhq.Sober";
	private const long InstalledCacheMilliseconds = 5000;
	private static int _installedState = -1;
	private static int _installedRefreshActive;
	private static long _installedCacheExpires;

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

	public static bool ForceX11Session { get; set; }

	public static bool StartedInThisProcess { get; private set; }

	public static IDisposable? TryAcquireLaunchLease()
	{
		string directory = Path.Combine(LinuxPaths.GetHomeDirectory(), ".var", "app", LinuxFlatpakHost.DefaultApplicationId, "cache", "voidstrap");
		Directory.CreateDirectory(directory);
		try
		{
			return new FileStream(Path.Combine(directory, "sober-launch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
		}
		catch (IOException)
		{
			return null;
		}
	}


	public static Func<CancellationToken, Task<bool>>? OnboardingAssist { get; set; }

	private static System.Diagnostics.Process? StartSoberKill()
	{
		return LinuxFlatpakHost.Start(["kill", SoberApplicationId]);
	}

	private static async Task<bool> IsSoberRunningAsync(CancellationToken cancellationToken)
	{
		try
		{
			LinuxSoberProcessProbe probe = new(new SystemProcessService());
			return await probe.IsRunningAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static async Task<bool> WaitForSoberStoppedAsync(CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(15));
		int stoppedChecks = 0;

		try
		{
			while (!timeout.IsCancellationRequested)
			{
				if (IsSoberInstanceLocked() || await IsSoberRunningAsync(timeout.Token).ConfigureAwait(false))
				{
					stoppedChecks = 0;
				}
				else if (++stoppedChecks >= 2)
				{
					return true;
				}

				await Task.Delay(250, timeout.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
		}

		cancellationToken.ThrowIfCancellationRequested();
		return false;
	}

	private const string SoberInstanceRunningMessage = "An instance of Sober is already running";

	[StructLayout(LayoutKind.Explicit, Size = 256)]
	private struct SoberPackageStatus
	{
		[FieldOffset(32)]
		public ulong Inode;

		[FieldOffset(136)]
		public uint DeviceMajor;

		[FieldOffset(140)]
		public uint DeviceMinor;
	}

	[LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
	private static partial int GetSoberPackageStatus(int directoryFileDescriptor, string path, int flags, uint mask, out SoberPackageStatus status);

	private const int CurrentDirectoryDescriptor = -100;

	private const uint StatusInodeMask = 0x100;

	private static List<int> SoberInstanceLockOwners()
	{
		List<int> owners = [];
		try
		{
			string packages = Path.Combine(SoberDataDirectory, "packages");
			if (!Directory.Exists(packages))
				return owners;

			HashSet<string> keys = new(StringComparer.Ordinal);
			foreach (string package in Directory.EnumerateFiles(packages, "*.apk", SearchOption.AllDirectories))
			{
				if (GetSoberPackageStatus(CurrentDirectoryDescriptor, package, 0, StatusInodeMask, out SoberPackageStatus status) == 0)
				{
					keys.Add(status.DeviceMajor.ToString("x2", CultureInfo.InvariantCulture)
						+ ":" + status.DeviceMinor.ToString("x2", CultureInfo.InvariantCulture)
						+ ":" + status.Inode.ToString(CultureInfo.InvariantCulture));
				}
			}

			if (keys.Count == 0)
				return owners;

			foreach (string line in File.ReadLines("/proc/locks"))
			{
				if (!line.Contains("FLOCK", StringComparison.Ordinal) || line.Contains("->", StringComparison.Ordinal))
					continue;

				string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				for (int index = 1; index < parts.Length; index++)
				{
					if (!keys.Contains(parts[index]))
						continue;
					if (int.TryParse(parts[index - 1], NumberStyles.None, CultureInfo.InvariantCulture, out int owner) && owner != Environment.ProcessId)
						owners.Add(owner);
					break;
				}
			}
		}
		catch (Exception)
		{
		}

		return owners;
	}

	private static bool IsSoberInstanceLocked()
	{
		return SoberInstanceLockOwners().Count > 0;
	}

	private static bool IsSoberInstanceHeldByNewProcess(IReadOnlyCollection<int> previousOwners)
	{
		foreach (int owner in SoberInstanceLockOwners())
		{
			if (!previousOwners.Contains(owner) && IsSoberSandboxProcess(owner))
				return true;
		}

		return false;
	}

	private static bool IsSoberSandboxProcess(int processId)
	{
		try
		{
			return File.ReadAllText("/proc/" + processId.ToString(CultureInfo.InvariantCulture) + "/cgroup").Contains(SoberApplicationId, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception)
		{
			return false;
		}
	}

	private sealed record SoberStartupLogSnapshot(ulong Inode, uint DeviceMajor, uint DeviceMinor, long Length, byte[] Prefix, byte[] Tail);

	private static SoberStartupLogSnapshot? CaptureSoberStartupLog()
	{
		try
		{
			string path = Path.Combine(SoberDataDirectory, "sober_logs", "latest.log");
			if (GetSoberPackageStatus(CurrentDirectoryDescriptor, path, 0, StatusInodeMask, out SoberPackageStatus status) != 0)
				return null;
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long length = stream.Length;
			byte[] prefix = new byte[(int)Math.Min(length, 256)];
			int read = stream.ReadAtLeast(prefix, prefix.Length, false);
			byte[] tail = new byte[(int)Math.Min(length, 65536)];
			stream.Seek(length - tail.Length, SeekOrigin.Begin);
			int tailRead = stream.ReadAtLeast(tail, tail.Length, false);
			return new SoberStartupLogSnapshot(status.Inode, status.DeviceMajor, status.DeviceMinor, length, prefix[..read], tail[..tailRead]);
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static OperationFailure? ReadSoberStartupFailure(DateTime launchedUtc, SoberStartupLogSnapshot? previousLog)
	{
		try
		{
			FileInfo log = new(Path.Combine(SoberDataDirectory, "sober_logs", "latest.log"));
			if (!log.Exists || log.LastWriteTimeUtc < launchedUtc)
				return null;
			using FileStream stream = new(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			long offset = 0;
			if (previousLog is not null
				&& stream.Length >= previousLog.Length
				&& GetSoberPackageStatus(CurrentDirectoryDescriptor, log.FullName, 0, StatusInodeMask, out SoberPackageStatus status) == 0
				&& status.Inode == previousLog.Inode
				&& status.DeviceMajor == previousLog.DeviceMajor
				&& status.DeviceMinor == previousLog.DeviceMinor)
			{
				byte[] prefix = new byte[previousLog.Prefix.Length];
				int read = stream.ReadAtLeast(prefix, prefix.Length, false);
				if (read == prefix.Length && prefix.AsSpan().SequenceEqual(previousLog.Prefix))
				{
					byte[] tail = new byte[previousLog.Tail.Length];
					stream.Seek(previousLog.Length - tail.Length, SeekOrigin.Begin);
					int tailRead = stream.ReadAtLeast(tail, tail.Length, false);
					if (tailRead == tail.Length && tail.AsSpan().SequenceEqual(previousLog.Tail))
						offset = previousLog.Length;
				}
			}
			offset = Math.Max(offset, stream.Length - 65536);
			stream.Seek(offset, SeekOrigin.Begin);
			byte[] buffer = new byte[(int)Math.Min(stream.Length - offset, 65536)];
			int count = stream.ReadAtLeast(buffer, buffer.Length, false);
			foreach (string line in Encoding.UTF8.GetString(buffer, 0, count).Split('\n'))
			{
				const string marker = "FATAL: Crash:";
				string text = line.TrimStart();
				if (text.StartsWith("ERROR: window: SDL_INIT_VIDEO failed.", StringComparison.OrdinalIgnoreCase))
					return new OperationFailure("SoberDisplayUnavailable", "Sober could not open a window. Start Voidstrap from your desktop session and check that Sober has access to its Wayland or X11 socket.", CapabilityState.Experimental);
				if (!text.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
					continue;
				string detail = text[marker.Length..].Trim();
				if (detail.Length == 0
					|| detail.StartsWith("Previous error is fatal", StringComparison.OrdinalIgnoreCase)
					|| detail.StartsWith("Aborting", StringComparison.OrdinalIgnoreCase))
					continue;
				if (detail.Contains(SoberInstanceRunningMessage, StringComparison.OrdinalIgnoreCase))
					return new OperationFailure("SoberInstanceRunning", "Another Sober instance is already running. Close it before trying again.", CapabilityState.Experimental);
				if (detail.Contains("SDL_INIT_VIDEO", StringComparison.OrdinalIgnoreCase)
					|| detail.Contains("wayland", StringComparison.OrdinalIgnoreCase)
					|| detail.Contains("x11", StringComparison.OrdinalIgnoreCase)
					|| detail.Contains("video driver", StringComparison.OrdinalIgnoreCase)
					|| detail.Contains("video subsystem", StringComparison.OrdinalIgnoreCase))
					return new OperationFailure("SoberDisplayUnavailable", "Sober could not open a window. Start Voidstrap from your desktop session and check that Sober has access to its Wayland or X11 socket.", CapabilityState.Experimental);
				detail = new string(detail.Where(character => !char.IsControl(character)).Take(256).ToArray());
				return new OperationFailure("SoberStartupCrash", "Sober stopped while starting: " + detail, CapabilityState.Experimental);
			}
		}
		catch (Exception)
		{
		}
		return null;
	}

	private static async Task<bool> ProbeSoberStartupAsync(CancellationToken cancellationToken)
	{
		using CancellationTokenSource probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		probeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
		try
		{
			return await IsSoberRunningAsync(probeTimeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
	}

	private static async Task<OperationResult> WaitForSoberStartedAsync(DateTime launchedUtc, SoberStartupLogSnapshot? previousLog, IReadOnlyCollection<int> previousOwners, CancellationToken cancellationToken)
	{
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(45));
		int runningChecks = 0;

		try
		{
			while (!timeout.IsCancellationRequested)
			{
				OperationFailure? failure = ReadSoberStartupFailure(launchedUtc, previousLog);
				cancellationToken.ThrowIfCancellationRequested();
				if (failure is not null)
					return OperationResult.Fail(failure.Code, failure.Message, failure.State);

				bool running = await ProbeSoberStartupAsync(timeout.Token).ConfigureAwait(false);
				failure = ReadSoberStartupFailure(launchedUtc, previousLog);
				cancellationToken.ThrowIfCancellationRequested();
				if (failure is not null)
					return OperationResult.Fail(failure.Code, failure.Message, failure.State);
				if (running)
				{
					runningChecks++;
					if (runningChecks >= 2 && IsSoberInstanceHeldByNewProcess(previousOwners))
					{
						cancellationToken.ThrowIfCancellationRequested();
						return OperationResult.Success();
					}
					if (runningChecks >= 3 && DateTime.UtcNow - launchedUtc >= SoberUnlockedStartGrace)
					{
						cancellationToken.ThrowIfCancellationRequested();
						return OperationResult.Success();
					}
				}
				else
				{
					runningChecks = 0;
				}

				await Task.Delay(500, timeout.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
		}

		cancellationToken.ThrowIfCancellationRequested();
		return OperationResult.Fail("SoberLaunchFailed", "Sober did not finish starting. Check Sober's latest log and its display socket permissions before trying again.", CapabilityState.Experimental);
	}

	private static readonly TimeSpan SoberUnlockedStartGrace = TimeSpan.FromSeconds(4);

	private static string SoberDataDirectory => Path.Combine(
		Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		".var", "app", SoberApplicationId, "data", "sober");

	private static string RobloxPackageDirectory => Path.Combine(SoberDataDirectory, "packages", "x86_64", "com.roblox.client");

	public static bool IsRobloxPackageInstalled()
	{
		try
		{
			return HasCompleteRobloxPackage();
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	public static async Task<bool> NeedsSandboxRefreshAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.IsSandboxed)
			return false;
		string root = Path.GetDirectoryName(Path.GetDirectoryName(SoberDataDirectory))!;
		SystemProcessService processes = new();
		if (!LinuxFlatpakHost.TryCreateHostCommand(processes, "stat", ["-Lc", "%d:%i", "--", root], out ProcessCommand command))
			return false;
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		if (!result.Succeeded || result.Value is not { ExitCode: 0 })
			return false;
		if (GetSoberPackageStatus(CurrentDirectoryDescriptor, root, 0, StatusInodeMask, out SoberPackageStatus status) != 0)
			return true;
		ulong major = status.DeviceMajor;
		ulong minor = status.DeviceMinor;
		ulong device = (minor & 0xff) | ((major & 0xfff) << 8) | ((minor & ~0xffUL) << 12) | ((major & ~0xfffUL) << 32);
		string identity = device.ToString(CultureInfo.InvariantCulture) + ":" + status.Inode.ToString(CultureInfo.InvariantCulture);
		return !string.Equals(result.Value.StandardOutput.Trim(), identity, StringComparison.Ordinal);
	}

	public static async Task<bool> IsRobloxPackageInstalledAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.IsSandboxed)
			return IsRobloxPackageInstalled();

		const string probe = """
			for directory in "$1"/packages/*/com.roblox.client; do
			    [ -f "$directory/base.apk" ] || continue
			    complete=1
			    for package in "$directory"/*.apk; do
			        tail -c 65557 -- "$package" | od -An -v -tu1 | awk '
			            { for (i = 1; i <= NF; i++) bytes[++count] = $i }
			            END {
			                for (i = count - 21; i >= 1; i--)
			                    if (bytes[i] == 80 && bytes[i+1] == 75 && bytes[i+2] == 5 && bytes[i+3] == 6 && i + 21 + bytes[i+20] + 256 * bytes[i+21] == count)
			                        exit 0
			                exit 1
			            }' || { complete=0; break; }
			    done
			    [ "$complete" = 1 ] && exit 0
			done
			exit 1
			""";
		SystemProcessService processes = new();
		if (!LinuxFlatpakHost.TryCreateHostCommand(processes, "sh", ["-c", probe, "voidstrap", SoberDataDirectory], out ProcessCommand command))
			return false;
		using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(5));
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		return result.Succeeded && result.Value?.ExitCode == 0;
	}

	private static bool HasCompleteRobloxPackage()
	{
		string root = Path.Combine(SoberDataDirectory, "packages");
		if (!Directory.Exists(root))
			return false;

		foreach (string architecture in Directory.EnumerateDirectories(root))
		{
			string directory = Path.Combine(architecture, "com.roblox.client");
			if (!Directory.Exists(directory))
				continue;
			string[] packages = Directory.GetFiles(directory, "*.apk");
			if (packages.Any(static package => string.Equals(Path.GetFileName(package), "base.apk", StringComparison.Ordinal))
				&& packages.All(IsCompleteArchive))
				return true;
		}
		return false;
	}

	private static bool IsCompleteArchive(string path)
	{
		try
		{
			using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			int size = (int)Math.Min(stream.Length, 65557);
			if (size < 22)
				return false;

			stream.Seek(-size, SeekOrigin.End);
			byte[] tail = new byte[size];
			stream.ReadExactly(tail);
			for (int index = size - 22; index >= 0; index--)
			{
				if (tail[index] == 0x50 && tail[index + 1] == 0x4b && tail[index + 2] == 0x05 && tail[index + 3] == 0x06
					&& index + 22 + tail[index + 20] + (tail[index + 21] << 8) == size)
					return true;
			}

			return false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private static void RemoveInterruptedRobloxPackage()
	{
		try
		{
			string directory = RobloxPackageDirectory;
			if (!Directory.Exists(directory))
				return;

			string[] packages = Directory.GetFiles(directory, "*.apk");
			if (packages.Length == 0 || packages.All(IsCompleteArchive))
				return;

			foreach (string package in packages)
				File.Delete(package);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
		}
	}

	public static string? GetInstalledRobloxVersion()
	{
		try
		{
			string state = Path.Combine(SoberDataDirectory, "state");
			if (!File.Exists(state))
				return null;
			using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(state));
			return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
				&& document.RootElement.TryGetProperty("v1", out System.Text.Json.JsonElement v1)
				&& v1.ValueKind == System.Text.Json.JsonValueKind.Object
				&& v1.TryGetProperty("app_version", out System.Text.Json.JsonElement version)
				&& version.ValueKind == System.Text.Json.JsonValueKind.String
				&& !string.IsNullOrWhiteSpace(version.GetString())
				? version.GetString()
				: null;
		}
		catch (Exception)
		{
			return null;
		}
	}

	public static long DownloadedRobloxBytes()
	{
		try
		{
			string packages = Path.Combine(SoberDataDirectory, "packages");
			if (!Directory.Exists(packages))
				return 0;

			long total = 0;
			foreach (string file in Directory.EnumerateFiles(packages, "*.apk", SearchOption.AllDirectories))
				total += new FileInfo(file).Length;
			return total;
		}
		catch (Exception)
		{
			return 0;
		}
	}

	public static async Task<bool> TryDownloadRobloxPackageAsync(CancellationToken cancellationToken, Action<string>? report = null, Action? onPackageReady = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (await IsRobloxPackageInstalledAsync(cancellationToken).ConfigureAwait(false))
		{
			onPackageReady?.Invoke();
			return true;
		}

		if (!await IsSoberRunningAsync(cancellationToken).ConfigureAwait(false))
			RemoveInterruptedRobloxPackage();

		System.Diagnostics.Process? process = null;
		bool installed = false;
		Func<CancellationToken, Task<bool>>? assist = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ? null : OnboardingAssist;
		using CancellationTokenSource assistStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		Task<bool>? assisting = null;

		try
		{
			report?.Invoke("Downloading Sober");
			SystemProcessService processes = new();
			await LinuxFlatpakHost.PrepareSessionEnvironmentAsync(processes, cancellationToken).ConfigureAwait(false);
			OperationResult<SoberFlatpakSelection> discovered = await FindSoberInstallationAsync(processes, cancellationToken).ConfigureAwait(false);
			if (!discovered.Succeeded || discovered.Value is null)
				return false;
			SoberFlatpakSelection selection = discovered.Value;
			StartedInThisProcess = true;
			process = assist is null
				? LinuxFlatpakHost.Start(["run", selection.Scope, selection.Reference])
				: LinuxFlatpakHost.Start(["run", selection.Scope, "--nosocket=wayland", "--socket=x11", selection.Reference]);
			if (process is null)
				return false;

			assisting = assist?.Invoke(assistStop.Token);

			for (int attempt = 0; attempt < 1800; attempt++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (await IsRobloxPackageInstalledAsync(cancellationToken).ConfigureAwait(false))
				{
					installed = true;
					onPackageReady?.Invoke();
					await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
					return true;
				}

				if (process is { HasExited: true }
					&& !await IsSoberRunningAsync(cancellationToken).ConfigureAwait(false))
				{
					installed = await IsRobloxPackageInstalledAsync(cancellationToken).ConfigureAwait(false);
					if (installed)
						onPackageReady?.Invoke();
					return installed;
				}

				await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
			}

			return false;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !installed)
		{
			throw;
		}
		catch (Exception)
		{
			return installed;
		}
		finally
		{
			try
			{
				if (installed)
					TryCloseSober();
				assistStop.Cancel();
				if (assisting is not null)
					await assisting.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
				process?.Dispose();
			}
			catch (Exception)
			{
			}
		}
	}

	public static bool TryCloseSober()
	{
		try
		{
			return TryCloseSoberAsync(CancellationToken.None).GetAwaiter().GetResult();
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static async Task<bool> TryCloseSoberAsync(CancellationToken cancellationToken)
	{
		if (!await IsSoberRunningAsync(cancellationToken).ConfigureAwait(false))
			return !IsSoberInstanceLocked() || await WaitForSoberStoppedAsync(cancellationToken).ConfigureAwait(false) || !IsSoberInstanceLocked();

		try
		{
			using System.Diagnostics.Process? process = StartSoberKill();

			if (process is null)
				return false;

			using CancellationTokenSource commandTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			commandTimeout.CancelAfter(TimeSpan.FromSeconds(5));

			await process.WaitForExitAsync(commandTimeout.Token).ConfigureAwait(false);
			if (process.ExitCode != 0 && await IsSoberRunningAsync(cancellationToken).ConfigureAwait(false))
				return false;

			return await WaitForSoberStoppedAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	public static IReadOnlyList<string> EffectLayerArguments { get; set; } = [];

	public static IReadOnlyList<string> ProxyArguments { get; set; } = [];



	public static bool IsInstalled()
	{
		try
		{
			if (LinuxFlatpakHost.IsSandboxed)
			{
				int state = Volatile.Read(ref _installedState);
				if (state < 0 || Environment.TickCount64 >= Interlocked.Read(ref _installedCacheExpires))
					QueueInstalledRefresh();
				return state < 0 ? HasSandboxedSoberData() : state == 1;
			}

			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			string[] roots =
			[
				Path.Combine(home, ".local", "share", "flatpak", "app", SoberApplicationId),
				Path.Combine("/var", "lib", "flatpak", "app", SoberApplicationId)
			];

			foreach (string root in roots)
			{
				if (Directory.Exists(root))
					return true;
			}

			return false;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool HasSandboxedSoberData()
	{
		try
		{
			string home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
			return Directory.Exists(Path.Combine(home, ".var", "app", SoberApplicationId));
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static void QueueInstalledRefresh()
	{
		if (Interlocked.CompareExchange(ref _installedRefreshActive, 1, 0) != 0)
			return;
		_ = Task.Run(RefreshInstalledStateAsync);
	}

	private static async Task RefreshInstalledStateAsync()
	{
		bool installed = HasSandboxedSoberData();
		try
		{
			SystemProcessService processes = new();
			if (LinuxFlatpakHost.TryCreateCommand(processes, ["info", SoberApplicationId], out ProcessCommand command))
			{
				using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
				OperationResult<ProcessExecution> result = await processes.ExecuteAsync(command, timeout.Token).ConfigureAwait(false);
				if (result.Succeeded && result.Value is not null)
					installed = result.Value.ExitCode == 0;
			}
		}
		catch (Exception)
		{
		}
		finally
		{
			Volatile.Write(ref _installedState, installed ? 1 : 0);
			Interlocked.Exchange(ref _installedCacheExpires, Environment.TickCount64 + InstalledCacheMilliseconds);
			Volatile.Write(ref _installedRefreshActive, 0);
		}
	}

	public CapabilityDescriptor PrerequisiteCapability => _prerequisiteCapability;

	private sealed record SoberFlatpakSelection(RuntimeInstallation Installation, string Scope, string Reference);

	private static async Task<OperationResult<SoberFlatpakSelection>> FindSoberInstallationAsync(IProcessService processes, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!LinuxFlatpakHost.TryCreateCommand(processes, ["info", SoberApplicationId], out ProcessCommand infoCommand))
			return OperationResult<SoberFlatpakSelection>.Fail("FlatpakMissing", "Flatpak is not installed", CapabilityState.RequiresExternalRuntime);
		string flatpak = LinuxFlatpakHost.IsSandboxed ? "flatpak" : infoCommand.FileName;
		if (!LinuxFlatpakHost.TryCreateHostCommand(processes, "env", ["LC_ALL=C", flatpak, "info", SoberApplicationId], out ProcessCommand localizedCommand))
			return OperationResult<SoberFlatpakSelection>.Fail("SoberDiscoveryFailed", "Sober's Flatpak installation could not be queried", CapabilityState.Experimental);
		OperationResult<ProcessExecution> result = await processes.ExecuteAsync(localizedCommand, cancellationToken).ConfigureAwait(false);
		ThrowIfCanceled(result.Failure, cancellationToken);
		if (!result.Succeeded || result.Value is null)
			return OperationResult<SoberFlatpakSelection>.Fail(result.Failure?.Code ?? "SoberDiscoveryFailed", result.Failure?.Message ?? "Sober discovery did not complete", CapabilityState.Experimental);
		if (result.Value.ExitCode != 0)
			return OperationResult<SoberFlatpakSelection>.Fail("SoberUnavailable", "Sober is not installed", CapabilityState.RequiresExternalRuntime);
		string? scope = null, reference = null;
		foreach (string line in result.Value.StandardOutput.Split('\n'))
		{
			string value = line.Trim();
			if (value.StartsWith("Installation:", StringComparison.Ordinal))
			{
				string name = value["Installation:".Length..].Trim();
				if (name == "user")
					scope = "--user";
				else if (name == "system")
					scope = "--system";
				else if (name.StartsWith("system (", StringComparison.Ordinal) && name.EndsWith(')'))
				{
					string id = name["system (".Length..^1];
					if (id.Length is > 0 and <= 255 && !id.Any(char.IsControl))
						scope = "--installation=" + id;
				}
			}
			else if (value.StartsWith("Ref:", StringComparison.Ordinal))
			{
				string candidate = value["Ref:".Length..].Trim();
				string[] parts = candidate.Split('/');
				if (candidate.Length <= 512 && parts.Length == 4 && parts[0] == "app" && parts[1] == SoberApplicationId
					&& parts.Skip(2).All(part => part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
					reference = candidate;
			}
		}
		if (scope is null || reference is null)
			return OperationResult<SoberFlatpakSelection>.Fail("SoberDiscoveryFailed", "Sober's Flatpak installation could not be identified", CapabilityState.Experimental);
		RuntimeInstallation installation = new(
			RuntimeKind.Player,
			"Sober",
			FlatpakApplicationInfo.ParseVersion(result.Value.StandardOutput) ?? string.Empty,
			infoCommand.FileName,
			GetSoberDataDirectory(),
			new CapabilityDescriptor(FeatureId.RobloxPlayer, CapabilityState.Experimental, "Sober is available", null, true));
		return OperationResult<SoberFlatpakSelection>.Success(new SoberFlatpakSelection(installation, scope, reference));
	}

	public async Task<RuntimeInstallation> FindInstallationAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!_prerequisiteCapability.IsAvailable)
			return UnsupportedInstallation(_prerequisiteCapability);
		OperationResult<SoberFlatpakSelection> result = await FindSoberInstallationAsync(_processes, cancellationToken).ConfigureAwait(false);
		return result.Succeeded && result.Value is not null
			? result.Value.Installation
			: MissingInstallation(result.Failure?.Message ?? "Sober discovery did not complete");
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

		if (!_prerequisiteCapability.IsAvailable)
			return OperationResult<LaunchSession>.Fail("SoberUnavailable", _prerequisiteCapability.Reason, _prerequisiteCapability.State);
		OperationResult<SoberFlatpakSelection> discovered = await FindSoberInstallationAsync(_processes, cancellationToken).ConfigureAwait(false);
		if (!discovered.Succeeded || discovered.Value is null)
			return OperationResult<LaunchSession>.Fail(discovered.Failure?.Code ?? "SoberUnavailable", discovered.Failure?.Message ?? "Sober discovery did not complete", discovered.Failure?.State ?? CapabilityState.Experimental);
		SoberFlatpakSelection selection = discovered.Value;
		RuntimeInstallation installation = selection.Installation;

		await LinuxFlatpakHost.PrepareSessionEnvironmentAsync(_processes, cancellationToken).ConfigureAwait(false);
		List<string> arguments = ["run", selection.Scope];
		if (ForceX11Session)
		{
			arguments.Add("--nosocket=wayland");
			arguments.Add("--socket=x11");
			arguments.Add("--env=SDL_VIDEODRIVER=x11");
		}

		foreach (string argument in EffectLayerArguments)
			arguments.Add(argument);

		foreach (string argument in ProxyArguments)
			arguments.Add(argument);

		arguments.Add(selection.Reference);

		Uri soberLink = SoberLaunchLink.Normalize(deeplink);
		if (!SoberLaunchLink.IsHomeLaunch(soberLink))
			arguments.Add(soberLink.AbsoluteUri);

		if (!await TryCloseSoberAsync(cancellationToken).ConfigureAwait(false))
		{
			return OperationResult<LaunchSession>.Fail(
				"SoberCloseFailed",
				"The current Sober session could not be closed before joining the requested server",
				CapabilityState.Experimental);
		}

		if (!LinuxFlatpakHost.TryCreateCommand(_processes, arguments, out ProcessCommand launchCommand, false))
			return OperationResult<LaunchSession>.Fail("FlatpakMissing", "Flatpak is not installed", CapabilityState.RequiresExternalRuntime);

		string flatpak = LinuxFlatpakHost.IsSandboxed ? "flatpak" : launchCommand.FileName;
		if (LinuxSoberResources.TryGetLaunchCpuList(out string cpuList)
			&& LinuxFlatpakHost.TryCreateHostCommand(_processes, "taskset", ["--cpu-list", cpuList, flatpak, .. arguments], out ProcessCommand pinnedCommand, false))
			launchCommand = pinnedCommand;

		List<int> previousOwners = SoberInstanceLockOwners();
		SoberStartupLogSnapshot? previousLog = CaptureSoberStartupLog();
		DateTime launchedUtc = DateTime.UtcNow;
		StartedInThisProcess = true;
		OperationResult<ProcessStartResult> result = await _processes.StartAsync(launchCommand, cancellationToken).ConfigureAwait(false);
		ThrowIfCanceled(result.Failure, cancellationToken);
		if (!result.Succeeded || result.Value is null)
			return OperationResult<LaunchSession>.Fail(result.Failure?.Code ?? "SoberLaunchFailed", result.Failure?.Message ?? "Sober could not start", CapabilityState.Experimental);
		OperationResult started = await WaitForSoberStartedAsync(launchedUtc, previousLog, previousOwners, cancellationToken).ConfigureAwait(false);
		if (!started.Succeeded)
			return OperationResult<LaunchSession>.Fail(started.Failure?.Code ?? "SoberLaunchFailed", started.Failure?.Message ?? "Sober could not start", CapabilityState.Experimental);
		return OperationResult<LaunchSession>.Success(new LaunchSession(
			RuntimeKind.Player, "Sober", result.Value.ProcessId, DateTimeOffset.UtcNow, installation, false));

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

public sealed partial class LinuxVinegarStudioRuntimeProvider : IRobloxRuntimeProvider
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

	public static bool IsInstalled()
	{
		try
		{
			string home = Environment.GetEnvironmentVariable("HOME")
				?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

			if (LinuxFlatpakHost.IsSandboxed)
				return Directory.Exists(Path.Combine(home, ".var", "app", VinegarApplicationId)) || HasNativeVinegar();

			string[] roots =
			[
				Path.Combine(home, ".local", "share", "flatpak", "app", VinegarApplicationId),
				Path.Combine("/var", "lib", "flatpak", "app", VinegarApplicationId)
			];

			foreach (string root in roots)
			{
				if (Directory.Exists(root))
					return true;
			}

			return HasNativeVinegar();
		}
		catch (Exception)
		{
			return false;
		}
	}

	private static bool HasNativeVinegar()
	{
		if (LinuxFlatpakHost.IsSandboxed)
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
			return LinuxFlatpakHost.FindExecutableAsync(new SystemProcessService(), "vinegar", timeout.Token).GetAwaiter().GetResult() is not null;
		}
		string? search = Environment.GetEnvironmentVariable("PATH");
		if (string.IsNullOrEmpty(search))
			return false;

		foreach (string directory in search.Split(Path.PathSeparator))
		{
			if (directory.Length == 0)
				continue;

			try
			{
				if (File.Exists(Path.Combine(directory, "vinegar")))
					return true;
			}
			catch (Exception)
			{
			}
		}

		return false;
	}

	public async Task<RuntimeInstallation> FindInstallationAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (!_prerequisiteCapability.IsAvailable)
		{
			return UnsupportedInstallation(_prerequisiteCapability);
		}

		string? vinegar = await LinuxFlatpakHost.FindExecutableAsync(_processes, "vinegar", cancellationToken).ConfigureAwait(false);
		if (vinegar is not null)
		{
			return CreateInstallation(
				"Vinegar Native",
				null,
				vinegar,
				GetNativeDataDirectory());
		}

		if (!LinuxFlatpakHost.TryCreateCommand(_processes, ["info", VinegarApplicationId], out ProcessCommand infoCommand))
		{
			return MissingInstallation("Vinegar is not installed");
		}

		OperationResult<ProcessExecution> flatpakResult = await _processes.ExecuteAsync(
			infoCommand,
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
			FlatpakApplicationInfo.ParseVersion(flatpakResult.Value.StandardOutput) ?? string.Empty,
			infoCommand.FileName,
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

		bool native = string.Equals(installation.Provider, "Vinegar Native", StringComparison.Ordinal);
		bool bareLaunch = string.Equals(deeplink.AbsoluteUri.TrimEnd('/'), "roblox-studio://launch", StringComparison.OrdinalIgnoreCase);
		IReadOnlyList<string> arguments = bareLaunch
			? (native ? [] : ["run", VinegarApplicationId])
			: (native ? [deeplink.AbsoluteUri] : ["run", VinegarApplicationId, deeplink.AbsoluteUri]);
		ProcessCommand launchCommand;
		if (native)
		{
			if (!LinuxFlatpakHost.TryCreateHostCommand(_processes, installation.Location, arguments, out launchCommand, false))
				return OperationResult<LaunchSession>.Fail("VinegarLaunchFailed", "The native Vinegar launcher is unavailable", CapabilityState.RequiresExternalRuntime);
		}
		else if (!LinuxFlatpakHost.TryCreateCommand(_processes, arguments, out launchCommand, false))
		{
			return OperationResult<LaunchSession>.Fail("FlatpakMissing", "Flatpak is not installed", CapabilityState.RequiresExternalRuntime);
		}
		OperationResult<ProcessStartResult> result = await _processes.StartAsync(
			launchCommand,
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

	public static string ToWinePath(string path)
	{
		return "Z:" + Path.GetFullPath(path).Replace('/', '\\');
	}

	public static bool TryOpenFile(string path, out string error)
	{
		error = string.Empty;
		try
		{
			if (!File.Exists(path))
			{
				error = "The Studio file no longer exists";
				return false;
			}

			string full = Path.GetFullPath(path);
			string winePath = ToWinePath(full);
			SystemProcessService processes = new();
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
			string? native = LinuxFlatpakHost.FindExecutableAsync(processes, "vinegar", timeout.Token).GetAwaiter().GetResult();
			System.Diagnostics.Process? process;
			if (native is not null && LinuxFlatpakHost.TryCreateHostCommand(processes, native, [winePath], out ProcessCommand nativeCommand, false))
			{
				System.Diagnostics.ProcessStartInfo startInfo = new(nativeCommand.FileName)
				{
					UseShellExecute = false,
					CreateNoWindow = true
				};
				foreach (string argument in nativeCommand.Arguments)
					startInfo.ArgumentList.Add(argument);
				process = System.Diagnostics.Process.Start(startInfo);
			}
			else
			{
				string folder = Path.GetDirectoryName(full) ?? full;
				process = LinuxFlatpakHost.Start(["run", "--filesystem=" + folder, VinegarApplicationId, winePath]);
			}

			if (process is null)
			{
				error = "Vinegar is not installed";
				return false;
			}

			process.Dispose();
			return true;
		}
		catch (Exception ex)
		{
			error = ex.Message;
			return false;
		}
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
		return Path.Combine(LinuxFlatpakHost.GetHostXdgDirectory("XDG_DATA_HOME", ".local", "share"), "vinegar");
	}

	private static string GetFlatpakDataDirectory()
	{
		return Path.Combine(LinuxPaths.GetHomeDirectory(), ".var", "app", VinegarApplicationId, "data", "vinegar");
	}
}

public sealed partial class LinuxStudioRuntimeProvider : IRobloxRuntimeProvider
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

public sealed partial class LinuxProtocolRegistration : IProtocolRegistration
{

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
		if (LinuxFlatpakHost.IsSandboxed)
			return new CapabilityDescriptor(FeatureId.ProtocolRegistration, CapabilityState.Available, "The Flatpak desktop entry provides protocol registration");

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
		if (LinuxFlatpakHost.IsSandboxed)
			return OperationResult.Success();
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

	[GeneratedRegex("^[a-z][a-z0-9+.-]*$", RegexOptions.CultureInvariant)]
	private static partial Regex SchemeExpression { get; }
}

internal static partial class FlatpakApplicationInfo
{
	public static string? ParseVersion(string? output)
	{
		if (string.IsNullOrWhiteSpace(output))
			return null;

		foreach (string line in output.Split('\n'))
		{
			string trimmed = line.Trim();
			if (!trimmed.StartsWith("Version:", StringComparison.Ordinal))
				continue;

			string value = trimmed.Substring("Version:".Length).Trim();
			return string.IsNullOrWhiteSpace(value) ? null : value;
		}

		return null;
	}
}
