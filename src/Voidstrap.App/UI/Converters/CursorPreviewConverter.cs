using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Enums;
using Voidstrap.Models.SettingTasks;
using Voidstrap.Utility;

namespace Voidstrap.UI.Converters;

internal sealed class CursorPreviewConverter : IValueConverter
{
    private static readonly ConcurrentDictionary<CursorType, ImageSource?> Cache = new();

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not CursorType type)
            return null;
        if (type == CursorType.Custom)
            return LoadCustom();
        return Cache.GetOrAdd(type, LoadPreset);
    }

    private static ImageSource? LoadCustom()
    {
        try
        {
            string path = CursorManager.PreviewPath(CursorSlot.Arrow);
            if (!File.Exists(path))
                return null;
            return DecodeAndCrop(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadPreset(CursorType type)
    {
        try
        {
            string? resource = ResolveResourceName(type);
            if (resource == null)
                return null;
            return DecodeAndCrop(Resource.Get(resource));
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("CursorPreviewConverter", "Could not load the preview for " + type + ": " + ex.Message);
            return null;
        }
    }

    private static string? ResolveResourceName(CursorType type)
    {
        if (type == CursorType.Default)
            return "Cursor.Default.ArrowFarCursor.png";
        if (!CursorPresetTask.CreateMap().TryGetValue(type, out var files))
            return null;
        return files
                .Where(pair => pair.Key.EndsWith("\\ArrowFarCursor.png", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .FirstOrDefault()
            ?? files
                .Where(pair => pair.Key.EndsWith("\\ArrowCursor.png", StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .FirstOrDefault();
    }

    private static ImageSource? DecodeAndCrop(byte[] bytes)
    {
        using MemoryStream stream = new MemoryStream(bytes);
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
            return null;
        FormatConvertedBitmap bgra = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth;
        int height = bgra.PixelHeight;
        if (width <= 0 || height <= 0)
            return null;
        byte[] pixels = new byte[width * height * 4];
        bgra.CopyPixels(pixels, width * 4, 0);

        int minX = width;
        int minY = height;
        int maxX = -1;
        int maxY = -1;
        int lightPixels = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = (y * width + x) * 4;
                byte a = pixels[idx + 3];
                if (a <= 16)
                    continue;
                if (x < minX)
                    minX = x;
                if (x > maxX)
                    maxX = x;
                if (y < minY)
                    minY = y;
                if (y > maxY)
                    maxY = y;
                if (a > 64 && (pixels[idx] > 120 || pixels[idx + 1] > 120 || pixels[idx + 2] > 120))
                    lightPixels++;
            }
        }

        if (maxX < minX || maxY < minY)
            return null;

        int boxWidth = maxX - minX + 1;
        int boxHeight = maxY - minY + 1;
        int square = Math.Max(Math.Max(boxWidth, boxHeight) + 4, 24);
        int offsetX = (square - boxWidth) / 2;
        int offsetY = (square - boxHeight) / 2;
        byte[] output = new byte[square * square * 4];

        for (int y = 0; y < boxHeight; y++)
        {
            for (int x = 0; x < boxWidth; x++)
            {
                int src = ((minY + y) * width + (minX + x)) * 4;
                int dst = ((offsetY + y) * square + (offsetX + x)) * 4;
                output[dst] = pixels[src];
                output[dst + 1] = pixels[src + 1];
                output[dst + 2] = pixels[src + 2];
                output[dst + 3] = pixels[src + 3];
            }
        }

        if (lightPixels < 12)
            AddLightOutline(output, square);

        BitmapSource result = BitmapSource.Create(
            square,
            square,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            output,
            square * 4
        );
        result.Freeze();
        return result;
    }

    private static void AddLightOutline(byte[] buffer, int size)
    {
        byte[] copy = (byte[])buffer.Clone();
        for (int y = 1; y < size - 1; y++)
        {
            for (int x = 1; x < size - 1; x++)
            {
                int idx = (y * size + x) * 4;
                if (copy[idx + 3] > 64)
                    continue;
                byte neighborAlpha = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        byte a = copy[((y + dy) * size + (x + dx)) * 4 + 3];
                        if (a > neighborAlpha)
                            neighborAlpha = a;
                    }
                }
                if (neighborAlpha > 96)
                {
                    byte rim = (byte)Math.Min(220, (int)neighborAlpha);
                    buffer[idx] = 245;
                    buffer[idx + 1] = 245;
                    buffer[idx + 2] = 245;
                    buffer[idx + 3] = rim;
                }
            }
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return Binding.DoNothing;
    }
}
