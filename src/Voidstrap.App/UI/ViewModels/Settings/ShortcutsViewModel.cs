using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Voidstrap.Models.SettingTasks;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Settings;

public class ShortcutsViewModel : NotifyPropertyChangedViewModel
{
	private static readonly HttpClient _httpClient = Voidstrap.Utility.VpnHttpClient.Create();

	private static readonly ConcurrentDictionary<string, (string? Url, DateTime Expiry)> _gameIconCache = new ConcurrentDictionary<string, (string?, DateTime)>();

	private static readonly ConcurrentDictionary<string, Task<string?>> _ongoingRequests = new ConcurrentDictionary<string, Task<string?>>();

	private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30L);

	private const int MaxGameIconCacheEntries = 64;

	private bool _isPrivateServer;

	private string _privateServerCode = null!;

	private string? _gameInstanceId;

	private string _gameID = App.Settings.Prop.LaunchGameID;

	private string? _gameIconUrl;

	private bool _isIconVisible;

	private string _displayGameName = "Enter a valid Game ID";

	public bool IsStudioOptionVisible => App.IsStudioVisible;

	private static string ShortcutSuffix => Voidstrap.Utility.Platform.IsLinux ? ".desktop" : ".lnk";

	private static string ApplicationsFolder => Voidstrap.Utility.Platform.IsLinux
		? Voidstrap.Utility.LinuxDesktopEntry.ApplicationsFolder
		: Paths.WindowsStartMenu;

	public ShortcutTask DesktopIconTask { get; } = new ShortcutTask("Desktop", Paths.Desktop, "Voidstrap" + ShortcutSuffix);

	public ShortcutTask StartMenuIconTask { get; } = new ShortcutTask("StartMenu", ApplicationsFolder, "Voidstrap" + ShortcutSuffix);

	public ShortcutTask PlayerIconTask { get; } = new ShortcutTask("RobloxPlayer", Paths.Desktop, Strings.LaunchMenu_LaunchRoblox + ShortcutSuffix, "-player");

	public ShortcutTask StudioIconTask { get; } = new ShortcutTask("RobloxStudio", Paths.Desktop, Strings.LaunchMenu_LaunchRobloxStudio + ShortcutSuffix, "-studio");

	public ShortcutTask SettingsIconTask { get; } = new ShortcutTask("Settings", Paths.Desktop, Strings.Menu_Title + ShortcutSuffix, "-settings");

	public ExtractIconsTask ExtractIconsTask { get; } = new ExtractIconsTask();

	public bool IsPrivateServer
	{
		get
		{
			return _isPrivateServer;
		}
		set
		{
			if (_isPrivateServer != value)
			{
				_isPrivateServer = value;
				OnPropertyChanged(nameof(IsPrivateServer));
				OnPropertyChanged(nameof(IsPublicServer));
			}
		}
	}

	public bool IsPublicServer => !IsPrivateServer;

	public string PrivateServerCode
	{
		get
		{
			return _privateServerCode;
		}
		set
		{
			if (_privateServerCode != value)
			{
				_privateServerCode = value;
				OnPropertyChanged(nameof(PrivateServerCode));
			}
		}
	}

	public string? GameInstanceId
	{
		get
		{
			return _gameInstanceId;
		}
		set
		{
			if (_gameInstanceId != value)
			{
				_gameInstanceId = value;
				OnPropertyChanged(nameof(GameInstanceId));
			}
		}
	}

	public string GameID
	{
		get
		{
			return _gameID;
		}
		set
		{
			if (_gameID != value)
			{
				_gameID = value;
				App.Settings.Prop.LaunchGameID = value;
				OnPropertyChanged(nameof(GameID));
				_ = LoadGameIconAsync(value);
			}
		}
	}

	public string? GameIconUrl
	{
		get
		{
			return _gameIconUrl;
		}
		set
		{
			_gameIconUrl = value;
			OnPropertyChanged(nameof(GameIconUrl));
			IsIconVisible = !string.IsNullOrEmpty(_gameIconUrl);
		}
	}

	public bool IsIconVisible
	{
		get
		{
			return _isIconVisible;
		}
		set
		{
			_isIconVisible = value;
			OnPropertyChanged(nameof(IsIconVisible));
		}
	}

	public string DisplayGameName
	{
		get
		{
			return _displayGameName;
		}
		set
		{
			_displayGameName = value;
			OnPropertyChanged(nameof(DisplayGameName));
		}
	}

	public ShortcutsViewModel()
	{
		_ = LoadGameIconAsync(GameID);
		LoadPrivateServerCode();
	}

	private void LoadPrivateServerCode()
	{
		try
		{
			string path = Path.Combine(Paths.UserData, "PrivateServerCode.txt");
			if (File.Exists(path))
			{
				PrivateServerCode = File.ReadAllText(path).Trim();
			}
		}
		catch
		{
		}
	}

	private async Task LoadGameIconAsync(string gameId)
	{
		gameId = gameId?.Trim() ?? "";
		if (!long.TryParse(gameId, out long placeId) || placeId <= 0)
		{
			GameIconUrl = null;
			DisplayGameName = "Enter a valid Game ID";
			return;
		}
		_ = LoadGameNameAsync(gameId);
		if (_gameIconCache.TryGetValue(gameId, out (string?, DateTime) value))
		{
			if (value.Item2 > DateTime.UtcNow)
			{
				if (!IsCurrentGame(gameId))
					return;
				GameIconUrl = value.Item1;
				return;
			}
			_gameIconCache.TryRemove(gameId, out _);
		}
		try
		{
			Task<string?> request = _ongoingRequests.GetOrAdd(gameId, (string _) => FetchGameIconAsync(gameId));
			string? text;
			try
			{
				text = await request;
			}
			finally
			{
				_ongoingRequests.TryRemove(gameId, out _);
			}
			_gameIconCache[gameId] = (text, DateTime.UtcNow.Add(CacheDuration));
			TrimGameIconCache();
			if (!IsCurrentGame(gameId))
				return;
			GameIconUrl = text;
		}
		catch
		{
			if (!IsCurrentGame(gameId))
				return;
			GameIconUrl = null;
		}
	}

	private async Task LoadGameNameAsync(string gameId)
	{
		string name = "Unknown Game";
		try
		{
			string requestUri = "https://apis.roblox.com/universes/v1/places/" + gameId + "/universe";
			using JsonDocument uniDoc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_httpClient, requestUri));
			if (uniDoc.RootElement.TryGetProperty("universeId", out JsonElement universeId))
			{
				string requestUri2 = "https://games.roblox.com/v1/games?universeIds=" + universeId.GetRawText().Trim('"');
				using JsonDocument gameDoc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_httpClient, requestUri2));
				if (gameDoc.RootElement.TryGetProperty("data", out JsonElement data) && data.GetArrayLength() != 0)
					name = data[0].GetProperty("name").GetString() ?? name;
			}
		}
		catch
		{
		}
		if (!IsCurrentGame(gameId))
			return;
		DisplayGameName = name;
	}

	private bool IsCurrentGame(string gameId)
	{
		return string.Equals(GameID?.Trim(), gameId, StringComparison.Ordinal);
	}

	private static void TrimGameIconCache()
	{
		DateTime now = DateTime.UtcNow;
		foreach (KeyValuePair<string, (string? Url, DateTime Expiry)> item in _gameIconCache)
		{
			if (item.Value.Expiry <= now)
			{
				_gameIconCache.TryRemove(item.Key, out _);
			}
		}
		int removeCount = _gameIconCache.Count - MaxGameIconCacheEntries;
		if (removeCount <= 0)
		{
			return;
		}
		foreach (KeyValuePair<string, (string? Url, DateTime Expiry)> item in _gameIconCache.OrderBy(entry => entry.Value.Expiry).Take(removeCount))
		{
			_gameIconCache.TryRemove(item.Key, out _);
		}
	}

	private static async Task<string?> FetchGameIconAsync(string gameId)
	{
		string requestUri = "https://thumbnails.roblox.com/v1/places/gameicons?placeIds=" + gameId + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false";
		using JsonDocument jsonDocument = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_httpClient, requestUri).ConfigureAwait(continueOnCapturedContext: false));
		JsonElement property = jsonDocument.RootElement.GetProperty("data");
		if (property.GetArrayLength() > 0 && property[0].TryGetProperty("imageUrl", out var value))
		{
			return value.GetString();
		}
		return null;
	}
}
