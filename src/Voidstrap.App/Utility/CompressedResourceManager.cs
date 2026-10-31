using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Resources;
using System.Text;

namespace Voidstrap.Utility;

public sealed class CompressedResourceManager : ResourceManager
{
    private readonly Assembly _assembly;
    private readonly string _baseName;
    private readonly object _gate = new();
    private const string MissingValue = "\u0001";
    private string[]? _keys;
    private readonly Dictionary<string, ResourceSet?> _sets = new(StringComparer.OrdinalIgnoreCase);

    public CompressedResourceManager(string baseName, Assembly assembly)
        : base(baseName, assembly)
    {
        _assembly = assembly;
        _baseName = baseName;
    }

    protected override ResourceSet? InternalGetResourceSet(CultureInfo culture, bool createIfNotExists, bool tryParents)
    {
        for (CultureInfo current = culture; !current.Equals(CultureInfo.InvariantCulture); current = current.Parent)
        {
            ResourceSet? set = Load(current.Name, createIfNotExists);
            if (set != null)
                return set;
            if (!tryParents)
                return null;
        }
        return base.InternalGetResourceSet(CultureInfo.InvariantCulture, createIfNotExists, tryParents);
    }

    private ResourceSet? Load(string cultureName, bool createIfNotExists)
    {
        lock (_gate)
        {
            if (_sets.TryGetValue(cultureName, out ResourceSet? cached))
                return cached;
            if (!createIfNotExists)
                return null;

            string? values = ReadText(_baseName + "." + cultureName + ".resources.br");
            ResourceSet? set = values == null ? null : BuildSet(_keys ??= (ReadText(_baseName + ".keys.br") ?? "").Split('\0'), values.Split('\0'));
            _sets[cultureName] = set;
            return set;
        }
    }

    private string? ReadText(string name)
    {
        using Stream? stream = _assembly.GetManifestResourceStream(name);
        if (stream == null)
            return null;
        using BrotliStream brotli = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(brotli, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private sealed class ValueReader(Dictionary<string, string> values) : IResourceReader
    {
        public IDictionaryEnumerator GetEnumerator() => values.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();

        public void Close()
        {
        }

        public void Dispose()
        {
        }
    }

    private static ResourceSet BuildSet(string[] keys, string[] values)
    {
        Dictionary<string, string> map = new(keys.Length, StringComparer.Ordinal);
        for (int i = 0; i < keys.Length && i < values.Length; i++)
        {
            if (values[i] != MissingValue)
                map[keys[i]] = values[i];
        }
        return new ResourceSet(new ValueReader(map));
    }

    public override void ReleaseAllResources()
    {
        lock (_gate)
        {
            foreach (ResourceSet? set in _sets.Values)
                set?.Dispose();
            _sets.Clear();
        }
        base.ReleaseAllResources();
    }
}
