using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
                if (NativeImaging.TryDecodeBgra(bytes, maxPixelWidth, MaxDecodedPixels * 4, out int nativeWidth, out int nativeHeight, out byte[] nativePixels) && nativeWidth <= MaxDimension && nativeHeight <= MaxDimension)
                {
                    var nativeSource = BitmapSource.Create(nativeWidth, nativeHeight, 96, 96, PixelFormats.Bgra32, null, nativePixels, nativeWidth * 4);
                    nativeSource.Freeze();
                    return nativeSource;
                }
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
