using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Threading;
using Voidstrap.Enums;
using Voidstrap.Integrations;
using Voidstrap.Properties;
using Voidstrap.Resources;
using Voidstrap.UI.Elements.ContextMenu;

namespace Voidstrap.UI;

public class NotifyIconWrapper : IDisposable
{
    private bool _disposed;

    private readonly NotifyIcon _notifyIcon;

    private MenuContainer? _menuContainer;

    private readonly Watcher _watcher;

    private EventHandler? _alertClickHandler;


    private readonly object _alertLock = new object();

    private CancellationTokenSource? _alertExpirationCts;

    private ActivityWatcher? ActivityWatcher => _watcher.ActivityWatcher;

    public bool EnableAppNotifications
    {
        get
        {
            try
            {
                return App.Settings?.Prop?.VoidNotify == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public NotifyIconWrapper(Watcher watcher)
    {
        App.Logger.WriteLine("NotifyIconWrapper::NotifyIconWrapper", "Initializing notification area icon");
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _notifyIcon = new NotifyIcon(new Container()) { Text = Voidstrap.Utility.Branding.Name };
        if (Voidstrap.Utility.Platform.IsWindows)
        {
            _notifyIcon.Icon = Voidstrap.Utility.Branding.CreateDrawingIcon() ?? Voidstrap.Properties.Resources.IconVoidstrap;
        }
        try
        {
            EnsureMenuContainer();
            App.Logger.WriteLine("NotifyIconWrapper::NotifyIconWrapper", "Tray menu host created before the icon was published");
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("NotifyIconWrapper::NotifyIconWrapper", ex);
        }

        _notifyIcon.Visible = true;

#if CROSSPLAT
		if (!Voidstrap.Utility.Platform.IsWindows)
		{
			try
			{
				_menuContainer?.AttachTrayMenuProvider();
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("NotifyIconWrapper::AttachTrayMenu", ex);
			}
		}
#endif

        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        RefreshGameJoinSubscription();
    }

    public void RefreshGameJoinSubscription()
    {
        if (_disposed)
            return;
        try
        {
            ActivityWatcher? watcher = ActivityWatcher;
            // The session dock's shortcut lives in the menu container too, so it starts for the dock even with notifications off
            bool joinNotifications =
                App.Settings.Prop.VoidNotify && App.Settings.Prop.NotifyGameJoins && App.Settings.Prop.NotificationWindowShow;
            if (
                watcher != null
                && (
                    joinNotifications
                    || App.Settings.Prop.SessionDockEnabled
                    || App.Settings.Prop.ClipperEnabled
                    || Watcher.NeedsInGameHotkeys
                )
            )
                EnsureMenuContainerAsync();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("NotifyIconWrapper::RefreshGameJoinSubscription", ex);
        }
    }

    private MenuContainer EnsureMenuContainer()
    {
        Dispatcher dispatcher = System.Windows.Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            return dispatcher.Invoke(EnsureMenuContainer);
        }
        if (_menuContainer == null)
        {
            _menuContainer = new MenuContainer(_watcher);
            if (Voidstrap.Utility.Platform.IsWindows)
            {
                _menuContainer.Show();
            }
        }
        return _menuContainer;
    }

    private void EnsureMenuContainerAsync()
    {
        if (_disposed || _menuContainer != null)
            return;
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            return;
        dispatcher.BeginInvoke(
            new Action(
                delegate
                {
                    try
                    {
                        if (_disposed || _menuContainer != null)
                            return;
                        EnsureMenuContainer();
                        App.Logger.WriteLine(
                            "NotifyIconWrapper::EnsureMenuContainerAsync",
                            "Notification host created up front so join notifications do not wait for the tray menu"
                        );
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteException("NotifyIconWrapper::EnsureMenuContainerAsync", ex);
                    }
                }
            )
        );
    }

    private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (_disposed || dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }
        try
        {
            dispatcher.BeginInvoke(
                (Action)
                    delegate
                    {
                        try
                        {
                            if (_disposed)
                                return;
                            MenuContainer menu = EnsureMenuContainer();
                            if (Voidstrap.Utility.Platform.IsWindows)
                            {
                                menu.OpenTrayMenu();
                                return;
                            }
                            menu.Activate();
                            if (menu.ContextMenu != null)
                                menu.ContextMenu.IsOpen = true;
                        }
                        catch (Exception ex)
                        {
                            App.Logger.WriteException("NotifyIconWrapper::NotifyIcon_MouseClick", ex);
                        }
                    }
            );
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("NotifyIconWrapper::NotifyIcon_MouseClick", ex);
        }
    }

    public void ShowAlert(string caption, string message, int durationSeconds, EventHandler? clickHandler)
    {
        if (_disposed)
            return;
        if (!EnableAppNotifications)
        {
            App.Logger.WriteLine("NotifyIconWrapper::ShowAlert", "Notifications disabled skipping alert display");
            return;
        }
        Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            try
            {
                dispatcher.BeginInvoke(new Action(() => ShowAlert(caption, message, durationSeconds, clickHandler)));
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("NotifyIconWrapper::ShowAlert", ex);
            }
            return;
        }
        try
        {
            ShowAlertCore(caption, message, durationSeconds, clickHandler);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("NotifyIconWrapper::ShowAlert", ex);
        }
    }

