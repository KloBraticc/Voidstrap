namespace VoidstrapClient.Common.Models;

public class Outfit
{
	public string Name { get; set; } = "";

	public string? Client { get; set; }

	public Character Character { get; set; } = new Character();

	public bool IsClient(string? client)
	{
		if (client == "None" && Client == null)
		{
			return true;
		}
		return Client == client;
	}
}
