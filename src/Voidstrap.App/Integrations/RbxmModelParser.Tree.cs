using System.Windows.Media.Media3D;
using Voidstrap.Integrations.Animation;

namespace Voidstrap.Integrations;

public static partial class RbxmModelParser
{
    private const byte StringType = 1;

    private const byte Int32Type = 3;

    private const byte FloatType = 4;

    private const byte Vector3Type = 14;

    private const byte CFrameType = 16;

    private const byte ContentType = 34;

    private const byte Color3Type = 12;

    private const byte Color3ByteType = 26;

    public static List<RbxmInstance> ReadTree(byte[] data)
    {
        List<RbxmInstance> roots = new();
        if (data == null || data.Length < 32)
            return roots;
        if (IsXmlModel(data))
            return ReadXmlTree(data);
        for (int i = 0; i < Magic.Length; i++)
            if (data[i] != Magic[i])
                return roots;
        Dictionary<int, int[]> classRefs = new();
        Dictionary<int, RbxmInstance> instances = new();
        Dictionary<int, int> parents = new();
        int totalInstances = 0;
        int pos = 32;
        while (pos + 16 <= data.Length)
        {
            string chunk = Encoding.ASCII.GetString(data, pos, 4);
            pos += 4;
            uint compressed = ReadU32(data, ref pos);
            uint uncompressed = ReadU32(data, ref pos);
            pos += 4;
            uint stored = compressed != 0 ? compressed : uncompressed;
            if (stored > int.MaxValue || stored > data.Length - pos)
                break;
            byte[] body = DecodeChunk(data, pos, compressed, uncompressed);
            pos += (int)stored;
            if (chunk == "END\0")
                break;
            if (body.Length == 0)
                continue;
            try
            {
                switch (chunk)
                {
                    case "INST":
                        ReadTreeClass(body, classRefs, instances, ref totalInstances);
                        break;
                    case "PROP":
                        ReadTreeProperty(body, classRefs, instances);
                        break;
                    case "PRNT":
                        ReadParents(body, parents);
                        break;
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
            {
                continue;
            }
        }
        foreach ((int referent, RbxmInstance instance) in instances)
        {
            if (parents.TryGetValue(referent, out int owner) && instances.TryGetValue(owner, out RbxmInstance? parent) && !ReferenceEquals(parent, instance))
            {
                instance.Parent = parent;
                parent.Children.Add(instance);
            }
            else
            {
                roots.Add(instance);
            }
        }
        return roots;
    }

    private static void ReadTreeClass(byte[] p, Dictionary<int, int[]> classRefs, Dictionary<int, RbxmInstance> instances, ref int totalInstances)
    {
        int pos = 0;
        int classId = (int)ReadU32(p, ref pos);
        string className = ReadString(p, ref pos);
        if (pos >= p.Length)
            return;
        pos++;
        int count = (int)ReadU32(p, ref pos);
        if (count < 0 || (long)totalInstances + count > MaxTotalInstances || (long)count * 4 > p.Length - pos)
            return;
        int[] referents = ReadReferentArray(p, ref pos, count);
        classRefs[classId] = referents;
        totalInstances += count;
        foreach (int referent in referents)
            instances[referent] = new RbxmInstance { ClassName = className };
    }

    private static void ReadTreeProperty(byte[] p, Dictionary<int, int[]> classRefs, Dictionary<int, RbxmInstance> instances)
    {
        int pos = 0;
        int classId = (int)ReadU32(p, ref pos);
        string name = ReadString(p, ref pos);
        if (pos >= p.Length || !classRefs.TryGetValue(classId, out int[]? referents))
            return;
        byte type = p[pos++];
        int count = referents.Length;
        if (count == 0)
            return;
        switch (type)
        {
            case StringType:
                for (int i = 0; i < count && pos < p.Length; i++)
                {
                    string value = ReadString(p, ref pos);
                    if (!instances.TryGetValue(referents[i], out RbxmInstance? instance))
                        continue;
                    if (name == "Name")
                        instance.Name = value;
                    else
                        instance.Strings[name] = value;
                }
                break;
            case ContentType:
                if ((long)count * 4 > p.Length - pos)
                    return;
                uint[] sources = ReadInterleavedU32(p, ref pos, count);
                int uriCount = (int)ReadU32(p, ref pos);
                int next = 0;
                for (int i = 0; i < uriCount && pos < p.Length; i++)
                {
                    string uri = ReadString(p, ref pos);
                    while (next < count && sources[next] == 0)
                        next++;
                    if (next < count && instances.TryGetValue(referents[next], out RbxmInstance? instance))
                        instance.Strings[name] = uri;
                    next++;
                }
                break;
            case Int32Type:
                if ((long)count * 4 > p.Length - pos)
                    return;
                uint[] integers = ReadInterleavedU32(p, ref pos, count);
                for (int i = 0; i < count; i++)
                    if (instances.TryGetValue(referents[i], out RbxmInstance? instance))
                        instance.Numbers[name] = (int)((integers[i] >> 1) ^ (0 - (integers[i] & 1)));
                break;
            case FloatType:
                if ((long)count * 4 > p.Length - pos)
                    return;
                float[] numbers = ReadInterleavedFloats(p, ref pos, count);
                for (int i = 0; i < count; i++)
                    if (instances.TryGetValue(referents[i], out RbxmInstance? instance))
                        instance.Numbers[name] = numbers[i];
                break;
            case Vector3Type:
                if ((long)count * 12 > p.Length - pos)
                    return;
                float[] xs = ReadInterleavedFloats(p, ref pos, count);
                float[] ys = ReadInterleavedFloats(p, ref pos, count);
                float[] zs = ReadInterleavedFloats(p, ref pos, count);
                for (int i = 0; i < count; i++)
                    if (instances.TryGetValue(referents[i], out RbxmInstance? instance))
                        instance.Vectors[name] = new Vector3D(xs[i], ys[i], zs[i]);
                break;
            case Color3Type:
                if ((long)count * 12 > p.Length - pos)
                    return;
                float[] reds = ReadInterleavedFloats(p, ref pos, count);
                float[] greens = ReadInterleavedFloats(p, ref pos, count);
                float[] blues = ReadInterleavedFloats(p, ref pos, count);
                for (int i = 0; i < count; i++)
                    if (instances.TryGetValue(referents[i], out RbxmInstance? instance))
                        instance.Vectors[name] = new Vector3D(reds[i], greens[i], blues[i]);
                break;
            case Color3ByteType:
                if ((long)count * 3 > p.Length - pos)
                    return;
                for (int i = 0; i < count; i++)
                    if (instances.TryGetValue(referents[i], out RbxmInstance? instance))
                        instance.Vectors[name] = new Vector3D(p[pos + i] / 255.0, p[pos + count + i] / 255.0, p[pos + count * 2 + i] / 255.0);
                break;
            case CFrameType:
                RobloxCFrame[] frames = ReadCFrames(p, ref pos, count);
                for (int i = 0; i < count; i++)
                    if (instances.TryGetValue(referents[i], out RbxmInstance? instance))
                        instance.CFrames[name] = frames[i];
                break;
        }
    }
}
