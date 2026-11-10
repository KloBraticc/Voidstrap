using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Voidstrap.Integrations.Animation;

namespace Voidstrap.Integrations;

internal static partial class AvatarAssembler
{
    private const string R6Root = "HumanoidRootPart";

    private const long R6IdleAnimation = 180435571;

    private const int R6MinimumParts = 5;

    private static readonly Dictionary<string, (string Color, string Mesh, string Limb)> R6Parts = new(StringComparer.Ordinal)
    {
        ["Head"] = ("headColor3", "", ""),
        ["Torso"] = ("torsoColor3", "rbxasset://avatar/meshes/torso.mesh", AvatarClothing.Torso),
        ["Left Arm"] = ("leftArmColor3", "rbxasset://avatar/meshes/leftarm.mesh", AvatarClothing.LeftArm),
        ["Right Arm"] = ("rightArmColor3", "rbxasset://avatar/meshes/rightarm.mesh", AvatarClothing.RightArm),
        ["Left Leg"] = ("leftLegColor3", "rbxasset://avatar/meshes/leftleg.mesh", AvatarClothing.LeftLeg),
        ["Right Leg"] = ("rightLegColor3", "rbxasset://avatar/meshes/rightleg.mesh", AvatarClothing.RightLeg),
    };

    private static readonly string[] R6BodyParts = ["Head", "Torso", "Left Arm", "Right Arm", "Left Leg", "Right Leg"];

