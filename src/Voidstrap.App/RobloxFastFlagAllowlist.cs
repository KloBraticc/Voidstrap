using System;
using System.Collections.Generic;

namespace Voidstrap;

public static class RobloxFastFlagAllowlist
{
    public const string AnnouncementUrl = "https://devforum.roblox.com/t/allowlist-for-local-client-configuration-via-fast-flags/3966569";

    public static IReadOnlyDictionary<string, string> Flags { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DFIntCSGLevelOfDetailSwitchingDistance"] = "Geometry",
        ["DFIntCSGLevelOfDetailSwitchingDistanceL12"] = "Geometry",
        ["DFIntCSGLevelOfDetailSwitchingDistanceL23"] = "Geometry",
        ["DFIntCSGLevelOfDetailSwitchingDistanceL34"] = "Geometry",
        ["FFlagHandleAltEnterFullscreenManually"] = "Rendering",
        ["DFFlagTextureQualityOverrideEnabled"] = "Rendering",
        ["DFIntTextureQualityOverride"] = "Rendering",
        ["FIntDebugForceMSAASamples"] = "Rendering",
        ["DFFlagDisableDPIScale"] = "Rendering",
        ["FFlagDebugGraphicsPreferD3D11"] = "Rendering",
        ["FFlagDebugSkyGray"] = "Rendering",
        ["DFFlagDebugPauseVoxelizer"] = "Rendering",
        ["DFIntDebugFRMQualityLevelOverride"] = "Rendering",
        ["FIntFRMMaxGrassDistance"] = "Rendering",
        ["FIntFRMMinGrassDistance"] = "Rendering",
        ["FFlagDebugGraphicsPreferVulkan"] = "Rendering",
        ["FFlagDebugGraphicsPreferOpenGL"] = "Rendering",
        ["FIntGrassMovementReducedMotionFactor"] = "User Interface",
    };

    public static bool IsAllowed(string name)
    {
        return Flags.ContainsKey(name);
    }
}
