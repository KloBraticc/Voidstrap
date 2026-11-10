namespace Voidstrap.Models;

internal sealed record AvatarAsset(long Id, string Name, int TypeId, string TypeName, string? MetaJson);

internal sealed record AvatarEmote(long Id, int Position);

internal sealed record AvatarRules(Dictionary<string, (int Id, int Max)> Types, List<string> Palette);

internal sealed class AvatarState
{
    public List<AvatarAsset> Assets { get; } = new();

    public Dictionary<string, double> Scales { get; } = new();

    public Dictionary<string, string> BodyColors { get; } = new();

    public List<AvatarEmote> Emotes { get; } = new();

    public string AvatarType { get; set; } = "R15";
}
