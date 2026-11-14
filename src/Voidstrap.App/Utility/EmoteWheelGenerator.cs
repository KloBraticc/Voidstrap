using System.Windows.Media;
using System.Windows.Media.Imaging;
using Voidstrap.AppData;

namespace Voidstrap.Utility;

internal static class EmoteWheelGenerator
{
    private const string LogIdent = "EmoteWheelGenerator";

    private const string SettingsFileName = "emotes.json";

    private const string CenterImageName = "center.png";

    private const string BackgroundImageName = "background.png";

    private const int ImageLimit = 1024;

    private const string EmotesFolder = "content/textures/ui/Emotes";

    private const double GlowAngle = -45.0;

    private static readonly string[] IconTextures =
    [
        "content/textures/ui/TopBar/emotesOn",
        "content/textures/ui/TopBar/emotesOff",
        "content/textures/ui/MenuBar/icon_emote",
        "content/textures/MenuBar/icon_emote",
    ];

    private static readonly string[] ScaleSuffixes = ["", "@2x", "@3x"];

    private static readonly object ImageGate = new object();

    private static readonly Dictionary<string, LoadedImage> ImageCache = new Dictionary<string, LoadedImage>(StringComparer.OrdinalIgnoreCase);

    private static readonly object PreviewGate = new object();

    private static PreviewTextures? _previewTextures;

    private static string Root => Path.Combine(Paths.Base, "EmoteManager");

    private enum Part
    {
        Skip,
        Background,
        Wheel,
        Glow,
        Marker,
        EditorWheel,
        EditorGlow,
        Icon,
    }

    private sealed class LoadedImage
    {
        public required byte[] Pixels { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }

        public required DateTime Stamp { get; init; }
    }

    private sealed class Texture
    {
        public required byte[] Pixels { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }
    }

    private sealed class PreviewTextures
    {
        public required string Guid { get; init; }

        public required Texture Background { get; init; }

        public required Texture Wheel { get; init; }

        public required Texture Glow { get; init; }

        public required Texture Marker { get; init; }

        public BitmapSource? Before { get; set; }

    }

    private sealed class Placed
    {
        public required LoadedImage Image { get; init; }

        public required EmoteImagePlacement Placement { get; init; }
    }

    private sealed class Painter
    {
        public Color Center;

        public Color? CenterEnd;

        public Color Lines;

        public Color Background;

        public double BackgroundOpacity;

        public Color Glow;

        public Color? GlowEnd;

        public Color Marker;

        public double WheelOpacity;

        public bool Editor;

        public bool Icons;

        public Color Icon;

        public Placed? CenterImage;

        public Placed? BackgroundImage;
    }

    public static bool TryParseColor(string? text, out Color color) => ModGenerator.TryParseColor(text, out color);

    public static string? FindModId()
    {
        if (!Directory.Exists(Root))
        {
            return null;
        }
        HashSet<string> live = new HashSet<string>(ManagedModStore.Load().Select(record => record.Id), StringComparer.OrdinalIgnoreCase);
        foreach (string folder in Directory.EnumerateDirectories(Root))
        {
            string id = Path.GetFileName(folder);
            if (live.Contains(id) && File.Exists(Path.Combine(folder, SettingsFileName)))
            {
                return id;
            }
        }
        return null;
    }

    public static EmoteWheelSettings? LoadSettings(string modId)
    {
        string path = Path.Combine(Root, modId, SettingsFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<EmoteWheelSettings>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The saved emote wheel settings could not be read: " + ex.Message);
            return null;
        }
    }

    public static string? Validate(EmoteWheelSettings settings)
    {
        string?[] required = [settings.CenterColor, settings.LineColor, settings.BackgroundColor, settings.GlowColor, settings.MarkerColor];
        if (required.Any(value => !TryParseColor(value, out _)))
        {
            return Strings.Ui_ModGen_EnterHexColour;
        }
        string?[] optional = [settings.CenterColorEnd, settings.GlowColorEnd];
        if (optional.Any(value => !string.IsNullOrWhiteSpace(value) && !TryParseColor(value, out _)))
        {
            return Strings.Ui_ModGen_EnterHexColour;
        }
        if (settings.RecolorIcons && !TryParseColor(settings.IconColor, out _))
        {
            return Strings.Ui_ModGen_EnterHexColour;
        }
        return null;
    }

