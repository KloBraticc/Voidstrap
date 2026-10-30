using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Voidstrap.Utility;

internal static class TextFontInstaller
{
    private static string? _pendingFontCache;
    private static readonly string[] FontFiles = new[]
    {
        "Inter_18pt-Light.ttf",
        "Inter_18pt-Regular.ttf",
        "Inter_18pt-Medium.ttf",
        "Inter_18pt-SemiBold.ttf",
        "Inter_18pt-Bold.ttf",
        "selawk.ttf",
        "selawkb.ttf",
        "selawkl.ttf",
        "selawksb.ttf",
        "selawksl.ttf"
    };

    private const string AliasConfig = """
<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "fonts.dtd">
<fontconfig>
  <match target="pattern">
    <test name="family"><string>Segoe UI</string></test>
    <edit name="family" mode="prepend" binding="strong"><string>Selawik</string></edit>
  </match>
  <match target="pattern">
    <test name="family"><string>Segoe UI Semibold</string></test>
    <edit name="family" mode="prepend" binding="strong"><string>Selawik Semibold</string></edit>
  </match>
  <match target="pattern">
    <test name="family"><string>Segoe UI Light</string></test>
    <edit name="family" mode="prepend" binding="strong"><string>Selawik Light</string></edit>
  </match>
  <match target="pattern">
    <test name="family"><string>Segoe UI Semilight</string></test>
    <edit name="family" mode="prepend" binding="strong"><string>Selawik Semilight</string></edit>
  </match>
  <alias>
    <family>Segoe UI</family>
    <prefer><family>Selawik</family></prefer>
  </alias>
</fontconfig>
""";

    private static readonly string[] BundledFallbackFamilies =
    [
        "Inter 18pt",
        "Selawik",
        "Selawik Semibold",
        "Selawik Light",
        "Selawik Semilight"
    ];

    private static readonly string[] AdditionalAliasedFamilies =
    [
        "Segoe UI Variable",
        "Segoe UI Semilight",
        "Inter",
        "Inter 18pt",
        "Selawik",
        "Helvetica",
        "Helvetica Neue",
        "Lucida Grande"
    ];

