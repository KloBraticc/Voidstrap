using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations;

namespace Voidstrap.UI.ViewModels.Settings
{
    internal sealed class AvatarViewModel : NotifyPropertyChangedViewModel, IDisposable
    {
        public const string RecentCategory = "Recent";

        public const string CharactersCategory = "Characters";

        public const string BodyCategory = "Body";

        public const string ClothingCategory = "Clothing";

        public const string AccessoriesCategory = "Accessories";

        public const string BackgroundsCategory = "Backgrounds";

        public const string AnimationsCategory = "Animations";

        private static readonly bool Supports3D = Voidstrap.Utility.Platform.IsWindows;

        private readonly SavedAccount _account;

        private readonly CancellationTokenSource _cts = new();

        private readonly List<AvatarItemRow> _all = new();

        private CancellationTokenSource? _categoryCts;

        private Dictionary<string, (int Id, int Max)> _rules = new();

        private AvatarState? _state;

        private string _category = RecentCategory;

        private string _searchText = "";

        private string? _status;

        private string? _avatarImageUrl;

        private string? _backgroundUrl;

        private AvatarScene? _scene;

        private long _backgroundId;

        private bool _isR15 = true;

        private double _bodyType;

        private bool _loading;

        private bool _previewLoading;

        private bool _busy;

        private bool _loaded;

        private bool _disposed;

        private int _previewVersion;

        public AvatarViewModel(SavedAccount account)
        {
            _account = account;
            ToggleItemCommand = new AsyncRelayCommand<AvatarItemRow?>(row => ToggleItemAsync(row));
            RemoveAccessoriesCommand = new AsyncRelayCommand(RemoveAccessoriesAsync);
        }

        public ObservableCollection<AvatarItemRow> Items { get; } = new();

        public ObservableCollection<AvatarItemRow> Wearing { get; } = new();

        public ICommand ToggleItemCommand { get; }

        public ICommand RemoveAccessoriesCommand { get; }

