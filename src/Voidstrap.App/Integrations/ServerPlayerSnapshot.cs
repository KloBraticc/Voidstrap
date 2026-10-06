using System;

namespace Voidstrap.Integrations;

public enum ServerPlayerCountState
{
	NotInGame,
	Live,
	Full,
	NotListed,
	PrivateNotListed,
	PrivateSignedOut,
	Reserved,
	RateLimited,
	Unavailable
}

public sealed record ServerPlayerSnapshot(ServerPlayerCountState State, int Playing, int MaxPlayers, int GameTotal, DateTime? UpdatedUtc, DateTime? RetryUtc)
{
	public static ServerPlayerSnapshot NotInGame { get; } = new(ServerPlayerCountState.NotInGame, 0, 0, 0, null, null);
}

public readonly record struct RobloxApiResponse(int StatusCode, string? Body, TimeSpan? RetryAfter)
{
	public bool Succeeded => StatusCode is >= 200 and < 300;

	public bool RateLimited => StatusCode == 429;
}
