using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Enums;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Models.Persistable;

namespace Voidstrap.UI.ViewModels.Settings;

public class BehaviourViewModel : NotifyPropertyChangedViewModel
{
	public sealed class DatacenterItem : NotifyPropertyChangedViewModel
	{
		private readonly Action<DatacenterItem> _onChange;

		private bool _isBlocked;

		private string _pingDisplay;

		private double _distanceKm = -1.0;

		public string City { get; }

		public string Region { get; }

		public string Country { get; }

		public double Lat { get; }

		public double Lon { get; }

		public string ServerIps { get; }

		public string PingIp { get; }

		public string Key => City + "|" + Country;

		public string Location => string.IsNullOrWhiteSpace(Region) || string.Equals(Region, City, StringComparison.OrdinalIgnoreCase)
			? City + ", " + Country
			: City + ", " + Region + ", " + Country;

		public double DistanceKm
		{
			get
			{
				return _distanceKm;
			}
			set
			{
				if (_distanceKm != value)
				{
					_distanceKm = value;
					OnPropertyChanged("DistanceKm");
					OnPropertyChanged("DistanceDisplay");
				}
			}
		}

		public string DistanceDisplay => _distanceKm < 0.0 ? "" : $"{(int)_distanceKm} km away";

		public string PingDisplay
		{
			get
			{
				return _pingDisplay;
			}
			set
			{
				if (_pingDisplay != value)
				{
					_pingDisplay = value;
					OnPropertyChanged("PingDisplay");
				}
			}
		}

		public bool IsAllowed
		{
			get
			{
				return !_isBlocked;
			}
			set
			{
				if (_isBlocked == !value)
					return;
				_isBlocked = !value;
				OnPropertyChanged("IsAllowed");
				OnPropertyChanged("IsBlocked");
				_onChange(this);
			}
		}

		public bool IsBlocked => _isBlocked;

		public DatacenterItem(string city, string region, string country, double lat, double lon, IReadOnlyList<string> serverIps, IReadOnlyList<string> cidrRanges, bool isBlocked, Action<DatacenterItem> onChange)
		{
			City = city;
			Region = region;
			Country = country;
			Lat = lat;
			Lon = lon;
			ServerIps = serverIps.Count > 0 ? string.Join(", ", serverIps) : string.Join(", ", cidrRanges);
			PingIp = serverIps.Count > 0 ? serverIps[0] : "";
			_pingDisplay = "...";
			_isBlocked = isBlocked;
			_onChange = onChange;
		}
	}

	public sealed class MatchmakerModeItem
	{
		public int Value { get; init; }

		public string Display { get; init; } = "";
	}

	public sealed class GamejoinApiItem
	{
		public int Value { get; init; }
		public string Display { get; init; } = "";
	}

	private static readonly ObservableCollection<GamejoinApiItem> _gamejoinApiOptions = new()
	{
		new GamejoinApiItem { Value = 1, Display = "V1 (stable)" },
		new GamejoinApiItem { Value = 2, Display = "V2 (newer)" },
	};

	public ObservableCollection<GamejoinApiItem> GamejoinApiOptions => _gamejoinApiOptions;

	public int GamejoinApiVersion
	{
		get
		{
			return App.Settings.Prop.VoidstrapMatchmakerGamejoinApiVersion;
		}
		set
		{
			if (App.Settings.Prop.VoidstrapMatchmakerGamejoinApiVersion != value)
			{
				App.Settings.Prop.VoidstrapMatchmakerGamejoinApiVersion = value;
				OnPropertyChanged("GamejoinApiVersion");
			}
		}
	}

	public sealed class PreferredDatacenterOption
	{
		public string Key { get; init; } = "";

		public string Display { get; init; } = "";

		public double DistanceKm { get; init; }

		public override string ToString()
		{
			return Display;
		}
	}

	private ICollectionView? _filteredDatacenters;

	private string _datacenterSearchText = "";

	private string _presetFetchStatus = "";

	private bool _isFetchingPreset;

	private PreferredDatacenterOption? _selectedPreferredDatacenter;

	private (double lat, double lon)? _userGeoForPreferred;

	private RobloxAccount? _account;

	private string _cpuModelName;

	private string _cpuSummary;

	private string _selectedCpuPriority = "Automatic";

	private List<string> CleanerItems;

	public ICommand CleanRobloxCacheCommand => new AsyncRelayCommand(CleanRobloxCacheAsync);

	public bool VoidstrapMatchmakerEnabled
	{
		get
		{
			return App.Settings.Prop.VoidstrapMatchmakerEnabled;
		}
		set
		{
			if (App.Settings.Prop.VoidstrapMatchmakerEnabled == value)
			{
				return;
			}
			if (value && !RobloxCookie.Exists)
			{
				if (Frontend.ShowMessageBox("Voidstrap can't find your Roblox login yet, so the matchmaker can't sync.\n\nClick Continue to launch Roblox and log in - it will sync automatically afterwards.\nClick Cancel to leave the matchmaker turned off.", MessageBoxImage.Exclamation, MessageBoxButton.OKCancel, MessageBoxResult.OK) == MessageBoxResult.OK)
				{
					App.Settings.Prop.VoidstrapMatchmakerEnabled = true;
					OnPropertyChanged("VoidstrapMatchmakerEnabled");
					RefreshLoginStatus();
					try
					{
						LaunchHandler.LaunchRoblox(LaunchMode.Player);
						return;
					}
					catch
					{
						return;
					}
				}
				App.Settings.Prop.VoidstrapMatchmakerEnabled = false;
				OnPropertyChanged("VoidstrapMatchmakerEnabled");
			}
			else
			{
				App.Settings.Prop.VoidstrapMatchmakerEnabled = value;
				OnPropertyChanged("VoidstrapMatchmakerEnabled");
				RefreshMatchmakerSummary();
			}
		}
	}



	private static readonly ObservableCollection<MatchmakerModeItem> _matchmakerModes = new()
	{
		new MatchmakerModeItem { Value = 0, Display = "Closest server" },
		new MatchmakerModeItem { Value = 1, Display = "Closest server that is nearly empty" },
		new MatchmakerModeItem { Value = 2, Display = "Always a specific datacenter" }
	};

	public ObservableCollection<MatchmakerModeItem> MatchmakerModes => _matchmakerModes;

	private bool _specificDatacenterMode = !string.IsNullOrWhiteSpace(App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter);

	public int MatchmakerMode
	{
		get
		{
			if (_specificDatacenterMode)
				return 2;
			return App.Settings.Prop.VoidstrapMatchmakerPreferEmpty ? 1 : 0;
		}
		set
		{
			if (MatchmakerMode == value)
				return;
			_specificDatacenterMode = value == 2;
			App.Settings.Prop.VoidstrapMatchmakerPreferEmpty = value == 1;
			if (value != 2)
			{
				App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter = "";
				_selectedPreferredDatacenter = null;
			}
			else if (string.IsNullOrWhiteSpace(App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter))
			{
				App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter = PreferredDatacenterOptions.FirstOrDefault()?.Key ?? "";
			}
			try
			{
				App.Settings.SaveDeferred();
			}
			catch
			{
			}
			OnPropertyChanged("MatchmakerMode");
			OnPropertyChanged("ShowPreferredDatacenterPicker");
			RefreshPreferredDatacenterOptions();
			RefreshMatchmakerSummary();
		}
	}

