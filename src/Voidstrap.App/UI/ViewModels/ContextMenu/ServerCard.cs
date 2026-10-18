using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using Voidstrap.Integrations;
using Voidstrap.Resources;

namespace Voidstrap.UI.ViewModels.ContextMenu;

public sealed class ServerRow
{
    public IReadOnlyList<ServerCard> Cards { get; }
    public int Columns { get; }

    public ServerRow(IReadOnlyList<ServerCard> cards, int columns)
    {
        Cards = cards;
        Columns = columns;
    }
}

public sealed class ServerCardLine
{
    public string Glyph { get; }
    public string Text { get; }

    public ServerCardLine(string glyph, string text)
    {
        Glyph = glyph;
        Text = text;
    }
}

public sealed class AvatarSlot : INotifyPropertyChanged
{
    private ImageSource? _image;
    private string? _url;

    public string Text { get; init; } = string.Empty;

    public bool IsMore => Text.Length > 0;

    public ImageSource? Image
    {
        get => _image;
        set
        {
            if (ReferenceEquals(_image, value))
                return;
            _image = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
        }
    }

    public string? Url
    {
        get => _url;
        set
        {
            if (string.Equals(_url, value, StringComparison.Ordinal))
                return;
            _url = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Url)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ServerCard
{
    private const int MaxFaces = 5;

    public string JobId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public IReadOnlyList<ServerCardLine> Lines { get; init; } = Array.Empty<ServerCardLine>();
    public IReadOnlyList<AvatarSlot> Avatars { get; init; } = Array.Empty<AvatarSlot>();
    public string[] Tokens { get; init; } = [];
    public double Fill { get; init; }
    public bool CanJoin { get; init; } = true;
    public string JoinText { get; init; } = Strings.ContextMenu_ServerBrowser_Join;
    public string Region { get; init; } = string.Empty;
    public int Playing { get; init; }
    public int Ping { get; init; } = int.MaxValue;
    public bool IsFull { get; init; }
    public bool IsBlocked { get; init; }
    public string AccessCode { get; init; } = string.Empty;
    public MatchmakerCandidate? Server { get; init; }
    public bool AvatarsRequested { get; set; }
    public int AvatarAttempts { get; set; }

    public static ServerCard ForPublic(MatchmakerCandidate server, bool current, bool blocked)
    {
        List<ServerCardLine> lines = new();
        if (server.Fps > 0)
            lines.Add(new ServerCardLine("", string.Format(Strings.ContextMenu_ServerBrowser_ServerPerformance, Math.Round(Math.Clamp(server.Fps / 60, 0, 1) * 100))));
        if (VoidstrapMatchmaker.KnownServerStart(server.JobId) is DateTimeOffset started)
            lines.Add(new ServerCardLine("", "~" + Compact(DateTimeOffset.UtcNow - started)));
        string region = DescribeRegion(server.Datacenter);
        lines.Add(new ServerCardLine("", (region.Length > 0 ? region : Strings.ContextMenu_ServerBrowser_UnknownLocation) + "  ·  ~" + server.EstimatedPingMs.ToString(CultureInfo.CurrentCulture) + " ms"));
        if (blocked)
            lines.Add(new ServerCardLine("", Strings.ContextMenu_ServerBrowser_RegionBlocked));
        return new ServerCard
        {
            JobId = server.JobId,
            Server = server,
            Title = server.MaxPlayers > 0 ? string.Format(Strings.ContextMenu_ServerBrowser_Capacity, server.Playing, server.MaxPlayers) : string.Format(Strings.ContextMenu_ServerBrowser_CapacityUnknown, server.Playing),
            Lines = lines,
            Avatars = Faces(server.Playing, server.PlayerTokens.Length),
            Tokens = server.PlayerTokens,
            Fill = server.MaxPlayers > 0 ? Math.Clamp((double)server.Playing / server.MaxPlayers, 0, 1) : 0,
            CanJoin = !current,
            JoinText = current ? Strings.ContextMenu_ServerBrowser_YouAreHere : Strings.ContextMenu_ServerBrowser_Join,
            Region = region,
            Playing = server.Playing,
            Ping = server.EstimatedPingMs,
            IsFull = server.MaxPlayers > 0 && server.Playing >= server.MaxPlayers,
            IsBlocked = blocked
        };
    }

    public static ServerCard ForPrivate(RobloxPrivateServer server)
    {
        List<ServerCardLine> lines = new();
        if (server.OwnerName.Length > 0)
            lines.Add(new ServerCardLine("", string.Format(Strings.ContextMenu_ServerBrowser_OwnedBy, server.OwnerName)));
        lines.Add(new ServerCardLine("", server.MaxPlayers > 0 ? string.Format(Strings.ContextMenu_ServerBrowser_Capacity, server.Playing, server.MaxPlayers) : string.Format(Strings.ContextMenu_ServerBrowser_CapacityUnknown, server.Playing)));
        return new ServerCard
        {
            JobId = server.JobId,
            Title = server.Name.Length > 0 ? server.Name : Strings.ContextMenu_ServerBrowser_PrivateServer,
            Lines = lines,
            Avatars = Faces(server.Playing, server.PlayerTokens.Length),
            Tokens = server.PlayerTokens,
            Fill = server.MaxPlayers > 0 ? Math.Clamp((double)server.Playing / server.MaxPlayers, 0, 1) : 0,
            AccessCode = server.AccessCode,
            Playing = server.Playing
        };
    }

    public static ServerCard ForRecent(ActivityData entry)
    {
        List<ServerCardLine> lines = new()
        {
            new ServerCardLine("", "Joined " + entry.TimeJoined.ToString("t", CultureInfo.CurrentCulture)
                + (entry.TimeLeft is DateTime left ? ", left " + left.ToString("t", CultureInfo.CurrentCulture) : string.Empty)),
            new ServerCardLine("", entry.JobId.Length > 13 ? entry.JobId[..13] + "…" : entry.JobId)
        };
        return new ServerCard
        {
            JobId = entry.JobId,
            Title = string.IsNullOrWhiteSpace(entry.GameName) ? "Server" : entry.GameName,
            Lines = lines,
            JoinText = "Rejoin"
        };
    }

    private static IReadOnlyList<AvatarSlot> Faces(int playing, int tokens)
    {
        int people = Math.Max(playing, tokens);
        List<AvatarSlot> slots = new();
        if (people <= MaxFaces + 1)
        {
            for (int i = 0; i < people; i++)
                slots.Add(new AvatarSlot());
            return slots;
        }
        for (int i = 0; i < MaxFaces; i++)
            slots.Add(new AvatarSlot());
        slots.Add(new AvatarSlot { Text = "+" + (people - MaxFaces).ToString(CultureInfo.CurrentCulture) });
        return slots;
    }

    private static string DescribeRegion(RobloxDatacenter? dc)
    {
        if (dc == null)
            return string.Empty;
        string country = string.IsNullOrEmpty(dc.Country) ? string.Empty : VoidstrapMatchmaker.CountryToDisplayName(dc.Country);
        return string.Join(", ", new[] { dc.City, country }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string Compact(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
        return span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s" : $"{span.Seconds}s";
    }
}
