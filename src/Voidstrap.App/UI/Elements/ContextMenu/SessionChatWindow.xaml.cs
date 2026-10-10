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
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TypingInterval = TimeSpan.FromSeconds(4);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _poll;
    private readonly ObservableCollection<ChatMessageItem> _messages = new();
    private readonly HashSet<string> _messageIds = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _avatarUrls = new();
    private readonly Dictionary<long, ImageSource> _avatars = new();
    private List<ChatListItem> _conversations = new();
    private List<ChatListItem> _friends = new();
    private RobloxConversation? _active;
    private long _selfId;
    private int _ticks;
    private int _messageGeneration;
    private bool _showFriends;
    private bool _busy;
    private bool _sending;
    private bool _pollBusy;
    private bool _closed;
    private DateTime _lastTypingUtc = DateTime.MinValue;

    public SessionChatWindow()
    {
        InitializeComponent();
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
                ListStatus.Text = "Chat could not be opened";
        }
    }

    private async Task StartAsync()
    {
        ListStatus.Text = "Loading chats";
        RobloxChatResult<long> self = await RobloxChat.GetSelfAsync(_lifetime.Token);
        if (_closed)
            return;
        if (self.Status != RobloxChatStatus.Ready)
        {
            ListStatus.Text = Describe(self.Status);
            ChatStatus.Text = ListStatus.Text;
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
                    ListStatus.Text = Describe(result.Status);
                return;
            }
            _conversations = result.Value
                .OrderByDescending(c => c.UpdatedUtc)
                .Select(c => new ChatListItem(c.Id, TitleOf(c), PreviewOf(c), c.Unread, OtherParticipant(c), c))
                .ToList();
            await LoadAvatarsAsync(_conversations.Select(c => c.UserId));
            if (!_showFriends)
                RenderList();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Chats could not be loaded: " + ex.Message);
            if (!_closed && _conversations.Count == 0)
                ListStatus.Text = "Chats could not be loaded";
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
            RenderList();
            return;
        }
        ListStatus.Text = "Loading friends";
        ConversationList.ItemsSource = null;
        try
        {
            RobloxChatResult<List<RobloxChatUser>> result = await RobloxChat.GetFriendsAsync(_selfId, _lifetime.Token);
            if (_closed)
                return;
            if (result.Status != RobloxChatStatus.Ready || result.Value == null)
            {
                ListStatus.Text = Describe(result.Status);
                return;
            }
            _friends = result.Value.Select(f => new ChatListItem(null, f.Label, "@" + f.Name, 0, f.Id, null)).ToList();
            await LoadAvatarsAsync(_friends.Select(f => f.UserId));
            if (_showFriends)
                RenderList();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "Friends could not be loaded: " + ex.Message);
            if (!_closed)
                ListStatus.Text = "Friends could not be loaded";
        }
    }

    private void RenderList()
    {
        if (_closed)
            return;
        string filter = FilterBox.Text.Trim();
        IEnumerable<ChatListItem> source = _showFriends ? _friends : _conversations;
        if (filter.Length > 0)
            source = source.Where(item => item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || item.Preview.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        List<ChatListItem> items = source.ToList();
        foreach (ChatListItem item in items)
            item.Avatar = AvatarFor(item.UserId);
        string? selected = _active?.Id;
        ConversationList.SelectionChanged -= ConversationList_SelectionChanged;
        ConversationList.ItemsSource = items;
        if (!_showFriends && selected != null)
            ConversationList.SelectedItem = items.FirstOrDefault(item => item.ConversationId == selected);
        ConversationList.SelectionChanged += ConversationList_SelectionChanged;
        ListStatus.Text = items.Count > 0 ? string.Empty : _showFriends ? "No friends found" : filter.Length > 0 ? "No chats match" : "No chats yet, start one from Friends";
    }

    private async void ConversationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_closed || ConversationList.SelectedItem is not ChatListItem item)
            return;
        try
        {
            if (item.Conversation != null)
            {
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
                    ? "Roblox did not allow a chat with " + item.Title + ", check that you are friends and chat is on in your Roblox privacy settings"
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
        HeaderTitle.Text = TitleOf(conversation);
        HeaderAvatar.Source = AvatarFor(OtherParticipant(conversation));
        ChatStatus.Text = "Loading messages";
        InputBox.IsEnabled = true;
        SendButton.IsEnabled = true;
        RobloxChatResult<List<RobloxChatMessage>> result = await RobloxChat.GetMessagesAsync(conversation.Id, null, _lifetime.Token);
        if (_closed || generation != _messageGeneration)
            return;
        if (result.Status != RobloxChatStatus.Ready || result.Value == null)
        {
            ChatStatus.Text = Describe(result.Status);
            return;
        }
        AppendMessages(result.Value);
        ChatStatus.Text = _messages.Count == 0 ? "Say hi" : string.Empty;
        InputBox.Focus();
    }

    private void AppendMessages(IEnumerable<RobloxChatMessage> messages)
    {
        bool added = false;
        foreach (RobloxChatMessage message in messages.OrderBy(m => m.CreatedUtc))
        {
            if (message.Id.Length > 0 && !_messageIds.Add(message.Id))
                continue;
            _messages.Add(new ChatMessageItem(message, message.SenderId == _selfId, SenderName(message.SenderId)));
            added = true;
        }
        if (!added)
            return;
        while (_messages.Count > 300)
            _messages.RemoveAt(0);
        ChatStatus.Text = string.Empty;
        if (_messages.Count > 0)
            MessageList.ScrollIntoView(_messages[^1]);
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
                if (!_closed && generation == _messageGeneration && result.Status == RobloxChatStatus.Ready && result.Value != null)
                    AppendMessages(result.Value);
                else if (result.Status == RobloxChatStatus.RateLimited)
                    _ticks = 0;
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
        _sending = true;
        SendButton.IsEnabled = false;
        try
        {
            RobloxChatResult<RobloxChatMessage> result = await RobloxChat.SendMessageAsync(active.Id, text, _lifetime.Token);
            if (_closed)
                return;
            if (result.Status != RobloxChatStatus.Ready)
            {
                ChatStatus.Text = result.Status == RobloxChatStatus.Unavailable ? "Roblox did not accept that message" : Describe(result.Status);
                return;
            }
            InputBox.Text = string.Empty;
            AppendMessages(new[]
            {
                result.Value ?? new RobloxChatMessage { Id = "local-" + Guid.NewGuid().ToString("N"), SenderId = _selfId, Content = text, CreatedUtc = DateTimeOffset.UtcNow }
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionChatWindow", "The message could not be sent: " + ex.Message);
            if (!_closed)
                ChatStatus.Text = "The message could not be sent";
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

    private async void SendButton_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private async void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            return;
        e.Handled = true;
        await SendAsync();
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
        SetMode(true);
        await LoadFriendsAsync();
    }

    private void SetMode(bool friends)
    {
        _showFriends = friends;
        ChatsTab.Appearance = friends ? Wpf.Ui.Common.ControlAppearance.Secondary : Wpf.Ui.Common.ControlAppearance.Primary;
        FriendsTab.Appearance = friends ? Wpf.Ui.Common.ControlAppearance.Primary : Wpf.Ui.Common.ControlAppearance.Secondary;
        FilterBox.PlaceholderText = friends ? "Search friends" : "Search chats";
        RenderList();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_closed && IsLoaded)
            RenderList();
    }

    private async Task LoadAvatarsAsync(IEnumerable<long> userIds)
    {
        List<long> missing = userIds.Where(id => id > 0 && !_avatarUrls.ContainsKey(id)).Distinct().ToList();
        if (missing.Count == 0)
            return;
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
            App.Logger.WriteLine("SessionChatWindow", "Avatars could not be loaded: " + ex.Message);
        }
    }

    private ImageSource? AvatarFor(long userId)
    {
        if (userId <= 0 || !_avatarUrls.TryGetValue(userId, out string? url))
            return null;
        if (_avatars.TryGetValue(userId, out ImageSource? cached))
            return cached;
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.UriSource = new Uri(url);
            image.DecodePixelWidth = 72;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            _avatars[userId] = image;
            return image;
        }
        catch (Exception)
        {
            return null;
        }
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
    public string Text { get; }
    public string Header { get; }
    public bool IsMine { get; }
    public bool Moderated { get; }
    public HorizontalAlignment Alignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    public ChatMessageItem(RobloxChatMessage message, bool mine, string sender)
    {
        IsMine = mine;
        Moderated = message.Moderated;
        Text = message.Moderated && message.Content.Length == 0 ? "Message hidden by Roblox" : message.Content;
        string time = message.CreatedUtc.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        Header = mine ? time : sender + "  " + time;
    }
}
