using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core;

namespace Voidstrap.Platform.MacOS;

public sealed class MacOSPlatformHost : IPlatformHost
{
	public MacOSPlatformHost()
	{
		Processes = new SystemProcessService();
		Paths = new MacOSPaths();
		SecureStore = new MacOSSecureStore();
		PlayerRuntime = new MacOSRobloxRuntimeProvider(RuntimeKind.Player, Processes);
		StudioRuntime = new MacOSRobloxRuntimeProvider(RuntimeKind.Studio, Processes);
		ProtocolRegistration = new MacOSProtocolRegistration(Processes);
		Updater = new UnavailablePlatformUpdater(CreateUpdaterCapability());
		Notifications = new ProcessNotificationService(
			Processes,
			CreateNotificationsCapability(),
			File.Exists("/usr/bin/osascript") ? "/usr/bin/osascript" : null,
			BuildNotificationArguments);
		Overlay = new CapabilityOnlyPlatformFeatureService(CreateOverlayCapability());
		Input = new CapabilityOnlyPlatformFeatureService(CreateInputCapability());
		AudioSession = new CapabilityOnlyPlatformFeatureService(CreateAudioSessionCapability());
		ResourceOptimization = new UnixResourceOptimizationService(
			Processes,
			new CapabilityDescriptor(FeatureId.ResourceOptimization, CapabilityState.Unavailable, "Resource optimization requires direct access to the Roblox process"),
			false);
		Capabilities = new CapabilitySet(PlatformId.MacOS, CreateCapabilities(Updater.Capability, Notifications.Capability, Overlay.Capability, Input.Capability, AudioSession.Capability));
	}

	public PlatformId Id => PlatformId.MacOS;

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
		CapabilityDescriptor audioSession)
	{
		yield return Available(FeatureId.DesktopShell, "Native macOS desktop support is available");
		yield return new CapabilityDescriptor(FeatureId.EmbeddedBrowser, CapabilityState.Experimental, "Basic WKWebView browsing and bridge support are available. Full document start injection is still being migrated", null, true);
		yield return new CapabilityDescriptor(FeatureId.RobloxPlayer, CapabilityState.RequiresExternalRuntime, "Requires the official Roblox application", "Install Roblox for macOS");
		yield return new CapabilityDescriptor(FeatureId.RobloxStudio, CapabilityState.RequiresExternalRuntime, "Requires the official Roblox Studio application", "Install Roblox Studio for macOS");
		yield return Available(FeatureId.SecureStorage, "Keychain support is available");
		yield return Available(FeatureId.ProtocolRegistration, "Application bundle protocol registration is available");
		yield return updater;
		yield return notifications;
		yield return new CapabilityDescriptor(FeatureId.Tray, CapabilityState.Unavailable, "The macOS menu bar adapter has not been ported to the shared desktop host");
		yield return overlay;
		yield return input;
		yield return audioSession;
		yield return new CapabilityDescriptor(FeatureId.ResourceOptimization, CapabilityState.Unavailable, "Resource optimization requires direct access to the Roblox process");
		yield return new CapabilityDescriptor(FeatureId.AssetInjection, CapabilityState.Unavailable, "The official Roblox application bundle cannot be modified");
		yield return new CapabilityDescriptor(FeatureId.FrameGeneration, CapabilityState.Unavailable, "Windows frame generation is not available on macOS");
		yield return new CapabilityDescriptor(FeatureId.VirtualController, CapabilityState.Unavailable, "Windows virtual controller support is not available on macOS");
		yield return new CapabilityDescriptor(FeatureId.ExtensionNativeAssets, CapabilityState.RequiresExternalRuntime, "Extensions require macOS native assets");
	}

	private static CapabilityDescriptor Available(FeatureId feature, string reason)
	{
		return new CapabilityDescriptor(feature, CapabilityState.Available, reason);
	}

	private static CapabilityDescriptor CreateNotificationsCapability()
	{
		return File.Exists("/usr/bin/osascript")
			? new CapabilityDescriptor(FeatureId.Notifications, CapabilityState.Available, "macOS Notification Center is available")
			: new CapabilityDescriptor(FeatureId.Notifications, CapabilityState.Unavailable, "The macOS notification command is unavailable");
	}

	private static CapabilityDescriptor CreateUpdaterCapability()
	{
		return new CapabilityDescriptor(FeatureId.Updater, CapabilityState.Unavailable, "The macOS updater has not been ported to the shared desktop host");
	}

	private static CapabilityDescriptor CreateOverlayCapability()
	{
		return new CapabilityDescriptor(FeatureId.Overlay, CapabilityState.Unavailable, "The ScreenCaptureKit overlay adapter has not been ported to the shared desktop host");
	}

	private static CapabilityDescriptor CreateInputCapability()
	{
		return new CapabilityDescriptor(FeatureId.GlobalInput, CapabilityState.Unavailable, "The Accessibility input adapter has not been ported to the shared desktop host");
	}

	private static CapabilityDescriptor CreateAudioSessionCapability()
	{
		return new CapabilityDescriptor(FeatureId.AudioSession, CapabilityState.Unavailable, "The CoreAudio adapter has not been ported to the shared desktop host");
	}

	private static IReadOnlyList<string> BuildNotificationArguments(NotificationRequest request)
	{
		return ["-e", "display notification " + QuoteAppleScript(request.Message) + " with title " + QuoteAppleScript(request.Title)];
	}

	private static string QuoteAppleScript(string value)
	{
		return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n") + "\"";
	}
}

