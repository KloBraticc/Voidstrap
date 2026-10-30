using System.Text;
using System.Text.Json.Nodes;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginFiles
{
    private const long MaxFileBytes = 16L * 1024 * 1024;

    private const long MaxTotalBytes = 256L * 1024 * 1024;

    private const int MaxFiles = 4096;

    private const int MaxDepth = 16;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".md", ".csv", ".log", ".xml", ".html", ".htm", ".css", ".js", ".ts", ".tsx",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".ico", ".mp3", ".ogg", ".wav", ".mp4", ".webm", ".ttf", ".otf", ".woff", ".woff2",
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private static string Install => Path.GetDirectoryName(Paths.Plugins) ?? Paths.Base;

    public static string RootOf(string mod) => Path.Combine(Install, "ModFiles", mod);

    public static string WebDataOf(string mod) => Path.Combine(Install, "ModWeb", mod);

    public static void DeleteAll(string mod)
    {
        ModFiles.TryDelete(RootOf(mod));
        ModFiles.TryDelete(WebDataOf(mod));
    }

    private static string Resolve(string mod, string relative, bool isFile)
    {
        if (string.IsNullOrWhiteSpace(relative) && isFile)
            throw new InvalidOperationException("a file path is required");
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new InvalidOperationException("paths must stay inside this mod's own folder");
        string[] parts = relative.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > MaxDepth)
            throw new InvalidOperationException("the path is too deep");
        foreach (string part in parts)
        {
            if (part is "." or ".." || part != part.TrimEnd('.', ' ') || ReservedNames.Contains(Path.GetFileNameWithoutExtension(part)))
                throw new InvalidOperationException("paths must stay inside this mod's own folder");
        }
        string root = Path.GetFullPath(RootOf(mod));
        string full = Path.GetFullPath(Path.Combine([root, .. parts]));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("paths must stay inside this mod's own folder");
        if (isFile && !AllowedExtensions.Contains(Path.GetExtension(full)))
            throw new InvalidOperationException("this file type is not allowed for mods, programs and scripts can never be stored");
        RejectLinks(root, full);
        return full;
    }

    private static void RejectLinks(string root, string full)
    {
        string current = root;
        foreach (string part in new[] { "" }.Concat(Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar)))
        {
            current = part.Length == 0 || part == "." ? current : Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("links are not allowed in a mod's folder");
        }
    }

    private static void RejectProgram(ReadOnlySpan<byte> content)
    {
        if (
            content.Length >= 2
            && (content[0] == 'M' && content[1] == 'Z' || content[0] == '#' && content[1] == '!' || content[0] == 'P' && content[1] == 'K')
            || content.Length >= 4 && content[0] == 0x7F && content[1] == 'E' && content[2] == 'L' && content[3] == 'F'
            || content.Length >= 4 && content[0] == 0xCF && content[1] == 0xFA && content[2] == 0xED && content[3] == 0xFE
        )
            throw new InvalidOperationException("programs and archives can never be stored by a mod");
    }

    public static string Read(string mod, string relative, bool base64)
    {
        string path = Resolve(mod, relative, true);
        if (new FileInfo(path).Length > MaxFileBytes)
            throw new InvalidOperationException("the file is too large");
        return base64 ? Convert.ToBase64String(File.ReadAllBytes(path)) : File.ReadAllText(path);
    }

    public static void Write(string mod, string relative, string content, bool base64)
    {
        byte[] bytes = base64 ? Convert.FromBase64String(content) : Encoding.UTF8.GetBytes(content);
        if (bytes.Length > MaxFileBytes)
            throw new InvalidOperationException("a mod file can be at most 16 MB");
        RejectProgram(bytes);
        string path = Resolve(mod, relative, true);
        string root = RootOf(mod);
        Directory.CreateDirectory(root);
        FileInfo[] existing = new DirectoryInfo(root).GetFiles("*", SearchOption.AllDirectories);
        if (existing.Length >= MaxFiles || existing.Sum(file => file.Length) + bytes.Length > MaxTotalBytes)
            throw new InvalidOperationException("this mod's folder is full, the limit is 256 MB and 4096 files");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        App.Logger.WriteLine("VoidstrapMods", mod + ": wrote a file in its own folder, " + bytes.Length + " bytes");
    }

    public static void MakeFolder(string mod, string relative) => Directory.CreateDirectory(Resolve(mod, relative, false));

    public static bool Exists(string mod, string relative)
    {
        string path = Resolve(mod, relative, false);
        return File.Exists(path) || Directory.Exists(path);
    }

    public static void Delete(string mod, string relative)
    {
        string path = Resolve(mod, relative, false);
        if (path == Path.GetFullPath(RootOf(mod)))
            throw new InvalidOperationException("name a file or folder inside the mod's folder");
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    public static JsonArray List(string mod, string relative)
    {
        JsonArray result = [];
        string path = Resolve(mod, relative, false);
        if (!Directory.Exists(path))
            return result;
        foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
            result.Add(
                new JsonObject
                {
                    ["name"] = entry.Name,
                    ["folder"] = entry is DirectoryInfo,
                    ["size"] = entry is FileInfo file ? file.Length : 0,
                }
            );
        return result;
    }
}
