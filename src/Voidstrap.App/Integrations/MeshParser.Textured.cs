using System.Windows;
using System.Windows.Media.Media3D;
using Openize.Drako;

namespace Voidstrap.Integrations;

public static partial class MeshParser
{
    private const int ModernVertexSize = 40;

    private const int SkinningSize = 8;

    public static TexturedMesh ParseTextured(byte[] data)
    {
        if (data == null || data.Length < 13 || data.Length > MaxInputBytes)
            throw new InvalidDataException("File is too small to be a mesh");
        string versionLine = ReadVersionLine(data, out int lineLength);
        Match match = MeshVersionPattern.Match(versionLine);
        if (!match.Success)
            throw new InvalidDataException("Not a Roblox mesh file");
        int major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int draco = IndexOf(data, DracoMagic, lineLength, 256);
        TexturedMesh mesh =
            draco >= 0 ? ParseTexturedDraco(data, draco)
            : major == 1 ? ParseTexturedText(data, lineLength, versionLine.Contains("1.00", StringComparison.Ordinal))
            : ParseTexturedBinary(data, lineLength, major);
        if (mesh.Positions.Count == 0 || mesh.Indices.Count < 3)
            throw new InvalidDataException("Mesh produced no geometry");
        return mesh;
    }

    private static TexturedMesh ParseTexturedText(byte[] data, int lineLength, bool scaleHalf)
    {
        string input = Encoding.ASCII.GetString(data, lineLength, data.Length - lineLength);
        MatchCollection groups = BracketGroupPattern.Matches(input);
        if (groups.Count > MaxCount * 9)
            throw new InvalidDataException("Mesh contains too much geometry");
        TexturedMesh mesh = new();
        double scale = scaleHalf ? 0.5 : 1.0;
        for (int i = 0; i + 2 < groups.Count; i += 3)
        {
            double[] position = Triple(groups[i].Groups[1].Value);
            double[] normal = Triple(groups[i + 1].Groups[1].Value);
            double[] coord = Triple(groups[i + 2].Groups[1].Value);
            mesh.Positions.Add(new Point3D(position[0] * scale, position[1] * scale, position[2] * scale));
            mesh.Normals.Add(new Vector3D(normal[0], normal[1], normal[2]));
            mesh.Coords.Add(new Point(coord[0], 1.0 - coord[1]));
            mesh.Indices.Add(mesh.Positions.Count - 1);
        }
        return mesh;
    }

    private static double[] Triple(string text)
    {
        string[] parts = text.Split(',');
        double[] values = new double[3];
        for (int i = 0; i < 3 && i < parts.Length; i++)
            values[i] = ParseD(parts[i]);
        return values;
    }

