using System;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace VoidstrapClient.ServerAuthInterface;

internal class AuthService
{
	private const long AuthorisationLifetimeMilliseconds = 2 * 60 * 1000;
	private const long ChallengeLifetimeMilliseconds = 60 * 1000;
	private const long PruneIntervalMilliseconds = 10 * 1000;
	private const int MaxAuthorisedIps = 4096;
	private const int MaxChallenges = 4096;
	private readonly ConcurrentDictionary<IPAddress, long> _authorisedIps = new();
	private readonly ConcurrentDictionary<IPAddress, ChallengeData> _challenges = new();
	private readonly object _admissionGate = new();
	private long _nextAuthorisationPruneAt;
	private long _nextChallengePruneAt;

	private sealed record ChallengeData(string Value, long ExpiresAt);

	public static AuthService Instance { get; } = new AuthService();

	public bool IsIPAuthorised(IPAddress ip)
	{
		ip = Normalize(ip);
		if (IPAddress.IsLoopback(ip))
		{
			return true;
		}
		if (!_authorisedIps.TryGetValue(ip, out long expiresAt))
		{
			return false;
		}
		if (expiresAt > Environment.TickCount64)
		{
			return true;
		}
		_authorisedIps.TryRemove(ip, out _);
		return false;
	}

	public bool TryAuthoriseIP(IPAddress ip)
	{
		ip = Normalize(ip);
		long now = Environment.TickCount64;
		lock (_admissionGate)
		{
			if (now >= _nextAuthorisationPruneAt)
			{
				RemoveExpired(_authorisedIps, now);
				_nextAuthorisationPruneAt = now + PruneIntervalMilliseconds;
			}
			if (!_authorisedIps.ContainsKey(ip) && _authorisedIps.Count >= MaxAuthorisedIps)
			{
				return false;
			}
			_authorisedIps[ip] = now + AuthorisationLifetimeMilliseconds;
			return true;
		}
	}

	public bool TryConsumeAuthorisation(IPAddress ip)
	{
		ip = Normalize(ip);
		if (IPAddress.IsLoopback(ip))
		{
			return true;
		}
		if (!_authorisedIps.TryRemove(ip, out long expiresAt))
		{
			return false;
		}
		return expiresAt > Environment.TickCount64;
	}

	public bool TryCreateChallenge(IPAddress ip, out string challenge)
	{
		ip = Normalize(ip);
		long now = Environment.TickCount64;
		lock (_admissionGate)
		{
			if (now >= _nextChallengePruneAt)
			{
				RemoveExpiredChallenges(now);
				_nextChallengePruneAt = now + PruneIntervalMilliseconds;
			}
			if (!_challenges.ContainsKey(ip) && _challenges.Count >= MaxChallenges)
			{
				challenge = string.Empty;
				return false;
			}
			challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
			_challenges[ip] = new ChallengeData(challenge, now + ChallengeLifetimeMilliseconds);
			return true;
		}
	}

	public bool TryConsumeChallenge(IPAddress ip, out string challenge)
	{
		ip = Normalize(ip);
		challenge = string.Empty;
		if (!_challenges.TryRemove(ip, out ChallengeData? data) || data.ExpiresAt <= Environment.TickCount64)
		{
			return false;
		}
		challenge = data.Value;
		return true;
	}

	private static IPAddress Normalize(IPAddress ip)
	{
		return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
	}

	private void RemoveExpiredChallenges(long now)
	{
		foreach ((IPAddress ip, ChallengeData data) in _challenges)
		{
			if (data.ExpiresAt <= now)
			{
				_challenges.TryRemove(ip, out _);
			}
		}
	}

	private static void RemoveExpired(ConcurrentDictionary<IPAddress, long> entries, long now)
	{
		foreach ((IPAddress ip, long expiresAt) in entries)
		{
			if (expiresAt <= now)
			{
				entries.TryRemove(ip, out _);
			}
		}
	}
}
