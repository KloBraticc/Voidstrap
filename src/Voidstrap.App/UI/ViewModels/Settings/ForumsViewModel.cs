using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.UI;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings
{
    public sealed partial class WebsiteBadgeEntry : ObservableObject
    {
        public string Name { get; init; } = "";
        [ObservableProperty] private ImageSource? image;
    }

    public sealed class ForumTag
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public string DisplayText { get; set; } = "";
    }

    public sealed partial class ForumPosterAvatar : ObservableObject
    {
        public string Initial { get; set; } = "U";
        public string Name { get; set; } = "";
        [ObservableProperty] private ImageSource? image;
    }

    public sealed partial class ForumReaction : ObservableObject
    {
        public string Emoji { get; set; } = "";
        public string Glyph { get; set; } = "";
        public ForumPost? Post { get; set; }
        [ObservableProperty] private int count;
        [ObservableProperty] private bool mine;
        [ObservableProperty] private string tooltip = "";
    }

    public sealed class ForumReactionChoice
    {
        public string Emoji { get; set; } = "";
        public string Glyph { get; set; } = "";
        public ForumPost? Post { get; set; }
    }

    public sealed class ForumCategory
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Announce { get; set; }
        public int PostCount { get; set; }
        public string LatestText { get; set; } = "";
    }

    public sealed partial class ForumThreadSummary : ObservableObject
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string AvatarInitial { get; set; } = "U";
        public string MetaText { get; set; } = "";
        public string ScoreText { get; set; } = "";
        public Visibility PinnedVisibility { get; set; } = Visibility.Collapsed;
        public string CategoryName { get; set; } = "";
        public Visibility CategoryVisibility { get; set; } = Visibility.Collapsed;
        public string RepliesText { get; set; } = "0";
        public string ViewsText { get; set; } = "0";
        public string TagsText { get; set; } = "";
        public Visibility TagsVisibility { get; set; } = Visibility.Collapsed;
        [ObservableProperty] private ImageSource? avatarImage;
        public ObservableCollection<WebsiteBadgeEntry> AuthorBadges { get; } = new();
        public ObservableCollection<ForumPosterAvatar> Posters { get; } = new();
    }

    public sealed partial class ForumPost : ObservableObject
    {
        public string ReplyId { get; set; } = "";
        public string AuthorName { get; set; } = "";
        public string AvatarInitial { get; set; } = "U";
        public string TimeText { get; set; } = "";
        public string Content { get; set; } = "";
        public string ReplyToId { get; set; } = "";
        public string ReplyToText { get; set; } = "";
        public Visibility ReplyToVisibility { get; set; } = Visibility.Collapsed;
        public Visibility EditedVisibility { get; set; } = Visibility.Collapsed;
        public List<ImageSource?> ImageSlots { get; } = new List<ImageSource?>();
        public List<byte[]?> ImageBytes { get; } = new List<byte[]?>();
        public List<string> ImageMetas { get; } = new List<string>();
        public List<string> MentionNames { get; } = new List<string>();
        public int Up { get; set; }
        public int Down { get; set; }
        public string My { get; set; } = "none";
        public bool Mine { get; set; }
        public Visibility ManageVisibility { get; set; } = Visibility.Collapsed;
        public Visibility ReplyButtonVisibility { get; set; } = Visibility.Collapsed;
        [ObservableProperty] private bool isEditing;
        [ObservableProperty] private string editText = "";
        [ObservableProperty] private ImageSource? avatarImage;
        [ObservableProperty] private string scoreText = "0";
        [ObservableProperty] private Brush upBrush = IdleVoteBrush;
        [ObservableProperty] private Brush downBrush = IdleVoteBrush;
        public ObservableCollection<WebsiteBadgeEntry> AuthorBadges { get; } = new();
        public ObservableCollection<ForumReaction> Reactions { get; } = new();
        public ObservableCollection<ForumReactionChoice> ReactionChoices { get; } = new();
        [ObservableProperty] private string myReaction = "";
        [ObservableProperty] private bool bookmarked;
        [ObservableProperty] private string bookmarkLabel = "Bookmark";
        public Visibility ReactionVisibility { get; set; } = Visibility.Collapsed;

        public static readonly Brush IdleVoteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)));
        public static readonly Brush UpVoteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99)));
        public static readonly Brush DownVoteBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));

        private static Brush Freeze(SolidColorBrush b)
        {
            b.Freeze();
            return b;
        }

        public void ApplyVoteState()
        {
            ScoreText = (Up - Down).ToString("+0;-0;0");
            UpBrush = My == "up" ? UpVoteBrush : IdleVoteBrush;
            DownBrush = My == "down" ? DownVoteBrush : IdleVoteBrush;
        }
    }

    public sealed partial class ForumsViewModel : ObservableObject
    {
        private const int MaxResponseBytes = 85000000;
        private const int MaxTitle = 120;
        private const int MaxContent = 5000;
        private const int MaxReply = 3000;
        private const int MaxRenderedReplies = 500;
        private const int MaxImagesPerPost = 4;
        private const int MaxImageBytes = 1600000;
        private const int MaxAvatarBytes = 1000000;
        private const int MaxAvatarCacheEntries = 64;
        private const int MaxRetainedImageBytes = 48000000;
        private const long MaxDecodedImageBytes = 48000000;

        private static readonly ConcurrentDictionary<string, ImageSource> _avatarCache = new ConcurrentDictionary<string, ImageSource>();
        private static readonly ConcurrentQueue<string> _avatarCacheOrder = new ConcurrentQueue<string>();
        private static readonly ConcurrentDictionary<string, Task<BitmapSource?>> _badgeCache = new(StringComparer.Ordinal);

        private static readonly (string Emoji, string Glyph)[] ReactionCatalogue =
        {
            ("heart", "\u2764\uFE0F"),
            ("thumbsup", "\uD83D\uDC4D"),
            ("smile", "\uD83D\uDE04"),
            ("open_mouth", "\uD83D\uDE2E"),
            ("clap", "\uD83D\uDC4F"),
            ("tada", "\uD83C\uDF89"),
            ("hugs", "\uD83E\uDD17"),
        };

        private static string GlyphFor(string emoji)
        {
            foreach (var entry in ReactionCatalogue)
            {
                if (entry.Emoji == emoji)
                    return entry.Glyph;
            }
            return "";
        }

        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private string _currentCategoryId = "";
        private string _currentThreadId = "";
        private int _page = 1;
        private int _pages = 1;

        [ObservableProperty] private ObservableCollection<ForumCategory> categories = new();
        [ObservableProperty] private ObservableCollection<ForumThreadSummary> threads = new();
        [ObservableProperty] private ObservableCollection<ForumPost> posts = new();
        [ObservableProperty] private bool isBusy;
        [ObservableProperty] private string statusText = "";
        [ObservableProperty] private string statusUrl = "";
        [ObservableProperty] private string currentCategoryName = "";
        [ObservableProperty] private string currentThreadTitle = "";
        [ObservableProperty] private string pageText = "";
        [ObservableProperty] private bool hasPrevPage;
        [ObservableProperty] private bool hasNextPage;
        [ObservableProperty] private string replyText = "";
        [ObservableProperty] private string newThreadTitle = "";
        [ObservableProperty] private string newThreadContent = "";
        [ObservableProperty] private bool canPostInCategory;
        [ObservableProperty] private bool isModerator;
        [ObservableProperty] private Visibility pinVisibility = Visibility.Collapsed;
        [ObservableProperty] private string pinLabel = "Pin thread";
        [ObservableProperty] private string replyTargetText = "";
        [ObservableProperty] private Visibility replyTargetVisibility = Visibility.Collapsed;
        private string _replyTargetId = "";
        [ObservableProperty] private Visibility signedOutVisibility = Visibility.Collapsed;
        [ObservableProperty] private Visibility categoriesVisibility = Visibility.Visible;
        [ObservableProperty] private Visibility threadsVisibility = Visibility.Collapsed;
        [ObservableProperty] private Visibility threadVisibility = Visibility.Collapsed;
        [ObservableProperty] private bool canInteract;
        [ObservableProperty] private ObservableCollection<ForumTag> tags = new();
        [ObservableProperty] private string searchText = "";
        [ObservableProperty] private string activeFilterText = "";
        [ObservableProperty] private Visibility activeFilterVisibility = Visibility.Collapsed;
        [ObservableProperty] private Visibility latestHeaderVisibility = Visibility.Collapsed;
        [ObservableProperty] private Visibility categoryHeaderVisibility = Visibility.Visible;
        [ObservableProperty] private Visibility tagsVisibility = Visibility.Collapsed;
        [ObservableProperty] private string threadViewsText = "";
        [ObservableProperty] private string newThreadTags = "";
        private string _sort = "latest";
        private string _tagFilter = "";
        private string _query = "";
        private bool _latestMode;
        private bool _bootstrapped;
        private bool _active;

        public ForumsViewModel()
        {
        }

        public void Activate()
        {
            if (_active)
                return;
            _active = true;
            WebsiteAuth.Changed += OnWebsiteAuthChanged;
        }

        public void Deactivate()
        {
            if (!_active)
                return;
            _active = false;
            WebsiteAuth.Changed -= OnWebsiteAuthChanged;
        }

        private void OnWebsiteAuthChanged()
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshForAccountChange));
        }

        private void RefreshForAccountChange()
        {
            RefreshCommand.Execute(null);
        }

        public void ClearCache()
        {
            _avatarCache.Clear();
            while (_avatarCacheOrder.TryDequeue(out _)) { }
            Posts.Clear();
            Threads.Clear();
            Categories.Clear();
            _currentCategoryId = "";
            _currentThreadId = "";
            _page = 1;
            _pages = 1;
            StatusText = "";
            StatusUrl = "";
            CurrentCategoryName = "";
            CurrentThreadTitle = "";
            PageText = "";
            ReplyText = "";
            ReplyTargetText = "";
            ReplyTargetVisibility = Visibility.Collapsed;
            _replyTargetId = "";
            NewThreadTitle = "";
            NewThreadContent = "";
            CategoriesVisibility = Visibility.Visible;
            ThreadsVisibility = Visibility.Collapsed;
            ThreadVisibility = Visibility.Collapsed;
            IsBusy = false;
        }

        private static bool IsValidServerId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 40)
                return false;
            foreach (char c in id)
            {
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-'))
                    return false;
            }
            return true;
        }

        private static string Sanitize(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
                return "";
            var sb = new StringBuilder(Math.Min(value.Length, maxLength));
            foreach (char c in value)
            {
                if (sb.Length >= maxLength)
                    break;
                if (c == '\n' || c == '\t')
                    sb.Append(c);
                else if (!char.IsControl(c))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Str(JsonElement e, string name, int maxLength)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
                return Sanitize(v.GetString() ?? "", maxLength);
            return "";
        }

        private static string RawStr(JsonElement e, string name, int maxLength)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String)
            {
                string s = v.GetString() ?? "";
                return s.Length > maxLength ? "" : s;
            }
            return "";
        }

        private static long Num(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n))
                return n;
            return 0;
        }

        private static bool Flag(JsonElement e, string name)
        {
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.True;
        }

        private static string TimeText(long epochMs)
        {
            if (epochMs <= 0)
                return "";
            try
            {
                DateTime local = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).ToLocalTime().DateTime;
                TimeSpan age = DateTime.Now - local;
                if (age.TotalMinutes < 1)
                    return "just now";
                if (age.TotalHours < 1)
                    return $"{(int)age.TotalMinutes}m ago";
                if (age.TotalDays < 1)
                    return $"{(int)age.TotalHours}h ago";
                if (age.TotalDays < 30)
                    return $"{(int)age.TotalDays}d ago";
                return local.ToString("MMM d, yyyy");
            }
            catch
            {
                return "";
            }
        }

        private static string InitialOf(string name)
        {
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                    return char.ToUpperInvariant(c).ToString();
            }
            return "U";
        }

        private static ImageSource? DecodeImageBytes(byte[] bytes, int decodePixelWidth)
        {
            try
            {
                var image = Voidstrap.Utility.SafeImaging.FromBytes(bytes, decodePixelWidth);
                if (image != null)
                {
                    return image;
                }
            }
            catch
            {
            }
            return WebpImage.TryDecode(bytes, decodePixelWidth);
        }

        private static byte[]? ParseDataUrlBytes(string dataUrl, int maxBytes)
        {
            try
            {
                int comma = dataUrl.IndexOf(',');
                if (comma <= 0 || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                    return null;
                string header = dataUrl.Substring(0, comma);
                if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    return null;
                string mediaType = header.Substring(11, header.Length - 11 - 7).ToLowerInvariant();
                if (mediaType != "png" && mediaType != "jpeg" && mediaType != "jpg" && mediaType != "webp")
                    return null;
                if (dataUrl.Length - comma > maxBytes * 4 / 3 + 8)
                    return null;
                byte[] bytes = Convert.FromBase64String(dataUrl.Substring(comma + 1));
                if (bytes.Length == 0 || bytes.Length > maxBytes)
                    return null;
                return bytes;
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource? DecodeDataUrl(string dataUrl, int maxBytes, int decodePixelWidth)
        {
            byte[]? bytes = ParseDataUrlBytes(dataUrl, maxBytes);
            return bytes == null ? null : DecodeImageBytes(bytes, decodePixelWidth);
        }

        private static string SizeText(int byteCount)
        {
            if (byteCount > 1048576)
                return (byteCount / 1048576.0).ToString("0.0") + " MB";
            return Math.Max(1, byteCount / 1024) + " KB";
        }

        private static (ImageSource? Source, string Meta, byte[]? Bytes, long DecodedBytes) BuildImageSlot(string dataUrl, int remainingBytes, long remainingDecodedBytes)
        {
            if (remainingBytes <= 0 || remainingDecodedBytes <= 0)
                return (null, "image", null, 0);
            byte[]? bytes = ParseDataUrlBytes(dataUrl, Math.Min(MaxImageBytes, remainingBytes));
            if (bytes == null)
                return (null, "image", null, 0);
            string dims = "";
            long decodedBytes = 0;
            try
            {
                var info = SixLabors.ImageSharp.Image.Identify(bytes);
                if (info != null && info.Width > 0 && info.Height > 0)
                {
                    dims = info.Width + "x" + info.Height;
                    long width = Math.Min(info.Width, 900);
                    long height = ((long)info.Height * width + info.Width - 1) / info.Width;
                    decodedBytes = checked(width * height * 4);
                }
            }
            catch
            {
            }
            string meta = "image" + (dims.Length > 0 ? "  " + dims : "") + "  " + SizeText(bytes.Length);
            if (decodedBytes <= 0 || decodedBytes > remainingDecodedBytes)
                return (null, meta, bytes, 0);
            ImageSource? source = DecodeImageBytes(bytes, 900);
            return (source, meta, bytes, source == null ? 0 : decodedBytes);
        }

        private static async Task<byte[]?> ReadBoundedBytesAsync(HttpContent content, int maxBytes, CancellationToken token)
        {
            if (content.Headers.ContentLength is long contentLength && (contentLength <= 0 || contentLength > maxBytes))
                return null;
            await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            int capacity = content.Headers.ContentLength is long length && length > 0 ? (int)length : 81920;
            using MemoryStream output = new MemoryStream(capacity);
            byte[] buffer = new byte[81920];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read == 0)
                    return output.Length == 0 ? null : output.ToArray();
                if (output.Length + read > maxBytes)
                    return null;
                output.Write(buffer, 0, read);
            }
        }

        private static bool IsAllowedAvatarHost(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
                return false;
            string host = uri.Host.ToLowerInvariant();
            if (host == "thumbnails.roblox.com" || host == "tr.rbxcdn.com" || host.EndsWith(".rbxcdn.com", StringComparison.Ordinal))
                return true;
            try
            {
                var siteHost = new Uri(App.WebsiteBaseUrl).Host.ToLowerInvariant();
                if (host == siteHost)
                    return true;
            }
            catch
            {
            }
            return false;
        }

        private static async Task<ImageSource?> LoadAvatarAsync(string avatar)
        {
            if (string.IsNullOrEmpty(avatar) || avatar.Length > MaxImageBytes * 2)
                return null;
            bool isDataUrl = avatar.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
            bool cacheable = !isDataUrl && avatar.Length <= 2048;
            if (cacheable && _avatarCache.TryGetValue(avatar, out ImageSource? cached))
                return cached;

            ImageSource? result = null;
            if (isDataUrl)
            {
                result = DecodeDataUrl(avatar, MaxAvatarBytes, 96);
            }
            else
            {
                string url = avatar;
                if (url.StartsWith("/", StringComparison.Ordinal))
                    url = App.WebsiteBaseUrl + url;
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && IsAllowedAvatarHost(uri))
                {
                    try
                    {
                        result = await Voidstrap.Utility.AppImage.LoadAsync(url, 96).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }

            if (result != null && cacheable && _avatarCache.TryAdd(avatar, result))
            {
                _avatarCacheOrder.Enqueue(avatar);
                while (_avatarCache.Count > MaxAvatarCacheEntries && _avatarCacheOrder.TryDequeue(out string? oldest))
                    _avatarCache.TryRemove(oldest, out _);
            }
            return result;
        }

        private static async Task AssignAvatarsAsync<T>(List<(string Avatar, T Target)> work, Action<T, ImageSource> assign)
        {
            var byUrl = new Dictionary<string, List<T>>(StringComparer.Ordinal);
            foreach (var (avatar, target) in work)
            {
                if (string.IsNullOrEmpty(avatar))
                    continue;
                if (!byUrl.TryGetValue(avatar, out List<T>? targets))
                {
                    targets = new List<T>();
                    byUrl[avatar] = targets;
                }
                targets.Add(target);
            }
            var loaded = new ConcurrentBag<(ImageSource Image, List<T> Targets)>();
            await Parallel.ForEachAsync(byUrl, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (pair, _) =>
            {
                ImageSource? image = await LoadAvatarAsync(pair.Key).ConfigureAwait(false);
                if (image != null)
                    loaded.Add((image, pair.Value));
            }).ConfigureAwait(false);
            if (loaded.IsEmpty || Application.Current?.Dispatcher is not { } dispatcher)
                return;
            await dispatcher.InvokeAsync(() =>
            {
                foreach (var item in loaded)
                {
                    foreach (T target in item.Targets)
                        assign(target, item.Image);
                }
            });
        }

        private static ImageSource? DecodeBadgeImage(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
                return null;
            int comma = value.IndexOf(',');
            if (comma <= 0)
                return null;
            try
            {
                return SafeImaging.FromBytes(Convert.FromBase64String(value.Substring(comma + 1)), 38);
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveBadgeUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                Uri.TryCreate(App.WebsiteBaseUrl.TrimEnd('/') + "/" + value.TrimStart('/'), UriKind.Absolute, out uri);
            if (uri == null || !Uri.TryCreate(App.WebsiteBaseUrl, UriKind.Absolute, out Uri? site) || uri.Scheme != site.Scheme || !string.Equals(uri.Host, site.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != site.Port)
                return null;
            return uri.AbsoluteUri;
        }

        private static async Task LoadBadgeAsync(WebsiteBadgeEntry badge, string url)
        {
            try
            {
                BitmapSource? image = await _badgeCache.GetOrAdd(url, LoadBadgeCoreAsync).ConfigureAwait(false);
                if (image != null && Application.Current?.Dispatcher is { } dispatcher)
                    await dispatcher.InvokeAsync(() => badge.Image = image);
            }
            catch
            {
                _badgeCache.TryRemove(url, out _);
            }
        }

        private static Task<BitmapSource?> LoadBadgeCoreAsync(string url)
        {
            return Voidstrap.Utility.AppImage.LoadAsync(url, 38, CancellationToken.None);
        }

        private static List<WebsiteBadgeEntry> ParseBadgesFromJson(JsonElement? element)
        {
            var result = new List<WebsiteBadgeEntry>();
            if (element == null || element.Value.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var b in element.Value.EnumerateArray())
            {
                if (result.Count >= 12)
                    break;
                string name = Str(b, "name", 32);
                string image = RawStr(b, "image", 3000000);
                if (string.IsNullOrEmpty(name))
                    continue;
                var src = DecodeBadgeImage(image);
                var entry = new WebsiteBadgeEntry { Name = name, Image = src };
                string? url = src == null ? ResolveBadgeUrl(image) : null;
                if (src != null || url != null)
                    result.Add(entry);
                if (url != null)
                    _ = LoadBadgeAsync(entry, url);
            }
            return result;
        }

        private static void AssignBadges(IReadOnlyList<WebsiteBadgeEntry> badges, ObservableCollection<WebsiteBadgeEntry> target)
        {
            if (badges.Count == 0)
                return;
            Application.Current?.Dispatcher.BeginInvoke((Action)(() =>
            {
                target.Clear();
                foreach (var b in badges)
                    target.Add(b);
            }));
        }

        private bool RefreshSignInState()
        {
            bool signedIn = WebsiteAuth.IsSignedIn();
            SignedOutVisibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            CanInteract = signedIn;
            return signedIn;
        }

        private async Task<(JsonDocument? Doc, string? Error)> GetAsync(string pathAndQuery)
        {
            string? token = WebsiteAuth.GetToken();
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Get, App.WebsiteBaseUrl + pathAndQuery);
                if (!string.IsNullOrEmpty(token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                return await ReadJsonAsync(response, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, "The forums took too long to respond.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("Forums", "Request failed: " + ex.GetType().Name);
                return (null, "Could not reach the forums.");
            }
        }

        private async Task<(JsonDocument? Doc, string? Error)> PostAsync(object body)
        {
            string? token = WebsiteAuth.GetToken();
            if (string.IsNullOrEmpty(token))
                return (null, "Sign in from the Home page to use the forums.");
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var request = new HttpRequestMessage(HttpMethod.Post, App.WebsiteBaseUrl + "/api/forum");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await App.HttpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
                return await ReadJsonAsync(response, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return (null, "The forums took too long to respond.");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("Forums", "Request failed: " + ex.GetType().Name);
                return (null, "Could not reach the forums.");
            }
        }

        private async Task<(JsonDocument? Doc, string? Error)> ReadJsonAsync(HttpResponseMessage response, CancellationToken token)
        {
            long? length = response.Content.Headers.ContentLength;
            if (length.HasValue && length.Value > MaxResponseBytes)
            {
                SetStatusUrl(response);
                return (null, "This forum content is too large to display in the app. Please use the button below to open it on the website.");
            }
            byte[]? payload = await ReadBoundedBytesAsync(response.Content, MaxResponseBytes, token).ConfigureAwait(false);
            if (payload == null)
            {
                SetStatusUrl(response);
                return (null, "This forum content is too large to display in the app. Please use the button below to open it on the website.");
            }
            string text = Encoding.UTF8.GetString(payload);
            JsonDocument? doc = null;
            try
            {
                doc = JsonDocument.Parse(text);
            }
            catch
            {
                return (null, "The forums sent an unreadable response (HTTP " + (int)response.StatusCode + ").");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                doc.Dispose();
                RefreshSignInState();
                return (null, "Your session expired. Sign in again from the Home page.");
            }
            if ((int)response.StatusCode == 429)
            {
                doc.Dispose();
                return (null, "Slow down, you are sending requests too quickly.");
            }
            if (!response.IsSuccessStatusCode)
            {
                string serverError = Str(doc.RootElement, "error", 200);
                doc.Dispose();
                return (null, string.IsNullOrEmpty(serverError) ? "The forums returned HTTP " + (int)response.StatusCode + "." : serverError);
            }
            return (doc, null);
        }

        private void SetStatusUrl(HttpResponseMessage response)
        {
            Uri? reqUri = response.RequestMessage?.RequestUri;
            if (reqUri == null)
            {
                StatusUrl = App.WebsiteBaseUrl + "/pages/forums.html";
                return;
            }
            string q = reqUri.Query;
            var qs = System.Web.HttpUtility.ParseQueryString(q);
            string action = qs["action"] ?? "";
            string baseUrl = App.WebsiteBaseUrl;
            if (action == "thread")
            {
                string id = qs["id"] ?? "";
                StatusUrl = baseUrl + "/pages/forum-thread.html" + (string.IsNullOrEmpty(id) ? "" : "?id=" + Uri.EscapeDataString(id));
            }
            else
            {
                string cat = qs["category"] ?? "";
                string page = qs["page"] ?? "";
                string catParam = string.IsNullOrEmpty(cat) ? "" : "category=" + Uri.EscapeDataString(cat);
                string pageParam = string.IsNullOrEmpty(page) || string.IsNullOrEmpty(catParam) ? "" : "&page=" + Uri.EscapeDataString(page);
                StatusUrl = baseUrl + "/pages/forums.html" + (string.IsNullOrEmpty(catParam) ? "" : "?" + catParam + pageParam);
            }
        }

        [RelayCommand]
        private void OpenStatusUrl()
        {
            if (!string.IsNullOrEmpty(StatusUrl))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(StatusUrl) { UseShellExecute = true }); } catch { }
            }
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (!_bootstrapped)
            {
                _bootstrapped = true;
                await LoadCategoriesAsync();
                return;
            }
            if (ThreadVisibility == Visibility.Visible)
                await LoadThreadAsync(_currentThreadId);
            else if (ThreadsVisibility == Visibility.Visible && _latestMode)
                await LoadLatestAsync(_page);
            else if (ThreadsVisibility == Visibility.Visible)
                await LoadThreadsAsync(_currentCategoryId, _page);
            else
                await LoadCategoriesAsync();
        }

        [RelayCommand]
        private Task OpenCategoryAsync(ForumCategory? category)
        {
            if (category == null || !IsValidServerId(category.Id))
                return Task.CompletedTask;
            CurrentCategoryName = category.Name;
            return LoadThreadsAsync(category.Id, 1);
        }

        [RelayCommand]
        private Task ShowLatestAsync()
        {
            _sort = "latest";
            _tagFilter = "";
            _query = "";
            SearchText = "";
            return LoadLatestAsync(1);
        }

        [RelayCommand]
        private Task ShowTopAsync()
        {
            _sort = "top";
            _tagFilter = "";
            _query = "";
            SearchText = "";
            return LoadLatestAsync(1);
        }

        [RelayCommand]
        private Task SearchAsync()
        {
            _query = Sanitize(SearchText, 80).Trim();
            _tagFilter = "";
            _sort = "latest";
            return LoadLatestAsync(1);
        }

        [RelayCommand]
        private Task ClearFilterAsync()
        {
            _query = "";
            _tagFilter = "";
            SearchText = "";
            return LoadLatestAsync(1);
        }

        [RelayCommand]
        private Task FilterTagAsync(ForumTag? tag)
        {
            if (tag == null || string.IsNullOrEmpty(tag.Name))
                return Task.CompletedTask;
            _tagFilter = tag.Name;
            _query = "";
            _sort = "latest";
            return LoadLatestAsync(1);
        }

        [RelayCommand]
        private async Task ToggleReactionAsync(object? parameter)
        {
            ForumPost? post = null;
            string emoji = "";
            if (parameter is ForumReaction existing)
            {
                post = existing.Post;
                emoji = existing.Emoji;
            }
            else if (parameter is ForumReactionChoice choice)
            {
                post = choice.Post;
                emoji = choice.Emoji;
            }
            if (post == null || string.IsNullOrEmpty(emoji) || !CanInteract)
                return;
            if (!IsValidServerId(_currentThreadId))
                return;
            if (!string.IsNullOrEmpty(post.ReplyId) && !IsValidServerId(post.ReplyId))
                return;
            object body = string.IsNullOrEmpty(post.ReplyId)
                ? new { action = "react", threadId = _currentThreadId, emoji = emoji }
                : (object)new { action = "react", threadId = _currentThreadId, replyId = post.ReplyId, emoji = emoji };
            var (doc, error) = await PostAsync(body);
            if (doc == null)
            {
                StatusText = error ?? "Could not react.";
                return;
            }
            using (doc)
            {
                post.MyReaction = Str(doc.RootElement, "myReaction", 20);
                post.Up = (int)Math.Clamp(Num(doc.RootElement, "up"), 0, 9999999);
                post.My = post.MyReaction == "heart" ? "up" : post.My == "up" ? "none" : post.My;
                post.ApplyVoteState();
                ApplyReactions(post, doc.RootElement);
            }
        }

        [RelayCommand]
        private async Task ToggleBookmarkAsync(ForumPost? post)
        {
            if (post == null || !CanInteract || !IsValidServerId(_currentThreadId))
                return;
            if (!string.IsNullOrEmpty(post.ReplyId) && !IsValidServerId(post.ReplyId))
                return;
            object body = string.IsNullOrEmpty(post.ReplyId)
                ? new { action = "bookmark", threadId = _currentThreadId }
                : (object)new { action = "bookmark", threadId = _currentThreadId, replyId = post.ReplyId };
            var (doc, error) = await PostAsync(body);
            if (doc == null)
            {
                StatusText = error ?? "Could not bookmark.";
                return;
            }
            using (doc)
            {
                post.Bookmarked = Flag(doc.RootElement, "bookmarked");
                post.BookmarkLabel = post.Bookmarked ? "Bookmarked" : "Bookmark";
            }
        }

        private static void ApplyReactions(ForumPost post, JsonElement root)
        {
            post.Reactions.Clear();
            if (!root.TryGetProperty("reactions", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement r in arr.EnumerateArray())
            {
                string emoji = Str(r, "emoji", 20);
                string glyph = GlyphFor(emoji);
                if (string.IsNullOrEmpty(glyph))
                    continue;
                var names = new List<string>();
                if (r.TryGetProperty("users", out JsonElement users) && users.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement u in users.EnumerateArray())
                    {
                        string n = Str(u, "name", 40);
                        if (!string.IsNullOrEmpty(n))
                            names.Add(n);
                        if (names.Count >= 12)
                            break;
                    }
                }
                post.Reactions.Add(new ForumReaction
                {
                    Emoji = emoji,
                    Glyph = glyph,
                    Post = post,
                    Count = (int)Math.Clamp(Num(r, "count"), 0, 9999999),
                    Mine = Flag(r, "mine"),
                    Tooltip = names.Count > 0 ? string.Join(", ", names) : "",
                });
            }
        }

        [RelayCommand]
        private void BackToCategories()
        {
            ThreadsVisibility = Visibility.Collapsed;
            ThreadVisibility = Visibility.Collapsed;
            CategoriesVisibility = Visibility.Visible;
            StatusText = "";
            _ = LoadCategoriesAsync();
        }

        [RelayCommand]
        private Task OpenThreadAsync(ForumThreadSummary? thread)
        {
            if (thread == null || !IsValidServerId(thread.Id))
                return Task.CompletedTask;
            return LoadThreadAsync(thread.Id);
        }

        [RelayCommand]
        private void BackToThreads()
        {
            ThreadVisibility = Visibility.Collapsed;
            CategoriesVisibility = Visibility.Collapsed;
            ThreadsVisibility = Visibility.Visible;
            StatusText = "";
            if (_latestMode)
                _ = LoadLatestAsync(_page);
            else
                _ = LoadThreadsAsync(_currentCategoryId, _page);
        }

        [RelayCommand]
        private Task PrevPageAsync()
        {
            if (_page <= 1)
                return Task.CompletedTask;
            return _latestMode ? LoadLatestAsync(_page - 1) : LoadThreadsAsync(_currentCategoryId, _page - 1);
        }

        [RelayCommand]
        private Task NextPageAsync()
        {
            if (_page >= _pages)
                return Task.CompletedTask;
            return _latestMode ? LoadLatestAsync(_page + 1) : LoadThreadsAsync(_currentCategoryId, _page + 1);
        }

        [RelayCommand]
        private Task VoteUpAsync(ForumPost? post)
        {
            return VoteAsync(post, "up");
        }

        [RelayCommand]
        private Task VoteDownAsync(ForumPost? post)
        {
            return VoteAsync(post, "down");
        }

        private async Task VoteAsync(ForumPost? post, string dir)
        {
            if (post == null || !IsValidServerId(_currentThreadId))
                return;
            if (!string.IsNullOrEmpty(post.ReplyId) && !IsValidServerId(post.ReplyId))
                return;
            string newDir = post.My == dir ? "none" : dir;
            object body = string.IsNullOrEmpty(post.ReplyId)
                ? new { action = "vote", threadId = _currentThreadId, dir = newDir }
                : (object)new { action = "vote", threadId = _currentThreadId, replyId = post.ReplyId, dir = newDir };
            var (doc, error) = await PostAsync(body);
            if (doc == null)
            {
                StatusText = error ?? "Could not vote.";
                return;
            }
            using (doc)
            {
                post.Up = (int)Math.Clamp(Num(doc.RootElement, "up"), 0, 9999999);
                post.Down = (int)Math.Clamp(Num(doc.RootElement, "down"), 0, 9999999);
                string my = Str(doc.RootElement, "my", 8);
                post.My = my == "up" || my == "down" ? my : "none";
                post.ApplyVoteState();
            }
        }

        [RelayCommand]
        private async Task CreateThreadAsync()
        {
            string title = Sanitize(NewThreadTitle, MaxTitle).Trim();
            string content = Sanitize(NewThreadContent, MaxContent).Trim();
            var newTags = new List<string>();
            foreach (string raw in Sanitize(NewThreadTags, 120).Split(','))
            {
                string tag = raw.Trim().ToLowerInvariant().Replace(' ', '-');
                if (!string.IsNullOrEmpty(tag))
                    newTags.Add(tag);
                if (newTags.Count >= 5)
                    break;
            }
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(content))
            {
                StatusText = "A title and content are required.";
                return;
            }
            if (!IsValidServerId(_currentCategoryId))
                return;
            string createdId = "";
            await _gate.WaitAsync();
            IsBusy = true;
            try
            {
                var (doc, error) = await PostAsync(new { action = "create-thread", category = _currentCategoryId, title, content, tags = newTags });
                if (doc == null)
                {
                    StatusText = error ?? "Could not create the thread.";
                    return;
                }
                using (doc)
                {
                    createdId = Str(doc.RootElement, "id", 40);
                    NewThreadTitle = "";
                    NewThreadContent = "";
                    StatusText = "Thread created.";
                }
            }
            finally
            {
                _gate.Release();
                IsBusy = false;
            }
            if (IsValidServerId(createdId))
                await LoadThreadAsync(createdId);
        }

        [RelayCommand]
        private async Task SubmitReplyAsync()
        {
            string content = Sanitize(ReplyText, MaxReply).Trim();
            if (string.IsNullOrEmpty(content))
            {
                StatusText = "Write a reply first.";
                return;
            }
            if (!IsValidServerId(_currentThreadId))
                return;
            await _gate.WaitAsync();
            IsBusy = true;
            try
            {
                object body = IsValidServerId(_replyTargetId)
                    ? new { action = "reply", threadId = _currentThreadId, content, replyTo = _replyTargetId }
                    : (object)new { action = "reply", threadId = _currentThreadId, content };
                var (doc, error) = await PostAsync(body);
                if (doc == null)
                {
                    StatusText = error ?? "Could not post the reply.";
                    return;
                }
                doc.Dispose();
                ReplyText = "";
                StatusText = "";
                _replyTargetId = "";
                ReplyTargetText = "";
                ReplyTargetVisibility = Visibility.Collapsed;
            }
            finally
            {
                _gate.Release();
                IsBusy = false;
            }
            await LoadThreadAsync(_currentThreadId);
        }

        [RelayCommand]
        private void SetReplyTarget(ForumPost? post)
        {
            if (post == null || !IsValidServerId(post.ReplyId))
                return;
            _replyTargetId = post.ReplyId;
            string preview = post.Content.Replace('\n', ' ');
            if (preview.Length > 80)
                preview = preview.Substring(0, 80) + "...";
            ReplyTargetText = "Replying to @" + post.AuthorName + ": " + preview;
            ReplyTargetVisibility = Visibility.Visible;
        }

        [RelayCommand]
        private void CancelReplyTarget()
        {
            _replyTargetId = "";
            ReplyTargetText = "";
            ReplyTargetVisibility = Visibility.Collapsed;
        }

        [RelayCommand]
        private void BeginEdit(ForumPost? post)
        {
            if (post == null)
                return;
            post.EditText = post.Content;
            post.IsEditing = true;
        }

        [RelayCommand]
        private void CancelEdit(ForumPost? post)
        {
            if (post == null)
                return;
            post.IsEditing = false;
        }

        [RelayCommand]
        private async Task SaveEditAsync(ForumPost? post)
        {
            if (post == null || !IsValidServerId(_currentThreadId))
                return;
            bool isReply = !string.IsNullOrEmpty(post.ReplyId);
            if (isReply && !IsValidServerId(post.ReplyId))
                return;
            string content = Sanitize(post.EditText, isReply ? MaxReply : MaxContent).Trim();
            if (string.IsNullOrEmpty(content))
            {
                StatusText = "Content is required.";
                return;
            }
            object body = isReply
                ? new { action = "edit", threadId = _currentThreadId, replyId = post.ReplyId, content }
                : (object)new { action = "edit", threadId = _currentThreadId, content };
            var (doc, error) = await PostAsync(body);
            if (doc == null)
            {
                StatusText = error ?? "Could not save the edit.";
                return;
            }
            doc.Dispose();
            post.IsEditing = false;
            StatusText = "";
            await LoadThreadAsync(_currentThreadId);
        }

        [RelayCommand]
        private async Task DeletePostAsync(ForumPost? post)
        {
            if (post == null || !IsValidServerId(_currentThreadId))
                return;
            bool isReply = !string.IsNullOrEmpty(post.ReplyId);
            if (isReply && !IsValidServerId(post.ReplyId))
                return;
            string what = isReply ? "reply" : "entire thread";
            if (Frontend.ShowMessageBox("Delete this " + what + "? This cannot be undone.", MessageBoxImage.Warning, MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                return;
            object body = isReply
                ? new { action = "delete", threadId = _currentThreadId, replyId = post.ReplyId }
                : (object)new { action = "delete", threadId = _currentThreadId };
            var (doc, error) = await PostAsync(body);
            if (doc == null)
            {
                StatusText = error ?? "Could not delete.";
                return;
            }
            doc.Dispose();
            StatusText = "";
            if (isReply)
                await LoadThreadAsync(_currentThreadId);
            else
                BackToThreads();
        }

        [RelayCommand]
        private async Task TogglePinAsync()
        {
            if (!IsValidServerId(_currentThreadId))
                return;
            var (doc, error) = await PostAsync(new { action = "pin", threadId = _currentThreadId });
            if (doc == null)
            {
                StatusText = error ?? "Could not change the pin state.";
                return;
            }
            doc.Dispose();
            await LoadThreadAsync(_currentThreadId);
        }

        public async Task LoadCategoriesAsync()
        {
            RefreshSignInState();
            string account = WebsiteAuth.GetActiveId() ?? "";
            await _gate.WaitAsync();
            IsBusy = true;
            StatusText = "";
            try
            {
                var (doc, error) = await GetAsync("/api/forum?action=categories");
                if (doc == null)
                {
                    StatusText = error ?? "Could not load the forums.";
                    return;
                }
                using (doc)
                {
                    if (account != (WebsiteAuth.GetActiveId() ?? ""))
                        return;
                    var list = new ObservableCollection<ForumCategory>();
                    if (doc.RootElement.TryGetProperty("categories", out JsonElement cats) && cats.ValueKind == JsonValueKind.Array)
                    {
                        int count = 0;
                        foreach (JsonElement c in cats.EnumerateArray())
                        {
                            if (++count > 50)
                                break;
                            string id = Str(c, "id", 40);
                            if (!IsValidServerId(id))
                                continue;
                            string latestText = "";
                            if (c.TryGetProperty("latest", out JsonElement latest) && latest.ValueKind == JsonValueKind.Object)
                            {
                                string lt = Str(latest, "title", 80);
                                string la = Str(latest, "authorName", 40);
                                string when = TimeText(Num(latest, "at"));
                                if (!string.IsNullOrEmpty(lt))
                                    latestText = "Latest: " + lt + " by " + la + (string.IsNullOrEmpty(when) ? "" : ", " + when);
                            }
                            list.Add(new ForumCategory
                            {
                                Id = id,
                                Name = Str(c, "name", 40),
                                Description = Str(c, "description", 200),
                                Announce = Flag(c, "announce"),
                                PostCount = (int)Math.Clamp(Num(c, "postCount"), 0, 999999),
                                LatestText = latestText,
                            });
                        }
                    }
                    Categories = list;
                    CategoriesVisibility = Visibility.Visible;
                    ThreadsVisibility = Visibility.Collapsed;
                    ThreadVisibility = Visibility.Collapsed;
                }
            }
            finally
            {
                _gate.Release();
                IsBusy = false;
            }
        }

        private static ForumThreadSummary BuildSummary(JsonElement t, string fallbackCategory, List<(string Avatar, ForumThreadSummary Target)> avatarWork, List<(string Avatar, ForumPosterAvatar Target)> posterWork)
        {
            string id = Str(t, "id", 40);
            long score = Num(t, "up") - Num(t, "down");
            if (t.TryGetProperty("score", out JsonElement sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt64(out long s2))
                score = s2;
            string authorName = Str(t, "authorName", 40);
            long replies = Num(t, "replyCount");
            long views = Num(t, "views");
            string category = Str(t, "catName", 40);
            if (string.IsNullOrEmpty(category))
                category = fallbackCategory;
            var tagList = new List<string>();
            if (t.TryGetProperty("tags", out JsonElement tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement tag in tagsEl.EnumerateArray())
                {
                    string name = tag.ValueKind == JsonValueKind.String ? Sanitize(tag.GetString() ?? "", 20) : "";
                    if (!string.IsNullOrEmpty(name))
                        tagList.Add(name);
                    if (tagList.Count >= 5)
                        break;
                }
            }
            var item = new ForumThreadSummary
            {
                Id = id,
                Title = Str(t, "title", MaxTitle),
                AuthorName = authorName,
                AvatarInitial = InitialOf(authorName),
                MetaText = "by " + authorName + ", " + TimeText(Num(t, "lastActivity")),
                RepliesText = replies.ToString(),
                ViewsText = views.ToString(),
                ScoreText = score.ToString("+0;-0;0"),
                CategoryName = category,
                CategoryVisibility = string.IsNullOrEmpty(category) ? Visibility.Collapsed : Visibility.Visible,
                TagsText = tagList.Count > 0 ? string.Join("  ", tagList) : "",
                TagsVisibility = tagList.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
                PinnedVisibility = Flag(t, "pinned") ? Visibility.Visible : Visibility.Collapsed,
            };
            string avatar = RawStr(t, "authorAvatar", 4000);
            if (!string.IsNullOrEmpty(avatar))
                avatarWork.Add((avatar, item));
            if (t.TryGetProperty("authorBadges", out JsonElement badgesEl))
            {
                var parsed = ParseBadgesFromJson(badgesEl);
                if (parsed.Count > 0)
                    AssignBadges(parsed, item.AuthorBadges);
            }
            if (t.TryGetProperty("posters", out JsonElement posters) && posters.ValueKind == JsonValueKind.Array)
            {
                int count = 0;
                foreach (JsonElement pEl in posters.EnumerateArray())
                {
                    if (++count > 5)
                        break;
                    string name = Str(pEl, "name", 40);
                    var slot = new ForumPosterAvatar { Name = name, Initial = InitialOf(name) };
                    item.Posters.Add(slot);
                    string pav = RawStr(pEl, "avatar", 4000);
                    if (!string.IsNullOrEmpty(pav))
                        posterWork.Add((pav, slot));
                }
            }
            return item;
        }

        private async Task LoadTagsAsync()
        {
            var (doc, _) = await GetAsync("/api/forum?action=tags");
            if (doc == null)
                return;
            using (doc)
            {
                var list = new ObservableCollection<ForumTag>();
                if (doc.RootElement.TryGetProperty("tags", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tg in arr.EnumerateArray())
                    {
                        string name = Sanitize(Str(tg, "name", 20), 20);
                        if (string.IsNullOrEmpty(name))
                            continue;
                        int count = (int)Math.Clamp(Num(tg, "count"), 0, 999999);
                        list.Add(new ForumTag { Name = name, Count = count, DisplayText = name + "  " + count });
                        if (list.Count >= 20)
                            break;
                    }
                }
                Tags = list;
                TagsVisibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        public async Task LoadLatestAsync(int page)
        {
            RefreshSignInState();
            string account = WebsiteAuth.GetActiveId() ?? "";
            List<(string Avatar, ForumThreadSummary Target)> avatarWork = new();
            List<(string Avatar, ForumPosterAvatar Target)> posterWork = new();
            await _gate.WaitAsync();
            IsBusy = true;
            StatusText = "";
            try
            {
                string query = "/api/forum?action=latest&page=" + Math.Clamp(page, 1, 10000);
                if (_sort == "top")
                    query += "&sort=top";
                if (!string.IsNullOrEmpty(_tagFilter))
                    query += "&tag=" + Uri.EscapeDataString(_tagFilter);
                if (!string.IsNullOrEmpty(_query))
                    query += "&q=" + Uri.EscapeDataString(_query);
                var (doc, error) = await GetAsync(query);
                if (doc == null)
                {
                    StatusText = error ?? "Could not load the forums.";
                    return;
                }
                using (doc)
                {
                    if (account != (WebsiteAuth.GetActiveId() ?? ""))
                        return;
                    _latestMode = true;
                    _currentCategoryId = "";
                    _page = (int)Math.Clamp(Num(doc.RootElement, "page"), 1, 10000);
                    _pages = (int)Math.Clamp(Num(doc.RootElement, "pages"), 1, 10000);
                    PageText = "Page " + _page + " of " + _pages;
                    HasPrevPage = _page > 1;
                    HasNextPage = _page < _pages;
                    CurrentCategoryName = _sort == "top" ? "Top topics" : "Latest topics";
                    CanPostInCategory = false;
                    if (!string.IsNullOrEmpty(_query))
                        ActiveFilterText = "Showing topics matching " + _query;
                    else if (!string.IsNullOrEmpty(_tagFilter))
                        ActiveFilterText = "Showing topics tagged " + _tagFilter;
                    else
                        ActiveFilterText = "";
                    ActiveFilterVisibility = string.IsNullOrEmpty(ActiveFilterText) ? Visibility.Collapsed : Visibility.Visible;
                    var list = new ObservableCollection<ForumThreadSummary>();
                    if (doc.RootElement.TryGetProperty("topics", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement t in arr.EnumerateArray())
                        {
                            if (!IsValidServerId(Str(t, "id", 40)))
                                continue;
                            list.Add(BuildSummary(t, "", avatarWork, posterWork));
                        }
                    }
                    Threads = list;
                    if (list.Count == 0 && string.IsNullOrEmpty(StatusText))
                        StatusText = "No topics match that filter.";
                    CategoriesVisibility = Visibility.Collapsed;
                    ThreadVisibility = Visibility.Collapsed;
                    ThreadsVisibility = Visibility.Visible;
                    LatestHeaderVisibility = Visibility.Visible;
                    CategoryHeaderVisibility = Visibility.Collapsed;
                }
            }
            finally
            {
                _gate.Release();
                IsBusy = false;
            }
            _ = AssignAvatarsAsync(avatarWork, (t, img) => t.AvatarImage = img);
            _ = AssignAvatarsAsync(posterWork, (t, img) => t.Image = img);
            _ = LoadTagsAsync();
        }

        private async Task LoadThreadsAsync(string categoryId, int page)
        {
            RefreshSignInState();
            if (!IsValidServerId(categoryId))
                return;
            string account = WebsiteAuth.GetActiveId() ?? "";
            List<(string Avatar, ForumThreadSummary Target)> avatarWork = new();
            List<(string Avatar, ForumPosterAvatar Target)> posterWork = new();
            _latestMode = false;
            await _gate.WaitAsync();
            IsBusy = true;
            StatusText = "";
            try
            {
                var (doc, error) = await GetAsync("/api/forum?action=threads&category=" + Uri.EscapeDataString(categoryId) + "&page=" + Math.Clamp(page, 1, 10000));
                if (doc == null)
                {
                    StatusText = error ?? "Could not load threads.";
                    return;
                }
                using (doc)
                {
                    if (account != (WebsiteAuth.GetActiveId() ?? ""))
                        return;
                    _currentCategoryId = categoryId;
                    _page = (int)Math.Clamp(Num(doc.RootElement, "page"), 1, 10000);
                    _pages = (int)Math.Clamp(Num(doc.RootElement, "pages"), 1, 10000);
                    PageText = "Page " + _page + " of " + _pages;
                    HasPrevPage = _page > 1;
                    HasNextPage = _page < _pages;
                    bool announce = false;
                    if (doc.RootElement.TryGetProperty("category", out JsonElement cat) && cat.ValueKind == JsonValueKind.Object)
                    {
                        CurrentCategoryName = Str(cat, "name", 40);
                        announce = Flag(cat, "announce");
                    }
                    CanPostInCategory = CanInteract && (!announce || Flag(doc.RootElement, "dev"));
                    var list = new ObservableCollection<ForumThreadSummary>();
                    if (doc.RootElement.TryGetProperty("threads", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement t in arr.EnumerateArray())
                        {
                            if (!IsValidServerId(Str(t, "id", 40)))
                                continue;
                            list.Add(BuildSummary(t, CurrentCategoryName, avatarWork, posterWork));
                        }
                    }
                    Threads = list;
                    ActiveFilterText = "";
                    ActiveFilterVisibility = Visibility.Collapsed;
                    LatestHeaderVisibility = Visibility.Collapsed;
                    CategoryHeaderVisibility = Visibility.Visible;
                    CategoriesVisibility = Visibility.Collapsed;
                    ThreadVisibility = Visibility.Collapsed;
                    ThreadsVisibility = Visibility.Visible;
                }
            }
            finally
            {
                _gate.Release();
                IsBusy = false;
            }
            _ = AssignAvatarsAsync(avatarWork, (t, img) => t.AvatarImage = img);
            _ = AssignAvatarsAsync(posterWork, (t, img) => t.Image = img);
        }

        private async Task LoadThreadAsync(string threadId)
        {
            RefreshSignInState();
            if (!IsValidServerId(threadId))
                return;
            string account = WebsiteAuth.GetActiveId() ?? "";
            List<(string Avatar, ForumPost Target)> avatarWork = new();
            await _gate.WaitAsync();
            IsBusy = true;
            StatusText = "";
            try
            {
                var (doc, error) = await GetAsync("/api/forum?action=thread&id=" + Uri.EscapeDataString(threadId));
                if (doc == null)
                {
                    StatusText = error ?? "Could not load the thread.";
                    return;
                }
                using (doc)
                {
                    if (account != (WebsiteAuth.GetActiveId() ?? ""))
                        return;
                    if (!doc.RootElement.TryGetProperty("thread", out JsonElement t) || t.ValueKind != JsonValueKind.Object)
                    {
                        StatusText = "Thread not found.";
                        return;
                    }
                    _currentThreadId = threadId;
                    CurrentThreadTitle = Str(t, "title", MaxTitle);
                    long threadViews = Num(doc.RootElement, "views");
                    ThreadViewsText = threadViews == 1 ? "1 view" : threadViews + " views";
                    if (doc.RootElement.TryGetProperty("category", out JsonElement ownCat) && ownCat.ValueKind == JsonValueKind.Object)
                    {
                        string ownName = Str(ownCat, "name", 40);
                        if (!string.IsNullOrEmpty(ownName))
                            CurrentCategoryName = ownName;
                    }
                    bool canModerate = Flag(t, "canModerate");
                    IsModerator = canModerate;
                    PinVisibility = canModerate ? Visibility.Visible : Visibility.Collapsed;
                    PinLabel = Flag(t, "pinned") ? "Unpin thread" : "Pin thread";
                    _replyTargetId = "";
                    ReplyTargetText = "";
                    ReplyTargetVisibility = Visibility.Collapsed;
                    var list = new ObservableCollection<ForumPost>();
                    int[] byteBudget = { MaxRetainedImageBytes };
                    long[] decodedBudget = { MaxDecodedImageBytes };
                    AddPost(list, t, "", avatarWork, canModerate, CanInteract, byteBudget, decodedBudget);
                    if (t.TryGetProperty("replies", out JsonElement replies) && replies.ValueKind == JsonValueKind.Array)
                    {
                        int count = 0;
                        foreach (JsonElement r in replies.EnumerateArray())
                        {
                            if (++count > MaxRenderedReplies)
                                break;
                            AddPost(list, r, Str(r, "id", 40), avatarWork, canModerate, CanInteract, byteBudget, decodedBudget);
                        }
                    }
                    var byId = new Dictionary<string, ForumPost>(StringComparer.Ordinal);
                    foreach (ForumPost p in list)
                    {
                        if (!string.IsNullOrEmpty(p.ReplyId))
                            byId[p.ReplyId] = p;
                    }
                    foreach (ForumPost p in list)
                    {
                        if (string.IsNullOrEmpty(p.ReplyToId) || !byId.TryGetValue(p.ReplyToId, out ForumPost? parent))
                            continue;
                        string preview = parent.Content.Replace('\n', ' ');
                        if (preview.Length > 70)
                            preview = preview.Substring(0, 70) + "...";
                        if (preview.Length > 0)
                            p.ReplyToText = "Replying to " + parent.AuthorName + ": " + preview;
                    }
                    Posts = list;
                    CategoriesVisibility = Visibility.Collapsed;
                    ThreadsVisibility = Visibility.Collapsed;
                    ThreadVisibility = Visibility.Visible;
                }
            }
            finally
            {
                _gate.Release();
                IsBusy = false;
            }
            _ = AssignAvatarsAsync(avatarWork, (p, img) => p.AvatarImage = img);
        }

        private static void AddPost(ObservableCollection<ForumPost> list, JsonElement e, string replyId, List<(string, ForumPost)> avatarWork, bool canModerate, bool canInteract, int[] byteBudget, long[] decodedBudget)
        {
            string author = "User";
            string avatar = "";
            List<WebsiteBadgeEntry> badges = null;
            if (e.TryGetProperty("author", out JsonElement a) && a.ValueKind == JsonValueKind.Object)
            {
                string name = Str(a, "name", 40);
                if (!string.IsNullOrEmpty(name))
                    author = name;
                avatar = RawStr(a, "avatar", 4000);
                if (a.TryGetProperty("badges", out JsonElement badgesEl))
                    badges = ParseBadgesFromJson(badgesEl);
            }
            string myRaw = Str(e, "my", 8);
            string replyToName = Str(e, "replyToName", 40);
            string replyToId = Str(e, "replyTo", 40);
            bool mine = Flag(e, "mine");
            var post = new ForumPost
            {
                ReplyId = replyId,
                ReplyToId = IsValidServerId(replyToId) ? replyToId : "",
                AuthorName = author,
                AvatarInitial = InitialOf(author),
                TimeText = TimeText(Num(e, "created")),
                Content = Str(e, "content", MaxContent),
                ReplyToText = string.IsNullOrEmpty(replyToName) ? "" : "Replying to " + replyToName,
                ReplyToVisibility = string.IsNullOrEmpty(replyToName) ? Visibility.Collapsed : Visibility.Visible,
                EditedVisibility = Num(e, "edited") > 0 ? Visibility.Visible : Visibility.Collapsed,
                Up = (int)Math.Clamp(Num(e, "up"), 0, 9999999),
                Down = (int)Math.Clamp(Num(e, "down"), 0, 9999999),
                My = myRaw == "up" || myRaw == "down" ? myRaw : "none",
                Mine = mine,
                ManageVisibility = mine || canModerate ? Visibility.Visible : Visibility.Collapsed,
                ReplyButtonVisibility = canInteract && !string.IsNullOrEmpty(replyId) ? Visibility.Visible : Visibility.Collapsed,
            };
            post.ApplyVoteState();
            post.MyReaction = Str(e, "myReaction", 20);
            post.Bookmarked = Flag(e, "bookmarked");
            post.BookmarkLabel = post.Bookmarked ? "Bookmarked" : "Bookmark";
            post.ReactionVisibility = canInteract ? Visibility.Visible : Visibility.Collapsed;
            ApplyReactions(post, e);
            if (canInteract)
            {
                foreach (var entry in ReactionCatalogue)
                    post.ReactionChoices.Add(new ForumReactionChoice { Emoji = entry.Emoji, Glyph = entry.Glyph, Post = post });
            }
            if (e.TryGetProperty("images", out JsonElement imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                int count = 0;
                foreach (JsonElement img in imgs.EnumerateArray())
                {
                    if (++count > MaxImagesPerPost)
                        break;
                    string dataUrl = img.ValueKind == JsonValueKind.String ? img.GetString() ?? "" : "";
                    var (decoded, meta, bytes, decodedBytes) = BuildImageSlot(dataUrl, byteBudget[0], decodedBudget[0]);
                    post.ImageSlots.Add(decoded);
                    post.ImageMetas.Add(meta);
                    if (bytes != null)
                    {
                        byteBudget[0] -= bytes.Length;
                        post.ImageBytes.Add(bytes);
                    }
                    else
                    {
                        post.ImageBytes.Add(null);
                    }
                    if (decodedBytes > 0)
                        decodedBudget[0] -= decodedBytes;
                }
            }
            if (e.TryGetProperty("mentions", out JsonElement mentions) && mentions.ValueKind == JsonValueKind.Array)
            {
                int count = 0;
                foreach (JsonElement mn in mentions.EnumerateArray())
                {
                    if (++count > 10 || mn.ValueKind != JsonValueKind.Object)
                        continue;
                    string name = Str(mn, "name", 40);
                    if (string.IsNullOrEmpty(name))
                        name = Str(mn, "username", 40);
                    if (string.IsNullOrEmpty(name))
                        name = Str(mn, "displayName", 40);
                    if (!string.IsNullOrEmpty(name))
                        post.MentionNames.Add(name);
                }
            }
            if (!string.IsNullOrEmpty(avatar))
                avatarWork.Add((avatar, post));
            if (badges != null && badges.Count > 0)
                AssignBadges(badges, post.AuthorBadges);
            list.Add(post);
        }
    }
}
