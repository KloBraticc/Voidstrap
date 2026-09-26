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

        public static IPlatformHost? RuntimeHost => RuntimeHostValue.Value;

        public static bool SupportsOverlays => IsWindows;
        public static bool SupportsWebBrowser => IsWindows;
        public static bool SupportsInputHooks => IsWindows || IsLinux;
        public static bool SupportsRegistry => IsWindows;
        public static bool SupportsTrayIcon => IsWindows || IsLinux;
        public static bool SupportsAudioDucking => IsWindows || IsLinux;
        public static bool SupportsWindowsClient => IsWindows;

        private static IPlatformHost? CreateRuntimeHost()
        {
            if (IsLinux)
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
