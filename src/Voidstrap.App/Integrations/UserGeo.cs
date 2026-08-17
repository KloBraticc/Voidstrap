namespace Voidstrap.Integrations;

public sealed class UserGeo
{
	public double Lat { get; init; }

	public double Lon { get; init; }

	public string City { get; init; } = "";

	public string Region { get; init; } = "";

	public string Country { get; init; } = "";
}
