using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Voidstrap.Models.Persistable;
using Voidstrap.UI;

namespace Voidstrap.UI.Elements.Overlay;

public partial class SessionNotificationSettings : Window
{
    private const double PreviewScale = 0.5;
    private static readonly string[] MotionNames = { "Slide", "Fade", "Pop", "None" };
    private NotificationAppearance _appearance = new();
    // Sliders raise ValueChanged while the XAML is still being loaded, before the other controls exist
    private bool _ready;
    private bool _loading;
    private bool _closed;
    private int _playGeneration;

    public SessionNotificationSettings(ImageSource? gameIcon, string? gameName)
    {
        InitializeComponent();
        _appearance = NotificationStyle.Current.Copy();
        PreviewBackground.ImageSource = gameIcon;
        MockIcon.Source = gameIcon;
        MockIcon.Visibility = gameIcon == null ? Visibility.Collapsed : Visibility.Visible;
        MockTitle.Text = string.IsNullOrWhiteSpace(gameName) ? "Roblox" : gameName;
        foreach (string name in MotionNames)
        {
            IntroBox.Items.Add(name);
            OutroBox.Items.Add(name);
        }
        _ready = true;
        LoadControls();
        Closed += (_, _) => _closed = true;
    }

    private void LoadControls()
    {
        if (!_ready)
            return;
        _loading = true;
        IntroBox.SelectedIndex = (int)_appearance.Intro;
        OutroBox.SelectedIndex = (int)_appearance.Outro;
        IntroSpeed.Value = _appearance.SafeIntroMilliseconds;
        OutroSpeed.Value = _appearance.SafeOutroMilliseconds;
        CornerSlider.Value = _appearance.SafeCornerRadius;
        SpacingSlider.Value = _appearance.SafeEdgeSpacing;
        WidthSlider.Value = _appearance.SafeWidth;
        OpacitySlider.Value = Math.Round(_appearance.SafeBackgroundOpacity * 100);
        SecondsSlider.Value = _appearance.SafeSecondsOnScreen;
        PauseToggle.IsChecked = _appearance.PauseWhileHovered;
        CloseToggle.IsChecked = _appearance.CloseButtonOnHover;
        _loading = false;
        UpdateValueTexts();
        UpdatePositionButtons();
        ApplyPreview();
    }

    // Every change is saved straight away and shows on the next notification
    private void Commit()
    {
        if (!_ready || _loading || _closed)
            return;
        App.Settings.Prop.NotificationAppearance = _appearance.Copy();
        App.Settings.SaveDeferred();
        UpdateValueTexts();
        ApplyPreview();
    }

