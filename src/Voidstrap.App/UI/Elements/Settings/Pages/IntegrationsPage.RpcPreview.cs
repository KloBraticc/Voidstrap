using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Integrations;
using Voidstrap.Models;
using Voidstrap.Models.Entities;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class IntegrationsPage
{
    private const double PreviewIntervalMs = 500.0;
    private const long FallbackPlaceId = 189707L;
    private const string FallbackGameName = "Natural Disaster Survival";
    private const string FallbackCreatorName = "Stickmasterluke";

    private DispatcherTimer? _rpcPreviewTimer;
    private string _rpcLargeLoaded = string.Empty;
    private string _rpcSmallLoaded = string.Empty;
    private string _rpcLastElapsed = string.Empty;
    private PreviewGame? _rpcPreviewGame;
    private bool _rpcGameResolveStarted;
    private readonly DateTime _rpcPreviewStart = DateTime.UtcNow;

    private string _rpcAvatarUrl = string.Empty;
    private string _rpcAvatarText = string.Empty;

    private sealed class PreviewGame
    {
        public long UniverseId { get; init; }

        public long UserId { get; init; }

        public string Name { get; set; } = FallbackGameName;

        public string Creator { get; set; } = FallbackCreatorName;

        public string IconUrl { get; set; } = string.Empty;
    }

    private void StartRpcPreview()
    {
        if (_rpcPreviewTimer != null)
            return;
        _rpcPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewIntervalMs) };
        _rpcPreviewTimer.Tick += RpcPreviewTimer_Tick;
        _rpcPreviewTimer.Start();
        if (!_rpcGameResolveStarted)
        {
            _rpcGameResolveStarted = true;
            _ = ResolvePreviewGameAsync();
        }
        RefreshRpcPreview();
    }

    private void StopRpcPreview()
    {
        if (_rpcPreviewTimer == null)
            return;
        _rpcPreviewTimer.Stop();
        _rpcPreviewTimer.Tick -= RpcPreviewTimer_Tick;
        _rpcPreviewTimer = null;
    }

    private void RpcPreviewTimer_Tick(object? sender, EventArgs e)
    {
        RefreshRpcPreview();
    }

    private async Task ResolvePreviewGameAsync()
    {
        try
        {
            PreviewGame resolved = await ReadMostRecentGameAsync().ConfigureAwait(true);
            if (resolved.UniverseId != 0)
            {
                if (UniverseDetails.LoadFromCache(resolved.UniverseId) == null)
                {
                    try
                    {
                        await UniverseDetails.FetchSingle(resolved.UniverseId).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine("IntegrationsPage", "Preview universe fetch failed: " + ex.Message);
                    }
                }
                UniverseDetails? details = UniverseDetails.LoadFromCache(resolved.UniverseId);
                if (details?.Data != null)
                {
                    if (!string.IsNullOrWhiteSpace(details.Data.Name))
                        resolved.Name = details.Data.Name;
                    if (!string.IsNullOrWhiteSpace(details.Data.Creator?.Name))
                        resolved.Creator = details.Data.Creator.Name;
                    if (!string.IsNullOrWhiteSpace(details.Thumbnail?.ImageUrl))
                        resolved.IconUrl = details.Thumbnail.ImageUrl;
                }
            }
            if (string.IsNullOrWhiteSpace(resolved.IconUrl))
                await ApplyFallbackGameAsync(resolved).ConfigureAwait(true);
            _rpcPreviewGame = resolved;
            await ResolvePreviewAvatarAsync(resolved.UserId).ConfigureAwait(true);
            RefreshRpcPreview();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("IntegrationsPage::ResolvePreviewGame", ex);
            _rpcPreviewGame = new PreviewGame();
        }
    }

    private async Task ResolvePreviewAvatarAsync(long userId)
    {
        if (userId <= 0)
            return;
        try
        {
            UserDetails details = await UserDetails.Fetch(userId).ConfigureAwait(true);
            if (details?.Data == null)
                return;
            if (!string.IsNullOrWhiteSpace(details.Thumbnail?.ImageUrl))
                _rpcAvatarUrl = details.Thumbnail.ImageUrl;
            _rpcAvatarText = details.Data.DisplayName + " (@" + details.Data.Name + ")";
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("IntegrationsPage", "Preview avatar fetch failed: " + ex.Message);
        }
    }

    private static async Task ApplyFallbackGameAsync(PreviewGame target)
    {
        try
        {
            string universeJson = await Voidstrap.Utility.Http.GetString("https://apis.roblox.com/universes/v1/places/" + FallbackPlaceId + "/universe").ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(universeJson);
            if (!document.RootElement.TryGetProperty("universeId", out JsonElement idElement))
                return;
            long universeId = idElement.GetInt64();
            if (universeId == 0)
                return;
            await UniverseDetails.FetchSingle(universeId).ConfigureAwait(false);
            UniverseDetails? details = UniverseDetails.LoadFromCache(universeId);
            if (details?.Data == null)
                return;
            if (!string.IsNullOrWhiteSpace(details.Data.Name))
                target.Name = details.Data.Name;
            if (!string.IsNullOrWhiteSpace(details.Data.Creator?.Name))
                target.Creator = details.Data.Creator.Name;
            if (!string.IsNullOrWhiteSpace(details.Thumbnail?.ImageUrl))
                target.IconUrl = details.Thumbnail.ImageUrl;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("IntegrationsPage", "Fallback game resolve failed: " + ex.Message);
        }
    }

    private static async Task<PreviewGame> ReadMostRecentGameAsync()
    {
        try
        {
            if (!File.Exists(Paths.ServerHistory))
                return new PreviewGame();
            string json = await File.ReadAllTextAsync(Paths.ServerHistory).ConfigureAwait(false);
            List<ActivityData>? history = JsonSerializer.Deserialize<List<ActivityData>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (history == null || history.Count == 0)
                return new PreviewGame();
            ActivityData? newest = history
                .Where(entry => entry != null && (entry.UniverseId != 0 || entry.PlaceId != 0))
                .OrderByDescending(entry => entry.TimeJoined)
                .FirstOrDefault();
            if (newest == null)
                return new PreviewGame();
            if (newest.UniverseId == 0)
            {
                await UniverseDetails.ResolvePlacesToUniversesAsync(new[] { newest.PlaceId }).ConfigureAwait(false);
                if (UniverseDetails.TryGetUniverseForPlace(newest.PlaceId, out long resolvedUniverse))
                    newest.UniverseId = resolvedUniverse;
            }
            return new PreviewGame { UniverseId = newest.UniverseId, UserId = newest.UserId };
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("IntegrationsPage", "Preview history read failed: " + ex.Message);
            return new PreviewGame();
        }
    }

    private void RefreshRpcPreview()
    {
        try
        {
            PresenceSnapshot snapshot = ResolveSnapshot();
            RpcPreviewHeader.Text = snapshot.Active ? "Playing Roblox" : "Not in a game";
            RpcPreviewDetails.Text = snapshot.Details;
            RpcPreviewDetails.Visibility = string.IsNullOrEmpty(snapshot.Details) ? Visibility.Collapsed : Visibility.Visible;

            string state = snapshot.State;
            if (snapshot.HasParty)
                state = string.IsNullOrEmpty(state) ? snapshot.PartyText : state + " " + snapshot.PartyText;
            RpcPreviewState.Text = state;
            RpcPreviewState.Visibility = string.IsNullOrEmpty(state) ? Visibility.Collapsed : Visibility.Visible;

            string elapsed = FormatElapsed(snapshot);
            if (!string.Equals(elapsed, _rpcLastElapsed, StringComparison.Ordinal))
            {
                _rpcLastElapsed = elapsed;
                RpcPreviewElapsed.Text = elapsed;
            }
            RpcPreviewElapsed.Visibility = string.IsNullOrEmpty(elapsed) ? Visibility.Collapsed : Visibility.Visible;

            ApplyPreviewImage(RpcLargeImageHost, snapshot.LargeImageKey, snapshot.LargeImageText, ref _rpcLargeLoaded);
            bool hasSmall = ApplyPreviewImage(RpcSmallImageHost, snapshot.SmallImageKey, snapshot.SmallImageText, ref _rpcSmallLoaded);
            RpcSmallImageHost.Visibility = hasSmall ? Visibility.Visible : Visibility.Collapsed;

            BuildPreviewButtons(snapshot);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("IntegrationsPage::RefreshRpcPreview", ex);
        }
    }

    private PresenceSnapshot ResolveSnapshot()
    {
        try
        {
            DiscordRichPresence? presence = Watcher.Current?.RichPresence;
            if (presence != null)
            {
                PresenceSnapshot snapshot = presence.GetPresenceSnapshot();
                if (snapshot.Active)
                    return snapshot;
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("IntegrationsPage::ResolveSnapshot", ex);
        }
        return BuildSimulatedSnapshot();
    }

    private PresenceSnapshot BuildSimulatedSnapshot()
    {
        if (!App.Settings.Prop.UseDiscordRichPresence)
            return new PresenceSnapshot { Active = false };

        PreviewGame game = _rpcPreviewGame ?? new PreviewGame();
        string gameName = string.IsNullOrWhiteSpace(App.Settings.Prop.CustomGameName) ? game.Name : App.Settings.Prop.CustomGameName;
        PresenceSnapshot snapshot = new PresenceSnapshot
        {
            Active = true,
            Details = App.Settings.Prop.GameNameChecked ? gameName : string.Empty,
            Start = _rpcPreviewStart,
        };

        string state = string.Empty;
        if (App.Settings.Prop.GameCreatorChecked && !string.IsNullOrWhiteSpace(game.Creator))
            state = "by " + game.Creator;
        if (App.Settings.Prop.FFlagRPCDisplayer)
            state = state.Length == 0 ? "FFlags: " + CountActiveFlags() : state + " | FFlags: " + CountActiveFlags();
        if (App.Settings.Prop.GameStatusChecked)
            state = state.Length == 0 ? "Public server" : state + " | Public server";
        snapshot.State = state;

        if (!string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon))
            snapshot.LargeImageKey = App.Settings.Prop.UseCustomIcon;
        else if (App.Settings.Prop.GameIconChecked)
            snapshot.LargeImageKey = game.IconUrl;
        if (App.Settings.Prop.GameIconChecked)
            snapshot.LargeImageText = gameName;

        if (App.Settings.Prop.ShowAccountOnRichPresence && !string.IsNullOrWhiteSpace(_rpcAvatarUrl))
        {
            snapshot.SmallImageKey = _rpcAvatarUrl;
            snapshot.SmallImageText = _rpcAvatarText;
        }

        if (!App.Settings.Prop.HideRPCButtons)
        {
            snapshot.Buttons.Add(new PresenceButton { Label = "Join server", Url = "https://www.roblox.com/games/" + FallbackPlaceId });
            snapshot.Buttons.Add(new PresenceButton { Label = "See game page", Url = "https://www.roblox.com/games/" + FallbackPlaceId });
        }
        return snapshot;
    }

    private static int CountActiveFlags()
    {
        try
        {
            string path = Path.Combine(Paths.Mods, "ClientSettings", "ClientAppSettings.json");
            if (!File.Exists(path))
                return 0;
			JsonElement root = Voidstrap.Utility.JsonFile.Deserialize<JsonElement>(path, Voidstrap.Utility.JsonOptions.Tolerant, 16777216);
			if (root.ValueKind != JsonValueKind.Object)
                return 0;
            int count = 0;
			foreach (JsonProperty _ in root.EnumerateObject())
                count++;
            return count;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatElapsed(PresenceSnapshot snapshot)
    {
        if (!snapshot.Active || !snapshot.Start.HasValue)
            return string.Empty;
        TimeSpan span = DateTime.UtcNow - snapshot.Start.Value;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        int totalSeconds = (int)Math.Floor(span.TotalSeconds);
        int hours = totalSeconds / 3600;
        int minutes = totalSeconds % 3600 / 60;
        int seconds = totalSeconds % 60;
        string clock = hours > 0
            ? hours + ":" + minutes.ToString("00") + ":" + seconds.ToString("00")
            : minutes.ToString("00") + ":" + seconds.ToString("00");
        return clock + " elapsed";
    }

    private static bool ApplyPreviewImage(Border target, string key, string tooltip, ref string loaded)
    {
        target.ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "voidstrap", StringComparison.OrdinalIgnoreCase))
        {
            if (loaded.Length != 0)
            {
                target.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
                loaded = string.Empty;
            }
            return false;
        }
        if (string.Equals(loaded, key, StringComparison.Ordinal))
            return target.Background is ImageBrush;
        loaded = key;
        if (!Uri.TryCreate(key, UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != "data"))
        {
            target.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            return false;
        }
        ImageSource? source = Voidstrap.Utility.AppImage.LoadSync(key, 160);
        if (source == null)
        {
            target.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            return false;
        }
        ImageBrush brush = new ImageBrush(source) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        target.Background = brush;
        return true;
    }

    private void BuildPreviewButtons(PresenceSnapshot snapshot)
    {
        if (RpcPreviewButtons.Children.Count == snapshot.Buttons.Count && snapshot.Buttons.Count == 0)
        {
            RpcPreviewButtons.Visibility = Visibility.Collapsed;
            return;
        }
        if (RpcPreviewButtons.Children.Count == snapshot.Buttons.Count)
            return;
        RpcPreviewButtons.Children.Clear();
        if (snapshot.Buttons.Count == 0)
        {
            RpcPreviewButtons.Visibility = Visibility.Collapsed;
            return;
        }
        RpcPreviewButtons.Visibility = Visibility.Visible;
        for (int i = 0; i < snapshot.Buttons.Count && i < 2; i++)
        {
            PresenceButton button = snapshot.Buttons[i];
            Border host = new Border
            {
                CornerRadius = new CornerRadius(3.0),
                Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
                Margin = new Thickness(0.0, i == 0 ? 0.0 : 6.0, 0.0, 0.0),
                ToolTip = string.IsNullOrEmpty(button.Url) ? null : button.Url,
            };
            host.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            host.SetResourceReference(Border.BorderBrushProperty, "ControlElevationBorderBrush");
            host.BorderThickness = new Thickness(1.0);
            TextBlock label = new TextBlock
            {
                Text = button.Label,
                FontSize = 13.0,
                FontWeight = FontWeights.Medium,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            host.Child = label;
            RpcPreviewButtons.Children.Add(host);
        }
    }
}
