using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Voidstrap.UI
{
    public sealed class ControllerCrosshairAdorner : Adorner
    {
        private Point _pos;
        private double _pulse;
        private double _breath;
        private readonly Pen _outline;
        private readonly Pen _ring;
        private readonly Brush _fill;

        public ControllerCrosshairAdorner(UIElement adorned, Color accent)
            : base(adorned)
        {
            IsHitTestVisible = false;
            _outline = new Pen(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)), 4.5);
            _outline.Freeze();
            _ring = new Pen(new SolidColorBrush(accent), 2.4);
            _ring.Freeze();
            _fill = new SolidColorBrush(Color.FromArgb(36, accent.R, accent.G, accent.B));
            _fill.Freeze();
        }

        public void SetPosition(Point p)
        {
            _pos = p;
            InvalidateVisual();
        }

        public void Pulse()
        {
            _pulse = 1.0;
        }

        public void Advance(double dt)
        {
            _breath += dt * 2.4;
            if (_breath > Math.PI * 2.0)
                _breath -= Math.PI * 2.0;
            if (_pulse > 0.0)
                _pulse = Math.Max(0.0, _pulse - dt * 5.0);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double r = 12.0 + Math.Sin(_breath) * 1.6 + _pulse * 7.0;
            dc.DrawEllipse(_fill, _outline, _pos, r, r);
            dc.DrawEllipse(null, _ring, _pos, r, r);
        }
    }

    public static partial class ControllerService
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static partial uint XInputGetState14(uint index, ref XINPUT_STATE state);

        [LibraryImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static partial uint XInputGetState91(uint index, ref XINPUT_STATE state);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorPos(out POINT p);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetCursorPos(int x, int y);

        [LibraryImport("user32.dll")]
        private static partial void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [LibraryImport("user32.dll")]
        private static partial short GetAsyncKeyState(int vKey);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        private const ushort DPAD_UP = 0x0001;
        private const ushort DPAD_DOWN = 0x0002;
        private const ushort DPAD_LEFT = 0x0004;
        private const ushort DPAD_RIGHT = 0x0008;
        private const ushort START = 0x0010;
        private const ushort BACK = 0x0020;
        private const ushort LB = 0x0100;
        private const ushort RB = 0x0200;
        private const ushort A = 0x1000;
        private const ushort B = 0x2000;
        private const ushort X = 0x4000;
        private const ushort Y = 0x8000;

        private const short StickDeadzone = 8000;
        private const double CursorBaseSpeed = 1400.0;
        private const double CursorCurveExponent = 1.8;
        private const double CursorRampBonus = 1.6;
        private const double CursorRampSeconds = 0.9;
        private const double PrecisionTriggerScale = 0.3;
        private const double TurboTriggerScale = 2.0;

        private static Vector _velocity;
        private static double _rampTime;

        private static bool _initialized;
        private static bool _rendering;
        private static bool _use14 = true;
        private static bool _xinputMissing;

        private static DispatcherTimer? _watchdog;
        private static ushort _lastButtons;
        private static TimeSpan _lastRenderTime;

        private static Window _activeWindow;
        private static UIElement _adornedRoot;
        private static AdornerLayer _adornerLayer;
        private static ControllerCrosshairAdorner _crosshair;
        private static Point _cursor;
        private static Point _drawPos;

        private static bool _mouseMode = true;
        private static POINT _lastMousePos;
        private static bool _haveMousePos;
        private static long _ignoreMouseUntil;
        private static bool _holdingClick;

        public static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            try
            {
                GetCursorPos(out _lastMousePos);
                _haveMousePos = true;
            }
            catch
            {
            }
            _watchdog = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _watchdog.Tick += OnWatchdog;
            _watchdog.Start();
            InputManager.Current.PreProcessInput += OnPreProcessInput;
        }

        public static void Shutdown()
        {
            if (!_initialized)
                return;
            _initialized = false;
            StopRendering();
            DetachCrosshair();
            InputManager.Current.PreProcessInput -= OnPreProcessInput;
            if (_watchdog != null)
            {
                _watchdog.Stop();
                _watchdog.Tick -= OnWatchdog;
                _watchdog = null;
            }
            _lastButtons = 0;
            _lastRenderTime = TimeSpan.Zero;
            _velocity = new Vector();
            _rampTime = 0.0;
        }

        private static bool TryGetState(ref XINPUT_STATE state)
        {
            if (_xinputMissing)
                return false;
            try
            {
                uint result = _use14 ? XInputGetState14(0, ref state) : XInputGetState91(0, ref state);
                return result == 0u;
            }
            catch (DllNotFoundException)
            {
                if (_use14)
                {
                    _use14 = false;
                    try
                    {
                        return XInputGetState91(0, ref state) == 0u;
                    }
                    catch
                    {
                        _xinputMissing = true;
                        return false;
                    }
                }
                _xinputMissing = true;
                return false;
            }
            catch
            {
                return false;
            }
        }

        private static void OnWatchdog(object sender, EventArgs e)
        {
            if (_xinputMissing)
            {
                _watchdog?.Stop();
                return;
            }
            XINPUT_STATE state = default;
            bool connected = TryGetState(ref state);
            if (!connected)
            {
                if (_rendering)
                {
                    StopRendering();
                    DetachCrosshair();
                }
                return;
            }
            if (_rendering)
                return;
            bool stickActive = Math.Abs(state.Gamepad.sThumbLX) > StickDeadzone || Math.Abs(state.Gamepad.sThumbLY) > StickDeadzone || Math.Abs(state.Gamepad.sThumbRY) > StickDeadzone;
            if (stickActive || state.Gamepad.wButtons != 0)
            {
                _rendering = true;
                _lastRenderTime = TimeSpan.Zero;
                _lastButtons = state.Gamepad.wButtons;
                _mouseMode = false;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        private static void StopRendering()
        {
            _rendering = false;
            CompositionTarget.Rendering -= OnRendering;
        }

        private static void OnRendering(object sender, EventArgs e)
        {
            double dt = 0.016;
            if (e is RenderingEventArgs rea)
            {
                if (rea.RenderingTime == _lastRenderTime)
                    return;
                if (_lastRenderTime != TimeSpan.Zero)
                    dt = Math.Min(0.05, Math.Max(0.001, (rea.RenderingTime - _lastRenderTime).TotalSeconds));
                _lastRenderTime = rea.RenderingTime;
            }

            XINPUT_STATE state = default;
            if (!TryGetState(ref state))
            {
                StopRendering();
                DetachCrosshair();
                return;
            }

            bool mouseMoved = DetectMouseMovement();
            bool mouseClicked = PhysicalMouseButtonDown();
            if (Environment.TickCount64 >= _ignoreMouseUntil && (mouseMoved || mouseClicked))
            {
                _mouseMode = true;
                StopRendering();
                DetachCrosshair();
                return;
            }

            Window win = FindActiveWindow();
            if (win == null)
            {
                StopRendering();
                DetachCrosshair();
                _lastButtons = state.Gamepad.wButtons;
                return;
            }

            ushort buttons = state.Gamepad.wButtons;
            ushort pressed = (ushort)(buttons & ~_lastButtons);

            bool stickActive = Math.Abs(state.Gamepad.sThumbLX) > StickDeadzone || Math.Abs(state.Gamepad.sThumbLY) > StickDeadzone;
            if (stickActive || pressed != 0)
                _mouseMode = false;

            if (_mouseMode)
            {
                _lastButtons = buttons;
                StopRendering();
                return;
            }

            AttachCrosshair(win);

            bool clickDown = (buttons & (A | B)) != 0;
            if (clickDown && !_holdingClick)
                PressAtCursor();
            else if (!clickDown && _holdingClick)
                ReleaseClick();
            if (_holdingClick)
                _ignoreMouseUntil = Environment.TickCount64 + 400;
            if ((pressed & START) != 0)
                SendKey(Key.Enter);
            if ((pressed & BACK) != 0)
                SendKey(Key.Escape);
            if ((pressed & LB) != 0)
                CycleTabs(-1);
            if ((pressed & RB) != 0)
                CycleTabs(1);

            if ((pressed & DPAD_UP) != 0)
                MoveFocus(FocusNavigationDirection.Up);
            else if ((pressed & DPAD_DOWN) != 0)
                MoveFocus(FocusNavigationDirection.Down);
            else if ((pressed & DPAD_LEFT) != 0)
                MoveFocus(FocusNavigationDirection.Left);
            else if ((pressed & DPAD_RIGHT) != 0)
                MoveFocus(FocusNavigationDirection.Right);

            MoveCursor(state.Gamepad.sThumbLX, state.Gamepad.sThumbLY, state.Gamepad.bLeftTrigger, state.Gamepad.bRightTrigger, dt);
            if (_crosshair != null)
            {
                double t = Math.Min(1.0, dt * 18.0);
                _drawPos = new Point(_drawPos.X + (_cursor.X - _drawPos.X) * t, _drawPos.Y + (_cursor.Y - _drawPos.Y) * t);
                _crosshair.Advance(dt);
                _crosshair.SetPosition(_drawPos);
                SyncSystemCursor();
            }
            HandleScroll(state.Gamepad.sThumbRY, dt);

            _lastButtons = buttons;
        }

        private static bool DetectMouseMovement()
        {
            try
            {
                if (!GetCursorPos(out POINT p))
                    return false;
                if (!_haveMousePos)
                {
                    _lastMousePos = p;
                    _haveMousePos = true;
                    return false;
                }
                int dx = p.X - _lastMousePos.X;
                int dy = p.Y - _lastMousePos.Y;
                _lastMousePos = p;
                return (dx * dx + dy * dy) > 4;
            }
            catch
            {
                return false;
            }
        }

        private static void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
        {
            try
            {
                if (Environment.TickCount64 < _ignoreMouseUntil)
                    return;
                object input = e.StagingItem?.Input;
                if (input is MouseButtonEventArgs || input is MouseWheelEventArgs)
                {
                    _mouseMode = true;
                    DetachCrosshair();
                }
            }
            catch
            {
            }
        }

        private static void SyncSystemCursor()
        {
            try
            {
                if (_adornedRoot == null || _mouseMode)
                    return;
                Point screen = _adornedRoot.PointToScreen(_drawPos);
                int x = (int)Math.Round(screen.X);
                int y = (int)Math.Round(screen.Y);
                if (_haveMousePos && Math.Abs(x - _lastMousePos.X) <= 1 && Math.Abs(y - _lastMousePos.Y) <= 1)
                    return;
                SetCursorPos(x, y);
                GetCursorPos(out _lastMousePos);
                _haveMousePos = true;
            }
            catch
            {
            }
        }

        private static bool PhysicalMouseButtonDown()
        {
            try
            {
                return (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static Window FindActiveWindow()
        {
            try
            {
                if (Application.Current == null)
                    return null;
                foreach (Window w in Application.Current.Windows)
                {
                    if (w != null && w.IsActive && w.IsVisible && w.Content is UIElement)
                        return w;
                }
            }
            catch
            {
            }
            return null;
        }

        private static void AttachCrosshair(Window win)
        {
            if (_crosshair != null && ReferenceEquals(_activeWindow, win))
                return;
            DetachCrosshair();
            if (win.Content is not UIElement root)
                return;
            AdornerLayer layer = AdornerLayer.GetAdornerLayer(root);
            if (layer == null)
            {
                AdornerDecorator decorator = FindDescendant<AdornerDecorator>(win);
                if (decorator != null && decorator.Child != null)
                {
                    root = decorator.Child;
                    layer = decorator.AdornerLayer;
                }
            }
            if (layer == null)
                return;
            _activeWindow = win;
            _adornedRoot = root;
            _adornerLayer = layer;
            _crosshair = new ControllerCrosshairAdorner(root, ReadAccent());
            _adornerLayer.Add(_crosshair);
            _cursor = new Point(root.RenderSize.Width / 2.0, root.RenderSize.Height / 2.0);
            _drawPos = _cursor;
            _velocity = new Vector(0.0, 0.0);
            _rampTime = 0.0;
            _crosshair.SetPosition(_drawPos);
            try
            {
                Mouse.OverrideCursor = Cursors.None;
            }
            catch
            {
            }
        }

        private static void DetachCrosshair()
        {
            ReleaseClick();
            if (_crosshair != null)
            {
                try
                {
                    _adornerLayer?.Remove(_crosshair);
                }
                catch
                {
                }
            }
            try
            {
                Mouse.OverrideCursor = null;
            }
            catch
            {
            }
            _crosshair = null;
            _adornerLayer = null;
            _adornedRoot = null;
            _activeWindow = null;
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
                return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                    return match;
                T nested = FindDescendant<T>(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }

        private static Color ReadAccent()
        {
            try
            {
                Color c = Voidstrap.Utility.SystemAccent.GetGlassColor();
                if (c.A == 0 && c.R == 0 && c.G == 0 && c.B == 0)
                    return Colors.White;
                return Color.FromRgb(c.R, c.G, c.B);
            }
            catch
            {
                return Colors.White;
            }
        }

        private static void MoveCursor(short lx, short ly, byte leftTrigger, byte rightTrigger, double dt)
        {
            if (_crosshair == null || _adornedRoot == null)
                return;
            double nx = Normalize(lx);
            double ny = Normalize(ly);
            double mag = Math.Min(1.0, Math.Sqrt(nx * nx + ny * ny));

            Vector targetVelocity = new(0.0, 0.0);
            if (mag > 0.0)
            {
                if (mag > 0.92)
                    _rampTime += dt;
                else
                    _rampTime = Math.Max(0.0, _rampTime - dt * 2.0);
                double curve = Math.Pow(mag, CursorCurveExponent);
                double ramp = 1.0 + CursorRampBonus * Math.Min(1.0, _rampTime / CursorRampSeconds);
                double trigger = 1.0;
                if (leftTrigger > 40)
                    trigger = PrecisionTriggerScale;
                else if (rightTrigger > 40)
                    trigger = TurboTriggerScale;
                double speed = CursorBaseSpeed * curve * ramp * trigger;
                targetVelocity = new Vector(nx / mag * speed, -ny / mag * speed);
            }
            else
            {
                _rampTime = 0.0;
            }

            double rate = (targetVelocity.LengthSquared > _velocity.LengthSquared) ? 9.0 : 16.0;
            _velocity += (targetVelocity - _velocity) * Math.Min(1.0, dt * rate);

            if (mag == 0.0 && _velocity.Length < 2.0)
            {
                _velocity = new Vector(0.0, 0.0);
                return;
            }

            _cursor.X += _velocity.X * dt;
            _cursor.Y += _velocity.Y * dt;
            double w = _adornedRoot.RenderSize.Width;
            double h = _adornedRoot.RenderSize.Height;
            if (w > 0)
                _cursor.X = Math.Max(2, Math.Min(w - 2, _cursor.X));
            if (h > 0)
                _cursor.Y = Math.Max(2, Math.Min(h - 2, _cursor.Y));
        }

        private static double Normalize(short v)
        {
            if (v > StickDeadzone)
                return (v - StickDeadzone) / (32767.0 - StickDeadzone);
            if (v < -StickDeadzone)
                return (v + StickDeadzone) / (32767.0 - StickDeadzone);
            return 0.0;
        }

        private static void PressAtCursor()
        {
            try
            {
                if (_adornedRoot == null)
                    return;
                Point screen = _adornedRoot.PointToScreen(_drawPos);
                _ignoreMouseUntil = Environment.TickCount64 + 400;
                SetCursorPos((int)screen.X, (int)screen.Y);
                GetCursorPos(out _lastMousePos);
                _haveMousePos = true;
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                _holdingClick = true;
                _crosshair?.Pulse();
            }
            catch
            {
                ActivateAtCursorFallback();
            }
        }

        private static void ReleaseClick()
        {
            if (!_holdingClick)
                return;
            _holdingClick = false;
            try
            {
                _ignoreMouseUntil = Environment.TickCount64 + 400;
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
            }
            catch
            {
            }
        }

        private static void ActivateAtCursorFallback()
        {
            try
            {
                if (_adornedRoot == null)
                    return;
                DependencyObject hit = null;
                VisualTreeHelper.HitTest(
                    _adornedRoot,
                    null,
                    r =>
                    {
                        hit = r.VisualHit;
                        return HitTestResultBehavior.Stop;
                    },
                    new PointHitTestParameters(_cursor));

                UIElement target = FindActivatable(hit);
                if (target != null)
                {
                    target.Focus();
                    Activate(target);
                }
            }
            catch
            {
            }
        }

        private static UIElement FindActivatable(DependencyObject node)
        {
            while (node != null)
            {
                if (node is UIElement ue)
                {
                    if (node is ButtonBase || node is ToggleButton || node is Selector
                        || node is ListBoxItem || node is TabItem || node is MenuItem
                        || node is RepeatButton || node is CheckBox || node is RadioButton || node is ComboBox)
                    {
                        return ue;
                    }
                    AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(ue);
                    if (peer != null && (peer.GetPattern(PatternInterface.Invoke) != null || peer.GetPattern(PatternInterface.Toggle) != null || peer.GetPattern(PatternInterface.SelectionItem) != null))
                    {
                        return ue;
                    }
                }
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private static void Activate(UIElement fe)
        {
            try
            {
                if (fe == null)
                    return;
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(fe);
                if (peer != null)
                {
                    if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                    {
                        invoke.Invoke();
                        return;
                    }
                    if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                    {
                        toggle.Toggle();
                        return;
                    }
                    if (peer.GetPattern(PatternInterface.SelectionItem) is ISelectionItemProvider select)
                    {
                        select.Select();
                        return;
                    }
                    if (peer.GetPattern(PatternInterface.ExpandCollapse) is IExpandCollapseProvider expand)
                    {
                        if (expand.ExpandCollapseState == System.Windows.Automation.ExpandCollapseState.Collapsed)
                            expand.Expand();
                        else
                            expand.Collapse();
                        return;
                    }
                }
                SendKey(Key.Space);
            }
            catch
            {
            }
        }

        private static void MoveFocus(FocusNavigationDirection direction)
        {
            try
            {
                if (_activeWindow == null)
                    return;
                if (Keyboard.FocusedElement is not UIElement source)
                {
                    _activeWindow.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }
                else
                {
                    source.MoveFocus(new TraversalRequest(direction));
                }
                (Keyboard.FocusedElement as FrameworkElement)?.BringIntoView();
            }
            catch
            {
            }
        }

        private static void HandleScroll(short ry, double dt)
        {
            if (Math.Abs(ry) <= StickDeadzone)
                return;
            ScrollViewer viewer = FindScrollViewerUnderCursor();
            if (viewer == null)
                return;
            double delta = -Normalize(ry) * 1600.0 * dt;
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset + delta);
        }

        private static ScrollViewer FindScrollViewerUnderCursor()
        {
            try
            {
                DependencyObject node = Mouse.DirectlyOver as DependencyObject;
                while (node != null)
                {
                    if (node is ScrollViewer sv && sv.IsVisible && sv.ScrollableHeight > 0.0)
                        return sv;
                    node = ((node is Visual || node is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node));
                }
            }
            catch
            {
            }
            return null;
        }

        private static void SendKey(Key key)
        {
            try
            {
                if (_activeWindow == null)
                    return;
                IInputElement target = Keyboard.FocusedElement ?? _activeWindow;
                PresentationSource source = PresentationSource.FromVisual(_activeWindow);
                if (source == null)
                    return;
                target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
                target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });
            }
            catch
            {
            }
        }

        private static void CycleTabs(int delta)
        {
            try
            {
                Selector selector = FindVisibleSelector(_activeWindow);
                if (selector == null || selector.Items.Count == 0)
                    return;
                int index = selector.SelectedIndex + delta;
                if (index < 0)
                    index = selector.Items.Count - 1;
                else if (index >= selector.Items.Count)
                    index = 0;
                selector.SelectedIndex = index;
            }
            catch
            {
            }
        }

        private static Selector FindVisibleSelector(DependencyObject root)
        {
            if (root == null)
                return null;
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is TabControl tab && tab.IsVisible && tab.Items.Count > 1)
                    return tab;
                Selector nested = FindVisibleSelector(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }
    }
}
