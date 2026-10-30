using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Voidstrap.Integrations.Keystrokes;

public static partial class KeystrokeInputService
{
    private const string ApplicationServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const uint MacLeftDown = 1;
    private const uint MacLeftUp = 2;
    private const uint MacRightDown = 3;
    private const uint MacRightUp = 4;
    private const uint MacKeyDown = 10;
    private const uint MacKeyUp = 11;
    private const uint MacFlagsChanged = 12;
    private const uint MacOtherDown = 25;
    private const uint MacOtherUp = 26;
    private const uint MacTapDisabledByTimeout = 0xFFFFFFFE;
    private const uint MacTapDisabledByUser = 0xFFFFFFFF;
    private const int MacKeycodeField = 9;
    private const int MacButtonField = 3;

    private static readonly HashSet<int> MacHeldModifiers = new();
    private static Thread? _macThread;
    private static nint _macRunLoop;
    private static nint _macTap;
    private static volatile bool _macStopping;

    [LibraryImport(ApplicationServices, EntryPoint = "CGEventTapCreate")]
    private static unsafe partial nint CGEventTapCreate(
        uint tap,
        uint place,
        uint options,
        ulong eventsOfInterest,
        delegate* unmanaged<nint, uint, nint, nint, nint> callback,
        nint userInfo
    );

    [LibraryImport(ApplicationServices, EntryPoint = "CGEventTapEnable")]
    private static partial void CGEventTapEnable(nint tap, [MarshalAs(UnmanagedType.U1)] bool enable);

    [LibraryImport(ApplicationServices, EntryPoint = "CGEventGetIntegerValueField")]
    private static partial long CGEventGetIntegerValueField(nint ev, int field);

    [LibraryImport(ApplicationServices, EntryPoint = "CGEventGetFlags")]
    private static partial ulong CGEventGetFlags(nint ev);

