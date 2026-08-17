using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Voidstrap.Integrations;
using Voidstrap.UI.Elements.Base;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.ContextMenu;

public partial class MeshViewerWindow : WpfUiWindow{
	private readonly QuaternionRotation3D _rotation = new QuaternionRotation3D(Quaternion.Identity);

	private double _yaw;

	private double _pitch;

	private double _distance = 12.0;

	private bool _dragging;

	private Point _lastPos;

	public MeshViewerWindow(byte[] data, string title)
	{
		InitializeComponent();
		if (!string.IsNullOrEmpty(title))
		{
			base.Title = "Mesh Viewer - " + title;
		}
		try
		{
			MeshModel meshModel = MeshParser.Parse(data);
			if (meshModel.Positions.Count == 0 || meshModel.Indices.Count < 3)
			{
				StatusText.Text = "No renderable geometry found in this file.";
				return;
			}
			BuildScene(meshModel);
			StatusText.Text = $"{meshModel.Positions.Count:N0} vertices, {meshModel.FaceCount:N0} faces";
		}
		catch (Exception ex)
		{
			StatusText.Text = "Could not parse mesh: " + ex.Message;
		}
	}

	private void BuildScene(MeshModel model)
	{
		double num = double.MaxValue;
		double num2 = double.MaxValue;
		double num3 = double.MaxValue;
		double num4 = double.MinValue;
		double num5 = double.MinValue;
		double num6 = double.MinValue;
		Point3DCollection point3DCollection = new Point3DCollection(model.Positions.Count);
		foreach (Point3D position in model.Positions)
		{
			point3DCollection.Add(position);
			if (position.X < num)
			{
				num = position.X;
			}
			if (position.X > num4)
			{
				num4 = position.X;
			}
			if (position.Y < num2)
			{
				num2 = position.Y;
			}
			if (position.Y > num5)
			{
				num5 = position.Y;
			}
			if (position.Z < num3)
			{
				num3 = position.Z;
			}
			if (position.Z > num6)
			{
				num6 = position.Z;
			}
		}
		Int32Collection int32Collection = new Int32Collection(model.Indices.Count);
		foreach (int index in model.Indices)
		{
			int32Collection.Add(index);
		}
		MeshGeometry3D geometry = new MeshGeometry3D
		{
			Positions = point3DCollection,
			TriangleIndices = int32Collection
		};
		Point3D point3D = new Point3D((num + num4) / 2.0, (num2 + num5) / 2.0, (num3 + num6) / 2.0);
		double num7 = Math.Max(num4 - num, Math.Max(num5 - num2, num6 - num3));
		if (num7 <= 0.0 || double.IsNaN(num7) || double.IsInfinity(num7))
		{
			num7 = 1.0;
		}
		Transform3DGroup transform3DGroup = new Transform3DGroup();
		transform3DGroup.Children.Add(new TranslateTransform3D(0.0 - point3D.X, 0.0 - point3D.Y, 0.0 - point3D.Z));
		transform3DGroup.Children.Add(new RotateTransform3D(_rotation));
		MaterialGroup materialGroup = new MaterialGroup();
		materialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(191, 191, 198))));
		materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(64, 64, 64)), 20.0));
		GeometryModel3D content = new GeometryModel3D
		{
			Geometry = geometry,
			Material = materialGroup,
			BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(96, 96, 102))),
			Transform = transform3DGroup
		};
		MeshVisual.Content = content;
		_distance = num7 * 2.2;
		if (_distance < 2.0)
		{
			_distance = 2.0;
		}
		UpdateCamera();
	}

	private void UpdateCamera()
	{
		Camera.Position = new Point3D(0.0, 0.0, _distance);
		Camera.LookDirection = new Vector3D(0.0, 0.0, -1.0);
		Camera.UpDirection = new Vector3D(0.0, 1.0, 0.0);
	}

	private void UpdateRotation()
	{
		Quaternion quaternion = new Quaternion(new Vector3D(0.0, 1.0, 0.0), _yaw);
		Quaternion quaternion2 = new Quaternion(new Vector3D(1.0, 0.0, 0.0), _pitch);
		_rotation.Quaternion = quaternion * quaternion2;
	}

	private void ViewportHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		_dragging = true;
		_lastPos = e.GetPosition(ViewportHost);
		ViewportHost.CaptureMouse();
	}

	private void ViewportHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
	{
		_dragging = false;
		ViewportHost.ReleaseMouseCapture();
	}

	private void ViewportHost_MouseMove(object sender, MouseEventArgs e)
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		if (_dragging)
		{
			Point position = e.GetPosition(ViewportHost);
			_yaw += (position.X - _lastPos.X) * 0.5;
			_pitch += (position.Y - _lastPos.Y) * 0.5;
			_pitch = Math.Clamp(_pitch, -89.0, 89.0);
			_lastPos = position;
			UpdateRotation();
		}
	}

	private void ViewportHost_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		_distance *= ((e.Delta > 0) ? 0.88 : 1.13);
		_distance = Math.Clamp(_distance, 0.5, 100000.0);
		UpdateCamera();
	}
}
