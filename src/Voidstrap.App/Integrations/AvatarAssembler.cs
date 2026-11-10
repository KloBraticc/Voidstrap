using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Voidstrap.Integrations.Animation;

namespace Voidstrap.Integrations;

internal static partial class AvatarAssembler
{
    private const string RootPart = "LowerTorso";

    private const string HeadPart = "Head";

    private const int MinimumParts = 8;

    private const int MaxTextureBytes = 16 * 1024 * 1024;

    private static readonly (string Joint, string Parent, string Child)[] Joints =
    [
        ("Waist", "LowerTorso", "UpperTorso"),
        ("Neck", "UpperTorso", "Head"),
        ("LeftShoulder", "UpperTorso", "LeftUpperArm"),
        ("LeftElbow", "LeftUpperArm", "LeftLowerArm"),
        ("LeftWrist", "LeftLowerArm", "LeftHand"),
        ("RightShoulder", "UpperTorso", "RightUpperArm"),
        ("RightElbow", "RightUpperArm", "RightLowerArm"),
        ("RightWrist", "RightLowerArm", "RightHand"),
        ("LeftHip", "LowerTorso", "LeftUpperLeg"),
        ("LeftKnee", "LeftUpperLeg", "LeftLowerLeg"),
        ("LeftAnkle", "LeftLowerLeg", "LeftFoot"),
        ("RightHip", "LowerTorso", "RightUpperLeg"),
        ("RightKnee", "RightUpperLeg", "RightLowerLeg"),
        ("RightAnkle", "RightLowerLeg", "RightFoot"),
    ];

    private static readonly Dictionary<string, string> PartColors = new(StringComparer.Ordinal)
    {
        ["Head"] = "headColor3",
        ["UpperTorso"] = "torsoColor3",
        ["LowerTorso"] = "torsoColor3",
        ["LeftUpperArm"] = "leftArmColor3",
        ["LeftLowerArm"] = "leftArmColor3",
        ["LeftHand"] = "leftArmColor3",
        ["RightUpperArm"] = "rightArmColor3",
        ["RightLowerArm"] = "rightArmColor3",
        ["RightHand"] = "rightArmColor3",
        ["LeftUpperLeg"] = "leftLegColor3",
        ["LeftLowerLeg"] = "leftLegColor3",
        ["LeftFoot"] = "leftLegColor3",
        ["RightUpperLeg"] = "rightLegColor3",
        ["RightLowerLeg"] = "rightLegColor3",
        ["RightFoot"] = "rightLegColor3",
    };

    private static readonly HashSet<string> BodyPartTypes = new(StringComparer.Ordinal) { "Torso", "LeftArm", "RightArm", "LeftLeg", "RightLeg", "DynamicHead", "Head" };

    private static readonly string[] PreferredFolders = ["R15ArtistIntent", "R15Fixed"];

