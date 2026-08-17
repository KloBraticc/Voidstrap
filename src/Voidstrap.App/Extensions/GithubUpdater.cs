using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Voidstrap;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Extensions;

public static class GithubUpdater
{
    private const long MaxUpdateBytes = 536870912L;

    private static readonly HttpClient http = CreateClient();

    private static HttpClient CreateClient()
    {
        HttpClient client = Voidstrap.Utility.VpnHttpClient.Create(TimeSpan.FromMinutes(10));
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Voidstrap-Updater");
        return client;
    }

    public static async Task<string?> GetLatestVersionTagAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (App.AllowPreReleaseUpdates)
            {
                var releases = await Voidstrap.Utility.GitHubCache.GetJsonWithFallbackAsync<List<Voidstrap.Models.APIs.GitHub.GithubRelease>>(
                    App.ProjectReleaseListApi,
                    App.ProjectFallbackReleaseListApi,
                    TimeSpan.FromMinutes(15),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var newest = releases?.FirstOrDefault(release => release != null && !release.Draft && release.Assets != null);
                if (newest != null && !string.IsNullOrEmpty(newest.TagName))
                    return newest.TagName;
                App.Logger.WriteLine("GitHubUpdater", "Prerelease lookup found nothing, using the stable release");
            }

            string? response = await Voidstrap.Utility.GitHubCache.GetStringWithFallbackAsync(
                App.ProjectReleaseApi,
                App.ProjectFallbackReleaseApi,
                TimeSpan.FromMinutes(15),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (response == null)
                return null;
            using var doc = JsonDocument.Parse(response);
            return doc.RootElement.GetProperty("tag_name").GetString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("GitHubUpdater", $"Failed to get latest release tag: {ex}");
            return null;
        }
    }

    public static async Task<bool> DownloadAndInstallUpdate(string tag, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var releases = await Voidstrap.Utility.GitHubCache.GetJsonWithFallbackAsync<List<Voidstrap.Models.APIs.GitHub.GithubRelease>>(
                App.ProjectReleaseListApi,
                App.ProjectFallbackReleaseListApi,
                TimeSpan.FromMinutes(15),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var release = releases?.FirstOrDefault(candidate =>
                candidate != null &&
                !candidate.Draft &&
                string.Equals(candidate.TagName, tag, StringComparison.OrdinalIgnoreCase));
            if (release == null)
            {
                string? response = await Voidstrap.Utility.GitHubCache.GetStringWithFallbackAsync(App.ProjectReleaseApi, App.ProjectFallbackReleaseApi, TimeSpan.FromMinutes(15));
                cancellationToken.ThrowIfCancellationRequested();
                if (response == null)
                    return false;
                using var doc = JsonDocument.Parse(response);
                string latestTag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
                if (!string.Equals(latestTag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.WriteLine("GitHubUpdater", "No release matches the tag " + tag);
                    return false;
                }
                release = new Voidstrap.Models.APIs.GitHub.GithubRelease
                {
                    TagName = latestTag,
                    Assets = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<GithubReleaseAsset>>(doc.RootElement.GetProperty("assets").GetRawText())
                };
            }

            if (release.Prerelease && !App.AllowPreReleaseUpdates)
            {
                App.Logger.WriteLine("GitHubUpdater", "Refusing to install prerelease " + tag + " because prerelease updates are off");
                return false;
            }

            string expectedAssetName = OperatingSystem.IsLinux()
                ? LinuxBundleInstaller.GetAssetName(tag, LinuxBundleInstaller.GetCurrentRuntimeIdentifier())
                : "Voidstrap.exe";

            foreach (var asset in release.Assets ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = asset.Name ?? "";
                string downloadUrl = asset.BrowserDownloadUrl ?? "";
                string digest = asset.Digest ?? "";
                string state = asset.State ?? "";

                bool assetNameMatches = string.Equals(name, expectedAssetName, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                if (assetNameMatches &&
                    string.Equals(state, "uploaded", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) &&
                    uri.Scheme == Uri.UriSchemeHttps &&
                    uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    return OperatingSystem.IsLinux()
                        ? await UpdateLinuxBundle(downloadUrl, name, digest, cancellationToken)
                        : await UpdateExe(downloadUrl, name, digest, tag, cancellationToken);
            }

            App.Logger.WriteLine("GitHubUpdater", OperatingSystem.IsLinux() ? "No valid Linux bundle asset found." : "No valid Voidstrap executable asset found.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("GitHubUpdater", $"Update failed: {ex}");
            return false;
        }
    }

    private static async Task<bool> UpdateLinuxBundle(string url, string name, string digest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Voidstrap.Utility.InterProcessLock updateLock = new("AutoUpdater", TimeSpan.FromSeconds(5));
        if (!updateLock.IsAcquired)
            throw new IOException("Another update is already in progress");
        cancellationToken.ThrowIfCancellationRequested();

        string tempDirectory = Path.Combine(Path.GetTempPath(), "Voidstrap_Update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string archivePath = Path.Combine(tempDirectory, name);
            await DownloadToFileAsync(url, archivePath, digest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string currentExecutable = Environment.ProcessPath ?? throw new InvalidOperationException("The current executable path is unavailable");
            await LinuxBundleInstaller.InstallAsync(archivePath, currentExecutable, cancellationToken);
            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                    Directory.Delete(tempDirectory, true);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("GitHubUpdater", "Temporary update cleanup failed: " + ex.Message);
            }
        }
    }

    private static async Task<bool> UpdateExe(string url, string name, string digest, string tag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Voidstrap.Utility.InterProcessLock updateLock = new("AutoUpdater", TimeSpan.FromSeconds(5));
        if (!updateLock.IsAcquired)
            throw new IOException("Another update is already in progress");
        cancellationToken.ThrowIfCancellationRequested();
        string tempDir = Path.Combine(Path.GetTempPath(), "Voidstrap_Update");
        Directory.CreateDirectory(tempDir);

        string exePath = Path.Combine(tempDir, name);
        await DownloadToFileAsync(url, exePath, digest, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        string currentExe = Environment.ProcessPath!;
        string backupExe = currentExe + ".old";
        string replacementExe = currentExe + ".update";
        if (File.Exists(backupExe)) File.Delete(backupExe);
        File.Copy(exePath, replacementExe, true);
        File.Replace(replacementExe, currentExe, backupExe, true);
        UpdateInstalledMetadata(tag);

        return true;
    }

    private static void UpdateInstalledMetadata(string tag)
    {
        if (!Voidstrap.Utility.Platform.SupportsRegistry)
            return;
        try
        {
            string? installedRoot = Voidstrap.Utility.InstallRecord.Read();
            if (string.IsNullOrWhiteSpace(installedRoot) ||
                !string.Equals(Path.GetFullPath(installedRoot), Path.GetFullPath(Paths.Base), StringComparison.OrdinalIgnoreCase))
                return;
            string version = tag.Trim().TrimStart('v', 'V');
            if (version.Length == 0 || version.Length > 64 || version.Any(char.IsControl))
                return;
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap", true);
            key?.SetValueSafe("DisplayVersion", version);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("GitHubUpdater", "Installed version metadata update failed: " + ex.Message);
        }
    }

    private static async Task DownloadToFileAsync(string url, string path, string digest, CancellationToken token)
    {
        if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 71)
            throw new CryptographicException("The update has no valid SHA256 digest");
        await Voidstrap.Utility.ResilientDownload.DownloadAsync(http, [url], path, MaxUpdateBytes, token, digest).ConfigureAwait(false);
    }
}
