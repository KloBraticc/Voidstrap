using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Core;

public sealed class RuntimeLaunchCoordinator
{
	private readonly IRobloxRuntimeProvider _playerRuntime;
	private readonly IRobloxRuntimeProvider _studioRuntime;

	public RuntimeLaunchCoordinator(IRobloxRuntimeProvider playerRuntime, IRobloxRuntimeProvider studioRuntime)
	{
		_playerRuntime = playerRuntime;
		_studioRuntime = studioRuntime;
	}

	public Task<OperationResult<LaunchSession>> LaunchAsync(RuntimeKind kind, string launchArguments, CancellationToken cancellationToken = default)
	{
		if (!RobloxDeeplink.TryExtract(launchArguments, out var deeplink) || deeplink is null)
		{
			return Task.FromResult(OperationResult<LaunchSession>.Fail("InvalidRobloxDeeplink", "No valid Roblox deeplink was supplied"));
		}

		IRobloxRuntimeProvider runtime = kind == RuntimeKind.Player ? _playerRuntime : _studioRuntime;
		return runtime.LaunchAsync(new LaunchRequest(kind, deeplink), cancellationToken);
	}
}