    private static TexturedMesh ParseTexturedBinary(byte[] data, int headerStart, int major)
    {
        if (headerStart + 16 > data.Length)
            throw new InvalidDataException("Mesh header is truncated");
        int headerSize = BitConverter.ToUInt16(data, headerStart);
        int vertexSize;
        int vertexCount;
        int faceCount;
        int lodCount = 0;
        int boneCount = 0;
        if (major == 2)
        {
            vertexSize = data[headerStart + 2];
            vertexCount = (int)BitConverter.ToUInt32(data, headerStart + 4);
            faceCount = (int)BitConverter.ToUInt32(data, headerStart + 8);
        }
        else if (major == 3)
        {
            vertexSize = data[headerStart + 2];
            lodCount = BitConverter.ToUInt16(data, headerStart + 6);
            vertexCount = (int)BitConverter.ToUInt32(data, headerStart + 8);
            faceCount = (int)BitConverter.ToUInt32(data, headerStart + 12);
        }
        else
        {
            vertexSize = ModernVertexSize;
            vertexCount = (int)BitConverter.ToUInt32(data, headerStart + 4);
            faceCount = (int)BitConverter.ToUInt32(data, headerStart + 8);
            lodCount = BitConverter.ToUInt16(data, headerStart + 12);
            boneCount = BitConverter.ToUInt16(data, headerStart + 14);
        }
        if (headerSize < 12 || headerSize > 256 || vertexSize < 32 || vertexCount <= 0 || vertexCount > MaxCount || faceCount <= 0 || faceCount > MaxCount)
            throw new InvalidDataException("Mesh header values look invalid");
        long vertexStart = headerStart + headerSize;
        long faceStart = vertexStart + (long)vertexCount * vertexSize + (boneCount > 0 ? (long)vertexCount * SkinningSize : 0);
        long lodStart = faceStart + (long)faceCount * 12;
        if (lodStart > data.Length)
            throw new InvalidDataException("Mesh data is truncated");
        int usedFaces = faceCount;
        if (lodCount >= 2 && lodStart + 8 <= data.Length)
        {
            uint first = BitConverter.ToUInt32(data, (int)lodStart);
            uint second = BitConverter.ToUInt32(data, (int)lodStart + 4);
            if (first == 0 && second > 0 && second <= faceCount)
                usedFaces = (int)second;
        }
        TexturedMesh mesh = new();
        for (int i = 0; i < vertexCount; i++)
        {
            int offset = (int)vertexStart + i * vertexSize;
            mesh.Positions.Add(new Point3D(Finite(data, offset), Finite(data, offset + 4), Finite(data, offset + 8)));
            mesh.Normals.Add(new Vector3D(Finite(data, offset + 12), Finite(data, offset + 16), Finite(data, offset + 20)));
            mesh.Coords.Add(new Point(Finite(data, offset + 24), Finite(data, offset + 28)));
        }
        for (int i = 0; i < usedFaces; i++)
        {
            int offset = (int)faceStart + i * 12;
            uint a = BitConverter.ToUInt32(data, offset);
            uint b = BitConverter.ToUInt32(data, offset + 4);
            uint c = BitConverter.ToUInt32(data, offset + 8);
            if (a < vertexCount && b < vertexCount && c < vertexCount)
            {
                mesh.Indices.Add((int)a);
                mesh.Indices.Add((int)b);
                mesh.Indices.Add((int)c);
            }
        }
        return mesh;
    }

    private static double Finite(byte[] data, int offset)
    {
        float value = BitConverter.ToSingle(data, offset);
        return float.IsFinite(value) ? value : 0.0;
    }

    private static TexturedMesh ParseTexturedDraco(byte[] data, int dracoPosition)
    {
        byte[] payload = dracoPosition == 0 ? data : data[dracoPosition..];
        if (Draco.Decode(payload) is not DracoMesh draco)
            throw new InvalidDataException("Draco data did not contain a mesh");
        PointAttribute? positions = draco.GetNamedAttribute(AttributeType.Position);
        if (positions == null)
            throw new InvalidDataException("Draco mesh has no position data");
        PointAttribute? normals = draco.GetNamedAttribute(AttributeType.Normal);
        PointAttribute? coords = draco.GetNamedAttribute(AttributeType.TexCoord);
        int pointCount = draco.NumPoints;
        int faceCount = draco.NumFaces;
        if (pointCount <= 0 || pointCount > MaxCount || faceCount <= 0 || faceCount > MaxCount)
            throw new InvalidDataException("Draco mesh is too large");
        TexturedMesh mesh = new();
        float[] three = new float[3];
        float[] two = new float[2];
        for (int i = 0; i < pointCount; i++)
        {
            positions.GetValue(positions.MappedIndex(i), three);
            mesh.Positions.Add(new Point3D(three[0], three[1], three[2]));
            if (normals != null)
            {
                normals.GetValue(normals.MappedIndex(i), three);
                mesh.Normals.Add(new Vector3D(three[0], three[1], three[2]));
            }
            else
            {
                mesh.Normals.Add(new Vector3D(0, 1, 0));
            }
            if (coords != null)
            {
                coords.GetValue(coords.MappedIndex(i), two);
                mesh.Coords.Add(new Point(two[0], two[1]));
            }
            else
            {
                mesh.Coords.Add(new Point(0, 0));
            }
        }
        int[] face = new int[3];
        for (int i = 0; i < faceCount; i++)
        {
            draco.ReadFace(i, face);
            if ((uint)face[0] < (uint)pointCount && (uint)face[1] < (uint)pointCount && (uint)face[2] < (uint)pointCount)
            {
                mesh.Indices.Add(face[0]);
                mesh.Indices.Add(face[1]);
                mesh.Indices.Add(face[2]);
            }
        }
        return mesh;
    }
}
