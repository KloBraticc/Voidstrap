using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Resources;

namespace Voidstrap.Utility;

public sealed class CompressedResourceManager : ResourceManager
{
	private readonly Assembly _assembly;
	private readonly string _baseName;
	private readonly object _gate = new();
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

			ResourceSet? set = null;
			using (Stream? stream = _assembly.GetManifestResourceStream(_baseName + "." + cultureName + ".resources.z"))
			{
				if (stream != null)
				{
					MemoryStream buffer = new();
					using (DeflateStream deflate = new(stream, CompressionMode.Decompress))
						deflate.CopyTo(buffer);
					buffer.Position = 0;
					set = new ResourceSet(buffer);
				}
			}
			_sets[cultureName] = set;
			return set;
		}
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
