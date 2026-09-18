using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Integrations;

public sealed class ThemeFileChange
{
    public string Path { get; set; } = "";

    public string Kind { get; set; } = "binary";

    public string? LocalPath { get; set; }

    public string LocalText { get; set; } = "";

    public long LocalSize { get; set; }

    public bool IsText => Kind == "text";

    public bool IsImage => Kind == "image";

    public bool IsFont => Kind == "font";

    public string SizeLabel
    {
        get
        {
            if (LocalSize < 1024)
                return LocalSize + " B";

            if (LocalSize < 1024 * 1024)
                return (LocalSize / 1024.0).ToString("0.#") + " KB";

            return (LocalSize / (1024.0 * 1024.0)).ToString("0.#") + " MB";
        }
    }
}

public static class ThemeFiles
{
    private static readonly string[] HiddenFiles = { ".published.json", ".installed.json" };

    private static readonly string[] AllowedExtensions =
    {
        ".xml",
        ".png", ".apng", ".jpg", ".jpeg", ".jfif", ".jpe", ".jff",
        ".webp", ".gif", ".bmp", ".dib", ".ico", ".cur", ".avif", ".avifs",
        ".ttf", ".otf", ".ttc",
        ".html", ".htm", ".css", ".js",
        ".mp4", ".webm", ".mov",
        ".mp3", ".wav", ".ogg", ".m4a", ".flac"
    };

    public static async Task<List<ThemeFileChange>> LoadAsync(string themeFolderName, CancellationToken token = default)
    {
        List<ThemeFileChange> result = new List<ThemeFileChange>();
        string folder = Path.Combine(Paths.CustomThemes, themeFolderName);

        if (!Directory.Exists(folder))
            return result;

        foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(folder, file).Replace(Path.DirectorySeparatorChar, '/');

            if (HiddenFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!AllowedExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                continue;

            ThemeFileChange entry = new ThemeFileChange
            {
                Path = relative,
                Kind = KindFor(relative),
                LocalPath = file,
                LocalSize = new FileInfo(file).Length
            };

            if (entry.IsText)
                entry.LocalText = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);

            result.Add(entry);
        }

        result.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static string KindFor(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (extension == ".xml")
            return "text";

        if (extension is ".png" or ".apng" or ".jpg" or ".jpeg" or ".jfif" or ".jpe" or ".jff"
            or ".webp" or ".gif" or ".bmp" or ".dib" or ".ico" or ".cur" or ".avif" or ".avifs")
            return "image";

        if (extension is ".ttf" or ".otf" or ".ttc")
            return "font";

        return "binary";
    }
}
