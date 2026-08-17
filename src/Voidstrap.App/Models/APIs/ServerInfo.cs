namespace Voidstrap.Models.APIs;

public sealed class ServerInfo
{
	public string Id { get; set; } = "";

	public int MaxPlayers { get; set; }

	public int Playing { get; set; }

	public double FPS { get; set; }

	public int Ping { get; set; }
}
