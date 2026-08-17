using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

internal static class AssetVersionIdHelper
{
	private const int MaxEntries = 1000000;
	private const long MaxMapBytes = 32L * 1024 * 1024;

	public static IReadOnlyDictionary<ulong, ulong> Map { get; }

	static AssetVersionIdHelper()
	{
		string text = Path.Combine(PathHelper.Data, "avid_map.json");
		if (!File.Exists(text))
		{
			throw new Exception("Could not find asset version id map: " + text);
		}
		string json = Config.ReadTextFile(text, MaxMapBytes);
		Dictionary<ulong, ulong> dictionary = JsonSerializer.Deserialize<Dictionary<ulong, ulong>>(json);
		if (dictionary == null)
		{
			throw new Exception("Deserialised map data is null");
		}
		if (dictionary.Count > MaxEntries)
		{
			throw new InvalidDataException("The asset version map contains too many entries");
		}
		Map = dictionary;
	}
}
