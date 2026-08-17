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

        public static void Start()
        {
            try
            {
                LaunchSettings launchSettings = App.LaunchSettings;
                if (launchSettings == null || launchSettings.RobloxLaunchMode != LaunchMode.Studio)
                {
                    return;
                }
                StudioPluginInstaller.RestoreAfterClassicClient();
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
