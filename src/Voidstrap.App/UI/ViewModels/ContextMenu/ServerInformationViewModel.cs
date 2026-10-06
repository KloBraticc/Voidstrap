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
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public class ServerInformationViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private readonly ActivityWatcher _activityWatcher;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly EventHandler _onGameJoin;

	private readonly EventHandler _onGameLeave;

	private readonly Dispatcher? _dispatcher;

	private bool _disposed;

	private DateTime _lastFriendsFetch = DateTime.MinValue;

	private string _gameName = Strings.Common_Loading;

	private ImageSource? _gameIcon;

	private string _username = Strings.Common_Loading;

	private string _playerCount = Strings.Common_Loading;

	private const int MaxVisibleFriends = 5;

	private IReadOnlyList<ServerFriend> _friends = [];

	private string _friendsHeader = "Friends in this server";

	private string _friendsStatus = string.Empty;

	private Visibility _friendsVisibility = Visibility.Collapsed;

	private string _playerCountHint = string.Empty;

	private int _friendsRefreshActive;

	private string _serverLocation = Strings.Common_Loading;

	private readonly DispatcherTimer? _uptimeTimer;

	private DateTimeOffset? _serverStartedUtc;

	private string? _uptimeJobId;

	private DateTime _nextUptimeFetch = DateTime.MinValue;

	private int _uptimeFetchActive;

	private string _uptime = string.Empty;

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
				OnPropertyChanged(nameof(GameName));
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
			OnPropertyChanged(nameof(GameIcon));
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
				OnPropertyChanged(nameof(Username));
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
				OnPropertyChanged(nameof(PlayerCount));
			}
		}
	}

	public IReadOnlyList<ServerFriend> Friends
	{
		get
		{
			return _friends;
		}
		private set
		{
			_friends = value ?? [];
			OnPropertyChanged(nameof(Friends));
			OnPropertyChanged(nameof(VisibleFriends));
			OnPropertyChanged(nameof(FriendsListVisibility));
			OnPropertyChanged(nameof(MoreFriendsText));
			OnPropertyChanged(nameof(MoreFriendsToolTip));
			OnPropertyChanged(nameof(MoreFriendsVisibility));
		}
	}

	public IReadOnlyList<ServerFriend> VisibleFriends => _friends.Count > MaxVisibleFriends ? _friends.Take(MaxVisibleFriends).ToList() : _friends;

	public Visibility FriendsListVisibility => _friends.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

	public string MoreFriendsText => _friends.Count > MaxVisibleFriends ? "+" + (_friends.Count - MaxVisibleFriends) : string.Empty;

	public string MoreFriendsToolTip => _friends.Count > MaxVisibleFriends ? string.Join(", ", _friends.Skip(MaxVisibleFriends).Select(friend => friend.ToolTipText)) : string.Empty;

	public Visibility MoreFriendsVisibility => _friends.Count > MaxVisibleFriends ? Visibility.Visible : Visibility.Collapsed;

	public string FriendsHeader
	{
		get
		{
			return _friendsHeader;
		}
		private set
		{
			if (_friendsHeader != value)
			{
				_friendsHeader = value;
				OnPropertyChanged(nameof(FriendsHeader));
			}
		}
	}

	public string FriendsStatus
	{
		get
		{
			return _friendsStatus;
		}
		private set
		{
			if (_friendsStatus != value)
			{
				_friendsStatus = value;
				OnPropertyChanged(nameof(FriendsStatus));
				OnPropertyChanged(nameof(FriendsStatusVisibility));
			}
		}
	}

	public Visibility FriendsStatusVisibility => string.IsNullOrEmpty(_friendsStatus) ? Visibility.Collapsed : Visibility.Visible;

	public string PlayerCountHint
	{
		get
		{
			return _playerCountHint;
		}
		private set
		{
			if (_playerCountHint != value)
			{
				_playerCountHint = value;
				OnPropertyChanged(nameof(PlayerCountHint));
				OnPropertyChanged(nameof(PlayerCountHintVisibility));
			}
		}
	}

	public Visibility PlayerCountHintVisibility => string.IsNullOrEmpty(_playerCountHint) ? Visibility.Collapsed : Visibility.Visible;

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
				OnPropertyChanged(nameof(FriendsVisibility));
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
				OnPropertyChanged(nameof(ServerLocation));
			}
		}
	}

	public string Uptime
	{
		get
		{
			return _uptime;
		}
		private set
		{
			if (_uptime != value)
			{
				_uptime = value;
				OnPropertyChanged(nameof(Uptime));
			}
		}
	}

	public Visibility UptimeVisibility => _serverStartedUtc.HasValue ? Visibility.Visible : Visibility.Collapsed;

	public ICommand CopyInstanceIdCommand { get; }

	public ICommand RefreshServerLocationCommand { get; }

	public ServerInformationViewModel(Watcher watcher)
	{
		_activityWatcher = watcher?.ActivityWatcher ?? throw new ArgumentNullException(nameof(watcher));
		_dispatcher = Application.Current?.Dispatcher;
		CopyInstanceIdCommand = new RelayCommand(CopyInstanceId);
		RefreshServerLocationCommand = new AsyncRelayCommand(QueryServerLocationAsync);
		_onGameJoin = delegate
		{
			RunOnDispatcher(delegate
			{
				_ = RefreshAllAsync();
			});
		};
		_onGameLeave = delegate
		{
			RunOnDispatcher(ResetForNoGame);
		};
		_activityWatcher.OnGameJoin += _onGameJoin;
		_activityWatcher.OnGameLeave += _onGameLeave;
		if (_dispatcher != null)
		{
			_uptimeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
			{
				Interval = TimeSpan.FromSeconds(1.0)
			};
			_uptimeTimer.Tick += OnUptimeTick;
		}
		_ = InitializeAsync();
	}

	private void RunOnDispatcher(Action action)
	{
		if (_disposed)
			return;
		Dispatcher? dispatcher = _dispatcher;
		if (dispatcher == null || dispatcher.CheckAccess())
		{
			action();
			return;
		}
		if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
			return;
		try
		{
			dispatcher.BeginInvoke(new Action(delegate
			{
				if (!_disposed)
					action();
			}));
		}
		catch (InvalidOperationException)
		{
		}
	}

	private async Task InitializeAsync()
	{
		await RefreshAllAsync();
		_ = RefreshLoopAsync(_cts.Token);
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
				await RefreshServerUptimeAsync();
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
		_lastFriendsFetch = DateTime.MinValue;
		Friends = [];
		FriendsHeader = "Friends in this server";
		FriendsStatus = "Checking your friends";
		FriendsVisibility = Visibility.Visible;
		PlayerCount = Strings.Common_Loading;
		PlayerCountHint = string.Empty;
		OnPropertyChanged(nameof(InstanceId));
		OnPropertyChanged(nameof(ServerType));
		OnPropertyChanged(nameof(ServerLocationVisibility));
		if (ServerLocationVisibility == Visibility.Visible)
		{
			_ = QueryServerLocationAsync();
		}
		else
		{
			ServerLocation = Strings.Common_NotAvailable;
		}
		ResetUptime();
		await Task.WhenAll(FetchUsernameAsync(), FetchGameInfoAsync(), RefreshPlayerCountAsync(), RefreshFriendsInServerAsync(), RefreshServerUptimeAsync());
	}

	private void ResetForNoGame()
	{
		_lastFriendsFetch = DateTime.MinValue;
		Friends = [];
		FriendsStatus = string.Empty;
		FriendsVisibility = Visibility.Collapsed;
		PlayerCountHint = string.Empty;
		GameName = Strings.Common_NotAvailable;
		GameIcon = null;
		Username = Strings.Common_NotAvailable;
		PlayerCount = Strings.Common_NotAvailable;
		ServerLocation = Strings.Common_NotAvailable;
		ResetUptime();
		OnPropertyChanged(nameof(InstanceId));
		OnPropertyChanged(nameof(ServerType));
	}

	private void ResetUptime()
	{
		_uptimeTimer?.Stop();
		_serverStartedUtc = null;
		_uptimeJobId = null;
		_nextUptimeFetch = DateTime.MinValue;
		Uptime = string.Empty;
		OnPropertyChanged(nameof(UptimeVisibility));
	}

	private async Task RefreshServerUptimeAsync()
	{
		ActivityData? data = _activityWatcher.Data;
		if (data == null || data.PlaceId <= 0 || string.IsNullOrEmpty(data.JobId) || !_activityWatcher.InGame)
			return;
		string jobId = data.JobId;
		if (_serverStartedUtc.HasValue && string.Equals(_uptimeJobId, jobId, StringComparison.Ordinal))
			return;
		if (data.ServerStartedUtc is DateTimeOffset fromServerLog)
		{
			ApplyServerStart(jobId, fromServerLog);
			return;
		}
		if (DateTime.UtcNow < _nextUptimeFetch || Interlocked.Exchange(ref _uptimeFetchActive, 1) != 0)
			return;
		_nextUptimeFetch = DateTime.UtcNow.AddMinutes(2.0);
		try
		{
			ServerStartLookup lookup = await VoidstrapMatchmaker.GetServerStartAsync(data.PlaceId, jobId, _cts.Token);
			if (_disposed || !string.Equals(_activityWatcher.Data?.JobId, jobId, StringComparison.Ordinal))
			{
				_nextUptimeFetch = DateTime.MinValue;
				return;
			}
			if (lookup.Status != ServerStartStatus.Found || _serverStartedUtc.HasValue)
				return;
			ApplyServerStart(jobId, lookup.StartedUtc);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ServerInformationViewModel", "Server uptime could not be refreshed: " + ex.Message);
		}
		finally
		{
			Interlocked.Exchange(ref _uptimeFetchActive, 0);
		}
	}

	private void ApplyServerStart(string jobId, DateTimeOffset started)
	{
		_serverStartedUtc = started;
		_uptimeJobId = jobId;
		UpdateUptimeText();
		OnPropertyChanged(nameof(UptimeVisibility));
		_uptimeTimer?.Start();
	}

	private void OnUptimeTick(object? sender, EventArgs e)
	{
		UpdateUptimeText();
	}

	private void UpdateUptimeText()
	{
		if (_serverStartedUtc is not DateTimeOffset started)
			return;
		Uptime = FormatUptime(DateTimeOffset.UtcNow - started);
	}

	internal static string FormatUptime(TimeSpan elapsed)
	{
		if (elapsed < TimeSpan.Zero)
			elapsed = TimeSpan.Zero;
		if (elapsed.TotalDays >= 1.0)
			return $"{(int)elapsed.TotalDays}d {elapsed.Hours}h {elapsed.Minutes:00}m {elapsed.Seconds:00}s";
		if (elapsed.TotalHours >= 1.0)
			return $"{elapsed.Hours}h {elapsed.Minutes:00}m {elapsed.Seconds:00}s";
		return $"{elapsed.Minutes}m {elapsed.Seconds:00}s";
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
			string? text = (rootElement.TryGetProperty("name", out value) ? value.GetString() : null);
			JsonElement value2;
			string? text2 = (rootElement.TryGetProperty("displayName", out value2) ? value2.GetString() : null);
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
			ActivityData? data = _activityWatcher.Data;
			Dictionary<int, ActivityData.UserLog>.ValueCollection? valueCollection = data?.PlayerLogs?.Values;
			if (valueCollection == null)
			{
				return null;
			}
			string targetId = data!.UserId.ToString();
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
			ActivityData? data = _activityWatcher.Data;
			if (data == null || data.PlaceId == 0L)
			{
				GameName = Strings.Common_NotAvailable;
				return;
			}
			UniverseDetails? details = data.UniverseDetails;
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
			BitmapSource? icon = Voidstrap.Utility.Platform.IsLinux
				? await Voidstrap.Utility.AppImage.LoadAsync(url, 512)
				: await Task.Run(() => Voidstrap.Utility.AppImage.LoadSync(url));
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
			ServerPlayerSnapshot snapshot = await _activityWatcher.GetServerPlayerSnapshotAsync();
			if (_disposed)
				return;
			if (snapshot.State == ServerPlayerCountState.NotInGame)
			{
				PlayerCount = Strings.Common_NotAvailable;
				PlayerCountHint = string.Empty;
				return;
			}
			string count = snapshot.Playing > 0
				? (snapshot.MaxPlayers > 0 ? $"{snapshot.Playing}/{snapshot.MaxPlayers}" : snapshot.Playing.ToString())
				: (snapshot.MaxPlayers > 0 ? string.Format(Strings.ServerInfo_PlayerCount_MaxOnly, snapshot.MaxPlayers) : Strings.ServerInfo_PlayerCount_Unknown);
			PlayerCount = snapshot.GameTotal > 0 ? count + "  •  " + string.Format(Strings.ServerInfo_PlayerCount_InGame, snapshot.GameTotal) : count;
			PlayerCountHint = BuildPlayerCountHint(snapshot);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ServerInformationViewModel::RefreshPlayerCount", "The player count could not be shown: " + ex.Message);
			PlayerCount = Strings.Common_ErrorFetchingPlayerCount;
			PlayerCountHint = string.Empty;
		}
	}

	private static string BuildPlayerCountHint(ServerPlayerSnapshot snapshot)
	{
		DateTime now = DateTime.UtcNow;
		bool stale = snapshot.Playing > 0 && snapshot.UpdatedUtc.HasValue;
		string age = snapshot.UpdatedUtc is { } updated ? FormatDuration(now - updated) : string.Empty;
		string retry = FormatDuration((snapshot.RetryUtc ?? now) - now);
		return snapshot.State switch
		{
			ServerPlayerCountState.Live => snapshot.UpdatedUtc is { } live && now - live >= TimeSpan.FromMinutes(1) ? string.Format(Strings.ServerInfo_Hint_Updated, age) : string.Empty,
			ServerPlayerCountState.RateLimited => stale ? string.Format(Strings.ServerInfo_Hint_RateLimitedStale, age, retry) : string.Format(Strings.ServerInfo_Hint_RateLimited, retry),
			ServerPlayerCountState.NotListed => stale ? string.Format(Strings.ServerInfo_Hint_NotListedStale, age) : string.Format(Strings.ServerInfo_Hint_NotListed, retry),
			ServerPlayerCountState.PrivateNotListed => Strings.ServerInfo_Hint_PrivateNotListed,
			ServerPlayerCountState.PrivateSignedOut => Strings.ServerInfo_Hint_PrivateSignedOut,
			ServerPlayerCountState.Reserved => Strings.ServerInfo_Hint_Reserved,
			ServerPlayerCountState.Unavailable => stale ? string.Format(Strings.ServerInfo_Hint_UnavailableStale, age, retry) : string.Format(Strings.ServerInfo_Hint_Unavailable, retry),
			_ => string.Empty
		};
	}

	private static string FormatDuration(TimeSpan span)
	{
		int seconds = Math.Max(1, (int)Math.Ceiling(span.TotalSeconds));
		if (seconds < 60)
			return seconds + "s";
		int minutes = seconds / 60;
		int rest = seconds % 60;
		return rest == 0 || minutes >= 10 ? minutes + "m" : minutes + "m " + rest + "s";
	}

	private async Task RefreshFriendsInServerAsync()
	{
		if ((DateTime.UtcNow - _lastFriendsFetch).TotalSeconds < 25.0 || Interlocked.Exchange(ref _friendsRefreshActive, 1) != 0)
		{
			return;
		}
		_lastFriendsFetch = DateTime.UtcNow;
		try
		{
			ActivityData? data = _activityWatcher.Data;
			if (data == null || string.IsNullOrEmpty(data.JobId) || !_activityWatcher.InGame)
			{
				Friends = [];
				FriendsStatus = string.Empty;
				FriendsVisibility = Visibility.Collapsed;
				return;
			}
			FriendsVisibility = Visibility.Visible;
			if (data.UserId <= 0)
			{
				Friends = [];
				FriendsHeader = "Friends in this server";
				FriendsStatus = "Waiting for Roblox to report your account";
				_lastFriendsFetch = DateTime.UtcNow - TimeSpan.FromSeconds(20.0);
				return;
			}
			string jobId = data.JobId;
			FriendsInServerResult result = await RobloxPresence.GetFriendsInServerAsync(data.UserId, jobId, _cts.Token);
			if (_disposed || !string.Equals(_activityWatcher.Data?.JobId, jobId, StringComparison.Ordinal))
			{
				_lastFriendsFetch = DateTime.MinValue;
				return;
			}
			switch (result.Status)
			{
			case FriendsInServerStatus.NotSignedIn:
				Friends = [];
				FriendsHeader = "Friends in this server";
				FriendsStatus = "Sign in to your Roblox account in Voidstrap to see which friends are here.";
				break;
			case FriendsInServerStatus.SignInExpired:
				Friends = [];
				FriendsHeader = "Friends in this server";
				FriendsStatus = "Your saved Roblox sign in has expired. Sign in again in Voidstrap to see which friends are here.";
				break;
			case FriendsInServerStatus.NoFriends:
				Friends = [];
				FriendsHeader = "Friends in this server";
				FriendsStatus = "Your Roblox account has no friends to look for yet.";
				break;
			case FriendsInServerStatus.Unavailable:
				if (_friends.Count == 0)
				{
					FriendsHeader = "Friends in this server";
					FriendsStatus = "Roblox did not answer, checking again shortly.";
				}
				_lastFriendsFetch = DateTime.UtcNow - TimeSpan.FromSeconds(10.0);
				break;
			default:
				Friends = result.Friends;
				FriendsHeader = result.Friends.Count > 0 ? $"Friends in this server ({result.Friends.Count})" : "Friends in this server";
				FriendsStatus = result.Friends.Count > 0 ? string.Empty : $"None of your {result.FriendCount:N0} friends are in this server.";
				break;
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ServerInformationViewModel", "Friends in this server could not be refreshed: " + ex.Message);
			if (_friends.Count == 0)
			{
				FriendsStatus = "Roblox did not answer, checking again shortly.";
			}
		}
		finally
		{
			Interlocked.Exchange(ref _friendsRefreshActive, 0);
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
			string? text = await _activityWatcher.Data.QueryServerLocation(cancellationToken);
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
			Voidstrap.Utility.ClipboardService.SetDataObject(InstanceId);
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
		if (_uptimeTimer != null)
		{
			_uptimeTimer.Stop();
			_uptimeTimer.Tick -= OnUptimeTick;
		}
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
