using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

internal class LocalAssetHelper
{
	private const int MaxMetadataEntries = 256;
	private const int MaxRegisteredAssets = 250000;
	private const long MaxMetadataBytes = 4L * 1024 * 1024;

	private enum LocalAssetDirectory : byte
	{
		Accessory,
		Core,
		Game,
		Linked,
		Music,
		Sound,
		Tool
	}

	private struct LocalAssetInfo
	{
		public string FileName;

		public LocalAssetDirectory Directory;
	}

	public struct AssetResult
	{
		public bool Success;

		public string FilePath;

		public bool Compressed;

		public bool IsLua;
	}

	private readonly Dictionary<ulong, LocalAssetInfo> _commonMap = new Dictionary<ulong, LocalAssetInfo>();

	private readonly Dictionary<ulong, string> _registeredCEMap = new Dictionary<ulong, string>();
	private int _inspectedFileCount;

	public static LocalAssetHelper Instance { get; }

	private LocalAssetHelper()
	{
		ParseCommonDirectories();
		ParseClientExclusiveAssets();
		PrintStatistics();
	}

	public AssetResult GetAssetData(ulong id)
	{
		AssetResult cEAsset = GetCEAsset(id);
		if (cEAsset.Success)
		{
			return cEAsset;
		}
		return GetCommonAsset(id);
	}

	public AssetResult GetCommonAsset(ulong id)
	{
		if (_commonMap.TryGetValue(id, out LocalAssetInfo localAssetInfo))
		{
			string commonDirectoryPath = GetCommonDirectoryPath(localAssetInfo.Directory);
			string path = Path.Combine(commonDirectoryPath, localAssetInfo.FileName);
			if (File.Exists(path))
			{
				return new AssetResult
				{
					Success = true,
					FilePath = path,
					Compressed = localAssetInfo.FileName.EndsWith(".gz", System.StringComparison.OrdinalIgnoreCase),
					IsLua = localAssetInfo.FileName.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase)
				};
			}
		}
		return new AssetResult
		{
			Success = false
		};
	}

	public AssetResult GetCEAsset(ulong id)
	{
		if (_registeredCEMap.TryGetValue(id, out string? text))
		{
			if (File.Exists(text))
			{
				return new AssetResult
				{
					Success = true,
					FilePath = text,
					Compressed = text.EndsWith(".gz", System.StringComparison.OrdinalIgnoreCase),
					IsLua = text.EndsWith(".lua", System.StringComparison.OrdinalIgnoreCase)
				};
			}
		}
		return new AssetResult
		{
			Success = false
		};
	}

	private void PrintStatistics()
	{
		int value = _commonMap.Count + _registeredCEMap.Count;
		Logger.Instance.Info($"LocalAssetHelper: Loaded {value} assets");
	}

	private string GetCommonDirectoryName(LocalAssetDirectory dir)
	{
		return dir.ToString().ToLowerInvariant();
	}

	private string GetCommonDirectoryPath(LocalAssetDirectory dir)
	{
		string commonDirectoryName = GetCommonDirectoryName(dir);
		return Path.Combine(PathHelper.Assets, commonDirectoryName);
	}

	private void ParseCommonDirectories()
	{
		ParseCommonDirectory(LocalAssetDirectory.Accessory);
		ParseCommonDirectory(LocalAssetDirectory.Core);
		ParseCommonDirectory(LocalAssetDirectory.Game);
		ParseCommonDirectory(LocalAssetDirectory.Linked);
		ParseCommonDirectory(LocalAssetDirectory.Music);
		ParseCommonDirectory(LocalAssetDirectory.Sound);
		ParseCommonDirectory(LocalAssetDirectory.Tool);
	}

	private void ParseCommonDirectory(LocalAssetDirectory dir)
	{
		string commonDirectoryPath = GetCommonDirectoryPath(dir);
		if (!Directory.Exists(commonDirectoryPath))
		{
			Logger.Instance.Warn("LocalAssetHelper: common dir " + commonDirectoryPath + " not found");
			return;
		}
		foreach (string text in Directory.EnumerateFiles(commonDirectoryPath, "*", SearchOption.TopDirectoryOnly))
		{
			if (_commonMap.Count + _registeredCEMap.Count >= MaxRegisteredAssets || _inspectedFileCount >= MaxRegisteredAssets)
			{
				break;
			}
			_inspectedFileCount++;
			if (!IsRegularFile(text))
			{
				continue;
			}
			string text2;
			if (!text.EndsWith(".gz", System.StringComparison.OrdinalIgnoreCase))
			{
				text2 = text;
			}
			else
			{
				string text3 = text;
				text2 = text3.Substring(0, text3.Length - 3);
			}
			string path = text2;
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
			if (!ulong.TryParse(fileNameWithoutExtension, out var result))
			{
				Logger.Instance.Warn("LocalAssetHelper: file name " + text + " is not a valid id");
				continue;
			}
			_commonMap[result] = new LocalAssetInfo
			{
				FileName = Path.GetFileName(text),
				Directory = dir
			};
		}
	}

	private void AddFolderFilesToCEMap(string folder)
	{
		if (!Directory.Exists(folder))
		{
			Logger.Instance.Warn("LocalAssetHelper: folder does not exist: " + folder);
			return;
		}
		foreach (string text in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
		{
			if (_commonMap.Count + _registeredCEMap.Count >= MaxRegisteredAssets || _inspectedFileCount >= MaxRegisteredAssets)
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
				_registeredCEMap[result] = text;
			}
			else
			{
				Logger.Instance.Warn("LocalAssetHelper: file name is not a valid ID: " + text);
			}
		}
	}

	private void ParseClientExclusiveAssets()
	{
		string path = Path.Combine(PathHelper.Assets, "clients");
		string path2 = Path.Combine(path, "metadata.json");
		if (!File.Exists(path2))
		{
			Logger.Instance.Warn("Could not find CEAssets metadata");
			return;
		}
		Dictionary<string, string[]>? dictionary;
		try
		{
			dictionary = JsonSerializer.Deserialize<Dictionary<string, string[]>>(Config.ReadTextFile(path2, MaxMetadataBytes));
		}
		catch
		{
			Logger.Instance.Warn("Failed to parse CEAssets");
			return;
		}
		if (dictionary == null)
		{
			Logger.Instance.Warn("Failed to parse CEAssets");
			return;
		}
		string value = Config.Instance.Client.ClientYear.ToString().ToUpperInvariant();
		foreach (KeyValuePair<string, string[]> item in dictionary.Take(MaxMetadataEntries))
		{
			string key = item.Key;
			string[] value2 = item.Value;
			if (value2 != null && value2.Length <= 64 && value2.Contains(value) && TryResolveFolder(path, key, out string folder))
			{
				AddFolderFilesToCEMap(folder);
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

	static LocalAssetHelper()
	{
		Instance = new LocalAssetHelper();
	}
}
