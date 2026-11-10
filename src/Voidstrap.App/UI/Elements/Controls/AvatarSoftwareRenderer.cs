using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Voidstrap.UI.Elements.Controls;

internal sealed class AvatarSoftwareRenderer
{
    private const float AmbientLight = 0.77f;

    private const float NearPlane = 0.0001f;

    private static readonly (Vector3D Direction, float Strength)[] Lights =
    [
        (Normalized(new Vector3D(0.4, 0.5, 0.75)), 0.29f),
        (Normalized(new Vector3D(-0.5, 0.2, -0.8)), 0.165f),
    ];

    private readonly List<Mesh> _avatar = new();

    private Mesh? _backdrop;

    private readonly List<Mesh> _effects = new();

    private readonly Dictionary<ImageSource, Texture> _effectTextures = new();

    private float[] _depth = [];

    private float[] _projected = [];

    public void SetAvatar(Model3D? model)
    {
        _avatar.Clear();
        Dictionary<ImageSource, Texture> textures = new();
        Collect(model, textures, false, _avatar);
    }

    public void SetTransforms(IReadOnlyList<Matrix3D> transforms)
    {
        for (int i = 0; i < _avatar.Count && i < transforms.Count; i++)
        {
            _avatar[i].Transform = transforms[i];
            _avatar[i].Moved = !transforms[i].IsIdentity;
        }
    }

    public void SetEffects(IEnumerable<GeometryModel3D> models)
    {
        _effects.Clear();
        foreach (GeometryModel3D model in models)
            Collect(model, _effectTextures, true, _effects);
    }

    public void SetBackdrop(Model3D? model)
    {
        List<Mesh> meshes = new();
        Collect(model, new Dictionary<ImageSource, Texture>(), true, meshes);
        _backdrop = meshes.FirstOrDefault();
    }

    public void Render(int[] frame, int width, int height, Point3D origin, Vector3D look, Vector3D upDirection, double fieldOfView)
    {
        Array.Clear(frame);
        if (_depth.Length != frame.Length)
            _depth = new float[frame.Length];
        else
            Array.Clear(_depth);
        if (look.LengthSquared < 1e-12)
            return;
        Vector3D forward = Normalized(look);
        Vector3D right = Vector3D.CrossProduct(forward, upDirection);
        if (right.LengthSquared < 1e-12)
            return;
        right.Normalize();
        Vector3D up = Vector3D.CrossProduct(right, forward);
        double tanX = Math.Tan(fieldOfView * Math.PI / 360.0);
        double tanY = tanX * height / width;
        View view = new(origin, forward, right, up, tanX, tanY, width, height);
        if (_backdrop != null)
            Draw(_backdrop, frame, view);
        foreach (Mesh mesh in _avatar)
            Draw(mesh, frame, view);
        foreach (Mesh mesh in _effects)
            Draw(mesh, frame, view);
    }

    private static Vector3D Normalized(Vector3D vector)
    {
        vector.Normalize();
        return vector;
    }

    private static void Collect(Model3D? model, Dictionary<ImageSource, Texture> textures, bool unlit, List<Mesh> meshes)
    {
        if (model is Model3DGroup group)
        {
            foreach (Model3D child in group.Children)
                Collect(child, textures, unlit, meshes);
            return;
        }
        if (model is not GeometryModel3D { Geometry: MeshGeometry3D geometry } || geometry.TriangleIndices.Count < 3)
            return;
        int count = geometry.Positions.Count;
        Mesh mesh = new()
        {
            Positions = new float[count * 3],
            Coords = new float[count * 2],
            Light = new float[count],
            Indices = geometry.TriangleIndices.Where(i => i >= 0 && i < count).ToArray(),
        };
        if (mesh.Indices.Length != geometry.TriangleIndices.Count)
            return;
        int index = 0;
        foreach (Point3D position in geometry.Positions)
        {
            mesh.Positions[index * 3] = (float)position.X;
            mesh.Positions[index * 3 + 1] = (float)position.Y;
            mesh.Positions[index * 3 + 2] = (float)position.Z;
            index++;
        }
        if (geometry.TextureCoordinates.Count == count)
        {
            index = 0;
            foreach (System.Windows.Point coord in geometry.TextureCoordinates)
            {
                mesh.Coords[index * 2] = (float)coord.X;
                mesh.Coords[index * 2 + 1] = (float)coord.Y;
                index++;
            }
        }
        Array.Fill(mesh.Light, 1f);
        if (!unlit && geometry.Normals.Count == count)
        {
            index = 0;
            foreach (Vector3D normal in geometry.Normals)
            {
                float light = AmbientLight;
                if (normal.LengthSquared > 1e-12)
                {
                    Vector3D unit = Normalized(normal);
                    foreach ((Vector3D direction, float strength) in Lights)
                        light += strength * (float)Math.Abs(Vector3D.DotProduct(unit, direction));
                }
                mesh.Light[index++] = Math.Min(light, 1f);
            }
        }
        if (model.Transform is Transform3D transform && !transform.Value.IsIdentity)
        {
            mesh.Transform = transform.Value;
            mesh.Moved = true;
        }
        Material? material = ((GeometryModel3D)model).Material;
        if (material is EmissiveMaterial glow)
        {
            mesh.Additive = true;
            mesh.Red = glow.Color.R;
            mesh.Green = glow.Color.G;
            mesh.Blue = glow.Color.B;
        }
        Brush? brush = FindBrush(material);
        if (brush is ImageBrush { ImageSource: BitmapSource source } imageBrush)
        {
            if (!textures.TryGetValue(source, out Texture? texture))
            {
                texture = ReadTexture(source);
                textures[source] = texture;
            }
            mesh.Texture = texture;
            mesh.Mirror = imageBrush.TileMode == TileMode.FlipXY;
        }
        else if (brush is SolidColorBrush solid)
        {
            mesh.Red = solid.Color.R;
            mesh.Green = solid.Color.G;
            mesh.Blue = solid.Color.B;
        }
        meshes.Add(mesh);
    }

