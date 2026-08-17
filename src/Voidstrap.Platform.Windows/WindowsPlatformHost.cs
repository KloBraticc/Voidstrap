using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Voidstrap.Core;

namespace Voidstrap.Platform.Windows;

public sealed class WindowsPlatformHost : IPlatformHost
{
	public WindowsPlatformHost()
	{
		Processes = new SystemProcessService();
		Paths = new WindowsPaths();
		SecureStore = new WindowsSecureStore((WindowsPaths)Paths);
		PlayerRuntime = new WindowsRobloxRuntimeProvider(RuntimeKind.Player, Processes, (WindowsPaths)Paths);
		StudioRuntime = new WindowsRobloxRuntimeProvider(RuntimeKind.Studio, Processes, (WindowsPaths)Paths);
		ProtocolRegistration = new WindowsProtocolRegistration();
		Updater = new UnavailablePlatformUpdater(CreateUpdaterCapability());
		Notifications = new UnavailableNotificationService(CreateNotificationsCapability());
		Overlay = new CapabilityOnlyPlatformFeatureService(CreateOverlayCapability());
		Input = new CapabilityOnlyPlatformFeatureService(CreateInputCapability());
		AudioSession = new CapabilityOnlyPlatformFeatureService(CreateAudioSessionCapability());
		ResourceOptimization = new WindowsResourceOptimizationService();
		Capabilities = new CapabilitySet(PlatformId.Windows, CreateCapabilities(Updater.Capability, Notifications.Capability, Overlay.Capability, Input.Capability, AudioSession.Capability));
	}

	public PlatformId Id => PlatformId.Windows;

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
		yield return Available(FeatureId.DesktopShell, "Windows desktop support is available");
		yield return new CapabilityDescriptor(FeatureId.EmbeddedBrowser, CapabilityState.Experimental, "Basic WebView2 browsing and bridge support are available. Full document start injection remains in the WPF baseline", null, true);
		yield return new CapabilityDescriptor(FeatureId.RobloxPlayer, CapabilityState.RequiresExternalRuntime, "Requires the official Roblox player", "Install Roblox");
		yield return new CapabilityDescriptor(FeatureId.RobloxStudio, CapabilityState.RequiresExternalRuntime, "Requires the official Roblox Studio application", "Install Roblox Studio");
		yield return Available(FeatureId.SecureStorage, "DPAPI secure storage is available");
		yield return Available(FeatureId.ProtocolRegistration, "Windows protocol registration is available");
		yield return updater;
		yield return notifications;
		yield return new CapabilityDescriptor(FeatureId.Tray, CapabilityState.Unavailable, "This Windows feature remains in the WPF baseline during migration");
		yield return overlay;
		yield return input;
		yield return audioSession;
		yield return Available(FeatureId.ResourceOptimization, "The shared desktop host can apply launch scheduling and CPU affinity to direct Roblox processes");
		yield return new CapabilityDescriptor(FeatureId.AssetInjection, CapabilityState.Unavailable, "This Windows feature remains in the WPF baseline during migration");
		yield return new CapabilityDescriptor(FeatureId.FrameGeneration, CapabilityState.Unavailable, "This Windows feature remains in the WPF baseline during migration");
		yield return new CapabilityDescriptor(FeatureId.VirtualController, CapabilityState.Unavailable, "This Windows feature remains in the WPF baseline during migration");
		yield return Available(FeatureId.ExtensionNativeAssets, "Windows extension assets can be detected");
	}

	private static CapabilityDescriptor Available(FeatureId feature, string reason)
	{
		return new CapabilityDescriptor(feature, CapabilityState.Available, reason);
	}

	private static CapabilityDescriptor CreateNotificationsCapability()
	{
		return new CapabilityDescriptor(FeatureId.Notifications, CapabilityState.Unavailable, "This Windows feature remains in the WPF baseline during migration");
	}

	private static CapabilityDescriptor CreateUpdaterCapability()
	{
		return new CapabilityDescriptor(FeatureId.Updater, CapabilityState.Unavailable, "The shared Windows updater remains in the WPF baseline during migration");
	}

	private static CapabilityDescriptor CreateOverlayCapability()
	{
		return new CapabilityDescriptor(FeatureId.Overlay, CapabilityState.Unavailable, "This Windows feature remains in the WPF baseline during migration");
	}

	private static CapabilityDescriptor CreateInputCapability()
	{
		return new CapabilityDescriptor(FeatureId.GlobalInput, CapabilityState.Unavailable, "This Windows feature remains in the WPF baseline during migration");
	}

	private static CapabilityDescriptor CreateAudioSessionCapability()
	{
		return new CapabilityDescriptor(FeatureId.AudioSession, CapabilityState.Unavailable, "This Windows feature remains in the WPF baseline during migration");
	}
}

