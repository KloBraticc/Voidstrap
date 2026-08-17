namespace Voidstrap.Integrations.Animation;

public sealed class RigJoint
{
	public string Name { get; init; } = "";

	public string Part0 { get; init; } = "";

	public string Part1 { get; init; } = "";

	public RobloxCFrame C0 { get; init; }

	public RobloxCFrame C1 { get; init; }
}
