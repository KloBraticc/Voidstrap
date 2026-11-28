using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Voidstrap.Integrations.Keystrokes;

public static partial class KeystrokeInputService
{
    private const int RawInputMessage = 0x00FF;
    private static HwndSource? _rawMouseSource;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouseDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public nint Device;
        public nint Parameter;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct RawMouse
    {
        [FieldOffset(0)] public ushort Flags;
        [FieldOffset(12)] public int DeltaX;
        [FieldOffset(16)] public int DeltaY;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool RegisterRawInputDevices(RawMouseDevice* devices, uint count, uint size);

    [LibraryImport("user32.dll")]
    private static unsafe partial uint GetRawInputData(nint input, uint command, void* data, ref uint size, uint headerSize);

    private static unsafe void StartWindowsRawMouse()
    {
        const string LOG_IDENT = "KeystrokeInputService::StartWindowsRawMouse";

        HwndSource? source = null;
        try
        {
            source = new HwndSource(new HwndSourceParameters("Keystroke input") { ParentWindow = new IntPtr(-3) });
            source.AddHook(OnWindowsRawMouse);
            RawMouseDevice device = new() { UsagePage = 1, Usage = 2, Flags = 0x100, Target = source.Handle };
            if (RegisterRawInputDevices(&device, 1, (uint)sizeof(RawMouseDevice)))
            {
                _rawMouseSource = source;
                return;
            }
            App.Logger?.WriteLine(LOG_IDENT, "Relative mouse input registration returned " + Marshal.GetLastPInvokeError());
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LOG_IDENT, ex.Message);
        }

        source?.Dispose();
    }

    private static unsafe void StopWindowsRawMouse()
    {
        if (_rawMouseSource is not { } source)
            return;

        _rawMouseSource = null;
        RawMouseDevice device = new() { UsagePage = 1, Usage = 2, Flags = 1 };
        RegisterRawInputDevices(&device, 1, (uint)sizeof(RawMouseDevice));
        source.RemoveHook(OnWindowsRawMouse);
        source.Dispose();
    }

    private static unsafe nint OnWindowsRawMouse(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != RawInputMessage || !IsRobloxFocused())
            return 0;

        uint headerSize = (uint)sizeof(RawInputHeader);
        uint size = headerSize + (uint)sizeof(RawMouse);
        byte* buffer = stackalloc byte[(int)size];
        if (GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize) != headerSize + sizeof(RawMouse)
            || ((RawInputHeader*)buffer)->Type != 0)
            return 0;

        RawMouse* mouse = (RawMouse*)(buffer + headerSize);
        if ((mouse->Flags & 1) == 0 && (mouse->DeltaX != 0 || mouse->DeltaY != 0))
        {
            Interlocked.Add(ref _mouseDx, mouse->DeltaX);
            Interlocked.Add(ref _mouseDy, mouse->DeltaY);
        }
        return 0;
    }
}
