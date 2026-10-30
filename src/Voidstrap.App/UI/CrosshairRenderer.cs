using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.UI
{
    internal static class CrosshairRenderer
    {
        public const int ShapeCross = 0;
        public const int ShapeDot = 1;
        public const int ShapeCircle = 2;
        public const int MaxSize = 60;

        public static BitmapSource Render(
            int dimension,
            double scale,
            int shape,
            string? fillHex,
            string? outlineHex,
            int crosshairSize,
            int crosshairGap,
            int crosshairThickness,
            double crosshairOpacity
        )
        {
            int width = Math.Max(1, dimension);
            byte[] pixels = RenderPixels(
                width,
                scale,
                shape,
                fillHex,
                outlineHex,
                crosshairSize,
                crosshairGap,
                crosshairThickness,
                crosshairOpacity
            );
            BitmapSource bitmap = BitmapSource.Create(width, width, 96.0, 96.0, PixelFormats.Pbgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        public static byte[] RenderPixels(
            int dimension,
            double scale,
            int shape,
            string? fillHex,
            string? outlineHex,
            int crosshairSize,
            int crosshairGap,
            int crosshairThickness,
            double crosshairOpacity
        )
        {
            const int samples = 4;
            const int sampleCount = samples * samples;
            int width = Math.Max(1, dimension);
            int height = width;
            byte[] pixels = new byte[width * height * 4];
            Color fill = ParseColor(fillHex, Color.FromRgb(0, 255, 0));
            Color outline = ParseColor(outlineHex, Colors.Black);
            double opacity = Math.Clamp(crosshairOpacity, 0.0, 1.0);
            double size = Math.Clamp(crosshairSize, 2, MaxSize) * scale;
            double gap = Math.Clamp(crosshairGap, 0, 40) * scale;
            double thickness = Math.Max(1.0, Math.Clamp(crosshairThickness, 1, 16) * scale);
            double centerX = width / 2.0;
            double centerY = height / 2.0;
            double reach = size + thickness + 4.0;
            double band = thickness / 2.0 + 3.0;
            bool cross = shape == ShapeCross;

            for (int y = 0; y < height; y++)
            {
                double rowOffset = Math.Abs(y + 0.5 - centerY);
                if (rowOffset > reach)
                    continue;
                for (int x = 0; x < width; x++)
                {
                    double columnOffset = Math.Abs(x + 0.5 - centerX);
                    if (columnOffset > reach || cross && columnOffset > band && rowOffset > band)
                        continue;
                    int alphaSum = 0;
                    int redSum = 0;
                    int greenSum = 0;
                    int blueSum = 0;
                    for (int sampleY = 0; sampleY < samples; sampleY++)
                    {
                        for (int sampleX = 0; sampleX < samples; sampleX++)
                        {
                            double px = x + (sampleX + 0.5) / samples - centerX;
                            double py = y + (sampleY + 0.5) / samples - centerY;
                            bool fillHit = Hit(shape, px, py, size, gap, thickness, false);
                            bool outlineHit = !fillHit && Hit(shape, px, py, size, gap, thickness, true);
                            if (!fillHit && !outlineHit)
                                continue;
                            Color color = fillHit ? fill : outline;
                            int alpha = (int)Math.Round(color.A * opacity);
                            alphaSum += alpha;
                            redSum += color.R * alpha / 255;
                            greenSum += color.G * alpha / 255;
                            blueSum += color.B * alpha / 255;
                        }
                    }
                    int offset = (y * width + x) * 4;
                    pixels[offset] = (byte)(blueSum / sampleCount);
                    pixels[offset + 1] = (byte)(greenSum / sampleCount);
                    pixels[offset + 2] = (byte)(redSum / sampleCount);
                    pixels[offset + 3] = (byte)(alphaSum / sampleCount);
                }
            }

            return pixels;
        }

        private static bool Hit(int shape, double x, double y, double size, double gap, double thickness, bool outline)
        {
            double radius = outline ? thickness / 2.0 + 1.0 : thickness / 2.0;
            switch (shape)
            {
                case ShapeCross:
                    return DistanceToSegment(x, y, -size, 0, -gap, 0) <= radius
                        || DistanceToSegment(x, y, gap, 0, size, 0) <= radius
                        || DistanceToSegment(x, y, 0, -size, 0, -gap) <= radius
                        || DistanceToSegment(x, y, 0, gap, 0, size) <= radius;
                case ShapeDot:
                    return Math.Sqrt(x * x + y * y) <= size / 3.0 + (outline ? 2.0 : 0.0);
                case ShapeCircle:
                    double circleRadius = outline ? size / 2.0 : Math.Max(0.0, size / 2.0 - 2.0);
                    return Math.Abs(Math.Sqrt(x * x + y * y) - circleRadius) <= radius;
                default:
                    return false;
            }
        }

        private static double DistanceToSegment(double px, double py, double x1, double y1, double x2, double y2)
        {
            double dx = x2 - x1;
            double dy = y2 - y1;
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared == 0 ? 0 : Math.Clamp(((px - x1) * dx + (py - y1) * dy) / lengthSquared, 0.0, 1.0);
            double nearestX = x1 + t * dx;
            double nearestY = y1 + t * dy;
            double distanceX = px - nearestX;
            double distanceY = py - nearestY;
            return Math.Sqrt(distanceX * distanceX + distanceY * distanceY);
        }

        private static Color ParseColor(string? value, Color fallback)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(value ?? "");
            }
            catch
            {
                return fallback;
            }
        }
    }
}
