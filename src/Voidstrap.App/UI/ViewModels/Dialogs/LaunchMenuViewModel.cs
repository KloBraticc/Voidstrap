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
        private const int AvatarDecodeWidth = 64;

        private readonly CancellationTokenSource _accountsCts = new();
        private CancellationTokenSource? _refreshCts;
        private List<AccountChoice> _accounts = new();
        private string? _pendingAvatarUrl;

        private CurrentAccountInfo? _currentAccount;
        private ImageSource? _currentAvatar;
        private bool _accountsBusy;
        private bool _switchingAccount;
        private bool _disposed;
        private string _accountStatus = "";

        public IEnumerable<AccountChoice> OtherAccounts => _accounts.Where(choice =>
            choice.Account.UserId <= 0 || choice.Account.UserId != _currentAccount?.UserId
        );

        public string Version => string.Format(Strings.Menu_About_Version, App.Version);

        public ICommand LaunchSettingsCommand => new RelayCommand(LaunchSettings);

        public ICommand LaunchRobloxCommand => new RelayCommand(LaunchRoblox);

        public ICommand LaunchAboutCommand => new RelayCommand(LaunchAbout);

        public event EventHandler<NextAction>? CloseWindowRequest;

        public string SelectedTargetName => TargetTitle(CurrentTarget()) ?? Strings.LaunchMenu_LaunchRoblox;

        public string CurrentAccountTitle =>
            string.IsNullOrWhiteSpace(_currentAccount?.DisplayName) ? _currentAccount?.Username ?? Strings.Accounts_NotSignedIn
            : _currentAccount.DisplayName;

        public ImageSource? CurrentAvatar => _currentAvatar;

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

        public void PrepareAccounts()
        {
            if (!SavedAccounts.SwitchingSupported)
                return;
            (CurrentAccountInfo? account, byte[]? avatar) = SavedAccounts.ReadCachedLaunchAccount();
            SetCurrentAccount(account, avatar);
            SetAccounts(SavedAccounts.ReadLaunchAccounts());
        }

        public void RefreshAccounts()
        {
            if (_disposed || IsAccountsBusy || !SavedAccounts.SwitchingSupported)
                return;
            StopAccountRefresh();
            _refreshCts = CancellationTokenSource.CreateLinkedTokenSource(_accountsCts.Token);
            _ = RefreshAccountsAsync(_refreshCts.Token);
        }

        private void StopAccountRefresh()
        {
            _refreshCts?.Cancel();
            _refreshCts?.Dispose();
            _refreshCts = null;
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

            StopAccountRefresh();
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
                RefreshAccounts();
            }
        }

        public async Task VerifyAllAccountsAsync()
        {
            const string LOG_IDENT = "LaunchMenuViewModel::VerifyAllAccountsAsync";
            if (!CanVerifyAccounts)
                return;
            StopAccountRefresh();
            CancellationToken token = _accountsCts.Token;
            IsAccountsBusy = true;
            try
            {
                (int valid, int total) = await SavedAccounts.CheckAllAsync(_accounts.Select(choice => choice.Account).ToList(), token);
                SavedAccount? current = _accounts.FirstOrDefault(choice => choice.Account.UserId == _currentAccount?.UserId)?.Account;
                if (current is { Expired: true })
                {
                    SavedAccounts.ForgetLaunchAccount(current.UserId);
                    SetCurrentAccount(null);
                }
                else if (current != null && _currentAccount != null)
                    SetCurrentAccount(_currentAccount with { Username = current.Username, DisplayName = current.DisplayName });
                SetAccounts(SavedAccounts.ReadLaunchAccounts());
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
            StopAccountRefresh();
            _accountsCts.Cancel();
            _accountsCts.Dispose();
            GC.SuppressFinalize(this);
        }

        private async Task RefreshAccountsAsync(CancellationToken token)
        {
            const string LOG_IDENT = "LaunchMenuViewModel::RefreshAccountsAsync";
            try
            {
                try
                {
                    CurrentAccountInfo? current = await SavedAccounts.RefreshLaunchAccountAsync(token);
                    token.ThrowIfCancellationRequested();
                    SetCurrentAccount(current);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    App.Logger.WriteLine(LOG_IDENT, "The current account could not be refreshed: " + ex.Message);
                }

                List<SavedAccount> saved = await SavedAccounts.LoadAsync(token);
                token.ThrowIfCancellationRequested();
                SetAccounts(saved);
                Dictionary<long, string> avatars = await SavedAccounts.GetAvatarsAsync(saved.Select(account => account.UserId), token);
                token.ThrowIfCancellationRequested();
                if (avatars.Count > 0)
                    SetAccounts(saved, avatars);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Saved accounts could not be reloaded: " + ex.Message);
            }
        }

        private void SetAccounts(List<SavedAccount> saved, Dictionary<long, string>? avatars = null)
        {
            _accounts = saved.Select(account => new AccountChoice
            {
                Account = account,
                AvatarUrl = avatars?.GetValueOrDefault(account.UserId)
                    ?? _accounts.Find(choice => choice.Account.UserId == account.UserId)?.AvatarUrl,
            }).ToList();
            OnPropertyChanged(nameof(OtherAccounts));
            OnPropertyChanged(nameof(CanVerifyAccounts));
        }

        private void SetCurrentAccount(CurrentAccountInfo? account, byte[]? avatar = null)
        {
            string? previousUrl = _currentAccount?.AvatarUrl;
            if (SetProperty(ref _currentAccount, account))
            {
                OnPropertyChanged(nameof(CurrentAccountTitle));
                OnPropertyChanged(nameof(OtherAccounts));
            }

            string? url = account?.AvatarUrl;
            if (avatar == null && url == previousUrl && (_currentAvatar != null || _pendingAvatarUrl == url))
                return;
            ImageSource? cached = avatar == null ? null : SafeImaging.FromBytes(avatar, AvatarDecodeWidth);
            bool hasSavedAvatar = cached != null;
            if (!hasSavedAvatar && !string.IsNullOrEmpty(url))
                cached = AppImage.LoadCachedBitmap(url, AvatarDecodeWidth);
            SetProperty(ref _currentAvatar, cached, nameof(CurrentAvatar));
            if (account != null && !string.IsNullOrEmpty(url) && !hasSavedAvatar && _pendingAvatarUrl != url)
            {
                _pendingAvatarUrl = url;
                _ = LoadCurrentAvatarAsync(account, url, _accountsCts.Token);
            }
        }

        private async Task LoadCurrentAvatarAsync(CurrentAccountInfo account, string url, CancellationToken token)
        {
            const string LOG_IDENT = "LaunchMenuViewModel::LoadCurrentAvatarAsync";
            try
            {
                foreach (string candidate in AppImage.GetCandidates(url, AvatarDecodeWidth))
                {
                    byte[]? bytes = await AppImage.DownloadBytesAsync(candidate, token);
                    if (bytes == null)
                        continue;
                    ImageSource? avatar = await AppImage.DecodeBytesAsync(bytes, AvatarDecodeWidth, token);
                    if (avatar == null)
                        continue;
                    await Task.Run(() => SavedAccounts.CacheLaunchAvatar(account, bytes), token);
                    if (!token.IsCancellationRequested && _currentAccount?.AvatarUrl == url)
                        SetProperty(ref _currentAvatar, avatar, nameof(CurrentAvatar));
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The account avatar could not be loaded: " + ex.Message);
            }
            finally
            {
                if (_pendingAvatarUrl == url)
                    _pendingAvatarUrl = null;
            }
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
                {
                    PrepareAccounts();
                    RefreshAccounts();
                }
                else
                {
                    StopAccountRefresh();
                    SetCurrentAccount(null);
                }
            }
        }

        private void LaunchSettings() => CloseWindowRequest?.Invoke(this, NextAction.LaunchSettings);

        private void LaunchRoblox() => CloseWindowRequest?.Invoke(this, NextAction.LaunchRoblox);

        private void LaunchAbout() => new MainWindow().ShowOwnedDialog();
    }
}