	public bool ShowPreferredDatacenterPicker => MatchmakerMode == 2;

	public string UserLocationText
	{
		get
		{
			if (_userGeoForPreferred == null)
				return "Finding your location...";
			DatacenterItem? nearest = Datacenters.Where(d => d.DistanceKm >= 0.0).OrderBy(d => d.DistanceKm).FirstOrDefault();
			if (nearest == null)
				return "Location found, learning which datacenters are near you.";
			return $"Nearest known datacenter: {nearest.Location}, about {Voidstrap.Integrations.VoidstrapMatchmaker.EstimatePingMs(nearest.DistanceKm)}ms";
		}
	}



	public int VoidstrapMatchmakerMaxCandidates
	{
		get
		{
			return App.Settings.Prop.VoidstrapMatchmakerMaxCandidates;
		}
		set
		{
			int num = Math.Clamp(value, Voidstrap.Integrations.VoidstrapMatchmaker.MinCandidateCount, Voidstrap.Integrations.VoidstrapMatchmaker.MaxCandidateCount);
			if (App.Settings.Prop.VoidstrapMatchmakerMaxCandidates != num)
			{
				App.Settings.Prop.VoidstrapMatchmakerMaxCandidates = num;
				OnPropertyChanged("VoidstrapMatchmakerMaxCandidates");
				RefreshMatchmakerAutoDetect();
			}
		}
	}



	public bool VoidstrapMatchmakerAutoCandidates
	{
		get
		{
			return App.Settings.Prop.VoidstrapMatchmakerAutoCandidates;
		}
		set
		{
			if (App.Settings.Prop.VoidstrapMatchmakerAutoCandidates != value)
			{
				App.Settings.Prop.VoidstrapMatchmakerAutoCandidates = value;
				OnPropertyChanged("VoidstrapMatchmakerAutoCandidates");
				OnPropertyChanged("VoidstrapMatchmakerManualCandidates");
				RefreshMatchmakerAutoDetect();
			}
		}
	}



	public bool VoidstrapMatchmakerManualCandidates => !VoidstrapMatchmakerAutoCandidates;

	public int RecommendedMatchmakerCandidates
	{
		get
		{
			int blocked = (App.Settings.Prop.VoidstrapMatchmakerDisabledDatacenters ?? new List<string>()).Count;
			return Math.Clamp(40 + blocked * 4, 40, Voidstrap.Integrations.VoidstrapMatchmaker.MaxCandidateCount);
		}
	}



	public int EffectiveMatchmakerCandidates => VoidstrapMatchmakerAutoCandidates ? RecommendedMatchmakerCandidates : VoidstrapMatchmakerMaxCandidates;

	public string SearchDepthDescription
	{
		get
		{
			int count = EffectiveMatchmakerCandidates;
			string speed = count <= 16 ? "fastest" : (count <= 32 ? "balanced" : "most thorough");
			return $"Checks up to {count} servers before joining, {speed}.";
		}
	}



	public ObservableCollection<DatacenterItem> Datacenters { get; } = new ObservableCollection<DatacenterItem>();

	public ICollectionView FilteredDatacenters
	{
		get
		{
			if (_filteredDatacenters == null)
			{
				_filteredDatacenters = CollectionViewSource.GetDefaultView(Datacenters);
				_filteredDatacenters.Filter = delegate(object obj)
				{
					if (string.IsNullOrWhiteSpace(_datacenterSearchText))
					{
						return true;
					}
					if (!(obj is DatacenterItem datacenterItem))
					{
						return false;
					}
					string value = _datacenterSearchText.Trim();
					string city = datacenterItem.City;
					if (city == null || city.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0)
					{
						string region = datacenterItem.Region;
						if (region == null || region.IndexOf(value, StringComparison.OrdinalIgnoreCase) < 0)
						{
							string country = datacenterItem.Country;
							if (country == null)
							{
								return false;
							}
							return country.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
						}
					}
					return true;
				};
			}
			return _filteredDatacenters;
		}
	}



	public string DatacenterSearchText
	{
		get
		{
			return _datacenterSearchText;
		}
		set
		{
			if (_datacenterSearchText == value)
			{
				return;
			}
			_datacenterSearchText = value ?? "";
			OnPropertyChanged("DatacenterSearchText");
			try
			{
				FilteredDatacenters.Refresh();
			}
			catch
			{
			}
		}
	}



	public string DatacenterPresetUrl
	{
		get
		{
			return App.Settings.Prop.VoidstrapMatchmakerPresetUrl ?? "";
		}
		set
		{
			string text = value ?? "";
			if (!(App.Settings.Prop.VoidstrapMatchmakerPresetUrl == text))
			{
				App.Settings.Prop.VoidstrapMatchmakerPresetUrl = text;
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
				OnPropertyChanged("DatacenterPresetUrl");
				if (!string.IsNullOrWhiteSpace(text))
				{
					FetchPresetAsync();
				}
			}
		}
	}



	public string PresetFetchStatus
	{
		get
		{
			return _presetFetchStatus;
		}
		set
		{
			_presetFetchStatus = value ?? "";
			OnPropertyChanged("PresetFetchStatus");
			OnPropertyChanged("HasPresetFetchStatus");
		}
	}



	public bool HasPresetFetchStatus => !string.IsNullOrEmpty(_presetFetchStatus);

	public bool IsFetchingPreset
	{
		get
		{
			return _isFetchingPreset;
		}
		set
		{
			_isFetchingPreset = value;
			OnPropertyChanged("IsFetchingPreset");
			OnPropertyChanged("CanFetchPreset");
		}
	}



	public bool CanFetchPreset
	{
		get
		{
			if (!_isFetchingPreset)
			{
				return !string.IsNullOrWhiteSpace(DatacenterPresetUrl);
			}
			return false;
		}
	}



	public ObservableCollection<PreferredDatacenterOption> PreferredDatacenterOptions { get; } = new ObservableCollection<PreferredDatacenterOption>();

	public PreferredDatacenterOption? SelectedPreferredDatacenter
	{
		get
		{
			return _selectedPreferredDatacenter;
		}
		set
		{
			if (_selectedPreferredDatacenter != value)
			{
				_selectedPreferredDatacenter = value;
				App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter = value?.Key ?? "";
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
				OnPropertyChanged("SelectedPreferredDatacenter");
				RefreshMatchmakerSummary();
			}
		}
	}



	public string VoidstrapMatchmakerLearnedStats
	{
		get
		{
			try
			{
				var (value, value2, value3, value4) = ServerFetchStore.GetStats();
				return $"{value} datacenters, {value2} unique servers, {value3} joins logged, {value4} pinged";
			}
			catch
			{
				return "";
			}
		}
	}



