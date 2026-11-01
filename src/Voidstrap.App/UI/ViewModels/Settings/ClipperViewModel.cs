using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations.Clipper;
using Voidstrap.UI.Elements.ContextMenu;

namespace Voidstrap.UI.ViewModels.Settings;

public class ClipperViewModel : NotifyPropertyChangedViewModel
{
    private static (string Key, string Label)[] GetClipperModes() =>
        [("record", Strings.Ui_Clipper_ModeRecord), ("clip", Strings.Ui_Clipper_ModeClip)];

    private static (int Seconds, string Label)[] GetClipperBuffers() =>
        [(15, Strings.Ui_Clipper_Sec15), (30, Strings.Ui_Clipper_Sec30), (60, Strings.Ui_Clipper_Sec60), (120, Strings.Ui_Clipper_Sec120)];

    private static (int Key, string Label)[] GetClipperQualities() =>
        [
            (0, Strings.Ui_Clipper_QualityLow),
            (1, Strings.Ui_Clipper_QualityMedium),
            (2, Strings.Ui_Clipper_QualityHigh),
            (3, Strings.Ui_Clipper_QualityUltra),
        ];

    private static (int Height, string Label)[] GetClipperResolutions() =>
        [(0, Strings.Ui_Clipper_ResNative), (720, "720p"), (1080, "1080p"), (1440, "1440p")];

    private static readonly (int Fps, string Label)[] ClipperFpsPresets = [(30, "30 FPS"), (60, "60 FPS"), (120, "120 FPS")];

    private static readonly (string Key, string Label)[] ClipperCodecs = [("h264", "H.264 (GPU)"), ("hevc", "HEVC (GPU)")];

    private static OverlayShortcut DefaultClipperShortcut() =>
        new() { Modifiers = OverlayShortcut.Alt, Key = 0x79 };

