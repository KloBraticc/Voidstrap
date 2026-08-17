using System.Collections.Generic;
using System.Windows.Media.Media3D;

namespace Voidstrap.Integrations;

public sealed class MeshModel
{
	public List<Point3D> Positions { get; } = new List<Point3D>();

	public List<int> Indices { get; } = new List<int>();

	public int FaceCount => Indices.Count / 3;
}
