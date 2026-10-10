using System;
using System.Runtime.InteropServices;
using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.Overlay;

// A plain black Win32 window dimmed through its layered alpha. A WPF window cannot be used here: WPF keeps
// drawing a non transparent window fully opaque even when it is layered, which turned the whole game black,
// and a transparent WPF window re-uploads a game sized bitmap on every frame of the fade.
internal sealed partial class SessionDimmer : IDisposable
{
    private const string ClassName = "VoidstrapSessionDimmer";
    private const int BlackBrush = 4;
    private static readonly object _classLock = new();
    private static AntiAliasingInterop.WndProcDelegate? _wndProc;
    private static ushort _classAtom;
    private IntPtr _hwnd;
    private bool _visible;
    private byte _alpha;

    public bool IsVisible => _visible;

    public SessionDimmer()
    {
        IntPtr instance = AntiAliasingInterop.GetModuleHandleW(null);
        ushort atom = EnsureClass(instance);
        if (atom == 0)
            return;
        int exStyle = AntiAliasingInterop.WS_EX_LAYERED | AntiAliasingInterop.WS_EX_TRANSPARENT | AntiAliasingInterop.WS_EX_NOACTIVATE
            | AntiAliasingInterop.WS_EX_TOOLWINDOW | AntiAliasingInterop.WS_EX_TOPMOST;
        _hwnd = AntiAliasingInterop.CreateWindowExW(exStyle, new IntPtr(atom), "Session dimmer", AntiAliasingInterop.WS_POPUP,
            0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            App.Logger.WriteLine("SessionDimmer", "The dimmer window could not be created, error " + Marshal.GetLastWin32Error());
            return;
        }
        AntiAliasingInterop.SetLayeredWindowAttributes(_hwnd, 0, 0, AntiAliasingInterop.LWA_ALPHA);
    }

    private static ushort EnsureClass(IntPtr instance)
    {
        lock (_classLock)
        {
            if (_classAtom != 0)
                return _classAtom;
            _wndProc = (hwnd, message, wParam, lParam) => AntiAliasingInterop.DefWindowProcW(hwnd, message, wParam, lParam);
            IntPtr name = Marshal.StringToHGlobalUni(ClassName);
            try
            {
                AntiAliasingInterop.WNDCLASSEXW wc = new()
                {
                    cbSize = (uint)Marshal.SizeOf<AntiAliasingInterop.WNDCLASSEXW>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = instance,
                    hbrBackground = GetStockObject(BlackBrush),
                    lpszClassName = name
                };
                _classAtom = AntiAliasingInterop.RegisterClassExW(ref wc);
                if (_classAtom == 0)
                    App.Logger.WriteLine("SessionDimmer", "The dimmer window class could not be registered, error " + Marshal.GetLastWin32Error());
                return _classAtom;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }
    }

    public void SetOpacity(double opacity)
    {
        if (_hwnd == IntPtr.Zero)
            return;
        byte alpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        if (alpha == _alpha)
            return;
        _alpha = alpha;
        AntiAliasingInterop.SetLayeredWindowAttributes(_hwnd, 0, alpha, AntiAliasingInterop.LWA_ALPHA);
    }

    public void Show(RobloxWindowRect bounds)
    {
        if (_hwnd == IntPtr.Zero || !bounds.Valid)
            return;
        AntiAliasingInterop.SetWindowPos(_hwnd, AntiAliasingInterop.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            AntiAliasingInterop.SWP_NOACTIVATE | AntiAliasingInterop.SWP_SHOWWINDOW);
        _visible = true;
    }

    public void Place(RobloxWindowRect bounds)
    {
        if (_hwnd == IntPtr.Zero || !_visible || !bounds.Valid)
            return;
        AntiAliasingInterop.SetWindowPos(_hwnd, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            AntiAliasingInterop.SWP_NOACTIVATE | AntiAliasingInterop.SWP_NOZORDER);
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero || !_visible)
            return;
        AntiAliasingInterop.ShowWindow(_hwnd, AntiAliasingInterop.SW_HIDE);
        _visible = false;
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        AntiAliasingInterop.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _visible = false;
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr GetStockObject(int index);
}