	public bool HasLearnedData
	{
		get
		{
			try
			{
				(int Datacenters, int Servers, int TotalSightings, int PingedDatacenters) stats = ServerFetchStore.GetStats();
				int item = stats.Servers;
				int item2 = stats.TotalSightings;
				int item3 = stats.PingedDatacenters;
				return item > 0 || item2 > 0 || item3 > 0;
			}
			catch
			{
				return false;
			}
		}
	}



	public string LoginStatusText
	{
		get
		{
			if (_account != null)
			{
				return $"Signed in as {_account.DisplayName} (@{_account.Username})";
			}
			if (!RobloxCookie.Exists)
			{
				return "No Roblox login found. Launch Roblox and log in once";
			}
			return "Roblox login found loading account details...";
		}
	}



	public bool IsSignedIn => RobloxCookie.Exists;

	public bool IsNotSignedIn => !IsSignedIn;

	public int ServerMatchmakerMaxRetries
	{
		get
		{
			return App.Settings.Prop.ServerMatchmakerMaxRetries;
		}
		set
		{
			int num = Math.Max(1, Math.Min(20, value));
			if (App.Settings.Prop.ServerMatchmakerMaxRetries != num)
			{
				App.Settings.Prop.ServerMatchmakerMaxRetries = num;
				OnPropertyChanged("ServerMatchmakerMaxRetries");
			}
		}
	}



	public string CpuModelName
	{
		get
		{
			return _cpuModelName;
		}
		set
		{
			_cpuModelName = value;
			OnPropertyChanged("CpuModelName");
		}
	}



	public string CpuSummary
	{
		get
		{
			return _cpuSummary;
		}
		set
		{
			_cpuSummary = value;
			OnPropertyChanged("CpuSummary");
		}
	}



	public ObservableCollection<string> CpuOptions { get; } = new ObservableCollection<string>();

	public string SelectedCpuPriority
	{
		get
		{
			return _selectedCpuPriority;
		}
		set
		{
			if (_selectedCpuPriority != value)
			{
				_selectedCpuPriority = value;
				OnPropertyChanged("SelectedCpuPriority");
				App.Settings.Prop.SelectedCpuPriority = value;
				App.Settings.SaveDeferred();
			}
		}
	}



	public bool disablecrashhandleryayyysocool
	{
		get
		{
			return App.Settings.Prop.DisableCrash;
		}
		set
		{
			if (App.Settings.Prop.DisableCrash != value)
			{
				App.Settings.Prop.DisableCrash = value;
				OnPropertyChanged("disablecrashhandleryayyysocool");
			}
		}
	}



	public bool ConfirmLaunches
	{
		get
		{
			return App.Settings.Prop.ConfirmLaunches;
		}
		set
		{
			App.Settings.Prop.ConfirmLaunches = value;
		}
	}



	public bool LaunchRobloxWebsite
	{
		get
		{
			return false;
		}
		set
		{
			OnPropertyChanged("LaunchRobloxWebsite");
		}
	}



	public bool IsBetterServersEnabled
	{
		get
		{
			return App.Settings.Prop.IsBetterServersEnabled;
		}
		set
		{
			App.Settings.Prop.IsBetterServersEnabled = value;
		}
	}



	public bool OverClockCPU
	{
		get
		{
			return App.Settings.Prop.OverClockCPU;
		}
		set
		{
			App.Settings.Prop.OverClockCPU = value;
		}
	}



	public bool IsGameEnabled
	{
		get
		{
			return App.Settings.Prop.IsGameEnabled;
		}
		set
		{
			App.Settings.Prop.IsGameEnabled = value;
		}
	}



	public bool OverClockGPU
	{
		get
		{
			return App.Settings.Prop.OverClockGPU;
		}
		set
		{
			App.Settings.Prop.OverClockGPU = value;
		}
	}



	public bool OptimizeRoblox
	{
		get
		{
			return App.Settings.Prop.OptimizeRoblox;
		}
		set
		{
			if (App.Settings.Prop.OptimizeRoblox != value)
			{
				App.Settings.Prop.OptimizeRoblox = value;
				OnPropertyChanged("OptimizeRoblox");
				App.Settings.SaveDeferred();
			}
		}
	}

	public bool BypassEmulationOverhead
	{
		get
		{
			return App.Settings.Prop.BypassEmulationOverhead;
		}
		set
		{
			if (App.Settings.Prop.BypassEmulationOverhead != value)
			{
				App.Settings.Prop.BypassEmulationOverhead = value;
				if (!value)
					Voidstrap.Utility.EmulationBypassService.RestoreCompatLayers();
				OnPropertyChanged("BypassEmulationOverhead");
				App.Settings.SaveDeferred();
			}
		}
	}

	public Visibility BypassEmulationOverheadVisibility => Voidstrap.Utility.Platform.IsWindows ? Visibility.Visible : Visibility.Collapsed;



	public bool ReduceMemoryOutOfFocus
	{
		get
		{
			return App.Settings.Prop.ReduceMemoryOutOfFocus;
		}
		set
		{
			if (App.Settings.Prop.ReduceMemoryOutOfFocus != value)
			{
				App.Settings.Prop.ReduceMemoryOutOfFocus = value;
				OnPropertyChanged("ReduceMemoryOutOfFocus");
				App.Settings.SaveDeferred();
			}
		}
	}


	public bool MultiAccount
	{
		get
		{
			return App.Settings.Prop.MultiAccount;
		}
		set
		{
			if (App.Settings.Prop.MultiAccount != value)
			{
				App.Settings.Prop.MultiAccount = value;
				OnPropertyChanged("MultiAccount");
				App.Settings.SaveDeferred();
			}
		}
	}



	public bool BackgroundWindow
	{
		get
		{
			return App.Settings.Prop.BackgroundWindow;
		}
		set
		{
			App.Settings.Prop.BackgroundWindow = value;
		}
	}



	public bool RenameClientToEurotrucks2
	{
		get
		{
			return App.Settings.Prop.RenameClientToEuroTrucks2;
		}
		set
		{
			App.Settings.Prop.RenameClientToEuroTrucks2 = value;
		}
	}



	public CleanerOptions SelectedCleanUpMode
	{
		get
		{
			return App.Settings.Prop.CleanerOptions;
		}
		set
		{
			App.Settings.Prop.CleanerOptions = value;
		}
	}



	public IEnumerable<CleanerOptions> CleanerOptions => CleanerOptionsEx.Selections;

	public CleanerOptions CleanerOption
	{
		get
		{
			return App.Settings.Prop.CleanerOptions;
		}
		set
		{
			App.Settings.Prop.CleanerOptions = value;
		}
	}



	public bool CleanerLogs
	{
		get
		{
			return CleanerItems.Contains("RobloxLogs");
		}
		set
		{
			if (value && !CleanerItems.Contains("RobloxLogs"))
			{
				CleanerItems.Add("RobloxLogs");
				UpdateCleanerItems();
			}
			else if (!value && CleanerItems.Contains("RobloxLogs"))
			{
				CleanerItems.Remove("RobloxLogs");
				UpdateCleanerItems();
			}
			OnPropertyChanged("CleanerLogs");
		}
	}



