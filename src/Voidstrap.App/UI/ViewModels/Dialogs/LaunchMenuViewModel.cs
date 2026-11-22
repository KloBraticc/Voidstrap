using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.UI;
using Voidstrap.UI.Elements.About;
using Voidstrap.UI.ViewModels.Settings;
using Voidstrap.Utility;
using SettingsViewModel = Voidstrap.UI.ViewModels.Settings.MainWindowViewModel;

namespace Voidstrap.UI.ViewModels.Installer
{
    public class LaunchMenuViewModel : NotifyPropertyChangedViewModel
    {
        private readonly CancellationTokenSource _accountsCts = new();
        private CurrentAccountInfo? _currentAccount;
        private bool _accountsBusy;
        private string _accountStatus = "";

        public ObservableCollection<AccountChoice> Accounts { get; } = new();

        public ObservableCollection<AccountChoice> OtherAccounts { get; } = new();

        public string Version => string.Format(Strings.Menu_About_Version, App.Version);

        public Visibility RobloxStudioOptionVisibility => App.IsStudioVisible ? Visibility.Visible : Visibility.Collapsed;

        public ICommand LaunchSettingsCommand => new RelayCommand(LaunchSettings);

        public ICommand LaunchRobloxCommand => new RelayCommand(LaunchRoblox);

        public ICommand LaunchAboutCommand => new RelayCommand(LaunchAbout);

        public event EventHandler<NextAction>? CloseWindowRequest;

        public string SelectedTargetName => TargetTitle(CurrentTarget()) ?? Strings.LaunchMenu_LaunchRoblox;

        public string CurrentAccountTitle =>
            string.IsNullOrWhiteSpace(_currentAccount?.DisplayName) ? _currentAccount?.Username ?? Strings.Accounts_NotSignedIn
            : _currentAccount.DisplayName;

        public string CurrentAccountUsername => _currentAccount == null ? "" : "@" + _currentAccount.Username;

        public string? CurrentAvatarUrl => _currentAccount?.AvatarUrl;

        public Visibility CurrentAccountUsernameVisibility => _currentAccount == null ? Visibility.Collapsed : Visibility.Visible;

        public bool IsAccountsBusy
        {
            get => _accountsBusy;
            private set
            {
                if (!SetProperty(ref _accountsBusy, value))
                    return;
                OnPropertyChanged(nameof(CanOpenAccounts));
                OnPropertyChanged(nameof(CanSwitchAccounts));
                OnPropertyChanged(nameof(CanVerifyAccounts));
            }
        }

        public bool CanOpenAccounts => !_accountsBusy;

        public bool CanSwitchAccounts => SavedAccounts.SwitchingSupported && !_accountsBusy;

        public bool CanVerifyAccounts => !_accountsBusy && Accounts.Count > 0;

        public string AccountStatus
        {
            get => _accountStatus;
            private set
            {
                if (SetProperty(ref _accountStatus, value))
                    OnPropertyChanged(nameof(AccountStatusVisibility));
            }
        }

        public Visibility AccountStatusVisibility => string.IsNullOrEmpty(_accountStatus) ? Visibility.Collapsed : Visibility.Visible;

