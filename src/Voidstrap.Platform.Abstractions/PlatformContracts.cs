using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Platform;

public enum PlatformId
{
	Windows,
	MacOS,
	Linux
}

public enum CapabilityState
{
	Available,
	RequiresPermission,
	RequiresExternalRuntime,
	Experimental,
	Unavailable
}

public enum FeatureId
{
	DesktopShell,
	EmbeddedBrowser,
	RobloxPlayer,
	RobloxStudio,
	SecureStorage,
	ProtocolRegistration,
	Updater,
	Notifications,
	Tray,
	Overlay,
	GlobalInput,
	AudioSession,
	ResourceOptimization,
	AssetInjection,
	FrameGeneration,
	VirtualController,
	ExtensionNativeAssets
}

public enum RuntimeKind
{
	Player,
	Studio
}

public enum ResourcePriority
{
	Automatic,
	Idle,
	BelowNormal,
	Normal,
	AboveNormal,
	High
}

public sealed record CapabilityDescriptor(
	FeatureId Feature,
	CapabilityState State,
	string Reason,
	string? RequiredAction = null,
	bool IsExperimental = false)
{
	public bool IsAvailable => State == CapabilityState.Available || State == CapabilityState.Experimental;
}

public sealed record OperationFailure(
	string Code,
	string Message,
	CapabilityState State = CapabilityState.Unavailable);

public sealed record OperationResult(bool Succeeded, OperationFailure? Failure = null)
{
	public static OperationResult Success()
	{
		return new OperationResult(true);
	}

	public static OperationResult Fail(string code, string message, CapabilityState state = CapabilityState.Unavailable)
	{
		return new OperationResult(false, new OperationFailure(code, message, state));
	}
}

public sealed record OperationResult<T>(bool Succeeded, T? Value = default, OperationFailure? Failure = null)
{
	public static OperationResult<T> Success(T value)
	{
		return new OperationResult<T>(true, value);
	}

	public static OperationResult<T> Fail(string code, string message, CapabilityState state = CapabilityState.Unavailable)
	{
		return new OperationResult<T>(false, default, new OperationFailure(code, message, state));
	}
}

public sealed record PlatformStoragePaths(
	string ApplicationSupport,
	string Configuration,
	string Data,
	string Cache,
	string Logs,
	string Downloads,
	string Extensions,
	string Temporary);

public sealed record RuntimeInstallation(
	RuntimeKind Kind,
	string Provider,
	string? Version,
	string? Location,
	string? DataDirectory,
	CapabilityDescriptor Capability);

public sealed record LaunchRequest(
	RuntimeKind Kind,
	Uri Deeplink,
	bool WaitForExit = false);

public sealed record LaunchSession(
	RuntimeKind Kind,
	string Provider,
	int ProcessId,
	DateTimeOffset StartedAt,
	RuntimeInstallation Installation,
	bool IsDirectProcess = true);

public sealed record ResourceOptimizationRequest(
	int ProcessId,
	ResourcePriority Priority,
	int? CpuLimit);

public sealed record ResourceOptimizationResult(
	int ProcessId,
	bool PriorityApplied,
	bool CpuLimitApplied,
	string Summary);

public sealed record ProcessCommand(
	string FileName,
	IReadOnlyList<string> Arguments,
	string? StandardInput = null,
	string? WorkingDirectory = null,
	bool CaptureOutput = true);

public sealed record ProcessExecution(
	int ExitCode,
	string StandardOutput,
	string StandardError);

public sealed record ProcessStartResult(int ProcessId);

public sealed record ProtocolRegistrationRequest(
	string Scheme,
	string ApplicationPath,
	string DisplayName);

public sealed record SecureValueResult(bool Found, string? Value);

public sealed record NotificationRequest(string Title, string Message);

public interface IPlatformCapabilities
{
	PlatformId Platform { get; }

	IReadOnlyCollection<CapabilityDescriptor> Features { get; }

	CapabilityDescriptor Get(FeatureId feature);
}

public interface IPlatformPaths
{
	PlatformStoragePaths Storage { get; }

	Task<OperationResult> EnsureDirectoriesAsync(CancellationToken cancellationToken = default);
}

public interface ISecureStore
{
	Task<OperationResult> SetAsync(string service, string key, string value, CancellationToken cancellationToken = default);

	Task<OperationResult<SecureValueResult>> GetAsync(string service, string key, CancellationToken cancellationToken = default);

	Task<OperationResult> DeleteAsync(string service, string key, CancellationToken cancellationToken = default);
}

public interface IProcessService
{
	string? FindExecutable(string name);

	Task<OperationResult<ProcessExecution>> ExecuteAsync(ProcessCommand command, CancellationToken cancellationToken = default);

	Task<OperationResult<ProcessStartResult>> StartAsync(ProcessCommand command, CancellationToken cancellationToken = default);
}

public interface IRobloxRuntimeProvider
{
	RuntimeKind Kind { get; }

	Task<RuntimeInstallation> FindInstallationAsync(CancellationToken cancellationToken = default);

	Task<OperationResult<LaunchSession>> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default);
}

public interface IProtocolRegistration
{
	Task<CapabilityDescriptor> GetCapabilityAsync(CancellationToken cancellationToken = default);

	Task<OperationResult> RegisterAsync(ProtocolRegistrationRequest request, CancellationToken cancellationToken = default);
}

public interface IWebViewHost
{
	event EventHandler<BrowserMessageReceivedEventArgs>? MessageReceived;

	Task<OperationResult> AddDocumentStartScriptAsync(string name, string script, CancellationToken cancellationToken = default);

	Task<OperationResult<string>> ExecuteScriptAsync(string script, CancellationToken cancellationToken = default);

	Task<OperationResult> NavigateAsync(Uri uri, CancellationToken cancellationToken = default);

	Task<OperationResult> RecreateAsync(Uri landingUri, CancellationToken cancellationToken = default);
}

public interface IPlatformFeatureService
{
	CapabilityDescriptor Capability { get; }
}

public interface INotificationService : IPlatformFeatureService
{
	Task<OperationResult> ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default);
}

public interface IPlatformUpdater : IPlatformFeatureService
{
	Task<OperationResult> CheckAsync(CancellationToken cancellationToken = default);

	Task<OperationResult> ApplyAsync(CancellationToken cancellationToken = default);
}

public interface IOverlayService : IPlatformFeatureService
{
}

public interface IInputService : IPlatformFeatureService
{
}

public interface IAudioSessionService : IPlatformFeatureService
{
}

public interface IResourceOptimizationService : IPlatformFeatureService
{
	Task<OperationResult<ResourceOptimizationResult>> ApplyAsync(ResourceOptimizationRequest request, CancellationToken cancellationToken = default);
}

public interface IPlatformHost
{
	PlatformId Id { get; }

	IPlatformCapabilities Capabilities { get; }

	IPlatformPaths Paths { get; }

	ISecureStore SecureStore { get; }

	IProcessService Processes { get; }

	IRobloxRuntimeProvider PlayerRuntime { get; }

	IRobloxRuntimeProvider StudioRuntime { get; }

	IProtocolRegistration ProtocolRegistration { get; }

	IPlatformUpdater Updater { get; }

	INotificationService Notifications { get; }

	IOverlayService Overlay { get; }

	IInputService Input { get; }

	IAudioSessionService AudioSession { get; }

	IResourceOptimizationService ResourceOptimization { get; }
}
