using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Platform.Linux;

namespace Voidstrap.UI;

internal static class LinuxOverlayToolTips
{
    private static readonly ConditionalWeakTable<Window, State> States = new();

    private static bool _installed;

    internal static void Attach(Window window)
    {
        if (!OperatingSystem.IsLinux() || window is TipWindow)
            return;
        if (!_installed)
        {
            _installed = true;
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipOpeningEvent, new ToolTipEventHandler(OnOpening));
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.ToolTipClosingEvent, new ToolTipEventHandler(OnClosing));
        }
        if (!States.TryGetValue(window, out _))
            States.Add(window, new State(window));
    }

    private static void OnOpening(object sender, ToolTipEventArgs e)
    {
        if (!e.Handled && sender is FrameworkElement target && Window.GetWindow(target) is Window owner
            && States.TryGetValue(owner, out State? state))
            state.Show(target, e);
    }

    private static void OnClosing(object sender, ToolTipEventArgs e)
    {
        if (sender is FrameworkElement target && Window.GetWindow(target) is Window owner
            && States.TryGetValue(owner, out State? state))
            state.Hide();
    }

    internal static void CloseAll()
    {
        foreach (var entry in States)
            entry.Value.Hide();
    }

    private sealed class TipWindow : Window
    {
        internal readonly TextBlock Label = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        internal readonly Border Face;

        internal TipWindow()
        {
            Title = "Voidstrap tooltip";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            SizeToContent = SizeToContent.Manual;
            SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
            Label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            Face = new Border { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(1), Child = Label };
            Face.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
            Face.SetResourceReference(Border.BorderBrushProperty, "SurfaceStrokeColorDefaultBrush");
            Content = Face;
            PortableOverlay.Prepare(this);
            SourceInitialized += (_, _) => LinuxWindowInterop.TrySetClickThrough(PortableOverlay.Handle(this));
            Closed += (_, _) => PortableOverlay.Release(this);
        }
    }

    private sealed class State
    {
        private readonly Window _owner;
        private readonly DispatcherTimer _duration;
        private FrameworkElement? _target;
        private TipWindow? _tip;

        internal State(Window owner)
        {
            _owner = owner;
            _duration = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher);
            _duration.Tick += OnDuration;
            owner.PreviewMouseDown += OnMouseDown;
            owner.PreviewMouseWheel += OnMouseWheel;
            owner.IsVisibleChanged += OnVisibleChanged;
            owner.LocationChanged += OnLocationChanged;
            owner.Closed += OnClosed;
        }

        internal void Show(FrameworkElement target, ToolTipEventArgs e)
        {
            if (!_owner.IsVisible)
                return;
            object content = ToolTipService.GetToolTip(target);
            if (content is ToolTip tooltip)
                content = tooltip.Content;
            if (content is not string text || string.IsNullOrWhiteSpace(text))
                return;
            e.Handled = true;
            Hide();
            try
            {
                _tip ??= new TipWindow();
                _tip.FontFamily = _owner.FontFamily;
                _tip.Label.Text = text;
                RobloxWindowRect game = RobloxWindowTracker.Current;
                double maximumWidth = game.Valid ? Math.Min(320, Math.Max(1, game.Width - 12)) : 320;
                _tip.Face.MaxWidth = maximumWidth;
                _tip.Face.Measure(new Size(maximumWidth, double.PositiveInfinity));
                Size size = _tip.Face.DesiredSize;
                Point point = target.TransformToAncestor(_owner).Transform(new Point(target.ActualWidth / 2, 0));
                if (LinuxWindowInterop.TryGetWindowGeometry(PortableOverlay.Handle(_owner), out int ownerLeft, out int ownerTop, out _, out _))
                {
                    point.Offset(ownerLeft, ownerTop);
                    if (_owner is Elements.Overlay.SessionDock)
                        point.Y = ownerTop;
                }
                else
                    point = target.PointToScreen(new Point(target.ActualWidth / 2, 0));
                double left = point.X - size.Width / 2;
                double top = point.Y - size.Height - 6;
                if (game.Valid)
                {
                    left = Math.Clamp(left, game.Left + 6, Math.Max(game.Left + 6, game.Left + game.Width - size.Width - 6));
                    if (top < game.Top + 6)
                        top = Math.Min(game.Top + game.Height - size.Height - 6, point.Y + target.ActualHeight + 6);
                }
                PortableOverlay.Place(_tip, left, top, Math.Ceiling(size.Width), Math.Ceiling(size.Height));
                nint handle = PortableOverlay.Handle(_tip);
                LinuxWindowInterop.TrySetClickThrough(handle);
                LinuxWindowInterop.TrySetRoundedCorners(handle, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 6);
                _target = target;
                target.MouseLeave += OnTargetLeave;
                _duration.Interval = TimeSpan.FromMilliseconds(Math.Clamp(ToolTipService.GetShowDuration(target), 100, 10000));
                _duration.Start();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                Hide();
                App.Logger?.WriteLine("LinuxOverlayToolTips", "The hover label could not be shown: " + ex.Message);
            }
        }

        internal void Hide()
        {
            _duration.Stop();
            if (_target is not null)
                _target.MouseLeave -= OnTargetLeave;
            _target = null;
            if (_tip?.IsVisible is true)
                _tip.Hide();
        }

        private void OnTargetLeave(object sender, MouseEventArgs e) => Hide();
        private void OnDuration(object? sender, EventArgs e) => Hide();
        private void OnMouseDown(object sender, MouseButtonEventArgs e) => Hide();
        private void OnMouseWheel(object sender, MouseWheelEventArgs e) => Hide();
        private void OnLocationChanged(object? sender, EventArgs e) => Hide();
        private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is not true)
                Hide();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            Hide();
            _tip?.Close();
            _duration.Tick -= OnDuration;
            _owner.PreviewMouseDown -= OnMouseDown;
            _owner.PreviewMouseWheel -= OnMouseWheel;
            _owner.IsVisibleChanged -= OnVisibleChanged;
            _owner.LocationChanged -= OnLocationChanged;
            _owner.Closed -= OnClosed;
            States.Remove(_owner);
        }
    }
}
