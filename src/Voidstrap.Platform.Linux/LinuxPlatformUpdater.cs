using Voidstrap.Platform;

namespace Voidstrap.Platform.Linux;

public sealed class LinuxPlatformUpdater : IPlatformUpdater
{
	private readonly Func<CancellationToken, Task<OperationResult>> _check;
	private readonly Func<CancellationToken, Task<OperationResult>> _apply;

	public LinuxPlatformUpdater(
		CapabilityDescriptor capability,
		Func<CancellationToken, Task<OperationResult>> check,
		Func<CancellationToken, Task<OperationResult>> apply)
	{
		Capability = capability ?? throw new ArgumentNullException(nameof(capability));
		_check = check ?? throw new ArgumentNullException(nameof(check));
		_apply = apply ?? throw new ArgumentNullException(nameof(apply));
	}

	public CapabilityDescriptor Capability { get; }

	public Task<OperationResult> CheckAsync(CancellationToken cancellationToken = default)
	{
		return InvokeAsync(_check, cancellationToken);
	}

	public Task<OperationResult> ApplyAsync(CancellationToken cancellationToken = default)
	{
		return InvokeAsync(_apply, cancellationToken);
	}

	private static async Task<OperationResult> InvokeAsync(
		Func<CancellationToken, Task<OperationResult>> operation,
		CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			OperationResult result = await operation(cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			return result;
		}
		catch (OperationCanceledException)
		{
			return OperationResult.Fail("OperationCanceled", "The update operation was canceled");
		}
	}
}
