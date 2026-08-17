using System;
using System.IO;
using VoidstrapClient.Common;

namespace VoidstrapClient.WebServer;

internal static class ClientPaths
{
	private static string _clientName;

	private static string? _client;

	private static string? _scripts;

	private static string? _assets;

	private static string? _config;

	private static string? _flags;

	private static string? _studioScript;

	private static string? _joinScript;

	private static string? _joinJson;

	private static string? _hostScript;

	private static string? _gameServerScript;

	private static string? _visitScript;

	private static string? _placeSpecificScript;

	private static string? _loadPlaceInfoScript;

	private static string? _autoSaveScript;

	private static string? _eggHuntScript;

	public static string Client => _client ?? (_client = Path.Combine(PathHelper.Clients, _clientName));

	public static string Scripts => _scripts ?? (_scripts = Path.Combine(Client, "scripts"));

	public static string Assets => _assets ?? (_assets = Path.Combine(Client, "assets"));

	public static string Config => _config ?? (_config = Path.Combine(Client, "VoidstrapClient_Config.json"));

	public static string Flags => _flags ?? (_flags = Path.Combine(Client, "Flags.json"));

	public static string StudioScript => _studioScript ?? (_studioScript = Path.Combine(Scripts, "studio.lua"));

	public static string JoinScript => _joinScript ?? (_joinScript = Path.Combine(Scripts, "join.lua"));

	public static string JoinJson => _joinJson ?? (_joinJson = Path.Combine(Scripts, "join.json"));

	public static string HostScript => _hostScript ?? (_hostScript = Path.Combine(Scripts, "host.lua"));

	public static string GameServerScript => _gameServerScript ?? (_gameServerScript = Path.Combine(Scripts, "gameserver.lua"));

	public static string VisitScript => _visitScript ?? (_visitScript = Path.Combine(Scripts, "visit.lua"));

	public static string PlaceSpecificScript => _placeSpecificScript ?? (_placeSpecificScript = Path.Combine(Scripts, "placespecificscript.lua"));

	public static string LoadPlaceInfoScript => _loadPlaceInfoScript ?? (_loadPlaceInfoScript = Path.Combine(Scripts, "loadplaceinfo.lua"));

	public static string AutoSaveScript
	{
		get
		{
			if (_autoSaveScript != null)
			{
				return _autoSaveScript;
			}
			string text = Path.Combine(Scripts, "autosave.lua");
			_autoSaveScript = (File.Exists(text) ? text : Path.Combine(PathHelper.SharedScripts, "autosave.lua"));
			return _autoSaveScript;
		}
	}

	public static string EggHuntScript
	{
		get
		{
			if (_eggHuntScript != null)
			{
				return _eggHuntScript;
			}
			string text = Path.Combine(Scripts, "egghunt.lua");
			_eggHuntScript = (File.Exists(text) ? text : Path.Combine(PathHelper.SharedScripts, "egghunt.lua"));
			return _eggHuntScript;
		}
	}

	public static void SetClientName(string clientName)
	{
		if (!string.IsNullOrEmpty(_clientName))
		{
			throw new Exception("Tried to set client name twice");
		}
		if (string.IsNullOrWhiteSpace(clientName) || clientName.Length > 64 || !PathHelper.IsFileNameValid(clientName))
		{
			throw new ArgumentException("The client name is invalid", nameof(clientName));
		}
		string root = Path.GetFullPath(PathHelper.Clients).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		string candidate = Path.GetFullPath(Path.Combine(root, clientName));
		if (!string.Equals(Path.GetDirectoryName(candidate), root, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The client name is invalid", nameof(clientName));
		}
		_clientName = clientName;
	}
}
