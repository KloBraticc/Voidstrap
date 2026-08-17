using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Voidstrap.Integrations.RiShade;

namespace Voidstrap.UI.ViewModels.Settings
{
    public class RiShadeViewModel : Voidstrap.UI.ViewModels.NotifyPropertyChangedViewModel
    {
        private static RiShadeSettings C => RiShadeSettings.Current;

        private void T(string name)
        {
            RiShadeSettings.Touch();
            OnPropertyChanged(name);
        }

        public bool GradeEnabled { get => C.GradeEnabled; set { C.GradeEnabled = value; T(nameof(GradeEnabled)); } }
        public double Brightness { get => C.Brightness; set { C.Brightness = (float)value; T(nameof(Brightness)); } }
        public double Gamma { get => C.Gamma; set { C.Gamma = (float)value; T(nameof(Gamma)); } }
        public double HueShift { get => C.HueShift; set { C.HueShift = (float)value; T(nameof(HueShift)); } }
        public int ColorTempIndex { get => C.ColorTemp; set { C.ColorTemp = value; T(nameof(ColorTempIndex)); } }
        public double TempCustomR { get => C.ColorTempCustom[0]; set { C.ColorTempCustom[0] = (float)value; T(nameof(TempCustomR)); } }
        public double TempCustomG { get => C.ColorTempCustom[1]; set { C.ColorTempCustom[1] = (float)value; T(nameof(TempCustomG)); } }
        public double TempCustomB { get => C.ColorTempCustom[2]; set { C.ColorTempCustom[2] = (float)value; T(nameof(TempCustomB)); } }
        public double BalanceR { get => C.ColorBalance[0]; set { C.ColorBalance[0] = (float)value; T(nameof(BalanceR)); } }
        public double BalanceG { get => C.ColorBalance[1]; set { C.ColorBalance[1] = (float)value; T(nameof(BalanceG)); } }
        public double BalanceB { get => C.ColorBalance[2]; set { C.ColorBalance[2] = (float)value; T(nameof(BalanceB)); } }
        public double LiftR { get => C.Lift[0]; set { C.Lift[0] = (float)value; T(nameof(LiftR)); } }
        public double LiftG { get => C.Lift[1]; set { C.Lift[1] = (float)value; T(nameof(LiftG)); } }
        public double LiftB { get => C.Lift[2]; set { C.Lift[2] = (float)value; T(nameof(LiftB)); } }
        public double GainR { get => C.Gain[0]; set { C.Gain[0] = (float)value; T(nameof(GainR)); } }
        public double GainG { get => C.Gain[1]; set { C.Gain[1] = (float)value; T(nameof(GainG)); } }
        public double GainB { get => C.Gain[2]; set { C.Gain[2] = (float)value; T(nameof(GainB)); } }

        public bool TonemapEnabled { get => C.TonemapEnabled; set { C.TonemapEnabled = value; T(nameof(TonemapEnabled)); } }
        public int TonemapModeIndex { get => C.TonemapMode; set { C.TonemapMode = value; T(nameof(TonemapModeIndex)); } }
        public double TonemapExposure { get => C.TonemapExposure; set { C.TonemapExposure = (float)value; T(nameof(TonemapExposure)); } }
        public double TonemapWhitepoint { get => C.TonemapWhitepoint; set { C.TonemapWhitepoint = (float)value; T(nameof(TonemapWhitepoint)); } }

        public bool VignetteEnabled { get => C.VignetteEnabled; set { C.VignetteEnabled = value; T(nameof(VignetteEnabled)); } }
        public double VignetteStrength { get => C.VignetteStrength; set { C.VignetteStrength = (float)value; T(nameof(VignetteStrength)); } }
        public double VignetteFeather { get => C.VignetteFeather; set { C.VignetteFeather = (float)value; T(nameof(VignetteFeather)); } }
        public double VignetteCenterX { get => C.VignetteCenterX; set { C.VignetteCenterX = (float)value; T(nameof(VignetteCenterX)); } }
        public double VignetteCenterY { get => C.VignetteCenterY; set { C.VignetteCenterY = (float)value; T(nameof(VignetteCenterY)); } }

