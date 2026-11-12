using System.Buffers.Binary;

namespace Voidstrap.UI;

internal static partial class WindowsColorEmoji
{
    private const int MaxNestedLookups = 8;

    private static byte[]? _gsub;
    private static int _lookupList;
    private static ushort[] _ccmpLookups = [];
    private static readonly Dictionary<string, ushort[]?> ShapeCache = new();

    private static void ParseGsub(byte[] table)
    {
        int featureList = U16(table, 6);
        int lookupList = U16(table, 8);
        int featureCount = U16(table, featureList);
        List<ushort> lookups = new();
        for (int i = 0; i < featureCount; i++)
        {
            int record = featureList + 2 + i * 6;
            if (table[record] != (byte)'c' || table[record + 1] != (byte)'c' || table[record + 2] != (byte)'m' || table[record + 3] != (byte)'p')
                continue;
            int feature = featureList + U16(table, record + 4);
            int count = U16(table, feature + 2);
            for (int k = 0; k < count; k++)
            {
                ushort index = U16(table, feature + 4 + k * 2);
                if (!lookups.Contains(index))
                    lookups.Add(index);
            }
        }
        lookups.Sort();
        _gsub = table;
        _lookupList = lookupList;
        _ccmpLookups = lookups.ToArray();
    }

    private static ushort[]? Shape(List<int> codePoints)
    {
        if (_typeface == null || _baseGlyphs == null)
            return null;
        string key = string.Join(',', codePoints);
        lock (Gate)
        {
            if (ShapeCache.TryGetValue(key, out ushort[]? cached))
                return cached;
        }
        ushort[]? result = null;
        try
        {
            List<ushort> glyphs = new(codePoints.Count);
            bool mapped = true;
            foreach (int codePoint in codePoints)
            {
                if (!_typeface.CharacterToGlyphMap.TryGetValue(codePoint, out ushort glyph))
                {
                    mapped = false;
                    break;
                }
                glyphs.Add(glyph);
            }
            if (mapped)
            {
                if (_gsub != null)
                {
                    foreach (ushort lookup in _ccmpLookups)
                    {
                        for (int i = 0; i < glyphs.Count; i++)
                            ApplyLookup(_gsub, lookup, glyphs, i, 0);
                    }
                }
                if (glyphs.Count > 0 && glyphs.TrueForAll(_baseGlyphs.ContainsKey))
                    result = glyphs.ToArray();
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            result = null;
        }
        lock (Gate)
            ShapeCache[key] = result;
        return result;
    }

    private static bool ApplyLookup(byte[] table, int lookupIndex, List<ushort> glyphs, int position, int depth)
    {
        if (depth > MaxNestedLookups || position >= glyphs.Count)
            return false;
        int lookup = _lookupList + U16(table, _lookupList + 2 + lookupIndex * 2);
        int type = U16(table, lookup);
        int subtableCount = U16(table, lookup + 4);
        for (int s = 0; s < subtableCount; s++)
        {
            int subtable = lookup + U16(table, lookup + 6 + s * 2);
            int subtableType = type;
            if (type == 7)
            {
                subtableType = U16(table, subtable + 2);
                subtable += (int)BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(subtable + 4));
            }
            bool applied = subtableType switch
            {
                1 => ApplySingle(table, subtable, glyphs, position),
                4 => ApplyLigature(table, subtable, glyphs, position),
                6 => ApplyChain(table, subtable, glyphs, position, depth),
                _ => false
            };
            if (applied)
                return true;
        }
        return false;
    }

    private static bool ApplySingle(byte[] table, int subtable, List<ushort> glyphs, int position)
    {
        int index = CoverageIndex(table, subtable + U16(table, subtable + 2), glyphs[position]);
        if (index < 0)
            return false;
        glyphs[position] = U16(table, subtable) == 1
            ? (ushort)(glyphs[position] + U16(table, subtable + 4))
            : U16(table, subtable + 6 + index * 2);
        return true;
    }

    private static bool ApplyLigature(byte[] table, int subtable, List<ushort> glyphs, int position)
    {
        int index = CoverageIndex(table, subtable + U16(table, subtable + 2), glyphs[position]);
        if (index < 0)
            return false;
        int set = subtable + U16(table, subtable + 6 + index * 2);
        int count = U16(table, set);
        for (int i = 0; i < count; i++)
        {
            int ligature = set + U16(table, set + 2 + i * 2);
            int components = U16(table, ligature + 2);
            if (position + components > glyphs.Count)
                continue;
            bool match = true;
            for (int k = 1; k < components && match; k++)
                match = glyphs[position + k] == U16(table, ligature + 4 + (k - 1) * 2);
            if (!match)
                continue;
            glyphs.RemoveRange(position + 1, components - 1);
            glyphs[position] = U16(table, ligature);
            return true;
        }
        return false;
    }

    private static bool ApplyChain(byte[] table, int subtable, List<ushort> glyphs, int position, int depth)
    {
        if (U16(table, subtable) != 3)
            return false;
        int offset = subtable + 2;
        int backtrack = U16(table, offset);
        int backtrackStart = offset + 2;
        offset = backtrackStart + backtrack * 2;
        int input = U16(table, offset);
        int inputStart = offset + 2;
        offset = inputStart + input * 2;
        int lookahead = U16(table, offset);
        int lookaheadStart = offset + 2;
        offset = lookaheadStart + lookahead * 2;
        if (position < backtrack || position + input + lookahead > glyphs.Count)
            return false;
        for (int k = 0; k < backtrack; k++)
        {
            if (CoverageIndex(table, subtable + U16(table, backtrackStart + k * 2), glyphs[position - 1 - k]) < 0)
                return false;
        }
        for (int k = 0; k < input; k++)
        {
            if (CoverageIndex(table, subtable + U16(table, inputStart + k * 2), glyphs[position + k]) < 0)
                return false;
        }
        for (int k = 0; k < lookahead; k++)
        {
            if (CoverageIndex(table, subtable + U16(table, lookaheadStart + k * 2), glyphs[position + input + k]) < 0)
                return false;
        }
        int records = U16(table, offset);
        for (int r = 0; r < records; r++)
        {
            int sequenceIndex = U16(table, offset + 2 + r * 4);
            int nested = U16(table, offset + 4 + r * 4);
            ApplyLookup(table, nested, glyphs, position + sequenceIndex, depth + 1);
        }
        return true;
    }

    private static int CoverageIndex(byte[] table, int coverage, ushort glyph)
    {
        int format = U16(table, coverage);
        int count = U16(table, coverage + 2);
        if (format == 1)
        {
            int low = 0;
            int high = count - 1;
            while (low <= high)
            {
                int mid = (low + high) >> 1;
                ushort value = U16(table, coverage + 4 + mid * 2);
                if (value == glyph)
                    return mid;
                if (value < glyph)
                    low = mid + 1;
                else
                    high = mid - 1;
            }
            return -1;
        }
        if (format == 2)
        {
            for (int i = 0; i < count; i++)
            {
                int range = coverage + 4 + i * 6;
                ushort first = U16(table, range);
                ushort last = U16(table, range + 2);
                if (glyph >= first && glyph <= last)
                    return U16(table, range + 4) + glyph - first;
            }
        }
        return -1;
    }

    private static ushort U16(byte[] table, int offset) => BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(offset, 2));
}
