using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Integrations;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionChatWindow : Window
{
    private const int ConversationRefreshTicks = 6;
    private const int MaxMessages = 400;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan EchoMatchWindow = TimeSpan.FromMinutes(2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SynchronizationContext? _previousSynchronizationContext;
    private readonly DispatcherSynchronizationContext _uiSynchronizationContext;
    private readonly DispatcherTimer _poll;
    private readonly ObservableCollection<ChatMessageItem> _messages = new();
    private readonly HashSet<string> _messageIds = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _avatarUrls = new();
    private readonly Dictionary<long, ImageSource> _avatars = new();
    private readonly HashSet<long> _avatarLoads = new();
    private List<ChatListItem> _conversations = new();
    private List<ChatListItem> _friends = new();
    private string _listSignature = string.Empty;
    private RobloxConversation? _active;
    private string? _olderCursor;
    private long _selfId;
    private int _ticks;
    private int _messageGeneration;
    private bool _showFriends;
    private bool _busy;
    private bool _sending;
    private bool _pollBusy;
    private bool _loadingOlder;
    private bool _autoOpened;
    private bool _closed;
    private DateTime _lastTypingUtc = DateTime.MinValue;

    public SessionChatWindow()
    {
        InitializeComponent();
        _previousSynchronizationContext = SynchronizationContext.Current;
        _uiSynchronizationContext = new DispatcherSynchronizationContext(Dispatcher);
        SynchronizationContext.SetSynchronizationContext(_uiSynchronizationContext);
        MessageList.ItemsSource = _messages;
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = PollInterval };
        _poll.Tick += OnPoll;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Chat could not be opened: " + ex.Message);
            if (!_closed)
                ShowListStatus("Chat could not be opened");
        }
    }

    private async Task StartAsync()
    {
        ShowListStatus("Loading chats");
        RobloxChatResult<long> self = await RobloxChat.GetSelfAsync(_lifetime.Token);
        if (_closed)
            return;
        if (self.Status != RobloxChatStatus.Ready)
        {
            ShowListStatus(Describe(self.Status));
            ChatStatus.Text = Describe(self.Status);
            return;
        }
        _selfId = self.Value;
        await LoadConversationsAsync();
        _poll.Start();
    }

    private async Task LoadConversationsAsync()
    {
        if (_busy || _closed)
            return;
        _busy = true;
        try
        {
            RobloxChatResult<List<RobloxConversation>> result = await RobloxChat.GetConversationsAsync(null, _lifetime.Token);
            if (_closed)
                return;
            if (result.Status != RobloxChatStatus.Ready || result.Value == null)
            {
                if (_conversations.Count == 0)
                {
                    ShowListStatus(Describe(result.Status));
                    ChatStatus.Text = Describe(result.Status);
                }
                return;
            }
            string? activeId = _active?.Id;
            _conversations = result.Value
                .OrderByDescending(c => c.UpdatedUtc)
                .Select(c => new ChatListItem(c.Id, TitleOf(c), PreviewOf(c), c.Id == activeId ? 0 : c.Unread, OtherParticipant(c), c))
                .ToList();
            await LoadAvatarsAsync(_conversations.Select(c => c.UserId));
            if (_closed)
                return;
            if (!_showFriends)
                RenderList(false);

            // Open the most recent chat straight away instead of waiting for a click
            if (!_autoOpened && _active == null && _conversations.Count > 0)
            {
                _autoOpened = true;
                ChatListItem first = _conversations[0];
                SelectSilently(first);
                await OpenConversationAsync(first.Conversation!);
            }
            else if (_conversations.Count == 0 && _active == null)
            {
                ChatStatus.Text = "No chats yet, pick a friend in the Friends tab to start one";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Chats could not be loaded: " + ex.Message);
            if (!_closed && _conversations.Count == 0)
                ShowListStatus("Chats could not be loaded");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task LoadFriendsAsync()
    {
        if (_friends.Count > 0 || _closed || _selfId <= 0)
        {
            RenderList(true);
            return;
        }
        ShowListStatus("Loading friends");
        ConversationList.ItemsSource = null;
        _listSignature = string.Empty;
        try
        {
            RobloxChatResult<List<RobloxChatUser>> result = await RobloxChat.GetFriendsAsync(_selfId, _lifetime.Token);
            if (_closed)
                return;
            if (result.Status != RobloxChatStatus.Ready || result.Value == null)
            {
                ShowListStatus(Describe(result.Status));
                return;
            }
            _friends = result.Value.Select(f => new ChatListItem(null, f.Label, "@" + f.Name, 0, f.Id, null)).ToList();
            await LoadAvatarsAsync(_friends.Select(f => f.UserId));
            if (!_closed && _showFriends)
                RenderList(true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Friends could not be loaded: " + ex.Message);
            if (!_closed)
                ShowListStatus("Friends could not be loaded");
        }
    }

    // Rebuilding the list resets scrolling and selection, so it only happens when what it shows actually changed
    private void RenderList(bool force)
    {
        if (_closed)
            return;
        string filter = FilterBox.Text.Trim();
        IEnumerable<ChatListItem> source = _showFriends ? _friends : _conversations;
        if (filter.Length > 0)
            source = source.Where(item => item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || item.Preview.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        List<ChatListItem> items = source.ToList();
        string signature = (_showFriends ? "f|" : "c|") + filter + "|" + string.Join("|", items.Select(item => item.Signature));
        if (!force && signature == _listSignature && ConversationList.ItemsSource != null)
            return;
        _listSignature = signature;
        foreach (ChatListItem item in items)
            item.Avatar = AvatarFor(item.UserId);
        double offset = FindScrollViewer(ConversationList)?.VerticalOffset ?? 0;
        ConversationList.SelectionChanged -= ConversationList_SelectionChanged;
        ConversationList.ItemsSource = items;
        if (!_showFriends && _active != null)
            ConversationList.SelectedItem = items.FirstOrDefault(item => item.ConversationId == _active.Id);
        ConversationList.SelectionChanged += ConversationList_SelectionChanged;
        FindScrollViewer(ConversationList)?.ScrollToVerticalOffset(offset);
        ShowListStatus(items.Count > 0 ? string.Empty : _showFriends ? "No friends found" : filter.Length > 0 ? "No chats match" : "No chats yet, start one from Friends");
    }

    private void SelectSilently(ChatListItem item)
    {
        ConversationList.SelectionChanged -= ConversationList_SelectionChanged;
        ConversationList.SelectedItem = item;
        ConversationList.SelectionChanged += ConversationList_SelectionChanged;
    }

    private async void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || ConversationList.SelectedItem is not ChatListItem item)
            return;
        try
        {
            if (item.Conversation != null)
            {
                if (_active?.Id != item.Conversation.Id)
                    await OpenConversationAsync(item.Conversation);
                return;
            }
            ChatStatus.Text = "Opening chat with " + item.Title;
            RobloxChatResult<RobloxConversation> opened = await RobloxChat.OpenDirectAsync(item.UserId, _lifetime.Token);
            if (_closed)
                return;
            if (opened.Status != RobloxChatStatus.Ready || opened.Value == null)
            {
                ChatStatus.Text = opened.Status == RobloxChatStatus.Unavailable
                    ? "Roblox did not allow a chat with " + item.Title + ", check that you are friends and that chat is on in your Roblox privacy settings"
                    : Describe(opened.Status);
                return;
            }
            SetMode(false);
            await OpenConversationAsync(opened.Value);
            _ = LoadConversationsAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "The chat could not be opened: " + ex.Message);
            if (!_closed)
                ChatStatus.Text = "The chat could not be opened";
        }
    }

    private async Task OpenConversationAsync(RobloxConversation conversation)
    {
        _active = conversation;
        int generation = ++_messageGeneration;
        _messages.Clear();
        _messageIds.Clear();
        _olderCursor = null;
        HeaderTitle.Text = TitleOf(conversation);
        HeaderAvatar.Source = AvatarFor(OtherParticipant(conversation));
        ChatStatus.Text = "Loading messages";
        InputBox.IsEnabled = true;
        SendButton.IsEnabled = true;
        ClearUnread(conversation.Id);
        try
        {
            RobloxChatResult<List<RobloxChatMessage>> result = await RobloxChat.GetMessagesAsync(conversation.Id, null, _lifetime.Token);
            if (_closed || generation != _messageGeneration)
                return;
            if (result.Status != RobloxChatStatus.Ready || result.Value == null)
            {
                ChatStatus.Text = result.Status == RobloxChatStatus.Unavailable ? "Messages could not be loaded, trying again shortly" : Describe(result.Status);
                return;
            }
            _olderCursor = result.Cursor;
            AppendMessages(result.Value, true);
            ChatStatus.Text = _messages.Count == 0 ? "No messages yet, say hi" : string.Empty;
            InputBox.Focus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Messages could not be loaded: " + ex.Message);
            if (!_closed && generation == _messageGeneration)
                ChatStatus.Text = "Messages could not be loaded, trying again shortly";
        }
    }

    private void ClearUnread(string conversationId)
    {
        ChatListItem? item = _conversations.FirstOrDefault(c => c.ConversationId == conversationId);
        if (item == null || item.Unread == 0)
            return;
        int index = _conversations.IndexOf(item);
        _conversations[index] = new ChatListItem(item.ConversationId, item.Title, item.Preview, 0, item.UserId, item.Conversation);
        if (!_showFriends)
            RenderList(false);
    }

    private void AppendMessages(IEnumerable<RobloxChatMessage> messages, bool forceScroll)
    {
        bool atBottom = forceScroll || IsNearBottom();
        bool added = false;
        foreach (RobloxChatMessage message in messages.OrderBy(m => m.CreatedUtc))
        {
            if (message.Id.Length > 0 && !_messageIds.Add(message.Id))
                continue;
            bool mine = message.SenderId == _selfId;
            // A message we echoed locally is replaced by the real one when it comes back from Roblox
            if (mine && ReplaceEcho(message))
                continue;
            ChatMessageItem item = new(message, mine, SenderName(message.SenderId));
            int index = _messages.Count;
            while (index > 0 && _messages[index - 1].CreatedUtc > item.CreatedUtc && !_messages[index - 1].IsEcho)
                index--;
            _messages.Insert(index, item);
            added = true;
        }
        if (!added)
            return;
        while (_messages.Count > MaxMessages)
        {
            _messageIds.Remove(_messages[0].Id);
            _messages.RemoveAt(0);
        }
        ChatStatus.Text = string.Empty;
        if (atBottom)
            ScrollToEnd();
    }

    private bool ReplaceEcho(RobloxChatMessage message)
    {
        for (int index = _messages.Count - 1; index >= 0; index--)
        {
            ChatMessageItem echo = _messages[index];
            if (!echo.IsEcho || echo.Text != message.Content || (message.CreatedUtc - echo.CreatedUtc).Duration() > EchoMatchWindow)
                continue;
            _messageIds.Remove(echo.Id);
            _messages[index] = new ChatMessageItem(message, true, SenderName(message.SenderId));
            return true;
        }
        return false;
    }

    private async Task LoadOlderAsync()
    {
        if (_loadingOlder || _closed || _active is not { } active || string.IsNullOrEmpty(_olderCursor))
            return;
        _loadingOlder = true;
        int generation = _messageGeneration;
        try
        {
            RobloxChatResult<List<RobloxChatMessage>> result = await RobloxChat.GetMessagesAsync(active.Id, _olderCursor, _lifetime.Token);
            if (_closed || generation != _messageGeneration || result.Status != RobloxChatStatus.Ready || result.Value == null)
                return;
            List<RobloxChatMessage> older = result.Value.Where(m => m.Id.Length == 0 || !_messageIds.Contains(m.Id)).OrderBy(m => m.CreatedUtc).ToList();
            // A cursor that brings nothing new means the start of the conversation was reached
            _olderCursor = older.Count == 0 || result.Cursor == _olderCursor ? null : result.Cursor;
            ScrollViewer? viewer = FindScrollViewer(MessageList);
            double fromEnd = viewer == null ? 0 : viewer.ExtentHeight - viewer.VerticalOffset;
            for (int index = older.Count - 1; index >= 0; index--)
            {
                RobloxChatMessage message = older[index];
                if (message.Id.Length > 0)
                    _messageIds.Add(message.Id);
                _messages.Insert(0, new ChatMessageItem(message, message.SenderId == _selfId, SenderName(message.SenderId)));
            }
            if (viewer != null && older.Count > 0)
            {
                viewer.UpdateLayout();
                viewer.ScrollToVerticalOffset(Math.Max(0, viewer.ExtentHeight - fromEnd));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Older messages could not be loaded: " + ex.Message);
        }
        finally
        {
            _loadingOlder = false;
        }
    }

    private async void MessageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_closed || e.VerticalChange >= 0 || e.VerticalOffset > 24 || _messages.Count == 0)
            return;
        try
        {
            await LoadOlderAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Older messages could not be shown: " + ex.Message);
        }
    }

    private bool IsNearBottom()
    {
        ScrollViewer? viewer = FindScrollViewer(MessageList);
        return viewer == null || viewer.ScrollableHeight - viewer.VerticalOffset < 48;
    }

    private void ScrollToEnd()
    {
        if (_messages.Count == 0)
            return;
        MessageList.ScrollIntoView(_messages[^1]);
        FindScrollViewer(MessageList)?.ScrollToEnd();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
            return viewer;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, index)) is { } found)
                return found;
        }
        return null;
    }

    private async void OnPoll(object? sender, EventArgs e)
    {
        // Only talk to Roblox while the chat is actually on screen
        if (_closed || _pollBusy || !Root.IsVisible)
            return;
        _pollBusy = true;
        try
        {
            if (_active is { } active)
            {
                int generation = _messageGeneration;
                RobloxChatResult<List<RobloxChatMessage>> result = await RobloxChat.GetMessagesAsync(active.Id, null, _lifetime.Token);
                if (_closed || generation != _messageGeneration)
                    return;
                if (result.Status == RobloxChatStatus.Ready && result.Value != null)
                {
                    _olderCursor ??= result.Cursor;
                    AppendMessages(result.Value, false);
                    if (_messages.Count == 0)
                        ChatStatus.Text = "No messages yet, say hi";
                }
                else if (result.Status == RobloxChatStatus.RateLimited)
                {
                    _ticks = 0;
                }
            }
            if (++_ticks >= ConversationRefreshTicks)
            {
                _ticks = 0;
                await LoadConversationsAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Chat refresh failed: " + ex.Message);
        }
        finally
        {
            _pollBusy = false;
        }
    }

    private async Task SendAsync()
    {
        if (_sending || _closed || _active is not { } active)
            return;
        string text = InputBox.Text.Trim();
        if (text.Length == 0)
            return;
        if (text.Length > RobloxChat.MaxMessageLength)
            text = text[..RobloxChat.MaxMessageLength];
        _sending = true;
        SendButton.IsEnabled = false;
        InputBox.Text = string.Empty;
        // Shown straight away and swapped for the real message when Roblox confirms it
        ChatMessageItem echo = ChatMessageItem.Echo(text, _selfId);
        _messageIds.Add(echo.Id);
        _messages.Add(echo);
        ChatStatus.Text = string.Empty;
        ScrollToEnd();
        try
        {
            RobloxChatResult<RobloxChatMessage> result = await RobloxChat.SendMessageAsync(active.Id, text, _lifetime.Token);
            if (_closed)
                return;
            if (result.Status != RobloxChatStatus.Ready)
            {
                RemoveEcho(echo);
                if (InputBox.Text.Length == 0)
                    InputBox.Text = text;
                ChatStatus.Text = result.Status == RobloxChatStatus.Unavailable ? "Roblox did not accept that message" : Describe(result.Status);
                return;
            }
            if (result.Value is { } sent && _active?.Id == active.Id)
            {
                // Swap the echo for the confirmed message directly, Roblox may have filtered the text
                int index = _messages.IndexOf(echo);
                _messageIds.Remove(echo.Id);
                bool known = sent.Id.Length > 0 && !_messageIds.Add(sent.Id);
                if (index >= 0)
                {
                    if (known)
                        _messages.RemoveAt(index);
                    else
                        _messages[index] = new ChatMessageItem(sent, true, SenderName(sent.SenderId));
                }
                else if (!known)
                {
                    _messages.Add(new ChatMessageItem(sent, true, SenderName(sent.SenderId)));
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "The message could not be sent: " + ex.Message);
            if (!_closed)
            {
                RemoveEcho(echo);
                if (InputBox.Text.Length == 0)
                    InputBox.Text = text;
                ChatStatus.Text = "The message could not be sent";
            }
        }
        finally
        {
            _sending = false;
            if (!_closed)
            {
                SendButton.IsEnabled = _active != null;
                InputBox.Focus();
            }
        }
    }

    private void RemoveEcho(ChatMessageItem echo)
    {
        _messageIds.Remove(echo.Id);
        _messages.Remove(echo);
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SendAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Sending failed: " + ex.Message);
        }
    }

    private async void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;
        e.Handled = true;
        try
        {
            await SendAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Sending failed: " + ex.Message);
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_closed || _active is not { } active || InputBox.Text.Length == 0 || DateTime.UtcNow - _lastTypingUtc < TypingInterval)
            return;
        _lastTypingUtc = DateTime.UtcNow;
        _ = SendTypingAsync(active.Id);
    }

    private async Task SendTypingAsync(string conversationId)
    {
        try
        {
            await RobloxChat.SendTypingAsync(conversationId, _lifetime.Token);
        }
        catch (Exception)
        {
        }
    }

    private void ChatsTab_Click(object sender, RoutedEventArgs e) => SetMode(false);

    private async void FriendsTab_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetMode(true);
            await LoadFriendsAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Friends could not be shown: " + ex.Message);
        }
    }

    private void SetMode(bool friends)
    {
        if (_showFriends == friends && ConversationList.ItemsSource != null)
            return;
        _showFriends = friends;
        ChatsTab.Appearance = friends ? Wpf.Ui.Common.ControlAppearance.Secondary : Wpf.Ui.Common.ControlAppearance.Primary;
        FriendsTab.Appearance = friends ? Wpf.Ui.Common.ControlAppearance.Primary : Wpf.Ui.Common.ControlAppearance.Secondary;
        FilterBox.PlaceholderText = friends ? "Search friends" : "Search chats";
        RenderList(true);
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_closed && IsLoaded)
            RenderList(false);
    }

    private void ShowListStatus(string text)
    {
        if (!_closed)
            ListStatus.Text = text;
    }

    private async Task LoadAvatarsAsync(IEnumerable<long> userIds)
    {
        List<long> missing = userIds.Where(id => id > 0 && !_avatarUrls.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
        {
            try
            {
                foreach (KeyValuePair<long, string> pair in await RobloxChat.GetHeadshotUrlsAsync(missing, _lifetime.Token))
                    _avatarUrls[pair.Key] = pair.Value;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("SessionChatWindow", "Avatar URLs could not be loaded: " + ex.Message);
            }
        }

        List<KeyValuePair<long, string>> pending = _avatarUrls
            .Where(pair => !_avatars.ContainsKey(pair.Key) && _avatarLoads.Add(pair.Key))
            .ToList();
        if (pending.Count > 0)
            _ = LoadAvatarImagesAsync(pending);
    }

    private async Task LoadAvatarImagesAsync(List<KeyValuePair<long, string>> pending)
    {
        try
        {
            Task<(long UserId, BitmapSource? Image)>[] loads = pending.Select(async pair =>
                (pair.Key, await Voidstrap.Utility.SafeImaging.FromHttpAsync(pair.Value, 72, _lifetime.Token))
            ).ToArray();
            (long UserId, BitmapSource? Image)[] results = await Task.WhenAll(loads);
            if (_closed)
                return;
            bool changed = false;
            foreach ((long userId, BitmapSource? image) in results)
            {
                if (image == null)
                    continue;
                _avatars[userId] = image;
                changed = true;
            }
            if (!changed)
                return;
            foreach (ChatListItem item in _conversations.Concat(_friends))
                item.Avatar = AvatarFor(item.UserId);
            RenderList(true);
            if (_active != null)
                HeaderAvatar.Source = AvatarFor(OtherParticipant(_active));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Avatar images could not be loaded: " + ex.Message);
        }
        finally
        {
            foreach (KeyValuePair<long, string> pair in pending)
                _avatarLoads.Remove(pair.Key);
        }
    }

    private ImageSource? AvatarFor(long userId)
    {
        if (userId <= 0)
            return null;
        if (_avatars.TryGetValue(userId, out ImageSource? cached))
            return cached;
        return null;
    }

    private long OtherParticipant(RobloxConversation conversation)
        => conversation.ParticipantIds.FirstOrDefault(id => id != _selfId);

    private string TitleOf(RobloxConversation conversation)
    {
        if (!string.IsNullOrWhiteSpace(conversation.Name))
            return conversation.Name;
        List<string> names = conversation.ParticipantIds.Where(id => id != _selfId).Select(SenderName).Take(4).ToList();
        return names.Count == 0 ? "Chat" : string.Join(", ", names);
    }

    private string PreviewOf(RobloxConversation conversation)
    {
        if (conversation.LastMessage is not { } last)
            return string.Empty;
        string prefix = last.SenderId == _selfId ? "You: " : string.Empty;
        return prefix + last.Content.ReplaceLineEndings(" ");
    }

    private static string SenderName(long userId)
        => RobloxChat.GetCachedUser(userId)?.Label ?? (userId > 0 ? userId.ToString(CultureInfo.InvariantCulture) : "Roblox");

    private static string Describe(RobloxChatStatus status) => status switch
    {
        RobloxChatStatus.NotSignedIn => "Sign in to Roblox in Voidstrap to use chat",
        RobloxChatStatus.SignInExpired => "Your Roblox sign in expired, sign in again in Voidstrap",
        RobloxChatStatus.RateLimited => "Roblox is rate limiting chat, try again in a moment",
        _ => "Roblox chat is unavailable right now"
    };

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        Closed -= OnClosed;
        _poll.Stop();
        _poll.Tick -= OnPoll;
        _lifetime.Cancel();
        _messages.Clear();
        _avatars.Clear();
        ConversationList.ItemsSource = null;
        MessageList.ItemsSource = null;
        if (ReferenceEquals(SynchronizationContext.Current, _uiSynchronizationContext))
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
    }
}

