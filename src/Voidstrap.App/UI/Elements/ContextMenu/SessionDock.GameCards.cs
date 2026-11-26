using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Overlays;
using Voidstrap.UI.Elements.Dialogs;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionDock
{
    private const int DeckCardLimit = 8;
    private const double DockSizeMultiplier = 1.16;
    private const int CardStaggerMilliseconds = 36;
    private readonly List<ExperienceCard> _experienceCards = new();
    private readonly ServerMatchmaker? _gameMatchmaker;
    private CancellationTokenSource? _deckRequest;
    private long _deckPlaceId;
    private double _deckScale = 1;
    private bool _launchingDeckGame;
    private bool _deckOpening;
    private bool _deckShown;
    private long _nextCardEntrance;
    private Button? _deckHighlight;
    private Point? _deckPointer;
    private FluentMessageBox? _deckJoinDialog;
    private static readonly TimeSpan DeckRetreatLength = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan CardEntranceLength = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CardHoverLength = TimeSpan.FromMilliseconds(150);
    private const double DeckSlideDistance = 26;

    private sealed record ExperienceCard(Button Button, Border Artwork, Image Icon, TextBlock Title, bool IsCurrent)
    {
        public ScaleTransform Scale { get; } = new();
        public TranslateTransform Offset { get; } = new();
        public TranslateTransform Entrance { get; } = new();
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

    private bool FitDeckAndCommands(bool sideways, bool orientationChanged)
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
        double scale = Math.Min(layout.SafeDockScale * DockSizeMultiplier, Math.Max(0.04, (limit - inset - cardClearance) / (DockHeight + cardDepth)));
        bool resizeCards = orientationChanged || _deckScale != scale;
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
        if (resizeCards)
        {
            foreach (ExperienceCard card in _experienceCards)
            {
                ClearCardMotion(card);
                SizeExperienceCard(card, sideways);
            }
        }
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
        ClearExperienceCards();
        DeckScroll.ScrollToHome();
        GameLaunchStatus.Visibility = Visibility.Collapsed;
        ExperienceCard featured = AddExperienceCard(name, 0, true);
        if (icon != null)
            UpdateExperienceCardImage(featured, icon, false);
        CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _deckRequest = request;
        _ = PortableOverlay.RunAsync(this, () => LoadDeckAsync(game, featured, request));
    }

    private void ClearExperienceCards()
    {
        _deckHighlight = null;
        _deckPointer = null;
        _nextCardEntrance = 0;
        foreach (ExperienceCard card in _experienceCards)
        {
            card.Button.Click -= ExperienceCard_Click;
            card.Button.IsKeyboardFocusWithinChanged -= ExperienceCardFocusChanged;
        }
        DeckCards.Children.Clear();
        _experienceCards.Clear();
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
        TextBlock caption = new()
        {
            Foreground = new SolidColorBrush(Color.FromRgb(218, 226, 244)),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        BindDeckText(caption, TextBlock.TextProperty,
            featured ? nameof(Strings.Ui_Misc2_NowPlaying) : nameof(Strings.ContextMenu_GameBrowser_Continue));
        labels.Children.Add(caption);
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
            ToolTip = name
        };
        if (featured)
            BindDeckText(button, ToolTipProperty, nameof(Strings.Ui_Nav_News));
        AutomationProperties.SetName(button, name);
        button.Click += ExperienceCard_Click;
        button.IsKeyboardFocusWithinChanged += ExperienceCardFocusChanged;
        ExperienceCard card = new(button, artwork, icon, title, featured);
        button.RenderTransform = new TransformGroup { Children = { card.Scale, card.Offset, card.Entrance } };
        _experienceCards.Add(card);
        SizeExperienceCard(card, VisualSide != OverlayDockSide.None);
        DeckCards.Children.Add(button);
        if (_deckShown && !DockReducedMotion)
            Wpf.Ui.Animations.RenderReady.Hold(this, StartCardEntrance(card, replay: true));
        return card;
    }

    private static void BindDeckText(FrameworkElement element, DependencyProperty property, string key) =>
        element.SetBinding(property, new Binding($"[{key}]") { Source = LocalizedStrings.Instance, Mode = BindingMode.OneWay });

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
        bool expanded = _deckHighlight == null ? card.IsCurrent : ReferenceEquals(card.Button, _deckHighlight);
        double width = _deckScale * (sideways ? expanded ? 202 : 166 : expanded ? 220 : 158);
        double height = _deckScale * (sideways ? expanded ? 158 : 120 : expanded ? 222 : 178);
        double fontSize = Math.Max(9, _deckScale * (expanded ? 22 : 16));
        card.Title.MaxHeight = fontSize * 2.6;
        card.Button.Width = width;
        card.Button.Height = height;
        card.Title.FontSize = fontSize;
        card.Button.Margin = sideways ? new Thickness(3, 5, 3, 5) : new Thickness(6, 0, 6, 0);
        card.Button.HorizontalAlignment = VisualSide == OverlayDockSide.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        card.Button.VerticalAlignment = _appliedTopEdge ? VerticalAlignment.Top : VerticalAlignment.Bottom;
    }

    private void IntroduceGameCards()
    {
        GameLaunchStatus.Visibility = Visibility.Collapsed;
        DeckScroll.ScrollToHome();
        RefreshDeckVisibility();
    }

    private bool HasOpeningPanel()
    {
        if (_popoutOnSettle || _panels.Values.Any(panel => panel.IsRequested))
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
        bool covered = _screenDragDimOverride || _islandExpanded || _launchingDeckGame || PopoutOpen
            || _panels.Values.Any(panel => panel.IsVisible)
            || (_deckOpening && HasOpeningPanel());
        SetDeckVisibility(_opened && IsVisible && !covered, true);
    }

    private void SetDeckVisibility(bool visible, bool animate)
    {
        if (_deckShown == visible && animate)
            return;
        _deckShown = visible;
        if (!visible)
        {
            GameLaunchStatus.Visibility = Visibility.Collapsed;
            _deckPointer = null;
        }
        bool replay = visible && DeckCards.Opacity <= 0.001;
        if (replay)
            SelectDeckHighlight(null, false);
        if (visible)
            DeckScroll.Visibility = Visibility.Visible;
        DeckScroll.IsHitTestVisible = visible;
        DeckScroll.IsEnabled = visible;

        Vector edge = DockEdgeDirection * DeckSlideDistance;
        Vector target = visible ? default : edge;
        double targetOpacity = visible ? 1 : 0;
        _nextCardEntrance = 0;
        if (!animate || DockReducedMotion)
        {
            SetDeckPose(targetOpacity, target);
            foreach (ExperienceCard card in _experienceCards)
                SetCardEntrancePose(card, 1, default);
            if (!visible)
                DeckScroll.Visibility = Visibility.Collapsed;
            return;
        }

        TimeSpan duration = visible ? EntranceLength : DeckRetreatLength;
        if (replay)
            SetDeckPose(1, default);
        else
        {
            EasingMode easing = visible ? EasingMode.EaseOut : EasingMode.EaseInOut;
            DoubleAnimation fade = DeckAnimation(DeckCards.Opacity, targetOpacity, duration, easing);
            if (!visible)
                fade.Completed += OnDeckRetreatFinished;
            DeckCards.BeginAnimation(OpacityProperty, fade);
            DeckMotion.BeginAnimation(TranslateTransform.XProperty, DeckAnimation(DeckMotion.X, target.X, duration, easing));
            DeckMotion.BeginAnimation(TranslateTransform.YProperty, DeckAnimation(DeckMotion.Y, target.Y, duration, easing));
        }
        foreach (ExperienceCard card in _experienceCards)
        {
            if (!visible)
                SetCardEntrancePose(card, card.Button.Opacity, new Vector(card.Entrance.X, card.Entrance.Y));
            else
            {
                TimeSpan cardDuration = StartCardEntrance(card, replay);
                if (cardDuration > duration)
                    duration = cardDuration;
            }
        }
        Wpf.Ui.Animations.RenderReady.Hold(this, duration);
    }

    private void SetDeckPose(double opacity, Vector offset)
    {
        SetEntranceValue(DeckCards, OpacityProperty, opacity);
        SetEntranceValue(DeckMotion, TranslateTransform.XProperty, offset.X);
        SetEntranceValue(DeckMotion, TranslateTransform.YProperty, offset.Y);
    }

    private void OnDeckRetreatFinished(object? sender, EventArgs e)
    {
        if (sender is Clock clock)
            clock.Completed -= OnDeckRetreatFinished;
        if (!_deckShown && DeckCards.Opacity <= 0.001)
            DeckScroll.Visibility = Visibility.Collapsed;
    }

    private TimeSpan StartCardEntrance(ExperienceCard card, bool replay)
    {
        double opacity = replay ? 0 : card.Button.Opacity;
        Vector offset = replay ? DockEdgeDirection * DeckSlideDistance : new Vector(card.Entrance.X, card.Entrance.Y);
        if (opacity == 1 && offset == default)
            return TimeSpan.Zero;

        TimeSpan delay = TimeSpan.Zero;
        if (opacity <= 0.001)
        {
            long now = Environment.TickCount64;
            long start = Math.Max(now, _nextCardEntrance);
            _nextCardEntrance = start + CardStaggerMilliseconds;
            delay = TimeSpan.FromMilliseconds(start - now);
        }

        SetCardEntrancePose(card, opacity, offset);
        AnimateEntranceValue(card.Button, OpacityProperty, opacity, 1, delay);
        if (offset.X != 0)
            AnimateEntranceValue(card.Entrance, TranslateTransform.XProperty, offset.X, 0, delay);
        if (offset.Y != 0)
            AnimateEntranceValue(card.Entrance, TranslateTransform.YProperty, offset.Y, 0, delay);
        return delay + CardEntranceLength;
    }

    private static void SetCardEntrancePose(ExperienceCard card, double opacity, Vector offset)
    {
        SetEntranceValue(card.Button, OpacityProperty, opacity);
        SetEntranceValue(card.Entrance, TranslateTransform.XProperty, offset.X);
        SetEntranceValue(card.Entrance, TranslateTransform.YProperty, offset.Y);
    }

    private static void SetEntranceValue(DependencyObject element, DependencyProperty property, double value)
    {
        ((IAnimatable)element).BeginAnimation(property, null);
        element.SetValue(property, value);
    }

    private static void AnimateEntranceValue(IAnimatable element, DependencyProperty property, double from, double to, TimeSpan delay)
    {
        DoubleAnimation animation = DeckAnimation(from, to, CardEntranceLength, EasingMode.EaseOut);
        animation.BeginTime = delay;
        element.BeginAnimation(property, animation);
    }

    private static DoubleAnimation DeckAnimation(double from, double to, TimeSpan duration, EasingMode easing) =>
        new(from, to, duration) { EasingFunction = new CubicEase { EasingMode = easing } };

    private void DeckScroll_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_deckShown)
            return;
        Point position = e.GetPosition(DeckScroll);
        if (_deckPointer == position)
            return;
        _deckPointer = position;
        Button? hovered = _experienceCards.FirstOrDefault(card => card.Button.IsMouseOver)?.Button;
        SelectDeckHighlight(hovered ?? FocusedDeckCard());
    }

    private void DeckScroll_MouseLeave(object sender, MouseEventArgs e)
    {
        _deckPointer = null;
        if (_deckShown)
            SelectDeckHighlight(FocusedDeckCard());
    }

    private Button? FocusedDeckCard() =>
        _experienceCards.FirstOrDefault(card => card.Button.IsKeyboardFocusWithin)?.Button;

    private void ExperienceCardFocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_deckShown)
            return;
        if ((bool)e.NewValue)
            SelectDeckHighlight((Button)sender);
        else if (!DeckScroll.IsMouseOver && !DeckScroll.IsKeyboardFocusWithin)
            SelectDeckHighlight(null);
    }

    private void SelectDeckHighlight(Button? highlight, bool animate = true)
    {
        Button? current = _experienceCards.FirstOrDefault(card => card.IsCurrent)?.Button;
        Button? previous = _deckHighlight ?? current;
        Button? next = highlight ?? current;
        _deckHighlight = highlight;
        if (ReferenceEquals(previous, next))
            return;

        Rect[]? oldBounds = animate && !DockReducedMotion && DeckScroll.IsVisible
            ? _experienceCards.Select(DeckCardBounds).ToArray()
            : null;
        foreach (ExperienceCard card in _experienceCards)
        {
            ClearCardMotion(card);
            if (ReferenceEquals(card.Button, previous) || ReferenceEquals(card.Button, next))
                SizeExperienceCard(card, VisualSide != OverlayDockSide.None);
        }
        if (oldBounds == null)
            return;

        DeckCards.UpdateLayout();
        for (int index = 0; index < _experienceCards.Count; index++)
            AnimateCardMotion(_experienceCards[index], oldBounds[index], DeckCardBounds(_experienceCards[index]));
        Wpf.Ui.Animations.RenderReady.Hold(this, CardHoverLength);
    }

    private Rect DeckCardBounds(ExperienceCard card)
    {
        Rect bounds = card.Button.TransformToAncestor(DeckCards).TransformBounds(new Rect(card.Button.RenderSize));
        bounds.Offset(-card.Entrance.X, -card.Entrance.Y);
        return bounds;
    }

    private static void ClearCardMotion(ExperienceCard card)
    {
        card.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        card.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        card.Offset.BeginAnimation(TranslateTransform.XProperty, null);
        card.Offset.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private static void AnimateCardMotion(ExperienceCard card, Rect from, Rect to)
    {
        if (from.IsEmpty || to.IsEmpty || from.Width <= 0 || from.Height <= 0 || to.Width <= 0 || to.Height <= 0)
            return;
        AnimateCardTransform(card.Scale, ScaleTransform.ScaleXProperty, from.Width / to.Width, 1);
        AnimateCardTransform(card.Scale, ScaleTransform.ScaleYProperty, from.Height / to.Height, 1);
        AnimateCardTransform(card.Offset, TranslateTransform.XProperty, from.X - to.X, 0);
        AnimateCardTransform(card.Offset, TranslateTransform.YProperty, from.Y - to.Y, 0);
    }

    private static void AnimateCardTransform(Animatable transform, DependencyProperty property, double from, double to)
    {
        if (Math.Abs(from - to) < 0.001)
            return;
        DoubleAnimation animation = DeckAnimation(from, to, CardHoverLength, EasingMode.EaseOut);
        animation.FillBehavior = FillBehavior.Stop;
        transform.BeginAnimation(property, animation);
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
            OpenAction(HubKey);
        else if (!_launchingDeckGame)
            _ = PortableOverlay.RunAsync(this, () => LaunchDeckGameAsync(placeId));
    }

    private async Task LaunchDeckGameAsync(long placeId)
    {
        const string LOG_IDENT = "SessionDock::LaunchDeckGameAsync";
        _launchingDeckGame = true;
        long currentPlace = _deckPlaceId;
        GameLaunchStatus.Visibility = Visibility.Collapsed;
        RefreshDeckVisibility();
        bool? launched = null;
        try
        {
            if (!ConfirmDeckGameJoin() || !_opened || _closed || currentPlace != _deckPlaceId)
                return;
            launched = await SessionGameBrowser.LaunchExperienceAsync(_activity, _gameMatchmaker, placeId);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The selected game could not be opened: " + ex.Message);
            launched = false;
        }
        finally
        {
            _launchingDeckGame = false;
            if (launched == true && _opened && !_closed)
                CloseDock();
            else
            {
                RefreshDeckVisibility();
                if (launched == false && _deckShown)
                    GameLaunchStatus.Visibility = Visibility.Visible;
            }
        }
    }

    private bool ConfirmDeckGameJoin()
    {
        FluentMessageBox dialog = new(Strings.ContextMenu_GameBrowser_ConfirmJoin, MessageBoxImage.Question, MessageBoxButton.YesNo)
        {
            Owner = this,
            Topmost = true,
            ShowInTaskbar = false,
            Result = MessageBoxResult.No
        };
        dialog.ButtonTwo.IsCancel = true;
        _deckJoinDialog = dialog;
        UpdateNowPlayingVisibility();
        IntPtr handle = IntPtr.Zero;

        void RegisterDialog(object? sender, EventArgs e)
        {
            if (PortableOverlay.Active)
                PortableOverlay.Handle(dialog);
            else
            {
                handle = new WindowInteropHelper(dialog).Handle;
                OverlayDiagnostics.RegisterOverlayHandle(handle);
            }
        }

        dialog.SourceInitialized += RegisterDialog;
        dialog.Activated += RegisterDialog;
        try
        {
            dialog.ShowOwnedDialog();
            return dialog.Result == MessageBoxResult.Yes;
        }
        finally
        {
            dialog.SourceInitialized -= RegisterDialog;
            dialog.Activated -= RegisterDialog;
            if (PortableOverlay.Active)
                PortableOverlay.Release(dialog);
            else
                OverlayDiagnostics.UnregisterOverlayHandle(handle);
            _deckJoinDialog = null;
            UpdateNowPlayingVisibility();
        }
    }
}
