namespace Voidstrap.Models.Entities;

public sealed record SpotifyItem(string Href, string Name, string Detail, string? Image);

public sealed record SpotifyState(bool SignedIn, string Title, string Artist, string? Image, bool Playing, string Position, string Duration, double Progress, double Volume, bool Shuffle, string Repeat, bool Liked);
