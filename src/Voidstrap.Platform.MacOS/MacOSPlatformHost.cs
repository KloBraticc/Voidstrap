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
		ProtocolRegistration = new MacOSProtocolRegistration();
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
		yield return Available(FeatureId.Tray, "Voidstrap shows its menu in the macOS menu bar while Roblox runs");
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

public sealed partial class MacOSSecureStore : ISecureStore
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
				_ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
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
				_ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
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
				_ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
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
				_ = SecKeychainItemFreeContent(IntPtr.Zero, passwordData);
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

	[LibraryImport(SecurityFramework)]
	private static partial int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, [In] byte[] serviceName, uint accountNameLength, [In] byte[] accountName, uint passwordLength, [In] byte[] passwordData, out IntPtr itemRef);

	[LibraryImport(SecurityFramework)]
	private static partial int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, [In] byte[] serviceName, uint accountNameLength, [In] byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

	[LibraryImport(SecurityFramework)]
	private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attributes, uint length, [In] byte[] data);

	[LibraryImport(SecurityFramework)]
	private static partial int SecKeychainItemDelete(IntPtr itemRef);

	[LibraryImport(SecurityFramework)]
	private static partial int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

	[LibraryImport(CoreFoundationFramework)]
	private static partial void CFRelease(IntPtr value);
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
		string applicationPath = FindApplicationPath(Kind);
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

	public static bool IsInstalled(RuntimeKind kind) => Directory.Exists(FindApplicationPath(kind));

	private static string FindApplicationPath(RuntimeKind kind)
	{
		string applicationName = kind == RuntimeKind.Player ? "Roblox.app" : "RobloxStudio.app";
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

public sealed partial class MacOSProtocolRegistration : IProtocolRegistration
{
	private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
	private const string CoreServices = "/System/Library/Frameworks/CoreServices.framework/CoreServices";
	private static readonly string[] Schemes = ["roblox", "roblox-player", "roblox-studio", "roblox-studio-auth"];

	public Task<CapabilityDescriptor> GetCapabilityAsync(CancellationToken cancellationToken = default)
	{
		string? bundle = FindApplicationBundle();
		CapabilityDescriptor descriptor = bundle is null
			? new CapabilityDescriptor(FeatureId.ProtocolRegistration, CapabilityState.Unavailable, "Protocol registration requires a packaged macOS application")
			: new CapabilityDescriptor(FeatureId.ProtocolRegistration, CapabilityState.Available, "Application bundle protocol registration is available");

		return Task.FromResult(descriptor);
	}

	public Task<OperationResult> RegisterAsync(ProtocolRegistrationRequest request, CancellationToken cancellationToken = default)
	{
		if (!Schemes.Contains(request.Scheme, StringComparer.OrdinalIgnoreCase))
			return Task.FromResult(OperationResult.Fail("UnsupportedProtocol", "The application does not handle this URL scheme"));
		return Task.FromResult(RegisterSchemes([request.Scheme.ToLowerInvariant()], cancellationToken));
	}

	public Task<OperationResult> RegisterAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(RegisterSchemes(Schemes, cancellationToken));

	public static OperationResult SetPlayerHandler(bool openRobloxDirectly)
	{
		return RegisterSchemes(["roblox", "roblox-player"], CancellationToken.None, openRobloxDirectly ? RobloxPlayerBundleIdentifier : null);
	}

	private const string RobloxPlayerBundleIdentifier = "com.roblox.RobloxPlayer";

	private static OperationResult RegisterSchemes(IReadOnlyList<string> schemes, CancellationToken cancellationToken, string? handlerOverride = null)
	{
		cancellationToken.ThrowIfCancellationRequested();
		string? bundle = FindApplicationBundle();
		if (bundle is null)
		{
			return OperationResult.Fail("ApplicationBundleMissing", "Protocol registration requires a packaged macOS application");
		}

		nint path = CFStringCreateWithCString(0, bundle, 0x08000100);
		nint url = 0;
		nint application = 0;
		try
		{
			if (path != 0)
				url = CFURLCreateWithFileSystemPath(0, path, 0, true);
			if (url != 0)
				application = CFBundleCreate(0, url);
			nint identifier = application != 0 ? CFBundleGetIdentifier(application) : 0;
			if (identifier == 0)
				return OperationResult.Fail("ApplicationBundleInvalid", "The application bundle identifier could not be read");
			int registrationStatus = LSRegisterURL(url, true);
			if (registrationStatus != 0)
				return OperationResult.Fail("ProtocolRegistrationFailed", "The application bundle could not be registered: " + registrationStatus);
			nint overrideIdentifier = handlerOverride is null ? 0 : CFStringCreateWithCString(0, handlerOverride, 0x08000100);
			if (handlerOverride is not null && overrideIdentifier == 0)
				return OperationResult.Fail("ProtocolRegistrationFailed", "The URL handler could not be prepared");
			if (overrideIdentifier != 0)
				identifier = overrideIdentifier;
			try
			{
				foreach (string scheme in schemes)
				{
					cancellationToken.ThrowIfCancellationRequested();
					nint name = CFStringCreateWithCString(0, scheme, 0x08000100);
					if (name == 0)
						return OperationResult.Fail("ProtocolRegistrationFailed", "The URL scheme could not be prepared");
					try
					{
						int status = LSSetDefaultHandlerForURLScheme(name, identifier);
						if (status != 0)
							return OperationResult.Fail("ProtocolRegistrationFailed", "The default URL handler could not be set: " + status);
					}
					finally
					{
						CFRelease(name);
					}
				}
				return OperationResult.Success();
			}
			finally
			{
				if (overrideIdentifier != 0)
					CFRelease(overrideIdentifier);
			}
		}
		finally
		{
			if (application != 0)
				CFRelease(application);
			if (url != 0)
				CFRelease(url);
			if (path != 0)
				CFRelease(path);
		}
	}

	[LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
	private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFURLCreateWithFileSystemPath(nint allocator, nint path, nint style, [MarshalAs(UnmanagedType.I1)] bool isDirectory);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFBundleCreate(nint allocator, nint url);

	[LibraryImport(CoreFoundation)]
	private static partial nint CFBundleGetIdentifier(nint bundle);

	[LibraryImport(CoreFoundation)]
	private static partial void CFRelease(nint value);

	[LibraryImport(CoreServices)]
	private static partial int LSRegisterURL(nint url, [MarshalAs(UnmanagedType.I1)] bool update);

	[LibraryImport(CoreServices)]
	private static partial int LSSetDefaultHandlerForURLScheme(nint scheme, nint bundleIdentifier);

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
}
