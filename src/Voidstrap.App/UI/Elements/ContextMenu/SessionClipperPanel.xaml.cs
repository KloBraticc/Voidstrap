using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Voidstrap.Integrations.Clipper;
using Voidstrap.Models.Persistable;
using Voidstrap.UI.Elements.ContextMenu;
using Voidstrap.Utility;

namespace Voidstrap.UI.Elements.Overlay;

public sealed class ClipListItemViewModel
{
    public string FullPath { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string DurationText { get; init; } = string.Empty;
    public long DurationMs { get; init; }
    public ImageSource? Thumbnail { get; init; }
}

public partial class SessionClipperPanel : Window
{
    private readonly DispatcherTimer _statusTimer;
    private bool _loading;
    private bool _recordingShortcut;
    private bool _trimUpdating;
    private ClipListItemViewModel? _activeTrimClip;

    public SessionClipperPanel()
    {
        InitializeComponent();
        LoadSettingsIntoControls();
        UpdateStatusUi();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusUi();
        _statusTimer.Start();

        ClipperService.ClipSaved += OnClipSaved;
        Closed += OnPanelClosed;

        _ = RefreshClipsListAsync();
    }

    private void OnPanelClosed(object? sender, EventArgs e)
    {
        _statusTimer.Stop();
        ClipperService.ClipSaved -= OnClipSaved;
        if (_recordingShortcut)
        {
            _recordingShortcut = false;
            MenuContainer.SuspendClipperHotkey(false);
        }
    }

