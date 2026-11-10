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

    private const long DefaultIdleAnimation = 507766388;

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

    private static readonly Dictionary<string, (string Property, Color Fallback)> EffectColors = new(StringComparer.Ordinal)
    {
        ["Sparkles"] = ("SparkleColor", Color.FromRgb(144, 25, 255)),
        ["Fire"] = ("Color", Color.FromRgb(236, 139, 70)),
        ["Smoke"] = ("Color", Color.FromRgb(120, 120, 120)),
        ["ParticleEmitter"] = ("", Color.FromRgb(255, 255, 255)),
    };

    private static readonly string[] RigFiles = ["characterR15DynamicHeadV2.rbxm", "characterR15.rbxm"];

    private static readonly string[] MeshKeys = ["MeshId", "MeshID", "MeshContent"];

    private static readonly string[] TextureKeys = ["TextureID", "TextureId", "TextureContent"];

    private static readonly string[] SurfaceKeys = ["ColorMap", "ColorMapContent"];

    private static readonly Dictionary<string, (string Class, string Property)> ClothingProperties = new(StringComparer.Ordinal)
    {
        ["Shirt"] = ("Shirt", "ShirtTemplate"),
        ["Pants"] = ("Pants", "PantsTemplate"),
        ["TShirt"] = ("ShirtGraphic", "Graphic"),
    };

    public static async Task<AvatarScene?> BuildAsync(
        AvatarState state,
        string installRoot,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        CancellationToken token
    )
    {
        string content = Path.Combine(installRoot, "content");
        if (state.AvatarType == "R6")
            return await BuildR6Async(state, content, fetchAsset, log, token);
        string rigPath = RigFiles.Select(name => Path.Combine(content, "avatar", name)).FirstOrDefault(File.Exists) ?? "";
        if (rigPath.Length == 0)
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
            int replaced = 0;
            if (asset.TypeName is "DynamicHead" or "Head" && parts.TryGetValue(HeadPart, out RbxmInstance? oldHead) && ClassicHead(roots) is RbxmInstance classic)
            {
                parts[HeadPart] = MergeHead(oldHead, classic);
                continue;
            }
            RbxmInstance? folder = PreferredFolders.Select(name => roots.FirstOrDefault(r => r.Name == name)).FirstOrDefault(f => f != null);
            IEnumerable<RbxmInstance> candidates = folder != null ? folder.Children : roots.Concat(roots.SelectMany(r => r.Descendants()));
            foreach (RbxmInstance candidate in candidates)
                if (candidate.ClassName == "MeshPart" && PartColors.ContainsKey(candidate.Name))
                {
                    parts[candidate.Name] = candidate;
                    replaced++;
                }
            if (replaced == 0)
                log("Body part " + asset.Id + " (" + asset.TypeName + ") had no usable parts: " + Outline(bytes, roots));
        }
        if (!parts.ContainsKey(RootPart))
            return null;
        Dictionary<string, BitmapSource?> painted = await PaintLimbsAsync(state, parts, content, fetchAsset, log, token);
        Vector3D bodyScale = new(Scale(state, "width"), Scale(state, "height"), Scale(state, "depth"));
        double headScale = Scale(state, "head");
        Dictionary<string, RobloxCFrame> world = new(StringComparer.Ordinal) { [RootPart] = RobloxCFrame.Identity };
        List<AvatarJoint> joints = new();
        List<AvatarPiece> pieces = new();
        List<AvatarEffect> effects = new();
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
            RobloxCFrame scaledC0 = Scaled(c0, parentName == HeadPart ? Uniform(headScale) : bodyScale);
            RobloxCFrame scaledC1 = Scaled(c1, childName == HeadPart ? Uniform(headScale) : bodyScale);
            world[childName] = parentFrame * scaledC0 * scaledC1.Inverse();
            joints.Add(new AvatarJoint(parentName, childName, scaledC0, scaledC1));
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
                Color color = ParseColor(state.BodyColors.GetValueOrDefault(PartColors[name]));
                BitmapSource? skin = AvatarClothing.PartLimbs.TryGetValue(name, out string? limb) ? painted.GetValueOrDefault(limb) : null;
                GeometryModel3D? model = await BuildPartAsync(part, frame, name == HeadPart ? Uniform(headScale) : bodyScale, color, true, name == HeadPart, skin, name, frame, pieces, content, fetchAsset, bounds, token);
                if (model != null)
                    models.Add(model);
                else
                    log("The " + name + " part has no mesh: " + string.Join(", ", part.Strings.Keys));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or NotSupportedException or ArgumentException)
            {
                log("The " + name + " part could not be built: " + ex.Message);
            }
        }
        if (models.Count < MinimumParts || !bounds.Valid)
            return null;
        Dictionary<(long, long), AvatarLayering.CagePoint> cage = await BuildCageAsync(parts, world, bodyScale, headScale, content, fetchAsset, log, token);
        await AddAccessoriesAsync(state, parts, world, bodyScale, headScale, cage, models, pieces, effects, bounds, content, fetchAsset, log, token);
        Model3DGroup group = new();
        foreach (GeometryModel3D model in models)
            group.Children.Add(model);
        group.Freeze();
        AnimationData? animation = await LoadAnimationAsync(state, fetchAsset, log, DefaultIdleAnimation, true, token);
        return new AvatarScene(group, bounds.ToRect(), 180.0) { Pieces = pieces, Rig = new AvatarRig(RootPart, joints, animation), Effects = effects };
    }

    private static (RobloxCFrame Frame, Vector3D Scale) Adjust(string? metaJson, RobloxCFrame frame, Vector3D scale)
    {
        if (string.IsNullOrEmpty(metaJson))
            return (frame, scale);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(metaJson);
            Vector3D position = ReadTriple(doc.RootElement, "position", 0);
            Vector3D rotation = ReadTriple(doc.RootElement, "rotation", 0);
            Vector3D size = ReadTriple(doc.RootElement, "scale", 1);
            const double radians = Math.PI / 180.0;
            return (
                frame * RobloxCFrame.FromPosition(position.X, position.Y, position.Z) * RobloxCFrame.Angles(rotation.X * radians, rotation.Y * radians, rotation.Z * radians),
                new Vector3D(scale.X * size.X, scale.Y * size.Y, scale.Z * size.Z)
            );
        }
        catch (JsonException)
        {
            return (frame, scale);
        }
    }

    private static Vector3D ReadTriple(JsonElement root, string name, double fallback)
    {
        if (!root.TryGetProperty(name, out JsonElement group) || group.ValueKind != JsonValueKind.Object)
            return new Vector3D(fallback, fallback, fallback);
        double Read(string axis) =>
            group.TryGetProperty(axis, out JsonElement value) && value.ValueKind == JsonValueKind.Number && double.IsFinite(value.GetDouble()) ? value.GetDouble() : fallback;
        Vector3D result = new(Read("X"), Read("Y"), Read("Z"));
        return name == "scale" && (result.X <= 0 || result.Y <= 0 || result.Z <= 0) ? new Vector3D(1, 1, 1) : result;
    }

    private static string? FirstValue(RbxmInstance instance, string[] keys)
    {
        foreach (string key in keys)
            if (instance.Strings.TryGetValue(key, out string? value) && value.Length > 0)
                return value;
        return null;
    }

    private static Vector3D MeshScale(RbxmInstance part) =>
        part.Vectors.TryGetValue("size", out Vector3D size) && part.Vectors.TryGetValue("InitialSize", out Vector3D initial) && initial.X > 1e-6 && initial.Y > 1e-6 && initial.Z > 1e-6
            ? new Vector3D(size.X / initial.X, size.Y / initial.Y, size.Z / initial.Z)
            : new Vector3D(1, 1, 1);

    private static async Task<Dictionary<(long, long), AvatarLayering.CagePoint>> BuildCageAsync(
        Dictionary<string, RbxmInstance> parts,
        Dictionary<string, RobloxCFrame> world,
        Vector3D bodyScale,
        double headScale,
        string content,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        CancellationToken token
    )
    {
        Dictionary<(long, long), AvatarLayering.CagePoint> cage = new();
        foreach ((string name, RbxmInstance part) in parts)
        {
            if (!world.TryGetValue(name, out RobloxCFrame frame) || part.Children.FirstOrDefault(c => c.ClassName == "WrapTarget") is not RbxmInstance target)
                continue;
            try
            {
                byte[]? bytes = await ResolveAsync(FirstValue(target, CageKeys), content, fetchAsset, token);
                if (bytes == null)
                    continue;
                TexturedMesh mesh = MeshParser.ParseTextured(bytes);
                Matrix3D origin = (target.CFrames.TryGetValue("CageOrigin", out RobloxCFrame cageOrigin) ? cageOrigin : RobloxCFrame.Identity).ToMatrix3D();
                Matrix3D placed = frame.ToMatrix3D();
                Vector3D scale = MeshScale(part);
                Vector3D body = name == HeadPart ? Uniform(headScale) : bodyScale;
                for (int i = 0; i < mesh.Positions.Count && i < mesh.Coords.Count; i++)
                {
                    Point3D local = origin.Transform(mesh.Positions[i]);
                    cage[AvatarLayering.Key(mesh.Coords[i])] = new AvatarLayering.CagePoint(
                        placed.Transform(new Point3D(local.X * scale.X * body.X, local.Y * scale.Y * body.Y, local.Z * scale.Z * body.Z)),
                        name
                    );
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException)
            {
                log("The fitting shape for " + name + " could not be read: " + ex.Message);
            }
        }
        return cage;
    }

    private static async Task<GeometryModel3D?> BuildLayeredAsync(
        RbxmInstance handle,
        RbxmInstance layer,
        Dictionary<(long, long), AvatarLayering.CagePoint> cage,
        Dictionary<string, RobloxCFrame> world,
        List<AvatarPiece> pieces,
        Bounds bounds,
        string content,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        CancellationToken token
    )
    {
        byte[]? meshBytes = await ResolveAsync(FirstValue(handle, MeshKeys), content, fetchAsset, token);
        byte[]? referenceBytes = await ResolveAsync(FirstValue(layer, ReferenceKeys), content, fetchAsset, token);
        byte[]? textureBytes = await ResolveAsync(TextureUri(handle), content, fetchAsset, token);
        if (meshBytes == null || referenceBytes == null)
            return null;
        TexturedMesh source = MeshParser.ParseTextured(meshBytes);
        AvatarLayering.Wrapped? wrapped = AvatarLayering.Wrap(
            source,
            MeshScale(handle),
            MeshParser.ParseTextured(referenceBytes),
            layer.CFrames.TryGetValue("ReferenceOrigin", out RobloxCFrame referenceOrigin) ? referenceOrigin : RobloxCFrame.Identity,
            cage,
            RootPart
        );
        if (wrapped == null)
            return null;
        string[] bones = wrapped.Parts.Distinct().Where(world.ContainsKey).ToArray();
        if (bones.Length == 0)
            return null;
        Dictionary<string, int> boneIndex = new(StringComparer.Ordinal);
        Matrix3D[] inverse = new Matrix3D[bones.Length];
        for (int i = 0; i < bones.Length; i++)
        {
            boneIndex[bones[i]] = i;
            inverse[i] = world[bones[i]].Inverse().ToMatrix3D();
        }
        MeshGeometry3D mesh = new();
        Point3D[] local = new Point3D[wrapped.Positions.Length];
        int[] bone = new int[wrapped.Positions.Length];
        for (int i = 0; i < wrapped.Positions.Length; i++)
        {
            mesh.Positions.Add(wrapped.Positions[i]);
            bounds.Add(wrapped.Positions[i]);
            bone[i] = boneIndex.GetValueOrDefault(wrapped.Parts[i]);
            local[i] = inverse[bone[i]].Transform(wrapped.Positions[i]);
        }
        foreach (Point coord in source.Coords)
            mesh.TextureCoordinates.Add(coord);
        foreach (int index in source.Indices)
            mesh.TriangleIndices.Add(index);
        mesh.Freeze();
        Brush brush = new SolidColorBrush(Color.FromRgb(0xA3, 0xA2, 0xA5));
        if (textureBytes != null && textureBytes.Length <= MaxTextureBytes && Composite(textureBytes, null) is BitmapSource texture)
            brush = new ImageBrush(texture)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
            };
        DiffuseMaterial material = new(brush);
        material.Freeze();
        pieces.Add(new AvatarPiece(mesh, material, "", Matrix3D.Identity) { Skin = new AvatarSkin(local, bone, bones) });
        GeometryModel3D model = new(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    private static readonly string[] CageKeys = ["CageMeshId", "CageMeshContent"];

    private static readonly string[] ReferenceKeys = ["ReferenceMeshId", "ReferenceMeshContent"];

    private static async Task AddAccessoriesAsync(
        AvatarState state,
        Dictionary<string, RbxmInstance> parts,
        Dictionary<string, RobloxCFrame> world,
        Vector3D bodyScale,
        double headScale,
        Dictionary<(long, long), AvatarLayering.CagePoint> cage,
        List<GeometryModel3D> models,
        List<AvatarPiece> pieces,
        List<AvatarEffect> effects,
        Bounds bounds,
        string content,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        CancellationToken token
    )
    {
        foreach (AvatarAsset asset in state.Assets)
        {
            token.ThrowIfCancellationRequested();
            if (BodyPartTypes.Contains(asset.TypeName) || !(asset.TypeName == "Hat" || asset.TypeName.EndsWith("Accessory", StringComparison.Ordinal)))
                continue;
            try
            {
                byte[]? bytes = await fetchAsset(asset.Id, token);
                if (bytes == null)
                {
                    log("Accessory " + asset.Id + " could not be downloaded");
                    continue;
                }
                List<RbxmInstance> roots = RbxmModelParser.ReadTree(bytes);
                RbxmInstance? accessory = roots.Concat(roots.SelectMany(r => r.Descendants())).FirstOrDefault(i => i.ClassName == "Accessory");
                RbxmInstance? handle = accessory?.Child("Handle") ?? accessory?.Children.FirstOrDefault(c => c.ClassName is "MeshPart" or "Part");
                RbxmInstance? attachment = handle?.Children.FirstOrDefault(c => c.ClassName == "Attachment" && c.CFrames.ContainsKey("CFrame"));
                if (handle != null && cage.Count > 0 && handle.Children.FirstOrDefault(c => c.ClassName == "WrapLayer") is RbxmInstance layer)
                {
                    GeometryModel3D? wrapped = await BuildLayeredAsync(handle, layer, cage, world, pieces, bounds, content, fetchAsset, token);
                    if (wrapped != null)
                    {
                        models.Add(wrapped);
                        continue;
                    }
                    log("Layered item " + asset.Id + " could not be fitted to the body, so it is placed without bending");
                }
                RobloxCFrame frame;
                Vector3D scale;
                string owner = HeadPart;
                if (handle != null && attachment != null)
                {
                    (string? partName, RbxmInstance? bodyAttachment) = parts
                        .Where(p => world.ContainsKey(p.Key))
                        .Select(p => ((string?)p.Key, p.Value.Child(attachment.Name)))
                        .FirstOrDefault(p => p.Item2 != null && p.Item2.CFrames.ContainsKey("CFrame"));
                    if (partName == null || bodyAttachment == null)
                    {
                        log("Accessory " + asset.Id + " attaches to " + attachment.Name + ", which this body does not have");
                        continue;
                    }
                    owner = partName;
                    scale = partName == HeadPart ? Uniform(headScale) : bodyScale;
                    frame = world[partName] * Scaled(bodyAttachment.CFrames["CFrame"], scale) * Scaled(attachment.CFrames["CFrame"], scale).Inverse();
                }
                else if (handle != null && accessory != null && accessory.CFrames.TryGetValue("AttachmentPoint", out RobloxCFrame point) && world.TryGetValue(HeadPart, out RobloxCFrame headFrame))
                {
                    scale = Uniform(headScale);
                    frame = headFrame * Scaled(RobloxCFrame.FromPosition(0, 0.5, 0), scale) * Scaled(point, scale).Inverse();
                }
                else
                {
                    log("Accessory " + asset.Id + " (" + asset.TypeName + ") has an unknown layout: " + Outline(bytes, roots));
                    continue;
                }
                (frame, scale) = Adjust(asset.MetaJson, frame, scale);
                GeometryModel3D? model = await BuildPartAsync(handle, frame, scale, Color.FromRgb(0xA3, 0xA2, 0xA5), false, false, null, owner, world[owner], pieces, content, fetchAsset, bounds, token);
                if (model != null)
                    models.Add(model);
                else
                    log("Accessory " + asset.Id + " has no mesh: " + Outline(bytes, roots));
                Matrix3D offset = (world[owner].Inverse() * frame).ToMatrix3D();
                foreach (RbxmInstance child in handle.Descendants())
                    if (EffectColors.TryGetValue(child.ClassName, out (string Property, Color Fallback) effect))
                        effects.Add(
                            new AvatarEffect(
                                child.ClassName,
                                owner,
                                offset,
                                child.Vectors.TryGetValue(effect.Property, out Vector3D tint)
                                    ? Color.FromRgb((byte)Math.Clamp(tint.X * 255, 0, 255), (byte)Math.Clamp(tint.Y * 255, 0, 255), (byte)Math.Clamp(tint.Z * 255, 0, 255))
                                    : effect.Fallback
                            )
                        );
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or NotSupportedException or ArgumentException)
            {
                log("Accessory " + asset.Id + " could not be built: " + ex.Message);
            }
        }
    }

    private static async Task<AnimationData?> LoadAnimationAsync(
        AvatarState state,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        long fallbackId,
        bool useEquipped,
        CancellationToken token
    )
    {
        long animationId = fallbackId;
        AvatarAsset? equipped = useEquipped ? state.Assets.FirstOrDefault(a => a.TypeName == "IdleAnimation") : null;
        if (equipped != null && await fetchAsset(equipped.Id, token) is byte[] pack)
        {
            List<RbxmInstance> roots = RbxmModelParser.ReadTree(pack);
            List<RbxmInstance> all = roots.Concat(roots.SelectMany(r => r.Descendants())).ToList();
            RbxmInstance? idle = all.FirstOrDefault(i => i.Name == "idle") ?? all.FirstOrDefault(i => i.ClassName == "Animation")?.Parent;
            string? uri = idle?.Children.FirstOrDefault(c => c.ClassName == "Animation")?.Strings.GetValueOrDefault("AnimationId");
            Match match = uri == null ? Match.Empty : AssetQueryPattern().Match(uri);
            if (!match.Success && uri != null)
                match = AssetIdPattern().Match(uri);
            if (match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long found) && found > 0)
                animationId = found;
            else
                log("The equipped idle animation has an unknown layout: " + Outline(pack, roots));
        }
        byte[]? bytes = await fetchAsset(animationId, token);
        AnimationData? animation = bytes == null ? null : RobloxAnimationParser.Parse(bytes);
        if (animation == null || animation.Keyframes.Count == 0)
        {
            log("Animation " + animationId + " could not be read, so the avatar stands still");
            return null;
        }
        return animation;
    }

    private static string Outline(byte[] bytes, List<RbxmInstance> roots)
    {
        if (roots.Count == 0)
            return "unreadable file starting with " + Convert.ToHexString(bytes, 0, Math.Min(bytes.Length, 12));
        return string.Join(
            "; ",
            roots
                .Concat(roots.SelectMany(r => r.Descendants()))
                .Take(30)
                .Select(i => i.ClassName + " " + i.Name + (i.Strings.Count > 0 ? " [" + string.Join(",", i.Strings.Where(v => v.Value.Length > 0).Select(v => v.Key)) + "]" : ""))
        );
    }

    private static double Scale(AvatarState state, string key) => state.Scales.TryGetValue(key, out double value) && value is > 0.1 and < 10 ? value : 1.0;

    private static Vector3D Uniform(double value) => new(value, value, value);

    private static RobloxCFrame Scaled(RobloxCFrame frame, Vector3D scale) =>
        new(frame.X * scale.X, frame.Y * scale.Y, frame.Z * scale.Z, frame.R00, frame.R01, frame.R02, frame.R10, frame.R11, frame.R12, frame.R20, frame.R21, frame.R22);

    private static async Task<GeometryModel3D?> BuildPartAsync(
        RbxmInstance part,
        RobloxCFrame frame,
        Vector3D bodyScale,
        Color color,
        bool opaque,
        bool defaultHead,
        BitmapSource? painted,
        string owner,
        RobloxCFrame ownerFrame,
        List<AvatarPiece> pieces,
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
            textureUri = TextureUri(part);
            if (meshBytes == null && defaultHead)
            {
                meshBytes = await ReadFileAsync(Path.Combine(content, "avatar", "heads", "head.mesh"), token);
                meshScale = new Vector3D(1, 1, 1);
                textureUri = "rbxasset://textures/face.png";
            }
        }
        else
        {
            RbxmInstance? special = part.Children.FirstOrDefault(c => c.ClassName == "SpecialMesh");
            string? meshUri = special?.Strings.GetValueOrDefault("MeshId");
            meshBytes = string.IsNullOrEmpty(meshUri) ? null : await ResolveAsync(meshUri, content, fetchAsset, token);
            textureUri = special == null ? null : FirstValue(special, TextureKeys);
            if (meshBytes != null && special != null && special.Vectors.TryGetValue("Scale", out Vector3D specialScale) && specialScale.X > 0 && specialScale.Y > 0 && specialScale.Z > 0)
                meshScale = specialScale;
            if (meshBytes == null && defaultHead)
            {
                meshBytes = await ReadFileAsync(Path.Combine(content, "avatar", "heads", "head.mesh"), token);
                textureUri = part.Children.FirstOrDefault(c => c.ClassName == "Decal")?.Strings.GetValueOrDefault("Texture") ?? "rbxasset://textures/face.png";
            }
        }
        if (meshBytes == null)
            return null;
        byte[]? textureBytes = painted != null || string.IsNullOrEmpty(textureUri) ? null : await ResolveAsync(textureUri, content, fetchAsset, token);
        TexturedMesh source = MeshParser.ParseTextured(meshBytes);
        Matrix3D matrix = frame.ToMatrix3D();
        MeshGeometry3D mesh = new();
        MeshGeometry3D local = new();
        foreach (Point3D position in source.Positions)
        {
            Point3D sized = new(position.X * meshScale.X * bodyScale.X, position.Y * meshScale.Y * bodyScale.Y, position.Z * meshScale.Z * bodyScale.Z);
            Point3D placed = matrix.Transform(sized);
            local.Positions.Add(sized);
            mesh.Positions.Add(placed);
            bounds.Add(placed);
        }
        foreach (Vector3D normal in source.Normals)
        {
            local.Normals.Add(normal);
            mesh.Normals.Add(matrix.Transform(normal));
        }
        foreach (Point coord in source.Coords)
        {
            local.TextureCoordinates.Add(coord);
            mesh.TextureCoordinates.Add(coord);
        }
        foreach (int index in source.Indices)
        {
            local.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index);
        }
        Brush brush = new SolidColorBrush(color);
        if ((painted ?? (textureBytes != null && textureBytes.Length <= MaxTextureBytes ? Composite(textureBytes, opaque ? color : null) : null)) is BitmapSource texture)
            brush = new ImageBrush(texture)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
            };
        DiffuseMaterial material = new(brush);
        material.Freeze();
        local.Freeze();
        pieces.Add(new AvatarPiece(local, material, owner, (ownerFrame.Inverse() * frame).ToMatrix3D()));
        GeometryModel3D model = new(mesh, material) { BackMaterial = material };
        model.Freeze();
        return model;
    }

    private static string? TextureUri(RbxmInstance part)
    {
        foreach (string key in TextureKeys)
            if (part.Strings.TryGetValue(key, out string? value) && value.Length > 0)
                return value;
        RbxmInstance? surface = part.Children.FirstOrDefault(c => c.ClassName == "SurfaceAppearance");
        if (surface != null)
            foreach (string key in SurfaceKeys)
                if (surface.Strings.TryGetValue(key, out string? value) && value.Length > 0)
                    return value;
        return null;
    }

    private static async Task<Dictionary<string, AvatarClothing.Picture?>> LoadClothingAsync(
        AvatarState state,
        string content,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        CancellationToken token
    )
    {
        Dictionary<string, AvatarClothing.Picture?> layers = new(StringComparer.Ordinal);
        foreach (AvatarAsset asset in state.Assets)
        {
            if (!ClothingProperties.TryGetValue(asset.TypeName, out (string Class, string Property) kind))
                continue;
            byte[]? bytes = await fetchAsset(asset.Id, token);
            List<RbxmInstance> roots = bytes == null ? new List<RbxmInstance>() : RbxmModelParser.ReadTree(bytes);
            string? uri = roots.Concat(roots.SelectMany(r => r.Descendants())).FirstOrDefault(i => i.ClassName == kind.Class)?.Strings.GetValueOrDefault(kind.Property);
            AvatarClothing.Picture? picture = AvatarClothing.Decode(await ResolveAsync(uri, content, fetchAsset, token));
            if (picture == null)
                log("Clothing " + asset.Id + " (" + asset.TypeName + ") could not be loaded" + (bytes == null ? "" : ": " + Outline(bytes, roots)));
            layers[asset.TypeName] = picture;
        }
        return layers;
    }

    private static async Task<Dictionary<string, BitmapSource?>> PaintLimbsAsync(
        AvatarState state,
        Dictionary<string, RbxmInstance> parts,
        string content,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        CancellationToken token
    )
    {
        Dictionary<string, BitmapSource?> painted = new(StringComparer.Ordinal);
        Dictionary<string, AvatarClothing.Picture?> layers = await LoadClothingAsync(state, content, fetchAsset, log, token);
        foreach (string limb in AvatarClothing.PartLimbs.Values.Distinct())
        {
            AvatarClothing.Picture? own = null;
            foreach ((string name, RbxmInstance part) in parts)
                if (own == null && AvatarClothing.PartLimbs.GetValueOrDefault(name) == limb && TextureUri(part) is string uri)
                    own = AvatarClothing.Decode(await ResolveAsync(uri, content, fetchAsset, token));
            string colorKey = PartColors[AvatarClothing.PartLimbs.First(p => p.Value == limb).Key];
            painted[limb] = AvatarClothing.Paint(
                limb,
                ParseColor(state.BodyColors.GetValueOrDefault(colorKey)),
                own,
                layers.GetValueOrDefault("Pants"),
                layers.GetValueOrDefault("Shirt"),
                layers.GetValueOrDefault("TShirt"),
                content
            );
        }
        return painted;
    }

    private static Color ParseColor(string? hex)
    {
        if (hex != null && hex.Length == 6 && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
            return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return Color.FromRgb(0xA3, 0xA2, 0xA5);
    }

    private static BitmapSource? Composite(byte[] bytes, Color? underlay)
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
            if (underlay is not Color color)
                return source;
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
        Match match = AssetQueryPattern().Match(uri);
        if (!match.Success)
            match = AssetIdPattern().Match(uri);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0 ? await fetchAsset(id, token) : null;
    }

    private static async Task<byte[]?> ReadFileAsync(string path, CancellationToken token) => File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;

    [GeneratedRegex("[?&]id=(\\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AssetQueryPattern();

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
