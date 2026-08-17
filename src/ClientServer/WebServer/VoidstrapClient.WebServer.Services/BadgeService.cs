using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VoidstrapClient.WebServer.Services;

public class BadgeService
{
	private const int MaxPlayers = 1024;
	public static BadgeService Instance { get; }

	private readonly Dictionary<int, List<int>> _awardedBadges = new Dictionary<int, List<int>>();

	private List<int> GetPlayerBadges(int id)
	{
		if (!_awardedBadges.ContainsKey(id))
		{
			_awardedBadges[id] = new List<int>();
		}
		return _awardedBadges[id];
	}

	private List<int>? GetPlayerBadgesIfExists(int id)
	{
		if (_awardedBadges.ContainsKey(id))
		{
			return _awardedBadges[id];
		}
		return null;
	}

	public async Task<string> AwardBadgeAsync(int userId, int badgeId, CancellationToken cancellationToken)
	{
		string value;
		lock (_awardedBadges)
		{
			if (userId <= 0 || !PlaceMetadata.Default.Badges.ContainsKey(badgeId))
			{
				return "0";
			}
			value = PlaceMetadata.Default.Badges[badgeId];
			if (GetPlayerBadgesIfExists(userId)?.Contains(badgeId) == true)
			{
				return "0";
			}
			if (!_awardedBadges.ContainsKey(userId) && _awardedBadges.Count >= MaxPlayers)
			{
				return "0";
			}
			List<int> playerBadges = GetPlayerBadges(userId);
			playerBadges.Add(badgeId);
		}

		string playerName = await PlayerTrackingService.Default.GetPlayerNameFromIdSafeAsync(userId, cancellationToken) ?? "MISSINGNO";
		string creator = PlaceMetadata.Default.Creator;
		return $"{playerName} won {creator}'s \"{value}\" award!";
	}

	public bool HasBadge(int userId, int badgeId)
	{
		lock (_awardedBadges)
		{
			return GetPlayerBadgesIfExists(userId)?.Contains(badgeId) ?? false;
		}
	}

	static BadgeService()
	{
		Instance = new BadgeService();
	}
}
