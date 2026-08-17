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

public partial class ShopPage
{
    private const string LOG_IDENT = "ShopPage";

    private sealed class BorderItem
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Section { get; init; } = string.Empty;
        public string Image { get; init; } = string.Empty;
        public int MinLevel { get; init; }
    }

    public event EventHandler? BackRequested;
    public event EventHandler? MarketRequested;

    private CancellationTokenSource? _cts;
    private readonly List<BorderItem> _items = new();
    private readonly List<string> _sectionOrder = new();
    private string _equippedId = string.Empty;
    private int _level;
    private int _maxLevel;
    private bool _busy;

    public ShopPage()
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
    }

    private void MarketTab_Click(object sender, RoutedEventArgs e)
    {
        MarketRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetStatus(string text)
    {
        LoadingRing.Visibility = Visibility.Collapsed;
        ItemsScroller.Visibility = Visibility.Collapsed;
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private async Task LoadAsync()
    {
        CancellationToken token = _cts?.Token ?? CancellationToken.None;
        if (token.IsCancellationRequested)
            return;

        if (!WebsiteAuth.IsSignedIn())
        {
            SetStatus("Sign in on the website to use the shop.");
            return;
        }

        LoadingRing.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        ItemsScroller.Visibility = Visibility.Collapsed;

        string? body = await GetAsync(App.WebsiteBaseUrl + "/api/store", token).ConfigureAwait(true);
        if (token.IsCancellationRequested)
            return;
        if (string.IsNullOrEmpty(body))
        {
            SetStatus("Could not load the shop.");
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                SetStatus("Could not load the shop.");
                return;
            }

            _items.Clear();
            if (root.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in items.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    _items.Add(new BorderItem
                    {
                        Id = ReadText(item, "id"),
                        Name = ReadText(item, "name"),
                        Section = ReadText(item, "section"),
                        Image = ReadText(item, "image"),
                        MinLevel = ReadInt(item, "minLevel"),
                    });
                }
            }

            _sectionOrder.Clear();
            if (root.TryGetProperty("sectionOrder", out JsonElement order) && order.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in order.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.String)
                        continue;
                    string name = entry.GetString() ?? string.Empty;
                    if (name.Length > 0 && !_sectionOrder.Contains(name))
                        _sectionOrder.Add(name);
                }
            }

            _level = ReadInt(root, "level");
            _maxLevel = ReadInt(root, "maxLevel");
            _equippedId = string.Empty;
            if (root.TryGetProperty("equipped", out JsonElement equipped))
            {
                if (equipped.ValueKind == JsonValueKind.String)
                    _equippedId = equipped.GetString() ?? string.Empty;
                else if (equipped.ValueKind == JsonValueKind.Object)
                    _equippedId = ReadText(equipped, "id");
            }

            int xp = 0;
            int needed = 0;
            if (root.TryGetProperty("progress", out JsonElement progress) && progress.ValueKind == JsonValueKind.Object)
            {
                xp = ReadInt(progress, "levelXp");
                needed = ReadInt(progress, "levelSpan");
                if (needed <= 0)
                    needed = ReadInt(progress, "nextLevelXp");
            }
            ApplyLevel(xp, needed);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT + "::Parse", ex);
            SetStatus("Could not load the shop.");
            return;
        }

        Render();
    }

    private void ApplyLevel(int xp, int needed)
    {
        LevelText.Text = "Level " + _level;
        bool maxed = _maxLevel > 0 && _level >= _maxLevel;
        if (maxed)
        {
            LevelXpText.Text = "Max level";
            LevelBar.Value = 100;
        }
        else if (needed > 0)
        {
            double ratio = Math.Max(0.0, Math.Min(1.0, (double)xp / needed));
            LevelXpText.Text = xp.ToString("N0") + " / " + needed.ToString("N0") + " XP";
            LevelBar.Value = ratio * 100.0;
        }
        else
        {
            LevelXpText.Text = string.Empty;
            LevelBar.Value = 0;
        }
    }

    private void Render()
    {
        ItemsHost.Items.Clear();
        if (_items.Count == 0)
        {
            SetStatus("No borders yet.");
            return;
        }

        Dictionary<string, List<BorderItem>> groups = new();
        List<string> discovered = new();
        foreach (BorderItem item in _items)
        {
            string section = string.IsNullOrWhiteSpace(item.Section) ? "General" : item.Section;
            if (!groups.TryGetValue(section, out List<BorderItem>? bucket))
            {
                bucket = new List<BorderItem>();
                groups[section] = bucket;
                discovered.Add(section);
            }
            bucket.Add(item);
        }

        List<string> ordered = new();
        foreach (string section in _sectionOrder)
        {
            if (groups.ContainsKey(section) && !ordered.Contains(section))
                ordered.Add(section);
        }
        foreach (string section in discovered)
        {
            if (!ordered.Contains(section))
                ordered.Add(section);
        }

        foreach (string section in ordered)
        {
            List<BorderItem> bucket = groups[section];
            bucket.Sort((a, b) => a.MinLevel.CompareTo(b.MinLevel));

            TextBlock heading = new TextBlock
            {
                Text = section,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 8),
            };
            ItemsHost.Items.Add(heading);

            WrapPanel row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            foreach (BorderItem item in bucket)
                row.Children.Add(BuildCard(item));
            ItemsHost.Items.Add(row);
        }

        int unlocked = 0;
        foreach (BorderItem item in _items)
        {
            if (_level >= item.MinLevel)
                unlocked++;
        }
        EarnedText.Text = "Unlocked " + unlocked + " of " + _items.Count + " borders at your level";

        LoadingRing.Visibility = Visibility.Collapsed;
        StatusText.Visibility = Visibility.Collapsed;
        ItemsScroller.Visibility = Visibility.Visible;
    }

    private UIElement BuildCard(BorderItem item)
    {
        bool unlocked = _level >= item.MinLevel;
        bool equipped = _equippedId == item.Id;

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

        if (!string.IsNullOrEmpty(item.Image))
        {
            Image art_image = new Image
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(8),
            };
            RenderOptions.SetBitmapScalingMode(art_image, BitmapScalingMode.HighQuality);
            try
            {
                string url = item.Image.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? item.Image
                    : App.WebsiteBaseUrl + item.Image;
                BitmapImage source = new BitmapImage();
                source.BeginInit();
                source.CacheOption = BitmapCacheOption.OnLoad;
                source.DecodePixelWidth = 256;
                source.UriSource = new Uri(url, UriKind.Absolute);
                source.EndInit();
                if (source.CanFreeze)
                    source.Freeze();
                art_image.Source = source;
            }
            catch (Exception)
            {
            }
            art.Child = art_image;
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

        TextBlock level = new TextBlock
        {
            Text = "Level " + item.MinLevel,
            FontSize = 11,
            Opacity = unlocked ? 0.65 : 1.0,
            Margin = new Thickness(0, 0, 0, 8),
        };
        if (!unlocked)
            level.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        stack.Children.Add(level);

        Wpf.Ui.Controls.Button action = new Wpf.Ui.Controls.Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Tag = item.Id,
        };

        if (!unlocked)
        {
            action.Content = "not high enough level!";
            action.IsEnabled = false;
        }
        else if (equipped)
        {
            action.Content = "Equipped";
            action.Appearance = Wpf.Ui.Common.ControlAppearance.Success;
            action.Click += Unequip_Click;
        }
        else
        {
            action.Content = "Equip";
            action.Appearance = Wpf.Ui.Common.ControlAppearance.Secondary;
            action.Click += Equip_Click;
        }
        stack.Children.Add(action);

        card.Child = stack;
        return card;
    }

    private void Equip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button button && button.Tag is string id)
            _ = EquipAsync(id);
    }

    private void Unequip_Click(object sender, RoutedEventArgs e)
    {
        _ = EquipAsync(string.Empty);
    }

    private async Task EquipAsync(string borderId)
    {
        if (_busy)
            return;
        _busy = true;
        try
        {
            CancellationToken token = _cts?.Token ?? CancellationToken.None;
            string payload = "{\"action\":\"equip\",\"borderId\":" + JsonSerializer.Serialize(borderId) + "}";
            string? body = await PostAsync(App.WebsiteBaseUrl + "/api/store", payload, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
                return;
            if (string.IsNullOrEmpty(body))
                return;

            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
                return;

            _equippedId = borderId;
            Render();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT + "::Equip", ex);
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
            if (!response.IsSuccessStatusCode)
                return null;
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
}