    private void ShowAlertCore(string caption, string message, int durationSeconds, EventHandler? clickHandler)
    {
        string text = Guid.NewGuid().ToString("N")[..8];
        string logIdent = "NotifyIconWrapper::ShowAlert." + text;
        App.Logger.WriteLine(logIdent, $"Showing alert for {durationSeconds}s (clickHandler set: {clickHandler != null})");
        App.Logger.WriteLine(logIdent, caption + ": " + message.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " "));
        if (_alertClickHandler != null)
        {
            App.Logger.WriteLine(logIdent, "Previous alert present, removing old click handler");
            _notifyIcon.BalloonTipClicked -= _alertClickHandler;
            _alertClickHandler = null;
        }
        CancellationToken token;
        lock (_alertLock)
        {
            if (_disposed)
                return;
            _alertExpirationCts?.Cancel();
            _alertExpirationCts?.Dispose();
            _alertExpirationCts = new CancellationTokenSource();
            token = _alertExpirationCts.Token;
        }
        _notifyIcon.BalloonTipTitle = caption;
        _notifyIcon.BalloonTipText = message;
        if (clickHandler != null)
        {
            _alertClickHandler = clickHandler;
            _notifyIcon.BalloonTipClicked += _alertClickHandler;
        }
        try
        {
            _notifyIcon.ShowBalloonTip(durationSeconds);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException(logIdent, ex);
            return;
        }
        _ = ExpireAlertAsync(durationSeconds, clickHandler, logIdent, token);
    }

    private async Task ExpireAlertAsync(int durationSeconds, EventHandler? clickHandler, string logIdent, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), token);
            if (!_disposed && clickHandler != null)
            {
                _notifyIcon.BalloonTipClicked -= clickHandler;
                App.Logger.WriteLine(logIdent, "Alert duration ended, removed click handler");
                if (_alertClickHandler == clickHandler)
                {
                    _alertClickHandler = null;
                }
                else
                {
                    App.Logger.WriteLine(logIdent, "Click handler was overridden by another alert");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            App.Logger.WriteException(logIdent, ex);
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }
        if (disposing)
        {
            App.Logger.WriteLine("NotifyIconWrapper::Dispose", "Disposing NotifyIcon");
            if (_menuContainer != null)
            {
                try
                {
                    ((DispatcherObject)_menuContainer).Dispatcher.Invoke((Action)_menuContainer.Close);
                }
                catch (Exception value)
                {
                    App.Logger.WriteLine("NotifyIconWrapper::Dispose", $"Failed to close menu container: {value}");
                }
                _menuContainer = null;
            }
            _alertExpirationCts?.Cancel();
            _alertExpirationCts?.Dispose();
            _alertExpirationCts = null;
            if (_alertClickHandler != null)
            {
                _notifyIcon.BalloonTipClicked -= _alertClickHandler;
                _alertClickHandler = null;
            }
            _notifyIcon.MouseClick -= NotifyIcon_MouseClick;
            _notifyIcon.Dispose();
        }
        _disposed = true;
    }
}
