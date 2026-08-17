using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using NVorbis;
using OggVorbisEncoder;
using Voidstrap.AppData;
using Voidstrap.Models.Manifest;

namespace Voidstrap;

public static partial class RobloxCompressor
{
    private const string ManifestFileName = "VoidstrapCompressed.json";
    private const long MaxPngOptimizationBytes = 32L * 1024L * 1024L;

    private sealed class CompressedEntry
    {
        public string Path { get; set; } = "";
        public long OriginalSize { get; set; }
        public long CompressedSize { get; set; }
        public int CompressionLevel { get; set; }
        public string SourcePackage { get; set; } = "";
    }

    private sealed class ManifestData
    {
        public List<CompressedEntry> Entries { get; set; } = [];
    }

    private static readonly HashSet<string> _imageExtensions = [".png"];
    private static readonly HashSet<string> _textExtensions = [".lua"];
    private static readonly HashSet<string> _audioExtensions = [".ogg"];
    private static readonly HashSet<string> _skipDirectories = ["PlatformContent", "shaders", "ssl", "Plugins", "ClientSettings", "content\\textures\\Cursors"];

    public static void Run(string versionDir, int level,
        IEnumerable<Package> packages,
        IReadOnlyDictionary<string, string> packageDirectoryMap)
    {
        App.Logger.WriteLine("RobloxCompressor::Run", $"Starting compression at level {level}");

        var packageList = packages.ToList();
        var manifest = LoadManifest(versionDir);

        RestoreCorruptedFiles(versionDir, manifest, packageList, packageDirectoryMap);
        RestoreFromPackages(versionDir, manifest, level, packageList, packageDirectoryMap);

        var eligibleFiles = GetEligibleFiles(versionDir);
        foreach (string filePath in eligibleFiles)
        {
            string relativePath = Path.GetRelativePath(versionDir, filePath);
            var existing = manifest.Entries.Find(e => e.Path == relativePath);
            if (existing != null && existing.CompressionLevel <= level)
                continue;

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length == 0) continue;

            long originalSize = fileInfo.Length;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            bool success = false;
            if (_imageExtensions.Contains(ext))
                success = CompressImage(filePath, level);
            else if (_textExtensions.Contains(ext))
                success = MinifyText(filePath, level);
            else if (_audioExtensions.Contains(ext))
                success = CompressOgg(filePath, level);

            if (!success) continue;

            long compressedSize = new FileInfo(filePath).Length;
            if (compressedSize >= originalSize) continue;

            string sourcePackage = ResolveSourcePackage(relativePath, packageDirectoryMap);

            manifest.Entries.RemoveAll(e => e.Path == relativePath);
            manifest.Entries.Add(new CompressedEntry
            {
                Path = relativePath,
                OriginalSize = originalSize,
                CompressedSize = compressedSize,
                CompressionLevel = level,
                SourcePackage = sourcePackage
            });
        }

        SaveManifest(versionDir, manifest);

