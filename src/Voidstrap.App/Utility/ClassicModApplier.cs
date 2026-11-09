namespace Voidstrap.Utility;

internal static class ClassicModApplier
{
    private const string StateFolderName = ".voidstrap-mods";
    private const string ManifestName = "applied.json";
    private const string BackupFolderName = "backup";

    private static readonly string[] ContentRoots = ["content/", "ExtraContent/", "PlatformContent/pc/"];

    private sealed class AppliedState
    {
        public List<string> Added { get; set; } = [];

        public List<string> Replaced { get; set; } = [];
    }

    public static void Apply(string executableDir)
    {
        const string LOG_IDENT = "ClassicModApplier::Apply";
        string contentDir = Path.Combine(executableDir, "content");
        try
        {
            Restore(executableDir);
            if (!Directory.Exists(contentDir))
            {
                App.Logger.WriteLine(LOG_IDENT, "This client has no content folder, so mods were skipped");
                return;
            }
            ManagedModScanResult scan = ManagedModStore.ScanEnabledFiles(ModInstallTargets.Classic);
            if (scan.Files.Count == 0)
                return;
            string stateDir = Path.Combine(executableDir, StateFolderName);
            string backupDir = Path.Combine(stateDir, BackupFolderName);
            AppliedState state = new();
            HashSet<string> written = new(StringComparer.OrdinalIgnoreCase);
            foreach (ManagedModFile file in scan.Files)
            {
                string? relative = MapToContent(file.Relative.Replace(Path.DirectorySeparatorChar, '/'));
                if (relative is null || !written.Add(relative))
                    continue;
                string destination = Path.GetFullPath(Path.Combine(contentDir, relative));
                if (!destination.StartsWith(Path.GetFullPath(contentDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    string backup = Path.Combine(backupDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, true);
                    state.Replaced.Add(relative);
                }
                else
                {
                    state.Added.Add(relative);
                }
                File.Copy(file.Source, destination, true);
                SaveState(stateDir, state);
            }
            App.Logger.WriteLine(LOG_IDENT, $"Applied {written.Count} mod files to the classic client");
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(LOG_IDENT, ex);
        }
    }

    public static void Restore(string executableDir)
    {
        const string LOG_IDENT = "ClassicModApplier::Restore";
        string stateDir = Path.Combine(executableDir, StateFolderName);
        string manifest = Path.Combine(stateDir, ManifestName);
        if (!File.Exists(manifest))
            return;
        string contentDir = Path.Combine(executableDir, "content");
        string backupDir = Path.Combine(stateDir, BackupFolderName);
        AppliedState state = JsonSerializer.Deserialize<AppliedState>(File.ReadAllText(manifest)) ?? new AppliedState();
        foreach (string relative in state.Added)
        {
            string path = Path.Combine(contentDir, relative);
            if (File.Exists(path))
                File.Delete(path);
        }
        foreach (string relative in state.Replaced)
        {
            string backup = Path.Combine(backupDir, relative);
            if (File.Exists(backup))
                File.Copy(backup, Path.Combine(contentDir, relative), true);
        }
        Directory.Delete(stateDir, true);
        App.Logger.WriteLine(LOG_IDENT, $"Restored {state.Added.Count + state.Replaced.Count} classic client files");
    }

    private static string? MapToContent(string relative)
    {
        foreach (string root in ContentRoots)
        {
            if (relative.StartsWith(root, StringComparison.OrdinalIgnoreCase) && relative.Length > root.Length)
                return relative[root.Length..];
        }
        return null;
    }

    private static void SaveState(string stateDir, AppliedState state)
    {
        Directory.CreateDirectory(stateDir);
        File.WriteAllText(Path.Combine(stateDir, ManifestName), JsonSerializer.Serialize(state));
    }
}