    [LibraryImport(ApplicationServices, EntryPoint = "CGPreflightListenEventAccess")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CGPreflightListenEventAccess();

    [LibraryImport(ApplicationServices, EntryPoint = "CGRequestListenEventAccess")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CGRequestListenEventAccess();

    [LibraryImport(CoreFoundation, EntryPoint = "CFMachPortCreateRunLoopSource")]
    private static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

    [LibraryImport(CoreFoundation, EntryPoint = "CFMachPortInvalidate")]
    private static partial void CFMachPortInvalidate(nint port);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopGetCurrent")]
    private static partial nint CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopAddSource")]
    private static partial void CFRunLoopAddSource(nint loop, nint source, nint mode);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopRunInMode")]
    private static partial int CFRunLoopRunInMode(nint mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRunLoopStop")]
    private static partial void CFRunLoopStop(nint loop);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void CFRelease(nint value);

    private static void StartMacMonitoring()
    {
        _macStopping = false;
        _macThread = new Thread(MacInputLoop) { Name = "KeystrokeMacInput", IsBackground = true };
        _macThread.Start();
    }

    private static void StopMacMonitoring()
    {
        _macStopping = true;
        nint loop = Interlocked.Exchange(ref _macRunLoop, IntPtr.Zero);
        try
        {
            if (loop != IntPtr.Zero)
                CFRunLoopStop(loop);
        }
        catch { }
        _macThread = null;
        lock (MacHeldModifiers)
            MacHeldModifiers.Clear();
    }

    private static unsafe void MacInputLoop()
    {
        nint source = IntPtr.Zero;
        try
        {
            if (!CGPreflightListenEventAccess())
                CGRequestListenEventAccess();

            ulong mask =
                (1UL << (int)MacLeftDown)
                | (1UL << (int)MacLeftUp)
                | (1UL << (int)MacRightDown)
                | (1UL << (int)MacRightUp)
                | (1UL << (int)MacKeyDown)
                | (1UL << (int)MacKeyUp)
                | (1UL << (int)MacFlagsChanged)
                | (1UL << (int)MacOtherDown)
                | (1UL << (int)MacOtherUp);

            while (!_macStopping)
            {
                _macTap = CGEventTapCreate(1, 0, 1, mask, &MacTapCallback, IntPtr.Zero);
                if (_macTap != IntPtr.Zero)
                    break;
                _hasInputAccess = false;
                Thread.Sleep(2000);
            }
            if (_macTap == IntPtr.Zero)
                return;

            _hasInputAccess = true;
            source = CFMachPortCreateRunLoopSource(IntPtr.Zero, _macTap, IntPtr.Zero);
            nint loop = CFRunLoopGetCurrent();
            nint commonModes = ReadCoreFoundationConstant("kCFRunLoopCommonModes");
            nint defaultMode = ReadCoreFoundationConstant("kCFRunLoopDefaultMode");
            CFRunLoopAddSource(loop, source, commonModes);
            CGEventTapEnable(_macTap, true);
            Interlocked.Exchange(ref _macRunLoop, loop);
            App.Logger?.WriteLine("KeystrokeInputService", "Reading keys through the macOS event tap");

            while (!_macStopping)
                CFRunLoopRunInMode(defaultMode, 0.5, false);
        }
        catch (Exception ex)
        {
            _hasInputAccess = false;
            App.Logger?.WriteLine("KeystrokeInputService", "macOS input loop stopped: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _macRunLoop, IntPtr.Zero);
            nint tap = Interlocked.Exchange(ref _macTap, IntPtr.Zero);
            try
            {
                if (tap != IntPtr.Zero)
                {
                    CGEventTapEnable(tap, false);
                    CFMachPortInvalidate(tap);
                    CFRelease(tap);
                }
                if (source != IntPtr.Zero)
                    CFRelease(source);
            }
            catch { }
        }
    }

    private static nint ReadCoreFoundationConstant(string name)
    {
        nint library = NativeLibrary.Load(CoreFoundation);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name));
    }

    [UnmanagedCallersOnly]
    private static nint MacTapCallback(nint proxy, uint type, nint ev, nint userInfo)
    {
        try
        {
            switch (type)
            {
                case MacTapDisabledByTimeout:
                case MacTapDisabledByUser:
                    if (_macTap != IntPtr.Zero && !_macStopping)
                        CGEventTapEnable(_macTap, true);
                    break;
                case MacKeyDown:
                case MacKeyUp:
                    {
                        if (CGEventGetIntegerValueField(ev, 8) != 0 && type == MacKeyDown)
                            break;
                        int code = (int)CGEventGetIntegerValueField(ev, MacKeycodeField);
                        QueueInput(code, MapMacKeyToString(code), MapMacKey(code), type == MacKeyDown);
                        break;
                    }
                case MacFlagsChanged:
                    {
                        int code = (int)CGEventGetIntegerValueField(ev, MacKeycodeField);
                        ulong flag = MacModifierFlag(code);
                        if (flag == 0)
                            break;
                        bool down;
                        lock (MacHeldModifiers)
                        {
                            down = (CGEventGetFlags(ev) & flag) != 0 && !MacHeldModifiers.Contains(code);
                            if (down)
                                MacHeldModifiers.Add(code);
                            else
                                MacHeldModifiers.Remove(code);
                        }
                        QueueInput(code, MapMacKeyToString(code), MapMacKey(code), down);
                        break;
                    }
                case MacLeftDown:
                case MacLeftUp:
                    QueueInput(0x1001, "LMB", KeystrokeTarget.Lmb, type == MacLeftDown);
                    break;
                case MacRightDown:
                case MacRightUp:
                    QueueInput(0x1002, "RMB", KeystrokeTarget.Rmb, type == MacRightDown);
                    break;
                case MacOtherDown:
                case MacOtherUp:
                    if (CGEventGetIntegerValueField(ev, MacButtonField) == 2)
                        QueueInput(0x1003, "MMB", null, type == MacOtherDown);
                    break;
            }
        }
        catch { }
        return ev;
    }

    private static ulong MacModifierFlag(int code)
    {
        return code switch
        {
            56 or 60 => 0x20000,
            59 or 62 => 0x40000,
            58 or 61 => 0x80000,
            55 or 54 => 0x100000,
            57 => 0x10000,
            _ => 0,
        };
    }

    public static string? MapMacKeyToString(int code)
    {
        return code switch
        {
            0 => "A",
            1 => "S",
            2 => "D",
            3 => "F",
            4 => "H",
            5 => "G",
            6 => "Z",
            7 => "X",
            8 => "C",
            9 => "V",
            11 => "B",
            12 => "Q",
            13 => "W",
            14 => "E",
            15 => "R",
            16 => "Y",
            17 => "T",
            18 => "1",
            19 => "2",
            20 => "3",
            21 => "4",
            22 => "6",
            23 => "5",
            24 => "=",
            25 => "9",
            26 => "7",
            27 => "-",
            28 => "8",
            29 => "0",
            30 => "]",
            31 => "O",
            32 => "U",
            33 => "[",
            34 => "I",
            35 => "P",
            36 => "Enter",
            37 => "L",
            38 => "J",
            39 => "'",
            40 => "K",
            41 => ";",
            42 => "\\",
            43 => ",",
            44 => "/",
            45 => "N",
            46 => "M",
            47 => ".",
            48 => "Tab",
            49 => "Space",
            50 => "`",
            51 => "Backspace",
            53 => "Esc",
            56 or 60 => "Shift",
            57 => "CapsLock",
            58 or 61 => "Alt",
            59 or 62 => "Ctrl",
            96 => "F5",
            97 => "F6",
            98 => "F7",
            99 => "F3",
            100 => "F8",
            101 => "F9",
            103 => "F11",
            109 => "F10",
            111 => "F12",
            114 => "Insert",
            115 => "Home",
            116 => "PageUp",
            117 => "Delete",
            118 => "F4",
            119 => "End",
            120 => "F2",
            121 => "PageDown",
            122 => "F1",
            123 => "Left",
            124 => "Right",
            125 => "Down",
            126 => "Up",
            _ => null,
        };
    }

    private static KeystrokeTarget? MapMacKey(int code)
    {
        return code switch
        {
            13 => KeystrokeTarget.W,
            0 => KeystrokeTarget.A,
            1 => KeystrokeTarget.S,
            2 => KeystrokeTarget.D,
            49 => KeystrokeTarget.Space,
            56 or 60 => KeystrokeTarget.Shift,
            _ => null,
        };
    }
}
