using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Voidstrap.Integrations.Animation;

public static class RigGeometry
{
	private static ImageSource? _faceTexture;

	private static bool _faceTried;

	private static ImageSource? FaceTexture()
	{
		if (_faceTried)
		{
			return _faceTexture;
		}
		_faceTried = true;
		try
		{
			var bitmapImage = Voidstrap.Utility.SafeImaging.FromUri(new Uri("pack://application:,,,/Resources/RobloxFace.png"));
			if (((Freezable)bitmapImage).CanFreeze)
			{
				((Freezable)bitmapImage).Freeze();
			}
			_faceTexture = bitmapImage;
		}
		catch
		{
			_faceTexture = null;
		}
		return _faceTexture;
	}

	public static Model3D BuildPart(RigPart part)
	{
		//IL_0213: Unknown result type (might be due to invalid IL or missing references)
		//IL_0230: Unknown result type (might be due to invalid IL or missing references)
		//IL_024d: Unknown result type (might be due to invalid IL or missing references)
		//IL_026a: Unknown result type (might be due to invalid IL or missing references)
		double b = Math.Clamp(Math.Min(part.Size.X, Math.Min(part.Size.Y, part.Size.Z)) * 0.1, 0.02, 0.08);
		MaterialGroup materialGroup = new();
		materialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(part.Color)));
		materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(40, 40, 40)), 8.0));
		GeometryModel3D geometryModel3D = new()
		{
			Geometry = BuildRoundedBox(part.Size, b),
			Material = materialGroup,
			BackMaterial = new DiffuseMaterial(new SolidColorBrush(part.Color))
		};
		if (part.Name != "Head")
		{
			return geometryModel3D;
		}
		ImageSource imageSource = FaceTexture();
		if (imageSource == null)
		{
			return geometryModel3D;
		}
		double num = part.Size.X * 0.94;
		double num2 = part.Size.Y * 0.94;
		double z = part.Size.Z / 2.0 + 0.015;
		MeshGeometry3D geometry = new()
		{
			Positions =
			[
				new Point3D((0.0 - num) / 2.0, (0.0 - num2) / 2.0, z),
				new Point3D(num / 2.0, (0.0 - num2) / 2.0, z),
				new Point3D(num / 2.0, num2 / 2.0, z),
				new Point3D((0.0 - num) / 2.0, num2 / 2.0, z)
			],
			TextureCoordinates =
			[
				new Point(0.0, 1.0),
				new Point(1.0, 1.0),
				new Point(1.0, 0.0),
				new Point(0.0, 0.0)
			],
			Normals =
			[
				new Vector3D(0.0, 0.0, 1.0),
				new Vector3D(0.0, 0.0, 1.0),
				new Vector3D(0.0, 0.0, 1.0),
				new Vector3D(0.0, 0.0, 1.0)
			],
			TriangleIndices = [0, 1, 2, 0, 2, 3]
		};
		GeometryModel3D value = new()
		{
			Geometry = geometry,
			Material = new DiffuseMaterial(new ImageBrush(imageSource)
			{
				Stretch = Stretch.Fill
			})
		};
		return new Model3DGroup
		{
			Children = 
			{
				(Model3D)geometryModel3D,
				(Model3D)value
			}
		};
	}

	public static MeshGeometry3D BuildRoundedBox(Vector3D size, double b)
	{
		double num = size.X / 2.0;
		double num2 = size.Y / 2.0;
		double num3 = size.Z / 2.0;
		b = Math.Min(b, Math.Min(num, Math.Min(num2, num3)) * 0.9);
		double num4 = num - b;
		double num5 = num2 - b;
		double num6 = num3 - b;
		Point3DCollection positions = [];
		Vector3DCollection normals = [];
		Int32Collection indices = [];
		Quad(new Point3D(num, 0.0 - num5, 0.0 - num6), new Point3D(num, 0.0 - num5, num6), new Point3D(num, num5, num6), new Point3D(num, num5, 0.0 - num6), new Vector3D(1.0, 0.0, 0.0));
		Quad(new Point3D(0.0 - num, 0.0 - num5, 0.0 - num6), new Point3D(0.0 - num, num5, 0.0 - num6), new Point3D(0.0 - num, num5, num6), new Point3D(0.0 - num, 0.0 - num5, num6), new Vector3D(-1.0, 0.0, 0.0));
		Quad(new Point3D(0.0 - num4, num2, 0.0 - num6), new Point3D(0.0 - num4, num2, num6), new Point3D(num4, num2, num6), new Point3D(num4, num2, 0.0 - num6), new Vector3D(0.0, 1.0, 0.0));
		Quad(new Point3D(0.0 - num4, 0.0 - num2, 0.0 - num6), new Point3D(num4, 0.0 - num2, 0.0 - num6), new Point3D(num4, 0.0 - num2, num6), new Point3D(0.0 - num4, 0.0 - num2, num6), new Vector3D(0.0, -1.0, 0.0));
		Quad(new Point3D(0.0 - num4, 0.0 - num5, num3), new Point3D(num4, 0.0 - num5, num3), new Point3D(num4, num5, num3), new Point3D(0.0 - num4, num5, num3), new Vector3D(0.0, 0.0, 1.0));
		Quad(new Point3D(0.0 - num4, 0.0 - num5, 0.0 - num3), new Point3D(0.0 - num4, num5, 0.0 - num3), new Point3D(num4, num5, 0.0 - num3), new Point3D(num4, 0.0 - num5, 0.0 - num3), new Vector3D(0.0, 0.0, -1.0));
		Quad(new Point3D(num, num5, 0.0 - num6), new Point3D(num, num5, num6), new Point3D(num4, num2, num6), new Point3D(num4, num2, 0.0 - num6), new Vector3D(1.0, 1.0, 0.0));
		Quad(new Point3D(num, 0.0 - num5, num6), new Point3D(num, 0.0 - num5, 0.0 - num6), new Point3D(num4, 0.0 - num2, 0.0 - num6), new Point3D(num4, 0.0 - num2, num6), new Vector3D(1.0, -1.0, 0.0));
		Quad(new Point3D(0.0 - num, num5, num6), new Point3D(0.0 - num, num5, 0.0 - num6), new Point3D(0.0 - num4, num2, 0.0 - num6), new Point3D(0.0 - num4, num2, num6), new Vector3D(-1.0, 1.0, 0.0));
		Quad(new Point3D(0.0 - num, 0.0 - num5, 0.0 - num6), new Point3D(0.0 - num, 0.0 - num5, num6), new Point3D(0.0 - num4, 0.0 - num2, num6), new Point3D(0.0 - num4, 0.0 - num2, 0.0 - num6), new Vector3D(-1.0, -1.0, 0.0));
		Quad(new Point3D(num, 0.0 - num5, num6), new Point3D(num, num5, num6), new Point3D(num4, num5, num3), new Point3D(num4, 0.0 - num5, num3), new Vector3D(1.0, 0.0, 1.0));
		Quad(new Point3D(num, num5, 0.0 - num6), new Point3D(num, 0.0 - num5, 0.0 - num6), new Point3D(num4, 0.0 - num5, 0.0 - num3), new Point3D(num4, num5, 0.0 - num3), new Vector3D(1.0, 0.0, -1.0));
		Quad(new Point3D(0.0 - num, num5, num6), new Point3D(0.0 - num, 0.0 - num5, num6), new Point3D(0.0 - num4, 0.0 - num5, num3), new Point3D(0.0 - num4, num5, num3), new Vector3D(-1.0, 0.0, 1.0));
		Quad(new Point3D(0.0 - num, 0.0 - num5, 0.0 - num6), new Point3D(0.0 - num, num5, 0.0 - num6), new Point3D(0.0 - num4, num5, 0.0 - num3), new Point3D(0.0 - num4, 0.0 - num5, 0.0 - num3), new Vector3D(-1.0, 0.0, -1.0));
		Quad(new Point3D(0.0 - num4, num2, num6), new Point3D(num4, num2, num6), new Point3D(num4, num5, num3), new Point3D(0.0 - num4, num5, num3), new Vector3D(0.0, 1.0, 1.0));
		Quad(new Point3D(num4, num2, 0.0 - num6), new Point3D(0.0 - num4, num2, 0.0 - num6), new Point3D(0.0 - num4, num5, 0.0 - num3), new Point3D(num4, num5, 0.0 - num3), new Vector3D(0.0, 1.0, -1.0));
		Quad(new Point3D(num4, 0.0 - num2, num6), new Point3D(0.0 - num4, 0.0 - num2, num6), new Point3D(0.0 - num4, 0.0 - num5, num3), new Point3D(num4, 0.0 - num5, num3), new Vector3D(0.0, -1.0, 1.0));
		Quad(new Point3D(0.0 - num4, 0.0 - num2, 0.0 - num6), new Point3D(num4, 0.0 - num2, 0.0 - num6), new Point3D(num4, 0.0 - num5, 0.0 - num3), new Point3D(0.0 - num4, 0.0 - num5, 0.0 - num3), new Vector3D(0.0, -1.0, -1.0));
		Tri(new Point3D(num, num5, num6), new Point3D(num4, num2, num6), new Point3D(num4, num5, num3), new Vector3D(1.0, 1.0, 1.0));
		Tri(new Point3D(num, num5, 0.0 - num6), new Point3D(num4, num5, 0.0 - num3), new Point3D(num4, num2, 0.0 - num6), new Vector3D(1.0, 1.0, -1.0));
		Tri(new Point3D(num, 0.0 - num5, num6), new Point3D(num4, 0.0 - num5, num3), new Point3D(num4, 0.0 - num2, num6), new Vector3D(1.0, -1.0, 1.0));
		Tri(new Point3D(num, 0.0 - num5, 0.0 - num6), new Point3D(num4, 0.0 - num2, 0.0 - num6), new Point3D(num4, 0.0 - num5, 0.0 - num3), new Vector3D(1.0, -1.0, -1.0));
		Tri(new Point3D(0.0 - num, num5, num6), new Point3D(0.0 - num4, num5, num3), new Point3D(0.0 - num4, num2, num6), new Vector3D(-1.0, 1.0, 1.0));
		Tri(new Point3D(0.0 - num, num5, 0.0 - num6), new Point3D(0.0 - num4, num2, 0.0 - num6), new Point3D(0.0 - num4, num5, 0.0 - num3), new Vector3D(-1.0, 1.0, -1.0));
		Tri(new Point3D(0.0 - num, 0.0 - num5, num6), new Point3D(0.0 - num4, 0.0 - num2, num6), new Point3D(0.0 - num4, 0.0 - num5, num3), new Vector3D(-1.0, -1.0, 1.0));
		Tri(new Point3D(0.0 - num, 0.0 - num5, 0.0 - num6), new Point3D(0.0 - num4, 0.0 - num5, 0.0 - num3), new Point3D(0.0 - num4, 0.0 - num2, 0.0 - num6), new Vector3D(-1.0, -1.0, -1.0));
		return new MeshGeometry3D
		{
			Positions = positions,
			Normals = normals,
			TriangleIndices = indices
		};
		void Quad(Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D n)
		{
			n.Normalize();
			int count = positions.Count;
			positions.Add(p0);
			positions.Add(p1);
			positions.Add(p2);
			positions.Add(p3);
			normals.Add(n);
			normals.Add(n);
			normals.Add(n);
			normals.Add(n);
			indices.Add(count);
			indices.Add(count + 1);
			indices.Add(count + 2);
			indices.Add(count);
			indices.Add(count + 2);
			indices.Add(count + 3);
		}
		void Tri(Point3D p0, Point3D p1, Point3D p2, Vector3D n)
		{
			n.Normalize();
			int count = positions.Count;
			positions.Add(p0);
			positions.Add(p1);
			positions.Add(p2);
			normals.Add(n);
			normals.Add(n);
			normals.Add(n);
			indices.Add(count);
			indices.Add(count + 1);
			indices.Add(count + 2);
		}
	}
}
