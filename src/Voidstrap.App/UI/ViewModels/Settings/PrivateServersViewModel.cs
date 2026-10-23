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

        public string Owner => Server.IsMine
            ? Strings.PrivateServers_Yours
            : string.IsNullOrWhiteSpace(Server.OwnerName) ? Strings.PrivateServers_Shared : string.Format(Strings.PrivateServers_OwnedBy, Server.OwnerName);

        public string Detail
        {
            get
            {
                if (!Server.Active)
                    return Owner + "  •  " + Strings.PrivateServers_Off;
                if (Server.IsMine && Server.Expires is DateTime expires)
                {
                    string date = expires.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);
                    return Owner + "  •  " + string.Format(Server.WillRenew ? Strings.PrivateServers_Renews : Strings.PrivateServers_Ends, date);
                }
                return Owner;
            }
        }

        public bool IsOn => Server.Active;

        public double RowOpacity => Server.Active ? 1.0 : 0.6;

        public Visibility ToggleVisibility => Server.IsMine ? Visibility.Visible : Visibility.Collapsed;

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

    internal sealed class PrivateServerGame
    {
        public string Name { get; init; } = string.Empty;

        public string? IconUrl { get; init; }

        public string CountText { get; init; } = string.Empty;

        public List<PrivateServerRow> Servers { get; init; } = [];

        public bool HasMine => Servers.Any(row => row.Server.IsMine);

        public string SidebarGroup => HasMine ? Strings.PrivateServers_Yours : Strings.PrivateServers_Shared;
    }

    internal sealed class PrivateServersViewModel : NotifyPropertyChangedViewModel
    {
        private readonly SemaphoreSlim _loadGate = new(1, 1);
        private List<PrivateServerGame> _allGames = [];
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
        }

        public ObservableCollection<PrivateServerGame> Games { get; } = new();

        public ICommand RefreshCommand { get; }

        public ICommand JoinCommand { get; }

        public ICommand ToggleCommand { get; }

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
                (PrivateServersStatus result, List<AccountPrivateServer> servers) = await Task.Run(() => RobloxServers.GetAccountPrivateServersAsync(CancellationToken.None));
                List<long> universes = servers.Select(server => server.UniverseId).Where(id => id > 0).Distinct().ToList();
                if (universes.Count > 0)
                {
                    try
                    {
                        foreach (long[] chunk in universes.Chunk(50))
                            await UniverseDetails.FetchBulk(string.Join(",", chunk));
                        await UniverseDetails.RefreshMissingThumbnailsAsync(universes);
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine("PrivateServersViewModel::Load", "Game names and icons could not be loaded: " + ex.Message);
                    }
                }
                List<PrivateServerGame> games = new();
                foreach (IGrouping<long, AccountPrivateServer> group in servers.GroupBy(server => server.UniverseId))
                {
                    UniverseDetails? details = group.Key > 0 ? UniverseDetails.LoadFromCache(group.Key) : null;
                    string name = details?.Data?.Name is { Length: > 0 } known ? known : group.Select(server => server.GameName).FirstOrDefault(value => value.Length > 0) ?? "";
                    List<PrivateServerRow> rows = group
                        .OrderByDescending(server => server.IsMine)
                        .ThenByDescending(server => server.Active)
                        .ThenBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
                        .Select(server => new PrivateServerRow(server))
                        .ToList();
                    games.Add(new PrivateServerGame
                    {
                        Name = name,
                        IconUrl = details?.Thumbnail?.ImageUrl,
                        CountText = string.Format(Strings.PrivateServers_Count, rows.Count),
                        Servers = rows
                    });
                }
                _allGames = games
                    .OrderByDescending(game => game.HasMine)
                    .ThenBy(game => game.Name.Contains("Content Deleted", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(game => game.Servers.Any(row => row.Server.Active))
                    .ThenBy(game => game.Name.TrimStart('[', ' ', '(', '{'), StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                ApplyFilter();
                Status = result switch
                {
                    PrivateServersStatus.NotSignedIn => Strings.PrivateServers_SignIn,
                    PrivateServersStatus.Unavailable => Strings.PrivateServers_Failed,
                    _ => servers.Count == 0 ? Strings.PrivateServers_Empty : string.Empty
                };
                _loaded = result == PrivateServersStatus.Ready;
                App.Logger.WriteLine("PrivateServersViewModel::Load", $"Showing {servers.Count} private servers in {_allGames.Count} games");
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

        private void ApplyFilter()
        {
            string query = _searchText.Trim();
            PrivateServerGame? previous = _selectedGame;
            Games.Clear();
            foreach (PrivateServerGame game in _allGames)
            {
                if (query.Length == 0
                    || game.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || game.Servers.Any(row => row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
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
                string? accessCode = await Task.Run(() => RobloxServers.GetAccessCodeAsync(row.Server.PlaceId, row.Server.Id, CancellationToken.None));
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
