using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Voidstrap.Integrations.Clipper;

internal static partial class NativeClipper
{
    private const string Library = "voidclip";

    private const int StateUnknown = 0;
    private const int StateReady = 1;
    private const int StateMissing = 2;

    private static int _state;

    [LibraryImport(Library, EntryPoint = "vc_version")]
    private static partial uint Version();

    [LibraryImport(Library, EntryPoint = "vc_start")]
    private static unsafe partial int StartNative(byte* configPtr, nuint configLen);

    [LibraryImport(Library, EntryPoint = "vc_stop")]
    private static partial int StopNative();

    [LibraryImport(Library, EntryPoint = "vc_reconfigure")]
    private static unsafe partial int ReconfigureNative(byte* configPtr, nuint configLen);

    [LibraryImport(Library, EntryPoint = "vc_save")]
    private static unsafe partial int SaveNative(byte* outputPtr, nuint outputLen, uint seconds);

    [LibraryImport(Library, EntryPoint = "vc_record_start")]
    private static unsafe partial int RecordStartNative(byte* tempPtr, nuint tempLen);

    [LibraryImport(Library, EntryPoint = "vc_record_stop")]
    private static unsafe partial int RecordStopNative(byte* outputPtr, nuint outputLen);

    [LibraryImport(Library, EntryPoint = "vc_trim")]
    private static unsafe partial int TrimNative(
        byte* ffmpegPtr,
        nuint ffmpegLen,
        byte* inputPtr,
        nuint inputLen,
        byte* outputPtr,
        nuint outputLen,
        ulong startMs,
        ulong endMs
    );

    [LibraryImport(Library, EntryPoint = "vc_thumbnail")]
    private static unsafe partial int ThumbnailNative(
        byte* ffmpegPtr,
        nuint ffmpegLen,
        byte* inputPtr,
        nuint inputLen,
        byte* outputPtr,
        nuint outputLen,
        ulong timeMs,
        uint width
    );

    [LibraryImport(Library, EntryPoint = "vc_probe_duration_ms")]
    private static unsafe partial long ProbeDurationMsNative(byte* ffmpegPtr, nuint ffmpegLen, byte* inputPtr, nuint inputLen);

    [LibraryImport(Library, EntryPoint = "vc_status_json")]
    private static partial int StatusJsonNative(out IntPtr outPtr, out nuint outLen);

    [LibraryImport(Library, EntryPoint = "vc_last_error")]
    private static partial int LastErrorNative(out IntPtr outPtr, out nuint outLen);

    [LibraryImport(Library, EntryPoint = "vc_free")]
    private static partial void FreeNative(IntPtr ptr, nuint len);

    public static bool IsAvailable
    {
        get
        {
            int state = Volatile.Read(ref _state);
            if (state != StateUnknown)
                return state == StateReady;

            try
            {
                bool ok = Version() == 1;
                Volatile.Write(ref _state, ok ? StateReady : StateMissing);
                return ok;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                App.Logger.WriteLine("NativeClipper", $"Native voidclip library is unavailable ({ex.GetType().Name})");
                Volatile.Write(ref _state, StateMissing);
                return false;
            }
        }
    }

    public static unsafe bool Start(NativeClipperConfig config, out string error)
    {
        error = string.Empty;
        if (!IsAvailable)
        {
            error = "Native voidclip library is not available";
            return false;
        }
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(config);
        fixed (byte* ptr = json)
        {
            if (StartNative(ptr, (nuint)json.Length) != 0)
                return true;
        }
        error = GetLastError();
        return false;
    }

    public static void Stop()
    {
        if (!IsAvailable)
            return;
        try
        {
            StopNative();
        }
        catch { }
    }

