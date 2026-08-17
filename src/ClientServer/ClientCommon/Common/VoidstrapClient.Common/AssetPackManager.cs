using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VoidstrapClient.Common.Enums;
using VoidstrapClient.Common.Models;

namespace VoidstrapClient.Common;

public class AssetPackManager
{
	private const int MaxAssetPacks = 256;
	private const long MaxConfigurationBytes = 4L * 1024 * 1024;
	private const int MaxCompatibilityRules = 128;

	private ClientYear? _clientYear;

	private bool _disabledInit;

	public static AssetPackManager Instance { get; } = new AssetPackManager();

	public List<AssetPack> AssetPacks { get; set; } = new List<AssetPack>();

	public List<string> DisabledAssetPacks { get; set; } = new List<string>();

	private AssetPackManager()
	{
		ParseAssetPacks();
	}

	public void Reparse()
	{
		AssetPacks.Clear();
		ParseAssetPacks();
		ParseDisabledAssetPacks();
		CheckForDisabledClientYear();
	}

	public void SetDisabledList(List<string> disabled)
	{
		if (_disabledInit)
		{
			throw new Exception("Tried to run AssetPackManager.SetDisabledList twice");
		}
		_disabledInit = true;
		DisabledAssetPacks = disabled.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 256).Distinct(StringComparer.Ordinal).Take(MaxAssetPacks).ToList();
		ParseDisabledAssetPacks();
	}

	public void SetClientYear(ClientYear clientYear)
	{
		_clientYear = clientYear;
		CheckForDisabledClientYear();
	}

	public void ToggleAssetPack(AssetPack assetPack)
	{
		if (!assetPack.Disabled)
		{
			assetPack.Disabled = true;
			DisabledAssetPacks.Add(assetPack.DisplayName);
		}
		else
		{
			assetPack.Disabled = false;
			DisabledAssetPacks.Remove(assetPack.DisplayName);
		}
	}

	public List<string> GetEnabledAssetPackDirectories()
	{
		List<string> list = new List<string>();
		foreach (AssetPack assetPack in AssetPacks)
		{
			if (!assetPack.Disabled)
			{
				list.Add(assetPack.Folder);
			}
		}
		return list;
	}

	private static bool IsClientYearCompatible(ClientYear clientYear, List<string> rules)
	{
		if (!rules.Any())
		{
			return true;
		}
		bool hasPositiveRule = false;
		bool matchesPositiveRule = false;
		foreach (string rule in rules)
		{
			if (rule == "*")
			{
				hasPositiveRule = true;
				matchesPositiveRule = true;
			}
			else if (rule.StartsWith('!'))
			{
				ClientYear excludedYear = new ClientYear(rule.Substring(1));
				if (clientYear == excludedYear)
				{
					return false;
				}
			}
			else
			{
				hasPositiveRule = true;
				matchesPositiveRule |= clientYear == new ClientYear(rule);
			}
		}
		return !hasPositiveRule || matchesPositiveRule;
	}

	private void CheckForDisabledClientYear()
	{
		if ((object)_clientYear == null)
		{
			return;
		}
		foreach (AssetPack assetPack in AssetPacks)
		{
			if (!assetPack.Disabled && !IsClientYearCompatible(_clientYear, assetPack.Clients))
			{
				assetPack.Disabled = true;
			}
		}
	}

	private void ParseDisabledAssetPacks()
	{
		if (!DisabledAssetPacks.Any())
		{
			return;
		}
		List<string> list = new List<string>();
		foreach (AssetPack assetPack in AssetPacks)
		{
			if (DisabledAssetPacks.Contains(assetPack.DisplayName))
			{
				assetPack.Disabled = true;
				list.Add(assetPack.DisplayName);
			}
		}
		foreach (string item in DisabledAssetPacks.Except(list).ToList())
		{
			DisabledAssetPacks.Remove(item);
		}
	}

	private void ParseAssetPacks()
	{
		Directory.CreateDirectory(PathHelper.AssetPacks);
		foreach (string text in Directory.EnumerateDirectories(PathHelper.AssetPacks).Take(MaxAssetPacks))
		{
			try
			{
				if ((File.GetAttributes(text) & FileAttributes.ReparsePoint) != 0)
				{
					continue;
				}
			}
			catch
			{
				continue;
			}
			string path = Path.Combine(text, "AssetPack.json");
			string path2 = Path.Combine(text, "SodikmAssetPack.ini");
			AssetPack assetPack;
			if (File.Exists(path))
			{
				try
				{
					assetPack = JsonSerializer.Deserialize<AssetPack>(ReadConfiguration(path)) ?? throw new Exception("Deserialised asset pack JSON is null");
					assetPack.Api = AssetPackApi.V1;
					ValidateAssetPack(assetPack);
				}
				catch (Exception value)
				{
					Logger.Instance.Warn($"AssetPackManager: failed to parse V1 asset pack: {value}");
					continue;
				}
			}
			else if (File.Exists(path2))
			{
				try
				{
					assetPack = IniParser.Parse<AssetPackSodikm>(ReadConfiguration(path2)).Convert();
					assetPack.Api = AssetPackApi.SodikmV1;
					ValidateAssetPack(assetPack);
				}
				catch (Exception value2)
				{
					Logger.Instance.Warn($"AssetPackManager: failed to parse Sodikm V1 asset pack: {value2}");
					continue;
				}
			}
			else
			{
				assetPack = new AssetPack();
				assetPack.Api = AssetPackApi.None;
			}
			assetPack.Folder = text;
			assetPack.FolderName = Path.GetFileName(text) ?? "errfolder";
			AssetPacks.Add(assetPack);
		}
	}

	private static string ReadConfiguration(string path)
	{
		using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
		if (stream.Length <= 0 || stream.Length > MaxConfigurationBytes)
		{
			throw new InvalidDataException("The asset pack configuration size is invalid");
		}
		byte[] data = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
		stream.ReadExactly(data);
		if (stream.ReadByte() != -1)
		{
			throw new InvalidDataException("The asset pack configuration size changed while reading");
		}
		using MemoryStream memory = new MemoryStream(data, writable: false);
		using StreamReader reader = new StreamReader(memory, detectEncodingFromByteOrderMarks: true);
		return reader.ReadToEnd();
	}

	private static void ValidateAssetPack(AssetPack assetPack)
	{
		if (assetPack.Name?.Length > 256 || assetPack.Description?.Length > 4096 || assetPack.Author?.Length > 256 || assetPack.Version?.Length > 256)
		{
			throw new InvalidDataException("The asset pack metadata exceeds its limits");
		}
		assetPack.Description ??= "";
		assetPack.Author ??= "";
		assetPack.Version ??= "";
		assetPack.Clients = (assetPack.Clients ?? new List<string>())
			.Where(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 32)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(MaxCompatibilityRules)
			.ToList();
		if (assetPack.Clients.Count == 0)
		{
			assetPack.Clients.Add("*");
		}
	}
}