        public string Category
        {
            get => _category;
            set
            {
                if (value != null && SetProperty(ref _category, value) && _loaded)
                    _ = LoadCategoryAsync();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? ""))
                    ApplyFilter();
            }
        }

        public string? Status
        {
            get => _status;
            private set
            {
                if (SetProperty(ref _status, value))
                    OnPropertyChanged(nameof(StatusVisibility));
            }
        }

        public Visibility StatusVisibility => string.IsNullOrEmpty(_status) ? Visibility.Collapsed : Visibility.Visible;

        public string? AvatarImageUrl
        {
            get => _avatarImageUrl;
            private set => SetProperty(ref _avatarImageUrl, value);
        }

        public string? BackgroundUrl
        {
            get => _backgroundUrl;
            private set => SetProperty(ref _backgroundUrl, value);
        }

        public AvatarScene? Scene
        {
            get => _scene;
            private set => SetProperty(ref _scene, value);
        }

        public bool IsR15
        {
            get => _isR15;
            set
            {
                if (SetProperty(ref _isR15, value) && _loaded)
                    _ = SetAvatarTypeAsync(value);
            }
        }

        public double BodyType
        {
            get => _bodyType;
            set
            {
                double clamped = Math.Clamp(Math.Round(value), 0, 100);
                if (!SetProperty(ref _bodyType, clamped))
                    return;
                OnPropertyChanged(nameof(BodyTypeText));
                if (_loaded)
                    _ = SetBodyTypeAsync(clamped);
            }
        }

        public string BodyTypeText => _bodyType.ToString("0", CultureInfo.CurrentCulture) + "%";

        public bool Loading
        {
            get => _loading;
            private set
            {
                if (SetProperty(ref _loading, value))
                    OnPropertyChanged(nameof(EmptyVisibility));
            }
        }

        public bool PreviewLoading
        {
            get => _previewLoading;
            private set => SetProperty(ref _previewLoading, value);
        }

        public Visibility EmptyVisibility => !_loading && Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        public async Task LoadAsync()
        {
            const string LOG_IDENT = "AvatarViewModel::LoadAsync";
            if (_loaded || _disposed)
                return;
            try
            {
                Loading = true;
                _rules = await RobloxAvatar.LoadRulesAsync(_cts.Token);
                await RefreshAvatarAsync();
                _loaded = true;
                await LoadCategoryAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The avatar page could not be loaded: " + ex.Message);
                Status = Strings.Accounts_Avatar_Failed;
                Loading = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _categoryCts?.Cancel();
            _categoryCts?.Dispose();
            _categoryCts = null;
            _cts.Cancel();
            _cts.Dispose();
            GC.SuppressFinalize(this);
        }

        private string? Cookie()
        {
            string? cookie = SavedAccounts.ReadCurrentCookie(_account);
            if (!string.IsNullOrEmpty(cookie))
                return cookie;
            Status = Strings.Accounts_Expired;
            return null;
        }

        private async Task RefreshAvatarAsync()
        {
            string? cookie = Cookie();
            if (cookie == null)
                return;
            AvatarState? state = await RobloxAvatar.GetAsync(cookie, _cts.Token);
            if (state == null)
            {
                Status = Strings.Accounts_Avatar_Failed;
                return;
            }
            _state = state;
            if (_isR15 != (state.AvatarType != "R6"))
            {
                _isR15 = state.AvatarType != "R6";
                OnPropertyChanged(nameof(IsR15));
            }
            double bodyType = Math.Round(state.Scales.GetValueOrDefault("bodyType") * 100);
            if (_bodyType != bodyType)
            {
                _bodyType = bodyType;
                OnPropertyChanged(nameof(BodyType));
                OnPropertyChanged(nameof(BodyTypeText));
            }
            AvatarAsset? background = state.Assets.FirstOrDefault(a => a.TypeName == RobloxAvatar.BackgroundType);
            if (background != null)
                _backgroundId = background.Id;
            Dictionary<long, string?> known = new();
            foreach (AvatarItemRow row in Wearing)
                known[row.Id] = row.ImageUrl;
            Wearing.Clear();
            foreach (AvatarAsset asset in state.Assets)
                Wearing.Add(
                    new AvatarItemRow(asset.Id, asset.Name, asset.TypeName, false) { Worn = true, ImageUrl = known.GetValueOrDefault(asset.Id) }
                );
            MarkWorn();
            _ = FillThumbnailsAsync(Wearing.ToList(), _cts.Token);
            _ = RefreshPreviewAsync(cookie);
        }

        private async Task RefreshPreviewAsync(string cookie)
        {
            const string LOG_IDENT = "AvatarViewModel::RefreshPreviewAsync";
            int version = ++_previewVersion;
            CancellationToken token = _cts.Token;
            try
            {
                PreviewLoading = true;
                if (_backgroundId == 0)
                {
                    List<AvatarItemRow> owned = await RobloxAvatar.ListInventoryAsync(_account.UserId, RobloxAvatar.BackgroundTypes, cookie, token);
                    if (_backgroundId == 0 && owned.Count > 0)
                        _backgroundId = owned[0].Id;
                }
                Task<string?> background = _backgroundId > 0 ? RobloxAvatar.ThumbnailAsync("Asset", _backgroundId, token) : Task.FromResult<string?>(null);
                Task<string?> image = RobloxAvatar.ThumbnailAsync("Avatar", _account.UserId, token);
                Task<AvatarScene?> scene = Supports3D ? RobloxAvatar.LoadSceneAsync(_account.UserId, cookie, token) : Task.FromResult<AvatarScene?>(null);
                string? backgroundUrl = await background;
                string? imageUrl = await image;
                if (version != _previewVersion)
                    return;
                BackgroundUrl = backgroundUrl;
                AvatarImageUrl = imageUrl ?? AvatarImageUrl;
                AvatarScene? loaded = await scene;
                if (version == _previewVersion)
                    Scene = loaded;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The avatar preview could not be refreshed: " + ex.Message);
            }
            finally
            {
                if (version == _previewVersion)
                    PreviewLoading = false;
            }
        }

        private async Task LoadCategoryAsync()
        {
            const string LOG_IDENT = "AvatarViewModel::LoadCategoryAsync";
            if (_disposed)
                return;
            _categoryCts?.Cancel();
            _categoryCts?.Dispose();
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _categoryCts = cts;
            CancellationToken token = cts.Token;
            _all.Clear();
            Items.Clear();
            Loading = true;
            try
            {
                string? cookie = Cookie();
                List<AvatarItemRow> rows = new();
                if (cookie != null)
                    rows = _category switch
                    {
                        RecentCategory => await RobloxAvatar.ListRecentAsync(cookie, token),
                        CharactersCategory => (await RobloxOutfits.ListAsync(_account.UserId, cookie, token))
                            .Select(o => new AvatarItemRow(o.Id, o.Name, "", true))
                            .ToList(),
                        BodyCategory => await RobloxAvatar.ListInventoryAsync(_account.UserId, RobloxAvatar.BodyTypes, cookie, token),
                        ClothingCategory => await RobloxAvatar.ListInventoryAsync(_account.UserId, RobloxAvatar.ClothingTypes, cookie, token),
                        AccessoriesCategory => await RobloxAvatar.ListInventoryAsync(_account.UserId, RobloxAvatar.AccessoryTypes, cookie, token),
                        BackgroundsCategory => await RobloxAvatar.ListInventoryAsync(_account.UserId, RobloxAvatar.BackgroundTypes, cookie, token),
                        AnimationsCategory => await RobloxAvatar.ListInventoryAsync(_account.UserId, RobloxAvatar.AnimationTypes, cookie, token),
                        _ => rows,
                    };
                if (token.IsCancellationRequested)
                    return;
                _all.AddRange(rows);
                MarkWorn();
                ApplyFilter();
                Loading = false;
                await RobloxAvatar.FillThumbnailsAsync(rows, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The avatar items could not be loaded: " + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_categoryCts, cts))
                    Loading = false;
            }
        }

        private static async Task FillThumbnailsAsync(List<AvatarItemRow> rows, CancellationToken token)
        {
            const string LOG_IDENT = "AvatarViewModel::FillThumbnailsAsync";
            try
            {
                await RobloxAvatar.FillThumbnailsAsync(rows, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The item pictures could not be loaded: " + ex.Message);
            }
        }

        private void MarkWorn()
        {
            HashSet<long> worn = _state == null ? new HashSet<long>() : _state.Assets.Select(a => a.Id).ToHashSet();
            foreach (AvatarItemRow row in _all)
                row.Worn = !row.IsOutfit && (worn.Contains(row.Id) || row.Id == _backgroundId && row.TypeName == RobloxAvatar.BackgroundType);
        }

        private void ApplyFilter()
        {
            string search = _searchText.Trim();
            Items.Clear();
            foreach (AvatarItemRow row in _all)
                if (search.Length == 0 || row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                    Items.Add(row);
            OnPropertyChanged(nameof(EmptyVisibility));
        }

        private async Task ToggleItemAsync(AvatarItemRow? row)
        {
            const string LOG_IDENT = "AvatarViewModel::ToggleItemAsync";
            if (row == null || _busy || _disposed || _state == null)
                return;
            string? cookie = Cookie();
            if (cookie == null)
                return;
            _busy = true;
            row.Busy = true;
            Status = null;
            try
            {
                bool ok;
                if (row.IsOutfit)
                {
                    ok = await RobloxOutfits.WearAsync(row.Id, cookie, _cts.Token) == 200;
                }
                else
                {
                    List<AvatarAsset> assets = _state.Assets.ToList();
                    if (assets.RemoveAll(a => a.Id == row.Id) == 0)
                    {
                        (int typeId, int max) = _rules.GetValueOrDefault(row.TypeName, (0, 1));
                        while (max > 0 && assets.Count(a => a.TypeName == row.TypeName) >= max)
                            assets.Remove(assets.First(a => a.TypeName == row.TypeName));
                        assets.Add(new AvatarAsset(row.Id, row.Name, typeId, row.TypeName, RobloxAvatar.DefaultMeta(row.TypeName)));
                    }
                    ok = await RobloxAvatar.SetWearingAsync(assets, cookie, _cts.Token);
                    if (ok && row.TypeName == RobloxAvatar.BackgroundType)
                        _backgroundId = row.Worn ? 0 : row.Id;
                }
                if (ok)
                {
                    await RefreshAvatarAsync();
                }
                else
                {
                    App.Logger?.WriteLine(LOG_IDENT, "Roblox did not accept the change for item " + row.Id);
                    Status = Strings.Accounts_Avatar_Failed;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                row.Busy = false;
                _busy = false;
            }
        }

        private async Task RemoveAccessoriesAsync()
        {
            if (_busy || _disposed || _state == null)
                return;
            List<AvatarAsset> assets = _state.Assets.Where(a => !RobloxAvatar.IsRigidAccessory(a)).ToList();
            if (assets.Count == _state.Assets.Count)
                return;
            await ApplyAsync(cookie => RobloxAvatar.SetWearingAsync(assets, cookie, _cts.Token), true);
        }

        private Task SetAvatarTypeAsync(bool r15) => ApplyAsync(cookie => RobloxAvatar.SetAvatarTypeAsync(r15, cookie, _cts.Token), false);

        private Task SetBodyTypeAsync(double percent)
        {
            if (_state == null)
                return Task.CompletedTask;
            Dictionary<string, double> scales = new(_state.Scales) { ["bodyType"] = percent / 100.0 };
            return ApplyAsync(cookie => RobloxAvatar.SetScalesAsync(scales, cookie, _cts.Token), false);
        }

        private async Task ApplyAsync(Func<string, Task<bool>> change, bool exclusive)
        {
            if (_disposed || exclusive && _busy)
                return;
            string? cookie = Cookie();
            if (cookie == null)
                return;
            if (exclusive)
                _busy = true;
            Status = null;
            try
            {
                if (!await change(cookie))
                    Status = Strings.Accounts_Avatar_Failed;
                await RefreshAvatarAsync();
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (exclusive)
                    _busy = false;
            }
        }
    }
}
