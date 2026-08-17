using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoidstrapClient.WebServer.Services;

internal class PlayerTrackingService
{
	private const int MaxPlayerNameRetries = 3;
	private const int MaxPlayers = 1024;
	private const int MaxPlayerNameLength = 64;

	private readonly ConcurrentDictionary<int, string> _players = new ConcurrentDictionary<int, string>();
	private readonly object _admissionGate = new object();

	public static PlayerTrackingService Default { get; } = new PlayerTrackingService();

	public bool TryRegisterPlayer(int userId, string name)
	{
		if (userId <= 0 || string.IsNullOrWhiteSpace(name) || name.Length > MaxPlayerNameLength)
		{
			return false;
		}
		lock (_admissionGate)
		{
			if (!_players.ContainsKey(userId) && _players.Count >= MaxPlayers)
			{
				return false;
			}
			_players[userId] = name;
			return true;
		}
	}

	public void UnregisterPlayer(int userId)
	{
		_players.TryRemove(userId, out _);
	}

	public string? GetPlayerNameFromId(int userId)
	{
		return _players.TryGetValue(userId, out string? name) ? name : null;
	}

	public async Task<string?> GetPlayerNameFromIdSafeAsync(int userId, CancellationToken cancellationToken)
	{
		for (int attempt = 0; attempt <= MaxPlayerNameRetries; attempt++)
		{
			string? playerNameFromId = GetPlayerNameFromId(userId);
			if (playerNameFromId != null)
			{
				return playerNameFromId;
			}
			if (attempt < MaxPlayerNameRetries)
			{
				await Task.Delay(100, cancellationToken);
			}
		}
		return null;
	}

	public int? GetPlayerIdFromName(string userName)
	{
		foreach (KeyValuePair<int, string> player in _players)
		{
			if (player.Value == userName)
			{
				return player.Key;
			}
		}
		return null;
	}
}
