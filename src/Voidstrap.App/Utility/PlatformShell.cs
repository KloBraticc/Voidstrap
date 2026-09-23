using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Voidstrap.Core;

namespace Voidstrap.Utility;

internal static class PlatformShell
{
    public static bool TryOpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception)
        {
        }

        return TryStart(folder);
    }

    public static bool TryRevealFile(string? file)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return false;
        }

        try
        {
            if (Platform.IsWindows)
            {
                using Process? process = Process.Start(new ProcessStartInfo(WindowsTool("explorer.exe"), "/select,\"" + file + "\"") { UseShellExecute = false });
                return process is not null;
            }

            if (OperatingSystem.IsMacOS())
            {
                using Process? process = Process.Start(new ProcessStartInfo("open", "-R \"" + file + "\"") { UseShellExecute = false });
                return process is not null;
            }
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("PlatformShell::TryRevealFile", "The file could not be revealed: " + ex.Message);
        }

        return TryOpenFolder(Path.GetDirectoryName(file));
    }

    public static bool TryOpenUrl(string? url)
    {
        return !string.IsNullOrWhiteSpace(url) && TryStart(url);
    }

    private static bool TryStart(string target)
    {
        try
        {
            if (Platform.IsWindows || OperatingSystem.IsMacOS())
            {
                using Process? process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                return true;
            }

            return TryStartLinux(target);
        }
        catch (Exception ex)
        {
            if (Platform.IsWindows && TryStartWindowsFallback(target))
            {
                return true;
            }

            App.Logger?.WriteLine("PlatformShell::TryStart", "The location could not be opened: " + ex.Message);
            return false;
        }
    }

    internal static string WindowsTool(string name)
    {
        Environment.SpecialFolder folder = string.Equals(name, "explorer.exe", StringComparison.OrdinalIgnoreCase)
            ? Environment.SpecialFolder.Windows
            : Environment.SpecialFolder.System;
        return Path.Combine(Environment.GetFolderPath(folder), name);
    }

    private static readonly string[] TextExtensions =
    [
        ".log", ".txt", ".json", ".xml", ".ini", ".cfg", ".conf", ".yml", ".yaml", ".md", ".csv", ".xshd"
    ];

    private static bool TryStartWindowsFallback(string target)
    {
        bool isWeb = Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        (string file, string arguments)[] attempts = isWeb
            ? [(WindowsTool("explorer.exe"), "\"" + uri!.AbsoluteUri + "\"")]
            : File.Exists(target) && TextExtensions.Contains(Path.GetExtension(target), StringComparer.OrdinalIgnoreCase)
                ? [(WindowsTool("notepad.exe"), "\"" + target + "\"")]
                : [(WindowsTool("explorer.exe"), "\"" + target + "\"")];
        foreach ((string file, string arguments) in attempts)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true });
                if (process is not null)
                {
                    App.Logger?.WriteLine("PlatformShell::TryStart", "Opened " + target + " through " + file);
                    return true;
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("PlatformShell::TryStart", file + " could not open the location: " + ex.Message);
            }
        }
        return false;
    }

    private static bool TryStartLinux(string target)
    {
        SystemProcessService processes = new();
        foreach ((string command, string? verb) in new (string, string?)[]
        {
            ("xdg-open", null),
            ("gio", "open"),
            ("kde-open6", null),
            ("kde-open5", null)
        })
        {
            string? executable = processes.FindExecutable(command);
            if (string.IsNullOrWhiteSpace(executable))
                continue;

            try
            {
                ProcessStartInfo startInfo = new(executable)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                if (verb != null)
                    startInfo.ArgumentList.Add(verb);
                startInfo.ArgumentList.Add(target);
                using Process? opened = Process.Start(startInfo);
                if (opened != null)
                    return true;
            }
            catch (Exception)
            {
            }
        }

        return false;
    }
}
