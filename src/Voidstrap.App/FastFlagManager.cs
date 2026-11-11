using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Voidstrap.Enums.FlagPresets;
using Voidstrap.UI;

namespace Voidstrap;

public class FastFlagManager : JsonManager<Dictionary<string, object>>
{
    public static readonly IReadOnlyDictionary<string, string> PresetFlags = new Dictionary<string, string>
    {
        { "Rendering.FRMQualityOverride", "DFIntDebugFRMQualityLevelOverride" },
        { "Rendering.ManualFullscreen", "FFlagHandleAltEnterFullscreenManually" },
        { "Geometry.MeshLOD.L0", "DFIntCSGLevelOfDetailSwitchingDistance" },
        { "Geometry.MeshLOD.L12", "DFIntCSGLevelOfDetailSwitchingDistanceL12" },
        { "Geometry.MeshLOD.L23", "DFIntCSGLevelOfDetailSwitchingDistanceL23" },
        { "Geometry.MeshLOD.L34", "DFIntCSGLevelOfDetailSwitchingDistanceL34" },
        { "Geometry.MeshDistance.L0", "DFIntCSGLevelOfDetailSwitchingDistance" },
        { "Geometry.MeshDistance.L12", "DFIntCSGLevelOfDetailSwitchingDistanceL12" },
        { "Geometry.MeshDistance.L23", "DFIntCSGLevelOfDetailSwitchingDistanceL23" },
        { "Geometry.MeshDistance.L34", "DFIntCSGLevelOfDetailSwitchingDistanceL34" },
        { "Rendering.FrmQuality", "DFIntDebugFRMQualityLevelOverride" },
        { "Rendering.RemoveGrass1", "FIntFRMMinGrassDistance" },
        { "Rendering.RemoveGrass2", "FIntFRMMaxGrassDistance" },
        { "Graphic.GraySky", "FFlagDebugSkyGray" },
        { "Rendering.Shaders2", "DFIntDebugFRMQualityLevelOverride" },
        { "Rendering.DisableScaling", "DFFlagDisableDPIScale" },
        { "Rendering.MSAA1", "FIntDebugForceMSAASamples" },
        { "Grass.Movement", "FIntGrassMovementReducedMotionFactor" },
        { "Rendering.Mode.D3D11", "FFlagDebugGraphicsPreferD3D11" },
        { "Rendering.Mode.Vulkan", "FFlagDebugGraphicsPreferVulkan" },
        { "Rendering.Mode.OpenGL", "FFlagDebugGraphicsPreferOpenGL" },
        { "Rendering.LowPolyMeshes1", "DFIntCSGLevelOfDetailSwitchingDistance" },
        { "Rendering.LowPolyMeshes2", "DFIntCSGLevelOfDetailSwitchingDistanceL12" },
        { "Rendering.LowPolyMeshes3", "DFIntCSGLevelOfDetailSwitchingDistanceL23" },
        { "Rendering.LowPolyMeshes4", "DFIntCSGLevelOfDetailSwitchingDistanceL34" },
        { "Rendering.TextureQuality.Level", "DFIntTextureQualityOverride" },
        { "Rendering.TextureQuality.OverrideEnabled", "DFFlagTextureQualityOverrideEnabled" },
        { "Rendering.Pause.Voxelizer", "DFFlagDebugPauseVoxelizer" },
        { "Rendering.Nograss1", "FIntFRMMinGrassDistance" },
        { "Rendering.Nograss2", "FIntFRMMaxGrassDistance" },
    };

    public override string ClassName => "FastFlagManager";

    public override string LOG_IDENT_CLASS => ClassName;

    public override string BackupsLocation => Paths.SavedBackups;

    public override string FileLocation => Path.Combine(Paths.Mods, "ClientSettings", "ClientAppSettings.json");

    public bool Changed
    {
        get
        {
            if (base.OriginalProp.Count != base.Prop.Count)
            {
                return true;
            }
            foreach (KeyValuePair<string, object> pair in base.Prop)
            {
                if (
                    !base.OriginalProp.TryGetValue(pair.Key, out object? original)
                    || (original?.ToString() ?? string.Empty) != (pair.Value?.ToString() ?? string.Empty)
                )
                {
                    return true;
                }
            }
            return false;
        }
    }

    public static IReadOnlyDictionary<RenderingMode, string> RenderingModes { get; } =
        new Dictionary<RenderingMode, string>
        {
            { RenderingMode.Default, "None" },
            { RenderingMode.D3D11, "D3D11" },
            { RenderingMode.Vulkan, "Vulkan" },
            { RenderingMode.OpenGL, "OpenGL" },
        };

    public static IReadOnlyDictionary<ProfileMode, string> ProfileModes { get; } =
        new Dictionary<ProfileMode, string>
        {
            { ProfileMode.Default, "None" },
            { ProfileMode.Voidstrap, "Voidstraps Official" },
            { ProfileMode.Stoof, "Stoofs" },
        };

    public static IReadOnlyDictionary<MSAAMode, string?> MSAAModes { get; } =
        new Dictionary<MSAAMode, string?>
        {
            { MSAAMode.Default, null },
            { MSAAMode.x1, "1" },
            { MSAAMode.x2, "2" },
            { MSAAMode.x4, "4" },
            { MSAAMode.x8, "8" },
        };

    public static IReadOnlyDictionary<TextureQuality, string?> TextureQualityLevels { get; } =
        new Dictionary<TextureQuality, string?>
        {
            { TextureQuality.Default, null },
            { TextureQuality.Lowest, "0" },
            { TextureQuality.Low, "1" },
            { TextureQuality.Medium, "2" },
            { TextureQuality.High, "3" },
        };

