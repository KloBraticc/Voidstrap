using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Tmds.DBus;

namespace Voidstrap.UI.Tray;

public sealed class LinuxTrayMenuItem
{
    public string Label { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool Visible { get; set; } = true;

    public bool IsSeparator { get; set; }

    public bool IsCheckable { get; set; }

    public bool IsChecked { get; set; }

    public Action? Activated { get; set; }

    public List<LinuxTrayMenuItem> Children { get; } = [];
}

[Dictionary]
public class DbusMenuProperties
{
    public uint Version = 3u;
    public string TextDirection = "ltr";
    public string Status = "normal";
    public string[] IconThemePath = [];
}

[DBusInterface("com.canonical.dbusmenu")]
public interface IDbusMenu : IDBusObject
{
    Task<(uint revision, (int, IDictionary<string, object>, object[]) layout)> GetLayoutAsync(int ParentId, int RecursionDepth, string[] PropertyNames);

    Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] Ids, string[] PropertyNames);

    Task<object> GetPropertyAsync(int Id, string Name);

    Task EventAsync(int Id, string EventId, object Data, uint Timestamp);

    Task<int[]> EventGroupAsync((int, string, object, uint)[] Events);

    Task<bool> AboutToShowAsync(int Id);

    Task<(int[] updatesNeeded, int[] idErrors)> AboutToShowGroupAsync(int[] Ids);

    Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint revision, int parent)> handler, Action<Exception>? onError = null);

    Task<object> GetAsync(string prop);

    Task<DbusMenuProperties> GetAllAsync();

    Task SetAsync(string prop, object val);
}

public sealed class DbusMenuObject : IDbusMenu
{
    public static readonly ObjectPath Path = new("/MenuBar");

    private const int MaxRememberedKeys = 2048;

    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(2.0);

    private static readonly TimeSpan RefreshReuseWindow = TimeSpan.FromMilliseconds(400.0);

    private static readonly TimeSpan ClickRefreshDelay = TimeSpan.FromMilliseconds(250.0);

    private readonly object _gate = new();

    private readonly DbusMenuProperties _properties = new();

    private readonly Dictionary<int, LinuxTrayMenuItem> _index = [];

    private readonly Dictionary<LinuxTrayMenuItem, int> _idsByItem = new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, int> _idsByKey = new(StringComparer.Ordinal);

    private List<LinuxTrayMenuItem> _items = [];

    private uint _revision = 1u;

    private int _nextId = 1;

    private string _signature = string.Empty;

    private Task<bool>? _refreshTask;

    private DateTime _lastRefreshUtc = DateTime.MinValue;

    private bool _timeoutLogged;

    private Action<(uint revision, int parent)>? _layoutUpdated;

    public ObjectPath ObjectPath => Path;

    public Func<Task<List<LinuxTrayMenuItem>>>? MenuProvider { get; set; }

