using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;

namespace Voidstrap.UI.ViewModels.ContextMenu;

internal class ServerInformationViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private readonly ActivityWatcher _activityWatcher;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly EventHandler _onGameJoin;

	private readonly EventHandler _onGameLeave;

	private bool _disposed;

	private int _maxPlayers;

	private int _gameTotal;

	private DateTime _lastApiFetch = DateTime.MinValue;

	private DateTime _lastFriendsFetch = DateTime.MinValue;

	private string _gameName = Strings.Common_Loading;

	private ImageSource? _gameIcon;

	private string _username = Strings.Common_Loading;

	private string _playerCount = Strings.Common_Loading;

	private string _friendsInServer = string.Empty;

	private Visibility _friendsVisibility = Visibility.Collapsed;

	private string _serverLocation = Strings.Common_Loading;

	public string InstanceId => _activityWatcher?.Data?.JobId ?? Strings.Common_NotAvailable;

	public string ServerType => _activityWatcher?.Data?.ServerType.ToTranslatedString() ?? Strings.Common_NotAvailable;

	public Visibility ServerLocationVisibility
	{
		get
		{
			if (!App.Settings.Prop.ShowServerDetails)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public string GameName
	{
		get
		{
			return _gameName;
		}
		private set
		{
			if (_gameName != value)
			{
				_gameName = value;
				OnPropertyChanged("GameName");
			}
		}
	}

	public ImageSource? GameIcon
	{
		get
		{
			return _gameIcon;
		}
		private set
		{
			_gameIcon = value;
			OnPropertyChanged("GameIcon");
		}
	}

	public string Username
	{
		get
		{
			return _username;
		}
		private set
		{
			if (_username != value)
			{
				_username = value;
				OnPropertyChanged("Username");
			}
		}
	}

	public string PlayerCount
	{
		get
		{
			return _playerCount;
		}
		private set
		{
			if (_playerCount != value)
			{
				_playerCount = value;
				OnPropertyChanged("PlayerCount");
			}
		}
	}

	public string FriendsInServer
	{
		get
		{
			return _friendsInServer;
		}
		private set
		{
			if (_friendsInServer != value)
			{
				_friendsInServer = value;
				OnPropertyChanged("FriendsInServer");
			}
		}
	}

	public Visibility FriendsVisibility
	{
		get
		{
			return _friendsVisibility;
		}
		private set
		{
			if (_friendsVisibility != value)
			{
				_friendsVisibility = value;
				OnPropertyChanged("FriendsVisibility");
			}
		}
	}

	public string ServerLocation
	{
		get
		{
			return _serverLocation;
		}
		private set
		{
			if (_serverLocation != value)
			{
				_serverLocation = value;
				OnPropertyChanged("ServerLocation");
			}
		}
	}

	public ICommand CopyInstanceIdCommand { get; }

	public ICommand RefreshServerLocationCommand { get; }

	public ServerInformationViewModel(Watcher watcher)
	{
		_activityWatcher = watcher?.ActivityWatcher ?? throw new ArgumentNullException("watcher");
		CopyInstanceIdCommand = new RelayCommand(CopyInstanceId);
		RefreshServerLocationCommand = new AsyncRelayCommand(QueryServerLocationAsync);
		_onGameJoin = delegate
		{
			RefreshAllAsync();
		};
		_onGameLeave = delegate
		{
			ResetForNoGame();
		};
		_activityWatcher.OnGameJoin += _onGameJoin;
		_activityWatcher.OnGameLeave += _onGameLeave;
		InitializeAsync();
	}

	private async Task InitializeAsync()
	{
		await RefreshAllAsync();
		RefreshLoopAsync(_cts.Token);
	}

	private async Task RefreshLoopAsync(CancellationToken token)
	{
		_ = 2;
		try
		{
			while (!token.IsCancellationRequested && !_activityWatcher.IsDisposed)
			{
				await Task.Delay(5000, token);
				await RefreshPlayerCountAsync();
				await RefreshFriendsInServerAsync();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch
		{
		}
	}

	private async Task RefreshAllAsync()
	{
		_maxPlayers = 0;
		_gameTotal = 0;
		_lastApiFetch = DateTime.MinValue;
		_lastFriendsFetch = DateTime.MinValue;
		OnPropertyChanged("InstanceId");
		OnPropertyChanged("ServerType");
		OnPropertyChanged("ServerLocationVisibility");
		if (ServerLocationVisibility == Visibility.Visible)
		{
			QueryServerLocationAsync();
		}
		else
		{
			ServerLocation = Strings.Common_NotAvailable;
		}
		InlineArray4<Task> buffer = default(InlineArray4<Task>);
		buffer[0] = FetchUsernameAsync();
		buffer[1] = FetchGameInfoAsync();
		buffer[2] = RefreshPlayerCountAsync();
		buffer[3] = RefreshFriendsInServerAsync();
		await Task.WhenAll(buffer);
	}

	private void ResetForNoGame()
	{
		_maxPlayers = 0;
		_gameTotal = 0;
		_lastApiFetch = DateTime.MinValue;
		_lastFriendsFetch = DateTime.MinValue;
		FriendsInServer = string.Empty;
		FriendsVisibility = Visibility.Collapsed;
		GameName = Strings.Common_NotAvailable;
		GameIcon = null;
		Username = Strings.Common_NotAvailable;
		PlayerCount = Strings.Common_NotAvailable;
		ServerLocation = Strings.Common_NotAvailable;
		OnPropertyChanged("InstanceId");
		OnPropertyChanged("ServerType");
	}

	private async Task FetchUsernameAsync()
	{
		try
		{
			long num = _activityWatcher.Data?.UserId ?? 0;
			if (num <= 0)
			{
				Username = UsernameFromLogs() ?? Strings.Common_NotAvailable;
				return;
			}
			using JsonDocument jsonDocument = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString($"https://users.roblox.com/v1/users/{num}"));
			JsonElement rootElement = jsonDocument.RootElement;
			JsonElement value;
			string text = (rootElement.TryGetProperty("name", out value) ? value.GetString() : null);
			JsonElement value2;
			string text2 = (rootElement.TryGetProperty("displayName", out value2) ? value2.GetString() : null);
			string text3 = ((string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2) || string.Equals(text, text2, StringComparison.Ordinal)) ? (text ?? text2 ?? string.Empty) : (text2 + " (@" + text + ")"));
			Username = (string.IsNullOrWhiteSpace(text3) ? (UsernameFromLogs() ?? Strings.Common_NotAvailable) : text3);
		}
		catch
		{
			Username = UsernameFromLogs() ?? Strings.Common_NotAvailable;
		}
	}

	private string? UsernameFromLogs()
	{
		try
		{
			ActivityData data = _activityWatcher.Data;
			Dictionary<int, ActivityData.UserLog>.ValueCollection valueCollection = data?.PlayerLogs?.Values;
			if (valueCollection == null)
			{
				return null;
			}
			string targetId = data.UserId.ToString();
			return (from u in valueCollection
				where u != null && (u.UserId?.Trim() ?? "") == targetId
				orderby u.Time descending
				select u).FirstOrDefault()?.Username;
		}
		catch
		{
			return null;
		}
	}

	private async Task FetchGameInfoAsync()
	{
		try
		{
			ActivityData data = _activityWatcher.Data;
			if (data == null || data.PlaceId == 0L)
			{
				GameName = Strings.Common_NotAvailable;
				return;
			}
			UniverseDetails details = data.UniverseDetails;
			if (details == null && data.UniverseId > 0)
			{
				try
				{
					await UniverseDetails.FetchSingle(data.UniverseId);
					details = (data.UniverseDetails = UniverseDetails.LoadFromCache(data.UniverseId));
				}
				catch
				{
				}
			}
			GameName = details?.Data?.Name ?? $"Place {data.PlaceId}";
			await SetGameIcon(details?.Thumbnail?.ImageUrl);
		}
		catch
		{
			GameName = Strings.Common_NotAvailable;
		}
	}

	private async Task SetGameIcon(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return;
		}
		try
		{
			BitmapSource? icon = await Task.Run(() => Voidstrap.Utility.AppImage.LoadSync(url));
			if (icon != null)
			{
				GameIcon = icon;
			}
		}
		catch
		{
		}
	}

	private async Task RefreshPlayerCountAsync()
	{
		try
		{
			ActivityData data = _activityWatcher.Data;
			if (data == null || data.PlaceId == 0L)
			{
				PlayerCount = Strings.Common_NotAvailable;
				return;
			}
			int num;
			if (_maxPlayers <= 0 || _gameTotal <= 0 || (DateTime.UtcNow - _lastApiFetch).TotalSeconds >= 15.0)
			{
				_lastApiFetch = DateTime.UtcNow;
				(int, int, int, bool) tuple = await _activityWatcher.GetServerPlayerStatsAsync();
				if (tuple.Item2 > 0)
				{
					_maxPlayers = tuple.Item2;
				}
				if (tuple.Item3 > 0)
				{
					_gameTotal = tuple.Item3;
				}
				(num, _, _, _) = tuple;
			}
			else
			{
				num = _activityWatcher.GetPlayerCountFromLogs();
				if (_activityWatcher.InGame && num < 1)
				{
					num = 1;
				}
				if (_maxPlayers > 0 && num > _maxPlayers)
				{
					num = _maxPlayers;
				}
			}
			string text = ((_maxPlayers > 0) ? $"{num}/{_maxPlayers}" : ((num > 0) ? num.ToString() : Strings.Common_NotAvailable));
			PlayerCount = ((_gameTotal > 0) ? $"{text}  •  {_gameTotal:N0} in game" : text);
		}
		catch
		{
			PlayerCount = Strings.Common_ErrorFetchingPlayerCount;
		}
	}

	private async Task RefreshFriendsInServerAsync()
	{
		if ((DateTime.UtcNow - _lastFriendsFetch).TotalSeconds < 25.0)
		{
			return;
		}
		_lastFriendsFetch = DateTime.UtcNow;
		try
		{
			ActivityData data = _activityWatcher.Data;
			if (data == null || string.IsNullOrEmpty(data.JobId) || !RobloxCookie.Exists)
			{
				FriendsInServer = string.Empty;
				FriendsVisibility = Visibility.Collapsed;
				return;
			}
			List<ServerFriend> list = await RobloxPresence.GetFriendsInServerAsync(data.UserId, data.JobId, _cts.Token);
			if (list.Count == 0)
			{
				FriendsInServer = string.Empty;
				FriendsVisibility = Visibility.Collapsed;
				return;
			}
			FriendsInServer = string.Join(", ", list.Select((ServerFriend f) => f.Label));
			FriendsVisibility = Visibility.Visible;
		}
		catch
		{
			FriendsInServer = string.Empty;
			FriendsVisibility = Visibility.Collapsed;
		}
	}

	private async Task QueryServerLocationAsync(CancellationToken cancellationToken = default(CancellationToken))
	{
		ServerLocation = Strings.Common_Loading;
		try
		{
			if (_activityWatcher.Data == null)
			{
				ServerLocation = Strings.Common_NotAvailable;
				return;
			}
			string text = await _activityWatcher.Data.QueryServerLocation();
			ServerLocation = ((!string.IsNullOrWhiteSpace(text)) ? text : "Location not available");
		}
		catch (Exception ex)
		{
			ServerLocation = "Error fetching location: " + ex.Message;
		}
	}

	private void CopyInstanceId()
	{
		try
		{
			Clipboard.SetDataObject(InstanceId);
		}
		catch (Exception ex)
		{
			MessageBox.Show("Error copying instance ID: " + ex.Message, "Clipboard Error", MessageBoxButton.OK, MessageBoxImage.Exclamation);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		try
		{
			_cts.Cancel();
		}
		catch
		{
		}
		try
		{
			_activityWatcher.OnGameJoin -= _onGameJoin;
			_activityWatcher.OnGameLeave -= _onGameLeave;
		}
		catch
		{
		}
		try
		{
			_cts.Dispose();
		}
		catch
		{
		}
		GC.SuppressFinalize(this);
	}
}
