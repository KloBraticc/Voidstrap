namespace Voidstrap.Models;

public sealed class LibraryGameCollection
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<long> UniverseIds { get; set; } = new();
}
