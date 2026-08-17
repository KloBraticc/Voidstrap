using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Voidstrap.Utility;

public static class MarketBackground
{
    private const string LOG_IDENT = "MarketBackground";
    private const string RemotePath = "/assets/img/blackmarket-bg.png";
    private const double DarkenOpacity = 0.62;
    private const int MaxBackgroundBytes = 16 * 1024 * 1024;

    private static bool _active;
    private static readonly SemaphoreSlim CacheGate = new SemaphoreSlim(1, 1);

    private static string CacheFile => Path.Combine(Paths.Temp, "blackmarket-bg.png");

    public static bool IsActive => _active;

    public static void Apply()
    {
        _active = true;
        _ = ApplyAsync();
    }

    public static void Restore()
    {
        if (!_active)
            return;
        _active = false;
        try
        {
            UI.Elements.Settings.MainWindow? window = FindWindow();
            window?.RestoreBackground();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT + "::Restore", ex);
        }
    }

    private static UI.Elements.Settings.MainWindow? FindWindow()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is UI.Elements.Settings.MainWindow main)
                return main;
        }
        return null;
    }

    private static async Task ApplyAsync()
    {
        try
        {
            string path = await EnsureCachedAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(path) || !_active)
                return;
            UI.Elements.Settings.MainWindow? window = FindWindow();
            if (window == null)
                return;
            await window.SetBackgroundImage(path).ConfigureAwait(true);
            if (_active)
                window.SetBackgroundOverlay(DarkenOpacity);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT + "::Apply", ex);
        }
    }

    private static async Task<string> EnsureCachedAsync()
    {
        string path = CacheFile;
        await CacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length is > 0 and <= MaxBackgroundBytes)
                return path;

            Directory.CreateDirectory(Paths.Temp);
            using HttpResponseMessage response = await App.HttpClient
                .GetAsync(App.WebsiteBaseUrl + RemotePath)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return string.Empty;

            byte[] bytes = await Http.ReadBytesBoundedAsync(response.Content, MaxBackgroundBytes, CancellationToken.None).ConfigureAwait(false);
            if (bytes.Length == 0)
                return string.Empty;

            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, bytes).ConfigureAwait(false);
                File.Move(temporary, path, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            return path;
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT + "::Cache", ex);
            return string.Empty;
        }
        finally
        {
            CacheGate.Release();
        }
    }
}
