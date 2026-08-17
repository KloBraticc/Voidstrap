using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using VoidstrapClient.Common.Collection;
using VoidstrapClient.Common.Models;

namespace VoidstrapClient.Common;

public class OutfitManager
{
	public struct Result
	{
		public bool Success;

		public string Message;

		internal Result(bool success)
		{
			Success = success;
			Message = null;
		}

		internal Result(bool success, string message)
		{
			Success = success;
			Message = message;
		}
	}

	private const int MinNameLength = 1;

	private const int MaxNameLength = 50;
	private const int MaxOutfits = 1024;
	private const long MaxOutfitBytes = 4L * 1024 * 1024;

	private Dictionary<string, string>? _preferredOutfitsList;

	public ObservableDictionary<string, Outfit> Outfits { get; } = new ObservableDictionary<string, Outfit>();

	public OutfitManager(Dictionary<string, string>? preferredOutfitsList = null)
	{
		_preferredOutfitsList = preferredOutfitsList;
		Load();
	}

	private Outfit? GetOutfitByPath(string path)
	{
		try
		{
			Character character = JsonSerializer.Deserialize<Character>(ReadOutfitText(path));
			if (character == null)
			{
				throw new Exception("Deserialised character JSON is NULL!");
			}
			character.Equipped ??= new Dictionary<Enums.AvatarSlot, ulong>();
			return new Outfit
			{
				Name = Path.GetFileNameWithoutExtension(path),
				Character = character
			};
		}
		catch (Exception value)
		{
			Logger.Instance.Error($"Failed to get outfit at path {path}: {value}");
			return null;
		}
	}

	private void Load()
	{
		MigrateOutfits();
		string[] outfitPaths = GetOutfitPaths();
		Dictionary<string, string> dictionary;
		if (_preferredOutfitsList != null)
		{
			HashSet<string> list = new HashSet<string>(StringComparer.Ordinal);
			List<string> list2 = new List<string>();
			foreach (KeyValuePair<string, string> preferredOutfits in _preferredOutfitsList)
			{
				if (list.Count >= MaxOutfits || string.IsNullOrWhiteSpace(preferredOutfits.Key) || preferredOutfits.Key.Length > 64 || !PathHelper.IsFileNameValid(preferredOutfits.Value) || !list.Add(preferredOutfits.Value))
				{
					list2.Add(preferredOutfits.Key);
				}
			}
			foreach (string item in list2)
			{
				_preferredOutfitsList.Remove(item);
			}
			dictionary = _preferredOutfitsList.ToDictionary<KeyValuePair<string, string>, string, string>((KeyValuePair<string, string> e) => e.Value, (KeyValuePair<string, string> e) => e.Key);
		}
		else
		{
			dictionary = new Dictionary<string, string>();
		}
		string[] array = outfitPaths;
		foreach (string path in array)
		{
			Outfit outfitByPath = GetOutfitByPath(path);
			if (outfitByPath != null)
			{
				if (dictionary.ContainsKey(outfitByPath.Name))
				{
					outfitByPath.Client = dictionary[outfitByPath.Name];
				}
				Outfits[outfitByPath.Name.ToLowerInvariant()] = outfitByPath;
			}
		}
		RemoveNonExistentOutfitsFromPreferredOutfits();
	}

	private static Dictionary<T, U> CopyDictionary<T, U>(Dictionary<T, U> dictionary) where T : notnull
	{
		return dictionary.ToDictionary<KeyValuePair<T, U>, T, U>((KeyValuePair<T, U> entry) => entry.Key, (KeyValuePair<T, U> entry) => entry.Value);
	}

	private void MigrateOutfits()
	{
		if (!Directory.Exists(PathHelper.UserOutfitsLegacy))
		{
			return;
		}
		Logger.Instance.Info("Found user outfits in old path. Migrating.");
		Directory.CreateDirectory(PathHelper.UserOutfits);
		int migratedCount = Directory.EnumerateFiles(PathHelper.UserOutfits, "*.json", SearchOption.TopDirectoryOnly).Take(MaxOutfits).Count();
		int inspectedCount = 0;
		foreach (string text in Directory.EnumerateFiles(PathHelper.UserOutfitsLegacy, "*", SearchOption.TopDirectoryOnly))
		{
			if (migratedCount >= MaxOutfits || inspectedCount >= MaxOutfits * 4)
			{
				break;
			}
			inspectedCount++;
			string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(text);
			if (!PathHelper.IsFileNameValid(fileNameWithoutExtension))
			{
				continue;
			}
			string outfitPath = GetOutfitPath(fileNameWithoutExtension);
			if (File.Exists(outfitPath))
			{
				Logger.Instance.Info("MigrateOutfits: Outfit with name " + fileNameWithoutExtension + " already exists.");
				continue;
			}
			try
			{
				FileInfo info = new FileInfo(text);
				if (info.Length <= 0 || info.Length > MaxOutfitBytes)
				{
					continue;
				}
				Logger.Instance.Info("MigrateOutfits: Migrating " + fileNameWithoutExtension + ".");
				File.Move(text, outfitPath);
				migratedCount++;
			}
			catch (Exception value)
			{
				Logger.Instance.Warn($"Failed to migrate outfit {fileNameWithoutExtension}: {value}");
			}
		}
		try
		{
			if (!Directory.EnumerateFileSystemEntries(PathHelper.UserOutfitsLegacy).Any())
			{
				Directory.Delete(PathHelper.UserOutfitsLegacy);
			}
		}
		catch (Exception value)
		{
			Logger.Instance.Info($"Failed to delete old user outfits path: {value}");
		}
		Logger.Instance.Info("Finished migration.");
	}

