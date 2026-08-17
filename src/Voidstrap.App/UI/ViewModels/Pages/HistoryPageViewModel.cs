using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.AppData;
using Voidstrap.Enums;
using Voidstrap.Integrations;
using Voidstrap.Models.Entities;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Pages;

internal class HistoryPageViewModel : NotifyPropertyChangedViewModel
{
	private readonly string _historyFilePath = Paths.ServerHistory;

	private const int MaxHistoryEntries = 50;

	private readonly ObservableCollection<HistoryGameEntry> _gameHistory = new ObservableCollection<HistoryGameEntry>();

	private GenericTriState _loadState = GenericTriState.Unknown;

	private string _error = string.Empty;

	private ICollectionView? _filteredHistory;

	private string _searchText = "";

	public ObservableCollection<DatacenterOption> DatacenterOptions { get; } = new ObservableCollection<DatacenterOption>();

	public ObservableCollection<HistoryGameEntry> GameHistory => _gameHistory;

	public bool IsEmpty => _gameHistory.Count == 0;

	public bool IsFilteredEmpty
	{
		get
		{
			if (_gameHistory.Count > 0)
			{
				return ((IEnumerable)FilteredHistory).Cast<object>().Count() == 0;
			}
			return false;
		}
	}

	public ICollectionView FilteredHistory
	{
		get
		{
			if (_filteredHistory == null)
			{
				_filteredHistory = CollectionViewSource.GetDefaultView(_gameHistory);
				_filteredHistory.Filter = delegate(object obj)
				{
					if (string.IsNullOrWhiteSpace(_searchText))
					{
						return true;
					}
					if (!(obj is HistoryGameEntry historyGameEntry))
					{
						return false;
					}
					string value = _searchText.Trim();
					return historyGameEntry.Name.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0 || historyGameEntry.CreatorName.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0 || historyGameEntry.PlaceId.ToString().IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
				};
			}
			return _filteredHistory;
		}
	}

	public string SearchText
	{
		get
		{
			return _searchText;
		}
		set
		{
			if (!(_searchText == value))
			{
				_searchText = value ?? "";
				OnPropertyChanged("SearchText");
				try
				{
					FilteredHistory.Refresh();
				}
				catch
				{
				}
				OnPropertyChanged("IsFilteredEmpty");
			}
		}
	}

	public GenericTriState LoadState
	{
		get
		{
			return _loadState;
		}
		private set
		{
			_loadState = value;
			OnPropertyChanged("LoadState");
		}
	}

	public string Error
	{
		get
		{
			return _error;
		}
		private set
		{
			_error = value;
			OnPropertyChanged("Error");
		}
	}

	public ICommand RefreshCommand { get; }

	public ICommand ClearCommand { get; }

	public ICommand LaunchCommand { get; }

	public ICommand CopyLinkCommand { get; }

	public HistoryPageViewModel()
	{
		RefreshCommand = new AsyncRelayCommand(LoadAsync);
		ClearCommand = new RelayCommand(ClearHistory);
		LaunchCommand = new RelayCommand<HistoryGameEntry>(LaunchGame);
		CopyLinkCommand = new RelayCommand<HistoryGameEntry>(CopyDeeplink);
		LoadDatacenterOptions();
		LoadAsync();
	}

