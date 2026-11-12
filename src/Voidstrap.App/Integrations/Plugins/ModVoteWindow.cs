using System.Windows;
#if CROSSPLAT
using System.Runtime.InteropServices;
#else
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
#endif

namespace Voidstrap.Integrations.Plugins;

internal static class ModVoteWindow
{
    private const double WindowWidth = 420;

    private const double WindowHeight = 460;

    private static string DataFolder => Path.Combine(Paths.Cache, "ModVoteWeb");

    public static void Show(string modId, int vote, Action<ModVotes.Tally?, int> done)
    {
        string url = ModVotes.VoteUrl(modId, vote);
#if CROSSPLAT
        if (Voidstrap.Utility.Platform.IsMacOS)
        {
            new MacVoteWindow(url, done) { Title = App.ProjectName, Width = WindowWidth, Height = WindowHeight, WindowStartupLocation = WindowStartupLocation.CenterScreen }.Show();
            return;
        }
        if (NativeLibrary.TryLoad(Voidstrap.UI.LinuxWebKitInterop.WebKit, out _) && NativeLibrary.TryLoad(Voidstrap.UI.LinuxWebKitInterop.Gtk, out _) && Voidstrap.UI.LinuxWebPanel.EnsureGtk())
        {
            Directory.CreateDirectory(DataFolder);
            Voidstrap.UI.LinuxWebPanel.Post(() => LinuxVoteWindow.Open(url, done));
            return;
        }
        Utilities.OpenWebLink(url);
        done(null, -1);
#else
        new VoteWindow(url, done)
        {
            Title = App.ProjectName,
            Width = WindowWidth,
            Height = WindowHeight,
            ResizeMode = ResizeMode.NoResize,
            Owner = Application.Current?.MainWindow is { IsVisible: true } owner ? owner : null,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        }.Show();
#endif
    }

#if CROSSPLAT
    private sealed class MacVoteWindow : Window
    {
        private readonly string _start;

        private readonly Action<ModVotes.Tally?, int> _done;

        private Voidstrap.Platform.MacOS.MacOSWebView? _view;

        private bool _finished;

        public MacVoteWindow(string start, Action<ModVotes.Tally?, int> done)
        {
            _start = start;
            _done = done;
            ContentRendered += OnRendered;
            SizeChanged += OnResized;
            Closed += OnClosed;
        }

        private void OnRendered(object? sender, EventArgs e)
        {
            const string LOG_IDENT = "ModVoteWindow::OnRendered";
            try
            {
                nint native = Voidstrap.UI.MacWindowMode.ResolveNativeWindow(this);
                _view = new Voidstrap.Platform.MacOS.MacOSWebView(native, "", "", [], 0, Decide, _start, new Guid("6d6f6476-6f74-6573-0000-000000000001"));
                Place();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The vote window could not start: " + ex.Message);
                Finish(null, 503);
            }
        }

        private bool Decide(string uri)
        {
            if (ModVotes.TryReadResult(uri, out ModVotes.Tally? tally, out int error))
            {
                Dispatcher.BeginInvoke(new Action(() => Finish(tally, error)));
                return false;
            }
            return ModVotes.IsAllowedFrame(uri);
        }

        private void Finish(ModVotes.Tally? tally, int error)
        {
            if (_finished)
                return;
            _finished = true;
            _done(tally, error);
            Close();
        }

        private void OnResized(object sender, SizeChangedEventArgs e) => Place();

        private void Place() => _view?.SetFrame(0, 0, ActualWidth, ActualHeight, true);

        private void OnClosed(object? sender, EventArgs e)
        {
            ContentRendered -= OnRendered;
            SizeChanged -= OnResized;
            Closed -= OnClosed;
            _view?.Dispose();
            _view = null;
            if (!_finished)
            {
                _finished = true;
                _done(null, 0);
            }
        }
    }

    private static class LinuxVoteWindow
    {
        private static readonly List<Delegate> Handlers = [];