    public static async Task<AvatarScene?> BuildAsync(
        AvatarState state,
        string installRoot,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        CancellationToken token
    )
    {
        string content = Path.Combine(installRoot, "content");
        string rigPath = Path.Combine(content, "avatar", "characterR15.rbxm");
        if (!File.Exists(rigPath))
        {
            log("The Roblox character rig was not found in the Roblox install");
            return null;
        }
        Dictionary<string, RbxmInstance> parts = new(StringComparer.Ordinal);
        foreach (RbxmInstance root in RbxmModelParser.ReadTree(await File.ReadAllBytesAsync(rigPath, token)))
        foreach (RbxmInstance instance in root.Descendants())
            if (instance.ClassName is "MeshPart" or "Part" && PartColors.ContainsKey(instance.Name))
                parts.TryAdd(instance.Name, instance);
        foreach (AvatarAsset asset in state.Assets)
        {
            if (!BodyPartTypes.Contains(asset.TypeName))
                continue;
            byte[]? bytes = await fetchAsset(asset.Id, token);
            if (bytes == null)
            {
                log("Body part " + asset.Id + " could not be downloaded");
                continue;
            }
            List<RbxmInstance> roots = RbxmModelParser.ReadTree(bytes);
            RbxmInstance? folder = PreferredFolders.Select(name => roots.FirstOrDefault(r => r.Name == name)).FirstOrDefault(f => f != null);
            IEnumerable<RbxmInstance> candidates = folder != null ? folder.Children : roots.Concat(roots.SelectMany(r => r.Descendants()));
            foreach (RbxmInstance candidate in candidates)
                if (candidate.ClassName == "MeshPart" && PartColors.ContainsKey(candidate.Name))
                    parts[candidate.Name] = candidate;
        }
        if (!parts.ContainsKey(RootPart))
            return null;
        Vector3D bodyScale = new(Scale(state, "width"), Scale(state, "height"), Scale(state, "depth"));
        double headScale = Scale(state, "head");
        Dictionary<string, RobloxCFrame> world = new(StringComparer.Ordinal) { [RootPart] = RobloxCFrame.Identity };
        foreach ((string joint, string parentName, string childName) in Joints)
        {
            if (!world.TryGetValue(parentName, out RobloxCFrame parentFrame) || !parts.TryGetValue(parentName, out RbxmInstance? parent) || !parts.TryGetValue(childName, out RbxmInstance? child))
                continue;
            string attachment = joint + "RigAttachment";
            if (
                parent.Child(attachment) is not RbxmInstance parentAttachment
                || child.Child(attachment) is not RbxmInstance childAttachment
                || !parentAttachment.CFrames.TryGetValue("CFrame", out RobloxCFrame c0)
                || !childAttachment.CFrames.TryGetValue("CFrame", out RobloxCFrame c1)
            )
            {
                log("The " + joint + " joint is missing on this avatar");
                continue;
            }
            world[childName] =
                parentFrame * Scaled(c0, parentName == HeadPart ? Uniform(headScale) : bodyScale) * Scaled(c1, childName == HeadPart ? Uniform(headScale) : bodyScale).Inverse();
        }
        List<GeometryModel3D> models = new();
        Bounds bounds = new();
        foreach ((string name, RbxmInstance part) in parts)
        {
            token.ThrowIfCancellationRequested();
            if (!world.TryGetValue(name, out RobloxCFrame frame))
                continue;
            try
            {
                GeometryModel3D? model = await BuildPartAsync(name, part, frame, name == HeadPart ? Uniform(headScale) : bodyScale, state, content, fetchAsset, bounds, token);
                if (model != null)
                    models.Add(model);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or NotSupportedException or ArgumentException)
            {
                log("The " + name + " part could not be built: " + ex.Message);
            }
        }
        if (models.Count < MinimumParts || !bounds.Valid)
            return null;
        Model3DGroup group = new();
        foreach (GeometryModel3D model in models)
            group.Children.Add(model);
        group.Freeze();
        return new AvatarScene(group, bounds.ToRect(), 180.0);
    }

    private static double Scale(AvatarState state, string key) => state.Scales.TryGetValue(key, out double value) && value is > 0.1 and < 10 ? value : 1.0;

    private static Vector3D Uniform(double value) => new(value, value, value);

    private static RobloxCFrame Scaled(RobloxCFrame frame, Vector3D scale) =>
        new(frame.X * scale.X, frame.Y * scale.Y, frame.Z * scale.Z, frame.R00, frame.R01, frame.R02, frame.R10, frame.R11, frame.R12, frame.R20, frame.R21, frame.R22);

