using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using DiscordRPC;
using Voidstrap.Integrations.AntiAliasing;
using Voidstrap.Integrations.RiShade;
using Voidstrap.UI.Elements.Crosshair;
using Voidstrap.UI.Elements.Overlay;
using Voidstrap.UI.ViewModels.Settings;

namespace Voidstrap.Utility
{
    public static partial class ClassicIntegrations
    {
        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrA", SetLastError = true)]
        private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrA", SetLastError = true)]
        private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [LibraryImport("psapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EmptyWorkingSet(IntPtr hProcess);

        private const int SW_HIDE = 0;
        private const int GWL_EXSTYLE = -20;
        private const long WS_EX_NOACTIVATE = 0x08000000;
        private const long WS_EX_TOOLWINDOW = 0x00000080;

        private static readonly object Sync = new object();
        private static DiscordRpcClient? _rpc;
        private static CancellationTokenSource? _cts;
        private static Process? _studioHost;
        private static Process? _player;
        private static bool _active;

        public static void Start(Process? player, Process? studioHost, string clientName, string map)
        {
            Stop();

            lock (Sync)
            {
                _active = true;
                _cts = new CancellationTokenSource();
                _studioHost = studioHost;
                _player = player;
            }

            CancellationToken token = _cts.Token;

            StartRichPresence(clientName, map);
            RunOnUi(CreateOverlays);

            if (studioHost != null)
                ManageStudioHost(studioHost, token);

            if (player == null)
                return;

            RunOnUi(HideVoidstrap);

            Task.Run(async delegate
            {
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (player.HasExited)
                            break;
                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
                EndSession();
            });
        }

        public static void HideVoidstrap()
        {
            try
            {
                foreach (Window window in Application.Current.Windows)
                {
                    if (window == null)
                        continue;
                    string ns = window.GetType().Namespace ?? "";
                    if (ns.Contains("Bootstrapper") || ns.Contains("Overlay") || ns.Contains("Crosshair") || ns.Contains("ContextMenu"))
                        continue;
                    HideWindow(window);
                }
            }
            catch
            {
            }
        }

        public static void HideWindow(Window window)
        {
            if (window == null)
                return;
            try { window.ShowInTaskbar = false; } catch { }
            try { window.WindowState = System.Windows.WindowState.Minimized; } catch { }
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero)
                {
                    long ex = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
                    SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
                    ShowWindow(handle, SW_HIDE);
                }
            }
            catch
            {
            }
            try { window.Visibility = Visibility.Hidden; } catch { }
        }

        private static void EndSession()
        {
            Stop();
            RunOnUi(delegate
            {
                try { Application.Current?.Shutdown(); } catch { }
            });
        }

