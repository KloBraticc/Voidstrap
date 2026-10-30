using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;

namespace Voidstrap.Integrations.Plugins;

public static class PluginCursor
{
    private static readonly object Gate = new();
    private static double _lastX;
    private static double _lastY;
    private static double _lastScreenX;
    private static double _lastScreenY;
    private static double _lastWindowWidth;
    private static double _lastWindowHeight;
    private static bool _lastInsideWindow;
    private static bool _lastIsDown;
    private static string _lastButton = "none";
    private static string _lastWindowTitle = "";
    private static bool _hasState;
    private static long _lastMoveDispatched;
    private static long _lastScrollDispatched;

    private static int _moveInFlight;

    private static int _moveDirty;
    private static int _scrollInFlight;
    private static bool _installed;

    private static volatile bool _hasMoveHook;
    private static volatile bool _hasDownHook;
    private static volatile bool _hasUpHook;
    private static volatile bool _hasEnterHook;
    private static volatile bool _hasLeaveHook;
    private static volatile bool _hasScrollHook;

    private const long MoveThrottleMs = 16;

    private const long ScrollThrottleMs = 80;

    public static void Start()
    {
        bool first;
        lock (Gate)
        {
            first = !_installed;
            _installed = true;
        }
        RefreshHooks();
        if (!first)
            return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent, new MouseEventHandler(OnWindowMouseMove), true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnWindowMouseDown), true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(OnWindowMouseUp), true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.MouseEnterEvent, new MouseEventHandler(OnWindowMouseEnter), true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.MouseLeaveEvent, new MouseEventHandler(OnWindowMouseLeave), true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWindowMouseWheel), true);
        });
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            _hasState = false;
            _hasMoveHook = false;
            _hasDownHook = false;
            _hasUpHook = false;
            _hasEnterHook = false;
            _hasLeaveHook = false;
        }
    }

    public static void RefreshHooks()
    {
        _hasMoveHook = PluginHost.HasHook("cursor.move");
        _hasDownHook = PluginHost.HasHook("cursor.down");
        _hasUpHook = PluginHost.HasHook("cursor.up");
        _hasEnterHook = PluginHost.HasHook("cursor.enter");
        _hasLeaveHook = PluginHost.HasHook("cursor.leave");
        _hasScrollHook = PluginHost.HasHook("cursor.scroll");
    }

    public static JsonObject Get()
    {
        lock (Gate)
        {
            if (_hasState)
                return Snapshot();
        }

        if (Application.Current != null && Application.Current.Dispatcher.CheckAccess())
        {
            CaptureCurrentPosition();
            lock (Gate)
            {
                return Snapshot();
            }
        }

        Application.Current?.Dispatcher.BeginInvoke(new Action(CaptureCurrentPosition));
        lock (Gate)
        {
            return Snapshot();
        }
    }

    private static void CaptureCurrentPosition()
    {
        Window? window = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? Application.Current?.MainWindow;
        if (window == null || !window.IsVisible)
            return;

        try
        {
            Point pos = Mouse.GetPosition(window);
            bool isDown = Mouse.LeftButton == MouseButtonState.Pressed;
            string button = ResolveButton(Mouse.LeftButton, Mouse.RightButton, Mouse.MiddleButton);
            UpdateState(window, pos, isDown, button);
        }
        catch (Exception)
        {
        }
    }

    private static string ResolveButton(MouseButtonState left, MouseButtonState right, MouseButtonState middle)
    {
        if (left == MouseButtonState.Pressed)
            return "left";
        if (right == MouseButtonState.Pressed)
            return "right";
        if (middle == MouseButtonState.Pressed)
            return "middle";
        return "none";
    }

    private static void UpdateState(Window window, Point pos, bool isDown, string button)
    {
        double width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        bool inside = pos.X >= 0 && pos.Y >= 0 && pos.X <= width && pos.Y <= height;
        string title = window.Title ?? "";
        if (title.Length > 120)
            title = title[..120];

        Point screenPos = new(pos.X + (double.IsNaN(window.Left) ? 0 : window.Left), pos.Y + (double.IsNaN(window.Top) ? 0 : window.Top));
        try
        {
            screenPos = window.PointToScreen(pos);
        }
        catch (Exception)
        {
        }

        lock (Gate)
        {
            _lastX = pos.X;
            _lastY = pos.Y;
            _lastScreenX = screenPos.X;
            _lastScreenY = screenPos.Y;
            _lastWindowWidth = width;
            _lastWindowHeight = height;
            _lastInsideWindow = inside;
            _lastIsDown = isDown;
            _lastButton = button;
            _lastWindowTitle = title;
            _hasState = true;
        }
    }

    private static JsonObject Snapshot()
    {
        return new JsonObject
        {
            ["ready"] = _hasState,
            ["x"] = Math.Round(_lastX, 2),
            ["y"] = Math.Round(_lastY, 2),
            ["screenX"] = Math.Round(_lastScreenX, 2),
            ["screenY"] = Math.Round(_lastScreenY, 2),
            ["windowWidth"] = Math.Round(_lastWindowWidth, 2),
            ["windowHeight"] = Math.Round(_lastWindowHeight, 2),
            ["insideWindow"] = _lastInsideWindow,
            ["isDown"] = _lastIsDown,
            ["button"] = _lastButton,
            ["windowTitle"] = _lastWindowTitle
        };
    }

    private static void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not Window window)
            return;

        Point pos = e.GetPosition(window);
        bool isDown = e.LeftButton == MouseButtonState.Pressed;
        string button = ResolveButton(e.LeftButton, e.RightButton, e.MiddleButton);
        UpdateState(window, pos, isDown, button);

        if (!_hasMoveHook)
            return;

        Volatile.Write(ref _moveDirty, 1);
        if (Interlocked.CompareExchange(ref _moveInFlight, 1, 0) == 0)
            _ = Task.Run(PumpMovesAsync);
    }

    private static async Task PumpMovesAsync()
    {
        while (true)
        {
            while (Interlocked.Exchange(ref _moveDirty, 0) == 1)
            {
                long wait = MoveThrottleMs - (Environment.TickCount64 - _lastMoveDispatched);
                if (wait > 0)
                    await Task.Delay((int)wait).ConfigureAwait(false);
                _lastMoveDispatched = Environment.TickCount64;
                Volatile.Write(ref _moveDirty, 0);
                JsonObject snapshot;
                lock (Gate)
                {
                    snapshot = Snapshot();
                }
                try
                {
                    PluginHost.Invoke("cursor.move", snapshot);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("PluginCursor::PumpMovesAsync", ex.Message);
                }
            }
            Interlocked.Exchange(ref _moveInFlight, 0);
            if (Volatile.Read(ref _moveDirty) == 0 || Interlocked.CompareExchange(ref _moveInFlight, 1, 0) != 0)
                return;
        }
    }

    private static void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window window)
            return;

        Point pos = e.GetPosition(window);
        string button = e.ChangedButton switch
        {
            MouseButton.Left => "left",
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => "none"
        };
        UpdateState(window, pos, true, button);

        if (_hasDownHook)
            PluginHost.Invoke("cursor.down", Snapshot());
    }

    private static void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window window)
            return;

        Point pos = e.GetPosition(window);
        string button = e.ChangedButton switch
        {
            MouseButton.Left => "left",
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => "none"
        };
        UpdateState(window, pos, false, button);

        if (_hasUpHook)
            PluginHost.Invoke("cursor.up", Snapshot());
    }

    private static void OnWindowMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Window window)
            return;

        Point pos = e.GetPosition(window);
        UpdateState(window, pos, e.LeftButton == MouseButtonState.Pressed, ResolveButton(e.LeftButton, e.RightButton, e.MiddleButton));

        if (_hasEnterHook)
            PluginHost.Invoke("cursor.enter", Snapshot());
    }

    private static void OnWindowMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Window window)
            return;

        lock (Gate)
        {
            _lastInsideWindow = false;
        }

        if (_hasLeaveHook)
            PluginHost.Invoke("cursor.leave", Snapshot());
    }

    private static void OnWindowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not Window window)
            return;

        Point pos = e.GetPosition(window);
        UpdateState(window, pos, Mouse.LeftButton == MouseButtonState.Pressed, ResolveButton(Mouse.LeftButton, Mouse.RightButton, Mouse.MiddleButton));

        if (!_hasScrollHook)
            return;

        long now = Environment.TickCount64;
        if (now - _lastScrollDispatched < ScrollThrottleMs)
            return;
        if (Interlocked.CompareExchange(ref _scrollInFlight, 1, 0) != 0)
            return;
        _lastScrollDispatched = now;
        JsonObject snapshot = Snapshot();
        snapshot["delta"] = e.Delta;
        _ = Task.Run(() =>
        {
            try
            {
                PluginHost.Invoke("cursor.scroll", snapshot);
            }
            catch
            {
            }
            finally
            {
                Interlocked.Exchange(ref _scrollInFlight, 0);
            }
        });
    }
}
