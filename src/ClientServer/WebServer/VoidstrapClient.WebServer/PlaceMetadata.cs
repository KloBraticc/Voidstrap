using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

public class PlaceMetadata
{
	public static PlaceMetadata Default { get; }

	[JsonPropertyName("creator")]
	public string Creator { get; set; } = "Unknown";

	[JsonPropertyName("badges")]
	public Dictionary<int, string> Badges { get; set; } = new Dictionary<int, string>();

	private static string? GetBadgeMetadataPath()
	{
		string selectedMap = Config.Instance.User.Launch.SelectedMap;
		if (string.IsNullOrEmpty(selectedMap))
		{
			return null;
		}
		string root = Path.GetFullPath(Utils.GetMapsDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string text = Path.GetFullPath(Path.Combine(root, selectedMap));
		if (!text.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		if (text.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
		{
			string text2 = text;
			text = text2.Substring(0, text2.Length - 3);
		}
		return text + ".meta.json";
	}

	private static PlaceMetadata GetMetadata()
	{
		string? badgeMetadataPath;
		try
		{
			badgeMetadataPath = GetBadgeMetadataPath();
		}
		catch (Exception value)
		{
			Logger.Instance.Warn("Failed to get place metadata: " + value.Message);
			return new PlaceMetadata();
		}
		if (string.IsNullOrEmpty(badgeMetadataPath))
		{
			Logger.Instance.Warn("Failed to get place metadata: No selected map found.");
			return new PlaceMetadata();
		}
		if (!File.Exists(badgeMetadataPath))
		{
			return new PlaceMetadata();
		}
		try
		{
			PlaceMetadata placeMetadata = JsonSerializer.Deserialize<PlaceMetadata>(Config.ReadTextFile(badgeMetadataPath, 1048576));
			if (placeMetadata != null)
			{
				if (placeMetadata.Creator.Length > 128 || placeMetadata.Badges.Count > 1024 || placeMetadata.Badges.Values.Any(value => value == null || value.Length > 256))
				{
					throw new InvalidDataException("Place metadata exceeds its limits");
				}
				Logger.Instance.Info($"Got place metadata! {placeMetadata.Badges.Count} badges.");
				return placeMetadata;
			}
		}
		catch (Exception value)
		{
			Logger.Instance.Warn($"Failed to get place metadata: Exception while parsing: {value}");
			return new PlaceMetadata();
		}
		Logger.Instance.Warn("Failed to get place metadata: parsed metadata is null");
		return new PlaceMetadata();
	}

	static PlaceMetadata()
	{
		Default = GetMetadata();
	}
}
