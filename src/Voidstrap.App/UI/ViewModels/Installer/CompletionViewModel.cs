using Timer = System.Timers.Timer;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Enums;
using Voidstrap.Models;
using Voidstrap.Models.APIs.Roblox;
using Voidstrap.RobloxInterfaces;

namespace Voidstrap.UI.ViewModels.Installer;

public class CompletionViewModel : ObservableObject, IDisposable
{
	private readonly Timer _saveTimer = new Timer(5000.0);

	private bool _disposed;

	private int _loadGeneration;

	private bool _showLoadingError;

	private string _channelInfoLoadingText = "";

	private DeployInfo? _channelDeployInfo;

	private bool _showChannelWarning;

	private string _viewChannel;

	public ICommand LaunchSettingsCommand { get; }

	public ICommand LaunchRobloxCommand { get; }

	public ICommand OpenAboutCommand { get; }

	public bool ShowLoadingError
	{
		get
		{
			return _showLoadingError;
		}
		set
		{
			SetProperty(ref _showLoadingError, value, "ShowLoadingError");
		}
	}

	public string ChannelInfoLoadingText
	{
		get
		{
			return _channelInfoLoadingText;
		}
		set
		{
			SetProperty(ref _channelInfoLoadingText, value, "ChannelInfoLoadingText");
		}
	}

	public DeployInfo? ChannelDeployInfo
	{
		get
		{
			return _channelDeployInfo;
		}
		set
		{
			SetProperty(ref _channelDeployInfo, value, "ChannelDeployInfo");
		}
	}

	public bool ShowChannelWarning
	{
		get
		{
			return _showChannelWarning;
		}
		set
		{
			SetProperty(ref _showChannelWarning, value, "ShowChannelWarning");
		}
	}

	public string ViewChannel
	{
		get
		{
			return _viewChannel;
		}
		set
		{
			string text = value?.Trim() ?? "production";
			if (!(_viewChannel == text))
			{
				_viewChannel = text;
				OnPropertyChanged("ViewChannel");
				LoadChannelDeployInfoAsync(text);
				App.Settings.Prop.Channel = text;
				_saveTimer.Stop();
				_saveTimer.Start();
			}
		}
	}

	public event EventHandler<NextAction>? CloseWindowRequest;

	public CompletionViewModel()
	{
		LaunchSettingsCommand = new RelayCommand(delegate
		{
			if (RestartInto("-settings"))
				return;
			this.CloseWindowRequest?.Invoke(this, NextAction.LaunchSettings);
		});
		LaunchRobloxCommand = new RelayCommand(delegate
		{
			if (RestartInto("-player"))
				return;
			this.CloseWindowRequest?.Invoke(this, NextAction.LaunchRoblox);
		});
		OpenAboutCommand = new RelayCommand(delegate
		{
			new Voidstrap.UI.Elements.About.MainWindow().ShowDialog();
		});
		_viewChannel = App.Settings.Prop.Channel;
		LoadChannelDeployInfoAsync(App.Settings.Prop.Channel);
		_saveTimer.Elapsed += OnSaveTimerElapsed;
		_saveTimer.AutoReset = false;
	}

	private static bool RestartInto(string flags)
	{
		try
		{
			App.State.Save();
			App.Settings.Save();
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("CompletionViewModel", "Could not flush settings before restarting: " + ex.Message);
		}

		string executable = "";
		try
		{
			executable = Paths.Application ?? "";
		}
		catch
		{
		}

		if (executable.Length == 0 || !System.IO.File.Exists(executable))
		{
			App.Logger.WriteLine("CompletionViewModel", "No installed executable to restart, using the in process path");
			return false;
		}

		try
		{
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
			{
				FileName = executable,
				Arguments = flags,
				UseShellExecute = true,
				WorkingDirectory = System.IO.Path.GetDirectoryName(executable) ?? ""
			});
			App.Logger.WriteLine("CompletionViewModel", "Restarting Voidstrap with " + flags);
			App.Terminate();
			return true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("CompletionViewModel", "Could not restart Voidstrap: " + ex.Message);
			return false;
		}
	}

	private void OnSaveTimerElapsed(object? sender, ElapsedEventArgs e)
	{
		if (!_disposed)
		{
			App.State.Save();
		}
	}

	private async Task LoadChannelDeployInfoAsync(string channel)
	{
		int generation = ++_loadGeneration;
		try
		{
			ShowLoadingError = false;
			ChannelDeployInfo = null;
			ChannelInfoLoadingText = "Fetching latest deploy info, please wait...";
			OnPropertyChanged("ShowLoadingError");
			OnPropertyChanged("ChannelDeployInfo");
			OnPropertyChanged("ChannelInfoLoadingText");
			ClientVersion clientVersion = await Deployment.GetInfo(channel);
			if (_disposed || generation != _loadGeneration)
			{
				return;
			}
			ShowChannelWarning = clientVersion.IsBehindDefaultChannel;
			ChannelDeployInfo = new DeployInfo
			{
				Version = clientVersion.Version,
				VersionGuid = clientVersion.VersionGuid
			};
			App.State.Prop.IgnoreOutdatedChannel = true;
			OnPropertyChanged("ShowChannelWarning");
			OnPropertyChanged("ChannelDeployInfo");
		}
		catch (HttpRequestException)
		{
			ShowLoadingError = true;
			ChannelInfoLoadingText = "The channel is likely private or unreachable. Try using a version hash or change the channel.";
		}
		catch (TaskCanceledException)
		{
			ShowLoadingError = true;
			ChannelInfoLoadingText = "The request timed out. Please check your internet connection and try again.";
		}
		catch (Exception ex3)
		{
			ShowLoadingError = true;
			ChannelInfoLoadingText = "An unexpected error occurred: " + ex3.Message;
		}
		finally
		{
			if (!_disposed && generation == _loadGeneration)
			{
				OnPropertyChanged("ShowLoadingError");
				OnPropertyChanged("ChannelInfoLoadingText");
			}
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_loadGeneration++;
		_saveTimer.Stop();
		_saveTimer.Elapsed -= OnSaveTimerElapsed;
		_saveTimer.Dispose();
		CloseWindowRequest = null;
		GC.SuppressFinalize(this);
	}
}
