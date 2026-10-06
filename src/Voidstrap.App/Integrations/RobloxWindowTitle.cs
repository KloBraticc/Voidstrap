using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Models.Entities;

namespace Voidstrap.Integrations;

internal sealed class RobloxWindowTitle : IDisposable
{
	private const string LOG_IDENT = "RobloxWindowTitle";
	private const string DefaultTitle = "Voidstrap";
	private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan LocationRetry = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan GameNameRetry = TimeSpan.FromSeconds(60);
	private static readonly TimeSpan PlayerCountWait = TimeSpan.FromSeconds(30);

	private readonly ActivityWatcher _activityWatcher;
	private readonly Action<string> _apply;
	private readonly CancellationTokenSource _cts = new();
	private readonly SemaphoreSlim _kick = new(0, 1);
	private string? _applied;
	private string? _locationJobId;
	private string? _location;
	private Task<string?>? _locationTask;
	private DateTime _nextLocationUtc = DateTime.MinValue;
	private long _nameUniverseId;
	private string? _gameName;
	private DateTime _nextGameNameUtc = DateTime.MinValue;
	private bool _disposed;

	public RobloxWindowTitle(ActivityWatcher activityWatcher, Action<string> apply)
	{
		_activityWatcher = activityWatcher;
		_apply = apply;
		CancellationToken token = _cts.Token;
		_ = Task.Run(() => LoopAsync(token));
	}

	public static string BaseTitle => Voidstrap.Utility.Branding.Apply(string.IsNullOrWhiteSpace(App.Settings.Prop.RobloxTitle) ? DefaultTitle : App.Settings.Prop.RobloxTitle);

	public static bool WantsUpdates => App.Settings.Prop.CycleTitleWithGameName || App.Settings.Prop.ShowServerInfoInTitle;

	public void Refresh()
	{
		if (_disposed)
			return;
		try
		{
			if (_kick.CurrentCount == 0)
				_kick.Release();
		}
		catch (SemaphoreFullException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
	}

	private async Task LoopAsync(CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				string title = await BuildAsync(token).ConfigureAwait(false);
				if (!token.IsCancellationRequested && !string.Equals(title, _applied, StringComparison.Ordinal))
				{
					_applied = title;
					App.Logger?.WriteLine(LOG_IDENT, "Setting the Roblox window title to '" + title + "'");
					_apply(title);
				}
				await _kick.WaitAsync(RefreshInterval, token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException(LOG_IDENT + "::Loop", ex);
		}
	}

	private async Task<string> BuildAsync(CancellationToken token)
	{
		string baseTitle = BaseTitle;
		ActivityData data = _activityWatcher.Data;
		if (!_activityWatcher.InGame || data.PlaceId == 0L || string.IsNullOrEmpty(data.JobId))
			return baseTitle;

		string title = baseTitle;
		if (App.Settings.Prop.CycleTitleWithGameName && data.UniverseId > 0)
		{
			string? gameName = await GetGameNameAsync(data.UniverseId).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(gameName))
				title = baseTitle + ": " + gameName;
		}
		if (!App.Settings.Prop.ShowServerInfoInTitle)
			return title;

		List<string> parts = new List<string>(2);
		string? location = GetLocation(data);
		if (!string.IsNullOrWhiteSpace(location))
			parts.Add(location);
		try
		{
			ServerPlayerSnapshot snapshot = await _activityWatcher.GetServerPlayerSnapshotAsync().WaitAsync(PlayerCountWait, token).ConfigureAwait(false);
			if (snapshot.Playing > 0)
				parts.Add(snapshot.MaxPlayers > 0 ? $"{snapshot.Playing}/{snapshot.MaxPlayers} players" : snapshot.Playing == 1 ? "1 player" : snapshot.Playing + " players");
		}
		catch (TimeoutException)
		{
		}
		return parts.Count == 0 ? title : title + " (" + string.Join(" · ", parts) + ")";
	}

	private async Task<string?> GetGameNameAsync(long universeId)
	{
		if (_nameUniverseId != universeId)
		{
			_nameUniverseId = universeId;
			_gameName = null;
			_nextGameNameUtc = DateTime.MinValue;
		}
		if (_gameName != null)
			return _gameName;
		UniverseDetails? details = UniverseDetails.LoadFromCache(universeId);
		if (string.IsNullOrWhiteSpace(details?.Data?.Name) && DateTime.UtcNow >= _nextGameNameUtc)
		{
			_nextGameNameUtc = DateTime.UtcNow + GameNameRetry;
			try
			{
				await UniverseDetails.FetchSingle(universeId).ConfigureAwait(false);
				details = UniverseDetails.LoadFromCache(universeId);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LOG_IDENT, "The game name could not be loaded: " + ex.Message);
			}
		}
		string? name = details?.Data?.Name;
		if (!string.IsNullOrWhiteSpace(name))
			_gameName = name;
		return _gameName;
	}

	private string? GetLocation(ActivityData data)
	{
		if (!string.Equals(_locationJobId, data.JobId, StringComparison.Ordinal))
		{
			_locationJobId = data.JobId;
			_location = null;
			_locationTask = null;
			_nextLocationUtc = DateTime.MinValue;
		}
		if (_location == null && _locationTask is { IsCompletedSuccessfully: true } done && !string.IsNullOrWhiteSpace(done.Result))
			_location = done.Result;
		if (_location != null)
			return _location;
		if ((_locationTask == null || _locationTask.IsCompleted) && DateTime.UtcNow >= _nextLocationUtc)
		{
			_nextLocationUtc = DateTime.UtcNow + LocationRetry;
			_locationTask = QueryLocationAsync(data);
		}
		return null;
	}

	private async Task<string?> QueryLocationAsync(ActivityData data)
	{
		try
		{
			string? location = await data.QueryServerLocation(_cts.Token).ConfigureAwait(false);
			if (!string.IsNullOrWhiteSpace(location))
				Refresh();
			return location;
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LOG_IDENT, "The server location could not be loaded: " + ex.Message);
			return null;
		}
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		try
		{
			_cts.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
		_cts.Dispose();
		GC.SuppressFinalize(this);
	}
}
