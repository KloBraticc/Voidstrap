using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Message = Voidstrap.Models.VoidstrapRPC.Message;

namespace Voidstrap.Integrations;

public sealed partial class WindowController : IDisposable
{
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_FRAME = 0x00C00000L | 0x00040000L | 0x00080000L | 0x00020000L | 0x00010000L;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;
    private const int MinWidth = 200;
    private const int MinHeight = 150;
    private const byte MinAlpha = 26;
    private const int MaxTitleLength = 256;

    private readonly ActivityWatcher _activityWatcher;
    private readonly Func<IntPtr> _findWindow;
    private readonly Action<IntPtr, string?> _setTitle;
    private readonly object _gate = new();
    private IntPtr _hwnd;
    private bool _saved;
    private Rect _originalRect;
    private nint _originalStyle;
    private nint _originalExStyle;
    private Rect _current;
    private bool _titleChanged;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private sealed class WindowMessage
    {
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public double? ScaleWidth { get; set; }
        public double? ScaleHeight { get; set; }
        public bool? Reset { get; set; }
    }

    private sealed class TransparencyMessage
    {
        public double? Transparency { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public WindowController(ActivityWatcher activityWatcher, Func<IntPtr> findWindow, Action<IntPtr, string?> setTitle)
    {
        _activityWatcher = activityWatcher;
        _findWindow = findWindow;
        _setTitle = setTitle;
        _activityWatcher.OnRPCMessage += OnRpcMessage;
        _activityWatcher.OnGameLeave += OnGameLeave;
    }

    private void OnGameLeave(object? sender, EventArgs e)
    {
        lock (_gate)
            Restore();
    }

    private void OnRpcMessage(object? sender, Message message)
    {
        const string LOG_IDENT = "WindowController::OnRpcMessage";
        var prop = App.Settings.Prop;
        if (!prop.WindowControlEnabled || !Voidstrap.Utility.Platform.IsWindows)
            return;
        try
        {
            lock (_gate)
            {
                if (_disposed || !EnsureWindow())
                    return;
                switch (message.Command)
                {
                    case "SetWindow" when prop.WindowControlMove:
                        SetWindow(Read<WindowMessage>(message));
                        break;
                    case "ResetWindow" when prop.WindowControlMove:
                        RestoreBounds();
                        break;
                    case "SetWindowBorderless" when prop.WindowControlMove:
                        SetBorderless(Read<bool>(message));
                        break;
                    case "SetWindowTitle" when prop.WindowControlTitle:
                        SetTitle(message.Data.ValueKind == JsonValueKind.String ? message.Data.GetString() : null);
                        break;
                    case "SetWindowTransparency" when prop.WindowControlTransparency:
                        SetTransparency(Read<TransparencyMessage>(message)?.Transparency);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, message.Command + " failed: " + ex.Message);
        }
    }

    private static T? Read<T>(Message message)
    {
        try
        {
            return message.Data.Deserialize<T>(JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private bool EnsureWindow()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            return true;
        _hwnd = _findWindow();
        _saved = false;
        _titleChanged = false;
        return _hwnd != IntPtr.Zero;
    }

    private void Save()
    {
        if (_saved)
            return;
        GetWindowRect(_hwnd, out _originalRect);
        _current = _originalRect;
        _originalStyle = GetWindowLongPtrW(_hwnd, GWL_STYLE);
        _originalExStyle = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE);
        _saved = true;
    }

    private void SetWindow(WindowMessage? data)
    {
        if (data == null)
            return;
        if (data.Reset == true)
        {
            RestoreBounds();
            return;
        }
        Save();
        System.Drawing.Rectangle screen = Screen.FromHandle(_hwnd).Bounds;
        double scaleX = data.ScaleWidth > 0 ? screen.Width / data.ScaleWidth.Value : 1;
        double scaleY = data.ScaleHeight > 0 ? screen.Height / data.ScaleHeight.Value : 1;
        int width = data.Width is double w ? (int)(w * scaleX) : _current.Right - _current.Left;
        int height = data.Height is double h ? (int)(h * scaleY) : _current.Bottom - _current.Top;
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, vw));
        height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, vh));
        int x = data.X is double px ? screen.X + (int)(px * scaleX) : _current.Left;
        int y = data.Y is double py ? screen.Y + (int)(py * scaleY) : _current.Top;
        x = Math.Clamp(x, vx - width + MinWidth, Math.Max(vx - width + MinWidth, vx + vw - MinWidth));
        y = Math.Clamp(y, vy, Math.Max(vy, vy + vh - MinHeight));
        Move(x, y, width, height);
    }

    private void Move(int x, int y, int width, int height)
    {
        if (_current.Left == x && _current.Top == y && _current.Right - _current.Left == width && _current.Bottom - _current.Top == height)
            return;
        _current = new Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
        SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    private void SetBorderless(bool borderless)
    {
        Save();
        nint style = borderless ? (nint)((long)_originalStyle & ~WS_FRAME) : _originalStyle;
        SetWindowLongPtrW(_hwnd, GWL_STYLE, style);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_ASYNCWINDOWPOS);
    }

    private void SetTitle(string? title)
    {
        title = title?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            if (_titleChanged)
                _setTitle(_hwnd, null);
            _titleChanged = false;
            return;
        }
        if (title.Length > MaxTitleLength)
            title = title[..MaxTitleLength];
        _titleChanged = true;
        _setTitle(_hwnd, title);
    }

    private void SetTransparency(double? transparency)
    {
        if (transparency is not double value || double.IsNaN(value))
            return;
        Save();
        byte alpha = (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        if (alpha >= 255)
        {
            SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, _originalExStyle);
            return;
        }
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, (nint)(((long)_originalExStyle | WS_EX_LAYERED) & ~WS_EX_TRANSPARENT));
        SetLayeredWindowAttributes(_hwnd, 0, Math.Max(alpha, MinAlpha), LWA_ALPHA);
    }

    private void RestoreBounds()
    {
        if (!_saved)
            return;
        Move(_originalRect.Left, _originalRect.Top, _originalRect.Right - _originalRect.Left, _originalRect.Bottom - _originalRect.Top);
    }

    private void Restore()
    {
        const string LOG_IDENT = "WindowController::Restore";
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
        {
            _hwnd = IntPtr.Zero;
            _saved = false;
            _titleChanged = false;
            return;
        }
        try
        {
            if (_saved)
            {
                SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, _originalExStyle);
                SetWindowLongPtrW(_hwnd, GWL_STYLE, _originalStyle);
                RestoreBounds();
                SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_ASYNCWINDOWPOS);
            }
            if (_titleChanged)
                _setTitle(_hwnd, null);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, ex.Message);
        }
        _saved = false;
        _titleChanged = false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _activityWatcher.OnRPCMessage -= OnRpcMessage;
            _activityWatcher.OnGameLeave -= OnGameLeave;
            Restore();
        }
        GC.SuppressFinalize(this);
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtrW(IntPtr hWnd, int nIndex, nint dwNewLong);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hWnd);
}
