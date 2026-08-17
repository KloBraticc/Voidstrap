using System;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed class UnavailablePlatformUpdater : IPlatformUpdater
{
	public UnavailablePlatformUpdater(CapabilityDescriptor capability)
	{
		Capability = capability ?? throw new ArgumentNullException(nameof(capability));
	}

	public CapabilityDescriptor Capability { get; }

	public Task<OperationResult> CheckAsync(CancellationToken cancellationToken = default)
	{
		return FailAsync("UpdateCheckUnavailable", cancellationToken);
	}

	public Task<OperationResult> ApplyAsync(CancellationToken cancellationToken = default)
	{
		return FailAsync("UpdateApplyUnavailable", cancellationToken);
	}

	private Task<OperationResult> FailAsync(string code, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return Task.FromResult(OperationResult.Fail("OperationCanceled", "The update operation was canceled"));
		}

		return Task.FromResult(OperationResult.Fail(code, Capability.Reason, Capability.State));
	}
}

public sealed class CapabilityOnlyPlatformFeatureService : IOverlayService, IInputService, IAudioSessionService
{
	public CapabilityOnlyPlatformFeatureService(CapabilityDescriptor capability)
	{
		Capability = capability ?? throw new ArgumentNullException(nameof(capability));
	}

	public CapabilityDescriptor Capability { get; }
}
