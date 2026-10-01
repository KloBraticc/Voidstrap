using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
#if CROSSPLAT
using System.Threading.Tasks;
using ProGPU.Wpf.Interop;
using Tmds.DBus;
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

            _tool = Voidstrap.Platform.Linux.LinuxFlatpakHost.IsSandboxed ? null : ResolveTool();
            if (_tool is null)
                App.Logger.WriteLine("LinuxFileDialog::Install", "File dialogs use the desktop file chooser portal");

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
                PortableFileDialogResult? chosen = ShowPortal(request, out bool portalReached);
                if (portalReached)
                    return chosen;

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

        private const string PortalService = "org.freedesktop.portal.Desktop";

        private static readonly ObjectPath PortalPath = new("/org/freedesktop/portal/desktop");

        private static PortableFileDialogResult? ShowPortal(PortableFileDialogRequest request, out bool reached)
        {
            reached = false;
            try
            {
                Task<(bool Reached, string[]? Paths)> pick = Task.Run(() => PickWithPortalAsync(request));
                if (!pick.Wait(PickerLifetime))
                {
                    reached = true;
                    return null;
                }

                (bool portalReached, string[]? paths) = pick.Result;
                reached = portalReached;
                if (paths is null || paths.Length == 0)
                    return null;

                return request.AllowMultipleSelection
                    ? new PortableFileDialogResult(paths)
                    : new PortableFileDialogResult(paths[0]);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("LinuxFileDialog::ShowPortal", $"The file chooser portal failed: {ex.GetBaseException().Message}");
                return null;
            }
        }

        private static async Task<(bool Reached, string[]? Paths)> PickWithPortalAsync(PortableFileDialogRequest request)
        {
            using Connection connection = new(Address.Session);
            ConnectionInfo info = await connection.ConnectAsync().ConfigureAwait(false);

            string token = "voidstrap" + Guid.NewGuid().ToString("N");
            string sender = info.LocalName.TrimStart(':').Replace('.', '_');
            ObjectPath expected = new("/org/freedesktop/portal/desktop/request/" + sender + "/" + token);
            TaskCompletionSource<(uint Response, IDictionary<string, object> Results)> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            IPortalRequest pending = connection.CreateProxy<IPortalRequest>(PortalService, expected);
            IDisposable watch = await pending.WatchResponseAsync(
                value => completion.TrySetResult(value),
                error => completion.TrySetException(error)).ConfigureAwait(false);

            try
            {
                IFileChooserPortal chooser = connection.CreateProxy<IFileChooserPortal>(PortalService, PortalPath);
                string title = string.IsNullOrWhiteSpace(request.Title) ? "Voidstrap" : request.Title;
                Dictionary<string, object> options = BuildPortalOptions(request, token);
                ObjectPath handle;
                try
                {
                    handle = string.Equals(request.Kind, "SaveFile", StringComparison.Ordinal)
                        ? await chooser.SaveFileAsync(string.Empty, title, options).ConfigureAwait(false)
                        : await chooser.OpenFileAsync(string.Empty, title, options).ConfigureAwait(false);
                }
                catch (DBusException ex) when (ex.ErrorName.StartsWith("org.freedesktop.DBus.Error.", StringComparison.Ordinal))
                {
                    App.Logger.WriteLine("LinuxFileDialog::PickWithPortal", $"The file chooser portal is unavailable: {ex.ErrorName}");
                    return (false, null);
                }

                if (handle.ToString() != expected.ToString())
                {
                    watch.Dispose();
                    watch = await connection.CreateProxy<IPortalRequest>(PortalService, handle).WatchResponseAsync(
                        value => completion.TrySetResult(value),
                        error => completion.TrySetException(error)).ConfigureAwait(false);
                }

                (uint response, IDictionary<string, object> results) = await completion.Task.ConfigureAwait(false);
                if (response != 0 || !results.TryGetValue("uris", out object? value) || value is not string[] uris)
                    return (true, null);

                List<string> paths = new();
                foreach (string uri in uris)
                {
                    if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile)
                        paths.Add(parsed.LocalPath);
                }

                return (true, paths.ToArray());
            }
            finally
            {
                watch.Dispose();
            }
        }

        private static Dictionary<string, object> BuildPortalOptions(PortableFileDialogRequest request, string token)
        {
            Dictionary<string, object> options = new()
            {
                ["handle_token"] = token,
                ["modal"] = true
            };

            bool save = string.Equals(request.Kind, "SaveFile", StringComparison.Ordinal);
            if (string.Equals(request.Kind, "OpenFolder", StringComparison.Ordinal))
                options["directory"] = true;
            else if (!save && request.AllowMultipleSelection)
                options["multiple"] = true;

            if (save && !string.IsNullOrWhiteSpace(request.SuggestedItemName))
                options["current_name"] = Path.GetFileName(request.SuggestedItemName);

            string directory = string.IsNullOrWhiteSpace(request.InitialDirectory) ? request.DefaultDirectory : request.InitialDirectory;
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                byte[] encoded = Encoding.UTF8.GetBytes(Path.GetFullPath(directory));
                byte[] terminated = new byte[encoded.Length + 1];
                encoded.CopyTo(terminated, 0);
                options["current_folder"] = terminated;
            }

            List<(string, (uint, string)[])> filters = BuildPortalFilters(request.Filter);
            if (filters.Count > 0 && !string.Equals(request.Kind, "OpenFolder", StringComparison.Ordinal))
            {
                options["filters"] = filters.ToArray();
                int selected = request.FilterIndex - 1;
                if (selected >= 0 && selected < filters.Count)
                    options["current_filter"] = filters[selected];
            }

            return options;
        }

        private static List<(string, (uint, string)[])> BuildPortalFilters(string filter)
        {
            List<(string, (uint, string)[])> filters = new();
            if (string.IsNullOrWhiteSpace(filter))
                return filters;

            string[] parts = filter.Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                string label = parts[i].Trim();
                string[] patterns = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (label.Length == 0 || patterns.Length == 0)
                    continue;

                (uint, string)[] globs = new (uint, string)[patterns.Length];
                for (int p = 0; p < patterns.Length; p++)
                    globs[p] = (0u, patterns[p]);
                filters.Add((label, globs));
            }

            return filters;
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

#if CROSSPLAT
    [DBusInterface("org.freedesktop.portal.FileChooser")]
    public interface IFileChooserPortal : IDBusObject
    {
        Task<ObjectPath> OpenFileAsync(string parentWindow, string title, IDictionary<string, object> options);

        Task<ObjectPath> SaveFileAsync(string parentWindow, string title, IDictionary<string, object> options);
    }

    [DBusInterface("org.freedesktop.portal.Request")]
    public interface IPortalRequest : IDBusObject
    {
        Task<IDisposable> WatchResponseAsync(Action<(uint Response, IDictionary<string, object> Results)> handler, Action<Exception>? onError = null);
    }
#endif
}
