using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
    private static readonly JsonSerializerOptions RpcHistoryJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };     

    private const double PreviewIntervalMs = 500.0;
    private const long FallbackPlaceId = 189707L;
    private const string FallbackGameName = "Natural Disaster Survival";
    private const string FallbackCreatorName = "Stickmasterluke";
    private const string VoidstrapImageKey = "voidstrap";
    private const string VoidstrapImageUri = "pack://application:,,,/Voidstrap.png";

    private DispatcherTimer? _rpcPreviewTimer;
    private string _rpcButtonsSignature = string.Empty;
    private int _rpcFlagCount;
    private long _rpcFlagsFileSize = -1;
    private long _rpcFlagsReadAt;
    private readonly PreviewImageLoad _rpcLargeImageLoad = new();
    private readonly PreviewImageLoad _rpcSmallImageLoad = new();
    private string _rpcLastElapsed = string.Empty;
    private PreviewGame? _rpcPreviewGame;
    private bool _rpcGameResolveStarted;
    private readonly DateTime _rpcPreviewStart = DateTime.UtcNow;

    private string _rpcAvatarUrl = string.Empty;
    private string _rpcAvatarText = string.Empty;

    private sealed class PreviewGame
    {
        public long UniverseId { get; set; }

        public long UserId { get; init; }

        public ActivityData Activity { get; init; } = new ActivityData { PlaceId = FallbackPlaceId };

        public string Name { get; set; } = FallbackGameName;

        public string Description { get; set; } = string.Empty;

        public string Creator { get; set; } = FallbackCreatorName;

        public bool Verified { get; set; }

        public string IconUrl { get; set; } = string.Empty;

        public string Location { get; set; } = string.Empty;
    }

    private sealed class PreviewImageLoad
    {
        public CancellationTokenSource? Cancellation { get; set; }

        public string RequestedKey { get; set; } = string.Empty;

        public string LoadedKey { get; set; } = string.Empty;

        public long RetryAfter { get; set; }

        public int Generation { get; set; }

        public bool Loading { get; set; }

        public bool FailureLogged { get; set; }
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
        if (_rpcPreviewTimer != null)
        {
            _rpcPreviewTimer.Stop();
            _rpcPreviewTimer.Tick -= RpcPreviewTimer_Tick;
            _rpcPreviewTimer = null;
        }
        CancelPreviewImageLoad(_rpcLargeImageLoad);
        CancelPreviewImageLoad(_rpcSmallImageLoad);
        _rpcLargeImageLoad.Generation++;
        _rpcSmallImageLoad.Generation++;
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
            if (resolved.Activity.PlaceId > 0 && resolved.UniverseId != 0)
            {
                string placeName = await DiscordRichPresence.GetPlaceNameAsync(resolved.Activity.PlaceId, CancellationToken.None).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(placeName))
                    resolved.Name = placeName;
            }
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
                    if (string.Equals(resolved.Name, FallbackGameName, StringComparison.Ordinal))
                        resolved.Name = string.IsNullOrWhiteSpace(details.Data.Name) ? "Private experience" : details.Data.Name;
                    resolved.Description = details.Data.Description ?? string.Empty;
                    resolved.Creator = details.Data.Creator?.Name ?? string.Empty;
                    resolved.Verified = details.Data.Creator?.HasVerifiedBadge ?? false;
                    if (!string.IsNullOrWhiteSpace(details.Thumbnail?.ImageUrl))
                        resolved.IconUrl = details.Thumbnail.ImageUrl;
                }
            }
            if (resolved.UniverseId == 0)
                await ApplyFallbackGameAsync(resolved).ConfigureAwait(true);
            if (resolved.Activity.MachineAddressValid)
            {
                try
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                    resolved.Location = await resolved.Activity.QueryServerLocation(timeout.Token).ConfigureAwait(true) ?? string.Empty;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("IntegrationsPage", "Preview server location failed: " + ex.Message);
                }
            }
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
            target.UniverseId = universeId;
            if (!string.IsNullOrWhiteSpace(details.Data.Name))
                target.Name = details.Data.Name;
            target.Description = details.Data.Description ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(details.Data.Creator?.Name))
                target.Creator = details.Data.Creator.Name;
            target.Verified = details.Data.Creator?.HasVerifiedBadge ?? false;
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
            List<ActivityData>? history = JsonSerializer.Deserialize<List<ActivityData>>(json, RpcHistoryJsonOptions);
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
            return new PreviewGame { UniverseId = newest.UniverseId, UserId = newest.UserId, Activity = newest };
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

            QueuePreviewImage(RpcLargeImageHost, RpcLargeImage, snapshot.LargeImageKey, snapshot.LargeImageText, _rpcLargeImageLoad, false);
            QueuePreviewImage(RpcSmallImageHost, RpcSmallImage, snapshot.SmallImageKey, snapshot.SmallImageText, _rpcSmallImageLoad, true);

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
        string shownName = string.IsNullOrWhiteSpace(App.Settings.Prop.CustomGameName) ? game.Name : App.Settings.Prop.CustomGameName;
        (string cleanName, string? betaTag) = DiscordRichPresence.ExtractBetaTag(shownName, game.Description);

        PresenceSnapshot snapshot = new PresenceSnapshot
        {
            Active = true,
            Details = Voidstrap.Utility.RpcText.Render(DiscordRichPresence.BuildDetailText(cleanName, betaTag, game.Creator, game.Verified)),
            State = Voidstrap.Utility.RpcText.Render(DiscordRichPresence.BuildStateText(
                game.Activity.ServerType,
                string.Empty,
                App.Settings.Prop.ServerLocationGame && game.Location.Length > 0 ? game.Location : null,
                App.Settings.Prop.FFlagRPCDisplayer ? ReadFlagCount() : 0)),
            Start = _rpcPreviewStart,
        };

        string largeImage = !string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon)
            ? App.Settings.Prop.UseCustomIcon
            : App.Settings.Prop.GameIconChecked ? game.IconUrl : string.Empty;
        snapshot.LargeImageKey = Voidstrap.Utility.DiscordPresenceGuard.Key(largeImage);
        snapshot.LargeImageText = Voidstrap.Utility.RpcText.Render(DiscordRichPresence.BuildLargeImageText(shownName, game.Creator));

        if (App.Settings.Prop.ShowAccountOnRichPresence && !string.IsNullOrWhiteSpace(_rpcAvatarUrl))
        {
            snapshot.SmallImageKey = _rpcAvatarUrl;
            snapshot.SmallImageText = _rpcAvatarText;
        }
        else
        {
            snapshot.SmallImageKey = VoidstrapImageKey;
            snapshot.SmallImageText = "Voidstrap";
        }

        foreach (DiscordRPC.Button button in DiscordRichPresence.BuildButtons(game.Activity))
            snapshot.Buttons.Add(new PresenceButton { Label = button.Label ?? string.Empty, Url = button.Url ?? string.Empty });
        return snapshot;
    }

    private int ReadFlagCount()
    {
        try
        {
            long now = Environment.TickCount64;
            if (_rpcFlagsReadAt != 0 && now - _rpcFlagsReadAt < 5000)
                return _rpcFlagCount;
            _rpcFlagsReadAt = now;
            string path = Path.Combine(Paths.Mods, "ClientSettings", "ClientAppSettings.json");
            if (!File.Exists(path))
            {
                _rpcFlagsFileSize = -1;
                _rpcFlagCount = 0;
                return 0;
            }
            long size = new FileInfo(path).Length;
            if (size == _rpcFlagsFileSize)
                return _rpcFlagCount;
            _rpcFlagsFileSize = size;
            _rpcFlagCount = size > 16777216 ? 0 : DiscordRichPresence.ParseFlagCount(File.ReadAllText(path));
            return _rpcFlagCount;
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

    private void QueuePreviewImage(Border host, Image image, string key, string tooltip, PreviewImageLoad state, bool hideHostWhenEmpty)
    {
        host.ToolTip = string.IsNullOrEmpty(tooltip) ? null : tooltip;
        if (string.Equals(key, VoidstrapImageKey, StringComparison.OrdinalIgnoreCase))
            key = VoidstrapImageUri;
        if (!IsPreviewImageKeySupported(key))
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                ResetPreviewImage(host, image, state, hideHostWhenEmpty);
                return;
            }
            if (!string.Equals(state.RequestedKey, key, StringComparison.Ordinal))
            {
                ResetPreviewImage(host, image, state, hideHostWhenEmpty);
                state.RequestedKey = key;
                state.FailureLogged = true;
                App.Logger.WriteLine("IntegrationsPage", "Preview image key is unsupported: " + key);
            }
            return;
        }

        long now = Environment.TickCount64;
        if (string.Equals(state.LoadedKey, key, StringComparison.Ordinal) && image.Source != null)
        {
            image.Visibility = Visibility.Visible;
            if (hideHostWhenEmpty)
                host.Visibility = Visibility.Visible;
            return;
        }
        if (string.Equals(state.RequestedKey, key, StringComparison.Ordinal) && (state.Loading || now < state.RetryAfter))
            return;

        CancelPreviewImageLoad(state);
        state.Generation++;
        state.RequestedKey = key;
        state.Loading = true;
        state.RetryAfter = 0;
        state.LoadedKey = string.Empty;
        state.FailureLogged = false;
        image.Source = null;
        image.Visibility = Visibility.Collapsed;
        if (hideHostWhenEmpty)
            host.Visibility = Visibility.Collapsed;
        CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(12));
        state.Cancellation = cancellation;
        _ = LoadPreviewImageAsync(host, image, key, state, state.Generation, hideHostWhenEmpty, cancellation);
    }

    private async Task LoadPreviewImageAsync(Border host, Image image, string key, PreviewImageLoad state, int generation, bool hideHostWhenEmpty, CancellationTokenSource cancellation)
    {
        ImageSource? source = null;
        try
        {
            source = await Voidstrap.Utility.AppImage.LoadAsync(key, 160, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("IntegrationsPage", "Preview image load failed: " + ex.Message);
        }
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (generation != state.Generation || !string.Equals(state.RequestedKey, key, StringComparison.Ordinal))
                    return;
                state.Loading = false;
                state.Cancellation = null;
                if (source == null)
                {
                    state.LoadedKey = string.Empty;
                    state.RetryAfter = Environment.TickCount64 + 3000;
                    if (!state.FailureLogged)
                    {
                        state.FailureLogged = true;
                        App.Logger.WriteLine("IntegrationsPage", "Preview image could not be loaded: " + key);
                    }
                    image.Source = null;
                    image.Visibility = Visibility.Collapsed;
                    if (hideHostWhenEmpty)
                        host.Visibility = Visibility.Collapsed;
                    return;
                }
                state.LoadedKey = key;
                state.RetryAfter = 0;
                state.FailureLogged = false;
                image.Source = source;
                image.Visibility = Visibility.Visible;
                if (hideHostWhenEmpty)
                    host.Visibility = Visibility.Visible;
                App.Logger.WriteLine("IntegrationsPage", "Preview image displayed at " + source.Width.ToString("F0") + "x" + source.Height.ToString("F0"));
            }, DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("IntegrationsPage", "Preview image display failed: " + ex.Message);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static bool IsPreviewImageKeySupported(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;
        if (key.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || File.Exists(key))
            return true;
        return Uri.TryCreate(key, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeFile || uri.Scheme == "pack");
    }

    private static void ResetPreviewImage(Border host, Image image, PreviewImageLoad state, bool hideHostWhenEmpty)
    {
        CancelPreviewImageLoad(state);
        state.Generation++;
        state.RequestedKey = string.Empty;
        state.LoadedKey = string.Empty;
        state.RetryAfter = 0;
        state.Loading = false;
        state.FailureLogged = false;
        image.Source = null;
        image.Visibility = Visibility.Collapsed;
        if (hideHostWhenEmpty)
            host.Visibility = Visibility.Collapsed;
    }

    private static void CancelPreviewImageLoad(PreviewImageLoad state)
    {
        CancellationTokenSource? cancellation = state.Cancellation;
        state.Cancellation = null;
        state.Loading = false;
        if (cancellation == null)
            return;
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void BuildPreviewButtons(PresenceSnapshot snapshot)
    {
        string signature = string.Join("\n", snapshot.Buttons.Select(button => button.Label + "\t" + button.Url));
        if (string.Equals(signature, _rpcButtonsSignature, StringComparison.Ordinal) && RpcPreviewButtons.Children.Count == Math.Min(snapshot.Buttons.Count, 2))
            return;
        _rpcButtonsSignature = signature;
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
