using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.MediaFoundation;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Animation;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Controls;

public partial class AssetPreviewPanel : UserControl{
	private const int MaxModelParts = 2000;
	private const int MaxModelMeshes = 128;
	private const long MaxModelMeshBytes = 32L * 1024 * 1024;
	private const int MaxPreviewAudioBytes = 32 * 1024 * 1024;

	private enum Mode
	{
		None,
		Image,
		Mesh,
		Audio,
		Animation,
		Text
	}

	private Mode _mode;

	private double _yaw = 0.2;

	private double _pitch = 0.12;

	private double _distance = 16.0;

	private double _targetX;

	private double _targetY;

	private double _targetZ;

	private bool _dragging;

	private Point _lastPos;

	private double _defaultYaw = 0.2;

	private double _defaultPitch = 0.12;

	private double _defaultDistance = 16.0;

	private double _defaultTargetY;

	private bool _autoRotate;

	private bool _wireframe;

	private bool _gridShown = true;

	private bool _handlersAttached;

	private readonly DispatcherTimer _spinTimer;

	private readonly DispatcherTimer _moveTimer;

	private MeshGeometry3D? _lastMeshGeometry;

	private Point3D _lastMeshCenter;

	private GeometryModel3D? _meshSolidModel;

	private readonly Dictionary<string, Model3D> _rigParts = new Dictionary<string, Model3D>(StringComparer.Ordinal);

	private RigDefinition _rig = RobloxRig.R6();

	private AnimationData? _anim;

	private readonly DispatcherTimer _animTimer;

	private double _animTime;

	private bool _animPlaying;

	private bool _draggingAnim;

	private bool _suppressAnim;

	private readonly DispatcherTimer _audioTimer;

	private bool _audioPlaying;

	private bool _audioLoop;

	private bool _draggingAudio;

	private bool _suppressAudio;

	private IWavePlayer? _waveOut;

	private WaveStream? _waveReader;

	private byte[]? _audioData;

	private string _audioExt = ".ogg";

	private Model3D? _gridModel;

	private Model3D? _axisModel;

	private CancellationTokenSource? _previewCts;

	private int _previewGeneration;

	public AssetPreviewPanel()
	{
		//IL_0095: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ad: Expected O, but got Unknown
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00df: Expected O, but got Unknown
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0110: Expected O, but got Unknown
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0141: Expected O, but got Unknown
		InitializeComponent();
		_animTimer = new DispatcherTimer((DispatcherPriority)7)
		{
			Interval = TimeSpan.FromMilliseconds(16L)
		};
		_audioTimer = new DispatcherTimer((DispatcherPriority)9)
		{
			Interval = TimeSpan.FromMilliseconds(60L)
		};
		_spinTimer = new DispatcherTimer((DispatcherPriority)7)
		{
			Interval = TimeSpan.FromMilliseconds(16L)
		};
		_moveTimer = new DispatcherTimer((DispatcherPriority)5)
		{
			Interval = TimeSpan.FromMilliseconds(16L)
		};
	}

	private void Panel_Loaded(object sender, RoutedEventArgs e)
	{
		if (_handlersAttached)
			return;
		_handlersAttached = true;
		_animTimer.Tick += AnimTimer_Tick;
		_audioTimer.Tick += AudioTimer_Tick;
		_spinTimer.Tick += SpinTimer_Tick;
		_moveTimer.Tick += MoveTimer_Tick;
		ContentHost.KeyDown += Host_KeyDown;
		ContentHost.MouseEnter += Host_MouseEnter;
	}

	private void Panel_Unloaded(object sender, RoutedEventArgs e)
	{
		ClearPreview();
		if (!_handlersAttached)
			return;
		_handlersAttached = false;
		_animTimer.Tick -= AnimTimer_Tick;
		_audioTimer.Tick -= AudioTimer_Tick;
		_spinTimer.Tick -= SpinTimer_Tick;
		_moveTimer.Tick -= MoveTimer_Tick;
		ContentHost.KeyDown -= Host_KeyDown;
		ContentHost.MouseEnter -= Host_MouseEnter;
	}

	private void Host_MouseEnter(object sender, MouseEventArgs e)
	{
		if (_mode == Mode.Mesh || _mode == Mode.Animation)
			Keyboard.Focus(ContentHost);
	}

	private void SpinTimer_Tick(object? sender, EventArgs e)
	{
		_yaw += 0.012;
		UpdateCamera();
	}

	public void Preview(string title, byte[] data)
	{
		ClearPreview();
		TitleRun.Text = title ?? "";
		if (data == null || data.Length == 0)
		{
			ShowPlaceholder("Nothing to preview.");
			return;
		}
		AssetTypeInfo assetTypeInfo = AssetTypeDetector.Detect(data);
		if (assetTypeInfo.IsImage)
		{
			PreviewImage(data);
		}
		else if (assetTypeInfo.IsMesh)
		{
			PreviewMesh(data);
		}
		else if (assetTypeInfo.Category == "Audio")
		{
			if (data.Length <= MaxPreviewAudioBytes)
				PreviewAudio(data, assetTypeInfo.Extension);
			else
				ShowPlaceholder("This audio is too large to preview.");
		}
		else if (assetTypeInfo.Category == "Model")
		{
			AnimationData animationData = RobloxAnimationParser.Parse(data);
			if (animationData != null && animationData.Keyframes.Count > 0)
			{
				PreviewAnimation(animationData);
			}
			else
			{
				ShowPlaceholder("3D model or CSG (no inline preview yet).");
			}
		}
		else if (assetTypeInfo.Category == "Data")
		{
			PreviewText(data);
		}
		else
		{
			ShowPlaceholder("No preview available for this type.");
		}
	}

