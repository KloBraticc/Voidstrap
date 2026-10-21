using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Utility;

namespace Voidstrap.Integrations.Keystrokes;

public enum KeystrokeTarget
{
    W = 0,
    A = 1,
    S = 2,
    D = 3,
    Space = 4,
    Shift = 5,
    Lmb = 6,
    Rmb = 7
}

public readonly record struct KeystrokeInputState(KeystrokeTarget Target, bool IsDown);

public static partial class KeystrokeInputService
{
    private static readonly object SyncGate = new();
    private static readonly object ClickGate = new();
    private static readonly List<long> LmbTimestamps = new(64);
    private static readonly List<long> RmbTimestamps = new(64);
    private static readonly bool[] KeyDownStates = new bool[8];
    private static readonly Dictionary<string, bool> NamedKeyStates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, HashSet<int>> HeldCodes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<int> HeldUnnamedCodes = new();

    private static bool _active;
    private static bool _hasInputAccess = true;
    private static int _generation;
    private static Dispatcher? _dispatcher;

    private static nint _keyboardHook;
    private static nint _mouseHook;
    private static HookProc? _keyboardProc;
    private static HookProc? _mouseProc;

    private static Thread? _linuxThread;
    private static CancellationTokenSource? _linuxCts;

    public static event Action<KeystrokeInputState>? StateChanged;
    public static event Action<string, bool>? KeyNamedChanged;

    public static bool HasInputAccess => _hasInputAccess;

    public static bool IsNamedDown(string name)
    {
        lock (SyncGate)
        {
            return NamedKeyStates.TryGetValue(name, out bool down) && down;
        }
    }

    public static void Start()
    {
        lock (SyncGate)
        {
            if (_active)
                return;

            _active = true;
            _generation++;
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            if (Voidstrap.Utility.Platform.IsWindows)
            {
                StartWindowsHooks();
            }
            else if (Voidstrap.Utility.Platform.IsLinux)
            {
                StartLinuxMonitoring();
            }
        }
    }

    public static void Stop()
    {
        lock (SyncGate)
        {
            if (!_active)
                return;

            _active = false;
            _generation++;

            if (Voidstrap.Utility.Platform.IsWindows)
            {
                StopWindowsHooks();
            }
            else if (Voidstrap.Utility.Platform.IsLinux)
            {
                StopLinuxMonitoring();
            }

            Array.Clear(KeyDownStates, 0, KeyDownStates.Length);
            NamedKeyStates.Clear();
            HeldCodes.Clear();
            HeldUnnamedCodes.Clear();
            lock (ClickGate)
            {
                LmbTimestamps.Clear();
                RmbTimestamps.Clear();
            }
        }
    }

    public static bool IsDown(KeystrokeTarget target)
    {
        int idx = (int)target;
        return idx >= 0 && idx < KeyDownStates.Length && KeyDownStates[idx];
    }

    public static int GetLmbCps()
    {
        long now = Stopwatch.GetTimestamp();
        long window = Stopwatch.Frequency;
        lock (ClickGate)
        {
            LmbTimestamps.RemoveAll(t => now - t > window);
            return LmbTimestamps.Count;
        }
    }

    public static int GetRmbCps()
    {
        long now = Stopwatch.GetTimestamp();
        long window = Stopwatch.Frequency;
        lock (ClickGate)
        {
            RmbTimestamps.RemoveAll(t => now - t > window);
            return RmbTimestamps.Count;
        }
    }

    private static void RecordClick(bool isLmb)
    {
        long now = Stopwatch.GetTimestamp();
        lock (ClickGate)
        {
            if (isLmb)
                LmbTimestamps.Add(now);
            else
                RmbTimestamps.Add(now);
        }
    }

