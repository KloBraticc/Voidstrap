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
	public static IReadOnlyDictionary<string, string> PresetFlags = new Dictionary<string, string>
	{
		{ "Players.LogLevel", "FStringDebugLuaLogLevel" },
		{ "Players.LogPattern", "FStringDebugLuaLogPattern" },
		{ "Players.EventLog", "DFLogSocialCounterpartyManager" },
		{ "Instances.WndCheck", "FLogWndProcessCheck" },
		{ "Rendering.FRMQualityOverride", "DFIntDebugFRMQualityLevelOverride" },
		{ "Geometry.MeshLOD.Static", "DFIntCSGLevelOfDetailSwitchingDistanceStatic" },
		{ "Geometry.MeshLOD.L0", "DFIntCSGLevelOfDetailSwitchingDistance" },
		{ "Geometry.MeshLOD.L12", "DFIntCSGLevelOfDetailSwitchingDistanceL12" },
		{ "Geometry.MeshLOD.L23", "DFIntCSGLevelOfDetailSwitchingDistanceL23" },
		{ "Geometry.MeshLOD.L34", "DFIntCSGLevelOfDetailSwitchingDistanceL34" },
		{ "Geometry.MeshDistance.L0", "DFIntCSGLevelOfDetailSwitchingDistance" },
		{ "Geometry.MeshDistance.L12", "DFIntCSGLevelOfDetailSwitchingDistanceL12" },
		{ "Geometry.MeshDistance.L23", "DFIntCSGLevelOfDetailSwitchingDistanceL23" },
		{ "Geometry.MeshDistance.L34", "DFIntCSGLevelOfDetailSwitchingDistanceL34" },
		{ "Hyper.Threading1", "FFlagDebugCheckRenderThreading" },
		{ "Hyper.Threading2", "FFlagRenderDebugCheckThreading2" },
		{ "Memory.Probe", "DFFlagPerformanceControlEnableMemoryProbing3" },
		{ "OptimizeCFrameUpdates", "FFlagOptimizeCFrameUpdates4" },
		{ "OptimizeCFrameUpdatesIC", "FFlagOptimizeCFrameUpdatesIC4" },
		{ "Rendering.FrmQuality", "DFIntDebugFRMQualityLevelOverride" },
		{ "Rendering.RemoveTexture1", "FFlagTextureUseACR3" },
		{ "Rendering.RemoveTexture2", "FIntTextureUseACRHundredthPercent" },
		{ "UI.SensetivityNumbers", "FFlagFixSensitivityTextPrecision" },
		{ "UI.NoGuiBlur", "FIntRobloxGuiBlurIntensity" },
		{ "UI.CustomDisconnectError1", "FFlagReconnectDisabled" },
		{ "UI.CustomDisconnectError2", "FStringReconnectDisabledReason" },
		{ "Network.DefaultBps", "DFIntBandwidthManagerApplicationDefaultBps" },
		{ "Network.MaxWorkCatchupMs", "DFIntBandwidthManagerDataSenderMaxWorkCatchupMs" },
		{ "Network.MeshPreloadding", "DFFlagEnableMeshPreloading2" },
		{ "Network.MaxAssetPreload", "DFIntNumAssetsMaxToPreload" },
		{ "Network.PlayerImageDefault", "FStringGetPlayerImageDefaultTimeout" },
		{ "Network.Payload1", "DFIntRccMaxPayloadSnd" },
		{ "Network.Payload2", "DFIntCliMaxPayloadRcv" },
		{ "Network.Payload3", "DFIntCliMaxPayloadSnd" },
		{ "Network.Payload4", "DFIntRccMaxPayloadRcv" },
		{ "Network.Payload5", "DFIntCliTcMaxPayloadRcv" },
		{ "Network.Payload6", "DFIntRccTcMaxPayloadRcv" },
		{ "Network.Payload7", "DFIntCliTcMaxPayloadSnd" },
		{ "Network.Payload8", "DFIntRccTcMaxPayloadSnd" },
		{ "Network.Payload9", "DFIntMaxDataPayloadSize" },
		{ "Network.Payload10", "DFIntMaxUREPayloadSingleLimit" },
		{ "Network.Payload11", "DFIntTotalRepPayloadLimit" },
		{ "Rendering.ForceVulkan", "FStringBuggyRenderpassList2" },
		{ "Rendering.BrighterVisual", "FFlagRenderFixFog" },
		{ "Rendering.RemoveGrass1", "FIntFRMMinGrassDistance" },
		{ "Rendering.RemoveGrass2", "FIntFRMMaxGrassDistance" },
		{ "Rendering.RemoveGrass3", "FIntRenderGrassDetailStrands" },
		{ "UI.TextSize1", "FFlagEnablePreferredTextSizeScale" },
		{ "UI.TextSize2", "FFlagEnablePreferredTextSizeSettingInMenus2" },
		{ "Debug.FlagState", "FStringDebugShowFlagState" },
		{ "Debug.PingBreakdown", "DFFlagDebugPrintDataPingBreakDown" },
		{ "Debug.Chunks", "FFlagDebugLightGridShowChunks" },
		{ "UI.RainbowText", "FFlagDebugDisplayUnthemedInstances" },
		{ "Rendering.CpuThreads", "DFIntRuntimeConcurrency" },
		{ "Graphic.GraySky", "FFlagDebugSkyGray" },
		{ "Graphic.WhiteSky", "FFlagSkyUseRGBEEncoding" },
		{ "Fake.Verify", "FStringWhitelistVerifiedUserId" },
		{ "Camera.Controls", "FFlagNewCameraControls" },
		{ "Camera.Chat", "FFlagDebugForceChatDisabled" },
		{ "UI.Pseudolocalization", "FFlagDebugEnablePseudolocalization" },
		{ "Rendering.Shaders", "DFIntRenderClampRoughnessMax" },
		{ "Rendering.Shaders2", "DFIntDebugFRMQualityLevelOverride" },
		{ "Telemetry.Webview1", "DFStringWebviewUrlAllowlist" },
		{ "Telemetry.Webview2", "DFFlagWindowsWebViewTelemetryEnabled" },
		{ "Telemetry.Webview3", "DFIntMacWebViewTelemetryThrottleHundredthsPercent" },
		{ "Telemetry.Webview4", "DFIntWindowsWebViewTelemetryThrottleHundredthsPercent" },
		{ "Telemetry.Webview5", "FIntStudioWebView2TelemetryHundredthsPercent" },
		{ "Telemetry.Webview6", "FFlagSyncWebViewCookieToEngine2" },
		{ "Telemetry.Webview7", "FFlagUpdateHTTPCookieStorageFromWKWebView" },
		{ "System.TargetRefreshRate1", "DFIntGraphicsOptimizationModeFRMFrameRateTarget" },
		{ "System.TargetRefreshRate2", "DFIntGraphicsOptimizationModeMaxFrameTimeTargetMs" },
		{ "System.TargetRefreshRate3", "DFIntGraphicsOptimizationModeMinFrameTimeTargetMs" },
		{ "Rendering.LimitFramerate", "FFlagTaskSchedulerLimitTargetFpsTo2402" },
		{ "Rendering.Framerate", "DFIntTaskSchedulerTargetFps" },
		{ "Rendering.DisableScaling", "DFFlagDisableDPIScale" },
		{ "Rendering.MSAA1", "FIntDebugForceMSAASamples" },
		{ "Rendering.MSAA2", "FIntDebugFRMOptionalMSAALevelOverride" },
		{ "Rendering.DisablePostFX", "FFlagDisablePostFx" },
		{ "System.CpuCore1", "DFIntInterpolationNumParallelTasks" },
		{ "System.CpuCore2", "DFIntMegaReplicatorNumParallelTasks" },
		{ "System.CpuCore3", "DFIntNetworkClusterPacketCacheNumParallelTasks" },
		{ "System.CpuCore4", "DFIntReplicationDataCacheNumParallelTasks" },
		{ "System.CpuCore5", "FIntLuaGcParallelMinMultiTasks" },
		{ "System.CpuCore6", "FIntSmoothClusterTaskQueueMaxParallelTasks" },
		{ "System.CpuCore7", "DFIntPhysicsReceiveNumParallelTasks" },
		{ "System.CpuCore8", "FIntTaskSchedulerAutoThreadLimit" },
		{ "System.CpuCore9", "FIntSimWorldTaskQueueParallelTasks" },
		{ "System.CpuThreads", "DFIntRuntimeConcurrency" },
		{ "System.CpuCoreMinThreadCount", "FIntTaskSchedulerAsyncTasksMinimumThreadCount" },
		{ "UI.Chatbubble", "FFlagEnableBubbleChatFromChatService" },
		{ "System.GpuCulling", "FFlagFastGPULightCulling3" },
		{ "System.CpuCulling", "FFlagDebugForceFSMCPULightCulling" },
		{ "Rendering.Camerazoom", "FIntCameraMaxZoomDistance" },
		{ "Rendering.NoFrmBloom", "FFlagRenderNoLowFrmBloom" },
		{ "Rendering.FRMRefactor", "FFlagFRMRefactor" },
		{ "Rendering.MinimalRendering", "FFlagDebugRenderingSetDeterministic" },
		{ "Network.Mtusize", "DFIntConnectionMTUSize" },
		{ "Grass.Movement", "FIntGrassMovementReducedMotionFactor" },
		{ "Rendering.Dynamic.Resolution", "DFIntDebugDynamicRenderKiloPixels" },
		{ "Rendering.Mode.DisableD3D11", "FFlagDebugGraphicsDisableDirect3D11" },
		{ "Rendering.Mode.D3D11", "FFlagDebugGraphicsPreferD3D11" },
		{ "Rendering.Mode.Vulkan", "FFlagDebugGraphicsPreferVulkan" },
		{ "Rendering.Mode.OpenGL", "FFlagDebugGraphicsPreferOpenGL" },
		{ "Rendering.AvoidSleep", "DFFlagTaskSchedulerAvoidSleep" },
		{ "Rendering.GrayAvatar", "DFIntTextureCompositorActiveJobs" },
		{ "Rendering.Lighting.Voxel", "DFFlagDebugRenderForceTechnologyVoxel" },
		{ "Rendering.Lighting.ShadowMap", "FFlagDebugForceFutureIsBrightPhase2" },
		{ "Rendering.Lighting.Future", "FFlagDebugForceFutureIsBrightPhase3" },
		{ "Rendering.Lighting.Unified", "FFlagRenderUnifiedLighting14" },
		{ "Rendering.WorserParticles1", "FFlagFixOutdatedParticles2" },
		{ "Rendering.WorserParticles2", "FFlagFixOutdatedTimeScaleParticles" },
		{ "Rendering.WorserParticles3", "FFlagFixParticleAttachmentCulling" },
		{ "Rendering.WorserParticles4", "FFlagFixParticleEmissionBias2" },
		{ "Rendering.LowPolyMeshes1", "DFIntCSGLevelOfDetailSwitchingDistance" },
		{ "Rendering.LowPolyMeshes2", "DFIntCSGLevelOfDetailSwitchingDistanceL12" },
		{ "Rendering.LowPolyMeshes3", "DFIntCSGLevelOfDetailSwitchingDistanceL23" },
		{ "Rendering.LowPolyMeshes4", "DFIntCSGLevelOfDetailSwitchingDistanceL34" },
		{ "Rendering.AndroidVfs", "FStringAndroidVfsLowspecHwCondition" },
		{ "Rendering.BGRA", "FFlagD3D11SupportBGRA" },
		{ "Rendering.TerrainTextureQuality", "FIntTerrainArraySliceSize" },
		{ "Rendering.TextureSkipping.Skips", "FIntDebugTextureManagerSkipMips" },
		{ "Rendering.TextureQuality.Level", "DFIntTextureQualityOverride" },
		{ "Rendering.TextureQuality.OverrideEnabled", "DFFlagTextureQualityOverrideEnabled" },
		{ "UI.Hide", "DFIntCanHideGuiGroupId" },
		{ "UI.Hide.Toggles", "FFlagUserShowGuiHideToggles" },
		{ "UI.FontSize", "FIntFontSizePadding" },
		{ "UI.RedFont", "FStringDebugHighlightSpecificFont" },
		{ "Rendering.NewFpsSystem", "FFlagEnableFPSAndFrameTime" },
		{ "Rendering.FrameRateBufferPercentage", "FIntMaquettesFrameRateBufferPercentage" },
		{ "Network.BetterPacketSending1", "DFIntNetworkStopProducingPacketsToProcessThresholdMs" },
		{ "Network.BetterPacketSending2", "DFIntMaxWaitTimeBeforeForcePacketProcessMS" },
		{ "Network.BetterPacketSending3", "DFIntClientPacketMaxDelayMs" },
		{ "Network.BetterPacketSending4", "DFIntClientPacketMinMicroseconds" },
		{ "Network.BetterPacketSending5", "DFIntClientPacketExcessMicroseconds" },
		{ "Network.BetterPacketSending6", "DFIntClientPacketMaxFrameMicroseconds" },
		{ "Network.BetterPacketSending7", "DFIntMaxProcessPacketsJobScaling" },
		{ "Network.BetterPacketSending8", "DFIntMaxProcessPacketsStepsAccumulated" },
		{ "Network.BetterPacketSending9", "DFIntMaxProcessPacketsStepsPerCyclic" },
		{ "Recommended.Buffer", "FIntRakNetResendBufferArrayLength" },
		{ "Telemetry.Voicechat1", "DFFlagVoiceChatCullingRecordEventIngestTelemetry" },
		{ "Telemetry.Voicechat2", "DFFlagVoiceChatJoinProfilingUsingTelemetryStat_RCC" },
		{ "Telemetry.Voicechat3", "DFFlagVoiceChatPossibleDuplicateSubscriptionsTelemetry" },
		{ "Telemetry.Voicechat4", "DFIntVoiceChatTaskStatsTelemetryThrottleHundrethsPercent" },
		{ "Telemetry.Voicechat5", "FFlagEnableLuaVoiceChatAnalyticsV2" },
		{ "Telemetry.Voicechat6", "FFlagLuaVoiceChatAnalyticsBanMessage" },
		{ "Telemetry.Voicechat7", "FFlagLuaVoiceChatAnalyticsUseCounterV2" },
		{ "Telemetry.Voicechat8", "FFlagLuaVoiceChatAnalyticsUseEventsV2" },
		{ "Telemetry.Voicechat9", "FFlagLuaVoiceChatAnalyticsUsePointsV2" },
		{ "Telemetry.Voicechat10", "FFlagVoiceChatCullingEnableMutedSubsTelemetry" },
		{ "Telemetry.Voicechat11", "FFlagVoiceChatCullingEnableStaleSubsTelemetry" },
		{ "Telemetry.Voicechat12", "FFlagVoiceChatCustomAudioDeviceEnableNeedMorePlayoutTelemetry" },
		{ "Telemetry.Voicechat13", "FFlagVoiceChatCustomAudioDeviceEnableNeedMorePlayoutTelemetry3" },
		{ "Telemetry.Voicechat14", "FFlagVoiceChatCustomAudioMixerEnableUpdateSourcesTelemetry2" },
		{ "Telemetry.Voicechat15", "FFlagVoiceChatDontSendTelemetryForPubIceTrickle" },
		{ "Telemetry.Voicechat16", "FFlagVoiceChatPeerConnectionTelemetryDetails" },
		{ "Telemetry.Voicechat17", "FFlagVoiceChatRobloxAudioDeviceUpdateRecordedBufferTelemetryEnabled" },
		{ "Telemetry.Voicechat18", "FFlagVoiceChatSubscriptionsDroppedTelemetry" },
		{ "Telemetry.Voicechat19", "FIntLuaVoiceChatAnalyticsPointsThrottle" },
		{ "Telemetry.Voicechat20", "FIntVoiceChatPerfSensitiveTelemetryIntervalSeconds" },
		{ "Telemetry.GraphicsQualityUsage", "DFFlagGraphicsQualityUsageTelemetry" },
		{ "Telemetry.GpuVsCpuBound", "DFFlagGpuVsCpuBoundTelemetry" },
		{ "Telemetry.RenderFidelity", "DFFlagSendRenderFidelityTelemetry" },
		{ "Telemetry.RenderDistance", "DFFlagReportRenderDistanceTelemetry" },
		{ "Telemetry.AudioPlugin", "DFFlagCollectAudioPluginTelemetry" },
		{ "Telemetry.FmodErrors", "DFFlagEnableFmodErrorsTelemetry" },
		{ "Telemetry.SoundLength", "DFFlagRccLoadSoundLengthTelemetryEnabled" },
		{ "Telemetry.AssetRequestV1", "DFFlagReportAssetRequestV1Telemetry" },
		{ "Telemetry.DeviceRAM", "DFFlagRobloxTelemetryAddDeviceRAMPointsV2" },
		{ "Telemetry.V2FrameRateMetrics", "DFFlagEnableTelemetryV2FRMStats" },
		{ "Telemetry.GlobalSkipUpdating", "DFFlagEnableSkipUpdatingGlobalTelemetryInfo2" },
		{ "Telemetry.CallbackSafety", "DFFlagEmitSafetyTelemetryInCallbackEnable" },
		{ "Telemetry.V2PointEncoding", "DFFlagRobloxTelemetryV2PointEncoding" },
		{ "Telemetry.ReplaceSeparator", "DFFlagDSTelemetryV2ReplaceSeparator" },
		{ "Telemetry.TelemetryV2Url", "DFStringTelemetryV2Url" },
		{ "Telemetry.Protocol", "FFlagEnableTelemetryProtocol" },
		{ "Telemetry.TelemetryService", "FFlagEnableTelemetryService1" },
		{ "Telemetry.PropertiesTelemetry", "FFlagPropertiesEnableTelemetry" },
		{ "Telemetry.OpenTelemetry", "FFlagOpenTelemetryEnabled" },
		{ "Telemetry.FLogTelemetry", "FLogRobloxTelemetry" },
		{ "DarkMode.BlueMode", "FFlagLuaAppEnableFoundationColors7" },
		{ "Layered.Clothing", "DFIntLCCageDeformLimit" },
		{ "Preload.Preload2", "DFFlagEnableMeshPreloading2" },
		{ "Preload.SoundPreload", "DFFlagEnableSoundPreloading" },
		{ "Preload.Texture", "DFFlagEnableTexturePreloading" },
		{ "Preload.TeleportPreload", "DFFlagTeleportClientAssetPreloadingEnabled9" },
		{ "Preload.FontsPreload", "FFlagPreloadAllFonts" },
		{ "Preload.ItemPreload", "FFlagPreloadTextureItemsOption4" },
		{ "Preload.Teleport2", "DFFlagTeleportPreloadingMetrics5" },
		{ "Network.RCore1", "DFIntSignalRCoreServerTimeoutMs" },
		{ "Network.RCore2", "DFIntSignalRCoreRpcQueueSize" },
		{ "Network.RCore3", "DFIntSignalRCoreHubBaseRetryMs" },
		{ "Network.RCore4", "DFIntSignalRCoreHandshakeTimeoutMs" },
		{ "Network.RCore5", "DFIntSignalRCoreKeepAlivePingPeriodMs" },
		{ "Network.RCore6", "DFIntSignalRCoreHubMaxBackoffMs" },
		{ "Network.EnableLargeReplicator", "FFlagLargeReplicatorEnabled7" },
		{ "Network.LargeReplicatorWrite", "FFlagLargeReplicatorWrite5" },
		{ "Network.LargeReplicatorRead", "FFlagLargeReplicatorRead5" },
		{ "Network.SerializeRead", "FFlagLargeReplicatorSerializeRead3" },
		{ "Network.SerializeWrite", "FFlagLargeReplicatorSerializeWrite3" },
		{ "UI.DisableAds1", "FFlagAdServiceEnabled" },
		{ "UI.DisableAds2", "FFlagEnableSponsoredAdsGameCarouselTooltip3" },
		{ "UI.DisableAds3", "FFlagEnableSponsoredAdsPerTileTooltipExperienceFooter" },
		{ "UI.DisableAds4", "FFlagEnableSponsoredAdsSeeAllGamesListTooltip" },
		{ "UI.DisableAds5", "FFlagEnableSponsoredTooltipForAvatarCatalog2" },
		{ "UI.DisableAds6", "FFlagLuaAppSponsoredGridTiles" },
		{ "UI.FullscreenTitlebarDelay", "FIntFullscreenTitleBarTriggerDelayMillis" },
		{ "UI.Menu.Style.V2Rollout", "FIntNewInGameMenuPercentRollout3" },
		{ "UI.Menu.Style.EnableV4.1", "FFlagEnableInGameMenuControls" },
		{ "UI.Menu.Style.EnableV4.2", "FFlagEnableInGameMenuModernization" },
		{ "UI.Menu.Style.EnableV4Chrome", "FFlagEnableInGameMenuChrome" },
		{ "UI.Menu.Style.ReportButtonCutOff", "FFlagFixReportButtonCutOff" },
		{ "Rendering.DisplayFps", "FFlagDebugDisplayFPS" },
		{ "Rendering.Pause.Voxelizer", "DFFlagDebugPauseVoxelizer" },
		{ "Rendering.ShadowIntensity", "FIntRenderShadowIntensity" },
		{ "Rendering.ShadowMapBias", "FIntRenderShadowmapBias" },
		{ "Rendering.Occlusion1", "DFFlagUseVisBugChecks" },
		{ "Rendering.Occlusion2", "FFlagEnableVisBugChecks27" },
		{ "Rendering.Occlusion3", "FFlagVisBugChecksThreadYield" },
		{ "UI.RemoveMiddle", "FFlagUIBloxMoveDetailsPageToLuaApps" },
		{ "UI.OLDUIRobloxStudio", "FFlagEnableRibbonPlugin3" },
		{ "Rendering.Distance.Chunks", "DFIntDebugRestrictGCDistance" },
		{ "Rendering.Start.Graphic", "FIntRomarkStartWithGraphicQualityLevel" },
		{ "UI.Menu.ChromeUI", "FFlagEnableInGameMenuChromeABTest4" },
		{ "UI.Menu.ChromeUI2", "FFlagEnableInGameMenuChrome" },
		{ "System.PreferredGPU", "FStringDebugGraphicsPreferredGPUName" },
		{ "System.DXT", "FStringGraphicsDisableUnalignedDxtGPUNameBlacklist" },
		{ "System.BypassVulkan", "FStringVulkanBuggyRenderpassList2" },
		{ "Rendering.Prerender", "FFlagMovePrerender" },
		{ "Rendering.PrerenderV2", "FFlagMovePrerenderV2" },
		{ "Menu.VRToggles", "FFlagAlwaysShowVRToggleV3" },
		{ "Menu.Feedback", "FFlagDisableFeedbackSoothsayerCheck" },
		{ "Menu.LanguageSelector", "FIntV1MenuLanguageSelectionFeaturePerMillageRollout" },
		{ "Menu.Haptics", "FFlagAddHapticsToggle" },
		{ "Menu.Framerate", "FFlagGameBasicSettingsFramerateCap5" },
		{ "Menu.ChatTranslation", "FFlagChatTranslationSettingEnabled3" },
		{ "UI.Menu.Style.ABTest.1", "FFlagEnableMenuControlsABTest" },
		{ "UI.Menu.Style.ABTest.2", "FFlagEnableV3MenuABTest3" },
		{ "UI.Menu.Style.ABTest.3", "FFlagEnableInGameMenuChromeABTest3" },
		{ "UI.Menu.Style.ABTest.4", "FFlagEnableInGameMenuChromeABTest4" },
		{ "UI.OldChromeUI1", "FFlagEnableHamburgerIcon" },
		{ "UI.OldChromeUI2", "FFlagEnableUnibarV4IA" },
		{ "UI.OldChromeUI3", "FFlagEnableAlwaysOpenUnibar2" },
		{ "UI.OldChromeUI4", "FFlagUseNewUnibarIcon" },
		{ "UI.OldChromeUI5", "FFlagUseSelfieViewFlatIcon" },
		{ "UI.OldChromeUI6", "FFlagUnibarRespawn" },
		{ "UI.OldChromeUI7", "FFlagEnableChromePinIntegrations2" },
		{ "UI.OldChromeUI8", "FFlagEnableUnibarMaxDefaultOpen" },
		{ "UI.OldChromeUI9", "FFlagUpdateHealthBar" },
		{ "UI.OldChromeUI10", "FFlagUseNewPinIcon" },
		{ "Cache.Increase1", "FFlagClearCacheableContentProviderOnGameLaunch" },
		{ "Cache.Increase2", "DFFlagAlwaysSkipDiskCache" },
		{ "Cache.Increase3", "FFlagUseCachedAudibilityMeasurements" },
		{ "Cache.Increase4", "DFIntCachedPatchLoadDelayMilliseconds" },
		{ "Cache.Increase5", "DFIntHttpCacheCleanScheduleAfterMs" },
		{ "Cache.Increase6", "DFIntHttpCacheCleanUpToAvailableSpaceMiB" },
		{ "Cache.Increase7", "DFIntHttpCacheAsyncWriterMaxPendingSize" },
		{ "Cache.Increase8", "DFIntHttpCacheEvictionExemptionMapMaxSize" },
		{ "Cache.Increase9", "DFIntHttpCacheReportSlowWritesMinDuration" },
		{ "Cache.Increase10", "DFIntMemCacheMaxCapacityMB" },
		{ "Cache.Increase11", "DFIntFileCacheReserveSize" },
		{ "Cache.Increase12", "DFIntThirdPartyInMemoryCacheCapacity" },
		{ "Cache.Increase13", "DFIntSoundServiceCacheCleanupMaxAgeDays" },
		{ "Cache.Increase14", "DFIntUserIdPlayerNameCacheLifetimeSeconds" },
		{ "Cache.Increase15", "DFIntAssetCacheErrorLogHundredthsPercent" },
		{ "Cache.Increase16", "DFFlagHttpTrackSyncWriteCachePhase" },
		{ "Cache.Increase17", "DFIntHttpCachePerfSamplingRate" },
		{ "Cache.Increase18", "DFIntHttpCachePerfHundredthsPercent" },
		{ "Cache.Increase19", "DFIntReportCacheDirSizesHundredthsPercent" },
		{ "Telemetry.Tencent1", "FStringTencentAuthPath" },
		{ "Telemetry.Tencent2", "FLogTencentAuthPath" },
		{ "Telemetry.Tencent3", "FStringXboxExperienceGuidelinesUrl" },
		{ "Telemetry.Tencent4", "FStringExperienceGuidelinesExplainedPageUrl" },
		{ "Telemetry.Tencent5", "DFFlagPolicyServiceReportIsNotSubjectToChinaPolicies" },
		{ "Telemetry.Tencent6", "DFFlagPolicyServiceReportDetailIsNotSubjectToChinaPolicies" },
		{ "Telemetry.Tencent7", "DFIntPolicyServiceReportDetailIsNotSubjectToChinaPoliciesHundredthsPercentage" },
		{ "Rendering.Nograss1", "FIntFRMMinGrassDistance" },
		{ "Rendering.Nograss2", "FIntFRMMaxGrassDistance" }
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
				if (!base.OriginalProp.TryGetValue(pair.Key, out object? original) || (original?.ToString() ?? string.Empty) != (pair.Value?.ToString() ?? string.Empty))
				{
					return true;
				}
			}
			return false;
		}
	}

	public static IReadOnlyDictionary<RenderingMode, string> RenderingModes { get; } = new Dictionary<RenderingMode, string>
	{
		{
			RenderingMode.Default,
			"None"
		},
		{
			RenderingMode.D3D11,
			"D3D11"
		},
		{
			RenderingMode.Vulkan,
			"Vulkan"
		},
		{
			RenderingMode.OpenGL,
			"OpenGL"
		}
	};

	public static IReadOnlyDictionary<LightingMode, string> LightingModes { get; } = new Dictionary<LightingMode, string>
	{
		{
			LightingMode.Default,
			"None"
		},
		{
			LightingMode.Voxel,
			"Voxel"
		},
		{
			LightingMode.ShadowMap,
			"ShadowMap"
		},
		{
			LightingMode.Future,
			"Future"
		},
		{
			LightingMode.Unified,
			"Unified"
		}
	};

	public static IReadOnlyDictionary<ProfileMode, string> ProfileModes { get; } = new Dictionary<ProfileMode, string>
	{
		{
			ProfileMode.Default,
			"None"
		},
		{
			ProfileMode.Voidstrap,
			"Voidstraps Official"
		},
		{
			ProfileMode.Stoof,
			"Stoofs"
		}
	};

	public static IReadOnlyDictionary<MSAAMode, string?> MSAAModes { get; } = new Dictionary<MSAAMode, string>
	{
		{
			MSAAMode.Default,
			null
		},
		{
			MSAAMode.x1,
			"1"
		},
		{
			MSAAMode.x2,
			"2"
		},
		{
			MSAAMode.x4,
			"4"
		},
		{
			MSAAMode.x8,
			"8"
		}
	};

	public static IReadOnlyDictionary<TextureSkipping, string?> TextureSkippingSkips { get; } = new Dictionary<TextureSkipping, string>
	{
		{
			TextureSkipping.Noskip,
			null
		},
		{
			TextureSkipping.Skip1x,
			"1"
		},
		{
			TextureSkipping.Skip2x,
			"2"
		},
		{
			TextureSkipping.Skip3x,
			"3"
		},
		{
			TextureSkipping.Skip4x,
			"4"
		},
		{
			TextureSkipping.Skip5x,
			"5"
		},
		{
			TextureSkipping.Skip6x,
			"6"
		},
		{
			TextureSkipping.Skip7x,
			"7"
		},
		{
			TextureSkipping.Skip8x,
			"8"
		}
	};

	public static IReadOnlyDictionary<DistanceRendering, string?> DistanceRenderings { get; } = new Dictionary<DistanceRendering, string>
	{
		{
			DistanceRendering.Default,
			null
		},
		{
			DistanceRendering.Chunks1x,
			"1"
		},
		{
			DistanceRendering.Chunks2x,
			"2"
		},
		{
			DistanceRendering.Chunks3x,
			"3"
		},
		{
			DistanceRendering.Chunks4x,
			"4"
		},
		{
			DistanceRendering.Chunks5x,
			"5"
		},
		{
			DistanceRendering.Chunks6x,
			"6"
		},
		{
			DistanceRendering.Chunks7x,
			"7"
		},
		{
			DistanceRendering.Chunks8x,
			"8"
		},
		{
			DistanceRendering.Chunks9x,
			"9"
		},
		{
			DistanceRendering.Chunks10x,
			"10"
		},
		{
			DistanceRendering.Chunks11x,
			"11"
		},
		{
			DistanceRendering.Chunks12x,
			"12"
		},
		{
			DistanceRendering.Chunks13x,
			"13"
		},
		{
			DistanceRendering.Chunks14x,
			"14"
		},
		{
			DistanceRendering.Chunks15x,
			"15"
		},
		{
			DistanceRendering.Chunks16x,
			"16"
		}
	};

	public static IReadOnlyDictionary<DynamicResolution, string?> DynamicResolutions { get; } = new Dictionary<DynamicResolution, string>
	{
		{
			DynamicResolution.Default,
			null
		},
		{
			DynamicResolution.Resolution1,
			"30"
		},
		{
			DynamicResolution.Resolution2,
			"77"
		},
		{
			DynamicResolution.Resolution3,
			"230"
		},
		{
			DynamicResolution.Resolution4,
			"410"
		},
		{
			DynamicResolution.Resolution5,
			"922"
		},
		{
			DynamicResolution.Resolution6,
			"2074"
		},
		{
			DynamicResolution.Resolution7,
			"3686"
		},
		{
			DynamicResolution.Resolution8,
			"8294"
		},
		{
			DynamicResolution.Resolution9,
			"33178"
		}
	};

	public static IReadOnlyDictionary<TextureQuality, string?> TextureQualityLevels { get; } = new Dictionary<TextureQuality, string>
	{
		{
			TextureQuality.Default,
			null
		},
		{
			TextureQuality.Lowest,
			"0"
		},
		{
			TextureQuality.Low,
			"1"
		},
		{
			TextureQuality.Medium,
			"2"
		},
		{
			TextureQuality.High,
			"3"
		}
	};

	public static IReadOnlyDictionary<InGameMenuVersion, Dictionary<string, string?>> IGMenuVersions { get; } = new Dictionary<InGameMenuVersion, Dictionary<string, string>>
	{
		{
			InGameMenuVersion.Default,
			new Dictionary<string, string>
			{
				{ "V2Rollout", null },
				{ "EnableV4", null },
				{ "EnableV4Chrome", null },
				{ "ABTest", null },
				{ "ReportButtonCutOff", null }
			}
		},
		{
			InGameMenuVersion.V2,
			new Dictionary<string, string>
			{
				{ "V2Rollout", "100" },
				{ "EnableV4", "False" },
				{ "EnableV4Chrome", "False" },
				{ "ABTest", "False" },
				{ "ReportButtonCutOff", null }
			}
		},
		{
			InGameMenuVersion.V4,
			new Dictionary<string, string>
			{
				{ "V2Rollout", "0" },
				{ "EnableV4", "True" },
				{ "EnableV4Chrome", "False" },
				{ "ABTest", "False" },
				{ "ReportButtonCutOff", null }
			}
		},
		{
			InGameMenuVersion.V4Chrome,
			new Dictionary<string, string>
			{
				{ "V2Rollout", "0" },
				{ "EnableV4", "True" },
				{ "EnableV4Chrome", "True" },
				{ "ABTest", "False" },
				{ "ReportButtonCutOff", null }
			}
		}
	};

	public static IReadOnlyDictionary<RomarkStart, string?> RomarkStartMappings { get; } = new Dictionary<RomarkStart, string>
	{
		{
			RomarkStart.Disabled,
			null
		},
		{
			RomarkStart.Bar1,
			"1"
		},
		{
			RomarkStart.Bar2,
			"2"
		},
		{
			RomarkStart.Bar3,
			"3"
		},
		{
			RomarkStart.Bar4,
			"4"
		},
		{
			RomarkStart.Bar5,
			"5"
		},
		{
			RomarkStart.Bar6,
			"6"
		},
		{
			RomarkStart.Bar7,
			"7"
		},
		{
			RomarkStart.Bar8,
			"8"
		},
		{
			RomarkStart.Bar9,
			"9"
		},
		{
			RomarkStart.Bar10,
			"10"
		}
	};

	public static IReadOnlyDictionary<Presents, string?> PresentsStartMappings { get; } = new Dictionary<Presents, string>
	{
		{
			Presents.Default,
			null
		},
		{
			Presents.Stoofs,
			"1"
		}
	};

	public static IReadOnlyDictionary<QualityLevel, string?> QualityLevels { get; } = new Dictionary<QualityLevel, string>
	{
		{
			QualityLevel.Disabled,
			null
		},
		{
			QualityLevel.Level1,
			"1"
		},
		{
			QualityLevel.Level2,
			"2"
		},
		{
			QualityLevel.Level3,
			"3"
		},
		{
			QualityLevel.Level4,
			"4"
		},
		{
			QualityLevel.Level5,
			"5"
		},
		{
			QualityLevel.Level6,
			"6"
		},
		{
			QualityLevel.Level7,
			"7"
		},
		{
			QualityLevel.Level8,
			"8"
		},
		{
			QualityLevel.Level9,
			"9"
		},
		{
			QualityLevel.Level10,
			"10"
		},
		{
			QualityLevel.Level11,
			"11"
		},
		{
			QualityLevel.Level12,
			"12"
		},
		{
			QualityLevel.Level13,
			"13"
		},
		{
			QualityLevel.Level14,
			"14"
		},
		{
			QualityLevel.Level15,
			"15"
		},
		{
			QualityLevel.Level16,
			"16"
		},
		{
			QualityLevel.Level17,
			"17"
		},
		{
			QualityLevel.Level18,
			"18"
		},
		{
			QualityLevel.Level19,
			"19"
		},
		{
			QualityLevel.Level20,
			"20"
		},
		{
			QualityLevel.Level21,
			"21"
		}
	};

	public static IReadOnlyDictionary<RefreshRate, string?> RefreshRates { get; } = new Dictionary<RefreshRate, string>
	{
		{
			RefreshRate.Default,
			null
		},
		{
			RefreshRate.RefreshRate75,
			"75"
		},
		{
			RefreshRate.RefreshRate85,
			"80"
		},
		{
			RefreshRate.RefreshRate90,
			"90"
		},
		{
			RefreshRate.RefreshRate100,
			"100"
		},
		{
			RefreshRate.RefreshRate120,
			"120"
		},
		{
			RefreshRate.RefreshRate144,
			"144"
		},
		{
			RefreshRate.RefreshRate165,
			"165"
		},
		{
			RefreshRate.RefreshRate180,
			"180"
		},
		{
			RefreshRate.RefreshRate200,
			"200"
		},
		{
			RefreshRate.RefreshRate240,
			"240"
		},
		{
			RefreshRate.RefreshRate360,
			"360"
		}
	};

	public static IReadOnlyDictionary<Shader, string?> Shaders { get; } = new Dictionary<Shader, string>
	{
		{
			Shader.Disabled,
			null
		},
		{
			Shader.x1,
			"-140000000"
		},
		{
			Shader.x2,
			"-340000000"
		},
		{
			Shader.x3,
			"-640000000"
		}
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
		if (base.Prop.TryGetValue(key, out object value) && value != null)
		{
			return value.ToString();
		}
		return null;
	}

	public void SetPreset(string prefix, object? value)
	{
		foreach (KeyValuePair<string, string> item in PresetFlags.Where<KeyValuePair<string, string>>((KeyValuePair<string, string> x) => x.Key.StartsWith(prefix)))
		{
			SetValue(item.Value, value);
		}
	}

	public void SetPresetEnum(string prefix, string target, object? value)
	{
		foreach (KeyValuePair<string, string> item in PresetFlags.Where<KeyValuePair<string, string>>((KeyValuePair<string, string> x) => x.Key.StartsWith(prefix)))
		{
			if (item.Key.StartsWith(prefix + "." + target))
			{
				SetValue(item.Value, value);
			}
			else
			{
				SetValue(item.Value, null);
			}
		}
	}

	public void MigratePlayerLoggingPreset()
	{
		if (GetPreset("Players.EventLog") == "7")
			return;
		if (GetPreset("Players.LogLevel") == "trace" && GetPreset("Players.LogPattern") == "ExpChat/mountClientApp")
			SetPreset("Players.EventLog", "7");
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

	public T GetPresetEnum<T>(IReadOnlyDictionary<T, string> mapping, string prefix, string value) where T : Enum
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
		base.OriginalProp = base.Prop.ToDictionary<KeyValuePair<string, object>, string, object>((KeyValuePair<string, object> pair) => pair.Key, (KeyValuePair<string, object> pair) => pair.Value?.ToString() ?? string.Empty);
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

	private static readonly string[] AssetWarpPreloadPresets =
	[
			"Cache.Increase1", "Cache.Increase2", "Cache.Increase3", "Cache.Increase4", "Cache.Increase5",
			"Cache.Increase6", "Cache.Increase7", "Cache.Increase8", "Cache.Increase9", "Cache.Increase10",
			"Cache.Increase11", "Cache.Increase12", "Cache.Increase13", "Cache.Increase14", "Cache.Increase15",
			"Cache.Increase16", "Cache.Increase17", "Cache.Increase18", "Cache.Increase19", "Preload.Preload2",
			"Preload.SoundPreload", "Preload.Texture", "Preload.TeleportPreload", "Preload.FontsPreload",
			"Preload.ItemPreload", "Preload.Teleport2"
	];

	private static IEnumerable<KeyValuePair<string, string>> AssetWarpPreloadFlags => AssetWarpPreloadPresets
		.Where(PresetFlags.ContainsKey)
		.Select(preset => new KeyValuePair<string, string>(preset, PresetFlags[preset]))
		.DistinctBy(pair => pair.Value);

	public void ApplyPreloadFlags()
	{
		if (!App.Settings.Prop.AssetWarpEnabled || !App.Settings.Prop.AssetWarpPreloadEnabled)
		{
			if (!App.Settings.Prop.AssetWarpPreloadFlagsOwned)
				return;
			foreach (KeyValuePair<string, string?> backup in App.Settings.Prop.AssetWarpPreloadFlagBackup)
			{
				SetValue(backup.Key, backup.Value);
			}
			App.Settings.Prop.AssetWarpPreloadFlagsOwned = false;
			App.Settings.SaveDeferred();
			return;
		}
		if (!App.Settings.Prop.AssetWarpPreloadFlagsOwned)
		{
			App.Settings.Prop.AssetWarpPreloadFlagBackup = AssetWarpPreloadFlags
				.ToDictionary(pair => pair.Value, pair => GetValue(pair.Value));
			App.Settings.Prop.AssetWarpPreloadFlagsOwned = true;
			App.Settings.Save();
		}
		foreach (string preset in AssetWarpPreloadPresets.Where(preset => preset.StartsWith("Cache.Increase", StringComparison.Ordinal)))
		{
			SetPreset(preset, null);
		}
		SetPreset("Preload.Preload2", "True");
		SetPreset("Preload.SoundPreload", "True");
		SetPreset("Preload.Texture", "True");
		SetPreset("Preload.TeleportPreload", "True");
		SetPreset("Preload.FontsPreload", "True");
		SetPreset("Preload.ItemPreload", "True");
		SetPreset("Preload.Teleport2", "True");
	}

	public void RemoveInstalledPreloadFlags(string path)
	{
		if (!File.Exists(path))
		{
			return;
		}
		string? temporary = null;
		try
		{
			Dictionary<string, System.Text.Json.JsonElement>? flags = JsonFile.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(path, JsonOptions.Tolerant, 16777216);
			if (flags == null)
			{
				return;
			}
			bool changed = false;
			foreach (KeyValuePair<string, string> owned in AssetWarpPreloadFlags)
			{
				bool cacheFlag = owned.Key.StartsWith("Cache.Increase", StringComparison.Ordinal);
				bool hasCurrent = flags.TryGetValue(owned.Value, out System.Text.Json.JsonElement current);
				bool stillOwned = cacheFlag ? !hasCurrent : hasCurrent && string.Equals(current.ToString(), "True", StringComparison.OrdinalIgnoreCase);
				if (!stillOwned)
					continue;
				if (App.Settings.Prop.AssetWarpPreloadFlagBackup.TryGetValue(owned.Value, out string? previous) && previous != null)
				{
					flags[owned.Value] = System.Text.Json.JsonSerializer.SerializeToElement(previous);
					changed = true;
				}
				else
					changed |= flags.Remove(owned.Value);
			}
			if (!changed)
			{
				return;
			}
			FileAttributes attributes = File.GetAttributes(path);
			bool readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
			if (readOnly)
			{
				File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
			}
			try
			{
				temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
				File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(flags, JsonOptions.Indented));
				File.Move(temporary, path, true);
				temporary = null;
				App.Settings.Prop.AssetWarpPreloadFlagBackup.Clear();
				App.Settings.SaveDeferred();
			}
			finally
			{
				if (readOnly && File.Exists(path))
				{
					File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
				}
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("FastFlagManager::RemoveInstalledPreloadFlags", "Could not remove AssetWarp preload flags: " + ex.Message);
		}
		finally
		{
			if (!string.IsNullOrEmpty(temporary))
			{
				try
				{
					File.Delete(temporary);
				}
				catch
				{
				}
			}
		}
	}
}
