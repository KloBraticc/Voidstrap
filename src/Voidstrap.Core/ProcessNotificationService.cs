using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed class ProcessNotificationService : INotificationService
{
	private readonly IProcessService _processes;
	private readonly string? _executable;
	private readonly Func<NotificationRequest, IReadOnlyList<string>> _argumentsFactory;

	public ProcessNotificationService(
		IProcessService processes,
		CapabilityDescriptor capability,
		string? executable,
		Func<NotificationRequest, IReadOnlyList<string>> argumentsFactory)
	{
		_processes = processes ?? throw new ArgumentNullException(nameof(processes));
		Capability = capability ?? throw new ArgumentNullException(nameof(capability));
		_executable = executable;
		_argumentsFactory = argumentsFactory ?? throw new ArgumentNullException(nameof(argumentsFactory));
	}

	public CapabilityDescriptor Capability { get; }

	public async Task<OperationResult> ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default)
	{
		if (!Capability.IsAvailable || string.IsNullOrWhiteSpace(_executable))
		{
			return OperationResult.Fail("NotificationsUnavailable", Capability.Reason, Capability.State);
		}

		if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Message))
		{
			return OperationResult.Fail("NotificationInvalid", "A notification title and message are required");
		}

		OperationResult<ProcessExecution> result = await _processes.ExecuteAsync(
			new ProcessCommand(_executable, _argumentsFactory(request)),
			cancellationToken);
		if (!result.Succeeded || result.Value is null)
		{
			return result.Failure is null
				? OperationResult.Fail("NotificationFailed", "The native notification command failed")
				: OperationResult.Fail(result.Failure.Code, result.Failure.Message, result.Failure.State);
		}

		if (result.Value.ExitCode == 0)
		{
			return OperationResult.Success();
		}

		string message = string.IsNullOrWhiteSpace(result.Value.StandardError)
			? "The native notification command failed"
			: result.Value.StandardError.Trim();
		CapabilityState state = message.Contains("not authorized", StringComparison.OrdinalIgnoreCase)
			|| message.Contains("permission", StringComparison.OrdinalIgnoreCase)
			? CapabilityState.RequiresPermission
			: CapabilityState.Unavailable;
		return OperationResult.Fail("NotificationFailed", message, state);
	}
}

public sealed class UnavailableNotificationService : INotificationService
{
	public UnavailableNotificationService(CapabilityDescriptor capability)
	{
		Capability = capability ?? throw new ArgumentNullException(nameof(capability));
	}

	public CapabilityDescriptor Capability { get; }

	public Task<OperationResult> ShowAsync(NotificationRequest request, CancellationToken cancellationToken = default)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "The notification was canceled"));
		}

		return Task.FromResult(OperationResult.Fail("NotificationsUnavailable", Capability.Reason, Capability.State));
	}
}
