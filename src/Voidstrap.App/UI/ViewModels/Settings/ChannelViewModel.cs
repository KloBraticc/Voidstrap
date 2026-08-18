using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Voidstrap.Models;
using Voidstrap.Utility;
using Voidstrap.Models.APIs.Roblox;
using Voidstrap.Models.Persistable;
using Voidstrap.RobloxInterfaces;
using Voidstrap.UI.Elements.Settings;

namespace Voidstrap.UI.ViewModels.Settings;

public class ChannelViewModel : INotifyPropertyChanged, IDisposable
{
	private const int MaximumLocalStorageBytes = 4 * 1024 * 1024;

	private const string HardwareAccelerationRestartKey = "application.hardwareAcceleration";

	private const double MonitorCanvasWidth = 540.0;

	private const double MonitorCanvasHeight = 190.0;

	private const double MonitorCanvasMargin = 10.0;

	private const double MonitorBoxInset = 3.0;

	private CancellationTokenSource? _loadChannelCts;

	private readonly CancellationTokenSource _lifetimeCts = new();

	private bool _disposed;

	private bool _showLoadingError;

	private bool _showChannelWarning;

	private DeployInfo? _channelDeployInfo;

	private string _channelInfoLoadingText = string.Empty;

	private DisplayMode? _selectedResolution;

	private MonitorTile? _selectedMonitor;

	private bool _suppressResolutionApply;

	private readonly DispatcherTimer _revertTimer;

	private string? _revertDevice;

	private DisplayMode? _revertMode;

	private bool _autoReverted;

	private ICommand? _selectMonitorCommand;

	private ICommand? _identifyMonitorsCommand;

	private ICommand? _clearInGameResolutionCommand;

	private string _selectedPriority;

	private string _viewChannel;

	private ICommand? _applyChannelCommand;

	private MirrorChoice? _selectedMirror;

	private bool _networkStreamingEnabled;

	private bool _hardwareAccelerationDisabled;

