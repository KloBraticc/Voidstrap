using Voidstrap.Platform.Linux;
using Voidstrap.Platform.MacOS;
using Voidstrap.RobloxInterfaces;
using Voidstrap.UI.Elements.Bootstrapper.Base;
using Voidstrap.UI.Elements.Settings;
using Voidstrap.UI.Elements.Settings.Pages;
using ICSharpCode.SharpZipLib.Core;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;
using System.Buffers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Shell;
using System.Windows.Threading;
using Voidstrap.AppData;
using Voidstrap.Core;
using Voidstrap.Integrations;
using Voidstrap.Integrations.AssetProxy;
using Voidstrap.Platform;

namespace Voidstrap;

public partial class Bootstrapper
{
    internal async Task PrepareLinuxStudioLaunchAsync(CancellationToken cancellationToken)
    {
        const string logIdent = "Bootstrapper::PrepareLinuxStudioLaunch";
        try
        {
            App.FastFlags.Save();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Fast flags could not be written before launch: " + ex.Message);
        }

        if (!ModsAllowedForThisLaunch())
        {
            App.Logger.WriteLine(
                logIdent,
                $"Mods are set to {App.Settings.Prop.ModApplyTarget}, so this Studio launch runs unmodded. Mod files are kept on disk."
            );
            return;
        }

        SetStatus(Strings.Bootstrapper_Status_ApplyingModifications);
        Directory.CreateDirectory(Paths.Mods);

        try
        {
            await ApplySkyboxModifications(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Skybox could not be applied: " + ex.Message);
        }

        RepairFlattenedModNames();
        RepairModRootCase();
        try
        {
            CursorManager.ApplyOnLaunch();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Cursors could not be applied: " + ex.Message);
        }

        App.Logger.WriteLine(logIdent, "Studio modifications are ready for Vinegar");
    }

    internal async Task PrepareLinuxLaunchAsync(CancellationToken cancellationToken)
    {
        const string logIdent = "Bootstrapper::PrepareLinuxLaunch";
        try
        {
            Voidstrap.Utility.VirtualMachineProfile.ReportOnce();
            if (Voidstrap.Utility.VirtualMachineProfile.ShouldForceSafeGraphics)
            {
                foreach (KeyValuePair<string, object> flag in Voidstrap.Utility.VirtualMachineProfile.SafeFastFlags)
                {
                    App.FastFlags.SetValue(flag.Key, flag.Value);
                }
            }

            App.FastFlags.MigratePlayerLoggingPreset();
            App.FastFlags.Save();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Fast flags could not be written before launch: " + ex.Message);
        }

        SetStatus(Strings.Bootstrapper_Status_ApplyingModifications);
        Directory.CreateDirectory(Paths.Mods);

        try
        {
            await ApplySkyboxModifications(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Skybox could not be applied: " + ex.Message);
        }

        try
        {
            await ApplyLinuxFontFamiliesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Font families could not be prepared: " + ex.Message);
        }

        if (!ModsAllowedForThisLaunch())
        {
            App.Logger.WriteLine(
                logIdent,
                $"Mods are set to {App.Settings.Prop.ModApplyTarget}, so this Sober launch runs unmodded. Mod files are kept on disk."
            );
            return;
        }

        RepairFlattenedModNames();
        RepairModRootCase();
        try
        {
            CursorManager.ApplyOnLaunch();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Cursors could not be applied: " + ex.Message);
        }

        (string ClientDirectory, string VersionGuid)? client = null;
        try
        {
            client = await PrepareSoberClientTreeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "The Sober Roblox files could not be unpacked for mods: " + ex.Message);
        }

        FileModManager.ApplyFromSettings(client?.ClientDirectory, client?.VersionGuid);
        if (client is not { } tree)
        {
            return;
        }

        try
        {
            ModAutoFixer.PrepareModSources(tree.ClientDirectory);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Mod folders could not be checked: " + ex.Message);
        }

        if (IsStudioLaunch)
        {
            return;
        }

        try
        {
            await ModGenerator.RefreshOutdatedAsync(tree.ClientDirectory, tree.VersionGuid, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "The generated UI mod could not be refreshed: " + ex.Message);
        }
    }

    internal static string? MacVersionGuid(string robloxVersion)
    {
        if (string.IsNullOrWhiteSpace(robloxVersion))
            return null;
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("macos:" + robloxVersion.Trim()));
        return "version-" + Convert.ToHexStringLower(hash, 0, 8);
    }

