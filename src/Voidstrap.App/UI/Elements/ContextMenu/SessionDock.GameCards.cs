using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Voidstrap.Enums;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Overlays;
using Voidstrap.Models.Entities;
using Voidstrap.Models.Persistable;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionDock
{
    private const int DeckCardLimit = 8;
    private readonly List<ExperienceCard> _experienceCards = new();
    private readonly ServerMatchmaker? _gameMatchmaker;
    private CancellationTokenSource? _deckRequest;
    private long _deckPlaceId;
    private double _deckScale = 1;
    private bool _launchingDeckGame;
    private bool _deckOpening;
    private bool _deckShown;
    private static readonly TimeSpan DeckRetreatLength = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan CardHoverLength = TimeSpan.FromMilliseconds(150);
    private const double DeckSlideDistance = 26;

    private sealed record ExperienceCard(Button Button, Border Artwork, Image Icon, TextBlock Title, bool Featured)
    {
        public bool HasCover { get; set; }
    }

    private static bool DockReducedMotion =>
        Wpf.Ui.Animations.RenderReady.ReducedMotion
        || Voidstrap.Utility.Platform.IsWindows && !SystemParameters.ClientAreaAnimation;

    private Vector DockEdgeDirection => VisualSide switch
    {
        OverlayDockSide.Left => new Vector(-1, 0),
        OverlayDockSide.Right => new Vector(1, 0),
        _ => new Vector(0, _appliedTopEdge ? -1 : 1)
    };

    private static int DockDepth(RobloxWindowRect bounds) => CurrentSide == OverlayDockSide.None ? bounds.Height : bounds.Width;

    private void ArrangeDeck(OverlayDockSide side, bool top)
    {
        bool vertical = side != OverlayDockSide.None;
        bool commandsFirst = vertical ? side == OverlayDockSide.Left : top;
        GridLength cardsLength = new(1, GridUnitType.Star);
        DockRoot.RowDefinitions.Clear();
        DockRoot.ColumnDefinitions.Clear();
        Grid.SetColumn(DockCommands, vertical && !commandsFirst ? 1 : 0);
        Grid.SetColumn(DeckHost, vertical && commandsFirst ? 1 : 0);
        Grid.SetRow(DockCommands, !vertical && !commandsFirst ? 1 : 0);
        Grid.SetRow(DeckHost, !vertical && commandsFirst ? 1 : 0);
        if (vertical)
        {
            DockRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = commandsFirst ? GridLength.Auto : cardsLength });
            DockRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = commandsFirst ? cardsLength : GridLength.Auto });
        }
        else
        {
            DockRoot.RowDefinitions.Add(new RowDefinition { Height = commandsFirst ? GridLength.Auto : cardsLength });
            DockRoot.RowDefinitions.Add(new RowDefinition { Height = commandsFirst ? cardsLength : GridLength.Auto });
        }

        Vector direction = DockEdgeDirection;
        DockEdgeFade.StartPoint = new Point(0.5 + direction.X / 2, 0.5 + direction.Y / 2);
        DockEdgeFade.EndPoint = new Point(0.5 - direction.X / 2, 0.5 - direction.Y / 2);
        DeckHost.Margin = side switch
        {
            OverlayDockSide.Left => new Thickness(8, 12, 0, 12),
            OverlayDockSide.Right => new Thickness(0, 12, 8, 12),
            _ => top ? new Thickness(16, 8, 16, 0) : new Thickness(16, 0, 16, 8)
        };
        DeckCards.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        DeckCards.HorizontalAlignment = vertical
            ? (commandsFirst ? HorizontalAlignment.Left : HorizontalAlignment.Right)
            : HorizontalAlignment.Center;
        DeckCards.VerticalAlignment = vertical
            ? VerticalAlignment.Center
            : (commandsFirst ? VerticalAlignment.Top : VerticalAlignment.Bottom);
        DeckScroll.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Hidden;
        DeckScroll.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Disabled;
        DeckScroll.PanningMode = vertical ? PanningMode.VerticalOnly : PanningMode.HorizontalOnly;
        DeckScroll.ScrollToHome();
    }

    private bool FitDeckAndCommands(bool sideways)
    {
        OverlayLayoutSettings layout = App.Settings.Prop.OverlayLayout ?? new OverlayLayoutSettings();
        RobloxWindowRect bounds = RobloxWindowTracker.Current;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _availableWidth = bounds.Valid ? DockExtent(bounds) : 0;
        _availableDepth = bounds.Valid ? DockDepth(bounds) : 0;
        double along = _availableWidth > 0
            ? _availableWidth / (PortableOverlay.Active ? 1 : sideways ? dpi.DpiScaleY : dpi.DpiScaleX)
            : 1060;
        double across = _availableDepth > 0
            ? _availableDepth / (PortableOverlay.Active ? 1 : sideways ? dpi.DpiScaleX : dpi.DpiScaleY)
            : 600;
        double inset = Math.Min(layout.SafeDockEdgeSpacing, across * 0.12);
        double limit = Math.Min(across, Math.Max(118, across * 0.56));
        double cardDepth = sideways ? 202 : 222;
        const double cardClearance = 18;
        double scale = Math.Min(layout.SafeDockScale, Math.Max(0.04, (limit - inset - cardClearance) / (DockHeight + cardDepth)));
        _deckScale = scale;
        SetPillScale(scale);
        Pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double sideMargin = Math.Min(12, along * 0.08);
        double usable = Math.Max(1, along - sideMargin * 2);
        double measured = PillLength(sideways);
        double commandLength = Math.Max(1, Math.Min(measured, Math.Min(usable, MaxDockWidth * scale)));
        if (measured > commandLength)
            SetPillScale(scale * commandLength / measured);
        double lead = sideMargin + (usable - commandLength) * (layout.SafeDockHorizontal / 100);
        DockCommands.Height = sideways ? commandLength : double.NaN;
        DockCommands.Width = sideways ? double.NaN : commandLength;
        DockCommands.Margin = sideways ? new Thickness(0, lead, 0, 0) : new Thickness(lead, 0, 0, 0);
        DockRoot.Margin = VisualSide switch
        {
            OverlayDockSide.Left => new Thickness(inset, 0, 0, 0),
            OverlayDockSide.Right => new Thickness(0, 0, inset, 0),
            _ => _appliedTopEdge ? new Thickness(0, inset, 0, 0) : new Thickness(0, 0, 0, inset)
        };
        foreach (ExperienceCard card in _experienceCards)
            SizeExperienceCard(card, sideways);
        double depth = Math.Min(across, Math.Ceiling((DockHeight + cardDepth) * scale + inset + cardClearance));
        double width = sideways ? depth : along;
        double height = sideways ? along : depth;
        bool changed = Math.Abs(Width - width) > 0.1 || Math.Abs(Height - height) > 0.1;
        SetCurrentValue(WidthProperty, width);
        SetCurrentValue(HeightProperty, height);
        return changed;
    }

    private void SetDeckGame(ActivityData game, string name, ImageSource? icon)
    {
        if (game.PlaceId <= 0)
            return;
        if (_deckPlaceId == game.PlaceId && _experienceCards.Count > 0)
        {
            ExperienceCard current = _experienceCards[0];
            SetText(current.Title, name);
            AutomationProperties.SetName(current.Button, name);
            if (icon != null && !ReferenceEquals(current.Icon.Source, icon))
                UpdateExperienceCardImage(current, icon, false);
            return;
        }

        _deckRequest?.Cancel();
        _deckPlaceId = game.PlaceId;
        DeckCards.Children.Clear();
        _experienceCards.Clear();
        DeckScroll.ScrollToHome();
        GameLaunchStatus.Visibility = Visibility.Collapsed;
        ExperienceCard featured = AddExperienceCard(name, 0, true);
        if (icon != null)
            UpdateExperienceCardImage(featured, icon, false);
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _deckRequest = request;
        _ = PortableOverlay.RunAsync(this, () => LoadDeckAsync(game, featured, request));
    }

    private async Task LoadDeckAsync(ActivityData current, ExperienceCard featured, CancellationTokenSource request)
    {
        const string LOG_IDENT = "SessionDock::LoadDeckAsync";
        CancellationToken token = request.Token;
        try
        {
            List<ActivityData> recent = await SessionGameBrowser.LoadRecentEntriesAsync(_activity, DeckCardLimit * 2, token);
            try
            {
                await UniverseDetails.FetchForEntriesAsync(recent.Prepend(current), token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not refresh recent game details: " + ex.Message);
            }
            token.ThrowIfCancellationRequested();
            if (current.UniverseDetails?.Data?.Name is { Length: > 0 } currentName)
            {
                SetText(featured.Title, currentName);
                AutomationProperties.SetName(featured.Button, currentName);
            }
            List<(ActivityData Game, ExperienceCard Card)> cards = [(current, featured)];
            foreach (ActivityData game in SelectRecentCardGames(current, recent))
            {
                long placeId = game.UniverseDetails?.Data?.RootPlaceId is > 0 and long root ? root : game.PlaceId;
                cards.Add((game, AddExperienceCard(game.GameName, placeId, false)));
            }

            Dictionary<long, string> artwork = new();
            try
            {
                artwork = await UniverseDetails.FetchArtworkUrlsAsync(cards.Select(item => GetUniverseId(item.Game)), token);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not load game covers: " + ex.Message);
            }
            await Task.WhenAll(cards.Select(item => LoadExperienceCardImagesAsync(item.Game, item.Card, artwork, token)));
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not load the game cards: " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_deckRequest, request))
                _deckRequest = null;
            request.Dispose();
        }
    }

    private static long GetUniverseId(ActivityData game) => game.UniverseDetails?.Data?.Id ?? game.UniverseId;

    private static IEnumerable<ActivityData> SelectRecentCardGames(ActivityData current, IEnumerable<ActivityData> recent)
    {
        HashSet<long> universes = new() { GetUniverseId(current) };
        return recent.Where(game => game.PlaceId > 0 && game.PlaceId != current.PlaceId)
            .Where(game => GetUniverseId(game) is long universe && (universe <= 0 || universes.Add(universe)))
            .Take(DeckCardLimit - 1);
    }

    private ExperienceCard AddExperienceCard(string name, long placeId, bool featured)
    {
        CornerRadius radius = new(14);
        Border artwork = new() { CornerRadius = radius };
        Grid content = new();
        content.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(26, 31, 44)), CornerRadius = radius });
        content.Children.Add(artwork);
        content.Children.Add(new Border { Background = (Brush)FindResource("GameCardShade"), CornerRadius = radius });
        Image icon = new()
        {
            Width = 30,
            Height = 30,
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 12, 0, 0),
            Clip = new RectangleGeometry(new Rect(0, 0, 30, 30), 6, 6)
        };
        content.Children.Add(icon);

        StackPanel labels = new() { Margin = new Thickness(12, 0, 12, 12), VerticalAlignment = VerticalAlignment.Bottom };
        labels.Children.Add(new TextBlock
        {
            Text = featured ? Strings.Ui_Misc2_NowPlaying : Strings.ContextMenu_GameBrowser_Continue,
            Foreground = new SolidColorBrush(Color.FromRgb(218, 226, 244)),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        TextBlock title = new()
        {
            Text = name,
            Foreground = Brushes.White,
            FontSize = featured ? 22 : 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = featured ? 57 : 42
        };
        labels.Children.Add(title);
        content.Children.Add(labels);

        Button button = new()
        {
            Style = (Style)FindResource("ExperienceCardButton"),
            Content = content,
            Tag = placeId,
            ToolTip = featured ? Strings.Ui_Session_GameAndServer : name,
            RenderTransform = new ScaleTransform(1, 1)
        };
        AutomationProperties.SetName(button, name);
        button.Click += ExperienceCard_Click;
        button.MouseEnter += ExperienceCardMouseEnter;
        button.MouseLeave += ExperienceCardMouseLeave;
        button.IsKeyboardFocusWithinChanged += ExperienceCardFocusChanged;
        ExperienceCard card = new(button, artwork, icon, title, featured);
        _experienceCards.Add(card);
        DeckCards.Children.Add(button);
        SizeExperienceCard(card, VisualSide != OverlayDockSide.None);
        if (_deckShown && !DockReducedMotion)
        {
            button.BeginAnimation(OpacityProperty, DeckAnimation(0.55, 1, CardHoverLength, EasingMode.EaseOut));
            Wpf.Ui.Animations.RenderReady.Hold(this, CardHoverLength);
        }
        return card;
    }

    private async Task LoadExperienceCardImagesAsync(
        ActivityData game, ExperienceCard card, Dictionary<long, string> artwork, CancellationToken token
    )
    {
        const string LOG_IDENT = "SessionDock::LoadExperienceCardImagesAsync";
        async Task LoadImageAsync(string? url, int width, bool cover)
        {
            try
            {
                BitmapSource? image = await SafeImaging.FromHttpAsync(url, width, token);
                if (!token.IsCancellationRequested && image != null)
                    UpdateExperienceCardImage(card, image, cover);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not load game artwork: " + ex.Message);
            }
        }

        string? coverUrl = artwork.GetValueOrDefault(GetUniverseId(game));
        Task icon = card.Icon.Source != null ? Task.CompletedTask : LoadImageAsync(game.UniverseDetails?.Thumbnail?.ImageUrl, 128, false);
        await Task.WhenAll(icon, LoadImageAsync(coverUrl, 600, true));
    }

    private static void UpdateExperienceCardImage(ExperienceCard card, ImageSource image, bool cover)
    {
        if (!cover)
            card.Icon.Source = image;
        if (cover || !card.HasCover)
        {
            card.Artwork.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            DynamicRenderSystem.SetPortableBrushImage(card.Artwork, image);
            card.HasCover = cover;
        }
    }

    private void SizeExperienceCard(ExperienceCard card, bool sideways)
    {
        card.Button.Width = _deckScale * (sideways ? card.Featured ? 202 : 166 : card.Featured ? 220 : 158);
        card.Button.Height = _deckScale * (sideways ? card.Featured ? 158 : 120 : card.Featured ? 222 : 178);
        card.Button.Margin = sideways ? new Thickness(3, 5, 3, 5) : new Thickness(6, 0, 6, 0);
        card.Button.HorizontalAlignment = VisualSide == OverlayDockSide.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        card.Button.VerticalAlignment = _appliedTopEdge ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        card.Title.FontSize = Math.Max(9, _deckScale * (card.Featured ? 22 : 16));
        card.Title.MaxHeight = card.Title.FontSize * 2.6;
    }

    private void IntroduceGameCards(bool fadeBackdrop)
    {
        GameLaunchStatus.Visibility = Visibility.Collapsed;
        DeckScroll.ScrollToHome();
        Wpf.Ui.Animations.RenderReady.Run(this, () =>
        {
            if (_closed || !_opened)
                return;
            if (fadeBackdrop)
            {
                DockBackdrop.BeginAnimation(OpacityProperty, DockReducedMotion
                    ? null
                    : DeckAnimation(0, 1, EntranceLength, EasingMode.EaseOut));
                if (!DockReducedMotion)
                    Wpf.Ui.Animations.RenderReady.Hold(this, EntranceLength);
            }
            RefreshDeckVisibility();
        });
    }

    private bool HasOpeningPanel()
    {
        if (_popoutOnSettle || _panels.Any(entry => entry.Key != HubKey && entry.Value.IsRequested))
            return true;
        return !_pinnedRestored && App.State.Prop.SessionPanels?.Any(entry =>
            entry.Value is { Pinned: true }
            && PanelActions.TryGetValue(entry.Key, out string? action) && IsDockActionAvailable(action)) == true;
    }

    private void OnDeckWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshDeckVisibility();

    private void RefreshDeckVisibility()
    {
        if (_closed)
            return;
        bool covered = _screenDragDimOverride || _islandExpanded || PopoutOpen
            || _panels.Any(entry => entry.Key != HubKey && entry.Value.IsVisible)
            || (_deckOpening && HasOpeningPanel());
        SetDeckVisibility(_opened && IsVisible && !covered, true);
    }

    private void SetDeckVisibility(bool visible, bool animate)
    {
        if (_deckShown == visible && animate)
            return;
        _deckShown = visible;
        DeckScroll.IsHitTestVisible = visible;
        DeckScroll.IsEnabled = visible;
        if (!visible)
            GameLaunchStatus.Visibility = Visibility.Collapsed;

        Vector edge = DockEdgeDirection * DeckSlideDistance;
        Vector target = visible ? default : edge;
        double targetOpacity = visible ? 1 : 0;
        if (!animate || DockReducedMotion)
        {
            DeckCards.BeginAnimation(OpacityProperty, null);
            DeckMotion.BeginAnimation(TranslateTransform.XProperty, null);
            DeckMotion.BeginAnimation(TranslateTransform.YProperty, null);
            DeckCards.Opacity = targetOpacity;
            DeckMotion.X = target.X;
            DeckMotion.Y = target.Y;
            return;
        }

        double opacity = DeckCards.Opacity;
        Vector start = opacity <= 0.001 ? edge : new Vector(DeckMotion.X, DeckMotion.Y);
        TimeSpan duration = visible ? EntranceLength : DeckRetreatLength;
        EasingMode easing = visible ? EasingMode.EaseOut : EasingMode.EaseInOut;
        DeckCards.BeginAnimation(OpacityProperty, DeckAnimation(opacity, targetOpacity, duration, easing));
        DeckMotion.BeginAnimation(TranslateTransform.XProperty, DeckAnimation(start.X, target.X, duration, easing));
        DeckMotion.BeginAnimation(TranslateTransform.YProperty, DeckAnimation(start.Y, target.Y, duration, easing));
        if (visible && opacity <= 0.001)
        {
            for (int index = 0; index < _experienceCards.Count; index++)
                RevealExperienceCard(_experienceCards[index].Button, index);
        }
        Wpf.Ui.Animations.RenderReady.Hold(this, duration);
    }

    private static void RevealExperienceCard(Button button, int index)
    {
        TimeSpan delay = TimeSpan.FromMilliseconds(12 * index);
        double start = index == 0 ? 0.72 : 0.48;
        DoubleAnimationUsingKeyFrames animation = new() { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > TimeSpan.Zero)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(delay)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(EntranceLength),
            new SineEase { EasingMode = EasingMode.EaseOut }));
        button.BeginAnimation(OpacityProperty, animation);
    }

    private static DoubleAnimation DeckAnimation(double from, double to, TimeSpan duration, EasingMode easing) =>
        new(from, to, duration) { EasingFunction = new CubicEase { EasingMode = easing } };

    private void ExperienceCardMouseEnter(object sender, MouseEventArgs e) => UpdateExperienceCardHover((Button)sender, true);

    private void ExperienceCardMouseLeave(object sender, MouseEventArgs e)
    {
        Button button = (Button)sender;
        UpdateExperienceCardHover(button, button.IsKeyboardFocusWithin);
    }

    private void ExperienceCardFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Button button = (Button)sender;
        UpdateExperienceCardHover(button, button.IsMouseOver || (bool)e.NewValue);
    }

    private void UpdateExperienceCardHover(Button button, bool highlighted)
    {
        bool raised = _deckShown && highlighted;
        Panel.SetZIndex(button, raised ? 1 : 0);
        ScaleTransform scale = (ScaleTransform)button.RenderTransform;
        bool animate = _deckShown && !DockReducedMotion;
        DoubleAnimation? motion = animate ? DeckAnimation(scale.ScaleX, raised ? 1.03 : 1, CardHoverLength, EasingMode.EaseOut) : null;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, motion);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, motion);
        if (animate)
            Wpf.Ui.Animations.RenderReady.Hold(this, CardHoverLength);
    }

    private void DeckScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (VisualSide != OverlayDockSide.None || DeckScroll.ScrollableWidth <= 0)
            return;
        DeckScroll.ScrollToHorizontalOffset(DeckScroll.HorizontalOffset - e.Delta * 1.35);
        e.Handled = true;
    }

    private void DockBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HidePopout();
        OnDimmerClicked();
    }

    private void ExperienceCard_Click(object sender, RoutedEventArgs e)
    {
        if (!_opened || !_deckShown || _closed || sender is not Button { Tag: long placeId })
            return;
        if (placeId == 0)
            ToggleServerPanel();
        else if (!_launchingDeckGame)
            _ = PortableOverlay.RunAsync(this, () => LaunchDeckGameAsync(placeId));
    }

    private async Task LaunchDeckGameAsync(long placeId)
    {
        const string LOG_IDENT = "SessionDock::LaunchDeckGameAsync";
        _launchingDeckGame = true;
        GameLaunchStatus.Visibility = Visibility.Collapsed;
        bool launched = false;
        try
        {
            launched = await SessionGameBrowser.LaunchExperienceAsync(_activity, _gameMatchmaker, placeId);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The selected game could not be opened: " + ex.Message);
        }
        finally
        {
            _launchingDeckGame = false;
        }
        if (_opened && !_closed)
        {
            if (launched)
                CloseDock();
            else if (_deckShown)
                GameLaunchStatus.Visibility = Visibility.Visible;
        }
    }
}
