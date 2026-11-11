using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Enums.FlagPresets;
using Voidstrap.Utility;

namespace Voidstrap.UI.ViewModels.Settings;

public class FastFlagsViewModel : NotifyPropertyChangedViewModel
{
    private Dictionary<string, object>? _preResetFlags;

    public const string Enabled = "True";

    public const string Disabled = "False";

    private static readonly string[] LODLevels = new string[4] { "L0", "L12", "L23", "L34" };

    private const int MinMeshQuality = 1;

    private const int MaxMeshQuality = 3;

    private const int MinFRMQuality = 1;

    private const int MaxFRMQuality = 21;

    private IEnumerable? profileModes;

    private string selectedProfileMods = string.Empty;

    public ICommand OpenFastFlagEditorCommand => new RelayCommand(OpenFastFlagEditor);

    public bool FRMQualityOverrideEnabled
    {
        get { return App.FastFlags.GetPreset("Rendering.FRMQualityOverride") != null; }
        set
        {
            if (value == FRMQualityOverrideEnabled)
            {
                return;
            }
            if (value)
            {
                App.FastFlags.SetPreset("Rendering.FRMQualityOverride", MaxFRMQuality);
            }
            else
            {
                App.FastFlags.SetPreset("Rendering.FRMQualityOverride", null);
            }
            OnPropertyChanged(nameof(FRMQualityOverride));
            OnPropertyChanged(nameof(FRMQualityOverrideEnabled));
        }
    }

    public int FRMQualityOverride
    {
        get
        {
            if (!int.TryParse(App.FastFlags.GetPreset("Rendering.FRMQualityOverride"), out var result))
            {
                return MaxFRMQuality;
            }
            return Math.Clamp(result, MinFRMQuality, MaxFRMQuality);
        }
        set
        {
            if (!FRMQualityOverrideEnabled)
            {
                OnPropertyChanged(nameof(FRMQualityOverride));
                return;
            }
            App.FastFlags.SetPreset("Rendering.FRMQualityOverride", Math.Clamp(value, MinFRMQuality, MaxFRMQuality));
            OnPropertyChanged(nameof(FRMQualityOverride));
        }
    }

    public bool ShadersEnabled
    {
        get { return App.FastFlags.GetPreset("Rendering.Shaders2") == "21"; }
        set { App.FastFlags.SetPreset("Rendering.Shaders2", value ? "21" : "0"); }
    }

    public bool UseFastFlagManager
    {
        get { return App.Settings.Prop.UseFastFlagManager; }
        set { App.Settings.Prop.UseFastFlagManager = value; }
    }

    public IEnumerable<Voidstrap.Enums.ModApplyTarget> FastFlagApplyTargets { get; } = Enum.GetValues<Voidstrap.Enums.ModApplyTarget>();

    public Voidstrap.Enums.ModApplyTarget FastFlagApplyTarget
    {
        get => App.Settings.Prop.FastFlagApplyTarget;
        set
        {
            if (App.Settings.Prop.FastFlagApplyTarget == value)
                return;
            App.Settings.Prop.FastFlagApplyTarget = value;
            OnPropertyChanged(nameof(FastFlagApplyTarget));
        }
    }

    public bool LowPolyMeshes
    {
        get { return App.FastFlags.GetPreset("Rendering.LowPolyMeshes1") == "0"; }
        set
        {
            App.FastFlags.SetPreset("Rendering.LowPolyMeshes1", value ? "0" : null);
            App.FastFlags.SetPreset("Rendering.LowPolyMeshes2", value ? "0" : null);
            App.FastFlags.SetPreset("Rendering.LowPolyMeshes3", value ? "0" : null);
            App.FastFlags.SetPreset("Rendering.LowPolyMeshes4", value ? "0" : null);
        }
    }

