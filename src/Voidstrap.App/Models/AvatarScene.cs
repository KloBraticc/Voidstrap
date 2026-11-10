using System.Windows.Media;
using System.Windows.Media.Media3D;
using Voidstrap.Integrations.Animation;

namespace Voidstrap.Models;

public sealed record AvatarSkin(Point3D[] Local, int[] Bone, string[] Bones);

public sealed record AvatarPiece(MeshGeometry3D Mesh, Material Material, string Part, Matrix3D Offset)
{
    public AvatarSkin? Skin { get; init; }
}

public sealed record AvatarEffect(string Kind, string Part, Matrix3D Offset, Color Color);

public sealed record AvatarJoint(string Parent, string Child, RobloxCFrame C0, RobloxCFrame C1);

public sealed record AvatarRig(string Root, IReadOnlyList<AvatarJoint> Joints, AnimationData? Animation);

public sealed record AvatarScene(Model3DGroup Model, Rect3D Bounds, double Yaw)
{
    public IReadOnlyList<AvatarPiece>? Pieces { get; init; }

    public AvatarRig? Rig { get; init; }

    public IReadOnlyList<AvatarEffect>? Effects { get; init; }
}
