#if !CROSSPLAT
using System.Runtime.InteropServices;

namespace Voidstrap.Utility;

internal static unsafe partial class NativeStack
{
    private const int MaxFrames = 40;

    private const int ContextSize = 1232;

    private const int StackFrameSize = 512;

    private const int SymbolNameLength = 256;

    private const uint ContextFull = 0x10000B;

    private const uint ThreadAccess = 0x0002 | 0x0008 | 0x0040;

    private const uint MachineAmd64 = 0x8664;

    private static readonly object Gate = new();

    private static uint _threadId;

    private static bool _symbolsReady;

    public static void RememberCurrentThread() => _threadId = GetCurrentThreadId();

    public static string Capture()
    {
        if (_threadId == 0 || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "";
        lock (Gate)
        {
            nint thread = OpenThread(ThreadAccess, 0, _threadId);
            if (thread == 0)
                return "\n  Native UI thread stack unavailable: error " + Marshal.GetLastPInvokeError();
            void* context = NativeMemory.AlignedAlloc(ContextSize, 16);
            byte* frame = stackalloc byte[StackFrameSize];
            try
            {
                NativeMemory.Clear(context, ContextSize);
                *(uint*)((byte*)context + 0x30) = ContextFull;
                if (SuspendThread(thread) == uint.MaxValue)
                    return "\n  Native UI thread stack unavailable: the thread could not be paused";
                int captured = GetThreadContext(thread, context);
                ResumeThread(thread);
                if (captured == 0)
                    return "\n  Native UI thread stack unavailable: error " + Marshal.GetLastPInvokeError();
                nint process = GetCurrentProcess();
                if (!_symbolsReady)
                {
                    SymSetOptions(0x2 | 0x4);
                    _symbolsReady = SymInitializeW(process, null, 1) != 0;
                }
                else
                    SymRefreshModuleList(process);
                if (!_symbolsReady)
                    return "\n  Native UI thread stack unavailable: symbols could not start";
                NativeMemory.Clear(frame, StackFrameSize);
                SetAddress(frame, 0, *(ulong*)((byte*)context + 0xF8));
                SetAddress(frame, 32, *(ulong*)((byte*)context + 0xA0));
                SetAddress(frame, 48, *(ulong*)((byte*)context + 0x98));
                nint dbghelp = NativeLibrary.Load("dbghelp.dll");
                nint tableAccess = NativeLibrary.GetExport(dbghelp, "SymFunctionTableAccess64");
                nint moduleBase = NativeLibrary.GetExport(dbghelp, "SymGetModuleBase64");
                System.Text.StringBuilder builder = new("\n  Native UI thread stack at the freeze:");
                for (int index = 0; index < MaxFrames; index++)
                {
                    if (StackWalk64(MachineAmd64, process, thread, frame, context, 0, tableAccess, moduleBase, 0) == 0)
                        break;
                    ulong address = *(ulong*)frame;
                    if (address == 0)
                        break;
                    builder.Append("\n    at ").Append(Describe(process, address));
                }
                return builder.ToString();
            }
            catch (Exception ex)
            {
                return "\n  Native UI thread stack unavailable: " + ex.GetType().Name;
            }
            finally
            {
                NativeMemory.AlignedFree(context);
                CloseHandle(thread);
            }
        }
    }

    private static void SetAddress(byte* frame, int offset, ulong value)
    {
        *(ulong*)(frame + offset) = value;
        *(uint*)(frame + offset + 12) = 3;
    }

    private static string Describe(nint process, ulong address)
    {
        string module = "?";
        char* path = stackalloc char[260];
        if (GetModuleHandleExW(0x4 | 0x2, (nint)address, out nint handle) != 0)
        {
            uint length = GetModuleFileNameW(handle, path, 260);
            if (length > 0)
                module = Path.GetFileName(new string(path, 0, (int)length));
        }
        else
            return module + "+0x" + address.ToString("X");
        int bufferSize = 88 + SymbolNameLength * 2;
        byte* symbol = stackalloc byte[bufferSize];
        NativeMemory.Clear(symbol, (nuint)bufferSize);
        *(uint*)symbol = 88;
        *(uint*)(symbol + 80) = SymbolNameLength;
        if (SymFromAddrW(process, address, out ulong displacement, symbol) != 0)
        {
            int nameLength = (int)Math.Min(*(uint*)(symbol + 76), SymbolNameLength);
            return module + "!" + new string((char*)(symbol + 84), 0, nameLength) + "+0x" + displacement.ToString("X");
        }
        return module + "+0x" + (address - (ulong)handle).ToString("X");
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenThread(uint access, int inherit, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SuspendThread(nint thread);

    [LibraryImport("kernel32.dll")]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetThreadContext(nint thread, void* context);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW")]
    private static partial int GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW")]
    private static partial uint GetModuleFileNameW(nint module, char* path, uint size);

    [LibraryImport("dbghelp.dll")]
    private static partial uint SymSetOptions(uint options);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymInitializeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SymInitializeW(nint process, string? searchPath, int invade);

    [LibraryImport("dbghelp.dll")]
    private static partial int SymRefreshModuleList(nint process);

    [LibraryImport("dbghelp.dll", EntryPoint = "SymFromAddrW")]
    private static partial int SymFromAddrW(nint process, ulong address, out ulong displacement, byte* symbol);

    [LibraryImport("dbghelp.dll")]
    private static partial int StackWalk64(
        uint machine,
        nint process,
        nint thread,
        byte* frame,
        void* context,
        nint readMemory,
        nint functionTableAccess,
        nint getModuleBase,
        nint translateAddress
    );
}
#endif
