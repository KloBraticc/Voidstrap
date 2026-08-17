using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Enums;
using Voidstrap.Models.SettingTasks.Base;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.About;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public class MainWindowViewModel : NotifyPropertyChangedViewModel
{
	public EventHandler? RequestSaveNoticeEvent;

	public EventHandler? RequestSaveLaunchNoticeEvent;

	public EventHandler? RequestCloseWindowEvent;

	private Voidstrap.UI.Elements.Settings.MainWindow.TabItemViewModel _selectedTab;

	public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version.ToString();

	public ICommand OpenAboutCommand => new RelayCommand(OpenAbout);

	public ICommand SaveSettingsCommand => new AsyncRelayCommand(SaveSettingsAsync);

	public ICommand SaveAndLaunchSettingsCommand => new AsyncRelayCommand(SaveAndLaunchSettingsAsync);

	public ICommand CloseWindowCommand => new RelayCommand(CloseWindow);

	public bool TestModeEnabled
	{
		get
		{
			return App.LaunchSettings.TestModeFlag.Active;
		}
		set
		{
			if (value && !App.State.Prop.TestModeWarningShown)
			{
				if (Frontend.ShowMessageBox(Strings.Menu_TestMode_Prompt, MessageBoxImage.Asterisk, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
				{
					OnPropertyChanged(nameof(TestModeEnabled));
					return;
				}
				App.State.Prop.TestModeWarningShown = true;
			}
			App.LaunchSettings.TestModeFlag.Active = value;
		}
	}

	public Voidstrap.UI.Elements.Settings.MainWindow.TabItemViewModel SelectedTab
	{
		get
		{
			return _selectedTab;
		}
		set
		{
			if (_selectedTab != value)
			{
				_selectedTab = value;
				OnPropertyChanged("SelectedTab");
			}
		}
	}

	public ObservableCollection<Voidstrap.UI.Elements.Settings.MainWindow.TabItemViewModel> Tabs { get; set; } = new ObservableCollection<Voidstrap.UI.Elements.Settings.MainWindow.TabItemViewModel>();

	public int SelectedLaunchModeIndex
	{
		get
		{
			return (App.Settings.Prop.LaunchSelectionIndex == 1) ? 1 : 0;
		}
		set
		{
			int num = ((value == 1) ? 1 : 0);
			if (App.Settings.Prop.LaunchSelectionIndex != num)
			{
				App.Settings.Prop.LaunchSelectionIndex = num;
				App.Settings.SaveDeferred();
				OnPropertyChanged("SelectedLaunchModeIndex");
				OnPropertyChanged("SelectedLaunchTargetName");
			}
		}
	}

	public string SelectedLaunchClient
	{
		get
		{
			return App.Settings.Prop.LaunchSelectedClient ?? "";
		}
		set
		{
			string newValue = value ?? "";
			if ((App.Settings.Prop.LaunchSelectedClient ?? "") != newValue)
			{
				App.Settings.Prop.LaunchSelectedClient = newValue;
				App.Settings.SaveDeferred();
				OnPropertyChanged("SelectedLaunchClient");
				OnPropertyChanged("SelectedLaunchTargetName");
			}
		}
	}

	public string SelectedLaunchTargetName
	{
		get
		{
			string code = SelectedLaunchClient;
			if (!string.IsNullOrEmpty(code) && ClassicClients.IsClientInstalled(code))
			{
				var config = ClassicClients.GetInstalledConfig(code);
				return (config != null && !string.IsNullOrWhiteSpace(config.Name)) ? config.Name : code;
			}
			if (SelectedLaunchModeIndex != 1)
			{
				return "Roblox";
			}
			return "Roblox Studio";
		}
	}

	public new event PropertyChangedEventHandler? PropertyChanged;

	private void OpenAbout()
	{
		new Voidstrap.UI.Elements.About.MainWindow().ShowDialog();
	}

	private void CloseWindow()
	{
		RequestCloseWindowEvent?.Invoke(this, EventArgs.Empty);
	}

	private async Task SaveSettingsAsync()
	{
		await TrySaveSettingsAsync(true);
	}

	public async Task<bool> TrySaveSettingsAsync(bool showNotice, bool showErrors = true)
	{
		try
		{
			App.Settings.SaveDeferred();
			App.State.Save();
			App.FastFlags.SaveDeferred();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("MainWindowViewModel::SaveSettings", ex);
			if (showErrors)
			{
				Frontend.ShowMessageBox("Settings could not be saved. Check the logs and try again.", MessageBoxImage.Warning);
			}
			return false;
		}
		int failedTasks = 0;
		foreach (KeyValuePair<string, BaseTask> pendingSettingTask in App.PendingSettingTasks.ToArray())
		{
			BaseTask value = pendingSettingTask.Value;
			if (!value.Changed)
			{
				App.PendingSettingTasks.Remove(pendingSettingTask.Key);
				continue;
			}
			try
			{
				App.Logger.WriteLine("MainWindowViewModel::SaveSettings", $"Executing pending task '{value}'");
				await value.ExecuteAsync();
				App.PendingSettingTasks.Remove(pendingSettingTask.Key);
			}
			catch (Exception ex)
			{
				failedTasks++;
				App.Logger.WriteException("MainWindowViewModel::SaveSettings", ex);
			}
		}
		if (failedTasks > 0)
		{
			if (showErrors)
			{
				Frontend.ShowMessageBox("Some settings could not be saved. Check the logs and try again.", MessageBoxImage.Warning);
			}
			return false;
		}
		if (showNotice)
		{
			RequestSaveNoticeEvent?.Invoke(this, EventArgs.Empty);
		}
		return true;
	}

	public async Task SaveAndLaunchSettingsAsync()
	{
		if (!await TrySaveSettingsAsync(false))
			return;
		RequestSaveLaunchNoticeEvent?.Invoke(this, EventArgs.Empty);
		string code = SelectedLaunchClient;
		if (!string.IsNullOrEmpty(code) && ClassicClients.EngineInstalled && ClassicClients.IsClientInstalled(code))
		{
			LaunchClassicClient(code, SelectedLaunchModeIndex == 1);
			return;
		}
		LaunchHandler.LaunchRoblox((SelectedLaunchModeIndex != 1) ? LaunchMode.Player : LaunchMode.Studio);
	}

	private static void StopVoidstrapRpcForClassic()
	{
		try
		{
			if (Application.Current == null)
			{
				return;
			}
			foreach (Window window in Application.Current.Windows)
			{
				if (window is Voidstrap.UI.Elements.Settings.MainWindow mainWindow)
				{
					mainWindow.ToggleDiscordRPC(false);
					App.Logger.WriteLine("MainWindowViewModel", "Voidstrap RPC stopped because a classic Roblox client is launching.");
					break;
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("MainWindowViewModel", "Failed to stop Voidstrap RPC for classic launch: " + ex.Message);
		}
	}

	private void LaunchClassicClient(string code, bool studio)
	{
		StopVoidstrapRpcForClassic();
		IBootstrapperDialog dialog = App.Settings.Prop.BootstrapperStyle.GetNew();
		dialog.CancelEnabled = true;
		dialog.ProgressStyle = System.Windows.Forms.ProgressBarStyle.Marquee;
		dialog.Message = "Starting Voidstrap";

		var launchCts = new CancellationTokenSource();
		int cancelled = 0;
		dialog.CancelCallback = delegate
		{
			if (System.Threading.Interlocked.Exchange(ref cancelled, 1) != 0)
				return;
			try { launchCts.Cancel(); } catch { }
			Task.Run(delegate
			{
				try { ClassicClients.ShutdownAll(); }
				catch (Exception ex) { App.Logger.WriteLine("MainWindowViewModel", "Classic shutdown failed: " + ex.Message); }
			});
		};

		Task.Run(async delegate
		{
			try
			{
				void Status(string message) => dialog.Message = message;

				using (var cts = CancellationTokenSource.CreateLinkedTokenSource(launchCts.Token))
				{
					cts.CancelAfter(TimeSpan.FromMinutes(10));
					await ClassicClients.UpdateEverythingAsync(code, Status, delegate (double percent, string message)
					{
						dialog.ProgressStyle = System.Windows.Forms.ProgressBarStyle.Continuous;
						dialog.ProgressMaximum = 100;
						dialog.Message = message;
						dialog.ProgressValue = (int)percent;
					}, cts.Token).ConfigureAwait(false);
				}

				if (launchCts.IsCancellationRequested)
					return;

				dialog.ProgressStyle = System.Windows.Forms.ProgressBarStyle.Marquee;
				if (studio)
				{
					var config = ClassicClients.GetInstalledConfig(code);
					if (config == null || !ClassicClients.HasStudio(config))
						throw new InvalidOperationException("The selected ORC client does not include Studio.");
					Status("Starting ORC Studio");
					Process? studioProcess = ClassicClients.Launch(code, ClientLaunchType.Studio, ClassicClients.DefaultPort, "localhost", Status);
					if (studioProcess == null)
						throw new InvalidOperationException("ORC Studio could not be started.");
					await Task.Delay(2000, launchCts.Token).ConfigureAwait(false);
					if (studioProcess.HasExited)
					{
						studioProcess.Dispose();
						throw new InvalidOperationException("ORC Studio closed during startup.");
					}
					return;
				}

				var maps = ClassicClients.ListMaps();
				string map = App.Settings.Prop.ClassicSelectedMap ?? "";
				if (string.IsNullOrEmpty(map) || !maps.Contains(map))
				{
					map = maps.FirstOrDefault() ?? "";
				}

				await ClassicClients.PlaySoloAsync(code, map, Status, launchCts.Token).ConfigureAwait(false);

				if (launchCts.IsCancellationRequested)
					ClassicClients.ShutdownAll();
			}
			catch (OperationCanceledException) when (launchCts.IsCancellationRequested)
			{
			}
			catch (Exception ex)
			{
				Frontend.ShowMessageBox($"Could not launch {code}: {ex.Message}", MessageBoxImage.Error);
			}
		}).ContinueWith(delegate
		{
			if (cancelled == 0)
				dialog.CloseBootstrapper();
			launchCts.Dispose();
		});

		dialog.ShowBootstrapper();

		try
		{
			Voidstrap.Utility.ClassicIntegrations.HideVoidstrap();
		}
		catch
		{
		}
	}

	protected new bool SetProperty<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
	{
		if (EqualityComparer<T>.Default.Equals(field, newValue))
		{
			return false;
		}
		field = newValue;
		this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName ?? throw new ArgumentNullException("propertyName")));
		return true;
	}
}
