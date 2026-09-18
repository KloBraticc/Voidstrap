using System;
using System.Threading.Tasks;
using Tmds.DBus;

namespace Voidstrap.UI.Tray;

[Dictionary]
public class StatusNotifierItemProperties
{
    public string Category = "ApplicationStatus";
    public string Id = "voidstrap";
    public string Title = "Voidstrap";
    public string Status = "Active";
    public string IconName = "voidstrap";
    public string IconThemePath = "";
    public string AttentionIconName = "";
    public string OverlayIconName = "";
    public string ToolTipTitle = "Voidstrap";
    public bool ItemIsMenu = true;
    public int WindowId = 0;
    public ObjectPath Menu = DbusMenuObject.Path;
}

[DBusInterface("org.kde.StatusNotifierItem")]
public interface IStatusNotifierItem : IDBusObject
{
    Task ActivateAsync(int X, int Y);
    Task SecondaryActivateAsync(int X, int Y);
    Task ContextMenuAsync(int X, int Y);
    Task ScrollAsync(int Delta, string Orientation);
    Task<object> GetAsync(string prop);
    Task<StatusNotifierItemProperties> GetAllAsync();
    Task SetAsync(string prop, object val);
}

[DBusInterface("org.kde.StatusNotifierWatcher")]
public interface IStatusNotifierWatcher : IDBusObject
{
    Task RegisterStatusNotifierItemAsync(string Service);
}

[DBusInterface("org.freedesktop.Notifications")]
public interface IFreedesktopNotifications : IDBusObject
{
    Task<uint> NotifyAsync(string AppName, uint ReplacesId, string AppIcon, string Summary, string Body, string[] Actions, System.Collections.Generic.IDictionary<string, object> Hints, int ExpireTimeout);
}

public sealed class StatusNotifierItemObject : IStatusNotifierItem
{
    public static readonly ObjectPath Path = new ObjectPath("/StatusNotifierItem");

    private readonly StatusNotifierItemProperties _properties = new();

    public ObjectPath ObjectPath => Path;

    public event Action? Activated;

    public event Action? SecondaryActivated;

    public event Action? ContextMenuRequested;

    public void SetTitle(string title)
    {
        _properties.Title = title;
        _properties.ToolTipTitle = title;
    }

    public void SetIconThemePath(string path)
    {
        _properties.IconThemePath = path;
    }

    public Task ActivateAsync(int X, int Y)
    {
        Activated?.Invoke();
        return Task.CompletedTask;
    }

    public Task SecondaryActivateAsync(int X, int Y)
    {
        SecondaryActivated?.Invoke();
        return Task.CompletedTask;
    }

    public Task ContextMenuAsync(int X, int Y)
    {
        ContextMenuRequested?.Invoke();
        return Task.CompletedTask;
    }

    public Task ScrollAsync(int Delta, string Orientation)
    {
        return Task.CompletedTask;
    }

    public Task<object> GetAsync(string prop)
    {
        return Task.FromResult(prop switch
        {
            "Category" => (object)_properties.Category,
            "Id" => _properties.Id,
            "Title" => _properties.Title,
            "Status" => _properties.Status,
            "IconName" => _properties.IconName,
            "IconThemePath" => _properties.IconThemePath,
            "AttentionIconName" => _properties.AttentionIconName,
            "OverlayIconName" => _properties.OverlayIconName,
            "ToolTipTitle" => _properties.ToolTipTitle,
            "ItemIsMenu" => _properties.ItemIsMenu,
            "WindowId" => _properties.WindowId,
            "Menu" => _properties.Menu,
            _ => ""
        });
    }

    public Task<StatusNotifierItemProperties> GetAllAsync()
    {
        return Task.FromResult(_properties);
    }

    public Task SetAsync(string prop, object val)
    {
        return Task.CompletedTask;
    }
}

public static class LinuxTray
{
    private const string WatcherService = "org.kde.StatusNotifierWatcher";

    private const string NotificationsService = "org.freedesktop.Notifications";

    private static readonly object Gate = new();

    private static Connection? _connection;

    private static StatusNotifierItemObject? _item;

    private static DbusMenuObject? _menu;

    private static bool _started;

    public static StatusNotifierItemObject? Item => _item;

    public static DbusMenuObject? Menu => _menu;

    public static bool IsActive => _started;

    private static readonly TimeSpan TrayOperationTimeout = TimeSpan.FromSeconds(5.0);

    public static bool TryStart(string title)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        lock (Gate)
        {
            if (_started)
            {
                return true;
            }
        }

        try
        {
            Task<bool> start = Task.Run(() => StartAsync(title));
            if (!start.Wait(TrayOperationTimeout))
            {
                Voidstrap.App.Logger?.WriteLine("LinuxTray::TryStart", "The tray icon did not register in time, continuing without it");
                return false;
            }

            return start.Result;
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::TryStart", "The tray icon could not be created: " + ex.Message);
            return false;
        }
    }

    private static async Task<bool> StartAsync(string title)
    {
        Connection connection = new(Address.Session);
        await connection.ConnectAsync().ConfigureAwait(false);

        StatusNotifierItemObject item = new();
        item.SetTitle(title);
        item.SetIconThemePath(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "icons"));

        DbusMenuObject menu = new();
        await connection.RegisterObjectAsync(item).ConfigureAwait(false);
        await connection.RegisterObjectAsync(menu).ConfigureAwait(false);

        string busName = "org.kde.StatusNotifierItem-" + Environment.ProcessId + "-1";
        await connection.RegisterServiceAsync(busName).ConfigureAwait(false);

        IStatusNotifierWatcher watcher = connection.CreateProxy<IStatusNotifierWatcher>(
            WatcherService,
            new ObjectPath("/StatusNotifierWatcher"));
        await watcher.RegisterStatusNotifierItemAsync(busName).ConfigureAwait(false);

        lock (Gate)
        {
            _connection = connection;
            _item = item;
            _menu = menu;
            _started = true;
        }

        Voidstrap.App.Logger?.WriteLine("LinuxTray::Start", "Registered the tray icon as " + busName);
        return true;
    }

    public static void Notify(string title, string body)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            if (!Task.Run(() => NotifyAsync(title, body)).Wait(TrayOperationTimeout))
            {
                Voidstrap.App.Logger?.WriteLine("LinuxTray::Notify", "The notification timed out");
            }
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::Notify", "The notification could not be sent: " + ex.Message);
        }
    }

    private static async Task NotifyAsync(string title, string body)
    {
        Connection? connection;
        lock (Gate)
        {
            connection = _connection;
        }

        if (connection == null)
        {
            connection = new Connection(Address.Session);
            await connection.ConnectAsync().ConfigureAwait(false);
        }

        IFreedesktopNotifications notifications = connection.CreateProxy<IFreedesktopNotifications>(
            NotificationsService,
            new ObjectPath("/org/freedesktop/Notifications"));

        await notifications.NotifyAsync(
            "Voidstrap",
            0u,
            "voidstrap",
            title,
            body,
            [],
            new System.Collections.Generic.Dictionary<string, object>(),
            5000).ConfigureAwait(false);
    }

    public static void Stop()
    {
        Connection? connection;
        lock (Gate)
        {
            connection = _connection;
            _connection = null;
            _item = null;
            _started = false;
        }

        try
        {
            connection?.Dispose();
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTray::Stop", "The tray connection could not be closed: " + ex.Message);
        }
    }
}
