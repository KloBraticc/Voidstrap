using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoidstrapClient.Common;
using VoidstrapClient.WebServer.Models;

namespace VoidstrapClient.WebServer;

internal static class AvatarItems
{
	private const int MaxEntries = 100000;
	private const int MaxPackageItems = 100;

	internal class AssetDatabase
	{
		[JsonPropertyName("data")]
		public Dictionary<ulong, AvatarItem> Data { get; set; } = new Dictionary<ulong, AvatarItem>();
	}

	public static Dictionary<ulong, AvatarItem> Database { get; private set; }

	private static void ParseDatabase()
	{
		string path = Path.Combine(PathHelper.Data, "assets.json");
		if (!File.Exists(path))
		{
			throw new Exception("Could not find assets database");
		}
		AssetDatabase assetDatabase = JsonSerializer.Deserialize<AssetDatabase>(Config.ReadTextFile(path, 33554432));
		if (assetDatabase?.Data == null)
		{
			throw new Exception("Parsed asset database is null");
		}
		foreach (KeyValuePair<ulong, AvatarItem> datum in assetDatabase.Data)
		{
			if (Database.Count >= MaxEntries)
				throw new InvalidDataException("The assets database contains too many entries");
			if (datum.Value == null || datum.Value.Items == null || datum.Value.Items.Count > MaxPackageItems || datum.Value.AssetVersion < 0)
				throw new InvalidDataException("The assets database contains invalid entries");
			datum.Value.Id = datum.Key;
			Database[datum.Key] = datum.Value;
		}
	}

	public static AvatarItem? GetById(ulong id)
	{
		if (!Database.ContainsKey(id))
		{
			return null;
		}
		return Database[id];
	}

	public static bool TryGetById(ulong id, [NotNullWhen(true)] out AvatarItem item)
	{
		item = GetById(id);
		return item != null;
	}

	static AvatarItems()
	{
		Database = new Dictionary<ulong, AvatarItem>();
		ParseDatabase();
	}
}
