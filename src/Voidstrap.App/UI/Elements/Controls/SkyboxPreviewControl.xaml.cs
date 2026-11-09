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

        private const int SoftwareFaceLimit = 512;

        private const int SoftwareFrameLimit = 1024;

        private static readonly bool Supports3D = Voidstrap.Utility.Platform.IsWindows;

        private static readonly Color PlaceholderColor = (Color)ColorConverter.ConvertFromString(PlaceholderHex);

        private static readonly Vector3D AmbientLight = new Vector3D(0xA9 / 255.0, 0xAD / 255.0, 0xB6 / 255.0);

        private static readonly (Vector3D Direction, Vector3D Color)[] DirectionalLights =
        {
            (ToLight(new Vector3D(-0.45, -0.55, -0.7)), new Vector3D(0x6B / 255.0, 0x6B / 255.0, 0x70 / 255.0)),
            (ToLight(new Vector3D(0.6, 0.2, 0.75)), new Vector3D(0x2E / 255.0, 0x2E / 255.0, 0x36 / 255.0)),
        };

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

        private readonly byte[]?[] _softwareFaces = new byte[FaceAxes.Length][];

        private readonly int[] _softwareFaceWidths = new int[FaceAxes.Length];

        private readonly int[] _softwareFaceHeights = new int[FaceAxes.Length];

        private WriteableBitmap? _softwareBitmap;

        private int[] _softwareFrame = Array.Empty<int>();

        private CancellationTokenSource? _loadCts;

        private double _yaw = DefaultYaw;

        private double _pitch = DefaultPitch;

        private double _distance = DefaultDistance;

        private Point _lastMouse;

        private bool _orbiting;

        private bool _softwareRenderPending;

        private bool _initialized;

        public SkyboxPreviewControl()
        {
            InitializeComponent();
            if (!Supports3D)
            {
                ((Grid)Viewport.Parent).Children.Remove(Viewport);
                SoftwareView.Visibility = Visibility.Visible;
                SizeChanged += OnControlSizeChanged;
            }
            BuildBall();
            UpdateCamera();
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

        private void OnControlSizeChanged(object sender, SizeChangedEventArgs e)
        {
            RequestSoftwareRender();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
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
                SkyboxPreviewResult result = await SkyboxPreviewLoader.LoadAsync(name, token);
                if (token.IsCancellationRequested)
                    return;
                if (result.Faces != null)
                {
                    ApplyFaces(result.Faces.Faces);
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    StatusText.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ApplyPlaceholder();
                    LoadingPanel.Visibility = Visibility.Collapsed;
                    StatusText.Text = result.Message ?? Strings.Ui_Mods_SkyboxPreviewFailed;
                    StatusText.Visibility = Visibility.Visible;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                    return;
                App.Logger?.WriteLine(LOG_IDENT, "The skybox preview could not be loaded: " + ex.Message);
                ApplyPlaceholder();
                LoadingPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = Strings.Ui_Mods_SkyboxPreviewFailed;
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
            if (Supports3D)
                return;
            for (int i = 0; i < _softwareFaces.Length; i++)
            {
                _softwareFaces[i] = i < faces.Length
                    ? ReadSoftwareFace(faces[i], out _softwareFaceWidths[i], out _softwareFaceHeights[i])
                    : null;
            }
            RequestSoftwareRender();
        }

        private static byte[] ReadSoftwareFace(BitmapSource face, out int width, out int height)
        {
            if (face.Format != PixelFormats.Bgra32)
            {
                FormatConvertedBitmap converted = new FormatConvertedBitmap(face, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                face = converted;
            }
            width = face.PixelWidth;
            height = face.PixelHeight;
            byte[] pixels = new byte[width * height * 4];
            face.CopyPixels(pixels, width * 4, 0);
            if (width <= SoftwareFaceLimit && height <= SoftwareFaceLimit)
                return pixels;
            double scale = Math.Min((double)SoftwareFaceLimit / width, (double)SoftwareFaceLimit / height);
            int targetWidth = Math.Max(1, (int)(width * scale));
            int targetHeight = Math.Max(1, (int)(height * scale));
            Raster resized = new Raster(width, height, pixels).Resize(targetWidth, targetHeight, RasterFilter.Bilinear);
            width = resized.Width;
            height = resized.Height;
            return resized.Pixels;
        }

        private void ApplyPlaceholder()
        {
            SolidColorBrush brush = new SolidColorBrush(PlaceholderColor);
            brush.Freeze();
            DiffuseMaterial material = new DiffuseMaterial(brush);
            material.Freeze();
            foreach (GeometryModel3D model in _faceModels)
            {
                model.Material = material;
                model.BackMaterial = material;
            }
            Array.Clear(_softwareFaces);
            RequestSoftwareRender();
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
            RequestSoftwareRender();
        }

        private static Vector3D ToLight(Vector3D direction)
        {
            direction.Normalize();
            return -direction;
        }

        private void RequestSoftwareRender()
        {
            if (Supports3D || !_initialized || _softwareRenderPending)
                return;
            _softwareRenderPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderSoftware));
        }

        private void RenderSoftware()
        {
            _softwareRenderPending = false;
            if (Supports3D || !_initialized)
                return;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int width = Math.Min(SoftwareFrameLimit, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX));
            int height = Math.Min(SoftwareFrameLimit, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY));
            if (width <= 0 || height <= 0)
                return;
            if (_softwareBitmap == null || _softwareBitmap.PixelWidth != width || _softwareBitmap.PixelHeight != height)
            {
                _softwareBitmap = new WriteableBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32, null);
                _softwareFrame = new int[width * height];
                SoftwareView.Source = _softwareBitmap;
            }
            Vector3D origin = (Vector3D)Camera.Position;
            Vector3D forward = Camera.LookDirection;
            forward.Normalize();
            Vector3D right = Vector3D.CrossProduct(forward, Camera.UpDirection);
            right.Normalize();
            Vector3D up = Vector3D.CrossProduct(right, forward);
            double tanX = Math.Tan(Camera.FieldOfView * Math.PI / 360.0);
            double tanY = tanX * height / width;
            double c = origin.LengthSquared - 1.0;
            for (int py = 0; py < height; py++)
            {
                double sy = (1.0 - 2.0 * (py + 0.5) / height) * tanY;
                for (int px = 0; px < width; px++)
                {
                    double sx = (2.0 * (px + 0.5) / width - 1.0) * tanX;
                    Vector3D ray = forward + right * sx + up * sy;
                    double a = ray.LengthSquared;
                    double b = 2.0 * Vector3D.DotProduct(origin, ray);
                    double discriminant = b * b - 4.0 * a * c;
                    int index = py * width + px;
                    if (discriminant < 0.0)
                    {
                        _softwareFrame[index] = 0;
                        continue;
                    }
                    Vector3D normal = origin + ray * ((-b - Math.Sqrt(discriminant)) / (2.0 * a));
                    normal.Normalize();
                    Vector3D light = AmbientLight;
                    foreach ((Vector3D direction, Vector3D color) in DirectionalLights)
                        light += color * Math.Max(0.0, Vector3D.DotProduct(normal, direction));
                    SampleFace(normal, out byte blue, out byte green, out byte red);
                    _softwareFrame[index] = unchecked((int)0xFF000000)
                        | Shade(red, light.X) << 16
                        | Shade(green, light.Y) << 8
                        | Shade(blue, light.Z);
                }
            }
            _softwareBitmap.WritePixels(new Int32Rect(0, 0, width, height), _softwareFrame, width * 4, 0);
        }

        private void SampleFace(Vector3D normal, out byte blue, out byte green, out byte red)
        {
            int face = 0;
            double major = double.MinValue;
            for (int i = 0; i < FaceAxes.Length; i++)
            {
                double dot = Vector3D.DotProduct(normal, FaceAxes[i].Major);
                if (dot > major)
                {
                    major = dot;
                    face = i;
                }
            }
            byte[]? pixels = _softwareFaces[face];
            if (pixels == null)
            {
                blue = PlaceholderColor.B;
                green = PlaceholderColor.G;
                red = PlaceholderColor.R;
                return;
            }
            double s = Vector3D.DotProduct(normal, FaceAxes[face].S) / major;
            double t = Vector3D.DotProduct(normal, FaceAxes[face].T) / major;
            int width = _softwareFaceWidths[face];
            int height = _softwareFaceHeights[face];
            int x = Math.Clamp((int)((s + 1.0) * 0.5 * width), 0, width - 1);
            int y = Math.Clamp((int)((1.0 - t) * 0.5 * height), 0, height - 1);
            int offset = (y * width + x) * 4;
            blue = pixels[offset];
            green = pixels[offset + 1];
            red = pixels[offset + 2];
        }

        private static int Shade(byte channel, double light) => Math.Min(255, (int)(channel * light));

        private void ResetView()
        {
            _yaw = DefaultYaw;
            _pitch = DefaultPitch;
            _distance = DefaultDistance;
            UpdateCamera();
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
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
