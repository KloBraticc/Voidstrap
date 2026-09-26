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
	private string _soberInstallStatus = string.Empty;

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
				OnPropertyChanged(nameof(ErrorMessage));
			}
			installer.InstallLocation = value;
			OnPropertyChanged(nameof(DataFoundMessageVisibility));
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

	public string SoberInstallStatus
	{
		get => _soberInstallStatus;
		private set
		{
			_soberInstallStatus = value;
			OnPropertyChanged(nameof(SoberInstallStatus));
		}
	}

	public void SetSoberInstallStatus(string message)
	{
		SoberInstallStatus = message;
	}

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

	public async System.Threading.Tasks.Task<bool> DoInstallAsync(Action<string>? report = null)
	{
		if (!installer.CheckInstallLocation())
		{
			SetCanContinueEvent?.Invoke(this, e: false);
			OnPropertyChanged(nameof(ErrorMessage));
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
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			SoberInstallStatus = "Installing Sober";
			try
			{
				Voidstrap.Platform.OperationResult result = await new Voidstrap.Platform.Linux.LinuxSoberInstaller(new Voidstrap.Core.SystemProcessService())
					.InstallAsync(report: report);
				App.Logger.WriteLine("InstallViewModel::DoInstall", result.Succeeded ? "Sober installed" : (result.Failure?.Message ?? "Sober could not be installed"));
				if (!result.Succeeded)
					Frontend.ShowMessageBox(result.Failure?.Message ?? "Sober could not be installed", MessageBoxImage.Error, MessageBoxButton.OK);
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("InstallViewModel::DoInstall", ex);
				Frontend.ShowMessageBox("Sober could not be installed: " + ex.Message, MessageBoxImage.Error, MessageBoxButton.OK);
			}
		}
		return true;
	}

	private void BrowseInstallLocation()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			Microsoft.Win32.OpenFolderDialog dialog = new()
			{
				Title = "Choose the Voidstrap installation folder",
				InitialDirectory = InstallLocation
			};
			if (dialog.ShowDialog() == true)
			{
				InstallLocation = dialog.FolderName;
				OnPropertyChanged(nameof(InstallLocation));
			}
			return;
		}

		using FolderBrowserDialog folderBrowserDialog = new FolderBrowserDialog();
		if (folderBrowserDialog.ShowDialog() == DialogResult.OK)
		{
			InstallLocation = folderBrowserDialog.SelectedPath;
			OnPropertyChanged(nameof(InstallLocation));
		}
	}

	private void ResetInstallLocation()
	{
		InstallLocation = _originalInstallLocation;
		OnPropertyChanged(nameof(InstallLocation));
	}

	private void OpenFolder()
	{
		if (Voidstrap.Utility.Platform.IsLinux)
		{
			Voidstrap.Utility.PlatformShell.TryOpenFolder(Paths.Base);
			return;
		}

		try
		{
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = Voidstrap.Utility.PlatformShell.WindowsTool("explorer.exe"),
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
