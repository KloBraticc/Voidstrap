namespace Voidstrap.Integrations;

public sealed class MatchmakerCandidate
{
	public string JobId { get; init; } = "";

	public string MachineAddress { get; init; } = "";

	public int Port { get; init; }

	public RobloxDatacenter? Datacenter { get; init; }

	public double DistanceKm { get; init; }

	public int Playing { get; init; }

	public int MaxPlayers { get; init; }

	public int Ping { get; init; }

	public int EstimatedPingMs { get; init; }

	public double Score { get; init; }

	public double Fps { get; init; }

	// Tokens for the players' headshots, as the server list gives them
	public string[] PlayerTokens { get; init; } = [];

	public string? BlockedClosestCity { get; init; }

	public double BlockedClosestDistanceKm { get; init; }

	public string DatacenterName => Datacenter == null ? "unknown" : (string.IsNullOrEmpty(Datacenter.Country) ? Datacenter.City : Datacenter.City + ", " + Datacenter.Country);
}