        public static void Open(string url, Action<ModVotes.Tally?, int> done)
        {
            string data = DataFolder;
            IntPtr manager = Voidstrap.UI.LinuxWebKitInterop.webkit_website_data_manager_new("base-data-directory", data, "base-cache-directory", Path.Combine(data, "cache"), IntPtr.Zero);
            IntPtr view = Voidstrap.UI.LinuxWebKitInterop.webkit_web_view_new_with_context(Voidstrap.UI.LinuxWebKitInterop.webkit_web_context_new_with_website_data_manager(manager));
            IntPtr settings = Voidstrap.UI.LinuxWebKitInterop.webkit_web_view_get_settings(view);
            Voidstrap.UI.LinuxWebKitInterop.webkit_settings_set_enable_developer_extras(settings, false);
            Voidstrap.UI.LinuxWebKitInterop.webkit_settings_set_javascript_can_open_windows_automatically(settings, false);
            IntPtr window = Voidstrap.UI.LinuxWebKitInterop.gtk_window_new(0);
            bool finished = false;
            void Finish(ModVotes.Tally? tally, int error)
            {
                if (finished)
                    return;
                finished = true;
                Application.Current?.Dispatcher.BeginInvoke(new Action(() => done(tally, error)));
            }
            Voidstrap.UI.LinuxWebKitInterop.DecidePolicyHandler policy = (_, decision, type, _) =>
            {
                if (type == 2)
                    return false;
                IntPtr action = Voidstrap.UI.LinuxWebKitInterop.webkit_navigation_policy_decision_get_navigation_action(decision);
                IntPtr request = action == IntPtr.Zero ? IntPtr.Zero : Voidstrap.UI.LinuxWebKitInterop.webkit_navigation_action_get_request(action);
                string uri = (request == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Voidstrap.UI.LinuxWebKitInterop.webkit_uri_request_get_uri(request))) ?? "";
                if (ModVotes.TryReadResult(uri, out ModVotes.Tally? tally, out int error))
                {
                    Voidstrap.UI.LinuxWebKitInterop.webkit_policy_decision_ignore(decision);
                    Finish(tally, error);
                    Voidstrap.UI.LinuxWebPanel.Post(() => Voidstrap.UI.LinuxWebKitInterop.gtk_widget_destroy(window));
                    return true;
                }
                if (type == 0 && ModVotes.IsAllowedFrame(uri))
                    return false;
                Voidstrap.UI.LinuxWebKitInterop.webkit_policy_decision_ignore(decision);
                return true;
            };
            Voidstrap.UI.LinuxWebKitInterop.ContextMenuHandler menu = (_, _, _, _, _) => true;
            Voidstrap.UI.LinuxWebKitInterop.PermissionHandler permission = (_, permissionRequest, _) =>
            {
                Voidstrap.UI.LinuxWebKitInterop.webkit_permission_request_deny(permissionRequest);
                return true;
            };
            List<Delegate> owned = [];
            Voidstrap.UI.LinuxWebKitInterop.DestroyHandler destroyed = (_, _) =>
            {
                Finish(null, 0);
                lock (Handlers)
                    foreach (Delegate handler in owned)
                        Handlers.Remove(handler);
            };
            owned.AddRange([policy, menu, permission, destroyed]);
            lock (Handlers)
                Handlers.AddRange(owned);
            Connect(view, "decide-policy", policy);
            Connect(view, "context-menu", menu);
            Connect(view, "permission-request", permission);
            Connect(window, "destroy", destroyed);
            Voidstrap.UI.LinuxWebKitInterop.gtk_window_set_title(window, App.ProjectName);
            Voidstrap.UI.LinuxWebKitInterop.gtk_window_resize(window, (int)WindowWidth, (int)WindowHeight);
            Voidstrap.UI.LinuxWebKitInterop.gtk_container_add(window, view);
            Voidstrap.UI.LinuxWebKitInterop.gtk_widget_show_all(window);
            Voidstrap.UI.LinuxWebKitInterop.webkit_web_view_load_uri(view, url);
        }

        private static void Connect(IntPtr target, string signal, Delegate handler) =>
            Voidstrap.UI.LinuxWebKitInterop.g_signal_connect_data(target, signal, Marshal.GetFunctionPointerForDelegate(handler), IntPtr.Zero, IntPtr.Zero, 0);
    }
#else
    private sealed class VoteWindow : Window
    {
        private readonly string _start;

        private readonly Action<ModVotes.Tally?, int> _done;

        private readonly WebView2 _view = new();

        private CoreWebView2? _core;

        private bool _finished;

        public VoteWindow(string start, Action<ModVotes.Tally?, int> done)
        {
            _start = start;
            _done = done;
            Content = _view;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            const string LOG_IDENT = "ModVoteWindow::OnLoaded";
            try
            {
                Directory.CreateDirectory(DataFolder);
                CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, DataFolder, new CoreWebView2EnvironmentOptions("--disable-sync --disable-extensions --no-first-run --no-default-browser-check"));
                await _view.EnsureCoreWebView2Async(environment);
                if (_finished)
                    return;
                _core = _view.CoreWebView2;
                CoreWebView2Settings settings = _core.Settings;
                settings.AreDevToolsEnabled = false;
                settings.AreHostObjectsAllowed = false;
                settings.IsWebMessageEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.AreDefaultContextMenusEnabled = false;
                settings.AreBrowserAcceleratorKeysEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.IsZoomControlEnabled = false;
                _view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(11, 11, 13);
                _core.NavigationStarting += OnNavigationStarting;
                _core.FrameNavigationStarting += OnFrameNavigationStarting;
                _core.NewWindowRequested += OnNewWindow;
                _core.DownloadStarting += OnDownload;
                _core.PermissionRequested += OnPermission;
                _core.Navigate(_start);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "The vote window could not start: " + ex.Message);
                Finish(null, 503);
            }
        }

        private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (ModVotes.TryReadResult(e.Uri, out ModVotes.Tally? tally, out int error))
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(() => Finish(tally, error)));
                return;
            }
            if (!ModVotes.IsWebsite(e.Uri))
                e.Cancel = true;
        }

        private void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (!ModVotes.IsAllowedFrame(e.Uri))
                e.Cancel = true;
        }

        private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e) => e.Handled = true;

        private void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e) => e.Cancel = true;

        private void OnPermission(object? sender, CoreWebView2PermissionRequestedEventArgs e) => e.State = CoreWebView2PermissionState.Deny;

        private void Finish(ModVotes.Tally? tally, int error)
        {
            if (_finished)
                return;
            _finished = true;
            _done(tally, error);
            Close();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            Loaded -= OnLoaded;
            Closed -= OnClosed;
            if (_core != null)
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.FrameNavigationStarting -= OnFrameNavigationStarting;
                _core.NewWindowRequested -= OnNewWindow;
                _core.DownloadStarting -= OnDownload;
                _core.PermissionRequested -= OnPermission;
                _core = null;
            }
            _view.Dispose();
            if (!_finished)
            {
                _finished = true;
                _done(null, 0);
            }
        }
    }
#endif
}
