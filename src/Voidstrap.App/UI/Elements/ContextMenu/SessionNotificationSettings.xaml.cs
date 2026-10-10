using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Models.Persistable;
using Voidstrap.UI;
using Voidstrap.UI.Elements.ContextMenu;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionNotificationSettings : Window
{
    private const double PreviewScale = 0.5;
    private static readonly string[] MotionNames = { "Slide in from the edge", "Fade in and out", "Pop", "None" };
    private const string ShortcutHintText = "The key combination that opens the overlay.";
    private readonly Action? _sendTest;
    private NotificationAppearance _appearance = new();
    // Sliders raise ValueChanged while the XAML is still being loaded, before the other controls exist
    private bool _ready;
    private bool _loading;
    private bool _closed;
    private bool _recording;
    private int _playGeneration;
    private bool _playedOnOpen;
    private readonly DispatcherTimer _replay;

    public SessionNotificationSettings(ImageSource? gameIcon, string? gameName, Action? sendTest = null)
    {
        _sendTest = sendTest;
        InitializeComponent();
        _appearance = NotificationStyle.Current.Copy();
        PreviewBackground.ImageSource = gameIcon;
        MockIcon.Source = gameIcon;
        MockIcon.Visibility = gameIcon == null ? Visibility.Collapsed : Visibility.Visible;
        foreach (string name in MotionNames)
            MotionBox.Items.Add(name);
        // Replays the preview once the position sliders have settled, not on every step of a drag
        _replay = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(350) };
        _replay.Tick += (_, _) =>
        {
            _replay.Stop();
            _ = PlayAsync();
        };
        _ready = true;
        LoadControls();
        ShowShortcut();
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _closed = true;
            _replay.Stop();
            _playGeneration++;
            StopRecording(false);
        };
    }

    // Plays once when the panel opens so the current animation is shown straight away
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_playedOnOpen || _closed)
            return;
        _playedOnOpen = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            PlaceMock();
            _ = PlayAsync();
        }));
    }

    private void LoadControls()
    {
        if (!_ready)
            return;
        _loading = true;
        CornerLayout.IsChecked = _appearance.Layout == NotificationLayout.Corner;
        FloatingLayout.IsChecked = _appearance.Layout == NotificationLayout.Floating;
        HorizontalSlider.Value = _appearance.SafeHorizontal;
        VerticalSlider.Value = _appearance.SafeVertical;
        CornerSlider.Value = _appearance.SafeCornerRadius;
        SizeSlider.Value = Math.Round(_appearance.SafeScale * 100);
        TextSizeSlider.Value = Math.Round(_appearance.SafeTextScale * 100);
        OpacitySlider.Value = Math.Round(_appearance.SafeBackgroundOpacity * 100);
        SecondsSlider.Value = _appearance.SafeSecondsOnScreen;
        MotionBox.SelectedIndex = (int)_appearance.Intro;
        HeaderOff.IsChecked = _appearance.Header == NotificationHeader.Off;
        HeaderServer.IsChecked = _appearance.Header == NotificationHeader.Server;
        HeaderFriends.IsChecked = _appearance.Header == NotificationHeader.Friends;
        HeaderBoth.IsChecked = _appearance.Header == NotificationHeader.Both;
        PauseToggle.IsChecked = _appearance.PauseWhileHovered;
        CloseToggle.IsChecked = _appearance.CloseButtonOnHover;
        ServerToggle.IsChecked = App.Settings.Prop.ServerDetailsInOverlay;
        FriendsToggle.IsChecked = App.Settings.Prop.NotifyFriends;
        BadgesToggle.IsChecked = App.Settings.Prop.NotifyBadges;
        ShortcutToggle.IsChecked = App.Settings.Prop.NotifyShortcutEveryGame;
        _loading = false;
        UpdateValueTexts();
        ApplyPreview();
    }

    // Every change is saved straight away and shows on the next notification
    private void Commit()
    {
        if (!_ready || _loading || _closed)
            return;
        _appearance.PositionMigrated = true;
        App.Settings.Prop.NotificationAppearance = _appearance.Copy();
        App.Settings.SaveDeferred();
        UpdateValueTexts();
        ApplyPreview();
    }

    private void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        _appearance.Layout = FloatingLayout.IsChecked == true ? NotificationLayout.Floating : NotificationLayout.Corner;
        Commit();
        _ = PlayAsync();
    }

    private void Header_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading || sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out NotificationHeader header))
            return;
        _appearance.Header = header;
        Commit();
    }

    private void Motion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading || MotionBox.SelectedIndex < 0)
            return;
        // One choice for both ways, it enters and leaves the same way
        _appearance.Intro = _appearance.Outro = (NotificationMotion)MotionBox.SelectedIndex;
        Commit();
        _ = PlayAsync();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _loading)
            return;
        _appearance.Horizontal = HorizontalSlider.Value;
        _appearance.Vertical = VerticalSlider.Value;
        _appearance.CornerRadius = CornerSlider.Value;
        _appearance.Size = SizeSlider.Value;
        _appearance.TextSize = TextSizeSlider.Value;
        _appearance.BackgroundOpacity = OpacitySlider.Value / 100;
        _appearance.SecondsOnScreen = SecondsSlider.Value;
        Commit();
        if (ReferenceEquals(sender, HorizontalSlider) || ReferenceEquals(sender, VerticalSlider))
        {
            _replay.Stop();
            _replay.Start();
        }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        _appearance.PauseWhileHovered = PauseToggle.IsChecked == true;
        _appearance.CloseButtonOnHover = CloseToggle.IsChecked == true;
        App.Settings.Prop.ServerDetailsInOverlay = ServerToggle.IsChecked == true;
        App.Settings.Prop.NotifyFriends = FriendsToggle.IsChecked == true;
        App.Settings.Prop.NotifyBadges = BadgesToggle.IsChecked == true;
        App.Settings.Prop.NotifyShortcutEveryGame = ShortcutToggle.IsChecked == true;
        Commit();
    }

    private void SendTest_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _sendTest?.Invoke();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionNotificationSettings", "The test notification could not be sent: " + ex.Message);
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;
        _appearance = new NotificationAppearance { PositionMigrated = true };
        App.Settings.Prop.ServerDetailsInOverlay = true;
        App.Settings.Prop.NotifyFriends = true;
        App.Settings.Prop.NotifyBadges = true;
        App.Settings.Prop.NotifyShortcutEveryGame = false;
        LoadControls();
        Commit();
        _ = PlayAsync();
    }

    private void UpdateValueTexts()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        HorizontalText.Text = _appearance.SafeHorizontal.ToString("0", culture) + "%";
        VerticalText.Text = _appearance.SafeVertical.ToString("0", culture) + "%";
        CornerText.Text = _appearance.SafeCornerRadius.ToString("0", culture) + " px";
        SizeText.Text = (_appearance.SafeScale * 100).ToString("0", culture) + "%";
        TextSizeText.Text = (_appearance.SafeTextScale * 100).ToString("0", culture) + "%";
        OpacityText.Text = Math.Round(_appearance.SafeBackgroundOpacity * 100).ToString(culture) + "%";
        SecondsText.Text = _appearance.SafeSecondsOnScreen.ToString("0", culture) + " s";
    }

    // The preview uses the real notification's corners, borders, background, size and placement at half scale
    private void ApplyPreview()
    {
        if (_closed)
            return;
        MockCard.Width = NotificationAppearance.BaseWidth;
        double scale = _appearance.SafeScale;
        MockCard.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        MockCard.CornerRadius = NotificationStyle.Corners(_appearance);
        MockCard.BorderThickness = NotificationStyle.Borders(_appearance);
        MockCard.Background = NotificationStyle.Background(this, _appearance);
        NotificationStyle.FillShadow(MockShadow, MockCard.CornerRadius);
        double text = _appearance.SafeTextScale;
        MockHeaderText.FontSize = 11 * text;
        MockTitle.FontSize = 14 * text;
        MockText.FontSize = 12 * text;
        MockText.MaxHeight = 64 * text;
        // The test notification counts as a server notification
        MockHeader.Visibility = _appearance.ShowsHeader(false) ? Visibility.Visible : Visibility.Collapsed;
        // The same shadow room the real notification window has around its card
        MockHost.Padding = NotificationStyle.ShadowMargins(_appearance);
        PlaceMock();
    }

    private void PlaceMock()
    {
        double stageWidth = PreviewStage.ActualWidth;
        double stageHeight = PreviewStage.ActualHeight;
        if (stageWidth <= 0 || stageHeight <= 0)
            return;
        // A changed size only marks the card dirty, without this the host reports the previous size
        MockCard.InvalidateMeasure();
        MockLayer.InvalidateMeasure();
        MockHost.InvalidateMeasure();
        MockHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = MockHost.DesiredSize;
        Thickness pad = MockHost.Padding;
        double cardWidth = size.Width - (pad.Left + pad.Right) * PreviewScale;
        double cardHeight = size.Height - (pad.Top + pad.Bottom) * PreviewScale;
        // Placed by the card like the real window, then pushed out by the shadow room around it
        Point spot = NotificationStyle.Place(_appearance, stageWidth, stageHeight, cardWidth, cardHeight, _appearance.SafeEdgeSpacing * PreviewScale);
        Canvas.SetLeft(MockHost, Math.Round(spot.X - pad.Left * PreviewScale));
        Canvas.SetTop(MockHost, Math.Round(spot.Y - pad.Top * PreviewScale));
    }

    private void PreviewClipHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RectangleGeometry clip = new(new Rect(e.NewSize), 8, 8);
        clip.Freeze();
        PreviewClipHost.Clip = clip;
    }

    private void PreviewStage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready)
            PlaceMock();
    }

    private void Preview_Click(object sender, MouseButtonEventArgs e)
    {
        _replay.Stop();
        _ = PlayAsync();
    }

    // Plays the entrance, holds briefly, plays the exit, then brings the card back so it stays visible
    private async Task PlayAsync()
    {
        if (!_ready || _closed)
            return;
        int generation = ++_playGeneration;
        try
        {
            PlaceMock();
            MockCard.UpdateLayout();
            Size travel = MockHost.RenderSize;
            // Drawn once into a bitmap while it moves, like the real notification, so the small text does not shimmer
            MockLayer.CacheMode ??= new BitmapCache { SnapsToDevicePixels = true };
            NotificationStyle.PrepareIntro(MockLayer, MockTranslate, MockScale, _appearance, travel);
            NotificationStyle.Play(MockLayer, MockTranslate, MockScale, _appearance, travel, true);
            await Task.Delay(NotificationStyle.Length(_appearance, true) + 1100);
            if (_closed || generation != _playGeneration)
                return;
            NotificationStyle.Play(MockLayer, MockTranslate, MockScale, _appearance, travel, false);
            await Task.Delay(NotificationStyle.Length(_appearance, false) + 450);
            if (_closed || generation != _playGeneration)
                return;
            // Comes back the same way it enters, a card that just pops back in looks like a glitch
            NotificationStyle.PrepareIntro(MockLayer, MockTranslate, MockScale, _appearance, travel);
            NotificationStyle.Play(MockLayer, MockTranslate, MockScale, _appearance, travel, true);
            await Task.Delay(NotificationStyle.Length(_appearance, true) + 50);
            if (_closed || generation != _playGeneration)
                return;
            RestMock();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionNotificationSettings", "The preview could not play: " + ex.Message);
            RestMock();
        }
    }

    // The resting state: fully shown, in place, and no longer cached so it stays sharp after a resize
    private void RestMock()
    {
        NotificationStyle.Stop(MockLayer, MockTranslate, MockScale);
        MockLayer.Opacity = 1;
        MockTranslate.X = 0;
        MockTranslate.Y = 0;
        MockScale.ScaleX = MockScale.ScaleY = 1;
        MockLayer.CacheMode = null;
    }

    // ---- Overlay shortcut ----

    private void ShowShortcut(string? hint = null)
    {
        OverlayShortcut shortcut = App.Settings.Prop.SessionDockShortcut ??= new OverlayShortcut();
        ShortcutText.Text = shortcut.IsSet ? shortcut.Describe() : "None";
        ShortcutClear.IsEnabled = shortcut.IsSet;
        ShortcutHint.Text = hint ?? (shortcut.IsSet ? ShortcutHintText : "No shortcut, set one to open the overlay again later.");
        ShortcutBox.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
    }

    private void ShortcutBox_Click(object sender, MouseButtonEventArgs e)
    {
        if (_recording)
            return;
        _recording = true;
        // The current shortcut is paused so pressing it here is recorded instead of closing the overlay
        MenuContainer.SuspendSessionDockHotkey(true);
        ShortcutText.Text = "Press a combination";
        ShortcutHint.Text = "Hold Ctrl or Alt and press a key. Esc cancels.";
        ShortcutBox.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        ShortcutBox.Focus();
        Keyboard.Focus(ShortcutBox);
    }

    private void ShortcutBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording)
            return;
        e.Handled = true;
        Key key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key
        };
        if (key == Key.Escape)
        {
            StopRecording(false);
            return;
        }
        ModifierKeys held = Keyboard.Modifiers;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            ShortcutText.Text = Describe(held) + "…";
            return;
        }
        uint modifiers = 0;
        if (held.HasFlag(ModifierKeys.Control))
            modifiers |= OverlayShortcut.Control;
        if (held.HasFlag(ModifierKeys.Alt))
            modifiers |= OverlayShortcut.Alt;
        if (held.HasFlag(ModifierKeys.Shift))
            modifiers |= OverlayShortcut.Shift;
        if (held.HasFlag(ModifierKeys.Windows))
            modifiers |= OverlayShortcut.Windows;
        uint virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        OverlayShortcut candidate = new() { Modifiers = modifiers, Key = virtualKey };
        if (!candidate.IsSet || virtualKey == 0)
        {
            ShortcutHint.Text = "Use Ctrl or Alt together with a key.";
            return;
        }
        if (!MenuContainer.TryShortcut(candidate))
        {
            ShortcutHint.Text = candidate.Describe() + " is already used by another app.";
            return;
        }
        App.Settings.Prop.SessionDockShortcut = candidate;
        App.Settings.SaveDeferred();
        StopRecording(true);
    }

    private static string Describe(ModifierKeys held)
    {
        string text = string.Empty;
        if (held.HasFlag(ModifierKeys.Control))
            text += "Ctrl + ";
        if (held.HasFlag(ModifierKeys.Alt))
            text += "Alt + ";
        if (held.HasFlag(ModifierKeys.Shift))
            text += "Shift + ";
        if (held.HasFlag(ModifierKeys.Windows))
            text += "Win + ";
        return text;
    }

    private void ShortcutBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => StopRecording(false);

    private void StopRecording(bool saved)
    {
        if (!_recording)
            return;
        _recording = false;
        // Registers whatever shortcut is saved now, the new one or the one from before
        MenuContainer.SuspendSessionDockHotkey(false);
        if (!_closed)
            ShowShortcut(saved ? "Saved. " + ShortcutHintText : null);
    }

    private void ShortcutClear_Click(object sender, RoutedEventArgs e)
    {
        StopRecording(false);
        App.Settings.Prop.SessionDockShortcut = new OverlayShortcut { Key = 0, Modifiers = 0 };
        App.Settings.SaveDeferred();
        MenuContainer.SyncSessionDockHotkey();
        ShowShortcut();
    }
}
