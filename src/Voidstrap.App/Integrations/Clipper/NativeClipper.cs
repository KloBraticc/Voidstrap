using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Voidstrap.Integrations.Clipper;

public sealed class NativeClipperConfig
{
    [JsonPropertyName("ffmpeg")]
    public string Ffmpeg { get; set; } = "ffmpeg";

    [JsonPropertyName("bufferSeconds")]
    public int BufferSeconds { get; set; } = 30;

    [JsonPropertyName("fps")]
    public int Fps { get; set; } = 60;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 1080;

    [JsonPropertyName("quality")]
    public int Quality { get; set; } = 2;

    [JsonPropertyName("codec")]
    public string Codec { get; set; } = "h264";

    [JsonPropertyName("gameAudio")]
    public bool GameAudio { get; set; } = true;

    [JsonPropertyName("microphone")]
    public bool Microphone { get; set; } = false;

    [JsonPropertyName("cursor")]
    public bool Cursor { get; set; } = false;

    [JsonPropertyName("window")]
    public long Window { get; set; }
}

public sealed class NativeClipperStatus
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("encoderId")]
    public string EncoderId { get; set; } = string.Empty;

    [JsonPropertyName("encoderLabel")]
    public string EncoderLabel { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    [JsonPropertyName("bitrateKbps")]
    public int BitrateKbps { get; set; }

    [JsonPropertyName("bufferedMs")]
    public long BufferedMs { get; set; }

    [JsonPropertyName("bufferSeconds")]
    public int BufferSeconds { get; set; }

    [JsonPropertyName("bufferedBytes")]
    public long BufferedBytes { get; set; }

    [JsonPropertyName("lastError")]
    public string LastError { get; set; } = string.Empty;
}

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

    [LibraryImport(Library, EntryPoint = "vc_trim")]
    private static unsafe partial int TrimNative(
        byte* ffmpegPtr, nuint ffmpegLen,
        byte* inputPtr, nuint inputLen,
        byte* outputPtr, nuint outputLen,
        ulong startMs, ulong endMs);

    [LibraryImport(Library, EntryPoint = "vc_thumbnail")]
    private static unsafe partial int ThumbnailNative(
        byte* ffmpegPtr, nuint ffmpegLen,
        byte* inputPtr, nuint inputLen,
        byte* outputPtr, nuint outputLen,
        ulong timeMs, uint width);

    [LibraryImport(Library, EntryPoint = "vc_probe_duration_ms")]
    private static unsafe partial long ProbeDurationMsNative(
        byte* ffmpegPtr, nuint ffmpegLen,
        byte* inputPtr, nuint inputLen);

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

    public static unsafe bool Reconfigure(NativeClipperConfig config, out string error)
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
            if (ReconfigureNative(ptr, (nuint)json.Length) != 0)
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
        catch
        {
        }
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
            if (TrimNative(ffPtr, (nuint)ff.Length, inPtr, (nuint)inp.Length, outPtr, (nuint)outp.Length, (ulong)Math.Max(0L, startMs), (ulong)Math.Max(0L, endMs)) != 0)
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
            return ThumbnailNative(ffPtr, (nuint)ff.Length, inPtr, (nuint)inp.Length, outPtr, (nuint)outp.Length, (ulong)Math.Max(0L, timeMs), (uint)Math.Max(120, width)) != 0;
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
        if (StatusJsonNative(out IntPtr ptr, out nuint len) == 0 || ptr == IntPtr.Zero)
            return new NativeClipperStatus();
        try
        {
            unsafe
            {
                ReadOnlySpan<byte> span = new((void*)ptr, checked((int)len));
                return JsonSerializer.Deserialize<NativeClipperStatus>(span) ?? new NativeClipperStatus();
            }
        }
        catch
        {
            return new NativeClipperStatus();
        }
        finally
        {
            FreeNative(ptr, len);
        }
    }

    public static string GetLastError()
    {
        if (!IsAvailable)
            return "Native voidclip library is not available";
        if (LastErrorNative(out IntPtr ptr, out nuint len) == 0 || ptr == IntPtr.Zero)
            return string.Empty;
        try
        {
            unsafe
            {
                return Encoding.UTF8.GetString((byte*)ptr, checked((int)len));
            }
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            FreeNative(ptr, len);
        }
    }
}
