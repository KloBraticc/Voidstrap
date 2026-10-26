using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations;
using Voidstrap.Resources;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings
{
    internal sealed class AccountRow : NotifyPropertyChangedViewModel
    {
        private string? _avatarUrl;
        private bool _isCurrent;

        public AccountRow(SavedAccount model)
        {
            Model = model;
        }

        public SavedAccount Model { get; }

        public string Title => string.IsNullOrWhiteSpace(Model.DisplayName) ? Model.Username : Model.DisplayName;

        public string Handle => "@" + Model.Username;

        public string Note
        {
            get => Model.Note;
            set
            {
                string next = (value ?? "").Trim();
                if (next == Model.Note)
                    return;
                Model.Note = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SubText));
                _ = SavedAccounts.UpdateAsync(Model);
            }
        }

        public string SubText => Model.Expired ? Strings.Accounts_Expired : string.IsNullOrEmpty(Model.Note) ? Handle : Model.Note;

        public string? AvatarUrl
        {
            get => _avatarUrl;
            set => SetProperty(ref _avatarUrl, value);
        }

        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (SetProperty(ref _isCurrent, value))
                    OnPropertyChanged(nameof(CurrentVisibility));
            }
        }

        public Visibility CurrentVisibility => _isCurrent ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PinnedVisibility => Model.Pinned ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ExpiredVisibility => Model.Expired ? Visibility.Visible : Visibility.Collapsed;

        public string PinText => Model.Pinned ? Strings.Accounts_Unpin : Strings.Accounts_Pin;

        public string LastUsedText => Model.LastUsedUtc == default ? Strings.Accounts_Never : Model.LastUsedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

        public string AddedText => Model.AddedUtc == default ? Strings.Accounts_Never : Model.AddedUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

        public void Refresh()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Handle));
            OnPropertyChanged(nameof(SubText));
            OnPropertyChanged(nameof(PinnedVisibility));
            OnPropertyChanged(nameof(ExpiredVisibility));
            OnPropertyChanged(nameof(PinText));
            OnPropertyChanged(nameof(LastUsedText));
        }
    }

    internal sealed class AccountsViewModel : NotifyPropertyChangedViewModel, IDisposable
    {
        private const string LogIdent = "AccountManager";

        private readonly List<AccountRow> _all = new();
        private CancellationTokenSource _cts = new();
        private CancellationTokenSource? _detailCts;
        private AccountRow? _selected;
        private string _searchText = "";
        private string _status = "";
        private string _cookieText = "";
        private bool _importVisible;
        private bool _busy;
        private bool _disposed;
        private string _robux = "";
        private string _premium = "";
        private string _joined = "";
        private string _friends = "";
        private string _followers = "";

        public AccountsViewModel()
        {
            SignInCommand = new RelayCommand(() => _ = SignInAsync(false));
            SignUpCommand = new RelayCommand(() => _ = SignInAsync(true));
            ToggleImportCommand = new RelayCommand(() => ImportVisible = !ImportVisible);
            ImportCommand = new RelayCommand(() => _ = ImportAsync());
            AddCurrentCommand = new RelayCommand(() => _ = AddCurrentAsync());
            SwitchCommand = new RelayCommand<AccountRow?>(row => _ = SwitchAsync(row ?? _selected, false));
            SwitchAndPlayCommand = new RelayCommand(() => _ = SwitchAsync(_selected, true));
            RenewCommand = new RelayCommand(() => _ = SignInAsync(false));
            PinCommand = new RelayCommand(() => _ = TogglePinAsync());
            RemoveCommand = new RelayCommand(() => _ = RemoveAsync());
            CopyIdCommand = new RelayCommand(() => Copy(_selected?.Model.UserId.ToString(CultureInfo.InvariantCulture)));
            CopyUsernameCommand = new RelayCommand(() => Copy(_selected?.Model.Username));
            OpenProfileCommand = new RelayCommand(() => { if (_selected != null) Utilities.OpenWebLink("https://www.roblox.com/users/" + _selected.Model.UserId + "/profile"); });
            SignOutCommand = new RelayCommand(() => _ = SignOutAsync());
            CheckAllCommand = new RelayCommand(() => _ = CheckAllAsync());
            RefreshCommand = new RelayCommand(() => _ = LoadAsync());
            SavedAccounts.Changed += OnStoreChanged;
        }

        public ObservableCollection<AccountRow> Accounts { get; } = new();

        public ICommand SignInCommand { get; }
        public ICommand SignUpCommand { get; }
        public ICommand ToggleImportCommand { get; }
        public ICommand ImportCommand { get; }
        public ICommand AddCurrentCommand { get; }
        public ICommand SwitchCommand { get; }
        public ICommand SwitchAndPlayCommand { get; }
        public ICommand RenewCommand { get; }
        public ICommand PinCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand CopyIdCommand { get; }
        public ICommand CopyUsernameCommand { get; }
        public ICommand OpenProfileCommand { get; }
        public ICommand SignOutCommand { get; }
        public ICommand CheckAllCommand { get; }
        public ICommand RefreshCommand { get; }

        public Visibility BrowserSignInVisibility => Voidstrap.Utility.Platform.IsWindows || Voidstrap.Utility.Platform.IsMacOS ? Visibility.Visible : Visibility.Collapsed;

        public Visibility UnsupportedVisibility => SavedAccounts.SwitchingSupported ? Visibility.Collapsed : Visibility.Visible;

        public bool CanSwitch => SavedAccounts.SwitchingSupported && !_busy;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? ""))
                    ApplyFilter();
            }
        }

        public AccountRow? SelectedAccount
        {
            get => _selected;
            set
            {
                if (!SetProperty(ref _selected, value))
                    return;
                OnPropertyChanged(nameof(DetailVisibility));
                OnPropertyChanged(nameof(EmptyVisibility));
                _ = LoadDetailsAsync(value);
            }
        }

        public Visibility DetailVisibility => _selected != null ? Visibility.Visible : Visibility.Collapsed;

        public Visibility EmptyVisibility => _selected == null ? Visibility.Visible : Visibility.Collapsed;

        public string EmptyText => _all.Count == 0 ? Strings.Accounts_Empty : Strings.Accounts_SelectHint;

        public string Status
        {
            get => _status;
            private set
            {
                if (SetProperty(ref _status, value))
                    OnPropertyChanged(nameof(StatusVisibility));
            }
        }

        public Visibility StatusVisibility => string.IsNullOrEmpty(_status) && !_busy ? Visibility.Collapsed : Visibility.Visible;

        public bool IsBusy
        {
            get => _busy;
            private set
            {
                if (!SetProperty(ref _busy, value))
                    return;
                OnPropertyChanged(nameof(CanSwitch));
                OnPropertyChanged(nameof(StatusVisibility));
            }
        }

        public string CookieText
        {
            get => _cookieText;
            set => SetProperty(ref _cookieText, value ?? "");
        }

        public bool ImportVisible
        {
            get => _importVisible;
            set
            {
                if (SetProperty(ref _importVisible, value))
                    OnPropertyChanged(nameof(ImportVisibility));
            }
        }

        public Visibility ImportVisibility => _importVisible ? Visibility.Visible : Visibility.Collapsed;

        public string Robux { get => _robux; private set => SetProperty(ref _robux, value); }
        public string Premium { get => _premium; private set => SetProperty(ref _premium, value); }
        public string Joined { get => _joined; private set => SetProperty(ref _joined, value); }
        public string Friends { get => _friends; private set => SetProperty(ref _friends, value); }
        public string Followers { get => _followers; private set => SetProperty(ref _followers, value); }

        public async Task LoadAsync()
        {
            CancellationToken token = ResetToken();
            try
            {
                long selectedId = _selected?.Model.UserId ?? 0;
                List<SavedAccount> stored = await SavedAccounts.LoadAsync(token);
                _all.Clear();
                _all.AddRange(stored.Select(a => new AccountRow(a)));
                ApplyFilter();
                OnPropertyChanged(nameof(EmptyText));
                CurrentAccountInfo? current = await SavedAccounts.RefreshCurrentAsync(token);
                MarkCurrent(current?.UserId ?? 0);
                SelectedAccount = Accounts.FirstOrDefault(a => a.Model.UserId == selectedId && selectedId != 0)
                    ?? Accounts.FirstOrDefault(a => a.IsCurrent)
                    ?? Accounts.FirstOrDefault();
                Dictionary<long, string> avatars = await SavedAccounts.GetAvatarsAsync(_all.Select(a => a.Model.UserId), token);
                foreach (AccountRow row in _all)
                    row.AvatarUrl = avatars.TryGetValue(row.Model.UserId, out string? url) ? url : null;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LogIdent, "The saved accounts could not be loaded: " + ex.Message);
            }
        }

        private void OnStoreChanged()
        {
            if (_busy || _disposed)
                return;
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => MarkCurrent(SavedAccounts.Current?.UserId ?? 0)));
        }

        private void MarkCurrent(long userId)
        {
            foreach (AccountRow row in _all)
                row.IsCurrent = userId != 0 && row.Model.UserId == userId;
        }

        private void ApplyFilter()
        {
            string query = _searchText.Trim();
            IEnumerable<AccountRow> rows = _all
                .OrderByDescending(a => a.Model.Pinned)
                .ThenByDescending(a => a.Model.LastUsedUtc)
                .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase);
            if (query.Length > 0)
            {
                rows = rows.Where(a => a.Model.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || a.Model.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || a.Model.Note.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || a.Model.UserId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.Ordinal));
            }
            AccountRow? keep = _selected;
            Accounts.Clear();
            foreach (AccountRow row in rows)
                Accounts.Add(row);
            if (keep != null && Accounts.Contains(keep))
                SelectedAccount = keep;
        }

        private async Task SignInAsync(bool signUp)
        {
            if (Voidstrap.Utility.Platform.IsMacOS)
            {
                await SignInThroughRobloxAsync();
                return;
            }
#if !CROSSPLAT
            string? cookie = Voidstrap.UI.Elements.Overlay.RobloxSignInWindow.Prompt(signUp);
            if (!string.IsNullOrEmpty(cookie))
                await SaveCookieAsync(cookie);
#else
            await Task.CompletedTask;
#endif
        }

        private async Task SignInThroughRobloxAsync()
        {
            if (_busy || !await EnsureRobloxClosedAsync())
                return;
            IsBusy = true;
            CancellationToken token = _cts.Token;
            try
            {
                await SavedAccounts.EnsureCurrentAccountSavedAsync(token);
                await SavedAccounts.SignOutAsync();
                MarkCurrent(0);
                LaunchHandler.LaunchRoblox(LaunchMode.Player);
                Status = Strings.Accounts_SignIn_RobloxApp;
                DateTime deadline = DateTime.UtcNow.AddMinutes(15);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(2000, token);
                    string? cookie = RobloxCookie.ExtractCookieFromDat(SavedAccounts.LiveCookiePath);
                    if (string.IsNullOrEmpty(cookie) || await RobloxCookie.GetAccountAsync(cookie, token) == null)
                        continue;
                    await SavedAccounts.EnsureCurrentAccountSavedAsync(token);
                    {
                        IsBusy = false;
                        await LoadAsync();
                        CurrentAccountInfo? current = SavedAccounts.Current;
                        if (current != null)
                        {
                            Status = string.Format(Strings.Accounts_Status_Added, current.Username);
                            SelectedAccount = Accounts.FirstOrDefault(a => a.Model.UserId == current.UserId) ?? SelectedAccount;
                        }
                        return;
                    }
                }
                Status = "";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LogIdent, "Signing in through Roblox failed: " + ex.Message);
                Status = Strings.Accounts_Status_Failed;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task ImportAsync()
        {
            if (string.IsNullOrWhiteSpace(_cookieText))
                return;
            if (await SaveCookieAsync(_cookieText))
            {
                CookieText = "";
                ImportVisible = false;
            }
        }

        private async Task AddCurrentAsync()
        {
            string? cookie = RobloxCookie.Get();
            if (string.IsNullOrEmpty(cookie))
            {
                Status = Strings.Accounts_NotSignedIn;
                return;
            }
            await SaveCookieAsync(cookie);
        }

        private async Task<bool> SaveCookieAsync(string cookie)
        {
            IsBusy = true;
            Status = Strings.Accounts_SignIn_Verifying;
            try
            {
                SavedAccount? saved = await SavedAccounts.AddFromCookieAsync(cookie, _cts.Token);
                if (saved == null)
                {
                    Status = Strings.Accounts_Status_Failed;
                    return false;
                }
                Status = string.Format(Strings.Accounts_Status_Added, saved.Username);
                IsBusy = false;
                await LoadAsync();
                SelectedAccount = Accounts.FirstOrDefault(a => a.Model.UserId == saved.UserId) ?? SelectedAccount;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LogIdent, "The account could not be saved: " + ex.Message);
                Status = Strings.Accounts_Status_Failed;
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task SwitchAsync(AccountRow? row, bool play)
        {
            if (row == null || _busy || !SavedAccounts.SwitchingSupported)
                return;
            if (row.IsCurrent && !play)
                return;
            if (row.Model.Expired)
            {
                Status = Strings.Accounts_Expired;
                return;
            }
            if (!row.IsCurrent && !await EnsureRobloxClosedAsync())
                return;
            IsBusy = true;
            try
            {
                if (!row.IsCurrent)
                {
                    await SavedAccounts.SwitchAsync(row.Model);
                    MarkCurrent(row.Model.UserId);
                    row.Refresh();
                    ApplyFilter();
                    Status = string.Format(Strings.Accounts_Status_Switched, row.Model.Username);
                }
                if (play)
                    LaunchHandler.LaunchRoblox(LaunchMode.Player);
            }
            catch (System.IO.FileNotFoundException)
            {
                row.Model.Expired = true;
                row.Refresh();
                Status = Strings.Accounts_MissingLogin;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LogIdent, "The account switch failed: " + ex.Message);
                Status = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private static async Task<bool> EnsureRobloxClosedAsync()
        {
            if (!SavedAccounts.IsRobloxRunning())
                return true;
            if (Frontend.ShowMessageBox(Strings.Accounts_CloseRobloxPrompt, MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return false;
            if (await SavedAccounts.CloseRobloxAsync())
                return true;
            Frontend.ShowMessageBox(Strings.Accounts_CloseRobloxFailed, MessageBoxImage.Warning);
            return false;
        }

        private async Task TogglePinAsync()
        {
            if (_selected == null)
                return;
            _selected.Model.Pinned = !_selected.Model.Pinned;
            _selected.Refresh();
            ApplyFilter();
            await SavedAccounts.UpdateAsync(_selected.Model);
        }

        private async Task RemoveAsync()
        {
            AccountRow? row = _selected;
            if (row == null)
                return;
            if (Frontend.ShowMessageBox(string.Format(Strings.Accounts_RemoveConfirm, row.Model.Username), MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            await SavedAccounts.RemoveAsync(row.Model);
            _all.Remove(row);
            SelectedAccount = null;
            ApplyFilter();
            SelectedAccount = Accounts.FirstOrDefault();
            OnPropertyChanged(nameof(EmptyText));
        }

        private async Task SignOutAsync()
        {
            if (!await EnsureRobloxClosedAsync())
                return;
            try
            {
                await SavedAccounts.SignOutAsync();
                MarkCurrent(0);
                Status = Strings.Accounts_Status_SignedOut;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LogIdent, "Signing out failed: " + ex.Message);
                Status = ex.Message;
            }
        }

        private async Task CheckAllAsync()
        {
            if (_busy)
                return;
            IsBusy = true;
            int valid = 0;
            try
            {
                foreach (AccountRow row in _all.ToList())
                {
                    string? cookie = SavedAccounts.ReadCurrentCookie(row.Model);
                    RobloxAccount? account = string.IsNullOrEmpty(cookie) ? null : await RobloxCookie.GetAccountAsync(cookie, _cts.Token);
                    bool expired = account == null;
                    if (!expired)
                    {
                        valid++;
                        row.Model.Username = account!.Username;
                        row.Model.DisplayName = account.DisplayName;
                    }
                    row.Model.Expired = expired;
                    row.Refresh();
                    await SavedAccounts.UpdateAsync(row.Model);
                }
                Status = string.Format(Strings.Accounts_Status_Checked, valid, _all.Count);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task LoadDetailsAsync(AccountRow? row)
        {
            _detailCts?.Cancel();
            _detailCts?.Dispose();
            _detailCts = null;
            Robux = Premium = Joined = Friends = Followers = "";
            if (row == null || row.Model.UserId == 0)
                return;
            _detailCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            CancellationToken token = _detailCts.Token;
            long id = row.Model.UserId;
            string? cookie = SavedAccounts.ReadCurrentCookie(row.Model);
            try
            {
                Task<RobloxApiResponse> robux = RobloxCookie.SendGetWithCookieAsync("https://economy.roblox.com/v1/users/" + id + "/currency", cookie, token);
                Task<RobloxApiResponse> premium = RobloxCookie.SendGetWithCookieAsync("https://premiumfeatures.roblox.com/v1/users/" + id + "/validate-membership", cookie, token);
                Task<RobloxApiResponse> user = RobloxCookie.SendGetWithCookieAsync("https://users.roblox.com/v1/users/" + id, null, token);
                Task<RobloxApiResponse> friends = RobloxCookie.SendGetWithCookieAsync("https://friends.roblox.com/v1/users/" + id + "/friends/count", null, token);
                Task<RobloxApiResponse> followers = RobloxCookie.SendGetWithCookieAsync("https://friends.roblox.com/v1/users/" + id + "/followers/count", null, token);
                await Task.WhenAll(robux, premium, user, friends, followers);
                if (token.IsCancellationRequested)
                    return;
                string none = Strings.Common_NotAvailable;
                Robux = ReadNumber(robux.Result.Body, "robux") ?? none;
                Premium = premium.Result.Body?.Trim() switch
                {
                    "true" => Strings.Accounts_Premium_Active,
                    "false" => Strings.Accounts_Premium_None,
                    _ => none
                };
                Joined = ReadDate(user.Result.Body) ?? none;
                Friends = ReadNumber(friends.Result.Body, "count") ?? none;
                Followers = ReadNumber(followers.Result.Body, "count") ?? none;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static string? ReadNumber(string? json, string property)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty(property, out JsonElement value) && value.TryGetInt64(out long number)
                    ? number.ToString("N0", CultureInfo.CurrentCulture)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? ReadDate(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                return doc.RootElement.TryGetProperty("created", out JsonElement value) && value.TryGetDateTime(out DateTime created)
                    ? created.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private void Copy(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            ClipboardService.SetText(text);
            Status = Strings.Accounts_Copied;
        }

        private CancellationToken ResetToken()
        {
            CancellationTokenSource next = new();
            CancellationTokenSource previous = Interlocked.Exchange(ref _cts, next);
            previous.Cancel();
            previous.Dispose();
            return next.Token;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            SavedAccounts.Changed -= OnStoreChanged;
            _detailCts?.Cancel();
            _detailCts?.Dispose();
            _cts.Cancel();
            _cts.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
