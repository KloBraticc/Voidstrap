using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Voidstrap.Integrations;

namespace Voidstrap.UI.Elements.Overlay;

// Your own account at a glance: avatar, names, counts, Robux, Premium and when you joined
public partial class SessionProfileWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _playing;
    private long _userId;
    private bool _started;
    private bool _closed;

    public SessionProfileWindow(string? playing = null)
    {
        _playing = string.IsNullOrWhiteSpace(playing) ? "Not in a game" : playing;
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _closed = true;
            // Cancelled but not disposed, loads that are still finishing read the token
            _lifetime.Cancel();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
            return;
        _started = true;
        try
        {
            await LoadAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionProfileWindow", "Your profile could not be shown: " + ex.Message);
            if (!_closed)
                ShowStatus("Your profile could not be loaded right now.");
        }
    }

    private async Task LoadAsync()
    {
        (RobloxChatStatus status, RobloxProfileInfo? profile) = await RobloxProfile.GetMineAsync(_lifetime.Token);
        if (_closed)
            return;
        if (profile == null)
        {
            ShowStatus(status switch
            {
                RobloxChatStatus.NotSignedIn => "Allow cookie access in Voidstrap's settings to see your profile.",
                RobloxChatStatus.SignInExpired => "Your Roblox sign in expired, sign in again to see your profile.",
                _ => "Your profile could not be loaded right now."
            });
            return;
        }
        _userId = profile.UserId;
        DisplayNameText.Text = profile.DisplayName.Length > 0 ? profile.DisplayName : profile.Name;
        UsernameText.Text = "@" + profile.Name;
        VerifiedGlyph.Visibility = profile.Verified ? Visibility.Visible : Visibility.Collapsed;
        PremiumText.Visibility = profile.Premium == true ? Visibility.Visible : Visibility.Collapsed;
        FriendsText.Text = Count(profile.Friends);
        FollowersText.Text = Count(profile.Followers);
        FollowingText.Text = Count(profile.Following);
        RobuxText.Text = profile.Robux is long robux ? robux.ToString("N0", CultureInfo.CurrentCulture) : "-";
        UserIdText.Text = profile.UserId.ToString(CultureInfo.InvariantCulture);
        JoinedText.Text = profile.Created is DateTimeOffset created
            ? created.LocalDateTime.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) + "  ·  " + Age(created)
            : "Unavailable";
        PlayingText.Text = _playing;
        if (profile.Description.Trim().Length > 0)
        {
            AboutText.Text = profile.Description.Trim();
            AboutCard.Visibility = Visibility.Visible;
        }
        AvatarImage.Source = Picture(profile.AvatarUrl.Length > 0 ? profile.AvatarUrl : profile.HeadshotUrl);
        Waiting.Visibility = Visibility.Collapsed;
        ProfileContent.Visibility = Visibility.Visible;
    }

    private void ShowStatus(string text)
    {
        LoadingBar.Visibility = Visibility.Collapsed;
        StatusText.Text = text;
        Waiting.Visibility = Visibility.Visible;
        ProfileContent.Visibility = Visibility.Collapsed;
    }

    private static BitmapImage? Picture(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        try
        {
            BitmapImage image = new();
            image.BeginInit();
            image.UriSource = uri;
            image.DecodePixelWidth = 264;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // 211, 12.4K, 1.2M
    private static string Count(long? value)
    {
        if (value is not long number)
            return "-";
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (number >= 1_000_000)
            return (number / 1_000_000d).ToString("0.#", culture) + "M";
        if (number >= 10_000)
            return (number / 1_000d).ToString("0.#", culture) + "K";
        return number.ToString("N0", culture);
    }

    private static string Age(DateTimeOffset created)
    {
        TimeSpan age = DateTimeOffset.UtcNow - created;
        int years = (int)(age.TotalDays / 365.25);
        if (years >= 1)
            return years == 1 ? "1 year ago" : years + " years ago";
        int months = (int)(age.TotalDays / 30.44);
        if (months >= 1)
            return months == 1 ? "1 month ago" : months + " months ago";
        int days = Math.Max(0, (int)age.TotalDays);
        return days == 1 ? "1 day ago" : days + " days ago";
    }

    private string ProfileLink => "https://www.roblox.com/users/" + _userId.ToString(CultureInfo.InvariantCulture) + "/profile";

    private void OpenProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_userId <= 0)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(ProfileLink) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionProfileWindow", "Your profile could not be opened: " + ex.Message);
        }
    }

    private async void CopyLink_Click(object sender, RoutedEventArgs e) => await CopyAsync(ProfileLink, CopyGlyph, "");

    private async void CopyId_Click(object sender, RoutedEventArgs e) => await CopyAsync(_userId.ToString(CultureInfo.InvariantCulture), CopyIdGlyph, "");

    // Copies, shows a tick for a moment, then puts the icon back
    private async Task CopyAsync(string text, TextBlock glyph, string original)
    {
        if (_userId <= 0)
            return;
        try
        {
            Clipboard.SetText(text);
            glyph.Text = "";
            await Task.Delay(1200);
            if (!_closed)
                glyph.Text = original;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionProfileWindow", "Could not copy: " + ex.Message);
        }
    }
}
