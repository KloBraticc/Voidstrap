using System.Linq;

namespace VoidstrapClient.Common.Models;

public class AssetPackSodikm
{
	public string Name { get; set; } = "";

	public string Description { get; set; } = "";

	public string Author { get; set; } = "";

	public string Version { get; set; } = "";

	public string Clients { get; set; } = "";

	public AssetPack Convert()
	{
		return new AssetPack
		{
			Name = Name,
			Description = Description,
			Author = Author,
			Version = Version,
			Clients = Clients.Split(';').ToList()
		};
	}
}
