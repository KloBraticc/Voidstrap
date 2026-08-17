namespace Voidstrap.Core.AssetProxy;

public sealed class AssetPreloadHint
{
	public string AssetId { get; set; } = "";

	public string Hash { get; set; } = "";

	public string Url { get; set; } = "";

	public int Priority { get; set; }

	public DateTime LastSeenUtc { get; set; }
}

public sealed class AssetPreloadGame
{
	public DateTime LastUsedUtc { get; set; }

	public int NextPriority { get; set; }

	public Dictionary<string, AssetPreloadHint> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AssetPreloadCatalog
{
	private Dictionary<long, AssetPreloadGame> _games = [];

	private bool _normalized;

	public Dictionary<long, AssetPreloadGame> Games
	{
		get => _games;
		set
		{
			_games = value ?? [];
			_normalized = false;
		}
	}

	public bool Observe(long placeId, string? assetId, string? hash, string? url, DateTime nowUtc)
	{
		Normalize();
		if (placeId <= 0 || !IsValidHash(hash))
		{
			return false;
		}

		string normalizedHash = hash!.ToLowerInvariant();
		if (!Games.TryGetValue(placeId, out AssetPreloadGame? game))
		{
			game = new AssetPreloadGame();
			Games[placeId] = game;
		}
		bool gameRecencyChanged = nowUtc - game.LastUsedUtc >= TimeSpan.FromMinutes(5);
		game.LastUsedUtc = nowUtc;
		if (game.Assets.TryGetValue(normalizedHash, out AssetPreloadHint? existing))
		{
			bool changed = gameRecencyChanged || nowUtc - existing.LastSeenUtc >= TimeSpan.FromMinutes(5);
			string normalizedId = NormalizeAssetId(assetId);
			if (normalizedId.Length > 0 && !string.Equals(existing.AssetId, normalizedId, StringComparison.Ordinal))
			{
				existing.AssetId = normalizedId;
				changed = true;
			}
			string normalizedUrl = NormalizeUrl(url);
			if (normalizedUrl.Length > 0 && !string.Equals(existing.Url, normalizedUrl, StringComparison.Ordinal))
			{
				existing.Url = normalizedUrl;
				changed = true;
			}
			existing.LastSeenUtc = nowUtc;
			return changed;
		}

		game.Assets[normalizedHash] = new AssetPreloadHint
		{
			AssetId = NormalizeAssetId(assetId),
			Hash = normalizedHash,
			Url = NormalizeUrl(url),
			Priority = game.NextPriority++,
			LastSeenUtc = nowUtc
		};
		return true;
	}

	public IReadOnlyList<AssetPreloadHint> GetForGame(long placeId)
	{
		Normalize();
		if (!Games.TryGetValue(placeId, out AssetPreloadGame? game))
		{
			return [];
		}
		return game.Assets.Values
			.Where(value => value.AssetId.Length > 0 && IsValidHash(value.Hash))
			.OrderBy(value => value.Priority)
			.ThenByDescending(value => value.LastSeenUtc)
			.ToArray();
	}

	public void TouchGame(long placeId, DateTime nowUtc)
	{
		Normalize();
		if (placeId > 0 && Games.TryGetValue(placeId, out AssetPreloadGame? game))
		{
			game.LastUsedUtc = nowUtc;
		}
	}

	public void Trim(int maximumGames, int maximumAssetsPerGame)
	{
		Normalize();
		maximumGames = Math.Max(1, maximumGames);
		maximumAssetsPerGame = Math.Max(1, maximumAssetsPerGame);
		foreach (AssetPreloadGame game in Games.Values)
		{
			if (game.Assets.Count <= maximumAssetsPerGame)
			{
				continue;
			}
			foreach (string hash in game.Assets.Values
				.OrderBy(value => value.Priority)
				.ThenByDescending(value => value.LastSeenUtc)
				.Skip(maximumAssetsPerGame)
				.Select(value => value.Hash)
				.ToArray())
			{
				game.Assets.Remove(hash);
			}
		}
		if (Games.Count <= maximumGames)
		{
			return;
		}
		foreach (long placeId in Games
			.OrderByDescending(pair => pair.Value.LastUsedUtc)
			.Skip(maximumGames)
			.Select(pair => pair.Key)
			.ToArray())
		{
			Games.Remove(placeId);
		}
	}

	public static bool IsValidHash(string? hash)
	{
		return hash is { Length: 32 } && hash.All(Uri.IsHexDigit);
	}

	private static string NormalizeAssetId(string? assetId)
	{
		return long.TryParse(assetId, out long value) && value > 0 ? value.ToString() : "";
	}

	private static string NormalizeUrl(string? url)
	{
		if (string.IsNullOrWhiteSpace(url) || url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
			return "";
		return parsed.Scheme is "http" or "https" ? parsed.AbsoluteUri : "";
	}

	private void Normalize()
	{
		if (_normalized)
			return;
		_normalized = true;
		foreach ((long placeId, AssetPreloadGame? game) in Games.ToArray())
		{
			if (placeId <= 0 || game is null)
			{
				Games.Remove(placeId);
				continue;
			}
			Dictionary<string, AssetPreloadHint> assets = new(StringComparer.OrdinalIgnoreCase);
			foreach ((string key, AssetPreloadHint? hint) in game.Assets ?? new Dictionary<string, AssetPreloadHint>())
			{
				if (hint is null || !IsValidHash(key) || !IsValidHash(hint.Hash) || !string.Equals(key, hint.Hash, StringComparison.OrdinalIgnoreCase))
					continue;
				string normalizedHash = key.ToLowerInvariant();
				assets[normalizedHash] = new AssetPreloadHint
				{
					AssetId = NormalizeAssetId(hint.AssetId),
					Hash = normalizedHash,
					Url = NormalizeUrl(hint.Url),
					Priority = Math.Clamp(hint.Priority, 0, 1_000_000),
					LastSeenUtc = hint.LastSeenUtc
				};
			}
			game.Assets = assets;
			game.NextPriority = Math.Max(game.NextPriority, assets.Count == 0 ? 0 : assets.Values.Max(static hint => hint.Priority) + 1);
		}
	}
}
