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
            if (OperatingSystem.IsLinux())
                return await GetLatestLinuxVersionTagAsync(cancellationToken).ConfigureAwait(false);
            if (App.AllowPreReleaseUpdates)
            {
                var releases = await Voidstrap.Utility.GitHubCache.GetJsonWithFallbackAsync<List<Voidstrap.Models.APIs.GitHub.GithubRelease>>(
                    App.ProjectReleaseListApi,
                    App.ProjectFallbackReleaseListApi,
                    TimeSpan.Zero,
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
                TimeSpan.Zero,
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
                TimeSpan.Zero,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var release = releases?.FirstOrDefault(candidate =>
                candidate != null &&
                !candidate.Draft &&
                string.Equals(candidate.TagName, tag, StringComparison.OrdinalIgnoreCase));
            if (release == null)
            {
                string? response = await Voidstrap.Utility.GitHubCache.GetStringWithFallbackAsync(App.ProjectReleaseApi, App.ProjectFallbackReleaseApi, TimeSpan.Zero, cancellationToken);
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

            if (OperatingSystem.IsLinux())
                return await InstallLinuxReleaseAsync(release, tag, cancellationToken).ConfigureAwait(false);

            foreach (var asset in release.Assets ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (asset != null && string.Equals(asset.Name, "Voidstrap.exe", StringComparison.OrdinalIgnoreCase) && IsInstallableAsset(asset))
                    return await UpdateExe(asset.BrowserDownloadUrl, asset.Name, asset.Digest ?? "", tag, cancellationToken);
            }

            App.Logger.WriteLine("GitHubUpdater", "No valid Voidstrap executable asset found.");
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

    private static bool IsInstallableAsset(GithubReleaseAsset asset)
    {
        string digest = asset.Digest ?? "";
        return string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)
            && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            && digest.Length == 71
            && Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetLinuxAssetName(string tag, LinuxInstallationKind kind)
    {
        try
        {
            return kind switch
            {
                LinuxInstallationKind.AppImage => LinuxBundleInstaller.GetAppImageAssetName(tag),
                LinuxInstallationKind.PortableBundle => LinuxBundleInstaller.GetAssetName(tag, LinuxBundleInstaller.GetCurrentRuntimeIdentifier()),
                LinuxInstallationKind.Flatpak => LinuxBundleInstaller.GetFlatpakAssetName(tag),
                LinuxInstallationKind.Debian => LinuxBundleInstaller.GetDebianAssetName(tag),
                LinuxInstallationKind.Rpm => LinuxBundleInstaller.GetRpmAssetName(tag),
                LinuxInstallationKind.Arch => LinuxInstallationUpdates.ArchRecipeAssetName,
                _ => null
            };
        }
        catch (Exception ex) when (ex is ArgumentException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static GithubReleaseAsset? FindLinuxAsset(Voidstrap.Models.APIs.GitHub.GithubRelease release, LinuxInstallationKind kind)
    {
        string? expected = GetLinuxAssetName(release.TagName ?? "", kind);
        if (expected is null)
            return null;
        return release.Assets?.FirstOrDefault(asset => asset != null && string.Equals(asset.Name, expected, StringComparison.Ordinal) && IsInstallableAsset(asset));
    }

    private static Task<LinuxInstallationInfo> DetectLinuxInstallationAsync(CancellationToken cancellationToken)
    {
        return LinuxInstallationUpdates.DetectAsync(new Voidstrap.Core.SystemProcessService(), Environment.ProcessPath, cancellationToken);
    }

    private static async Task<string?> GetLatestLinuxVersionTagAsync(CancellationToken cancellationToken)
    {
        LinuxInstallationInfo installation = await DetectLinuxInstallationAsync(cancellationToken).ConfigureAwait(false);
        var releases = await Voidstrap.Utility.GitHubCache.GetJsonWithFallbackAsync<List<Voidstrap.Models.APIs.GitHub.GithubRelease>>(
            App.ProjectReleaseListApi,
            App.ProjectFallbackReleaseListApi,
            TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (releases == null)
            return null;

        Version? newest = null;
        string? newestTag = null;
        foreach (var release in releases)
        {
            if (release == null || release.Draft || (release.Prerelease && !App.AllowPreReleaseUpdates) || string.IsNullOrEmpty(release.TagName))
                continue;
            if (!Version.TryParse(release.TagName.TrimStart('v', 'V'), out Version? version) || (newest != null && version <= newest))
                continue;
            if (FindLinuxAsset(release, installation.Kind) == null)
                continue;
            newest = version;
            newestTag = release.TagName;
        }

        if (newestTag != null)
            return newestTag;
        App.Logger.WriteLine("GitHubUpdater", "No release has a " + installation.Kind + " build for this computer yet");
        return "v" + (typeof(GithubUpdater).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
    }

    private static async Task<bool> InstallLinuxReleaseAsync(Voidstrap.Models.APIs.GitHub.GithubRelease release, string tag, CancellationToken cancellationToken)
    {
        LinuxInstallationInfo installation = await DetectLinuxInstallationAsync(cancellationToken).ConfigureAwait(false);
        GithubReleaseAsset? asset = FindLinuxAsset(release, installation.Kind);
        if (asset == null)
        {
            App.Logger.WriteLine("GitHubUpdater", "The release " + tag + " has no " + installation.Kind + " build for this computer");
            return false;
        }
        if (installation.Kind is LinuxInstallationKind.AppImage or LinuxInstallationKind.PortableBundle
            && !LinuxBundleInstaller.CanUpdateInPlace(installation.ExecutablePath))
        {
            App.Logger.WriteLine("GitHubUpdater", "The folder holding Voidstrap is not writable, so the update was skipped: " + installation.ExecutablePath);
            return false;
        }

        string digest = asset.Digest ?? "";
        return installation.Kind switch
        {
            LinuxInstallationKind.AppImage => await UpdateLinuxAppImage(asset.BrowserDownloadUrl, asset.Name, digest, installation.ExecutablePath, cancellationToken).ConfigureAwait(false),
            LinuxInstallationKind.PortableBundle => await UpdateLinuxBundle(asset.BrowserDownloadUrl, asset.Name, digest, installation.ExecutablePath, cancellationToken).ConfigureAwait(false),
            LinuxInstallationKind.Arch => await UpdateLinuxArch(asset.BrowserDownloadUrl, asset.Name, digest, installation, tag, cancellationToken).ConfigureAwait(false),
            _ => await UpdateLinuxPackage(asset.BrowserDownloadUrl, asset.Name, digest, installation, tag, cancellationToken).ConfigureAwait(false)
        };
    }

    private static async Task<bool> FinishLinuxPackageUpdateAsync(
        Voidstrap.Platform.OperationResult result,
        LinuxInstallationInfo installation,
        string tag,
        CancellationToken cancellationToken)
    {
        if (!result.Succeeded)
        {
            if (result.Failure?.Code == LinuxInstallationUpdates.AuthorizationCancelledCode)
            {
                App.State.Prop.DeclinedLinuxUpdateTag = tag;
                App.State.Save();
            }
            App.Logger.WriteLine("GitHubUpdater", "The " + installation.Kind + " update could not be installed: " + (result.Failure?.Message ?? "Unknown package error"));
            return false;
        }
        if (!await LinuxInstallationUpdates.HasExpectedVersionAsync(new Voidstrap.Core.SystemProcessService(), installation, tag, cancellationToken).ConfigureAwait(false))
        {
            App.Logger.WriteLine("GitHubUpdater", "The package manager finished but " + installation.PackageName + " is still older than " + tag);
            return false;
        }
        App.Logger.WriteLine("GitHubUpdater", "The " + installation.Kind + " package update to " + tag + " completed");
        return true;
    }

    private static async Task<bool> UpdateLinuxArch(
        string url,
        string name,
        string digest,
        LinuxInstallationInfo installation,
        string tag,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Voidstrap.Utility.InterProcessLock updateLock = new("AutoUpdater", TimeSpan.FromSeconds(5));
        if (!updateLock.IsAcquired)
            throw new IOException("Another update is already in progress");

        string temporaryDirectory = Path.Combine(Path.GetTempPath(), "Voidstrap_Update_" + Guid.NewGuid().ToString("N"));
        string recipeDirectory = Path.Combine(temporaryDirectory, "recipe");
        Directory.CreateDirectory(recipeDirectory);
        try
        {
            string archivePath = Path.Combine(temporaryDirectory, name);
            await DownloadToFileAsync(url, archivePath, digest, cancellationToken).ConfigureAwait(false);
            await ExtractArchRecipeAsync(archivePath, recipeDirectory, cancellationToken).ConfigureAwait(false);
            Voidstrap.Platform.OperationResult result = await LinuxInstallationUpdates.InstallArchRecipeAsync(
                new Voidstrap.Core.SystemProcessService(),
                recipeDirectory,
                cancellationToken).ConfigureAwait(false);
            return await FinishLinuxPackageUpdateAsync(result, installation, tag, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, true);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("GitHubUpdater", "Temporary update cleanup failed: " + ex.Message);
            }
        }
    }

    private static async Task ExtractArchRecipeAsync(string archivePath, string recipeDirectory, CancellationToken cancellationToken)
    {
        bool hasRecipe = false;
        await using FileStream archive = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using System.IO.Compression.GZipStream gzip = new(archive, System.IO.Compression.CompressionMode.Decompress);
        using System.Formats.Tar.TarReader reader = new(gzip);
        while (await reader.GetNextEntryAsync(false, cancellationToken).ConfigureAwait(false) is System.Formats.Tar.TarEntry entry)
        {
            string entryName = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;
            if (entryName is not ("PKGBUILD" or ".SRCINFO")
                || entry.EntryType is not (System.Formats.Tar.TarEntryType.RegularFile or System.Formats.Tar.TarEntryType.V7RegularFile)
                || entry.Length > 1048576)
                continue;
            await entry.ExtractToFileAsync(Path.Combine(recipeDirectory, entryName), true, cancellationToken).ConfigureAwait(false);
            hasRecipe |= entryName == "PKGBUILD";
        }
        if (!hasRecipe)
            throw new InvalidDataException("The Arch package recipe is missing from the release");
    }

    private static async Task<bool> UpdateLinuxPackage(
        string url,
        string name,
        string digest,
        LinuxInstallationInfo installation,
        string tag,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Voidstrap.Utility.InterProcessLock updateLock = new("AutoUpdater", TimeSpan.FromSeconds(5));
        if (!updateLock.IsAcquired)
            throw new IOException("Another update is already in progress");

        string temporaryParent = installation.Kind == LinuxInstallationKind.Flatpak
            ? ResolveFlatpakUpdateDirectory()
            : Path.GetTempPath();
        string temporaryDirectory = Path.Combine(temporaryParent, "Voidstrap_Update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            string packagePath = Path.Combine(temporaryDirectory, name);
            await DownloadToFileAsync(url, packagePath, digest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Voidstrap.Platform.OperationResult result = await LinuxInstallationUpdates.InstallPackageAsync(
                new Voidstrap.Core.SystemProcessService(),
                installation,
                packagePath,
                cancellationToken).ConfigureAwait(false);
            return await FinishLinuxPackageUpdateAsync(result, installation, tag, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temporaryDirectory))
                    Directory.Delete(temporaryDirectory, true);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("GitHubUpdater", "Temporary update cleanup failed: " + ex.Message);
            }
        }
    }

    private static string ResolveFlatpakUpdateDirectory()
    {
        string directory = Paths.Cache;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory))
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            directory = Path.Combine(profile, ".var", "app", LinuxInstallationUpdates.ApplicationId, "cache", "Voidstrap", "Updates");
        }
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<bool> UpdateLinuxBundle(string url, string name, string digest, string currentExecutable, CancellationToken cancellationToken)
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

    private static async Task<bool> UpdateLinuxAppImage(string url, string name, string digest, string currentAppImagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Voidstrap.Utility.InterProcessLock updateLock = new("AutoUpdater", TimeSpan.FromSeconds(5));
        if (!updateLock.IsAcquired)
            throw new IOException("Another update is already in progress");

        string tempDirectory = Path.Combine(Path.GetTempPath(), "Voidstrap_Update_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        try
        {
            string imagePath = Path.Combine(tempDirectory, name);
            await DownloadToFileAsync(url, imagePath, digest, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await LinuxBundleInstaller.InstallAppImageAsync(imagePath, currentAppImagePath, cancellationToken);
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
        if (File.Exists(backupExe))
        {
            try
            {
                File.Delete(backupExe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                backupExe = currentExe + "." + Guid.NewGuid().ToString("N") + ".old";
                App.Logger.WriteLine("GitHubUpdater", "The previous backup is still in use, keeping this one as " + Path.GetFileName(backupExe));
            }
        }
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
        await Voidstrap.Utility.ResilientDownload.DownloadAsync(http, [url], path, MaxUpdateBytes, digest, token: token).ConfigureAwait(false);
    }
}
