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

    private System.Threading.CancellationTokenSource? _soberCancellation;

    public EventHandler<bool>? SetCanContinueEvent;

    public Voidstrap.Models.SettingTasks.ShortcutTask PlayerIconTask { get; }
    public Voidstrap.Models.SettingTasks.ShortcutTask StudioIconTask { get; }
    public Voidstrap.Models.SettingTasks.ShortcutTask SettingsIconTask { get; }

    internal void SetImport(Voidstrap.Utility.BootstrapperImportPlan? plan)
    {
        installer.PendingImport = plan;
    }

    public string InstallLocation
    {
        get { return installer.InstallLocation; }
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

    public string InstallLocationTitle =>
        Voidstrap.Utility.Platform.IsMacOS
            ? Voidstrap.Resources.Strings.Installer_MacOS_DataLocation_Title
            : Voidstrap.Resources.Strings.Installer_Install_Location_Title;

    public string InstallLocationDescription =>
        Voidstrap.Utility.Platform.IsMacOS
            ? Voidstrap.Resources.Strings.Installer_MacOS_DataLocation_Text
            : Voidstrap.Resources.Strings.Installer_Install_Location_Text;

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

    public bool RosemaryAvailable => Voidstrap.Utility.Platform.IsLinux && Voidstrap.Platform.Linux.LinuxRosemary.IsSupportedHost;

    public Visibility RosemaryVisibility => RosemaryAvailable ? Visibility.Visible : Visibility.Collapsed;

    private bool _useRosemary;

    public bool UseSober
    {
        get => !_useRosemary;
        set
        {
            if (value)
                UseRosemary = false;
        }
    }

    public bool UseRosemary
    {
        get => _useRosemary;
        set
        {
            bool chosen = value && RosemaryAvailable;
            if (_useRosemary == chosen)
                return;
            _useRosemary = chosen;
            OnPropertyChanged(nameof(UseRosemary));
            OnPropertyChanged(nameof(UseSober));
        }
    }

    public bool CreateDesktopShortcuts
    {
        get { return installer.CreateDesktopShortcuts; }
        set { installer.CreateDesktopShortcuts = value; }
    }

    public bool CreateStartMenuShortcuts
    {
        get { return installer.CreateStartMenuShortcuts; }
        set { installer.CreateStartMenuShortcuts = value; }
    }

    public bool ExtractRobloxIcons
    {
        get { return installer.ExtractRobloxIcons; }
        set { installer.ExtractRobloxIcons = value; }
    }

    public bool CreatePlayerShortcut
    {
        get { return installer.CreatePlayerShortcut; }
        set { installer.CreatePlayerShortcut = value; }
    }

    public bool CreateStudioShortcut
    {
        get { return installer.CreateStudioShortcut; }
        set { installer.CreateStudioShortcut = value; }
    }

    public bool CreateSettingsShortcut
    {
        get { return installer.CreateSettingsShortcut; }
        set { installer.CreateSettingsShortcut = value; }
    }

    public ICommand BrowseInstallLocationCommand => new RelayCommand(BrowseInstallLocation);

    public ICommand ResetInstallLocationCommand => new RelayCommand(ResetInstallLocation);

    public ICommand OpenFolderCommand => new RelayCommand(OpenFolder);

    public InstallViewModel()
    {
        _originalInstallLocation = installer.InstallLocation;
        string suffix = Voidstrap.Utility.Shortcut.Suffix;
        PlayerIconTask = new(
            "RobloxPlayer",
            Paths.Desktop,
            Voidstrap.Resources.Strings.LaunchMenu_LaunchRoblox + suffix,
            "-player",
            true,
            true
        );
        StudioIconTask = new(
            "RobloxStudio",
            Paths.Desktop,
            Voidstrap.Resources.Strings.LaunchMenu_LaunchRobloxStudio + suffix,
            "-studio",
            true,
            true
        );
        SettingsIconTask = new("Settings", Paths.Desktop, Voidstrap.Resources.Strings.Menu_Title + suffix, "-settings", true, true);
        installer.FunctionShortcutTasks = [PlayerIconTask, StudioIconTask, SettingsIconTask];
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
            PlayerIconTask.ValidateAppearance(CreatePlayerShortcut);
            StudioIconTask.ValidateAppearance(CreateStudioShortcut);
            SettingsIconTask.ValidateAppearance(CreateSettingsShortcut);
            installer.DoInstall();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("InstallViewModel::DoInstall", ex);
            Frontend.ShowMessageBox(
                "Voidstrap could not finish installing to this location.\n\n" + ex.Message + "\n\nPick a different folder and try again.",
                MessageBoxImage.Hand
            );
            SetCanContinueEvent?.Invoke(this, e: true);
            return false;
        }
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            Voidstrap.Utility.LinuxRuntimeChoice.SelectDefaults(UseRosemary);
            App.Logger.WriteLine(
                "InstallViewModel::DoInstall",
                UseRosemary ? "Roblox runs through Rosemary" : "Roblox runs through Sober and Vinegar"
            );
        }
        if (Voidstrap.Utility.Platform.IsLinux && UseRosemary)
        {
            using System.Threading.CancellationTokenSource cancellation = new();
            _soberCancellation = cancellation;
            Action<string> status = message =>
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => SoberInstallStatus = message));
                report?.Invoke(message);
            };
            try
            {
                Voidstrap.Platform.Linux.LinuxRosemary.DownloadSegments = Voidstrap.Utility.DownloadConfiguration.NormalizeSegments(
                    App.Settings.Prop.MaxDownloadSegments
                );
                System.Threading.Tasks.Task<Voidstrap.Platform.OperationResult<string>> runtimeTask =
                    Voidstrap.Platform.Linux.LinuxRosemary.EnsureRuntimeAsync(status, cancellation.Token);
                System.Threading.Tasks.Task<Voidstrap.Platform.OperationResult<string>> robloxTask =
                    Voidstrap.Platform.Linux.LinuxRosemary.EnsureRobloxAsync(
                        Voidstrap.Platform.Linux.RosemaryApplication.Player,
                        new Voidstrap.Platform.Linux.RosemaryRobloxOptions(),
                        status,
                        cancellation.Token
                    );
                await System.Threading.Tasks.Task.WhenAll(runtimeTask, robloxTask);
                Voidstrap.Platform.OperationResult<string> runtime = runtimeTask.Result;
                if (!robloxTask.Result.Succeeded)
                    App.Logger.WriteLine(
                        "InstallViewModel::DoInstall",
                        robloxTask.Result.Failure?.Message ?? "macOS Roblox could not be downloaded yet, it downloads on the first launch"
                    );
                App.Logger.WriteLine(
                    "InstallViewModel::DoInstall",
                    runtime.Succeeded ? "Rosemary installed" : (runtime.Failure?.Message ?? "Rosemary could not be installed")
                );
                if (!runtime.Succeeded)
                    Frontend.ShowMessageBox(
                        runtime.Failure?.Message ?? "Rosemary could not be installed",
                        MessageBoxImage.Error,
                        MessageBoxButton.OK
                    );
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                App.Logger.WriteLine("InstallViewModel::DoInstall", "The Rosemary install was cancelled");
                return false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("InstallViewModel::DoInstall", ex);
                Frontend.ShowMessageBox("Rosemary could not be installed: " + ex.Message, MessageBoxImage.Error, MessageBoxButton.OK);
            }
            finally
            {
                _soberCancellation = null;
            }
        }
        else if (Voidstrap.Utility.Platform.IsLinux)
        {
            SoberInstallStatus = "Installing Sober";
            using System.Threading.CancellationTokenSource cancellation = new();
            _soberCancellation = cancellation;
            try
            {
                Voidstrap.Platform.OperationResult result = await new Voidstrap.Platform.Linux.LinuxSoberInstaller(
                    new Voidstrap.Core.SystemProcessService()
                ).InstallAsync(cancellation.Token, report);
                App.Logger.WriteLine(
                    "InstallViewModel::DoInstall",
                    result.Succeeded ? "Sober installed" : (result.Failure?.Message ?? "Sober could not be installed")
                );
                if (!result.Succeeded)
                    Frontend.ShowMessageBox(
                        result.Failure?.Message ?? "Sober could not be installed",
                        MessageBoxImage.Error,
                        MessageBoxButton.OK
                    );
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                App.Logger.WriteLine("InstallViewModel::DoInstall", "The Sober install was cancelled");
                return false;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("InstallViewModel::DoInstall", ex);
                Frontend.ShowMessageBox("Sober could not be installed: " + ex.Message, MessageBoxImage.Error, MessageBoxButton.OK);
            }
            finally
            {
                _soberCancellation = null;
            }
        }
        return true;
    }

    public void CancelSoberInstall()
    {
        try
        {
            _soberCancellation?.Cancel();
        }
        catch (ObjectDisposedException) { }
    }

    private void BrowseInstallLocation()
    {
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            Microsoft.Win32.OpenFolderDialog dialog = new()
            {
                Title = "Choose the Voidstrap installation folder",
                InitialDirectory = InstallLocation,
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
        if (!Voidstrap.Utility.Platform.IsWindows)
        {
            Voidstrap.Utility.PlatformShell.TryOpenFolder(Paths.Base);
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Voidstrap.Utility.PlatformShell.WindowsTool("explorer.exe"),
                UseShellExecute = true,
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
