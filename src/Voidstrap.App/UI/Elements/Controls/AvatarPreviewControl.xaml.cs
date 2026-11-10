using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace Voidstrap.UI.Elements.Controls
{
    public partial class AvatarPreviewControl : UserControl
    {
        private const double DefaultPitch = 6.0;

        private const double MinZoom = 0.35;

        private const double MaxZoom = 1.6;

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

        public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
            nameof(IsLoading),
            typeof(bool),
            typeof(AvatarPreviewControl),
            new PropertyMetadata(false)
        );

        private double _yaw;

        private double _pitch = DefaultPitch;

        private double _zoom = 0.82;

        private Point _lastMouse;

        private bool _orbiting;

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
            Cursor = show ? Cursors.SizeAll : null;
            if (scene == null)
                return;
            if (resetView)
            {
                _yaw = scene.Yaw;
                _pitch = DefaultPitch;
            }
            UpdateCamera(scene);
        }

        private void UpdateCamera(AvatarScene scene)
        {
            Rect3D bounds = scene.Bounds;
            Point3D center = new(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
            double radius = Math.Max(0.5 * Math.Sqrt(bounds.SizeX * bounds.SizeX + bounds.SizeY * bounds.SizeY + bounds.SizeZ * bounds.SizeZ), 0.01);
            double distance = radius / Math.Sin(Camera.FieldOfView * Math.PI / 360.0) * _zoom;
            double yaw = _yaw * Math.PI / 180.0;
            double pitch = _pitch * Math.PI / 180.0;
            Vector3D offset = new(Math.Sin(yaw) * Math.Cos(pitch) * distance, Math.Sin(pitch) * distance, Math.Cos(yaw) * Math.Cos(pitch) * distance);
            Camera.Position = center + offset;
            Camera.LookDirection = -offset;
            Camera.NearPlaneDistance = Math.Max(distance - radius * 1.5, radius * 0.02);
            Camera.FarPlaneDistance = distance + radius * 3.0;
        }

        private void OnPreviewDown(object sender, MouseButtonEventArgs e)
        {
            if (!Supports3D || Scene == null)
                return;
            _lastMouse = e.GetPosition(this);
            _orbiting = CaptureMouse();
        }

        private void OnPreviewMove(object sender, MouseEventArgs e)
        {
            if (!_orbiting || Scene is not AvatarScene scene)
                return;
            Point position = e.GetPosition(this);
            _yaw -= (position.X - _lastMouse.X) * 0.6;
            _pitch = Math.Clamp(_pitch + (position.Y - _lastMouse.Y) * 0.4, -35.0, 60.0);
            _lastMouse = position;
            UpdateCamera(scene);
        }

        private void OnPreviewUp(object sender, MouseButtonEventArgs e)
        {
            if (_orbiting)
                ReleaseMouseCapture();
        }

        private void OnPreviewLostCapture(object sender, MouseEventArgs e)
        {
            _orbiting = false;
        }

        private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
        {
            if (!Supports3D || Scene is not AvatarScene scene)
                return;
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 0.9 : 1.1), MinZoom, MaxZoom);
            UpdateCamera(scene);
            e.Handled = true;
        }
    }
}
