using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class BlackMarketPage
{
    private const string LOG_IDENT = "BlackMarketPage";

    private sealed class MarketItem
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public string Reward { get; init; } = string.Empty;
        public int Minutes { get; init; }
        public int BoostMinutes { get; init; }
        public bool Unlocked { get; init; }
        public bool Claimed { get; init; }
        public bool Claimable { get; init; }
    }

    public event EventHandler? BackRequested;
    public event EventHandler? ShopRequested;

    private CancellationTokenSource? _cts;
    private readonly List<MarketItem> _items = new();
    private bool _busy;

    public BlackMarketPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _cts = new CancellationTokenSource();
        try
        {
            System.Windows.Media.Imaging.BitmapImage bottle = new System.Windows.Media.Imaging.BitmapImage();
            bottle.BeginInit();
            bottle.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bottle.DecodePixelWidth = 64;
            bottle.UriSource = new Uri(App.WebsiteBaseUrl + "/assets/img/blackmarket-allinone.png?v=3", UriKind.Absolute);
            bottle.EndInit();
            if (bottle.CanFreeze)
                bottle.Freeze();
            MarketTabIcon.Source = bottle;
        }
        catch (Exception)
        {
        }
        Voidstrap.Utility.MarketBackground.Apply();
        _ = LoadAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        catch (Exception)
        {
        }
        _cts = null;
        Voidstrap.Utility.MarketBackground.Restore();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _ = LoadAsync();
    }

    private void ShopTab_Click(object sender, RoutedEventArgs e)
    {
        ShopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MarketTab_Click(object sender, RoutedEventArgs e)
    {
    }

    private void SetStatus(string text)
    {
        LoadingRing.Visibility = Visibility.Collapsed;
        ItemsScroller.Visibility = Visibility.Collapsed;
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private static string PlayLabel(int minutes)
    {
        int value = Math.Max(0, minutes);
        if (value < 60)
            return value + "m";
        int hours = value / 60;
        int rest = value % 60;
        return rest > 0 ? hours + "h " + rest + "m" : hours + "h";
    }

    private static string RewardLabel(string reward)
    {
        switch (reward)
        {
            case "triple-xp": return "Triple XP";
            case "quad-xp": return "Quadruple XP";
            case "double-playtime": return "Double playtime";
            case "triple-playtime": return "Triple playtime";
            case "quad-playtime": return "Quadruple playtime";
            case "all-in-one": return "All in One";
            default: return "Double XP";
        }
    }

    private static Color RewardColor(string reward)
    {
        switch (reward)
        {
            case "triple-xp":
            case "triple-playtime":
                return Color.FromRgb(0x38, 0xBD, 0xF8);
            case "quad-xp":
            case "quad-playtime":
                return Color.FromRgb(0xF8, 0x71, 0x71);
            case "all-in-one":
                return Color.FromRgb(0xE8, 0x79, 0xF9);
            case "double-playtime":
                return Color.FromRgb(0xA3, 0xE6, 0x35);
            default:
                return Color.FromRgb(0xFB, 0xBF, 0x24);
        }
    }

    private async Task LoadAsync()
    {
        CancellationToken token = _cts?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
            return;

        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        ItemsScroller.Visibility = Visibility.Collapsed;

        string? body = await GetAsync(App.WebsiteBaseUrl + "/api/blackmarket", token).ConfigureAwait(true);
        if (token.IsCancellationRequested)
            return;
        if (string.IsNullOrEmpty(body))
        {
            SetStatus("Could not load the Black Market.");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                SetStatus("Could not load the Black Market.");
                return;
            }
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
            {
                SetStatus("The Black Market is not open yet.");
                return;
            }

            _items.Clear();
            if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    _items.Add(new MarketItem
                    {
                        Id = ReadText(item, "id"),
                        Name = ReadText(item, "name"),
                        Icon = ReadText(item, "icon"),
                        Reward = ReadText(item, "reward"),
                        Minutes = ReadInt(item, "minutes"),
                        BoostMinutes = ReadInt(item, "boostMinutes"),
                        Unlocked = ReadBool(item, "unlocked"),
                        Claimed = ReadBool(item, "claimed"),
                        Claimable = ReadBool(item, "claimable"),
                    });
                }
            }

            if (root.TryGetProperty("summary", out JsonElement summary) && summary.ValueKind == JsonValueKind.Object)
            {
                int played = ReadInt(summary, "minutes");
                int required = ReadInt(summary, "required");
                int percent = ReadInt(summary, "percent");
                int remaining = ReadInt(summary, "remaining");
                ProgressText.Text = PlayLabel(played) + " / " + PlayLabel(required) + " played";
                ProgressFill.Width = Math.Max(0.0, Math.Min(100.0, percent)) / 100.0 * 320.0;
                FocusName.Text = remaining > 0
                    ? "Play " + PlayLabel(remaining) + " to unlock the next item"
                    : "Everything in stock is unlocked.";
            }

            if (root.TryGetProperty("playtime", out JsonElement playtime) && playtime.ValueKind == JsonValueKind.Object)
            {
                long resetsAt = ReadLong(playtime, "resetsAt");
                if (resetsAt > 0)
                {
                    long left = resetsAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (left > 0)
                    {
                        long days = left / 86400000L;
                        long hours = (left % 86400000L) / 3600000L;
                        RestockText.Text = days > 0
                            ? "Restocks in " + days + "d " + hours + "h"
                            : "Restocks in " + hours + "h";
                    }
                }
            }

            SetHeaderIcon("/assets/img/blackmarket-allinone.png?v=3");
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT + "::Parse", ex);
            SetStatus("Could not load the Black Market.");
            return;
        }

        Render();
    }

    private void SetHeaderIcon(string icon)
    {
        BitmapImage? source = LoadIcon(icon);
        if (source != null)
            HeaderIcon.Source = source;
    }

    private static BitmapImage? LoadIcon(string icon)
    {
        if (string.IsNullOrEmpty(icon))
            return null;
        try
        {
            string url = icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? icon
                : App.WebsiteBaseUrl + icon;
            BitmapImage source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.DecodePixelWidth = 256;
            source.UriSource = new Uri(url, UriKind.Absolute);
            source.EndInit();
            if (source.CanFreeze)
                source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Render()
    {
        ItemsHost.Items.Clear();
        if (_items.Count == 0)
        {
            SetStatus("The market is empty right now.");
            return;
        }

        foreach (MarketItem item in _items)
            ItemsHost.Items.Add(BuildCard(item));

        LoadingRing.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ItemsScroller.Visibility = Visibility.Visible;
    }

    private UIElement BuildCard(MarketItem item)
    {
        Border card = new Border
        {
            Width = 168,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "ControlFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");

        StackPanel stack = new StackPanel();

        Border art = new Border
        {
            Height = 132,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(0, 0, 0, 10),
        };
        art.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        BitmapImage? icon = LoadIcon(item.Icon);
        if (icon != null)
        {
            Image image = new Image
            {
                Source = icon,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(14),
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            art.Child = image;
        }
        stack.Children.Add(art);

        TextBlock name = new TextBlock
        {
            Text = item.Name,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 2),
        };
        stack.Children.Add(name);

        TextBlock played = new TextBlock
        {
            Text = PlayLabel(item.Minutes) + " played",
            FontSize = 11,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        stack.Children.Add(played);

        TextBlock reward = new TextBlock
        {
            Text = RewardLabel(item.Reward) + " " + PlayLabel(item.BoostMinutes),
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(RewardColor(item.Reward)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 8),
        };
        stack.Children.Add(reward);

        Wpf.Ui.Controls.Button action = new Wpf.Ui.Controls.Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = item.Id,
        };
        if (item.Claimed)
        {
            action.Content = "Taken";
            action.Appearance = Wpf.Ui.Common.ControlAppearance.Success;
            action.IsEnabled = false;
        }
        else if (item.Claimable)
        {
            action.Content = "Take";
            action.Appearance = Wpf.Ui.Common.ControlAppearance.Primary;
            action.Click += Take_Click;
        }
        else
        {
            action.Content = "Locked";
            action.IsEnabled = false;
        }
        stack.Children.Add(action);

        card.Child = stack;
        return card;
    }

    private void Take_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button button && button.Tag is string id)
            _ = TakeAsync(id);
    }

    private async Task TakeAsync(string id)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            CancellationToken token = _cts?.Token ?? CancellationToken.None;
            string payload = "{\"action\":\"claim\",\"id\":" + JsonSerializer.Serialize(id) + "}";
            string? body = await PostAsync(App.WebsiteBaseUrl + "/api/blackmarket", payload, token).ConfigureAwait(true);
            if (token.IsCancellationRequested || string.IsNullOrEmpty(body))
                return;
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT + "::Take", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private static async Task<string?> GetAsync(string url, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
            string? websiteToken = WebsiteAuth.GetToken();
            if (!string.IsNullOrEmpty(websiteToken))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + websiteToken);
            using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> PostAsync(string url, string payload, CancellationToken token)
    {
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, url);
            string? websiteToken = WebsiteAuth.GetToken();
            if (!string.IsNullOrEmpty(websiteToken))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + websiteToken);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            return await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, 4 * 1024 * 1024, token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ReadText(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static int ReadInt(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed)
            ? parsed
            : 0;
    }

    private static long ReadLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed)
            ? parsed
            : 0L;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
    }
}
