using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.UI.Elements.About;
using Voidstrap.UI.ViewModels.Settings;
using SettingsViewModel = Voidstrap.UI.ViewModels.Settings.MainWindowViewModel;

namespace Voidstrap.UI.ViewModels.Installer
{
    public class LaunchMenuViewModel : NotifyPropertyChangedViewModel, IDisposable
    {
        private readonly CancellationTokenSource _accountsCts = new();
        private List<AccountChoice> _accounts = new();
        private CurrentAccountInfo? _currentAccount;
        private bool _accountsBusy;
        private bool _switchingAccount;
        private bool _disposed;
        private string _accountStatus = "";

        public IEnumerable<AccountChoice> OtherAccounts => _accounts.Where(choice => choice.Account.UserId != _currentAccount?.UserId);

        public string Version => string.Format(Strings.Menu_About_Version, App.Version);

        public ICommand LaunchSettingsCommand => new RelayCommand(LaunchSettings);

        public ICommand LaunchRobloxCommand => new RelayCommand(LaunchRoblox);

        public ICommand LaunchAboutCommand => new RelayCommand(LaunchAbout);

        public event EventHandler<NextAction>? CloseWindowRequest;

        public string SelectedTargetName => TargetTitle(CurrentTarget()) ?? Strings.LaunchMenu_LaunchRoblox;

        public string CurrentAccountTitle =>
            string.IsNullOrWhiteSpace(_currentAccount?.DisplayName) ? _currentAccount?.Username ?? Strings.Accounts_NotSignedIn
            : _currentAccount.DisplayName;

        public string? CurrentAvatarUrl => _currentAccount?.AvatarUrl;

        public Visibility AccountSelectorVisibility => SavedAccounts.SwitchingSupported ? Visibility.Visible : Visibility.Collapsed;

        public bool CanLeaveLaunchMenu => !_switchingAccount;

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

        public bool CanOpenAccounts => !_accountsBusy && SavedAccounts.SwitchingSupported;

        public bool CanSwitchAccounts => SavedAccounts.SwitchingSupported && !_accountsBusy;

        public bool CanVerifyAccounts => !_accountsBusy && _accounts.Count > 0;

        public string AccountStatus => _accountStatus;

        public async Task LoadAccountsAsync()
        {
            const string LOG_IDENT = "LaunchMenuViewModel::LoadAccountsAsync";
            if (IsAccountsBusy || !SavedAccounts.SwitchingSupported)
                return;
            CancellationToken token = _accountsCts.Token;
            IsAccountsBusy = true;
            try
            {
                await RefreshAccountsAsync(token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Saved accounts could not be loaded: " + ex.Message);
                _accountStatus = Strings.Accounts_Status_Failed;
            }
            finally
            {
                IsAccountsBusy = false;
            }
        }

        public async Task SwitchAccountAsync(SavedAccount account)
        {
            const string LOG_IDENT = "LaunchMenuViewModel::SwitchAccountAsync";
            if (IsAccountsBusy || !SavedAccounts.SwitchingSupported)
                return;
            if (account.Expired)
            {
                Frontend.ShowMessageBox(Strings.Accounts_Expired, MessageBoxImage.Warning);
                return;
            }
            if (_currentAccount?.UserId == account.UserId)
                return;

            _switchingAccount = true;
            OnPropertyChanged(nameof(CanLeaveLaunchMenu));
            IsAccountsBusy = true;
            try
            {
                if (!await AccountsViewModel.EnsureRobloxClosedAsync())
                    return;
                await SavedAccounts.SwitchAsync(account);
                SetCurrentAccount(SavedAccounts.Current);
                _accountStatus = string.Format(Strings.Accounts_Status_Switched, account.Username);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The account switch failed: " + ex.Message);
                Frontend.ShowMessageBox(
                    ex is FileNotFoundException ? Strings.Accounts_MissingLogin : ex.Message,
                    MessageBoxImage.Warning
                );
            }
            finally
            {
                IsAccountsBusy = false;
                _switchingAccount = false;
                OnPropertyChanged(nameof(CanLeaveLaunchMenu));
            }
        }

        public async Task VerifyAllAccountsAsync()
        {
            const string LOG_IDENT = "LaunchMenuViewModel::VerifyAllAccountsAsync";
            if (!CanVerifyAccounts)
                return;
            CancellationToken token = _accountsCts.Token;
            IsAccountsBusy = true;
            try
            {
                (int valid, int total) = await SavedAccounts.CheckAllAsync(_accounts.Select(choice => choice.Account).ToList(), token);
                SavedAccount? current = _accounts.FirstOrDefault(choice => choice.Account.UserId == _currentAccount?.UserId)?.Account;
                if (current != null && _currentAccount != null)
                    SetCurrentAccount(new CurrentAccountInfo(current.UserId, current.Username, current.DisplayName, _currentAccount.AvatarUrl));
                _accountStatus = string.Format(Strings.Accounts_Status_Checked, valid, total);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The saved accounts could not be checked: " + ex.Message);
                _accountStatus = Strings.Accounts_Status_Failed;
            }
            finally
            {
                IsAccountsBusy = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _accountsCts.Cancel();
            _accountsCts.Dispose();
            GC.SuppressFinalize(this);
        }

        private async Task RefreshAccountsAsync(CancellationToken token)
        {
            CurrentAccountInfo? current = await SavedAccounts.RefreshCurrentAsync(token);
            List<SavedAccount> saved = await SavedAccounts.LoadAsync(token);
            Dictionary<long, string> avatars = await SavedAccounts.GetAvatarsAsync(saved.Select(account => account.UserId), token);
            token.ThrowIfCancellationRequested();
            SetCurrentAccount(current);
            _accounts = saved.Select(account => new AccountChoice
            {
                Account = account,
                AvatarUrl = avatars.TryGetValue(account.UserId, out string? url) ? url : null,
            }).ToList();
        }

        private void SetCurrentAccount(CurrentAccountInfo? account)
        {
            if (!SetProperty(ref _currentAccount, account))
                return;
            OnPropertyChanged(nameof(CurrentAccountTitle));
            OnPropertyChanged(nameof(CurrentAvatarUrl));
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
            bool accountSwitchingWasSupported = SavedAccounts.SwitchingSupported;
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
            if (accountSwitchingWasSupported != SavedAccounts.SwitchingSupported)
            {
                OnPropertyChanged(nameof(AccountSelectorVisibility));
                OnPropertyChanged(nameof(CanOpenAccounts));
                OnPropertyChanged(nameof(CanSwitchAccounts));
                if (SavedAccounts.SwitchingSupported)
                    _ = LoadAccountsAsync();
            }
        }

        private void LaunchSettings() => CloseWindowRequest?.Invoke(this, NextAction.LaunchSettings);

        private void LaunchRoblox() => CloseWindowRequest?.Invoke(this, NextAction.LaunchRoblox);

        private void LaunchAbout() => new MainWindow().ShowOwnedDialog();
    }
}