        private static void CreateOverlays()
        {
            try
            {
				CrosshairWindow.Reconcile();
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicIntegrations", "Crosshair failed: " + ex.Message);
            }

            try
            {
                if (App.Settings.Prop.OverlaysEnabled)
                    ScreenColorEffect.ApplyConfigured();
                else
                    ScreenColorEffect.Reset();
                if (App.Settings.Prop.OverlaysEnabled && OverlayWindow.SurfaceRequired && Application.Current.Resources["OverlayWindow"] is not OverlayWindow)
                {
                    var overlay = new OverlayWindow();
                    overlay.Show();
                    Application.Current.Resources["OverlayWindow"] = overlay;
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicIntegrations", "Overlay failed: " + ex.Message);
            }

            try { Voidstrap.Integrations.Fullscreen.FakeExclusiveFullscreen.OnGameJoin(); } catch { }
            try { AntiAliasingManager.OnGameJoin(); } catch { }
            try { RiShadeManager.OnGameJoin(); } catch { }
        }

        private static void CloseOverlays()
        {
            try
            {
				CrosshairWindow.CloseAll();
            }
            catch { }

            try
            {
                if (Application.Current.Resources["OverlayWindow"] is OverlayWindow overlay)
                {
                    overlay.Close();
                    Application.Current.Resources.Remove("OverlayWindow");
                }
            }
            catch { }

            try { ScreenColorEffect.Reset(); } catch { }

            try { RiShadeManager.OnGameLeave(); } catch { }
            try { AntiAliasingManager.OnGameLeave(); } catch { }
            try { Voidstrap.Integrations.FrameGeneration.FrameGenManager.OnGameLeave(); } catch { }
            try { Voidstrap.Integrations.Fullscreen.FakeExclusiveFullscreen.OnGameLeave(); } catch { }
        }

        private static void ManageStudioHost(Process studio, CancellationToken token)
        {
            Task.Run(async delegate
            {
                int tick = 0;
                bool styled = false;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (studio.HasExited)
                            break;
                        try
                        {
                            studio.Refresh();
                            IntPtr handle = studio.MainWindowHandle;
                            if (handle != IntPtr.Zero)
                            {
                                if (!styled)
                                {
                                    try
                                    {
                                        long ex = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
                                        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
                                    }
                                    catch
                                    {
                                    }
                                    styled = true;
                                }
                                if (IsWindowVisible(handle))
                                    ShowWindow(handle, SW_HIDE);
                            }
                        }
                        catch
                        {
                        }
                        if (tick % 20 == 0)
                        {
                            if (Platform.IsWindows)
                            {
                                try { EmptyWorkingSet(studio.Handle); } catch { }
                            }
                        }
                        tick++;
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            }, token);
        }

        private static void StartRichPresence(string clientName, string map)
        {
            if (!App.Settings.Prop.UseDiscordRichPresence)
                return;
            try
            {
                string icon = !string.IsNullOrWhiteSpace(App.Settings.Prop.UseCustomIcon)
                    ? App.Settings.Prop.UseCustomIcon
                    : Voidstrap.Integrations.DiscordRichPresence.GetIdleIconUrl();

                if (!Voidstrap.Integrations.DiscordIpc.TryFindPipe(out int pipe))
                    return;
                var rpc = new DiscordRpcClient("1005469189907173486", pipe, null, true, null);
                rpc.Initialize();
                rpc.SetPresenceSafe(new DiscordRPC.RichPresence
                {
                    Details = DiscordPresenceGuard.Text(PlaceName(map, clientName)),
                    State = DiscordPresenceGuard.Text(string.IsNullOrWhiteSpace(clientName) ? "Classic Roblox" : clientName),
                    Timestamps = new Timestamps { Start = DateTime.UtcNow },
                    Assets = new Assets
                    {
                        LargeImageKey = icon,
                        LargeImageText = "Voidstrap"
                    }
                });
                lock (Sync)
                {
                    _rpc = rpc;
                }
            }
            catch (Exception ex)
            {
                App.Logger?.WriteLine("ClassicIntegrations", "Discord presence failed: " + ex.Message);
            }
        }

        private static string PlaceName(string map, string fallback)
        {
            string safeFallback = string.IsNullOrWhiteSpace(fallback) ? "Classic Roblox" : fallback;
            if (string.IsNullOrWhiteSpace(map))
                return safeFallback;
            string name = Path.GetFileName(map);
            foreach (string ext in new[] { ".gz", ".rbxl", ".rbxlx" })
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - ext.Length);
            }
            return string.IsNullOrWhiteSpace(name) ? safeFallback : name;
        }

        public static void Stop()
        {
            Stop(killProcesses: false);
        }

        public static void Stop(bool killProcesses)
        {
            DiscordRpcClient? rpc;
            CancellationTokenSource? cts;
            Process? studio;
            Process? player;
            lock (Sync)
            {
                if (!_active && _rpc == null && _studioHost == null && _player == null)
                    return;
                _active = false;
                rpc = _rpc;
                _rpc = null;
                cts = _cts;
                _cts = null;
                studio = _studioHost;
                _studioHost = null;
                player = _player;
                _player = null;
            }

            try { cts?.Cancel(); } catch { }
            try { cts?.Dispose(); } catch { }

            RunOnUi(CloseOverlays);

            try { rpc?.ClearPresence(); } catch { }
            try { rpc?.Dispose(); } catch { }

            KillProcess(studio);

            if (killProcesses)
                KillProcess(player);
            else
                DisposeProcess(player);
        }

        private static void KillProcess(Process? process)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(4000);
                }
            }
            catch { }
            finally
            {
                DisposeProcess(process);
            }
        }

        private static void DisposeProcess(Process? process)
        {
            try { process?.Dispose(); } catch { }
        }

        private static void RunOnUi(Action action)
        {
            Application? app = Application.Current;
            if (app?.Dispatcher == null)
            {
                try { action(); } catch { }
                return;
            }
            if (app.Dispatcher.CheckAccess())
                action();
            else
                app.Dispatcher.InvokeAsync(action);
        }
    }
}
