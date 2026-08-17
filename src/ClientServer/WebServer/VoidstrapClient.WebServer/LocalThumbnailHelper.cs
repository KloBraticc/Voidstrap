using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

internal class LocalThumbnailHelper
{
	private const int MaxMetadataEntries = 256;
	private const int MaxRegisteredThumbnails = 100000;
	private const long MaxMetadataBytes = 4L * 1024 * 1024;
	private const long MaxThumbnailBytes = 10L * 1024 * 1024;

	private readonly Dictionary<ulong, string> _registeredMap = new Dictionary<ulong, string>();
	private int _inspectedFileCount;

	public static LocalThumbnailHelper Instance { get; }

	private LocalThumbnailHelper()
	{
		ParseClientExclusiveThumbnails();
	}

	public byte[]? GetThumbnailData(ulong id)
	{
		if (_registeredMap.TryGetValue(id, out string? path))
		{
			try
			{
				return Config.ReadBytesFile(path, MaxThumbnailBytes);
			}
			catch
			{
				return null;
			}
		}
		return null;
	}

	private void AddFolderFilesToMap(string folder)
	{
		if (!Directory.Exists(folder))
		{
			Logger.Instance.Warn("ThumbnailHandler: folder does not exist: " + folder);
			return;
		}
		foreach (string text in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
		{
			if (_registeredMap.Count >= MaxRegisteredThumbnails || _inspectedFileCount >= MaxRegisteredThumbnails)
			{
				break;
			}
			_inspectedFileCount++;
			if (!IsRegularFile(text))
			{
				continue;
			}
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text);
			if (ulong.TryParse(fileNameWithoutExtension, out var result))
			{
				_registeredMap[result] = text;
			}
			else
			{
				Logger.Instance.Warn("ThumbnailHandler: file name is not a valid ID: " + text);
			}
		}
	}

	private void ParseClientExclusiveThumbnails()
	{
		string path = Path.Combine(PathHelper.Thumbnails, "clients");
		string path2 = Path.Combine(path, "metadata.json");
		if (!File.Exists(path2))
		{
			Logger.Instance.Warn("Could not find CEThumbs metadata");
			return;
		}
		Dictionary<string, string[]>? dictionary;
		try
		{
			dictionary = JsonSerializer.Deserialize<Dictionary<string, string[]>>(Config.ReadTextFile(path2, MaxMetadataBytes));
		}
		catch
		{
			Logger.Instance.Warn("Failed to parse CEThumbs");
			return;
		}
		if (dictionary == null)
		{
			Logger.Instance.Warn("Failed to parse CEThumbs");
			return;
		}
		string value = Config.Instance.Client.ClientYear.ToString().ToUpperInvariant();
		foreach (KeyValuePair<string, string[]> item in dictionary.Take(MaxMetadataEntries))
		{
			string key = item.Key;
			string[] value2 = item.Value;
			if (value2 != null && value2.Length <= 64 && (value2.Contains("*") || value2.Contains(value)) && TryResolveFolder(path, key, out string folder))
			{
				AddFolderFilesToMap(folder);
			}
		}
	}

	private static bool TryResolveFolder(string root, string name, out string folder)
	{
		folder = "";
		if (name.Length > 128 || !PathHelper.IsFileNameValid(name))
		{
			return false;
		}
		try
		{
			string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
			string candidate = Path.GetFullPath(Path.Combine(root, name));
			if (!candidate.StartsWith(fullRoot, System.StringComparison.OrdinalIgnoreCase) || !Directory.Exists(candidate) || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
			{
				return false;
			}
			folder = candidate;
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsRegularFile(string path)
	{
		try
		{
			return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
		}
		catch
		{
			return false;
		}
	}

	static LocalThumbnailHelper()
	{
		Instance = new LocalThumbnailHelper();
	}
}
