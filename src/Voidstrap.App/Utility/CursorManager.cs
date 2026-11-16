using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using ICSharpCode.SharpZipLib.Zip;
using Voidstrap.Enums;
using Voidstrap.Models.SettingTasks;

namespace Voidstrap.Utility;

public enum CursorSlot
{
    Arrow,
    ArrowFar,
    Text,
    Drag,
    ShiftLock,
}

public enum CursorOverrideKind
{
    Normal,
    Hover,
    Click,
}

internal static class CursorManager
{
    private const long MaximumImageBytes = 32L * 1024 * 1024;

    private const int MaximumImageDimension = 4096;

    private const int MaximumSetNameLength = 64;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static readonly CursorSlot[] PointerSlots = { CursorSlot.Arrow, CursorSlot.ArrowFar, CursorSlot.Text, CursorSlot.Drag };

    internal static readonly CursorSlot[] OneImageSlots = { CursorSlot.Arrow, CursorSlot.ArrowFar, CursorSlot.Text };

    private static readonly Lazy<Dictionary<string, List<byte[]>>> PresetHashes = new Lazy<Dictionary<string, List<byte[]>>>(
        BuildPresetHashes
    );

    internal static string SetsFolder => Paths.CustomCursors;

    private static string WorkingFolder => Path.Combine(Paths.Themes, "CursorCustom");

    private static string TexturesFolder => Path.Combine(Paths.Mods, "content", "textures");

    private static CursorType CurrentStyle => App.Settings.Prop.CursorType;

    internal static readonly char[] anyOf = new[] { '/', '\\' };

    internal static string PreviewPath(CursorSlot slot)
    {
        return slot == CursorSlot.ShiftLock ? ModsPath(slot) : WorkingPath(slot);
    }

    internal static string? SetPreviewPath(string? name, CursorSlot slot)
    {
        if (!IsSafeSetName(name))
            return null;
        return Path.Combine(SetsFolder, name!, RelativePath(slot));
    }

    internal static void EnsureInitialized()
    {
        VoidstrapDefaultCursor.EnsureSelection();
        if (Directory.Exists(WorkingFolder))
            return;
        try
        {
            Directory.CreateDirectory(WorkingFolder);
            bool rescued = false;
            foreach (CursorSlot slot in PointerSlots)
            {
                string? existing = FindExistingModsFile(slot);
                if (existing == null || MatchesPreset(slot, existing))
                    continue;
                Filesystem.CopyWritableFile(existing, WorkingPath(slot));
                rescued = true;
            }
            if (rescued && CurrentStyle is CursorType.Default or CursorType.VoidstrapDefault)
                SaveStyle(CursorType.Custom);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("CursorManager", "Could not prepare the custom cursor folder: " + ex.Message);
        }
    }

