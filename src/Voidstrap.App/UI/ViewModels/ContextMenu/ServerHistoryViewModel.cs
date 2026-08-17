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
    public string Display { get; init; }
    public ServerHistorySortBy Value { get; init; }
    public override string ToString() => Display;
}

public class ServerTypeFilterOption
{
    public string Display { get; init; }
    public ServerType? Value { get; init; }
    public override string ToString() => Display;
}

internal class ServerHistoryViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	private readonly ActivityWatcher _activityWatcher;
	private readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);
	private readonly CancellationTokenSource _lifetimeCts = new CancellationTokenSource();

	private bool _disposed;

	private readonly string _historyFilePath = Paths.ServerHistory;

	private const int MaxHistoryEntries = 100;
	private const int MaxLoadedHistoryEntries = 500;
	private const long MaxHistoryFileBytes = 8L * 1024 * 1024;

	public List<ActivityData> GameHistory { get; private set; } = new List<ActivityData>();

	public IEnumerable<ActivityData> Top10RecentHistory => GameHistory.Take(10);

	public GenericTriState LoadState { get; private set; } = GenericTriState.Unknown;

	public string Error { get; private set; } = string.Empty;

	public ICommand CloseWindowCommand { get; }

	public ICommand CopyDeeplinkCommand { get; }

	public ICommand LaunchDeeplinkCommand { get; }

	public ObservableCollection<SortByOption> SortOptions { get; } = new ObservableCollection<SortByOption>
	{
		new SortByOption { Display = "Latest", Value = ServerHistorySortBy.Latest },
		new SortByOption { Display = "Oldest", Value = ServerHistorySortBy.Oldest },
		new SortByOption { Display = "Most Played", Value = ServerHistorySortBy.MostPlayed },
		new SortByOption { Display = "Game Name (A-Z)", Value = ServerHistorySortBy.NameAZ },
		new SortByOption { Display = "Game Name (Z-A)", Value = ServerHistorySortBy.NameZA }
	};

	public ObservableCollection<ServerTypeFilterOption> ServerTypeFilters { get; } = new ObservableCollection<ServerTypeFilterOption>
	{
		new ServerTypeFilterOption { Display = "All Servers", Value = null },
		new ServerTypeFilterOption { Display = "Public", Value = ServerType.Public },
		new ServerTypeFilterOption { Display = "Private", Value = ServerType.Private },
		new ServerTypeFilterOption { Display = "VIP / Reserved", Value = ServerType.Reserved }
	};

	public SortByOption SelectedSort
	{
		get => _selectedSort;
		set
		{
			if (_selectedSort != value)
			{
				_selectedSort = value;
				OnPropertyChanged("SelectedSort");
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
				OnPropertyChanged("SelectedServerTypeFilter");
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
		_activityWatcher = activityWatcher ?? throw new ArgumentNullException("activityWatcher");
		CloseWindowCommand = new RelayCommand(RequestClose);
		CopyDeeplinkCommand = new RelayCommand<ActivityData>(CopyDeeplinkToClipboard);
		LaunchDeeplinkCommand = new RelayCommand<ActivityData>(LaunchDeeplink);
		_selectedSort = SortOptions[0];
		_selectedServerTypeFilter = ServerTypeFilters[0];
		LoadHistoryFromFile();
		_activityWatcher.OnGameLeave += OnGameLeave;
		_ = LoadDataAsync(_lifetimeCts.Token);
	}

	private async void OnGameLeave(object? sender, EventArgs e)
	{
		await LoadDataAsync(_lifetimeCts.Token);
	}

	private void LoadHistoryFromFile()
	{
		try
		{
			if (File.Exists(_historyFilePath))
			{
				FileInfo file = new FileInfo(_historyFilePath);
				if (file.Length <= 0 || file.Length > MaxHistoryFileBytes)
					return;
				List<ActivityData> list = JsonFile.Deserialize<List<ActivityData>>(_historyFilePath, JsonOptions.Tolerant, MaxHistoryFileBytes).Where(HistoryPersister.IsWithinDesktopRetention).ToList();
				if (list != null && list.Count != 0)
				{
					if (list.Count > MaxLoadedHistoryEntries)
						list = list.Take(MaxLoadedHistoryEntries).ToList();
					MergeAndConsolidateHistory(list);
					NotifyHistoryChanged();
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerHistoryViewModel::LoadHistoryFromFile", ex);
		}
	}

	private async Task LoadDataAsync(CancellationToken token)
	{
		bool gateHeld = false;
		try
		{
			await _loadGate.WaitAsync(token);
			gateHeld = true;
			token.ThrowIfCancellationRequested();
			SetLoadingState();
			List<ActivityData> history = _activityWatcher.History.ToList();
			try
			{
				await UniverseDetails.FetchForEntriesAsync(GameHistory.Concat(history), token);
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
				MergeAndConsolidateHistory(history);
				foreach (ActivityData item in GameHistory)
					item.ComputeDisplayTimes();
			}, DispatcherPriority.Background, token);
			token.ThrowIfCancellationRequested();
			await Task.Run(delegate
			{
				SaveHistoryToFile();
			}, token);
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

	private void MergeAndConsolidateHistory(IEnumerable<ActivityData> incoming)
	{
		Dictionary<string, ActivityData> dictionary = GameHistory.Where(HistoryPersister.IsWithinDesktopRetention).ToDictionary((ActivityData x) => $"{x.PlaceId}_{x.JobId}", (ActivityData x) => x);
		foreach (ActivityData item in incoming.Where(HistoryPersister.IsWithinDesktopRetention))
		{
			string key = $"{item.PlaceId}_{item.JobId}";
			if (dictionary.TryGetValue(key, out var value))
			{
				if (item.TimeJoined != default && value.TimeJoined > item.TimeJoined)
				{
					value.TimeJoined = item.TimeJoined;
				}
				if (item.TimeLeft.HasValue && (!value.TimeLeft.HasValue || value.TimeLeft.Value < item.TimeLeft.Value))
				{
					value.TimeLeft = item.TimeLeft;
				}
				if (value.RootActivity == null && item.RootActivity != null)
				{
					value.RootActivity = item.RootActivity;
				}
				if (value.UniverseDetails == null && item.UniverseDetails != null)
				{
					value.UniverseDetails = item.UniverseDetails;
				}
				foreach (KeyValuePair<int, ActivityData.UserLog> playerLog in item.PlayerLogs)
				{
					value.PlayerLogs[playerLog.Key] = playerLog.Value;
				}
				foreach (KeyValuePair<int, ActivityData.UserMessage> messageLog in item.MessageLogs)
				{
					value.MessageLogs[messageLog.Key] = messageLog.Value;
				}
			}
			else
			{
				dictionary[key] = item;
			}
		}
		GameHistory = dictionary.Values.Where(HistoryPersister.IsWithinDesktopRetention).OrderByDescending((ActivityData x) => x.TimeJoined).Take(MaxHistoryEntries).ToList();
	}

	private void SaveHistoryToFile()
	{
		try
		{
			Directory.CreateDirectory(Paths.Data);
			JsonFile.SerializeAtomic(_historyFilePath, GameHistory, JsonOptions.Indented);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerHistoryViewModel::SaveHistoryToFile", ex);
		}
	}

	private void LaunchDeeplink(ActivityData? data)
	{
		if (data == null || data.PlaceId == 0L)
		{
			return;
		}
		try
		{
			string voidstrapPath = Paths.Process;
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = voidstrapPath,
				UseShellExecute = false,
				CreateNoWindow = true,
				WorkingDirectory = Path.GetDirectoryName(voidstrapPath) ?? ""
			};
			startInfo.ArgumentList.Add("-player");
			startInfo.ArgumentList.Add(data.GetNativeJoinUri());
			Process.Start(startInfo);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerHistoryViewModel::LaunchDeeplink", ex);
		}
	}

	private void CopyDeeplinkToClipboard(ActivityData? data)
	{
		if (data == null)
		{
			return;
		}
		try
		{
			Clipboard.SetText(data.GetInviteDeeplink());
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("ServerHistoryViewModel::CopyDeeplinkToClipboard", ex);
		}
	}

	private void NotifyHistoryChanged()
	{
		OnPropertyChanged("GameHistory");
		OnPropertyChanged("Top10RecentHistory");
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
			ServerHistorySortBy.MostPlayed => source.OrderByDescending((ActivityData x) => (x.TimeLeft ?? DateTime.Now) - x.TimeJoined),
			ServerHistorySortBy.NameAZ => source.OrderBy((ActivityData x) => x.GameName),
			ServerHistorySortBy.NameZA => source.OrderByDescending((ActivityData x) => x.GameName),
			_ => source.OrderByDescending((ActivityData x) => x.TimeJoined),
		};
		FilteredGameHistory = source.ToList();
		OnPropertyChanged("FilteredGameHistory");
	}

	private void SetLoadingState()
	{
		RunOnUi(delegate
		{
			LoadState = GenericTriState.Unknown;
			OnPropertyChanged("LoadState");
		});
	}

	private void SetSuccessState()
	{
		RunOnUi(delegate
		{
			LoadState = GenericTriState.Successful;
			OnPropertyChanged("LoadState");
		});
	}

	private void HandleError(Exception ex)
	{
		App.Logger.WriteException("ServerHistoryViewModel::HandleError", ex);
		RunOnUi(delegate
		{
			Error = "Failed to load history: " + ex.Message;
			LoadState = GenericTriState.Failed;
			OnPropertyChanged("Error");
			OnPropertyChanged("LoadState");
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

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_activityWatcher.OnGameLeave -= OnGameLeave;
		_lifetimeCts.Cancel();
		_lifetimeCts.Dispose();
		GameHistory.Clear();
		FilteredGameHistory.Clear();
		RequestCloseEvent = null;
		GC.SuppressFinalize(this);
	}
}
