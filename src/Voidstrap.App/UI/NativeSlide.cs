using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Voidstrap.UI;

// Slides a whole overlay window by moving the native window, instead of animating what is drawn inside it.
// A transparent WPF window that animates its content has to be redrawn and copied back from the graphics card
// on every frame, which stutters and slows the game down with it; moving the window lets Windows reuse the
// picture it already has. While it moves, the window is clipped to the game's area so it appears to come out of,
// and go back into, the game's edge.
internal sealed partial class NativeSlide
{
	private const uint SwpNoSize = 0x0001;
	private const uint SwpNoZOrder = 0x0004;
	private const uint SwpNoActivate = 0x0010;
	private const uint SwpNoOwnerZOrder = 0x0200;
	private readonly Window _window;
	private readonly Stopwatch _clock = new();
	private IntPtr _handle;
	private Point _from;
	private Point _to;
	private Int32Rect _clip;
	private int _width;
	private int _height;
	private double _length;
	private bool _easeOut;
	private Action? _finished;
	private bool _subscribed;
    private UIElement? _clippedContent;
    private Geometry? _originalClip;
    private RectangleGeometry? _portableClip;

	public NativeSlide(Window window)
	{
		_window = window;
	}

	public bool IsRunning => _subscribed;

	public static bool Supported => (Voidstrap.Utility.Platform.IsWindows || PortableOverlay.Active) && SystemParameters.ClientAreaAnimation;

	// Where the window is right now, in screen pixels
	public Point? CurrentPosition()
	{
		if (PortableOverlay.Active)
        {
            if (!_window.IsVisible)
                return null;
            nint native = PortableOverlay.Handle(_window);
            if (Voidstrap.Utility.Platform.IsLinux && Voidstrap.Platform.Linux.LinuxWindowInterop.TryGetWindowGeometry(native, out int left, out int top, out _, out _))
                return new Point(left, top);
            if (Voidstrap.Utility.Platform.IsMacOS && native != 0)
            {
                var frame = Voidstrap.Platform.MacOS.MacOSWindow.GetFrame(native);
                return new Point(frame.X, Voidstrap.Platform.MacOS.MacOSWindow.PrimaryScreenHeight() - frame.Y - frame.Height);
            }
            return new Point(_window.Left, _window.Top);
        }
		IntPtr handle = new WindowInteropHelper(_window).Handle;
		if (handle == IntPtr.Zero || !GetWindowRect(handle, out NativeRect rect))
			return null;
		return new Point(rect.Left, rect.Top);
	}

	// Moves from one screen position to another; clip is the area the window may be seen in, in screen pixels
	public bool Start(Point from, Point to, Int32Rect clip, TimeSpan length, bool easeOut, Action? finished)
	{
		Stop(false);
		if (PortableOverlay.Active)
        {
            _handle = PortableOverlay.Handle(_window);
            _width = (int)Math.Ceiling(_window.Width);
            _height = (int)Math.Ceiling(_window.Height);
        }
        else
        {
            _handle = new WindowInteropHelper(_window).Handle;
            if (_handle == IntPtr.Zero || !GetWindowRect(_handle, out NativeRect rect))
                return false;
            _width = rect.Right - rect.Left;
            _height = rect.Bottom - rect.Top;
        }
        if (_handle == IntPtr.Zero)
            return false;
		_from = from;
		_to = to;
		_clip = clip;
		_length = Math.Max(1, length.TotalMilliseconds);
		_easeOut = easeOut;
		_finished = finished;
		Place(from);
		_clock.Restart();
		CompositionTarget.Rendering += OnFrame;
		_subscribed = true;
		return true;
	}

	// Stops where it is; the clip is removed when asked so the window shows whole again
	public void Stop(bool removeClip)
	{
		if (_subscribed)
		{
			CompositionTarget.Rendering -= OnFrame;
			_subscribed = false;
		}
		_finished = null;
		if (removeClip)
			RemoveClip();
	}

	public void RemoveClip()
	{
        if (PortableOverlay.Active)
        {
            if (Voidstrap.Utility.Platform.IsLinux)
                Voidstrap.Platform.Linux.LinuxWindowInterop.TryClearShape(PortableOverlay.Handle(_window));
            if (_clippedContent != null)
                _clippedContent.Clip = _originalClip;
            _clippedContent = null;
            _originalClip = null;
            _portableClip = null;
            return;
        }
		IntPtr handle = _handle != IntPtr.Zero ? _handle : new WindowInteropHelper(_window).Handle;
		if (handle != IntPtr.Zero)
			_ = SetWindowRgn(handle, IntPtr.Zero, false);
	}

	private void OnFrame(object? sender, EventArgs e)
	{
		double progress = Math.Clamp(_clock.Elapsed.TotalMilliseconds / _length, 0, 1);
		// Cubic ease, out for arriving and in for leaving, no overshoot so it settles exactly once
		double eased = _easeOut ? 1 - Math.Pow(1 - progress, 3) : progress * progress * progress;
		Place(new Point(_from.X + (_to.X - _from.X) * eased, _from.Y + (_to.Y - _from.Y) * eased));
		if (progress < 1)
			return;
        if (PortableOverlay.Active)
        {
            _window.Left = _to.X;
            _window.Top = _to.Y;
        }
		Action? finished = _finished;
		Stop(false);
		try
		{
			finished?.Invoke();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("NativeSlide", "Finishing a slide failed: " + ex.Message);
		}
	}

	private void Place(Point position)
	{
		if (_handle == IntPtr.Zero)
			return;
		int left = (int)Math.Round(position.X);
		int top = (int)Math.Round(position.Y);
		// Only the part of the window inside the game's area is shown while it moves
		int x0 = Math.Clamp(_clip.X - left, 0, _width);
		int y0 = Math.Clamp(_clip.Y - top, 0, _height);
		int x1 = Math.Clamp(_clip.X + _clip.Width - left, 0, _width);
		int y1 = Math.Clamp(_clip.Y + _clip.Height - top, 0, _height);
		if (PortableOverlay.Active)
        {
            if (Voidstrap.Utility.Platform.IsLinux)
            {
                Voidstrap.Platform.Linux.LinuxWindowInterop.TryMoveResize(_handle, left, top, _width, _height);
                Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetBoundingRectangles(_handle, new[] { (x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0)) });
            }
            else
            {
                Voidstrap.Platform.MacOS.MacOSOverlayWindow.MoveTo(_handle, left, top, _width, _height);
                UIElement content = _window;
                if (_portableClip == null)
                {
                    _clippedContent = content;
                    _originalClip = content.Clip;
                    _portableClip = new RectangleGeometry();
                    content.Clip = _originalClip == null ? _portableClip
                        : new CombinedGeometry(GeometryCombineMode.Intersect, _originalClip, _portableClip);
                }
                _portableClip.Rect = new Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
            }
            Voidstrap.Integrations.Overlays.LinuxOverlaySurface.WakePresentation(_window);
            return;
        }
		IntPtr region = CreateRectRgn(x0, y0, Math.Max(x0, x1), Math.Max(y0, y1));
		if (region != IntPtr.Zero && SetWindowRgn(_handle, region, false) == 0)
			_ = DeleteObject(region);
		_ = SetWindowPos(_handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

	[LibraryImport("user32.dll")]
	private static partial int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

	[LibraryImport("gdi32.dll")]
	private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

	[LibraryImport("gdi32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteObject(IntPtr handle);
}
