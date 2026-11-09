using System.IO.Compression;

namespace Voidstrap.Utility;

internal static class ManagedModImporter
{
    private static readonly string[] RootFolders = ["content", "ExtraContent", "PlatformContent"];

    private const int MaxSearchDepth = 3;

    public static void Import(string name, string source, bool isZip)
    {
        const string LOG_IDENT = "ManagedModImporter::Import";
        string? staging = null;
        try
        {
            string folder = source;
            if (isZip)
            {
                staging = Path.Combine(Path.GetTempPath(), "Voidstrap", "ModImport", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                ExtractSafely(source, staging);
                folder = staging;
            }
            string root = FindModRoot(folder, 0) ?? folder;
            if (!Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Any())
                throw new InvalidDataException(Strings.Menu_Mods_Managed_ImportEmpty);
            ManagedModRecord record = ManagedModStore.Create(string.IsNullOrWhiteSpace(name) ? "Imported Mod" : name);
            int copied = CopyTree(root, ManagedModStore.GetFolder(record.Id));
            App.Logger.WriteLine(LOG_IDENT, $"Imported {copied} files into mod {record.Id}");
        }
        finally
        {
            if (staging != null)
            {
                try
                {
                    Directory.Delete(staging, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    App.Logger.WriteLine(LOG_IDENT, "The import staging folder could not be removed: " + ex.Message);
                }
            }
        }
    }

    private static string? FindModRoot(string folder, int depth)
    {
        if (RootFolders.Any(root => Directory.Exists(Path.Combine(folder, root))))
            return folder;
        if (depth >= MaxSearchDepth)
            return null;
        foreach (string child in Directory.GetDirectories(folder))
        {
            string? found = FindModRoot(child, depth + 1);
            if (found != null)
                return found;
        }
        return null;
    }

    private static void ExtractSafely(string zipPath, string destination)
    {
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The ZIP contains an unsafe path: " + entry.FullName);
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
    }

    private static int CopyTree(string source, string destination)
    {
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            count++;
        }
        return count;
    }
}