    public static IReadOnlyDictionary<QualityLevel, string?> QualityLevels { get; } =
        new Dictionary<QualityLevel, string?>
        {
            { QualityLevel.Disabled, null },
            { QualityLevel.Level1, "1" },
            { QualityLevel.Level2, "2" },
            { QualityLevel.Level3, "3" },
            { QualityLevel.Level4, "4" },
            { QualityLevel.Level5, "5" },
            { QualityLevel.Level6, "6" },
            { QualityLevel.Level7, "7" },
            { QualityLevel.Level8, "8" },
            { QualityLevel.Level9, "9" },
            { QualityLevel.Level10, "10" },
            { QualityLevel.Level11, "11" },
            { QualityLevel.Level12, "12" },
            { QualityLevel.Level13, "13" },
            { QualityLevel.Level14, "14" },
            { QualityLevel.Level15, "15" },
            { QualityLevel.Level16, "16" },
            { QualityLevel.Level17, "17" },
            { QualityLevel.Level18, "18" },
            { QualityLevel.Level19, "19" },
            { QualityLevel.Level20, "20" },
            { QualityLevel.Level21, "21" },
        };

    public void SetValue(string key, object? value)
    {
        if (value == null)
        {
            if (base.Prop.Remove(key))
            {
                App.Logger.WriteLine("FastFlagManager::SetValue", "Deletion of '" + key + "' is pending");
            }
            return;
        }
        string newValue = value.ToString() ?? string.Empty;
        if (base.Prop.TryGetValue(key, out object? existing))
        {
            if ((existing?.ToString() ?? string.Empty) == newValue)
            {
                return;
            }
            App.Logger.WriteLine("FastFlagManager::SetValue", $"Changing of '{key}' from '{existing}' to '{newValue}' is pending");
        }
        else
        {
            App.Logger.WriteLine("FastFlagManager::SetValue", $"Setting of '{key}' to '{newValue}' is pending");
        }
        base.Prop[key] = newValue;
    }

    public string? GetValue(string key)
    {
        if (base.Prop.TryGetValue(key, out object? value) && value != null)
        {
            return value.ToString();
        }
        return null;
    }

    public void SetPreset(string prefix, object? value)
    {
        foreach (
            KeyValuePair<string, string> item in PresetFlags.Where<KeyValuePair<string, string>>(
                (KeyValuePair<string, string> x) => x.Key.StartsWith(prefix, StringComparison.Ordinal)
            )
        )
        {
            SetValue(item.Value, value);
        }
    }

    public void SetPresetEnum(string prefix, string target, object? value)
    {
        foreach (
            KeyValuePair<string, string> item in PresetFlags.Where<KeyValuePair<string, string>>(
                (KeyValuePair<string, string> x) => x.Key.StartsWith(prefix, StringComparison.Ordinal)
            )
        )
        {
            if (item.Key.StartsWith(prefix + "." + target, StringComparison.Ordinal))
            {
                SetValue(item.Value, value);
            }
            else
            {
                SetValue(item.Value, null);
            }
        }
    }

    public string? GetPreset(string name)
    {
        if (PresetFlags.TryGetValue(name, out string? flag))
        {
            return GetValue(flag);
        }
        App.Logger.WriteLine("FastFlagManager::GetPreset", "Could not find preset " + name);
        return null;
    }

    public T GetPresetEnum<T>(IReadOnlyDictionary<T, string> mapping, string prefix, string value)
        where T : Enum
    {
        foreach (KeyValuePair<T, string> item in mapping)
        {
            if (!(item.Value == "None") && GetPreset(prefix + "." + item.Value) == value)
            {
                return item.Key;
            }
        }
        return mapping.First().Key;
    }

    public override void Save()
    {
        foreach (string key in base.Prop.Keys.ToList())
        {
            base.Prop[key] = base.Prop[key]?.ToString() ?? string.Empty;
        }
        base.Save();
        base.OriginalProp = new Dictionary<string, object>(base.Prop);
    }

    public override void Load(bool alertFailure = false)
    {
        base.Load(alertFailure);
        base.OriginalProp = base.Prop.ToDictionary<KeyValuePair<string, object>, string, object>(
            (KeyValuePair<string, object> pair) => pair.Key,
            (KeyValuePair<string, object> pair) => pair.Value?.ToString() ?? string.Empty
        );
        if (GetPreset("Rendering.ManualFullscreen") is null)
            SetPreset("Rendering.ManualFullscreen", "False");
    }

    public void DeleteBackup(string Backup)
    {
        if (string.IsNullOrWhiteSpace(Backup))
        {
            return;
        }
        try
        {
            string text = Paths.SavedBackups;
            Directory.CreateDirectory(text);
            string path = Path.Combine(text, Path.GetFileName(Backup));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox("Error deleting backup: " + ex.Message, MessageBoxImage.Hand);
        }
    }

    public void RestoreAssetWarpPreloadFlags()
    {
        if (!App.Settings.Prop.AssetWarpPreloadFlagsOwned)
            return;
        foreach (KeyValuePair<string, string?> backup in App.Settings.Prop.AssetWarpPreloadFlagBackup)
        {
            SetValue(backup.Key, backup.Value);
        }
        App.Settings.Prop.AssetWarpPreloadFlagsOwned = false;
        App.Settings.Prop.AssetWarpPreloadFlagBackup.Clear();
        App.Settings.SaveDeferred();
    }
}
