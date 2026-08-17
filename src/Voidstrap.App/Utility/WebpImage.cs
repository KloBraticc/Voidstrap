using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Voidstrap.Utility
{
    public static class WebpImage
    {
        private const int MaxDimension = 8192;
        private const long MaxDecodedPixels = 16L * 1024L * 1024L;

        public static bool LooksLikeWebp(byte[] bytes)
        {
            return bytes != null && bytes.Length >= 12
                && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P';
        }

        public static ImageSource? TryDecode(byte[] bytes, int maxPixelWidth)
        {
            if (!LooksLikeWebp(bytes))
                return null;
            try
            {
                var info = SixLabors.ImageSharp.Image.Identify(bytes);
                if (info == null || info.Width < 1 || info.Height < 1 || info.Width > MaxDimension || info.Height > MaxDimension || (long)info.Width * info.Height > MaxDecodedPixels)
                    return null;
                using var image = SixLabors.ImageSharp.Image.Load<Bgra32>(bytes);
                if (image.Width < 1 || image.Height < 1 || image.Width > MaxDimension || image.Height > MaxDimension)
                    return null;
                if (maxPixelWidth > 0 && image.Width > maxPixelWidth)
                {
                    int height = Math.Max(1, (int)Math.Round(image.Height * (double)maxPixelWidth / image.Width));
                    image.Mutate(x => x.Resize(maxPixelWidth, height));
                }
                int stride = image.Width * 4;
                byte[] pixels = new byte[stride * image.Height];
                image.CopyPixelDataTo(pixels);
                var source = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
        }
    }
}
