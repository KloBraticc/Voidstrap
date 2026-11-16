using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Integrations.Overlays;

internal sealed partial class RobloxCursorOverlay : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExLayered = 0x80000;
    private const long WsExNoActivate = 0x8000000;
    private const int CursorShowing = 0x1;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const uint SwpNoOwnerZOrder = 0x200;
    private static readonly IntPtr HwndTopmost = new(-1);

    private static RobloxCursorOverlay? _instance;

    private readonly Image _image;
    private readonly IDisposable _trackerLease;
    private IntPtr _handle;
    private int _pixelWidth;
    private int _pixelHeight;
    private int _hotspotX;
    private int _hotspotY;
    private int _lastLeft = int.MinValue;
    private int _lastTop = int.MinValue;
    private bool _shown;
    private bool _closed;

    internal static void Reconcile()
    {
        bool wanted = CursorManager.OverrideActive && OverlayHub.RobloxProcessRunning && !OverlayHub.ShuttingDown;
        if (wanted && _instance == null)
        {
            try
            {
                _instance = new RobloxCursorOverlay();
                _instance.Show();
                App.Logger.WriteLine("RobloxCursorOverlay::Reconcile", "Showing the Roblox cursor override overlay");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RobloxCursorOverlay::Reconcile", ex);
                _instance = null;
            }
        }
        else if (!wanted && _instance != null)
        {
            _instance.Close();
            App.Logger.WriteLine("RobloxCursorOverlay::Reconcile", "Closed the Roblox cursor override overlay");
        }
    }

    private RobloxCursorOverlay()
    {
        Title = "Voidstrap Cursor";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Opacity = 0;
        Left = -32000;
        Top = -32000;
        _image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Content = _image;
        _trackerLease = RobloxWindowTracker.Acquire();
        SourceInitialized += OnOverlaySourceInitialized;
        Closed += OnOverlayClosed;
        LoadCursor();
    }

    private void LoadCursor()
    {
        const string LOG_IDENT = "RobloxCursorOverlay::LoadCursor";
        (IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames, double hotspotX, double hotspotY, int width) = CursorManager.LoadOverrideCursor();
        GifImageBehavior.SetFrames(_image, frames);
        if (frames.Count == 0)
        {
            App.Logger.WriteLine(LOG_IDENT, "No cursor image could be loaded");
            _pixelWidth = _pixelHeight = 1;
            ApplySize();
            return;
        }
        BitmapSource first = frames[0].Frame;
        _pixelWidth = width;
        _pixelHeight = Math.Max(1, (int)Math.Round(width * first.PixelHeight / (double)Math.Max(1, first.PixelWidth)));
        _hotspotX = (int)Math.Round(hotspotX * _pixelWidth);
        _hotspotY = (int)Math.Round(hotspotY * _pixelHeight);
        ApplySize();
        App.Logger.WriteLine(LOG_IDENT, "Loaded " + frames.Count + " cursor frame(s) at " + _pixelWidth + "x" + _pixelHeight);
    }

    private void ApplySize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Width = _pixelWidth / dpi.DpiScaleX;
        Height = _pixelHeight / dpi.DpiScaleY;
        _image.Width = Width;
        _image.Height = Height;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplySize();
    }

    private void OnOverlaySourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        long style = GetWindowLongPtrW(_handle, GwlExStyle).ToInt64();
        SetWindowLongPtrW(_handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExLayered | WsExNoActivate));
        ApplySize();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_closed || _handle == IntPtr.Zero)
            return;
        bool show = TryGetPointer(out int x, out int y);
        if (show)
        {
            int left = x - _hotspotX;
            int top = y - _hotspotY;
            if (left != _lastLeft || top != _lastTop || !_shown)
            {
                SetWindowPos(_handle, HwndTopmost, left, top, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | (_shown ? SwpNoZOrder : 0));
                _lastLeft = left;
                _lastTop = top;
            }
        }
        if (show == _shown)
            return;
        _shown = show;
        Opacity = show ? 1 : 0;
    }

    private static bool TryGetPointer(out int x, out int y)
    {
        x = y = 0;
        RobloxWindowRect rect = RobloxWindowTracker.Current;
        if (!rect.Valid || rect.Hwnd == IntPtr.Zero || GetForegroundWindow() != rect.Hwnd)
            return false;
        CursorInfo info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref info) || (info.Flags & CursorShowing) == 0)
            return false;
        if (!GetClientRect(rect.Hwnd, out NativeRect client))
            return false;
        NativePoint origin = default;
        if (!ClientToScreen(rect.Hwnd, ref origin))
            return false;
        x = info.X;
        y = info.Y;
        return x >= origin.X && y >= origin.Y && x < origin.X + client.Right && y < origin.Y + client.Bottom;
    }

    private void OnOverlayClosed(object? sender, EventArgs e)
    {
        _closed = true;
        CompositionTarget.Rendering -= OnRendering;
        SourceInitialized -= OnOverlaySourceInitialized;
        Closed -= OnOverlayClosed;
        GifImageBehavior.SetFrames(_image, []);
        _trackerLease.Dispose();
        if (ReferenceEquals(_instance, this))
            _instance = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Cursor;
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", EntryPoint = "GetCursorInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorInfo(ref CursorInfo info);

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
}