    public int MeshQuality
    {
        get
        {
            if (!int.TryParse(App.FastFlags.GetPreset("Geometry.MeshLOD.L0"), out var result))
            {
                return MaxMeshQuality;
            }
            return Math.Clamp(result, MinMeshQuality, MaxMeshQuality);
        }
        set
        {
            int num = Math.Clamp(value, MinMeshQuality, MaxMeshQuality);
            if (!MeshQualityEnabled)
            {
                OnPropertyChanged(nameof(MeshQuality));
                return;
            }
            for (int i = 0; i < LODLevels.Length; i++)
            {
                int num2 = Math.Clamp(num - i, 0, 3);
                string text = LODLevels[i];
                App.FastFlags.SetPreset("Geometry.MeshLOD." + text, num2);
            }
            OnPropertyChanged(nameof(MeshQuality));
            OnPropertyChanged(nameof(MeshQualityEnabled));
        }
    }

    public bool MeshQualityEnabled
    {
        get { return App.FastFlags.GetPreset("Geometry.MeshLOD.L0") != null; }
        set
        {
            if (value == MeshQualityEnabled)
            {
                return;
            }
            if (value)
            {
                for (int i = 0; i < LODLevels.Length; i++)
                {
                    int level = Math.Clamp(MaxMeshQuality - i, 0, 3);
                    App.FastFlags.SetPreset("Geometry.MeshLOD." + LODLevels[i], level);
                }
            }
            else
            {
                string[] lODLevels = LODLevels;
                foreach (string text in lODLevels)
                {
                    App.FastFlags.SetPreset("Geometry.MeshLOD." + text, null);
                }
            }
            OnPropertyChanged(nameof(MeshQualityEnabled));
            OnPropertyChanged(nameof(MeshQuality));
        }
    }

    public IReadOnlyDictionary<MSAAMode, string?> MSAALevels => FastFlagManager.MSAAModes;

    public MSAAMode SelectedMSAALevel
    {
        get
        {
            return MSAALevels
                .FirstOrDefault<KeyValuePair<MSAAMode, string?>>(
                    (KeyValuePair<MSAAMode, string?> x) => x.Value == App.FastFlags.GetPreset("Rendering.MSAA1")
                )
                .Key;
        }
        set
        {
            App.FastFlags.SetPreset("Rendering.MSAA1", MSAALevels[value]);
        }
    }

    public IReadOnlyDictionary<TextureQuality, string?> TextureQualities => FastFlagManager.TextureQualityLevels;

    public TextureQuality SelectedTextureQuality
    {
        get
        {
            return TextureQualities
                .FirstOrDefault<KeyValuePair<TextureQuality, string?>>(
                    (KeyValuePair<TextureQuality, string?> x) => x.Value == App.FastFlags.GetPreset("Rendering.TextureQuality.Level")
                )
                .Key;
        }
        set
        {
            if (value == TextureQuality.Default)
            {
                App.FastFlags.SetPreset("Rendering.TextureQuality.Level", null);
                App.FastFlags.SetPreset("Rendering.TextureQuality.OverrideEnabled", null);
                return;
            }
            App.FastFlags.SetPreset("Rendering.TextureQuality.OverrideEnabled", "True");
            App.FastFlags.SetPreset("Rendering.TextureQuality.Level", TextureQualities[value]);
        }
    }

    public IReadOnlyDictionary<RenderingMode, string> RenderingModes => FastFlagManager.RenderingModes;

    public RenderingMode SelectedRenderingMode
    {
        get { return App.FastFlags.GetPresetEnum(RenderingModes, "Rendering.Mode", "True"); }
        set
        {
            App.FastFlags.SetPresetEnum("Rendering.Mode", value.ToString(), "True");
        }
    }

    public bool FixDisplayScaling
    {
        get { return App.FastFlags.GetPreset("Rendering.DisableScaling") == "True"; }
        set { App.FastFlags.SetPreset("Rendering.DisableScaling", value ? "True" : null); }
    }

    public int MinGrassDistance
    {
        get
        {
            if (!int.TryParse(App.FastFlags.GetPreset("Rendering.Nograss1"), out var result))
            {
                return 100;
            }
            return result;
        }
        set
        {
            App.FastFlags.SetPreset("Rendering.Nograss1", value.ToString());
            OnPropertyChanged(nameof(MinGrassDistance));
        }
    }