public sealed class MacOSPaths : PlatformPathsBase
{
	public MacOSPaths()
		: base(CreateStorage())
	{
	}

	private static PlatformStoragePaths CreateStorage()
	{
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string applicationSupport = Path.Combine(home, "Library", "Application Support", "Voidstrap");
		string cache = Path.Combine(home, "Library", "Caches", "Voidstrap");
		string logs = Path.Combine(home, "Library", "Logs", "Voidstrap");
		string downloads = Path.Combine(home, "Downloads");

		return new PlatformStoragePaths(
			applicationSupport,
			Path.Combine(applicationSupport, "Config"),
			Path.Combine(applicationSupport, "Data"),
			cache,
			logs,
			downloads,
			Path.Combine(applicationSupport, "Extensions"),
			Path.Combine(cache, "Temporary"));
	}
}

public sealed class MacOSSecureStore : ISecureStore
{
	private const int Success = 0;
	private const int ItemNotFound = -25300;
	private const int MaximumIdentifierLength = 256;
	private const int MaximumSecureValueBytes = 4194304;
	private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
	private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

	public Task<OperationResult> SetAsync(string service, string key, string value, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "The Keychain operation was canceled"));
		}
		if (!HasValidIdentifiers(service, key))
			return Task.FromResult(OperationResult.Fail("KeychainKeyInvalid", "The Keychain service or key is invalid"));
		if (Encoding.UTF8.GetByteCount(value) > MaximumSecureValueBytes)
			return Task.FromResult(OperationResult.Fail("KeychainValueTooLarge", "The Keychain value is too large"));
		byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
		byte[] keyBytes = Encoding.UTF8.GetBytes(key);
		byte[] valueBytes = Encoding.UTF8.GetBytes(value);
		IntPtr passwordData = IntPtr.Zero;
		IntPtr item = IntPtr.Zero;
		try
		{
			int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)keyBytes.Length, keyBytes, out _, out passwordData, out item);
			if (passwordData != IntPtr.Zero)
			{
				SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
				passwordData = IntPtr.Zero;
			}
			if (status == Success)
			{
				status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)valueBytes.Length, valueBytes);
			}
			else if (status == ItemNotFound)
			{
				status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)keyBytes.Length, keyBytes, (uint)valueBytes.Length, valueBytes, out item);
			}
			return Task.FromResult(status == Success
				? OperationResult.Success()
				: OperationResult.Fail("KeychainWriteFailed", "The Keychain write failed"));
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return Task.FromResult(OperationResult.Fail("KeychainUnavailable", "The macOS Keychain is unavailable"));
		}
		finally
		{
			if (passwordData != IntPtr.Zero)
			{
				SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
			}
			if (item != IntPtr.Zero)
			{
				CFRelease(item);
			}
			CryptographicOperations.ZeroMemory(valueBytes);
		}
	}

	public Task<OperationResult<SecureValueResult>> GetAsync(string service, string key, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return Task.FromResult(OperationResult<SecureValueResult>.Fail("OperationCanceled", "The Keychain operation was canceled"));
		}
		if (!HasValidIdentifiers(service, key))
			return Task.FromResult(OperationResult<SecureValueResult>.Fail("KeychainKeyInvalid", "The Keychain service or key is invalid"));
		byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
		byte[] keyBytes = Encoding.UTF8.GetBytes(key);
		IntPtr passwordData = IntPtr.Zero;
		IntPtr item = IntPtr.Zero;
		try
		{
			int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)keyBytes.Length, keyBytes, out uint passwordLength, out passwordData, out item);
			if (status == ItemNotFound)
			{
				return Task.FromResult(OperationResult<SecureValueResult>.Success(new SecureValueResult(false, null)));
			}
			if (status != Success || passwordData == IntPtr.Zero || passwordLength > MaximumSecureValueBytes)
			{
				return Task.FromResult(OperationResult<SecureValueResult>.Fail("KeychainReadFailed", "The Keychain read failed"));
			}
			byte[] valueBytes = new byte[(int)passwordLength];
			try
			{
				Marshal.Copy(passwordData, valueBytes, 0, valueBytes.Length);
				string value = Encoding.UTF8.GetString(valueBytes);
				return Task.FromResult(OperationResult<SecureValueResult>.Success(new SecureValueResult(true, value)));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(valueBytes);
			}
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return Task.FromResult(OperationResult<SecureValueResult>.Fail("KeychainUnavailable", "The macOS Keychain is unavailable"));
		}
		finally
		{
			if (passwordData != IntPtr.Zero)
			{
				SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
			}
			if (item != IntPtr.Zero)
			{
				CFRelease(item);
			}
		}
	}

	public Task<OperationResult> DeleteAsync(string service, string key, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "The Keychain operation was canceled"));
		}
		if (!HasValidIdentifiers(service, key))
			return Task.FromResult(OperationResult.Fail("KeychainKeyInvalid", "The Keychain service or key is invalid"));
		byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
		byte[] keyBytes = Encoding.UTF8.GetBytes(key);
		IntPtr passwordData = IntPtr.Zero;
		IntPtr item = IntPtr.Zero;
		try
		{
			int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)serviceBytes.Length, serviceBytes, (uint)keyBytes.Length, keyBytes, out _, out passwordData, out item);
			if (status == ItemNotFound)
			{
				return Task.FromResult(OperationResult.Success());
			}
			if (status == Success)
			{
				status = SecKeychainItemDelete(item);
			}
			return Task.FromResult(status == Success
				? OperationResult.Success()
				: OperationResult.Fail("KeychainDeleteFailed", "The Keychain delete failed"));
		}
		catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
		{
			return Task.FromResult(OperationResult.Fail("KeychainUnavailable", "The macOS Keychain is unavailable"));
		}
		finally
		{
			if (passwordData != IntPtr.Zero)
			{
				SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
			}
			if (item != IntPtr.Zero)
			{
				CFRelease(item);
			}
		}
	}

	private static bool HasValidIdentifiers(string service, string key)
	{
		return !string.IsNullOrWhiteSpace(service)
			&& service.Length <= MaximumIdentifierLength
			&& !string.IsNullOrWhiteSpace(key)
			&& key.Length <= MaximumIdentifierLength;
	}

	[DllImport(SecurityFramework)]
	private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

	[DllImport(SecurityFramework)]
	private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

	[DllImport(SecurityFramework)]
	private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attributes, uint length, byte[] data);

	[DllImport(SecurityFramework)]
	private static extern int SecKeychainItemDelete(IntPtr itemRef);

	[DllImport(SecurityFramework)]
	private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

	[DllImport(CoreFoundationFramework)]
	private static extern void CFRelease(IntPtr value);
}

