using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Voidstrap.Utility;

namespace Voidstrap.Integrations;

internal static class VoidEditLauncher
{
    private const string NewestRelease = "https://api.github.com/repos/KloBraticc/VoidEdit/releases?per_page=1";

    private const string ReleasePage = "https://github.com/KloBraticc/VoidEdit/releases";

    private const string DownloadPrefix = "https://github.com/KloBraticc/VoidEdit/releases/download/";

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Voidstrap");
        return client;
    }

    private static string AssetName =>
        OperatingSystem.IsWindows() ? "voidedit-windows-x64.exe" : OperatingSystem.IsLinux() ? "voidedit-linux-x64" : "voidedit-macos-arm64";

    private static string ExecutablePath => Path.Combine(Paths.Extensions, "VoidEdit", AssetName);

    private static string VersionPath => Path.Combine(Paths.Extensions, "VoidEdit", "version.txt");

    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public enum InstallState
    {
        NotInstalled,
        UpdateAvailable,
        Installed,
    }

    public static bool IsInstalled => File.Exists(ExecutablePath);

    public static async Task<InstallState> GetStateAsync()
    {
        const string LOG_IDENT = "VoidEditLauncher::GetStateAsync";
        if (!IsInstalled)
            return InstallState.NotInstalled;
        try
        {
            string installed = File.Exists(VersionPath) ? (await File.ReadAllTextAsync(VersionPath)).Trim() : "";
            using HttpResponseMessage response = await Client.GetAsync(NewestRelease);
            if (!response.IsSuccessStatusCode)
                return InstallState.Installed;
            using JsonDocument releases = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement release = releases.RootElement[0];
            string latest = release.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString() ?? "" : "";
            return latest.Length > 0 && !latest.Equals(installed, StringComparison.Ordinal) ? InstallState.UpdateAvailable : InstallState.Installed;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or TaskCanceledException or InvalidOperationException or IndexOutOfRangeException)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not check for an update: " + ex.GetType().Name);
            return InstallState.Installed;
        }
    }

    public static async Task<bool> InstallAsync()
    {
        const string LOG_IDENT = "VoidEditLauncher::InstallAsync";
        try
        {
            return await DownloadAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or TaskCanceledException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException)
        {
            App.Logger.WriteLine(LOG_IDENT, "Void Edit could not be downloaded: " + ex.GetType().Name);
            return false;
        }
    }

    public static async Task OpenAsync(string? project = null)
    {
        const string LOG_IDENT = "VoidEditLauncher::OpenAsync";
        try
        {
            if (!File.Exists(ExecutablePath) && !await DownloadAsync())
            {
                Utilities.OpenWebLink(ReleasePage);
                return;
            }
            try
            {
                Launch(project);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                App.Logger.WriteLine(LOG_IDENT, "The saved copy would not start, downloading it again");
                if (!await DownloadAsync())
                    throw;
                Launch(project);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or TaskCanceledException or InvalidOperationException or IndexOutOfRangeException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            App.Logger.WriteLine(LOG_IDENT, "Void Edit could not open: " + ex.GetType().Name);
            Utilities.OpenWebLink(ReleasePage);
        }
    }

    private static void Launch(string? project)
    {
        ProcessStartInfo info = new(ExecutablePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExecutablePath)! };
        if (!string.IsNullOrEmpty(project))
            info.ArgumentList.Add(project);
        Process.Start(info);
    }

    private static async Task<bool> DownloadAsync()
    {
        await DownloadLock.WaitAsync();
        try
        {
            return await DownloadLatestAsync();
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    private static async Task<bool> DownloadLatestAsync()
    {
        const string LOG_IDENT = "VoidEditLauncher::DownloadLatestAsync";
        using HttpResponseMessage response = await Client.GetAsync(NewestRelease);
        if (!response.IsSuccessStatusCode)
        {
            App.Logger.WriteLine(LOG_IDENT, "No release is available: " + (int)response.StatusCode);
            return false;
        }
        using JsonDocument releases = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement release = releases.RootElement[0];
        string tagName = release.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString() ?? "" : "";
        foreach (JsonElement asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName)
                continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            string digest = asset.TryGetProperty("digest", out JsonElement value) ? value.GetString() ?? "" : "";
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal) || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                return false;
            byte[] data = await Client.GetByteArrayAsync(url);
            if (!Convert.ToHexString(SHA256.HashData(data)).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
            {
                App.Logger.WriteLine(LOG_IDENT, "The download failed its integrity check");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ExecutablePath)!);
            string partial = ExecutablePath + ".part";
            await File.WriteAllBytesAsync(partial, data);
            File.Move(partial, ExecutablePath, true);
            await File.WriteAllTextAsync(VersionPath, tagName);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return true;
        }
        return false;
    }
}