    public int MaxGrassDistance
    {
        get
        {
            if (!int.TryParse(App.FastFlags.GetPreset("Rendering.Nograss2"), out var result))
            {
                return 290;
            }
            return result;
        }
        set
        {
            App.FastFlags.SetPreset("Rendering.Nograss2", value.ToString());
            OnPropertyChanged(nameof(MaxGrassDistance));
        }
    }

    public IReadOnlyDictionary<int, string?> GrassMovementOptions { get; } =
        new Dictionary<int, string?>
        {
            { 0, "No Movement" },
            { 1, "Minimal Movement" },
            { 2, "Medium Movement" },
            { 3, "High Movement" },
            { 4, "Ultra Movement" },
            { 5, "Default Movement" },
        };

    public int SelectedGrassMovementFactor
    {
        get
        {
            if (int.TryParse(App.FastFlags.GetPreset("Grass.Movement"), out var result) && GrassMovementOptions.ContainsKey(result))
            {
                return result;
            }
            return 5;
        }
        set
        {
            if (value == 5)
            {
                App.FastFlags.SetPreset("Grass.Movement", null);
            }
            else
            {
                App.FastFlags.SetPreset("Grass.Movement", value.ToString());
            }
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedGrassMovementFactor)));
        }
    }

    public IReadOnlyDictionary<QualityLevel, string?> QualityLevels => FastFlagManager.QualityLevels;

    public QualityLevel SelectedQualityLevel
    {
        get
        {
            return FastFlagManager
                .QualityLevels.FirstOrDefault<KeyValuePair<QualityLevel, string?>>(
                    (KeyValuePair<QualityLevel, string?> x) => x.Value == App.FastFlags.GetPreset("Rendering.FrmQuality")
                )
                .Key;
        }
        set
        {
            if (value == QualityLevel.Disabled)
            {
                App.FastFlags.SetPreset("Rendering.FrmQuality", null);
            }
            else
            {
                App.FastFlags.SetPreset("Rendering.FrmQuality", FastFlagManager.QualityLevels[value]);
            }
        }
    }

    public bool EnableGraySky
    {
        get { return App.FastFlags.GetPreset("Graphic.GraySky") == "True"; }
        set { App.FastFlags.SetPreset("Graphic.GraySky", value ? "True" : null); }
    }

    public bool ResetConfiguration
    {
        get { return _preResetFlags != null; }
        set
        {
            if (value)
            {
                _preResetFlags = new Dictionary<string, object>(App.FastFlags.Prop);
                App.FastFlags.Prop.Clear();
            }
            else
            {
                App.FastFlags.Prop = _preResetFlags ?? App.FastFlags.Prop;
                _preResetFlags = null;
            }
            this.RequestPageReloadEvent?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyDictionary<string, string?>? CpuThreads => GetCpuThreads();

    public IReadOnlyDictionary<string, string?>? CpuCoreMinThreadCount => GetCpuCoreMinThreadCount();

    public IEnumerable? ProfileModes
    {
        get { return profileModes; }
        set { SetProperty(ref profileModes, value, nameof(ProfileModes)); }
    }

    public string SelectedProfileMods
    {
        get { return selectedProfileMods; }
        set { SetProperty(ref selectedProfileMods, value, nameof(SelectedProfileMods)); }
    }

    public bool IsWindows => Voidstrap.Utility.Platform.IsWindows;

    public bool NvidiaTabSupported => Voidstrap.Utility.Platform.IsWindows || Voidstrap.Utility.Platform.IsLinux;

    public bool HasNvidiaGpu => Voidstrap.Utility.GpuInventory.HasNvidia;

    public string NvidiaTabUnavailableReason =>
        Voidstrap.Utility.GpuInventory.HasNvidia
            ? string.Empty
            : "These options need an NVIDIA graphics card. Detected: " + Voidstrap.Utility.GpuInventory.Summary + ".";

    public int TextureParticleStyle
    {
        get => App.Settings.Prop.TextureParticleStyle;
        set
        {
            int v = Math.Min(value, 2);
            if (value < 0 || App.Settings.Prop.TextureParticleStyle == v)
                return;
            App.Settings.Prop.TextureParticleStyle = v;
            ApplyTextureMods();
            OnPropertyChanged(nameof(TextureParticleStyle));
            OnPropertyChanged(nameof(TextureParticleSquares));
        }
    }
    public bool TextureParticleSquares => App.Settings.Prop.TextureParticleStyle == 2;
    public double TextureParticleOpacity
    {
        get => App.Settings.Prop.TextureParticleOpacity;
        set
        {
            int v = (int)Math.Clamp(Math.Round(value), 10, 100);
            if (App.Settings.Prop.TextureParticleOpacity == v)
                return;
            App.Settings.Prop.TextureParticleOpacity = v;
            ApplyTextureMods();
            OnPropertyChanged(nameof(TextureParticleOpacity));
            OnPropertyChanged(nameof(TextureParticleOpacityDisplay));
        }
    }
    public string TextureParticleOpacityDisplay => App.Settings.Prop.TextureParticleOpacity + "%";
    public int TextureStudStyle
    {
        get => App.Settings.Prop.TextureStudStyle;
        set
        {
            int v = Math.Min(value, 1);
            if (value < 0 || App.Settings.Prop.TextureStudStyle == v)
                return;
            App.Settings.Prop.TextureStudStyle = v;
            ApplyTextureMods();
            OnPropertyChanged(nameof(TextureStudStyle));
            OnPropertyChanged(nameof(TextureStudFlat));
        }
    }
    public bool TextureStudFlat => App.Settings.Prop.TextureStudStyle == 1;
    public double TextureStudShade
    {
        get => App.Settings.Prop.TextureStudShade;
        set
        {
            int v = (int)Math.Clamp(Math.Round(value), 0, 100);
            if (App.Settings.Prop.TextureStudShade == v)
                return;
            App.Settings.Prop.TextureStudShade = v;
            ApplyTextureMods();
            OnPropertyChanged(nameof(TextureStudShade));
            OnPropertyChanged(nameof(TextureStudShadeDisplay));
        }
    }
    public string TextureStudShadeDisplay => App.Settings.Prop.TextureStudShade + "%";
    public bool TextureTerrainEnabled
    {
        get => App.Settings.Prop.TextureTerrainEnabled;
        set
        {
            if (App.Settings.Prop.TextureTerrainEnabled == value)
                return;
            App.Settings.Prop.TextureTerrainEnabled = value;
            ApplyTextureMods();
            OnPropertyChanged(nameof(TextureTerrainEnabled));
        }
    }
    public double TextureTerrainSize
    {
        get => App.Settings.Prop.TextureTerrainSize;
        set
        {
            double v = Math.Clamp(value, 0.05, 1);
            if (Math.Abs(App.Settings.Prop.TextureTerrainSize - v) < 0.001)
                return;
            App.Settings.Prop.TextureTerrainSize = v;
            ApplyTextureMods();
            OnPropertyChanged(nameof(TextureTerrainSize));
            OnPropertyChanged(nameof(TextureTerrainSizeDisplay));
        }
    }
    public string TextureTerrainSizeDisplay => App.Settings.Prop.TextureTerrainSize.ToString("0.00") + "x";
    public double TextureTerrainRepeat
    {
        get => App.Settings.Prop.TextureTerrainRepeat;
        set
        {
            double v = Math.Clamp(value, 1, 10);
            if (Math.Abs(App.Settings.Prop.TextureTerrainRepeat - v) < 0.01)
                return;
            App.Settings.Prop.TextureTerrainRepeat = v;
            ApplyTextureMods();
            OnPropertyChanged(nameof(TextureTerrainRepeat));
            OnPropertyChanged(nameof(TextureTerrainRepeatDisplay));
        }
    }
    public string TextureTerrainRepeatDisplay => App.Settings.Prop.TextureTerrainRepeat.ToString("0.#");
    public int TextureWaterStyle
    {
        get => App.Settings.Prop.TextureWaterStyle;
        set
        {
            int v = Math.Min(value, 5);
            if (value < 0 || App.Settings.Prop.TextureWaterStyle == v)
                return;
            App.Settings.Prop.TextureWaterStyle = v;
            ApplyTextureMods();
            RefreshWaterPreview();
            OnPropertyChanged(nameof(TextureWaterStyle));
            OnPropertyChanged(nameof(TextureWaterCustom));
            OnPropertyChanged(nameof(TextureWaterShowStrength));
            OnPropertyChanged(nameof(TextureWaterShowSpeed));
            OnPropertyChanged(nameof(TextureWaterShowFrame));
            OnPropertyChanged(nameof(TextureWaterShowGlitch));
        }
    }
    public bool TextureWaterCustom => App.Settings.Prop.TextureWaterStyle == 5;
    public bool TextureWaterShowStrength => App.Settings.Prop.TextureWaterStyle is 1 or 3 or 4 or 5;
    public bool TextureWaterShowSpeed => App.Settings.Prop.TextureWaterStyle is 1 or 4 or 5;
    public bool TextureWaterShowFrame => App.Settings.Prop.TextureWaterStyle == 3;
    public bool TextureWaterShowGlitch => App.Settings.Prop.TextureWaterStyle == 4;
    public double TextureWaterStrength
    {
        get => App.Settings.Prop.TextureWaterStrength;
        set
        {
            int v = (int)Math.Clamp(Math.Round(value), 0, 300);
            if (App.Settings.Prop.TextureWaterStrength == v)
                return;
            App.Settings.Prop.TextureWaterStrength = v;
            ApplyTextureMods();
            RefreshWaterPreview();
            OnPropertyChanged(nameof(TextureWaterStrength));
            OnPropertyChanged(nameof(TextureWaterStrengthDisplay));
        }
    }
    public string TextureWaterStrengthDisplay => App.Settings.Prop.TextureWaterStrength + "%";
    public double TextureWaterSpeed
    {
        get => App.Settings.Prop.TextureWaterSpeed;
        set
        {
            int v = (int)Math.Clamp(Math.Round(value), 1, 4);
            if (App.Settings.Prop.TextureWaterSpeed == v)
                return;
            App.Settings.Prop.TextureWaterSpeed = v;
            ApplyTextureMods();
            RefreshWaterPreview();
            OnPropertyChanged(nameof(TextureWaterSpeed));
            OnPropertyChanged(nameof(TextureWaterSpeedDisplay));
        }
    }
    public string TextureWaterSpeedDisplay => App.Settings.Prop.TextureWaterSpeed + "x";
    public double TextureWaterFrame
    {
        get => App.Settings.Prop.TextureWaterFrame;
        set
        {
            int v = (int)Math.Clamp(Math.Round(value), 1, 25);
            if (App.Settings.Prop.TextureWaterFrame == v)
                return;
            App.Settings.Prop.TextureWaterFrame = v;
            ApplyTextureMods();
            RefreshWaterPreview();
            OnPropertyChanged(nameof(TextureWaterFrame));
            OnPropertyChanged(nameof(TextureWaterFrameDisplay));
        }
    }
    public string TextureWaterFrameDisplay => App.Settings.Prop.TextureWaterFrame.ToString();
    public double TextureWaterGlitch
    {
        get => App.Settings.Prop.TextureWaterGlitch;
        set
        {
            int v = (int)Math.Clamp(Math.Round(value), 5, 100);
            if (App.Settings.Prop.TextureWaterGlitch == v)
                return;
            App.Settings.Prop.TextureWaterGlitch = v;
            ApplyTextureMods();
            RefreshWaterPreview();
            OnPropertyChanged(nameof(TextureWaterGlitch));
            OnPropertyChanged(nameof(TextureWaterGlitchDisplay));
        }
    }
    public string TextureWaterGlitchDisplay => App.Settings.Prop.TextureWaterGlitch + "%";
    private System.Windows.Media.Imaging.BitmapSource[]? _waterPreviewFrames;
    private int _waterPreviewIndex;
    private int _waterPreviewRequest;
    private string _waterPreviewStatus = "Building preview";
    public System.Windows.Media.ImageSource? TextureWaterPreview =>
        _waterPreviewFrames is { Length: > 0 } frames ? frames[_waterPreviewIndex % frames.Length] : null;
    public string TextureWaterPreviewStatus => _waterPreviewStatus;

    public void AdvanceWaterPreview()
    {
        if (_waterPreviewFrames is not { Length: > 0 } frames)
            return;
        _waterPreviewIndex = (_waterPreviewIndex + 1) % frames.Length;
        _waterPreviewStatus = "Frame " + (_waterPreviewIndex + 1) + " of " + frames.Length;
        OnPropertyChanged(nameof(TextureWaterPreview));
        OnPropertyChanged(nameof(TextureWaterPreviewStatus));
    }

    public async void RefreshWaterPreview()
    {
        int request = System.Threading.Interlocked.Increment(ref _waterPreviewRequest);
        var settings = App.Settings.Prop;
        int style = settings.TextureWaterStyle;
        int strength = settings.TextureWaterStrength;
        int speed = settings.TextureWaterSpeed;
        int frame = settings.TextureWaterFrame;
        int glitch = settings.TextureWaterGlitch;
        try
        {
            await System.Threading.Tasks.Task.Delay(250);
            if (request != _waterPreviewRequest)
                return;
            byte[][]? pixels = await System.Threading.Tasks.Task.Run(() =>
                FileModManager.BuildWaterPreview(style, strength, speed, frame, glitch)
            );
            if (request != _waterPreviewRequest)
                return;
            if (pixels is null)
            {
                _waterPreviewFrames = null;
                _waterPreviewStatus =
                    style == 5 ? "Choose images to see a preview"
                    : Voidstrap.Utility.Platform.IsLinux
                        ? (
                            Voidstrap.Platform.Linux.LinuxSoberProcessProbe.IsRunningNow()
                                ? "Close Roblox to load the water preview, Sober's files are read while it is closed"
                                : "The Sober water textures could not be read. Make sure Sober has downloaded Roblox, then try again"
                        )
                    : "The Roblox water textures could not be read. Start Roblox once and try again";
            }
            else
            {
                Dictionary<byte[], System.Windows.Media.Imaging.BitmapSource> made = new Dictionary<
                    byte[],
                    System.Windows.Media.Imaging.BitmapSource
                >(ReferenceEqualityComparer.Instance);
                System.Windows.Media.Imaging.BitmapSource[] frames = new System.Windows.Media.Imaging.BitmapSource[pixels.Length];
                for (int index = 0; index < pixels.Length; index++)
                {
                    if (!made.TryGetValue(pixels[index], out System.Windows.Media.Imaging.BitmapSource? bitmap))
                    {
                        bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                            256,
                            256,
                            96,
                            96,
                            System.Windows.Media.PixelFormats.Bgra32,
                            null,
                            pixels[index],
                            256 * 4
                        );
                        bitmap.Freeze();
                        made[pixels[index]] = bitmap;
                    }
                    frames[index] = bitmap;
                }
                _waterPreviewFrames = frames;
                _waterPreviewIndex %= frames.Length;
                _waterPreviewStatus = "Frame " + (_waterPreviewIndex + 1) + " of " + frames.Length;
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FastFlagsViewModel::RefreshWaterPreview", ex);
            _waterPreviewFrames = null;
            _waterPreviewStatus = "The water preview could not be built";
        }
        OnPropertyChanged(nameof(TextureWaterPreview));
        OnPropertyChanged(nameof(TextureWaterPreviewStatus));
    }

    private int _waterFrameCount = -1;

    private int WaterFrameCount
    {
        get
        {
            if (_waterFrameCount < 0)
                _waterFrameCount = FileModManager.CountWaterFrames();
            return _waterFrameCount;
        }
    }

    public bool TextureWaterHasFrames => WaterFrameCount > 0;
    public string TextureWaterFramesSummary
    {
        get
        {
            int count = WaterFrameCount;
            return count == 0
                ? "Pick a GIF or up to 25 images. Bright areas become ripple peaks"
                : count + (count == 1 ? " image" : " images") + " chosen. Bright areas become ripple peaks";
        }
    }
    private CommunityToolkit.Mvvm.Input.RelayCommand? _chooseWaterFramesCommand;
    public System.Windows.Input.ICommand ChooseWaterFramesCommand =>
        _chooseWaterFramesCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(ChooseWaterFrames);
    private CommunityToolkit.Mvvm.Input.RelayCommand? _clearWaterFramesCommand;
    public System.Windows.Input.ICommand ClearWaterFramesCommand =>
        _clearWaterFramesCommand ??= new CommunityToolkit.Mvvm.Input.RelayCommand(ClearWaterFrames);

    private async void ChooseWaterFrames()
    {
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose water animation images",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.jfif;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.tga;*.qoi|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
            return;
        string[] names = dialog.FileNames;
        Array.Sort(names, StringComparer.OrdinalIgnoreCase);
        try
        {
            await System.Threading.Tasks.Task.Run(() => FileModManager.ImportWaterFrames(names));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException("FastFlagsViewModel::ChooseWaterFrames", ex);
            Frontend.ShowMessageBox("Could not use those images: " + ex.Message, System.Windows.MessageBoxImage.Error);
        }
        _waterFrameCount = -1;
        OnPropertyChanged(nameof(TextureWaterFramesSummary));
        OnPropertyChanged(nameof(TextureWaterHasFrames));
        ApplyTextureMods();
        RefreshWaterPreview();
    }

    private void ClearWaterFrames()
    {
        try
        {
            FileModManager.ClearWaterFrames();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FastFlagsViewModel::ClearWaterFrames", ex);
        }
        _waterFrameCount = -1;
        OnPropertyChanged(nameof(TextureWaterFramesSummary));
        OnPropertyChanged(nameof(TextureWaterHasFrames));
        ApplyTextureMods();
        RefreshWaterPreview();
    }

    private static void ApplyTextureMods()
    {
        App.Settings.SaveDeferred();
        FileModManager.QueueApplyFromSettings();
    }

    public event EventHandler? RequestPageReloadEvent;

    public event EventHandler? OpenFlagEditorEvent;

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OpenFastFlagEditor()
    {
        this.OpenFlagEditorEvent?.Invoke(this, EventArgs.Empty);
    }

    public bool GetFlagAsBool(string flagKey, string falseValue = "False")
    {
        return App.FastFlags.GetPreset(flagKey) != falseValue;
    }

    public void SetFlagFromBool(string flagKey, bool value, string falseValue = "False")
    {
        App.FastFlags.SetPreset(flagKey, value ? null : falseValue);
    }

    public static IReadOnlyDictionary<string, string?> GetCpuThreads()
    {
        Dictionary<string, string?> dictionary = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Automatic"] = null };
        try
        {
            int logicalProcessorCount = SystemInfo.GetLogicalProcessorCount();
            if (logicalProcessorCount > 0)
            {
                for (int i = 1; i <= logicalProcessorCount; i++)
                {
                    string text = i.ToString();
                    dictionary[text] = text;
                }
            }
            else
            {
                App.Logger.WriteLine("FFlagPresets::GetCpuThreads", "Logical processor count returned 0.");
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("FFlagPresets::GetCpuThreads", "Failed to get CPU thread count: " + ex.Message);
        }
        return dictionary;
    }

    public static IReadOnlyDictionary<string, string?> GetCpuCoreMinThreadCount()
    {
        Dictionary<string, string?> dictionary = new Dictionary<string, string?>();
        dictionary.Add("Automatic", null);
        try
        {
            int logicalProcessorCount = SystemInfo.GetLogicalProcessorCount();
            for (int i = 1; i <= logicalProcessorCount; i++)
            {
                dictionary.Add(i.ToString(), i.ToString());
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("FFlagPresets::GetCpuCoreMinThreadCount", "Failed to get CPU thread count: " + ex.Message);
        }
        return dictionary;
    }

    protected new bool SetProperty<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
    {
        if (!object.Equals(field, newValue))
        {
            field = newValue;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
        return false;
    }
}