    public bool ClipperEnabled
    {
        get => App.Settings.Prop.ClipperEnabled;
        set
        {
            if (App.Settings.Prop.ClipperEnabled == value)
                return;
            App.Settings.Prop.ClipperEnabled = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ClipperDescription));
            MenuContainer.SyncClipperHotkey();
            ClipperService.ReconcileWithSettings();
        }
    }

    public string ClipperDescription
    {
        get
        {
            var shortcut = App.Settings.Prop.ClipperShortcut ?? DefaultClipperShortcut();
            return shortcut.IsSet
                ? string.Format(Strings.Ui_Clipper_ShortcutDescription, shortcut.Describe())
                : Strings.Ui_Clipper_NoShortcutDescription;
        }
    }

    public bool CanResetClipperShortcut
    {
        get
        {
            var current = App.Settings.Prop.ClipperShortcut ?? DefaultClipperShortcut();
            return !current.SameAs(DefaultClipperShortcut());
        }
    }

    public ICommand ResetClipperShortcutCommand => new RelayCommand(ResetClipperShortcut);

    private void ResetClipperShortcut()
    {
        App.Settings.Prop.ClipperShortcut = DefaultClipperShortcut();
        App.Settings.SaveDeferred();
        MenuContainer.SyncClipperHotkey();
        OnPropertyChanged(nameof(ClipperDescription));
        OnPropertyChanged(nameof(CanResetClipperShortcut));
    }

    public IReadOnlyList<string> ClipperModeOptions => GetClipperModes().Select(x => x.Label).ToList();

    public bool ClipperIsClipMode => string.Equals(App.Settings.Prop.ClipperMode, "clip", StringComparison.OrdinalIgnoreCase);

    public string SelectedClipperMode
    {
        get
        {
            string m = App.Settings.Prop.ClipperMode ?? "record";
            foreach (var item in GetClipperModes())
            {
                if (string.Equals(item.Key, m, StringComparison.OrdinalIgnoreCase))
                    return item.Label;
            }
            return Strings.Ui_Clipper_ModeRecord;
        }
        set
        {
            foreach (var item in GetClipperModes())
            {
                if (item.Label == value)
                {
                    if (string.Equals(App.Settings.Prop.ClipperMode, item.Key, StringComparison.OrdinalIgnoreCase))
                        return;
                    App.Settings.Prop.ClipperMode = item.Key;
                    App.Settings.SaveDeferred();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ClipperIsClipMode));
                    ClipperService.ReconcileWithSettings();
                    return;
                }
            }
        }
    }

    public bool ClipperRecordOnGameStart
    {
        get => App.Settings.Prop.ClipperRecordOnGameStart;
        set
        {
            if (App.Settings.Prop.ClipperRecordOnGameStart == value)
                return;
            App.Settings.Prop.ClipperRecordOnGameStart = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged();
            MenuContainer.SyncClipperHotkey();
        }
    }

    public bool ClipperRedBorder
    {
        get => App.Settings.Prop.ClipperRedBorder;
        set
        {
            if (App.Settings.Prop.ClipperRedBorder == value)
                return;
            App.Settings.Prop.ClipperRedBorder = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged();
            ClipperService.ReconcileWithSettings();
        }
    }

    public IReadOnlyList<string> ClipperBufferOptions => GetClipperBuffers().Select(x => x.Label).ToList();

    public string SelectedClipperBuffer
    {
        get
        {
            int secs = App.Settings.Prop.ClipperBufferSeconds;
            foreach (var item in GetClipperBuffers())
            {
                if (item.Seconds == secs)
                    return item.Label;
            }
            return Strings.Ui_Clipper_Sec30;
        }
        set
        {
            foreach (var item in GetClipperBuffers())
            {
                if (item.Label == value)
                {
                    if (App.Settings.Prop.ClipperBufferSeconds == item.Seconds)
                        return;
                    App.Settings.Prop.ClipperBufferSeconds = item.Seconds;
                    App.Settings.SaveDeferred();
                    OnPropertyChanged();
                    ClipperService.ReconcileWithSettings();
                    return;
                }
            }
        }
    }

    public IReadOnlyList<string> ClipperQualityOptions => GetClipperQualities().Select(x => x.Label).ToList();

    public string SelectedClipperQuality
    {
        get
        {
            int q = App.Settings.Prop.ClipperQuality;
            foreach (var item in GetClipperQualities())
            {
                if (item.Key == q)
                    return item.Label;
            }
            return Strings.Ui_Clipper_QualityHigh;
        }
        set
        {
            foreach (var item in GetClipperQualities())
            {
                if (item.Label == value)
                {
                    if (App.Settings.Prop.ClipperQuality == item.Key)
                        return;
                    App.Settings.Prop.ClipperQuality = item.Key;
                    App.Settings.SaveDeferred();
                    OnPropertyChanged();
                    ClipperService.ReconcileWithSettings();
                    return;
                }
            }
        }
    }

    public IReadOnlyList<string> ClipperResolutionOptions => GetClipperResolutions().Select(x => x.Label).ToList();

    public string SelectedClipperResolution
    {
        get
        {
            int h = App.Settings.Prop.ClipperResolutionHeight;
            foreach (var item in GetClipperResolutions())
            {
                if (item.Height == h)
                    return item.Label;
            }
            return "1080p";
        }
        set
        {
            foreach (var item in GetClipperResolutions())
            {
                if (item.Label == value)
                {
                    if (App.Settings.Prop.ClipperResolutionHeight == item.Height)
                        return;
                    App.Settings.Prop.ClipperResolutionHeight = item.Height;
                    App.Settings.SaveDeferred();
                    OnPropertyChanged();
                    ClipperService.ReconcileWithSettings();
                    return;
                }
            }
        }
    }

    public IReadOnlyList<string> ClipperFpsOptions { get; } = ClipperFpsPresets.Select(x => x.Label).ToList();

    public string SelectedClipperFps
    {
        get
        {
            int fps = App.Settings.Prop.ClipperFps;
            foreach (var item in ClipperFpsPresets)
            {
                if (item.Fps == fps)
                    return item.Label;
            }
            return "60 FPS";
        }
        set
        {
            foreach (var item in ClipperFpsPresets)
            {
                if (item.Label == value)
                {
                    if (App.Settings.Prop.ClipperFps == item.Fps)
                        return;
                    App.Settings.Prop.ClipperFps = item.Fps;
                    App.Settings.SaveDeferred();
                    OnPropertyChanged();
                    ClipperService.ReconcileWithSettings();
                    return;
                }
            }
        }
    }

    public IReadOnlyList<string> ClipperCodecOptions { get; } = ClipperCodecs.Select(x => x.Label).ToList();

    public string SelectedClipperCodec
    {
        get
        {
            string c = App.Settings.Prop.ClipperCodec ?? "h264";
            foreach (var item in ClipperCodecs)
            {
                if (string.Equals(item.Key, c, StringComparison.OrdinalIgnoreCase))
                    return item.Label;
            }
            return "H.264 (GPU)";
        }
        set
        {
            foreach (var item in ClipperCodecs)
            {
                if (item.Label == value)
                {
                    if (string.Equals(App.Settings.Prop.ClipperCodec, item.Key, StringComparison.OrdinalIgnoreCase))
                        return;
                    App.Settings.Prop.ClipperCodec = item.Key;
                    App.Settings.SaveDeferred();
                    OnPropertyChanged();
                    ClipperService.ReconcileWithSettings();
                    return;
                }
            }
        }
    }

    public bool ClipperGameAudio
    {
        get => App.Settings.Prop.ClipperGameAudio;
        set
        {
            if (App.Settings.Prop.ClipperGameAudio == value)
                return;
            App.Settings.Prop.ClipperGameAudio = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged();
            ClipperService.ReconcileWithSettings();
        }
    }

    public bool ClipperMicrophone
    {
        get => App.Settings.Prop.ClipperMicrophone;
        set
        {
            if (App.Settings.Prop.ClipperMicrophone == value)
                return;
            App.Settings.Prop.ClipperMicrophone = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged();
            ClipperService.ReconcileWithSettings();
        }
    }

    public bool VoidEditAskForRecordings
    {
        get => App.Settings.Prop.VoidEditAskForRecordings;
        set
        {
            if (App.Settings.Prop.VoidEditAskForRecordings == value)
                return;
            App.Settings.Prop.VoidEditAskForRecordings = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged();
        }
    }

    public bool ClipperShowIndicator
    {
        get => App.Settings.Prop.ClipperShowIndicator;
        set
        {
            if (App.Settings.Prop.ClipperShowIndicator == value)
                return;
            App.Settings.Prop.ClipperShowIndicator = value;
            App.Settings.SaveDeferred();
            OnPropertyChanged();
            ClipperService.ReconcileWithSettings();
        }
    }

    public ICommand OpenClipsFolderCommand =>
        new RelayCommand(() => Utilities.ShellExecute(ClipperService.GetClipsDirectory()));
}