    private static async Task<AvatarScene?> BuildR6Async(
        AvatarState state,
        string content,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        CancellationToken token
    )
    {
        string rigPath = Path.Combine(content, "avatar", "character.rbxm");
        if (!File.Exists(rigPath))
        {
            log("The classic Roblox character was not found in the Roblox install");
            return null;
        }
        Dictionary<string, RbxmInstance> parts = new(StringComparer.Ordinal);
        foreach (RbxmInstance root in RbxmModelParser.ReadTree(await File.ReadAllBytesAsync(rigPath, token)))
        foreach (RbxmInstance instance in root.Descendants())
            if (instance.ClassName == "Part" && R6Parts.ContainsKey(instance.Name))
                parts.TryAdd(instance.Name, instance);
        foreach (string name in R6BodyParts)
            if (!parts.ContainsKey(name))
                return null;
        Dictionary<string, string> meshes = R6Parts.Where(p => p.Value.Mesh.Length > 0).ToDictionary(p => p.Key, p => p.Value.Mesh, StringComparer.Ordinal);
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
            List<RbxmInstance> all = roots.Concat(roots.SelectMany(r => r.Descendants())).ToList();
            if (asset.TypeName is "DynamicHead" or "Head")
            {
                if (ClassicHead(all) is RbxmInstance head)
                    parts[HeadPart] = MergeHead(parts[HeadPart], head);
                continue;
            }
            foreach (RbxmInstance mesh in all.Where(i => i.ClassName == "CharacterMesh"))
            {
                int bodyPart = (int)mesh.Numbers.GetValueOrDefault("BodyPart", -1);
                long meshId = (long)mesh.Numbers.GetValueOrDefault("MeshId");
                if (bodyPart >= 1 && bodyPart < R6BodyParts.Length && meshId > 0)
                    meshes[R6BodyParts[bodyPart]] = "rbxassetid://" + meshId.ToString(CultureInfo.InvariantCulture);
            }
        }
        RigDefinition rig = RobloxRig.R6();
        Dictionary<string, RobloxCFrame> world = new(StringComparer.Ordinal) { [R6Root] = RobloxCFrame.Identity };
        List<AvatarJoint> joints = new();
        bool progressed = true;
        while (progressed)
        {
            progressed = false;
            foreach (RigJoint joint in rig.Joints)
            {
                if (world.ContainsKey(joint.Part1) || !world.TryGetValue(joint.Part0, out RobloxCFrame parent))
                    continue;
                world[joint.Part1] = parent * joint.C0 * joint.C1.Inverse();
                joints.Add(new AvatarJoint(joint.Part0, joint.Part1, joint.C0, joint.C1));
                progressed = true;
            }
        }
        BitmapSource? atlas = await PaintClassicAsync(state, content, fetchAsset, log, token);
        List<AvatarPiece> pieces = new();
        List<AvatarEffect> effects = new();
        List<GeometryModel3D> models = new();
        Bounds bounds = new();
        Vector3D unit = new(1, 1, 1);
        foreach (string name in R6BodyParts)
        {
            token.ThrowIfCancellationRequested();
            if (!world.TryGetValue(name, out RobloxCFrame frame))
                continue;
            try
            {
                RbxmInstance part = parts[name];
                if (meshes.TryGetValue(name, out string? meshUri))
                {
                    part = new RbxmInstance { ClassName = "Part", Name = name };
                    RbxmInstance special = new() { ClassName = "SpecialMesh", Name = "Mesh", Parent = part };
                    special.Strings["MeshId"] = meshUri;
                    part.Children.Add(special);
                }
                Color color = ParseColor(state.BodyColors.GetValueOrDefault(R6Parts[name].Color));
                GeometryModel3D? model = await BuildPartAsync(part, frame, unit, color, true, name == HeadPart, name == HeadPart ? null : atlas, name, frame, pieces, content, fetchAsset, bounds, token);
                if (model != null)
                    models.Add(model);
                else
                    log("The " + name + " part has no mesh");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or NotSupportedException or ArgumentException)
            {
                log("The " + name + " part could not be built: " + ex.Message);
            }
        }
        if (models.Count < R6MinimumParts || !bounds.Valid)
            return null;
        await AddAccessoriesAsync(state, parts, world, unit, 1.0, new Dictionary<(long, long), AvatarLayering.CagePoint>(), models, pieces, effects, bounds, content, fetchAsset, log, token);
        Model3DGroup group = new();
        foreach (GeometryModel3D model in models)
            group.Children.Add(model);
        group.Freeze();
        AnimationData? animation = await LoadAnimationAsync(state, fetchAsset, log, R6IdleAnimation, false, token);
        return new AvatarScene(group, bounds.ToRect(), 180.0) { Pieces = pieces, Rig = new AvatarRig(R6Root, joints, animation), Effects = effects };
    }

    private static RbxmInstance? ClassicHead(List<RbxmInstance> all) =>
        all.FirstOrDefault(i => i.ClassName == "SpecialMesh" && i.Parent == null && FirstValue(i, MeshKeys) != null);

    private static RbxmInstance MergeHead(RbxmInstance original, RbxmInstance special)
    {
        RbxmInstance head = new() { ClassName = "Part", Name = HeadPart };
        RbxmInstance mesh = new() { ClassName = "SpecialMesh", Name = "Mesh", Parent = head };
        foreach ((string key, string value) in special.Strings)
            mesh.Strings[key] = value;
        foreach ((string key, Vector3D value) in special.Vectors)
            mesh.Vectors[key] = value;
        head.Children.Add(mesh);
        HashSet<string> moved = new(StringComparer.Ordinal);
        foreach (RbxmInstance child in special.Children)
        {
            if (child.ClassName != "Vector3Value" || !child.Vectors.TryGetValue("Value", out Vector3D position))
                continue;
            RbxmInstance attachment = new() { ClassName = "Attachment", Name = child.Name, Parent = head };
            attachment.CFrames["CFrame"] = RobloxCFrame.FromPosition(position.X, position.Y, position.Z);
            head.Children.Add(attachment);
            moved.Add(child.Name);
        }
        foreach (RbxmInstance child in original.Children)
            if (child.ClassName is "Attachment" or "WrapTarget" && !moved.Contains(child.Name))
                head.Children.Add(child);
        return head;
    }

    private static async Task<BitmapSource?> PaintClassicAsync(
        AvatarState state,
        string content,
        Func<long, CancellationToken, Task<byte[]?>> fetchAsset,
        Action<string> log,
        CancellationToken token
    )
    {
        Dictionary<string, AvatarClothing.Picture?> layers = await LoadClothingAsync(state, content, fetchAsset, log, token);
        Dictionary<string, Color> colors = new(StringComparer.Ordinal);
        foreach ((_, (string color, _, string limb)) in R6Parts)
            if (limb.Length > 0)
                colors[limb] = ParseColor(state.BodyColors.GetValueOrDefault(color));
        return AvatarClothing.PaintClassic(colors, layers.GetValueOrDefault("Pants"), layers.GetValueOrDefault("Shirt"), layers.GetValueOrDefault("TShirt"), content);
    }
}
