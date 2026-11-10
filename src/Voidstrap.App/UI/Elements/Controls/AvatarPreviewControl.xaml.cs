using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Voidstrap.UI.Elements.Controls
{
    public partial class AvatarPreviewControl : UserControl
    {
        private const double DefaultPitch = 6.0;

        private const double DefaultZoom = 1.0;

        private const double MinZoom = 0.3;

        private const double MaxZoom = 1.5;

        private const double BackdropRadius = 9.0;

        private const int BackdropSegments = 64;

        private const int SoftwareFrameLimit = 1024;

        private const double SoftwareSupersample = 2.0;

        private const double RotateDegreesPerSecond = 14.0;

        private static readonly TimeSpan RotateInterval = TimeSpan.FromMilliseconds(33);

        private static readonly TimeSpan RotateResumeDelay = TimeSpan.FromSeconds(2.5);

        private static readonly bool Supports3D = Voidstrap.Utility.Platform.IsWindows;

        public static readonly DependencyProperty SceneProperty = DependencyProperty.Register(
            nameof(Scene),
            typeof(AvatarScene),
            typeof(AvatarPreviewControl),
            new PropertyMetadata(null, OnSceneChanged)
        );

        public static readonly DependencyProperty ImageUrlProperty = DependencyProperty.Register(
            nameof(ImageUrl),
            typeof(string),
            typeof(AvatarPreviewControl),
            new PropertyMetadata(null)
        );

        public static readonly DependencyProperty BackgroundUrlProperty = DependencyProperty.Register(
            nameof(BackgroundUrl),
            typeof(string),
            typeof(AvatarPreviewControl),
            new PropertyMetadata(null)
        );

        public static readonly DependencyProperty BackgroundImageProperty = DependencyProperty.Register(
            nameof(BackgroundImage),
            typeof(ImageSource),
            typeof(AvatarPreviewControl),
            new PropertyMetadata(null, OnBackgroundImageChanged)
        );

        public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
            nameof(IsLoading),
            typeof(bool),
            typeof(AvatarPreviewControl),
            new PropertyMetadata(false)
        );

        private double _yaw;

        private double _pitch = DefaultPitch;

        private double _zoom = DefaultZoom;

        private Vector3D _pan;

        private Point _lastMouse;

        private bool _orbiting;

        private bool _panning;

        private bool _hasBackdrop;

        private readonly AvatarSoftwareRenderer? _renderer = Supports3D ? null : new AvatarSoftwareRenderer();

        private WriteableBitmap? _softwareBitmap;

        private int[] _softwareFrame = [];

        private bool _softwarePending;

        private DispatcherTimer? _rotateTimer;

        private bool _autoRotate = true;

        private DateTime _lastInteraction = DateTime.MinValue;

        private DateTime _lastRotateTick;

        private List<(AvatarPiece Piece, MatrixTransform3D Transform, MeshGeometry3D? Skinned)>? _pieces;

        private double _animationTime;

        private AvatarParticles? _particles;

        public AvatarPreviewControl()
        {
            InitializeComponent();
            if (!Supports3D)
                Host.Children.Remove(Viewport);
            else
                Host.Children.Remove(SoftwareView);
        }

        public AvatarScene? Scene
        {
            get => (AvatarScene?)GetValue(SceneProperty);
            set => SetValue(SceneProperty, value);
        }

        public string? ImageUrl
        {
            get => (string?)GetValue(ImageUrlProperty);
            set => SetValue(ImageUrlProperty, value);
        }

        public string? BackgroundUrl
        {
            get => (string?)GetValue(BackgroundUrlProperty);
            set => SetValue(BackgroundUrlProperty, value);
        }

        public ImageSource? BackgroundImage
        {
            get => (ImageSource?)GetValue(BackgroundImageProperty);
            set => SetValue(BackgroundImageProperty, value);
        }

        public bool IsLoading
        {
            get => (bool)GetValue(IsLoadingProperty);
            set => SetValue(IsLoadingProperty, value);
        }

        private static void OnSceneChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is AvatarPreviewControl control)
                control.ApplyScene(e.OldValue == null);
        }

        private static void OnBackgroundImageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is AvatarPreviewControl control)
                control.ApplyBackdrop();
        }

        private void ApplyScene(bool resetView)
        {
            const string LOG_IDENT = "AvatarPreviewControl::ApplyScene";
            AvatarScene? scene = Scene;
            try
            {
                Model3D? model = BuildPieces(scene) ?? scene?.Model;
                _particles = scene?.Effects is { Count: > 0 } effects && _pieces != null ? new AvatarParticles(effects) : null;
                if (_particles is { Any: true } && Supports3D && model is Model3DGroup holder)
                    foreach (GeometryModel3D effect in _particles.Models)
                        holder.Children.Add(effect);
                if (_renderer != null)
                    _renderer.SetAvatar(model);
                else
                    AvatarVisual.Content = model;
                if (scene != null)
                    ApplyPose(scene);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The 3D avatar could not be shown: " + ex.Message);
                _pieces = null;
                _particles = null;
                _renderer?.SetAvatar(null);
                if (_renderer == null)
                    AvatarVisual.Content = null;
                scene = null;
            }
            bool show = scene != null;
            Viewport.Visibility = show && Supports3D ? Visibility.Visible : Visibility.Collapsed;
            SoftwareView.Visibility = show && !Supports3D ? Visibility.Visible : Visibility.Collapsed;
            FlatImage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            ResetButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            RotateButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            UpdateRotateTimer();
            Cursor = show ? Cursors.SizeAll : null;
            if (scene != null && resetView)
                ResetView(scene);
            ApplyBackdrop();
            if (scene != null)
                UpdateCamera(scene);
        }

        private Model3DGroup? BuildPieces(AvatarScene? scene)
        {
            _pieces = null;
            if (scene?.Pieces == null || scene.Rig == null || scene.Pieces.Count == 0)
                return null;
            List<(AvatarPiece Piece, MatrixTransform3D Transform, MeshGeometry3D? Skinned)> pieces = new();
            Model3DGroup group = new();
            foreach (AvatarPiece piece in scene.Pieces)
            {
                MatrixTransform3D transform = new();
                MeshGeometry3D? skinned = piece.Skin == null
                    ? null
                    : new MeshGeometry3D { Positions = piece.Mesh.Positions, TextureCoordinates = piece.Mesh.TextureCoordinates, TriangleIndices = piece.Mesh.TriangleIndices };
                group.Children.Add(new GeometryModel3D(skinned ?? piece.Mesh, piece.Material) { BackMaterial = piece.Material, Transform = transform });
                pieces.Add((piece, transform, skinned));
            }
            _pieces = pieces;
            return group;
        }

        private void ApplyPose(AvatarScene scene)
        {
            if (_pieces == null || scene.Rig == null)
                return;
            ApplyPose(Voidstrap.Integrations.AvatarPose.Evaluate(scene.Rig, _animationTime));
        }

        private void ApplyPose(Dictionary<string, Voidstrap.Integrations.Animation.RobloxCFrame> world)
        {
            if (_pieces == null)
                return;
            List<Matrix3D>? moved = _renderer != null ? new List<Matrix3D>(_pieces.Count) : null;
            for (int index = 0; index < _pieces.Count; index++)
            {
                (AvatarPiece piece, MatrixTransform3D transform, MeshGeometry3D? skinned) = _pieces[index];
                if (piece.Skin is AvatarSkin skin && skinned != null)
                {
                    Matrix3D[] bones = new Matrix3D[skin.Bones.Length];
                    for (int b = 0; b < bones.Length; b++)
                        bones[b] = world.TryGetValue(skin.Bones[b], out Voidstrap.Integrations.Animation.RobloxCFrame bone) ? bone.ToMatrix3D() : Matrix3D.Identity;
                    Point3DCollection posed = new(skin.Local.Length);
                    for (int v = 0; v < skin.Local.Length; v++)
                        posed.Add(bones[skin.Bone[v]].Transform(skin.Local[v]));
                    posed.Freeze();
                    skinned.Positions = posed;
                    _renderer?.SetPositions(index, posed);
                    moved?.Add(Matrix3D.Identity);
                    continue;
                }
                Matrix3D matrix = world.TryGetValue(piece.Part, out Voidstrap.Integrations.Animation.RobloxCFrame frame) ? piece.Offset * frame.ToMatrix3D() : piece.Offset;
                transform.Matrix = matrix;
                moved?.Add(matrix);
            }
            if (moved != null)
            {
                _renderer!.SetTransforms(moved);
                RequestSoftwareRender();
            }
        }

        private void UpdateParticles(Dictionary<string, Voidstrap.Integrations.Animation.RobloxCFrame> world, double elapsed)
        {
            if (_particles is not { Any: true })
                return;
            Vector3D look = Camera.LookDirection;
            Vector3D right = Vector3D.CrossProduct(look, Camera.UpDirection);
            if (right.LengthSquared < 1e-12)
                return;
            right.Normalize();
            Vector3D up = Vector3D.CrossProduct(right, look);
            up.Normalize();
            _particles.Update(
                elapsed,
                effect => world.TryGetValue(effect.Part, out Voidstrap.Integrations.Animation.RobloxCFrame frame) ? effect.Offset * frame.ToMatrix3D() : effect.Offset,
                right,
                up
            );
            if (_renderer != null)
            {
                _renderer.SetEffects(_particles.Models);
                RequestSoftwareRender();
            }
        }

        private void ResetView(AvatarScene scene)
        {
            _yaw = scene.Yaw;
            _pitch = DefaultPitch;
            _zoom = DefaultZoom;
            _pan = default;
        }

        private void ApplyBackdrop()
        {
            const string LOG_IDENT = "AvatarPreviewControl::ApplyBackdrop";
            GeometryModel3D? backdrop = null;
            try
            {
                if (Scene is AvatarScene scene && BackgroundImage is ImageSource image)
                    backdrop = BuildBackdrop(scene, image);
                if (_renderer != null)
                    _renderer.SetBackdrop(backdrop);
                else
                    BackdropVisual.Content = backdrop;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The 3D background could not be shown: " + ex.Message);
                backdrop = null;
                _renderer?.SetBackdrop(null);
                if (_renderer == null)
                    BackdropVisual.Content = null;
            }
            _hasBackdrop = backdrop != null;
            if (Scene is AvatarScene current)
                UpdateCamera(current);
        }

        private void RequestSoftwareRender()
        {
            if (_renderer == null || _softwarePending)
                return;
            _softwarePending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(RenderSoftware));
        }

        private void RenderSoftware()
        {
            const string LOG_IDENT = "AvatarPreviewControl::RenderSoftware";
            _softwarePending = false;
            if (_renderer == null || Scene == null)
                return;
            try
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                int width = Math.Min(SoftwareFrameLimit, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX * SoftwareSupersample));
                int height = Math.Min(SoftwareFrameLimit, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY * SoftwareSupersample));
                if (width <= 0 || height <= 0)
                    return;
                if (_softwareBitmap == null || _softwareBitmap.PixelWidth != width || _softwareBitmap.PixelHeight != height)
                {
                    _softwareBitmap = new WriteableBitmap(width, height, 96.0, 96.0, PixelFormats.Pbgra32, null);
                    _softwareFrame = new int[width * height];
                    SoftwareView.Source = _softwareBitmap;
                }
                _renderer.Render(_softwareFrame, width, height, Camera.Position, Camera.LookDirection, Camera.UpDirection, Camera.FieldOfView);
                _softwareBitmap.WritePixels(new Int32Rect(0, 0, width, height), _softwareFrame, width * 4, 0);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The 3D avatar could not be drawn: " + ex.Message);
            }
        }

        private void UpdateRotateTimer()
        {
            bool run = (_autoRotate || Scene?.Rig?.Animation != null || _particles is { Any: true }) && IsLoaded && Scene != null;
            if (run && _rotateTimer == null)
            {
                _lastRotateTick = DateTime.UtcNow;
                _rotateTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = RotateInterval };
                _rotateTimer.Tick += OnRotateTick;
                _rotateTimer.Start();
            }
            else if (!run && _rotateTimer != null)
            {
                _rotateTimer.Stop();
                _rotateTimer.Tick -= OnRotateTick;
                _rotateTimer = null;
            }
        }

        private void OnRotateTick(object? sender, EventArgs e)
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = Math.Min((now - _lastRotateTick).TotalSeconds, 0.25);
            _lastRotateTick = now;
            if (Scene is not AvatarScene scene)
                return;
            bool animate = scene.Rig?.Animation != null && _pieces != null;
            if (scene.Rig != null && (animate || _particles is { Any: true }))
            {
                if (animate)
                    _animationTime += elapsed;
                Dictionary<string, Voidstrap.Integrations.Animation.RobloxCFrame> world = Voidstrap.Integrations.AvatarPose.Evaluate(scene.Rig, _animationTime);
                if (animate)
                    ApplyPose(world);
                UpdateParticles(world, elapsed);
            }
            if (!_autoRotate || _orbiting || _panning || now - _lastInteraction < RotateResumeDelay)
                return;
            _yaw = (_yaw + RotateDegreesPerSecond * elapsed) % 360.0;
            UpdateCamera(scene);
        }

        private void OnRotateClick(object sender, RoutedEventArgs e)
        {
            _autoRotate = !_autoRotate;
            RotateButton.Icon = _autoRotate ? Wpf.Ui.Common.SymbolRegular.Pause24 : Wpf.Ui.Common.SymbolRegular.Play24;
            UpdateRotateTimer();
        }

        private void OnPreviewLoaded(object sender, RoutedEventArgs e)
        {
            if (Scene != null && _pieces == null)
                ApplyScene(false);
            UpdateRotateTimer();
        }

        private void OnPreviewUnloaded(object sender, RoutedEventArgs e)
        {
            UpdateRotateTimer();
            _pieces = null;
            _particles = null;
            _renderer?.SetAvatar(null);
            _renderer?.SetBackdrop(null);
            if (_renderer == null)
            {
                AvatarVisual.Content = null;
                BackdropVisual.Content = null;
            }
            _softwareBitmap = null;
            _softwareFrame = [];
            SoftwareView.Source = null;
        }

        private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Scene is AvatarScene scene)
                UpdateCamera(scene);
        }

        private static (Point3D Center, double Radius) Measure(AvatarScene scene)
        {
            Rect3D bounds = scene.Bounds;
            Point3D center = new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            double radius = Math.Max(0.5 * Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ), 0.01);
            return (center, radius);
        }

        private static GeometryModel3D BuildBackdrop(AvatarScene scene, ImageSource image)
        {
            (Point3D center, double avatarRadius) = Measure(scene);
            double radius = avatarRadius * BackdropRadius;
            double halfHeight = Math.PI * radius / 4.0;
            MeshGeometry3D mesh = new();
            for (int i = 0; i <= BackdropSegments; i++)
            {
                double t = (double)i / BackdropSegments;
                double angle = t * Math.PI * 2.0;
                double x = center.X + Math.Sin(angle) * radius;
                double z = center.Z + Math.Cos(angle) * radius;
                mesh.Positions.Add(new Point3D(x, center.Y + halfHeight * 3.0, z));
                mesh.Positions.Add(new Point3D(x, center.Y - halfHeight * 3.0, z));
                mesh.TextureCoordinates.Add(new Point(t * 4.0, -1));
                mesh.TextureCoordinates.Add(new Point(t * 4.0, 2));
            }
            for (int i = 0; i < BackdropSegments; i++)
            {
                int a = i * 2;
                mesh.TriangleIndices.Add(a);
                mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 2);
                mesh.TriangleIndices.Add(a + 2);
                mesh.TriangleIndices.Add(a + 1);
                mesh.TriangleIndices.Add(a + 3);
            }
            ImageBrush brush = new(image)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.FlipXY,
                Stretch = Stretch.Fill,
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            MaterialGroup material = new();
            material.Children.Add(new DiffuseMaterial(Brushes.Black));
            material.Children.Add(new EmissiveMaterial(brush));
            GeometryModel3D model = new(mesh, material) { BackMaterial = material };
            model.Freeze();
            return model;
        }

        private void UpdateCamera(AvatarScene scene)
        {
            (Point3D center, double radius) = Measure(scene);
            double maxPitch = _hasBackdrop ? 28.0 : 60.0;
            double minPitch = _hasBackdrop ? -18.0 : -35.0;
            _pitch = Math.Clamp(_pitch, minPitch, maxPitch);
            double maxPan = radius * 1.4;
            if (_pan.Length > maxPan)
                _pan *= maxPan / _pan.Length;
            double tanX = Math.Tan(Camera.FieldOfView * Math.PI / 360.0);
            double tanY = tanX * (ActualWidth > 1.0 && ActualHeight > 1.0 ? ActualHeight / ActualWidth : 1.25);
            double halfWidth = Math.Max(0.5 * Math.Sqrt(scene.Bounds.SizeX * scene.Bounds.SizeX + scene.Bounds.SizeZ * scene.Bounds.SizeZ), 0.01);
            double halfHeight = Math.Max(0.5 * scene.Bounds.SizeY, 0.01);
            double distance = (Math.Max(halfHeight / tanY, halfWidth / tanX) * 1.12 + halfWidth) * _zoom;
            double yaw = _yaw * Math.PI / 180.0;
            double pitch = _pitch * Math.PI / 180.0;
            Vector3D offset = new(Math.Sin(yaw) * Math.Cos(pitch) * distance, Math.Sin(pitch) * distance, Math.Cos(yaw) * Math.Cos(pitch) * distance);
            Camera.Position = center + _pan + offset;
            Camera.LookDirection = -offset;
            Camera.NearPlaneDistance = Math.Max(radius * 0.05, 0.01);
            Camera.FarPlaneDistance = distance + radius * (BackdropRadius * 2.5);
            RequestSoftwareRender();
        }

        private void OnPreviewDown(object sender, MouseButtonEventArgs e)
        {
            if (Scene == null || _orbiting || _panning || e.OriginalSource is DependencyObject source && IsInsideButton(source))
                return;
            _lastMouse = e.GetPosition(this);
            _lastInteraction = DateTime.UtcNow;
            bool pan = e.ChangedButton == MouseButton.Right || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (!CaptureMouse())
                return;
            _panning = pan;
            _orbiting = !pan;
            e.Handled = true;
        }

        private bool IsInsideButton(DependencyObject source)
        {
            for (DependencyObject? current = source; current != null && !ReferenceEquals(current, this); current = current is Visual ? VisualTreeHelper.GetParent(current) : null)
                if (ReferenceEquals(current, ResetButton) || ReferenceEquals(current, RotateButton))
                    return true;
            return false;
        }

        private void OnPreviewMove(object sender, MouseEventArgs e)
        {
            if (!_orbiting && !_panning || Scene is not AvatarScene scene)
                return;
            Point position = e.GetPosition(this);
            double dx = position.X - _lastMouse.X;
            double dy = position.Y - _lastMouse.Y;
            _lastMouse = position;
            if (_panning)
            {
                double radius = Measure(scene).Radius;
                double step = radius * 2.4 * _zoom / Math.Max(ActualHeight, 1.0);
                double yaw = _yaw * Math.PI / 180.0;
                Vector3D right = new(Math.Cos(yaw), 0, -Math.Sin(yaw));
                _pan += right * (-dx * step) + new Vector3D(0, dy * step, 0);
            }
            else
            {
                _yaw -= dx * 0.6;
                _pitch += dy * 0.4;
            }
            UpdateCamera(scene);
        }

        private void OnPreviewUp(object sender, MouseButtonEventArgs e)
        {
            _lastInteraction = DateTime.UtcNow;
            if (_orbiting || _panning)
                ReleaseMouseCapture();
        }

        private void OnPreviewLostCapture(object sender, MouseEventArgs e)
        {
            _orbiting = false;
            _panning = false;
        }

        private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
        {
            if (Scene is not AvatarScene scene)
                return;
            _lastInteraction = DateTime.UtcNow;
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 0.9 : 1.1), MinZoom, MaxZoom);
            UpdateCamera(scene);
            e.Handled = true;
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            if (Scene is not AvatarScene scene)
                return;
            ResetView(scene);
            UpdateCamera(scene);
        }
    }
}