    public static unsafe bool Save(string outputPath, int seconds, out string error)
    {
        error = string.Empty;
        if (!IsAvailable)
        {
            error = "Native voidclip library is not available";
            return false;
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(outputPath);
        fixed (byte* ptr = utf8)
        {
            if (SaveNative(ptr, (nuint)utf8.Length, (uint)Math.Max(0, seconds)) != 0)
                return true;
        }
        error = GetLastError();
        return false;
    }

    public static unsafe bool StartRecording(string tempPath, out string error)
    {
        error = string.Empty;
        if (!IsAvailable)
        {
            error = "Native voidclip library is not available";
            return false;
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(tempPath);
        fixed (byte* ptr = utf8)
        {
            if (RecordStartNative(ptr, (nuint)utf8.Length) != 0)
                return true;
        }
        error = GetLastError();
        return false;
    }

    public static unsafe bool StopRecording(string outputPath, out string error)
    {
        error = string.Empty;
        if (!IsAvailable)
        {
            error = "Native voidclip library is not available";
            return false;
        }
        byte[] utf8 = Encoding.UTF8.GetBytes(outputPath);
        fixed (byte* ptr = utf8)
        {
            if (RecordStopNative(ptr, (nuint)utf8.Length) != 0)
                return true;
        }
        error = GetLastError();
        return false;
    }

    public static unsafe bool Trim(string ffmpegPath, string inputPath, string outputPath, long startMs, long endMs, out string error)
    {
        error = string.Empty;
        if (!IsAvailable)
        {
            error = "Native voidclip library is not available";
            return false;
        }
        byte[] ff = Encoding.UTF8.GetBytes(ffmpegPath);
        byte[] inp = Encoding.UTF8.GetBytes(inputPath);
        byte[] outp = Encoding.UTF8.GetBytes(outputPath);
        fixed (byte* ffPtr = ff)
        fixed (byte* inPtr = inp)
        fixed (byte* outPtr = outp)
        {
            if (
                TrimNative(
                    ffPtr,
                    (nuint)ff.Length,
                    inPtr,
                    (nuint)inp.Length,
                    outPtr,
                    (nuint)outp.Length,
                    (ulong)Math.Max(0L, startMs),
                    (ulong)Math.Max(0L, endMs)
                ) != 0
            )
                return true;
        }
        error = GetLastError();
        return false;
    }

    public static unsafe bool CreateThumbnail(string ffmpegPath, string inputPath, string outputPath, long timeMs, int width)
    {
        if (!IsAvailable)
            return false;
        byte[] ff = Encoding.UTF8.GetBytes(ffmpegPath);
        byte[] inp = Encoding.UTF8.GetBytes(inputPath);
        byte[] outp = Encoding.UTF8.GetBytes(outputPath);
        fixed (byte* ffPtr = ff)
        fixed (byte* inPtr = inp)
        fixed (byte* outPtr = outp)
        {
            return ThumbnailNative(
                    ffPtr,
                    (nuint)ff.Length,
                    inPtr,
                    (nuint)inp.Length,
                    outPtr,
                    (nuint)outp.Length,
                    (ulong)Math.Max(0L, timeMs),
                    (uint)Math.Max(120, width)
                ) != 0;
        }
    }

    public static unsafe long ProbeDurationMs(string ffmpegPath, string inputPath)
    {
        if (!IsAvailable)
            return -1;
        byte[] ff = Encoding.UTF8.GetBytes(ffmpegPath);
        byte[] inp = Encoding.UTF8.GetBytes(inputPath);
        fixed (byte* ffPtr = ff)
        fixed (byte* inPtr = inp)
        {
            return ProbeDurationMsNative(ffPtr, (nuint)ff.Length, inPtr, (nuint)inp.Length);
        }
    }

    public static NativeClipperStatus GetStatus()
    {
        if (!IsAvailable)
            return new NativeClipperStatus();
        try
        {
            if (StatusJsonNative(out IntPtr ptr, out nuint len) == 0 || ptr == IntPtr.Zero)
                return new NativeClipperStatus();
            try
            {
                if (len == 0)
                    return new NativeClipperStatus();
                unsafe
                {
                    ReadOnlySpan<byte> span = new((void*)ptr, (int)len);
                    return JsonSerializer.Deserialize<NativeClipperStatus>(span) ?? new NativeClipperStatus();
                }
            }
            finally
            {
                FreeNative(ptr, len);
            }
        }
        catch
        {
            return new NativeClipperStatus();
        }
    }

    public static string GetLastError()
    {
        if (!IsAvailable)
            return "Native voidclip library is not available";
        try
        {
            if (LastErrorNative(out IntPtr ptr, out nuint len) == 0 || ptr == IntPtr.Zero)
                return string.Empty;
            try
            {
                if (len == 0)
                    return string.Empty;
                return Marshal.PtrToStringUTF8(ptr, (int)len) ?? string.Empty;
            }
            finally
            {
                FreeNative(ptr, len);
            }
        }
        catch
        {
            return string.Empty;
        }
    }
}
