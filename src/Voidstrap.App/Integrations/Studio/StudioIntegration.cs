using System;
using Voidstrap.Enums;
using Voidstrap.Integrations;

namespace Voidstrap.Integrations.Studio
{
    public static class StudioIntegration
    {
        private const string LogTag = "StudioIntegration";

        private static readonly object _lock = new object();

        private static StudioRichPresence? _rpc;

        public static void Start(bool studioSession = false)
        {
            try
            {
                LaunchSettings launchSettings = App.LaunchSettings;
                if (!studioSession && (launchSettings == null || launchSettings.RobloxLaunchMode != LaunchMode.Studio))
                {
                    return;
                }
                if (Voidstrap.Utility.Platform.IsMacOS)
                {
                    StartMacOS();
                    return;
                }
                StudioPluginInstaller.RestoreAfterClassicClient();
                if (Voidstrap.Utility.Platform.IsLinux)
                {
                    StartLinux();
                    return;
                }
                if (!App.Settings.Prop.StudioPluginEnabled)
                {
                    return;
                }
                lock (_lock)
                {
                    StudioBridge.Start();
                    StudioPluginInstaller.EnsureInstalled();
                    if (_rpc == null)
                    {
                        _rpc = new StudioRichPresence();
                    }
                }
                App.Logger.WriteLine(LogTag, "Studio integration started");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LogTag, "Start failed: " + ex.Message);
            }
        }

        private static void StartMacOS()
        {
            bool presence = App.Settings.Prop.UseDiscordRichPresence;
            lock (_lock)
            {
                if (presence && _rpc == null)
                {
                    _rpc = new StudioRichPresence();
                }
            }
            App.Logger.WriteLine(LogTag, "Studio integration started for macOS, presence " + (presence ? "on" : "off"));
        }

        private static void StartLinux()
        {
            bool plugin = App.Settings.Prop.StudioPluginEnabled;
            bool presence = App.Settings.Prop.UseDiscordRichPresence;
            lock (_lock)
            {
                if (plugin)
                {
                    StudioBridge.Start();
                    StudioPluginInstaller.EnsureInstalled(force: true);
                }
                if (presence && _rpc == null)
                {
                    _rpc = new StudioRichPresence();
                }
            }
            App.Logger.WriteLine(LogTag, "Studio integration started for Vinegar, presence " + (presence ? "on" : "off") + ", plugin " + (plugin ? "on" : "off"));
        }

        public static void Shutdown()
        {
            lock (_lock)
            {
                try
                {
                    _rpc?.Dispose();
                }
                catch
                {
                }
                _rpc = null;
                StudioBridge.Shutdown();
            }
        }
    }
}
