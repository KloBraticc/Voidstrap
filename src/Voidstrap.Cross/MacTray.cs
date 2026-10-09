using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using Voidstrap.Platform.MacOS;

namespace Voidstrap.UI.Tray;

public static class MacTray
{
    private static bool _started;

    private static int _refreshing;

    private static DispatcherTimer? _refreshTimer;

    public static bool IsActive => _started;

    public static bool TryStart(string title)
    {
        if (!OperatingSystem.IsMacOS())
            return false;
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
            return false;
        if (!dispatcher.CheckAccess())
            return dispatcher.Invoke(() => TryStart(title));
        if (_started)
            return true;
        if (!MacOSStatusItem.Show(title, Voidstrap.Utility.Branding.MacIconPath))
        {
            App.Logger?.WriteLine("MacTray::TryStart", "The menu bar icon could not be created");
            return false;
        }
        MacOSStatusItem.MenuOpening += OnMenuOpening;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
        _started = true;
        App.Logger?.WriteLine("MacTray::TryStart", "Voidstrap is in the menu bar");
        RequestRefresh();
        return true;
    }

    public static void Stop()
    {
        if (!_started)
            return;
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.Invoke(Stop);
            return;
        }
        MacOSStatusItem.MenuOpening -= OnMenuOpening;
        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTick;
            _refreshTimer = null;
        }
        MacOSStatusItem.Hide();
        _started = false;
    }

    public static void RequestRefresh()
    {
        if (_started)
            _ = RefreshAsync();
    }

    private static void OnMenuOpening() => RequestRefresh();

    private static void OnRefreshTick(object? sender, EventArgs e) => RequestRefresh();

    private static async Task RefreshAsync()
    {
        if (System.Threading.Interlocked.Exchange(ref _refreshing, 1) == 1)
            return;
        try
        {
            Func<Task<List<LinuxTrayMenuItem>>>? provider = LinuxTray.MenuProvider;
            List<LinuxTrayMenuItem> items = provider == null ? [] : await provider();
            List<MacOSStatusMenuItem> converted = Convert(items);
            Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || !_started)
                return;
            await dispatcher.InvokeAsync(() =>
            {
                if (_started)
                    MacOSStatusItem.SetMenu(converted);
            });
        }
        catch (Exception ex)
        {
            App.Logger?.WriteLine("MacTray::Refresh", "The menu bar menu could not be built: " + ex.Message);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    private static List<MacOSStatusMenuItem> Convert(List<LinuxTrayMenuItem> items)
    {
        List<MacOSStatusMenuItem> result = [];
        foreach (LinuxTrayMenuItem item in items)
        {
            if (!item.Visible)
                continue;
            MacOSStatusMenuItem entry = new()
            {
                Label = item.Label,
                Enabled = item.Enabled,
                IsSeparator = item.IsSeparator,
                IsChecked = item.IsCheckable && item.IsChecked,
                Activated = item.Activated
            };
            entry.Children.AddRange(Convert(item.Children));
            result.Add(entry);
        }
        return result;
    }
}