        public bool SharpenEnabled { get => C.SharpenEnabled; set { C.SharpenEnabled = value; T(nameof(SharpenEnabled)); } }
        public double SharpenStrength { get => C.SharpenStrength; set { C.SharpenStrength = (float)value; T(nameof(SharpenStrength)); } }
        public double SharpenRadius { get => C.SharpenRadius; set { C.SharpenRadius = (float)value; T(nameof(SharpenRadius)); } }
        public double SharpenClamp { get => C.SharpenClamp; set { C.SharpenClamp = (float)value; T(nameof(SharpenClamp)); } }

        public bool BloomEnabled { get => C.BloomEnabled; set { C.BloomEnabled = value; T(nameof(BloomEnabled)); } }
        public double BloomStrength { get => C.BloomStrength; set { C.BloomStrength = (float)value; T(nameof(BloomStrength)); } }
        public double BloomPasses { get => C.BloomPasses; set { C.BloomPasses = (int)Math.Round(value); T(nameof(BloomPasses)); } }
        public double BloomThreshold { get => C.BloomThreshold; set { C.BloomThreshold = (float)value; T(nameof(BloomThreshold)); } }
        public double BloomRadius { get => C.BloomRadius; set { C.BloomRadius = (float)value; T(nameof(BloomRadius)); } }
        public double BloomTintR { get => C.BloomTint[0]; set { C.BloomTint[0] = (float)value; T(nameof(BloomTintR)); } }
        public double BloomTintG { get => C.BloomTint[1]; set { C.BloomTint[1] = (float)value; T(nameof(BloomTintG)); } }
        public double BloomTintB { get => C.BloomTint[2]; set { C.BloomTint[2] = (float)value; T(nameof(BloomTintB)); } }

        public bool ChromaEnabled { get => C.ChromaEnabled; set { C.ChromaEnabled = value; T(nameof(ChromaEnabled)); } }
        public double ChromaStrength { get => C.ChromaStrength; set { C.ChromaStrength = (float)value; T(nameof(ChromaStrength)); } }
        public bool ChromaRadial { get => C.ChromaRadial; set { C.ChromaRadial = value; T(nameof(ChromaRadial)); } }

        public bool GrainEnabled { get => C.GrainEnabled; set { C.GrainEnabled = value; T(nameof(GrainEnabled)); } }
        public double GrainStrength { get => C.GrainStrength; set { C.GrainStrength = (float)value; T(nameof(GrainStrength)); } }
        public double GrainSize { get => C.GrainSize; set { C.GrainSize = (float)value; T(nameof(GrainSize)); } }
        public bool GrainColored { get => C.GrainColored; set { C.GrainColored = value; T(nameof(GrainColored)); } }

        public bool DofEnabled { get => C.DofEnabled; set { C.DofEnabled = value; T(nameof(DofEnabled)); } }
        public double DofStrength { get => C.DofStrength; set { C.DofStrength = (float)value; T(nameof(DofStrength)); } }
        public double DofFocusRange { get => C.DofFocusRange; set { C.DofFocusRange = (float)value; T(nameof(DofFocusRange)); } }
        public double DofFeather { get => C.DofFeather; set { C.DofFeather = (float)value; T(nameof(DofFeather)); } }

        public bool SsrEnabled { get => C.SsrEnabled; set { C.SsrEnabled = value; T(nameof(SsrEnabled)); } }
        public double SsrIntensity { get => C.SsrIntensity; set { C.SsrIntensity = (float)value; T(nameof(SsrIntensity)); } }
        public double SsrGlossiness { get => C.SsrGlossiness; set { C.SsrGlossiness = (float)value; T(nameof(SsrGlossiness)); } }
        public double SsrReflectivity { get => C.SsrReflectivity; set { C.SsrReflectivity = (float)value; T(nameof(SsrReflectivity)); } }
        public double SsrSheen { get => C.SsrSheen; set { C.SsrSheen = (float)value; T(nameof(SsrSheen)); } }
        public double SsrDistance { get => C.SsrDistance; set { C.SsrDistance = (float)value; T(nameof(SsrDistance)); } }

        public bool AoEnabled { get => C.AoEnabled; set { C.AoEnabled = value; T(nameof(AoEnabled)); } }
        public double AoStrength { get => C.AoStrength; set { C.AoStrength = (float)value; T(nameof(AoStrength)); } }
        public double AoRadius { get => C.AoRadius; set { C.AoRadius = (float)value; T(nameof(AoRadius)); } }
        public int AoSamplesIndex { get => C.AoSamplesIndex; set { C.AoSamplesIndex = value; T(nameof(AoSamplesIndex)); } }

