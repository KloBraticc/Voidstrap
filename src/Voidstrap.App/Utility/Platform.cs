using System;
using System.Runtime.Versioning;
using System.Threading;
using Voidstrap.Core;
using Voidstrap.Extensions;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.Utility
{
    public static class Platform
    {
        private static readonly Lazy<IPlatformHost?> RuntimeHostValue = new(CreateRuntimeHost, LazyThreadSafetyMode.ExecutionAndPublication);

        [SupportedOSPlatformGuard("windows")]
        public static readonly bool IsWindows = OperatingSystem.IsWindows();
        [UnsupportedOSPlatformGuard("windows")]
        public static readonly bool IsMacOS = OperatingSystem.IsMacOS();
        [UnsupportedOSPlatformGuard("windows")]
        public static readonly bool IsLinux = OperatingSystem.IsLinux();
        [UnsupportedOSPlatformGuard("windows")]
        public static readonly bool IsFreeBSD = HostSystem.IsFreeBSD;
        [UnsupportedOSPlatformGuard("windows")]
        public static readonly bool UsesLinuxHost = HostSystem.UsesLinuxHost;

        public static IPlatformHost? RuntimeHost => RuntimeHostValue.Value;

        public static bool SupportsOverlays => IsWindows;
        public static bool SupportsWebBrowser => IsWindows;
        public static bool SupportsInputHooks => IsWindows || UsesLinuxHost || IsMacOS;
        public static bool SupportsRegistry => IsWindows;
        public static bool SupportsTrayIcon => IsWindows || UsesLinuxHost || IsMacOS;
        public static bool SupportsAudioDucking => IsWindows || UsesLinuxHost;
        public static bool SupportsWindowsClient => IsWindows;

        public static bool UsesPortableUi => !IsWindows;

        public static string RobloxPlayerProcessName => IsMacOS ? "RobloxPlayer" : "RobloxPlayerBeta";

        public static string RobloxStudioProcessName => IsMacOS ? "RobloxStudio" : "RobloxStudioBeta";

        private static IPlatformHost? CreateRuntimeHost()
        {
            if (UsesLinuxHost)
            {
                return new LinuxPlatformHost(new SystemProcessService(), LinuxGithubPlatformUpdater.Create());
            }

            if (IsMacOS)
            {
                return new MacOSPlatformHost();
            }

            return null;
        }
    }
}
