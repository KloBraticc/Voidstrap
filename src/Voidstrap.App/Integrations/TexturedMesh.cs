using System.Windows;
using System.Windows.Media.Media3D;

namespace Voidstrap.Integrations;

public sealed class TexturedMesh
{
    public List<Point3D> Positions { get; } = new();

    public List<Vector3D> Normals { get; } = new();

    public List<Point> Coords { get; } = new();

    public List<int> Indices { get; } = new();
}