    private void Position_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse(tag, out NotificationPosition position))
            return;
        _appearance.Position = position;
        UpdatePositionButtons();
        Commit();
        _ = PlayAsync();
    }

    private void Motion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading)
            return;
        if (IntroBox.SelectedIndex >= 0)
            _appearance.Intro = (NotificationMotion)IntroBox.SelectedIndex;
        if (OutroBox.SelectedIndex >= 0)
            _appearance.Outro = (NotificationMotion)OutroBox.SelectedIndex;
        Commit();
        _ = PlayAsync();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _loading)
            return;
        _appearance.IntroMilliseconds = (int)IntroSpeed.Value;
        _appearance.OutroMilliseconds = (int)OutroSpeed.Value;
        _appearance.CornerRadius = CornerSlider.Value;
        _appearance.EdgeSpacing = SpacingSlider.Value;
        _appearance.Width = WidthSlider.Value;
        _appearance.BackgroundOpacity = OpacitySlider.Value / 100;
        _appearance.SecondsOnScreen = SecondsSlider.Value;
        Commit();
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready)
            return;
        _appearance.PauseWhileHovered = PauseToggle.IsChecked == true;
        _appearance.CloseButtonOnHover = CloseToggle.IsChecked == true;
        Commit();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _appearance = new NotificationAppearance();
        LoadControls();
        _loading = false;
        Commit();
        _ = PlayAsync();
    }

    private void UpdateValueTexts()
    {
        IntroSpeedText.Text = _appearance.SafeIntroMilliseconds.ToString(CultureInfo.CurrentCulture) + " ms";
        OutroSpeedText.Text = _appearance.SafeOutroMilliseconds.ToString(CultureInfo.CurrentCulture) + " ms";
        CornerText.Text = _appearance.SafeCornerRadius.ToString("0", CultureInfo.CurrentCulture) + " px";
        SpacingText.Text = _appearance.SafeEdgeSpacing.ToString("0", CultureInfo.CurrentCulture) + " px";
        WidthText.Text = _appearance.SafeWidth.ToString("0", CultureInfo.CurrentCulture) + " px";
        OpacityText.Text = Math.Round(_appearance.SafeBackgroundOpacity * 100).ToString(CultureInfo.CurrentCulture) + "%";
        SecondsText.Text = _appearance.SafeSecondsOnScreen.ToString("0", CultureInfo.CurrentCulture) + " s";
        bool intro = _appearance.Intro != NotificationMotion.None;
        bool outro = _appearance.Outro != NotificationMotion.None;
        IntroSpeed.IsEnabled = intro;
        OutroSpeed.IsEnabled = outro;
    }

    private void UpdatePositionButtons()
    {
        foreach (object child in PositionGrid.Children)
        {
            if (child is Wpf.Ui.Controls.Button button)
                button.Appearance = string.Equals(button.Tag as string, _appearance.Position.ToString(), StringComparison.Ordinal)
                    ? Wpf.Ui.Common.ControlAppearance.Primary
                    : Wpf.Ui.Common.ControlAppearance.Secondary;
        }
    }

    // The preview uses the real notification's corners, borders, background and placement at half size
    private void ApplyPreview()
    {
        if (_closed)
            return;
        MockCard.Width = _appearance.SafeWidth;
        MockCard.CornerRadius = NotificationStyle.Corners(_appearance);
        MockCard.BorderThickness = NotificationStyle.Borders(_appearance);
        MockCard.Background = NotificationStyle.Background(this, _appearance);
        PlaceMock();
    }

    private void PlaceMock()
    {
        double stageWidth = PreviewStage.ActualWidth;
        double stageHeight = PreviewStage.ActualHeight;
        if (stageWidth <= 0 || stageHeight <= 0)
            return;
        MockHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = MockHost.DesiredSize;
        double spacing = _appearance.SafeEdgeSpacing * PreviewScale;
        double left = _appearance.IsLeft ? spacing
            : _appearance.IsRight ? stageWidth - size.Width - spacing
            : (stageWidth - size.Width) / 2;
        double top = _appearance.IsTop ? spacing : stageHeight - size.Height - spacing;
        Canvas.SetLeft(MockHost, Math.Round(left));
        Canvas.SetTop(MockHost, Math.Round(top));
    }

    private void PreviewStage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_ready)
            PlaceMock();
    }

    private void Preview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => _ = PlayAsync();

    // Plays the entrance, holds briefly, plays the exit, then brings the card back so it stays visible
    private async Task PlayAsync()
    {
        int generation = ++_playGeneration;
        try
        {
            MockCard.UpdateLayout();
            double height = Math.Max(1, MockCard.ActualHeight);
            NotificationStyle.PrepareIntro(MockCard, MockTranslate, MockScale, _appearance, height);
            NotificationStyle.Play(MockCard, MockTranslate, MockScale, _appearance, height, true);
            await Task.Delay(NotificationStyle.Length(_appearance, true) + 1100);
            if (_closed || generation != _playGeneration)
                return;
            NotificationStyle.Play(MockCard, MockTranslate, MockScale, _appearance, height, false);
            await Task.Delay(NotificationStyle.Length(_appearance, false) + 450);
            if (_closed || generation != _playGeneration)
                return;
            NotificationStyle.Stop(MockCard, MockTranslate, MockScale);
            MockCard.Opacity = 1;
            MockTranslate.Y = 0;
            MockScale.ScaleX = MockScale.ScaleY = 1;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SessionNotificationSettings", "The preview could not play: " + ex.Message);
        }
    }
}
