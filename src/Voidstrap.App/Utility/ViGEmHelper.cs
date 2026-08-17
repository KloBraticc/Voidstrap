using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Voidstrap.Utility
{
    static class ViGEmHelper
    {
        private const string ViGEmBusPath = @"\\.\ViGEmBus";
        private const string DownloadUrl = "https://github.com/nefarius/ViGEmBus/releases/download/v1.22.0/ViGEmBus_1.22.0_x64_x86_arm64.exe";
        private const string DownloadSha256 = "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A";
        private const long MaxDownloadBytes = 10000000L;
        private const string LogTag = "ViGEmHelper";

        private static readonly HttpClient _httpClient = VpnHttpClient.Create(TimeSpan.FromMinutes(5));
        private static readonly SemaphoreSlim _installLock = new(1, 1);
        private static bool _isInstalling;

        public static event Action<string, bool>? OnProgressChanged;

        private static void ReportProgress(string title, bool show)
        {
            OnProgressChanged?.Invoke(title, show);
        }

        public static bool IsViGEmBusInstalled()
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
                return false;

            if (Voidstrap.Integrations.ViGEmInterop.IsInstalled())
                return true;

            using var handle = PInvoke.CreateFile(
                ViGEmBusPath,
                (uint)(GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE),
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
                null,
                FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                null
            );
            return !handle.IsInvalid;
        }

        public static async Task<bool> EnsureViGEmBusInstalledAsync(Action<string>? statusCallback = null)
        {
            if (!Voidstrap.Utility.Platform.IsWindows)
            {
                statusCallback?.Invoke(Strings.Common_NotAvailableOnPlatform);
                return false;
            }

            if (IsViGEmBusInstalled())
                return true;

            if (_isInstalling)
                return false;

            await _installLock.WaitAsync();
            try
            {
                _isInstalling = true;

                var result = Frontend.ShowMessageBox(
                    "ViGEmBus is required for Double Movement.\nWould you like to install it?",
                    MessageBoxImage.Warning,
                    MessageBoxButton.YesNo);

                if (result != MessageBoxResult.Yes)
                {
                    statusCallback?.Invoke("ViGEmBus install declined");
                    return false;
                }

                string installerPath = Path.Combine(Paths.Temp, "ViGEmBusSetup.exe");

                try
                {
                    statusCallback?.Invoke("Downloading ViGEmBus...");
                    ReportProgress("Downloading ViGEmBus...", true);
                    App.Logger.WriteLine(LogTag, "Downloading ViGEmBus installer...");

                    await ResilientDownload.DownloadAsync(_httpClient, [DownloadUrl], installerPath, MaxDownloadBytes, expectedSha256: DownloadSha256,
                        progress: (read, total) =>
                        {
                            if (total is not > 0)
                                return;
                            double percent = (double)read / total.Value * 100;
                            string msg = $"Downloading ViGEmBus... {percent:0}%";
                            statusCallback?.Invoke(msg);
                            ReportProgress(msg, true);
                        }).ConfigureAwait(false);

                    statusCallback?.Invoke("Installing ViGEmBus...");
                    ReportProgress("Installing ViGEmBus...", true);
                    App.Logger.WriteLine(LogTag, "Launching ViGEmBus installer...");

                    using var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = installerPath,
                        UseShellExecute = true
                    });

                    if (process != null)
                        await process.WaitForExitAsync().ConfigureAwait(false);

                    if (!IsViGEmBusInstalled())
                    {
                        App.Logger.WriteLine(LogTag, "Polling for installation completion...");
                        for (int i = 0; i < 15; i++)
                        {
                            await Task.Delay(2000).ConfigureAwait(false);
                            if (IsViGEmBusInstalled())
                                break;
                        }
                    }

                    try { File.Delete(installerPath); }
                    catch { }

                    if (IsViGEmBusInstalled())
                    {
                        statusCallback?.Invoke("ViGEmBus ready!");
                        ReportProgress("ViGEmBus ready!", false);
                        App.Logger.WriteLine(LogTag, "ViGEmBus installed successfully");
                        return true;
                    }

                    App.Logger.WriteLine(LogTag, "Installer did not result in ViGEmBus being installed");
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException(LogTag, ex);
                    try { File.Delete(installerPath); }
                    catch { }
                }

                statusCallback?.Invoke("ViGEmBus install failed");
                ReportProgress("Install failed", false);
                return false;
            }
            finally
            {
                _isInstalling = false;
                _installLock.Release();
            }
        }
    }
}
