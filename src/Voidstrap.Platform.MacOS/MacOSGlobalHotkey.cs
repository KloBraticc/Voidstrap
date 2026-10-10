using System.Runtime.InteropServices;

namespace Voidstrap.Platform.MacOS;

public sealed partial class MacOSGlobalHotkey : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private static int _nextId;
    private readonly Handler _callback;
    private readonly Action _pressed;
    private readonly HotkeyId _id;
    private nint _hotkey;
    private nint _handler;
    private bool _down;
    private bool _disposed;

    private MacOSGlobalHotkey(uint modifiers, uint key, Action pressed)
    {
        _pressed = pressed;
        _callback = OnEvent;
        _id = new HotkeyId { Signature = 0x56535452, Id = (uint)Interlocked.Increment(ref _nextId) };
        if (!OperatingSystem.IsMacOS() || KeyCode(key) is not uint code)
            return;
        ushort[] fallback = Enumerable.Range(0, 256).Select(value => (ushort)(KeyCode((uint)value) ?? ushort.MaxValue)).ToArray();
        using (MacOSKeyboardLayout layout = new())
        {
            layout.Refresh(fallback, 0, out ushort[] mapped);
            if (key < mapped.Length && mapped[key] != ushort.MaxValue)
                code = mapped[key];
        }
        EventType[] events = [new() { Class = 0x6B657962, Kind = 5 }, new() { Class = 0x6B657962, Kind = 6 }];
        if (InstallEventHandler(GetApplicationEventTarget(), _callback, 2, events, 0, out _handler) != 0)
            return;
        uint mask = ((modifiers & 1) != 0 ? 0x0800u : 0) | ((modifiers & 2) != 0 ? 0x1000u : 0)
            | ((modifiers & 4) != 0 ? 0x0200u : 0) | ((modifiers & 8) != 0 ? 0x0100u : 0);
        IsRegistered = RegisterEventHotKey(code, mask, _id, GetApplicationEventTarget(), 0, out _hotkey) == 0;
    }

    public bool IsRegistered { get; }

    public static MacOSGlobalHotkey? Register(uint modifiers, uint key, Action pressed)
    {
        MacOSGlobalHotkey hotkey = new(modifiers, key, pressed);
        if (hotkey.IsRegistered)
            return hotkey;
        hotkey.Dispose();
        return null;
    }

    private int OnEvent(nint next, nint item, nint data)
    {
        if (_disposed || GetEventParameter(item, 0x2D2D2D2D, 0x686B6964, 0, 8, 0, out HotkeyId id) != 0
            || id.Signature != _id.Signature || id.Id != _id.Id)
            return -9874;
        if (GetEventKind(item) == 6)
            _down = false;
        else if (!_down)
        {
            _down = true;
            try
            {
                _pressed();
            }
            catch (Exception)
            {
            }
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hotkey != 0)
            UnregisterEventHotKey(_hotkey);
        if (_handler != 0)
            RemoveEventHandler(_handler);
        _hotkey = _handler = 0;
        GC.SuppressFinalize(this);
    }

    private static uint? KeyCode(uint key) => key switch
    {
        0x41 => 0, 0x53 => 1, 0x44 => 2, 0x46 => 3, 0x48 => 4, 0x47 => 5,
        0x5A => 6, 0x58 => 7, 0x43 => 8, 0x56 => 9, 0x42 => 11,
        0x51 => 12, 0x57 => 13, 0x45 => 14, 0x52 => 15, 0x59 => 16, 0x54 => 17,
        0x31 => 18, 0x32 => 19, 0x33 => 20, 0x34 => 21, 0x36 => 22, 0x35 => 23,
        0xBB => 24, 0x39 => 25, 0x37 => 26, 0xBD => 27, 0x38 => 28, 0x30 => 29,
        0xDD => 30, 0x4F => 31, 0x55 => 32, 0xDB => 33, 0x49 => 34, 0x50 => 35,
        0x0D => 36, 0x4C => 37, 0x4A => 38, 0xDE => 39, 0x4B => 40, 0xBA => 41,
        0xDC => 42, 0xBC => 43, 0xBF => 44, 0x4E => 45, 0x4D => 46, 0xBE => 47,
        0x09 => 48, 0x20 => 49, 0xC0 => 50, 0x08 => 51, 0x1B => 53,
        0x60 => 82, 0x61 => 83, 0x62 => 84, 0x63 => 85, 0x64 => 86, 0x65 => 87,
        0x66 => 88, 0x67 => 89, 0x68 => 91, 0x69 => 92,
        0x70 => 122, 0x71 => 120, 0x72 => 99, 0x73 => 118, 0x74 => 96, 0x75 => 97,
        0x76 => 98, 0x77 => 100, 0x78 => 101, 0x79 => 109, 0x7A => 103, 0x7B => 111,
        0x7C => 105, 0x7D => 107, 0x7E => 113, 0x7F => 106, 0x80 => 64,
        0x81 => 79, 0x82 => 80, 0x83 => 90,
        0x2D => 114, 0x24 => 115, 0x21 => 116, 0x2E => 117, 0x23 => 119, 0x22 => 121,
        0x25 => 123, 0x27 => 124, 0x28 => 125, 0x26 => 126,
        _ => null
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct HotkeyId { public uint Signature; public uint Id; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EventType { public uint Class; public uint Kind; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Handler(nint next, nint item, nint data);
    [LibraryImport(Carbon)] private static partial nint GetApplicationEventTarget();
    [LibraryImport(Carbon)] private static partial int InstallEventHandler(nint target, Handler handler, uint count, [In] EventType[] types, nint data, out nint reference);
    [LibraryImport(Carbon)] private static partial int RemoveEventHandler(nint reference);
    [LibraryImport(Carbon)] private static partial int RegisterEventHotKey(uint key, uint modifiers, HotkeyId id, nint target, uint options, out nint reference);
    [LibraryImport(Carbon)] private static partial int UnregisterEventHotKey(nint reference);
    [LibraryImport(Carbon)] private static partial uint GetEventKind(nint item);
    [LibraryImport(Carbon)] private static partial int GetEventParameter(nint item, uint name, uint type, nint actualType, uint size, nint actualSize, out HotkeyId value);
}
