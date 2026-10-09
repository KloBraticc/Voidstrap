using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Enums;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;
using Voidstrap.Utility;
using Voidstrap.Extensions;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public enum ServerHistorySortBy
{
    Latest,
    Oldest,
    MostPlayed,
    NameAZ,
    NameZA
}

public class SortByOption
{
    public string Display { get; init; } = null!;
    public ServerHistorySortBy Value { get; init; }
    public override string ToString() => Display;
}

public class ServerTypeFilterOption
{
    public string Display { get; init; } = null!;
    public ServerType? Value { get; init; }
    public override string ToString() => Display;
}

internal class ServerHistoryViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private readonly ActivityWatcher _activityWatcher;
	private readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);
	private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

	private bool _disposed;
	private readonly CancellationToken _lifetimeToken;
	private readonly SemaphoreSlim _statusGate = new(1, 1);
	private int _nextStatusPlace;
	private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(60) };

	private readonly string _historyFilePath = Paths.ServerHistory;

	private const int MaxLoadedHistoryEntries = 500;
	private const long MaxHistoryFileBytes = 8L * 1024 * 1024;

	public List<ActivityData> GameHistory { get; private set; } = new List<ActivityData>();

	public IEnumerable<ActivityData> Top10RecentHistory => GameHistory.Take(10);

	public GenericTriState LoadState { get; private set; } = GenericTriState.Unknown;

	public string Error { get; private set; } = string.Empty;

	public string EmptyText => HistoryText("Empty");

	public string CopyLinkText => HistoryText("CopyLink");

	public ICommand CloseWindowCommand { get; }

	public ICommand CopyDeeplinkCommand { get; }

	public ICommand LaunchDeeplinkCommand { get; }

	public ObservableCollection<SortByOption> SortOptions { get; } = new ObservableCollection<SortByOption>
	{
		new SortByOption { Display = HistoryText("Latest"), Value = ServerHistorySortBy.Latest },
		new SortByOption { Display = HistoryText("Oldest"), Value = ServerHistorySortBy.Oldest },
		new SortByOption { Display = HistoryText("MostPlayed"), Value = ServerHistorySortBy.MostPlayed },
		new SortByOption { Display = HistoryText("NameAscending"), Value = ServerHistorySortBy.NameAZ },
		new SortByOption { Display = HistoryText("NameDescending"), Value = ServerHistorySortBy.NameZA }
	};

	public ObservableCollection<ServerTypeFilterOption> ServerTypeFilters { get; } = new ObservableCollection<ServerTypeFilterOption>
	{
		new ServerTypeFilterOption { Display = HistoryText("AllServers"), Value = null },
		new ServerTypeFilterOption { Display = HistoryText("Public"), Value = ServerType.Public },
		new ServerTypeFilterOption { Display = HistoryText("Private"), Value = ServerType.Private },
		new ServerTypeFilterOption { Display = HistoryText("Reserved"), Value = ServerType.Reserved }
	};

	public SortByOption SelectedSort
	{
		get => _selectedSort;
		set
		{
			if (_selectedSort != value)
			{
				_selectedSort = value;
				OnPropertyChanged(nameof(SelectedSort));
				ApplyFilterAndSort();
			}
		}
	}

	public ServerTypeFilterOption SelectedServerTypeFilter
	{
		get => _selectedServerTypeFilter;
		set
		{
			if (_selectedServerTypeFilter != value)
			{
				_selectedServerTypeFilter = value;
				OnPropertyChanged(nameof(SelectedServerTypeFilter));
				ApplyFilterAndSort();
			}
		}
	}

	public List<ActivityData> FilteredGameHistory { get; private set; } = new List<ActivityData>();

	private SortByOption _selectedSort = null!;
	private ServerTypeFilterOption _selectedServerTypeFilter = null!;

	public event EventHandler? RequestCloseEvent;

	public ServerHistoryViewModel(ActivityWatcher activityWatcher)
	{
		_activityWatcher = activityWatcher ?? throw new ArgumentNullException(nameof(activityWatcher));
		CloseWindowCommand = new RelayCommand(RequestClose);
		CopyDeeplinkCommand = new RelayCommand<ActivityData>(CopyDeeplinkToClipboard, CanCopyLink);
		LaunchDeeplinkCommand = new RelayCommand<ActivityData>(LaunchDeeplink, CanRejoin);
		_selectedSort = SortOptions[0];
		_selectedServerTypeFilter = ServerTypeFilters[0];
		_lifetimeToken = _lifetimeCts.SafeToken();
		_activityWatcher.OnGameLeave += OnGameLeave;
		_statusTimer.Tick += OnStatusTick;
		_statusTimer.Start();
		_ = LoadDataAsync(_lifetimeToken);
	}

	private async void OnGameLeave(object? sender, EventArgs e)
	{
		await LoadDataAsync(_lifetimeToken);
	}

	private List<ActivityData> LoadHistoryFromFile()
	{
		try
		{
			if (!File.Exists(_historyFilePath))
				return new();
			FileInfo file = new(_historyFilePath);
			if (file.Length <= 0 || file.Length > MaxHistoryFileBytes)
				return new();
			return JsonFile.Deserialize<List<ActivityData>>(_historyFilePath, JsonOptions.Tolerant, MaxHistoryFileBytes)
				.Where(HistoryPersister.IsWithinDesktopRetention).OrderByDescending(item => item.TimeJoined).Take(MaxLoadedHistoryEntries).ToList();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerHistoryViewModel::LoadHistoryFromFile", ex);
			return new();
		}
	}

	private async Task LoadDataAsync(CancellationToken token)
	{
		bool gateHeld = false;
		try
		{
			if (!await _loadGate.WaitAsync(0, token))
				return;
			gateHeld = true;
			token.ThrowIfCancellationRequested();
			SetLoadingState();
			List<ActivityData> stored = await Task.Run(LoadHistoryFromFile, token);
			List<ActivityData> history;
			lock (_activityWatcher.History)
				history = _activityWatcher.History.ToList();
			await Application.Current.Dispatcher.InvokeAsync(() =>
			{
				if (_disposed || token.IsCancellationRequested)
					return;
				MergeAndConsolidateHistory(stored.Concat(history));
				NotifyHistoryChanged();
				SetSuccessState();
			}, DispatcherPriority.Background, token);
			try
			{
				using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
				timeout.CancelAfter(TimeSpan.FromSeconds(20));
				await UniverseDetails.FetchForEntriesAsync(GameHistory.ToArray(), timeout.Token);
			}
			catch (OperationCanceledException) when (token.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("ServerHistoryViewModel::FetchForEntries", ex);
			}
			token.ThrowIfCancellationRequested();
			await Application.Current.Dispatcher.InvokeAsync((Action)delegate
			{
				if (token.IsCancellationRequested || _disposed)
					return;
				lock (_activityWatcher.History)
					history = _activityWatcher.History.ToList();
				MergeAndConsolidateHistory(history);
			}, DispatcherPriority.Background, token);
			token.ThrowIfCancellationRequested();
			await Application.Current.Dispatcher.InvokeAsync((Action)delegate
			{
				if (token.IsCancellationRequested || _disposed)
					return;
				NotifyHistoryChanged();
				SetSuccessState();
			}, DispatcherPriority.Background, token);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			if (!_disposed)
				HandleError(ex);
		}
		finally
		{
			if (gateHeld)
				_loadGate.Release();
		}
	}

	private async void OnStatusTick(object? sender, EventArgs e)
	{
		if (Application.Current.Windows.OfType<Voidstrap.UI.Elements.ContextMenu.ServerHistory>().Any(window => window.IsVisible && window.WindowState != System.Windows.WindowState.Minimized))
			await RefreshServerStatusesAsync();
	}

	private async Task RefreshServerStatusesAsync()
	{
		bool acquired = false;
		try
		{
			if (_disposed || !await _statusGate.WaitAsync(0, _lifetimeToken))
				return;
			acquired = true;
			var places = FilteredGameHistory.Where(item => item.ServerType == ServerType.Public && !string.IsNullOrWhiteSpace(item.JobId)).GroupBy(item => item.PlaceId).ToArray();
			if (places.Length == 0)
				return;
			int start = (int)((uint)_nextStatusPlace % (uint)places.Length);
			places = places.Skip(start).Concat(places.Take(start)).Take(2).ToArray();
			using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
			deadline.CancelAfter(TimeSpan.FromSeconds(20));
			await Parallel.ForEachAsync(places, new ParallelOptions { MaxDegreeOfParallelism = 1, CancellationToken = deadline.Token }, async (place, token) =>
			{
				Interlocked.Increment(ref _nextStatusPlace);
				Dictionary<string, bool?> statuses = await QueryServerStatusesAsync(App.HttpClient, place.Key, place.Select(item => item.JobId), token);
				await Application.Current.Dispatcher.InvokeAsync(() =>
				{
					if (_disposed)
						return;
					foreach (ActivityData item in GameHistory.Where(item => item.PlaceId == place.Key && item.ServerType == ServerType.Public && !string.IsNullOrWhiteSpace(item.JobId)))
						item.SetServerStatus(statuses.GetValueOrDefault(item.JobId));
					((IRelayCommand)LaunchDeeplinkCommand).NotifyCanExecuteChanged();
				}, DispatcherPriority.Background, _lifetimeToken);
			});
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			if (!_disposed)
				App.Logger.WriteException("ServerHistoryViewModel::RefreshServerStatuses", ex);
		}
		finally
		{
			if (acquired)
				_statusGate.Release();
		}
	}

	private static async Task<Dictionary<string, bool?>> QueryServerStatusesAsync(System.Net.Http.HttpClient client, long placeId, IEnumerable<string> jobIds, CancellationToken token)
	{
		Dictionary<string, bool?> statuses = jobIds.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(job => job, _ => (bool?)null, StringComparer.OrdinalIgnoreCase);
		string cursor = string.Empty;
		HashSet<string> cursors = new(StringComparer.Ordinal);
		try
		{
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
			timeout.CancelAfter(TimeSpan.FromSeconds(10));
			for (int page = 0; page < 10; page++)
			{
				string url = $"https://games.roblox.com/v1/games/{placeId}/servers/Public?excludeFullGames=false&limit=100&sortOrder=Desc&cursor={Uri.EscapeDataString(cursor)}";
				string json = await Http.GetStringBoundedAsync(client, url, 1024 * 1024, timeout.Token).ConfigureAwait(false);
				using JsonDocument document = JsonDocument.Parse(json);
				JsonElement root = document.RootElement;
				if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array || !root.TryGetProperty("nextPageCursor", out JsonElement next))
					return statuses;
				foreach (JsonElement server in data.EnumerateArray())
				{
					if (!server.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
						return statuses;
					string jobId = id.GetString()!;
					if (statuses.ContainsKey(jobId))
						statuses[jobId] = true;
				}
				if (statuses.Values.All(value => value == true))
					return statuses;
				if (next.ValueKind == JsonValueKind.Null)
				{
					foreach (string job in statuses.Keys.ToArray())
						statuses[job] ??= false;
					return statuses;
				}
				if (next.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(next.GetString()) || !cursors.Add(next.GetString()!))
					return statuses;
				cursor = next.GetString()!;
			}
		}
		catch (OperationCanceledException) when (!token.IsCancellationRequested)
		{
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Http.RateLimitedException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerHistoryViewModel::QueryServerStatuses", ex);
		}
		return statuses;
	}

	private static string HistoryText(string key) => Voidstrap.Resources.Strings.ResourceManager.GetString("ContextMenu.GameHistory." + key, Voidstrap.Resources.Strings.Culture) ?? key;

	private static bool CanCopyLink(ActivityData? data) => data is { PlaceId: > 0 } && data.ServerType != ServerType.Reserved && (data.ServerType != ServerType.Private || !string.IsNullOrWhiteSpace(data.AccessCode));

	private static bool CanRejoin(ActivityData? data) => CanCopyLink(data) && data?.ServerOnline != false;

	private void MergeAndConsolidateHistory(IEnumerable<ActivityData> incoming)
	{
		GameHistory = HistoryPersister.Merge(GameHistory, incoming);
		foreach (ActivityData item in GameHistory)
			item.ComputeDisplayTimes();
	}

	private void LaunchDeeplink(ActivityData? data)
	{
		if (!CanRejoin(data) || data == null)
		{
			return;
		}
		try
		{
			string voidstrapPath = Paths.LaunchExecutable;
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = voidstrapPath,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(voidstrapPath) ?? ""
			};
			startInfo.ArgumentList.Add("-player");
			startInfo.ArgumentList.Add(data.GetNativeJoinUri());
			using Process? process = Process.Start(startInfo);
			if (process != null)
				RequestClose();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerHistoryViewModel::LaunchDeeplink", ex);
			Voidstrap.UI.Frontend.ShowMessageBox(HistoryText("LaunchFailed"), MessageBoxImage.Error);
		}
	}

	private void CopyDeeplinkToClipboard(ActivityData? data)
	{
		if (!CanCopyLink(data) || data == null)
		{
			return;
		}
		try
		{
			Voidstrap.Utility.ClipboardService.SetText(data.GetInviteDeeplink());
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerHistoryViewModel::CopyDeeplinkToClipboard", ex);
		}
	}

	private void NotifyHistoryChanged()
	{
		OnPropertyChanged(nameof(GameHistory));
		OnPropertyChanged(nameof(Top10RecentHistory));
		ApplyFilterAndSort();
	}

	private void ApplyFilterAndSort()
	{
		IEnumerable<ActivityData> source = GameHistory;
		if (_selectedServerTypeFilter?.Value.HasValue == true)
		{
			ServerType filterType = _selectedServerTypeFilter.Value.Value;
			source = source.Where((ActivityData x) => x.ServerType == filterType);
		}
		source = (_selectedSort?.Value) switch
		{
			ServerHistorySortBy.Oldest => source.OrderBy((ActivityData x) => x.TimeJoined),
			ServerHistorySortBy.MostPlayed => source.OrderByDescending((ActivityData x) => (x.TimeLeft ?? x.TimeJoined).ToUniversalTime() - x.TimeJoined.ToUniversalTime()),
			ServerHistorySortBy.NameAZ => source.OrderBy((ActivityData x) => x.GameName),
			ServerHistorySortBy.NameZA => source.OrderByDescending((ActivityData x) => x.GameName),
			_ => source.OrderByDescending((ActivityData x) => x.TimeJoined),
		};
		FilteredGameHistory = source.ToList();
		OnPropertyChanged(nameof(FilteredGameHistory));
		if (!_disposed)
			_ = RefreshServerStatusesAsync();
	}

	private void SetLoadingState()
	{
		RunOnUi(delegate
		{
			LoadState = GenericTriState.Unknown;
			OnPropertyChanged(nameof(LoadState));
		});
	}

	private void SetSuccessState()
	{
		RunOnUi(delegate
		{
			LoadState = GenericTriState.Successful;
			OnPropertyChanged(nameof(LoadState));
		});
	}

	private void HandleError(Exception ex)
	{
		App.Logger.WriteException("ServerHistoryViewModel::HandleError", ex);
		RunOnUi(delegate
		{
			Error = HistoryText("LoadFailed");
			LoadState = GenericTriState.Failed;
			OnPropertyChanged(nameof(Error));
			OnPropertyChanged(nameof(LoadState));
		});
	}

	private static void RunOnUi(Action action)
	{
		Dispatcher? dispatcher = Application.Current?.Dispatcher;
		if (dispatcher == null || dispatcher.CheckAccess())
			action();
		else
			dispatcher.Invoke(action);
	}

	private void RequestClose()
	{
		this.RequestCloseEvent?.Invoke(this, EventArgs.Empty);
	}

	private async Task DisposeStatusGateAsync()
	{
		await _statusGate.WaitAsync().ConfigureAwait(false);
		_statusGate.Dispose();
	}

	private async Task DisposeLoadGateAsync()
	{
		await _loadGate.WaitAsync().ConfigureAwait(false);
		_loadGate.Dispose();
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_activityWatcher.OnGameLeave -= OnGameLeave;
		_statusTimer.Stop();
		_statusTimer.Tick -= OnStatusTick;
		_lifetimeCts.Cancel();
		_lifetimeCts.Dispose();
		_ = DisposeLoadGateAsync();
		_ = DisposeStatusGateAsync();
		GameHistory.Clear();
		FilteredGameHistory.Clear();
		RequestCloseEvent = null;
		GC.SuppressFinalize(this);
	}
}
