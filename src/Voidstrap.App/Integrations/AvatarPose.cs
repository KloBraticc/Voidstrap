using Voidstrap.Integrations.Animation;

namespace Voidstrap.Integrations;

internal static class AvatarPose
{
    public static Dictionary<string, RobloxCFrame> Evaluate(AvatarRig rig, double time)
    {
        Dictionary<string, RobloxCFrame>? poses = Sample(rig.Animation, time);
        Dictionary<string, RobloxCFrame> world = new(StringComparer.Ordinal)
        {
            [rig.Root] = poses != null && poses.TryGetValue(rig.Root, out RobloxCFrame rootPose) ? rootPose : RobloxCFrame.Identity,
        };
        foreach (AvatarJoint joint in rig.Joints)
        {
            if (!world.TryGetValue(joint.Parent, out RobloxCFrame parent))
                continue;
            RobloxCFrame pose = poses != null && poses.TryGetValue(joint.Child, out RobloxCFrame value) ? value : RobloxCFrame.Identity;
            world[joint.Child] = parent * joint.C0 * pose * joint.C1.Inverse();
        }
        return world;
    }

    private static Dictionary<string, RobloxCFrame>? Sample(AnimationData? animation, double time)
    {
        if (animation == null || animation.Keyframes.Count == 0)
            return null;
        List<AnimKeyframe> frames = animation.Keyframes;
        double length = animation.Length > 0 ? animation.Length : frames[^1].Time;
        if (frames.Count == 1 || length <= 0)
            return frames[0].Poses;
        double t = time % length;
        if (t <= frames[0].Time)
            return frames[0].Poses;
        for (int i = 0; i + 1 < frames.Count; i++)
        {
            AnimKeyframe from = frames[i];
            AnimKeyframe to = frames[i + 1];
            if (t > to.Time)
                continue;
            double span = to.Time - from.Time;
            double amount = span <= 1e-9 ? 0 : (t - from.Time) / span;
            Dictionary<string, RobloxCFrame> blended = new(StringComparer.Ordinal);
            foreach (string part in from.Poses.Keys.Union(to.Poses.Keys))
                blended[part] = RobloxCFrame.Lerp(
                    from.Poses.TryGetValue(part, out RobloxCFrame a) ? a : RobloxCFrame.Identity,
                    to.Poses.TryGetValue(part, out RobloxCFrame b) ? b : RobloxCFrame.Identity,
                    amount
                );
            return blended;
        }
        return frames[^1].Poses;
    }
}
