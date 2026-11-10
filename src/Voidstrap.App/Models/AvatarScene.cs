using System.Windows.Media.Media3D;

namespace Voidstrap.Models;

public sealed record AvatarScene(Model3DGroup Model, Rect3D Bounds, double Yaw);
