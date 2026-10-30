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

    }
}
