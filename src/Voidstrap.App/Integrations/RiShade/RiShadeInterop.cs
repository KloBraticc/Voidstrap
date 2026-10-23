using System;
using System.Runtime.InteropServices;

namespace Voidstrap.Integrations.RiShade
{
    internal static partial class RiShadeInterop
    {
        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRuntimeLoad", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool RuntimeLoad(string dxgiPath);

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRuntimeCreate", StringMarshalling = StringMarshalling.Utf8)]
        public static partial IntPtr RuntimeCreate(IntPtr device, IntPtr immediateContext, IntPtr swapchain, IntPtr window, string configPath);

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRuntimeAttachWindow")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool RuntimeAttachWindow(IntPtr runtime, IntPtr window);

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRuntimeDetachWindow")]
        public static partial void RuntimeDetachWindow();

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRuntimeDestroy")]
        public static partial void RuntimeDestroy(IntPtr runtime);

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRuntimeOpenOverlay")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool RuntimeOpenOverlay(IntPtr runtime, [MarshalAs(UnmanagedType.I1)] bool open);

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRuntimePresent")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool RuntimePresent(IntPtr runtime);

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRenderEffects")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool RenderEffects(IntPtr runtime, IntPtr targetRtv, IntPtr targetRtvSrgb);

        [LibraryImport("rishade_depth_bridge", EntryPoint = "RiShadeDepthBridgeSetModel", StringMarshalling = StringMarshalling.Utf16)]
        public static partial void DepthBridgeSetModel(string modelFile);

        [LibraryImport("rishade_depth_bridge", EntryPoint = "RiShadeDepthBridgeReload", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool DepthBridgeReload(string modelFile);

        [LibraryImport("rishade_depth_bridge", EntryPoint = "RiShadeDepthBridgeUsingModel")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool DepthBridgeUsingModel();

        [LibraryImport("rishade_depth_bridge", EntryPoint = "RiShadeDepthBridgeInitialize", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool DepthBridgeInitialize(IntPtr device, IntPtr immediateContext, string modelDirectory);

        [LibraryImport("rishade_depth_bridge", EntryPoint = "RiShadeDepthBridgeUpdate")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool DepthBridgeUpdate(IntPtr sourceSrv, long timestamp);

        [LibraryImport("rishade_depth_bridge", EntryPoint = "RiShadeDepthBridgeSetDevice")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool DepthBridgeSetDevice(IntPtr device, IntPtr immediateContext);

        [LibraryImport("rishade_depth_bridge", EntryPoint = "RiShadeDepthBridgeReleaseDevice")]
        public static partial void DepthBridgeReleaseDevice();

        [LibraryImport("rishade_depth_bridge", EntryPoint = "RiShadeDepthBridgeShutdown")]
        public static partial void DepthBridgeShutdown();

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeAddonIsOverlayOpen")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool IsOverlayOpen();

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeRuntimeSelectPreset", StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool RuntimeSelectPreset(IntPtr runtime, string presetPath);

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeAddonActiveRuntime")]
        public static partial IntPtr ActiveRuntime();

        [LibraryImport("rishade_reshade_addon", EntryPoint = "RiShadeAddonCopyCurrentPresetPath")]
        public static partial nuint CopyCurrentPresetPath(IntPtr buffer, nuint capacity);

        [LibraryImport("rishade_core", EntryPoint = "RiShadeInstallDlss", StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool InstallDlss(string baseDirectory, IntPtr statusBuffer, nuint bufferLength);

        [LibraryImport("rishade_core", EntryPoint = "RiShadeRemoveDlss", StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool RemoveDlss(string baseDirectory, IntPtr statusBuffer, nuint bufferLength);

        [LibraryImport("rishade_core", EntryPoint = "RiShadeDlssInstalled", StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool DlssInstalled(string baseDirectory);

        [LibraryImport("rishade_core", EntryPoint = "RiShadeHasRtxGpu")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool HasRtxGpu();

        [LibraryImport("rishade_core", EntryPoint = "RiShadePrepareConfig", StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool PrepareConfig(string baseDirectory);

        [LibraryImport("rishade_core", EntryPoint = "RiShadeInstallEffectsAndPresets", StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool InstallEffectsAndPresets(string baseDirectory, IntPtr statusBuffer, nuint bufferLength);

        [LibraryImport("rishade_core", EntryPoint = "RiShadePresetFileIsLoadable", StringMarshalling = StringMarshalling.Utf8)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool PresetFileIsLoadable(string presetPath);
    }
}