    private static void QueueInput(int code, string? name, KeystrokeTarget? target, bool isDown)
    {
        if (isDown && !IsRobloxFocused())
            return;

        Dispatcher? dispatcher = _dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;

        int generation = _generation;
        dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => ProcessInput(generation, code, name, target, isDown)));
    }

    private static void ProcessInput(int generation, int code, string? name, KeystrokeTarget? target, bool isDown)
    {
        lock (SyncGate)
        {
            if (!_active || generation != _generation)
                return;

            if (string.IsNullOrEmpty(name))
            {
                bool added = isDown ? HeldUnnamedCodes.Add(code) : HeldUnnamedCodes.Remove(code);
                if (!added || !isDown)
                    return;
            }
            else
            {
                if (!HeldCodes.TryGetValue(name, out HashSet<int>? codes))
                    HeldCodes[name] = codes = new HashSet<int>();
                if (isDown)
                    codes.Add(code);
                else
                    codes.Remove(code);

                bool nowDown = codes.Count > 0;
                NamedKeyStates.TryGetValue(name, out bool previous);
                if (previous == nowDown)
                    return;
                NamedKeyStates[name] = nowDown;
                isDown = nowDown;
            }
        }

        if (string.IsNullOrEmpty(name))
        {
            KeystrokeAudioService.PlayKeySound();
            return;
        }

        if (isDown)
        {
            if (name is "LMB" or "RMB")
                RecordClick(name == "LMB");
            if (name is "LMB" or "RMB" or "MMB")
                KeystrokeAudioService.PlayMouseSound();
            else
                KeystrokeAudioService.PlayKeySound();
        }

        if (target.HasValue)
        {
            KeyDownStates[(int)target.Value] = isDown;
            StateChanged?.Invoke(new KeystrokeInputState(target.Value, isDown));
        }

        KeyNamedChanged?.Invoke(name, isDown);
    }

    private static bool IsRobloxFocused()
    {
        var rect = RobloxWindowTracker.Current;
        return rect.Valid && rect.Foreground;
    }

    private static void StartWindowsHooks()
    {
        try
        {
            _keyboardProc = WindowsKeyboardCallback;
            _mouseProc = WindowsMouseCallback;

            nint moduleHandle = GetModuleHandleW(IntPtr.Zero);
            _keyboardHook = SetWindowsHookExW(13, _keyboardProc, moduleHandle, 0);
            _mouseHook = SetWindowsHookExW(14, _mouseProc, moduleHandle, 0);
            _hasInputAccess = _keyboardHook != IntPtr.Zero;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("KeystrokeInputService", "Failed to start Windows hooks: " + ex.Message);
            _hasInputAccess = false;
        }
    }

    private static void StopWindowsHooks()
    {
        try
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            _keyboardProc = null;
            _mouseProc = null;
        }
        catch
        {
        }
    }

    private delegate nint HookProc(int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll", EntryPoint = "UnhookWindowsHookEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandleW(nint lpModuleName);

    private static nint WindowsKeyboardCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && lParam != IntPtr.Zero)
        {
            try
            {
                uint msg = (uint)wParam;
                bool isDown = msg is 0x0100 or 0x0104;
                bool isUp = msg is 0x0101 or 0x0105;

                if (isDown || isUp)
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    QueueInput(vkCode, MapVirtualKeyToString(vkCode), MapVirtualKey(vkCode), isDown);
                }
            }
            catch
            {
            }
        }
        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static nint WindowsMouseCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                switch ((uint)wParam)
                {
                    case 0x0201:
                        QueueInput(0x01, "LMB", KeystrokeTarget.Lmb, true);
                        break;
                    case 0x0202:
                        QueueInput(0x01, "LMB", KeystrokeTarget.Lmb, false);
                        break;
                    case 0x0204:
                        QueueInput(0x02, "RMB", KeystrokeTarget.Rmb, true);
                        break;
                    case 0x0205:
                        QueueInput(0x02, "RMB", KeystrokeTarget.Rmb, false);
                        break;
                    case 0x0207:
                        QueueInput(0x04, "MMB", null, true);
                        break;
                    case 0x0208:
                        QueueInput(0x04, "MMB", null, false);
                        break;
                }
            }
            catch
            {
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public static string? MapVirtualKeyToString(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A)
            return ((char)vk).ToString();

        if (vk >= 0x30 && vk <= 0x39)
            return ((char)vk).ToString();

        if (vk >= 0x60 && vk <= 0x69)
            return "Num" + (vk - 0x60);

        if (vk >= 0x70 && vk <= 0x7B)
            return "F" + (vk - 0x70 + 1);

        return vk switch
        {
            0x20 => "Space",
            0x10 or 0xA0 or 0xA1 => "Shift",
            0x11 or 0xA2 or 0xA3 => "Ctrl",
            0x12 or 0xA4 or 0xA5 => "Alt",
            0x09 => "Tab",
            0x14 => "CapsLock",
            0x1B => "Esc",
            0x0D => "Enter",
            0x08 => "Backspace",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x24 => "Home",
            0x23 => "End",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0xBC => ",",
            0xBE => ".",
            0xBD => "-",
            0xBB => "=",
            0xBA => ";",
            0xDE => "'",
            0xBF => "/",
            0xDB => "[",
            0xDD => "]",
            0xDC => "\\",
            0xC0 => "`",
            _ => null
        };
    }

    private static KeystrokeTarget? MapVirtualKey(int vk)
    {
        return vk switch
        {
            0x57 => KeystrokeTarget.W,
            0x41 => KeystrokeTarget.A,
            0x53 => KeystrokeTarget.S,
            0x44 => KeystrokeTarget.D,
            0x20 => KeystrokeTarget.Space,
            0x10 or 0xA0 or 0xA1 => KeystrokeTarget.Shift,
            _ => null
        };
    }

    private static void StartLinuxMonitoring()
    {
        _linuxCts = new CancellationTokenSource();
        var token = _linuxCts.Token;

        _linuxThread = new Thread(() => LinuxInputLoop(token))
        {
            Name = "KeystrokeLinuxInput",
            IsBackground = true
        };
        _linuxThread.Start();
    }

    private static void StopLinuxMonitoring()
    {
        try
        {
            _linuxCts?.Cancel();
            _linuxCts?.Dispose();
            _linuxCts = null;
            _linuxThread = null;
        }
        catch
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxInputEvent
    {
        public nint TimeSec;
        public nint TimeUsec;
        public ushort Type;
        public ushort Code;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true)]
    private static partial int OpenLinux(byte[] path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseLinux(int fd);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    private static partial nint ReadLinux(int fd, byte[] buf, nint count);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static partial int PollLinux([In, Out] PollFd[] fds, uint nfds, int timeout);

    private static void LinuxInputLoop(CancellationToken token)
    {
        List<int> openedFds = [];
        try
        {
            string[] eventNodes = Directory.Exists("/dev/input")
                ? Directory.GetFiles("/dev/input", "event*")
                : [];

            foreach (string node in eventNodes)
            {
                byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(node + "\0");
                int fd = OpenLinux(pathBytes, 0x800, 0);
                if (fd >= 0)
                    openedFds.Add(fd);
            }

            if (openedFds.Count == 0)
            {
                _hasInputAccess = false;
                LinuxX11FallbackLoop(token);
                return;
            }

            _hasInputAccess = true;
            byte[] eventBuffer = new byte[Marshal.SizeOf<LinuxInputEvent>() * 16];
            int structSize = Marshal.SizeOf<LinuxInputEvent>();

            PollFd[] pollFds = new PollFd[openedFds.Count];
            for (int i = 0; i < openedFds.Count; i++)
            {
                pollFds[i] = new PollFd
                {
                    Fd = openedFds[i],
                    Events = 1,
                    Revents = 0
                };
            }

            while (!token.IsCancellationRequested)
            {
                int ret = PollLinux(pollFds, (uint)pollFds.Length, 50);
                if (ret <= 0)
                    continue;

                bool lostDevice = false;
                for (int i = 0; i < pollFds.Length; i++)
                {
                    if ((pollFds[i].Revents & 0x38) != 0)
                    {
                        lostDevice = true;
                        continue;
                    }

                    if ((pollFds[i].Revents & 1) == 0)
                        continue;

                    nint readBytes = ReadLinux(pollFds[i].Fd, eventBuffer, (nint)eventBuffer.Length);
                    if (readBytes < structSize)
                        continue;

                    int count = (int)(readBytes / structSize);
                    for (int e = 0; e < count; e++)
                    {
                        int offset = e * structSize;
                        ushort type = BitConverter.ToUInt16(eventBuffer, offset + 16);
                        ushort code = BitConverter.ToUInt16(eventBuffer, offset + 18);
                        int value = BitConverter.ToInt32(eventBuffer, offset + 20);

                        if (type != 1 || value is not (0 or 1))
                            continue;

                        QueueInput(code, MapLinuxCodeToString(code), MapLinuxCode(code), value == 1);
                    }
                }

                if (lostDevice)
                {
                    List<PollFd> alive = new(pollFds.Length);
                    foreach (PollFd pollFd in pollFds)
                    {
                        if ((pollFd.Revents & 0x38) != 0)
                        {
                            CloseLinux(pollFd.Fd);
                            openedFds.Remove(pollFd.Fd);
                        }
                        else
                        {
                            alive.Add(pollFd with { Revents = 0 });
                        }
                    }
                    pollFds = alive.ToArray();
                    if (pollFds.Length == 0)
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("KeystrokeInputService", "Linux input loop stopped: " + ex.Message);
        }
        finally
        {
            foreach (int fd in openedFds)
                CloseLinux(fd);
        }
    }

    private static void LinuxX11FallbackLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Thread.Sleep(25);
            }
            catch
            {
                break;
            }
        }
    }

    public static string? MapLinuxCodeToString(ushort code)
    {
        return code switch
        {
            1 => "Esc",
            2 => "1",
            3 => "2",
            4 => "3",
            5 => "4",
            6 => "5",
            7 => "6",
            8 => "7",
            9 => "8",
            10 => "9",
            11 => "0",
            12 => "-",
            13 => "=",
            14 => "Backspace",
            15 => "Tab",
            16 => "Q",
            17 => "W",
            18 => "E",
            19 => "R",
            20 => "T",
            21 => "Y",
            22 => "U",
            23 => "I",
            24 => "O",
            25 => "P",
            26 => "[",
            27 => "]",
            28 => "Enter",
            29 or 97 => "Ctrl",
            30 => "A",
            31 => "S",
            32 => "D",
            33 => "F",
            34 => "G",
            35 => "H",
            36 => "J",
            37 => "K",
            38 => "L",
            39 => ";",
            40 => "'",
            41 => "`",
            42 or 54 => "Shift",
            43 => "\\",
            44 => "Z",
            45 => "X",
            46 => "C",
            47 => "V",
            48 => "B",
            49 => "N",
            50 => "M",
            51 => ",",
            52 => ".",
            53 => "/",
            56 or 100 => "Alt",
            57 => "Space",
            58 => "CapsLock",
            59 => "F1",
            60 => "F2",
            61 => "F3",
            62 => "F4",
            63 => "F5",
            64 => "F6",
            65 => "F7",
            66 => "F8",
            67 => "F9",
            68 => "F10",
            87 => "F11",
            88 => "F12",
            103 => "Up",
            105 => "Left",
            106 => "Right",
            108 => "Down",
            272 => "LMB",
            273 => "RMB",
            274 => "MMB",
            _ => null
        };
    }

    private static KeystrokeTarget? MapLinuxCode(ushort code)
    {
        return code switch
        {
            17 => KeystrokeTarget.W,
            30 => KeystrokeTarget.A,
            31 => KeystrokeTarget.S,
            32 => KeystrokeTarget.D,
            57 => KeystrokeTarget.Space,
            42 or 54 => KeystrokeTarget.Shift,
            272 => KeystrokeTarget.Lmb,
            273 => KeystrokeTarget.Rmb,
            _ => null
        };
    }
}