    private static async Task<GeometryModel3D?> BuildPartAsync(
        string name,
        RbxmInstance part,
        RobloxCFrame frame,
        Vector3D bodyScale,
        AvatarState state,
        string content,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Bounds bounds,
        CancellationToken token
    )
    {
        byte[]? meshBytes;
        Vector3D meshScale = new(1, 1, 1);
        string? textureUri = null;
        if (part.ClassName == "MeshPart")
        {
            string? meshUri = part.Strings.GetValueOrDefault("MeshId") ?? part.Strings.GetValueOrDefault("MeshID") ?? part.Strings.GetValueOrDefault("MeshContent");
            meshBytes = await ResolveAsync(meshUri, content, fetchAsset, token);
            if (part.Vectors.TryGetValue("size", out Vector3D size) && part.Vectors.TryGetValue("InitialSize", out Vector3D initial) && initial.X > 1e-6 && initial.Y > 1e-6 && initial.Z > 1e-6)
                meshScale = new Vector3D(size.X / initial.X, size.Y / initial.Y, size.Z / initial.Z);
            textureUri = part.Strings.GetValueOrDefault("TextureID") ?? part.Strings.GetValueOrDefault("TextureId") ?? part.Strings.GetValueOrDefault("TextureContent");
        }
        else
        {
            RbxmInstance? special = part.Children.FirstOrDefault(c => c.ClassName == "SpecialMesh");
            string? meshUri = special?.Strings.GetValueOrDefault("MeshId");
            meshBytes = string.IsNullOrEmpty(meshUri)
                ? await ReadFileAsync(Path.Combine(content, "avatar", "heads", "head.mesh"), token)
                : await ResolveAsync(meshUri, content, fetchAsset, token);
            if (special != null && special.Vectors.TryGetValue("Scale", out Vector3D specialScale))
                meshScale = specialScale;
            textureUri = part.Children.FirstOrDefault(c => c.ClassName == "Decal")?.Strings.GetValueOrDefault("Texture");
        }
        if (meshBytes == null)
            return null;
        byte[]? textureBytes = string.IsNullOrEmpty(textureUri) ? null : await ResolveAsync(textureUri, content, fetchAsset, token);
        TexturedMesh source = MeshParser.ParseTextured(meshBytes);
        Matrix3D matrix = frame.ToMatrix3D();
        MeshGeometry3D mesh = new();
        foreach (Point3D position in source.Positions)
        {
            Point3D placed = matrix.Transform(new Point3D(position.X * meshScale.X * bodyScale.X, position.Y * meshScale.Y * bodyScale.Y, position.Z * meshScale.Z * bodyScale.Z));
            mesh.Positions.Add(placed);
            bounds.Add(placed);
        }
        foreach (Vector3D normal in source.Normals)
            mesh.Normals.Add(matrix.Transform(normal));
        foreach (Point coord in source.Coords)
            mesh.TextureCoordinates.Add(coord);
        foreach (int index in source.Indices)
            mesh.TriangleIndices.Add(index);
        Color color = ParseColor(state.BodyColors.GetValueOrDefault(PartColors[name]));
        Brush brush = new SolidColorBrush(color);
        if (textureBytes != null && textureBytes.Length <= MaxTextureBytes && Composite(textureBytes, color) is BitmapSource texture)
            brush = new ImageBrush(texture)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
            };
        DiffuseMaterial material = new(brush);
        GeometryModel3D model = new(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    private static Color ParseColor(string? hex)
    {
        if (hex != null && hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
            return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return Color.FromRgb(0xA3, 0xA2, 0xA5);
    }

    private static BitmapSource? Composite(byte[] bytes, Color color)
    {
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
            byte[] pixels = new byte[width * height * 4];
            source.CopyPixels(pixels, width * 4, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                pixels[i] = (byte)((pixels[i] * alpha + color.B * (255 - alpha)) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * alpha + color.G * (255 - alpha)) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * alpha + color.R * (255 - alpha)) / 255);
                pixels[i + 3] = 255;
            }
            BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ResolveAsync(string? uri, string content, Func<long, CancellationToken, Task<byte[]?>> fetchAsset, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return null;
        const string local = "rbxasset://";
        if (uri.StartsWith(local, StringComparison.OrdinalIgnoreCase))
        {
            string relative = uri[local.Length..].Replace('\\', '/');
            string path = Path.GetFullPath(Path.Combine(content, relative));
            return path.StartsWith(Path.GetFullPath(content) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? await ReadFileAsync(path, token) : null;
        }
        Match match = AssetIdPattern().Match(uri);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0 ? await fetchAsset(id, token) : null;
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken token) => File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;

    [GeneratedRegex("(\\d{3,})\\s*$")]
    private static partial Regex AssetIdPattern();

    private sealed class Bounds
    {
        private double _minX = double.MaxValue;
        private double _minY = double.MaxValue;
        private double _minZ = double.MaxValue;
        private double _maxX = double.MinValue;
        private double _maxY = double.MinValue;
        private double _maxZ = double.MinValue;

        public bool Valid => _maxX >= _minX;

        public void Add(Point3D point)
        {
            _minX = Math.Min(_minX, point.X);
            _minY = Math.Min(_minY, point.Y);
            _minZ = Math.Min(_minZ, point.Z);
            _maxX = Math.Max(_maxX, point.X);
            _maxY = Math.Max(_maxY, point.Y);
            _maxZ = Math.Max(_maxZ, point.Z);
        }

        public Rect3D ToRect() => new(_minX, _minY, _minZ, _maxX - _minX, _maxY - _minY, _maxZ - _minZ);
    }
}
