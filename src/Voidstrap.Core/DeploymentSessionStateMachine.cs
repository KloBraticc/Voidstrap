using System;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public enum DeploymentState
{
	Idle,
	DiscoveringRuntime,
	LaunchingRuntime,
	ApplyingResourceProfile,
	Ready,
	Failed,
	Canceled
}

public sealed record DeploymentStateChangedEventArgs(DeploymentState State, string Message);

public sealed record DeploymentLaunchResult(
	LaunchSession Session,
	ResourceOptimizationResult? ResourceOptimization,
	OperationFailure? ResourceOptimizationFailure,
	string Summary);

public sealed class DeploymentSessionStateMachine : IDisposable
{
	private readonly object _sync = new();
	private readonly RuntimeLaunchCoordinator _launchCoordinator;
	private readonly IResourceOptimizationService _resourceOptimization;
	private CancellationTokenSource? _activeCancellation;
	private int _launching;
	private bool _disposed;

	public DeploymentSessionStateMachine(RuntimeLaunchCoordinator launchCoordinator, IResourceOptimizationService resourceOptimization)
	{
		_launchCoordinator = launchCoordinator;
		_resourceOptimization = resourceOptimization;
	}

	public event EventHandler<DeploymentStateChangedEventArgs>? StateChanged;

	public DeploymentState State { get; private set; } = DeploymentState.Idle;

	public void Cancel()
	{
		lock (_sync)
		{
			_activeCancellation?.Cancel();
		}
	}

	public async Task<OperationResult<DeploymentLaunchResult>> LaunchAsync(
		RuntimeKind kind,
		string launchArguments,
		SettingsDocument? settings,
		CancellationToken cancellationToken = default)
	{
		if (_disposed)
		{
			return OperationResult<DeploymentLaunchResult>.Fail("DeploymentDisposed", "The deployment session is closed");
		}

		if (Interlocked.CompareExchange(ref _launching, 1, 0) != 0)
		{
			return OperationResult<DeploymentLaunchResult>.Fail("DeploymentBusy", "A launch is already in progress");
		}

		using CancellationTokenSource operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		lock (_sync)
		{
			if (_disposed)
			{
				Interlocked.Exchange(ref _launching, 0);
				return OperationResult<DeploymentLaunchResult>.Fail("DeploymentDisposed", "The deployment session is closed");
			}

			_activeCancellation = operationCancellation;
		}

		try
		{
			Transition(DeploymentState.DiscoveringRuntime, "Checking the selected Roblox runtime.");
			Transition(DeploymentState.LaunchingRuntime, "Starting the selected Roblox runtime.");
			OperationResult<LaunchSession> launchResult = await _launchCoordinator.LaunchAsync(kind, launchArguments, operationCancellation.Token);
			if (!launchResult.Succeeded || launchResult.Value is null)
			{
				Transition(DeploymentState.Failed, launchResult.Failure?.Message ?? "The Roblox runtime did not accept the launch request.");
				return CopyFailure<DeploymentLaunchResult>(launchResult.Failure);
			}

			LaunchSession session = launchResult.Value;
			ResourceOptimizationResult? optimization = null;
			OperationFailure? optimizationFailure = null;
			string summary = session.Provider + " accepted the launch request.";
			if (settings is not null)
			{
				ResourceOptimizationProfile profile = ResourceOptimizationProfileResolver.Resolve(settings);
				if (profile.IsEnabled)
				{
					if (!session.IsDirectProcess)
					{
						optimizationFailure = new OperationFailure(
							"DirectProcessUnavailable",
							"The selected runtime does not expose a directly managed game process");
						summary += " The selected resource profile was not applied because this runtime does not expose a directly managed game process.";
					}
					else
					{
						Transition(DeploymentState.ApplyingResourceProfile, "Applying the selected launch resource profile.");
						OperationResult<ResourceOptimizationResult> optimizationResult = await _resourceOptimization.ApplyAsync(
							new ResourceOptimizationRequest(session.ProcessId, profile.Priority, profile.CpuLimit),
							operationCancellation.Token);
						if (optimizationResult.Succeeded && optimizationResult.Value is not null)
						{
							optimization = optimizationResult.Value;
							summary += " " + optimization.Summary;
						}
						else
						{
							optimizationFailure = optimizationResult.Failure;
							summary += " Resource profile: " + (optimizationFailure?.Message ?? "The selected resource profile could not be applied.");
						}
					}
				}
			}

			Transition(DeploymentState.Ready, summary);
			return OperationResult<DeploymentLaunchResult>.Success(new DeploymentLaunchResult(session, optimization, optimizationFailure, summary));
		}
		catch (OperationCanceledException)
		{
			Transition(DeploymentState.Canceled, "The launch was canceled.");
			return OperationResult<DeploymentLaunchResult>.Fail("OperationCanceled", "The launch was canceled");
		}
		finally
		{
			lock (_sync)
			{
				if (ReferenceEquals(_activeCancellation, operationCancellation))
				{
					_activeCancellation = null;
				}
			}

			Interlocked.Exchange(ref _launching, 0);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			_activeCancellation?.Cancel();
			_activeCancellation = null;
		}

		GC.SuppressFinalize(this);
	}

	private void Transition(DeploymentState state, string message)
	{
		State = state;
		StateChanged?.Invoke(this, new DeploymentStateChangedEventArgs(state, message));
	}

	private static OperationResult<T> CopyFailure<T>(OperationFailure? failure)
	{
		return failure is null
			? OperationResult<T>.Fail("RobloxLaunchFailed", "The Roblox runtime did not accept the launch request.")
			: OperationResult<T>.Fail(failure.Code, failure.Message, failure.State);
	}
}