public sealed class MacOSRobloxRuntimeProvider : IRobloxRuntimeProvider
{
	private readonly IProcessService _processes;

	public MacOSRobloxRuntimeProvider(RuntimeKind kind, IProcessService processes)
	{
		Kind = kind;
		_processes = processes;
	}

	public RuntimeKind Kind { get; }

	public Task<RuntimeInstallation> FindInstallationAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string applicationPath = FindApplicationPath();
		bool available = Directory.Exists(applicationPath);
		CapabilityDescriptor capability = available
			? new CapabilityDescriptor(GetFeature(), CapabilityState.Available, "The official Roblox application is available")
			: new CapabilityDescriptor(GetFeature(), CapabilityState.RequiresExternalRuntime, "The official Roblox application is not installed", GetInstallAction());

		return Task.FromResult(new RuntimeInstallation(
			Kind,
			"Roblox",
			null,
			available ? applicationPath : null,
			GetRobloxDataDirectory(),
			capability));
	}

	public async Task<OperationResult<LaunchSession>> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
	{
		if (request.Kind != Kind)
		{
			return OperationResult<LaunchSession>.Fail("RuntimeKindMismatch", "The requested runtime does not match this provider");
		}

		RuntimeInstallation installation = await FindInstallationAsync(cancellationToken);
		if (!installation.Capability.IsAvailable || string.IsNullOrWhiteSpace(installation.Location))
		{
			return OperationResult<LaunchSession>.Fail(
				"RobloxNotInstalled",
				installation.Capability.Reason,
				installation.Capability.State);
		}

		ProcessCommand command = new ProcessCommand(
			"/usr/bin/open",
			["-a", installation.Location, request.Deeplink.AbsoluteUri],
			CaptureOutput: false);
		OperationResult<ProcessStartResult> result = await _processes.StartAsync(command, cancellationToken);

		if (!result.Succeeded || result.Value is null)
		{
			return result.Failure is null
				? OperationResult<LaunchSession>.Fail("RobloxLaunchFailed", "The Roblox application did not start")
				: OperationResult<LaunchSession>.Fail(result.Failure.Code, result.Failure.Message, result.Failure.State);
		}

		return OperationResult<LaunchSession>.Success(new LaunchSession(
			Kind,
			"Roblox",
			result.Value.ProcessId,
			DateTimeOffset.UtcNow,
			installation,
			false));
	}

	private string FindApplicationPath()
	{
		string applicationName = Kind == RuntimeKind.Player ? "Roblox.app" : "RobloxStudio.app";
		string userApplication = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", applicationName);
		if (Directory.Exists(userApplication))
		{
			return userApplication;
		}

		return Path.Combine("/Applications", applicationName);
	}

	private FeatureId GetFeature()
	{
		return Kind == RuntimeKind.Player ? FeatureId.RobloxPlayer : FeatureId.RobloxStudio;
	}

	private string GetInstallAction()
	{
		return Kind == RuntimeKind.Player ? "Install Roblox for macOS" : "Install Roblox Studio for macOS";
	}

	private static string GetRobloxDataDirectory()
	{
		return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Roblox");
	}
}