public sealed class WindowsResourceOptimizationService : IResourceOptimizationService
{
	private static readonly int ProcessorCount = Environment.ProcessorCount;

	public CapabilityDescriptor Capability { get; } = new(
		FeatureId.ResourceOptimization,
		CapabilityState.Available,
		"The shared desktop host can apply launch scheduling and CPU affinity to direct Roblox processes");

	public Task<OperationResult<ResourceOptimizationResult>> ApplyAsync(ResourceOptimizationRequest request, CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			OperationResult validation = Validate(request);
			if (!validation.Succeeded)
			{
				return Task.FromResult(CopyFailure<ResourceOptimizationResult>(validation.Failure));
			}

			using Process process = Process.GetProcessById(request.ProcessId);
			if (process.HasExited)
			{
				return Task.FromResult(OperationResult<ResourceOptimizationResult>.Fail("ProcessExited", "The Roblox process has already exited"));
			}

			bool priorityApplied = false;
			if (request.Priority != ResourcePriority.Automatic)
			{
				ProcessPriorityClass priority = MapPriority(request.Priority);
				if (process.PriorityClass != priority)
				{
					process.PriorityClass = priority;
				}

				priorityApplied = true;
			}

			bool cpuApplied = false;
			if (request.CpuLimit is int cpuLimit && cpuLimit < ProcessorCount)
			{
				ulong mask = (1UL << cpuLimit) - 1UL;
				process.ProcessorAffinity = (IntPtr)unchecked((long)mask);
				cpuApplied = true;
			}

			string summary = priorityApplied || cpuApplied
				? "The launch resource profile was applied."
				: "No launch resource profile is selected.";
			return Task.FromResult(OperationResult<ResourceOptimizationResult>.Success(new ResourceOptimizationResult(request.ProcessId, priorityApplied, cpuApplied, summary)));
		}
		catch (OperationCanceledException)
		{
			return Task.FromResult(OperationResult<ResourceOptimizationResult>.Fail("OperationCanceled", "The process resource change was canceled"));
		}
		catch (ArgumentException)
		{
			return Task.FromResult(OperationResult<ResourceOptimizationResult>.Fail("ProcessMissing", "The Roblox process could not be found"));
		}
		catch (UnauthorizedAccessException exception)
		{
			return Task.FromResult(OperationResult<ResourceOptimizationResult>.Fail("ProcessAccessDenied", exception.Message, CapabilityState.RequiresPermission));
		}
		catch (System.ComponentModel.Win32Exception exception)
		{
			return Task.FromResult(OperationResult<ResourceOptimizationResult>.Fail("ProcessResourceChangeFailed", exception.Message, CapabilityState.RequiresPermission));
		}
		catch (InvalidOperationException exception)
		{
			return Task.FromResult(OperationResult<ResourceOptimizationResult>.Fail("ProcessUnavailable", exception.Message));
		}
	}

	private static OperationResult Validate(ResourceOptimizationRequest request)
	{
		if (request.ProcessId < 1)
		{
			return OperationResult.Fail("ProcessIdInvalid", "A valid Roblox process is required");
		}

		if (request.CpuLimit is not int cpuLimit)
		{
			return OperationResult.Success();
		}

		if (cpuLimit < 1 || cpuLimit > ProcessorCount)
		{
			return OperationResult.Fail("CpuLimitInvalid", "The CPU limit is outside the available processor range");
		}

		if (ProcessorCount > IntPtr.Size * 8 && cpuLimit < ProcessorCount)
		{
			return OperationResult.Fail("CpuLimitUnsupported", "CPU limiting is unavailable on systems that use processor groups");
		}

		return OperationResult.Success();
	}

	private static ProcessPriorityClass MapPriority(ResourcePriority priority)
	{
		return priority switch
		{
			ResourcePriority.Idle => ProcessPriorityClass.Idle,
			ResourcePriority.BelowNormal => ProcessPriorityClass.BelowNormal,
			ResourcePriority.AboveNormal => ProcessPriorityClass.AboveNormal,
			ResourcePriority.High => ProcessPriorityClass.High,
			_ => ProcessPriorityClass.Normal
		};
	}

	private static OperationResult<T> CopyFailure<T>(OperationFailure? failure)
	{
		return failure is null
			? OperationResult<T>.Fail("ProcessResourceChangeFailed", "The process resource change failed")
			: OperationResult<T>.Fail(failure.Code, failure.Message, failure.State);
	}
}

