using System.Buffers.Binary;
using System.Windows.Media.Imaging;

namespace Voidstrap.Utility;

internal static class AnimatedIcon
{
    private const int MaxFrames = 120;

    private const long MaxFileBytes = 16L * 1024L * 1024L;

    private const int MinimumDelayMilliseconds = 20;

    private const int AniIconFlag = 1;

    public const string FileFilterPatterns = "*.ico;*.ani;*.cur;*.png;*.apng;*.gif;*.webp;*.bmp;*.jpg;*.jpeg;*.tga";

    public static bool IsAnimatable(string? path)
    {
        string extension = Path.GetExtension(path ?? "").ToLowerInvariant();
        return extension is ".ani" or ".gif" or ".webp" or ".png" or ".apng";
    }

    public static IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> Load(string? path, int decodeWidth)
    {
        const string LOG_IDENT = "AnimatedIcon::Load";
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return [];
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
                return [];
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is ".ani" or ".cur")
            {
                byte[] bytes = File.ReadAllBytes(path);
                return extension == ".ani" ? DecodeAni(bytes, decodeWidth) : DecodeCursorFrame(bytes, decodeWidth);
            }
            return SafeImaging.DecodeAnimationPortable(path, decodeWidth);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException)
        {
            App.Logger?.WriteLine(LOG_IDENT, "Could not read the icon: " + ex.Message);
            return [];
        }
    }

    public static BitmapSource? LoadFirstFrame(string? path, int decodeWidth)
    {
        IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames = Load(path, decodeWidth);
        return frames.Count > 0 ? frames[0].Frame : null;
    }

    private static List<(BitmapSource Frame, int DelayMilliseconds)> DecodeCursorFrame(byte[] bytes, int decodeWidth)
    {
        BitmapSource? frame = DecodeIconData(bytes, decodeWidth);
        return frame == null ? [] : [(frame, 100)];
    }

    private static BitmapSource? DecodeIconData(byte[] data, int decodeWidth)
    {
        if (data.Length >= 6 && BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2)) == 2)
        {
            data = (byte[])data.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), 1);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4));
            for (int index = 0; index < count && 6 + index * 16 + 8 <= data.Length; index++)
            {
                int entry = 6 + index * 16;
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entry + 4), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entry + 6), 32);
            }
        }
        return SafeImaging.FromBytes(data, decodeWidth);
    }

    private static List<(BitmapSource Frame, int DelayMilliseconds)> DecodeAni(byte[] bytes, int decodeWidth)
    {
        if (bytes.Length < 12 || !Matches(bytes, 0, "RIFF") || !Matches(bytes, 8, "ACON"))
            throw new InvalidDataException("The file is not an animated cursor");

        List<byte[]> icons = [];
        int[]? rates = null;
        int[]? sequence = null;
        int defaultRate = 6;
        bool iconFrames = true;

        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
            int body = offset + 8;
            if (size < 0 || body + size > bytes.Length)
                break;
            switch (id)
            {
                case "anih" when size >= 36:
                    defaultRate = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 28)));
                    iconFrames = (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 32)) & AniIconFlag) != 0;
                    break;
                case "rate":
                    rates = ReadIntegers(bytes, body, size);
                    break;
                case "seq ":
                    sequence = ReadIntegers(bytes, body, size);
                    break;
                case "LIST" when size >= 4 && Matches(bytes, body, "fram"):
                    ReadFrames(bytes, body + 4, body + size, icons);
                    break;
            }
            offset = body + size + (size & 1);
        }

        if (!iconFrames || icons.Count == 0)
            throw new InvalidDataException("The animated cursor has no icon frames");

        List<BitmapSource?> decoded = icons.Select(icon => DecodeIconData(icon, decodeWidth)).ToList();
        int steps = Math.Min(sequence?.Length ?? decoded.Count, MaxFrames);
        List<(BitmapSource Frame, int DelayMilliseconds)> frames = [];
        for (int step = 0; step < steps; step++)
        {
            int index = sequence != null ? sequence[step] : step;
            if (index < 0 || index >= decoded.Count || decoded[index] is not BitmapSource frame)
                continue;
            int jiffies = rates != null && step < rates.Length && rates[step] > 0 ? rates[step] : defaultRate;
            frames.Add((frame, Math.Max(MinimumDelayMilliseconds, jiffies * 1000 / 60)));
        }
        return frames;
    }

    private static void ReadFrames(byte[] bytes, int offset, int end, List<byte[]> icons)
    {
        while (offset + 8 <= end && icons.Count < MaxFrames)
        {
            int size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)));
            int body = offset + 8;
            if (size < 0 || body + size > end)
                return;
            if (Matches(bytes, offset, "icon"))
                icons.Add(bytes.AsSpan(body, size).ToArray());
            offset = body + size + (size & 1);
        }
    }

    private static int[] ReadIntegers(byte[] bytes, int offset, int size)
    {
        int count = Math.Min(size / 4, MaxFrames * 4);
        int[] values = new int[count];
        for (int index = 0; index < count; index++)
            values[index] = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + index * 4)), int.MaxValue);
        return values;
    }

    private static bool Matches(byte[] bytes, int offset, string tag) =>
        offset + 4 <= bytes.Length
        && bytes[offset] == tag[0]
        && bytes[offset + 1] == tag[1]
        && bytes[offset + 2] == tag[2]
        && bytes[offset + 3] == tag[3];
}