    internal static void ApplyOnLaunch()
    {
        if (OverrideActive)
        {
            ApplyInvisibleCursor();
            return;
        }
        VoidstrapDefaultCursor.Apply();
        if (CurrentStyle != CursorType.Custom)
            return;
        foreach (CursorSlot slot in PointerSlots)
        {
            try
            {
                string source = WorkingPath(slot);
                string destination = ModsPath(slot);
                bool upToDate = File.Exists(source)
                    ? File.Exists(destination) && FilesEqual(source, destination)
                    : !File.Exists(destination);
                if (!upToDate)
                    CopyWorkingToMods(slot);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("CursorManager", "Could not apply the custom " + slot + " cursor: " + ex.Message);
            }
        }
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> GetPresetAssetHashes()
    {
        return PresetHashes.Value.ToDictionary(
            pair => pair.Key.Replace('\\', '/'),
            pair => (IReadOnlyList<string>)pair.Value.Select(hash => Convert.ToHexString(hash)).ToArray(),
            StringComparer.Ordinal
        );
    }

    internal static bool OverrideActive => Platform.IsWindows && App.Settings.Prop.RobloxCursorOverride;

    private static string OverrideFolder => Path.Combine(Paths.Themes, "CursorOverride");

    internal const byte OverrideMarkerAlpha = 3;

    internal static string? OverrideImagePath(CursorOverrideKind kind)
    {
        try
        {
            return Directory.Exists(OverrideFolder) ? Directory.EnumerateFiles(OverrideFolder, OverrideFileName(kind) + ".*").FirstOrDefault() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string OverrideFileName(CursorOverrideKind kind)
    {
        return kind switch
        {
            CursorOverrideKind.Hover => "hover",
            CursorOverrideKind.Click => "click",
            _ => "cursor",
        };
    }

    internal static void SetOverrideEnabled(bool enabled)
    {
        App.Settings.Prop.RobloxCursorOverride = enabled;
        App.Settings.SaveDeferred();
        if (enabled)
            ApplyInvisibleCursor();
        else
            ApplyStyle(CurrentStyle);
        App.Logger.WriteLine(
            "CursorManager",
            enabled ? "Roblox cursor override enabled, the Roblox cursor is now invisible" : "Roblox cursor override disabled, restored the " + CurrentStyle + " cursor"
        );
    }

    internal static void SetOverrideImage(CursorOverrideKind kind, string sourcePath)
    {
        if (new FileInfo(sourcePath).Length > MaximumImageBytes)
            throw new InvalidDataException("The image is larger than 32 MB");
        if (AnimatedIcon.Load(sourcePath, 64).Count == 0)
            throw new InvalidDataException("The image could not be read");
        ClearOverrideImage(kind);
        Directory.CreateDirectory(OverrideFolder);
        Filesystem.CopyWritableFile(sourcePath, Path.Combine(OverrideFolder, OverrideFileName(kind) + Path.GetExtension(sourcePath).ToLowerInvariant()));
    }

    internal static void ClearOverrideImage(CursorOverrideKind kind)
    {
        if (!Directory.Exists(OverrideFolder))
            return;
        foreach (string file in Directory.EnumerateFiles(OverrideFolder, OverrideFileName(kind) + ".*").ToList())
            Filesystem.DeleteWritableFile(file);
    }

    internal static (IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> Frames, double HotspotX, double HotspotY, int Width) LoadOverrideCursor(CursorOverrideKind kind)
    {
        int size = Math.Clamp(App.Settings.Prop.RobloxCursorOverrideSize, 8, 256);
        string? path = OverrideImagePath(kind);
        if (path != null)
        {
            IReadOnlyList<(BitmapSource Frame, int DelayMilliseconds)> frames = AnimatedIcon.Load(path, size);
            if (frames.Count > 0)
            {
                (double X, double Y)? hotspot = AnimatedIcon.ReadHotspot(path);
                double fallback = App.Settings.Prop.RobloxCursorOverrideCentered ? 0.5 : 0;
                return (frames, hotspot?.X ?? fallback, hotspot?.Y ?? fallback, size);
            }
        }
        if (kind != CursorOverrideKind.Normal)
            return ([], 0, 0, size);
        int textureSize = size * 2;
        BitmapSource? texture = LoadStyleTexture(textureSize);
        return texture == null ? ([], 0, 0, size) : ([(texture, 0)], 0.5, 0.5, textureSize);
    }

    private static BitmapSource? LoadStyleTexture(int size)
    {
        if (CurrentStyle == CursorType.Custom && File.Exists(WorkingPath(CursorSlot.Arrow)))
            return SafeImaging.FromFile(WorkingPath(CursorSlot.Arrow), size);
        Dictionary<CursorType, Dictionary<string, string>> map = CursorPresetTask.CreateMap();
        CursorType style = map.ContainsKey(CurrentStyle) ? CurrentStyle : CursorType.VoidstrapDefault;
        string arrow = map[style].First(static pair => pair.Key.EndsWith("ArrowCursor.png", StringComparison.OrdinalIgnoreCase)).Value;
        return SafeImaging.FromBytes(Resource.Get(arrow), size);
    }

    private static void ApplyInvisibleCursor()
    {
        foreach (CursorSlot slot in PointerSlots)
        {
            try
            {
                byte[] png = CreateInvisiblePng(OverrideMarker(slot));
                string destination = ModsPath(slot);
                if (File.Exists(destination) && File.ReadAllBytes(destination).AsSpan().SequenceEqual(png))
                    continue;
                WriteFile(destination, png);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                App.Logger.WriteLine("CursorManager", "Could not hide the Roblox " + slot + " cursor: " + ex.Message);
            }
        }
    }

    internal static int OverrideMarker(CursorSlot slot)
    {
        return slot switch
        {
            CursorSlot.Arrow => 1,
            CursorSlot.ArrowFar => 2,
            CursorSlot.Text => 3,
            _ => 0,
        };
    }

    private static byte[] CreateInvisiblePng(int marker)
    {
        const int Size = 64;
        const int Half = Size / 2;
        byte[] pixels = new byte[Size * Size * 4];
        int markerLeft = marker == 3 ? Half : 0;
        int markerTop = marker == 1 ? 0 : Half;
        if (marker != 0)
        {
            for (int y = markerTop; y < markerTop + Half; y++)
            {
                for (int x = markerLeft; x < markerLeft + Half; x++)
                    pixels[(y * Size + x) * 4 + 3] = OverrideMarkerAlpha;
            }
        }
        BitmapSource bitmap = BitmapSource.Create(Size, Size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, Size * 4);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    internal static void ApplyStyle(CursorType style)
    {
        foreach (CursorSlot slot in PointerSlots)
            Filesystem.DeleteWritableFile(ModsPath(slot));
        if (style == CursorType.Custom)
        {
            foreach (CursorSlot slot in PointerSlots)
                CopyWorkingToMods(slot);
        }
        else if (CursorPresetTask.CreateMap().TryGetValue(style, out Dictionary<string, string>? files))
        {
            foreach ((string relativePath, string resourceName) in files)
            {
                string destination = Path.Combine(Paths.Mods, relativePath.Replace('\\', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Filesystem.AssertReadOnly(destination);
                File.WriteAllBytes(destination, Resource.Get(resourceName));
            }
        }
        SaveStyle(style);
    }

    internal static void SetImage(CursorSlot slot, string sourcePath)
    {
        byte[] png = EncodePng(sourcePath);
        if (slot == CursorSlot.ShiftLock)
        {
            WriteFile(ModsPath(slot), png);
            return;
        }
        WriteFile(WorkingPath(slot), png);
        if (CurrentStyle == CursorType.Custom)
            CopyWorkingToMods(slot);
    }

    internal static void ClearImage(CursorSlot slot)
    {
        if (slot == CursorSlot.ShiftLock)
        {
            Filesystem.DeleteWritableFile(ModsPath(slot));
            return;
        }
        Filesystem.DeleteWritableFile(WorkingPath(slot));
        if (CurrentStyle == CursorType.Custom)
            CopyWorkingToMods(slot);
    }

    internal static List<string> ListSets()
    {
        try
        {
            Directory.CreateDirectory(SetsFolder);
            return Directory
                .GetDirectories(SetsFolder)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!)
                .OrderBy(static name => name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("CursorManager", "Could not list cursor sets: " + ex.Message);
            return new List<string>();
        }
    }

    internal static string SuggestSetName()
    {
        return UniqueSetName("Custom Cursor Set");
    }

    internal static string CreateSet(string requestedName)
    {
        string name = ValidateSetName(requestedName);
        string folder = Path.Combine(SetsFolder, name);
        if (Directory.Exists(folder))
            throw new IOException("A cursor set named " + name + " already exists.");
        Directory.CreateDirectory(folder);
        return name;
    }

    internal static void SetSetImage(string name, CursorSlot slot, string sourcePath)
    {
        string folder = ExistingSetFolder(name);
        WriteFile(Path.Combine(folder, RelativePath(slot)), EncodePng(sourcePath));
    }

    internal static void ClearSetImage(string name, CursorSlot slot)
    {
        string folder = ExistingSetFolder(name);
        Filesystem.DeleteWritableFile(Path.Combine(folder, RelativePath(slot)));
    }

    internal static void CopyCurrentToSet(string name)
    {
        string folder = ExistingSetFolder(name);
        foreach (CursorSlot slot in PointerSlots)
            CopyOrDelete(WorkingPath(slot), Path.Combine(folder, RelativePath(slot)));
        CopyOrDelete(ModsPath(CursorSlot.ShiftLock), Path.Combine(folder, RelativePath(CursorSlot.ShiftLock)));
    }

    internal static void UseSet(string name)
    {
        string folder = ExistingSetFolder(name);
        foreach (CursorSlot slot in PointerSlots)
            CopyOrDelete(Path.Combine(folder, RelativePath(slot)), WorkingPath(slot));
        string shiftLock = Path.Combine(folder, RelativePath(CursorSlot.ShiftLock));
        if (File.Exists(shiftLock))
            Filesystem.CopyWritableFile(shiftLock, ModsPath(CursorSlot.ShiftLock));
        ApplyStyle(CursorType.Custom);
    }

    internal static string RenameSet(string oldName, string requestedName)
    {
        string folder = ExistingSetFolder(oldName);
        string name = ValidateSetName(requestedName);
        if (string.Equals(name, oldName, StringComparison.Ordinal))
            return name;
        string destination = Path.Combine(SetsFolder, name);
        if (string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = Path.Combine(SetsFolder, name + "." + Guid.NewGuid().ToString("N"));
            Directory.Move(folder, temporary);
            Directory.Move(temporary, destination);
            return name;
        }
        if (Directory.Exists(destination))
            throw new IOException("A cursor set named " + name + " already exists.");
        Directory.Move(folder, destination);
        return name;
    }

    internal static void DeleteSet(string name)
    {
        string folder = ExistingSetFolder(name);
        Filesystem.AssertReadOnlyDirectory(folder);
        Directory.Delete(folder, true);
    }

    internal static void ExportSet(string name, string zipPath)
    {
        string folder = ExistingSetFolder(name);
        using FileStream output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using ZipOutputStream zip = new ZipOutputStream(output);
        foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            ZipEntry entry = new ZipEntry(Path.GetRelativePath(folder, file).Replace('\\', '/'))
            {
                DateTime = File.GetLastWriteTime(file),
                Size = new FileInfo(file).Length,
            };
            zip.PutNextEntry(entry);
            using (FileStream input = File.OpenRead(file))
                input.CopyTo(zip);
            zip.CloseEntry();
        }
        zip.Finish();
    }

    internal static string ImportSet(string zipPath)
    {
        string name = UniqueSetName(SanitizeName(Path.GetFileNameWithoutExtension(zipPath)));
        string folder = Path.Combine(SetsFolder, name);
        string temporary = Path.Combine(Path.GetTempPath(), "VoidstrapCursorImport" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            SafeZipExtractor.ExtractToDirectory(zipPath, temporary, true, 64L * 1024 * 1024, 256);
            int copied = 0;
            foreach (CursorSlot slot in Enum.GetValues<CursorSlot>())
            {
                string fileName = Path.GetFileName(RelativePath(slot));
                string? source = Directory
                    .EnumerateFiles(
                        temporary,
                        fileName,
                        new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }
                    )
                    .FirstOrDefault();
                if (source == null)
                    continue;
                WriteFile(Path.Combine(folder, RelativePath(slot)), EncodePng(source));
                copied++;
            }
            if (copied == 0)
                throw new InvalidDataException("That zip does not contain any cursor images.");
            return name;
        }
        catch
        {
            TryDeleteFolder(folder);
            throw;
        }
        finally
        {
            TryDeleteFolder(temporary);
        }
    }

    private static string RelativePath(CursorSlot slot)
    {
        return slot switch
        {
            CursorSlot.Arrow => Path.Combine("Cursors", "KeyboardMouse", "ArrowCursor.png"),
            CursorSlot.ArrowFar => Path.Combine("Cursors", "KeyboardMouse", "ArrowFarCursor.png"),
            CursorSlot.Text => Path.Combine("Cursors", "KeyboardMouse", "IBeamCursor.png"),
            CursorSlot.Drag => Path.Combine("Cursors", "KeyboardMouse", "ArrowCursorDecalDrag.png"),
            _ => "MouseLockedCursor.png",
        };
    }

    private static string ModsPath(CursorSlot slot)
    {
        return Path.Combine(TexturesFolder, RelativePath(slot));
    }

    private static string WorkingPath(CursorSlot slot)
    {
        return Path.Combine(WorkingFolder, RelativePath(slot));
    }

    private static string? FindExistingModsFile(CursorSlot slot)
    {
        string current = ModsPath(slot);
        if (File.Exists(current))
            return current;
        string legacy = Path.Combine(Paths.Mods, "Content", "textures", RelativePath(slot));
        return File.Exists(legacy) ? legacy : null;
    }

    private static void CopyWorkingToMods(CursorSlot slot)
    {
        CopyOrDelete(WorkingPath(slot), ModsPath(slot));
    }

    private static void CopyOrDelete(string source, string destination)
    {
        if (File.Exists(source))
            Filesystem.CopyWritableFile(source, destination);
        else
            Filesystem.DeleteWritableFile(destination);
    }

    private static void SaveStyle(CursorType style)
    {
        App.Settings.Prop.CursorType = style;
        App.Settings.Prop.HasSelectedCursorType = true;
        App.Settings.SaveDeferred();
    }

    private static void WriteFile(string destination, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        Filesystem.AssertReadOnly(destination);
        File.WriteAllBytes(destination, bytes);
    }

    private static byte[] EncodePng(string path)
    {
        FileInfo file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > MaximumImageBytes)
            throw new InvalidDataException("Choose an image smaller than 32 MB.");
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(PngSignature))
            return bytes;
        try
        {
            return EncodeWithRaster(bytes);
        }
        catch (NotSupportedException) when (OperatingSystem.IsWindows()) { }
        catch (NotSupportedException)
        {
            throw new InvalidDataException("That image format is not supported.");
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("That image could not be read.", ex);
        }
        try
        {
            return EncodeWithWindows(bytes);
        }
        catch (Exception ex) when (ex is not InvalidDataException)
        {
            throw new InvalidDataException("That image format is not supported.", ex);
        }
    }

    private static byte[] EncodeWithRaster(byte[] bytes)
    {
        if (!Raster.TryIdentify(bytes, out int width, out int height))
            throw new NotSupportedException("That image format is not supported.");
        CheckDimensions(width, height);
        return Raster.Decode(bytes).EncodePng();
    }

    private static byte[] EncodeWithWindows(byte[] bytes)
    {
        using MemoryStream input = new MemoryStream(bytes, false);
        BitmapDecoder decoder = BitmapDecoder.Create(
            input,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None
        );
        BitmapFrame frame = decoder
            .Frames.OrderByDescending(static candidate => (long)candidate.PixelWidth * candidate.PixelHeight)
            .First();
        CheckDimensions(frame.PixelWidth, frame.PixelHeight);
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using MemoryStream output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaximumImageDimension || height > MaximumImageDimension)
            throw new InvalidDataException("Use an image that is 4096 pixels or smaller on each side.");
    }

    private static bool FilesEqual(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length)
            return false;
        return File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second));
    }

    private static Dictionary<string, List<byte[]>> BuildPresetHashes()
    {
        Dictionary<string, List<byte[]>> hashes = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, string> files in CursorPresetTask.CreateMap().Values)
        {
            foreach ((string relativePath, string resourceName) in files)
            {
                try
                {
                    string key = relativePath.Replace('\\', Path.DirectorySeparatorChar);
                    if (!hashes.TryGetValue(key, out List<byte[]>? list))
                    {
                        list = new List<byte[]>();
                        hashes[key] = list;
                    }
                    list.Add(SHA256.HashData(Resource.Get(resourceName)));
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("CursorManager", "Could not read the built in cursor " + resourceName + ": " + ex.Message);
                }
            }
        }
        return hashes;
    }

    private static bool MatchesPreset(CursorSlot slot, string path)
    {
        string key = Path.Combine("content", "textures", RelativePath(slot));
        if (!PresetHashes.Value.TryGetValue(key, out List<byte[]>? hashes))
            return false;
        try
        {
            byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
            return hashes.Exists(candidate => candidate.AsSpan().SequenceEqual(hash));
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("CursorManager", "Could not read " + path + ": " + ex.Message);
            return false;
        }
    }

    private static string ValidateSetName(string? requested)
    {
        string name = (requested ?? string.Empty).Trim().TrimEnd('.').Trim();
        if (name.Length == 0)
            throw new ArgumentException("Enter a name for the cursor set.");
        if (name.Length > MaximumSetNameLength)
            throw new ArgumentException("Use a name with 64 characters or fewer.");
        if (
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || PathValidator.IsFileNameValid(name) != PathValidator.ValidationResult.Ok
        )
            throw new ArgumentException("That name cannot be used for a folder. Try a different name.");
        return name;
    }

    private static string SanitizeName(string raw)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(raw.Select(character => Array.IndexOf(invalid, character) >= 0 ? ' ' : character).ToArray())
            .Trim()
            .TrimEnd('.')
            .Trim();
        if (cleaned.Length > MaximumSetNameLength)
            cleaned = cleaned[..MaximumSetNameLength].Trim();
        if (cleaned.Length == 0 || PathValidator.IsFileNameValid(cleaned) != PathValidator.ValidationResult.Ok)
            cleaned = "Imported cursor set";
        return cleaned;
    }

    private static bool IsSafeSetName(string? name)
    {
        return !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." && name.IndexOfAny(anyOf) < 0;
    }

    private static string ExistingSetFolder(string name)
    {
        if (!IsSafeSetName(name))
            throw new ArgumentException("That cursor set name is not valid.");
        string folder = Path.Combine(SetsFolder, name);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException("That cursor set no longer exists.");
        return folder;
    }

    private static string UniqueSetName(string baseName)
    {
        string name = baseName;
        for (int index = 2; Directory.Exists(Path.Combine(SetsFolder, name)); index++)
            name = baseName + " " + index;
        return name;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            Filesystem.AssertReadOnlyDirectory(folder);
            Directory.Delete(folder, true);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("CursorManager", "Could not remove " + folder + ": " + ex.Message);
        }
    }
}