    internal async Task PrepareMacLaunchAsync(string bundlePath, string robloxVersion, CancellationToken cancellationToken)
    {
        const string logIdent = "Bootstrapper::PrepareMacLaunch";
        string resources = Path.Combine(bundlePath, "Contents", "Resources");
        SetStatus(Strings.Bootstrapper_Status_ApplyingModifications);
        Directory.CreateDirectory(Paths.Mods);

        try
        {
            await ApplySkyboxModifications(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Skybox could not be applied: " + ex.Message);
        }

        try
        {
            Voidstrap.Utility.CustomFontMod.Apply(Path.Combine(resources, "content", "fonts", "families"), logIdent);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Custom font failed: " + ex.Message);
        }

        if (!ModsAllowedForThisLaunch())
        {
            MacModSync.Apply(resources, new Dictionary<string, string>());
            App.Logger.WriteLine(
                logIdent,
                $"Mods are set to {App.Settings.Prop.ModApplyTarget}, so this launch runs unmodded. Mod files are kept on disk."
            );
            return;
        }

        RepairFlattenedModNames();
        RepairModRootCase();
        try
        {
            CursorManager.ApplyOnLaunch();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Cursors could not be applied: " + ex.Message);
        }

        FileModManager.ApplyFromSettings(resources, MacVersionGuid(robloxVersion));
        try
        {
            ModAutoFixer.PrepareModSources(resources);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(logIdent, "Mod folders could not be checked: " + ex.Message);
        }

        if (!IsStudioLaunch && robloxVersion.Length > 0)
        {
            try
            {
                await ModGenerator.RefreshOutdatedAsync(resources, robloxVersion, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(logIdent, "The generated UI mod could not be refreshed: " + ex.Message);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        MacModSync.Apply(resources, MacModSync.Collect(!IsStudioLaunch));
    }

    private static readonly string[] CanonicalModRoots = ["content", "ExtraContent", "PlatformContent"];

    internal static void RepairModRootCase()
    {
        if (Path.DirectorySeparatorChar == '\\' || !Directory.Exists(Paths.Mods))
        {
            return;
        }

        FileModManager.FolderGate.Wait();
        try
        {
            RepairModRootCaseCore();
        }
        finally
        {
            FileModManager.FolderGate.Release();
        }
    }

    private static void RepairModRootCaseCore()
    {
        const string LOG_IDENT = "Bootstrapper::RepairModRootCase";

        foreach (string directory in Directory.EnumerateDirectories(Paths.Mods))
        {
            string name = Path.GetFileName(directory);
            string? canonical = CanonicalModRoots.FirstOrDefault(root => string.Equals(root, name, StringComparison.OrdinalIgnoreCase));
            if (canonical == null || string.Equals(canonical, name, StringComparison.Ordinal))
            {
                continue;
            }

            string target = Path.Combine(Paths.Mods, canonical);
            try
            {
                string temporary = Path.Combine(Paths.Mods, Guid.NewGuid().ToString("N"));
                Directory.Move(directory, temporary);
                try
                {
                    if (!Directory.Exists(target))
                    {
                        Directory.Move(temporary, target);
                        continue;
                    }
                }
                finally
                {
                    if (Directory.Exists(temporary))
                        Directory.Move(temporary, directory);
                }
                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    string destination = Path.Combine(target, Path.GetRelativePath(directory, file));
                    if (File.Exists(destination) && File.GetLastWriteTimeUtc(destination) >= File.GetLastWriteTimeUtc(file))
                    {
                        File.Delete(file);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(file, destination, true);
                }
                Directory.Delete(directory, true);
                App.Logger.WriteLine(LOG_IDENT, "Moved the " + name + " mod folder into " + canonical);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not move the " + name + " mod folder: " + ex.Message);
            }
        }
    }

    private static void RepairFlattenedModNames()
    {
        if (Path.DirectorySeparatorChar == '\\' || !Directory.Exists(Paths.Mods))
        {
            return;
        }

        FileModManager.FolderGate.Wait();
        try
        {
            RepairFlattenedModNamesCore();
        }
        finally
        {
            FileModManager.FolderGate.Release();
        }
    }

    private static void RepairFlattenedModNamesCore()
    {
        const string LOG_IDENT = "Bootstrapper::RepairFlattenedModNames";

        foreach (string file in Directory.EnumerateFiles(Paths.Mods))
        {
            string name = Path.GetFileName(file);
            if (!name.Contains('\\'))
            {
                continue;
            }

            try
            {
                string target = Path.Combine(Paths.Mods, name.Replace('\\', Path.DirectorySeparatorChar));
                if (File.Exists(target))
                {
                    File.Delete(file);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target);
                }
                App.Logger.WriteLine(LOG_IDENT, "Moved " + name + " into its folder");
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not move " + name + ": " + ex.Message);
            }
        }
    }

    internal static string SoberClientRoot => Path.Combine(Paths.Cache, "SoberClient");

    internal static async Task<(string ClientDirectory, string VersionGuid)?> PrepareSoberClientTreeAsync(
        CancellationToken cancellationToken
    )
    {
        const string logIdent = "Bootstrapper::PrepareSoberClientTree";
        OperationResult<SoberApkAssetIndex> indexResult = await SoberApkAssetIndexProvider.CreateDefault().LoadAsync(cancellationToken);
        if (!indexResult.Succeeded || indexResult.Value is null)
        {
            App.Logger.WriteLine(logIdent, indexResult.Failure?.Message ?? "The Sober Roblox package is unavailable");
            return null;
        }

        string sha = indexResult.Value.PackageSha256;
        if (sha.Length < 16)
        {
            return null;
        }

        string versionGuid = "version-" + sha[..16].ToLowerInvariant();
        string root = SoberClientRoot;
        string directory = Path.Combine(root, versionGuid);
        string marker = directory + ".complete";
        if (!File.Exists(marker))
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }

            OperationResult<int> extracted = await indexResult.Value.ExtractClientTreeAsync(directory, cancellationToken);
            if (!extracted.Succeeded)
            {
                App.Logger.WriteLine(logIdent, extracted.Failure?.Message ?? "The Sober Roblox files could not be unpacked");
                return null;
            }

            await File.WriteAllTextAsync(marker, sha, cancellationToken);
            App.Logger.WriteLine(logIdent, "Unpacked " + extracted.Value + " Sober Roblox files so mods can read the originals");
        }

        foreach (string stale in Directory.EnumerateFileSystemEntries(root))
        {
            if (
                string.Equals(Path.GetFullPath(stale), Path.GetFullPath(directory), StringComparison.Ordinal)
                || string.Equals(Path.GetFullPath(stale), Path.GetFullPath(marker), StringComparison.Ordinal)
            )
            {
                continue;
            }

            try
            {
                if (Directory.Exists(stale))
                {
                    Directory.Delete(stale, true);
                }
                else
                {
                    File.Delete(stale);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(logIdent, "An old Sober file cache could not be removed: " + ex.Message);
            }
        }

        return (directory, versionGuid);
    }

    private static async Task ApplyLinuxFontFamiliesAsync(CancellationToken cancellationToken)
    {
        const string logIdent = "Bootstrapper::ApplyLinuxFontFamilies";
        if (!File.Exists(Paths.CustomFont))
        {
            Voidstrap.Utility.CustomFontMod.RemoveGeneratedFamilies();
            return;
        }

        OperationResult<SoberApkAssetIndex> indexResult = await SoberApkAssetIndexProvider.CreateDefault().LoadAsync(cancellationToken);
        if (!indexResult.Succeeded || indexResult.Value is null)
        {
            App.Logger.WriteLine(logIdent, indexResult.Failure?.Message ?? "The Sober Roblox package is unavailable");
            return;
        }

        string familiesDirectory = Path.Combine(Paths.Base, "Cache", "SoberFontFamilies");
        OperationResult<int> extracted = await indexResult.Value.ExtractEntriesAsync(
            "content/fonts/families",
            familiesDirectory,
            ".json",
            cancellationToken
        );
        if (!extracted.Succeeded)
        {
            App.Logger.WriteLine(logIdent, extracted.Failure?.Message ?? "The Sober font families could not be extracted");
            return;
        }

        App.Logger.WriteLine(logIdent, "Extracted " + extracted.Value + " font families from the Sober Roblox package");
        Voidstrap.Utility.CustomFontMod.Apply(familiesDirectory, logIdent);
    }
}