    public static async Task<int> GenerateAsync(EmoteWheelSettings settings, IProgress<double>? progress, CancellationToken token)
    {
        await ModGenerator.GenerateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => GenerateCore(settings, FindModId(), progress, token, null, null), token).ConfigureAwait(false);
        }
        finally
        {
            ModGenerator.GenerateGate.Release();
        }
    }

    public static async Task RefreshOutdatedAsync(string versionDirectory, string versionGuid, CancellationToken token)
    {
        await ModGenerator.GenerateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string? modId = FindModId();
            if (modId == null)
            {
                return;
            }
            EmoteWheelSettings? settings = LoadSettings(modId);
            if (settings == null || string.Equals(settings.RobloxVersion, versionGuid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            App.Logger?.WriteLine(LogIdent, "Roblox updated, rebuilding " + settings.ModName + " for the new emote textures");
            await Task.Run(() => GenerateCore(settings, modId, null, token, versionDirectory, versionGuid), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The emote wheel mod could not be refreshed: " + ex.Message);
        }
        finally
        {
            ModGenerator.GenerateGate.Release();
        }
    }

    public static async Task<bool> RemoveAsync(CancellationToken token)
    {
        await ModGenerator.GenerateGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string? modId = FindModId();
            if (modId == null)
            {
                return false;
            }
            ManagedModStore.Delete(modId);
            string state = Path.Combine(Root, modId);
            if (Directory.Exists(state))
            {
                Directory.Delete(state, recursive: true);
            }
            App.Logger?.WriteLine(LogIdent, "Removed the emote wheel mod");
            return true;
        }
        finally
        {
            ModGenerator.GenerateGate.Release();
        }
    }

    public static (BitmapSource Before, BitmapSource After)? RenderPreview(EmoteWheelSettings settings)
    {
        try
        {
            PreviewTextures? textures = GetPreviewTextures();
            if (textures == null)
            {
                return null;
            }
            Painter painter = BuildPainter(settings, null);
            Texture background = Process(Part.Background, textures.Background, painter);
            Texture wheel = Process(Part.Wheel, textures.Wheel, painter);
            Texture glow = Process(Part.Glow, textures.Glow, painter);
            Texture marker = Process(Part.Marker, textures.Marker, painter);
            BitmapSource? before = textures.Before;
            if (before == null)
            {
                before = Compose(textures.Background, textures.Wheel, textures.Glow, textures.Marker);
                textures.Before = before;
            }
            return (before, Compose(background, wheel, glow, marker));
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The emote wheel preview could not be drawn: " + ex.Message);
            return null;
        }
    }

    private static PreviewTextures? GetPreviewTextures()
    {
        RobloxPlayerData data = new RobloxPlayerData();
        lock (PreviewGate)
        {
            (string folder, string guid) = ModGenerator.ResolveInstall(data);
            if (_previewTextures != null && string.Equals(_previewTextures.Guid, guid, StringComparison.OrdinalIgnoreCase))
            {
                return _previewTextures;
            }
            HashSet<string> manifest = ModGenerator.CurrentManifest(data);
            Texture? Read(string name)
            {
                string relative = EmotesFolder + "/Large/" + name + ".png";
                if (!File.Exists(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return null;
                }
                (byte[] pixels, int width, int height) = ModGenerator.Load(ModGenerator.ResolveSource(folder, guid, relative, manifest));
                return new Texture { Pixels = pixels, Width = width, Height = height };
            }
            Texture? background = Read("CircleBackground");
            Texture? wheel = Read("SegmentedCircle");
            Texture? glow = Read("SelectedGradient");
            Texture? marker = Read("SelectedLine");
            if (background == null || wheel == null || glow == null || marker == null)
            {
                return null;
            }
            _previewTextures = new PreviewTextures
            {
                Guid = guid,
                Background = background,
                Wheel = wheel,
                Glow = glow,
                Marker = marker,
            };
            return _previewTextures;
        }
    }

    private static int GenerateCore(
        EmoteWheelSettings settings,
        string? existingId,
        IProgress<double>? progress,
        CancellationToken token,
        string? versionDirectory,
        string? versionGuid
    )
    {
        const string LOG_IDENT = "EmoteWheelGenerator::GenerateCore";
        RobloxPlayerData data = new RobloxPlayerData();
        if (versionDirectory == null || versionGuid == null)
        {
            (string folder, string guid) = ModGenerator.ResolveInstall(data);
            versionDirectory ??= folder;
            versionGuid ??= guid;
        }
        if (!Directory.Exists(Path.Combine(versionDirectory, EmotesFolder.Replace('/', Path.DirectorySeparatorChar))))
        {
            throw new InvalidOperationException(Strings.Ui_EmoteMgr_NoTextures);
        }
        List<(string Relative, Part Part)> targets = FindTargets(versionDirectory, settings);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException(Strings.Ui_EmoteMgr_NoTextures);
        }
        progress?.Report(0.02);
        string name = string.IsNullOrWhiteSpace(settings.ModName) ? "Emote wheel" : settings.ModName.Trim();
        string modId = existingId ?? ManagedModStore.Create(name).Id;
        string stateFolder = Path.Combine(Root, modId);
        Directory.CreateDirectory(stateFolder);
        settings.CenterImage.Path = StoreImage(settings.CenterImage.Path, Path.Combine(stateFolder, CenterImageName));
        settings.BackgroundImage.Path = StoreImage(settings.BackgroundImage.Path, Path.Combine(stateFolder, BackgroundImageName));

        string output = ManagedModStore.GetFolder(modId);
        string staging = output + ".generating";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }
        Directory.CreateDirectory(staging);

        HashSet<string> manifest = ModGenerator.CurrentManifest(data);
        Painter painter = BuildPainter(settings, stateFolder);
        int written = 0;
        int skipped = 0;
        int done = 0;
        Parallel.ForEach(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, targets.Count)), CancellationToken = token },
            target =>
            {
                try
                {
                    string source = ModGenerator.ResolveSource(versionDirectory, versionGuid, target.Relative, manifest);
                    (byte[] pixels, int width, int height) = ModGenerator.Load(source);
                    Texture result = Process(target.Part, new Texture { Pixels = pixels, Width = width, Height = height }, painter);
                    string destination = Path.Combine(staging, target.Relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    ModGenerator.Save(destination, result.Pixels, result.Width, result.Height);
                    Interlocked.Increment(ref written);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref skipped);
                    App.Logger?.WriteLine(LOG_IDENT, "Skipped " + target.Relative + ": " + ex.Message);
                }
                progress?.Report(0.05 + 0.9 * Interlocked.Increment(ref done) / targets.Count);
            }
        );
        token.ThrowIfCancellationRequested();
        if (written == 0)
        {
            Directory.Delete(staging, recursive: true);
            throw new InvalidOperationException(Strings.Ui_EmoteMgr_NoTextures);
        }

        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }
        Directory.Move(staging, output);
        if (existingId != null && !string.IsNullOrWhiteSpace(settings.ModName))
        {
            ManagedModStore.Rename(modId, name);
        }

        settings.RobloxVersion = versionGuid;
        settings.GeneratedUtc = DateTime.UtcNow;
        JsonFile.SerializeAtomic(Path.Combine(stateFolder, SettingsFileName), settings, JsonOptions.Indented);
        progress?.Report(1.0);
        App.Logger?.WriteLine(
            LOG_IDENT,
            "Restyled " + written + " emote textures for Roblox " + versionGuid + (skipped > 0 ? ", skipped " + skipped : "")
        );
        return written;
    }

    private static List<(string Relative, Part Part)> FindTargets(string versionDirectory, EmoteWheelSettings settings)
    {
        List<(string, Part)> targets = [];
        string emotes = Path.Combine(versionDirectory, EmotesFolder.Replace('/', Path.DirectorySeparatorChar));
        foreach (string file in Directory.EnumerateFiles(emotes, "*.png", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(versionDirectory, file).Replace('\\', '/');
            Part part = Classify(relative);
            if (part == Part.Skip || (!settings.RecolorEditor && part is Part.EditorWheel or Part.EditorGlow))
            {
                continue;
            }
            if (!settings.RecolorIcons && part == Part.Icon)
            {
                continue;
            }
            targets.Add((relative, part));
        }
        if (settings.RecolorIcons)
        {
            foreach (string icon in IconTextures)
            {
                foreach (string suffix in ScaleSuffixes)
                {
                    string relative = icon + suffix + ".png";
                    if (File.Exists(Path.Combine(versionDirectory, relative.Replace('/', Path.DirectorySeparatorChar))))
                    {
                        targets.Add((relative, Part.Icon));
                    }
                }
            }
        }
        return targets;
    }

    private static Part Classify(string relative)
    {
        string name = Path.GetFileNameWithoutExtension(relative);
        int at = name.IndexOf('@');
        if (at > 0)
        {
            name = name[..at];
        }
        bool editor = relative.Contains("/Editor/", StringComparison.OrdinalIgnoreCase);
        return name.ToLowerInvariant() switch
        {
            "circlebackground" => Part.Background,
            "segmentedcircle" => Part.Wheel,
            "selectedgradient" => Part.Glow,
            "selectedline" => Part.Marker,
            "wheel" when editor => Part.EditorWheel,
            "orangehighlight" when editor => Part.EditorGlow,
            "emotesicon" or "emotesradialicon" => Part.Icon,
            _ => Part.Skip,
        };
    }

    private static string? StoreImage(string? source, string destination)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
            return null;
        }
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            return destination;
        }
        if (!File.Exists(source))
        {
            App.Logger?.WriteLine(LogIdent, "The image " + Path.GetFileName(source) + " no longer exists, leaving it out");
            return File.Exists(destination) ? destination : null;
        }
        (byte[] pixels, int width, int height) = ModGenerator.Load(source);
        (pixels, width, height) = ModGenerator.Shrink(pixels, width, height, ImageLimit);
        ModGenerator.Save(destination, pixels, width, height);
        return destination;
    }

    private static Painter BuildPainter(EmoteWheelSettings settings, string? stateFolder)
    {
        Painter painter = new Painter
        {
            Center = ParseOr(settings.CenterColor, Colors.White),
            CenterEnd = TryParseColor(settings.CenterColorEnd, out Color centerEnd) ? centerEnd : null,
            Lines = ParseOr(settings.LineColor, Colors.White),
            Background = ParseOr(settings.BackgroundColor, Colors.Black),
            BackgroundOpacity = Math.Clamp(settings.BackgroundOpacity, 0.0, 1.0),
            Glow = ParseOr(settings.GlowColor, Colors.White),
            GlowEnd = TryParseColor(settings.GlowColorEnd, out Color glowEnd) ? glowEnd : null,
            Marker = ParseOr(settings.MarkerColor, Colors.White),
            WheelOpacity = Math.Clamp(settings.WheelOpacity, 0.1, 1.0),
            Editor = settings.RecolorEditor,
            Icons = settings.RecolorIcons,
            Icon = ParseOr(settings.IconColor, Colors.White),
        };
        painter.CenterImage = LoadPlaced(settings.CenterImage, stateFolder == null ? null : Path.Combine(stateFolder, CenterImageName));
        painter.BackgroundImage = LoadPlaced(
            settings.BackgroundImage,
            stateFolder == null ? null : Path.Combine(stateFolder, BackgroundImageName)
        );
        return painter;
    }

    private static Color ParseOr(string? text, Color fallback) => TryParseColor(text, out Color color) ? color : fallback;

    private static Placed? LoadPlaced(EmoteImagePlacement placement, string? stored)
    {
        string? path = placement.Path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            path = stored != null && File.Exists(stored) ? stored : null;
        }
        if (path == null)
        {
            return null;
        }
        LoadedImage? image = LoadImage(path);
        return image == null ? null : new Placed { Image = image, Placement = placement };
    }

    private static LoadedImage? LoadImage(string path)
    {
        try
        {
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            lock (ImageGate)
            {
                if (ImageCache.TryGetValue(path, out LoadedImage? cached) && cached.Stamp == stamp)
                {
                    return cached;
                }
            }
            (byte[] pixels, int width, int height) = ModGenerator.Load(path);
            (pixels, width, height) = ModGenerator.Shrink(pixels, width, height, ImageLimit);
            LoadedImage image = new LoadedImage { Pixels = pixels, Width = width, Height = height, Stamp = stamp };
            lock (ImageGate)
            {
                if (ImageCache.Count > 8)
                {
                    ImageCache.Clear();
                }
                ImageCache[path] = image;
            }
            return image;
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine(LogIdent, "The image " + Path.GetFileName(path) + " could not be read: " + ex.Message);
            return null;
        }
    }

    private static Texture Process(Part part, Texture source, Painter painter)
    {
        byte[] pixels = (byte[])source.Pixels.Clone();
        int width = source.Width;
        int height = source.Height;
        switch (part)
        {
            case Part.Background:
                PaintBackground(pixels, width, height, painter);
                break;
            case Part.Wheel:
                PaintWheel(pixels, width, height, painter);
                break;
            case Part.Glow:
                PaintFlat(pixels, width, height, painter.Glow, painter.GlowEnd, 1.0);
                break;
            case Part.Marker:
                PaintFlat(pixels, width, height, painter.Marker, null, 1.0);
                break;
            case Part.EditorWheel:
                PaintSplit(pixels, painter.Background, painter.Lines);
                break;
            case Part.EditorGlow:
                PaintFlat(pixels, width, height, painter.Glow, painter.GlowEnd, 1.0);
                break;
            case Part.Icon:
                PaintShaded(pixels, painter.Icon);
                break;
        }
        return new Texture { Pixels = pixels, Width = width, Height = height };
    }

    private static void PaintBackground(byte[] pixels, int width, int height, Painter painter)
    {
        int maxAlpha = 1;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            maxAlpha = Math.Max(maxAlpha, pixels[i]);
        }
        Placed? image = painter.BackgroundImage;
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                double coverage = pixels[offset + 3] / (double)maxAlpha;
                if (coverage <= 0)
                {
                    continue;
                }
                double r = painter.Background.R;
                double g = painter.Background.G;
                double b = painter.Background.B;
                double alpha = coverage * painter.BackgroundOpacity;
                if (image != null)
                {
                    (double ir, double ig, double ib, double ia) = Sample(image, (x + 0.5) / width, (y + 0.5) / height);
                    ia *= image.Placement.Opacity;
                    double mixed = Math.Max(alpha, coverage * ia);
                    if (mixed > 0)
                    {
                        double imageShare = coverage * ia / mixed;
                        r = r + (ir - r) * imageShare;
                        g = g + (ig - g) * imageShare;
                        b = b + (ib - b) * imageShare;
                    }
                    alpha = mixed;
                }
                Write(pixels, offset, r, g, b, alpha);
            }
        });
    }

    private static void PaintWheel(byte[] pixels, int width, int height, Painter painter)
    {
        (double low, double high) = LumaRange(pixels);
        double span = Math.Max(1.0, high - low);
        int top = height;
        int bottom = -1;
        int left = width;
        int right = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                if (pixels[offset + 3] > 200 && (Luma(pixels, offset) - low) / span < 0.25)
                {
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }
        }
        bool hasDisc = bottom >= top && right >= left;
        double discHeight = Math.Max(1, bottom - top + 1);
        double discWidth = Math.Max(1, right - left + 1);
        Placed? image = painter.CenterImage;
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte a = pixels[offset + 3];
                if (a == 0)
                {
                    continue;
                }
                double t = Math.Clamp((Luma(pixels, offset) - low) / span, 0.0, 1.0);
                double v = hasDisc ? Math.Clamp((y - top) / discHeight, 0.0, 1.0) : 0.5;
                Color center = painter.CenterEnd is Color end ? Lerp(painter.Center, end, v) : painter.Center;
                double cr = center.R;
                double cg = center.G;
                double cb = center.B;
                if (image != null && hasDisc)
                {
                    (double ir, double ig, double ib, double ia) = Sample(image, (x + 0.5 - left) / discWidth, (y + 0.5 - top) / discHeight);
                    ia *= image.Placement.Opacity;
                    cr += (ir - cr) * ia;
                    cg += (ig - cg) * ia;
                    cb += (ib - cb) * ia;
                }
                double r = cr + (painter.Lines.R - cr) * t;
                double g = cg + (painter.Lines.G - cg) * t;
                double b = cb + (painter.Lines.B - cb) * t;
                Write(pixels, offset, r, g, b, a / 255.0 * painter.WheelOpacity);
            }
        });
    }

    private static void PaintFlat(byte[] pixels, int width, int height, Color start, Color? end, double opacity)
    {
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte a = pixels[offset + 3];
                if (a == 0)
                {
                    continue;
                }
                Color color = end is Color last ? Lerp(start, last, width > 1 ? x / (double)(width - 1) : 0) : start;
                Write(pixels, offset, color.R, color.G, color.B, a / 255.0 * opacity);
            }
        }
    }

    private static void PaintSplit(byte[] pixels, Color dark, Color light)
    {
        (double low, double high) = LumaRange(pixels);
        double span = Math.Max(1.0, high - low);
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte a = pixels[offset + 3];
            if (a == 0)
            {
                continue;
            }
            double t = Math.Clamp((Luma(pixels, offset) - low) / span, 0.0, 1.0);
            Write(
                pixels,
                offset,
                dark.R + (light.R - dark.R) * t,
                dark.G + (light.G - dark.G) * t,
                dark.B + (light.B - dark.B) * t,
                a / 255.0
            );
        }
    }

    private static void PaintShaded(byte[] pixels, Color color)
    {
        (_, double high) = LumaRange(pixels);
        double top = Math.Max(1.0, high);
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            byte a = pixels[offset + 3];
            if (a == 0)
            {
                continue;
            }
            double shade = Math.Clamp(Luma(pixels, offset) / top, 0.0, 1.0);
            Write(pixels, offset, color.R * shade, color.G * shade, color.B * shade, a / 255.0);
        }
    }

    private static (double Low, double High) LumaRange(byte[] pixels)
    {
        double low = double.MaxValue;
        double high = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset + 3] < 32)
            {
                continue;
            }
            double luma = Luma(pixels, offset);
            low = Math.Min(low, luma);
            high = Math.Max(high, luma);
        }
        return low == double.MaxValue ? (0, 255) : (low, high);
    }

    private static double Luma(byte[] pixels, int offset) =>
        0.0722 * pixels[offset] + 0.7152 * pixels[offset + 1] + 0.2126 * pixels[offset + 2];

    private static Color Lerp(Color from, Color to, double t) =>
        Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t)
        );

    private static void Write(byte[] pixels, int offset, double r, double g, double b, double alpha)
    {
        pixels[offset] = (byte)Math.Clamp(Math.Round(b), 0, 255);
        pixels[offset + 1] = (byte)Math.Clamp(Math.Round(g), 0, 255);
        pixels[offset + 2] = (byte)Math.Clamp(Math.Round(r), 0, 255);
        pixels[offset + 3] = (byte)Math.Clamp(Math.Round(alpha * 255.0), 0, 255);
    }

    private static (double R, double G, double B, double A) Sample(Placed placed, double u, double v)
    {
        EmoteImagePlacement placement = placed.Placement;
        LoadedImage image = placed.Image;
        double scale = Math.Clamp(placement.Scale, 0.1, 5.0);
        double du = u - 0.5 - placement.OffsetX;
        double dv = v - 0.5 - placement.OffsetY;
        double radians = -placement.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double ru = (du * cos - dv * sin) / scale;
        double rv = (du * sin + dv * cos) / scale;
        double side = Math.Min(image.Width, image.Height);
        double px = image.Width / 2.0 + ru * side - 0.5;
        double py = image.Height / 2.0 + rv * side - 0.5;
        return Bilinear(image.Pixels, image.Width, image.Height, px, py);
    }

    private static (double R, double G, double B, double A) Bilinear(byte[] pixels, int width, int height, double px, double py)
    {
        if (px < -1 || py < -1 || px > width || py > height)
        {
            return (0, 0, 0, 0);
        }
        int x0 = (int)Math.Floor(px);
        int y0 = (int)Math.Floor(py);
        double fx = px - x0;
        double fy = py - y0;
        double r = 0;
        double g = 0;
        double b = 0;
        double a = 0;
        for (int j = 0; j < 2; j++)
        {
            for (int i = 0; i < 2; i++)
            {
                int x = x0 + i;
                int y = y0 + j;
                if (x < 0 || y < 0 || x >= width || y >= height)
                {
                    continue;
                }
                double weight = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                int offset = (y * width + x) * 4;
                double alpha = pixels[offset + 3] / 255.0 * weight;
                b += pixels[offset] * alpha;
                g += pixels[offset + 1] * alpha;
                r += pixels[offset + 2] * alpha;
                a += alpha;
            }
        }
        return a <= 0 ? (0, 0, 0, 0) : (r / a, g / a, b / a, a);
    }

    private static BitmapSource Compose(Texture background, Texture wheel, Texture glow, Texture marker)
    {
        int size = Math.Max(background.Width, wheel.Width);
        double center = size / 2.0;
        double[] canvas = new double[size * size * 4];
        Draw(canvas, size, background, background.Width / 2.0, background.Height / 2.0, center, center, 0);
        Draw(canvas, size, glow, 0, glow.Height / 2.0, center, center, GlowAngle);
        Draw(canvas, size, wheel, wheel.Width / 2.0, wheel.Height / 2.0, center, center, 0);
        double radians = GlowAngle * Math.PI / 180.0;
        double reach = wheel.Width / 2.0 - marker.Width / 2.0 - 2;
        Draw(
            canvas,
            size,
            marker,
            marker.Width / 2.0,
            marker.Height / 2.0,
            center + Math.Cos(radians) * reach,
            center + Math.Sin(radians) * reach,
            GlowAngle
        );
        byte[] pixels = new byte[size * size * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            double a = canvas[offset + 3];
            if (a <= 0)
            {
                continue;
            }
            pixels[offset] = (byte)Math.Clamp(Math.Round(canvas[offset] / a), 0, 255);
            pixels[offset + 1] = (byte)Math.Clamp(Math.Round(canvas[offset + 1] / a), 0, 255);
            pixels[offset + 2] = (byte)Math.Clamp(Math.Round(canvas[offset + 2] / a), 0, 255);
            pixels[offset + 3] = (byte)Math.Clamp(Math.Round(a * 255.0), 0, 255);
        }
        return ModGenerator.Create(pixels, size, size);
    }

    private static void Draw(double[] canvas, int size, Texture texture, double anchorX, double anchorY, double x, double y, double angle)
    {
        double radians = angle * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double reach = Math.Sqrt(texture.Width * texture.Width + texture.Height * texture.Height);
        int minX = Math.Max(0, (int)Math.Floor(x - reach));
        int maxX = Math.Min(size - 1, (int)Math.Ceiling(x + reach));
        int minY = Math.Max(0, (int)Math.Floor(y - reach));
        int maxY = Math.Min(size - 1, (int)Math.Ceiling(y + reach));
        Parallel.For(minY, maxY + 1, py =>
        {
            for (int px = minX; px <= maxX; px++)
            {
                double dx = px + 0.5 - x;
                double dy = py + 0.5 - y;
                double sx = dx * cos + dy * sin + anchorX - 0.5;
                double sy = -dx * sin + dy * cos + anchorY - 0.5;
                (double r, double g, double b, double a) = Bilinear(texture.Pixels, texture.Width, texture.Height, sx, sy);
                if (a <= 0)
                {
                    continue;
                }
                int offset = (py * size + px) * 4;
                double keep = 1 - a;
                canvas[offset] = b * a + canvas[offset] * keep;
                canvas[offset + 1] = g * a + canvas[offset + 1] * keep;
                canvas[offset + 2] = r * a + canvas[offset + 2] * keep;
                canvas[offset + 3] = a + canvas[offset + 3] * keep;
            }
        });
    }
}