    public Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint revision, int parent)> handler, Action<Exception>? onError = null)
    {
        _layoutUpdated = handler;
        return Task.FromResult<IDisposable>(new LayoutSubscription(this, handler));
    }

    private sealed class LayoutSubscription : IDisposable
    {
        private readonly DbusMenuObject _owner;

        private readonly Action<(uint revision, int parent)> _handler;

        internal LayoutSubscription(DbusMenuObject owner, Action<(uint revision, int parent)> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            if (ReferenceEquals(_owner._layoutUpdated, _handler))
                _owner._layoutUpdated = null;
        }
    }

    public bool SetItems(List<LinuxTrayMenuItem> items)
    {
        List<LinuxTrayMenuItem> normalized = Normalize(items ?? []);
        uint revision;

        lock (_gate)
        {
            _items = normalized;
            _index.Clear();
            _idsByItem.Clear();
            if (_idsByKey.Count > MaxRememberedKeys)
                _idsByKey.Clear();
            Index(_items, string.Empty);

            string signature = BuildSignature(_items);

            if (string.Equals(signature, _signature, StringComparison.Ordinal))
                return false;

            _signature = signature;
            _revision++;
            revision = _revision;
        }

        NotifyLayoutUpdated(revision);
        return true;
    }

    private static List<LinuxTrayMenuItem> Normalize(List<LinuxTrayMenuItem> items)
    {
        List<LinuxTrayMenuItem> result = [];

        foreach (LinuxTrayMenuItem item in items)
        {
            if (item is null || !item.Visible)
                continue;

            if (item.IsSeparator)
            {
                if (result.Count > 0 && !result[^1].IsSeparator)
                    result.Add(item);
                continue;
            }

            if (item.Children.Count > 0)
            {
                List<LinuxTrayMenuItem> children = Normalize(item.Children);
                item.Children.Clear();
                item.Children.AddRange(children);
            }

            result.Add(item);
        }

        while (result.Count > 0 && result[^1].IsSeparator)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private void NotifyLayoutUpdated(uint revision)
    {
        try
        {
            _layoutUpdated?.Invoke((revision, 0));
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTrayMenu::NotifyLayoutUpdated", "The layout change could not be announced: " + ex.Message);
        }
    }

    private static string BuildSignature(List<LinuxTrayMenuItem> items)
    {
        System.Text.StringBuilder builder = new();
        AppendSignature(items, builder);
        return builder.ToString();
    }

    private static void AppendSignature(List<LinuxTrayMenuItem> items, System.Text.StringBuilder builder)
    {
        foreach (LinuxTrayMenuItem item in items)
        {
            builder.Append(item.IsSeparator ? "|sep" : "|" + item.Label);
            builder.Append(item.Enabled ? "+e" : "-e");

            if (item.IsCheckable)
                builder.Append(item.IsChecked ? "+c" : "-c");

            if (item.Children.Count > 0)
            {
                builder.Append('{');
                AppendSignature(item.Children, builder);
                builder.Append('}');
            }
        }
    }

    private void Index(List<LinuxTrayMenuItem> items, string prefix)
    {
        HashSet<string> used = new(StringComparer.Ordinal);
        string previous = "start";

        for (int position = 0; position < items.Count; position++)
        {
            LinuxTrayMenuItem item = items[position];
            string own = item.IsSeparator
                ? "sep:" + previous
                : string.IsNullOrEmpty(item.Label) ? "#" + position.ToString(System.Globalization.CultureInfo.InvariantCulture) : "item:" + item.Label;

            string key = prefix + "/" + own;
            if (!used.Add(key))
            {
                int copy = 2;
                while (!used.Add(key + "~" + copy.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                    copy++;
                key = key + "~" + copy.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (!_idsByKey.TryGetValue(key, out int id))
            {
                id = _nextId++;
                _idsByKey[key] = id;
            }

            _index[id] = item;
            _idsByItem[item] = id;

            if (!item.IsSeparator)
                previous = item.Label;

            Index(item.Children, key);
        }
    }

    private int IdOf(LinuxTrayMenuItem item)
    {
        return _idsByItem.TryGetValue(item, out int id) ? id : -1;
    }

    private static Dictionary<string, object> BuildProperties(LinuxTrayMenuItem item, string[]? names = null)
    {
        Dictionary<string, object> properties = new(StringComparer.Ordinal);
        if (item.IsSeparator)
        {
            properties["type"] = "separator";
            properties["visible"] = true;
        }
        else
        {
            properties["label"] = item.Label.Replace("_", "__", StringComparison.Ordinal);
            properties["enabled"] = item.Enabled;
            properties["visible"] = true;

            if (item.IsCheckable)
            {
                properties["toggle-type"] = "checkmark";
                properties["toggle-state"] = item.IsChecked ? 1 : 0;
            }

            if (item.Children.Count > 0)
                properties["children-display"] = "submenu";
        }

        if (names is { Length: > 0 })
        {
            foreach (string key in properties.Keys.ToArray())
            {
                if (Array.IndexOf(names, key) < 0)
                    properties.Remove(key);
            }
        }

        return properties;
    }

    private (int, IDictionary<string, object>, object[]) BuildLayout(int id, LinuxTrayMenuItem item, int depth)
    {
        object[] children = depth == 0 || item.Children.Count == 0
            ? []
            : [.. item.Children.Select(child => (object)BuildLayout(IdOf(child), child, depth < 0 ? -1 : depth - 1))];

        return (id, BuildProperties(item), children);
    }

    public void Rebuild() => _ = RefreshAsync(force: true);

    public Task<bool> RefreshAsync(bool force = false)
    {
        lock (_gate)
        {
            if (_refreshTask is { IsCompleted: false } pending)
                return pending;

            if (!force && _refreshTask != null && DateTime.UtcNow - _lastRefreshUtc < RefreshReuseWindow)
                return Task.FromResult(false);

            Task<bool> refresh = RefreshCoreAsync();
            _refreshTask = refresh;
            return refresh;
        }
    }

    private async Task<bool> RefreshCoreAsync()
    {
        await Task.Yield();
        Func<Task<List<LinuxTrayMenuItem>>>? provider = MenuProvider;
        if (provider is null)
            return false;

        try
        {
            List<LinuxTrayMenuItem> items = await provider().WaitAsync(RefreshTimeout).ConfigureAwait(false);
            _timeoutLogged = false;
            return SetItems(items);
        }
        catch (TimeoutException)
        {
            if (!_timeoutLogged)
            {
                _timeoutLogged = true;
                Voidstrap.App.Logger?.WriteLine("LinuxTrayMenu::Refresh", "The interface was busy, the tray shows the last menu until it answers");
            }
            return false;
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTrayMenu::Refresh", "The tray menu could not be rebuilt: " + ex.Message);
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _lastRefreshUtc = DateTime.UtcNow;
            }
        }
    }

    public async Task<(uint revision, (int, IDictionary<string, object>, object[]) layout)> GetLayoutAsync(int ParentId, int RecursionDepth, string[] PropertyNames)
    {
        await RefreshAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (ParentId == 0)
            {
                object[] children = RecursionDepth == 0
                    ? []
                    : [.. _items.Select(item => (object)BuildLayout(IdOf(item), item, RecursionDepth < 0 ? -1 : RecursionDepth - 1))];
                Dictionary<string, object> rootProperties = new(StringComparer.Ordinal)
                {
                    ["children-display"] = "submenu"
                };
                return (_revision, (0, rootProperties, children));
            }

            if (_index.TryGetValue(ParentId, out LinuxTrayMenuItem? parent))
                return (_revision, BuildLayout(ParentId, parent, RecursionDepth));

            return (_revision, (ParentId, new Dictionary<string, object>(StringComparer.Ordinal), Array.Empty<object>()));
        }
    }

    public async Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] Ids, string[] PropertyNames)
    {
        await RefreshAsync().ConfigureAwait(false);
        lock (_gate)
        {
            IEnumerable<KeyValuePair<int, LinuxTrayMenuItem>> selected = Ids is { Length: > 0 }
                ? _index.Where(pair => Ids.Contains(pair.Key))
                : _index;
            return selected.Select(pair => (pair.Key, (IDictionary<string, object>)BuildProperties(pair.Value, PropertyNames))).ToArray();
        }
    }

    public Task<object> GetPropertyAsync(int Id, string Name)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(Id, out LinuxTrayMenuItem? item) && BuildProperties(item).TryGetValue(Name, out object? value))
                return Task.FromResult(value);
        }

        throw new DBusException("com.canonical.dbusmenu.UnknownProperty", "The menu item has no property " + Name);
    }

    public Task EventAsync(int Id, string EventId, object Data, uint Timestamp)
    {
        if (string.Equals(EventId, "opened", StringComparison.Ordinal))
        {
            _ = RefreshAsync();
            return Task.CompletedTask;
        }

        if (!string.Equals(EventId, "clicked", StringComparison.Ordinal))
            return Task.CompletedTask;

        LinuxTrayMenuItem? item;
        lock (_gate)
        {
            _index.TryGetValue(Id, out item);
        }

        if (item is null || item.IsSeparator || !item.Enabled || item.Activated is null)
            return Task.CompletedTask;

        try
        {
            item.Activated();
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTrayMenu::Event", "The tray menu action failed: " + ex.Message);
        }

        _ = RefreshAfterClickAsync();
        return Task.CompletedTask;
    }

    private async Task RefreshAfterClickAsync()
    {
        try
        {
            await Task.Delay(ClickRefreshDelay).ConfigureAwait(false);
            await RefreshAsync(force: true).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTrayMenu::Event", "The tray menu could not refresh after a click: " + ex.Message);
        }
    }

    public Task<int[]> EventGroupAsync((int, string, object, uint)[] Events)
    {
        List<int> missing = [];
        foreach ((int id, string eventId, object data, uint timestamp) in Events ?? [])
        {
            bool known;
            lock (_gate)
            {
                known = _index.ContainsKey(id) || id == 0;
            }

            if (!known)
            {
                missing.Add(id);
                continue;
            }

            _ = EventAsync(id, eventId, data, timestamp);
        }

        return Task.FromResult(missing.ToArray());
    }

    public async Task<bool> AboutToShowAsync(int Id)
    {
        return await RefreshAsync(force: Id == 0).ConfigureAwait(false);
    }

    public async Task<(int[] updatesNeeded, int[] idErrors)> AboutToShowGroupAsync(int[] Ids)
    {
        bool changed = await RefreshAsync(force: true).ConfigureAwait(false);
        int[] requested = Ids ?? [];
        lock (_gate)
        {
            int[] errors = [.. requested.Where(id => id != 0 && !_index.ContainsKey(id))];
            int[] updates = changed ? [.. requested.Where(id => id == 0 || _index.ContainsKey(id))] : [];
            return (updates, errors);
        }
    }

    public Task<object> GetAsync(string prop)
    {
        return Task.FromResult(prop switch
        {
            "Version" => (object)_properties.Version,
            "TextDirection" => _properties.TextDirection,
            "Status" => _properties.Status,
            "IconThemePath" => _properties.IconThemePath,
            _ => string.Empty
        });
    }

    public Task<DbusMenuProperties> GetAllAsync()
    {
        return Task.FromResult(_properties);
    }

    public Task SetAsync(string prop, object val)
    {
        return Task.CompletedTask;
    }
}
