import Foundation

public enum FlagKeys {
    public static let grassMotion = "FIntGrassMovementReducedMotionFactor"
    public static let lod = ["DFIntCSGLevelOfDetailSwitchingDistance", "DFIntCSGLevelOfDetailSwitchingDistanceL12",
                             "DFIntCSGLevelOfDetailSwitchingDistanceL23", "DFIntCSGLevelOfDetailSwitchingDistanceL34"]
    public static let frm = "DFIntDebugFRMQualityLevelOverride"
    public static let sky = "FFlagDebugSkyGray"
    public static let msaa = "FIntDebugForceMSAASamples"
    public static let msaaOptional = "FIntDebugFRMOptionalMSAALevelOverride"
    public static let texture = "DFIntTextureQualityOverride"
    public static let textureOn = "DFFlagTextureQualityOverrideEnabled"
    public static let dpi = "DFFlagDisableDPIScale"
    public static let grassMin = "FIntFRMMinGrassDistance"
    public static let grassMax = "FIntFRMMaxGrassDistance"
    public static let targetFps = "DFIntTaskSchedulerTargetFps"
    public static let limit240 = "FFlagTaskSchedulerLimitTargetFpsTo2402"
    public static let showFps = "FFlagDebugDisplayFPS"
    public static let shadows = ["DFFlagDebugPauseVoxelizer", "FIntRenderShadowmapBias"]
    public static let resolution = "DFIntDebugDynamicRenderKiloPixels"
    public static let blur = "FIntRobloxGuiBlurIntensity"
    public static let hideGuiGroup = "DFIntCanHideGuiGroupId"
    public static let hideGuiToggles = "FFlagUserShowGuiHideToggles"
    public static let fontPadding = "FIntFontSizePadding"
    public static let lagSpikes = ["DFIntBandwidthManagerApplicationDefaultBps", "DFIntBandwidthManagerDataSenderMaxWorkCatchupMs"]
    public static let fastLoad = ["DFIntNumAssetsMaxToPreload", "FStringGetPlayerImageDefaultTimeout"]
    public static let mtu = "DFIntConnectionMTUSize"
    public static let flagState = "FStringDebugShowFlagState"
    public static let ping = "DFFlagDebugPrintDataPingBreakDown"

    public static let fpsValues = [0, 30, 60, 90, 120, 144, 165, 240, 360, 480, 9999]
    public static let msaaValues = [0, 1, 2, 4, 8]
    public static let resolutions = [0, 144, 240, 360, 480, 720, 1080, 1440, 2160, 4320]
    public static let profiles = [(2, 2), (0, 1), (3, 4)]
    public static let meshRange = 1...3
    public static let frmRange = 1...21

    public static func kiloPixels(_ height: Int) -> Int {
        let width = Int((Double(height) * 16 / 9).rounded())
        return width * height / 1000
    }

    public static func resolutionIndex(_ kp: Int?) -> Int {
        guard let kp, kp > 0 else { return 0 }
        var best = 1
        for i in 1..<resolutions.count where abs(kiloPixels(resolutions[i]) - kp) < abs(kiloPixels(resolutions[best]) - kp) { best = i }
        return best
    }
}