	private readonly string _robloxLocalStorage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "LocalStorage");


	private string? _installLocationText;

	private ICommand? _browseInstallLocationCommand;

	private ICommand? _applyInstallLocationCommand;

	public ObservableCollection<int> CpuLimitOptions { get; set; }

	public ObservableCollection<DisplayMode> AvailableResolutionsInGame { get; } = new ObservableCollection<DisplayMode>();

	public bool UsePlaceId
	{
		get
		{
			return App.Settings.Prop.UsePlaceId;
		}
		set
		{
			if (App.Settings.Prop.UsePlaceId != value)
			{
				App.Settings.Prop.UsePlaceId = value;
				OnPropertyChanged("UsePlaceId");
				App.Settings.SaveDeferred();
			}
		}
	}

	public string PlaceId
	{
		get
		{
			return App.Settings.Prop.PlaceId;
		}
		set
		{
			if (App.Settings.Prop.PlaceId != value)
			{
				App.Settings.Prop.PlaceId = value;
				OnPropertyChanged("PlaceId");
				App.Settings.SaveDeferred();
			}
		}
	}

	public ObservableCollection<DisplayMode> AvailableResolutions { get; } = new ObservableCollection<DisplayMode>();

	public ObservableCollection<MonitorTile> Monitors { get; } = new ObservableCollection<MonitorTile>();

	public ICommand SelectMonitorCommand => _selectMonitorCommand ?? (_selectMonitorCommand = new RelayCommand<MonitorTile>(SelectMonitorFromUi));

	public ICommand IdentifyMonitorsCommand => _identifyMonitorsCommand ?? (_identifyMonitorsCommand = new RelayCommand(DisplaySystem.IdentifyDisplays));

	public ICommand ClearInGameResolutionCommand => _clearInGameResolutionCommand ?? (_clearInGameResolutionCommand = new RelayCommand(ClearInGameResolution));

	public string SelectedMonitorSummary
	{
		get
		{
			if (_selectedMonitor == null)
			{
				return string.Empty;
			}
			DisplayMode? current = DisplaySystem.GetCurrentMode(_selectedMonitor.DeviceName);
			string label = $"Monitor {_selectedMonitor.Number}: {_selectedMonitor.FriendlyName}";
			if (_selectedMonitor.IsPrimary)
			{
				label += " (primary)";
			}
			if (current != null)
			{
				label = label + ", current " + current.DisplayName;
			}
			return label;
		}
	}

	public DisplayMode? SelectedResolution
	{
		get
		{
			return _selectedResolution;
		}
		set
		{
			if (_selectedResolution != value)
			{
				_selectedResolution = value;
				OnPropertyChanged("SelectedResolution");
				if (_selectedResolution != null && !_suppressResolutionApply)
				{
					ApplyResolutionToSelected(_selectedResolution);
				}
			}
		}
	}

	public DisplayMode? SelectedResolutionInGame
	{
		get
		{
			AppSettings.ResolutionSetting r = App.Settings.Prop.InGameResolution;
			if (r == null)
			{
				return null;
			}
			return AvailableResolutionsInGame.FirstOrDefault((DisplayMode m) => m.Width == r.Width && m.Height == r.Height && m.RefreshRate == r.RefreshRate);
		}
		set
		{
			if (value == null)
			{
				App.Settings.Prop.InGameResolution = null;
			}
			else
			{
				App.Settings.Prop.InGameResolution = new AppSettings.ResolutionSetting
				{
					Width = value.Width,
					Height = value.Height,
					RefreshRate = value.RefreshRate,
					Monitor = _selectedMonitor?.DeviceName
				};
			}
			OnPropertyChanged("SelectedResolutionInGame");
			App.Settings.SaveDeferred();
		}
	}

	public bool RobloxEfficiencyMode
	{
		get
		{
			return App.Settings.Prop.RobloxEfficiencyMode;
		}
		set
		{
			if (App.Settings.Prop.RobloxEfficiencyMode != value)
			{
				App.Settings.Prop.RobloxEfficiencyMode = value;
				OnPropertyChanged("RobloxEfficiencyMode");
				App.Settings.SaveDeferred();
			}
		}
	}

	public ObservableCollection<string> PriorityOptions { get; set; }

	public string SelectedPriority
	{
		get
		{
			return _selectedPriority;
		}
		set
		{
			if (_selectedPriority != value)
			{
				_selectedPriority = value;
				OnPropertyChanged("SelectedPriority");
				App.Settings.Prop.PriorityLimit = value;
				App.Settings.SaveDeferred();
			}
		}
	}

	public int SelectedCpuLimit
	{
		get
		{
			return App.Settings.Prop.CpuCoreLimit;
		}
		set
		{
			if (App.Settings.Prop.CpuCoreLimit != value)
			{
				App.Settings.Prop.CpuCoreLimit = value;
				OnPropertyChanged("SelectedCpuLimit");
				App.Settings.SaveDeferred();
				CpuCoreLimiter.SetCpuCoreLimit(value);
			}
		}
	}

	public bool UpdateCheckingEnabled
	{
		get
		{
			return App.Settings.Prop.CheckForUpdates;
		}
		set
		{
			App.Settings.Prop.CheckForUpdates = value;
		}
	}

	public ObservableCollection<MirrorChoice> MirrorChoices { get; } = BuildMirrorChoices();

	public MirrorChoice SelectedMirrorChoice
	{
		get
		{
			if (_selectedMirror == null)
			{
				string saved = App.Settings?.Prop?.PreferredMirror ?? string.Empty;
				_selectedMirror = MirrorChoices.FirstOrDefault(choice => string.Equals(choice.Url, saved, StringComparison.OrdinalIgnoreCase)) ?? MirrorChoices[0];
				if (!string.Equals(_selectedMirror.Url, saved, StringComparison.Ordinal))
				{
					App.Settings.Prop.PreferredMirror = _selectedMirror.Url;
					Deployment.PreferredBaseUrl = _selectedMirror.Url;
					App.Settings.SaveDeferred();
				}
			}
			return _selectedMirror;
		}
		set
		{
			if (value == null || value == _selectedMirror)
			{
				return;
			}
			_selectedMirror = value;
			OnPropertyChanged("SelectedMirrorChoice");
			try
			{
				App.Settings.Prop.PreferredMirror = value.Url;
				Deployment.PreferredBaseUrl = value.Url;
				App.Settings.SaveDeferred();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ChannelViewModel::SelectedMirrorChoice", "Could not save the download server: " + ex.Message);
			}
		}
	}

	private static ObservableCollection<MirrorChoice> BuildMirrorChoices()
	{
		ObservableCollection<MirrorChoice> choices = new ObservableCollection<MirrorChoice>
		{
			new MirrorChoice("Auto (fastest responding server)", string.Empty)
		};
		foreach (string url in Deployment.Mirrors)
		{
			choices.Add(new MirrorChoice(MirrorChoice.Describe(url), url));
		}
		return choices;
	}

	public bool AllowPreReleaseUpdates
	{
		get
		{
			return App.Settings?.Prop?.AllowPreReleaseUpdates == true;
		}
		set
		{
			if (App.Settings?.Prop != null && App.Settings.Prop.AllowPreReleaseUpdates != value)
			{
				App.Settings.Prop.AllowPreReleaseUpdates = value;
				OnPropertyChanged("AllowPreReleaseUpdates");
			}
		}
	}

	public bool IsChannelEnabled
	{
		get
		{
			return App.Settings.Prop.IsChannelEnabled;
		}
		set
		{
			if (App.Settings.Prop.IsChannelEnabled != value)
			{
				App.Settings.Prop.IsChannelEnabled = value;
				OnPropertyChanged("IsChannelEnabled");
				OnPropertyChanged("EffectiveChannel");
				OnPropertyChanged("ChannelApplyPending");
			}
		}
	}

	public bool ShowLoadingError
	{
		get
		{
			return _showLoadingError;
		}
		private set
		{
			if (_showLoadingError != value)
			{
				_showLoadingError = value;
				OnPropertyChanged("ShowLoadingError");
			}
		}
	}

	public bool ShowChannelWarning
	{
		get
		{
			return _showChannelWarning;
		}
		private set
		{
			if (_showChannelWarning != value)
			{
				_showChannelWarning = value;
				OnPropertyChanged("ShowChannelWarning");
			}
		}
	}

	public DeployInfo? ChannelDeployInfo
	{
		get
		{
			return _channelDeployInfo;
		}
		private set
		{
			if (_channelDeployInfo != value)
			{
				_channelDeployInfo = value;
				OnPropertyChanged("ChannelDeployInfo");
			}
		}
	}

	public string ChannelInfoLoadingText
	{
		get
		{
			return _channelInfoLoadingText;
		}
		private set
		{
			if (_channelInfoLoadingText != value)
			{
				_channelInfoLoadingText = value;
				OnPropertyChanged("ChannelInfoLoadingText");
			}
		}
	}

	public bool VoidNotify
	{
		get
		{
			return App.Settings.Prop.VoidNotify;
		}
		set
		{
			App.Settings.Prop.VoidNotify = value;
		}
	}

	public string BufferSizeKbte
	{
		get
		{
			return App.Settings.Prop.BufferSizeKbte;
		}
		set
		{
			App.Settings.Prop.BufferSizeKbte = value;
		}
	}

	public string BufferSizeKbtes
	{
		get
		{
			return App.Settings.Prop.BufferSizeKbtes;
		}
		set
		{
			App.Settings.Prop.BufferSizeKbtes = value;
		}
	}

	public IReadOnlyList<int> DownloadBufferOptions => DownloadConfiguration.BufferChoices;

	public int DownloadBufferKb
	{
		get
		{
			return App.Settings.Prop.DownloadBufferKb;
		}
		set
		{
			int normalized = DownloadConfiguration.NormalizeBuffer(value);
			if (App.Settings.Prop.DownloadBufferKb == normalized)
				return;
			App.Settings.Prop.DownloadBufferKb = normalized;
			OnPropertyChanged("DownloadBufferKb");
			OnPropertyChanged("DownloadConfigurationSummary");
			App.Settings.SaveDeferred();
		}
	}

	public IReadOnlyList<int> ConcurrentDownloadOptions => DownloadConfiguration.ConcurrentChoices;

	public int MaxConcurrentDownloads
	{
		get
		{
			return App.Settings.Prop.MaxConcurrentDownloads;
		}
		set
		{
			int normalized = DownloadConfiguration.NormalizeConcurrent(value);
			if (App.Settings.Prop.MaxConcurrentDownloads == normalized)
				return;
			App.Settings.Prop.MaxConcurrentDownloads = normalized;
			OnPropertyChanged("MaxConcurrentDownloads");
			OnPropertyChanged("DownloadConfigurationSummary");
			App.Settings.SaveDeferred();
		}
	}

	public IReadOnlyList<int> DownloadSegmentOptions => DownloadConfiguration.SegmentChoices;

	public int MaxDownloadSegments
	{
		get
		{
			return App.Settings.Prop.MaxDownloadSegments;
		}
		set
		{
			int normalized = DownloadConfiguration.NormalizeSegments(value);
			if (App.Settings.Prop.MaxDownloadSegments == normalized)
				return;
			App.Settings.Prop.MaxDownloadSegments = normalized;
			OnPropertyChanged("MaxDownloadSegments");
			OnPropertyChanged("DownloadConfigurationSummary");
			App.Settings.SaveDeferred();
		}
	}

	public string DownloadConfigurationSummary => $"{MaxConcurrentDownloads} package workers, {MaxDownloadSegments} parts per large package, {DownloadConfiguration.ResolveSegmentRequestLimit(App.Settings.Prop)} maximum ranged requests";

	public bool StaticDirectory
	{
		get
		{
			return App.Settings.Prop.StaticDirectory;
		}
		set
		{
			if (App.Settings.Prop.StaticDirectory == value)
				return;
			App.Settings.Prop.StaticDirectory = value;
			new Voidstrap.AppData.RobloxPlayerData().TryMigrateInstallDirectory(value);
			new Voidstrap.AppData.RobloxStudioData().TryMigrateInstallDirectory(value);
			OnPropertyChanged("StaticDirectory");
			App.Settings.Save();
		}
	}

	public string ViewChannel
	{
		get
		{
			return _viewChannel ?? App.Settings?.Prop?.Channel ?? Deployment.DefaultChannel;
		}
		set
		{
			string incoming = value ?? string.Empty;
			if (_viewChannel == incoming)
			{
				return;
			}
			_viewChannel = incoming;
			OnPropertyChanged("ViewChannel");
			OnPropertyChanged("ChannelApplyPending");
		}
	}

	public string EffectiveChannel
	{
		get
		{
			if (App.Settings?.Prop?.IsChannelEnabled != true)
			{
				return Deployment.DefaultChannel;
			}
			string typed = (_viewChannel ?? App.Settings?.Prop?.Channel ?? Deployment.DefaultChannel).Trim();
			return (typed.Length == 0) ? Deployment.DefaultChannel : typed;
		}
	}

	public bool ChannelApplyPending => !string.Equals(EffectiveChannel, App.Settings?.Prop?.Channel ?? Deployment.DefaultChannel, StringComparison.OrdinalIgnoreCase);

	public ICommand ApplyChannelCommand => _applyChannelCommand ?? (_applyChannelCommand = new RelayCommand(ApplyChannel));

	private void ApplyChannel()
	{
		string channel = EffectiveChannel;
		bool changed = !string.Equals(channel, App.Settings?.Prop?.Channel ?? Deployment.DefaultChannel, StringComparison.OrdinalIgnoreCase);

		_viewChannel = channel;
		OnPropertyChanged("ViewChannel");

		try
		{
			if (App.Settings?.Prop != null)
			{
				App.Settings.Prop.Channel = channel;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("ChannelViewModel::ApplyChannel", "Could not save the channel: " + ex.Message);
		}

		if (changed)
		{
			try
			{
				DeleteDirectorySafe(Paths.Versions);
				DeleteDirectorySafe(Paths.Downloads);
				DeleteRobloxLocalStorageFiles();
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ChannelViewModel::ApplyChannel", "Could not clear the previous channel files: " + ex.Message);
			}
		}

		OnPropertyChanged("ChannelApplyPending");
		RunSafeAsync(() => LoadChannelDeployInfoAsync(channel));
	}

	public bool NetworkStreamingEnabled
	{
		get
		{
			return _networkStreamingEnabled;
		}
		set
		{
			if (!_disposed && _networkStreamingEnabled != value)
			{
				_networkStreamingEnabled = value;
				OnPropertyChanged("NetworkStreamingEnabled");
				RunSafeAsync(() => SaveNetworkStreamingStateAsync(value, _lifetimeCts.Token));
			}
		}
	}

	public string ChannelHash
	{
		get
		{
			return App.Settings.Prop.ChannelHash;
		}
		set
		{
			if (string.IsNullOrEmpty(value) || Regex.IsMatch(value, "version-(.*)"))
			{
				App.Settings.Prop.ChannelHash = value;
			}
		}
	}

	public bool UpdateRoblox
	{
		get
		{
			return App.Settings.Prop.UpdateRoblox;
		}
		set
		{
			App.Settings.Prop.UpdateRoblox = value;
		}
	}

	public bool ForceRobloxReinstallation
	{
		get
		{
			return App.Settings.Prop.ForceRobloxReinstall;
		}
		set
		{
			if (App.Settings.Prop.ForceRobloxReinstall == value)
				return;

			App.Settings.Prop.ForceRobloxReinstall = value;
			App.Settings.Save();
			App.Logger.WriteLine("ChannelViewModel::ForceRobloxReinstallation", value
				? "Roblox will be reinstalled on the next launch"
				: "Reinstall on next launch cancelled");
			OnPropertyChanged("ForceRobloxReinstallation");
		}
	}

	public bool HWAccelEnabled
	{
		get
		{
			return !_hardwareAccelerationDisabled;
		}
		set
		{
			SetHardwareAccelerationDisabled(!value);
		}
	}

	public bool HWAccelDisabled
	{
		get
		{
			return _hardwareAccelerationDisabled;
		}
		set
		{
			SetHardwareAccelerationDisabled(value);
		}
	}

	private void SetHardwareAccelerationDisabled(bool value)
	{
		if (_hardwareAccelerationDisabled == value)
		{
			return;
		}

		_hardwareAccelerationDisabled = value;
		OnPropertyChanged("HWAccelDisabled");
		OnPropertyChanged("HWAccelEnabled");
		RestartNotificationService.TrackApplicationSetting(
			HardwareAccelerationRestartKey,
			value,
			"Hardware acceleration changed",
			value ? Resources.Strings.Menu_Channel_HWAccel_DisableRestart : Resources.Strings.Menu_Channel_HWAccel_EnableRestart,
			value ? ApplyHardwareAccelerationDisabled : ApplyHardwareAccelerationEnabled);
	}

	private static void ApplyHardwareAccelerationDisabled()
	{
		App.Settings.Prop.WPFSoftwareRender = true;
		App.Settings.SaveDeferred();
	}

	private static void ApplyHardwareAccelerationEnabled()
	{
		App.Settings.Prop.WPFSoftwareRender = false;
		App.Settings.SaveDeferred();
	}

	public bool VoidRPC
	{
		get
		{
			return App.Settings.Prop.VoidRPC;
		}
		set
		{
			if (App.Settings.Prop.VoidRPC == value)
			{
				return;
			}
			App.Settings.Prop.VoidRPC = value;
			try
			{
				if (Application.Current != null)
				{
					foreach (Window window in Application.Current.Windows)
					{
						if (window is MainWindow mainWindow)
						{
							mainWindow.ToggleDiscordRPC(value);
							break;
						}
					}
				}
			}
			catch (Exception ex)
			{
				App.Logger.WriteLine("ChannelViewModel", "Live RPC toggle failed: " + ex.Message);
			}
			OnPropertyChanged("VoidRPC");
		}
	}

	public string CurrentInstallLocation => Paths.Base;

	public string InstallLocationText
	{
		get
		{
			return _installLocationText ?? (_installLocationText = MaskUserName(Paths.Base));
		}
		set
		{
			if (_installLocationText != value)
			{
				_installLocationText = value;
				OnPropertyChanged("InstallLocationText");
				OnPropertyChanged("MoveButtonVisibility");
			}
		}
	}

	public Visibility MoveButtonVisibility
	{
		get
		{
			string text = UnmaskUserName((InstallLocationText ?? string.Empty).Trim());
			if (string.IsNullOrWhiteSpace(text))
			{
				return Visibility.Collapsed;
			}
			if (!string.Equals(text.TrimEnd('\\'), Paths.Base.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
			{
				return Visibility.Visible;
			}
			return Visibility.Collapsed;
		}
	}

	public ICommand BrowseInstallLocationCommand => _browseInstallLocationCommand ?? (_browseInstallLocationCommand = new RelayCommand(BrowseInstallLocation));

	public ICommand ApplyInstallLocationCommand => _applyInstallLocationCommand ?? (_applyInstallLocationCommand = new RelayCommand(ApplyInstallLocation));

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged(string propertyName)
	{
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}

	public ChannelViewModel()
	{
		bool savedHardwareAccelerationDisabled = App.Settings.Prop.WPFSoftwareRender;
		RestartNotificationService.RegisterSetting(HardwareAccelerationRestartKey, savedHardwareAccelerationDisabled);
		_hardwareAccelerationDisabled = RestartNotificationService.TryGetPendingValue(HardwareAccelerationRestartKey, out bool pendingHardwareAccelerationDisabled)
			? pendingHardwareAccelerationDisabled
			: savedHardwareAccelerationDisabled;
		if (DownloadConfiguration.Normalize(App.Settings.Prop))
		{
			App.Settings.SaveDeferred();
		}
		_revertTimer = new DispatcherTimer
		{
			Interval = TimeSpan.FromSeconds(15)
		};
		_revertTimer.Tick += OnRevertTimerTick;
		LoadMonitors();
		RunSafeAsync(() => LoadNetworkStreamingStateAsync(_lifetimeCts.Token));
		CpuLimitOptions = new ObservableCollection<int>();
		int processorCount = Environment.ProcessorCount;
		for (int i = 1; i <= processorCount; i++)
		{
			CpuLimitOptions.Add(i);
		}
		if (!CpuLimitOptions.Contains(App.Settings.Prop.CpuCoreLimit))
		{
			SelectedCpuLimit = processorCount;
		}
		PriorityOptions = new ObservableCollection<string> { "High", "Above Normal", "Normal", "Below Normal", "Low" };
		_selectedPriority = NormalizePriority(App.Settings.Prop.PriorityLimit);
		if (!string.Equals(App.Settings.Prop.PriorityLimit, _selectedPriority, StringComparison.Ordinal))
		{
			App.Settings.Prop.PriorityLimit = _selectedPriority;
			App.Settings.SaveDeferred();
		}
		LoadChannelDeployInfoSafeAsync(App.Settings.Prop.Channel);
	}

	private static string NormalizePriority(string? priority)
	{
		if (priority?.Equals("High", StringComparison.OrdinalIgnoreCase) == true || priority?.Equals("Realtime", StringComparison.OrdinalIgnoreCase) == true || priority?.Equals("RealTime", StringComparison.OrdinalIgnoreCase) == true)
		{
			return "High";
		}
		if (priority?.Equals("Above Normal", StringComparison.OrdinalIgnoreCase) == true || priority?.Equals("AboveNormal", StringComparison.OrdinalIgnoreCase) == true)
		{
			return "Above Normal";
		}
		if (priority?.Equals("Below Normal", StringComparison.OrdinalIgnoreCase) == true || priority?.Equals("BelowNormal", StringComparison.OrdinalIgnoreCase) == true)
		{
			return "Below Normal";
		}
		if (priority?.Equals("Low", StringComparison.OrdinalIgnoreCase) == true || priority?.Equals("Idle", StringComparison.OrdinalIgnoreCase) == true)
		{
			return "Low";
		}
		return "Normal";
	}

	private void LoadMonitors(string? preserveDevice = null)
	{
		List<DisplayInfo> displays = DisplaySystem.GetDisplays();
		int minX = displays.Min((DisplayInfo d) => d.X);
		int minY = displays.Min((DisplayInfo d) => d.Y);
		int maxX = displays.Max((DisplayInfo d) => d.X + d.Width);
		int maxY = displays.Max((DisplayInfo d) => d.Y + d.Height);
		double bbWidth = Math.Max(1, maxX - minX);
		double bbHeight = Math.Max(1, maxY - minY);
		double scale = Math.Min((MonitorCanvasWidth - MonitorCanvasMargin * 2.0) / bbWidth, (MonitorCanvasHeight - MonitorCanvasMargin * 2.0) / bbHeight);
		double offsetX = (MonitorCanvasWidth - bbWidth * scale) / 2.0;
		double offsetY = (MonitorCanvasHeight - bbHeight * scale) / 2.0;
		Monitors.Clear();
		foreach (DisplayInfo display in displays)
		{
			Monitors.Add(new MonitorTile
			{
				DeviceName = display.DeviceName,
				FriendlyName = display.FriendlyName,
				Number = display.Number,
				IsPrimary = display.IsPrimary,
				CanvasX = offsetX + (display.X - minX) * scale + MonitorBoxInset,
				CanvasY = offsetY + (display.Y - minY) * scale + MonitorBoxInset,
				BoxWidth = Math.Max(36.0, display.Width * scale - MonitorBoxInset * 2.0),
				BoxHeight = Math.Max(24.0, display.Height * scale - MonitorBoxInset * 2.0)
			});
		}
		MonitorTile? target = null;
		if (preserveDevice != null)
		{
			target = Monitors.FirstOrDefault((MonitorTile m) => m.DeviceName == preserveDevice);
		}
		if (target == null)
		{
			string? saved = App.Settings.Prop.InGameResolution?.Monitor;
			if (!string.IsNullOrEmpty(saved))
			{
				target = Monitors.FirstOrDefault((MonitorTile m) => m.DeviceName == saved);
			}
		}
		target ??= Monitors.FirstOrDefault((MonitorTile m) => m.IsPrimary) ?? Monitors.FirstOrDefault();
		if (target != null)
		{
			SelectMonitor(target);
		}
	}

	private void SelectMonitorFromUi(MonitorTile? tile)
	{
		if (tile != null && !object.ReferenceEquals(tile, _selectedMonitor))
		{
			SelectMonitor(tile);
		}
	}

	private void SelectMonitor(MonitorTile tile)
	{
		foreach (MonitorTile monitor in Monitors)
		{
			monitor.IsSelected = object.ReferenceEquals(monitor, tile);
		}
		_selectedMonitor = tile;
		LoadResolutionsForSelectedMonitor();
		OnPropertyChanged("SelectedMonitorSummary");
		OnPropertyChanged("SelectedResolutionInGame");
	}

	private void LoadResolutionsForSelectedMonitor()
	{
		AvailableResolutions.Clear();
		AvailableResolutionsInGame.Clear();
		foreach (DisplayMode mode in DisplaySystem.GetModes(_selectedMonitor?.DeviceName))
		{
			AvailableResolutions.Add(mode);
			AvailableResolutionsInGame.Add(mode);
		}
		SyncSelectedResolutionToCurrent();
	}

	private void SyncSelectedResolutionToCurrent()
	{
		DisplayMode? current = DisplaySystem.GetCurrentMode(_selectedMonitor?.DeviceName);
		_suppressResolutionApply = true;
		_selectedResolution = ((current == null) ? null : AvailableResolutions.FirstOrDefault((DisplayMode m) => m.Width == current.Width && m.Height == current.Height && m.RefreshRate == current.RefreshRate));
		OnPropertyChanged("SelectedResolution");
		_suppressResolutionApply = false;
	}

	private void ApplyResolutionToSelected(DisplayMode mode)
	{
		string? device = _selectedMonitor?.DeviceName;
		DisplayMode? previous = DisplaySystem.GetCurrentMode(device);
		if (previous != null && previous.Width == mode.Width && previous.Height == mode.Height && previous.RefreshRate == mode.RefreshRate)
		{
			return;
		}
		int code = DisplaySystem.ApplyMode(device, mode.Width, mode.Height, mode.RefreshRate);
		if (code != DisplaySystem.Success)
		{
			Frontend.ShowMessageBox("Failed to change resolution: " + DisplaySystem.DescribeError(code), MessageBoxImage.Hand);
			SyncSelectedResolutionToCurrent();
			return;
		}
		if (previous != null)
		{
			_revertDevice = device;
			_revertMode = previous;
			_autoReverted = false;
			_revertTimer.Start();
			MessageBoxResult result = Frontend.ShowMessageBox("Keep this resolution? It will revert automatically in 15 seconds if you do not confirm.", MessageBoxImage.Question, MessageBoxButton.YesNo);
			_revertTimer.Stop();
			if (_autoReverted || result != MessageBoxResult.Yes)
			{
				if (!_autoReverted)
				{
					DisplaySystem.ApplyMode(device, previous.Width, previous.Height, previous.RefreshRate);
				}
			}
			_revertMode = null;
			_revertDevice = null;
		}
		LoadMonitors(device);
	}

	private void OnRevertTimerTick(object? sender, EventArgs e)
	{
		_revertTimer.Stop();
		_autoReverted = true;
		if (_revertMode != null)
		{
			DisplaySystem.ApplyMode(_revertDevice, _revertMode.Width, _revertMode.Height, _revertMode.RefreshRate);
		}
	}

	private void ClearInGameResolution()
	{
		SelectedResolutionInGame = null;
	}

	private static void DeleteRobloxLocalStorageFiles()
	{
		try
		{
			string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "LocalStorage");
			if (!Directory.Exists(path))
			{
				return;
			}
			string[] files = Directory.GetFiles(path, "memProfStorage*.json", SearchOption.TopDirectoryOnly);
			foreach (string path2 in files)
			{
				try
				{
					File.Delete(path2);
				}
				catch (Exception)
				{
				}
			}
		}
		catch (Exception)
		{
		}
	}

	private static void DeleteDirectorySafe(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
		{
			return;
		}
		try
		{
			Directory.Delete(path, recursive: true);
		}
		catch (Exception)
		{
		}
	}

	private async Task LoadNetworkStreamingStateAsync(CancellationToken token)
	{
		try
		{
			token.ThrowIfCancellationRequested();
			if (!Directory.Exists(_robloxLocalStorage))
			{
				SetNetworkStreamingState(false);
				return;
			}
			string[] files = Directory.GetFiles(_robloxLocalStorage, "memProfStorage*.json", SearchOption.TopDirectoryOnly);
			if (files.Length == 0)
			{
				SetNetworkStreamingState(false);
				return;
			}
			bool? foundValue = null;
			string[] array = files;
			foreach (string path in array)
			{
				try
				{
					token.ThrowIfCancellationRequested();
					Match match = Regex.Match(await JsonFile.ReadTextAsync(path, MaximumLocalStorageBytes, token), "\"NetworkStreamingEnabled\"\\s*:\\s*\"?(\\d+)\"?");
					if (match.Success && int.TryParse(match.Groups[1].Value, out var result))
					{
						foundValue = result == 1;
						break;
					}
				}
				catch (IOException)
				{
				}
			}
			SetNetworkStreamingState(foundValue == true);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception)
		{
			SetNetworkStreamingState(false);
		}
	}

	private void SetNetworkStreamingState(bool value)
	{
		if (_disposed || _networkStreamingEnabled == value)
		{
			return;
		}
		_networkStreamingEnabled = value;
		OnPropertyChanged("NetworkStreamingEnabled");
	}

	private async Task SaveNetworkStreamingStateAsync(bool isEnabled, CancellationToken token)
	{
		try
		{
			token.ThrowIfCancellationRequested();
			if (!Directory.Exists(_robloxLocalStorage))
			{
				return;
			}
			string[] files = Directory.GetFiles(_robloxLocalStorage, "memProfStorage*.json", SearchOption.TopDirectoryOnly);
			string[] array = files;
			foreach (string file in array)
			{
				try
				{
					token.ThrowIfCancellationRequested();
					string text = await JsonFile.ReadTextAsync(file, MaximumLocalStorageBytes, token);
					if (text.Contains("\"NetworkStreamingEnabled\""))
					{
						text = Regex.Replace(text, "\"NetworkStreamingEnabled\"\\s*:\\s*\"?\\d+\"?", $"\"NetworkStreamingEnabled\":\"{(isEnabled ? 1 : 0)}\"");
					}
					else
					{
						text = text.TrimEnd(new char[4] { '}', ' ', '\n', '\r' });
						text += $", \"NetworkStreamingEnabled\":\"{(isEnabled ? 1 : 0)}\" }}";
					}
					await Task.Run(() => JsonFile.WriteAtomicText(file, text, false), token);
				}
				catch (IOException)
				{
				}
			}
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested)
		{
		}
		catch (Exception)
		{
		}
	}

	private static string MaskUserName(string path)
	{
		try
		{
			string userProfile = Paths.UserProfile;
			if (!string.IsNullOrEmpty(userProfile) && path.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase))
			{
				string text = Directory.GetParent(userProfile)?.FullName;
				if (!string.IsNullOrEmpty(text))
				{
					return text + "\\user" + path.Substring(userProfile.Length);
				}
			}
		}
		catch
		{
		}
		return path;
	}

	private static string UnmaskUserName(string path)
	{
		try
		{
			string userProfile = Paths.UserProfile;
			string text = Directory.GetParent(userProfile)?.FullName;
			if (string.IsNullOrEmpty(text))
			{
				return path;
			}
			string text2 = text + "\\user";
			if (path.Equals(text2, StringComparison.OrdinalIgnoreCase))
			{
				return userProfile;
			}
			if (path.StartsWith(text2 + "\\", StringComparison.OrdinalIgnoreCase))
			{
				return userProfile + path.Substring(text2.Length);
			}
		}
		catch
		{
		}
		return path;
	}

	private void BrowseInstallLocation()
	{
		OpenFolderDialog openFolderDialog = new OpenFolderDialog
		{
			Title = "Choose a new Voidstrap install location",
			Multiselect = false
		};
		if (openFolderDialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(openFolderDialog.FolderName))
		{
			InstallLocationText = MaskUserName(Path.Combine(openFolderDialog.FolderName, "Voidstrap"));
		}
	}

	private void ApplyInstallLocation()
	{
		string text = UnmaskUserName((InstallLocationText ?? string.Empty).Trim());
		if (!string.IsNullOrWhiteSpace(text))
		{
			if (string.Equals(text.TrimEnd('\\'), Paths.Base.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
			{
				Frontend.ShowMessageBox("Voidstrap is already installed at that location.", MessageBoxImage.Asterisk);
			}
			else if (Frontend.ShowMessageBox("Voidstrap will be moved to:\n" + text + "\n\nIt will restart automatically when done. Close Roblox first if it is running.\n\nContinue?", MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No) == MessageBoxResult.Yes)
			{
				Voidstrap.Installer.RelocateInstall(text);
			}
		}
	}

	private async Task LoadChannelDeployInfoSafeAsync(string channel)
	{
		try
		{
			await LoadChannelDeployInfoAsync(channel);
		}
		catch (Exception)
		{
		}
	}

	private async Task LoadChannelDeployInfoAsync(string channel)
	{
		if (_disposed)
		{
			return;
		}
		_loadChannelCts?.Cancel();
		_loadChannelCts?.Dispose();
		_loadChannelCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
		CancellationToken token = _loadChannelCts.Token;
		ShowLoadingError = false;
		ChannelDeployInfo = null;
		ChannelInfoLoadingText = "Fetching latest deploy info, please wait...";
		ShowChannelWarning = false;
		try
		{
			ClientVersion clientVersion = await Deployment.GetInfo(channel);
			token.ThrowIfCancellationRequested();
			if (!token.IsCancellationRequested)
			{
				ShowChannelWarning = clientVersion.IsBehindDefaultChannel;
				ChannelDeployInfo = new DeployInfo
				{
					Version = clientVersion.Version,
					VersionGuid = clientVersion.VersionGuid
				};
				App.State.Prop.IgnoreOutdatedChannel = true;
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			if (!token.IsCancellationRequested)
			{
				ShowLoadingError = true;
				ChannelInfoLoadingText = ex2 is HttpRequestException requestError && Deployment.BadChannelCodes.Contains(requestError.StatusCode)
					? "The channel is unavailable or private. Please change the channel or try again later.\nError: " + ex2.Message
					: "Roblox deployment services could not be reached. Please check your connection and try again.\nError: " + ex2.Message;
			}
		}
	}

	private async void RunSafeAsync(Func<Task> asyncFunc)
	{
		try
		{
			await asyncFunc().ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
		{
		}
		catch (Exception value)
		{
			Console.Error.WriteLine($"Error in background task: {value}");
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_revertTimer.Stop();
		_revertTimer.Tick -= OnRevertTimerTick;
		_loadChannelCts?.Cancel();
		_lifetimeCts.Cancel();
		_loadChannelCts?.Dispose();
		_loadChannelCts = null;
		_lifetimeCts.Dispose();
		_disposed = true;
		GC.SuppressFinalize(this);
	}
}