	public void ClearPreview()
	{
		_previewGeneration++;
		CancellationTokenSource? previewCts = _previewCts;
		_previewCts = null;
		if (previewCts != null)
		{
			try
			{
				previewCts.Cancel();
			}
			catch (ObjectDisposedException)
			{
			}
			previewCts.Dispose();
		}
		_animTimer.Stop();
		_audioTimer.Stop();
		_spinTimer.Stop();
		_moveTimer.Stop();
		DisposeAudio();
		_audioData = null;
		_mode = Mode.None;
		_anim = null;
		_rigParts.Clear();
		_lastMeshGeometry = null;
		_meshSolidModel = null;
		_gridModel = null;
		_axisModel = null;
		_targetX = 0.0;
		_targetZ = 0.0;
		_autoRotate = false;
		_wireframe = false;
		if (AutoRotateItem != null)
		{
			AutoRotateItem.IsChecked = false;
		}
		if (WireframeItem != null)
		{
			WireframeItem.IsChecked = false;
		}
		Placeholder.Visibility = Visibility.Collapsed;
		ImageView.Visibility = Visibility.Collapsed;
		ImageView.Source = null;
		Viewport.Visibility = Visibility.Collapsed;
		MeshVisual.Content = null;
		RigVisual.Content = null;
		WireVisual.Content = null;
		GridVisual.Content = null;
		AxisVisual.Content = null;
		TextView.Visibility = Visibility.Collapsed;
		TextView.Text = string.Empty;
		AudioView.Visibility = Visibility.Collapsed;
		MeshInfoText.Visibility = Visibility.Collapsed;
		AnimBar.Visibility = Visibility.Collapsed;
		MeshControls.Visibility = Visibility.Collapsed;
		HelpButton.Visibility = Visibility.Collapsed;
	}

	public void ShowInfo(string message)
	{
		ClearPreview();
		ShowPlaceholder(message);
	}

	private void ShowPlaceholder(string text)
	{
		Placeholder.Text = text;
		Placeholder.Visibility = Visibility.Visible;
	}

	private void PreviewImage(byte[] data)
	{
		try
		{
			ImageSource imageSource;
			if (KtxDecoder.IsKtx(data))
			{
				imageSource = KtxDecoder.DecodeToBitmap(data);
				if (imageSource == null)
				{
					ShowPlaceholder("Couldn't decode this KTX texture (unsupported GPU format).");
					return;
				}
			}
			else
			{
				using MemoryStream streamSource = new MemoryStream(data);
				var bitmapImage = Voidstrap.Utility.SafeImaging.FromStream(streamSource);
				if (((Freezable)bitmapImage).CanFreeze)
				{
					((Freezable)bitmapImage).Freeze();
				}
				imageSource = bitmapImage;
			}
			ImageView.Source = imageSource;
			ImageView.Visibility = Visibility.Visible;
			_mode = Mode.Image;
		}
		catch (Exception ex)
		{
			ShowPlaceholder("Couldn't decode image: " + ex.Message);
		}
	}