        public async Task LoadAccountsAsync()
        {
            CancellationToken token = _accountsCts.Token;
            IsAccountsBusy = true;
            try
            {
                await RefreshAccountsAsync(token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LaunchMenuViewModel::LoadAccountsAsync", "Saved accounts could not be loaded: " + ex.Message);
                AccountStatus = Strings.Accounts_Status_Failed;
            }
            finally
            {
                IsAccountsBusy = false;
            }
        }

        public async Task SwitchAccountAsync(SavedAccount account)
        {
            if (IsAccountsBusy || !SavedAccounts.SwitchingSupported)
                return;
            CancellationToken token = _accountsCts.Token;
            if (account.Expired)
            {
                Frontend.ShowMessageBox(Strings.Accounts_Expired, MessageBoxImage.Warning);
                return;
            }
            if (_currentAccount?.UserId == account.UserId)
                return;

            IsAccountsBusy = true;
            try
            {
                if (!await AccountsViewModel.EnsureRobloxClosedAsync())
                    return;
                await SavedAccounts.SwitchAsync(account);
                await RefreshAccountsAsync(token);
                AccountStatus = string.Format(Strings.Accounts_Status_Switched, account.Username);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LaunchMenuViewModel::SwitchAccountAsync", "The account switch failed: " + ex.Message);
                Frontend.ShowMessageBox(
                    ex is System.IO.FileNotFoundException ? Strings.Accounts_MissingLogin : ex.Message,
                    MessageBoxImage.Warning
                );
            }
            finally
            {
                IsAccountsBusy = false;
            }
        }

        public async Task VerifyAllAccountsAsync()
        {
            if (!CanVerifyAccounts)
                return;
            CancellationToken token = _accountsCts.Token;
            IsAccountsBusy = true;
            AccountStatus = Strings.Accounts_SignIn_Verifying;
            try
            {
                List<AccountChoice> choices = Accounts.ToList();
                (int valid, int total) = await SavedAccounts.CheckAllAsync(choices.Select(choice => choice.Account).ToList(), token);
                SavedAccount? current = choices.FirstOrDefault(choice => choice.Account.UserId == _currentAccount?.UserId)?.Account;
                if (current != null && _currentAccount != null)
                    SetCurrentAccount(new CurrentAccountInfo(current.UserId, current.Username, current.DisplayName, _currentAccount.AvatarUrl));
                SetAccounts(choices);
                AccountStatus = string.Format(Strings.Accounts_Status_Checked, valid, total);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LaunchMenuViewModel::VerifyAllAccountsAsync", "The saved accounts could not be checked: " + ex.Message);
                AccountStatus = Strings.Accounts_Status_Failed;
            }
            finally
            {
                IsAccountsBusy = false;
            }
        }

        public void Dispose()
        {
            _accountsCts.Cancel();
            _accountsCts.Dispose();
        }

        private async Task RefreshAccountsAsync(CancellationToken token)
        {
            CurrentAccountInfo? current = await SavedAccounts.RefreshCurrentAsync(token);
            List<SavedAccount> saved = await SavedAccounts.LoadAsync(token);
            Dictionary<long, string> avatars = await SavedAccounts.GetAvatarsAsync(saved.Select(account => account.UserId), token);
            token.ThrowIfCancellationRequested();
            SetCurrentAccount(current);
            SetAccounts(
                saved.Select(account => new AccountChoice
                {
                    Account = account,
                    AvatarUrl = avatars.TryGetValue(account.UserId, out string? url) ? url : null,
                })
            );
        }

        private void SetAccounts(IEnumerable<AccountChoice> choices)
        {
            Accounts.Clear();
            OtherAccounts.Clear();
            foreach (AccountChoice choice in choices)
            {
                Accounts.Add(choice);
                if (choice.Account.UserId != (_currentAccount?.UserId ?? 0))
                    OtherAccounts.Add(choice);
            }
            OnPropertyChanged(nameof(CanOpenAccounts));
            OnPropertyChanged(nameof(CanVerifyAccounts));
        }

        private void SetCurrentAccount(CurrentAccountInfo? account)
        {
            if (!SetProperty(ref _currentAccount, account))
                return;
            OnPropertyChanged(nameof(CurrentAccountTitle));
            OnPropertyChanged(nameof(CurrentAccountUsername));
            OnPropertyChanged(nameof(CurrentAvatarUrl));
            OnPropertyChanged(nameof(CurrentAccountUsernameVisibility));
        }

        public IReadOnlyList<object> LaunchTargets()
        {
            DownloadsViewModel downloads = DownloadsViewModel.Shared;
            IEnumerable<object> players = downloads.Items.Where(download => download.LaunchMode == LaunchMode.Player);
            IEnumerable<object> studios = downloads.Items.Where(download => download.LaunchMode == LaunchMode.Studio);
            if (!Voidstrap.Utility.Platform.IsWindows)
                return [.. players, .. studios];
            if (downloads.ClientItems.Count == 0)
                downloads.RefreshClassic();
            return [.. players, .. studios, .. (downloads.Vr != null ? new object[] { downloads.Vr } : []), .. downloads.ClientItems.Where(client => client.IsInstalled)];
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
            LaunchMode mode = StudioSelected ? LaunchMode.Studio : LaunchMode.Player;
            if (Voidstrap.Utility.Platform.UsesLinuxHost)
            {
                bool rosemary = LinuxRuntimeChoice.UsesRosemary(mode);
                DownloadsViewModel.DownloadItem? runtime = downloads.Items.FirstOrDefault(download =>
                    download.LaunchMode == mode && download.IsRosemary == rosemary
                );
                if (runtime != null)
                    return runtime;
            }
            if (mode == LaunchMode.Player && App.Settings.Prop.UseVng && downloads.VngItem is DownloadsViewModel.DownloadItem vng)
                return vng;
            return downloads.Items.FirstOrDefault(download => download.LaunchMode == mode && !download.IsVng);
        }

        public static bool StudioSelected => App.Settings.Prop.LaunchSelectionIndex == 1;

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
                    App.Settings.Prop.LaunchSelectionIndex = 0;
                    break;
                case DownloadsViewModel.VrItem:
                    App.Settings.Prop.LaunchSelectedClient = App.SteamVrLaunchClientCode;
                    App.Settings.Prop.LaunchSelectionIndex = 0;
                    break;
                case DownloadsViewModel.DownloadItem download:
                    App.Settings.Prop.LaunchSelectedClient = "";
                    App.Settings.Prop.LaunchSelectionIndex = download.LaunchMode == LaunchMode.Studio ? 1 : 0;
                    if (Voidstrap.Utility.Platform.UsesLinuxHost)
                        LinuxRuntimeChoice.Select(download.LaunchMode, download.RuntimeKey);
                    if (download.LaunchMode == LaunchMode.Player && DownloadsViewModel.Shared.VngItem is DownloadsViewModel.DownloadItem vng)
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

        private void LaunchAbout() => new MainWindow().ShowOwnedDialog();
    }
}
