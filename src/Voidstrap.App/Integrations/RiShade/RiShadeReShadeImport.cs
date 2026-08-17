using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Voidstrap.Integrations.RiShade
{
    public static class RiShadeReShadeImport
    {
        public static RiShadeSettings Parse(string path, out string report)
        {
            var lines = File.ReadAllLines(path);
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            string techniques = "";
            string current = "";
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    current = line[1..^1];
                    if (!sections.ContainsKey(current))
                        sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = line[..eq].Trim();
                string val = line[(eq + 1)..].Trim();
                if (current.Length == 0)
                {
                    if (key.Equals("Techniques", StringComparison.OrdinalIgnoreCase))
                        techniques = val;
                    continue;
                }
                sections[current][key] = val;
            }

            var s = new RiShadeSettings();
            var mapped = new List<string>();
            var skipped = new List<string>();
            var tech = techniques.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var t in tech)
            {
                int at = t.IndexOf('@');
                string fx = at >= 0 ? t[(at + 1)..].ToLowerInvariant() : t.ToLowerInvariant();
                switch (fx)
                {
                    case "clarity.fx":
                        s.ClarityStrength = GetF(sections, "Clarity.fx", "ClarityStrength", 0.4f);
                        mapped.Add("Clarity");
                        break;
                    case "smaa.fx":
                    case "fxaa.fx":
                    case "nfaa.fx":
                    case "dlaa_plus.fx":
                    case "daa.fx":
                        skipped.Add(fx + " (use the Anti Aliasing overlay in Mods, Overlays)");
                        break;
                    case "magicbloom.fx":
                        s.BloomEnabled = true;
                        s.BloomStrength = Math.Clamp(GetF(sections, "MagicBloom.fx", "fBloom_Intensity", 1f) * 0.3f, 0.05f, 1.5f);
                        s.BloomThreshold = Math.Clamp(GetF(sections, "MagicBloom.fx", "fBloom_Threshold", 2f) * 0.3f, 0.2f, 0.9f);
                        mapped.Add("Bloom");
                        break;
                    case "ppfx_bloom.fx":
                        s.BloomEnabled = true;
                        s.BloomStrength = Math.Clamp(GetF(sections, "PPFX_Bloom.fx", "pBloomIntensity", 0.5f) * 0.7f, 0.05f, 1.5f);
                        s.BloomThreshold = Math.Clamp(GetF(sections, "PPFX_Bloom.fx", "pBloomThreshold", 0.6f), 0.2f, 0.9f);
                        mapped.Add("Bloom");
                        break;
                    case "qUINT_bloom.fx":
                    case "bloom.fx":
                    case "bloominghdr.fx":
                    case "pd80_02_bloom.fx":
                        s.BloomEnabled = true;
                        mapped.Add("Bloom");
                        break;
                    case "mxao.fx":
                    case "quint_mxao.fx":
                    case "gloomao.fx":
                    case "ppfx_ssdo.fx":
                    case "neossao.fx":
                    case "miao.fx":
                    case "baba_xegtao.fx":
                    case "depthdarknening.fx":
                        s.AoEnabled = true;
                        s.AoStrength = Math.Clamp(GetF(sections, "qUINT_mxao.fx", "MXAO_SSAO_AMOUNT", GetF(sections, "MXAO.fx", "fMXAOIntensity", 0.7f)), 0.2f, 2f);
                        s.AoSamplesIndex = 2;
                        mapped.Add("Ambient occlusion");
                        break;
                    case "quint_ssr.fx":
                    case "ssr.fx":
                    case "baba_ssr.fx":
                    case "baba_ssr_lite.fx":
                    case "reflectivebumpmapping.fx":
                        s.SsrEnabled = true;
                        s.SsrIntensity = 0.65f;
                        s.SsrGlossiness = 0.85f;
                        s.SsrReflectivity = 0.45f;
                        s.SsrDistance = 0.6f;
                        mapped.Add("Reflections");
                        break;
                    case "eyeadaption.fx":
                        s.EyeAdaptEnabled = true;
                        s.EyeAdaptStrength = 0.5f;
                        mapped.Add("Auto exposure");
                        break;
                    case "ambientlight.fx":
                        s.AmbientStrength = Math.Clamp(GetF(sections, "AmbientLight.fx", "alInt", 4f) * 0.06f, 0.05f, 1f);
                        mapped.Add("Ambient light");
                        break;
                    case "quint_deband.fx":
                    case "deband.fx":
                    case "baba_deband.fx":
                        s.DebandEnabled = true;
                        mapped.Add("Deband");
                        break;
                    case "radiantgi.fx":
                    case "quint_rtgi.fx":
                    case "baba_gi.fx":
                        s.GiStrength = 0.5f;
                        mapped.Add("Global illumination");
                        break;
                    case "lightdof.fx":
                    case "quint_dof.fx":
                    case "cinematicdof.fx":
                    case "dof.fx":
                        s.DofEnabled = true;
                        s.DofStrength = 0.5f;
                        mapped.Add("Depth of field");
                        break;
                    case "depthhaze.fx":
                    case "adaptivefog.fx":
                        s.FogStrength = Math.Clamp(GetF(sections, "DepthHaze.fx", "EffectStrength", 0.6f), 0.1f, 1f);
                        s.FogStart = Math.Clamp(GetF(sections, "DepthHaze.fx", "FogStart", 0.2f), 0f, 0.9f);
                        mapped.Add("Depth fog");
                        break;
                    case "vignette.fx":
                        s.VignetteEnabled = true;
                        s.VignetteStrength = 0.45f;
                        mapped.Add("Vignette");
                        break;
                    case "filmgrain.fx":
                    case "filmgrain2.fx":
                    case "pd80_06_film_grain.fx":
                        s.GrainEnabled = true;
                        s.GrainStrength = Math.Clamp(GetF(sections, "FilmGrain.fx", "Intensity", 0.5f) * 0.08f, 0.01f, 0.2f);
                        mapped.Add("Film grain");
                        break;
                    case "chromaticaberration.fx":
                    case "prism.fx":
                    case "pd80_06_chromatic_aberration.fx":
                        s.ChromaEnabled = true;
                        mapped.Add("Chromatic aberration");
                        break;
                    case "tonemap.fx":
                    case "pd80_03_filmic_adaptation.fx":
                    case "vividtone.fx":
                    case "phdr.fx":
                    case "ufakehdr.fx":
                        s.TonemapEnabled = true;
                        s.TonemapMode = 1;
                        mapped.Add("Tonemap");
                        break;
                    case "liftgammagain.fx":
                    case "levels.fx":
                    case "levelsplus.fx":
                    case "pd80_04_color_balance.fx":
                    case "pd80_04_color_temperature.fx":
                        s.GradeEnabled = true;
                        mapped.Add("Colour grading");
                        break;
                    case "lumasharpen.fx":
                    case "adaptivesharpen.fx":
                    case "quint_sharp.fx":
                    case "pd80_05_sharpening.fx":
                    case "finesharp.fx":
                    case "highpasssharpen.fx":
                    case "jasharpen.fx":
                    case "baba_nvsharpen.fx":
                    case "baba_neuralsharpen.fx":
                        s.SharpenEnabled = true;
                        s.SharpenStrength = 0.8f;
                        mapped.Add("Sharpen");
                        break;
                    case "curves.fx":
                    case "colourfulness.fx":
                    case "vibrance.fx":
                    case "technicolor.fx":
                    case "technicolor2.fx":
                    case "dpx.fx":
                    case "pd80_04_contrast_brightness_saturation.fx":
                        skipped.Add(fx + " (use the app Overlays for saturation and contrast)");
                        break;
                    default:
                        skipped.Add(fx);
                        break;
                }
            }
            report = $"Mapped: {string.Join(", ", mapped.Count > 0 ? mapped : ["nothing"])}";
            if (skipped.Count > 0)
                report += $"\nNot supported: {string.Join(", ", skipped)}";
            return s;
        }

        private static float GetF(Dictionary<string, Dictionary<string, string>> sections, string section, string key, float fallback)
        {
            if (sections.TryGetValue(section, out var kv) && kv.TryGetValue(key, out var raw))
            {
                var first = raw.Split(',')[0].Trim();
                if (float.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    return v;
            }
            return fallback;
        }
    }
}
