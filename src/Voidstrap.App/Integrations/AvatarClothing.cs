using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Voidstrap.Integrations;

internal static class AvatarClothing
{
    public const string Torso = "Torso";

    public const string LeftArm = "LeftArm";

    public const string RightArm = "RightArm";

    public const string LeftLeg = "LeftLeg";

    public const string RightLeg = "RightLeg";

    private const double TemplateWidth = 585.0;

    private const double TemplateHeight = 559.0;

    private const double FrontLeft = 231.0;

    private const double FrontTop = 74.0;

    private const double FrontSize = 128.0;

    private static readonly Dictionary<string, (string Mesh, int Width, int Height)> Limbs = new(StringComparer.Ordinal)
    {
        [Torso] = ("R15CompositTorsoBase.mesh", 388, 268),
        [LeftArm] = ("R15CompositLeftArmBase.mesh", 264, 284),
        [LeftLeg] = ("R15CompositLeftArmBase.mesh", 264, 284),
        [RightArm] = ("R15CompositRightArmBase.mesh", 264, 284),
        [RightLeg] = ("R15CompositRightArmBase.mesh", 264, 284),
    };

    private static readonly ConcurrentDictionary<string, TexturedMesh?> Meshes = new(StringComparer.OrdinalIgnoreCase);

    public static readonly Dictionary<string, string> PartLimbs = new(StringComparer.Ordinal)
    {
        ["UpperTorso"] = Torso,
        ["LowerTorso"] = Torso,
        ["LeftUpperArm"] = LeftArm,
        ["LeftLowerArm"] = LeftArm,
        ["LeftHand"] = LeftArm,
        ["RightUpperArm"] = RightArm,
        ["RightLowerArm"] = RightArm,
        ["RightHand"] = RightArm,
        ["LeftUpperLeg"] = LeftLeg,
        ["LeftLowerLeg"] = LeftLeg,
        ["LeftFoot"] = LeftLeg,
        ["RightUpperLeg"] = RightLeg,
        ["RightLowerLeg"] = RightLeg,
        ["RightFoot"] = RightLeg,
    };

    public sealed record Picture(byte[] Pixels, int Width, int Height);

    public static Picture? Decode(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return null;
        try
        {
            using MemoryStream stream = new(bytes);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            BitmapSource source = image;
            if (source.Format != PixelFormats.Bgra32)
            {
                FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);
                converted.Freeze();
                source = converted;
            }
            int width = source.PixelWidth;
            int height = source.PixelHeight;
            if (width <= 0 || height <= 0 || (long)width * height > 16L * 1024 * 1024)
                return null;
            byte[] pixels = new byte[width * height * 4];
            source.CopyPixels(pixels, width * 4, 0);
            return new Picture(pixels, width, height);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static BitmapSource? Paint(string limb, Color skin, Picture? own, Picture? pants, Picture? shirt, Picture? graphic, string content)
    {
        if (!Limbs.TryGetValue(limb, out (string Mesh, int Width, int Height) layout))
            return null;
        bool torso = limb == Torso;
        bool arm = limb is LeftArm or RightArm;
        Picture? lower = torso || !arm ? pants : null;
        Picture? upper = torso || arm ? shirt : null;
        Picture? front = torso ? graphic : null;
        if (own == null && lower == null && upper == null && front == null)
            return null;
        TexturedMesh? mesh = Meshes.GetOrAdd(
            Path.Combine(content, "avatar", "compositing", layout.Mesh),
            path =>
            {
                try
                {
                    return File.Exists(path) ? MeshParser.ParseTextured(File.ReadAllBytes(path)) : null;
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    return null;
                }
            }
        );
        if (mesh == null && (lower != null || upper != null || front != null))
            return null;
        int width = layout.Width;
        int height = layout.Height;
        byte[] canvas = new byte[width * height * 4];
        for (int i = 0; i < canvas.Length; i += 4)
        {
            canvas[i] = skin.B;
            canvas[i + 1] = skin.G;
            canvas[i + 2] = skin.R;
            canvas[i + 3] = 255;
        }
        if (own != null)
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Blend(canvas, (y * width + x) * 4, own, (x + 0.5) / width, (y + 0.5) / height);
        if (mesh != null)
        {
            if (lower != null)
                Draw(canvas, width, height, mesh, lower, false);
            if (upper != null)
                Draw(canvas, width, height, mesh, upper, false);
            if (front != null)
                Draw(canvas, width, height, mesh, front, true);
        }
        BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, canvas, width * 4);
        result.Freeze();
        return result;
    }

    private static void Draw(byte[] canvas, int width, int height, TexturedMesh mesh, Picture source, bool frontOnly)
    {
        for (int t = 0; t + 2 < mesh.Indices.Count; t += 3)
        {
            int a = mesh.Indices[t];
            int b = mesh.Indices[t + 1];
            int c = mesh.Indices[t + 2];
            double ax = mesh.Positions[a].X, ay = height - mesh.Positions[a].Y;
            double bx = mesh.Positions[b].X, by = height - mesh.Positions[b].Y;
            double cx = mesh.Positions[c].X, cy = height - mesh.Positions[c].Y;
            double area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(area) < 1e-9)
                continue;
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int maxX = Math.Min(width - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
            int maxY = Math.Min(height - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            Point ua = mesh.Coords[a];
            Point ub = mesh.Coords[b];
            Point uc = mesh.Coords[c];
            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                double px = x + 0.5;
                double py = y + 0.5;
                double w0 = ((cx - bx) * (py - by) - (cy - by) * (px - bx)) / area;
                double w1 = ((ax - cx) * (py - cy) - (ay - cy) * (px - cx)) / area;
                double w2 = 1.0 - w0 - w1;
                if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6)
                    continue;
                double u = w0 * ua.X + w1 * ub.X + w2 * uc.X;
                double v = w0 * ua.Y + w1 * ub.Y + w2 * uc.Y;
                if (frontOnly)
                {
                    u = (u * TemplateWidth - FrontLeft) / FrontSize;
                    v = (v * TemplateHeight - FrontTop) / FrontSize;
                    if (u < 0 || u > 1 || v < 0 || v > 1)
                        continue;
                }
                Blend(canvas, (y * width + x) * 4, source, u, v);
            }
        }
    }

    private static void Blend(byte[] canvas, int offset, Picture source, double u, double v)
    {
        int x = Math.Clamp((int)(u * source.Width), 0, source.Width - 1);
        int y = Math.Clamp((int)(v * source.Height), 0, source.Height - 1);
        int from = (y * source.Width + x) * 4;
        int alpha = source.Pixels[from + 3];
        if (alpha == 0)
            return;
        canvas[offset] = (byte)((source.Pixels[from] * alpha + canvas[offset] * (255 - alpha)) / 255);
        canvas[offset + 1] = (byte)((source.Pixels[from + 1] * alpha + canvas[offset + 1] * (255 - alpha)) / 255);
        canvas[offset + 2] = (byte)((source.Pixels[from + 2] * alpha + canvas[offset + 2] * (255 - alpha)) / 255);
    }
}
