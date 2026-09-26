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
	public sealed class ExcludedGameItem : NotifyPropertyChangedViewModel
	{
		private string _name;

		private string _iconUrl = "";

		private string _players = "";

		private string _detail = "";

		public ExcludedGameItem(long placeId)
		{
			PlaceId = placeId;
			_name = "Place " + placeId.ToString(CultureInfo.InvariantCulture);
		}

		public long PlaceId { get; }

		public string PlaceIdDisplay => PlaceId.ToString(CultureInfo.InvariantCulture);

		public string Name
		{
			get => _name;
			set
			{
				if (string.IsNullOrWhiteSpace(value) || _name == value)
					return;
				_name = value;
				OnPropertyChanged(nameof(Name));
			}
		}

		public string IconUrl
		{
			get => _iconUrl;
			set
			{
				if (_iconUrl == value)
					return;
				_iconUrl = value ?? "";
				OnPropertyChanged(nameof(IconUrl));
				OnPropertyChanged(nameof(HasIcon));
			}
		}

		public bool HasIcon => !string.IsNullOrEmpty(_iconUrl);

		public long UniverseId { get; set; }

		public string Players
		{
			get => _players;
			set
			{
				if (_players == value)
					return;
				_players = value ?? "";
				OnPropertyChanged(nameof(Players));
				OnPropertyChanged(nameof(HasPlayers));
			}
		}

		public bool HasPlayers => !string.IsNullOrEmpty(_players);

		public string Detail
		{
			get => _detail;
			set
			{
				if (_detail == value)
					return;
				_detail = value ?? "";
				OnPropertyChanged(nameof(Detail));
				OnPropertyChanged(nameof(HasDetail));
			}
		}

		public bool HasDetail => !string.IsNullOrEmpty(_detail);

		public static string FormatCount(long value)
		{
			if (value >= 1000000L)
				return (value / 1000000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M";
			if (value >= 1000L)
				return (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "K";
			return value.ToString(CultureInfo.InvariantCulture);
		}
	}

	public sealed class DatacenterItem : NotifyPropertyChangedViewModel
	{
		private readonly Action<DatacenterItem> _onChange;

		private bool _isBlocked;

		private string _pingDisplay;

		private int _pingMs = -1;

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
					OnPropertyChanged(nameof(DistanceKm));
					OnPropertyChanged(nameof(DistanceDisplay));
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
					OnPropertyChanged(nameof(PingDisplay));
				}
			}
		}

		public int PingMs
		{
			get
			{
				return _pingMs;
			}
			set
			{
				if (_pingMs != value)
				{
					_pingMs = value;
					OnPropertyChanged(nameof(PingMs));
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
				OnPropertyChanged(nameof(IsAllowed));
				OnPropertyChanged(nameof(IsBlocked));
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
				OnPropertyChanged(nameof(GamejoinApiVersion));
			}
		}
	}

	private ICollectionView? _filteredDatacenters;

	private string _datacenterSearchText = "";

	private string _presetFetchStatus = "";

	private bool _isFetchingPreset;

	private (double lat, double lon)? _userGeoForPreferred;

	private RobloxAccount? _account;

	private string _cpuModelName = null!;

	private string _cpuSummary = null!;

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
					OnPropertyChanged(nameof(VoidstrapMatchmakerEnabled));
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
				OnPropertyChanged(nameof(VoidstrapMatchmakerEnabled));
			}
			else
			{
				App.Settings.Prop.VoidstrapMatchmakerEnabled = value;
				OnPropertyChanged(nameof(VoidstrapMatchmakerEnabled));
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
			OnPropertyChanged(nameof(MatchmakerMode));
			OnPropertyChanged(nameof(ShowPreferredDatacenterPicker));
			OnPropertyChanged(nameof(PreferredDatacenterKey));
			OnPropertyChanged(nameof(SelectedPreferredDatacenter));
			RefreshPreferredDatacenterOptions();
			EnsurePreferredDatacenter();
			RefreshMatchmakerSummary();
		}
	}

	public bool ShowPreferredDatacenterPicker => MatchmakerMode == 2;

	public double UserLatitude => _userGeoForPreferred?.lat ?? double.NaN;

	public double UserLongitude => _userGeoForPreferred?.lon ?? double.NaN;

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
				OnPropertyChanged(nameof(VoidstrapMatchmakerMaxCandidates));
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
				OnPropertyChanged(nameof(VoidstrapMatchmakerAutoCandidates));
				OnPropertyChanged(nameof(VoidstrapMatchmakerManualCandidates));
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
							return country.Contains(value, StringComparison.OrdinalIgnoreCase);
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
			OnPropertyChanged(nameof(DatacenterSearchText));
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
				OnPropertyChanged(nameof(DatacenterPresetUrl));
				if (!string.IsNullOrWhiteSpace(text))
				{
					_ = FetchPresetAsync();
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
			OnPropertyChanged(nameof(PresetFetchStatus));
			OnPropertyChanged(nameof(HasPresetFetchStatus));
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
			OnPropertyChanged(nameof(IsFetchingPreset));
			OnPropertyChanged(nameof(CanFetchPreset));
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



	private PreferredDatacenterOption? _selectedPreferredDatacenter;

	public ObservableCollection<PreferredDatacenterOption> PreferredDatacenterOptions { get; } = new ObservableCollection<PreferredDatacenterOption>();

	public PreferredDatacenterOption? SelectedPreferredDatacenter
	{
		get
		{
			return _selectedPreferredDatacenter;
		}
		set
		{
			if (value == null && _specificDatacenterMode && PreferredDatacenterOptions.Count > 0)
				return;
			if (_selectedPreferredDatacenter != value)
			{
				_selectedPreferredDatacenter = value;
				string key = value?.Key ?? "";
				App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter = key;
				try
				{
					App.Settings.SaveDeferred();
				}
				catch
				{
				}
				OnPropertyChanged(nameof(SelectedPreferredDatacenter));
				OnPropertyChanged(nameof(PreferredDatacenterKey));
				RefreshMatchmakerSummary();
			}
		}
	}

	public string PreferredDatacenterKey
	{
		get
		{
			return App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter ?? "";
		}
		set
		{
			string key = value ?? "";
			if (string.Equals(PreferredDatacenterKey, key, StringComparison.OrdinalIgnoreCase))
				return;
			App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter = key;
			try
			{
				App.Settings.SaveDeferred();
			}
			catch
			{
			}
			_selectedPreferredDatacenter = PreferredDatacenterOptions.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));
			OnPropertyChanged(nameof(PreferredDatacenterKey));
			OnPropertyChanged(nameof(SelectedPreferredDatacenter));
			RefreshMatchmakerSummary();
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
				OnPropertyChanged(nameof(ServerMatchmakerMaxRetries));
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
			OnPropertyChanged(nameof(CpuModelName));
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
			OnPropertyChanged(nameof(CpuSummary));
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
				OnPropertyChanged(nameof(SelectedCpuPriority));
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
				OnPropertyChanged(nameof(disablecrashhandleryayyysocool));
			}
		}
	}



	public bool TasxOptimization
	{
		get
		{
			return App.Settings.Prop.TasxOptimization;
		}
		set
		{
			if (App.Settings.Prop.TasxOptimization != value)
			{
				App.Settings.Prop.TasxOptimization = value;
				OnPropertyChanged(nameof(TasxOptimization));
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
			OnPropertyChanged(nameof(LaunchRobloxWebsite));
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
				OnPropertyChanged(nameof(OptimizeRoblox));
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
				OnPropertyChanged(nameof(BypassEmulationOverhead));
				App.Settings.SaveDeferred();
			}
		}
	}

	public Visibility BypassEmulationOverheadVisibility => Voidstrap.Utility.Platform.IsWindows ? Visibility.Visible : Visibility.Collapsed;

	public string PrioritizeRobloxDescription => Voidstrap.Utility.Platform.IsLinux
		? "Gives Roblox a larger CPU share while it is in the foreground. A custom Roblox Priority choice takes precedence."
		: "Uses safe Above Normal scheduling while Roblox is in the foreground. A custom Roblox Priority choice takes precedence.";

	public string ReduceResourcesDescription => Voidstrap.Utility.Platform.IsLinux
		? "Lowers Roblox's CPU share while another app is active, then restores it when you return."
		: "Lowers Roblox's Windows scheduling priority while another app is active, then restores it when you return.";

	public Visibility CleanRobloxCacheVisibility => Voidstrap.Utility.Platform.IsWindows || Voidstrap.Utility.Platform.IsLinux ? Visibility.Visible : Visibility.Collapsed;



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
				OnPropertyChanged(nameof(ReduceMemoryOutOfFocus));
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
				OnPropertyChanged(nameof(MultiAccount));
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
			OnPropertyChanged(nameof(CleanerLogs));
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
			OnPropertyChanged(nameof(CleanerCache));
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
			OnPropertyChanged(nameof(CleanerVoidstrap));
		}
	}



	public BehaviourViewModel()
	{
		CleanerItems = new List<string>(App.Settings.Prop.CleanerDirectories);
		LoadCpuOptions();
		LoadDatacenters();
		_ = LoadUserGeoForPreferredAsync();
		_ = FetchPresetAsync(false);
		_ = LoadAccountAsync();
		LoadExcludedGames();
		SelectedWebBackground = App.Settings.Prop.WebCustomBackgrounds.FirstOrDefault();
	}

	public ObservableCollection<ExcludedGameItem> ExcludedGames { get; } = new ObservableCollection<ExcludedGameItem>();

	private static readonly string GameSearchSessionId = Guid.NewGuid().ToString();

	private string _gameSearchText = "";

	private string _gameSearchStatus = "";

	private CancellationTokenSource? _gameSearchCts;

	public ObservableCollection<ExcludedGameItem> GameSearchResults { get; } = new ObservableCollection<ExcludedGameItem>();

	public string GameSearchText
	{
		get => _gameSearchText;
		set
		{
			if (_gameSearchText == value)
				return;
			_gameSearchText = value ?? "";
			OnPropertyChanged(nameof(GameSearchText));
			BeginGameSearch();
		}
	}

	public string GameSearchStatus
	{
		get => _gameSearchStatus;
		private set
		{
			if (_gameSearchStatus == value)
				return;
			_gameSearchStatus = value ?? "";
			OnPropertyChanged(nameof(GameSearchStatus));
			OnPropertyChanged(nameof(HasGameSearchStatus));
		}
	}

	public bool HasGameSearchStatus => !string.IsNullOrEmpty(_gameSearchStatus);

	public bool HasGameSearchResults => GameSearchResults.Count > 0;

	public ICommand AddSearchResultCommand => new RelayCommand<ExcludedGameItem>(AddSearchResult);

	private void BeginGameSearch()
	{
		CancellationTokenSource? previous = _gameSearchCts;
		_gameSearchCts = null;
		try
		{
			previous?.Cancel();
			previous?.Dispose();
		}
		catch
		{
		}

		string query = (_gameSearchText ?? "").Trim();
		if (query.Length < 2)
		{
			ClearGameSearchResults();
			GameSearchStatus = "";
			return;
		}

		var cts = new CancellationTokenSource();
		_gameSearchCts = cts;
		_ = RunGameSearchAsync(query, cts);
	}

	private async Task RunGameSearchAsync(string query, CancellationTokenSource cts)
	{
		try
		{
			await Task.Delay(350, cts.Token).ConfigureAwait(true);
			GameSearchStatus = "Searching...";

			List<ExcludedGameItem> found = await FindGamesAsync(query, cts.Token).ConfigureAwait(true);
			if (cts.Token.IsCancellationRequested)
				return;

			ClearGameSearchResults();
			foreach (ExcludedGameItem item in found)
				GameSearchResults.Add(item);

			OnPropertyChanged(nameof(HasGameSearchResults));
			GameSearchStatus = found.Count == 0 ? "No games found." : "";

			if (found.Count > 0)
				await ApplyExcludedGameIconsAsync([.. found], string.Join(",", found.Select(g => g.PlaceId.ToString(CultureInfo.InvariantCulture)))).ConfigureAwait(true);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("BehaviourViewModel::RunGameSearch", "Game search failed: " + ex.Message);
			GameSearchStatus = "Search is unavailable right now, paste a place id instead.";
		}
		finally
		{
			if (ReferenceEquals(_gameSearchCts, cts))
				_gameSearchCts = null;
			try
			{
				cts.Dispose();
			}
			catch
			{
			}
		}
	}

	private void ClearGameSearchResults()
	{
		if (GameSearchResults.Count == 0)
			return;
		GameSearchResults.Clear();
		OnPropertyChanged(nameof(HasGameSearchResults));
	}

	private static async Task<List<ExcludedGameItem>> FindGamesAsync(string query, CancellationToken token)
	{
		if (long.TryParse(query, NumberStyles.Integer, CultureInfo.InvariantCulture, out long typedPlaceId) && typedPlaceId > 0)
		{
			var direct = new ExcludedGameItem(typedPlaceId);
			await ApplyExcludedGameNamesAsync([direct], typedPlaceId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
			return [direct];
		}

		using var request = new System.Net.Http.HttpRequestMessage(
			System.Net.Http.HttpMethod.Get,
			"https://apis.roblox.com/search-api/omni-search?searchQuery=" + Uri.EscapeDataString(query)
				+ "&pageToken=&sessionId=" + GameSearchSessionId + "&pageType=all");

		string? cookie = RobloxCookie.Get();
		if (!string.IsNullOrEmpty(cookie))
			request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);

		using System.Net.Http.HttpResponseMessage response = await App.HttpClient.SendAsync(request, token).ConfigureAwait(false);
		if (!response.IsSuccessStatusCode)
			return [];

		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
		if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("searchResults", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
			return [];

		var results = new List<ExcludedGameItem>();
		var seen = new HashSet<long>();

		foreach (JsonElement group in groups.EnumerateArray())
		{
			if (!group.TryGetProperty("contents", out JsonElement contents) || contents.ValueKind != JsonValueKind.Array)
				continue;

			foreach (JsonElement entry in contents.EnumerateArray())
			{
				if (results.Count >= 12)
					return results;

				if (entry.TryGetProperty("isSponsored", out JsonElement sponsoredElement) && sponsoredElement.ValueKind == JsonValueKind.True)
					continue;

				if (!entry.TryGetProperty("rootPlaceId", out JsonElement placeElement) || !placeElement.TryGetInt64(out long placeId) || placeId <= 0 || !seen.Add(placeId))
					continue;

				var item = new ExcludedGameItem(placeId);
				if (entry.TryGetProperty("universeId", out JsonElement universeElement) && universeElement.TryGetInt64(out long universeId))
					item.UniverseId = universeId;
				if (entry.TryGetProperty("name", out JsonElement nameElement) && nameElement.ValueKind == JsonValueKind.String)
					item.Name = nameElement.GetString() ?? "";

				if (entry.TryGetProperty("playerCount", out JsonElement playersElement) && playersElement.TryGetInt64(out long players))
					item.Players = ExcludedGameItem.FormatCount(players) + " playing";

				item.Detail = BuildSearchDetail(entry);
				results.Add(item);
			}
		}

		return results;
	}

	private static string BuildSearchDetail(JsonElement entry)
	{
		var parts = new List<string>(2);

		if (entry.TryGetProperty("creatorName", out JsonElement creatorElement) && creatorElement.ValueKind == JsonValueKind.String)
		{
			string creator = creatorElement.GetString() ?? "";
			if (creator.Length > 0)
				parts.Add("by " + creator);
		}

		long up = entry.TryGetProperty("totalUpVotes", out JsonElement upElement) && upElement.TryGetInt64(out long upVotes) ? upVotes : 0L;
		long down = entry.TryGetProperty("totalDownVotes", out JsonElement downElement) && downElement.TryGetInt64(out long downVotes) ? downVotes : 0L;
		if (up + down > 0L)
			parts.Add(Math.Round(up * 100.0 / (up + down)).ToString("0", CultureInfo.InvariantCulture) + "% rating");

		return string.Join("  ", parts);
	}

	private void AddSearchResult(ExcludedGameItem? item)
	{
		if (item == null || item.PlaceId <= 0)
			return;

		GameSearchText = "";
		ClearGameSearchResults();
		GameSearchStatus = "";

		if (ExcludedGames.Any(g => g.PlaceId == item.PlaceId))
			return;

		ServerMatchmaker.SetExcluded(item.PlaceId, excluded: true);
		ExcludedGames.Add(item);
		RaiseExcludedGamesChanged();
		RunSafeExcludedGamesAsync();
	}

	public string ExcludedGamesSummary => ExcludedGames.Count == 0
		? "Voidstrap picks a server for every game."
		: (ExcludedGames.Count == 1
			? "1 game joins normally, Voidstrap never picks its server."
			: $"{ExcludedGames.Count} games join normally, Voidstrap never picks their servers.");

	public bool HasExcludedGames => ExcludedGames.Count > 0;

	public ICommand RemoveExcludedGameCommand => new RelayCommand<ExcludedGameItem>(RemoveExcludedGame);

	public ICommand ClearExcludedGamesCommand => new RelayCommand(ClearExcludedGames);

	public void RefreshExcludedGames()
	{
		if (ExcludedGames.Count == (App.Settings.Prop.MatchmakerExcludedPlaceIds ?? []).Distinct().Count(id => id > 0)
			&& ExcludedGames.All(g => ServerMatchmaker.IsExcluded(g.PlaceId)))
		{
			return;
		}

		LoadExcludedGames();
	}

	private void LoadExcludedGames()
	{
		ExcludedGames.Clear();
		foreach (long placeId in (App.Settings.Prop.MatchmakerExcludedPlaceIds ?? []).Distinct().Where(id => id > 0))
			ExcludedGames.Add(new ExcludedGameItem(placeId));

		RaiseExcludedGamesChanged();
		RunSafeExcludedGamesAsync();
	}

	private void RaiseExcludedGamesChanged()
	{
		OnPropertyChanged(nameof(ExcludedGamesSummary));
		OnPropertyChanged(nameof(HasExcludedGames));
	}

	private void RemoveExcludedGame(ExcludedGameItem? item)
	{
		if (item == null)
			return;

		ServerMatchmaker.SetExcluded(item.PlaceId, excluded: false);
		ExcludedGames.Remove(item);
		RaiseExcludedGamesChanged();
	}

	private void ClearExcludedGames()
	{
		foreach (ExcludedGameItem item in ExcludedGames.ToList())
			ServerMatchmaker.SetExcluded(item.PlaceId, excluded: false);

		ExcludedGames.Clear();
		RaiseExcludedGamesChanged();
	}

	private void RunSafeExcludedGamesAsync()
	{
		_ = ResolveExcludedGameDetailsAsync();
	}

	private async Task ResolveExcludedGameDetailsAsync()
	{
		ExcludedGameItem[] pending = ExcludedGames.Where(g => !g.HasIcon).ToArray();
		if (pending.Length == 0)
			return;

		foreach (ExcludedGameItem[] chunk in pending.Chunk(25))
		{
			try
			{
				string ids = string.Join(",", chunk.Select(g => g.PlaceId.ToString(CultureInfo.InvariantCulture)));
				await ApplyExcludedGameNamesAsync(chunk, ids).ConfigureAwait(true);
				await ApplyExcludedGameIconsAsync(chunk, ids).ConfigureAwait(true);
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine("BehaviourViewModel::ResolveExcludedGameDetails", "Could not resolve excluded game details: " + ex.Message);
			}
		}
	}

	private static async Task ApplyExcludedGameNamesAsync(ExcludedGameItem[] chunk, string ids)
	{
		using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://games.roblox.com/v1/games/multiget-place-details?placeIds=" + ids);
		string? cookie = RobloxCookie.Get();
		if (!string.IsNullOrEmpty(cookie))
			request.Headers.TryAddWithoutValidation("Cookie", ".ROBLOSECURITY=" + cookie);

		using System.Net.Http.HttpResponseMessage response = await App.HttpClient.SendAsync(request).ConfigureAwait(true);
		if (!response.IsSuccessStatusCode)
			return;

		using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
		if (json.RootElement.ValueKind != JsonValueKind.Array)
			return;

		foreach (JsonElement entry in json.RootElement.EnumerateArray())
		{
			if (!entry.TryGetProperty("placeId", out JsonElement idElement) || !idElement.TryGetInt64(out long placeId))
				continue;
			if (!entry.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String)
				continue;

			ExcludedGameItem? match = chunk.FirstOrDefault(g => g.PlaceId == placeId);
			if (match != null)
				match.Name = nameElement.GetString() ?? "";
		}
	}

	private static async Task ApplyExcludedGameIconsAsync(ExcludedGameItem[] chunk, string ids)
	{
		var response = await Voidstrap.Utility.Http.GetJson<Voidstrap.Models.APIs.Roblox.ApiArrayResponse<Voidstrap.Models.APIs.Roblox.ThumbnailResponse>>(
			"https://thumbnails.roblox.com/v1/places/gameicons?placeIds=" + ids + "&returnPolicy=PlaceHolder&size=150x150&format=Png&isCircular=false").ConfigureAwait(true);

		if (response?.Data == null)
			return;

		foreach (var thumbnail in response.Data)
		{
			if (thumbnail == null || string.IsNullOrWhiteSpace(thumbnail.ImageUrl))
				continue;

			ExcludedGameItem? match = chunk.FirstOrDefault(g => g.PlaceId == thumbnail.TargetId);
			if (match != null)
				match.IconUrl = thumbnail.ImageUrl;
		}
	}



	private async Task CleanRobloxCacheAsync()
	{
		bool linux = Voidstrap.Utility.Platform.IsLinux;
		List<Process> list = new List<Process>();
		if (!linux && !string.IsNullOrEmpty(App.State.Prop.Player.VersionGuid))
		{
			list.AddRange(Process.GetProcessesByName("RobloxPlayerBeta"));
		}
		if (!linux && App.IsStudioVisible)
		{
			list.AddRange(Process.GetProcessesByName("RobloxStudioBeta"));
		}
		if (list.Count != 0 || linux && Voidstrap.Platform.Linux.LinuxSoberProcessProbe.IsRunningNow())
		{
			Frontend.ShowMessageBox("Close Roblox before cleaning the cache.", MessageBoxImage.Hand);
			return;
		}
		string soberRoot = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".var", "app", "org.vinegarhq.Sober");
		string soberAppData = Path.Combine(soberRoot, "data", "sober", "appData");
		string path = linux ? Path.Combine(soberRoot, "cache", "sober") : Path.Combine(Path.GetTempPath(), "Roblox");
		string path2 = linux ? Path.Combine(soberAppData, "rbx-storage") : Path.Combine(Paths.LocalAppData, "Roblox", "rbx-storage");
		string dbFile = linux ? Path.Combine(soberAppData, "rbx-storage.db") : Path.Combine(Paths.LocalAppData, "Roblox", "rbx-storage.db");
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
			if (linux && hasDb && !File.Exists(dbFile))
			{
				foreach (string companion in new[] { dbFile + "-wal", dbFile + "-shm" })
				{
					if (File.Exists(companion) && !TryDeleteFile(companion))
					{
						num2++;
					}
				}
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
					EnsurePreferredDatacenter();
					OnPropertyChanged(nameof(UserLatitude));
					OnPropertyChanged(nameof(UserLongitude));
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
			UserGeo? geo = await VoidstrapMatchmaker.GetUserGeoAsync().ConfigureAwait(continueOnCapturedContext: false);
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
		OnPropertyChanged(nameof(RecommendedMatchmakerCandidates));
		OnPropertyChanged(nameof(EffectiveMatchmakerCandidates));
		OnPropertyChanged(nameof(SearchDepthDescription));
	}

	public void RefreshMatchmakerSummary()
	{
		OnPropertyChanged(nameof(UserLocationText));
		OnPropertyChanged(nameof(BlockedDatacenterSummary));
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
			List<LearnedServerEntry> entries = ServerFetchStore.AllEntries();
			HashSet<string> learnedCidrs = new HashSet<string>(entries.Select(e => e.Cidr), StringComparer.OrdinalIgnoreCase);
			entries.AddRange(RobloxDatacenterMap.AllSeedEntries()
				.Where(seed => !learnedCidrs.Contains(seed.Cidr))
				.Select(seed => new LearnedServerEntry { Cidr = seed.Cidr, City = seed.City, Region = seed.Region, Country = seed.Country, Lat = seed.Lat, Lon = seed.Lon }));
			foreach (LearnedServerEntry item in entries)
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
					IEnumerable<string>? iPs = x.IPs;
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
			EnsurePreferredDatacenter();
			RefreshMatchmakerSummary();
			_ = MeasureDatacenterPingsAsync();
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
			if (item.PingMs < 0)
			{
				item.PingMs = Voidstrap.Integrations.VoidstrapMatchmaker.EstimatePingMs(item.DistanceKm);
				item.PingDisplay = $"{item.PingMs} ms";
			}
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
		if (!IPAddress.TryParse(ip, out _))
		{
			return false;
		}
		return !VoidstrapMatchmaker.IsPrivateIp(ip);
	}






	public async Task FetchPresetAsync(bool force = true)
	{
		if (_isFetchingPreset)
		{
			return;
		}
		IsFetchingPreset = true;
		try
		{
			await Voidstrap.Utility.RemoteData.RefreshServerLocationsAsync(force, CancellationToken.None).ConfigureAwait(continueOnCapturedContext: true);
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
			string savedKey = App.Settings.Prop.VoidstrapMatchmakerPreferredDatacenter ?? "";
			PreferredDatacenterOptions.Clear();
			foreach (DatacenterItem item in Datacenters.Where(d => d.IsAllowed))
			{
				string ping = item.PingMs >= 0 ? $"{item.PingMs} ms" : (item.DistanceKm >= 0.0 ? $"{VoidstrapMatchmaker.EstimatePingMs(item.DistanceKm)} ms" : "");
				string display = string.IsNullOrEmpty(ping) ? item.Location : $"{item.Location} ({ping})";
				PreferredDatacenterOptions.Add(new PreferredDatacenterOption
				{
					Key = item.Key,
					Display = display,
					DistanceKm = item.DistanceKm
				});
			}
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
				OnPropertyChanged(nameof(PreferredDatacenterKey));
			}
			OnPropertyChanged(nameof(SelectedPreferredDatacenter));
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("BehaviourViewModel::RefreshPreferredDatacenterOptions", "Failed: " + ex.Message);
		}
	}

	public void EnsurePreferredDatacenter()
	{
		if (!_specificDatacenterMode)
			return;
		string saved = PreferredDatacenterKey;
		if (Datacenters.Any(d => d.IsAllowed && string.Equals(d.Key, saved, StringComparison.OrdinalIgnoreCase)))
		{
			_selectedPreferredDatacenter = PreferredDatacenterOptions.FirstOrDefault(o => string.Equals(o.Key, saved, StringComparison.OrdinalIgnoreCase));
			OnPropertyChanged(nameof(SelectedPreferredDatacenter));
			return;
		}
		DatacenterItem? nearest = Datacenters.Where(d => d.IsAllowed).OrderBy(d => d.DistanceKm < 0.0 ? double.MaxValue : d.DistanceKm).FirstOrDefault();
		if (nearest != null)
		{
			PreferredDatacenterKey = nearest.Key;
			_selectedPreferredDatacenter = PreferredDatacenterOptions.FirstOrDefault(o => string.Equals(o.Key, nearest.Key, StringComparison.OrdinalIgnoreCase));
			OnPropertyChanged(nameof(SelectedPreferredDatacenter));
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
		EnsurePreferredDatacenter();
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
				await Application.Current.Dispatcher.InvokeAsync(delegate
				{
					item.PingMs = ms;
					item.PingDisplay = text;
				});
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
			await Application.Current.Dispatcher.InvokeAsync(RefreshPreferredDatacenterOptions);
		}
		catch
		{
		}
	}



	private static async Task<int> MeasureTcpPingAsync(string ip)
	{
		if (!IPAddress.TryParse(ip, out IPAddress? addr))
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
		EnsurePreferredDatacenter();
		RefreshMatchmakerSummary();
	}



	public void RefreshLearnedStats()
	{
		OnPropertyChanged(nameof(VoidstrapMatchmakerLearnedStats));
		OnPropertyChanged(nameof(HasLearnedData));
	}



	public void RefreshLoginStatus()
	{
		RobloxCookie.InvalidateCache();
		OnPropertyChanged(nameof(LoginStatusText));
		OnPropertyChanged(nameof(IsSignedIn));
		OnPropertyChanged(nameof(IsNotSignedIn));
		_ = LoadAccountAsync();
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
		OnPropertyChanged(nameof(LoginStatusText));
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
			OnPropertyChanged(nameof(SelectedCpuPriority));
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
			OnPropertyChanged(nameof(SelectedCpuPriority));
		}
	}



	private static int GetPhysicalCoreCount()
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
					OnPropertyChanged(nameof(RobloxFullscreenMode));
					OnPropertyChanged(nameof(ShowExclusiveFullscreenWarning));
					return;
				}
				FakeBorderlessFullscreen = false;
			}
			else
			{
				FakeExclusiveFullscreen = false;
				FakeBorderlessFullscreen = value == 1;
			}
			OnPropertyChanged(nameof(RobloxFullscreenMode));
			OnPropertyChanged(nameof(ShowExclusiveFullscreenWarning));
		}
	}

	public bool ShowExclusiveFullscreenWarning => RobloxFullscreenMode == 2 && !Voidstrap.Utility.Platform.IsLinux;

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
				OnPropertyChanged(nameof(FakeBorderlessFullscreen));
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
				OnPropertyChanged(nameof(FakeExclusiveFullscreen));
				return;
			}
			App.Settings.Prop.FakeExclusiveFullscreen = value;
			OnPropertyChanged(nameof(FakeExclusiveFullscreen));
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
				OnPropertyChanged(nameof(CycleTitleWithGameName));
			}
		}
	}



	public bool UseGameIconForRobloxWindow
	{
		get
		{
			return App.Settings.Prop.UseGameIconForRobloxWindow;
		}
		set
		{
			if (App.Settings.Prop.UseGameIconForRobloxWindow != value)
			{
				App.Settings.Prop.UseGameIconForRobloxWindow = value;
				OnPropertyChanged(nameof(UseGameIconForRobloxWindow));
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
				OnPropertyChanged(nameof(ShowServerInfoInTitle));
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
				OnPropertyChanged(nameof(RobloxBackdropType));
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
				OnPropertyChanged(nameof(RobloxTitle));
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
				OnPropertyChanged(nameof(WebViewDevTools));
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
				OnPropertyChanged(nameof(WebCustomBackgroundEnabled));
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
				OnPropertyChanged(nameof(WebCustomBackgroundBlur));
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
				OnPropertyChanged(nameof(WebCustomBackgroundOpacity));
				App.Settings.SaveDeferred();
			}
		}
	}



	public string WebCustomBackgroundDisplay
	{
		get
		{
			string? path = App.Settings.Prop.WebCustomBackgroundPath;
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
			OnPropertyChanged(nameof(SelectedWebBackground));
			OnPropertyChanged(nameof(ApplyButtonText));
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
			OnPropertyChanged(nameof(SelectedBackgroundPreview));
			OnPropertyChanged(nameof(HasBackgroundPreview));
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
			if (Voidstrap.Utility.Platform.IsLinux)
			{
				SelectedBackgroundPreview = Voidstrap.Utility.SafeImaging.FromFile(selected.FilePath, 360);
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
		OnPropertyChanged(nameof(WebCustomBackgroundDisplay));
		OnPropertyChanged(nameof(ApplyButtonText));
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
		Voidstrap.Models.CustomBackground? last = null;
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
			OnPropertyChanged(nameof(WebCustomBackgroundDisplay));
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
		OnPropertyChanged(nameof(WebCustomBackgroundDisplay));
		OnPropertyChanged(nameof(WebCustomBackgroundEnabled));
		OnPropertyChanged(nameof(ApplyButtonText));
	}


}
