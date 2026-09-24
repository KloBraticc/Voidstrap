using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
#if CROSSPLAT
using ProGPU.Wpf.Interop;
#endif

namespace Voidstrap.UI
{
    public static class LinuxFileDialog
    {
#if !CROSSPLAT
        public static void Install()
        {
        }

        public static void Uninstall()
        {
        }
#else
        private static readonly string[] CandidateTools = { "zenity", "qarma", "yad", "kdialog" };

        private static readonly TimeSpan PickerLifetime = TimeSpan.FromMinutes(10);

        private static readonly List<IDisposable> Registrations = new();

        private static bool _installed;

        private static string? _tool;

        public static void Install()
        {
            if (_installed || !Voidstrap.Utility.Platform.IsLinux)
                return;

            _tool = ResolveTool();
            if (_tool is null)
                App.Logger.WriteLine("LinuxFileDialog::Install", "No portable file picker was found, file dialogs will ask for zenity or kdialog");

            _installed = true;
            PortableWpfServiceRegistry.FileDialogServiceRegistered += OnFileDialogServiceRegistered;

            if (PortableWpfServiceRegistry.TryGetFileDialogService(PortableWpfServiceKey.PresentationFramework, out IPortableFileDialogServiceRegistrar framework))
                Attach(framework);

            if (PortableWpfServiceRegistry.TryGetFileDialogService(PortableWpfServiceKey.WinForms, out IPortableFileDialogServiceRegistrar winForms))
                Attach(winForms);

            if (_tool is not null)
                App.Logger.WriteLine("LinuxFileDialog::Install", $"Registered the {Path.GetFileName(_tool)} file picker");
        }

        public static void Uninstall()
        {
            if (!_installed)
                return;

            _installed = false;
            PortableWpfServiceRegistry.FileDialogServiceRegistered -= OnFileDialogServiceRegistered;
            foreach (IDisposable registration in Registrations)
            {
                try
                {
                    registration.Dispose();
                }
                catch (Exception)
                {
                }
            }

            Registrations.Clear();
        }

        private static void OnFileDialogServiceRegistered(IPortableFileDialogServiceRegistrar service)
        {
            Attach(service);
        }

        private static void Attach(IPortableFileDialogServiceRegistrar service)
        {
            if (service is null)
                return;

            try
            {
                Registrations.Add(service.RegisterResult(Show));
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LinuxFileDialog::Attach", $"Could not register the picker: {ex.Message}");
            }
        }

        private static string? ResolveTool()
        {
            foreach (string candidate in CandidateTools)
            {
                foreach (string directory in new[] { "/usr/bin", "/bin", "/usr/local/bin" })
                {
                    string path = Path.Combine(directory, candidate);
                    if (File.Exists(path))
                        return path;
                }
            }

            return null;
        }

