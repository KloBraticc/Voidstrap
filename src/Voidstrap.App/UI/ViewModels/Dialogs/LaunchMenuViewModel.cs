using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;

using Voidstrap.UI.Elements.About;
using Voidstrap.UI.ViewModels.Settings;
using SettingsViewModel = Voidstrap.UI.ViewModels.Settings.MainWindowViewModel;

namespace Voidstrap.UI.ViewModels.Installer
{
    public class LaunchMenuViewModel : NotifyPropertyChangedViewModel
    {
        public string Version => string.Format(Strings.Menu_About_Version, App.Version);

        public Visibility RobloxStudioOptionVisibility => App.IsStudioVisible ? Visibility.Visible : Visibility.Collapsed;

        public ICommand LaunchSettingsCommand => new RelayCommand(LaunchSettings);

        public ICommand LaunchRobloxCommand => new RelayCommand(LaunchRoblox);

        public ICommand LaunchRobloxStudioCommand => new RelayCommand(LaunchRobloxStudio);

        public ICommand LaunchAboutCommand => new RelayCommand(LaunchAbout);

        public event EventHandler<NextAction>? CloseWindowRequest;

        public string SelectedTargetName => TargetTitle(CurrentTarget()) ?? Strings.LaunchMenu_LaunchRoblox;

        public IReadOnlyList<object> LaunchTargets()
        {
            DownloadsViewModel downloads = DownloadsViewModel.Shared;
            IEnumerable<object> players = downloads.Items.Where(download => download.LaunchMode == LaunchMode.Player);
            if (!Voidstrap.Utility.Platform.IsWindows)
                return [.. players];
            if (downloads.ClientItems.Count == 0)
                downloads.RefreshClassic();
            return [.. players, .. (downloads.Vr != null ? new object[] { downloads.Vr } : []), .. downloads.ClientItems.Where(client => client.IsInstalled)];
        }

        public object? CurrentTarget()
        {
            DownloadsViewModel downloads = DownloadsViewModel.Shared;
            string code = SettingsViewModel.StoredLaunchClient;
            if (downloads.Vr != null && string.Equals(code, App.SteamVrLaunchClientCode, StringComparison.Ordinal))
                return downloads.Vr;
            if (Voidstrap.Utility.Platform.IsWindows && !string.IsNullOrEmpty(code))
            {
                DownloadsViewModel.ClientItem? client = downloads.ClientItems.FirstOrDefault(item =>
                    item.IsInstalled && string.Equals(item.Code, code, StringComparison.OrdinalIgnoreCase)
                );
                if (client != null)
                    return client;
            }
            if (Voidstrap.Utility.Platform.UsesLinuxHost)
            {
                bool rosemary = LinuxRuntimeChoice.UsesRosemary(LaunchMode.Player);
                DownloadsViewModel.DownloadItem? runtime = downloads.Items.FirstOrDefault(download =>
                    download.LaunchMode == LaunchMode.Player && download.IsRosemary == rosemary
                );
                if (runtime != null)
                    return runtime;
            }
            if (App.Settings.Prop.UseVng && downloads.VngItem is DownloadsViewModel.DownloadItem vng)
                return vng;
            return downloads.Items.FirstOrDefault(download => download.LaunchMode == LaunchMode.Player && !download.IsVng);
        }

        public static string? TargetTitle(object? target) =>
            target switch
            {
                DownloadsViewModel.DownloadItem download => download.Title,
                DownloadsViewModel.VrItem vr => vr.Title,
                DownloadsViewModel.ClientItem client => client.Title,
                _ => null,
            };

        public static ImageSource? TargetIcon(object? target) =>
            target switch
            {
                DownloadsViewModel.DownloadItem download => download.IconImage,
                DownloadsViewModel.VrItem vr => vr.IconImage,
                DownloadsViewModel.ClientItem client => client.IconImage,
                _ => null,
            };

        public void SelectTarget(object target)
        {
            switch (target)
            {
                case DownloadsViewModel.ClientItem client:
                    App.Settings.Prop.LaunchSelectedClient = client.Code;
                    break;
                case DownloadsViewModel.VrItem:
                    App.Settings.Prop.LaunchSelectedClient = App.SteamVrLaunchClientCode;
                    App.Settings.Prop.LaunchSelectionIndex = 0;
                    break;
                case DownloadsViewModel.DownloadItem download:
                    App.Settings.Prop.LaunchSelectedClient = "";
                    App.Settings.Prop.LaunchSelectionIndex = 0;
                    if (Voidstrap.Utility.Platform.UsesLinuxHost)
                        LinuxRuntimeChoice.Select(download.LaunchMode, download.RuntimeKey);
                    if (DownloadsViewModel.Shared.VngItem is DownloadsViewModel.DownloadItem vng)
                        vng.UseVng = download.IsVng;
                    break;
                default:
                    return;
            }
            App.Settings.SaveDeferred();
            OnPropertyChanged(nameof(SelectedTargetName));
        }

        private void LaunchSettings() => CloseWindowRequest?.Invoke(this, NextAction.LaunchSettings);

        private void LaunchRoblox() => CloseWindowRequest?.Invoke(this, NextAction.LaunchRoblox);

        private void LaunchRobloxStudio() => CloseWindowRequest?.Invoke(this, NextAction.LaunchRobloxStudio);

        private void LaunchAbout() => new MainWindow().ShowOwnedDialog();
    }
}