    public static void Install()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string fontDirectory = Path.Combine(home, ".local", "share", "fonts", "voidstrap");
                string refreshMarker = Path.Combine(fontDirectory, "cache-refresh-pending");
                bool wroteFonts = ExtractFonts(fontDirectory);
                bool wroteConfig = WriteAliasConfig(home);
                LinkSystemFontDirectories(Path.GetDirectoryName(fontDirectory)!, home);
                EnsureRuntimeFallbacks();
                if (wroteFonts || wroteConfig)
                    File.WriteAllText(refreshMarker, string.Empty);
                if (File.Exists(refreshMarker))
                    _pendingFontCache = fontDirectory;
            }
            else if (OperatingSystem.IsMacOS())
            {
                string fontDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts");
                ExtractFonts(fontDirectory);
                EnsureRuntimeFallbacks();
            }
        }
        catch
        {
        }
    }

    public static void EnsureRuntimeFallbacks()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string fontDirectory = Path.Combine(home, ".local", "share", "fonts", "voidstrap");
                if (!File.Exists(Path.Combine(fontDirectory, "Inter_18pt-Regular.ttf"))
                    || !File.Exists(Path.Combine(fontDirectory, "selawk.ttf")))
                {
                    ExtractFonts(fontDirectory);
                    LinkSystemFontDirectories(Path.GetDirectoryName(fontDirectory)!, home);
                }
            }
            PatchPortableFamilyAliases();
        }
        catch
        {
        }
    }

    private static void PatchPortableFamilyAliases()
    {
        try
        {
            Type? collectionType = typeof(System.Windows.Media.FontFamily).Assembly.GetType("MS.Internal.Text.TextInterface.FontCollection", false);
            FieldInfo? aliasesField = collectionType?.GetField("s_portableFamilyAliases", BindingFlags.NonPublic | BindingFlags.Static);
            if (aliasesField?.GetValue(null) is not System.Collections.Generic.Dictionary<string, string[]> aliases)
                return;

            foreach (string key in new System.Collections.Generic.List<string>(aliases.Keys))
            {
                string[] existing = aliases[key];
                System.Collections.Generic.List<string> merged = new(existing.Length + BundledFallbackFamilies.Length);
                foreach (string item in existing)
                {
                    if (!merged.Exists(candidate => string.Equals(candidate, item, StringComparison.OrdinalIgnoreCase)))
                        merged.Add(item);
                }
                foreach (string fallback in BundledFallbackFamilies)
                {
                    if (!merged.Exists(candidate => string.Equals(candidate, fallback, StringComparison.OrdinalIgnoreCase)))
                        merged.Add(fallback);
                }
                aliases[key] = merged.ToArray();
            }

            string[] defaultChain =
            [
                "Inter 18pt",
                "Selawik",
                "DejaVu Sans",
                "Noto Sans",
                "Liberation Sans",
                "Roboto",
                "Helvetica Neue",
                "Helvetica",
                "Arial"
            ];
            foreach (string family in AdditionalAliasedFamilies)
            {
                if (!aliases.ContainsKey(family))
                    aliases[family] = defaultChain;
            }
        }
        catch
        {
        }
    }

    private static bool ExtractFonts(string directory)
    {
        bool wroteAny = false;
        Directory.CreateDirectory(directory);
        Assembly assembly = Assembly.GetExecutingAssembly();
        foreach (string fileName in FontFiles)
        {
            try
            {
                string destination = Path.Combine(directory, fileName);
                using Stream? source = assembly.GetManifestResourceStream("Voidstrap.Resources.Fonts." + fileName);
                if (source == null)
                {
                    continue;
                }
                if (File.Exists(destination) && new FileInfo(destination).Length == source.Length)
                {
                    continue;
                }
                using FileStream target = File.Create(destination);
                source.CopyTo(target);
                wroteAny = true;
            }
            catch
            {
            }
        }
        return wroteAny;
    }

    private static void LinkSystemFontDirectories(string fontsRoot, string home)
    {
        try
        {
            Directory.CreateDirectory(fontsRoot);
        }
        catch
        {
            return;
        }

        System.Collections.Generic.HashSet<string> seenTargets = new(StringComparer.Ordinal);
        foreach (string standard in new[]
        {
            "/usr/share/fonts",
            "/usr/local/share/fonts",
            Path.Combine(home, ".fonts"),
            fontsRoot
        })
        {
            if (!string.IsNullOrWhiteSpace(standard))
            {
                try
                {
                    seenTargets.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(standard)));
                }
                catch
                {
                }
            }
        }

        string user = Environment.UserName;
        System.Collections.Generic.List<(string Name, string Target)> candidates =
        [
            ("voidstrap-host", "/run/host/fonts"),
            ("voidstrap-host-user", "/run/host/user-fonts"),
            ("voidstrap-nix-system-x11", "/run/current-system/sw/share/X11/fonts"),
            ("voidstrap-nix-system", "/run/current-system/sw/share/fonts"),
            ("voidstrap-guix-system", "/run/current-system/profile/share/fonts")
        ];
        if (!string.IsNullOrWhiteSpace(user))
        {
            candidates.Add(("voidstrap-nix-etc-x11", Path.Combine("/etc/profiles/per-user", user, "share", "X11", "fonts")));
            candidates.Add(("voidstrap-nix-etc", Path.Combine("/etc/profiles/per-user", user, "share", "fonts")));
        }
        if (!string.IsNullOrWhiteSpace(home))
        {
            candidates.Add(("voidstrap-nix-user-x11", Path.Combine(home, ".nix-profile", "share", "X11", "fonts")));
            candidates.Add(("voidstrap-nix-user", Path.Combine(home, ".nix-profile", "share", "fonts")));
            candidates.Add(("voidstrap-nix-state-x11", Path.Combine(home, ".local", "state", "nix", "profile", "share", "X11", "fonts")));
            candidates.Add(("voidstrap-nix-state", Path.Combine(home, ".local", "state", "nix", "profile", "share", "fonts")));
            candidates.Add(("voidstrap-guix-user", Path.Combine(home, ".guix-profile", "share", "fonts")));
        }

        string? xdgDataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (!string.IsNullOrWhiteSpace(xdgDataDirs))
        {
            int xdgIndex = 0;
            foreach (string entry in xdgDataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Path.IsPathRooted(entry))
                    continue;
                candidates.Add(($"voidstrap-xdg-{xdgIndex}-fonts", Path.Combine(entry, "fonts")));
                candidates.Add(($"voidstrap-xdg-{xdgIndex}-x11", Path.Combine(entry, "X11", "fonts")));
                xdgIndex++;
            }
        }

        System.Collections.Generic.HashSet<string> activeLinks = new(StringComparer.Ordinal);
        foreach ((string name, string target) in candidates)
        {
            try
            {
                string link = Path.Combine(fontsRoot, name);
                if (!Directory.Exists(target))
                {
                    RemoveSymlinkIfPresent(link);
                    continue;
                }

                DirectoryInfo targetInfo = new(target);
                string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetInfo.ResolveLinkTarget(true)?.FullName ?? targetInfo.FullName));
                if (!seenTargets.Add(resolved))
                {
                    RemoveSymlinkIfPresent(link);
                    continue;
                }

                activeLinks.Add(name);
                DirectoryInfo existingDir = new(link);
                FileInfo existingFile = new(link);
                if (existingDir.LinkTarget != null || existingFile.LinkTarget != null)
                {
                    if (Directory.Exists(link) && string.Equals(existingDir.LinkTarget, target, StringComparison.Ordinal))
                        continue;
                    RemoveSymlinkIfPresent(link);
                }
                else if (Directory.Exists(link) || File.Exists(link))
                {
                    continue;
                }

                Directory.CreateSymbolicLink(link, target);
            }
            catch
            {
            }
        }

        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(fontsRoot, "voidstrap-*"))
            {
                string name = Path.GetFileName(entry);
                if (!activeLinks.Contains(name) && !Directory.Exists(entry))
                    RemoveSymlinkIfPresent(entry);
            }
        }
        catch
        {
        }
    }

    private static void RemoveSymlinkIfPresent(string path)
    {
        try
        {
            DirectoryInfo directory = new(path);
            if (directory.LinkTarget != null)
            {
                directory.Delete();
                return;
            }
            FileInfo file = new(path);
            if (file.LinkTarget != null)
            {
                file.Delete();
            }
        }
        catch
        {
        }
    }

    private static bool WriteAliasConfig(string home)
    {
        try
        {
            string configDirectory = Path.Combine(home, ".config", "fontconfig", "conf.d");
            Directory.CreateDirectory(configDirectory);
            string configPath = Path.Combine(configDirectory, "60-voidstrap-segoe.conf");
            if (File.Exists(configPath) && File.ReadAllText(configPath) == AliasConfig)
            {
                return false;
            }
            File.WriteAllText(configPath, AliasConfig);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task RefreshPendingAsync(CancellationToken token)
    {
        string? fontDirectory = Interlocked.Exchange(ref _pendingFontCache, null);
        if (fontDirectory == null)
            return;
        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "fc-cache",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(fontDirectory);
            using Process? process = Process.Start(startInfo);
            if (process != null)
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    if (process.ExitCode == 0)
                        File.Delete(Path.Combine(fontDirectory, "cache-refresh-pending"));
                }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                    if (token.IsCancellationRequested)
                        throw;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }
}
