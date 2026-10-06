using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Voidstrap.Resources;

namespace Voidstrap.Utility;

internal static class SwiftTunnel
{
    public const string Icon = "pack://application:,,,/Voidstrap;component/Resources/AppImages/ext-swifttunnel.png";

    public static string Text(string key) => Strings.ResourceManager.GetString("SwiftTunnel." + key, Strings.Culture) ?? string.Empty;

    public static async Task<(string Name, string Url, string Digest)> GetLatestInstallerAsync(HttpClient client, CancellationToken token)
    {
        using JsonDocument release = JsonDocument.Parse(await Http.GetStringBoundedAsync(client, "https://api.github.com/repos/Swift-tunnel/swifttunnel-app/releases/latest", token: token));
        return SelectInstaller(release.RootElement, RuntimeInformation.OSArchitecture == Architecture.Arm64);
    }

    private static (string Name, string Url, string Digest) SelectInstaller(JsonElement release, bool arm)
    {
        if (release.ValueKind == JsonValueKind.Object && release.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object)
                    continue;
                string name = ReadString(asset, "name");
                string url = ReadString(asset, "browser_download_url");
                string digest = ReadString(asset, "digest");
                if (name.StartsWith("SwiftTunnel_", StringComparison.Ordinal) && name.EndsWith(arm ? "_arm64_en-US.msi" : "_x64_en-US.msi", StringComparison.Ordinal)
                    && Path.GetFileName(name) == name && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                    && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                    && uri.AbsolutePath.StartsWith("/Swift-tunnel/swifttunnel-app/releases/download/", StringComparison.OrdinalIgnoreCase)
                    && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length == 71 && digest[7..].All(Uri.IsHexDigit))
                    return (name, url, digest[7..]);
            }
        }
        throw new InvalidDataException(Text("NoInstaller"));
    }

    private static string ReadString(JsonElement element, string property) => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    public static (string ProductCode, string Executable)? FindInstallation()
    {
        if (!Platform.IsWindows)
            return null;
        try
        {
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall == null)
                    continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(name);
                    if (entry == null || !string.Equals(entry.GetValue("DisplayName") as string, "SwiftTunnel", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string product = Guid.TryParse(name, out Guid key) ? key.ToString("B") : ReadProductCode(entry.GetValue("UninstallString") as string);
                    return (product, LocateExecutable(entry));
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine("SwiftTunnel::FindInstallation", ex.Message);
        }
        return null;
    }

    private static readonly string[] ExecutableNames = ["swifttunnel-desktop.exe", "SwiftTunnel.exe"];

    private static readonly string[] ProcessNames = ["swifttunnel-desktop", "SwiftTunnel"];

    private static string ReadProductCode(string? uninstallString)
    {
        if (string.IsNullOrEmpty(uninstallString))
            return string.Empty;
        int open = uninstallString.IndexOf('{');
        int close = open < 0 ? -1 : uninstallString.IndexOf('}', open);
        return close > open && Guid.TryParse(uninstallString[open..(close + 1)], out Guid product) ? product.ToString("B") : string.Empty;
    }

    private static string LocateExecutable(RegistryKey entry)
    {
        string displayIcon = (entry.GetValue("DisplayIcon") as string ?? string.Empty).Split(',')[0].Trim('"', ' ');
        string[] directories =
        [
            (entry.GetValue("InstallLocation") as string ?? string.Empty).Trim('"', ' '),
            displayIcon.Length > 0 ? Path.GetDirectoryName(displayIcon) ?? string.Empty : string.Empty,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SwiftTunnel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SwiftTunnel")
        ];
        foreach (string directory in directories)
        {
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
                continue;
            foreach (string executable in ExecutableNames)
            {
                string path = Path.Combine(directory, executable);
                if (File.Exists(path))
                    return path;
            }
        }
        return string.Empty;
    }

    private static bool IsRunning()
    {
        foreach (string name in ProcessNames)
        {
            Process[] running = Process.GetProcessesByName(name);
            try
            {
                if (running.Any(process => !process.HasExited))
                    return true;
            }
            catch (Exception)
            {
                return true;
            }
            finally
            {
                foreach (Process process in running)
                    process.Dispose();
            }
        }
        return false;
    }

    public static void Open()
    {
        try
        {
            var installation = FindInstallation();
            if (installation == null || !File.Exists(installation.Value.Executable))
                throw new FileNotFoundException(Text(installation == null ? "NotInstalledError" : "Missing"));
            if (IsRunning())
                return;
            Process.Start(new ProcessStartInfo(installation.Value.Executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(installation.Value.Executable) ?? string.Empty });
        }
        catch (Exception ex)
        {
            Frontend.ShowMessageBox(string.Format(Text("Error"), ex.Message), System.Windows.MessageBoxImage.Error);
        }
    }

    public static async Task UninstallAsync((string ProductCode, string Executable) installation, CancellationToken token)
    {
        if (!Guid.TryParse(installation.ProductCode, out Guid product))
            throw new InvalidOperationException(Text("UninstallUnavailable"));
        bool hasExecutable = File.Exists(installation.Executable);
        string installDirectory = hasExecutable ? Path.GetDirectoryName(Path.GetFullPath(installation.Executable)) ?? string.Empty : string.Empty;
        bool ownDirectory = installDirectory.Length > 0 && Path.GetFileName(Path.TrimEndingDirectorySeparator(installDirectory)).Equals("SwiftTunnel", StringComparison.OrdinalIgnoreCase);
        string[] directories =
        [
            ownDirectory ? installDirectory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SwiftTunnel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SwiftTunnel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SwiftTunnel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftTunnel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SwiftTunnel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "net.swifttunnel.desktop"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "net.swifttunnel.desktop"),
            Path.Combine(Paths.Cache, "SwiftTunnel"),
            Path.Combine(Path.GetTempPath(), "SwiftTunnel")
        ];
        string removal = BuildRemovalScript(directories);
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            ProductCode = product.ToString("B"),
            Executable = hasExecutable ? Path.GetFullPath(installation.Executable) : string.Empty,
            Directory = installDirectory,
            Installer = Path.Combine(Environment.SystemDirectory, "msiexec.exe")
        })));
        string script = "$payload = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + payload + "')) | ConvertFrom-Json\n" + """
            $ErrorActionPreference = 'Stop'
            try {
                $prefix = $payload.Directory.TrimEnd('\') + '\'
                $names = @('SwiftTunnel.exe', 'swifttunnel-desktop.exe')
                Get-CimInstance Win32_Process | Where-Object {
                    $_.ExecutablePath -and $names -contains $_.Name -and ($payload.Directory -eq '' -or $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))
                } | ForEach-Object {
                    $running = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
                    if ($running) {
                        $running | Stop-Process -Force
                        if (-not $running.WaitForExit(10000)) { throw 'SwiftTunnel process did not exit' }
                    }
                }
                if ($payload.Executable -ne '') {
                    $cleanup = Start-Process -FilePath $payload.Executable -ArgumentList '--cleanup' -PassThru -WindowStyle Hidden
                    if (-not $cleanup.WaitForExit(60000)) { $cleanup | Stop-Process -Force; exit 10 }
                    if ($cleanup.ExitCode -ne 0) { exit 10 }
                }
                $uninstaller = Start-Process -FilePath $payload.Installer -ArgumentList @('/x', $payload.ProductCode, '/norestart') -PassThru -Wait
                if ($uninstaller.ExitCode -eq 1602) { exit 1602 }
                if ($uninstaller.ExitCode -notin @(0, 1641, 3010)) { exit 11 }
            } catch { exit 12 }
            """ + "\n" + removal;
        token.ThrowIfCancellationRequested();
        Process? started;
        try
        {
            started = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return;
        }
        using Process? process = started;
        if (process == null)
            throw new InvalidOperationException(Text("InstallerFailed"));
        await process.WaitForExitAsync();
        if (process.ExitCode == 1602)
            return;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Text(process.ExitCode == 10 ? "CleanupFailed" : "UninstallIncomplete"));
    }

    private static string BuildRemovalScript(string[] directories)
    {
        string[] targets = directories.Select(directory =>
        {
            if (!Path.IsPathFullyQualified(directory))
                throw new InvalidOperationException(Text("UnsafeDirectory"));
            string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string name = Path.GetFileName(path);
            if (!name.Equals("SwiftTunnel", StringComparison.OrdinalIgnoreCase) && !name.Equals("net.swifttunnel.desktop", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(Text("UnsafeDirectory"));
            return path;
        }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(targets)));
        return "$targets = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + data + "')) | ConvertFrom-Json\n" + """
            $ErrorActionPreference = 'Stop'
            function Remove-AppDirectory([IO.DirectoryInfo] $directory) {
                if (-not $directory.Exists) { return }
                if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    $directory.Delete()
                    return
                }
                foreach ($item in $directory.GetFileSystemInfos()) {
                    if ($item -is [IO.DirectoryInfo]) {
                        Remove-AppDirectory $item
                    } else {
                        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { $item.Attributes = [IO.FileAttributes]::Normal }
                        $item.Delete()
                    }
                }
                $directory.Delete()
            }
            try {
                foreach ($target in $targets) {
                    $resolved = [IO.Path]::GetFullPath($target).TrimEnd('\')
                    if ($resolved -ne $target -or [IO.Path]::GetFileName($resolved) -notin @('SwiftTunnel', 'net.swifttunnel.desktop')) { throw 'Invalid removal directory' }
                    $directory = [IO.DirectoryInfo]::new($resolved)
                    $parent = $directory.Parent
                    while ($parent) {
                        if ($parent.Exists -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked parent directory' }
                        $parent = $parent.Parent
                    }
                }
                foreach ($target in $targets) {
                    for ($attempt = 0; $attempt -lt 5; $attempt++) {
                        try { Remove-AppDirectory ([IO.DirectoryInfo]::new($target)); break }
                        catch { if ($attempt -eq 4) { throw }; Start-Sleep -Milliseconds 500 }
                    }
                }
                exit 0
            } catch { exit 13 }
            """;
    }
}
