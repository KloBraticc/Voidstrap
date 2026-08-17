using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.UI;
using Voidstrap.Utility;
using Wpf.Ui.Common;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages
{
    public sealed class NotificationItem : INotifyPropertyChanged
    {
        private bool _read;
        private bool _isActionEnabled = true;
        private ImageSource? _borderImageSource;
        private double _borderImageWidth;
        private double _borderImageHeight;
        private Thickness _borderImageMargin;

        public WebsiteNotification Source { get; }
        public string Id => Source.Id;
        public bool IsUnread => !_read;
        public string AvatarUrl { get; }
        public string NotificationImageUrl { get; }
        public Visibility AvatarVisibility => string.IsNullOrEmpty(AvatarUrl) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility NotificationImageVisibility => string.IsNullOrEmpty(NotificationImageUrl) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility FallbackVisibility => string.IsNullOrEmpty(AvatarUrl) && string.IsNullOrEmpty(NotificationImageUrl) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ActionVisibility => Source.Type == "friend_request" && IsUnread ? Visibility.Visible : Visibility.Collapsed;
        public SymbolRegular Icon { get; }
        public Brush IconBackground { get; }
        public string LeadText { get; }
        public string MessageText { get; }
        public string StrongTailText { get; }
        public string TimeText { get; }

        public bool IsActionEnabled
        {
            get => _isActionEnabled;
            set
            {
                if (_isActionEnabled == value)
                    return;
                _isActionEnabled = value;
                OnChanged(nameof(IsActionEnabled));
            }
        }

        public ImageSource? BorderImageSource
        {
            get => _borderImageSource;
            private set
            {
                _borderImageSource = value;
                OnChanged(nameof(BorderImageSource));
            }
        }

        public double BorderImageWidth
        {
            get => _borderImageWidth;
            private set
            {
                _borderImageWidth = value;
                OnChanged(nameof(BorderImageWidth));
            }
        }

        public double BorderImageHeight
        {
            get => _borderImageHeight;
            private set
            {
                _borderImageHeight = value;
                OnChanged(nameof(BorderImageHeight));
            }
        }

        public Thickness BorderImageMargin
        {
            get => _borderImageMargin;
            private set
            {
                _borderImageMargin = value;
                OnChanged(nameof(BorderImageMargin));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public NotificationItem(WebsiteNotification source)
        {
            Source = source;
            _read = source.Read;
            AvatarUrl = ResolveImage(source.FromAvatar);
            NotificationImageUrl = string.IsNullOrEmpty(AvatarUrl) ? ResolveImage(source.Image) : string.Empty;
            Icon = ResolveIcon(source.Type);
            IconBackground = ResolveIconBackground(source.Type);
            (LeadText, MessageText, StrongTailText) = ResolveText(source);
            TimeText = ResolveTime(source.Timestamp);
        }

        public void MarkRead()
        {
            if (_read)
                return;
            _read = true;
            Source.Read = true;
            OnChanged(nameof(IsUnread));
            OnChanged(nameof(ActionVisibility));
        }

        public async Task LoadBorderImageAsync()
        {
            if (string.IsNullOrEmpty(Source.EquippedBorderJson))
                return;
            try
            {
                string raw = Source.EquippedBorderJson;
                BorderRender? render = await Task.Run(() =>
                {
                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 32 });
                        return WebsiteBorderRenderer.Build(document.RootElement, 40.0, 56.0);
                    }
                    catch
                    {
                        return null;
                    }
                }).ConfigureAwait(true);
                if (render?.Image == null)
                    return;
                BorderImageWidth = render.Width;
                BorderImageHeight = render.Height;
                BorderImageMargin = render.Margin;
                BorderImageSource = render.Image;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("NotificationItem::LoadBorder", ex);
            }
        }

        private static string ResolveImage(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            if (value.StartsWith("/", StringComparison.Ordinal))
                return App.WebsiteBaseUrl.TrimEnd('/') + value;
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "data"))
                return value;
            return "";
        }

        private static SymbolRegular ResolveIcon(string type)
        {
            return type switch
            {
                "friend_request" => SymbolRegular.PersonAdd24,
                "friend_accept" => SymbolRegular.PersonAvailable24,
                "follow" => SymbolRegular.Person24,
                "like" => SymbolRegular.Heart24,
                "wishlist" => SymbolRegular.Tag24,
                "report" => SymbolRegular.Flag24,
                "warn" => SymbolRegular.Warning24,
                "ban" => SymbolRegular.ShieldError24,
                "unban" => SymbolRegular.ShieldCheckmark24,
                "forum_reply" => SymbolRegular.Chat24,
                "mention" => SymbolRegular.Mention24,
                "quest" => SymbolRegular.TaskListSquareLtr24,
                "level" => SymbolRegular.ArrowTrending24,
                "blackmarket" => SymbolRegular.Tag24,
                _ => SymbolRegular.Info24
            };
        }

        private static Brush ResolveIconBackground(string type)
        {
            Color color = type switch
            {
                "friend_request" or "follow" => Color.FromRgb(2, 132, 199),
                "friend_accept" or "unban" => Color.FromRgb(22, 163, 74),
                "like" => Color.FromRgb(219, 39, 119),
                "wishlist" => Color.FromRgb(124, 58, 237),
                "report" or "warn" => Color.FromRgb(217, 119, 6),
                "ban" => Color.FromRgb(220, 38, 38),
                "quest" => Color.FromRgb(5, 150, 105),
                "level" => Color.FromRgb(79, 70, 229),
                "blackmarket" => Color.FromRgb(124, 58, 237),
                _ => Color.FromRgb(82, 82, 91)
            };
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static (string Lead, string Message, string StrongTail) ResolveText(WebsiteNotification source)
        {
            string name = string.IsNullOrWhiteSpace(source.FromName) ? "Someone" : source.FromName;
            return source.Type switch
            {
                "friend_request" => (name, " sent you a friend request", ""),
                "friend_accept" => (name, " accepted your friend request", ""),
                "follow" => (name, " started following you", ""),
                "like" => (name, " liked your profile", ""),
                "wishlist" => ("", string.IsNullOrWhiteSpace(source.Text) ? "Wishlist updated" : source.Text, ""),
                "report" => ("", string.IsNullOrWhiteSpace(source.Text) ? "New report" : source.Text, ""),
                "warn" => ("", string.IsNullOrWhiteSpace(source.Text) ? "You received a warning" : source.Text, ""),
                "ban" => ("", string.IsNullOrWhiteSpace(source.Text) ? "You have been banned" : source.Text, ""),
                "unban" => ("", string.IsNullOrWhiteSpace(source.Text) ? "You have been unbanned" : source.Text, ""),
                "forum_reply" => (name, " replied in ", string.IsNullOrWhiteSpace(source.Text) ? "a thread" : source.Text),
                "mention" => (name, " mentioned you", ""),
                "quest" => ("", string.IsNullOrWhiteSpace(source.Text) ? "Quest complete" : source.Text, ""),
                "level" => ("", string.IsNullOrWhiteSpace(source.Text) ? "You reached a new level" : source.Text, ""),
                _ => ("", string.IsNullOrWhiteSpace(source.Text) ? "New notification" : source.Text, "")
            };
        }

        private static string ResolveTime(long timestamp)
        {
            if (timestamp <= 0)
                return "";
            try
            {
                TimeSpan age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
                if (age < TimeSpan.Zero || age.TotalSeconds < 60)
                    return "just now";
                if (age.TotalMinutes < 60)
                    return (int)age.TotalMinutes + "m ago";
                if (age.TotalHours < 24)
                    return (int)age.TotalHours + "h ago";
                return (int)age.TotalDays + "d ago";
            }
            catch
            {
                return "";
            }
        }

        private void OnChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public partial class NotificationsPage : UiPage
    {
        private readonly List<NotificationItem> _allNotifications = new List<NotificationItem>();
        private CancellationTokenSource? _pageCts;
        private bool _loaded;
        private bool _unreadOnly;
        private int _loadGeneration;

        public ObservableCollection<NotificationItem> Notifications { get; } = new ObservableCollection<NotificationItem>();
        public event EventHandler? BackRequested;

        public NotificationsPage()
        {
            InitializeComponent();
            DataContext = this;
        }

        public void Refresh()
        {
            if (_loaded)
                _ = LoadAsync();
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded)
                return;
            _loaded = true;
            _pageCts = new CancellationTokenSource();
            WebsiteAuth.Changed += OnWebsiteAuthChanged;
            _ = LoadAsync();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_loaded)
                return;
            _loaded = false;
            WebsiteAuth.Changed -= OnWebsiteAuthChanged;
            _loadGeneration++;
            _pageCts?.Cancel();
            _pageCts?.Dispose();
            _pageCts = null;
        }

        private void OnWebsiteAuthChanged()
        {
            if (!_loaded)
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_loaded)
                    return;
                _pageCts?.Cancel();
                _pageCts?.Dispose();
                _pageCts = new CancellationTokenSource();
                _ = LoadAsync();
            }));
        }

        private async Task LoadAsync()
        {
            CancellationToken token = _pageCts?.Token ?? CancellationToken.None;
            int generation = ++_loadGeneration;
            SetBusy(true);
            StatusText.Text = WebsiteAuth.IsSignedIn() ? "Loading notifications" : "Sign in on the Home page to see your notifications.";
            StatusText.Visibility = Visibility.Visible;
            EmptyPanel.Visibility = Visibility.Collapsed;
            try
            {
                var result = await WebsiteNotifications.GetAsync(token);
                if (token.IsCancellationRequested || generation != _loadGeneration || !_loaded)
                    return;
                _allNotifications.Clear();
                if (!result.Ok)
                {
                    Notifications.Clear();
                    StatusText.Text = result.Error ?? "Could not load notifications.";
                    FilterPanel.Visibility = Visibility.Collapsed;
                    MarkAllButton.Visibility = Visibility.Collapsed;
                    ClearAllButton.Visibility = Visibility.Collapsed;
                    return;
                }
                foreach (WebsiteNotification source in result.Items)
                {
                    NotificationItem item = new NotificationItem(source);
                    _allNotifications.Add(item);
                    if (!string.IsNullOrEmpty(source.EquippedBorderJson))
                        _ = item.LoadBorderImageAsync();
                }
                ApplyFilter();
            }
            finally
            {
                if (generation == _loadGeneration && _loaded)
                    SetBusy(false);
            }
        }

        private void ApplyFilter()
        {
            Notifications.Clear();
            IEnumerable<NotificationItem> visible = _unreadOnly ? _allNotifications.Where(item => item.IsUnread) : _allNotifications;
            foreach (NotificationItem item in visible)
                Notifications.Add(item);
            int unread = _allNotifications.Count(item => item.IsUnread);
            FilterPanel.Visibility = _allNotifications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            MarkAllButton.Visibility = unread == 0 ? Visibility.Collapsed : Visibility.Visible;
            ClearAllButton.Visibility = _allNotifications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyPanel.Visibility = Notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = _unreadOnly ? "No unread notifications." : "No notifications yet.";
            StatusText.Visibility = Notifications.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            StatusText.Text = unread == 0 ? "All caught up" : unread == 1 ? "1 unread notification" : unread + " unread notifications";
            AllButton.Appearance = _unreadOnly ? ControlAppearance.Secondary : ControlAppearance.Primary;
            UnreadButton.Appearance = _unreadOnly ? ControlAppearance.Primary : ControlAppearance.Secondary;
        }

        private void SetBusy(bool busy)
        {
            RefreshButton.IsEnabled = !busy;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Refresh();
        }

        private void All_Click(object sender, RoutedEventArgs e)
        {
            _unreadOnly = false;
            ApplyFilter();
        }

        private void Unread_Click(object sender, RoutedEventArgs e)
        {
            _unreadOnly = true;
            ApplyFilter();
        }

        private async void MarkAll_Click(object sender, RoutedEventArgs e)
        {
            CancellationToken token = _pageCts?.Token ?? CancellationToken.None;
            MarkAllButton.IsEnabled = false;
            var result = await WebsiteNotifications.MarkAllAsync(token);
            MarkAllButton.IsEnabled = true;
            if (!result.Ok)
            {
                if (!string.IsNullOrEmpty(result.Error))
                    Frontend.ShowMessageBox(result.Error, MessageBoxImage.Warning);
                return;
            }
            foreach (NotificationItem item in _allNotifications)
                item.MarkRead();
            WebsiteNotifications.PublishUnread(0);
            ApplyFilter();
        }

        private async void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (Frontend.ShowMessageBox("Remove all of your notifications? This cannot be undone.", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            CancellationToken token = _pageCts?.Token ?? CancellationToken.None;
            ClearAllButton.IsEnabled = false;
            var result = await WebsiteNotifications.ClearAsync(token);
            ClearAllButton.IsEnabled = true;
            if (!result.Ok)
            {
                if (!string.IsNullOrEmpty(result.Error))
                    Frontend.ShowMessageBox(result.Error, MessageBoxImage.Warning);
                return;
            }
            _allNotifications.Clear();
            WebsiteNotifications.PublishUnread(0);
            ApplyFilter();
        }

        private void NotificationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NotificationList.SelectedItem is not NotificationItem item)
                return;
            NotificationList.SelectedItem = null;
            if (item.IsUnread)
            {
                item.MarkRead();
                WebsiteNotifications.PublishUnread(_allNotifications.Count(value => value.IsUnread));
                _ = MarkOneAndRecoverAsync(item.Id, _pageCts?.Token ?? CancellationToken.None);
                ApplyFilter();
            }
            string link = ResolveLink(item.Source);
            if (!string.IsNullOrEmpty(link))
                OpenLink(link);
        }

        private async Task MarkOneAndRecoverAsync(string id, CancellationToken token)
        {
            var result = await WebsiteNotifications.MarkOneAsync(id, token);
            if (!result.Ok && !token.IsCancellationRequested)
                await LoadAsync();
        }

        private void Accept_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement { Tag: NotificationItem item })
                _ = RespondAsync(item, true);
        }

        private void Decline_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is FrameworkElement { Tag: NotificationItem item })
                _ = RespondAsync(item, false);
        }

        private async Task RespondAsync(NotificationItem item, bool accept)
        {
            if (!item.IsActionEnabled)
                return;
            item.IsActionEnabled = false;
            CancellationToken token = _pageCts?.Token ?? CancellationToken.None;
            var result = await WebsiteNotifications.RespondToFriendRequestAsync(item.Source.FromId, accept, token);
            item.IsActionEnabled = true;
            if (!result.Ok)
            {
                if (!string.IsNullOrEmpty(result.Error))
                    Frontend.ShowMessageBox(result.Error, MessageBoxImage.Warning);
                return;
            }
            await LoadAsync();
        }

        private static string ResolveLink(WebsiteNotification notification)
        {
            string relative = notification.Type switch
            {
                "friend_request" => "",
                "forum_reply" or "mention" when notification.Target.StartsWith("/", StringComparison.Ordinal) => notification.Target,
                "quest" or "level" => "/pages/quests.html",
                "warn" or "ban" or "unban" => "/pages/standing.html",
                "report" when IsNumericId(notification.Target) => "/pages/profile.html?id=" + Uri.EscapeDataString(notification.Target),
                "wishlist" when notification.Target.StartsWith("item:", StringComparison.Ordinal) => "/pages/item.html?id=" + Uri.EscapeDataString(notification.Target.Substring(5)),
                "blackmarket" when notification.Target.StartsWith("/", StringComparison.Ordinal) => notification.Target,
                _ when IsNumericId(notification.FromId) => "/pages/profile.html?id=" + Uri.EscapeDataString(notification.FromId),
                _ => ""
            };
            if (string.IsNullOrEmpty(relative))
                return "";
            if (!Uri.TryCreate(App.WebsiteBaseUrl, UriKind.Absolute, out Uri? root) || !Uri.TryCreate(root, relative, out Uri? target))
                return "";
            if (target.Scheme != Uri.UriSchemeHttps || !string.Equals(target.Host, root.Host, StringComparison.OrdinalIgnoreCase))
                return "";
            return target.AbsoluteUri;
        }

        private static bool IsNumericId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 20)
                return false;
            foreach (char character in value)
            {
                if (character < '0' || character > '9')
                    return false;
            }
            return true;
        }

        private static void OpenLink(string link)
        {
            try
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("NotificationsPage::OpenLink", ex);
            }
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
                App.Logger.WriteException("NotificationsPage::Back", ex);
            }
        }
    }
}
