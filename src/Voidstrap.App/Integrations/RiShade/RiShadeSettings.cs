using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Voidstrap.Integrations.RiShade
{
    public class RiShadeSettings
    {
        public bool GradeEnabled { get; set; } = false;
        public float Brightness { get; set; } = 0.0f;
        public float Gamma { get; set; } = 1.0f;
        public float HueShift { get; set; } = 0.0f;
        public float[] Lift { get; set; } = [0f, 0f, 0f];
        public float[] Gain { get; set; } = [1f, 1f, 1f];
        public float[] ColorBalance { get; set; } = [1f, 1f, 1f];
        public int ColorTemp { get; set; } = 1;
        public float[] ColorTempCustom { get; set; } = [1f, 1f, 1f];

        public bool TonemapEnabled { get; set; } = false;
        public int TonemapMode { get; set; } = 0;
        public float TonemapExposure { get; set; } = 1.0f;
        public float TonemapWhitepoint { get; set; } = 4.0f;

        public bool VignetteEnabled { get; set; } = false;
        public float VignetteStrength { get; set; } = 0.5f;
        public float VignetteFeather { get; set; } = 1.2f;
        public float VignetteCenterX { get; set; } = 0.0f;
        public float VignetteCenterY { get; set; } = 0.0f;

        public bool SharpenEnabled { get; set; } = false;
        public float SharpenStrength { get; set; } = 0.8f;
        public float SharpenRadius { get; set; } = 1.0f;
        public float SharpenClamp { get; set; } = 0.08f;

        public bool BloomEnabled { get; set; } = false;
        public float BloomStrength { get; set; } = 0.2f;
        public int BloomPasses { get; set; } = 3;
        public float BloomThreshold { get; set; } = 0.7f;
        public float BloomRadius { get; set; } = 1.5f;
        public float[] BloomTint { get; set; } = [1f, 1f, 1f];

        public bool ChromaEnabled { get; set; } = false;
        public float ChromaStrength { get; set; } = 0.003f;
        public bool ChromaRadial { get; set; } = true;

        public bool GrainEnabled { get; set; } = false;
        public float GrainStrength { get; set; } = 0.04f;
        public float GrainSize { get; set; } = 1.0f;
        public bool GrainColored { get; set; } = false;

        public bool DofEnabled { get; set; } = false;
        public float DofStrength { get; set; } = 0.6f;
        public float DofFocusRange { get; set; } = 0.25f;
        public float DofFeather { get; set; } = 0.4f;

        public bool SsrEnabled { get; set; } = false;
        public float SsrIntensity { get; set; } = 0.55f;
        public float SsrGlossiness { get; set; } = 0.92f;
        public float SsrReflectivity { get; set; } = 0.3f;
        public float SsrDistance { get; set; } = 0.6f;
        public float SsrSheen { get; set; } = 0.15f;
        public bool AoEnabled { get; set; } = false;
        public float AoStrength { get; set; } = 0.6f;
        public float AoRadius { get; set; } = 0.012f;
        public int AoSamplesIndex { get; set; } = 1;

        public float ClarityStrength { get; set; } = 0.0f;
        public bool DebandEnabled { get; set; } = false;
        public float DebandStrength { get; set; } = 0.5f;
        public float GiStrength { get; set; } = 0.0f;
        public float GiRadius { get; set; } = 0.5f;
        public float FogStrength { get; set; } = 0.0f;
        public float FogStart { get; set; } = 0.2f;
        public float FogBrightness { get; set; } = 0.8f;
        public float AmbientStrength { get; set; } = 0.0f;
        public bool EyeAdaptEnabled { get; set; } = false;
        public float EyeAdaptStrength { get; set; } = 0.5f;

        public bool PerfMode { get; set; } = false;
        public int DebugView { get; set; } = 0;
        public int AiQuality { get; set; } = 0;
        public int RenderScaleIndex { get; set; } = 0;

        public static readonly string[] RenderScaleNames = ["Native", "Balanced", "Performance", "Max FPS"];
        public static readonly float[] RenderScaleValues = [1.0f, 0.7f, 0.5f, 0.35f];

        public float ResolveRenderScale()
        {
            int i = Math.Clamp(RenderScaleIndex, 0, RenderScaleValues.Length - 1);
            return RenderScaleValues[i];
        }

        public int PanelDock { get; set; } = 5;
        public double PanelX { get; set; } = -1;
        public double PanelY { get; set; } = -1;
        public double PanelW { get; set; } = 420;
        public double PanelH { get; set; } = 620;

        public static readonly string[] DebugViewNames = ["Off", "Depth map", "Normals", "Polygons"];
        public static readonly string[] AiQualityNames = ["Fast", "Quality"];

        public static readonly int[] AoSampleCounts = [2, 4, 8, 16];
        public static readonly string[] TonemapNames = ["Reinhard", "ACES", "Uncharted 2", "Filmic", "AgX"];
        public static readonly string[] ColorTempNames = ["Warm", "Neutral", "Cool", "Custom"];
        public static readonly float[][] ColorTempValues =
        [
            [1.00f, 0.96f, 0.88f],
            [1.00f, 1.00f, 1.00f],
            [0.88f, 0.96f, 1.00f],
        ];

        public float[] ResolveColorTemp()
        {
            if (ColorTemp >= 0 && ColorTemp < ColorTempValues.Length)
                return ColorTempValues[ColorTemp];
            return ColorTempCustom;
        }

        public int ResolveAoSamples()
        {
            int i = Math.Clamp(AoSamplesIndex, 0, AoSampleCounts.Length - 1);
            return AoSampleCounts[i];
        }

        private static float[] FixTriple(float[]? v, float fill)
        {
            var r = new[] { fill, fill, fill };
            if (v != null)
            {
                for (int i = 0; i < v.Length && i < 3; i++)
                    r[i] = v[i];
            }
            return r;
        }

        public void Normalize()
        {
            Lift = FixTriple(Lift, 0f);
            Gain = FixTriple(Gain, 1f);
            ColorBalance = FixTriple(ColorBalance, 1f);
            ColorTempCustom = FixTriple(ColorTempCustom, 1f);
            BloomTint = FixTriple(BloomTint, 1f);
        }

        public bool NeedsDepth =>
            DofEnabled
            || SsrEnabled
            || AoEnabled
            || GiStrength > 0f
            || FogStrength > 0f
            || AmbientStrength > 0f
            || EyeAdaptEnabled
            || DebugView > 0
            || HasCustomEffects;

        public bool HasVisibleEffects =>
            GradeEnabled
            || TonemapEnabled
            || VignetteEnabled
            || SharpenEnabled
            || BloomEnabled
            || ChromaEnabled
            || GrainEnabled
            || DofEnabled
            || SsrEnabled
            || AoEnabled
            || ClarityStrength > 0f
            || DebandEnabled
            || GiStrength > 0f
            || FogStrength > 0f
            || AmbientStrength > 0f
            || EyeAdaptEnabled
            || DebugView > 0
            || HasCustomEffects;

        private static readonly object CustomEffectScanLock = new();
        private static long _lastCustomEffectScan;
        private static int _hasCustomEffects;

        private static bool HasCustomEffects
        {
            get
            {
                long now = Environment.TickCount64;
                if (now - Volatile.Read(ref _lastCustomEffectScan) < 2000)
                    return Volatile.Read(ref _hasCustomEffects) != 0;
                lock (CustomEffectScanLock)
                {
                    if (now - Volatile.Read(ref _lastCustomEffectScan) >= 2000)
                    {
                        bool found;
                        try
                        {
                            string directory = Path.Combine(Paths.RiShade, "Effects");
                            found = Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.hlsl").Any();
                        }
                        catch
                        {
                            found = false;
                        }
                        Volatile.Write(ref _hasCustomEffects, found ? 1 : 0);
                        Volatile.Write(ref _lastCustomEffectScan, now);
                    }
                }
                return Volatile.Read(ref _hasCustomEffects) != 0;
            }
        }

        private static RiShadeSettings _current = new();
        private static int _version = 1;
        private static int _savePending;
        private static readonly Lock _ioLock = new();

        public static RiShadeSettings Current => _current;
        public static int Version => Volatile.Read(ref _version);

        private static string FilePath => Path.Combine(Paths.Config, "RiShadeSettings.json");

        public static void Touch()
        {
            Interlocked.Increment(ref _version);
            if (Interlocked.CompareExchange(ref _savePending, 1, 0) == 0)
            {
                System.Threading.Tasks.Task.Run(async delegate
                {
                    await System.Threading.Tasks.Task.Delay(150).ConfigureAwait(false);
                    Interlocked.Exchange(ref _savePending, 0);
                    Save();
                });
            }
        }

        public static void ReplaceCurrent(RiShadeSettings s, string source)
        {
            s.Normalize();
            _current = s;
            App.Logger.WriteLine("RiShade", "Settings replaced from " + source);
            Touch();
        }

        public static void Load()
        {
            try
            {
                lock (_ioLock)
                {
                    if (!File.Exists(FilePath))
                        return;
                    if (Voidstrap.Utility.JsonFile.TryLoad<RiShadeSettings>(FilePath, _jsonOptions, out RiShadeSettings? loaded, out bool recovered, out Exception? failure) && loaded != null)
                    {
                        loaded.Normalize();
                        _current = loaded;
                    }
                    else if (failure != null)
                        App.Logger.WriteLine("RiShade", "Settings recovery failed: " + failure.Message);
                    if (recovered)
                        App.Logger.WriteLine("RiShade", "Recovered the last valid settings backup");
                }
                Interlocked.Increment(ref _version);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RiShade", "Settings load failed: " + ex.Message);
            }
        }

        private static long _lastSaveTicks;
        public static long LastSaveTicks => Volatile.Read(ref _lastSaveTicks);

        private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

        public static void Save()
        {
            try
            {
                lock (_ioLock)
                {
                    Volatile.Write(ref _lastSaveTicks, Environment.TickCount64);
                    Voidstrap.Utility.JsonFile.SerializeAtomic(FilePath, _current, _jsonOptions);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("RiShade", "Settings save failed: " + ex.Message);
            }
        }

        public static readonly string[] PresetNames =
        [
            "Vanilla (off)",
            "Ultra",
            "Cinematic",
            "Vibrant",
            "Dark and Moody",
            "Retro Film",
            "Dreamy Blur",
        ];

        public static void ApplyPreset(string name)
        {
            var s = new RiShadeSettings();
            switch (name)
            {
                case "Ultra":
                    s.ClarityStrength = 0.3f;
                    s.DebandEnabled = true;
                    s.DebandStrength = 0.45f;
                    s.GiStrength = 0.45f;
                    s.GiRadius = 0.5f;
                    s.AmbientStrength = 0.2f;
                    s.EyeAdaptEnabled = true;
                    s.EyeAdaptStrength = 0.4f;
                    s.AoEnabled = true;
                    s.AoStrength = 0.65f;
                    s.AoSamplesIndex = 2;
                    s.SsrEnabled = true;
                    s.SsrIntensity = 0.5f;
                    s.SsrGlossiness = 0.92f;
                    s.SsrReflectivity = 0.28f;
                    s.SsrDistance = 0.6f;
                    s.SsrSheen = 0.12f;
                    s.BloomEnabled = true;
                    s.BloomStrength = 0.18f;
                    s.BloomThreshold = 0.75f;
                    s.BloomRadius = 1.6f;
                    s.BloomPasses = 4;
                    s.SharpenEnabled = true;
                    s.SharpenStrength = 0.35f;
                    s.SharpenRadius = 1.0f;
                    s.SharpenClamp = 0.06f;
                    s.TonemapEnabled = true;
                    s.TonemapMode = 1;
                    s.TonemapExposure = 1.05f;
                    s.GradeEnabled = true;
                    s.Gain = [1.02f, 1.0f, 1.03f];
                    break;
                case "Cinematic":
                    s.GradeEnabled = true;
                    s.TonemapEnabled = true;
                    s.TonemapMode = 1;
                    s.TonemapExposure = 1.1f;
                    s.VignetteEnabled = true;
                    s.VignetteStrength = 0.4f;
                    s.BloomEnabled = true;
                    s.BloomStrength = 0.15f;
                    s.BloomThreshold = 0.75f;
                    s.GrainEnabled = true;
                    s.GrainStrength = 0.025f;
                    break;
                case "Vibrant":
                    s.GradeEnabled = true;
                    s.Brightness = 0.05f;
                    s.BloomEnabled = true;
                    s.BloomStrength = 0.13f;
                    s.BloomThreshold = 0.72f;
                    s.TonemapEnabled = true;
                    s.TonemapMode = 0;
                    s.TonemapExposure = 1.05f;
                    break;
                case "Dark and Moody":
                    s.GradeEnabled = true;
                    s.Brightness = -0.08f;
                    s.Gamma = 0.88f;
                    s.VignetteEnabled = true;
                    s.VignetteStrength = 0.7f;
                    s.TonemapEnabled = true;
                    s.TonemapMode = 3;
                    s.TonemapExposure = 0.9f;
                    break;
                case "Retro Film":
                    s.GradeEnabled = true;
                    s.GrainEnabled = true;
                    s.GrainStrength = 0.07f;
                    s.GrainColored = true;
                    s.ChromaEnabled = true;
                    s.ChromaStrength = 0.004f;
                    s.VignetteEnabled = true;
                    s.VignetteStrength = 0.55f;
                    s.TonemapEnabled = true;
                    s.TonemapMode = 3;
                    break;
                case "Dreamy Blur":
                    s.DofEnabled = true;
                    s.DofStrength = 0.7f;
                    s.DofFocusRange = 0.18f;
                    s.BloomEnabled = true;
                    s.BloomStrength = 0.25f;
                    s.BloomThreshold = 0.6f;
                    s.BloomRadius = 2.0f;
                    s.GradeEnabled = true;
                    s.Brightness = 0.04f;
                    break;
            }
            _current = s;
            App.Logger.WriteLine("RiShade", "Preset applied: " + name);
            Touch();
        }
    }
}
