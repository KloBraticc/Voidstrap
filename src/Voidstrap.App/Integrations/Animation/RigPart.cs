using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Voidstrap.Integrations.Animation;

public sealed class RigPart
{
	public string Name { get; init; } = "";

	public Vector3D Size { get; init; }

	public Color Color { get; init; }
}
