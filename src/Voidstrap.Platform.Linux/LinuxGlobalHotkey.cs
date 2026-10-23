using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public sealed partial class LinuxGlobalHotkey : IDisposable
{
    private const string X11 = "libX11.so.6";
    private static readonly ConcurrentDictionary<nint, LinuxGlobalHotkey> Instances = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Action _pressed;
    private readonly nint _display;
    private readonly nint _root;
    private readonly int _key;
    private readonly uint[] _masks;
    private readonly uint _modifiers;
    private Task? _pump;
    private Task? _evdev;
    private bool _disposed;
    private bool _failed;

    private LinuxGlobalHotkey(uint modifiers, uint key, Action pressed)
    {
        _pressed = pressed;
        _modifiers = modifiers;
        _masks = [];
        if (!OperatingSystem.IsLinux() || !LinuxWindowInterop.IsAvailable)
            return;
        _display = XOpenDisplay(0);
        if (_display == 0)
            return;
        _root = XDefaultRootWindow(_display);
        _key = XKeysymToKeycode(_display, KeySym(key));
        uint mask = ((modifiers & 1) != 0 ? 8u : 0) | ((modifiers & 2) != 0 ? 4u : 0)
            | ((modifiers & 4) != 0 ? 1u : 0) | ((modifiers & 8) != 0 ? 64u : 0);
        HashSet<uint> variants = [0, 2];
        nint map = XGetModifierMapping(_display);
        if (map != 0)
        {
            ModifierMap mapping = Marshal.PtrToStructure<ModifierMap>(map);
            foreach (nuint symbol in new nuint[] { 0xFF7F, 0xFF14 })
            {
                byte code = XKeysymToKeycode(_display, symbol);
                for (int slot = 0; slot < 8 && code != 0; slot++)
                    for (int i = 0; i < mapping.Count; i++)
                        if (Marshal.ReadByte(mapping.Keys, slot * mapping.Count + i) == code)
                            foreach (uint existing in variants.ToArray())
                                variants.Add(existing | (1u << slot));
            }
            XFreeModifiermap(map);
        }
        _masks = variants.Select(value => value | mask).Distinct().ToArray();
        Instances[_display] = this;
        LinuxWindowInterop.KeepIgnoringXErrors();
        if (_key == 0)
            return;
        foreach (uint variant in _masks)
            XGrabKey(_display, _key, variant, _root, false, 1, 1);
        XSync(_display, false);
        IsRegistered = !_failed;
        if (IsRegistered)
        {
            XkbSetDetectableAutoRepeat(_display, true, out _);
            _pump = Task.Factory.StartNew(Pump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && _key > 8)
                _evdev = Task.Factory.StartNew(EvdevPump, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    public bool IsRegistered { get; }

    public static LinuxGlobalHotkey? Register(uint modifiers, uint key, Action pressed)
    {
        LinuxGlobalHotkey hotkey = new(modifiers, key, pressed);
        if (hotkey.IsRegistered)
            return hotkey;
        hotkey.Dispose();
        return null;
    }

    internal static void ObserveError(nint display)
    {
        if (Instances.TryGetValue(display, out LinuxGlobalHotkey? instance))
            instance._failed = true;
    }

    private void Pump()
    {
        bool down = false;
        PollDescriptor descriptor = new() { File = XConnectionNumber(_display), Events = 1 };
        while (!_lifetime.IsCancellationRequested)
        {
            if (XPending(_display) == 0)
            {
                Poll(ref descriptor, 1, 500);
                continue;
            }
            XNextEvent(_display, out KeyEvent item);
            if (item.KeyCode != _key)
                continue;
            if (item.Type == 3)
            {
                if (XPending(_display) > 0)
                {
                    XPeekEvent(_display, out KeyEvent next);
                    if (next.Type == 2 && next.KeyCode == item.KeyCode && next.Time == item.Time)
                    {
                        XNextEvent(_display, out _);
                        continue;
                    }
                }
                down = false;
                continue;
            }
            if (item.Type != 2 || down)
                continue;
            down = true;
            if (_lifetime.IsCancellationRequested)
                break;
            try
            {
                _pressed();
            }
            catch (Exception)
            {
            }
        }
    }

    private void EvdevPump()
    {
        int target = _key - 8;
        Dictionary<string, int> devices = new(StringComparer.Ordinal);
        HashSet<int> held = [];
        byte[] buffer = new byte[24 * 64];
        long nextScan = 0;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                if (Environment.TickCount64 >= nextScan)
                {
                    nextScan = Environment.TickCount64 + 5000;
                    try
                    {
                        foreach (string node in Directory.GetFiles("/dev/input", "event*"))
                        {
                            if (devices.ContainsKey(node))
                                continue;
                            int opened = Open(node, 0x800, 0);
                            if (opened >= 0)
                                devices[node] = opened;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
                if (devices.Count == 0)
                {
                    _lifetime.Token.WaitHandle.WaitOne(2000);
                    continue;
                }
                PollDescriptor[] descriptors = devices.Values.Select(file => new PollDescriptor { File = file, Events = 1 }).ToArray();
                if (PollMany(descriptors, (nuint)descriptors.Length, 500) <= 0)
                    continue;
                foreach (PollDescriptor descriptor in descriptors)
                {
                    if ((descriptor.Returned & 1) == 0)
                    {
                        if ((descriptor.Returned & 0x18) != 0)
                        {
                            string node = devices.First(pair => pair.Value == descriptor.File).Key;
                            Close(descriptor.File);
                            devices.Remove(node);
                        }
                        continue;
                    }
                    long count = Read(descriptor.File, buffer, (nuint)buffer.Length);
                    for (int offset = 0; offset + 24 <= count; offset += 24)
                    {
                        if (BitConverter.ToUInt16(buffer, offset + 16) != 1)
                            continue;
                        int code = BitConverter.ToUInt16(buffer, offset + 18);
                        int value = BitConverter.ToInt32(buffer, offset + 20);
                        if (value == 0)
                        {
                            held.Remove(code);
                            continue;
                        }
                        if (value != 1 || !held.Add(code) || code != target || !ModifiersHeld(held))
                            continue;
                        if (!LinuxWindowInterop.IsNativeWindowActive())
                            continue;
                        try
                        {
                            _pressed();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (int file in devices.Values)
                Close(file);
        }
    }

    private bool ModifiersHeld(HashSet<int> held)
    {
        bool alt = held.Contains(56) || held.Contains(100);
        bool control = held.Contains(29) || held.Contains(97);
        bool shift = held.Contains(42) || held.Contains(54);
        bool super = held.Contains(125) || held.Contains(126);
        return alt == ((_modifiers & 1) != 0) && control == ((_modifiers & 2) != 0)
            && shift == ((_modifiers & 4) != 0) && super == ((_modifiers & 8) != 0);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        _pump?.GetAwaiter().GetResult();
        _evdev?.GetAwaiter().GetResult();
        if (_display != 0)
        {
            foreach (uint mask in _masks)
                if (_key != 0)
                    XUngrabKey(_display, _key, mask, _root);
            XSync(_display, false);
            Instances.TryRemove(_display, out _);
            XCloseDisplay(_display);
        }
        _lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    private static nuint KeySym(uint key) => key switch
    {
        >= 0x41 and <= 0x5A => key + 32,
        >= 0x30 and <= 0x39 => key,
        >= 0x70 and <= 0x87 => 0xFFBE + key - 0x70,
        >= 0x60 and <= 0x69 => 0xFFB0 + key - 0x60,
        0x08 => 0xFF08, 0x09 => 0xFF09, 0x0D => 0xFF0D, 0x1B => 0xFF1B,
        0x20 => 0x20, 0x21 => 0xFF55, 0x22 => 0xFF56, 0x23 => 0xFF57,
        0x24 => 0xFF50, 0x25 => 0xFF51, 0x26 => 0xFF52, 0x27 => 0xFF53,
        0x28 => 0xFF54, 0x2D => 0xFF63, 0x2E => 0xFFFF,
        0xBA => 0x3B, 0xBB => 0x3D, 0xBC => 0x2C, 0xBD => 0x2D,
        0xBE => 0x2E, 0xBF => 0x2F, 0xC0 => 0x60, 0xDB => 0x5B,
        0xDC => 0x5C, 0xDD => 0x5D, 0xDE => 0x27, 0xE2 => 0x3C,
        _ => 0
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ModifierMap { public int Count; public nint Keys; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor { public int File; public short Events; public short Returned; }
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    private struct KeyEvent
    {
        public int Type;
        public nuint Serial;
        public int Sent;
        public nint Display;
        public nuint Window;
        public nuint Root;
        public nuint Subwindow;
        public nuint Time;
        public int X;
        public int Y;
        public int RootX;
        public int RootY;
        public uint State;
        public uint KeyCode;
    }

    [LibraryImport(X11)] private static partial nint XOpenDisplay(nint name);
    [LibraryImport(X11)] private static partial int XCloseDisplay(nint display);
    [LibraryImport(X11)] private static partial nint XDefaultRootWindow(nint display);
    [LibraryImport(X11)] private static partial byte XKeysymToKeycode(nint display, nuint symbol);
    [LibraryImport(X11)] private static partial nint XGetModifierMapping(nint display);
    [LibraryImport(X11)] private static partial int XFreeModifiermap(nint map);
    [LibraryImport(X11)] private static partial int XGrabKey(nint display, int key, uint modifiers, nint window, [MarshalAs(UnmanagedType.Bool)] bool owner, int pointer, int keyboard);
    [LibraryImport(X11)] private static partial int XUngrabKey(nint display, int key, uint modifiers, nint window);
    [LibraryImport(X11)] private static partial int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [LibraryImport(X11)] private static partial int XPending(nint display);
    [LibraryImport(X11)] private static partial int XPeekEvent(nint display, out KeyEvent item);
    [LibraryImport(X11)] private static partial int XNextEvent(nint display, out KeyEvent item);
    [LibraryImport(X11)] private static partial int XConnectionNumber(nint display);
    [LibraryImport(X11)] private static partial int XkbSetDetectableAutoRepeat(nint display, [MarshalAs(UnmanagedType.Bool)] bool enabled, out int supported);
    [LibraryImport("libc", EntryPoint = "poll")] private static partial int PollMany([In, Out] PollDescriptor[] descriptors, nuint count, int timeout);
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)] private static partial int Open(string path, int flags, int mode);
    [LibraryImport("libc", EntryPoint = "read")] private static partial long Read(int file, [Out] byte[] buffer, nuint count);
    [LibraryImport("libc", EntryPoint = "close")] private static partial int Close(int file);
    [LibraryImport("libc", EntryPoint = "poll")] private static partial int Poll(ref PollDescriptor descriptor, nuint count, int timeout);
}