        public double ClarityStrength { get => C.ClarityStrength; set { C.ClarityStrength = (float)value; T(nameof(ClarityStrength)); } }
        public bool DebandEnabled { get => C.DebandEnabled; set { C.DebandEnabled = value; T(nameof(DebandEnabled)); } }
        public double DebandStrength { get => C.DebandStrength; set { C.DebandStrength = (float)value; T(nameof(DebandStrength)); } }
        public double GiStrength { get => C.GiStrength; set { C.GiStrength = (float)value; T(nameof(GiStrength)); } }
        public double GiRadius { get => C.GiRadius; set { C.GiRadius = (float)value; T(nameof(GiRadius)); } }
        public double FogStrength { get => C.FogStrength; set { C.FogStrength = (float)value; T(nameof(FogStrength)); } }
        public double FogStart { get => C.FogStart; set { C.FogStart = (float)value; T(nameof(FogStart)); } }
        public double FogBrightness { get => C.FogBrightness; set { C.FogBrightness = (float)value; T(nameof(FogBrightness)); } }
        public double AmbientStrength { get => C.AmbientStrength; set { C.AmbientStrength = (float)value; T(nameof(AmbientStrength)); } }
        public bool EyeAdaptEnabled { get => C.EyeAdaptEnabled; set { C.EyeAdaptEnabled = value; T(nameof(EyeAdaptEnabled)); } }
        public double EyeAdaptStrength { get => C.EyeAdaptStrength; set { C.EyeAdaptStrength = (float)value; T(nameof(EyeAdaptStrength)); } }

        public bool PerfMode { get => C.PerfMode; set { C.PerfMode = value; T(nameof(PerfMode)); } }
        public int DebugViewIndex { get => C.DebugView; set { C.DebugView = value; T(nameof(DebugViewIndex)); } }
        public string[] DebugViewNames => RiShadeSettings.DebugViewNames;
        public int AiQualityIndex { get => C.AiQuality; set { C.AiQuality = value; T(nameof(AiQualityIndex)); } }
        public string[] AiQualityNames => RiShadeSettings.AiQualityNames;
        public int RenderScaleIndex { get => C.RenderScaleIndex; set { C.RenderScaleIndex = value; T(nameof(RenderScaleIndex)); } }
        public string[] RenderScaleNames => RiShadeSettings.RenderScaleNames;

        public string[] ColorTempNames => RiShadeSettings.ColorTempNames;
        public string[] TonemapNames => RiShadeSettings.TonemapNames;
        public string[] AoSampleNames { get; } = RiShadeSettings.AoSampleCounts.Select(x => x.ToString()).ToArray();
        public string[] BuiltinPresets => RiShadeSettings.PresetNames;

        private string? _selectedBuiltinPreset;
        public string? SelectedBuiltinPreset
        {
            get => _selectedBuiltinPreset;
            set
            {
                if (value == null)
                    return;
                _selectedBuiltinPreset = value;
                _selectedCustomPreset = null;
                RiShadeSettings.ApplyPreset(value);
                RefreshAll();
            }
        }

        public ObservableCollection<string> CustomPresets { get; } = new ObservableCollection<string>();

        private string? _selectedCustomPreset;
        public string? SelectedCustomPreset
        {
            get => _selectedCustomPreset;
            set
            {
                _selectedCustomPreset = value;
                if (value == null)
                    return;
                _selectedBuiltinPreset = null;
                LoadCustomPreset(value);
            }
        }

        private string _presetName = "";
        public string PresetName { get => _presetName; set { _presetName = value; OnPropertyChanged(nameof(PresetName)); } }

        public ICommand SavePresetCommand { get; }
        public ICommand DeletePresetCommand { get; }
        public ICommand ImportPresetCommand { get; }
        public ICommand ExportPresetCommand { get; }
        public ICommand ImportReShadeCommand { get; }

