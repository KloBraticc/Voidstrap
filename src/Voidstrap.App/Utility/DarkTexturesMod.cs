using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap
{
    public class DarkTexturesInstaller
    {
        private static readonly string DownloadUrl = "https://cocajola.com/wp-content/uploads/2024/09/dark-textures-rivals.zip";
        private const long DownloadBytes = 14794081L;
        private const string DownloadSha256 = "7F95B757A3BB664950A433FE656D07FC0599F05DD0F253ACFB53B569487F5FEA";
        private static readonly SemaphoreSlim InstallGate = new SemaphoreSlim(1, 1);
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        public static async Task DownloadAndExtractAsync()
        {
            await InstallGate.WaitAsync().ConfigureAwait(false);
            string workRoot = Path.Combine(Paths.Temp, "DarkTextures", Guid.NewGuid().ToString("N"));
            List<(string Destination, string? Backup)> applied = new();
            try
            {
                string modsPath = Path.Combine(Paths.Mods, "PlatformContent", "pc", "textures");
                string tempZip = Path.Combine(workRoot, "source.zip");
                string tempExtractPath = Path.Combine(workRoot, "extracted");
                string backupRoot = Path.Combine(workRoot, "backup");
                Directory.CreateDirectory(workRoot);
                EnsureSafeDirectory(modsPath, modsPath);
                await Utility.ResilientDownload.DownloadAsync(App.HttpClient, [DownloadUrl], tempZip, DownloadBytes, expectedSha256: DownloadSha256).ConfigureAwait(false);
                Utility.SafeZipExtractor.ExtractToDirectory(tempZip, tempExtractPath, maxExpandedBytes: 67108864L, maxEntries: 1000);
                string[] topFolders = Directory.GetDirectories(tempExtractPath);
                if (topFolders.Length != 1 || Directory.EnumerateFiles(tempExtractPath).Any())
                    throw new InvalidDataException("The dark textures archive has an invalid layout");
                string topFolder = topFolders[0];
                string fullModsRoot = Path.GetFullPath(modsPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                foreach (string source in Directory.EnumerateFiles(topFolder, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(topFolder, source);
                    string destination = Path.GetFullPath(Path.Combine(modsPath, relative));
                    if (!destination.StartsWith(fullModsRoot, PathComparison))
                        throw new InvalidDataException("The dark textures archive contains an invalid path");
                    string? parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent))
                        EnsureSafeDirectory(modsPath, parent);
                    string? backup = null;
                    if (File.Exists(destination))
                    {
                        EnsureNotReparsePoint(destination);
                        backup = Path.Combine(backupRoot, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                        File.Copy(destination, backup, true);
                    }
                    applied.Add((destination, backup));
                    string staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.Copy(source, staged, true);
                        File.Move(staged, destination, true);
                    }
                    finally
                    {
                        if (File.Exists(staged))
                            File.Delete(staged);
                    }
                }
            }
            catch
            {
                for (int i = applied.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        var item = applied[i];
                        if (item.Backup == null)
                            File.Delete(item.Destination);
                        else
                            File.Move(item.Backup, item.Destination, true);
                    }
                    catch
                    {
                    }
                }
                throw;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workRoot))
                        Directory.Delete(workRoot, true);
                }
                catch
                {
                }
                InstallGate.Release();
            }
        }

        private static void EnsureSafeDirectory(string root, string directory)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(fullRoot, fullDirectory, PathComparison) && !fullDirectory.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison))
                throw new InvalidDataException("The dark textures destination is invalid");
            Directory.CreateDirectory(fullRoot);
            EnsureNotReparsePoint(fullRoot);
            string relative = Path.GetRelativePath(fullRoot, fullDirectory);
            if (relative == ".")
                return;
            string current = fullRoot;
            foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                Directory.CreateDirectory(current);
                EnsureNotReparsePoint(current);
            }
        }

        private static void EnsureNotReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The dark textures destination contains a redirected path");
        }
    }
}
