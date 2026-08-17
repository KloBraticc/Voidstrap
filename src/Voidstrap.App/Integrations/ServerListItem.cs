namespace Voidstrap.Integrations;

internal sealed class ServerListItem
{
	public string JobId { get; init; } = "";

	public int Playing { get; init; }

	public int MaxPlayers { get; init; }

	public int Ping { get; init; } = -1;
}
