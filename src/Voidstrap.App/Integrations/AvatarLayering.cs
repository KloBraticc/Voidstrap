using System.Windows;
using System.Windows.Media.Media3D;
using Voidstrap.Integrations.Animation;

namespace Voidstrap.Integrations;

internal static class AvatarLayering
{
    private const int Neighbors = 6;

    private const int MinimumMatches = 24;

    private const double KeyScale = 10000.0;

    public sealed record CagePoint(Point3D Position, string Part);

    public sealed record Wrapped(Point3D[] Positions, string[] Parts);

    public static (long, long) Key(Point coord) => ((long)Math.Round(coord.X * KeyScale), (long)Math.Round(coord.Y * KeyScale));

    public static Wrapped? Wrap(
        TexturedMesh clothing,
        Vector3D clothingScale,
        TexturedMesh reference,
        RobloxCFrame referenceOrigin,
        IReadOnlyDictionary<(long, long), CagePoint> body,
        string fallbackPart
    )
    {
        int count = reference.Positions.Count;
        if (count == 0 || reference.Coords.Count != count || clothing.Positions.Count == 0)
            return null;
        Matrix3D origin = referenceOrigin.ToMatrix3D();
        Point3D[] sources = new Point3D[count];
        Vector3D[] shifts = new Vector3D[count];
        string?[] owners = new string?[count];
        Vector3D total = default;
        int matches = 0;
        for (int i = 0; i < count; i++)
        {
            sources[i] = origin.Transform(reference.Positions[i]);
            if (!body.TryGetValue(Key(reference.Coords[i]), out CagePoint? target))
                continue;
            shifts[i] = target.Position - sources[i];
            owners[i] = target.Part;
            total += shifts[i];
            matches++;
        }
        if (matches < MinimumMatches)
            return null;
        Vector3D mean = total / matches;
        for (int i = 0; i < count; i++)
            if (owners[i] == null)
                shifts[i] = mean;
        Point3D[] positions = new Point3D[clothing.Positions.Count];
        string[] parts = new string[positions.Length];
        int[] nearest = new int[Neighbors];
        double[] distances = new double[Neighbors];
        for (int v = 0; v < positions.Length; v++)
        {
            Point3D point = clothing.Positions[v];
            point = new Point3D(point.X * clothingScale.X, point.Y * clothingScale.Y, point.Z * clothingScale.Z);
            Array.Fill(distances, double.MaxValue);
            Array.Fill(nearest, -1);
            for (int i = 0; i < count; i++)
            {
                double dx = sources[i].X - point.X;
                double dy = sources[i].Y - point.Y;
                double dz = sources[i].Z - point.Z;
                double distance = dx * dx + dy * dy + dz * dz;
                if (distance >= distances[Neighbors - 1])
                    continue;
                int slot = Neighbors - 1;
                while (slot > 0 && distances[slot - 1] > distance)
                {
                    distances[slot] = distances[slot - 1];
                    nearest[slot] = nearest[slot - 1];
                    slot--;
                }
                distances[slot] = distance;
                nearest[slot] = i;
            }
            Vector3D shift = default;
            double weightTotal = 0;
            string? owner = null;
            for (int k = 0; k < Neighbors; k++)
            {
                int index = nearest[k];
                if (index < 0)
                    continue;
                double weight = 1.0 / (distances[k] + 1e-6);
                shift += shifts[index] * weight;
                weightTotal += weight;
                owner ??= owners[index];
            }
            positions[v] = weightTotal > 0 ? point + shift / weightTotal : point + mean;
            parts[v] = owner ?? fallbackPart;
        }
        return new Wrapped(positions, parts);
    }
}
