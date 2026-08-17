using System;
using System.Collections.Generic;
using System.IO;

namespace VoidstrapClient.Common;

public static class PathHelper
{
	private static string? _base = null;

	private static string? _logs = null;

	private static string? _data = null;

	private static string? _clients = null;

	private static string? _maps = null;

	private static string? _common = null;

	private static string? _assets = null;

	private static string? _character = null;

	private static string? _thumbnailsDeprecated = null;

	private static string? _thumbnails = null;

	private static string? _sharedScripts = null;

	private static string? _autoSaves = null;

	private static string? _assetPacks = null;

	private static string? _clientAddons = null;

	private static string? _clientAddonsDXVK = null;

	private static string? _clientAddonsDgVoodoo = null;

	private static string? _clientAddonsCache = null;

	private static string? _userOutfitsLegacy = null;

	private static string? _userOutfits = null;

	private static string? _userAppData = null;

	private static string? _characterFetch = null;

	private static string? _settingsLegacy = null;

	private static string? _settings = null;

	private static string? _secureSettings = null;

	private static readonly HashSet<string> _reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6",
		"COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
		"LPT8", "LPT9"
	};

	private static readonly char[] _directorySeperatorDelimiters = new char[2]
	{
		Path.DirectorySeparatorChar,
		Path.AltDirectorySeparatorChar
	};

	private static readonly char[] _invalidFileNameChars = Path.GetInvalidFileNameChars();

	public static string Base => _base ?? (_base = AppDomain.CurrentDomain.BaseDirectory);

	public static string Logs => _logs ?? (_logs = Path.Combine(Base, "logs"));

	public static string Data => _data ?? (_data = Path.Combine(Base, "data"));

	public static string Clients => _clients ?? (_clients = Path.Combine(Data, "clients"));

	public static string Maps => _maps ?? (_maps = Path.Combine(Base, "maps"));

	public static string Common => _common ?? (_common = Path.Combine(Data, "common"));

	public static string Assets => _assets ?? (_assets = Path.Combine(Data, "assets"));

	public static string Character => _character ?? (_character = Path.Combine(Data, "character"));

	public static string ThumbnailsDeprecated => _thumbnailsDeprecated ?? (_thumbnailsDeprecated = Path.Combine(Data, "thumbnails"));

	public static string Thumbnails => _thumbnails ?? (_thumbnails = Path.Combine(Data, "thumbnails2"));

	public static string SharedScripts => _sharedScripts ?? (_sharedScripts = Path.Combine(Data, "scripts"));

	public static string AutoSaves => _autoSaves ?? (_autoSaves = Path.Combine(Base, "autosaves"));

	public static string AssetPacks => _assetPacks ?? (_assetPacks = Path.Combine(Base, "assetpacks"));

	public static string ClientAddons => _clientAddons ?? (_clientAddons = Path.Combine(Data, "client_addons"));

	public static string ClientAddonsDXVK => _clientAddonsDXVK ?? (_clientAddonsDXVK = Path.Combine(ClientAddons, "dxvk"));

	public static string ClientAddonsDgVoodoo => _clientAddonsDgVoodoo ?? (_clientAddonsDgVoodoo = Path.Combine(ClientAddons, "dgvoodoo"));

	public static string ClientAddonsCache => _clientAddonsCache ?? (_clientAddonsCache = Path.Combine(ClientAddons, "cache"));

	public static string UserOutfitsLegacy => _userOutfitsLegacy ?? (_userOutfitsLegacy = Path.Combine(Data, "user_outfits"));

	public static string UserOutfits => _userOutfits ?? (_userOutfits = Path.Combine(UserAppData, "UserOutfits"));

	public static string UserAppData => _userAppData ?? (_userAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoidstrapClientData"));

	public static string CharacterFetch => _characterFetch ?? (_characterFetch = Path.Combine(Data, "characterfetch"));

	public static string SettingsLegacy => _settingsLegacy ?? (_settingsLegacy = Path.Combine(Data, "UserSettings.json"));

	public static string Settings => _settings ?? (_settings = Path.Combine(UserAppData, "UserSettings.json"));

	public static string SecureSettings => _secureSettings ?? (_secureSettings = Path.Combine(UserAppData, "SecureSettings.dat"));

	public static bool IsFileNameValid(string fileName)
	{
		if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 255 || fileName is "." or ".." || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
		{
			return false;
		}
		if (fileName.IndexOfAny(_invalidFileNameChars) != -1 || fileName.EndsWith(' ') || fileName.EndsWith('.'))
		{
			return false;
		}
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
		return !_reservedNames.Contains(fileNameWithoutExtension);
	}

	public static bool IsPathValid(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		try
		{
			string? pathRoot = Path.GetPathRoot(path);
			string[] parts = (pathRoot != null ? path.Substring(pathRoot.Length) : path).Split(_directorySeperatorDelimiters, StringSplitOptions.RemoveEmptyEntries);
			foreach (string part in parts)
			{
				if (!IsFileNameValid(part))
				{
					return false;
				}
			}
			return true;
		}
		catch
		{
			return false;
		}
	}
}
