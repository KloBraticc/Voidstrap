using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Voidstrap;

public partial class Bootstrapper
{
    private async Task ApplySkyboxModifications(bool allowStoragePatch)
    {
        const string LOG_IDENT = "Bootstrapper::ApplyModifications";

        string[] files;
        if (IsStudioLaunch)
        {
            App.Logger.WriteLine(LOG_IDENT, "Skipping skybox (Roblox Studio).");
        }
        else if (!App.Settings.Prop.SkyBoxDataSending)
        {
            App.Logger.WriteLine(LOG_IDENT, "Skipping skybox (disabled).");
            try
            {
                InlineArray5<string> buffer = default;
                buffer[0] = Paths.Mods;
                buffer[1] = "PlatformContent";
                buffer[2] = "pc";
                buffer[3] = "textures";
                buffer[4] = "sky";
                string path = Path.Combine(buffer);
                FileModManager.FolderGate.Wait();
                try
                {
                    if (Directory.Exists(path))
                    {
                        files = Directory.GetFiles(path, "*.*", SearchOption.AllDirectories);
                        for (int i = 0; i < files.Length; i++)
                        {
                            File.SetAttributes(files[i], FileAttributes.Normal);
                        }
                        Directory.Delete(path, recursive: true);
                    }
                }
                finally
                {
                    FileModManager.FolderGate.Release();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Skybox cleanup failed: " + ex.Message);
            }
        }
        else
        {
            try
            {
                try
                {
                    if (allowStoragePatch)
                        await ApplySkyboxPatchToRobloxStorageAsync(_cancelTokenSource.Token);
                }
                catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception patchException)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Skybox storage patch unavailable: " + patchException.Message);
                }
                if (!App.Settings.Prop.SkyboxName.Equals(SkyboxImageConverter.CustomPackName, StringComparison.OrdinalIgnoreCase))
                    await EnsureSkyboxPackDownloadedAsync(App.Settings.Prop.SkyboxName);
                await ApplySkyboxAsync(App.Settings.Prop.SkyboxName, Paths.Mods);
                App.Logger.WriteLine(LOG_IDENT, "Skybox applied: " + App.Settings.Prop.SkyboxName);
            }
            catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex2)
            {
                App.Logger.WriteLine(LOG_IDENT, "Skybox failed: " + ex2.Message);
            }
        }
    }

    private async Task ApplyModifications()
    {
        const string LOG_IDENT = "Bootstrapper::ApplyModifications";

        SetStatus(Strings.Bootstrapper_Status_ApplyingModifications);
        File.Delete(Path.Combine(Paths.Base, "ModManifest.txt"));
        Directory.CreateDirectory(Paths.Mods);
        await ApplySkyboxModifications(true);
        _cancelTokenSource.Token.ThrowIfCancellationRequested();
        string installedFontDir = Path.Combine(_latestVersionDirectory, "content", "fonts", "families");
        try
        {
            CustomFontMod.Apply(installedFontDir, LOG_IDENT);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Custom font failed: " + ex.Message);
        }
        bool modsAllowed = ModsAllowedForThisLaunch();
        bool fastFlagsAllowed = FastFlagsAllowedForThisLaunch();
        if (!modsAllowed)
        {
            App.Logger.WriteLine(
                LOG_IDENT,
                $"Mods are set to {App.Settings.Prop.ModApplyTarget}, so this {(IsStudioLaunch ? "Studio" : "Player")} launch runs unmodded. Mod files are kept on disk."
            );
        }
        if (App.Settings.Prop.UseFastFlagManager && !fastFlagsAllowed)
        {
            App.Logger.WriteLine(
                LOG_IDENT,
                $"Fast Flags are set to {App.Settings.Prop.FastFlagApplyTarget}, so this {(IsStudioLaunch ? "Studio" : "Player")} launch runs without them. Your Fast Flags are kept."
            );
        }
        else
        {
            CursorManager.ApplyOnLaunch();
            FileModManager.ApplyFromSettings(_latestVersionDirectory, _latestVersionGuid);
        }

        if (modsAllowed)
        {
            ModAutoFixer.PrepareModSources(_latestVersionDirectory);
        }
        HashSet<string> modFolderFiles = new(StringComparer.OrdinalIgnoreCase);
        int ignoredModFiles = 0;
        int appliedModCount = 0;
        int failedModCount = 0;
        Dictionary<string, string> selectedMods = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> nextManagedManifest = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> preservedManagedPaths = new(StringComparer.OrdinalIgnoreCase);
        FileModManager.FolderGate.Wait();
        try
        {
            foreach (string text in Directory.EnumerateFiles(Paths.Mods, "*", SearchOption.AllDirectories))
            {
                if (_cancelTokenSource.IsCancellationRequested)
                {
                    return;
                }
                string text2 = text[(Paths.Mods.Length + 1)..];
                if (text2 == "README.txt")
                {
                    try
                    {
                        File.Delete(text);
                    }
                    catch (Exception ex5)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Could not remove README.txt: " + ex5.Message);
                    }
                }
                else if (modsAllowed && ModAutoFixer.IsIgnoredModFile(text2))
                {
                    ignoredModFiles++;
                }
                else if (string.Equals(text2, "ClientSettings\\ClientAppSettings.json", StringComparison.OrdinalIgnoreCase))
                {
                    if (fastFlagsAllowed)
                        selectedMods[text2] = text;
                }
                else if (
                    modsAllowed
                    && !text2.EndsWith(".lock", StringComparison.Ordinal)
                    && (!IsStudioLaunch || !text2.StartsWith("PlatformContent\\pc\\textures\\sky", StringComparison.OrdinalIgnoreCase))
                )
                {
                    selectedMods[text2] = text;
                }
            }
        }
        finally
        {
            FileModManager.FolderGate.Release();
        }
        if (modsAllowed && !IsStudioLaunch)
        {
            try
            {
                await ModGenerator
                    .RefreshOutdatedAsync(_latestVersionDirectory, _latestVersionGuid, _cancelTokenSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The generated UI mod could not be refreshed: " + ex.Message);
            }
        }
        try
        {
            ManagedModScanResult scan = ManagedModStore.ScanEnabledFiles();
            ignoredModFiles += scan.IgnoredSkipped;
            foreach (string id in scan.SuccessfulModIds)
                nextManagedManifest[id] = [];
            foreach (ManagedModFile file in scan.Files)
            {
                if (_cancelTokenSource.IsCancellationRequested)
                    return;
                if (file.Relative.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (
                    !fastFlagsAllowed
                    && string.Equals(file.Relative, "ClientSettings\\ClientAppSettings.json", StringComparison.OrdinalIgnoreCase)
                )
                    continue;
                if (IsStudioLaunch && file.Relative.StartsWith("PlatformContent\\pc\\textures\\sky", StringComparison.OrdinalIgnoreCase))
                    continue;
                selectedMods[file.Relative] = file.Source;
                nextManagedManifest[file.Mod.Id].Add(file.Relative);
            }
            Dictionary<string, List<string>> previousManagedManifest = GetPreviousManagedModManifest();
            foreach ((string id, string message) in scan.Failures)
            {
                App.Logger.WriteLine(LOG_IDENT, "Managed mod " + id[..8] + " could not be indexed: " + message);
                if (previousManagedManifest.TryGetValue(id, out List<string>? previousPaths))
                {
                    nextManagedManifest[id] = [.. previousPaths];
                    preservedManagedPaths.UnionWith(previousPaths);
                }
                else
                {
                    List<string> fallbackPaths = [.. GetPreviousModManifest()];
                    nextManagedManifest[id] = fallbackPaths;
                    preservedManagedPaths.UnionWith(fallbackPaths);
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Managed mods could not be indexed: " + ex.Message);
            nextManagedManifest = GetPreviousManagedModManifest()
                .ToDictionary(item => item.Key, item => new List<string>(item.Value), StringComparer.OrdinalIgnoreCase);
            preservedManagedPaths.UnionWith(GetPreviousModManifest());
        }
        List<string> nativeCode = [.. selectedMods.Keys.Where(IsNativeCodeModFile)];
        foreach (string relative in nativeCode)
        {
            selectedMods.Remove(relative);
        }
        if (nativeCode.Count > 0)
        {
            string names =
                string.Join(", ", nativeCode.Take(12)) + (nativeCode.Count > 12 ? " and " + (nativeCode.Count - 12) + " more" : "");
            App.Logger.WriteLine(
                LOG_IDENT,
                "Skipped "
                    + nativeCode.Count
                    + " mod files that are programs or DLLs, Roblox closes itself when it finds these in its folder: "
                    + names
            );
            Voidstrap.Utility.AppNotifications.RecordInfo(
                "mods:nativecode",
                "Some mod files were skipped",
                "Roblox closes itself as soon as it finds programs or DLL files, like the dxgi.dll that ReShade installs, in its folder. Voidstrap skipped these mod files so Roblox can start: "
                    + names
            );
        }
        if (ignoredModFiles > 0)
        {
            App.Logger.WriteLine(
                LOG_IDENT,
                "Skipped "
                    + ignoredModFiles
                    + " mod files that Roblox cannot load from the client folder: app scripts, configs and translations tied to another Roblox version, duplicate copies, and classic terrain and material textures, which Roblox now downloads instead."
            );
        }
        if (
            !IsStudioLaunch
            && !App.Settings.Prop.SkyBoxDataSending
            && selectedMods.Keys.Any(relative =>
                relative.StartsWith(ModAutoFixer.SkyFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            try
            {
                _modSkyPatchApplied = true;
                await ApplySkyboxPatchToRobloxStorageAsync(_cancelTokenSource.Token);
                App.Logger.WriteLine(
                    LOG_IDENT,
                    "A mod replaces the sky, so the skybox storage patch was applied to let Roblox use it."
                );
            }
            catch (OperationCanceledException) when (_cancelTokenSource.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception patchException)
            {
                App.Logger.WriteLine(
                    LOG_IDENT,
                    "The skybox storage patch for a mod sky is unavailable: " + patchException.Message
                );
            }
        }
        List<(string Source, string Relative, string Target)> pendingMods = new(selectedMods.Count);
        foreach ((string relative, string source) in selectedMods)
        {
            modFolderFiles.Add(relative);
            pendingMods.Add((source, relative, Path.Combine(_latestVersionDirectory, relative)));
        }
        modFolderFiles.UnionWith(preservedManagedPaths);
        foreach (
            string directory in pendingMods
                .Select(item => Path.GetDirectoryName(item.Target))
                .OfType<string>()
                .Where(path => path.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            Directory.CreateDirectory(directory);
        }
        bool cacheVersionMatches = string.Equals(AppData.State.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string> previousCache = cacheVersionMatches
            ? new Dictionary<string, string>(AppData.State.ModApplyCache ?? [], StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string, string> nextCache = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentQueue<(string Relative, string Message)> applyErrors = new();
        await Parallel.ForEachAsync(
            pendingMods,
            new ParallelOptions { MaxDegreeOfParallelism = ModApplyConcurrency, CancellationToken = _cancelTokenSource.Token },
            (item, token) =>
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    FileInfo sourceInfo = new(item.Source);
                    FileInfo targetInfo = new(item.Target);
                    string fingerprint = BuildModFingerprint(sourceInfo, targetInfo);
                    if (
                        targetInfo.Exists
                        && previousCache.TryGetValue(item.Relative, out string? cached)
                        && string.Equals(cached, fingerprint, StringComparison.Ordinal)
                    )
                    {
                        nextCache[item.Relative] = fingerprint;
                        return ValueTask.CompletedTask;
                    }
                    bool same =
                        cacheVersionMatches
                        && targetInfo.Exists
                        && sourceInfo.Length == targetInfo.Length
                        && MD5Hash.FromFile(item.Source) == MD5Hash.FromFile(item.Target);
                    if (!same)
                    {
                        Filesystem.AssertReadOnly(item.Target);
                        File.Copy(item.Source, item.Target, overwrite: true);
                        Interlocked.Increment(ref appliedModCount);
                        targetInfo.Refresh();
                    }
                    nextCache[item.Relative] = BuildModFingerprint(sourceInfo, targetInfo);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedModCount);
                    applyErrors.Enqueue((item.Relative, ex.Message));
                }
                return ValueTask.CompletedTask;
            }
        );
        while (applyErrors.TryDequeue(out (string Relative, string Message) error))
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not apply " + error.Relative + ": " + error.Message);
        }
        App.Logger.WriteLine(LOG_IDENT, $"Applied {appliedModCount} changed mod files.");
        if (failedModCount > 0)
        {
            App.Logger.WriteLine(
                LOG_IDENT,
                $"{failedModCount} mod files could not be applied, the rest were still applied."
            );
        }
        try
        {
            ModAutoFixer.Run(_latestVersionDirectory, selectedMods.Count > 0);
        }
        catch (Exception autoFixError)
        {
            App.Logger.WriteLine(LOG_IDENT, "The mod auto fixer could not run: " + autoFixError.Message);
        }
        Dictionary<string, List<string>> dictionary = [];
        KeyValuePair<string, string>[] packagePrefixes =
        [
            .. AppData.PackageDirectoryMap.Where(item => !string.IsNullOrEmpty(item.Value)).OrderByDescending(item => item.Value.Length),
        ];
        HashSet<string> unresolvedStalePaths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> previousModManifest = new(GetPreviousModManifest(), StringComparer.OrdinalIgnoreCase);
        string installedCustomFont = Path.Combine(_latestVersionDirectory, "content", "fonts", "CustomFont.ttf");
        if (File.Exists(installedCustomFont))
        {
            previousModManifest.Add(Path.Combine("content", "fonts", "CustomFont.ttf"));
        }
        foreach (string familyName in CustomFontMod.FindGeneratedFamilyNames(installedFontDir))
        {
            previousModManifest.Add(Path.Combine("content", "fonts", "families", familyName));
        }
        foreach (string loc in previousModManifest)
        {
            if (modFolderFiles.Contains(loc))
            {
                continue;
            }
            if (!TryResolveVersionRelativePath(loc, out string safeTarget))
            {
                App.Logger.WriteLine(LOG_IDENT, "Ignored an invalid stale mod path");
                continue;
            }
            KeyValuePair<string, string> keyValuePair = packagePrefixes.FirstOrDefault(item =>
                loc.StartsWith(item.Value, StringComparison.OrdinalIgnoreCase)
            );
            string key = keyValuePair.Key;
            if (string.IsNullOrEmpty(key))
            {
                try
                {
                    if (File.Exists(safeTarget))
                    {
                        Filesystem.AssertReadOnly(safeTarget);
                        File.Delete(safeTarget);
                    }
                }
                catch (Exception ex)
                {
                    unresolvedStalePaths.Add(loc);
                    App.Logger.WriteLine(LOG_IDENT, "Could not remove stale mod " + loc + ": " + ex.Message);
                }
            }
            else
            {
                if (!dictionary.TryGetValue(key, out List<string>? value))
                {
                    value = [];
                    dictionary[key] = value;
                }

                value.Add(loc[keyValuePair.Value.Length..]);
            }
        }
        bool packageManifestFetched = false;
        foreach (KeyValuePair<string, List<string>> item in dictionary)
        {
            string pkgName;
            List<string> files2;
            (pkgName, files2) = item;
            if (_cancelTokenSource.IsCancellationRequested)
            {
                return;
            }
            try
            {
                Package? pkg = ResolveInstalledPackage(pkgName);
                if (pkg == null || !IsPackageCacheValid(pkg))
                {
                    if (!packageManifestFetched)
                    {
                        await GetLatestVersionInfo(fetchManifest: true, forceManifest: true);
                        packageManifestFetched = true;
                    }
                    pkg = ResolveInstalledPackage(pkgName);
                }
                if (pkg == null)
                {
                    throw new InvalidDataException("The Roblox package could not be identified");
                }
                await DownloadPackage(pkg);
                ExtractPackage(pkg, files2);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string prefix = AppData.PackageDirectoryMap.GetValueOrDefault(pkgName) ?? string.Empty;
                foreach (string file in files2)
                {
                    unresolvedStalePaths.Add(prefix + file);
                }
                App.Logger.WriteLine(
                    LOG_IDENT,
                    "Could not restore stale files from " + pkgName + ": " + ex.Message
                );
            }
        }
        modFolderFiles.UnionWith(unresolvedStalePaths);
        AppData.State.ModManifest = [.. modFolderFiles];
        AppData.State.ModApplyCache = new Dictionary<string, string>(nextCache, StringComparer.OrdinalIgnoreCase);
        AppData.State.ManagedModManifest = nextManagedManifest;
        AppData.State.ModApplyVersion = _latestVersionGuid;
        App.State.Save();
        try
        {
            bool flag = File.Exists(Path.Combine(_latestVersionDirectory, "eurotrucks2.exe"));
            if (App.Settings.Prop.RenameClientToEuroTrucks2 && !flag)
            {
                File.Move(
                    Path.Combine(_latestVersionDirectory, "RobloxPlayerBeta.exe"),
                    Path.Combine(_latestVersionDirectory, "eurotrucks2.exe")
                );
            }
            else if (!App.Settings.Prop.RenameClientToEuroTrucks2 && flag)
            {
                File.Move(
                    Path.Combine(_latestVersionDirectory, "eurotrucks2.exe"),
                    Path.Combine(_latestVersionDirectory, "RobloxPlayerBeta.exe")
                );
            }
        }
        catch (Exception ex4)
        {
            App.Logger.WriteLine(LOG_IDENT, "EuroTrucks rename failed: " + ex4.Message);
        }
    }

    private static string BuildModFingerprint(FileInfo source, FileInfo target)
    {
        return string.Concat(
            source.FullName,
            "|",
            source.Length,
            "|",
            source.LastWriteTimeUtc.Ticks,
            "|",
            target.Exists ? target.Length : -1L,
            "|",
            target.Exists ? target.LastWriteTimeUtc.Ticks : -1L
        );
    }

    private bool TryResolveVersionRelativePath(string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }
        string[] segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        );
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }
        string root =
            Path.GetFullPath(_latestVersionDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        fullPath = candidate;
        return true;
    }

    private List<string> GetPreviousModManifest()
    {
        if (string.Equals(AppData.State.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase))
        {
            return AppData.State.ModManifest ?? [];
        }
        if (string.Equals(App.State.Prop.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase))
        {
            return App.State.Prop.ModManifest ?? [];
        }
        return [];
    }

    private Dictionary<string, List<string>> GetPreviousManagedModManifest()
    {
        if (string.Equals(AppData.State.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase))
        {
            return AppData.State.ManagedModManifest ?? [];
        }
        if (string.Equals(App.State.Prop.ModApplyVersion, _latestVersionGuid, StringComparison.OrdinalIgnoreCase))
        {
            return App.State.Prop.ManagedModManifest ?? [];
        }
        return [];
    }

    private Package? ResolveInstalledPackage(string packageName)
    {
        Package? package = _versionPackageManifest.Find(item => string.Equals(item.Name, packageName, StringComparison.OrdinalIgnoreCase));
        if (package != null)
        {
            return package;
        }
        if (!AppData.State.PackageHashes.TryGetValue(packageName, out string? signature) || string.IsNullOrWhiteSpace(signature))
        {
            return null;
        }
        string cachedPath = Path.Combine(Paths.Downloads, signature);
        return new Package
        {
            Name = packageName,
            Signature = signature,
            PackedSize = File.Exists(cachedPath) ? checked((int)Math.Min(new FileInfo(cachedPath).Length, int.MaxValue)) : 0,
            Size = 0,
        };
    }

    private static bool IsPackageCacheValid(Package package)
    {
        return File.Exists(package.DownloadPath)
            && string.Equals(MD5Hash.FromFile(package.DownloadPath), package.Signature, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> GetLatestCommitShaAsync(CancellationToken ct)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, App.SkyboxCommitApiUrl);
        req.Headers.UserAgent.ParseAdd("SkyboxInstaller");
        using HttpResponseMessage res = await SkyboxHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        using Stream stream = await res.Content.ReadAsStreamAsync(ct);
        using JsonDocument jsonDocument = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        string? sha = jsonDocument.RootElement.GetProperty("sha").GetString();
        if (sha == null || sha.Length != 40 || !sha.All(Uri.IsHexDigit))
            throw new InvalidDataException("The skybox version is invalid");
        return sha;
    }
}
