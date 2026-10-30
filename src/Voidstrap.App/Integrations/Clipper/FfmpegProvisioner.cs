using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Voidstrap.Platform;

namespace Voidstrap.Integrations.Clipper;

public static class FfmpegProvisioner
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _cachedPath;

    private static string ExecutableName => Voidstrap.Utility.Platform.IsWindows ? "ffmpeg.exe" : "ffmpeg";

    public static string ManagedToolDirectory => Path.Combine(Paths.UserData, "Tools", "ffmpeg");

    public static string ManagedExecutablePath => Path.Combine(ManagedToolDirectory, ExecutableName);

    public static string? TryFindExisting()
    {
        if (!string.IsNullOrEmpty(_cachedPath) && File.Exists(_cachedPath))
            return _cachedPath;

        if (File.Exists(ManagedExecutablePath))
        {
            _cachedPath = ManagedExecutablePath;
            return _cachedPath;
        }

        try
        {
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir.Trim(), ExecutableName);
                    if (File.Exists(candidate))
                    {
                        _cachedPath = candidate;
                        return _cachedPath;
                    }
                }
            }
        }
        catch { }

        if (Voidstrap.Utility.Platform.IsWindows)
        {
            try
            {
                string winget = Path.Combine(Paths.LocalAppData, "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(winget))
                {
                    foreach (string pkg in Directory.EnumerateDirectories(winget, "*FFmpeg*"))
                    {
                        string? exe = Directory.EnumerateFiles(pkg, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                        {
                            _cachedPath = exe;
                            return _cachedPath;
                        }
                    }
                }
            }
            catch { }
        }
        else
        {
            string[] candidates = ["/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/opt/homebrew/bin/ffmpeg", "/snap/bin/ffmpeg"];
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    _cachedPath = candidate;
                    return _cachedPath;
                }
            }
        }

        return null;
    }

    public static async Task<string?> EnsureAvailableAsync(
        Action<string>? statusCallback = null,
        CancellationToken cancellationToken = default
    )
    {
        string? existing = TryFindExisting();
        if (!string.IsNullOrEmpty(existing))
            return existing;

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = TryFindExisting();
            if (!string.IsNullOrEmpty(existing))
                return existing;

            Directory.CreateDirectory(ManagedToolDirectory);
            string tempArchive = Path.Combine(ManagedToolDirectory, "ffmpeg-download.tmp");

            string url = ResolveDownloadUrl(out bool isTarXz);
            if (string.IsNullOrEmpty(url))
                return null;

            statusCallback?.Invoke("Downloading FFmpeg encoder...");
            App.Logger.WriteLine("FfmpegProvisioner", $"Downloading FFmpeg from {url}");

            using (
                HttpResponseMessage response = await App
                    .HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false)
            )
            {
                response.EnsureSuccessStatusCode();
                await using FileStream fs = new(tempArchive, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            statusCallback?.Invoke("Extracting FFmpeg encoder...");
            if (isTarXz)
            {
                ExtractFromTarXz(tempArchive, ManagedExecutablePath);
            }
            else
            {
                ExtractFromZip(tempArchive, ManagedExecutablePath);
            }

            try
            {
                if (File.Exists(tempArchive))
                    File.Delete(tempArchive);
            }
            catch { }

            if (!Voidstrap.Utility.Platform.IsWindows && File.Exists(ManagedExecutablePath))
            {
                try
                {
                    File.SetUnixFileMode(
                        ManagedExecutablePath,
                        UnixFileMode.UserRead
                            | UnixFileMode.UserWrite
                            | UnixFileMode.UserExecute
                            | UnixFileMode.GroupRead
                            | UnixFileMode.GroupExecute
                            | UnixFileMode.OtherRead
                            | UnixFileMode.OtherExecute
                    );
                }
                catch { }
            }

            if (File.Exists(ManagedExecutablePath))
            {
                _cachedPath = ManagedExecutablePath;
                statusCallback?.Invoke("FFmpeg ready");
                return _cachedPath;
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("FfmpegProvisioner::EnsureAvailableAsync", ex);
        }
        finally
        {
            Gate.Release();
        }

        return null;
    }

    private static string ResolveDownloadUrl(out bool isTarXz)
    {
        isTarXz = false;
        if (Voidstrap.Utility.Platform.IsWindows)
        {
            return "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        }
        if (Voidstrap.Utility.Platform.IsLinux)
        {
            isTarXz = true;
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl.tar.xz"
                : "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz";
        }
        if (Voidstrap.Utility.Platform.IsMacOS)
        {
            return "https://evermeet.cx/ffmpeg/getrelease/zip";
        }
        return string.Empty;
    }

    private static void ExtractFromZip(string archivePath, string destinationPath)
    {
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, ExecutableName, StringComparison.OrdinalIgnoreCase)
        );
        if (entry == null)
            throw new FileNotFoundException("ffmpeg executable not found inside archive");
        string tempOut = destinationPath + ".part";
        entry.ExtractToFile(tempOut, overwrite: true);
        File.Move(tempOut, destinationPath, overwrite: true);
    }

    private static void ExtractFromTarXz(string archivePath, string destinationPath)
    {
        string extractDir = Path.Combine(ManagedToolDirectory, "extract");
        try
        {
            if (Directory.Exists(extractDir))
                Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);
            using Process? tar = Process.Start(
                new ProcessStartInfo
                {
                    FileName = "tar",
                    ArgumentList = { "-xf", archivePath, "-C", extractDir },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            );
            tar?.WaitForExit(60000);
            string? found = Directory.EnumerateFiles(extractDir, "ffmpeg", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(found))
                File.Move(found, destinationPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);
            }
            catch { }
        }
    }
}