	private void RemoveNonExistentOutfitsFromPreferredOutfits()
	{
		if (_preferredOutfitsList == null)
		{
			return;
		}
		foreach (KeyValuePair<string, string> item in CopyDictionary(_preferredOutfitsList))
		{
			string key = item.Value.ToLowerInvariant();
			if (!Outfits.ContainsKey(key))
			{
				_preferredOutfitsList.Remove(item.Key);
			}
		}
	}

	private string GetOutfitPath(string outfitName)
	{
		Directory.CreateDirectory(PathHelper.UserOutfits);
		return Path.Combine(PathHelper.UserOutfits, outfitName + ".json");
	}

	private string[] GetOutfitPaths()
	{
		Directory.CreateDirectory(PathHelper.UserOutfits);
		return Directory.EnumerateFiles(PathHelper.UserOutfits, "*.json", SearchOption.TopDirectoryOnly).Take(MaxOutfits).ToArray();
	}

	public Outfit? GetOutfit(string outfitName)
	{
		string key = outfitName.ToLowerInvariant();
		if (Outfits.ContainsKey(key))
		{
			return Outfits[key];
		}
		return null;
	}

	public IEnumerable<Outfit> GetOutfits()
	{
		return Outfits.Select<KeyValuePair<string, Outfit>, Outfit>((KeyValuePair<string, Outfit> x) => x.Value);
	}

	private void SaveOutfit(string name, Character character)
	{
		if (!PathHelper.IsFileNameValid(name))
		{
			throw new ArgumentException("The outfit name is invalid", nameof(name));
		}
		string outfitPath = GetOutfitPath(name);
		string contents = JsonSerializer.Serialize(character);
		if (Encoding.UTF8.GetByteCount(contents) > MaxOutfitBytes)
		{
			throw new InvalidDataException("The outfit is too large");
		}
		string temporary = outfitPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
		try
		{
			File.WriteAllText(temporary, contents);
			File.Move(temporary, outfitPath, true);
		}
		finally
		{
			if (File.Exists(temporary))
			{
				File.Delete(temporary);
			}
		}
	}

