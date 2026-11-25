using System.IO.Compression;
using System.Security.Cryptography;
using System.Windows;
using Voidstrap.AppData;
using Voidstrap.Integrations.ClassicTopBar;

namespace Voidstrap.Utility;

public static partial class ModGenerator
{
    private static IReadOnlyDictionary<string, string>? PrepareControllerTextures(string versionDirectory, string versionGuid, CancellationToken token)
    {
        const string LOG_IDENT = "ModGenerator::PrepareControllerTextures";
        string packageId = ClassicTopBarMod.Ps4ButtonsPackageId;
        if (packageId.Length == 0)
            return null;

        IReadOnlyList<ManagedModRecord> mods = ManagedModStore.Load();
        if (!mods.Any(mod => mod.Id == packageId && mod.Enabled && mod.AppliesTo(ModInstallTargets.Roblox)))
            return null;

        string? themeId = FindModId();
        Painter? painter = null;
        if (themeId != null && mods.Any(mod => mod.Id == themeId && mod.Enabled && mod.AppliesTo(ModInstallTargets.Roblox)))
        {
            if (LoadSettings(themeId) is { } settings)
                painter = BuildPainter(settings, Path.Combine(Root, themeId));
        }

        string folder = ManagedModStore.GetFolder(packageId);
        string cache = Path.Combine(Paths.Cache, "ControllerTextures");
        Directory.CreateDirectory(cache);
        Dictionary<string, string> prepared = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ZipArchive?> archives = new(StringComparer.OrdinalIgnoreCase);
        RobloxPlayerData data = new();
        HashSet<string> manifest = Platform.IsWindows && string.Equals(data.State.VersionGuid, versionGuid, StringComparison.OrdinalIgnoreCase)
            ? CurrentManifest(data)
            : new(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string file in ManagedModStore.EnumeratePackageFiles(folder))
            {
                token.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                if (!IsControllerTexture(relative))
                    continue;
                try
                {
                    Raster image = Raster.Load(file);
                    Raster? original = LoadStockControllerTexture(data, versionDirectory, versionGuid, relative, manifest, archives);
                    if (original != null)
                        image = FitControllerTexture(image, original);
                    if (painter != null && ControllerBounds(image) is { IsEmpty: false } bounds)
                    {
                        double peak = 0;
                        for (int i = 0; i < image.Pixels.Length; i += 4)
                            if (image.Pixels[i + 3] > 0)
                                peak = Math.Max(peak, Luma(image.Pixels, i));
                        PaintRegion(image.Pixels, image.Width, null, 0, bounds.X, bounds.Y, bounds.X + bounds.Width - 1,
                            bounds.Y + bounds.Height - 1, peak, painter);
                    }
                    if (original == null && painter == null)
                        continue;

                    byte[] png = image.EncodePng();
                    string destination = Path.Combine(cache, Convert.ToHexStringLower(SHA256.HashData(png)) + ".png");
                    if (!File.Exists(destination))
                    {
                        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.WriteAllBytes(temporary, png);
                            File.Move(temporary, destination, overwrite: true);
                        }
                        finally
                        {
                            File.Delete(temporary);
                        }
                    }
                    prepared[file] = destination;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not prepare " + relative + ": " + ex.Message);
                }
            }
        }
        finally
        {
            foreach (ZipArchive? archive in archives.Values)
                archive?.Dispose();
        }
        return prepared;
    }

    private static bool IsControllerTexture(string relative)
    {
        string normalized = relative.Replace('\\', '/');
        if (!normalized.Contains("/textures/ui/Controls/", StringComparison.OrdinalIgnoreCase)
            || !normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return false;
        string name = Path.GetFileNameWithoutExtension(normalized);
        return name.StartsWith("Button", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("DPad", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Thumbstick", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Xbox", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GenericController", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("PlatformController", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> PreserveControllerTextures(string output, string staging, string manifest, CancellationToken token)
    {
        Dictionary<string, string> previous = File.Exists(manifest)
            ? new(JsonFile.Deserialize<Dictionary<string, string>>(manifest), StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> generated = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ManagedModStore.EnumeratePackageFiles(staging))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
            if (IsControllerTexture(relative))
                generated[relative] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }
        foreach (string file in ManagedModStore.EnumeratePackageFiles(output))
        {
            token.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(output, file).Replace('\\', '/');
            if (!IsControllerTexture(relative))
                continue;
            if (previous.TryGetValue(relative, out string? fingerprint)
                && Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file))) == fingerprint)
                continue;

            string destination = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
            generated.Remove(relative);
        }
        return generated;
    }

    private static Raster? LoadStockControllerTexture(
        RobloxPlayerData data,
        string versionDirectory,
        string versionGuid,
        string relative,
        HashSet<string> manifest,
        Dictionary<string, ZipArchive?> archives
    )
    {
        string installed = Path.Combine(versionDirectory, relative);
        if (!File.Exists(installed))
            return null;
        if (Platform.IsMacOS && MacModSync.FindOriginal(versionDirectory, relative) is { } backup)
            return Raster.Load(backup);
        string cached = Path.Combine(OriginalsRoot, versionGuid, relative);
        if (File.Exists(cached))
            return Raster.Load(cached);
        if (!manifest.Contains(relative))
            return Raster.Load(ResolveSource(versionDirectory, versionGuid, relative, manifest));

        foreach ((string package, string prefix) in data.PackageDirectoryMap.OrderByDescending(pair => pair.Value.Length))
        {
            string normalizedPrefix = prefix.Replace('\\', '/');
            if (!relative.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase)
                || !data.State.PackageHashes.TryGetValue(package, out string? hash)
                || hash.Length != 32 || !hash.All(Uri.IsHexDigit))
                continue;
            if (!archives.TryGetValue(hash, out ZipArchive? archive))
            {
                string path = Path.Combine(Paths.Downloads, hash);
                archive = File.Exists(path) ? ZipFile.OpenRead(path) : null;
                archives.Add(hash, archive);
            }
            string name = relative[normalizedPrefix.Length..];
            ZipArchiveEntry? entry = archive?.GetEntry(name) ?? archive?.Entries.FirstOrDefault(item =>
                string.Equals(item.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length > 4 * 1024 * 1024)
                continue;
            using Stream stream = entry.Open();
            using MemoryStream content = new((int)entry.Length);
            stream.CopyTo(content);
            return Raster.Decode(content.ToArray());
        }
        return null;
    }

    private static Raster FitControllerTexture(Raster replacement, Raster original)
    {
        Int32Rect from = ControllerBounds(replacement);
        Int32Rect to = ControllerBounds(original);
        if (from.IsEmpty || to.IsEmpty)
            return replacement;
        if (replacement.Width == original.Width && replacement.Height == original.Height && from == to)
            return replacement;

        Raster canvas = Raster.Blank(original.Width, original.Height);
        Raster icon = replacement.Crop(from.X, from.Y, from.Width, from.Height).ResizeToPad(to.Width, to.Height, RasterFilter.Lanczos3);
        canvas.DrawOver(icon, to.X, to.Y);
        return canvas;
    }

    private static Int32Rect ControllerBounds(Raster image)
    {
        int left = image.Width, top = image.Height, right = -1, bottom = -1;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                if (image.Pixels[(y * image.Width + x) * 4 + 3] == 0)
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = y;
            }
        }
        return right >= left ? new Int32Rect(left, top, right - left + 1, bottom - top + 1) : Int32Rect.Empty;
    }
}
