using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Voidstrap.Utility
{
    public sealed class ExternalEditorInfo
    {
        public string Name { get; init; } = "";

        public string Path { get; init; } = "";

        public string Arguments { get; init; } = "\"{0}\"";
    }

    public static class ExternalEditor
    {
        private const string LOG_IDENT = "ExternalEditor";

        private static readonly (string Name, string Relative)[] Candidates =
        {
            ("Visual Studio Code", @"Programs\Microsoft VS Code\Code.exe"),
            ("Visual Studio Code Insiders", @"Programs\Microsoft VS Code Insiders\Code - Insiders.exe"),
            ("Cursor", @"Programs\cursor\Cursor.exe"),
            ("Windsurf", @"Programs\Windsurf\Windsurf.exe"),
            ("Zed", @"Zed\Zed.exe"),
            ("Zed", @"Programs\Zed\Zed.exe")
        };

        private static readonly (string Name, string Absolute)[] MachineCandidates =
        {
            ("Visual Studio Code", @"Microsoft VS Code\Code.exe"),
            ("Sublime Text", @"Sublime Text\sublime_text.exe"),
            ("Sublime Text", @"Sublime Text 3\sublime_text.exe"),
            ("Notepad++", @"Notepad++\notepad++.exe")
        };

        private static List<ExternalEditorInfo>? _cache;

        private const string LinuxDefaultApp = "default";

        private static readonly (string Name, string Command)[] LinuxCandidates =
        {
            ("Visual Studio Code", "code"),
            ("VSCodium", "codium"),
            ("Zed", "zeditor"),
            ("Zed", "zed"),
            ("Sublime Text", "subl"),
            ("GNOME Text Editor", "gnome-text-editor"),
            ("gedit", "gedit"),
            ("Kate", "kate"),
            ("KWrite", "kwrite"),
            ("Mousepad", "mousepad"),
            ("Xed", "xed"),
            ("Pluma", "pluma")
        };

        private static List<ExternalEditorInfo> DetectLinux()
        {
            List<ExternalEditorInfo> found = new() { new ExternalEditorInfo { Name = "Default text editor", Path = LinuxDefaultApp } };
            try
            {
                if (Voidstrap.Platform.Linux.LinuxFlatpakHost.IsSandboxed)
                    return found;
                Voidstrap.Core.SystemProcessService processes = new();
                foreach ((string name, string command) in LinuxCandidates)
                {
                    string? resolved = processes.FindExecutable(command);
                    if (string.IsNullOrWhiteSpace(resolved) || found.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    found.Add(new ExternalEditorInfo { Name = name, Path = resolved });
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Editor detection failed: " + ex.Message);
            }
            return found;
        }

        private static bool OpenLinux(ExternalEditorInfo editor, string filePath)
        {
            if (editor.Path == LinuxDefaultApp)
                return PlatformShell.TryOpenUrl(filePath);
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo(editor.Path)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory
                };
                startInfo.ArgumentList.Add(filePath);
                using Process? process = Process.Start(startInfo);
                return process != null;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Could not open " + editor.Name + ": " + ex.Message);
                return PlatformShell.TryOpenUrl(filePath);
            }
        }

        public static IReadOnlyList<ExternalEditorInfo> Detect()
        {
            if (_cache != null)
                return _cache;

            if (Voidstrap.Utility.Platform.IsLinux)
            {
                _cache = DetectLinux();
                return _cache;
            }

            List<ExternalEditorInfo> found = new();
            void Add(string name, string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return;
                if (found.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return;
                if (found.Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                    return;
                found.Add(new ExternalEditorInfo { Name = name, Path = path });
            }

            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                foreach ((string name, string relative) in Candidates)
                    Add(name, Path.Combine(localAppData, relative));

                foreach (string root in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                })
                {
                    if (string.IsNullOrEmpty(root))
                        continue;
                    foreach ((string name, string absolute) in MachineCandidates)
                        Add(name, Path.Combine(root, absolute));
                }

                foreach ((string name, string command) in new[]
                {
                    ("Visual Studio Code", "code.cmd"),
                    ("Zed", "zed.exe"),
                    ("Sublime Text", "subl.exe"),
                    ("Neovim", "nvim.exe")
                })
                {
                    string? resolved = FromPath(command);
                    if (resolved != null)
                        Add(name, resolved);
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Editor detection failed: " + ex.Message);
            }

            string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
            found.Add(new ExternalEditorInfo { Name = "Notepad", Path = File.Exists(notepad) ? notepad : "notepad.exe" });
            _cache = found;
            return found;
        }

        private static string? FromPath(string executable)
        {
            try
            {
                string? paths = Environment.GetEnvironmentVariable("PATH");
                if (string.IsNullOrEmpty(paths))
                    return null;
                foreach (string dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = Path.Combine(dir.Trim(), executable);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
            }
            return null;
        }

        public static bool Open(ExternalEditorInfo editor, string filePath)
        {
            if (Voidstrap.Utility.Platform.IsLinux)
                return OpenLinux(editor, filePath);
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = editor.Path,
                    Arguments = string.Format(editor.Arguments, filePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory
                });
                return true;
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine(LOG_IDENT, "Could not open " + editor.Name + ": " + ex.Message);
                return false;
            }
        }
    }
}
