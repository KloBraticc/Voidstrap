using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Voidstrap;
using Voidstrap.Integrations;
using Voidstrap.UI.ViewModels.ContextMenu;

namespace Voidstrap.UI.ViewModels
{
    public sealed class SwitcherAccount : INotifyPropertyChanged
    {
        private string _note = "";
        private DateTime _lastUsedUtc;
        private string? _avatarUrl;
        private bool _isCurrent;

        public long UserId { get; set; }
        public string Username { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string DatFile { get; set; } = "";
        public DateTime AddedUtc { get; set; }

        public string Note
        {
            get => _note;
            set { if (_note != (value ?? "")) { _note = value ?? ""; Raise(nameof(Note)); NoteChanged?.Invoke(this); } }
        }

        public DateTime LastUsedUtc
        {
            get => _lastUsedUtc;
            set { _lastUsedUtc = value; Raise(nameof(LastUsedUtc)); Raise(nameof(LastUsedDisplay)); }
        }

        [JsonIgnore]
        public string? AvatarUrl
        {
            get => _avatarUrl;
            set { var v = string.IsNullOrEmpty(value) ? null : value; if (_avatarUrl != v) { _avatarUrl = v; Raise(nameof(AvatarUrl)); } }
        }

        [JsonIgnore]
        public bool IsCurrent
        {
            get => _isCurrent;
            set { if (_isCurrent != value) { _isCurrent = value; Raise(nameof(IsCurrent)); Raise(nameof(CurrentBadgeVisibility)); } }
        }

        public event Action<SwitcherAccount>? NoteChanged;

        [JsonIgnore]
        public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Username : DisplayName;

        [JsonIgnore]
        public string Handle => "@" + Username;

        [JsonIgnore]
        public string Subtitle => "@" + Username + "  ·  ID " + UserId;

        [JsonIgnore]
        public string LastUsedDisplay => LastUsedUtc == default ? "Never switched to" : "Last used " + LastUsedUtc.ToLocalTime().ToString("g");

        [JsonIgnore]
        public Visibility CurrentBadgeVisibility => _isCurrent ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public sealed class AccountSwitcherViewModel : INotifyPropertyChanged, IDisposable
    {
        private sealed class StoredAccount
        {
            public long UserId { get; set; }
            public string Username { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Note { get; set; } = "";
            public string DatFile { get; set; } = "";
            public DateTime AddedUtc { get; set; }
            public DateTime LastUsedUtc { get; set; }
        }

        private static readonly string[] RobloxProcessNames = { "RobloxPlayerBeta", "RobloxStudioBeta", "RobloxPlayer", "Roblox" };

        private readonly string _folder = Paths.AccountBackups;
        private readonly string _metaPath = Path.Combine(Paths.AccountBackups, "accounts.json");
        private readonly string _liveCookiePath = App.RobloxCookiesFilePath;

        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _opLock = new(1, 1);
        private bool _disposed;
        private bool _busy;

        private string _status = "Ready";
        private string _searchText = "";
        private SwitcherAccount? _selected;
        private string _newCookieText = "";
        private bool _importVisible;

        private string _currentTitle = "Not signed in";
        private string _currentSubtitle = "Sign into Roblox to add this account";
        private string? _currentAvatarUrl;
        private bool _isLoggedIn;
        private long _currentUserId;
        private bool _currentInLibrary;

        public ObservableCollection<SwitcherAccount> Accounts { get; } = new();
        public ObservableCollection<SwitcherAccount> FilteredAccounts { get; } = new();

        public ICommand RefreshCommand { get; }
        public ICommand AddCurrentCommand { get; }
        public ICommand SwitchCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand LogoutCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ToggleImportCommand { get; }
        public ICommand ImportCookieCommand { get; }
        public ICommand CopyUserIdCommand { get; }

        public AccountSwitcherViewModel()
        {
            Directory.CreateDirectory(_folder);

            RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
            AddCurrentCommand = new RelayCommand(_ => _ = AddCurrentAsync());
            SwitchCommand = new RelayCommand(o => _ = SwitchAsync(o as SwitcherAccount));
            DeleteCommand = new RelayCommand(o => Delete(o as SwitcherAccount));
            LogoutCommand = new RelayCommand(_ => _ = LogoutAsync());
            OpenFolderCommand = new RelayCommand(_ => OpenFolder());
            ToggleImportCommand = new RelayCommand(_ => ImportVisible = !ImportVisible);
            ImportCookieCommand = new RelayCommand(_ => _ = ImportByCookieAsync());
            CopyUserIdCommand = new RelayCommand(o => CopyUserId(o as SwitcherAccount));

            _ = RefreshAsync();
        }

        public string Status
        {
            get => _status;
            set { _status = value; Raise(nameof(Status)); }
        }

        public string SearchText
        {
            get => _searchText;
            set { if (_searchText != (value ?? "")) { _searchText = value ?? ""; Raise(nameof(SearchText)); ApplyFilter(); } }
        }

        public SwitcherAccount? Selected
        {
            get => _selected;
            set { _selected = value; Raise(nameof(Selected)); }
        }

        public string NewCookieText
        {
            get => _newCookieText;
            set { _newCookieText = value ?? ""; Raise(nameof(NewCookieText)); }
        }

        public bool ImportVisible
        {
            get => _importVisible;
            set { if (_importVisible != value) { _importVisible = value; Raise(nameof(ImportVisible)); Raise(nameof(ImportVisibility)); } }
        }

        public Visibility ImportVisibility => _importVisible ? Visibility.Visible : Visibility.Collapsed;

        public string CurrentTitle { get => _currentTitle; private set { _currentTitle = value; Raise(nameof(CurrentTitle)); } }
        public string CurrentSubtitle { get => _currentSubtitle; private set { _currentSubtitle = value; Raise(nameof(CurrentSubtitle)); } }
        public string? CurrentAvatarUrl { get => _currentAvatarUrl; private set { _currentAvatarUrl = value; Raise(nameof(CurrentAvatarUrl)); } }
        public bool IsLoggedIn { get => _isLoggedIn; private set { _isLoggedIn = value; Raise(nameof(IsLoggedIn)); Raise(nameof(LoggedInVisibility)); Raise(nameof(AddCurrentEnabled)); } }
        public Visibility LoggedInVisibility => _isLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        public bool AddCurrentEnabled => _isLoggedIn && !_currentInLibrary && !_busy;

        public string EmptyStateTitle => Accounts.Count == 0 ? "No saved accounts yet" : "No accounts match your search";
        public string EmptyStateSubtitle => Accounts.Count == 0
            ? "Sign into Roblox, then click \"Add this account\" to save it here for one click switching."
            : "Try a different name, note, or user ID.";
        public Visibility EmptyStateVisibility => FilteredAccounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        private async Task RefreshAsync()
        {
            var nextCts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref _cts, nextCts);
            try
            {
                previousCts?.Cancel();
            }
            catch
            {
            }
            previousCts?.Dispose();
            var ct = nextCts.Token;
            try
            {
                Status = "Loading accounts...";
                LoadMeta();
                await MigrateLooseBackupsAsync();
                ApplyFilter();

                RobloxCookie.InvalidateCache();
                var current = await RobloxCookie.GetAccountAsync(ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested)
                    return;

                if (current != null)
                {
                    _currentUserId = current.UserId;
                    IsLoggedIn = true;
                    CurrentTitle = string.IsNullOrWhiteSpace(current.DisplayName) ? current.Username : current.DisplayName;
                    CurrentSubtitle = "@" + current.Username + "  ·  ID " + current.UserId;
                    _currentInLibrary = Accounts.Any(a => a.UserId == current.UserId);
                    Raise(nameof(AddCurrentEnabled));
                }
                else
                {
                    _currentUserId = 0;
                    IsLoggedIn = false;
                    CurrentTitle = "Not signed in";
                    CurrentSubtitle = "Sign into Roblox to add this account";
                    _currentInLibrary = false;
                }

                foreach (var a in Accounts)
                    a.IsCurrent = a.UserId != 0 && a.UserId == _currentUserId;

                await FetchAvatarsAsync(ct).ConfigureAwait(true);
                Status = Accounts.Count == 0 ? "No saved accounts yet." : $"{Accounts.Count} account(s) saved.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Status = "Error: " + ex.Message;
            }
        }

        private async Task FetchAvatarsAsync(CancellationToken ct)
        {
            var ids = new List<long>();
            if (_currentUserId != 0)
                ids.Add(_currentUserId);
            foreach (var a in Accounts)
                if (a.UserId != 0 && !ids.Contains(a.UserId))
                    ids.Add(a.UserId);
            if (ids.Count == 0)
                return;

            try
            {
                var map = new Dictionary<long, string>();
                for (int i = 0; i < ids.Count; i += 100)
                {
                    var chunk = ids.GetRange(i, Math.Min(100, ids.Count - i));
                    string url = "https://thumbnails.roblox.com/v1/users/avatar-headshot?userIds=" + string.Join(",", chunk) + "&size=150x150&format=Png&isCircular=false";
                    using var doc = JsonDocument.Parse(await Voidstrap.Utility.Http.GetString(url, ct).ConfigureAwait(true));
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in data.EnumerateArray())
                        {
                            long id = el.TryGetProperty("targetId", out var t) && t.TryGetInt64(out var l) ? l : 0;
                            string img = el.TryGetProperty("imageUrl", out var iu) ? (iu.GetString() ?? "") : "";
                            if (id != 0 && !string.IsNullOrEmpty(img))
                                map[id] = img;
                        }
                    }
                }
                if (_currentUserId != 0 && map.TryGetValue(_currentUserId, out var curImg))
                    CurrentAvatarUrl = curImg;
                foreach (var a in Accounts)
                    if (map.TryGetValue(a.UserId, out var img))
                        a.AvatarUrl = img;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("AccountSwitcher", "Avatar fetch failed: " + ex.Message);
            }
        }