public sealed class WindowsPaths : PlatformPathsBase
{
	public WindowsPaths()
		: base(CreateStorage())
	{
	}

	public string SecureStorageDirectory => Path.Combine(Storage.Data, "Secure");

	private static PlatformStoragePaths CreateStorage()
	{
		string baseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Voidstrap");
		string temp = Path.Combine(Path.GetTempPath(), "Voidstrap");

		return new PlatformStoragePaths(
			baseDirectory,
			Path.Combine(baseDirectory, "Config"),
			Path.Combine(baseDirectory, "Data"),
			Path.Combine(baseDirectory, "Cache"),
			Path.Combine(baseDirectory, "Logs"),
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
			Path.Combine(baseDirectory, "Extensions"),
			temp);
	}
}

public sealed class WindowsSecureStore : ISecureStore
{
	private const int CryptProtectUiForbidden = 1;
	private const int MaximumIdentifierLength = 256;
	private const int MaximumSecureValueBytes = 4194304;

	private readonly WindowsPaths _paths;

	public WindowsSecureStore(WindowsPaths paths)
	{
		_paths = paths;
	}

	public async Task<OperationResult> SetAsync(string service, string key, string value, CancellationToken cancellationToken = default)
	{
		string? temporaryPath = null;
		byte[]? valueBytes = null;
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			valueBytes = Encoding.UTF8.GetBytes(value);
			if (valueBytes.Length > MaximumSecureValueBytes)
				return OperationResult.Fail("SecureStorageValueTooLarge", "The secure value is too large");
			byte[] protectedValue = Protect(valueBytes);
			if (protectedValue.Length > MaximumSecureValueBytes)
				return OperationResult.Fail("SecureStorageValueTooLarge", "The protected secure value is too large");
			Directory.CreateDirectory(_paths.SecureStorageDirectory);
			string targetPath = GetStoragePath(service, key);
			temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
			await File.WriteAllBytesAsync(temporaryPath, protectedValue, cancellationToken);
			File.Move(temporaryPath, targetPath, true);
			return OperationResult.Success();
		}
		catch (OperationCanceledException)
		{
			return OperationResult.Fail("OperationCanceled", "Secure storage write was canceled");
		}
		catch (CryptographicException exception)
		{
			return OperationResult.Fail("DpapiWriteFailed", exception.Message);
		}
		catch (IOException exception)
		{
			return OperationResult.Fail("SecureStorageWriteFailed", exception.Message);
		}
		catch (UnauthorizedAccessException exception)
		{
			return OperationResult.Fail("SecureStorageAccessDenied", exception.Message, CapabilityState.RequiresPermission);
		}
		catch (ArgumentException exception)
		{
			return OperationResult.Fail("SecureStorageKeyInvalid", exception.Message);
		}
		finally
		{
			if (valueBytes is not null)
				CryptographicOperations.ZeroMemory(valueBytes);
			if (temporaryPath is not null)
			{
				try
				{
					File.Delete(temporaryPath);
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

	public async Task<OperationResult<SecureValueResult>> GetAsync(string service, string key, CancellationToken cancellationToken = default)
	{
		try
		{
			string path = GetStoragePath(service, key);
			FileInfo file = new FileInfo(path);
			if (!file.Exists)
			{
				return OperationResult<SecureValueResult>.Success(new SecureValueResult(false, null));
			}
			if (file.Length <= 0 || file.Length > MaximumSecureValueBytes)
				return OperationResult<SecureValueResult>.Fail("SecureStorageValueInvalid", "The protected secure value has an invalid size");

			byte[] protectedValue = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
			byte[] value = Unprotect(protectedValue);
			try
			{
				if (value.Length > MaximumSecureValueBytes)
					return OperationResult<SecureValueResult>.Fail("SecureStorageValueTooLarge", "The secure value is too large");
				return OperationResult<SecureValueResult>.Success(new SecureValueResult(true, Encoding.UTF8.GetString(value)));
			}
			finally
			{
				CryptographicOperations.ZeroMemory(value);
			}
		}
		catch (OperationCanceledException)
		{
			return OperationResult<SecureValueResult>.Fail("OperationCanceled", "Secure storage read was canceled");
		}
		catch (CryptographicException exception)
		{
			return OperationResult<SecureValueResult>.Fail("DpapiReadFailed", exception.Message);
		}
		catch (IOException exception)
		{
			return OperationResult<SecureValueResult>.Fail("SecureStorageReadFailed", exception.Message);
		}
		catch (UnauthorizedAccessException exception)
		{
			return OperationResult<SecureValueResult>.Fail("SecureStorageAccessDenied", exception.Message, CapabilityState.RequiresPermission);
		}
		catch (ArgumentException exception)
		{
			return OperationResult<SecureValueResult>.Fail("SecureStorageKeyInvalid", exception.Message);
		}
	}

	public Task<OperationResult> DeleteAsync(string service, string key, CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			string path = GetStoragePath(service, key);
			if (File.Exists(path))
			{
				File.Delete(path);
			}

			return Task.FromResult(OperationResult.Success());
		}
		catch (OperationCanceledException)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "Secure storage deletion was canceled"));
		}
		catch (IOException exception)
		{
			return Task.FromResult(OperationResult.Fail("SecureStorageDeleteFailed", exception.Message));
		}
		catch (UnauthorizedAccessException exception)
		{
			return Task.FromResult(OperationResult.Fail("SecureStorageAccessDenied", exception.Message, CapabilityState.RequiresPermission));
		}
		catch (ArgumentException exception)
		{
			return Task.FromResult(OperationResult.Fail("SecureStorageKeyInvalid", exception.Message));
		}
	}

	private string GetStoragePath(string service, string key)
	{
		if (string.IsNullOrWhiteSpace(service) || service.Length > MaximumIdentifierLength)
			throw new ArgumentException("The secure storage service is invalid", nameof(service));
		if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumIdentifierLength)
			throw new ArgumentException("The secure storage key is invalid", nameof(key));
		byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(service + "\u0000" + key));
		return Path.Combine(_paths.SecureStorageDirectory, Convert.ToHexString(bytes) + ".bin");
	}

	private static byte[] Protect(byte[] value)
	{
		DataBlob input = CreateBlob(value);
		DataBlob output = default;

		try
		{
			if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
			{
				throw new CryptographicException(Marshal.GetLastWin32Error());
			}

			return CopyBlob(output);
		}
		finally
		{
			FreeBlob(input, false, true);
			FreeBlob(output, true, false);
		}
	}

	private static byte[] Unprotect(byte[] value)
	{
		DataBlob input = CreateBlob(value);
		DataBlob output = default;

		try
		{
			if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, ref output))
			{
				throw new CryptographicException(Marshal.GetLastWin32Error());
			}

			return CopyBlob(output);
		}
		finally
		{
			FreeBlob(input, false, false);
			FreeBlob(output, true, true);
		}
	}

	private static DataBlob CreateBlob(byte[] value)
	{
		int length = Math.Max(value.Length, 1);
		IntPtr pointer = Marshal.AllocHGlobal(length);
		if (value.Length > 0)
		{
			Marshal.Copy(value, 0, pointer, value.Length);
		}

		return new DataBlob
		{
			cbData = value.Length,
			pbData = pointer
		};
	}

	private static byte[] CopyBlob(DataBlob blob)
	{
		byte[] result = new byte[blob.cbData];
		if (blob.cbData > 0)
		{
			Marshal.Copy(blob.pbData, result, 0, blob.cbData);
		}

		return result;
	}

	private static void FreeBlob(DataBlob blob, bool localAllocated, bool zeroMemory)
	{
		if (blob.pbData == IntPtr.Zero)
		{
			return;
		}
		if (zeroMemory && blob.cbData > 0)
		{
			byte[] zeros = new byte[Math.Min(blob.cbData, 65536)];
			for (int offset = 0; offset < blob.cbData; offset += zeros.Length)
			{
				Marshal.Copy(zeros, 0, IntPtr.Add(blob.pbData, offset), Math.Min(zeros.Length, blob.cbData - offset));
			}
		}

		if (localAllocated)
		{
			LocalFree(blob.pbData);
		}
		else
		{
			Marshal.FreeHGlobal(blob.pbData);
		}
	}

	[DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CryptProtectData(
		ref DataBlob dataIn,
		string? description,
		IntPtr optionalEntropy,
		IntPtr reserved,
		IntPtr promptStruct,
		int flags,
		ref DataBlob dataOut);

	[DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool CryptUnprotectData(
		ref DataBlob dataIn,
		IntPtr description,
		IntPtr optionalEntropy,
		IntPtr reserved,
		IntPtr promptStruct,
		int flags,
		ref DataBlob dataOut);

	[DllImport("kernel32.dll")]
	private static extern IntPtr LocalFree(IntPtr memory);

	[StructLayout(LayoutKind.Sequential)]
	private struct DataBlob
	{
		public int cbData;

		public IntPtr pbData;
	}
}

public sealed class WindowsRobloxRuntimeProvider : IRobloxRuntimeProvider
{
	private readonly IProcessService _processes;
	private readonly WindowsPaths _paths;

	public WindowsRobloxRuntimeProvider(RuntimeKind kind, IProcessService processes, WindowsPaths paths)
	{
		Kind = kind;
		_processes = processes;
		_paths = paths;
	}

	public RuntimeKind Kind { get; }

	public async Task<RuntimeInstallation> FindInstallationAsync(CancellationToken cancellationToken = default)
	{
		SettingsDocument? settings = await LoadSettingsAsync(cancellationToken);
		string? executable = await Task.Run(() => FindExecutable(settings, cancellationToken), cancellationToken);
		CapabilityDescriptor capability = executable is null
			? new CapabilityDescriptor(GetFeature(), CapabilityState.RequiresExternalRuntime, "The official Roblox application is not installed", GetInstallAction())
			: new CapabilityDescriptor(GetFeature(), CapabilityState.Available, "The official Roblox application is available");

		return new RuntimeInstallation(
			Kind,
			"Roblox",
			null,
			executable,
			Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox"),
			capability);
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
			return OperationResult<LaunchSession>.Fail("RobloxNotInstalled", installation.Capability.Reason, installation.Capability.State);
		}

		OperationResult<ProcessStartResult> result = await _processes.StartAsync(
			new ProcessCommand(installation.Location, [request.Deeplink.AbsoluteUri], CaptureOutput: false),
			cancellationToken);

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
			installation));
	}

	private async Task<SettingsDocument?> LoadSettingsAsync(CancellationToken cancellationToken)
	{
		OperationResult<SettingsLoadResult> result = await new PortableSettingsStore(_paths).LoadAsync(cancellationToken);
		return result.Succeeded ? result.Value?.Document : null;
	}

	private string? FindExecutable(SettingsDocument? settings, CancellationToken cancellationToken)
	{
		string executableName = GetExecutableName(settings);
		List<string> roots = new();
		string configuredRoot = settings?.Get(GetInstallLocationKey(), string.Empty) ?? string.Empty;
		if (!string.IsNullOrWhiteSpace(configuredRoot))
		{
			roots.Add(configuredRoot);
		}

		roots.Add(Path.Combine(_paths.Storage.ApplicationSupport, "RblxVersions"));
		roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"));
		foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			cancellationToken.ThrowIfCancellationRequested();
			string? executable = FindExecutableInRoot(root, executableName, cancellationToken);
			if (executable is not null)
			{
				return executable;
			}
		}

		return null;
	}

	private static string? FindExecutableInRoot(string root, string executableName, CancellationToken cancellationToken)
	{
		try
		{
			if (File.Exists(root))
			{
				return string.Equals(Path.GetFileName(root), executableName, StringComparison.OrdinalIgnoreCase) ? root : null;
			}

			if (!Directory.Exists(root))
			{
				return null;
			}

			string direct = Path.Combine(root, executableName);
			if (File.Exists(direct))
			{
				return direct;
			}

			return Directory.EnumerateDirectories(root)
				.Take(1024)
				.Select(directory =>
				{
					cancellationToken.ThrowIfCancellationRequested();
					return directory;
				})
				.OrderByDescending(Directory.GetLastWriteTimeUtc)
				.Select(directory => Path.Combine(directory, executableName))
				.FirstOrDefault(File.Exists);
		}
		catch (IOException)
		{
			return null;
		}
		catch (UnauthorizedAccessException)
		{
			return null;
		}
	}

	private string GetExecutableName(SettingsDocument? settings)
	{
		if (Kind == RuntimeKind.Player && settings?.Get("RenameClientToEuroTrucks2", false) == true)
		{
			return "eurotrucks2.exe";
		}

		return Kind == RuntimeKind.Player ? "RobloxPlayerBeta.exe" : "RobloxStudioBeta.exe";
	}

	private string GetInstallLocationKey()
	{
		return Kind == RuntimeKind.Player ? "PlayerInstallLocation" : "StudioInstallLocation";
	}

	private FeatureId GetFeature()
	{
		return Kind == RuntimeKind.Player ? FeatureId.RobloxPlayer : FeatureId.RobloxStudio;
	}

	private string GetInstallAction()
	{
		return Kind == RuntimeKind.Player ? "Install Roblox" : "Install Roblox Studio";
	}
}

