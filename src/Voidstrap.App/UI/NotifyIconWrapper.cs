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

	private ActivityWatcher? _subscribedWatcher;

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
		_notifyIcon = new NotifyIcon(new Container())
		{
			Icon = Voidstrap.Properties.Resources.IconVoidstrap,
			Text = "Voidstrap",
			Visible = true
		};
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
			if (watcher != null && App.Settings.Prop.NotificationWindowShow)
				EnsureMenuContainerAsync();
			bool wanted = watcher != null && App.Settings.Prop.ShowServerDetails && !App.Settings.Prop.NotificationWindowShow;
			ActivityWatcher? desired = wanted ? watcher : null;
			if (ReferenceEquals(desired, _subscribedWatcher))
				return;
			if (_subscribedWatcher != null)
				_subscribedWatcher.OnGameJoin -= ActivityWatcher_OnGameJoin;
			_subscribedWatcher = desired;
			if (desired != null)
				desired.OnGameJoin += ActivityWatcher_OnGameJoin;
			App.Logger.WriteLine("NotifyIconWrapper::RefreshGameJoinSubscription", desired != null ? "Tray join notification is armed" : "Tray join notification is off");
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("NotifyIconWrapper::RefreshGameJoinSubscription", ex);
		}
	}

	private async void ActivityWatcher_OnGameJoin(object? sender, EventArgs e)
	{
		try
		{
			await OnGameJoinAsync(sender, e);
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("NotifyIconWrapper::ActivityWatcher_OnGameJoin", ex);
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
			_menuContainer.Show();
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
		dispatcher.BeginInvoke(new Action(delegate
		{
			try
			{
				if (_disposed || _menuContainer != null)
					return;
				EnsureMenuContainer();
				App.Logger.WriteLine("NotifyIconWrapper::EnsureMenuContainerAsync", "Notification host created up front so join notifications do not wait for the tray menu");
			}
			catch (Exception ex)
			{
				App.Logger.WriteException("NotifyIconWrapper::EnsureMenuContainerAsync", ex);
			}
		}));
	}

	private void NotifyIcon_MouseClick(object? sender, MouseEventArgs e)
	{
		if (e.Button != MouseButtons.Right)
		{
			return;
		}
		try
		{
			System.Windows.Application.Current.Dispatcher.Invoke((Action)delegate
			{
				MenuContainer menu = EnsureMenuContainer();
				menu.Activate();
				menu.ContextMenu.IsOpen = true;
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("NotifyIconWrapper::NotifyIcon_MouseClick", ex);
		}
	}

	private void ShowServerInformationAlertClicked(object? sender, EventArgs e)
	{
		try
		{
			System.Windows.Application.Current.Dispatcher.Invoke((Action)delegate
			{
				EnsureMenuContainer().ShowServerInformationWindow();
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteException("NotifyIconWrapper::ShowServerInformationAlertClicked", ex);
		}
	}

	public async Task OnGameJoinAsync(object? sender, EventArgs e)
	{
		if (ActivityWatcher == null)
		{
			return;
		}
		string text = await ActivityWatcher.Data.QueryServerLocation();
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		string caption = ActivityWatcher.Data.ServerType switch
		{
			ServerType.Public => Strings.ContextMenu_ServerInformation_Notification_Title_Public, 
			ServerType.Private => Strings.ContextMenu_ServerInformation_Notification_Title_Private, 
			ServerType.Reserved => Strings.ContextMenu_ServerInformation_Notification_Title_Reserved, 
			_ => string.Empty, 
		};
		if (EnableAppNotifications)
		{
			ShowAlert(caption, string.Format(Strings.ContextMenu_ServerInformation_Notification_Text, text), 10, ShowServerInformationAlertClicked);
		}
		else
		{
			App.Logger.WriteLine("NotifyIconWrapper::OnGameJoinAsync", "App notifications disabled skipping alert");
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
		catch (OperationCanceledException)
		{
		}
		catch (ObjectDisposedException)
		{
		}
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
			if (_subscribedWatcher != null)
			{
				try
				{
					_subscribedWatcher.OnGameJoin -= ActivityWatcher_OnGameJoin;
				}
				catch
				{
				}
				_subscribedWatcher = null;
			}
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
