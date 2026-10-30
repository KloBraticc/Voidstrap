using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.Settings
{
    internal sealed class PrivateServerRow : NotifyPropertyChangedViewModel
    {
        private bool _busy;

        public PrivateServerRow(AccountPrivateServer server)
        {
            Server = server;
        }

        public AccountPrivateServer Server { get; }

        public string Title => string.IsNullOrWhiteSpace(Server.Name) ? Strings.PrivateServers_Title : Server.Name;

        public string Owner =>
            Server.IsMine ? Strings.PrivateServers_Yours
            : string.IsNullOrWhiteSpace(Server.OwnerName) ? Strings.PrivateServers_Shared
            : string.Format(Strings.PrivateServers_OwnedBy, Server.OwnerName);

        public string Detail
        {
            get
            {
                if (!Server.Active)
                    return Owner + "  •  " + Strings.PrivateServers_Off;
                if (Server.IsMine && Server.Expires is DateTime expires)
                {
                    string date = expires.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
                    return Owner
                        + "  •  "
                        + string.Format(Server.WillRenew ? Strings.PrivateServers_Renews : Strings.PrivateServers_Ends, date);
                }
                return Owner;
            }
        }

        public bool IsOn => Server.Active;

        public double RowOpacity => Server.Active ? 1.0 : 0.6;

        public Visibility ToggleVisibility =>
            Server.IsMine && (Server.Active || Server.Expires is not DateTime expires || expires > DateTime.UtcNow)
                ? Visibility.Visible
                : Visibility.Collapsed;

        public string ToggleText => Server.Active ? Strings.PrivateServers_TurnOff : Strings.PrivateServers_TurnOn;

        public bool Busy
        {
            get => _busy;
            set
            {
                if (_busy == value)
                    return;
                _busy = value;
                OnPropertyChanged(nameof(Busy));
                OnPropertyChanged(nameof(CanAct));
            }
        }

        public bool CanAct => !_busy;

        public bool CanJoin => !_busy && Server.Active;

        public void Refresh()
        {
            OnPropertyChanged(nameof(Detail));
            OnPropertyChanged(nameof(IsOn));
            OnPropertyChanged(nameof(RowOpacity));
            OnPropertyChanged(nameof(ToggleText));
            OnPropertyChanged(nameof(CanJoin));
        }
    }

    internal sealed class PrivateServerGame : NotifyPropertyChangedViewModel
    {
        private string? _iconUrl;

        public long UniverseId { get; init; }

        public string Name { get; init; } = string.Empty;

        public string? IconUrl
        {
            get => _iconUrl;
            set
            {
                if (_iconUrl == value)
                    return;
                _iconUrl = value;
                OnPropertyChanged(nameof(IconUrl));
            }
        }

        public string CountText { get; init; } = string.Empty;

        public List<PrivateServerRow> Servers { get; init; } = [];
    }

    internal sealed class PrivateServersViewModel : NotifyPropertyChangedViewModel
    {
        private readonly SemaphoreSlim _loadGate = new(1, 1);
        private List<PrivateServerGame> _mineGames = [];
        private List<PrivateServerGame> _friendGames = [];
        private bool _showFriends;
        private string _searchText = string.Empty;
        private PrivateServerGame? _selectedGame;
        private bool _isLoading;
        private bool _loaded;
        private string _status = string.Empty;

        public PrivateServersViewModel()
        {
            RefreshCommand = new AsyncRelayCommand(() => LoadAsync(true));
            JoinCommand = new AsyncRelayCommand<PrivateServerRow>(JoinAsync);
            ToggleCommand = new AsyncRelayCommand<PrivateServerRow>(ToggleAsync);
            ShowMineCommand = new RelayCommand(() => SetShowFriends(false));
            ShowFriendsCommand = new RelayCommand(() => SetShowFriends(true));
        }

        public ObservableCollection<PrivateServerGame> Games { get; } = new();

        public ICommand RefreshCommand { get; }

        public ICommand JoinCommand { get; }

        public ICommand ToggleCommand { get; }

        public ICommand ShowMineCommand { get; }

        public ICommand ShowFriendsCommand { get; }

        public Wpf.Ui.Common.ControlAppearance MineAppearance =>
            _showFriends ? Wpf.Ui.Common.ControlAppearance.Secondary : Wpf.Ui.Common.ControlAppearance.Primary;

        public Wpf.Ui.Common.ControlAppearance FriendsAppearance =>
            _showFriends ? Wpf.Ui.Common.ControlAppearance.Primary : Wpf.Ui.Common.ControlAppearance.Secondary;

        private void SetShowFriends(bool friends)
        {
            if (_showFriends == friends)
                return;
            _showFriends = friends;
            OnPropertyChanged(nameof(MineAppearance));
            OnPropertyChanged(nameof(FriendsAppearance));
            SelectedGame = null;
            ApplyFilter();
        }

        public PrivateServerGame? SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (ReferenceEquals(_selectedGame, value))
                    return;
                _selectedGame = value;
                OnPropertyChanged(nameof(SelectedGame));
                OnPropertyChanged(nameof(DetailVisibility));
            }
        }

        public Visibility DetailVisibility => _selectedGame == null ? Visibility.Collapsed : Visibility.Visible;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText == value)
                    return;
                _searchText = value ?? string.Empty;
                OnPropertyChanged(nameof(SearchText));
                ApplyFilter();
            }
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set
            {
                _isLoading = value;
                OnPropertyChanged(nameof(IsLoading));
            }
        }

        public string Status
        {
            get => _status;
            private set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(StatusVisibility));
            }
        }

        public Visibility StatusVisibility => string.IsNullOrEmpty(_status) ? Visibility.Collapsed : Visibility.Visible;

        public async Task LoadAsync(bool force)
        {
            if (!force && _loaded)
                return;
            if (!await _loadGate.WaitAsync(0))
                return;
            IsLoading = true;
            Status = Strings.PrivateServers_Loading;
            try
            {
                (PrivateServersStatus result, List<AccountPrivateServer> servers) = await Task.Run(() =>
                    RobloxServers.GetAccountPrivateServersAsync(CancellationToken.None)
                );
                _mineGames = BuildGames(servers.Where(server => server.IsMine));
                _friendGames = BuildGames(servers.Where(server => !server.IsMine));
                ApplyFilter();
                Status = result switch
                {
                    PrivateServersStatus.NotSignedIn => Strings.PrivateServers_SignIn,
                    PrivateServersStatus.Unavailable => Strings.PrivateServers_Failed,
                    _ => servers.Count == 0 ? Strings.PrivateServers_Empty : string.Empty,
                };
                _loaded = result == PrivateServersStatus.Ready;
                App.Logger.WriteLine(
                    "PrivateServersViewModel::Load",
                    $"Showing {servers.Count} private servers, {_mineGames.Count} games of your own and {_friendGames.Count} shared with you"
                );
                IsLoading = false;
                await LoadIconsAsync();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("PrivateServersViewModel::Load", ex);
                Status = Strings.PrivateServers_Failed;
            }
            finally
            {
                IsLoading = false;
                _loadGate.Release();
            }
        }

        private async Task LoadIconsAsync()
        {
            List<PrivateServerGame> games = _mineGames.Concat(_friendGames).ToList();
            List<long> missing = games
                .Where(game => game.UniverseId > 0 && string.IsNullOrEmpty(game.IconUrl))
                .Select(game => game.UniverseId)
                .Distinct()
                .ToList();
            if (missing.Count == 0)
                return;
            try
            {
                await Task.Run(async () =>
                {
                    await Task.WhenAll(missing.Chunk(50).Select(chunk => UniverseDetails.FetchBulk(string.Join(",", chunk))));
                    await UniverseDetails.RefreshMissingThumbnailsAsync(missing);
                });
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("PrivateServersViewModel::LoadIcons", "Game icons could not be loaded: " + ex.Message);
            }
            foreach (PrivateServerGame game in games)
            {
                if (game.UniverseId > 0 && UniverseDetails.LoadFromCache(game.UniverseId)?.Thumbnail?.ImageUrl is { Length: > 0 } url)
                    game.IconUrl = url;
            }
        }

        private static List<PrivateServerGame> BuildGames(IEnumerable<AccountPrivateServer> servers)
        {
            List<PrivateServerGame> games = new();
            foreach (IGrouping<long, AccountPrivateServer> group in servers.GroupBy(server => server.UniverseId))
            {
                if (group.Any(server => IsDeleted(server.GameName)))
                    continue;
                UniverseDetails? details = group.Key > 0 ? UniverseDetails.LoadFromCache(group.Key) : null;
                string name = details?.Data?.Name is { Length: > 0 } known
                    ? known
                    : group.Select(server => server.GameName).FirstOrDefault(value => value.Length > 0) ?? "";
                List<PrivateServerRow> rows = group
                    .OrderByDescending(server => server.Active)
                    .ThenBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(server => new PrivateServerRow(server))
                    .ToList();
                if (IsDeleted(name))
                    continue;
                games.Add(
                    new PrivateServerGame
                    {
                        UniverseId = group.Key,
                        Name = name,
                        IconUrl = details?.Thumbnail?.ImageUrl,
                        CountText = string.Format(Strings.PrivateServers_Count, rows.Count),
                        Servers = rows,
                    }
                );
            }
            return games
                .OrderByDescending(game => game.Servers.Any(row => row.Server.Active))
                .ThenBy(game => game.Name.TrimStart('[', ' ', '(', '{'), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static bool IsDeleted(string name) => name.Contains("Content Deleted", StringComparison.OrdinalIgnoreCase);

        private void ApplyFilter()
        {
            string query = _searchText.Trim();
            PrivateServerGame? previous = _selectedGame;
            Games.Clear();
            foreach (PrivateServerGame game in _showFriends ? _friendGames : _mineGames)
            {
                if (
                    query.Length == 0
                    || game.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || game.Servers.Any(row => row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                )
                {
                    Games.Add(game);
                }
            }
            SelectedGame = previous != null && Games.Contains(previous) ? previous : Games.FirstOrDefault();
        }

        private async Task JoinAsync(PrivateServerRow? row)
        {
            if (row == null || !row.CanJoin || row.Server.PlaceId <= 0)
                return;
            row.Busy = true;
            Status = string.Format(Strings.ContextMenu_ServerBrowser_Joining, row.Title);
            try
            {
                string? accessCode = await Task.Run(() =>
                    RobloxServers.GetAccessCodeAsync(row.Server.PlaceId, row.Server.Id, CancellationToken.None)
                );
                if (string.IsNullOrEmpty(accessCode))
                {
                    Status = Strings.PrivateServers_CannotJoin;
                    return;
                }
                if (!LibraryViewModel.TryLaunchPlayer(LaunchInterceptor.BuildExperienceUri(row.Server.PlaceId, null, accessCode)))
                {
                    Status = Strings.ContextMenu_ServerBrowser_JoinFailed;
                    return;
                }
                Status = string.Empty;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("PrivateServersViewModel::Join", ex);
                Status = Strings.ContextMenu_ServerBrowser_JoinFailed;
            }
            finally
            {
                row.Busy = false;
                row.Refresh();
            }
        }

        private async Task ToggleAsync(PrivateServerRow? row)
        {
            if (row == null || !row.Server.IsMine || row.Busy)
                return;
            row.Busy = true;
            bool target = !row.Server.Active;
            try
            {
                if (await Task.Run(() => RobloxServers.SetPrivateServerActiveAsync(row.Server.Id, target, CancellationToken.None)))
                {
                    row.Server.Active = target;
                    Status = string.Empty;
                }
                else
                {
                    Status = Strings.PrivateServers_ChangeFailed;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("PrivateServersViewModel::Toggle", ex);
                Status = Strings.PrivateServers_ChangeFailed;
            }
            finally
            {
                row.Busy = false;
                row.Refresh();
            }
        }
    }
}
