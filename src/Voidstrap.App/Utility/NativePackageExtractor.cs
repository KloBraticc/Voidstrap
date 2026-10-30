using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Voidstrap.Utility;

internal static partial class NativePackageExtractor
{
    private const string Library = "voidpkg";
    private const string LOG_IDENT = "NativePackageExtractor";

    private const int StateUnknown = 0;
    private const int StateReady = 1;
    private const int StateMissing = 2;

    private const int ResultOk = 0;
    private const int ResultCancelled = 2;

    private static int _state;

    [LibraryImport(Library, EntryPoint = "vp_version")]
    private static partial uint Version();

    [LibraryImport(Library, EntryPoint = "vp_extract")]
    private static unsafe partial int ExtractNative(
        char* zipPtr,
        nuint zipLen,
        char* destPtr,
        nuint destLen,
        char* filterPtr,
        nuint filterLen,
        int* cancelFlag
    );

    [LibraryImport(Library, EntryPoint = "vp_last_error")]
    private static unsafe partial nuint LastErrorNative(char* buffer, nuint capacity);

    public static bool IsAvailable
    {
        get
        {
            int state = Volatile.Read(ref _state);
            if (state != StateUnknown)
                return state == StateReady;
            bool ready = false;
            if (Platform.IsWindows)
            {
                try
                {
                    ready = Version() >= 1;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Native extractor unavailable, using the managed extractor: " + ex.Message);
                }
            }
            Volatile.Write(ref _state, ready ? StateReady : StateMissing);
            return ready;
        }
    }

    public static unsafe void Extract(string zipPath, string destination, IReadOnlyCollection<string>? files, CancellationToken token)
    {
        string? filter = files == null ? null : string.Join('\n', files);
        int* cancelFlag = (int*)NativeMemory.AllocZeroed(sizeof(int));
        try
        {
            using CancellationTokenRegistration registration = token.Register(SetFlag, (IntPtr)cancelFlag);
            int result;
            fixed (char* zip = zipPath)
            fixed (char* dest = destination)
            fixed (char* filterChars = filter)
            {
                result = ExtractNative(
                    zip,
                    (nuint)zipPath.Length,
                    dest,
                    (nuint)destination.Length,
                    filter == null ? null : filterChars,
                    (nuint)(filter?.Length ?? 0),
                    cancelFlag
                );
            }
            if (result == ResultOk)
                return;
            if (result == ResultCancelled)
                throw new OperationCanceledException(token);
            throw new IOException("Could not extract " + Path.GetFileName(zipPath) + ": " + ReadLastError());
        }
        finally
        {
            NativeMemory.Free(cancelFlag);
        }
    }

    private static unsafe void SetFlag(object? state)
    {
        if (state is IntPtr flag)
            Volatile.Write(ref *(int*)flag, 1);
    }

    private static unsafe string ReadLastError()
    {
        Span<char> buffer = stackalloc char[1024];
        fixed (char* ptr = buffer)
        {
            int length = (int)Math.Min((ulong)LastErrorNative(ptr, (nuint)buffer.Length), (ulong)buffer.Length);
            return new string(ptr, 0, length);
        }
    }
}