	private void LoadDatacenterOptions()
	{
		try
		{
			DatacenterOptions.Clear();
			DatacenterOptions.Add(new DatacenterOption
			{
				Key = "",
				Display = "Preferred Server (Auto)"
			});
			Dictionary<string, LearnedServerEntry> dictionary = new Dictionary<string, LearnedServerEntry>(StringComparer.OrdinalIgnoreCase);
			foreach (LearnedServerEntry item in ServerFetchStore.AllEntries())
			{
				if (!string.IsNullOrWhiteSpace(item.City) && (item.Lat != 0.0 || item.Lon != 0.0))
				{
					string key = item.City + "|" + item.Country;
					if (!dictionary.TryGetValue(key, out var value) || item.SeenCount > value.SeenCount)
					{
						dictionary[key] = item;
					}
				}
			}
			foreach (LearnedServerEntry item2 in dictionary.Values.OrderBy<LearnedServerEntry, string>((LearnedServerEntry x) => x.Country, StringComparer.OrdinalIgnoreCase).ThenBy<LearnedServerEntry, string>((LearnedServerEntry x) => x.City, StringComparer.OrdinalIgnoreCase))
			{
				DatacenterOptions.Add(new DatacenterOption
				{
					Key = item2.City + "|" + item2.Country,
					Display = item2.City + ", " + item2.Country
				});
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPageViewModel::LoadDatacenterOptions", ex);
		}
	}

	public async Task LoadAsync()
	{
		Error = string.Empty;
		try
		{
			List<ActivityData> entries = await Task.Run(() => ReadFromFile());
			((DispatcherObject)Application.Current).Dispatcher.Invoke((Action)delegate
			{
				_gameHistory.Clear();
				foreach (ActivityData item in entries)
				{
					_gameHistory.Add(new HistoryGameEntry(item, DatacenterOptions));
				}
				NotifyCollectionsChanged();
			});
			if (entries.Count == 0)
			{
				LoadState = GenericTriState.Successful;
				return;
			}
			try
			{
				await UniverseDetails.FetchForEntriesAsync(entries);
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("HistoryPageViewModel::FetchForEntries", ex);
			}
			((DispatcherObject)Application.Current).Dispatcher.Invoke((Action)delegate
			{
				foreach (HistoryGameEntry item2 in _gameHistory)
				{
					if (item2.Data.UniverseDetails == null)
					{
						item2.Data.UniverseDetails = UniverseDetails.LoadFromCache(item2.UniverseId);
					}
					item2.RefreshDetails();
				}
				NotifyCollectionsChanged();
			});
			await FetchVotesAndPlayingAsync();
			LoadState = GenericTriState.Successful;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPageViewModel::LoadAsync", ex);
			Error = "Failed to load history: " + ex.Message;
			LoadState = GenericTriState.Failed;
		}
	}

	private void NotifyCollectionsChanged()
	{
		OnPropertyChanged("GameHistory");
		OnPropertyChanged("IsEmpty");
		OnPropertyChanged("IsFilteredEmpty");
		try
		{
			FilteredHistory.Refresh();
		}
		catch
		{
		}
	}

	private async Task FetchVotesAndPlayingAsync()
	{
		List<HistoryGameEntry> source;
		try
		{
			source = ((DispatcherObject)Application.Current).Dispatcher.Invoke<List<HistoryGameEntry>>((Func<List<HistoryGameEntry>>)(() => _gameHistory.ToList()));
		}
		catch
		{
			return;
		}
		List<long> list = (from x in source
			where x.UniverseId != 0
			select x.UniverseId).Distinct().ToList();
		if (list.Count == 0)
		{
			return;
		}
		Dictionary<long, string> likeById = new Dictionary<long, string>();
		try
		{
			string requestUri = "https://games.roblox.com/v1/games/votes?universeIds=" + string.Join(',', list);
			using JsonDocument jsonDocument = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString(requestUri));
			if (jsonDocument.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in value.EnumerateArray())
				{
					if (item.TryGetProperty("id", out var value2))
					{
						long @int = value2.GetInt64();
						JsonElement value3;
						long num = (item.TryGetProperty("upVotes", out value3) ? value3.GetInt64() : 0);
						JsonElement value4;
						long num2 = (item.TryGetProperty("downVotes", out value4) ? value4.GetInt64() : 0);
						long num3 = num + num2;
						likeById[@int] = ((num3 > 0) ? $"{Math.Round((double)num * 100.0 / (double)num3):0}%" : "--");
					}
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPageViewModel::FetchVotes", ex);
		}
		try
		{
			await ((DispatcherObject)Application.Current).Dispatcher.InvokeAsync((Action)delegate
			{
				foreach (HistoryGameEntry item2 in _gameHistory)
				{
					if (likeById.TryGetValue(item2.UniverseId, out string value5))
					{
						item2.LikePercent = value5;
					}
					long valueOrDefault = (item2.Data.UniverseDetails?.Data?.Playing).GetValueOrDefault();
					item2.PlayerCount = ((valueOrDefault > 0) ? FormatCount(valueOrDefault) : "--");
				}
			});
		}
		catch
		{
		}
	}

	private static string FormatCount(long count)
	{
		if (count >= 1000000)
		{
			return $"{(double)count / 1000000.0:0.#}M";
		}
		if (count >= 1000)
		{
			return $"{(double)count / 1000.0:0.#}K";
		}
		return count.ToString();
	}

	private List<ActivityData> ReadFromFile()
	{
		try
		{
			if (!File.Exists(_historyFilePath))
			{
				App.Logger.WriteLine("HistoryPageViewModel::ReadFromFile", "File does not exist");
				return new List<ActivityData>();
			}
			JsonSerializerOptions options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};
			List<ActivityData> list = (from x in JsonFile.Deserialize<List<ActivityData>>(_historyFilePath, options, 16777216).Where(HistoryPersister.IsWithinDesktopRetention)
				orderby x.TimeJoined descending
				group x by ((x.UniverseId != 0) ? x.UniverseId : -Math.Abs(x.PlaceId)) into g
				select g.First()).Take(50).ToList();
			foreach (ActivityData item in list)
			{
				item.ComputeDisplayTimes();
			}
			return list;
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPageViewModel::ReadFromFile", ex);
			return new List<ActivityData>();
		}
	}

	private void ClearHistory()
	{
		try
		{
			if (File.Exists(_historyFilePath))
			{
				File.Delete(_historyFilePath);
			}
			_gameHistory.Clear();
			NotifyCollectionsChanged();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPageViewModel::ClearHistory", ex);
		}
	}

	private void LaunchGame(HistoryGameEntry? entry)
	{
		if (entry == null || entry.PlaceId == 0L)
		{
			return;
		}
		try
		{
			string text = $"roblox://experiences/start?placeId={entry.PlaceId}";
			string process = Paths.Process;
			if (!string.IsNullOrEmpty(process))
			{
				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = process,
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = (Path.GetDirectoryName(process) ?? "")
				};
				startInfo.ArgumentList.Add("-player");
				startInfo.ArgumentList.Add(text);
				Process.Start(startInfo);
			}
			else
			{
				string executablePath = new RobloxPlayerData().ExecutablePath;
				ProcessStartInfo startInfo = new ProcessStartInfo
				{
					FileName = executablePath,
					UseShellExecute = false,
					WorkingDirectory = (Path.GetDirectoryName(executablePath) ?? "")
				};
				startInfo.ArgumentList.Add(text);
				Process.Start(startInfo);
			}
			Application.Current.Shutdown();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPageViewModel::LaunchGame", ex);
		}
	}

	private void CopyDeeplink(HistoryGameEntry? entry)
	{
		if (entry == null)
		{
			return;
		}
		try
		{
			Clipboard.SetText($"https://www.roblox.com/games/{entry.PlaceId}");
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("HistoryPageViewModel::CopyDeeplink", ex);
		}
	}
}