	private static string ReadOutfitText(string path)
	{
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaxOutfitBytes)
		{
			throw new InvalidDataException("The outfit file size is invalid");
		}
		byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
		stream.ReadExactly(data);
		if (stream.ReadByte() != -1)
		{
			throw new InvalidDataException("The outfit file size changed while reading");
		}
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, Encoding.UTF8, true);
		return reader.ReadToEnd();
	}

	public void SaveOutfit(Outfit outfit, Character character)
	{
		SaveOutfit(outfit.Name, character);
		Outfits[outfit.Name.ToLowerInvariant()] = outfit;
	}

	public Result CreateOutfit(string name, Character character)
	{
		if (!PathHelper.IsFileNameValid(name))
		{
			return new Result(success: false, "Name is not valid.");
		}
		string key = name.ToLowerInvariant();
		if (Outfits.ContainsKey(key))
		{
			return new Result(success: false, "Name is already taken.");
		}
		if (name.Length < 1)
		{
			return new Result(success: false, "Name is too short.");
		}
		if (name.Length > 50)
		{
			return new Result(success: false, "Name is too long.");
		}
		if (Outfits.Count >= MaxOutfits)
		{
			return new Result(success: false, "Too many outfits are stored.");
		}
		try
		{
			SaveOutfit(name, character);
			Character character2 = (Character)character.Clone();
			Outfit outfit = new Outfit();
			outfit.Name = name;
			outfit.Character = character2;
			Outfits[key] = outfit;
		}
		catch (Exception value)
		{
			Logger.Instance.Error($"An exception occured while creating outfit {name}: {value}");
			return new Result(success: false, "An exception occured. View logs for more details.");
		}
		return new Result(success: true);
	}

	public Result DeleteOutfit(string name)
	{
		string key = name.ToLowerInvariant();
		if (!Outfits.ContainsKey(key))
		{
			return new Result(success: false, "Outfit not found.");
		}
		string outfitPath = GetOutfitPath(name);
		if (!File.Exists(outfitPath))
		{
			return new Result(success: false, "Outfit file does not exist.");
		}
		try
		{
			File.Delete(outfitPath);
		}
		catch (Exception value)
		{
			Logger.Instance.Error($"An exception occured while deleting outfit {name}: {value}");
			return new Result(success: false, "An exception occured. View logs for more details.");
		}
		if (_preferredOutfitsList != null)
		{
			IEnumerable<KeyValuePair<string, string>> source = _preferredOutfitsList.Where<KeyValuePair<string, string>>((KeyValuePair<string, string> x) => x.Value == name);
			if (source.Any())
			{
				_preferredOutfitsList.Remove(source.First().Key);
			}
		}
		Outfits.Remove(key);
		return new Result(success: true);
	}

	public Result DeleteOutfit(Outfit outfit)
	{
		return DeleteOutfit(outfit.Name);
	}

	public Result RenameOutfit(string name, string newName)
	{
		Outfit outfit = GetOutfit(name);
		if (outfit == null)
		{
			return new Result(success: false, "Outfit not found.");
		}
		return RenameOutfit(outfit, newName);
	}

	public Result RenameOutfit(Outfit outfit, string newName)
	{
		if (!PathHelper.IsFileNameValid(newName))
		{
			return new Result(success: false, "New name is not valid.");
		}
		string key = newName.ToLowerInvariant();
		if (Outfits.ContainsKey(key))
		{
			return new Result(success: false, "New name is already taken.");
		}
		string outfitPath = GetOutfitPath(outfit.Name);
		if (!File.Exists(outfitPath))
		{
			return new Result(success: false, "Outfit file not found.");
		}
		if (newName.Length < 1)
		{
			return new Result(success: false, "New name is too short.");
		}
		if (newName.Length > 50)
		{
			return new Result(success: false, "New name is too long.");
		}
		string outfitPath2 = GetOutfitPath(newName);
		if (File.Exists(outfitPath2))
		{
			return new Result(success: false, "Outfit file for new name already exists.");
		}
		try
		{
			File.Move(outfitPath, outfitPath2);
		}
		catch (Exception value)
		{
			Logger.Instance.Error($"An exception occured while renaming outfit {outfit.Name} to {newName}: {value}");
			return new Result(success: false, "An exception occured. View logs for more details.");
		}
		string oldName = outfit.Name;
		string key2 = oldName.ToLowerInvariant();
		Outfits.Remove(key2);
		if (_preferredOutfitsList != null)
		{
			foreach (string client in _preferredOutfitsList.Where(entry => string.Equals(entry.Value, oldName, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToArray())
			{
				_preferredOutfitsList[client] = newName;
			}
		}
		outfit.Name = newName;
		Outfits[key] = outfit;
		return new Result(success: true);
	}

	public void SetPreferredOutfitForClient(Outfit outfit, string? client)
	{
		if (_preferredOutfitsList == null)
		{
			throw new NullReferenceException("_preferredOutfitsList");
		}
		if (client == "None")
		{
			client = null;
		}
		outfit.Client = client;
		IEnumerable<KeyValuePair<string, string>> source = _preferredOutfitsList.Where<KeyValuePair<string, string>>((KeyValuePair<string, string> x) => x.Value == outfit.Name);
		if (source.Any())
		{
			_preferredOutfitsList.Remove(source.First().Key);
		}
		if (client != null)
		{
			_preferredOutfitsList[client] = outfit.Name;
		}
	}

	public Outfit? GetPreferredOutfitForClient(string client)
	{
		if (_preferredOutfitsList == null)
		{
			throw new NullReferenceException("_preferredOutfitsList");
		}
		if (_preferredOutfitsList.TryGetValue(client, out string? name) && Outfits.TryGetValue(name.ToLowerInvariant(), out Outfit? outfit))
		{
			return outfit;
		}
		return null;
	}

	public List<string> GetClientsWithPreferred()
	{
		if (_preferredOutfitsList == null)
		{
			throw new NullReferenceException("_preferredOutfitsList");
		}
		return _preferredOutfitsList.Select<KeyValuePair<string, string>, string>((KeyValuePair<string, string> x) => x.Key).ToList();
	}
}
