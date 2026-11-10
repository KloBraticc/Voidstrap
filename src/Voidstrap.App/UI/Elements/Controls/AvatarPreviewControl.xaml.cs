using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Voidstrap.UI.Elements.Controls
{
    public partial class AvatarPreviewControl : UserControl
    {
        private const double DefaultPitch = 6.0;

        private const double DefaultZoom = 0.82;

        private const double MinZoom = 0.3;

        private const double MaxZoom = 1.6;

        private const double BackdropRadius = 9.0;

        private const int BackdropSegments = 64;

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

        public AvatarPreviewControl()
        {
            InitializeComponent();
            if (!Supports3D)
                Host.Children.Remove(Viewport);
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
            if (!Supports3D)
                return;
            AvatarScene? scene = Scene;
            try
            {
                AvatarVisual.Content = scene?.Model;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The 3D avatar could not be shown: " + ex.Message);
                AvatarVisual.Content = null;
                scene = null;
            }
            bool show = scene != null;
            Viewport.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            FlatImage.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            ResetButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Cursor = show ? Cursors.SizeAll : null;
            if (scene != null && resetView)
                ResetView(scene);
            ApplyBackdrop();
            if (scene != null)
                UpdateCamera(scene);
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
            if (!Supports3D)
                return;
            try
            {
                BackdropVisual.Content = Scene is AvatarScene scene && BackgroundImage is ImageSource image ? BuildBackdrop(scene, image) : null;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The 3D background could not be shown: " + ex.Message);
                BackdropVisual.Content = null;
            }
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
                mesh.Positions.Add(new Point3D(x, center.Y + halfHeight, z));
                mesh.Positions.Add(new Point3D(x, center.Y - halfHeight, z));
                mesh.TextureCoordinates.Add(new Point(t * 4.0, 0));
                mesh.TextureCoordinates.Add(new Point(t * 4.0, 1));
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
                TileMode = TileMode.FlipX,
                Stretch = Stretch.Fill,
            };
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
            bool backdrop = BackdropVisual.Content != null;
            double maxPitch = backdrop ? 28.0 : 60.0;
            double minPitch = backdrop ? -18.0 : -35.0;
            _pitch = Math.Clamp(_pitch, minPitch, maxPitch);
            double maxPan = radius * 1.4;
            if (_pan.Length > maxPan)
                _pan *= maxPan / _pan.Length;
            double distance = radius / Math.Sin(Camera.FieldOfView * Math.PI / 360.0) * _zoom;
            double yaw = _yaw * Math.PI / 180.0;
            double pitch = _pitch * Math.PI / 180.0;
            Vector3D offset = new(Math.Sin(yaw) * Math.Cos(pitch) * distance, Math.Sin(pitch) * distance, Math.Cos(yaw) * Math.Cos(pitch) * distance);
            Camera.Position = center + _pan + offset;
            Camera.LookDirection = -offset;
            Camera.NearPlaneDistance = Math.Max(radius * 0.05, 0.01);
            Camera.FarPlaneDistance = distance + radius * (BackdropRadius * 2.5);
        }

        private void OnPreviewDown(object sender, MouseButtonEventArgs e)
        {
            if (!Supports3D || Scene == null || _orbiting || _panning || e.OriginalSource is DependencyObject source && IsInsideButton(source))
                return;
            _lastMouse = e.GetPosition(this);
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
                if (ReferenceEquals(current, ResetButton))
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
            if (!Supports3D || Scene is not AvatarScene scene)
                return;
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
