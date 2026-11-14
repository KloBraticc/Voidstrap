using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Voidstrap.UI.Elements.Controls;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.UI.Elements.Settings.Pages;

public partial class EmoteManagerSection : UserControl
{
    private EmoteImagePlacement _centerImage = new EmoteImagePlacement();

    private EmoteImagePlacement _backgroundImage = new EmoteImagePlacement();

    private CancellationTokenSource? _generateCts;

    private bool _loading;

    private bool _initialized;

    private bool _generating;

    private bool _previewRunning;

    private bool _previewPending;

    private Point? _dragStart;

    private double _dragOffsetX;

    private double _dragOffsetY;

    public EmoteManagerSection()
    {
        InitializeComponent();
    }

    private EmoteImagePlacement Target => TargetPicker.SelectedIndex == 1 ? _backgroundImage : _centerImage;

    private void Section_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_initialized)
        {
            _initialized = true;
            LoadSettings();
        }
        SchedulePreview();
    }

    private void Section_Unloaded(object sender, RoutedEventArgs e)
    {
        _previewPending = false;
        _generateCts?.Cancel();
    }

    private void LoadSettings()
    {
        string? modId = EmoteWheelGenerator.FindModId();
        EmoteWheelSettings settings = (modId != null ? EmoteWheelGenerator.LoadSettings(modId) : null) ?? new EmoteWheelSettings();
        ApplySettings(settings);
        if (modId != null)
        {
            RemoveButton.Visibility = Visibility.Visible;
            GenerateButton.Content = Strings.Ui_ModGen_RegenerateMod;
            if (settings.GeneratedUtc != default)
            {
                StatusText.Text = string.Format(
                    CultureInfo.CurrentCulture,
                    Strings.Ui_ModGen_LastGenerated,
                    settings.GeneratedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
                );
            }
        }
    }

    private void ApplySettings(EmoteWheelSettings settings)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            CenterBox.Text = settings.CenterColor;
            CenterEndBox.Text = settings.CenterColorEnd ?? "";
            LineBox.Text = settings.LineColor;
            BackgroundBox.Text = settings.BackgroundColor;
            GlowBox.Text = settings.GlowColor;
            GlowEndBox.Text = settings.GlowColorEnd ?? "";
            MarkerBox.Text = settings.MarkerColor;
            IconBox.Text = settings.IconColor;
            BackgroundOpacitySlider.Value = Math.Round(Math.Clamp(settings.BackgroundOpacity, 0.0, 1.0) * 100.0);
            WheelOpacitySlider.Value = Math.Round(Math.Clamp(settings.WheelOpacity, 0.1, 1.0) * 100.0);
            EditorToggle.IsChecked = settings.RecolorEditor;
            IconsToggle.IsChecked = settings.RecolorIcons;
            ModNameBox.Text = settings.ModName;
            _centerImage = Copy(settings.CenterImage);
            _backgroundImage = Copy(settings.BackgroundImage);
            LoadTarget();
        }
        finally
        {
            _loading = wasLoading;
        }
        UpdateSliderTexts();
        UpdateSwatches();
        UpdateImageLabels();
    }

    private static EmoteImagePlacement Copy(EmoteImagePlacement? source) =>
        source == null
            ? new EmoteImagePlacement()
            : new EmoteImagePlacement
            {
                Path = source.Path,
                Scale = double.IsFinite(source.Scale) ? Math.Clamp(source.Scale, 0.1, 5.0) : 1.0,
                OffsetX = double.IsFinite(source.OffsetX) ? Math.Clamp(source.OffsetX, -1.0, 1.0) : 0.0,
                OffsetY = double.IsFinite(source.OffsetY) ? Math.Clamp(source.OffsetY, -1.0, 1.0) : 0.0,
                Rotation = double.IsFinite(source.Rotation) ? Math.Clamp(source.Rotation, -180.0, 180.0) : 0.0,
                Opacity = double.IsFinite(source.Opacity) ? Math.Clamp(source.Opacity, 0.0, 1.0) : 1.0,
            };

    private EmoteWheelSettings ReadSettings() =>
        new EmoteWheelSettings
        {
            CenterColor = CenterBox.Text.Trim(),
            CenterColorEnd = string.IsNullOrWhiteSpace(CenterEndBox.Text) ? null : CenterEndBox.Text.Trim(),
            LineColor = LineBox.Text.Trim(),
            BackgroundColor = BackgroundBox.Text.Trim(),
            BackgroundOpacity = BackgroundOpacitySlider.Value / 100.0,
            GlowColor = GlowBox.Text.Trim(),
            GlowColorEnd = string.IsNullOrWhiteSpace(GlowEndBox.Text) ? null : GlowEndBox.Text.Trim(),
            MarkerColor = MarkerBox.Text.Trim(),
            WheelOpacity = WheelOpacitySlider.Value / 100.0,
            RecolorEditor = EditorToggle.IsChecked == true,
            RecolorIcons = IconsToggle.IsChecked == true,
            IconColor = IconBox.Text.Trim(),
            CenterImage = Copy(_centerImage),
            BackgroundImage = Copy(_backgroundImage),
            ModName = string.IsNullOrWhiteSpace(ModNameBox.Text) ? new EmoteWheelSettings().ModName : ModNameBox.Text.Trim(),
        };

    private void LoadTarget()
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            EmoteImagePlacement target = Target;
            SizeSlider.Value = Math.Round(target.Scale * 100.0);
            RotationSlider.Value = Math.Round(target.Rotation);
            ImageOpacitySlider.Value = Math.Round(target.Opacity * 100.0);
        }
        finally
        {
            _loading = wasLoading;
        }
        UpdateSliderTexts();
    }

    private void UpdateSliderTexts()
    {
        BackgroundOpacitySliderText.Text = Math.Round(BackgroundOpacitySlider.Value) + "%";
        WheelOpacitySliderText.Text = Math.Round(WheelOpacitySlider.Value) + "%";
        SizeSliderText.Text = Math.Round(SizeSlider.Value) + "%";
        RotationSliderText.Text = Math.Round(RotationSlider.Value) + "°";
        ImageOpacitySliderText.Text = Math.Round(ImageOpacitySlider.Value) + "%";
    }

    private void UpdateSwatches()
    {
        SetSwatch(CenterSwatch, CenterBox.Text);
        SetSwatch(CenterEndSwatch, CenterEndBox.Text);
        SetSwatch(LineSwatch, LineBox.Text);
        SetSwatch(BackgroundSwatch, BackgroundBox.Text);
        SetSwatch(GlowSwatch, GlowBox.Text);
        SetSwatch(GlowEndSwatch, GlowEndBox.Text);
        SetSwatch(MarkerSwatch, MarkerBox.Text);
        SetSwatch(IconSwatch, IconBox.Text);
        IconBox.IsEnabled = IconsToggle.IsChecked == true;
    }

    private static void SetSwatch(Border swatch, string text)
    {
        swatch.Background = EmoteWheelGenerator.TryParseColor(text, out Color color) ? new SolidColorBrush(color) : Brushes.Transparent;
    }

    private void UpdateImageLabels()
    {
        bool hasCenter = !string.IsNullOrWhiteSpace(_centerImage.Path);
        bool hasBackground = !string.IsNullOrWhiteSpace(_backgroundImage.Path);
        CenterImageText.Text = hasCenter ? Path.GetFileName(_centerImage.Path) : Strings.Ui_ModGen_NoImageChosen;
        BackgroundImageText.Text = hasBackground ? Path.GetFileName(_backgroundImage.Path) : Strings.Ui_ModGen_NoImageChosen;
        ClearCenterButton.Visibility = hasCenter ? Visibility.Visible : Visibility.Collapsed;
        ClearBackgroundButton.Visibility = hasBackground ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }
        UpdateSwatches();
        SchedulePreview();
    }

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized || _loading)
        {
            return;
        }
        if (ReferenceEquals(sender, SizeSlider))
        {
            Target.Scale = SizeSlider.Value / 100.0;
        }
        else if (ReferenceEquals(sender, RotationSlider))
        {
            Target.Rotation = RotationSlider.Value;
        }
        else if (ReferenceEquals(sender, ImageOpacitySlider))
        {
            Target.Opacity = ImageOpacitySlider.Value / 100.0;
        }
        UpdateSliderTexts();
        SchedulePreview();
    }

    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }
        LoadTarget();
    }

    private void Swatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: TextBox box } || !box.IsEnabled)
        {
            return;
        }
        e.Handled = true;
        if (!EmoteWheelGenerator.TryParseColor(box.Text, out Color initial))
        {
            initial = Colors.White;
        }
        RinColorPickerDialog dialog = new RinColorPickerDialog(initial);
        if (dialog.ShowOwnedDialog() != true)
        {
            return;
        }
        Color picked = dialog.SelectedColor;
        box.Text =
            "#"
            + picked.R.ToString("X2", CultureInfo.InvariantCulture)
            + picked.G.ToString("X2", CultureInfo.InvariantCulture)
            + picked.B.ToString("X2", CultureInfo.InvariantCulture);
    }

    private void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        OpenFileDialog dialog = new OpenFileDialog
        {
            Filter = Strings.Ui_ModGen_ImagesFilter + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            Multiselect = false,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        bool background = ReferenceEquals(sender, ChooseBackgroundButton);
        EmoteImagePlacement placement = background ? _backgroundImage : _centerImage;
        placement.Path = dialog.FileName;
        TargetPicker.SelectedIndex = background ? 1 : 0;
        LoadTarget();
        UpdateImageLabels();
        SchedulePreview();
    }

    private void ClearImage_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, ClearBackgroundButton))
        {
            _backgroundImage.Path = null;
        }
        else
        {
            _centerImage.Path = null;
        }
        UpdateImageLabels();
        SchedulePreview();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        EmoteImagePlacement target = Target;
        target.OffsetX = 0;
        target.OffsetY = 0;
        target.Scale = 1.0;
        target.Rotation = 0;
        LoadTarget();
        SchedulePreview();
    }

    private void Preview_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_generating || AfterImage.ActualWidth <= 0)
        {
            return;
        }
        _dragStart = e.GetPosition(AfterImage);
        _dragOffsetX = Target.OffsetX;
        _dragOffsetY = Target.OffsetY;
        AfterImage.CaptureMouse();
        e.Handled = true;
    }

    private void Preview_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        Point now = e.GetPosition(AfterImage);
        double side = Math.Max(1.0, Math.Min(AfterImage.ActualWidth, AfterImage.ActualHeight));
        EmoteImagePlacement target = Target;
        target.OffsetX = Math.Clamp(_dragOffsetX + (now.X - start.X) / side, -1.0, 1.0);
        target.OffsetY = Math.Clamp(_dragOffsetY + (now.Y - start.Y) / side, -1.0, 1.0);
        SchedulePreview();
    }

    private void Preview_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart == null)
        {
            return;
        }
        _dragStart = null;
        AfterImage.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_generating)
        {
            return;
        }
        SizeSlider.Value = Math.Clamp(SizeSlider.Value + (e.Delta > 0 ? 5 : -5), SizeSlider.Minimum, SizeSlider.Maximum);
        e.Handled = true;
    }

    private void SchedulePreview()
    {
        if (_previewRunning)
        {
            _previewPending = true;
            return;
        }
        _ = RenderPreviewAsync();
    }

    private async Task RenderPreviewAsync()
    {
        const string LOG_IDENT = "EmoteManagerSection::RenderPreviewAsync";
        EmoteWheelSettings settings = ReadSettings();
        string? problem = EmoteWheelGenerator.Validate(settings);
        if (problem != null)
        {
            PreviewNote.Text = problem;
            return;
        }
        _previewRunning = true;
        bool first = AfterImage.Source == null;
        if (first)
        {
            PreviewRing.Visibility = Visibility.Visible;
        }
        (BitmapSource Before, BitmapSource After)? preview = null;
        try
        {
            preview = await Task.Run(() => EmoteWheelGenerator.RenderPreview(settings));
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The preview could not be rendered: " + ex.Message);
        }
        finally
        {
            _previewRunning = false;
            PreviewRing.Visibility = Visibility.Collapsed;
        }
        if (!IsLoaded)
        {
            _previewPending = false;
            return;
        }
        if (preview is (BitmapSource before, BitmapSource after))
        {
            if (!ReferenceEquals(BeforeImage.Source, before))
            {
                BeforeImage.Source = before;
            }
            AfterImage.Source = after;
            PreviewNote.Text = Strings.Ui_EmoteMgr_DragHint;
        }
        else if (!_previewPending)
        {
            BeforeImage.Source = null;
            AfterImage.Source = null;
            PreviewNote.Text = Strings.Ui_EmoteMgr_NoTextures;
        }
        if (_previewPending)
        {
            _previewPending = false;
            SchedulePreview();
        }
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        const string LOG_IDENT = "EmoteManagerSection::Generate_Click";
        if (_generating)
        {
            return;
        }
        EmoteWheelSettings settings = ReadSettings();
        string? problem = EmoteWheelGenerator.Validate(settings);
        if (problem != null)
        {
            StatusText.Text = problem;
            return;
        }
        _generating = true;
        _generateCts?.Dispose();
        _generateCts = new CancellationTokenSource();
        CancellationToken token = _generateCts.Token;
        bool waiting = ModGenerator.IsGenerating;
        SetToolsEnabled(false);
        CancelButton.Visibility = Visibility.Visible;
        GenerateProgress.Visibility = Visibility.Visible;
        GenerateProgress.IsIndeterminate = waiting;
        GenerateProgress.Value = 0;
        StatusText.Text = waiting ? Strings.Ui_ModGen_WaitingForOther : Strings.Ui_EmoteMgr_Restyling;
        Progress<double> progress = new Progress<double>(OnProgress);
        try
        {
            int written = await EmoteWheelGenerator.GenerateAsync(settings, progress, token);
            _centerImage.Path = settings.CenterImage.Path;
            _backgroundImage.Path = settings.BackgroundImage.Path;
            UpdateImageLabels();
            StatusText.Text = string.Format(
                CultureInfo.CurrentCulture,
                Strings.Ui_EmoteMgr_Generated,
                written.ToString("N0", CultureInfo.CurrentCulture)
            );
            GenerateButton.Content = Strings.Ui_ModGen_RegenerateMod;
            RemoveButton.Visibility = Visibility.Visible;
            RefreshMods();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Strings.Ui_ModGen_Cancelled;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Generation failed: " + ex.Message);
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Ui_ModGen_Failed, ex.Message);
        }
        finally
        {
            _generating = false;
            SetToolsEnabled(true);
            CancelButton.Visibility = Visibility.Collapsed;
            GenerateProgress.Visibility = Visibility.Collapsed;
            GenerateProgress.IsIndeterminate = false;
        }
    }

    private void OnProgress(double value)
    {
        GenerateProgress.IsIndeterminate = false;
        GenerateProgress.Value = value;
        StatusText.Text = Strings.Ui_EmoteMgr_Restyling;
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        const string LOG_IDENT = "EmoteManagerSection::Remove_Click";
        if (_generating)
        {
            return;
        }
        _generating = true;
        SetToolsEnabled(false);
        try
        {
            if (await EmoteWheelGenerator.RemoveAsync(CancellationToken.None))
            {
                StatusText.Text = Strings.Ui_EmoteMgr_Removed;
                RemoveButton.Visibility = Visibility.Collapsed;
                GenerateButton.Content = Strings.Ui_ModGen_GenerateMod;
                RefreshMods();
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "The emote mod could not be removed: " + ex.Message);
            StatusText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Ui_ModGen_Failed, ex.Message);
        }
        finally
        {
            _generating = false;
            SetToolsEnabled(true);
        }
    }

    private void RefreshMods()
    {
        if (DataContext is ModsViewModel mods && mods.RefreshManagedModsCommand.CanExecute(null))
        {
            mods.RefreshManagedModsCommand.Execute(null);
        }
    }

    private void SetToolsEnabled(bool enabled)
    {
        GenerateButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
        ResetButton.IsEnabled = enabled;
        RandomizeButton.IsEnabled = enabled;
        ChooseCenterButton.IsEnabled = enabled;
        ChooseBackgroundButton.IsEnabled = enabled;
    }

    private void Randomize_Click(object sender, RoutedEventArgs e)
    {
        double hue = Random.Shared.NextDouble() * 360.0;
        double accent = (hue + 20.0 + Random.Shared.NextDouble() * 60.0) % 360.0;
        _loading = true;
        try
        {
            CenterBox.Text = FromHsv(hue, 0.8, 0.25);
            CenterEndBox.Text = Random.Shared.Next(2) == 0 ? "" : FromHsv(accent, 0.8, 0.35);
            LineBox.Text = FromHsv(hue, 0.6, 1.0);
            BackgroundBox.Text = FromHsv(hue, 0.8, 0.12);
            GlowBox.Text = FromHsv(accent, 0.75, 1.0);
            GlowEndBox.Text = "";
            MarkerBox.Text = FromHsv(accent, 0.3, 1.0);
            IconBox.Text = LineBox.Text;
        }
        finally
        {
            _loading = false;
        }
        UpdateSwatches();
        SchedulePreview();
    }

    private static string FromHsv(double hue, double saturation, double value)
    {
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        double m = value - chroma;
        (double r, double g, double b) = (hue % 360) switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return "#"
            + ((byte)Math.Round((r + m) * 255)).ToString("X2", CultureInfo.InvariantCulture)
            + ((byte)Math.Round((g + m) * 255)).ToString("X2", CultureInfo.InvariantCulture)
            + ((byte)Math.Round((b + m) * 255)).ToString("X2", CultureInfo.InvariantCulture);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        ApplySettings(new EmoteWheelSettings { ModName = ModNameBox.Text });
        SchedulePreview();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _generateCts?.Cancel();
    }
}