	public bool CleanerCache
	{
		get
		{
			return CleanerItems.Contains("RobloxCache");
		}
		set
		{
			if (value && !CleanerItems.Contains("RobloxCache"))
			{
				CleanerItems.Add("RobloxCache");
				UpdateCleanerItems();
			}
			else if (!value && CleanerItems.Contains("RobloxCache"))
			{
				CleanerItems.Remove("RobloxCache");
				UpdateCleanerItems();
			}
			OnPropertyChanged("CleanerCache");
		}
	}



	public bool CleanerVoidstrap
	{
		get
		{
			return CleanerItems.Contains("VoidstrapLogs");
		}
		set
		{
			if (value && !CleanerItems.Contains("VoidstrapLogs"))
			{
				CleanerItems.Add("VoidstrapLogs");
				UpdateCleanerItems();
			}
			else if (!value && CleanerItems.Contains("VoidstrapLogs"))
			{
				CleanerItems.Remove("VoidstrapLogs");
				UpdateCleanerItems();
			}
			OnPropertyChanged("CleanerVoidstrap");
		}
	}



	public BehaviourViewModel()
	{
		CleanerItems = new List<string>(App.Settings.Prop.CleanerDirectories);
		LoadCpuOptions();
		LoadDatacenters();
		LoadUserGeoForPreferredAsync();
		FetchPresetAsync();
		LoadAccountAsync();
		SelectedWebBackground = App.Settings.Prop.WebCustomBackgrounds.FirstOrDefault();
	}



	private async Task CleanRobloxCacheAsync()
	{
		List<Process> list = new List<Process>();
		if (!string.IsNullOrEmpty(App.State.Prop.Player.VersionGuid))
		{
			list.AddRange(Process.GetProcessesByName("RobloxPlayerBeta"));
		}
		if (App.IsStudioVisible)
		{
			list.AddRange(Process.GetProcessesByName("RobloxStudioBeta"));
		}
		if (list.Any())
		{
			Frontend.ShowMessageBox("Close Roblox before cleaning the cache.", MessageBoxImage.Hand);
			return;
		}
		string path = Path.Combine(Path.GetTempPath(), "Roblox");
		string path2 = Path.Combine(Paths.LocalAppData, "Roblox", "rbx-storage");
		string dbFile = Path.Combine(Paths.LocalAppData, "Roblox", "rbx-storage.db");
		List<string> dirs = new List<string>();
		try
		{
			if (Directory.Exists(path))
			{
				dirs.AddRange(Directory.GetDirectories(path));
			}
			if (Directory.Exists(path2))
			{
				dirs.AddRange(Directory.GetDirectories(path2));
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("BehaviourViewModel::CleanRobloxCache", ex);
		}
		bool hasDb = File.Exists(dbFile);
		if (dirs.Count == 0 && !hasDb)
		{
			Frontend.ShowMessageBox("There's nothing to clean.", MessageBoxImage.Asterisk);
			return;
		}
		int num = await Task.Run(delegate
		{
			int num2 = 0;
			if (hasDb && !TryDeleteFile(dbFile))
			{
				num2++;
			}
			foreach (string item in dirs)
			{
				num2 += DeleteDirectoryContents(item);
			}
			return num2;
		});
		if (num > 0)
		{
			App.Logger.WriteLine("BehaviourViewModel::CleanRobloxCache", $"Cleaned the cache; {num} item(s) were in use and skipped.");
			Frontend.ShowMessageBox($"Cleaned the Roblox cache.\n{num} file(s) were in use and skipped.", MessageBoxImage.Asterisk);
		}
		else
		{
			Frontend.ShowMessageBox("Successfully cleaned the Roblox cache.", MessageBoxImage.Asterisk);
		}
	}



	private static bool TryDeleteFile(string path)
	{
		try
		{
			FileAttributes attributes = File.GetAttributes(path);
			if ((attributes & FileAttributes.ReadOnly) != FileAttributes.None)
			{
				File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			}
			File.Delete(path);
			return true;
		}
		catch
		{
			return false;
		}
	}



	private static int DeleteDirectoryContents(string dir)
	{
		int num = 0;
		List<string> list;
		try
		{
			list = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
		}
		catch
		{
			return 1;
		}
		foreach (string item in list)
		{
			if (!TryDeleteFile(item))
			{
				num++;
			}
		}
		try
		{
			foreach (string item2 in from p in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories)
				orderby p.Length descending
				select p)
			{
				try
				{
					Directory.Delete(item2, recursive: false);
				}
				catch
				{
				}
			}
			Directory.Delete(dir, recursive: false);
		}
		catch
		{
		}
		return num;
	}



	private async Task LoadUserGeoForPreferredAsync()
	{
		_ = 1;
		try
		{
			(double, double)? userGeoForPreferred = await FetchUserGeoAsync().ConfigureAwait(continueOnCapturedContext: false);
			if (userGeoForPreferred.HasValue)
			{
				_userGeoForPreferred = userGeoForPreferred;
				await Application.Current.Dispatcher.InvokeAsync(delegate
				{
					ApplyDistances();
					RefreshPreferredDatacenterOptions();
					RefreshMatchmakerSummary();
				});
			}
		}
		catch
		{
		}
	}



	private static async Task<(double lat, double lon)?> FetchUserGeoAsync()
	{
		try
		{
			UserGeo geo = await VoidstrapMatchmaker.GetUserGeoAsync().ConfigureAwait(continueOnCapturedContext: false);
			if (geo == null)
			{
				return null;
			}
			return (geo.Lat, geo.Lon);
		}
		catch
		{
			return null;
		}
	}



	public void RefreshMatchmakerAutoDetect()
	{
		OnPropertyChanged("RecommendedMatchmakerCandidates");
		OnPropertyChanged("EffectiveMatchmakerCandidates");
		OnPropertyChanged("SearchDepthDescription");
	}

	public void RefreshMatchmakerSummary()
	{
		OnPropertyChanged("UserLocationText");
		OnPropertyChanged("BlockedDatacenterSummary");
	}

	public string BlockedDatacenterSummary
	{
		get
		{
			int blocked = Datacenters.Count(d => d.IsBlocked);
			if (Datacenters.Count == 0)
				return "No datacenters learned yet, they appear as you play.";
			return blocked == 0
				? $"{Datacenters.Count} datacenters known, all allowed."
				: $"{Datacenters.Count} datacenters known, {blocked} blocked.";
		}
	}



	private static void MigrateKickListIntoBlocklist()
	{
		try
		{
			List<string>? kick = App.Settings.Prop.VoidstrapMatchmakerKickDatacenters;
			if (kick == null || kick.Count == 0)
				return;
			AppSettings prop = App.Settings.Prop;
			List<string> blocked = prop.VoidstrapMatchmakerDisabledDatacenters ??= new List<string>();
			foreach (string key in kick)
			{
				if (!string.IsNullOrWhiteSpace(key) && !blocked.Contains(key, StringComparer.OrdinalIgnoreCase))
					blocked.Add(key);
			}
			kick.Clear();
			App.Settings.SaveDeferred();
		}
		catch
		{
		}
	}