        private void ImportReShade()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ReShade preset (*.ini)|*.ini" };
                if (dlg.ShowDialog() != true)
                    return;
                var loaded = RiShadeReShadeImport.Parse(dlg.FileName, out string report);
                RiShadeSettings.ReplaceCurrent(loaded, "ReShade preset " + Path.GetFileNameWithoutExtension(dlg.FileName));
                RefreshAll();
                App.Logger.WriteLine("RiShade", "ReShade preset imported. " + report.Replace('\n', ' '));
                System.Windows.MessageBox.Show(report, "ReShade preset import", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeViewModel::ImportReShade", ex);
            }
        }

        private static string PresetsDir
        {
            get
            {
                string dir = Path.Combine(Paths.RiShade, "CreatedPresets");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public RiShadeViewModel()
        {
            SavePresetCommand = new RelayCommand(SavePreset);
            DeletePresetCommand = new RelayCommand(DeletePreset);
            ImportPresetCommand = new RelayCommand(ImportPreset);
            ExportPresetCommand = new RelayCommand(ExportPreset);
            ImportReShadeCommand = new RelayCommand(ImportReShade);
            RefreshCustomPresetList();
        }

        private void RefreshCustomPresetList()
        {
            try
            {
                CustomPresets.Clear();
                foreach (string f in Directory.GetFiles(PresetsDir, "*.json").OrderBy(x => x))
                    CustomPresets.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeViewModel::RefreshCustomPresetList", ex);
            }
        }

        private static string SanitizeName(string name)
        {
            foreach (char bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');
            return name.Trim();
        }

        private void SavePreset()
        {
            try
            {
                string name = SanitizeName(PresetName);
                if (string.IsNullOrWhiteSpace(name))
                    return;
				Voidstrap.Utility.JsonFile.SerializeAtomic(Path.Combine(PresetsDir, name + ".json"), RiShadeSettings.Current, Voidstrap.Utility.JsonOptions.Indented);
                App.Logger.WriteLine("RiShade", "Saved custom preset " + name);
                RefreshCustomPresetList();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeViewModel::SavePreset", ex);
            }
        }

        private void DeletePreset()
        {
            try
            {
                if (_selectedCustomPreset == null)
                    return;
                string path = Path.Combine(PresetsDir, SanitizeName(_selectedCustomPreset) + ".json");
                if (File.Exists(path))
                    File.Delete(path);
                _selectedCustomPreset = null;
                OnPropertyChanged(nameof(SelectedCustomPreset));
                RefreshCustomPresetList();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeViewModel::DeletePreset", ex);
            }
        }

        private void LoadCustomPreset(string name)
        {
            try
            {
                string path = Path.Combine(PresetsDir, SanitizeName(name) + ".json");
                if (!File.Exists(path))
                    return;
				var loaded = Voidstrap.Utility.JsonFile.Deserialize<RiShadeSettings>(path, Voidstrap.Utility.JsonOptions.Tolerant, 4194304);
                RiShadeSettings.ReplaceCurrent(loaded, "custom preset " + name);
                RefreshAll();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeViewModel::LoadCustomPreset", ex);
            }
        }

        private void ImportPreset()
        {
            try
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "RiShade preset (*.json)|*.json" };
                if (dlg.ShowDialog() != true)
                    return;
				var loaded = Voidstrap.Utility.JsonFile.Deserialize<RiShadeSettings>(dlg.FileName, Voidstrap.Utility.JsonOptions.Tolerant, 4194304);
                string dest = Path.Combine(PresetsDir, Path.GetFileName(dlg.FileName));
                File.Copy(dlg.FileName, dest, true);
                RiShadeSettings.ReplaceCurrent(loaded, "imported preset " + Path.GetFileNameWithoutExtension(dlg.FileName));
                RefreshCustomPresetList();
                RefreshAll();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeViewModel::ImportPreset", ex);
            }
        }

        private void ExportPreset()
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "RiShade preset (*.json)|*.json", FileName = "MyRiShadePreset.json" };
                if (dlg.ShowDialog() != true)
                    return;
				Voidstrap.Utility.JsonFile.SerializeAtomic(dlg.FileName, RiShadeSettings.Current, Voidstrap.Utility.JsonOptions.Indented, false);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeViewModel::ExportPreset", ex);
            }
        }

        public void RefreshAll()
        {
            OnPropertyChanged(string.Empty);
        }
    }
}
