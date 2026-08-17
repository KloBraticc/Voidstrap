using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public static class ModGenerator
    {
        public sealed class GradientStop
        {
            public float Stop { get; }
            public Color Color { get; }

            public GradientStop(float offset, Color color)
            {
                Stop = offset;
                Color = color;
            }
        }

        public sealed class SpriteDef
        {
            public string Name { get; }
            public int X { get; }
            public int Y { get; }
            public int W { get; }
            public int H { get; }

            public SpriteDef(string name, int x, int y, int w, int h)
            {
                Name = name;
                X = x;
                Y = y;
                W = w;
                H = h;
            }
        }

        public static void RecolorAllPngs(
            string voidstrapTemp,
            Color? solidColor,
            List<GradientStop>? gradient,
            string imageSetDataPath,
            string? customLogoPath,
            string? customSpinnerPath,
            float gradientAngle,
            bool colorCursors,
            bool colorShiftlock,
            bool colorEmoteWheel,
            bool colorVoiceChat)
        {
            if (string.IsNullOrEmpty(voidstrapTemp) || !Directory.Exists(voidstrapTemp))
                return;

            foreach (string path in Directory.EnumerateFiles(voidstrapTemp, "*.png", SearchOption.AllDirectories))
            {
                if (IsCustomTarget(path, customLogoPath, "logo") || IsCustomTarget(path, customSpinnerPath, "spinner"))
                    continue;

                if (!ShouldRecolor(path, colorCursors, colorShiftlock, colorEmoteWheel, colorVoiceChat))
                    continue;

                try
                {
                    RecolorFile(path, solidColor, gradient, gradientAngle);
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteException("ModGenerator::RecolorAllPngs", ex);
                }
            }

            ApplyCustomImage(voidstrapTemp, customLogoPath, "logo");
            ApplyCustomImage(voidstrapTemp, customSpinnerPath, "spinner");
        }

        private static bool ShouldRecolor(string path, bool colorCursors, bool colorShiftlock, bool colorEmoteWheel, bool colorVoiceChat)
        {
            string p = path.Replace('\\', '/');

            if (!colorShiftlock && p.IndexOf("MouseLockedCursor", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (!colorCursors && p.IndexOf("/Cursors/", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (!colorEmoteWheel && p.IndexOf("/Emotes/", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
            if (!colorVoiceChat && p.IndexOf("VoiceChat", StringComparison.OrdinalIgnoreCase) >= 0)
                return false;

            return true;
        }

        private static bool IsCustomTarget(string path, string? customPath, string keyword)
        {
            if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                return false;

            string name = Path.GetFileNameWithoutExtension(path);
            return name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ApplyCustomImage(string voidstrapTemp, string? customPath, string keyword)
        {
            if (string.IsNullOrEmpty(customPath) || !File.Exists(customPath))
                return;

            foreach (string path in Directory.EnumerateFiles(voidstrapTemp, "*.png", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                try
                {
                    using var src = LoadBitmap(customPath);
                    using var resized = new Bitmap(src, GetPngSize(path));
                    SaveBitmap(resized, path);
                }
                catch (Exception ex)
                {
                    App.Logger?.WriteException("ModGenerator::ApplyCustomImage", ex);
                }
            }
        }

        private static Size GetPngSize(string path)
        {
            try
            {
                using var bmp = LoadBitmap(path);
                return new Size(bmp.Width, bmp.Height);
            }
            catch
            {
                return new Size(150, 150);
            }
        }

        private static void RecolorFile(string path, Color? solidColor, List<GradientStop>? gradient, float gradientAngle)
        {
            Bitmap recolored;
            using (var original = LoadBitmap(path))
                recolored = Recolor(original, solidColor, gradient, gradientAngle);

            using (recolored)
                SaveBitmap(recolored, path);
        }

        private static Bitmap LoadBitmap(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var tmp = new Bitmap(fs);
            var copy = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(copy);
            g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
            return copy;
        }

        private static void SaveBitmap(Bitmap bmp, string path)
        {
            string temp = path + ".tmp";
            bmp.Save(temp, ImageFormat.Png);
            File.Copy(temp, path, true);
            File.Delete(temp);
        }

        private static Bitmap Recolor(Bitmap original, Color? solidColor, List<GradientStop>? gradient, float gradientAngleDeg)
        {
            var recolored = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
            if (original.Width == 0 || original.Height == 0)
                return recolored;

            double theta = gradientAngleDeg * Math.PI / 180.0;
            double cos = Math.Cos(theta);
            double sin = Math.Sin(theta);
            double w = original.Width - 1;
            double h = original.Height - 1;

            double[] projs =
            {
                0,
                w * cos,
                h * sin,
                w * cos + h * sin
            };
            double minProj = projs.Min();
            double maxProj = projs.Max();
            double denom = Math.Abs(maxProj - minProj) < 1e-6 ? 1.0 : (maxProj - minProj);

            for (int y = 0; y < original.Height; y++)
            {
                for (int x = 0; x < original.Width; x++)
                {
                    Color src = original.GetPixel(x, y);
                    if (src.A <= 5)
                    {
                        recolored.SetPixel(x, y, Color.Transparent);
                        continue;
                    }

                    Color applyColor;
                    if (gradient != null && gradient.Count > 0)
                    {
                        double proj = x * cos + y * sin;
                        float t = (float)((proj - minProj) / denom);
                        t = Math.Clamp(t, 0f, 1f);
                        applyColor = Interpolate(gradient, t);
                    }
                    else
                    {
                        applyColor = solidColor ?? Color.White;
                    }

                    float alphaFactor = src.A / 255f;
                    recolored.SetPixel(x, y, Color.FromArgb(
                        src.A,
                        (byte)(applyColor.R * alphaFactor),
                        (byte)(applyColor.G * alphaFactor),
                        (byte)(applyColor.B * alphaFactor)));
                }
            }

            return recolored;
        }

        private static Color Interpolate(List<GradientStop> gradient, float t)
        {
            if (gradient == null || gradient.Count == 0)
                return Color.White;

            var stops = gradient.OrderBy(s => s.Stop).ToList();

            if (t <= stops[0].Stop) return stops[0].Color;
            if (t >= stops[^1].Stop) return stops[^1].Color;

            GradientStop left = stops[0];
            GradientStop right = stops[^1];
            for (int i = 0; i < stops.Count - 1; i++)
            {
                if (t >= stops[i].Stop && t <= stops[i + 1].Stop)
                {
                    left = stops[i];
                    right = stops[i + 1];
                    break;
                }
            }

            float span = right.Stop - left.Stop;
            float localT = span > 0 ? (t - left.Stop) / span : 0f;
            localT = Math.Clamp(localT, 0f, 1f);

            int r = (int)Math.Round(left.Color.R + (right.Color.R - left.Color.R) * localT);
            int g = (int)Math.Round(left.Color.G + (right.Color.G - left.Color.G) * localT);
            int b = (int)Math.Round(left.Color.B + (right.Color.B - left.Color.B) * localT);
            return Color.FromArgb(255, Clamp(r), Clamp(g), Clamp(b));
        }

        private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
    }
}