public sealed class WindowsProtocolRegistration : IProtocolRegistration
{
	public Task<CapabilityDescriptor> GetCapabilityAsync(CancellationToken cancellationToken = default)
	{
		return Task.FromResult(new CapabilityDescriptor(FeatureId.ProtocolRegistration, CapabilityState.Available, "Windows protocol registration is available"));
	}

	public Task<OperationResult> RegisterAsync(ProtocolRegistrationRequest request, CancellationToken cancellationToken = default)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrWhiteSpace(request.Scheme) || request.Scheme.Length > 64 || !Uri.CheckSchemeName(request.Scheme))
			{
				return Task.FromResult(OperationResult.Fail("InvalidProtocolScheme", "The protocol scheme is invalid"));
			}

			if (string.IsNullOrWhiteSpace(request.ApplicationPath) || !File.Exists(request.ApplicationPath))
			{
				return Task.FromResult(OperationResult.Fail("ApplicationPathMissing", "The protocol handler application path does not exist"));
			}
			if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 128 || request.DisplayName.Any(char.IsControl))
				return Task.FromResult(OperationResult.Fail("ApplicationNameInvalid", "The protocol handler application name is invalid"));

			using RegistryKey key = Registry.CurrentUser.CreateSubKey($"Software\\Classes\\{request.Scheme}");
			key.SetValue(string.Empty, $"URL:{request.DisplayName}");
			key.SetValue("URL Protocol", string.Empty);
			using RegistryKey command = key.CreateSubKey("shell\\open\\command");
			command.SetValue(string.Empty, $"\"{request.ApplicationPath}\" \"%1\"");

			return Task.FromResult(OperationResult.Success());
		}
		catch (OperationCanceledException)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "Protocol registration was canceled"));
		}
		catch (UnauthorizedAccessException exception)
		{
			return Task.FromResult(OperationResult.Fail("ProtocolRegistrationDenied", exception.Message, CapabilityState.RequiresPermission));
		}
		catch (Exception exception)
		{
			return Task.FromResult(OperationResult.Fail("ProtocolRegistrationFailed", exception.Message));
		}
	}
}
