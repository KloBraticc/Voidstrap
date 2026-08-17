using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;

namespace Voidstrap.UI.ViewModels.Installer;

public class InstallViewModel : NotifyPropertyChangedViewModel
{
	private readonly Voidstrap.Installer installer = new Voidstrap.Installer();

	private readonly string _originalInstallLocation;

	public EventHandler<bool>? SetCanContinueEvent;

	public string InstallLocation
	{
		get
		{
			return installer.InstallLocation;
		}
		set
		{
			if (!string.IsNullOrEmpty(ErrorMessage))
			{
				SetCanContinueEvent?.Invoke(this, e: true);
				installer.InstallLocationError = "";
				OnPropertyChanged("ErrorMessage");
			}
			installer.InstallLocation = value;
			OnPropertyChanged("DataFoundMessageVisibility");
		}
	}

	public Visibility DataFoundMessageVisibility
	{
		get
		{
			if (!installer.ExistingDataPresent)
			{
				return Visibility.Collapsed;
			}
			return Visibility.Visible;
		}
	}

	public string ErrorMessage => installer.InstallLocationError;

	public bool CreateDesktopShortcuts
	{
		get
		{
			return installer.CreateDesktopShortcuts;
		}
		set
		{
			installer.CreateDesktopShortcuts = value;
		}
	}

	public bool CreateStartMenuShortcuts
	{
		get
		{
			return installer.CreateStartMenuShortcuts;
		}
		set
		{
			installer.CreateStartMenuShortcuts = value;
		}
	}

	public bool ExtractRobloxIcons
	{
		get
		{
			return installer.ExtractRobloxIcons;
		}
		set
		{
			installer.ExtractRobloxIcons = value;
		}
	}

	public bool CreatePlayerShortcut
	{
		get
		{
			return installer.CreatePlayerShortcut;
		}
		set
		{
			installer.CreatePlayerShortcut = value;
		}
	}

	public bool CreateStudioShortcut
	{
		get
		{
			return installer.CreateStudioShortcut;
		}
		set
		{
			installer.CreateStudioShortcut = value;
		}
	}

	public bool CreateSettingsShortcut
	{
		get
		{
			return installer.CreateSettingsShortcut;
		}
		set
		{
			installer.CreateSettingsShortcut = value;
		}
	}

	public ICommand BrowseInstallLocationCommand => new RelayCommand(BrowseInstallLocation);

	public ICommand ResetInstallLocationCommand => new RelayCommand(ResetInstallLocation);

	public ICommand OpenFolderCommand => new RelayCommand(OpenFolder);

	public InstallViewModel()
	{
		_originalInstallLocation = installer.InstallLocation;
	}

	public bool DoInstall()
	{
		if (!installer.CheckInstallLocation())
		{
			SetCanContinueEvent?.Invoke(this, e: false);
			OnPropertyChanged("ErrorMessage");
			return false;
		}
		try
		{
			installer.DoInstall();
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("InstallViewModel::DoInstall", ex);
			Frontend.ShowMessageBox("Voidstrap could not finish installing to this location.\n\n" + ex.Message + "\n\nPick a different folder and try again.", MessageBoxImage.Hand);
			SetCanContinueEvent?.Invoke(this, e: true);
			return false;
		}
		return true;
	}

	private void BrowseInstallLocation()
	{
		using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
		if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
		{
			InstallLocation = folderBrowserDialog.SelectedPath;
			OnPropertyChanged("InstallLocation");
		}
	}

	private void ResetInstallLocation()
	{
		InstallLocation = _originalInstallLocation;
		OnPropertyChanged("InstallLocation");
	}

	private void OpenFolder()
	{
		try
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = "explorer.exe",
				UseShellExecute = true
			};
			startInfo.ArgumentList.Add(Paths.Base);
			Process.Start(startInfo);
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("InstallViewModel::OpenFolder", "Could not open the install folder: " + ex.Message);
		}
	}
}