    private void OnClipSaved(SavedClipInfo clip)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ShowFeedback($"Saved {clip.FileName} ({clip.FormattedDuration} • {clip.FormattedSize})");
            _ = RefreshClipsListAsync();
        }));
    }

    private void LoadSettingsIntoControls()
    {
        _loading = true;
        try
        {
            var prop = App.Settings.Prop;
            MasterToggle.IsChecked = prop.ClipperEnabled;
            SelectComboByTag(BufferCombo, prop.ClipperBufferSeconds.ToString(CultureInfo.InvariantCulture), 1);
            SelectComboByTag(QualityCombo, prop.ClipperQuality.ToString(CultureInfo.InvariantCulture), 2);
            SelectComboByTag(ResolutionCombo, prop.ClipperResolutionHeight.ToString(CultureInfo.InvariantCulture), 2);
            SelectComboByTag(FpsCombo, prop.ClipperFps.ToString(CultureInfo.InvariantCulture), 1);
            SelectComboByTag(CodecCombo, string.Equals(prop.ClipperCodec, "hevc", StringComparison.OrdinalIgnoreCase) ? "hevc" : "h264", 0);

            GameAudioCheck.IsChecked = prop.ClipperGameAudio;
            MicAudioCheck.IsChecked = prop.ClipperMicrophone;
            IndicatorCheck.IsChecked = prop.ClipperShowIndicator;

            OverlayShortcut shortcut = prop.ClipperShortcut ??= new OverlayShortcut { Modifiers = OverlayShortcut.Alt, Key = 0x79 };
            ClipShortcutText.Text = shortcut.Describe();
        }
        finally
        {
            _loading = false;
        }
    }

    private static void SelectComboByTag(ComboBox combo, string tag, int fallbackIndex)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        if (fallbackIndex >= 0 && fallbackIndex < combo.Items.Count)
            combo.SelectedIndex = fallbackIndex;
    }

    private void UpdateStatusUi()
    {
        bool enabled = App.Settings.Prop.ClipperEnabled;
        if (_loading)
            return;

        if (MasterToggle.IsChecked != enabled)
        {
            _loading = true;
            MasterToggle.IsChecked = enabled;
            _loading = false;
        }

        string prov = ClipperService.ProvisionStatus;
        if (!string.IsNullOrEmpty(prov))
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xD4, 0xA7, 0x4A));
            StatusHeadline.Text = prov;
            StatusSubline.Text = "Preparing hardware video encoder...";
            SaveNowButton.IsEnabled = false;
            return;
        }

        NativeClipperStatus st = ClipperService.GetStatus();
        int targetSec = Math.Max(5, App.Settings.Prop.ClipperBufferSeconds);
        double bufferedSec = Math.Min(targetSec, st.BufferedMs / 1000.0);
        double mb = st.BufferedBytes / (1024.0 * 1024.0);

        BufferMeter.Maximum = targetSec;
        BufferMeter.Value = st.Running ? bufferedSec : 0;
        BufferReadout.Text = $"{bufferedSec:0}s / {targetSec}s ({mb:0.0} MB)";

        if (!enabled)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90));
            StatusHeadline.Text = "Instant Replay Disabled";
            StatusSubline.Text = "Turn on the switch on the right to start the replay buffer.";
            SaveNowButton.IsEnabled = false;
            return;
        }

        if (st.Running)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x6A));
            string enc = string.IsNullOrWhiteSpace(st.EncoderLabel) ? "Hardware Encoder" : st.EncoderLabel;
            StatusHeadline.Text = $"Recording Replay Buffer • {enc}";
            string shortcut = (App.Settings.Prop.ClipperShortcut ?? new OverlayShortcut { Modifiers = OverlayShortcut.Alt, Key = 0x79 }).Describe();
            StatusSubline.Text = $"{st.Width}x{st.Height} @ {st.Fps} FPS • {st.BitrateKbps / 1000.0:0.0} Mbps • Press {shortcut} to save";
            SaveNowButton.IsEnabled = st.BufferedMs >= 500;
        }
        else if (ClipperService.InGame)
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0xD4, 0xA7, 0x4A));
            StatusHeadline.Text = "Starting Replay Buffer...";
            StatusSubline.Text = !string.IsNullOrWhiteSpace(st.LastError)
                ? st.LastError
                : "Initializing GPU capture pipeline...";
            SaveNowButton.IsEnabled = false;
        }
        else
        {
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x90));
            StatusHeadline.Text = "Armed for Next Game Session";
            StatusSubline.Text = "Replay capture will start automatically as soon as Roblox is running.";
            SaveNowButton.IsEnabled = false;
        }
    }

    private void MasterToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        bool enabled = MasterToggle.IsChecked == true;
        App.Settings.Prop.ClipperEnabled = enabled;
        App.Settings.SaveDeferred();
        MenuContainer.SyncClipperHotkey();
        ClipperService.ReconcileWithSettings();
        UpdateStatusUi();
    }

    private void QualitySetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        var prop = App.Settings.Prop;
        if (BufferCombo.SelectedItem is ComboBoxItem bufItem && int.TryParse(bufItem.Tag?.ToString(), out int bufSec))
            prop.ClipperBufferSeconds = bufSec;
        if (QualityCombo.SelectedItem is ComboBoxItem qItem && int.TryParse(qItem.Tag?.ToString(), out int q))
            prop.ClipperQuality = q;
        if (ResolutionCombo.SelectedItem is ComboBoxItem resItem && int.TryParse(resItem.Tag?.ToString(), out int res))
            prop.ClipperResolutionHeight = res;
        if (FpsCombo.SelectedItem is ComboBoxItem fpsItem && int.TryParse(fpsItem.Tag?.ToString(), out int fps))
            prop.ClipperFps = fps;
        if (CodecCombo.SelectedItem is ComboBoxItem codecItem && codecItem.Tag is string codec)
            prop.ClipperCodec = codec;

        App.Settings.SaveDeferred();
        ClipperService.ReconcileWithSettings();
        UpdateStatusUi();
    }

    private void AudioOrBadge_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var prop = App.Settings.Prop;
        prop.ClipperGameAudio = GameAudioCheck.IsChecked == true;
        prop.ClipperMicrophone = MicAudioCheck.IsChecked == true;
        prop.ClipperShowIndicator = IndicatorCheck.IsChecked == true;
        App.Settings.SaveDeferred();
        ClipperService.ReconcileWithSettings();
        UpdateStatusUi();
    }

    private async void SaveNowButton_Click(object sender, RoutedEventArgs e)
    {
        SaveNowButton.IsEnabled = false;
        SaveNowButtonText.Text = "Saving...";
        try
        {
            var (ok, clip, error) = await ClipperService.SaveClipAsync();
            if (!ok || clip == null)
                ShowFeedback(string.IsNullOrWhiteSpace(error) ? "Could not save replay clip." : error);
        }
        finally
        {
            SaveNowButtonText.Text = "Save Clip";
            UpdateStatusUi();
        }
    }

    private void ClipShortcutBox_Click(object sender, MouseButtonEventArgs e)
    {
        _recordingShortcut = true;
        MenuContainer.SuspendClipperHotkey(true);
        ClipShortcutText.Text = "Press keys...";
        ClipShortcutBox.Focus();
        Keyboard.Focus(ClipShortcutBox);
        e.Handled = true;
    }

    private void ClipShortcutBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!_recordingShortcut)
            return;
        _recordingShortcut = false;
        MenuContainer.SuspendClipperHotkey(false);
        OverlayShortcut current = App.Settings.Prop.ClipperShortcut ??= new OverlayShortcut { Modifiers = OverlayShortcut.Alt, Key = 0x79 };
        ClipShortcutText.Text = current.Describe();
    }

    private void ClipShortcutBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingShortcut)
            return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _recordingShortcut = false;
            MenuContainer.SuspendClipperHotkey(false);
            ClipShortcutText.Text = (App.Settings.Prop.ClipperShortcut ?? new OverlayShortcut { Modifiers = OverlayShortcut.Alt, Key = 0x79 }).Describe();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        uint mods = 0;
        ModifierKeys m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Alt) != 0)
            mods |= OverlayShortcut.Alt;
        if ((m & ModifierKeys.Control) != 0)
            mods |= OverlayShortcut.Control;
        if ((m & ModifierKeys.Shift) != 0)
            mods |= OverlayShortcut.Shift;
        if ((m & ModifierKeys.Windows) != 0)
            mods |= OverlayShortcut.Windows;

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0)
            return;
        if (mods == 0 && (vk < 0x70 || vk > 0x87))
        {
            ClipShortcutHint.Text = "Include Ctrl, Alt, Shift, or use a function key (F1-F12).";
            return;
        }
        if (mods == 0)
            mods = OverlayShortcut.Alt;

        var candidate = new OverlayShortcut { Modifiers = mods, Key = (uint)vk };
        App.Settings.Prop.ClipperShortcut = candidate;
        App.Settings.SaveDeferred();
        _recordingShortcut = false;
        MenuContainer.SuspendClipperHotkey(false);
        MenuContainer.SyncClipperHotkey();
        ClipShortcutText.Text = candidate.Describe();
        ClipShortcutHint.Text = "Click the box and press a key combination to save a clip in game.";
        UpdateStatusUi();
    }

    private void ResetClipShortcut_Click(object sender, RoutedEventArgs e)
    {
        var def = new OverlayShortcut { Modifiers = OverlayShortcut.Alt, Key = 0x79 };
        App.Settings.Prop.ClipperShortcut = def;
        App.Settings.SaveDeferred();
        MenuContainer.SyncClipperHotkey();
        ClipShortcutText.Text = def.Describe();
        UpdateStatusUi();
    }

    public async Task RefreshClipsListAsync()
    {
        IReadOnlyList<SavedClipInfo> clips = await ClipperService.GetSavedClipsAsync();
        List<ClipListItemViewModel> items = new(clips.Count);
        foreach (SavedClipInfo c in clips)
        {
            items.Add(new ClipListItemViewModel
            {
                FullPath = c.FullPath,
                Title = c.Title,
                Subtitle = $"{c.FormattedDate} • {c.FormattedSize}",
                DurationText = c.FormattedDuration,
                DurationMs = c.DurationMs > 0 ? c.DurationMs : 30000,
                Thumbnail = LoadThumbnailSafe(c.ThumbnailPath)
            });
        }
        ClipsList.ItemsSource = items;
        EmptyClipsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ImageSource? LoadThumbnailSafe(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return null;
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapImage bmp = new();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void RefreshClips_Click(object sender, RoutedEventArgs e)
    {
        _ = RefreshClipsListAsync();
    }

    private void OpenClipsFolder_Click(object sender, RoutedEventArgs e)
    {
        Utilities.ShellExecute(ClipperService.GetClipsDirectory());
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && File.Exists(path))
            Utilities.ShellExecute(path);
    }

    private void DeleteClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path } || !File.Exists(path))
            return;
        try
        {
            File.Delete(path);
            if (_activeTrimClip != null && string.Equals(_activeTrimClip.FullPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _activeTrimClip = null;
                TrimEditorCard.Visibility = Visibility.Collapsed;
            }
            _ = RefreshClipsListAsync();
        }
        catch (Exception ex)
        {
            ShowFeedback("Could not delete clip: " + ex.Message);
        }
    }

    private void OpenTrimClip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ClipListItemViewModel item })
            return;
        _activeTrimClip = item;
        _trimUpdating = true;
        try
        {
            double maxMs = Math.Max(1000, item.DurationMs);
            TrimClipTitle.Text = "Trim: " + item.Title;
            TrimStartSlider.Maximum = maxMs;
            TrimEndSlider.Maximum = maxMs;
            TrimStartSlider.Value = 0;
            TrimEndSlider.Value = maxMs;
            UpdateTrimLabels();
            TrimEditorCard.Visibility = Visibility.Visible;
        }
        finally
        {
            _trimUpdating = false;
        }
    }

    private void CloseTrimEditor_Click(object sender, RoutedEventArgs e)
    {
        _activeTrimClip = null;
        TrimEditorCard.Visibility = Visibility.Collapsed;
    }

    private void TrimSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_trimUpdating || _activeTrimClip == null)
            return;
        _trimUpdating = true;
        try
        {
            if (ReferenceEquals(sender, TrimStartSlider) && TrimStartSlider.Value > TrimEndSlider.Value - 500)
                TrimStartSlider.Value = Math.Max(0, TrimEndSlider.Value - 500);
            else if (ReferenceEquals(sender, TrimEndSlider) && TrimEndSlider.Value < TrimStartSlider.Value + 500)
                TrimEndSlider.Value = Math.Min(TrimEndSlider.Maximum, TrimStartSlider.Value + 500);
            UpdateTrimLabels();
        }
        finally
        {
            _trimUpdating = false;
        }
    }

    private void UpdateTrimLabels()
    {
        double startSec = TrimStartSlider.Value / 1000.0;
        double endSec = TrimEndSlider.Value / 1000.0;
        double lenSec = Math.Max(0, endSec - startSec);
        TrimStartValue.Text = $"{startSec:0.0}s";
        TrimEndValue.Text = $"{endSec:0.0}s";
        TrimRangeLabel.Text = $"{FormatSeconds(startSec)} - {FormatSeconds(endSec)} (Length: {lenSec:0.1}s)";
    }

    private static string FormatSeconds(double sec)
    {
        int mins = (int)(sec / 60);
        double rem = sec - mins * 60;
        return $"{mins:D2}:{rem:00.0}";
    }

    private async void TrimSaveCopy_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteTrimAsync(replaceOriginal: false);
    }

    private async void TrimOverwrite_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteTrimAsync(replaceOriginal: true);
    }

    private async Task ExecuteTrimAsync(bool replaceOriginal)
    {
        if (_activeTrimClip == null)
            return;
        long startMs = (long)Math.Round(TrimStartSlider.Value);
        long endMs = (long)Math.Round(TrimEndSlider.Value);
        var (ok, clip, error) = await ClipperService.TrimClipAsync(_activeTrimClip.FullPath, startMs, endMs, replaceOriginal);
        if (!ok || clip == null)
        {
            ShowFeedback(string.IsNullOrWhiteSpace(error) ? "Trim failed." : error);
            return;
        }
        ShowFeedback($"Trimmed clip saved: {clip.FileName} ({clip.FormattedDuration})");
        TrimEditorCard.Visibility = Visibility.Collapsed;
        _activeTrimClip = null;
        await RefreshClipsListAsync();
    }

    private void ShowFeedback(string message)
    {
        FeedbackBanner.Text = message;
        FeedbackBanner.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }
}
