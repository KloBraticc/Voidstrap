using System;
using Microsoft.Win32;

namespace Voidstrap.Utility;

public static class EmulationBypassService
{
    private const string LayersKeyPath = "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags\\Layers";

    private const string BackupKeyPath = "Software\\Voidstrap\\EmulationBypass";

    private const string GpuPreferencesKeyPath = "Software\\Microsoft\\DirectX\\UserGpuPreferences";

    private const string GpuBackupKeyPath = "Software\\Voidstrap\\EmulationBypass\\Gpu";

    public static void RestoreCompatLayers()
    {
        if (!Platform.SupportsRegistry)
            return;

        Restore(GpuBackupKeyPath, GpuPreferencesKeyPath);
        Restore(BackupKeyPath, LayersKeyPath);
    }

    private static void Restore(string backupPath, string targetPath)
    {
        try
        {
            using RegistryKey? backup = Registry.CurrentUser.OpenSubKey(backupPath, writable: true);

            if (backup == null)
                return;

            using RegistryKey? target = Registry.CurrentUser.CreateSubKey(targetPath);

            if (target == null)
                return;

            foreach (string name in backup.GetValueNames())
            {
                string original = backup.GetValue(name) as string ?? string.Empty;

                if (string.IsNullOrEmpty(original))
                    target.DeleteValue(name, throwOnMissingValue: false);
                else
                    target.SetValue(name, original);

                backup.DeleteValue(name, throwOnMissingValue: false);
            }

            App.Logger.WriteLine("EmulationBypassService::Restore", "Restored the original registry values under " + targetPath);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("EmulationBypassService::Restore", "Could not restore the original registry values: " + ex.Message);
        }
    }
}
