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
    private const string LatestRelease = "https://api.github.com/repos/KloBraticc/VoidEdit/releases/latest";

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

    public static async Task OpenAsync()
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
                Launch();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                App.Logger.WriteLine(LOG_IDENT, "The saved copy would not start, downloading it again");
                if (!await DownloadAsync())
                    throw;
                Launch();
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or TaskCanceledException or System.ComponentModel.Win32Exception)
        {
            App.Logger.WriteLine(LOG_IDENT, "Void Edit could not open: " + ex.GetType().Name);
            Utilities.OpenWebLink(ReleasePage);
        }
    }

    private static void Launch()
    {
        Process.Start(new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(ExecutablePath)! });
    }

    private static async Task<bool> DownloadAsync()
    {
        const string LOG_IDENT = "VoidEditLauncher::DownloadAsync";
        using HttpResponseMessage response = await Client.GetAsync(LatestRelease);
        if (!response.IsSuccessStatusCode)
        {
            App.Logger.WriteLine(LOG_IDENT, "No release is available: " + (int)response.StatusCode);
            return false;
        }
        using JsonDocument release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (JsonElement asset in release.RootElement.GetProperty("assets").EnumerateArray())
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
            await File.WriteAllBytesAsync(ExecutablePath, data);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(ExecutablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return true;
        }
        return false;
    }
}
