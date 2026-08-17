using System;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Core.AssetProxy;

namespace Voidstrap.Integrations.AssetProxy;

public static class UsernameSpoofer
{
	private static readonly Lazy<UsernameSpoofRuntime> Runtime = new(() => new UsernameSpoofRuntime(LoadFromSettings()), LazyThreadSafetyMode.ExecutionAndPublication);

	private static readonly UsernameSpoofProcessor Processor = new(ResolveIdentityAsync);
	private static readonly SemaphoreSlim IdentityGate = new(1, 1);
	private static UsernameSpoofIdentity? _cachedIdentity;
	private static DateTime _identityExpiresUtc;

	public static UsernameSpoofState CurrentState
	{
		get => Runtime.Value.Current;
	}

	public static bool IsEnabled()
	{
		return CurrentState.IsEnabled;
	}

	public static void SetRuntimeState(UsernameSpoofState state)
	{
		Runtime.Value.Set(state);
	}

	public static bool CanProcessResponse(string host, string path)
	{
		return UsernameSpoofProcessor.CanProcessResponse(host, path, CurrentState);
	}

	public static async Task<byte[]?> ProcessResponseAsync(string host, string path, byte[] body, CancellationToken ct)
	{
		return await Processor.ProcessResponseAsync(host, path, body, CurrentState, ct).ConfigureAwait(false);
	}

	private static async Task<UsernameSpoofIdentity?> ResolveIdentityAsync(CancellationToken ct)
	{
		if (_cachedIdentity.HasValue && DateTime.UtcNow < _identityExpiresUtc)
			return _cachedIdentity;
		await IdentityGate.WaitAsync(ct).ConfigureAwait(false);
		try
		{
			if (_cachedIdentity.HasValue && DateTime.UtcNow < _identityExpiresUtc)
				return _cachedIdentity;
			RobloxAccount? account = await RobloxCookie.GetAccountAsync(ct).ConfigureAwait(false);
			_cachedIdentity = account == null ? null : new UsernameSpoofIdentity(account.UserId, account.Username);
			_identityExpiresUtc = DateTime.UtcNow.AddSeconds(30);
			return _cachedIdentity;
		}
		finally
		{
			IdentityGate.Release();
		}
	}

	private static UsernameSpoofState LoadFromSettings()
	{
		var settings = App.Settings.Prop;
		return new UsernameSpoofState(
			settings.SpoofOthersName,
			settings.SpoofOthersApplyIngame,
			settings.SpoofOthersVerified,
			settings.SpoofSelfName,
			settings.SpoofSelfApplyIngame,
			settings.SpoofSelfVerified,
			settings.SpoofSelfGameCreator);
	}
}