	public void LoadDatacenters()
	{
		try
		{
			MigrateKickListIntoBlocklist();
			HashSet<string> hashSet = new HashSet<string>(App.Settings.Prop.VoidstrapMatchmakerDisabledDatacenters ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
			Datacenters.Clear();
			Dictionary<string, List<LearnedServerEntry>> dictionary = new Dictionary<string, List<LearnedServerEntry>>(StringComparer.OrdinalIgnoreCase);
			foreach (LearnedServerEntry item in ServerFetchStore.AllEntries())
			{
				if (!string.IsNullOrWhiteSpace(item.City) && (item.Lat != 0.0 || item.Lon != 0.0))
				{
					string key = item.City + "|" + item.Country;
					if (!dictionary.TryGetValue(key, out var value))
					{
						value = (dictionary[key] = new List<LearnedServerEntry>());
					}
					value.Add(item);
				}
			}
			foreach (KeyValuePair<string, List<LearnedServerEntry>> item2 in dictionary.OrderBy<KeyValuePair<string, List<LearnedServerEntry>>, string>((KeyValuePair<string, List<LearnedServerEntry>> x) => x.Value[0].Country, StringComparer.OrdinalIgnoreCase).ThenBy<KeyValuePair<string, List<LearnedServerEntry>>, string>((KeyValuePair<string, List<LearnedServerEntry>> x) => x.Value[0].City, StringComparer.OrdinalIgnoreCase))
			{
				List<LearnedServerEntry> value2 = item2.Value;
				LearnedServerEntry learnedServerEntry = value2.OrderByDescending((LearnedServerEntry x) => x.SeenCount).First();
				List<string> serverIps = value2.SelectMany(delegate(LearnedServerEntry x)
				{
					IEnumerable<string> iPs = x.IPs;
					return iPs ?? Enumerable.Empty<string>();
				}).Where(IsUsablePingIp).Distinct<string>(StringComparer.OrdinalIgnoreCase)
					.ToList();
				List<string> cidrRanges = (from x in value2
					select x.Cidr into x
					where !string.IsNullOrWhiteSpace(x)
					select x).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
				Datacenters.Add(new DatacenterItem(learnedServerEntry.City, learnedServerEntry.Region, learnedServerEntry.Country, learnedServerEntry.Lat, learnedServerEntry.Lon, serverIps, cidrRanges, hashSet.Contains(item2.Key), OnDatacenterToggled));
			}
			ApplyDistances();
			RefreshPreferredDatacenterOptions();
			RefreshMatchmakerSummary();
			MeasureDatacenterPingsAsync();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("BehaviourViewModel::LoadDatacenters", "Failed: " + ex.Message);
		}
	}

	private void ApplyDistances()
	{
		if (_userGeoForPreferred == null)
			return;
		foreach (DatacenterItem item in Datacenters)
		{
			item.DistanceKm = Voidstrap.Integrations.VoidstrapMatchmaker.HaversineKm(_userGeoForPreferred.Value.lat, _userGeoForPreferred.Value.lon, item.Lat, item.Lon);
			item.PingDisplay = $"{Voidstrap.Integrations.VoidstrapMatchmaker.EstimatePingMs(item.DistanceKm)} ms";
		}
		SortDatacentersByDistance();
	}

	private void SortDatacentersByDistance()
	{
		List<DatacenterItem> sorted = Datacenters.OrderBy(d => d.DistanceKm < 0.0 ? double.MaxValue : d.DistanceKm).ThenBy(d => d.City, StringComparer.OrdinalIgnoreCase).ToList();
		for (int i = 0; i < sorted.Count; i++)
		{
			int current = Datacenters.IndexOf(sorted[i]);
			if (current != i)
				Datacenters.Move(current, i);
		}
	}



	private static bool IsUsablePingIp(string? ip)
	{
		if (string.IsNullOrWhiteSpace(ip))
		{
			return false;
		}
		if (ip.Contains('/'))
		{
			return false;
		}
		if (!IPAddress.TryParse(ip, out IPAddress _))
		{
			return false;
		}
		return !VoidstrapMatchmaker.IsPrivateIp(ip);
	}






	public async Task FetchPresetAsync()
	{
		if (_isFetchingPreset)
		{
			return;
		}
		IsFetchingPreset = true;
		try
		{
			await Voidstrap.Utility.WebsiteGeoSync.PullAsync().ConfigureAwait(continueOnCapturedContext: true);
			LoadDatacenters();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("BehaviourViewModel::FetchPreset", "Datacenter fetch failed: " + ex.Message);
		}
		finally
		{
			IsFetchingPreset = false;
		}
	}



	public void ClearAllLearnedDatacenters()
	{
		try
		{
			int num = ServerFetchStore.PruneUnseenSeedEntries();
			PresetFetchStatus = ((num > 0) ? $"Cleared {num} unseen pre-seeded entries." : "No unseen pre-seeded entries to clear.");
			LoadDatacenters();
		}
		catch (Exception ex)
		{
			PresetFetchStatus = "Clear failed: " + ex.Message;
		}
	}



	public void RefreshPreferredDatacenterOptions()
	{
		try
		{
			HashSet<string> hashSet = new HashSet<string>(App.Settings.Prop.VoidstrapMatchmakerDisabledDatacenters ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
			List<PreferredDatacenterOption> list = new List<PreferredDatacenterOption>();
			Dictionary<string, LearnedServerEntry> dictionary = new Dictionary<string, LearnedServerEntry>(StringComparer.OrdinalIgnoreCase);
			foreach (LearnedServerEntry item in ServerFetchStore.AllEntries())
			{
				if (!string.IsNullOrWhiteSpace(item.City) && (item.Lat != 0.0 || item.Lon != 0.0))
				{
					string text = item.City + "|" + item.Country;
					if (!hashSet.Contains(text) && (!dictionary.TryGetValue(text, out var value) || item.SeenCount > value.SeenCount))
					{
						dictionary[text] = item;
					}
				}
			}
			List<PreferredDatacenterOption> collection = (from e in dictionary.Values
				let km = (_userGeoForPreferred.HasValue ? VoidstrapMatchmaker.HaversineKm(_userGeoForPreferred.Value.lat, _userGeoForPreferred.Value.lon, e.Lat, e.Lon) : double.PositiveInfinity)
				select new PreferredDatacenterOption
				{
					Key = e.City + "|" + e.Country,
					Display = double.IsPositiveInfinity(km) ? e.City + ", " + e.Country : $"{e.City}, {e.Country} ({VoidstrapMatchmaker.EstimatePingMs(km)}ms)",
					DistanceKm = km
				} into o
				orderby o.DistanceKm, o.Display
				select o).ToList();
			list.AddRange(collection);
			PreferredDatacenterOptions.Clear();
			foreach (PreferredDatacenterOption item2 in list)
			{
				PreferredDatacenterOptions.Add(item2);
			}
			string savedKey = App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter ?? "";
			PreferredDatacenterOption? match = PreferredDatacenterOptions.FirstOrDefault(o => string.Equals(o.Key, savedKey, StringComparison.OrdinalIgnoreCase));
			_selectedPreferredDatacenter = match ?? (_specificDatacenterMode ? PreferredDatacenterOptions.FirstOrDefault() : null);
			string resolvedKey = _selectedPreferredDatacenter?.Key ?? "";
			if (!string.Equals(resolvedKey, savedKey, StringComparison.OrdinalIgnoreCase))
			{
				App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter = resolvedKey;
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
			}
			OnPropertyChanged("SelectedPreferredDatacenter");
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("BehaviourViewModel::RefreshPreferredDatacenterOptions", "Failed: " + ex.Message);
		}
	}



	private void OnDatacenterToggled(DatacenterItem item)
	{
		string key = item.Key;
		AppSettings prop = App.Settings.Prop;
		List<string> blocked = prop.VoidstrapMatchmakerDisabledDatacenters ??= new List<string>();
		bool listed = blocked.Contains(key, StringComparer.OrdinalIgnoreCase);
		if (item.IsBlocked && !listed)
			blocked.Add(key);
		else if (!item.IsBlocked && listed)
			blocked.RemoveAll(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
		try
		{
			App.Settings.SaveDeferred();
		}
		catch
		{
		}
		RefreshMatchmakerAutoDetect();
		RefreshPreferredDatacenterOptions();
		RefreshMatchmakerSummary();
	}



	private async Task MeasureDatacenterPingsAsync()
	{
		List<DatacenterItem> pingable = Datacenters.Where(d => !string.IsNullOrEmpty(d.PingIp)).ToList();
		if (pingable.Count == 0)
			return;
		using SemaphoreSlim throttler = new SemaphoreSlim(8);
		IEnumerable<Task> tasks = pingable.Select(async item =>
		{
			await throttler.WaitAsync().ConfigureAwait(false);
			try
			{
				int ms = await MeasureTcpPingAsync(item.PingIp).ConfigureAwait(false);
				if (ms < 0)
					return;
				string text = $"{ms} ms";
				await Application.Current.Dispatcher.InvokeAsync(() => item.PingDisplay = text);
			}
			catch
			{
			}
			finally
			{
				throttler.Release();
			}
		});
		try
		{
			await Task.WhenAll(tasks).ConfigureAwait(false);
		}
		catch
		{
		}
	}



	private static async Task<int> MeasureTcpPingAsync(string ip)
	{
		if (!IPAddress.TryParse(ip, out IPAddress addr))
		{
			return -1;
		}
		int best = -1;
		int[] array = new int[2] { 443, 80 };
		foreach (int port in array)
		{
			int num = await TcpConnectRttAsync(addr, port, 700).ConfigureAwait(continueOnCapturedContext: false);
			if (num >= 0 && (best < 0 || num < best))
			{
				best = num;
			}
		}
		return best;
	}



	private static async Task<int> TcpConnectRttAsync(IPAddress addr, int port, int timeoutMs)
	{
		using Socket socket = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
		using CancellationTokenSource cts = new CancellationTokenSource(timeoutMs);
		Stopwatch sw = Stopwatch.StartNew();
		try
		{
			await socket.ConnectAsync(addr, port, cts.Token).ConfigureAwait(continueOnCapturedContext: false);
			sw.Stop();
			return (int)sw.ElapsedMilliseconds;
		}
		catch (SocketException ex)
		{
			sw.Stop();
			if (ex.SocketErrorCode == SocketError.ConnectionRefused)
			{
				return (int)sw.ElapsedMilliseconds;
			}
			return -1;
		}
		catch (OperationCanceledException)
		{
			return -1;
		}
		catch
		{
			return -1;
		}
	}



	public void ResetDatacenters()
	{
		App.Settings.Prop.VoidstrapMatchmakerDisabledDatacenters?.Clear();
		App.Settings.Prop.VoidstrapMatchmakerKickDatacenters?.Clear();
		foreach (DatacenterItem datacenter in Datacenters)
			datacenter.IsAllowed = true;
		try
		{
			App.Settings.SaveDeferred();
		}
		catch
		{
		}
		RefreshMatchmakerAutoDetect();
		RefreshPreferredDatacenterOptions();
		RefreshMatchmakerSummary();
	}



	public void RefreshLearnedStats()
	{
		OnPropertyChanged("VoidstrapMatchmakerLearnedStats");
		OnPropertyChanged("HasLearnedData");
	}



	public void RefreshLoginStatus()
	{
		RobloxCookie.InvalidateCache();
		OnPropertyChanged("LoginStatusText");
		OnPropertyChanged("IsSignedIn");
		OnPropertyChanged("IsNotSignedIn");
		LoadAccountAsync();
	}



	private async Task LoadAccountAsync()
	{
		try
		{
			_account = await RobloxCookie.GetAccountAsync();
		}
		catch
		{
			_account = null;
		}
		OnPropertyChanged("LoginStatusText");
	}



	private void LoadCpuOptions()
	{
		try
		{
			CpuOptions.Clear();
			int processorCount = Environment.ProcessorCount;
			int physicalCoreCount = GetPhysicalCoreCount();
			string text = Voidstrap.Utility.CpuInfo.GetModelName() ?? "Unknown CPU";
			if (Voidstrap.Utility.Platform.IsWindows)
			{
				try
				{
					using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("select Name from Win32_Processor");
					using ManagementObjectCollection.ManagementObjectEnumerator managementObjectEnumerator = managementObjectSearcher.Get().GetEnumerator();
					if (managementObjectEnumerator.MoveNext())
					{
						text = managementObjectEnumerator.Current["Name"]?.ToString()?.Trim() ?? "Unknown CPU";
					}
				}
				catch
				{
				}
			}
			CpuModelName = text;
			CpuSummary = $"{text}, {physicalCoreCount} physical cores, {processorCount} logical processors. Automatic uses every available processor.";
			if (processorCount > IntPtr.Size * 8)
			{
				CpuSummary += " Manual limits are unavailable because this system uses processor groups.";
			}
			CpuOptions.Add("Automatic");
			if (processorCount <= IntPtr.Size * 8)
			{
				for (int i = 1; i <= processorCount; i++)
				{
					CpuOptions.Add($"{i} Core{((i > 1) ? "s" : "")}");
				}
			}
			if (string.IsNullOrWhiteSpace(App.Settings.Prop.SelectedCpuPriority) || !CpuOptions.Contains(App.Settings.Prop.SelectedCpuPriority))
			{
				App.Settings.Prop.SelectedCpuPriority = "Automatic";
				App.Settings.SaveDeferred();
			}
			_selectedCpuPriority = App.Settings.Prop.SelectedCpuPriority;
			OnPropertyChanged("SelectedCpuPriority");
			App.Settings.Prop.TotalLogicalCores = processorCount;
			App.Settings.Prop.TotalPhysicalCores = physicalCoreCount;
		}
		catch
		{
			CpuOptions.Clear();
			CpuOptions.Add("Automatic");
			_selectedCpuPriority = "Automatic";
			CpuModelName = "Unknown CPU";
			CpuSummary = "CPU information unavailable. Automatic uses every available processor.";
			OnPropertyChanged("SelectedCpuPriority");
		}
	}



	private int GetPhysicalCoreCount()
	{
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			int cores = Voidstrap.Utility.CpuInfo.GetPhysicalCoreCount();
			return (cores > 0) ? cores : Environment.ProcessorCount;
		}
		try
		{
			using ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("select NumberOfCores from Win32_Processor");
			int num = 0;
			foreach (ManagementBaseObject item in managementObjectSearcher.Get())
			{
				num += Convert.ToInt32(item["NumberOfCores"]);
			}
			return (num > 0) ? num : Environment.ProcessorCount;
		}
		catch
		{
			return Environment.ProcessorCount;
		}
	}



	private void UpdateCleanerItems()
	{
		App.Settings.Prop.CleanerDirectories = new List<string>(CleanerItems);
	}



	public sealed class FullscreenModeItem
	{
		public int Value { get; init; }

		public string Display { get; init; } = "";
	}

	private static readonly ObservableCollection<FullscreenModeItem> _fullscreenModes = new()
	{
		new FullscreenModeItem { Value = 0, Display = "Normal window" },
		new FullscreenModeItem { Value = 1, Display = "Borderless fullscreen" },
		new FullscreenModeItem { Value = 2, Display = "Exclusive fullscreen" }
	};

	public ObservableCollection<FullscreenModeItem> FullscreenModes => _fullscreenModes;

	public int RobloxFullscreenMode
	{
		get
		{
			if (App.Settings.Prop.FakeExclusiveFullscreen)
				return 2;
			return App.Settings.Prop.FakeBorderlessFullscreen ? 1 : 0;
		}
		set
		{
			if (RobloxFullscreenMode == value)
				return;
			if (value == 2)
			{
				FakeExclusiveFullscreen = true;
				if (!App.Settings.Prop.FakeExclusiveFullscreen)
				{
					OnPropertyChanged("RobloxFullscreenMode");
					OnPropertyChanged("ShowExclusiveFullscreenWarning");
					return;
				}
				FakeBorderlessFullscreen = false;
			}
			else
			{
				FakeExclusiveFullscreen = false;
				FakeBorderlessFullscreen = value == 1;
			}
			OnPropertyChanged("RobloxFullscreenMode");
			OnPropertyChanged("ShowExclusiveFullscreenWarning");
		}
	}

	public bool ShowExclusiveFullscreenWarning => RobloxFullscreenMode == 2;

	public bool FakeBorderlessFullscreen
	{
		get
		{
			return App.Settings.Prop.FakeBorderlessFullscreen;
		}
		set
		{
			if (App.Settings.Prop.FakeBorderlessFullscreen != value)
			{
				App.Settings.Prop.FakeBorderlessFullscreen = value;
				OnPropertyChanged("FakeBorderlessFullscreen");
			}
		}
	}

	public bool FakeExclusiveFullscreen
	{
		get
		{
			return App.Settings.Prop.FakeExclusiveFullscreen;
		}
		set
		{
			if (App.Settings.Prop.FakeExclusiveFullscreen == value)
			{
				return;
			}
			if (value && Frontend.ShowMessageBox(
				"Fake Exclusive Fullscreen presents Roblox through a fullscreen layer.\n\nWhile it is on:\n\nYour Windows mouse cursor is hidden.\nEvery overlay is hidden, including the crosshair, the FPS and ping counters, RiShade and Anti Aliasing.\n\nTurn it off if you need any of those. Enable it anyway?",
				MessageBoxImage.Warning,
				MessageBoxButton.YesNo) != MessageBoxResult.Yes)
			{
				OnPropertyChanged("FakeExclusiveFullscreen");
				return;
			}
			App.Settings.Prop.FakeExclusiveFullscreen = value;
			OnPropertyChanged("FakeExclusiveFullscreen");
		}
	}



	public bool CycleTitleWithGameName
	{
		get
		{
			return App.Settings.Prop.CycleTitleWithGameName;
		}
		set
		{
			if (App.Settings.Prop.CycleTitleWithGameName != value)
			{
				App.Settings.Prop.CycleTitleWithGameName = value;
				OnPropertyChanged("CycleTitleWithGameName");
			}
		}
	}



	public bool ShowServerInfoInTitle
	{
		get
		{
			return App.Settings.Prop.ShowServerInfoInTitle;
		}
		set
		{
			if (App.Settings.Prop.ShowServerInfoInTitle != value)
			{
				App.Settings.Prop.ShowServerInfoInTitle = value;
				OnPropertyChanged("ShowServerInfoInTitle");
			}
		}
	}



	public sealed class BackdropItem
	{
		public int Value { get; init; }

		public string Display { get; init; } = "";
	}

	private static readonly ObservableCollection<BackdropItem> _backdropOptions = new()
	{
		new BackdropItem { Value = 0, Display = "Default (off)" },
		new BackdropItem { Value = 2, Display = "Mica" },
		new BackdropItem { Value = 4, Display = "Mica Alt" },
		new BackdropItem { Value = 3, Display = "Acrylic" },
		new BackdropItem { Value = 5, Display = "Aero (glass blur)" }
	};

	public ObservableCollection<BackdropItem> BackdropOptions => _backdropOptions;

	public int RobloxBackdropType
	{
		get
		{
			return App.Settings.Prop.RobloxWindowBackdropType;
		}
		set
		{
			if (App.Settings.Prop.RobloxWindowBackdropType != value)
			{
				App.Settings.Prop.RobloxWindowBackdropType = value;
				App.Settings.SaveDeferred();
				OnPropertyChanged("RobloxBackdropType");
			}
		}
	}

	public bool IsWindows11 => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);



	public string RobloxTitle
	{
		get
		{
			return App.Settings.Prop.RobloxTitle;
		}
		set
		{
			string text = value ?? "";
			if (App.Settings.Prop.RobloxTitle != text)
			{
				App.Settings.Prop.RobloxTitle = text;
				OnPropertyChanged("RobloxTitle");
			}
		}
	}



	public bool WebViewDevTools
	{
		get
		{
			return App.Settings.Prop.WebViewDevTools;
		}
		set
		{
			if (App.Settings.Prop.WebViewDevTools != value)
			{
				App.Settings.Prop.WebViewDevTools = value;
				OnPropertyChanged("WebViewDevTools");
			}
		}
	}



	public bool WebCustomBackgroundEnabled
	{
		get
		{
			return App.Settings.Prop.WebCustomBackgroundEnabled;
		}
		set
		{
			if (App.Settings.Prop.WebCustomBackgroundEnabled != value)
			{
				App.Settings.Prop.WebCustomBackgroundEnabled = value;
				OnPropertyChanged("WebCustomBackgroundEnabled");
				App.Settings.SaveDeferred();
			}
		}
	}



	public bool WebCustomBackgroundBlur
	{
		get
		{
			return App.Settings.Prop.WebCustomBackgroundBlur;
		}
		set
		{
			if (App.Settings.Prop.WebCustomBackgroundBlur != value)
			{
				App.Settings.Prop.WebCustomBackgroundBlur = value;
				OnPropertyChanged("WebCustomBackgroundBlur");
				App.Settings.SaveDeferred();
			}
		}
	}



	public int WebCustomBackgroundOpacity
	{
		get
		{
			return App.Settings.Prop.WebCustomBackgroundOpacity;
		}
		set
		{
			int num = Math.Max(0, Math.Min(100, value));
			if (App.Settings.Prop.WebCustomBackgroundOpacity != num)
			{
				App.Settings.Prop.WebCustomBackgroundOpacity = num;
				OnPropertyChanged("WebCustomBackgroundOpacity");
				App.Settings.SaveDeferred();
			}
		}
	}



	public string WebCustomBackgroundDisplay
	{
		get
		{
			string path = App.Settings.Prop.WebCustomBackgroundPath;
			return string.IsNullOrEmpty(path) ? "No background applied" : ("Applied: " + Path.GetFileName(path));
		}
	}



	public string ApplyButtonText
	{
		get
		{
			Voidstrap.Models.CustomBackground? selected = _selectedWebBackground;
			if (selected != null && !string.IsNullOrEmpty(selected.FilePath) && string.Equals(selected.FilePath, App.Settings.Prop.WebCustomBackgroundPath, StringComparison.OrdinalIgnoreCase))
			{
				return "Already Applied";
			}
			return "Apply Set";
		}
	}



	public ObservableCollection<Voidstrap.Models.CustomBackground> WebBackgrounds => App.Settings.Prop.WebCustomBackgrounds;

	private Voidstrap.Models.CustomBackground? _selectedWebBackground;

	private System.Windows.Media.ImageSource? _selectedBackgroundPreview;

	public Voidstrap.Models.CustomBackground? SelectedWebBackground
	{
		get
		{
			return _selectedWebBackground;
		}
		set
		{
			_selectedWebBackground = value;
			OnPropertyChanged("SelectedWebBackground");
			OnPropertyChanged("ApplyButtonText");
			LoadBackgroundPreview();
		}
	}



	public System.Windows.Media.ImageSource? SelectedBackgroundPreview
	{
		get
		{
			return _selectedBackgroundPreview;
		}
		private set
		{
			_selectedBackgroundPreview = value;
			OnPropertyChanged("SelectedBackgroundPreview");
			OnPropertyChanged("HasBackgroundPreview");
		}
	}



	public bool HasBackgroundPreview => _selectedBackgroundPreview != null;

	private void LoadBackgroundPreview()
	{
		try
		{
			Voidstrap.Models.CustomBackground? selected = _selectedWebBackground;
			if (selected == null || string.IsNullOrEmpty(selected.FilePath) || !File.Exists(selected.FilePath))
			{
				SelectedBackgroundPreview = null;
				return;
			}
			var bitmap = new System.Windows.Media.Imaging.BitmapImage();
			bitmap.BeginInit();
			bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
			bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
			bitmap.DecodePixelWidth = 360;
			bitmap.UriSource = new Uri(selected.FilePath, UriKind.Absolute);
			bitmap.EndInit();
			if (bitmap.CanFreeze)
			{
				bitmap.Freeze();
			}
			SelectedBackgroundPreview = bitmap;
		}
		catch
		{
			SelectedBackgroundPreview = null;
		}
	}



	public ICommand AddWebBackgroundCommand => new RelayCommand(AddWebBackground);

	public ICommand RemoveWebBackgroundCommand => new RelayCommand(RemoveWebBackground);

	public ICommand ApplyWebBackgroundCommand => new RelayCommand(ApplyWebBackground);

	public ICommand RenameWebBackgroundCommand => new RelayCommand(RenameWebBackground);

	public ICommand ClearWebBackgroundCommand => new RelayCommand(ClearWebBackground);

	private void RenameWebBackground()
	{
		App.Settings.SaveDeferred();
	}



	private void ClearWebBackground()
	{
		App.Settings.Prop.WebCustomBackgroundPath = null;
		App.Settings.SaveDeferred();
		OnPropertyChanged("WebCustomBackgroundDisplay");
		OnPropertyChanged("ApplyButtonText");
	}



	private void AddWebBackground()
	{
		Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
		{
			Filter = "Images and GIFs|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
			Multiselect = true
		};
		if (dialog.ShowDialog() != true)
		{
			return;
		}
		Voidstrap.Models.CustomBackground last = null;
		foreach (string file in dialog.FileNames)
		{
			if (App.Settings.Prop.WebCustomBackgrounds.Any((Voidstrap.Models.CustomBackground b) => string.Equals(b.FilePath, file, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			last = new Voidstrap.Models.CustomBackground
			{
				Name = Path.GetFileNameWithoutExtension(file),
				FilePath = file
			};
			App.Settings.Prop.WebCustomBackgrounds.Add(last);
		}
		if (last != null)
		{
			SelectedWebBackground = last;
		}
		App.Settings.SaveDeferred();
	}



	private void RemoveWebBackground()
	{
		Voidstrap.Models.CustomBackground? selected = SelectedWebBackground;
		if (selected == null)
		{
			return;
		}
		int index = App.Settings.Prop.WebCustomBackgrounds.IndexOf(selected);
		App.Settings.Prop.WebCustomBackgrounds.Remove(selected);
		if (!string.IsNullOrEmpty(selected.FilePath) && string.Equals(App.Settings.Prop.WebCustomBackgroundPath, selected.FilePath, StringComparison.OrdinalIgnoreCase))
		{
			App.Settings.Prop.WebCustomBackgroundPath = null;
			OnPropertyChanged("WebCustomBackgroundDisplay");
		}
		int count = App.Settings.Prop.WebCustomBackgrounds.Count;
		if (count == 0)
		{
			SelectedWebBackground = null;
		}
		else
		{
			SelectedWebBackground = App.Settings.Prop.WebCustomBackgrounds[Math.Min(index, count - 1)];
		}
		App.Settings.SaveDeferred();
	}



	private void ApplyWebBackground()
	{
		Voidstrap.Models.CustomBackground? selected = SelectedWebBackground;
		if (selected == null || string.IsNullOrEmpty(selected.FilePath))
		{
			return;
		}
		App.Settings.Prop.WebCustomBackgroundPath = selected.FilePath;
		App.Settings.Prop.WebCustomBackgroundEnabled = true;
		App.Settings.SaveDeferred();
		OnPropertyChanged("WebCustomBackgroundDisplay");
		OnPropertyChanged("WebCustomBackgroundEnabled");
		OnPropertyChanged("ApplyButtonText");
	}


}