        private static PortableFileDialogResult? Show(PortableFileDialogRequest request)
        {
            if (request is null)
                return null;

            if (_tool is null)
            {
                Frontend.ShowMessageBox("Voidstrap needs a file picker to choose files on Linux. Install zenity or kdialog with your package manager, then try again.", System.Windows.MessageBoxImage.Information);
                return null;
            }

            try
            {
                ProcessStartInfo startInfo = new()
                {
                    FileName = _tool,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                bool kdialog = string.Equals(Path.GetFileName(_tool), "kdialog", StringComparison.Ordinal);
                foreach (string argument in kdialog ? BuildKDialogArguments(request) : BuildArguments(request))
                    startInfo.ArgumentList.Add(argument);

                using Process? picker = Process.Start(startInfo);
                if (picker is null)
                    return null;

                string output = picker.StandardOutput.ReadToEnd();
                if (!picker.WaitForExit((int)PickerLifetime.TotalMilliseconds))
                {
                    try
                    {
                        picker.Kill(true);
                    }
                    catch (Exception)
                    {
                    }

                    return null;
                }

                if (picker.ExitCode != 0)
                    return null;

                string[] selected = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (selected.Length == 0)
                    return null;

                return request.AllowMultipleSelection
                    ? new PortableFileDialogResult(selected)
                    : new PortableFileDialogResult(selected[0]);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LinuxFileDialog::Show", $"The picker failed: {ex.Message}");
                return null;
            }
        }

        private static List<string> BuildArguments(PortableFileDialogRequest request)
        {
            List<string> arguments = new() { "--file-selection" };

            if (!string.IsNullOrWhiteSpace(request.Title))
                arguments.Add("--title=" + request.Title);

            if (string.Equals(request.Kind, "SaveFile", StringComparison.Ordinal))
            {
                arguments.Add("--save");
                arguments.Add("--confirm-overwrite");
            }
            else if (string.Equals(request.Kind, "OpenFolder", StringComparison.Ordinal))
            {
                arguments.Add("--directory");
            }

            if (request.AllowMultipleSelection)
            {
                arguments.Add("--multiple");
                arguments.Add("--separator=\n");
            }

            string start = ResolveStartPath(request);
            if (start.Length > 0)
                arguments.Add("--filename=" + start);

            foreach (string filter in BuildFilters(request.Filter))
                arguments.Add(filter);

            return arguments;
        }

        private static List<string> BuildKDialogArguments(PortableFileDialogRequest request)
        {
            List<string> arguments = new();
            if (!string.IsNullOrWhiteSpace(request.Title))
            {
                arguments.Add("--title");
                arguments.Add(request.Title);
            }

            string start = ResolveStartPath(request);
            if (start.Length == 0)
                start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (string.Equals(request.Kind, "OpenFolder", StringComparison.Ordinal))
            {
                arguments.Add("--getexistingdirectory");
                arguments.Add(start);
                return arguments;
            }

            if (string.Equals(request.Kind, "SaveFile", StringComparison.Ordinal))
            {
                arguments.Add("--getsavefilename");
            }
            else
            {
                if (request.AllowMultipleSelection)
                {
                    arguments.Add("--multiple");
                    arguments.Add("--separate-output");
                }

                arguments.Add("--getopenfilename");
            }

            arguments.Add(start);
            string filter = BuildKDialogFilter(request.Filter);
            if (filter.Length > 0)
                arguments.Add(filter);

            return arguments;
        }

        private static string BuildKDialogFilter(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return string.Empty;

            List<string> entries = new();
            string[] parts = filter.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string label = parts[i].Trim();
                string patterns = string.Join(' ', parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (label.Length > 0 && patterns.Length > 0)
                    entries.Add(patterns + "|" + label.Replace('|', ' '));
            }

            return string.Join('\n', entries);
        }

        private static string ResolveStartPath(PortableFileDialogRequest request)
        {
            string directory = request.InitialDirectory;
            if (string.IsNullOrWhiteSpace(directory))
                directory = request.DefaultDirectory;

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return string.IsNullOrWhiteSpace(request.SuggestedItemName) ? string.Empty : request.SuggestedItemName;

            return string.IsNullOrWhiteSpace(request.SuggestedItemName)
                ? directory + "/"
                : Path.Combine(directory, request.SuggestedItemName);
        }

        private static List<string> BuildFilters(string filter)
        {
            List<string> filters = new();
            if (string.IsNullOrWhiteSpace(filter))
                return filters;

            string[] parts = filter.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string label = parts[i].Trim();
                string[] patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (label.Length == 0 || patterns.Length == 0)
                    continue;

                StringBuilder builder = new();
                builder.Append("--file-filter=");
                builder.Append(label);
                builder.Append('|');
                for (int p = 0; p < patterns.Length; p++)
                {
                    if (p > 0)
                        builder.Append(' ');
                    builder.Append(patterns[p]);
                }

                filters.Add(builder.ToString());
            }

            return filters;
        }
#endif
    }
}
