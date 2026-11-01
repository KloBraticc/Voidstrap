using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Voidstrap.UI.Elements.Controls
{
    public partial class SkyboxPreviewControl : UserControl
    {
        private const int FaceSegments = 40;

        private const double DefaultYaw = 180.0;

        private const double DefaultPitch = 8.0;

        private const double DefaultDistance = 3.0;

        private const string PlaceholderHex = "#FFB9BDC6";

        private static readonly (Vector3D Major, Vector3D S, Vector3D T)[] FaceAxes =
        {
            (new Vector3D(0, 0, 1), new Vector3D(1, 0, 0), new Vector3D(0, -1, 0)),
            (new Vector3D(0, -1, 0), new Vector3D(1, 0, 0), new Vector3D(0, 0, -1)),
            (new Vector3D(0, 0, -1), new Vector3D(-1, 0, 0), new Vector3D(0, -1, 0)),
            (new Vector3D(-1, 0, 0), new Vector3D(0, 0, 1), new Vector3D(0, -1, 0)),
            (new Vector3D(1, 0, 0), new Vector3D(0, 0, -1), new Vector3D(0, -1, 0)),
            (new Vector3D(0, 1, 0), new Vector3D(1, 0, 0), new Vector3D(0, 0, 1)),
        };

        public static readonly DependencyProperty PackNameProperty = DependencyProperty.Register(
            nameof(PackName),
            typeof(string),
            typeof(SkyboxPreviewControl),
            new PropertyMetadata(null, OnPackNameChanged)
        );

        public static readonly DependencyProperty ReloadKeyProperty = DependencyProperty.Register(
            nameof(ReloadKey),
            typeof(int),
            typeof(SkyboxPreviewControl),
            new PropertyMetadata(0, OnReloadKeyChanged)
        );

        private readonly GeometryModel3D[] _faceModels = new GeometryModel3D[FaceAxes.Length];

        private readonly DispatcherTimer _spinTimer;

        private CancellationTokenSource? _loadCts;

        private double _yaw = DefaultYaw;

        private double _pitch = DefaultPitch;

        private double _distance = DefaultDistance;

        private Point _lastMouse;

        private bool _orbiting;

        private bool _userInteracted;

        private bool _initialized;

        public SkyboxPreviewControl()
        {
            InitializeComponent();
            BuildBall();
            UpdateCamera();
            _spinTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _spinTimer.Tick += SpinTimer_Tick;
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
            _initialized = true;
        }

        public string? PackName
        {
            get => (string?)GetValue(PackNameProperty);
            set => SetValue(PackNameProperty, value);
        }

        public int ReloadKey
        {
            get => (int)GetValue(ReloadKeyProperty);
            set => SetValue(ReloadKeyProperty, value);
        }

        private static void OnPackNameChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is SkyboxPreviewControl control && control._initialized)
                _ = control.ReloadAsync();
        }

        private static void OnReloadKeyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is SkyboxPreviewControl control && control._initialized)
                _ = control.ReloadAsync();
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            if (!_userInteracted)
                _spinTimer.Start();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _spinTimer.Stop();
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
        }

        private void SpinTimer_Tick(object? sender, EventArgs e)
        {
            _yaw += 0.15;
            if (_yaw >= 360.0)
                _yaw -= 360.0;
            UpdateCamera();
        }

        private async Task ReloadAsync()
        {
            const string LOG_IDENT = "SkyboxPreviewControl::ReloadAsync";
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            CancellationToken token = _loadCts.Token;
            string? name = PackName;
            if (string.IsNullOrWhiteSpace(name))
            {
                ApplyPlaceholder();
                LoadingPanel.Visibility = Visibility.Collapsed;
                StatusText.Visibility = Visibility.Collapsed;
                return;
            }
            LoadingPanel.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
            try
            {
                SkyboxPreviewFaces faces = await SkyboxPreviewLoader.LoadAsync(name, token);
                if (token.IsCancellationRequested)
                    return;
                ApplyFaces(faces.Faces);
                LoadingPanel.Visibility = Visibility.Collapsed;
                StatusText.Visibility = Visibility.Collapsed;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                    return;
                App.Logger?.WriteLine(LOG_IDENT, "The skybox preview could not be loaded: " + ex.Message);
                ApplyPlaceholder();
                LoadingPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = string.IsNullOrWhiteSpace(ex.Message) ? Strings.Ui_Mods_SkyboxPreviewFailed : ex.Message;
                StatusText.Visibility = Visibility.Visible;
            }
        }

        private void ApplyFaces(BitmapSource[] faces)
        {
            for (int i = 0; i < _faceModels.Length && i < faces.Length; i++)
            {
                ImageBrush brush = new ImageBrush(faces[i]);
                brush.Freeze();
                DiffuseMaterial material = new DiffuseMaterial(brush);
                material.Freeze();
                _faceModels[i].Material = material;
                _faceModels[i].BackMaterial = material;
            }
        }

        private void ApplyPlaceholder()
        {
            SolidColorBrush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(PlaceholderHex));
            brush.Freeze();
            DiffuseMaterial material = new DiffuseMaterial(brush);
            material.Freeze();
            foreach (GeometryModel3D model in _faceModels)
            {
                model.Material = material;
                model.BackMaterial = material;
            }
        }

        private void BuildBall()
        {
            Model3DGroup group = new Model3DGroup();
            for (int i = 0; i < FaceAxes.Length; i++)
            {
                (Vector3D Major, Vector3D S, Vector3D T) axis = FaceAxes[i];
                MeshGeometry3D mesh = BuildFaceMesh(axis.Major, axis.S, axis.T);
                mesh.Freeze();
                GeometryModel3D model = new GeometryModel3D { Geometry = mesh };
                _faceModels[i] = model;
                group.Children.Add(model);
            }
            BallVisual.Content = group;
            ApplyPlaceholder();
        }

        private static MeshGeometry3D BuildFaceMesh(Vector3D major, Vector3D sAxis, Vector3D tAxis)
        {
            int verticesPerRow = FaceSegments + 1;
            MeshGeometry3D mesh = new MeshGeometry3D();
            Point3DCollection positions = new Point3DCollection(verticesPerRow * verticesPerRow);
            Vector3DCollection normals = new Vector3DCollection(verticesPerRow * verticesPerRow);
            PointCollection uvs = new PointCollection(verticesPerRow * verticesPerRow);
            Int32Collection indices = new Int32Collection(FaceSegments * FaceSegments * 6);
            for (int j = 0; j <= FaceSegments; j++)
            {
                double t = 1.0 - 2.0 * j / FaceSegments;
                for (int i = 0; i <= FaceSegments; i++)
                {
                    double s = 2.0 * i / FaceSegments - 1.0;
                    Vector3D direction = major + sAxis * s + tAxis * t;
                    direction.Normalize();
                    positions.Add(new Point3D(direction.X, direction.Y, direction.Z));
                    normals.Add(direction);
                    uvs.Add(new Point((double)i / FaceSegments, (double)j / FaceSegments));
                }
            }
            for (int j = 0; j < FaceSegments; j++)
            {
                for (int i = 0; i < FaceSegments; i++)
                {
                    int topLeft = j * verticesPerRow + i;
                    int topRight = topLeft + 1;
                    int bottomLeft = topLeft + verticesPerRow;
                    int bottomRight = bottomLeft + 1;
                    indices.Add(topLeft);
                    indices.Add(bottomLeft);
                    indices.Add(bottomRight);
                    indices.Add(topLeft);
                    indices.Add(bottomRight);
                    indices.Add(topRight);
                }
            }
            mesh.Positions = positions;
            mesh.Normals = normals;
            mesh.TextureCoordinates = uvs;
            mesh.TriangleIndices = indices;
            return mesh;
        }

        private void UpdateCamera()
        {
            double yawRad = _yaw * Math.PI / 180.0;
            double pitchRad = _pitch * Math.PI / 180.0;
            double x = _distance * Math.Cos(pitchRad) * Math.Sin(yawRad);
            double y = _distance * Math.Sin(pitchRad);
            double z = _distance * Math.Cos(pitchRad) * Math.Cos(yawRad);
            Camera.Position = new Point3D(x, y, z);
            Camera.LookDirection = new Vector3D(-x, -y, -z);
        }

        private void ResetView()
        {
            _yaw = DefaultYaw;
            _pitch = DefaultPitch;
            _distance = DefaultDistance;
            UpdateCamera();
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _userInteracted = true;
            _spinTimer.Stop();
            if (e.ClickCount == 2)
            {
                ResetView();
                return;
            }
            _orbiting = true;
            _lastMouse = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_orbiting || e.LeftButton != MouseButtonState.Pressed)
                return;
            Point position = e.GetPosition(this);
            _yaw -= (position.X - _lastMouse.X) * 0.45;
            _pitch += (position.Y - _lastMouse.Y) * 0.45;
            _pitch = Math.Clamp(_pitch, -85.0, 85.0);
            _lastMouse = position;
            UpdateCamera();
        }

        private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _orbiting = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
        }

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.9 : 1.1), 2.05, 5.5);
            UpdateCamera();
            e.Handled = true;
        }
    }
}