        private async Task AddCurrentAsync()
        {
            if (_busy)
                return;
            if (IsRobloxRunning())
            {
                Frontend.ShowMessageBox("Close all Roblox windows before adding the current account.", MessageBoxImage.Warning);
                return;
            }
            _busy = true;
            Raise(nameof(AddCurrentEnabled));
            await _opLock.WaitAsync();
            try
            {
                string? cookie = RobloxCookie.Get();
                if (string.IsNullOrEmpty(cookie))
                {
                    Frontend.ShowMessageBox("You are not signed into Roblox on this PC.", MessageBoxImage.Warning);
                    return;
                }
                var account = await RobloxCookie.GetAccountAsync(cookie).ConfigureAwait(true);
                if (account == null)
                {
                    Frontend.ShowMessageBox("Could not verify the current Roblox account. The cookie may be expired.", MessageBoxImage.Warning);
                    return;
                }
                if (!File.Exists(_liveCookiePath))
                {
                    Frontend.ShowMessageBox("RobloxCookies.dat was not found, so this account cannot be snapshotted.", MessageBoxImage.Warning);
                    return;
                }

                var existing = Accounts.FirstOrDefault(a => a.UserId == account.UserId);
                string datName = existing?.DatFile ?? ("acc_" + account.UserId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dat");
                string datPath = Path.Combine(_folder, datName);
                await CopyWithRetryAsync(_liveCookiePath, datPath, overwrite: true).ConfigureAwait(true);

                if (existing != null)
                {
                    existing.Username = account.Username;
                    existing.DisplayName = account.DisplayName;
                    existing.LastUsedUtc = DateTime.UtcNow;
                    Status = "Updated saved account: " + account.Username;
                }
                else
                {
                    var item = CreateAccount(account.UserId, account.Username, account.DisplayName, "", datName, DateTime.UtcNow, DateTime.UtcNow);
                    Accounts.Insert(0, item);
                    Status = "Added account: " + account.Username;
                }
                _currentInLibrary = true;
                SaveMeta();
                ApplyFilter();
                foreach (var a in Accounts)
                    a.IsCurrent = a.UserId == account.UserId;
                await FetchAvatarsSafeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Add failed: " + ex.Message;
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
            }
            finally
            {
                _opLock.Release();
                _busy = false;
                Raise(nameof(AddCurrentEnabled));
            }
        }

        private async Task ImportByCookieAsync()
        {
            if (_busy)
                return;
            string cookie = _newCookieText.Trim();
            if (string.IsNullOrEmpty(cookie))
            {
                Frontend.ShowMessageBox("Paste a .ROBLOSECURITY cookie value first.", MessageBoxImage.Warning);
                return;
            }
            _busy = true;
            Raise(nameof(AddCurrentEnabled));
            await _opLock.WaitAsync();
            try
            {
                Status = "Verifying cookie...";
                var account = await RobloxCookie.GetAccountAsync(cookie).ConfigureAwait(true);
                if (account == null)
                {
                    Status = "That cookie is invalid or expired.";
                    Frontend.ShowMessageBox("That cookie is invalid or expired.", MessageBoxImage.Warning);
                    return;
                }

                string template = File.Exists(_liveCookiePath)
                    ? _liveCookiePath
                    : Accounts.Select(a => Path.Combine(_folder, a.DatFile)).FirstOrDefault(File.Exists) ?? "";
                if (string.IsNullOrEmpty(template))
                {
                    Frontend.ShowMessageBox("Sign into any Roblox account once (or add the current account) before importing by cookie. Voidstrap needs an existing login as a template.", MessageBoxImage.Warning);
                    return;
                }

                var existing = Accounts.FirstOrDefault(a => a.UserId == account.UserId);
                string datName = existing?.DatFile ?? ("acc_" + account.UserId + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dat");
                string datPath = Path.Combine(_folder, datName);

                if (!RobloxCookie.SynthesizeDatWithCookie(template, cookie, datPath))
                {
                    Status = "Could not import this cookie.";
                    Frontend.ShowMessageBox("Could not build a login file from that cookie.", MessageBoxImage.Error);
                    return;
                }

                if (existing != null)
                {
                    existing.Username = account.Username;
                    existing.DisplayName = account.DisplayName;
                }
                else
                {
                    Accounts.Insert(0, CreateAccount(account.UserId, account.Username, account.DisplayName, "", datName, DateTime.UtcNow, default));
                }
                NewCookieText = "";
                ImportVisible = false;
                SaveMeta();
                ApplyFilter();
                Status = "Imported account: " + account.Username;
                await FetchAvatarsSafeAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Status = "Import failed: " + ex.Message;
            }
            finally
            {
                _opLock.Release();
                _busy = false;
                Raise(nameof(AddCurrentEnabled));
            }
        }

        private async Task SwitchAsync(SwitcherAccount? account)
        {
            if (account == null)
                return;
            try
            {
                if (IsRobloxRunning())
                {
                    Frontend.ShowMessageBox("Close all Roblox windows before switching accounts.", MessageBoxImage.Warning);
                    return;
                }
                string datPath = Path.Combine(_folder, account.DatFile);
                if (!File.Exists(datPath))
                {
                    Frontend.ShowMessageBox("The saved login for this account is missing. Remove it and add the account again.", MessageBoxImage.Warning);
                    return;
                }

                await _opLock.WaitAsync();
                bool backedUp = false;
                try
                {
                    Status = "Switching to " + account.Username + "...";
                    if (File.Exists(_liveCookiePath))
                    {
                        string safety = Path.Combine(_folder, "_previous_session.dat");
                        await CopyWithRetryAsync(_liveCookiePath, safety, overwrite: true).ConfigureAwait(true);
                        backedUp = true;
                    }
                    await ReplaceLiveCookieAsync(datPath).ConfigureAwait(true);
                    RobloxCookie.InvalidateCache();

                    account.LastUsedUtc = DateTime.UtcNow;
                    SaveMeta();
                    ApplyFilter();
                    foreach (var a in Accounts)
                        a.IsCurrent = a == account;
                    _currentUserId = account.UserId;
                    _currentInLibrary = true;
                    CurrentTitle = account.Title;
                    CurrentSubtitle = account.Subtitle;
                    CurrentAvatarUrl = account.AvatarUrl;
                    IsLoggedIn = true;
                    Status = "Signed in as " + account.Username + ". Launch Roblox to play on this account.";
                    await FetchAvatarsSafeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    if (backedUp && !File.Exists(_liveCookiePath))
                    {
                        try
                        {
                            await ReplaceLiveCookieAsync(Path.Combine(_folder, "_previous_session.dat")).ConfigureAwait(true);
                            RobloxCookie.InvalidateCache();
                        }
                        catch
                        {
                        }
                    }
                    Status = "Switch failed: " + ex.Message;
                    Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
                }
                finally
                {
                    _opLock.Release();
                }
            }
            catch (Exception ex)
            {
                Status = "Switch failed: " + ex.Message;
                Frontend.ShowMessageBox(ex.Message, MessageBoxImage.Error);
            }
        }

        private void Delete(SwitcherAccount? account)
        {
            if (account == null)
                return;
            if (Frontend.ShowMessageBox($"Remove the saved account {account.Username}?\nThis only deletes Voidstrap's saved login, not the Roblox account.", MessageBoxImage.Question, MessageBoxButton.YesNo, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            try
            {
                string datPath = Path.Combine(_folder, account.DatFile);
                if (File.Exists(datPath))
                    File.Delete(datPath);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("AccountSwitcher", "Delete dat failed: " + ex.Message);
            }
            account.NoteChanged -= OnNoteChanged;
            Accounts.Remove(account);
            if (_currentInLibrary && account.UserId == _currentUserId)
            {
                _currentInLibrary = false;
                Raise(nameof(AddCurrentEnabled));
            }
            SaveMeta();
            ApplyFilter();
            Status = "Removed: " + account.Username;
        }

        private async Task LogoutAsync()
        {
            try
            {
                if (IsRobloxRunning())
                {
                    Frontend.ShowMessageBox("Close all Roblox windows before signing out.", MessageBoxImage.Warning);
                    return;
                }
                await _opLock.WaitAsync();
                try
                {
                    if (File.Exists(_liveCookiePath))
                    {
                        File.Delete(_liveCookiePath);
                        RobloxCookie.InvalidateCache();
                        _currentUserId = 0;
                        _currentInLibrary = false;
                        IsLoggedIn = false;
                        CurrentTitle = "Not signed in";
                        CurrentSubtitle = "Sign into Roblox to add this account";
                        CurrentAvatarUrl = null;
                        foreach (var a in Accounts)
                            a.IsCurrent = false;
                        Status = "Signed out of Roblox.";
                    }
                    else
                    {
                        Status = "No active Roblox login found.";
                    }
                }
                finally
                {
                    _opLock.Release();
                }
            }
            catch (Exception ex)
            {
                Status = "Sign out failed: " + ex.Message;
            }
        }

        private void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(_folder);
                Process.Start(new ProcessStartInfo { FileName = _folder, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Status = "Failed to open folder: " + ex.Message;
            }
        }

        private void CopyUserId(SwitcherAccount? account)
        {
            if (account == null)
                return;
            try
            {
                Clipboard.SetText(account.UserId.ToString());
                Status = "Copied user ID " + account.UserId;
            }
            catch (Exception ex)
            {
                Status = "Could not copy the user ID: " + ex.Message;
            }
        }

        private SwitcherAccount CreateAccount(long userId, string username, string displayName, string note, string datFile, DateTime addedUtc, DateTime lastUsedUtc)
        {
            var item = new SwitcherAccount
            {
                UserId = userId,
                Username = username,
                DisplayName = displayName,
                Note = note,
                DatFile = datFile,
                AddedUtc = addedUtc,
                LastUsedUtc = lastUsedUtc
            };
            item.NoteChanged += OnNoteChanged;
            return item;
        }

        private void OnNoteChanged(SwitcherAccount account) => SaveMeta();

        private void ApplyFilter()
        {
            string q = _searchText.Trim();
            IEnumerable<SwitcherAccount> ordered = Accounts.OrderByDescending(a => a.LastUsedUtc);
            IEnumerable<SwitcherAccount> src = string.IsNullOrEmpty(q)
                ? ordered
                : ordered.Where(a =>
                    a.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    a.Note.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    a.UserId.ToString().Contains(q, StringComparison.OrdinalIgnoreCase));

            FilteredAccounts.Clear();
            foreach (var a in src)
                FilteredAccounts.Add(a);
            Raise(nameof(Accounts));
            Raise(nameof(EmptyStateVisibility));
            Raise(nameof(EmptyStateTitle));
            Raise(nameof(EmptyStateSubtitle));
        }

        private void LoadMeta()
        {
            foreach (var a in Accounts)
                a.NoteChanged -= OnNoteChanged;
            Accounts.Clear();
            try
            {
                if (!File.Exists(_metaPath))
                    return;
                var list = Voidstrap.Utility.JsonFile.Deserialize<List<StoredAccount>>(_metaPath, Voidstrap.Utility.JsonOptions.Tolerant, 4194304);
                foreach (var s in list.OrderByDescending(s => s.LastUsedUtc))
                {
                    if (string.IsNullOrEmpty(s.DatFile) || !File.Exists(Path.Combine(_folder, s.DatFile)))
                        continue;
                    Accounts.Add(CreateAccount(s.UserId, s.Username, s.DisplayName, s.Note, s.DatFile, s.AddedUtc, s.LastUsedUtc));
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("AccountSwitcher", "LoadMeta failed: " + ex.Message);
            }
        }

        private async Task MigrateLooseBackupsAsync()
        {
            try
            {
                var known = new HashSet<string>(Accounts.Select(a => a.DatFile), StringComparer.OrdinalIgnoreCase);
                bool changed = false;
                foreach (var file in Directory.EnumerateFiles(_folder, "*.dat", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    if (known.Contains(name) || name.StartsWith("_previous", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_auto", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? cookie = RobloxCookie.ExtractCookieFromDat(file);
                    long userId = 0;
                    string username = Path.GetFileNameWithoutExtension(name);
                    if (!string.IsNullOrEmpty(cookie))
                    {
                        try
                        {
                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                            var acc = await RobloxCookie.GetAccountAsync(cookie, cts.Token).ConfigureAwait(true);
                            if (acc != null)
                            {
                                userId = acc.UserId;
                                username = acc.Username;
                                if (Accounts.Any(a => a.UserId == userId && userId != 0))
                                    continue;
                                Accounts.Add(CreateAccount(acc.UserId, acc.Username, acc.DisplayName, "", name, File.GetCreationTimeUtc(file), default));
                                changed = true;
                                continue;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            continue;
                        }
                        catch
                        {
                        }
                    }
                    Accounts.Add(CreateAccount(userId, username, "", "Imported backup", name, File.GetCreationTimeUtc(file), default));
                    changed = true;
                }
                if (changed)
                {
                    SaveMeta();
                    ApplyFilter();
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("AccountSwitcher", "Migrate failed: " + ex.Message);
            }
        }

        private void SaveMeta()
        {
            try
            {
                var list = Accounts.Select(a => new StoredAccount
                {
                    UserId = a.UserId,
                    Username = a.Username,
                    DisplayName = a.DisplayName,
                    Note = a.Note,
                    DatFile = a.DatFile,
                    AddedUtc = a.AddedUtc,
                    LastUsedUtc = a.LastUsedUtc
                }).ToList();
                Voidstrap.Utility.JsonFile.SerializeAtomic(_metaPath, list, Voidstrap.Utility.JsonOptions.Indented);
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("AccountSwitcher", "SaveMeta failed: " + ex.Message);
            }
        }

        private static bool IsRobloxRunning()
        {
            foreach (var name in RobloxProcessNames)
            {
                Process[] processes = Array.Empty<Process>();
                try
                {
                    processes = Process.GetProcessesByName(name);
                    foreach (Process process in processes)
                    {
                        try
                        {
                            if (!process.HasExited)
                                return true;
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
                finally
                {
                    foreach (Process process in processes)
                    {
                        try
                        {
                            process.Dispose();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            return false;
        }

        private static async Task CopyWithRetryAsync(string src, string dest, bool overwrite = false, int retries = 5)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var d = new FileStream(dest, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await s.CopyToAsync(d).ConfigureAwait(false);
                    return;
                }
                catch (IOException) when (i < retries - 1)
                {
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }
            throw new IOException("Could not access the Roblox cookie file. Make sure Roblox is fully closed.");
        }

        private async Task ReplaceLiveCookieAsync(string source)
        {
            string tmp = _liveCookiePath + ".tmp";
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    await CopyWithRetryAsync(source, tmp, overwrite: true).ConfigureAwait(false);
                    if (File.Exists(_liveCookiePath))
                        File.Replace(tmp, _liveCookiePath, null);
                    else
                        File.Move(tmp, _liveCookiePath);
                    return;
                }
                catch (IOException) when (i < 4)
                {
                    await Task.Delay(150).ConfigureAwait(false);
                }
            }
            try
            {
                if (File.Exists(tmp))
                    File.Delete(tmp);
            }
            catch
            {
            }
            throw new IOException("Could not write the Roblox cookie file. Make sure Roblox is fully closed.");
        }

        private async Task FetchAvatarsSafeAsync()
        {
            if (_disposed)
                return;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await FetchAvatarsAsync(cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("AccountSwitcher", "Avatar fetch failed: " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                _opLock.Dispose();
                foreach (var a in Accounts)
                    a.NoteChanged -= OnNoteChanged;
            }
            catch
            {
            }
            GC.SuppressFinalize(this);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