public sealed class ChatListItem
{
    public string? ConversationId { get; }
    public string Title { get; }
    public string Preview { get; }
    public int Unread { get; }
    public long UserId { get; }
    public RobloxConversation? Conversation { get; }
    public ImageSource? Avatar { get; set; }
    public bool HasUnread => Unread > 0;
    public string UnreadText => Unread > 99 ? "99+" : Unread.ToString(CultureInfo.CurrentCulture);
    public string Signature => (ConversationId ?? UserId.ToString(CultureInfo.InvariantCulture)) + ":" + Title + ":" + Preview + ":" + Unread;

    public ChatListItem(string? conversationId, string title, string preview, int unread, long userId, RobloxConversation? conversation)
    {
        ConversationId = conversationId;
        Title = title;
        Preview = preview;
        Unread = unread;
        UserId = userId;
        Conversation = conversation;
    }
}

public sealed class ChatMessageItem
{
    public string Id { get; }
    public string Text { get; }
    public string Header { get; }
    public bool IsMine { get; }
    public bool IsEcho { get; }
    public bool Moderated { get; }
    public DateTimeOffset CreatedUtc { get; }
    public HorizontalAlignment Alignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public ChatMessageItem(RobloxChatMessage message, bool mine, string sender)
        : this(message.Id.Length > 0 ? message.Id : "anon-" + Guid.NewGuid().ToString("N"), message.Moderated && message.Content.Length == 0 ? "Message hidden by Roblox" : message.Content,
            mine, message.Moderated, message.CreatedUtc, sender, false)
    {
    }

    private ChatMessageItem(string id, string text, bool mine, bool moderated, DateTimeOffset created, string sender, bool echo)
    {
        Id = id;
        Text = text;
        IsMine = mine;
        Moderated = moderated;
        CreatedUtc = created;
        IsEcho = echo;
        string time = created.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        Header = echo ? "Sending" : mine ? time : sender + "  " + time;
    }

    public static ChatMessageItem Echo(string text, long selfId)
        => new("echo-" + Guid.NewGuid().ToString("N"), text, true, false, DateTimeOffset.UtcNow, string.Empty, true);
}
