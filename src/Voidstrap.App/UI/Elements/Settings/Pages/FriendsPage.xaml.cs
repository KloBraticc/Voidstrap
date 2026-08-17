using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.UI;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public sealed class FriendBadgeEntry
    {
        public string Name { get; init; } = "";
        public ImageSource? Image { get; init; }
    }

    public sealed class FriendItem : INotifyPropertyChanged
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string UsernameDisplay { get; }
        public string Avatar { get; }
        public string AvatarBorder { get; }
        public string EquippedBorderJson { get; }
        public ObservableCollection<FriendBadgeEntry> FriendBadges { get; } = new();

        public Brush RingBrush { get; }

        private ImageSource _borderImageSource;
        public ImageSource BorderImageSource
        {
            get => _borderImageSource;
            private set { _borderImageSource = value; OnChanged(nameof(BorderImageSource)); }
        }

        private double _borderImageWidth;
        public double BorderImageWidth
        {
            get => _borderImageWidth;
            private set { _borderImageWidth = value; OnChanged(nameof(BorderImageWidth)); }
        }

        private double _borderImageHeight;
        public double BorderImageHeight
        {
            get => _borderImageHeight;
            private set { _borderImageHeight = value; OnChanged(nameof(BorderImageHeight)); }
        }

        private Thickness _borderImageMargin;
        public Thickness BorderImageMargin
        {
            get => _borderImageMargin;
            private set { _borderImageMargin = value; OnChanged(nameof(BorderImageMargin)); }
        }

        private int _borderImageZ;
        public int BorderImageZ
        {
            get => _borderImageZ;
            private set { _borderImageZ = value; OnChanged(nameof(BorderImageZ)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;
            string q = query.Trim();
            return DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || UsernameDisplay.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        public FriendItem(WebsiteFriend f)
        {
            Id = f.Id;
            DisplayName = string.IsNullOrWhiteSpace(f.DisplayName) ? f.Username : f.DisplayName;
            UsernameDisplay = "@" + f.Username;
            string a = f.Avatar ?? "";
            if (!string.IsNullOrEmpty(a) && !a.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (!a.StartsWith("/"))
                    a = "/" + a;
                a = App.WebsiteBaseUrl.TrimEnd('/') + a;
            }
            Avatar = a;
            AvatarBorder = f.AvatarBorder ?? "";
            EquippedBorderJson = f.EquippedBorderJson ?? "";
            Brush ring = GradientProfileBorder.ParseBorder(AvatarBorder);
            RingBrush = ring ?? (Application.Current?.TryFindResource("ControlFillColorSecondaryBrush") as Brush) ?? Brushes.Transparent;
            if (!string.IsNullOrEmpty(f.BadgesJson))
            {
                try
                {
                    using JsonDocument badgesDoc = JsonDocument.Parse(f.BadgesJson);
                    var root = badgesDoc.RootElement;
                    if (root.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var b in root.EnumerateArray())
                        {
                            string name = b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                            string image = b.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String ? img.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(name))
                                continue;
                            ImageSource? src = null;
                            if (!string.IsNullOrEmpty(image) && image.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                            {
                                int comma = image.IndexOf(',');
                                if (comma > 0)
                                {
                                    try { src = SafeImaging.FromBytes(Convert.FromBase64String(image.Substring(comma + 1)), 38); } catch { }
                                }
                            }
                            if (src != null)
                                FriendBadges.Add(new FriendBadgeEntry { Name = name, Image = src });
                        }
                    }
                }
                catch { }
            }
        }

        public async Task LoadBorderImageAsync(double avatarSize, double containerSize)
        {
            if (string.IsNullOrEmpty(EquippedBorderJson))
                return;
            try
            {
                string raw = EquippedBorderJson;
                BorderRender render = await Task.Run(() =>
                {
                    try
                    {
                        using JsonDocument d = JsonDocument.Parse(raw);
                        return WebsiteBorderRenderer.Build(d.RootElement, avatarSize, containerSize);
                    }
                    catch
                    {
                        return null;
                    }
                }).ConfigureAwait(true);
                if (render == null || render.Image == null)
                    return;
                BorderImageWidth = render.Width;
                BorderImageHeight = render.Height;
                BorderImageMargin = render.Margin;
                BorderImageZ = render.ZIndex;
                BorderImageSource = render.Image;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("FriendItem::LoadBorder", ex);
            }
        }
    }

    public partial class FriendsPage : UiPage, INotifyPropertyChanged
    {
        private const double MinCardWidth = 260.0;
        private const double CardGap = 12.0;
        private const double AvatarSize = 58.0;

        private const double AvatarContainerSize = 96.0;

        private enum PageState
        {
            Loading,
            SignedOut,
            Empty,
            NoResults,
            Ready
        }

        public ObservableCollection<FriendItem> Friends { get; } = new ObservableCollection<FriendItem>();

        private ICollectionView? _filteredFriends;

        public ICollectionView FilteredFriends
        {
            get
            {
                if (_filteredFriends == null)
                {
                    _filteredFriends = CollectionViewSource.GetDefaultView(Friends);
                    _filteredFriends.Filter = o => o is FriendItem item && item.Matches(_searchText);
                }
                return _filteredFriends;
            }
        }

        private string _searchText = "";

        public string SearchText
        {
            get => _searchText;
            set
            {
                string next = value ?? "";
                if (_searchText == next)
                    return;
                _searchText = next;
                OnChanged(nameof(SearchText));
                try
                {
                    FilteredFriends.Refresh();
                }
                catch
                {
                }
                UpdateState();
            }
        }

        private int _gridColumns = 3;

        public int GridColumns
        {
            get => _gridColumns;
            private set
            {
                if (_gridColumns == value)
                    return;
                _gridColumns = value;
                OnChanged(nameof(GridColumns));
            }
        }

        private PageState _state = PageState.Loading;

        private string _errorMessage = "";

        public event EventHandler BackRequested;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public bool CanRefresh => _state != PageState.Loading;

        public Visibility ListVisibility => _state == PageState.Ready ? Visibility.Visible : Visibility.Collapsed;

        public Visibility StatePanelVisibility => _state == PageState.Ready ? Visibility.Collapsed : Visibility.Visible;

        public Visibility SpinnerVisibility => _state == PageState.Loading ? Visibility.Visible : Visibility.Collapsed;

        public Visibility StateIconVisibility => _state == PageState.Loading ? Visibility.Collapsed : Visibility.Visible;

        public Visibility SearchVisibility => Friends.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        public Visibility CountBadgeVisibility => _state == PageState.Ready || _state == PageState.NoResults ? Visibility.Visible : Visibility.Collapsed;

        public string FriendCountText => Friends.Count == 1 ? "1 friend" : Friends.Count + " friends";

        public SymbolRegular StateIcon => _state switch
        {
            PageState.SignedOut => SymbolRegular.PersonProhibited24,
            PageState.NoResults => SymbolRegular.Search24,
            _ => SymbolRegular.PeopleTeam24,
        };

        public string StateTitle => _state switch
        {
            PageState.Loading => "Loading your friends",
            PageState.SignedOut => "You are not signed in",
            PageState.NoResults => "No friends match that search",
            PageState.Empty => _errorMessage.Length > 0 ? "Could not load friends" : "No friends yet",
            _ => "",
        };

        public string StateSubtitle => _state switch
        {
            PageState.Loading => "This only takes a moment.",
            PageState.SignedOut => "Sign in on the Home page to see your Voidstrap friends.",
            PageState.NoResults => "Try a different name or username.",
            PageState.Empty => _errorMessage.Length > 0 ? _errorMessage : "Add people on the Voidstrap website and they will show up here.",
            _ => "",
        };

        public FriendsPage()
        {
            InitializeComponent();
            DataContext = this;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateColumns();
            _ = LoadAsync();
        }

        private void FriendsPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateColumns();
        }

        private void UpdateColumns()
        {
            double available = FriendsList?.ActualWidth ?? 0.0;
            if (available <= 0.0)
                return;
            int columns = Math.Max(1, (int)((available + CardGap) / (MinCardWidth + CardGap)));
            GridColumns = Math.Min(5, columns);
        }

        private void SetState(PageState state)
        {
            _state = state;
            OnChanged(nameof(CanRefresh));
            OnChanged(nameof(ListVisibility));
            OnChanged(nameof(StatePanelVisibility));
            OnChanged(nameof(SpinnerVisibility));
            OnChanged(nameof(StateIconVisibility));
            OnChanged(nameof(SearchVisibility));
            OnChanged(nameof(CountBadgeVisibility));
            OnChanged(nameof(FriendCountText));
            OnChanged(nameof(StateIcon));
            OnChanged(nameof(StateTitle));
            OnChanged(nameof(StateSubtitle));
        }

        private void UpdateState()
        {
            if (_state == PageState.Loading || _state == PageState.SignedOut)
                return;
            if (Friends.Count == 0)
            {
                SetState(PageState.Empty);
                return;
            }
            SetState(FilteredFriends.Cast<object>().Any() ? PageState.Ready : PageState.NoResults);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _ = LoadAsync();
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (BackRequested != null)
                {
                    BackRequested(this, EventArgs.Empty);
                    return;
                }
                if (NavigationService != null && NavigationService.CanGoBack)
                    NavigationService.GoBack();
                else
                    NavigationService?.Navigate(new HomePage());
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("FriendsPage::Back", ex);
            }
        }

        private async Task LoadAsync()
        {
            if (_state == PageState.Loading && Friends.Count > 0)
                return;
            _errorMessage = "";
            SetState(PageState.Loading);
            try
            {
                if (!WebsiteFriends.IsSignedIn())
                {
                    Friends.Clear();
                    SetState(PageState.SignedOut);
                    return;
                }

                var (ok, friends, error) = await WebsiteFriends.GetFriendsAsync();
                Friends.Clear();
                if (!ok)
                {
                    _errorMessage = error ?? "Could not load friends.";
                    SetState(PageState.Empty);
                    return;
                }
                foreach (WebsiteFriend f in friends)
                {
                    FriendItem item = new FriendItem(f);
                    Friends.Add(item);
                    if (!string.IsNullOrEmpty(item.EquippedBorderJson))
                        _ = item.LoadBorderImageAsync(AvatarSize, AvatarContainerSize);
                }
                SetState(Friends.Count == 0 ? PageState.Empty : PageState.Ready);
                UpdateColumns();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("FriendsPage::Load", ex);
                _errorMessage = "Could not load friends.";
                SetState(PageState.Empty);
            }
        }

        private async void Unfriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string id || string.IsNullOrEmpty(id))
                return;
            FriendItem item = Friends.FirstOrDefault(x => x.Id == id);
            if (item == null)
                return;
            if (Frontend.ShowMessageBox("Remove " + item.DisplayName + " from your friends?", System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
                return;
            var (ok, error) = await WebsiteFriends.UnfriendAsync(id);
            if (ok)
            {
                Friends.Remove(item);
                UpdateState();
            }
            else
            {
                Frontend.ShowMessageBox(error ?? "Could not unfriend.", System.Windows.MessageBoxImage.Warning);
            }
        }
    }
}