	private void PreviewText(byte[] data)
	{
		try
		{
			string text = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 200000));
			TextView.Text = text;
			TextView.Visibility = Visibility.Visible;
			_mode = Mode.Text;
		}
		catch
		{
			ShowPlaceholder("Couldn't read text.");
		}
	}

	private void PreviewMesh(byte[] data)
	{
		try
		{
			MeshModel meshModel = MeshParser.Parse(data);
			if (meshModel.Positions.Count == 0 || meshModel.Indices.Count < 3)
			{
				ShowPlaceholder("No geometry in this mesh.");
				return;
			}
			double num = double.MaxValue;
			double num2 = double.MaxValue;
			double num3 = double.MaxValue;
			double num4 = double.MinValue;
			double num5 = double.MinValue;
			double num6 = double.MinValue;
			Point3DCollection point3DCollection = new Point3DCollection(meshModel.Positions.Count);
			foreach (Point3D position in meshModel.Positions)
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
			Int32Collection int32Collection = new Int32Collection(meshModel.Indices.Count);
			foreach (int index in meshModel.Indices)
			{
				int32Collection.Add(index);
			}
			Point3D lastMeshCenter = new Point3D((num + num4) / 2.0, (num2 + num5) / 2.0, (num3 + num6) / 2.0);
			double num7 = Math.Max(num4 - num, Math.Max(num5 - num2, num6 - num3));
			if (num7 <= 0.0 || double.IsNaN(num7) || double.IsInfinity(num7))
			{
				num7 = 1.0;
			}
			MeshGeometry3D meshGeometry3D = new MeshGeometry3D
			{
				Positions = point3DCollection,
				TriangleIndices = int32Collection
			};
			MaterialGroup materialGroup = new MaterialGroup();
			materialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(194, 194, 202))));
			materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(64, 64, 64)), 18.0));
			_meshSolidModel = new GeometryModel3D
			{
				Geometry = meshGeometry3D,
				Material = materialGroup,
				BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(96, 96, 102))),
				Transform = new TranslateTransform3D(0.0 - lastMeshCenter.X, 0.0 - lastMeshCenter.Y, 0.0 - lastMeshCenter.Z)
			};
			MeshVisual.Content = _meshSolidModel;
			_lastMeshGeometry = meshGeometry3D;
			_lastMeshCenter = lastMeshCenter;
			_mode = Mode.Mesh;
			_moveTimer.Start();
			_defaultTargetY = 0.0;
			_defaultDistance = Math.Max(2.0, num7 * 2.2);
			_defaultYaw = 0.5;
			_defaultPitch = 0.18;
			ResetView();
			BuildGrid((0.0 - num7) * 0.55, num7 * 1.5, num7 / 10.0);
			BuildAxis(num7 * 0.6);
			Viewport.Visibility = Visibility.Visible;
			MeshInfoText.Text = $"{meshModel.Positions.Count:N0} verts, {meshModel.FaceCount:N0} faces";
			MeshInfoText.Visibility = Visibility.Visible;
			MeshControls.Visibility = Visibility.Visible;
			HelpButton.Visibility = Visibility.Visible;
		}
		catch (Exception ex)
		{
			ShowPlaceholder("Couldn't parse mesh: " + ex.Message);
		}
	}

	public async Task PreviewModelAsync(string title, byte[] data, Func<string, CancellationToken, Task<byte[]?>> meshFetcher)
	{
		ClearPreview();
		TitleRun.Text = title ?? "";
		List<ModelPart> parts;
		try
		{
			parts = RbxmModelParser.Parse(data);
		}
		catch
		{
			parts = new List<ModelPart>();
		}
		if (parts.Count == 0)
		{
			AnimationData animationData = RobloxAnimationParser.Parse(data);
			if (animationData != null && animationData.Keyframes.Count > 0)
			{
				PreviewAnimation(animationData);
			}
			else
			{
				ShowPlaceholder("This model has no parts to preview.");
			}
			return;
		}
		if (parts.Count > MaxModelParts)
		{
			ShowPlaceholder("This model is too large to preview.");
			return;
		}
		CancellationTokenSource previewCts = new CancellationTokenSource();
		_previewCts = previewCts;
		CancellationToken token = previewCts.Token;
		int previewGeneration = _previewGeneration;
		try
		{
		Dictionary<string, MeshModel?> meshCache = new Dictionary<string, MeshModel>(StringComparer.OrdinalIgnoreCase);
		long meshBytes = 0;
		if (meshFetcher != null)
		{
			foreach (ModelPart part in parts)
			{
				if (token.IsCancellationRequested || previewGeneration != _previewGeneration)
					return;
				if (string.IsNullOrEmpty(part.MeshId) || meshCache.ContainsKey(part.MeshId))
				{
					continue;
				}
				if (meshCache.Count >= MaxModelMeshes)
					break;
				meshCache[part.MeshId] = null;
				try
				{
					byte[] array = await meshFetcher(part.MeshId, token);
					if (token.IsCancellationRequested || previewGeneration != _previewGeneration)
						return;
					if (array != null && array.Length > 0 && array.Length <= 8 * 1024 * 1024 && meshBytes + array.Length <= MaxModelMeshBytes)
					{
						meshBytes += array.Length;
						meshCache[part.MeshId] = MeshParser.Parse(array);
					}
				}
				catch (OperationCanceledException)
				{
					return;
				}
				catch
				{
				}
			}
		}
		if (token.IsCancellationRequested || previewGeneration != _previewGeneration)
			return;
		Model3DGroup model3DGroup = new Model3DGroup();
		double num = double.MaxValue;
		double num2 = double.MaxValue;
		double num3 = double.MaxValue;
		double num4 = double.MinValue;
		double num5 = double.MinValue;
		double num6 = double.MinValue;
		foreach (ModelPart item in parts)
		{
			MeshModel value = null;
			if (!string.IsNullOrEmpty(item.MeshId))
			{
				meshCache.TryGetValue(item.MeshId, out value);
			}
			Geometry3D geometry = ((value != null && value.Positions.Count > 0 && value.Indices.Count >= 3) ? BuildScaledMeshGeometry(value, item.SizeX, item.SizeY, item.SizeZ) : BuildBoxGeometry(item.SizeX, item.SizeY, item.SizeZ));
			MaterialGroup materialGroup = new MaterialGroup();
			materialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(item.R, item.G, item.B))));
			materialGroup.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(48, 48, 48)), 12.0));
			model3DGroup.Children.Add(new GeometryModel3D
			{
				Geometry = geometry,
				Material = materialGroup,
				BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(80, 80, 86))),
				Transform = new MatrixTransform3D(item.CFrame.ToMatrix3D())
			});
			for (int i = -1; i <= 1; i += 2)
			{
				for (int j = -1; j <= 1; j += 2)
				{
					for (int k = -1; k <= 1; k += 2)
					{
						RobloxCFrame robloxCFrame = item.CFrame * RobloxCFrame.FromPosition((double)i * item.SizeX / 2.0, (double)j * item.SizeY / 2.0, (double)k * item.SizeZ / 2.0);
						if (robloxCFrame.X < num)
						{
							num = robloxCFrame.X;
						}
						if (robloxCFrame.X > num4)
						{
							num4 = robloxCFrame.X;
						}
						if (robloxCFrame.Y < num2)
						{
							num2 = robloxCFrame.Y;
						}
						if (robloxCFrame.Y > num5)
						{
							num5 = robloxCFrame.Y;
						}
						if (robloxCFrame.Z < num3)
						{
							num3 = robloxCFrame.Z;
						}
						if (robloxCFrame.Z > num6)
						{
							num6 = robloxCFrame.Z;
						}
					}
				}
			}
		}
		Point3D point3D = new Point3D((num + num4) / 2.0, (num2 + num5) / 2.0, (num3 + num6) / 2.0);
		double num7 = Math.Max(num4 - num, Math.Max(num5 - num2, num6 - num3));
		if (num7 <= 0.0 || double.IsNaN(num7) || double.IsInfinity(num7))
		{
			num7 = 4.0;
		}
		model3DGroup.Transform = new TranslateTransform3D(0.0 - point3D.X, 0.0 - point3D.Y, 0.0 - point3D.Z);
		MeshVisual.Content = model3DGroup;
		_meshSolidModel = null;
		_lastMeshGeometry = null;
		_mode = Mode.Mesh;
		_moveTimer.Start();
		_defaultTargetY = 0.0;
		_defaultDistance = Math.Max(2.0, num7 * 2.2);
		_defaultYaw = 0.6;
		_defaultPitch = 0.22;
		ResetView();
		BuildGrid((0.0 - num7) * 0.55, num7 * 1.5, num7 / 10.0);
		BuildAxis(num7 * 0.6);
		Viewport.Visibility = Visibility.Visible;
		int num8 = 0;
		foreach (MeshModel value2 in meshCache.Values)
		{
			if (value2 != null)
			{
				num8++;
			}
		}
		MeshInfoText.Text = ((num8 > 0) ? $"{parts.Count:N0} parts, {num8:N0} meshes" : $"{parts.Count:N0} parts");
		MeshInfoText.Visibility = Visibility.Visible;
		MeshControls.Visibility = Visibility.Visible;
		HelpButton.Visibility = Visibility.Visible;
		}
		finally
		{
			if (ReferenceEquals(_previewCts, previewCts))
				_previewCts = null;
			previewCts.Dispose();
		}
	}

	private static MeshGeometry3D BuildBoxGeometry(double sx, double sy, double sz)
	{
		double num = Math.Max(0.01, sx / 2.0);
		double num2 = Math.Max(0.01, sy / 2.0);
		double num3 = Math.Max(0.01, sz / 2.0);
		Point3DCollection positions = new Point3DCollection();
		Int32Collection indices = new Int32Collection();
		double x = 0.0 - num;
		double x2 = num;
		double y = 0.0 - num2;
		double y2 = num2;
		double z = 0.0 - num3;
		double z2 = num3;
		Quad(new Point3D(x, y, z2), new Point3D(x2, y, z2), new Point3D(x2, y2, z2), new Point3D(x, y2, z2));
		Quad(new Point3D(x2, y, z), new Point3D(x, y, z), new Point3D(x, y2, z), new Point3D(x2, y2, z));
		Quad(new Point3D(x2, y, z2), new Point3D(x2, y, z), new Point3D(x2, y2, z), new Point3D(x2, y2, z2));
		Quad(new Point3D(x, y, z), new Point3D(x, y, z2), new Point3D(x, y2, z2), new Point3D(x, y2, z));
		Quad(new Point3D(x, y2, z2), new Point3D(x2, y2, z2), new Point3D(x2, y2, z), new Point3D(x, y2, z));
		Quad(new Point3D(x, y, z), new Point3D(x2, y, z), new Point3D(x2, y, z2), new Point3D(x, y, z2));
		return new MeshGeometry3D
		{
			Positions = positions,
			TriangleIndices = indices
		};
		void Quad(Point3D a, Point3D b, Point3D c, Point3D d)
		{
			int count = positions.Count;
			positions.Add(a);
			positions.Add(b);
			positions.Add(c);
			positions.Add(d);
			indices.Add(count);
			indices.Add(count + 1);
			indices.Add(count + 2);
			indices.Add(count);
			indices.Add(count + 2);
			indices.Add(count + 3);
		}
	}

	private static MeshGeometry3D BuildScaledMeshGeometry(MeshModel mm, double sx, double sy, double sz)
	{
		double num = double.MaxValue;
		double num2 = double.MaxValue;
		double num3 = double.MaxValue;
		double num4 = double.MinValue;
		double num5 = double.MinValue;
		double num6 = double.MinValue;
		foreach (Point3D position in mm.Positions)
		{
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
		double num7 = (num + num4) / 2.0;
		double num8 = (num2 + num5) / 2.0;
		double num9 = (num3 + num6) / 2.0;
		double num10 = Math.Max(0.0001, num4 - num);
		double num11 = Math.Max(0.0001, num5 - num2);
		double num12 = Math.Max(0.0001, num6 - num3);
		double num13 = sx / num10;
		double num14 = sy / num11;
		double num15 = sz / num12;
		Point3DCollection point3DCollection = new Point3DCollection(mm.Positions.Count);
		foreach (Point3D position2 in mm.Positions)
		{
			point3DCollection.Add(new Point3D((position2.X - num7) * num13, (position2.Y - num8) * num14, (position2.Z - num9) * num15));
		}
		Int32Collection int32Collection = new Int32Collection(mm.Indices.Count);
		foreach (int index in mm.Indices)
		{
			int32Collection.Add(index);
		}
		return new MeshGeometry3D
		{
			Positions = point3DCollection,
			TriangleIndices = int32Collection
		};
	}

	private void PreviewAnimation(AnimationData anim)
	{
		_anim = anim;
		_rig = (anim.IsR15 ? RobloxRig.R15() : RobloxRig.R6());
		TitleRun.Text = TitleRun.Text + "   (" + (anim.IsR15 ? "R15" : "R6") + ")";
		BuildRig();
		_mode = Mode.Animation;
		_moveTimer.Start();
		_defaultTargetY = -0.5;
		_defaultDistance = 15.0;
		_defaultYaw = 0.2;
		_defaultPitch = 0.1;
		ResetView();
		BuildGrid(-3.2, 7.0, 0.5);
		BuildAxis(1.6);
		Viewport.Visibility = Visibility.Visible;
		AnimBar.Visibility = Visibility.Visible;
		MeshControls.Visibility = Visibility.Visible;
		HelpButton.Visibility = Visibility.Visible;
		_animTime = 0.0;
		_animPlaying = true;
		AnimPlayButton.Icon = SymbolRegular.Pause24;
		UpdatePose(0.0);
		_animTimer.Start();
	}

	private void BuildRig()
	{
		Model3DGroup model3DGroup = new Model3DGroup();
		_rigParts.Clear();
		foreach (RigPart part in _rig.Parts)
		{
			if (!(part.Name == _rig.RootPart))
			{
				Model3D value = RigGeometry.BuildPart(part);
				_rigParts[part.Name] = value;
				model3DGroup.Children.Add(value);
			}
		}
		RigVisual.Content = model3DGroup;
	}

	private Dictionary<string, RobloxCFrame> ComputePoses(double t)
	{
		Dictionary<string, RobloxCFrame> dictionary = new Dictionary<string, RobloxCFrame>(StringComparer.Ordinal);
		if (_anim == null || _anim.Keyframes.Count == 0)
		{
			return dictionary;
		}
		List<AnimKeyframe> keyframes = _anim.Keyframes;
		if (keyframes.Count == 1)
		{
			return keyframes[0].Poses;
		}
		int num = 0;
		for (int i = 0; i < keyframes.Count && keyframes[i].Time <= t; i++)
		{
			num = i;
		}
		if (num >= keyframes.Count - 1)
		{
			return keyframes[keyframes.Count - 1].Poses;
		}
		AnimKeyframe animKeyframe = keyframes[num];
		AnimKeyframe animKeyframe2 = keyframes[num + 1];
		double num2 = animKeyframe2.Time - animKeyframe.Time;
		double t2 = ((num2 > 0.0) ? Math.Clamp((t - animKeyframe.Time) / num2, 0.0, 1.0) : 0.0);
		foreach (string item in animKeyframe.Poses.Keys.Union(animKeyframe2.Poses.Keys))
		{
			RobloxCFrame value;
			RobloxCFrame a = (animKeyframe.Poses.TryGetValue(item, out value) ? value : RobloxCFrame.Identity);
			RobloxCFrame value2;
			RobloxCFrame b = (animKeyframe2.Poses.TryGetValue(item, out value2) ? value2 : RobloxCFrame.Identity);
			dictionary[item] = RobloxCFrame.Lerp(a, b, t2);
		}
		return dictionary;
	}

	private void UpdatePose(double t)
	{
		Dictionary<string, RobloxCFrame> dictionary = ComputePoses(t);
		RobloxCFrame value;
		Dictionary<string, RobloxCFrame> dictionary2 = new Dictionary<string, RobloxCFrame>(StringComparer.Ordinal) { [_rig.RootPart] = (dictionary.TryGetValue(_rig.RootPart, out value) ? value : RobloxCFrame.Identity) };
		foreach (RigJoint joint in _rig.Joints)
		{
			if (dictionary2.TryGetValue(joint.Part0, out var value2))
			{
				RobloxCFrame value3;
				RobloxCFrame robloxCFrame = (dictionary.TryGetValue(joint.Part1, out value3) ? value3 : RobloxCFrame.Identity);
				dictionary2[joint.Part1] = value2 * joint.C0 * robloxCFrame * joint.C1.Inverse();
			}
		}
		foreach (KeyValuePair<string, Model3D> rigPart in _rigParts)
		{
			if (dictionary2.TryGetValue(rigPart.Key, out var value4))
			{
				rigPart.Value.Transform = new MatrixTransform3D(value4.ToMatrix3D());
			}
		}
		double num = _anim?.Length ?? 0.0;
		AnimTimeText.Text = $"{t:0.00}s / {num:0.00}s";
		if (!_draggingAnim && num > 0.0)
		{
			_suppressAnim = true;
			AnimSlider.Value = Math.Clamp(t / num, 0.0, 1.0);
			_suppressAnim = false;
		}
	}

	private void AnimTimer_Tick(object? sender, EventArgs e)
	{
		if (_animPlaying && _anim != null && !(_anim.Length <= 0.0))
		{
			_animTime += 0.016;
			if (_animTime >= _anim.Length)
			{
				_animTime %= _anim.Length;
			}
			UpdatePose(_animTime);
		}
	}

	private void AnimPlay_Click(object sender, RoutedEventArgs e)
	{
		if (_anim != null && !(_anim.Length <= 0.0))
		{
			_animPlaying = !_animPlaying;
			AnimPlayButton.Icon = (_animPlaying ? SymbolRegular.Pause24 : SymbolRegular.Play24);
		}
	}

	private void AnimSeek_DragStarted(object sender, DragStartedEventArgs e)
	{
		_draggingAnim = true;
	}

	private void AnimSeek_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		_draggingAnim = false;
	}

	private void AnimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (!_suppressAnim && _anim != null && !(_anim.Length <= 0.0) && _draggingAnim)
		{
			_animTime = e.NewValue * _anim.Length;
			UpdatePose(_animTime);
		}
	}

	private void PreviewAudio(byte[] data, string ext)
	{
		try
		{
			DisposeAudio();
			_audioData = data;
			_audioExt = (string.IsNullOrEmpty(ext) ? ".ogg" : ext);
			_waveReader = CreateWaveReader(data, ext);
			_waveOut = new WaveOutEvent();
			_waveOut.Init(_waveReader);
			_waveOut.Volume = (float)Math.Clamp(VolumeSlider.Value, 0.0, 1.0);
			_waveOut.PlaybackStopped += WaveOut_PlaybackStopped;
			_waveOut.Play();
			_audioPlaying = true;
			AudioPlayButton.Icon = SymbolRegular.Pause24;
			AudioPositionSlider.Maximum = Math.Max(0.001, _waveReader.TotalTime.TotalSeconds);
			UpdateAudioTime();
			AudioView.Visibility = Visibility.Visible;
			_mode = Mode.Audio;
			_audioTimer.Start();
		}
		catch (Exception ex)
		{
			DisposeAudio();
			ShowPlaceholder("Couldn't play audio: " + ex.Message);
		}
	}

	private void AudioDownload_Click(object sender, RoutedEventArgs e)
	{
		if (_audioData == null || _audioData.Length == 0)
		{
			return;
		}
		string text = (AudioFormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Original";
		string text2;
		string text3;
		switch (text)
		{
		case "WAV":
			text2 = ".wav";
			text3 = "WAV audio|*.wav";
			break;
		case "MP3":
			text2 = ".mp3";
			text3 = "MP3 audio|*.mp3";
			break;
		case "AAC (M4A)":
			text2 = ".m4a";
			text3 = "AAC audio|*.m4a";
			break;
		case "WMA":
			text2 = ".wma";
			text3 = "WMA audio|*.wma";
			break;
		default:
			text2 = (string.IsNullOrEmpty(_audioExt) ? ".ogg" : _audioExt);
			text3 = "Audio|*" + text2;
			break;
		}
		SaveFileDialog saveFileDialog = new SaveFileDialog
		{
			Filter = text3 + "|All files|*.*",
			FileName = "audio" + text2
		};
		if (saveFileDialog.ShowDialog() != true)
		{
			return;
		}
		try
		{
			if (text == "Original")
			{
				File.WriteAllBytes(saveFileDialog.FileName, _audioData);
			}
			else
			{
				ConvertAudio(_audioData, _audioExt, text, saveFileDialog.FileName);
			}
		}
		catch (Exception ex)
		{
			Frontend.ShowMessageBox("Couldn't save audio: " + ex.Message, MessageBoxImage.Exclamation);
		}
	}

	private static void ConvertAudio(byte[] data, string ext, string fmt, string path)
	{
		using WaveStream waveProvider = CreateWaveReader(data, ext);
		SampleToWaveProvider16 sampleToWaveProvider = new SampleToWaveProvider16(waveProvider.ToSampleProvider());
		if (fmt == "WAV")
		{
			WaveFileWriter.CreateWaveFile(path, sampleToWaveProvider);
			return;
		}
		MediaFoundationApi.Startup();
		switch (fmt)
		{
		case "MP3":
			MediaFoundationEncoder.EncodeToMp3(sampleToWaveProvider, path);
			break;
		case "AAC (M4A)":
			MediaFoundationEncoder.EncodeToAac(sampleToWaveProvider, path);
			break;
		case "WMA":
			MediaFoundationEncoder.EncodeToWma(sampleToWaveProvider, path);
			break;
		}
	}

	private static WaveStream CreateWaveReader(byte[] data, string ext)
	{
		MemoryStream memoryStream = new MemoryStream(data, writable: false);
		bool num = data.Length > 4 && data[0] == 79 && data[1] == 103 && data[2] == 103 && data[3] == 83;
		bool flag = data.Length > 12 && data[0] == 82 && data[1] == 73 && data[2] == 70 && data[3] == 70 && data[8] == 87 && data[9] == 65 && data[10] == 86 && data[11] == 69;
		bool flag2 = data.Length > 3 && ((data[0] == 73 && data[1] == 68 && data[2] == 51) || (data[0] == byte.MaxValue && (data[1] & 0xE0) == 224));
		if (num || string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase))
		{
			return new VorbisWaveReader(memoryStream, closeOnDispose: true);
		}
		if (flag || string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
		{
			return new WaveFileReader(memoryStream);
		}
		if (flag2 || string.Equals(ext, ".mp3", StringComparison.OrdinalIgnoreCase))
		{
			return new Mp3FileReader(memoryStream);
		}
		return new StreamMediaFoundationReader(memoryStream);
	}

	private void WaveOut_PlaybackStopped(object? sender, StoppedEventArgs e)
	{
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => HandlePlaybackStopped(sender)));
			return;
		}
		HandlePlaybackStopped(sender);
	}

	private void HandlePlaybackStopped(object? sender)
	{
		if (!ReferenceEquals(sender, _waveOut))
			return;
		if (_waveReader == null)
		{
			return;
		}
		bool flag = _waveReader.CurrentTime >= _waveReader.TotalTime - TimeSpan.FromMilliseconds(150L);
		if (_audioLoop && flag)
		{
			try
			{
				_waveReader.CurrentTime = TimeSpan.Zero;
				_waveOut?.Play();
				return;
			}
			catch
			{
			}
		}
		if (flag)
		{
			_audioPlaying = false;
			AudioPlayButton.Icon = SymbolRegular.Play24;
		}
	}

	private void DisposeAudio()
	{
		try
		{
			if (_waveOut != null)
			{
				_waveOut.PlaybackStopped -= WaveOut_PlaybackStopped;
			}
		}
		catch
		{
		}
		try
		{
			_waveOut?.Stop();
		}
		catch
		{
		}
		try
		{
			_waveOut?.Dispose();
		}
		catch
		{
		}
		try
		{
			_waveReader?.Dispose();
		}
		catch
		{
		}
		_waveOut = null;
		_waveReader = null;
		_audioPlaying = false;
	}

	private void AudioTimer_Tick(object? sender, EventArgs e)
	{
		if (!_draggingAudio && _waveReader != null)
		{
			_suppressAudio = true;
			AudioPositionSlider.Value = Math.Min(_waveReader.CurrentTime.TotalSeconds, AudioPositionSlider.Maximum);
			_suppressAudio = false;
			UpdateAudioTime();
		}
	}

	private void UpdateAudioTime()
	{
		TimeSpan t = _waveReader?.CurrentTime ?? TimeSpan.Zero;
		TimeSpan t2 = _waveReader?.TotalTime ?? TimeSpan.Zero;
		AudioTimeText.Text = Fmt(t) + " / " + Fmt(t2);
	}

	private static string Fmt(TimeSpan t)
	{
		return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds:000}";
	}

	private void AudioPlay_Click(object sender, RoutedEventArgs e)
	{
		if (_waveOut == null)
		{
			return;
		}
		if (_audioPlaying)
		{
			_waveOut.Pause();
			_audioPlaying = false;
			AudioPlayButton.Icon = SymbolRegular.Play24;
			return;
		}
		if (_waveReader != null && _waveReader.CurrentTime >= _waveReader.TotalTime - TimeSpan.FromMilliseconds(150L))
		{
			_waveReader.CurrentTime = TimeSpan.Zero;
		}
		_waveOut.Play();
		_audioPlaying = true;
		AudioPlayButton.Icon = SymbolRegular.Pause24;
	}

	private void AudioLoop_Click(object sender, RoutedEventArgs e)
	{
		_audioLoop = !_audioLoop;
		AudioLoopButton.Appearance = ((!_audioLoop) ? ControlAppearance.Secondary : ControlAppearance.Primary);
	}

	private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		try
		{
			if (_waveOut != null)
			{
				_waveOut.Volume = (float)Math.Clamp(e.NewValue, 0.0, 1.0);
			}
		}
		catch
		{
		}
	}

	private void AudioSeek_DragStarted(object sender, DragStartedEventArgs e)
	{
		_draggingAudio = true;
	}

	private void AudioSeek_DragCompleted(object sender, DragCompletedEventArgs e)
	{
		_draggingAudio = false;
		try
		{
			if (_waveReader != null)
			{
				_waveReader.CurrentTime = TimeSpan.FromSeconds(AudioPositionSlider.Value);
			}
		}
		catch
		{
		}
	}

	private void UpdateCamera()
	{
		double num = _targetX + _distance * Math.Cos(_pitch) * Math.Sin(_yaw);
		double num2 = _targetY + _distance * Math.Sin(_pitch);
		double num3 = _targetZ + _distance * Math.Cos(_pitch) * Math.Cos(_yaw);
		Camera.Position = new Point3D(num, num2, num3);
		Camera.LookDirection = new Vector3D(_targetX - num, _targetY - num2, _targetZ - num3);
		Camera.UpDirection = new Vector3D(0.0, 1.0, 0.0);
	}

	private void ResetView()
	{
		_yaw = _defaultYaw;
		_pitch = _defaultPitch;
		_distance = _defaultDistance;
		_targetX = 0.0;
		_targetY = _defaultTargetY;
		_targetZ = 0.0;
		UpdateCamera();
	}

	private void ResetView_Click(object sender, RoutedEventArgs e)
	{
		ResetView();
	}

	private void Options_Click(object sender, RoutedEventArgs e)
	{
		if (OptionsButton.ContextMenu != null)
		{
			OptionsButton.ContextMenu.PlacementTarget = OptionsButton;
			OptionsButton.ContextMenu.IsOpen = true;
		}
	}

	private void GridToggle_Click(object sender, RoutedEventArgs e)
	{
		_gridShown = GridItem.IsChecked;
		ApplyGridVisibility();
	}

	private void ApplyGridVisibility()
	{
		GridVisual.Content = (_gridShown ? _gridModel : null);
		AxisVisual.Content = (_gridShown ? _axisModel : null);
	}

	private void AutoRotate_Click(object sender, RoutedEventArgs e)
	{
		_autoRotate = AutoRotateItem.IsChecked;
		if (_autoRotate)
		{
			_spinTimer.Start();
		}
		else
		{
			_spinTimer.Stop();
		}
	}

	private void Wireframe_Click(object sender, RoutedEventArgs e)
	{
		_wireframe = WireframeItem.IsChecked;
		ApplyWireframe();
	}

	private void Help_Click(object sender, RoutedEventArgs e)
	{
		Frontend.ShowMessageBox("Camera Controls\n\nOrbit Mode (default)\n  Click + Drag  -  Rotate the model\n  Scroll Wheel  -  Zoom in / out\n\nFPS Mode (press W/A/S/D to enter)\n  W / A / S / D  -  Move forward / left / back / right\n  Space / Shift  -  Move up / down\n  Click + Drag   -  Look around\n  Scroll Wheel   -  Move in / out\n  Hold Q / E     -  Move slower / faster", MessageBoxImage.Asterisk);
	}

	private void Host_KeyDown(object sender, KeyEventArgs e)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_001d: Invalid comparison between Unknown and I4
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Invalid comparison between Unknown and I4
		//IL_001f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Invalid comparison between Unknown and I4
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Invalid comparison between Unknown and I4
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Invalid comparison between Unknown and I4
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0027: Invalid comparison between Unknown and I4
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Invalid comparison between Unknown and I4
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_003e: Invalid comparison between Unknown and I4
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Invalid comparison between Unknown and I4
		if (_mode != Mode.Mesh && _mode != Mode.Animation)
		{
			return;
		}
		Key key = e.Key;
		if ((int)key <= 48)
		{
			if ((int)key != 18 && (int)key != 44 && (int)key - 47 > 1)
			{
				return;
			}
		}
		else if ((int)key <= 62)
		{
			if ((int)key != 60 && (int)key != 62)
			{
				return;
			}
		}
		else if ((int)key != 66 && (int)key - 116 > 1)
		{
			return;
		}
		e.Handled = true;
	}

	private void MoveTimer_Tick(object? sender, EventArgs e)
	{
		if ((_mode == Mode.Mesh || _mode == Mode.Animation) && (ContentHost.IsMouseOver || ContentHost.IsKeyboardFocusWithin))
		{
			double num = _distance * 0.035;
			if (Keyboard.IsKeyDown((Key)48))
			{
				num *= 2.4;
			}
			if (Keyboard.IsKeyDown((Key)60))
			{
				num *= 0.4;
			}
			double num2 = Math.Sin(_yaw);
			double num3 = Math.Cos(_yaw);
			double num4 = Math.Cos(_yaw);
			double num5 = 0.0 - Math.Sin(_yaw);
			double num6 = 0.0;
			double num7 = 0.0;
			double num8 = 0.0;
			if (Keyboard.IsKeyDown((Key)66))
			{
				num6 -= num2 * num;
				num8 -= num3 * num;
			}
			if (Keyboard.IsKeyDown((Key)62))
			{
				num6 += num2 * num;
				num8 += num3 * num;
			}
			if (Keyboard.IsKeyDown((Key)44))
			{
				num6 -= num4 * num;
				num8 -= num5 * num;
			}
			if (Keyboard.IsKeyDown((Key)47))
			{
				num6 += num4 * num;
				num8 += num5 * num;
			}
			if (Keyboard.IsKeyDown((Key)18))
			{
				num7 += num;
			}
			if (Keyboard.IsKeyDown((Key)116) || Keyboard.IsKeyDown((Key)117))
			{
				num7 -= num;
			}
			if (num6 != 0.0 || num7 != 0.0 || num8 != 0.0)
			{
				_targetX += num6;
				_targetY += num7;
				_targetZ += num8;
				UpdateCamera();
			}
		}
	}

	private void Host_MouseDown(object sender, MouseButtonEventArgs e)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		ContentHost.Focus();
		if (_mode == Mode.Mesh || _mode == Mode.Animation)
		{
			_dragging = true;
			_lastPos = e.GetPosition(ContentHost);
			ContentHost.CaptureMouse();
		}
	}

	private void Host_MouseUp(object sender, MouseButtonEventArgs e)
	{
		_dragging = false;
		ContentHost.ReleaseMouseCapture();
	}

	private void Host_MouseMove(object sender, MouseEventArgs e)
	{
		//IL_0010: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		if (_dragging)
		{
			Point position = e.GetPosition(ContentHost);
			_yaw -= (position.X - _lastPos.X) * 0.01;
			_pitch += (position.Y - _lastPos.Y) * 0.01;
			_pitch = Math.Clamp(_pitch, -1.4, 1.4);
			_lastPos = position;
			UpdateCamera();
		}
	}

	private void Host_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		e.Handled = true;
		if (_mode == Mode.Mesh || _mode == Mode.Animation)
		{
			_distance *= ((e.Delta > 0) ? 0.9 : 1.11);
			_distance = Math.Clamp(_distance, 0.5, 400.0);
			UpdateCamera();
		}
	}

	private void BuildGrid(double y, double extent, double step)
	{
		if (step <= 0.0)
		{
			step = extent / 10.0;
		}
		double num = extent / 2.0;
		double w = step * 0.04;
		Point3DCollection positions = new Point3DCollection();
		Int32Collection indices = new Int32Collection();
		for (double num2 = 0.0 - num; num2 <= num + 1E-06; num2 += step)
		{
			Line(0.0 - num, num2, num, num2);
			Line(num2, 0.0 - num, num2, num);
		}
		MeshGeometry3D geometry = new MeshGeometry3D
		{
			Positions = positions,
			TriangleIndices = indices
		};
		EmissiveMaterial emissiveMaterial = new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(74, 74, 82)));
		_gridModel = new GeometryModel3D
		{
			Geometry = geometry,
			Material = emissiveMaterial,
			BackMaterial = emissiveMaterial
		};
		ApplyGridVisibility();
		void Line(double x0, double z0, double x1, double z1)
		{
			int count = positions.Count;
			double num3 = x1 - x0;
			double num4 = z1 - z0;
			double num5 = Math.Sqrt(num3 * num3 + num4 * num4);
			if (!(num5 <= 0.0))
			{
				double num6 = (0.0 - num4) / num5 * w;
				double num7 = num3 / num5 * w;
				positions.Add(new Point3D(x0 + num6, y, z0 + num7));
				positions.Add(new Point3D(x1 + num6, y, z1 + num7));
				positions.Add(new Point3D(x1 - num6, y, z1 - num7));
				positions.Add(new Point3D(x0 - num6, y, z0 - num7));
				indices.Add(count);
				indices.Add(count + 1);
				indices.Add(count + 2);
				indices.Add(count);
				indices.Add(count + 2);
				indices.Add(count + 3);
			}
		}
	}

	private void BuildAxis(double length)
	{
		Model3DGroup model3DGroup = new Model3DGroup();
		double t = length * 0.03;
		model3DGroup.Children.Add(AxisBar(new Vector3D(length, 0.0, 0.0), Color.FromRgb(224, 64, 64), t));
		model3DGroup.Children.Add(AxisBar(new Vector3D(0.0, length, 0.0), Color.FromRgb(64, 208, 64), t));
		model3DGroup.Children.Add(AxisBar(new Vector3D(0.0, 0.0, length), Color.FromRgb(64, 112, 224), t));
		_axisModel = model3DGroup;
		ApplyGridVisibility();
	}

	private static GeometryModel3D AxisBar(Vector3D axis, Color color, double t)
	{
		double num = Math.Max(Math.Abs(axis.X) / 2.0, t);
		double num2 = Math.Max(Math.Abs(axis.Y) / 2.0, t);
		double num3 = Math.Max(Math.Abs(axis.Z) / 2.0, t);
		Point3D point3D = new Point3D(axis.X / 2.0, axis.Y / 2.0, axis.Z / 2.0);
		Point3DCollection positions = new Point3DCollection();
		Int32Collection indices = new Int32Collection();
		double x = point3D.X - num;
		double x2 = point3D.X + num;
		double y = point3D.Y - num2;
		double y2 = point3D.Y + num2;
		double z = point3D.Z - num3;
		double z2 = point3D.Z + num3;
		Quad(new Point3D(x, y, z2), new Point3D(x2, y, z2), new Point3D(x2, y2, z2), new Point3D(x, y2, z2));
		Quad(new Point3D(x2, y, z), new Point3D(x, y, z), new Point3D(x, y2, z), new Point3D(x2, y2, z));
		Quad(new Point3D(x2, y, z2), new Point3D(x2, y, z), new Point3D(x2, y2, z), new Point3D(x2, y2, z2));
		Quad(new Point3D(x, y, z), new Point3D(x, y, z2), new Point3D(x, y2, z2), new Point3D(x, y2, z));
		Quad(new Point3D(x, y2, z2), new Point3D(x2, y2, z2), new Point3D(x2, y2, z), new Point3D(x, y2, z));
		Quad(new Point3D(x, y, z), new Point3D(x2, y, z), new Point3D(x2, y, z2), new Point3D(x, y, z2));
		EmissiveMaterial emissiveMaterial = new EmissiveMaterial(new SolidColorBrush(color));
		return new GeometryModel3D
		{
			Geometry = new MeshGeometry3D
			{
				Positions = positions,
				TriangleIndices = indices
			},
			Material = emissiveMaterial,
			BackMaterial = emissiveMaterial
		};
		void Quad(Point3D a, Point3D b, Point3D cc, Point3D d)
		{
			int count = positions.Count;
			positions.Add(a);
			positions.Add(b);
			positions.Add(cc);
			positions.Add(d);
			indices.Add(count);
			indices.Add(count + 1);
			indices.Add(count + 2);
			indices.Add(count);
			indices.Add(count + 2);
			indices.Add(count + 3);
		}
	}

	private void ApplyWireframe()
	{
		if (_mode != Mode.Mesh || _lastMeshGeometry == null)
		{
			WireVisual.Content = null;
		}
		else if (_wireframe)
		{
			MeshVisual.Content = null;
			WireVisual.Content = BuildWireframe(_lastMeshGeometry, _lastMeshCenter);
		}
		else
		{
			WireVisual.Content = null;
			MeshVisual.Content = _meshSolidModel;
		}
	}

	private static Model3D BuildWireframe(MeshGeometry3D mesh, Point3D center)
	{
		Point3DCollection positions = new Point3DCollection();
		Int32Collection indices = new Int32Collection();
		Point3DCollection positions2 = mesh.Positions;
		Int32Collection triangleIndices = mesh.TriangleIndices;
		double w = 0.012;
		int num = Math.Min(triangleIndices.Count, 60000);
		for (int i = 0; i + 2 < num; i += 3)
		{
			Point3D point3D = positions2[triangleIndices[i]];
			Point3D point3D2 = positions2[triangleIndices[i + 1]];
			Point3D point3D3 = positions2[triangleIndices[i + 2]];
			Vector3D n = Vector3D.CrossProduct(point3D2 - point3D, point3D3 - point3D);
			if (n.LengthSquared > 0.0)
			{
				n.Normalize();
			}
			else
			{
				n = new Vector3D(0.0, 0.0, 1.0);
			}
			Edge(point3D, point3D2, n);
			Edge(point3D2, point3D3, n);
			Edge(point3D3, point3D, n);
		}
		EmissiveMaterial emissiveMaterial = new EmissiveMaterial(new SolidColorBrush(Color.FromRgb(221, 221, 230)));
		return new GeometryModel3D
		{
			Geometry = new MeshGeometry3D
			{
				Positions = positions,
				TriangleIndices = indices
			},
			Material = emissiveMaterial,
			BackMaterial = emissiveMaterial,
			Transform = new TranslateTransform3D(0.0 - center.X, 0.0 - center.Y, 0.0 - center.Z)
		};
		void Edge(Point3D p0, Point3D p1, Vector3D vector2)
		{
			Vector3D vector = p1 - p0;
			if (!(vector.LengthSquared <= 0.0))
			{
				Vector3D vector3D = Vector3D.CrossProduct(vector2, vector);
				if (vector3D.LengthSquared <= 0.0)
				{
					vector3D = Vector3D.CrossProduct(new Vector3D(0.0, 1.0, 0.0), vector);
				}
				if (!(vector3D.LengthSquared <= 0.0))
				{
					vector3D.Normalize();
					vector3D *= w;
					int count = positions.Count;
					positions.Add(p0 + vector3D);
					positions.Add(p1 + vector3D);
					positions.Add(p1 - vector3D);
					positions.Add(p0 - vector3D);
					indices.Add(count);
					indices.Add(count + 1);
					indices.Add(count + 2);
					indices.Add(count);
					indices.Add(count + 2);
					indices.Add(count + 3);
				}
			}
		}
	}
}
