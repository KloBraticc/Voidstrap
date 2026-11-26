using System.Runtime.InteropServices;

namespace Voidstrap.Platform.Linux;

public static unsafe partial class LinuxPcmPlayback
{
    private const string PulseSimple = "libpulse-simple.so.0";
    private const int Playback = 1;
    private const int Signed16BitLittleEndian = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleSpec
    {
        public int Format;
        public uint Rate;
        public byte Channels;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferAttributes
    {
        public uint MaximumLength;
        public uint TargetLength;
        public uint Prebuffer;
        public uint MinimumRequest;
        public uint FragmentSize;
    }

    public static bool TryPlay(string streamName, byte[] pcm, uint sampleRate, byte channels)
    {
        if (!OperatingSystem.IsLinux() || pcm.Length == 0 || sampleRate == 0 || channels == 0)
            return false;

        nint stream = 0;
        try
        {
            SampleSpec format = new() { Format = Signed16BitLittleEndian, Rate = sampleRate, Channels = channels };
            BufferAttributes buffer = new()
            {
                MaximumLength = uint.MaxValue,
                TargetLength = sampleRate / 20 * channels * sizeof(short),
                Prebuffer = 0,
                MinimumRequest = uint.MaxValue,
                FragmentSize = uint.MaxValue
            };
            stream = Open(0, "Voidstrap", Playback, 0, streamName, in format, 0, in buffer, out _);
            if (stream == 0)
                return false;

            fixed (byte* samples = pcm)
                return Write(stream, samples, (nuint)pcm.Length, out _) == 0 && Drain(stream, out _) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        finally
        {
            if (stream != 0)
                Free(stream);
        }
    }

    [LibraryImport(PulseSimple, EntryPoint = "pa_simple_new", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Open(nint server, string application, int direction, nint device, string name, in SampleSpec format,
        nint channelMap, in BufferAttributes attributes, out int error);

    [LibraryImport(PulseSimple, EntryPoint = "pa_simple_write")]
    private static partial int Write(nint stream, byte* data, nuint bytes, out int error);

    [LibraryImport(PulseSimple, EntryPoint = "pa_simple_drain")]
    private static partial int Drain(nint stream, out int error);

    [LibraryImport(PulseSimple, EntryPoint = "pa_simple_free")]
    private static partial void Free(nint stream);
}
