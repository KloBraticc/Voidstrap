using Voidstrap.Integrations.Animation;

namespace Voidstrap.Integrations;

public sealed class ModelPart
{
	public RobloxCFrame CFrame = RobloxCFrame.Identity;

	public double SizeX = 1.0;

	public double SizeY = 1.0;

	public double SizeZ = 1.0;

	public string MeshId = "";

	public byte R = 180;

	public byte G = 180;

	public byte B = 188;

	public bool HasSize;

	public bool HasCFrame;
}
