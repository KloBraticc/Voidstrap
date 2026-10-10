using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Voidstrap.UI.Elements.Overlay;

// One tab: its page, and everything the tab strip shows for it
public sealed class BrowserTab : INotifyPropertyChanged
{
    public const string NewTabUrl = "about:newtab";

    private string _title;
    private string _url;
    private ImageSource? _icon;
    private bool _isActive;
    private bool _isShown;
    private bool _isLoading;
    private bool _isAudible;
    private bool _isMuted;
    private bool _isPinned;
    private bool _isHibernated;
    private bool _isSplit;
    private int _blocked;

    public BrowserTab(string url, string title, double scroll)
    {
        _url = url;
        _title = title;
        PendingScroll = scroll;
        Scroll = scroll;
        LastShown = DateTime.UtcNow;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WebView2? View { get; set; }
    public Border? Frame { get; set; }
    public CoreWebView2? Core { get; set; }
    public BrowserTab? Opener { get; set; }
    public bool Closed { get; set; }
    public double Scroll { get; set; }
    public double PendingScroll { get; set; }
    public DateTime LastShown { get; set; }
    public DateTime LoadStarted { get; set; }

    public bool IsNewTab => _url == NewTabUrl;

    public string Url
    {
        get => _url;
        set
        {
            if (Set(ref _url, value))
            {
                Raise(nameof(Host));
                Raise(nameof(IsNewTab));
                Raise(nameof(HoverText));
            }
        }
    }

    public string Host => IsNewTab ? "New tab" : Uri.TryCreate(_url, UriKind.Absolute, out Uri? uri) ? uri.Host : string.Empty;

    public string Title
    {
        get => _title;
        set
        {
            if (Set(ref _title, value))
                Raise(nameof(HoverText));
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    // Shown on screen, the active tab and its split view partner
    public bool IsShown
    {
        get => _isShown;
        set => Set(ref _isShown, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => Set(ref _isLoading, value);
    }

    public bool IsAudible
    {
        get => _isAudible;
        set
        {
            if (Set(ref _isAudible, value))
                Raise(nameof(ShowsAudio));
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (Set(ref _isMuted, value))
            {
                Raise(nameof(ShowsAudio));
                Raise(nameof(AudioGlyph));
            }
        }
    }

    public bool ShowsAudio => _isAudible || _isMuted;
    public string AudioGlyph => _isMuted ? "\uE74F" : "\uE767";

    public bool IsPinned
    {
        get => _isPinned;
        set => Set(ref _isPinned, value);
    }

    // Put away to free memory: the page is closed and opens again where you were when you come back
    public bool IsHibernated
    {
        get => _isHibernated;
        set
        {
            if (Set(ref _isHibernated, value))
                Raise(nameof(HoverText));
        }
    }

    public bool IsSplit
    {
        get => _isSplit;
        set => Set(ref _isSplit, value);
    }

    public int Blocked
    {
        get => _blocked;
        set
        {
            if (Set(ref _blocked, value))
                Raise(nameof(HoverText));
        }
    }

    // The second line of the card shown when hovering a tab
    public string HoverText
    {
        get
        {
            string text = Host;
            if (_isHibernated)
                text += " · Sleeping to save memory";
            if (_blocked > 0)
                text += $" · {_blocked} blocked";
            return text;
        }
    }

    public SavedTab Save() => new() { Url = _url, Title = _title, Scroll = Scroll, Pinned = _isPinned, Muted = _isMuted };

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