    private static Brush? FindBrush(Material? material)
    {
        switch (material)
        {
            case DiffuseMaterial diffuse:
                return diffuse.Brush;
            case EmissiveMaterial emissive:
                return emissive.Brush;
            case MaterialGroup group:
                Brush? fallback = null;
                foreach (Material child in group.Children)
                {
                    Brush? brush = FindBrush(child);
                    if (brush is ImageBrush)
                        return brush;
                    fallback ??= brush;
                }
                return fallback;
            default:
                return null;
        }
    }

    private static Texture ReadTexture(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgra32)
        {
            FormatConvertedBitmap converted = new(source, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            source = converted;
        }
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        byte[] pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        return new Texture(pixels, width, height);
    }

    private void Draw(Mesh mesh, int[] frame, View view)
    {
        int count = mesh.Light.Length;
        if (_projected.Length < count * 3)
            _projected = new float[count * 3];
        float[] projected = _projected;
        Matrix3D move = mesh.Transform;
        for (int i = 0; i < count; i++)
        {
            double sx = mesh.Positions[i * 3];
            double sy = mesh.Positions[i * 3 + 1];
            double sz = mesh.Positions[i * 3 + 2];
            if (mesh.Moved)
            {
                double tx = sx * move.M11 + sy * move.M21 + sz * move.M31 + move.OffsetX;
                double ty = sx * move.M12 + sy * move.M22 + sz * move.M32 + move.OffsetY;
                double tz = sx * move.M13 + sy * move.M23 + sz * move.M33 + move.OffsetZ;
                sx = tx;
                sy = ty;
                sz = tz;
            }
            double dx = sx - view.Origin.X;
            double dy = sy - view.Origin.Y;
            double dz = sz - view.Origin.Z;
            double z = dx * view.Forward.X + dy * view.Forward.Y + dz * view.Forward.Z;
            if (z < NearPlane)
            {
                projected[i * 3 + 2] = 0f;
                continue;
            }
            double x = dx * view.Right.X + dy * view.Right.Y + dz * view.Right.Z;
            double y = dx * view.Up.X + dy * view.Up.Y + dz * view.Up.Z;
            projected[i * 3] = (float)((x / (z * view.TanX) * 0.5 + 0.5) * view.Width);
            projected[i * 3 + 1] = (float)((0.5 - y / (z * view.TanY) * 0.5) * view.Height);
            projected[i * 3 + 2] = (float)(1.0 / z);
        }
        float[] depth = _depth;
        Texture? texture = mesh.Texture;
        int[] indices = mesh.Indices;
        for (int t = 0; t + 2 < indices.Length; t += 3)
        {
            int a = indices[t];
            int b = indices[t + 1];
            int c = indices[t + 2];
            float az = projected[a * 3 + 2];
            float bz = projected[b * 3 + 2];
            float cz = projected[c * 3 + 2];
            if (az <= 0f || bz <= 0f || cz <= 0f)
                continue;
            float ax = projected[a * 3];
            float ay = projected[a * 3 + 1];
            float bx = projected[b * 3];
            float by = projected[b * 3 + 1];
            float cx = projected[c * 3];
            float cy = projected[c * 3 + 1];
            float area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
            if (Math.Abs(area) < 1e-6f || float.IsNaN(area) || float.IsInfinity(area))
                continue;
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(ax, Math.Min(bx, cx))));
            int maxX = Math.Min(view.Width - 1, (int)Math.Ceiling(Math.Max(ax, Math.Max(bx, cx))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(ay, Math.Min(by, cy))));
            int maxY = Math.Min(view.Height - 1, (int)Math.Ceiling(Math.Max(ay, Math.Max(by, cy))));
            if (minX > maxX || minY > maxY)
                continue;
            float inverseArea = 1f / area;
            float la = mesh.Light[a];
            float lb = mesh.Light[b];
            float lc = mesh.Light[c];
            float ua = mesh.Coords[a * 2] * az;
            float va = mesh.Coords[a * 2 + 1] * az;
            float ub = mesh.Coords[b * 2] * bz;
            float vb = mesh.Coords[b * 2 + 1] * bz;
            float uc = mesh.Coords[c * 2] * cz;
            float vc = mesh.Coords[c * 2 + 1] * cz;
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                int row = y * view.Width;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float w0 = ((cx - bx) * (py - by) - (cy - by) * (px - bx)) * inverseArea;
                    float w1 = ((ax - cx) * (py - cy) - (ay - cy) * (px - cx)) * inverseArea;
                    float w2 = 1f - w0 - w1;
                    if (w0 < 0f || w1 < 0f || w2 < 0f)
                        continue;
                    float inverseZ = w0 * az + w1 * bz + w2 * cz;
                    int pixel = row + x;
                    if (inverseZ <= depth[pixel])
                        continue;
                    int red = mesh.Red;
                    int green = mesh.Green;
                    int blue = mesh.Blue;
                    if (texture != null && mesh.Additive)
                    {
                        float u = (w0 * ua + w1 * ub + w2 * uc) / inverseZ;
                        float v = (w0 * va + w1 * vb + w2 * vc) / inverseZ;
                        int strength = texture.Pixels[texture.Offset(u, v, false) + 3];
                        if (strength == 0)
                            continue;
                        int existing = frame[pixel];
                        int addRed = Math.Min(255, ((existing >> 16) & 255) + red * strength / 255);
                        int addGreen = Math.Min(255, ((existing >> 8) & 255) + green * strength / 255);
                        int addBlue = Math.Min(255, (existing & 255) + blue * strength / 255);
                        int addAlpha = Math.Max((existing >> 24) & 255, Math.Max(addRed, Math.Max(addGreen, addBlue)));
                        frame[pixel] = addAlpha << 24 | addRed << 16 | addGreen << 8 | addBlue;
                        continue;
                    }
                    if (texture != null)
                    {
                        float u = (w0 * ua + w1 * ub + w2 * uc) / inverseZ;
                        float v = (w0 * va + w1 * vb + w2 * vc) / inverseZ;
                        int offset = texture.Offset(u, v, mesh.Mirror);
                        if (texture.Pixels[offset + 3] < 128)
                            continue;
                        blue = texture.Pixels[offset];
                        green = texture.Pixels[offset + 1];
                        red = texture.Pixels[offset + 2];
                    }
                    float light = w0 * la + w1 * lb + w2 * lc;
                    depth[pixel] = inverseZ;
                    frame[pixel] = unchecked((int)0xFF000000) | (int)(red * light) << 16 | (int)(green * light) << 8 | (int)(blue * light);
                }
            }
        }
    }

    private readonly record struct View(Point3D Origin, Vector3D Forward, Vector3D Right, Vector3D Up, double TanX, double TanY, int Width, int Height);

    private sealed record Texture(byte[] Pixels, int Width, int Height)
    {
        public int Offset(float u, float v, bool mirror)
        {
            if (float.IsNaN(u) || float.IsInfinity(u))
                u = 0f;
            if (float.IsNaN(v) || float.IsInfinity(v))
                v = 0f;
            if (mirror)
            {
                u -= MathF.Floor(u * 0.5f) * 2f;
                if (u > 1f)
                    u = 2f - u;
                v -= MathF.Floor(v * 0.5f) * 2f;
                if (v > 1f)
                    v = 2f - v;
            }
            else
            {
                u -= MathF.Floor(u);
                v -= MathF.Floor(v);
            }
            int x = Math.Clamp((int)(u * Width), 0, Width - 1);
            int y = Math.Clamp((int)(v * Height), 0, Height - 1);
            return (y * Width + x) * 4;
        }
    }

    private sealed class Mesh
    {
        public required float[] Positions { get; init; }

        public required float[] Coords { get; init; }

        public required float[] Light { get; init; }

        public required int[] Indices { get; init; }

        public Texture? Texture { get; set; }

        public bool Mirror { get; set; }

        public Matrix3D Transform { get; set; } = Matrix3D.Identity;

        public bool Moved { get; set; }

        public bool Additive { get; set; }

        public int Red { get; set; } = 200;

        public int Green { get; set; } = 200;

        public int Blue { get; set; } = 200;
    }
}
