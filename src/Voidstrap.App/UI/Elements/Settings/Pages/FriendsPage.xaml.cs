using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.UI.Elements.Overlay;
using Voidstrap.UI.ViewModels.Pages;
using Wpf.Ui.Controls;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class FriendsPage : UiPage
{
    private List<FriendEntry> _friends = new();
    private CancellationTokenSource? _cts;
    private SessionChatWindow? _chat;
    private FriendEntry? _nicknameTarget;
    private bool _busy;

    public event EventHandler<int>? RequestCountChanged;

    public FriendsPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _ = RefreshAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        ReleaseChat();
    }

    private void ReleaseChat()
    {
        if (_chat == null)
            return;
        ChatHost.Content = null;
        _chat.ReleaseDetached();
        _chat = null;
    }

    private SessionChatWindow EnsureChat()
    {
        if (_chat == null)
        {
            _chat = new SessionChatWindow();
            ChatHost.Content = _chat.DetachContent();
        }
        return _chat;
    }

    private void FriendTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource != FriendTabs)
            return;
        if (FriendTabs.SelectedItem == ChatTab)
            EnsureChat();
        else if (FriendTabs.SelectedItem == RequestsTab)
            _ = LoadRequestsAsync();
    }

    public void ShowRequests() => FriendTabs.SelectedItem = RequestsTab;

    private CancellationToken Token()
    {
        _cts ??= new CancellationTokenSource();
        return _cts.Token;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        await Task.WhenAll(LoadFriendsAsync(), LoadRequestsAsync());
    }

    private void ShowStatus(string? text)
    {
        StatusText.Text = text ?? string.Empty;
        StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string? Describe(RobloxChatStatus status) =>
        status switch
        {
            RobloxChatStatus.Ready => null,
            RobloxChatStatus.NotSignedIn or RobloxChatStatus.SignInExpired => Strings.Ui_Friends_SignIn,
            _ => Strings.Ui_Friends_Failed,
        };

    private async Task LoadFriendsAsync()
    {
        const string LOG_IDENT = "FriendsPage::LoadFriendsAsync";
        CancellationToken token = Token();
        FriendsLoading.Visibility = Visibility.Visible;
        try
        {
            RobloxChatResult<long> self = await RobloxChat.GetSelfAsync(token);
            if (self.Status != RobloxChatStatus.Ready)
            {
                SetFriends(new(), Describe(self.Status));
                return;
            }
            RobloxChatResult<List<RobloxChatUser>> friends = await RobloxChat.GetFriendsAsync(self.Value, token);
            if (friends.Status != RobloxChatStatus.Ready || friends.Value == null)
            {
                SetFriends(new(), Describe(friends.Status));
                return;
            }
            List<long> ids = friends.Value.Select(static user => user.Id).ToList();
            Task<Dictionary<long, string>> heads = RobloxChat.GetHeadshotUrlsAsync(ids, token);
            Task<Dictionary<long, FriendPresenceInfo>> presence = RobloxFriends.GetPresenceAsync(ids, token);
            Task<Dictionary<long, DateTimeOffset>> lastOnline = RobloxFriends.GetLastOnlineAsync(ids, token);
            Task<Dictionary<long, string>> nicknames = RobloxFriends.GetNicknamesAsync(ids, token);
            await Task.WhenAll(heads, presence, lastOnline, nicknames);
            List<FriendEntry> entries = friends
                .Value.Select(user => new FriendEntry
                {
                    UserId = user.Id,
                    Name = user.Name,
                    DisplayName = user.DisplayName,
                    Nickname = nicknames.Result.GetValueOrDefault(user.Id) ?? string.Empty,
                    AvatarUrl = heads.Result.GetValueOrDefault(user.Id) ?? string.Empty,
                    Presence = presence.Result.GetValueOrDefault(user.Id) ?? new FriendPresenceInfo(),
                    LastOnline = lastOnline.Result.TryGetValue(user.Id, out DateTimeOffset when) ? when : null,
                })
                .OrderByDescending(static entry => entry.Presence.Kind == FriendPresenceKind.InGame ? 3 : entry.IsOnline ? 2 : 0)
                .ThenByDescending(static entry => entry.LastOnline ?? DateTimeOffset.MinValue)
                .ToList();
            SetFriends(entries, null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
            SetFriends(new(), Strings.Ui_Friends_Failed);
        }
        finally
        {
            FriendsLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void SetFriends(List<FriendEntry> entries, string? error)
    {
        _friends = entries;
        ShowStatus(error);
        FriendsHeader.Text = string.Format(Strings.Ui_Friends_Header, entries.Count(static entry => entry.IsOnline));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        List<FriendEntry> shown = query.Length == 0
            ? _friends
            : _friends
                .Where(entry =>
                    entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || entry.Nickname.Contains(query, StringComparison.OrdinalIgnoreCase)
                )
                .ToList();
        FriendList.ItemsSource = shown;
        FriendsEmpty.Text = Strings.Ui_Friends_NoFriends;
        FriendsEmpty.Visibility = shown.Count == 0 && StatusText.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async Task LoadRequestsAsync()
    {
        const string LOG_IDENT = "FriendsPage::LoadRequestsAsync";
        CancellationToken token = Token();
        RequestsLoading.Visibility = Visibility.Visible;
        try
        {
            RobloxChatResult<List<FriendRequestInfo>> result = await RobloxFriends.GetRequestsAsync(token);
            List<FriendRequestInfo> requests = result.Value ?? new();
            Dictionary<long, string> heads = requests.Count == 0
                ? new()
                : await RobloxChat.GetHeadshotUrlsAsync(requests.Select(static request => request.UserId), token);
            List<FriendEntry> entries = requests
                .Select(request => new FriendEntry
                {
                    UserId = request.UserId,
                    Name = request.Name,
                    DisplayName = request.DisplayName,
                    AvatarUrl = heads.GetValueOrDefault(request.UserId) ?? string.Empty,
                    RequestedUtc = request.SentUtc,
                })
                .ToList();
            SetRequests(entries);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
        }
        finally
        {
            RequestsLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void SetRequests(List<FriendEntry> entries)
    {
        RequestList.ItemsSource = entries;
        RequestsEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DeclineAllButton.IsEnabled = entries.Count > 0;
        RequestsTitle.Text = Strings.Ui_Friends_Requests + " (" + entries.Count.ToString(CultureInfo.CurrentCulture) + ")";
        RequestsHeader.Text = entries.Count > 0 ? Strings.Ui_Friends_Requests + " (" + entries.Count.ToString(CultureInfo.CurrentCulture) + ")" : Strings.Ui_Friends_Requests;
        RequestCountChanged?.Invoke(this, entries.Count);
    }

    private static FriendEntry? EntryOf(object sender) => (sender as FrameworkElement)?.Tag as FriendEntry;

    private async Task RunAsync(Func<CancellationToken, Task<RobloxChatStatus>> action, Func<Task> after)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            RobloxChatStatus status = await action(Token());
            ShowStatus(Describe(status));
            if (status == RobloxChatStatus.Ready)
                await after();
        }
        catch (OperationCanceledException) { }
        finally
        {
            _busy = false;
        }
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is FriendEntry entry && entry.CanJoin)
            HomePageViewModel.LaunchPlace(entry.Presence.PlaceId, entry.Presence.GameId.Length > 0 ? entry.Presence.GameId : null);
    }

    private void Message_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not FriendEntry entry)
            return;
        FriendTabs.SelectedItem = ChatTab;
        _ = EnsureChat().OpenWithAsync(entry.UserId);
    }

    private void Nickname_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not FriendEntry entry)
            return;
        _nicknameTarget = entry;
        NicknameTarget.Text = Strings.Ui_Friends_Nickname + ": " + entry.Handle;
        NicknameBox.Text = entry.Nickname;
        NicknameEditor.Visibility = Visibility.Visible;
        NicknameBox.Focus();
    }

    private void NicknameCancel_Click(object sender, RoutedEventArgs e) => CloseNickname();

    private void CloseNickname()
    {
        _nicknameTarget = null;
        NicknameEditor.Visibility = Visibility.Collapsed;
    }

    private void NicknameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = SaveNicknameAsync();
        else if (e.Key == Key.Escape)
            CloseNickname();
    }

    private void NicknameSave_Click(object sender, RoutedEventArgs e) => _ = SaveNicknameAsync();

    private async Task SaveNicknameAsync()
    {
        if (_nicknameTarget is not FriendEntry entry)
            return;
        string nickname = NicknameBox.Text.Trim();
        await RunAsync(
            token => RobloxFriends.SetNicknameAsync(entry.UserId, nickname, token),
            () =>
            {
                CloseNickname();
                return LoadFriendsAsync();
            }
        );
    }

    private void Unfriend_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not FriendEntry entry)
            return;
        if (
            Frontend.ShowMessageBox(string.Format(Strings.Ui_Friends_UnfriendConfirm, entry.Title), MessageBoxImage.Warning, MessageBoxButton.YesNo)
            != System.Windows.MessageBoxResult.Yes
        )
            return;
        _ = RunAsync(token => RobloxFriends.UnfriendAsync(entry.UserId, token), LoadFriendsAsync);
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is FriendEntry entry)
            _ = RunAsync(token => RobloxFriends.AcceptAsync(entry.UserId, token), RefreshAsync);
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is FriendEntry entry)
            _ = RunAsync(token => RobloxFriends.DeclineAsync(entry.UserId, token), LoadRequestsAsync);
    }

    private void DeclineAll_Click(object sender, RoutedEventArgs e) =>
        _ = RunAsync(RobloxFriends.DeclineAllAsync, LoadRequestsAsync);

    private void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = AddAsync();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e) => _ = AddAsync();

    private async Task AddAsync()
    {
        string name = AddBox.Text.Trim().TrimStart('@');
        if (name.Length == 0 || _busy)
            return;
        AddButton.IsEnabled = false;
        try
        {
            long id = await RobloxFriends.FindUserIdAsync(name, Token());
            if (id <= 0)
            {
                ShowStatus(string.Format(Strings.Ui_Friends_UserNotFound, name));
                return;
            }
            RobloxChatStatus status = await RobloxFriends.SendRequestAsync(id, Token());
            if (status == RobloxChatStatus.Ready)
            {
                AddBox.Text = string.Empty;
                ShowStatus(string.Format(Strings.Ui_Friends_RequestSent, name));
            }
            else
            {
                ShowStatus(Describe(status));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            AddButton.IsEnabled = true;
        }
    }
}
