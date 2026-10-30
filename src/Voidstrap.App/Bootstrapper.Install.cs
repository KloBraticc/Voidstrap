using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Shell;
using System.Windows.Threading;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;
using Voidstrap.AppData;
using Voidstrap.Core;
using Voidstrap.Enums;
using Voidstrap.Exceptions;
using Voidstrap.Extensions;
using Voidstrap.Integrations;
using Voidstrap.Integrations.AssetProxy;
using Voidstrap.Models;
using Voidstrap.Models.APIs.Roblox;
using Voidstrap.Models.Manifest;
using Voidstrap.Models.Persistable;
using Voidstrap.Platform;
using Voidstrap.Platform.Linux;
using Voidstrap.Platform.MacOS;
using Voidstrap.Resources;
using Voidstrap.RobloxInterfaces;
using Voidstrap.UI;
using Voidstrap.UI.Elements.Bootstrapper.Base;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.Elements.Settings.Pages;
using Voidstrap.Utility;

namespace Voidstrap;

public partial class Bootstrapper
{
    private async Task UpgradeRoblox()
    {
        CancellationToken ct = _cancelTokenSource.Token;
        string stagingDirectory = _latestVersionDirectory + ".installing";
        if (Interlocked.Exchange(ref _isInstalling, 1) == 1)
        {
            App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "Upgrade already in progress, skipping.");
            return;
        }
        try
        {
            if (!App.Settings.Prop.UpdateRoblox)
            {
                SetStatus(Strings.Bootstrapper_Status_CancelUpgrade);
                await Task.Delay(250, ct).ConfigureAwait(continueOnCapturedContext: false);
                if (!Directory.Exists(_latestVersionDirectory))
                {
                    Frontend.ShowMessageBox(Strings.Bootstrapper_Dialog_NoUpgradeWithoutClient, MessageBoxImage.Exclamation);
                }
                return;
            }
            SetStatus(string.IsNullOrEmpty(AppData.State.VersionGuid) ? "Installing Packages" : "Upgrading Packages");
            ApplyOptimizedDownloadDefaults();
            Directory.CreateDirectory(Paths.Base);
            Directory.CreateDirectory(Paths.Downloads);
            Directory.CreateDirectory(AppData.VersionsRoot);
            try
            {
                foreach (string staleFile in Directory.GetFiles(Paths.Downloads))
                {
                    if (
                        (
                            staleFile.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                            || staleFile.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                            || staleFile.EndsWith(".meta.bak", StringComparison.OrdinalIgnoreCase)
                        )
                        && (DateTime.UtcNow - File.GetLastWriteTimeUtc(staleFile)).TotalHours >= 24.0
                    )
                    {
                        File.Delete(staleFile);
                        App.Logger.WriteLine(
                            "Bootstrapper::UpgradeRoblox",
                            "Deleted stale partial download older than 24 hours: " + Path.GetFileName(staleFile)
                        );
                    }
                }
            }
            catch { }
            if (!IsStudioLaunch)
            {
                await StopRobloxPlayersAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
            }
            DeleteAbandonedStagingDirectories(AppData.VersionsRoot, stagingDirectory);
            foreach (string blockingPath in new[] { stagingDirectory, _latestVersionDirectory })
            {
                if (File.Exists(blockingPath))
                {
                    File.SetAttributes(blockingPath, FileAttributes.Normal);
                    File.Delete(blockingPath);
                    App.Logger.WriteLine(
                        "Bootstrapper::UpgradeRoblox",
                        "Removed a file that was blocking the installation folder: " + Path.GetFileName(blockingPath)
                    );
                }
            }
            if (Directory.Exists(stagingDirectory))
            {
                await SafeDeleteDirectoryAsync(stagingDirectory, "Bootstrapper::UpgradeRoblox", ct)
                    .ConfigureAwait(continueOnCapturedContext: false);
            }
            Directory.CreateDirectory(stagingDirectory);
            _packageExtractionDirectory = stagingDirectory;
            if (_versionPackageManifest == null || !_versionPackageManifest.Any())
            {
                throw new Exception("Package manifest is null or empty.");
            }
            long totalPacked = ((IEnumerable<Package>)_versionPackageManifest).Sum((Func<Package, long>)(p => p.PackedSize));
            long num = ((IEnumerable<Package>)_versionPackageManifest).Sum((Func<Package, long>)(p => p.Size));
            long packedHeadroom = checked((long)Math.Ceiling(totalPacked * 1.1));
            long expandedHeadroom = checked((long)Math.Ceiling(num * 1.1));
            string downloadsRoot = Path.GetPathRoot(Path.GetFullPath(Paths.Downloads)) ?? Paths.Downloads;
            string versionsVolume = Path.GetPathRoot(Path.GetFullPath(AppData.VersionsRoot)) ?? AppData.VersionsRoot;
            bool sameVolume = string.Equals(downloadsRoot, versionsVolume, StringComparison.OrdinalIgnoreCase);
            bool insufficientSpace = sameVolume
                ? Filesystem.GetFreeDiskSpace(Paths.Downloads) < packedHeadroom + expandedHeadroom
                : Filesystem.GetFreeDiskSpace(Paths.Downloads) < packedHeadroom
                    || Filesystem.GetFreeDiskSpace(AppData.VersionsRoot) < expandedHeadroom;
            if (insufficientSpace)
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_NotEnoughSpace, MessageBoxImage.Hand);
                App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                return;
            }
            if (Dialog != null)
            {
                SetProgressIndeterminate(false);
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Normal;
                SetProgressMaximum(10000);
                _progressIncrement = 10000.0 / (double)Math.Max(1L, totalPacked);
                _taskbarProgressMaximum = ((Dialog is WinFormsDialogBase) ? 100.0 : 1.0);
                _taskbarProgressIncrement = _taskbarProgressMaximum / (double)Math.Max(1L, totalPacked);
            }
            int totalPackages = _versionPackageManifest.Count;
            int packagesComplete = 0;
            int failedPackages = 0;
            Exception? firstPackageFailure = null;
            Interlocked.Exchange(ref _totalDownloadedBytes, 0L);
            using SemaphoreSlim downloadThrottler = new(
                DownloadConfiguration.NormalizeConcurrent(App.Settings.Prop.MaxConcurrentDownloads)
            );
            using SemaphoreSlim extractionThrottler = new(ExtractionConcurrency);
            using CancellationTokenSource progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task progressTask = RunDownloadProgressLoopAsync(
                totalPacked,
                totalPackages,
                () => Volatile.Read(in packagesComplete),
                progressCts.Token
            );
            try
            {
                await Task.WhenAll(
                        ((IEnumerable<Package>)_versionPackageManifest)
                            .Select(
                                (Func<Package, Task>)
                                    async delegate(Package package)
                                    {
                                        try
                                        {
                                            await downloadThrottler.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
                                            try
                                            {
                                                await DownloadPackage(package).ConfigureAwait(continueOnCapturedContext: false);
                                            }
                                            finally
                                            {
                                                downloadThrottler.Release();
                                            }
                                            await extractionThrottler.WaitAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
                                            try
                                            {
                                                await WithRetryAsync(
                                                        delegate
                                                        {
                                                            ExtractPackage(package);
                                                            return Task.CompletedTask;
                                                        },
                                                        "Bootstrapper::UpgradeRoblox::Extract(" + package.Name + ")",
                                                        4,
                                                        800,
                                                        ex7 => (ex7 is IOException || ex7 is UnauthorizedAccessException),
                                                        ct
                                                    )
                                                    .ConfigureAwait(continueOnCapturedContext: false);
                                            }
                                            finally
                                            {
                                                extractionThrottler.Release();
                                            }
                                            Interlocked.Increment(ref packagesComplete);
                                            UpdateProgressBar();
                                        }
                                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                                        {
                                            throw;
                                        }
                                        catch (Exception value)
                                        {
                                            Interlocked.Increment(ref failedPackages);
                                            Interlocked.CompareExchange(ref firstPackageFailure, value, null);
                                            App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", $"Package {package.Name} failed: {value}");
                                        }
                                    }
                            )
                            .ToList()
                    )
                    .ConfigureAwait(continueOnCapturedContext: false);
            }
            finally
            {
                progressCts.Cancel();
                try
                {
                    await progressTask.ConfigureAwait(continueOnCapturedContext: false);
                }
                catch { }
            }
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (failedPackages > 0)
            {
                throw new Exception($"{failedPackages} package(s) failed during upgrade.{DescribePackageFailure(firstPackageFailure)}");
            }
            if (Dialog != null)
            {
                SetProgressIndeterminate(true);
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Indeterminate;
                SetStatus(Strings.Bootstrapper_Status_Configuring);
            }
            await WithRetryAsync(
                    () => File.WriteAllTextAsync(Path.Combine(stagingDirectory, "AppSettings.xml"), AppSettingsXml, ct),
                    "Bootstrapper::UpgradeRoblox::Write(AppSettings.xml)",
                    3,
                    600,
                    ex6 => (ex6 is IOException || ex6 is UnauthorizedAccessException),
                    ct
                )
                .ConfigureAwait(continueOnCapturedContext: false);
            List<InstallManifest.Entry> installedFiles = VerifyExtractedPackages(stagingDirectory);
            ValidateStagedInstallation(stagingDirectory);
            File.WriteAllText(Path.Combine(stagingDirectory, VersionOwnershipFileName), AppData.BinaryType, new UTF8Encoding(false));
            InstallManifest.Write(stagingDirectory, installedFiles);
            if (
                !PathsEqual(AppData.VersionsRoot, Paths.Versions)
                && Directory.Exists(_latestVersionDirectory)
                && !string.Equals(AppData.State.VersionGuid, _latestVersionGuid, StringComparison.Ordinal)
                && !IsOwnedVersionDirectory(_latestVersionDirectory)
            )
            {
                throw new InvalidOperationException("The custom install location contains an unmanaged version directory");
            }
            await CommitInstallationAsync(stagingDirectory, _latestVersionDirectory, AppData.VersionsRoot, ct)
                .ConfigureAwait(continueOnCapturedContext: false);
            _packageExtractionDirectory = null;
            VerifyCommittedInstallation();
            _missingInstalledFiles = -1;
            try
            {
                MigrateCompatibilityFlags();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "MigrateCompatibilityFlags: " + ex.Message);
            }
            AppData.State.VersionGuid = _latestVersionGuid;
            AppData.State.CriticalFiles = [.. _stagedCriticalFiles];
            AppData.State.PackageHashes.Clear();
            foreach (Package item in _versionPackageManifest)
            {
                AppData.State.PackageHashes[item.Name] = item.Signature;
            }
            AppData.State.ModManifest.Clear();
            AppData.State.ModApplyCache.Clear();
            AppData.State.ManagedModManifest.Clear();
            AppData.State.ModApplyVersion = string.Empty;
            ClearForcedReinstall();
            try
            {
                CleanupVersionsFolder();
            }
            catch (Exception ex2)
            {
                App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "CleanupVersionsFolder: " + ex2.Message);
            }
            IEnumerable<string>? enumerable = App.State.Prop.Player?.PackageHashes.Values;
            IEnumerable<string> first = enumerable ?? [];
            enumerable = App.State.Prop.Studio?.PackageHashes.Values;
            HashSet<string> allHashes = [.. first, .. enumerable ?? [], .. App.State.Prop.VngPlayer.PackageHashes.Values];
            Voidstrap.Utility.PackageCache.Trim(allHashes);
            try
            {
                long installedBytes = _versionPackageManifest.Sum(package => (long)package.Size + package.PackedSize);
                long installedKilobytes = installedBytes / 1024L;
                AppData.State.Size = (int)Math.Clamp(installedKilobytes, 0L, int.MaxValue);
                long combinedKilobytes =
                    (long)(App.State.Prop.Player?.Size ?? 0) + (App.State.Prop.Studio?.Size ?? 0) + App.State.Prop.VngPlayer.Size;
                if (Voidstrap.Utility.Platform.IsWindows)
                {
                    using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(
                        "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\Voidstrap"
                    );
                    registryKey?.SetValueSafe("EstimatedSize", (int)Math.Clamp(combinedKilobytes, 0L, int.MaxValue));
                }
            }
            catch (Exception ex3)
            {
                App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "Register size failed: " + ex3.Message);
            }
            App.State.Save();
        }
        catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
        {
            App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "Upgrade cancelled.");
        }
        catch (Exception ex5)
        {
            App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", $"Upgrade error: {ex5}");
            string message = IsInstallationAccessError(ex5)
                ? "Roblox could not be installed because Windows is still using part of the installation. No incomplete replacement was activated. Close Roblox and programs that scan or modify Roblox files, then try again."
                : "Roblox could not be installed:\n" + ex5.Message;
            Frontend.ShowMessageBox(message, MessageBoxImage.Hand);
            _cancelTokenSource.Cancel();
        }
        finally
        {
            _packageExtractionDirectory = null;
            if (ct.IsCancellationRequested && Directory.Exists(stagingDirectory))
            {
                try
                {
                    await SafeDeleteDirectoryAsync(stagingDirectory, "Bootstrapper::UpgradeRoblox", CancellationToken.None)
                        .ConfigureAwait(continueOnCapturedContext: false);
                    App.Logger.WriteLine("Bootstrapper::UpgradeRoblox", "Removed the unfinished installation after the upgrade stopped");
                }
                catch (Exception cleanupException)
                {
                    App.Logger.WriteLine(
                        "Bootstrapper::UpgradeRoblox",
                        "The unfinished installation could not be removed: " + cleanupException.Message
                    );
                }
            }
            Interlocked.Exchange(ref _isInstalling, 0);
            _installationStopped.TrySetResult();
        }
    }

    private static void DeleteAbandonedStagingDirectories(string versionsRoot, string currentStagingDirectory)
    {
        try
        {
            foreach (string directory in Directory.GetDirectories(versionsRoot, "version-*.installing"))
            {
                if (
                    string.Equals(directory, currentStagingDirectory, StringComparison.OrdinalIgnoreCase)
                    || DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) < AbandonedStagingAge
                )
                    continue;
                try
                {
                    Directory.Delete(directory, recursive: true);
                    App.Logger.WriteLine(
                        "Bootstrapper::UpgradeRoblox",
                        "Deleted an abandoned unfinished installation: " + Path.GetFileName(directory)
                    );
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    App.Logger.WriteLine(
                        "Bootstrapper::UpgradeRoblox",
                        "Could not delete the abandoned installation " + Path.GetFileName(directory) + ": " + ex.Message
                    );
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static readonly TimeSpan AbandonedStagingAge = TimeSpan.FromHours(6);

    private static void ApplyOptimizedDownloadDefaults()
    {
        if (DownloadConfiguration.Normalize(App.Settings.Prop))
            App.Settings.SaveDeferred();
    }

    private static async Task CommitInstallationAsync(
        string stagingDirectory,
        string targetDirectory,
        string installationRoot,
        CancellationToken ct
    )
    {
        string stagingPath = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string targetPath = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        string versionsRoot = Path.GetFullPath(installationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (
            !targetPath.StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(stagingPath), Path.GetDirectoryName(targetPath), StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new InvalidOperationException("The Roblox installation paths are not valid");
        }
        if (!Directory.Exists(stagingPath))
        {
            throw new DirectoryNotFoundException("The prepared Roblox installation could not be found");
        }
        string backupDirectory = targetPath + ".previous." + Guid.NewGuid().ToString("N");
        bool movedExisting = false;
        try
        {
            if (Directory.Exists(targetPath))
            {
                await MoveDirectoryWithRetryAsync(targetPath, backupDirectory, "Bootstrapper::CommitInstallation::Backup", ct)
                    .ConfigureAwait(continueOnCapturedContext: false);
                movedExisting = true;
            }
            await MoveDirectoryWithRetryAsync(stagingPath, targetPath, "Bootstrapper::CommitInstallation::Activate", ct)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception commitException)
        {
            if (movedExisting && !Directory.Exists(targetPath) && Directory.Exists(backupDirectory))
            {
                try
                {
                    await MoveDirectoryWithRetryAsync(
                            backupDirectory,
                            targetPath,
                            "Bootstrapper::CommitInstallation::Restore",
                            CancellationToken.None
                        )
                        .ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (Exception rollbackException)
                {
                    throw new AggregateException(
                        "The Roblox installation could not be activated or restored",
                        commitException,
                        rollbackException
                    );
                }
            }
            throw;
        }
        if (Directory.Exists(backupDirectory))
        {
            try
            {
                await SafeDeleteDirectoryAsync(backupDirectory, "Bootstrapper::CommitInstallation::Cleanup", CancellationToken.None)
                    .ConfigureAwait(continueOnCapturedContext: false);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                App.Logger.WriteLine("Bootstrapper::CommitInstallation", "The previous installation will be cleaned later: " + ex.Message);
            }
        }
    }

    private static async Task MoveDirectoryWithRetryAsync(string source, string destination, string context, CancellationToken ct)
    {
        await WithRetryAsync(
                delegate
                {
                    NormalizeInstallationAttributes(source);
                    return Task.CompletedTask;
                },
                context + "::Prepare",
                3,
                500,
                IsInstallationAccessError,
                ct
            )
            .ConfigureAwait(continueOnCapturedContext: false);
        await WithRetryAsync(
                delegate
                {
                    if (!Directory.Exists(source) && Directory.Exists(destination))
                    {
                        return Task.CompletedTask;
                    }
                    Directory.Move(source, destination);
                    return Task.CompletedTask;
                },
                context,
                8,
                500,
                IsInstallationAccessError,
                ct
            )
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    private static void NormalizeInstallationAttributes(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    pending.Push(entry);
                }
                else if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
            }
            FileAttributes directoryAttributes = File.GetAttributes(directory);
            if ((directoryAttributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(directory, directoryAttributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    private static bool IsInstallationAccessError(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return true;
        }
        if (ex is IOException ioException)
        {
            int errorCode = ioException.HResult & 0xFFFF;
            return errorCode is 5 or 32 or 33 or 145 or 183;
        }
        if (ex is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(IsInstallationAccessError);
        }
        return ex.InnerException != null && IsInstallationAccessError(ex.InnerException);
    }

    private void ValidateStagedInstallation(string stagingDirectory)
    {
        string executable = Path.Combine(stagingDirectory, IsStudioLaunch ? AppData.ExecutableName : ProcRobloxExe);
        FileInfo executableInfo = new(executable);
        if (!executableInfo.Exists || executableInfo.Length < 1048576)
        {
            throw new InvalidDataException("The staged Roblox executable is missing or incomplete");
        }
        if (!File.Exists(Path.Combine(stagingDirectory, "AppSettings.xml")))
        {
            throw new InvalidDataException("The staged Roblox settings file is missing");
        }
        _stagedCriticalFiles = ScanCriticalFiles(stagingDirectory);
        foreach (string name in _stagedCriticalFiles)
        {
            FileInfo info = new(Path.Combine(stagingDirectory, name));
            if (!info.Exists || info.Length == 0)
            {
                throw new InvalidDataException("The staged Roblox client file " + name + " is missing or empty");
            }
        }
    }

    private List<string> _stagedCriticalFiles = [];

    private static string InstalledFilesListName => InstallManifest.FileName;

    private List<InstallManifest.Entry> VerifyExtractedPackages(string stagingDirectory)
    {
        List<InstallManifest.Entry> installedFiles = [];
        List<string> damaged = [];
        foreach (Package package in _versionPackageManifest ?? [])
        {
            if (FindMissingPackageFiles(package, stagingDirectory, installedFiles, out int missing) && missing > 0)
            {
                App.Logger.WriteLine(
                    "Bootstrapper::VerifyExtractedPackages",
                    $"{package.Name} is missing {missing} files after extraction, extracting it again"
                );
                ExtractPackage(package);
                List<InstallManifest.Entry> ignored = [];
                FindMissingPackageFiles(package, stagingDirectory, ignored, out missing);
                if (missing > 0)
                    damaged.Add($"{package.Name} ({missing} files)");
            }
        }
        if (damaged.Count > 0)
        {
            throw new InvalidDataException(
                "Files disappeared from the Roblox installation while it was being installed: "
                    + string.Join(", ", damaged)
                    + ". Your antivirus most likely removed them. Add the Roblox versions folder to your antivirus exclusions, then launch again."
            );
        }
        App.Logger.WriteLine("Bootstrapper::VerifyExtractedPackages", $"Verified {installedFiles.Count} installed files");
        return installedFiles;
    }

    private bool FindMissingPackageFiles(
        Package package,
        string stagingDirectory,
        List<InstallManifest.Entry> installedFiles,
        out int missing
    )
    {
        missing = 0;
        string? target = AppData.PackageDirectoryMap.GetValueOrDefault(package.Name);
        if (target == null || !File.Exists(package.DownloadPath))
            return false;
        using System.IO.Compression.ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(package.DownloadPath);
        foreach (System.IO.Compression.ZipArchiveEntry entry in archive.Entries)
        {
            string name = entry.FullName.Replace('/', '\\');
            if (name.Length == 0 || name.EndsWith('\\'))
                continue;
            string relative = Path.Combine(target, name);
            installedFiles.Add(new InstallManifest.Entry(relative, entry.Length, entry.Crc32));
            FileInfo info = new(Path.Combine(stagingDirectory, relative));
            if (!info.Exists || info.Length != entry.Length)
                missing++;
        }
        return true;
    }

    private int _missingInstalledFiles = -1;

    private int CountMissingInstalledFiles()
    {
        if (_missingInstalledFiles >= 0)
            return _missingInstalledFiles;
        int missing = 0;
        string? first = null;
        try
        {
            string root = Path.GetFullPath(AppData.Directory);
            foreach (InstallManifest.Entry entry in InstallManifest.Read(root) ?? [])
            {
                string full = Path.GetFullPath(Path.Combine(root, entry.Path));
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !File.Exists(full))
                {
                    missing++;
                    first ??= entry.Path;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            App.Logger.WriteLine("Bootstrapper::MustUpgrade", ex.Message + ", the last install was cut off, repairing Roblox");
            missing = 1;
            first = InstalledFilesListName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Logger.WriteLine("Bootstrapper::MustUpgrade", "The installed file list could not be read: " + ex.Message);
        }
        if (missing > 0)
            App.Logger.WriteLine(
                "Bootstrapper::MustUpgrade",
                $"{missing} installed Roblox files are missing, a repair is required, first: {first}"
            );
        _missingInstalledFiles = missing;
        return missing;
    }

    private void VerifyCommittedInstallation()
    {
        foreach (string name in _stagedCriticalFiles)
        {
            if (File.Exists(Path.Combine(_latestVersionDirectory, name)))
            {
                continue;
            }
            App.Logger.WriteLine("Bootstrapper::VerifyCommittedInstallation", "Missing after commit: " + name);
            throw new InvalidDataException(
                "Roblox was installed but "
                    + name
                    + " is missing. Your antivirus most likely removed it. "
                    + "Add the Roblox versions folder to your antivirus exclusions, then launch again."
            );
        }
    }

    private void ExtractPackage(Package package, List<string>? files = null)
    {
        string valueOrDefault =
            AppData.PackageDirectoryMap.GetValueOrDefault(package.Name)
            ?? throw new InvalidDataException("Package " + package.Name + " is not present in the extraction map");
        string extractionRoot = _packageExtractionDirectory ?? _latestVersionDirectory;
        if (NativePackageExtractor.IsAvailable)
        {
            App.Logger.WriteLine("Bootstrapper::ExtractPackage", "Extracting " + package.Name + " natively...");
            NativePackageExtractor.Extract(
                package.DownloadPath,
                Path.Combine(extractionRoot, valueOrDefault),
                files,
                _cancelTokenSource.Token
            );
            App.Logger.WriteLine("Bootstrapper::ExtractPackage", "Done: " + package.Name);
            return;
        }
        string? fileFilter = null;
        if (files != null)
        {
            IEnumerable<string> values = files.Select(f =>
                "(?i)^" + System.Text.RegularExpressions.Regex.Escape(f).Replace("\\\\", "[\\\\/]") + "$"
            );
            fileFilter = string.Join(';', values);
        }
        App.Logger.WriteLine("Bootstrapper::ExtractPackage", "Extracting " + package.Name + "...");
        new FastZip(_fastZipEvents).ExtractZip(package.DownloadPath, Path.Combine(extractionRoot, valueOrDefault), fileFilter);
        App.Logger.WriteLine("Bootstrapper::ExtractPackage", "Done: " + package.Name);
    }

    private static readonly string[] NativeCodeExtensions =
    [
        ".dll",
        ".exe",
        ".asi",
        ".sys",
        ".drv",
        ".ocx",
        ".cpl",
        ".scr",
        ".com",
        ".efi",
    ];

    private static bool IsNativeCodeModFile(string relative)
    {
        string extension = Path.GetExtension(relative);
        return NativeCodeExtensions.Any(native => string.Equals(native, extension, StringComparison.OrdinalIgnoreCase));
    }
}
