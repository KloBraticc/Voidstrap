namespace Voidstrap.Models;

internal sealed record AvatarAsset(long Id, string Name, int TypeId, string TypeName, string? MetaJson);

internal sealed class AvatarState
{
    public List<AvatarAsset> Assets { get; } = new();

    public Dictionary<string, double> Scales { get; } = new();

    public string AvatarType { get; set; } = "R15";
}
