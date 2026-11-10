using System.Windows.Media.Media3D;
using Voidstrap.Integrations.Animation;

namespace Voidstrap.Integrations;

public sealed class RbxmInstance
{
    public string ClassName { get; init; } = "";

    public string Name { get; set; } = "";

    public RbxmInstance? Parent { get; set; }

    public List<RbxmInstance> Children { get; } = new();

    public Dictionary<string, string> Strings { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Vector3D> Vectors { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, RobloxCFrame> CFrames { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, double> Numbers { get; } = new(StringComparer.Ordinal);

    public RbxmInstance? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

    public IEnumerable<RbxmInstance> Descendants()
    {
        foreach (RbxmInstance child in Children)
        {
            yield return child;
            foreach (RbxmInstance nested in child.Descendants())
                yield return nested;
        }
    }
}
