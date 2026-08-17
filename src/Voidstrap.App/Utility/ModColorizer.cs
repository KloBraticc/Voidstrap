using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Voidstrap.Utility
{
    public enum ColorizeMode
    {
        Gradient,
        Tint,
        Solid
    }

    public static class ModColorizer
    {
        private const string LogTag = "ModColorizer";
        private static readonly string[] ContentFolders = { "content", "ExtraContent", "PlatformContent" };
        private static readonly string[] ExcludedSegments = { "shaders", "ssl", "fonts" };

        private static bool IsExcluded(string path)
        {
            foreach (var seg in ExcludedSegments)
                if (path.IndexOf(Path.DirectorySeparatorChar + seg + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        public static string? FindRobloxContentDir()
        {
            try
            {
                var procs = Process.GetProcessesByName("RobloxPlayerBeta");
                try
                {
                    foreach (var p in procs)
                    {
                        try
                        {
                            var file = p.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(file))
                            {
                                var dir = Path.GetDirectoryName(file);
                                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                                    return dir;
                            }
                        }
                        catch { }
                    }
                }
                finally { foreach (var p in procs) { try { p.Dispose(); } catch { } } }
            }
            catch { }

            try
            {
                var guid = App.State.Prop.Player.VersionGuid;
                if (!string.IsNullOrWhiteSpace(guid))
                {
                    var d = Path.Combine(Paths.Versions, guid);
                    if (Directory.Exists(d)) return d;
                }
            }
            catch { }

            try
            {
                if (Directory.Exists(Paths.Versions))
                {
                    return Directory.GetDirectories(Paths.Versions)
                        .Where(d => File.Exists(Path.Combine(d, "RobloxPlayerBeta.exe")))
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                }
            }
            catch { }

            return null;
        }

        public static List<string> EnumerateImages(string root)
        {
            var images = new List<string>();
            foreach (var folder in ContentFolders)
            {
                var path = Path.Combine(root, folder);
                if (!Directory.Exists(path)) continue;
                try
                {
                    images.AddRange(Directory.EnumerateFiles(path, "*.png", SearchOption.AllDirectories)
                        .Where(f => !IsExcluded(f)));
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LogTag, $"Enumerate failed for {folder}: {ex.Message}");
                }
            }
            return images;
        }

        public static int CountImages()
        {
            var root = FindRobloxContentDir();
            if (root == null) return 0;
            return EnumerateImages(root).Count;
        }

        public static async Task<int> ApplyAsync(ColorizeMode mode, string colorAHex, string colorBHex, IProgress<(int done, int total)>? progress = null)
        {
            return await Task.Run(() =>
            {
                var root = FindRobloxContentDir();
                if (root == null)
                {
                    App.Logger.WriteLine(LogTag, "Could not find the Roblox install directory.");
                    return 0;
                }

                Color a = ParseColor(colorAHex, Color.FromArgb(255, 120, 0, 255));
                Color b = ParseColor(colorBHex, Color.FromArgb(255, 0, 200, 255));

                var images = EnumerateImages(root);
                int total = images.Count;
                int done = 0, written = 0;

                App.Logger.WriteLine(LogTag, $"Colorizing {total} images from {root} into {Paths.Mods}");

                foreach (var src in images)
                {
                    try
                    {
                        string rel = Path.GetRelativePath(root, src);
                        string dest = Path.Combine(Paths.Mods, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        if (Recolor(src, dest, mode, a, b))
                            written++;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LogTag, $"Skip {Path.GetFileName(src)}: {ex.Message}");
                    }

                    done++;
                    if ((done % 25) == 0 || done == total)
                        progress?.Report((done, total));
                }

                App.Logger.WriteLine(LogTag, $"Colorize complete: {written}/{total} images written to mods");
                return written;
            });
        }

        public static void Clear()
        {
            try
            {
                foreach (var folder in ContentFolders)
                {
                    var path = Path.Combine(Paths.Mods, folder);
                    if (Directory.Exists(path))
                        Directory.Delete(path, true);
                }
                App.Logger.WriteLine(LogTag, "Cleared colorized images from mods");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogTag, $"Clear failed: {ex.Message}");
            }
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            try
            {
                hex = (hex ?? "").Trim().TrimStart('#');
                if (hex.Length == 6)
                {
                    int r = Convert.ToInt32(hex.Substring(0, 2), 16);
                    int g = Convert.ToInt32(hex.Substring(2, 2), 16);
                    int bl = Convert.ToInt32(hex.Substring(4, 2), 16);
                    return Color.FromArgb(255, r, g, bl);
                }
            }
            catch { }
            return fallback;
        }

        private static unsafe bool Recolor(string src, string dest, ColorizeMode mode, Color a, Color b)
        {
            using var loaded = new Bitmap(src);
            int w = loaded.Width, h = loaded.Height;
            if (w <= 0 || h <= 0) return false;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.DrawImage(loaded, 0, 0, w, h);

            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)data.Scan0;
                int stride = data.Stride;

                for (int y = 0; y < h; y++)
                {
                    byte* row = scan0 + y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int i = x * 4;
                        byte bl = row[i];
                        byte gr = row[i + 1];
                        byte re = row[i + 2];
                        byte al = row[i + 3];
                        if (al == 0) continue;

                        double lum = (0.299 * re + 0.587 * gr + 0.114 * bl) / 255.0;

                        int nr, ng, nb;
                        switch (mode)
                        {
                            case ColorizeMode.Tint:
                                nr = (int)(re * (a.R / 255.0));
                                ng = (int)(gr * (a.G / 255.0));
                                nb = (int)(bl * (a.B / 255.0));
                                break;
                            case ColorizeMode.Solid:
                                nr = (int)(a.R * lum);
                                ng = (int)(a.G * lum);
                                nb = (int)(a.B * lum);
                                break;
                            default:
                                nr = (int)(a.R + (b.R - a.R) * lum);
                                ng = (int)(a.G + (b.G - a.G) * lum);
                                nb = (int)(a.B + (b.B - a.B) * lum);
                                break;
                        }

                        row[i] = (byte)Math.Clamp(nb, 0, 255);
                        row[i + 1] = (byte)Math.Clamp(ng, 0, 255);
                        row[i + 2] = (byte)Math.Clamp(nr, 0, 255);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            bmp.Save(dest, ImageFormat.Png);
            return true;
        }
    }
}
