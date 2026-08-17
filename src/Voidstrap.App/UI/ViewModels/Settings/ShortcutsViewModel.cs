using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;
using Voidstrap.Models.SettingTasks;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Settings;

public class ShortcutsViewModel : NotifyPropertyChangedViewModel
{
	private static readonly HttpClient _httpClient = Voidstrap.Utility.VpnHttpClient.Create();

	private static readonly ConcurrentDictionary<string, (string Url, DateTime Expiry)> _gameIconCache = new ConcurrentDictionary<string, (string, DateTime)>();

	private static readonly ConcurrentDictionary<string, Task<string>> _ongoingRequests = new ConcurrentDictionary<string, Task<string>>();

	private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30L);

	private const int MaxGameIconCacheEntries = 64;

	private bool _isPrivateServer;

	private string _privateServerCode;

	private string? _gameInstanceId;

	private string _gameID = App.Settings.Prop.LaunchGameID;

	private string _gameIconUrl;

	private bool _isIconVisible;

	private bool _isGameIconVisible;

	private string _displayGameName;

	private string _gameName;

	public bool IsStudioOptionVisible => App.IsStudioVisible;

	public ShortcutTask DesktopIconTask { get; } = new ShortcutTask("Desktop", Paths.Desktop, "Voidstrap.lnk");

	public ShortcutTask StartMenuIconTask { get; } = new ShortcutTask("StartMenu", Paths.WindowsStartMenu, "Voidstrap.lnk");

	public ShortcutTask PlayerIconTask { get; } = new ShortcutTask("RobloxPlayer", Paths.Desktop, Strings.LaunchMenu_LaunchRoblox + ".lnk", "-player");

	public ShortcutTask StudioIconTask { get; } = new ShortcutTask("RobloxStudio", Paths.Desktop, Strings.LaunchMenu_LaunchRobloxStudio + ".lnk", "-studio");

	public ShortcutTask SettingsIconTask { get; } = new ShortcutTask("Settings", Paths.Desktop, Strings.Menu_Title + ".lnk", "-settings");

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
				OnPropertyChanged("IsPrivateServer");
			}
		}
	}

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
				if (TryParseShareLink(value, out string code))
				{
					_privateServerCode = code;
				}
				else
				{
					_privateServerCode = value;
				}
				OnPropertyChanged("PrivateServerCode");
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
				OnPropertyChanged("GameInstanceId");
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
				OnPropertyChanged("GameID");
				LoadGameIconAsync(value);
			}
		}
	}

	public string GameIconUrl
	{
		get
		{
			return _gameIconUrl;
		}
		set
		{
			_gameIconUrl = value;
			OnPropertyChanged("GameIconUrl");
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
			OnPropertyChanged("IsIconVisible");
		}
	}

	public bool IsGameIconVisible
	{
		get
		{
			return _isGameIconVisible;
		}
		set
		{
			if (_isGameIconVisible != value)
			{
				_isGameIconVisible = value;
				OnPropertyChanged("IsGameIconVisible");
			}
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
			OnPropertyChanged("DisplayGameName");
		}
	}

	public string GameName
	{
		get
		{
			return _gameName;
		}
		set
		{
			_gameName = value;
			OnPropertyChanged("GameName");
		}
	}

	public ShortcutsViewModel()
	{
		LoadGameIconAsync(GameID);
		LoadPrivateServerCode();
	}

	private bool TryParseShareLink(string input, out string code)
	{
		code = null;
		if (string.IsNullOrEmpty(input))
		{
			return false;
		}
		try
		{
			NameValueCollection nameValueCollection = HttpUtility.ParseQueryString(new Uri(input).Query);
			string a = nameValueCollection["type"];
			string text = nameValueCollection["code"];
			if (!string.IsNullOrEmpty(text) && string.Equals(a, "Server", StringComparison.OrdinalIgnoreCase))
			{
				code = text;
				return true;
			}
		}
		catch
		{
		}
		return false;
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
		if (string.IsNullOrWhiteSpace(gameId))
		{
			GameIconUrl = null;
			GameName = null;
			IsGameIconVisible = false;
			return;
		}
		if (_gameIconCache.TryGetValue(gameId, out (string, DateTime) value))
		{
			if (value.Item2 > DateTime.UtcNow)
			{
				GameIconUrl = value.Item1;
				IsGameIconVisible = !string.IsNullOrEmpty(value.Item1);
				LoadGameNameAsync(gameId);
				return;
			}
			_gameIconCache.TryRemove(gameId, out (string, DateTime) _);
		}
		try
		{
			Task<string> request = _ongoingRequests.GetOrAdd(gameId, (string _) => FetchGameIconAsync(gameId));
			string text;
			try
			{
				text = await request;
			}
			finally
			{
				_ongoingRequests.TryRemove(gameId, out Task<string> _);
			}
			_gameIconCache[gameId] = (text, DateTime.UtcNow.Add(CacheDuration));
			TrimGameIconCache();
			GameIconUrl = text;
			IsGameIconVisible = !string.IsNullOrEmpty(text);
			await LoadGameNameAsync(gameId);
		}
		catch
		{
			GameIconUrl = null;
			IsGameIconVisible = false;
			GameName = null;
		}
	}

	private async Task LoadGameNameAsync(string gameId)
	{
		if (string.IsNullOrWhiteSpace(gameId))
		{
			ShortcutsViewModel shortcutsViewModel = this;
			string gameName = (DisplayGameName = "Unknown Game");
			shortcutsViewModel.GameName = gameName;
			return;
		}
		try
		{
			string requestUri = "https://apis.roblox.com/universes/v1/places/" + gameId + "/universe";
			using JsonDocument uniDoc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_httpClient, requestUri));
			if (!uniDoc.RootElement.TryGetProperty("universeId", out var value))
			{
				ShortcutsViewModel shortcutsViewModel2 = this;
				string gameName = (DisplayGameName = "Unknown Game");
				shortcutsViewModel2.GameName = gameName;
				return;
			}
			string text3 = value.GetRawText().Trim('"');
			string requestUri2 = "https://games.roblox.com/v1/games?universeIds=" + text3;
			using JsonDocument jsonDocument = JsonDocument.Parse(await Voidstrap.Utility.Http.GetStringBoundedAsync(_httpClient, requestUri2));
			JsonElement property = jsonDocument.RootElement.GetProperty("data");
			if (property.GetArrayLength() == 0)
			{
				ShortcutsViewModel shortcutsViewModel3 = this;
				string gameName = (DisplayGameName = "Unknown Game");
				shortcutsViewModel3.GameName = gameName;
				return;
			}
			string displayGameName = (GameName = property[0].GetProperty("name").GetString() ?? "Unknown Game");
			DisplayGameName = displayGameName;
		}
		catch (Exception)
		{
			ShortcutsViewModel shortcutsViewModel4 = this;
			string gameName = (DisplayGameName = "Unknown Game");
			shortcutsViewModel4.GameName = gameName;
		}
	}

	private static void TrimGameIconCache()
	{
		DateTime now = DateTime.UtcNow;
		foreach (KeyValuePair<string, (string Url, DateTime Expiry)> item in _gameIconCache)
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
		foreach (KeyValuePair<string, (string Url, DateTime Expiry)> item in _gameIconCache.OrderBy(entry => entry.Value.Expiry).Take(removeCount))
		{
			_gameIconCache.TryRemove(item.Key, out _);
		}
	}

	private async Task<string> FetchGameIconAsync(string gameId)
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

