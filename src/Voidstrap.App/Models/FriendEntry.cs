using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Integrations;

namespace Voidstrap.Models;

public sealed class FriendEntry
{
    private static readonly Brush OnlineBrush = Frozen(0x00, 0xB0, 0x6F);
    private static readonly Brush GameBrush = Frozen(0x33, 0x95, 0xFF);
    private static readonly Brush StudioBrush = Frozen(0xF6, 0x88, 0x02);
    private static readonly Brush OfflineBrush = Frozen(0x80, 0x80, 0x80);
    private static readonly Dictionary<string, ImageSource> ArtCache = new();

    public long UserId { get; init; }

    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string Nickname { get; init; } = string.Empty;

    public string AvatarUrl { get; init; } = string.Empty;

    public string? GameImage { get; init; }

    public ImageSource? GameArt
    {
        get
        {
            if (string.IsNullOrEmpty(GameImage))
                return null;
            if (ArtCache.TryGetValue(GameImage, out ImageSource? cached))
                return cached;
            BitmapImage image = new();
            image.BeginInit();
            image.DecodePixelWidth = 480;
            image.UriSource = new Uri(GameImage);
            image.EndInit();
            ArtCache[GameImage] = image;
            return image;
        }
    }

    public FriendPresenceInfo Presence { get; init; } = new();

    public DateTimeOffset? LastOnline { get; init; }

    public DateTimeOffset? RequestedUtc { get; init; }

    public string Title => Nickname.Length > 0 ? Nickname : DisplayName.Length > 0 ? DisplayName : Name;

    public string Handle => "@" + Name;

    public bool IsOnline => Presence.Kind != FriendPresenceKind.Offline;

    public bool CanJoin => Presence.Kind == FriendPresenceKind.InGame && Presence.PlaceId > 0;

    public Visibility JoinVisibility => CanJoin ? Visibility.Visible : Visibility.Collapsed;

    public Brush PresenceBrush =>
        Presence.Kind switch
        {
            FriendPresenceKind.Online => OnlineBrush,
            FriendPresenceKind.InGame => GameBrush,
            FriendPresenceKind.InStudio => StudioBrush,
            _ => OfflineBrush,
        };

    public string Status =>
        Presence.Kind switch
        {
            FriendPresenceKind.InGame => Presence.LastLocation.Length > 0 ? string.Format(Strings.Ui_Friends_Playing, Presence.LastLocation) : Strings.Ui_Friends_Online,
            FriendPresenceKind.InStudio => Strings.Ui_Friends_InStudio,
            FriendPresenceKind.Online => Strings.Ui_Friends_Online,
            _ => LastOnline is DateTimeOffset when ? string.Format(Strings.Ui_Friends_LastSeen, Ago(when)) : Strings.Ui_Friends_Offline,
        };

    public string RequestStatus => RequestedUtc is DateTimeOffset when ? Handle + "  ·  " + string.Format(Strings.Ui_Friends_Sent, Ago(when)) : Handle;

    private static string Ago(DateTimeOffset when)
    {
        TimeSpan span = DateTimeOffset.UtcNow - when;
        if (span.TotalMinutes < 1)
            return "1m";
        if (span.TotalHours < 1)
            return ((int)span.TotalMinutes).ToString(CultureInfo.CurrentCulture) + "m";
        if (span.TotalDays < 1)
            return ((int)span.TotalHours).ToString(CultureInfo.CurrentCulture) + "h";
        if (span.TotalDays < 365)
            return ((int)span.TotalDays).ToString(CultureInfo.CurrentCulture) + "d";
        return ((int)(span.TotalDays / 365)).ToString(CultureInfo.CurrentCulture) + "y";
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        SolidColorBrush brush = new(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
