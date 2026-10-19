using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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

    private static bool _active;
    private static bool _hasInputAccess = true;

    private static nint _keyboardHook;
    private static nint _mouseHook;
    private static HookProc? _keyboardProc;
    private static HookProc? _mouseProc;

    private static Thread? _linuxThread;
    private static CancellationTokenSource? _linuxCts;

    public static event Action<KeystrokeInputState>? StateChanged;

    public static bool HasInputAccess => _hasInputAccess;

    public static void Start()
    {
        lock (SyncGate)
        {
            if (_active)
                return;

            _active = true;

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

            if (Voidstrap.Utility.Platform.IsWindows)
            {
                StopWindowsHooks();
            }
            else if (Voidstrap.Utility.Platform.IsLinux)
            {
                StopLinuxMonitoring();
            }

            Array.Clear(KeyDownStates, 0, KeyDownStates.Length);
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

    private static void DispatchKey(KeystrokeTarget target, bool isDown)
    {
        int idx = (int)target;
        if (idx < 0 || idx >= KeyDownStates.Length)
            return;

        bool previous = KeyDownStates[idx];
        if (previous == isDown)
            return;

        KeyDownStates[idx] = isDown;

        if (isDown)
        {
            if (target is KeystrokeTarget.Lmb or KeystrokeTarget.Rmb)
            {
                RecordClick(target == KeystrokeTarget.Lmb);
                KeystrokeAudioService.PlayMouseSound();
            }
            else
            {
                KeystrokeAudioService.PlayKeySound();
            }
        }

        StateChanged?.Invoke(new KeystrokeInputState(target, isDown));
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

                if ((isDown || isUp) && IsRobloxFocused())
                {
                    int vkCode = Marshal.ReadInt32(lParam);
                    KeystrokeTarget? target = MapVirtualKey(vkCode);
                    if (target.HasValue)
                        DispatchKey(target.Value, isDown);
                    else if (isDown)
                        KeystrokeAudioService.PlayKeySound();
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
                uint msg = (uint)wParam;
                if (IsRobloxFocused())
                {
                    if (msg == 0x0201)
                        DispatchKey(KeystrokeTarget.Lmb, true);
                    else if (msg == 0x0202)
                        DispatchKey(KeystrokeTarget.Lmb, false);
                    else if (msg == 0x0204)
                        DispatchKey(KeystrokeTarget.Rmb, true);
                    else if (msg == 0x0205)
                        DispatchKey(KeystrokeTarget.Rmb, false);
                }
            }
            catch
            {
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
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

                for (int i = 0; i < pollFds.Length; i++)
                {
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

                        if (type != 1)
                            continue;

                        if (!IsRobloxFocused())
                            continue;

                        bool down = value == 1;
                        bool up = value == 0;
                        if (!down && !up)
                            continue;

                        KeystrokeTarget? target = MapLinuxCode(code);
                        if (target.HasValue)
                            DispatchKey(target.Value, down);
                        else if (down)
                            KeystrokeAudioService.PlayKeySound();
                    }
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
