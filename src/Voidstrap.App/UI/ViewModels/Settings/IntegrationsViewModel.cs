using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.ContextMenu;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public class IntegrationsViewModel : NotifyPropertyChangedViewModel, IDisposable
{
	public class RpcIdleIconOption
	{
		public string Key { get; set; } = "";

		public string Name { get; set; } = "";

		public override string ToString()
		{
			return Name;
		}
	}

	private readonly ActivityWatcher _watcher;
	private readonly bool _ownsWatcher;
	private readonly CancellationTokenSource _lifetimeCts = new();
	private bool _disposed;
	private bool _blockTelemetry;
	private int _telemetryVersion;

	private CustomIntegration? _selectedCustomIntegration;

	public ICommand AddIntegrationCommand => new RelayCommand(AddIntegration);

	public ICommand DeleteIntegrationCommand => new RelayCommand(DeleteIntegration);

	public ICommand BrowseIntegrationLocationCommand => new RelayCommand(BrowseIntegrationLocation);

	public ICommand OpenHistoryWindowCommand { get; }

	public ICommand MusicWindowCommand { get; }

	public ICommand RPCWindowCommand { get; }

	public ICommand AccountWindowCommand { get; }

	public bool DuckRobloxAudio
	{
		get
		{
			return App.Settings.Prop.DuckRobloxAudioOnUnfocus;
		}
		set
		{
			if (App.Settings.Prop.DuckRobloxAudioOnUnfocus == value)
			{
				return;
			}
			SettingChangeResult result = SettingChangeNotifier.Try(
				"IntegrationsViewModel::DuckRobloxAudio",
				"The Roblox audio setting could not be changed.",
				() =>
				{
					if (value)
					{
						if (!Integrations.AudioDucker.Start())
						{
							throw new InvalidOperationException("Audio ducking could not start");
						}
					}
					else
					{
						Integrations.AudioDucker.Stop();
						Integrations.AudioDucker.MarkResetOnNextLaunch();
					}
				});
			if (!result.Success)
			{
				OnPropertyChanged(nameof(DuckRobloxAudio));
				return;
			}
			App.Settings.Prop.DuckRobloxAudioOnUnfocus = value;
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(DuckRobloxAudio));
		}
	}

	public bool HeadsetLoudness
	{
		get
		{
			return App.Settings.Prop.EnableHeadsetLoudness;
		}
		set
		{
			if (App.Settings.Prop.EnableHeadsetLoudness == value)
			{
				return;
			}
			SettingChangeResult result = SettingChangeNotifier.Try(
				"IntegrationsViewModel::HeadsetLoudness",
				"The headset audio setting could not be changed.",
				() =>
				{
					if (value)
					{
						if (!Integrations.HeadsetAudio.Start())
						{
							throw new InvalidOperationException("Headset audio could not start");
						}
					}
					else
					{
						Integrations.HeadsetAudio.Stop();
					}
				});
			if (!result.Success)
			{
				OnPropertyChanged(nameof(HeadsetLoudness));
				return;
			}
			App.Settings.Prop.EnableHeadsetLoudness = value;
			App.Settings.SaveDeferred();
			OnPropertyChanged(nameof(HeadsetLoudness));
		}
	}

	public bool BlockTelemetry
	{
		get
		{
			return _blockTelemetry;
		}
		set
		{
			if (_blockTelemetry == value)
			{
				return;
			}
			_blockTelemetry = value;
			OnPropertyChanged("BlockTelemetry");
			int version = Interlocked.Increment(ref _telemetryVersion);
			_ = ApplyTelemetrySettingAsync(value, version);
		}
	}

	public bool RobloxSystemTray
	{
		get
		{
			return App.Settings.Prop.RobloxMinimizeToTray;
		}
		set
		{
			if (App.Settings.Prop.RobloxMinimizeToTray == value)
			{
				return;
			}
			ApplyAppStorage(nameof(RobloxSystemTray), "The Roblox system tray setting could not be saved.", value, App.Settings.Prop.RobloxLaunchAtStartup);
		}
	}

	public bool LaunchStartup
	{
		get
		{
			return App.Settings.Prop.RobloxLaunchAtStartup;
		}
		set
		{
			if (App.Settings.Prop.RobloxLaunchAtStartup == value)
			{
				return;
			}
			ApplyAppStorage(nameof(LaunchStartup), "The Roblox startup setting could not be saved.", App.Settings.Prop.RobloxMinimizeToTray, value);
		}
	}

	private void ApplyAppStorage(string propertyName, string failureMessage, bool minimizeToTray, bool launchAtStartup)
	{
		SettingChangeResult result = SettingChangeNotifier.Try(
			"IntegrationsViewModel::" + propertyName,
			failureMessage,
			() =>
			{
				if (!RobloxAppStorage.Apply(minimizeToTray, launchAtStartup))
				{
					throw new InvalidOperationException("The Roblox app storage file could not be written");
				}
			});
		if (result.Success)
		{
			App.Settings.Prop.RobloxMinimizeToTray = minimizeToTray;
			App.Settings.Prop.RobloxLaunchAtStartup = launchAtStartup;
			App.Settings.SaveDeferred();
		}
		OnPropertyChanged(propertyName);
	}

	public bool ActivityTrackingEnabled
	{
		get
		{
			return App.Settings.Prop.EnableActivityTracking;
		}
		set
		{
			if (App.Settings.Prop.EnableActivityTracking == value)
			{
				return;
			}
			App.Settings.Prop.EnableActivityTracking = value;
			SaveAppSetting(nameof(ActivityTrackingEnabled));
			OnPropertyChanged(nameof(ShowServerDetailsEnabled));
			OnPropertyChanged(nameof(DisableAppPatchEnabled));
			OnPropertyChanged(nameof(DiscordActivityEnabled));
			OnPropertyChanged(nameof(DiscordActivityJoinEnabled));
		}
	}

	public bool ShowServerDetailsEnabled
	{
		get
		{
			return App.Settings.Prop.ShowServerDetails;
		}
		set
		{
			if (App.Settings.Prop.ShowServerDetails == value)
			{
				return;
			}
			App.Settings.Prop.ShowServerDetails = value;
			SaveAppSetting(nameof(ShowServerDetailsEnabled));
		}
	}

	public bool joinGameNotify
	{
		get
		{
			return App.Settings.Prop.NotificationWindowShow;
		}
		set
		{
			if (App.Settings.Prop.NotificationWindowShow == value)
			{
				return;
			}
			App.Settings.Prop.NotificationWindowShow = value;
			SaveAppSetting(nameof(joinGameNotify));
		}
	}

	public bool ExitOnDissy
	{
		get
		{
			return App.Settings.Prop.ExitOnDissy;
		}
		set
		{
			if (App.Settings.Prop.ExitOnDissy == value)
			{
				return;
			}
			App.Settings.Prop.ExitOnDissy = value;
			SaveAppSetting(nameof(ExitOnDissy));
		}
	}

	public string gamename
	{
		get
		{
			return App.Settings.Prop.CustomGameName;
		}
		set
		{
			if (App.Settings.Prop.CustomGameName == value)
			{
				return;
			}
			App.Settings.Prop.CustomGameName = value;
			SaveAppSetting(nameof(gamename));
		}
	}

	public bool GameWIP
	{
		get
		{
			return App.Settings.Prop.GameWIP;
		}
		set
		{
			if (App.Settings.Prop.GameWIP == value)
			{
				return;
			}
			App.Settings.Prop.GameWIP = value;
			SaveAppSetting(nameof(GameWIP));
		}
	}

	public bool FFlagAmountRPC
	{
		get
		{
			return App.Settings.Prop.FFlagRPCDisplayer;
		}
		set
		{
			if (App.Settings.Prop.FFlagRPCDisplayer == value)
			{
				return;
			}
			App.Settings.Prop.FFlagRPCDisplayer = value;
			SaveAppSetting(nameof(FFlagAmountRPC));
		}
	}

	public bool ServerUptimeBetterBLOXcuzitsbetterXD
	{
		get
		{
			return App.Settings.Prop.ServerUptimeBetterBLOXcuzitsbetterXD;
		}
		set
		{
			if (App.Settings.Prop.ServerUptimeBetterBLOXcuzitsbetterXD == value)
			{
				return;
			}
			App.Settings.Prop.ServerUptimeBetterBLOXcuzitsbetterXD = value;
			SaveAppSetting(nameof(ServerUptimeBetterBLOXcuzitsbetterXD));
		}
	}

	public string gameimage
	{
		get
		{
			return App.Settings.Prop.UseCustomIcon;
		}
		set
		{
			if (App.Settings.Prop.UseCustomIcon == value)
			{
				return;
			}
			App.Settings.Prop.UseCustomIcon = value;
			SaveAppSetting(nameof(gameimage));
		}
	}

	public bool PlayerLogsEnabled
	{
		get
		{
			return ActivityWatcher.PlayerLoggingEnabled;
		}
		set
		{
			App.FastFlags.SetPreset("Players.EventLog", value ? "7" : null);
			App.FastFlags.SetPreset("Players.LogLevel", value ? "trace" : null);
			App.FastFlags.SetPreset("Players.LogPattern", value ? "ExpChat/mountClientApp" : null);
		}
	}

	public bool DiscordActivityEnabled
	{
		get
		{
			return App.Settings.Prop.UseDiscordRichPresence;
		}
		set
		{
			if (App.Settings.Prop.UseDiscordRichPresence == value)
			{
				return;
			}
			App.Settings.Prop.UseDiscordRichPresence = value;
			SaveAppSetting(nameof(DiscordActivityEnabled));
			OnPropertyChanged(nameof(DiscordActivityJoinEnabled));
			OnPropertyChanged(nameof(DiscordAccountOnProfile));
			OnPropertyChanged(nameof(GameIconChecked));
			OnPropertyChanged(nameof(ServerLocationGame));
		}
	}

	public bool UncapFPS
	{
		get
		{
			return App.GlobalSettings.GetInt("FramerateCap", 0) > 240;
		}
		set
		{
			if (App.GlobalSettings.SetProperty("FramerateCap", value ? 9999 : 240))
				App.GlobalSettings.Save();
		}
	}

	public bool UncapFpsToggleEnabled => Voidstrap.Integrations.FrameGeneration.FrameGenSettings.ModeIndex == 0;

	public bool DiscordActivityJoinEnabled
	{
		get
		{
			return !App.Settings.Prop.HideRPCButtons;
		}
		set
		{
			if (App.Settings.Prop.HideRPCButtons == !value)
			{
				return;
			}
			App.Settings.Prop.HideRPCButtons = !value;
			SaveAppSetting(nameof(DiscordActivityJoinEnabled));
		}
	}

	public bool DiscordAccountOnProfile
	{
		get
		{
			return App.Settings.Prop.ShowAccountOnRichPresence;
		}
		set
		{
			if (App.Settings.Prop.ShowAccountOnRichPresence == value)
			{
				return;
			}
			App.Settings.Prop.ShowAccountOnRichPresence = value;
			SaveAppSetting(nameof(DiscordAccountOnProfile));
		}
	}

	public bool GameIconChecked
	{
		get
		{
			return App.Settings.Prop.GameIconChecked;
		}
		set
		{
			if (App.Settings.Prop.GameIconChecked == value)
			{
				return;
			}
			App.Settings.Prop.GameIconChecked = value;
			SaveAppSetting(nameof(GameIconChecked));
		}
	}

	public IReadOnlyList<RpcIdleIconOption> RpcIdleIconOptions { get; } = DiscordRichPresence.IdleIconPresets.Select<KeyValuePair<string, (string, string)>, RpcIdleIconOption>((KeyValuePair<string, (string Name, string Url)> kvp) => new RpcIdleIconOption
	{
		Key = kvp.Key,
		Name = kvp.Value.Name
	}).ToList();

	public RpcIdleIconOption SelectedRpcIdleIcon
	{
		get
		{
			string key = App.Settings.Prop.RpcIdleIcon ?? "blue";
			return RpcIdleIconOptions.FirstOrDefault((RpcIdleIconOption x) => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase)) ?? RpcIdleIconOptions[0];
		}
		set
		{
			if (value != null && !string.Equals(App.Settings.Prop.RpcIdleIcon, value.Key, StringComparison.OrdinalIgnoreCase))
			{
				App.Settings.Prop.RpcIdleIcon = value.Key;
				App.Settings.SaveDeferred();
				OnPropertyChanged("SelectedRpcIdleIcon");
			}
		}
	}

	public bool GameNameChecked
	{
		get
		{
			return App.Settings.Prop.GameNameChecked;
		}
		set
		{
			if (App.Settings.Prop.GameNameChecked == value)
			{
				return;
			}
			App.Settings.Prop.GameNameChecked = value;
			SaveAppSetting(nameof(GameNameChecked));
		}
	}

	public bool GameCreatorChecked
	{
		get
		{
			return App.Settings.Prop.GameCreatorChecked;
		}
		set
		{
			if (App.Settings.Prop.GameCreatorChecked == value)
			{
				return;
			}
			App.Settings.Prop.GameCreatorChecked = value;
			SaveAppSetting(nameof(GameCreatorChecked));
		}
	}

	public bool GameStatusChecked
	{
		get
		{
			return App.Settings.Prop.GameStatusChecked;
		}
		set
		{
			if (App.Settings.Prop.GameStatusChecked == value)
			{
				return;
			}
			App.Settings.Prop.GameStatusChecked = value;
			SaveAppSetting(nameof(GameStatusChecked));
		}
	}

	public bool ServerLocationGame
	{
		get
		{
			return App.Settings.Prop.ServerLocationGame;
		}
		set
		{
			if (App.Settings.Prop.ServerLocationGame == value)
			{
				return;
			}
			App.Settings.Prop.ServerLocationGame = value;
			SaveAppSetting(nameof(ServerLocationGame));
		}
	}

	public bool DisableAppPatchEnabled
	{
		get
		{
			return App.Settings.Prop.UseDisableAppPatch;
		}
		set
		{
			if (App.Settings.Prop.UseDisableAppPatch == value)
			{
				return;
			}
			App.Settings.Prop.UseDisableAppPatch = value;
			SaveAppSetting(nameof(DisableAppPatchEnabled));
		}
	}

	public ObservableCollection<CustomIntegration> CustomIntegrations
	{
		get
		{
			return App.Settings.Prop.CustomIntegrations;
		}
		set
		{
			if (ReferenceEquals(App.Settings.Prop.CustomIntegrations, value))
			{
				return;
			}
			App.Settings.Prop.CustomIntegrations = value;
			SaveAppSetting(nameof(CustomIntegrations));
		}
	}

	public CustomIntegration? SelectedCustomIntegration
	{
		get
		{
			return _selectedCustomIntegration;
		}
		set
		{
			if (_selectedCustomIntegration != value)
			{
				_selectedCustomIntegration = value;
				OnPropertyChanged("SelectedCustomIntegration");
				OnPropertyChanged("IsCustomIntegrationSelected");
			}
		}
	}

	public int SelectedCustomIntegrationIndex { get; set; }

	public bool IsCustomIntegrationSelected => SelectedCustomIntegration != null;

	public IntegrationsViewModel(ActivityWatcher watcher, bool ownsWatcher = false)
	{
		_watcher = watcher;
		_ownsWatcher = ownsWatcher;
		_blockTelemetry = App.Settings.Prop.BlockRobloxTelemetry;
		OpenHistoryWindowCommand = new RelayCommand(OpenHistoryWindow);
		MusicWindowCommand = new RelayCommand(MusicPlayerWindow);
		RPCWindowCommand = new RelayCommand(RPCUIWindow);
		AccountWindowCommand = new RelayCommand(AccountWindow);
	}

	private void AddIntegration()
	{
		CustomIntegrations.Add(new CustomIntegration
		{
			Name = Strings.Menu_Integrations_Custom_NewIntegration
		});
		SelectedCustomIntegrationIndex = CustomIntegrations.Count - 1;
		OnPropertyChanged("SelectedCustomIntegrationIndex");
		OnPropertyChanged("IsCustomIntegrationSelected");
	}

	private void SaveAppSetting(string propertyName)
	{
		App.Settings.SaveDeferred();
		OnPropertyChanged(propertyName);
	}

	private void OpenHistoryWindow()
	{
		new ServerHistory(_watcher).Show();
	}

	private void MusicPlayerWindow()
	{
		new MusicPlayer(_watcher).Show();
	}

	private void RPCUIWindow()
	{
		new RPCWindow().Show();
	}

	private void DeleteIntegration()
	{
		if (SelectedCustomIntegration != null)
		{
			CustomIntegrations.Remove(SelectedCustomIntegration);
			if (CustomIntegrations.Count > 0)
			{
				SelectedCustomIntegrationIndex = CustomIntegrations.Count - 1;
				OnPropertyChanged("SelectedCustomIntegrationIndex");
			}
			OnPropertyChanged("IsCustomIntegrationSelected");
		}
	}

	private void BrowseIntegrationLocation()
	{
		if (SelectedCustomIntegration != null)
		{
			OpenFileDialog openFileDialog = new OpenFileDialog
			{
				Filter = Strings.Menu_AllFiles + "|*.*"
			};
			if (openFileDialog.ShowDialog() == true)
			{
				SelectedCustomIntegration.Name = openFileDialog.SafeFileName;
				SelectedCustomIntegration.Location = openFileDialog.FileName;
				OnPropertyChanged("SelectedCustomIntegration");
			}
		}
	}

	private void AccountWindow()
	{
		new AccountManagerWindow().Show();
	}

	private async Task ApplyTelemetrySettingAsync(bool value, int version)
	{
		try
		{
			bool applied = await TelemetryBlocker.SetAsync(value, _lifetimeCts.Token).ConfigureAwait(false);
			if (_disposed || version != Volatile.Read(ref _telemetryVersion))
			{
				return;
			}
			await Application.Current.Dispatcher.InvokeAsync(() =>
			{
				if (_disposed || version != Volatile.Read(ref _telemetryVersion))
				{
					return;
				}
				if (applied)
				{
					App.Settings.Prop.BlockRobloxTelemetry = value;
					App.Settings.SaveDeferred();
					return;
				}
				_blockTelemetry = !value;
				OnPropertyChanged("BlockTelemetry");
				Frontend.ShowMessageBox(value ? "Voidstrap could not enable the telemetry blocker. Administrator approval is required to edit the hosts file." : "Voidstrap could not disable the telemetry blocker. Administrator approval is required to edit the hosts file.", MessageBoxImage.Warning);
			});
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("IntegrationsViewModel::ApplyTelemetrySetting", ex);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_lifetimeCts.Cancel();
		if (_ownsWatcher)
		{
			_watcher.Dispose();
		}
		_lifetimeCts.Dispose();
		GC.SuppressFinalize(this);
	}
}
