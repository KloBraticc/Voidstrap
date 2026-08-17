using System;
using System.IO;
using System.Threading;
using System.Windows;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.Integrations.RiShade
{
    public static class RiShadeManager
    {
        private static bool _installed;
        private static FileSystemWatcher? _settingsWatcher;
        private static System.Threading.Timer? _reloadDebounce;

        public static void Install()
        {
            if (_installed)
                return;
            _installed = true;
            RiShadeSettings.Load();
            App.Logger.WriteLine("RiShade", "Installed, enabled setting is " + App.Settings.Prop.RiShadeEnabled);
            if (App.Settings.Prop.RiShadeEnabled)
                StartSettingsWatcher();
            OverlayHub.Refresh();
        }

        private static void StartSettingsWatcher()
        {
            if (_settingsWatcher != null)
                return;
            try
            {
                _reloadDebounce = new System.Threading.Timer(OnReloadDebounce, null, Timeout.Infinite, Timeout.Infinite);
                _settingsWatcher = new FileSystemWatcher(Paths.Config, "RiShadeSettings.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                };
                _settingsWatcher.Changed += SettingsFile_Changed;
                _settingsWatcher.Created += SettingsFile_Changed;
                _settingsWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("RiShadeManager::StartSettingsWatcher", ex);
            }
        }

        private static void SettingsFile_Changed(object sender, FileSystemEventArgs e)
        {
			if (!_installed)
				return;
            try
            {
				_reloadDebounce?.Change(500, Timeout.Infinite);
            }
            catch
            {
            }
        }

        private static void OnReloadDebounce(object? state)
        {
			if (!_installed)
				return;
            if (Environment.TickCount64 - RiShadeSettings.LastSaveTicks < 2000)
                return;
            RiShadeSettings.Load();
			if (!_installed)
				return;
            App.Logger.WriteLine("RiShade", "Settings reloaded live");
            OverlayHub.Refresh();
        }

        public static void SetEnabled(bool enabled)
        {
            Install();
            if (enabled && !App.Settings.Prop.RiShadeEnabled)
            {
                MessageBoxResult result = Frontend.ShowMessageBox(
                    "RiShade should only be enabled for screenshots, not regular gameplay. It is not intended for sustained gameplay and is highly unstable.\n\nEnable RiShade anyway?",
                    MessageBoxImage.Warning,
                    MessageBoxButton.YesNo,
                    MessageBoxResult.No);
                if (result != MessageBoxResult.Yes)
                    return;
            }
            App.Settings.Prop.RiShadeEnabled = enabled;
            App.Settings.SaveDeferred();
            App.Logger.WriteLine("RiShade", enabled ? "Enabled by user" : "Disabled by user");
            if (enabled)
                StartSettingsWatcher();
            else
                StopSettingsWatcher();
            OverlayHub.Refresh();
        }

        private static void StopSettingsWatcher()
        {
            try
            {
                if (_settingsWatcher != null)
                {
                    _settingsWatcher.EnableRaisingEvents = false;
                    _settingsWatcher.Changed -= SettingsFile_Changed;
                    _settingsWatcher.Created -= SettingsFile_Changed;
                    _settingsWatcher.Dispose();
                    _settingsWatcher = null;
                }
                _reloadDebounce?.Dispose();
                _reloadDebounce = null;
            }
            catch
            {
            }
        }

        public static void OnGameJoin()
        {
            OverlayHub.OnGameJoin();
        }

        public static void OnGameLeave()
        {
            OverlayHub.OnGameLeave();
        }

        public static void Shutdown()
        {
			_installed = false;
            OverlayHub.Shutdown();
            RiShadeDepth.Shutdown();
            StopSettingsWatcher();
        }
    }
}