        CompressRbxStorage(level);
    }

    public static void CompressRbxStorage(int level)
    {
        const string LOG_IDENT = "RobloxCompressor::CompressRbxStorage";
        if (level <= 40) return;

        string cacheDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox", "rbx-storage");

        if (!Directory.Exists(cacheDir))
        {
            App.Logger.WriteLine(LOG_IDENT, $"Cache directory not found: {cacheDir}");
            return;
        }

        int compressed = 0;
        int failed = 0;

        foreach (string file in Directory.EnumerateFiles(cacheDir, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            if (name.Length != 32 || name.Any(c => !Uri.IsHexDigit(c))) continue;

            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read);
                byte[] header = new byte[4];
                if (fs.Read(header, 0, 4) < 4) continue;
                bool isOgg = header[0] == 0x4F && header[1] == 0x67 &&
                             header[2] == 0x67 && header[3] == 0x53;
                if (!isOgg) continue;
            }
            catch { continue; }

            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }

            if (CompressOgg(file, level))
            {
                compressed++;
                App.Logger.WriteLine(LOG_IDENT, $"Compressed cache {name}");
            }
            else
            {
                failed++;
            }
        }

        App.Logger.WriteLine(LOG_IDENT, $"Done: {compressed} compressed, {failed} failed");
    }

    public static async Task RepairCorruptedAsync()
    {
        const string LOG_IDENT = "RobloxCompressor::RepairCorruptedAsync";
        string versionsDir = Paths.Versions;
        App.Logger.WriteLine(LOG_IDENT, $"Scanning {versionsDir} (exists={Directory.Exists(versionsDir)})");
        if (!Directory.Exists(versionsDir)) return;

        var mergedMap = new Dictionary<string, string>();
        foreach (var kv in new RobloxPlayerData().PackageDirectoryMap)
            mergedMap[kv.Key] = kv.Value;
        foreach (var kv in new RobloxStudioData().PackageDirectoryMap)
            mergedMap[kv.Key] = kv.Value;

        foreach (string dir in Directory.GetDirectories(versionsDir))
        {
            string versionGuid = Path.GetFileName(dir);
            string manifestPath = System.IO.Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                App.Logger.WriteLine(LOG_IDENT, $"  Dir {versionGuid}: no manifest found");
                continue;
            }

            var manifest = LoadManifest(dir);
            var allCorrupted = CollectCorruptedFiles(dir, manifest, mergedMap);
            App.Logger.WriteLine(LOG_IDENT, $"  Dir {versionGuid}: manifest entries={manifest.Entries.Count}, corrupt={allCorrupted.Count}");
            if (allCorrupted.Count == 0) continue;

            var manifestCorrupted = allCorrupted
                .Where(e => manifest.Entries.Any(m => m.Path == e.Path))
                .ToList();

            try
            {
                string manifestText = null;
                foreach (string pkgManifestUrl in Voidstrap.RobloxInterfaces.Deployment.GetLocations($"/{versionGuid}-rbxPkgManifest.txt"))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"  Fetching {pkgManifestUrl}");
                    try
                    {
                        using var resp = await App.HttpClient.GetAsync(pkgManifestUrl).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            App.Logger.WriteLine(LOG_IDENT, $"  CDN returned {(int)resp.StatusCode} {resp.StatusCode}, trying next mirror");
                            continue;
                        }
                        manifestText = await Voidstrap.Utility.Http.ReadStringBoundedAsync(resp.Content, 8388608, CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    catch (System.Net.Http.HttpRequestException ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"  CDN fetch failed: {ex.Message}");
                    }
                }
                if (manifestText == null)
                {
                    foreach (var entry in manifestCorrupted)
                        manifest.Entries.Remove(entry);
                    SaveManifest(dir, manifest);
                    continue;
                }
                var packages = new PackageManifest(manifestText);
                App.Logger.WriteLine(LOG_IDENT, $"  CDN manifest: {packages.Count} packages");

                RestoreFilesFromPackages(dir, allCorrupted, packages, mergedMap);
                App.Logger.WriteLine(LOG_IDENT, $"  Restored {allCorrupted.Count} files from packages");

                foreach (var entry in manifestCorrupted)
                    manifest.Entries.Remove(entry);

                SaveManifest(dir, manifest);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"  Repair failed: {ex.Message}");
                foreach (var entry in manifestCorrupted)
                    manifest.Entries.Remove(entry);
                SaveManifest(dir, manifest);
            }
        }
    }

    private static bool IsCorrupted(string filePath, long fileSize)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".png" && fileSize < 67)
            return true;
        if (ext == ".ogg" && fileSize < 100)
            return true;
        return false;
    }

    private static List<CompressedEntry> CollectCorruptedFiles(string versionDir, ManifestData manifest,
        IReadOnlyDictionary<string, string> packageDirectoryMap)
    {
        const string LOG_IDENT = "RobloxCompressor::CollectCorruptedFiles";
        var result = new List<CompressedEntry>();
        int manifestCorrupt = 0;

        foreach (var entry in manifest.Entries)
        {
            string fullPath = System.IO.Path.Combine(versionDir, entry.Path);
            if (!File.Exists(fullPath)) continue;
            long actualSize = new FileInfo(fullPath).Length;
            bool sizeMismatch = actualSize != entry.CompressedSize;
            bool corrupt = IsCorrupted(fullPath, actualSize);
            if (sizeMismatch || corrupt)
            {
                App.Logger.WriteLine(LOG_IDENT, $"  Manifest entry corrupt: {entry.Path} (expected={entry.CompressedSize}, actual={actualSize}, sizeMismatch={sizeMismatch}, heuristic={corrupt})");
                result.Add(entry);
                manifestCorrupt++;
            }
        }

        var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Entries)
            manifestPaths.Add(entry.Path);

        int unmanagedCorrupt = 0;
        foreach (string filePath in GetEligibleFiles(versionDir))
        {
            string relativePath = Path.GetRelativePath(versionDir, filePath);
            if (manifestPaths.Contains(relativePath)) continue;

            long fileSize = new FileInfo(filePath).Length;
            if (!IsCorrupted(filePath, fileSize)) continue;

            App.Logger.WriteLine(LOG_IDENT, $"  Unmanaged file corrupt: {relativePath} (size={fileSize})");
            result.Add(new CompressedEntry
            {
                Path = relativePath,
                SourcePackage = ResolveSourcePackage(relativePath, packageDirectoryMap)
            });
            unmanagedCorrupt++;
        }

        App.Logger.WriteLine(LOG_IDENT, $"Total: {result.Count} corrupt (manifest={manifestCorrupt}, unmanaged={unmanagedCorrupt})");
        return result;
    }

    private static void RestoreCorruptedFiles(string versionDir, ManifestData manifest,
        List<Package> packages, IReadOnlyDictionary<string, string> packageDirectoryMap)
    {
        const string LOG_IDENT = "RobloxCompressor::RestoreCorruptedFiles";
        var allCorrupted = CollectCorruptedFiles(versionDir, manifest, packageDirectoryMap);
        var manifestCorrupted = allCorrupted.Where(e => manifest.Entries.Any(m => m.Path == e.Path)).ToList();

        if (allCorrupted.Count == 0) return;

        App.Logger.WriteLine(LOG_IDENT, $"Restoring {allCorrupted.Count} files from packages ({manifestCorrupted.Count} from manifest)");
        RestoreFilesFromPackages(versionDir, allCorrupted, packages, packageDirectoryMap);
        App.Logger.WriteLine(LOG_IDENT, $"Restoration complete");

        foreach (var entry in manifestCorrupted)
            manifest.Entries.Remove(entry);
    }

    private static void RestoreFromPackages(string versionDir, ManifestData manifest, int level,
        List<Package> packages, IReadOnlyDictionary<string, string> packageDirectoryMap)
    {
        var toRestore = manifest.Entries.Where(e => e.CompressionLevel > level).ToList();
        if (toRestore.Count == 0) return;

        RestoreFilesFromPackages(versionDir, toRestore, packages, packageDirectoryMap);

        manifest.Entries.RemoveAll(e => e.CompressionLevel > level);
    }

    private static void RestoreFilesFromPackages(string versionDir, List<CompressedEntry> entries,
        List<Package> packages, IReadOnlyDictionary<string, string> packageDirectoryMap)
    {
        var restoreMap = new Dictionary<string, List<string>>();
        foreach (var entry in entries)
        {
            string fullPath = System.IO.Path.Combine(versionDir, entry.Path);
            if (!File.Exists(fullPath)) continue;

            if (string.IsNullOrEmpty(entry.SourcePackage)) continue;

            string? pkgDir = packageDirectoryMap.GetValueOrDefault(entry.SourcePackage);
            if (string.IsNullOrEmpty(pkgDir)) continue;

            if (!restoreMap.ContainsKey(entry.SourcePackage))
                restoreMap[entry.SourcePackage] = [];

            string relativeInPackage = entry.Path.Substring(pkgDir.Length).TrimStart('\\', '/');
            restoreMap[entry.SourcePackage].Add(relativeInPackage);
        }

        foreach (var (pkgName, files) in restoreMap)
        {
            var pkg = packages.Find(x => x.Name == pkgName);
            if (pkg == null) continue;

            string? pkgDir = packageDirectoryMap.GetValueOrDefault(pkgName);
            if (string.IsNullOrEmpty(pkgDir)) continue;

            new FastZip().ExtractZip(
                pkg.DownloadPath,
                System.IO.Path.Combine(versionDir, pkgDir),
                string.Join(";", files.Select(f => "^" + Regex.Escape(f) + "$")));
        }
    }

    public static byte[]? CompressData(byte[] data, string contentType, int level = 80)
    {
        if (data is null || data.Length == 0)
            return null;

        string ext;
        if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ||
            contentType.Contains("image", StringComparison.OrdinalIgnoreCase))
            ext = ".png";
        else if (contentType.Contains("ogg", StringComparison.OrdinalIgnoreCase) ||
                 contentType.Contains("audio", StringComparison.OrdinalIgnoreCase))
            ext = ".ogg";
        else
            return null;

        string tempPath = Path.GetTempFileName() + ext;
        try
        {
            File.WriteAllBytes(tempPath, data);
            long originalSize = data.Length;

            bool success = ext == ".png" ? CompressPng(tempPath, level) : CompressOgg(tempPath, level);
            if (!success)
                return null;

            byte[] result = File.ReadAllBytes(tempPath);
            return result.Length < originalSize ? result : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static bool CompressImage(string filePath, int level)
    {
        try
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".png")
                return CompressPng(filePath, level);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool CompressPng(string filePath, int level)
    {
        try
        {
            var tempPath = filePath + ".tmp";

            System.Windows.Media.Imaging.BitmapFrame frame;
            using (var input = File.OpenRead(filePath))
            {
                var decoder = new System.Windows.Media.Imaging.PngBitmapDecoder(
                    input,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0) return false;
                frame = decoder.Frames[0];
            }

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder
            {
                Interlace = System.Windows.Media.Imaging.PngInterlaceOption.Off
            };
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));

            using (var output = File.Create(tempPath))
                encoder.Save(output);

            OptimizePngDeflate(tempPath);

            long originalSize = new FileInfo(filePath).Length;
            long newSize = new FileInfo(tempPath).Length;

            if (newSize > 0 && newSize < originalSize)
            {
                File.Delete(filePath);
                File.Move(tempPath, filePath);
                return true;
            }

            File.Delete(tempPath);
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void OptimizePngDeflate(string filePath)
    {
        try
        {
            if (new FileInfo(filePath).Length > MaxPngOptimizationBytes)
                return;
        }
        catch
        {
            return;
        }
        byte[] data = File.ReadAllBytes(filePath);
        if (data.Length < 8) return;

        using var output = new MemoryStream();
        output.Write(data, 0, 8);

        int i = 8;
        while (i + 8 <= data.Length)
        {
            int length = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
            if (length < 0 || i + 12 + length > data.Length) break;

            string type = System.Text.Encoding.ASCII.GetString(data, i + 4, 4);
            if (type is "IHDR" or "PLTE" or "tRNS" or "IDAT" or "IEND")
                output.Write(data, i, 12 + length);
            i += 12 + length;
        }

        if (output.Length < data.Length)
            File.WriteAllBytes(filePath, output.ToArray());
    }

    private static Bitmap ReducePalette(Bitmap original, int maxColors)
    {
        var reduced = new Bitmap(original.Width, original.Height, PixelFormat.Format8bppIndexed);
        using var graphics = Graphics.FromImage(reduced);
        var colorPalette = reduced.Palette;

        using var quantized = original.Clone(new Rectangle(0, 0, original.Width, original.Height), PixelFormat.Format32bppArgb);
        var colorQuantizer = new ColorQuantizer(maxColors);
        var palette = colorQuantizer.Quantize(quantized);

        for (int j = 0; j < palette.Length && j < 256; j++)
            colorPalette.Entries[j] = palette[j];

        reduced.Palette = colorPalette;
        graphics.DrawImage(original, 0, 0);
        return reduced;
    }

    private sealed class ColorQuantizer(int maxColors)
    {
        public Color[] Quantize(Bitmap bitmap)
        {
            var colors = new List<Color>();
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (!colors.Contains(pixel))
                        colors.Add(pixel);
                }
            }

            while (colors.Count > maxColors)
            {
                int closestDist = int.MaxValue;
                int closestA = 0, closestB = 0;

                for (int i = 0; i < colors.Count; i++)
                {
                    for (int j = i + 1; j < colors.Count; j++)
                    {
                        int dist = ColorDistance(colors[i], colors[j]);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closestA = i;
                            closestB = j;
                        }
                    }
                }

                colors[closestA] = AverageColors(colors[closestA], colors[closestB]);
                colors.RemoveAt(closestB);
            }

            return [.. colors];
        }

        private static int ColorDistance(Color a, Color b)
        {
            int dr = a.R - b.R;
            int dg = a.G - b.G;
            int db = a.B - b.B;
            int da = a.A - b.A;
            return dr * dr + dg * dg + db * db + da * da;
        }

        private static Color AverageColors(Color a, Color b)
        {
            return Color.FromArgb(
                (a.A + b.A) / 2,
                (a.R + b.R) / 2,
                (a.G + b.G) / 2,
                (a.B + b.B) / 2);
        }
    }

    private static bool MinifyText(string filePath, int level)
    {
        try
        {
            string text = File.ReadAllText(filePath);
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            string minified = ext switch
            {
                ".lua" => MinifyLua(text, level),
                ".txt" or ".csv" => level > 40 ? text.Trim() : text,
                _ => text
            };

            if (minified.Length >= text.Length) return false;

            File.WriteAllText(filePath, minified);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string MinifyLua(string lua, int level)
    {
        var result = new StringBuilder();
        var lines = lua.Split('\n');
        foreach (var line in lines)
        {
            string trimmed = line.TrimEnd();
            if (level > 40)
            {
                int commentIdx = trimmed.IndexOf("--");
                if (commentIdx >= 0)
                {
                    int quoteCheck = trimmed.IndexOf('"');
                    if (quoteCheck < 0 || commentIdx < quoteCheck)
                        trimmed = trimmed[..commentIdx].TrimEnd();
                }
            }
            if (!string.IsNullOrEmpty(trimmed))
                result.AppendLine(trimmed);
        }
        return result.ToString();
    }

    private static bool CompressOgg(string filePath, int level)
    {
        string tempPath = filePath + ".tmp";
        try
        {
            if (level <= 40) return false;

            using var vorbisReader = new VorbisReader(filePath);
            int channels = vorbisReader.Channels;
            int sampleRate = vorbisReader.SampleRate;
            long totalSamples = vorbisReader.TotalSamples;
            if (totalSamples <= 0 || channels < 1 || channels > 8 || sampleRate < 8000 || sampleRate > 384000)
                return false;

            int framesPerBuffer = Math.Min(sampleRate * 2, 65536);
            int readBufferSize = framesPerBuffer * channels;
            float[] readBuffer = new float[readBufferSize];
            float[][] channelData = new float[channels][];
            for (int ch = 0; ch < channels; ch++)
                channelData[ch] = new float[framesPerBuffer];

            float quality = level switch
            {
                <= 40 => 0f,
                <= 55 => 0.3f,
                <= 70 => 0.2f,
                <= 85 => 0.08f,
                _ => 0.01f
            };

            var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);
            var serial = Random.Shared.Next();
            var oggStream = new OggStream(serial);
            var comments = new Comments();

            using (var outputData = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                var infoPacket = HeaderPacketBuilder.BuildInfoPacket(info);
                var commentsPacket = HeaderPacketBuilder.BuildCommentsPacket(comments);
                var booksPacket = HeaderPacketBuilder.BuildBooksPacket(info);

                oggStream.PacketIn(infoPacket);
                oggStream.PacketIn(commentsPacket);
                oggStream.PacketIn(booksPacket);

                FlushOggPages(oggStream, outputData, true);

                var processingState = ProcessingState.Create(info);
                int readIndex;

                while ((readIndex = vorbisReader.ReadSamples(readBuffer, 0, readBufferSize)) > 0)
                {
                    int frameCount = readIndex / channels;
                    for (int ch = 0; ch < channels; ch++)
                    {
                        for (int i = 0; i < frameCount; i++)
                            channelData[ch][i] = readBuffer[i * channels + ch];
                    }

                    processingState.WriteData(channelData, frameCount, 0);

                    while (processingState.PacketOut(out OggPacket packet))
                        oggStream.PacketIn(packet);

                    FlushOggPages(oggStream, outputData, false);
                }

                processingState.WriteEndOfStream();
                while (processingState.PacketOut(out OggPacket packet))
                    oggStream.PacketIn(packet);

                FlushOggPages(oggStream, outputData, true);
            }

            long originalSize = new FileInfo(filePath).Length;
            long newSize = new FileInfo(tempPath).Length;
            if (newSize < originalSize)
            {
                File.Delete(filePath);
                File.Move(tempPath, filePath);
                return true;
            }
            else
            {
                File.Delete(tempPath);
                return false;
            }
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            return false;
        }
    }

    private static void FlushOggPages(OggStream oggStream, Stream output, bool force)
    {
        while (oggStream.PageOut(out OggPage page, force))
        {
            output.Write(page.Header, 0, page.Header.Length);
            output.Write(page.Body, 0, page.Body.Length);
        }
    }

    private static ManifestData LoadManifest(string versionDir)
    {
        string path = System.IO.Path.Combine(versionDir, ManifestFileName);
        if (!File.Exists(path)) return new ManifestData();
        try
        {
            return Voidstrap.Utility.JsonFile.Deserialize<ManifestData>(path, Voidstrap.Utility.JsonOptions.Tolerant, 16777216);
        }
        catch
        {
            return new ManifestData();
        }
    }

    private static void SaveManifest(string versionDir, ManifestData manifest)
    {
        string path = System.IO.Path.Combine(versionDir, ManifestFileName);
		Voidstrap.Utility.JsonFile.SerializeAtomic(path, manifest);
    }

    private static List<string> GetEligibleFiles(string versionDir)
    {
        var files = new List<string>();
        if (!Directory.Exists(versionDir)) return files;

            foreach (string file in Directory.EnumerateFiles(versionDir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (!_imageExtensions.Contains(ext) && !_textExtensions.Contains(ext) &&
                !_audioExtensions.Contains(ext))
                continue;

            string relative = Path.GetRelativePath(versionDir, file);
            bool skip = false;
            foreach (var skipDir in _skipDirectories)
            {
                if (relative.StartsWith(skipDir, StringComparison.OrdinalIgnoreCase))
                { skip = true; break; }
            }
            if (skip) continue;

            files.Add(file);
        }

        return files;
    }

    private static string ResolveSourcePackage(string relativePath,
        IReadOnlyDictionary<string, string> packageDirectoryMap)
    {
        foreach (var (pkgName, pkgDir) in packageDirectoryMap)
        {
            if (string.IsNullOrEmpty(pkgDir)) continue;
            if (relativePath.StartsWith(pkgDir, StringComparison.OrdinalIgnoreCase))
                return pkgName;
        }
        return "";
    }

}
