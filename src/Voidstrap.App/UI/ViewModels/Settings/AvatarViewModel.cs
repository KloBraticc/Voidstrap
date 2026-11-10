using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

        public const string EmotesCategory = "Emotes";

        private const string BodyTypeScale = "bodyType";

        private const string HeightScale = "height";

        private const string WidthScale = "width";

        private const string HeadScale = "head";

        private const string ProportionScale = "proportion";

        private static readonly Dictionary<string, string> ScaleProperties = new()
        {
            [BodyTypeScale] = nameof(BodyType),
            [HeightScale] = nameof(ScaleHeight),
            [WidthScale] = nameof(ScaleWidth),
            [HeadScale] = nameof(ScaleHead),
            [ProportionScale] = nameof(ScaleProportion),
        };

        private readonly SavedAccount _account;

        private readonly CancellationTokenSource _cts = new();

        private readonly List<AvatarItemRow> _all = new();

        private CancellationTokenSource? _categoryCts;

        private readonly Dictionary<string, double> _scales = new();

        private readonly Dictionary<string, double> _adjust = new();

        private AvatarRules _rules = new(
            new Dictionary<string, (int Id, int Max)>(),
            new List<string>(),
            new Dictionary<string, (double Min, double Max)>()
        );

        private AvatarItemRow? _selectedAdjust;

        private bool _adjustLoading;

        private ImageSource? _backgroundImage;

        private AvatarState? _state;

        private string _category = RecentCategory;

        private string _searchText = "";

        private string? _status;

        private string? _avatarImageUrl;

        private string? _backgroundUrl;

        private AvatarScene? _scene;

        private long _backgroundId;

        private bool _isR15 = true;

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
            SetSkinColorCommand = new AsyncRelayCommand<string?>(hex => SetSkinColorAsync(hex));
            ResetAdjustCommand = new AsyncRelayCommand(ResetAdjustAsync);
        }

        public ObservableCollection<AvatarItemRow> Items { get; } = new();

        public ObservableCollection<AvatarItemRow> Wearing { get; } = new();

        public ICommand ToggleItemCommand { get; }

        public ICommand RemoveAccessoriesCommand { get; }

        public ICommand SetSkinColorCommand { get; }

        public ICommand ResetAdjustCommand { get; }

        public ObservableCollection<AvatarItemRow> Adjustable { get; } = new();

        public Visibility AdjustVisibility => Adjustable.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        public AvatarItemRow? SelectedAdjust
        {
            get => _selectedAdjust;
            set
            {
                if (SetProperty(ref _selectedAdjust, value))
                    LoadAdjust();
            }
        }

        public double AdjustPositionX
        {
            get => GetAdjustPercent(RobloxAvatar.PositionKind, "X");
            set => SetAdjustPercent(RobloxAvatar.PositionKind, "X", value);
        }

        public double AdjustPositionY
        {
            get => GetAdjustPercent(RobloxAvatar.PositionKind, "Y");
            set => SetAdjustPercent(RobloxAvatar.PositionKind, "Y", value);
        }

        public double AdjustPositionZ
        {
            get => GetAdjustPercent(RobloxAvatar.PositionKind, "Z");
            set => SetAdjustPercent(RobloxAvatar.PositionKind, "Z", value);
        }

        public double AdjustRotationX
        {
            get => GetAdjustPercent(RobloxAvatar.RotationKind, "X");
            set => SetAdjustPercent(RobloxAvatar.RotationKind, "X", value);
        }

        public double AdjustRotationY
        {
            get => GetAdjustPercent(RobloxAvatar.RotationKind, "Y");
            set => SetAdjustPercent(RobloxAvatar.RotationKind, "Y", value);
        }

        public double AdjustRotationZ
        {
            get => GetAdjustPercent(RobloxAvatar.RotationKind, "Z");
            set => SetAdjustPercent(RobloxAvatar.RotationKind, "Z", value);
        }

        public double AdjustScale
        {
            get => Math.Round(_adjust.GetValueOrDefault(RobloxAvatar.ScaleKind + ".X", 1.0) * 100);
            set
            {
                double scale = Math.Round(value) / 100.0;
                if (_adjustLoading || _selectedAdjust == null || Math.Abs(_adjust.GetValueOrDefault(RobloxAvatar.ScaleKind + ".X", 1.0) - scale) < 0.0005)
                    return;
                foreach (string axis in RobloxAvatar.AdjustAxes)
                    _adjust[RobloxAvatar.ScaleKind + "." + axis] = scale;
                OnPropertyChanged();
                _ = ApplyAdjustAsync();
            }
        }

        public double AdjustScaleMin => Math.Round(AdjustBound(RobloxAvatar.ScaleKind, "X").Min * 100);

        public double AdjustScaleMax => Math.Round(AdjustBound(RobloxAvatar.ScaleKind, "X").Max * 100);

        public ObservableCollection<string> Palette { get; } = new();

        public ImageSource? BackgroundImage
        {
            get => _backgroundImage;
            private set => SetProperty(ref _backgroundImage, value);
        }

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
            get => _scales.GetValueOrDefault(BodyTypeScale);
            set => SetScale(BodyTypeScale, value);
        }

        public double ScaleHeight
        {
            get => _scales.GetValueOrDefault(HeightScale, 100);
            set => SetScale(HeightScale, value);
        }

        public double ScaleWidth
        {
            get => _scales.GetValueOrDefault(WidthScale, 100);
            set => SetScale(WidthScale, value);
        }

        public double ScaleHead
        {
            get => _scales.GetValueOrDefault(HeadScale, 100);
            set => SetScale(HeadScale, value);
        }

        public double ScaleProportion
        {
            get => _scales.GetValueOrDefault(ProportionScale);
            set => SetScale(ProportionScale, value);
        }

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
                foreach (string hex in _rules.Palette)
                    Palette.Add("#" + hex);
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
            foreach ((string key, string property) in ScaleProperties)
            {
                double percent = Math.Round(state.Scales.GetValueOrDefault(key) * 100);
                if (_scales.TryGetValue(key, out double current) && current == percent)
                    continue;
                _scales[key] = percent;
                OnPropertyChanged(property);
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
            long adjustId = _selectedAdjust?.Id ?? 0;
            Adjustable.Clear();
            foreach (AvatarItemRow row in Wearing)
                if (_rules.Bounds.ContainsKey(RobloxAvatar.AdjustKey(row.TypeName, RobloxAvatar.ScaleKind, "X")))
                    Adjustable.Add(row);
            _selectedAdjust = Adjustable.FirstOrDefault(r => r.Id == adjustId) ?? Adjustable.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedAdjust));
            OnPropertyChanged(nameof(AdjustVisibility));
            LoadAdjust();
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
                Task<AvatarScene?> scene = RobloxAvatar.LoadSceneAsync(_account.UserId, cookie, token);
                string? backgroundUrl = await background;
                string? imageUrl = await image;
                if (version != _previewVersion)
                    return;
                if (backgroundUrl != BackgroundUrl || backgroundUrl == null)
                {
                    BackgroundUrl = backgroundUrl;
                    ImageSource? picture = await RobloxAvatar.LoadBitmapAsync(backgroundUrl, token);
                    if (version != _previewVersion)
                        return;
                    BackgroundImage = picture;
                }
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
                        EmotesCategory => await RobloxAvatar.ListInventoryAsync(_account.UserId, RobloxAvatar.EmoteTypes, cookie, token),
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
            HashSet<long> worn = _state == null ? new HashSet<long>() : _state.Assets.Select(a => a.Id).Concat(_state.Emotes.Select(e => e.Id)).ToHashSet();
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
            bool wasWorn = row.Worn;
            if (!row.IsOutfit)
                row.Worn = !wasWorn;
            try
            {
                bool ok;
                if (row.IsOutfit)
                {
                    ok = await RobloxOutfits.WearAsync(row.Id, cookie, _cts.Token) == 200;
                }
                else if (row.TypeName == RobloxAvatar.EmoteType)
                {
                    if (_state.Emotes.FirstOrDefault(e => e.Id == row.Id) is AvatarEmote equipped)
                    {
                        ok = await RobloxAvatar.UnequipEmoteAsync(equipped.Position, cookie, _cts.Token);
                    }
                    else
                    {
                        int slot = Enumerable.Range(1, RobloxAvatar.EmoteSlots).FirstOrDefault(p => _state.Emotes.All(e => e.Position != p), RobloxAvatar.EmoteSlots);
                        ok = await RobloxAvatar.EquipEmoteAsync(row.Id, slot, cookie, _cts.Token);
                    }
                }
                else
                {
                    List<AvatarAsset> assets = _state.Assets.ToList();
                    if (assets.RemoveAll(a => a.Id == row.Id) == 0)
                    {
                        (int typeId, int max) = _rules.Types.GetValueOrDefault(row.TypeName, (0, 1));
                        while (max > 0 && assets.Count(a => a.TypeName == row.TypeName) >= max)
                            assets.Remove(assets.First(a => a.TypeName == row.TypeName));
                        assets.Add(new AvatarAsset(row.Id, row.Name, typeId, row.TypeName, RobloxAvatar.DefaultMeta(row.TypeName)));
                    }
                    ok = await RobloxAvatar.SetWearingAsync(assets, cookie, _cts.Token);
                    if (ok && row.TypeName == RobloxAvatar.BackgroundType)
                        _backgroundId = wasWorn ? 0 : row.Id;
                }
                if (ok)
                {
                    await RefreshAvatarAsync();
                }
                else
                {
                    App.Logger?.WriteLine(LOG_IDENT, "Roblox did not accept the change for item " + row.Id);
                    Status = Strings.Accounts_Avatar_Failed;
                    row.Worn = wasWorn;
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

        private Task SetSkinColorAsync(string? hex)
        {
            string value = (hex ?? "").TrimStart('#');
            if (value.Length != 6 || !value.All(Uri.IsHexDigit))
                return Task.CompletedTask;
            return ApplyAsync(cookie => RobloxAvatar.SetBodyColorsAsync(value, cookie, _cts.Token), true);
        }

        private void SetScale(string key, double value)
        {
            double percent = Math.Round(value);
            if (_scales.TryGetValue(key, out double current) && current == percent)
                return;
            _scales[key] = percent;
            OnPropertyChanged(ScaleProperties[key]);
            if (!_loaded || _state == null)
                return;
            Dictionary<string, double> scales = new(_state.Scales);
            foreach ((string name, double amount) in _scales)
                scales[name] = amount / 100.0;
            _ = ApplyAsync(cookie => RobloxAvatar.SetScalesAsync(scales, cookie, _cts.Token), false);
        }

        private (double Min, double Max) AdjustBound(string kind, string axis)
        {
            if (_selectedAdjust != null && _rules.Bounds.TryGetValue(RobloxAvatar.AdjustKey(_selectedAdjust.TypeName, kind, axis), out (double Min, double Max) bound))
                return bound;
            return kind switch
            {
                RobloxAvatar.ScaleKind => (0.8, 1.2),
                RobloxAvatar.RotationKind => (-30.0, 30.0),
                _ => (-0.25, 0.25),
            };
        }

        private double GetAdjustPercent(string kind, string axis)
        {
            (double min, double max) = AdjustBound(kind, axis);
            double value = Math.Clamp(_adjust.GetValueOrDefault(kind + "." + axis), min, max);
            return Math.Round((value - min) / (max - min) * 200.0 - 100.0);
        }

        private void SetAdjustPercent(string kind, string axis, double percent, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
        {
            if (_adjustLoading || _selectedAdjust == null || Math.Round(percent) == GetAdjustPercent(kind, axis))
                return;
            (double min, double max) = AdjustBound(kind, axis);
            _adjust[kind + "." + axis] = Math.Round(min + (Math.Clamp(Math.Round(percent), -100, 100) + 100.0) / 200.0 * (max - min), 4);
            OnPropertyChanged(property);
            _ = ApplyAdjustAsync();
        }

        private void LoadAdjust()
        {
            const string LOG_IDENT = "AvatarViewModel::LoadAdjust";
            _adjustLoading = true;
            try
            {
                _adjust.Clear();
                foreach (string axis in RobloxAvatar.AdjustAxes)
                    _adjust[RobloxAvatar.ScaleKind + "." + axis] = 1.0;
                string? meta = _selectedAdjust == null ? null : _state?.Assets.FirstOrDefault(a => a.Id == _selectedAdjust.Id)?.MetaJson;
                if (meta != null)
                {
                    using JsonDocument doc = JsonDocument.Parse(meta);
                    foreach (string kind in RobloxAvatar.AdjustKinds)
                        if (doc.RootElement.TryGetProperty(kind, out JsonElement group) && group.ValueKind == JsonValueKind.Object)
                            foreach (string axis in RobloxAvatar.AdjustAxes)
                                if (group.TryGetProperty(axis, out JsonElement value) && value.ValueKind == JsonValueKind.Number)
                                    _adjust[kind + "." + axis] = value.GetDouble();
                }
            }
            catch (JsonException ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The accessory adjustment could not be read: " + ex.Message);
            }
            OnPropertyChanged(nameof(AdjustScaleMin));
            OnPropertyChanged(nameof(AdjustScaleMax));
            OnPropertyChanged(nameof(AdjustScale));
            OnPropertyChanged(nameof(AdjustPositionX));
            OnPropertyChanged(nameof(AdjustPositionY));
            OnPropertyChanged(nameof(AdjustPositionZ));
            OnPropertyChanged(nameof(AdjustRotationX));
            OnPropertyChanged(nameof(AdjustRotationY));
            OnPropertyChanged(nameof(AdjustRotationZ));
            _adjustLoading = false;
        }

        private Task ApplyAdjustAsync()
        {
            const string LOG_IDENT = "AvatarViewModel::ApplyAdjustAsync";
            if (_state == null || _selectedAdjust == null || !_loaded)
                return Task.CompletedTask;
            long id = _selectedAdjust.Id;
            AvatarAsset? current = _state.Assets.FirstOrDefault(a => a.Id == id);
            if (current == null)
                return Task.CompletedTask;
            JsonObject meta = new();
            try
            {
                if (current.MetaJson != null && JsonNode.Parse(current.MetaJson) is JsonObject existing)
                    meta = existing;
            }
            catch (JsonException ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "The old accessory adjustment was replaced: " + ex.Message);
            }
            if (!meta.ContainsKey("version"))
                meta["version"] = 1;
            foreach (string kind in RobloxAvatar.AdjustKinds)
            {
                JsonObject group = new();
                foreach (string axis in RobloxAvatar.AdjustAxes)
                    group[axis] = _adjust.GetValueOrDefault(kind + "." + axis);
                meta[kind] = group;
            }
            string json = meta.ToJsonString();
            List<AvatarAsset> assets = _state.Assets.Select(a => a.Id == id ? a with { MetaJson = json } : a).ToList();
            return ApplyAsync(cookie => RobloxAvatar.SetWearingAsync(assets, cookie, _cts.Token), false);
        }

        private Task ResetAdjustAsync()
        {
            if (_selectedAdjust == null)
                return Task.CompletedTask;
            _adjustLoading = true;
            foreach (string kind in RobloxAvatar.AdjustKinds)
            foreach (string axis in RobloxAvatar.AdjustAxes)
                _adjust[kind + "." + axis] = kind == RobloxAvatar.ScaleKind ? 1.0 : 0.0;
            _adjustLoading = false;
            return ApplyAdjustAsync();
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