public sealed class MacOSProtocolRegistration : IProtocolRegistration
{
	private static readonly string[] LaunchServicesRegistrationPaths =
	[
		"/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister",
		"/System/Library/Frameworks/ApplicationServices.framework/Frameworks/LaunchServices.framework/Support/lsregister"
	];

	private readonly IProcessService _processes;

	public MacOSProtocolRegistration(IProcessService processes)
	{
		_processes = processes;
	}

	public Task<CapabilityDescriptor> GetCapabilityAsync(CancellationToken cancellationToken = default)
	{
		string? bundle = FindApplicationBundle();
		CapabilityDescriptor descriptor = bundle is null
			? new CapabilityDescriptor(FeatureId.ProtocolRegistration, CapabilityState.Unavailable, "Protocol registration requires a packaged macOS application")
			: new CapabilityDescriptor(FeatureId.ProtocolRegistration, CapabilityState.Available, "Application bundle protocol registration is available");

		return Task.FromResult(descriptor);
	}

	public async Task<OperationResult> RegisterAsync(ProtocolRegistrationRequest request, CancellationToken cancellationToken = default)
	{
		string? bundle = FindApplicationBundle();
		if (bundle is null)
		{
			return OperationResult.Fail("ApplicationBundleMissing", "Protocol registration requires a packaged macOS application");
		}

		string? registrationTool = FindRegistrationTool();
		if (registrationTool is null)
		{
			return OperationResult.Fail("LaunchServicesUnavailable", "Launch Services registration is unavailable");
		}

		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(
			new ProcessCommand(registrationTool, ["-f", bundle]),
			cancellationToken);

		if (!result.Succeeded || result.Value is null)
		{
			return result.Failure is null
				? OperationResult.Fail("ProtocolRegistrationFailed", "Launch Services registration failed")
				: OperationResult.Fail(result.Failure.Code, result.Failure.Message, result.Failure.State);
		}

		return result.Value.ExitCode == 0
			? OperationResult.Success()
			: OperationResult.Fail("ProtocolRegistrationFailed", result.Value.StandardError);
	}

	private static string? FindApplicationBundle()
	{
		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath))
		{
			return null;
		}

		DirectoryInfo? directory = new FileInfo(processPath).Directory;
		while (directory is not null)
		{
			if (directory.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		return null;
	}

	private static string? FindRegistrationTool()
	{
		foreach (string path in LaunchServicesRegistrationPaths)
		{
			if (File.Exists(path))
			{
				return path;
			}
		}

		return null;
	}
}
