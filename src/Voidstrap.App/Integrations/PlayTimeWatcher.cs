using Voidstrap.Integrations;

public sealed class PlaytimeTracker : IDisposable
{
    private readonly ActivityWatcher _watcher;
    private readonly Dictionary<long, TimeSpan> _gamePlaytimes = new();

	private bool _disposed;

    public PlaytimeTracker(ActivityWatcher watcher)
    {
        _watcher = watcher;
        _watcher.OnGameJoin += Watcher_OnGameJoin;
        _watcher.OnGameLeave += Watcher_OnGameLeave;
    }

    private DateTime _currentJoinTime;
    private long _currentPlaceId;

    private void Watcher_OnGameJoin(object? sender, EventArgs e)
    {
        if (_watcher.Data.PlaceId != 0)
        {
            _currentJoinTime = DateTime.Now;
            _currentPlaceId = _watcher.Data.PlaceId;
        }
    }

    private void Watcher_OnGameLeave(object? sender, EventArgs e)
    {
        if (_currentPlaceId != 0)
        {
            TimeSpan sessionTime = DateTime.Now - _currentJoinTime;
            if (_gamePlaytimes.ContainsKey(_currentPlaceId))
                _gamePlaytimes[_currentPlaceId] += sessionTime;
            else
                _gamePlaytimes[_currentPlaceId] = sessionTime;

            _currentPlaceId = 0;
        }
    }

    public (long placeId, TimeSpan total)? GetMostPlayedGame()
    {
        if (_gamePlaytimes.Count == 0) return null;
        var max = _gamePlaytimes.OrderByDescending(x => x.Value).First();
        return (max.Key, max.Value);
    }

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		_watcher.OnGameJoin -= Watcher_OnGameJoin;
		_watcher.OnGameLeave -= Watcher_OnGameLeave;
		GC.SuppressFinalize(this);
	}
}
