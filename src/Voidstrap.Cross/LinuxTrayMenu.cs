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

    private readonly object _gate = new();

    private readonly DbusMenuProperties _properties = new();

    private readonly Dictionary<int, LinuxTrayMenuItem> _index = [];

    private List<LinuxTrayMenuItem> _items = [];

    private uint _revision = 1u;

    public ObjectPath ObjectPath => Path;

    public Func<List<LinuxTrayMenuItem>>? MenuProvider { get; set; }

    private Action<(uint revision, int parent)>? _layoutUpdated;

    private string _signature = string.Empty;

    public Task<IDisposable> WatchLayoutUpdatedAsync(Action<(uint revision, int parent)> handler, Action<Exception>? onError = null)
    {
        _layoutUpdated = handler;
        return Task.FromResult<IDisposable>(new LayoutSubscription(this));
    }

    private sealed class LayoutSubscription : IDisposable
    {
        private readonly DbusMenuObject _owner;

        internal LayoutSubscription(DbusMenuObject owner) => _owner = owner;

        public void Dispose() => _owner._layoutUpdated = null;
    }

    public void SetItems(List<LinuxTrayMenuItem> items)
    {
        uint revision;

        lock (_gate)
        {
            _items = items ?? [];
            _index.Clear();
            Index(_items, string.Empty);

            string signature = BuildSignature(_items);

            if (string.Equals(signature, _signature, StringComparison.Ordinal))
                return;

            _signature = signature;
            _revision++;
            revision = _revision;
        }

        NotifyLayoutUpdated(revision);
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
            builder.Append(item.Visible ? "+v" : "-v");

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

    private readonly Dictionary<string, int> _idsByKey = new(StringComparer.Ordinal);

    private int _nextId = 1;

    private void Index(List<LinuxTrayMenuItem> items, string prefix)
    {
        for (int position = 0; position < items.Count; position++)
        {
            LinuxTrayMenuItem item = items[position];
            string key = BuildKey(prefix, item, position);

            if (!_idsByKey.TryGetValue(key, out int id))
            {
                id = _nextId++;
                _idsByKey[key] = id;
            }

            _index[id] = item;
            Index(item.Children, key);
        }
    }

    private static string BuildKey(string prefix, LinuxTrayMenuItem item, int position)
    {
        string own = item.IsSeparator || string.IsNullOrEmpty(item.Label)
            ? "#" + position.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : item.Label;

        return prefix + "/" + own;
    }

    private int IdOf(LinuxTrayMenuItem item)
    {
        foreach ((int id, LinuxTrayMenuItem candidate) in _index)
        {
            if (ReferenceEquals(candidate, item))
                return id;
        }

        return 0;
    }

    private static Dictionary<string, object> BuildProperties(LinuxTrayMenuItem item)
    {
        Dictionary<string, object> properties = new(StringComparer.Ordinal);
        if (item.IsSeparator)
        {
            properties["type"] = "separator";
            properties["visible"] = item.Visible;
            return properties;
        }

        properties["label"] = item.Label;
        properties["enabled"] = item.Enabled;
        properties["visible"] = item.Visible;
        if (item.IsCheckable)
        {
            properties["toggle-type"] = "checkmark";
            properties["toggle-state"] = item.IsChecked ? 1 : 0;
        }

        if (item.Children.Count > 0)
            properties["children-display"] = "submenu";

        return properties;
    }

    private (int, IDictionary<string, object>, object[]) BuildLayout(int id, LinuxTrayMenuItem item, int depth)
    {
        object[] children = depth == 0 || item.Children.Count == 0
            ? []
            : [.. item.Children.Select(child => (object)BuildLayout(IdOf(child), child, depth - 1))];

        return (id, BuildProperties(item), children);
    }

    public void Rebuild() => Refresh();

    private void Refresh()
    {
        Func<List<LinuxTrayMenuItem>>? provider = MenuProvider;
        if (provider is null)
            return;

        try
        {
            SetItems(provider());
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTrayMenu::Refresh", "The tray menu could not be rebuilt: " + ex.Message);
        }
    }

    public Task<(uint revision, (int, IDictionary<string, object>, object[]) layout)> GetLayoutAsync(int ParentId, int RecursionDepth, string[] PropertyNames)
    {
        Refresh();
        lock (_gate)
        {
            int depth = RecursionDepth < 0 ? 16 : RecursionDepth;
            if (ParentId == 0)
            {
                object[] children = [.. _items.Select(item => (object)BuildLayout(IdOf(item), item, depth - 1))];
                Dictionary<string, object> rootProperties = new(StringComparer.Ordinal)
                {
                    ["children-display"] = "submenu"
                };
                return Task.FromResult((_revision, (0, (IDictionary<string, object>)rootProperties, children)));
            }

            if (_index.TryGetValue(ParentId, out LinuxTrayMenuItem? parent))
                return Task.FromResult((_revision, BuildLayout(ParentId, parent, depth)));

            return Task.FromResult((_revision, (ParentId, (IDictionary<string, object>)new Dictionary<string, object>(StringComparer.Ordinal), Array.Empty<object>())));
        }
    }

    public Task<(int, IDictionary<string, object>)[]> GetGroupPropertiesAsync(int[] Ids, string[] PropertyNames)
    {
        Refresh();
        lock (_gate)
        {
            IEnumerable<KeyValuePair<int, LinuxTrayMenuItem>> selected = Ids is { Length: > 0 }
                ? _index.Where(pair => Ids.Contains(pair.Key))
                : _index;
            return Task.FromResult(selected.Select(pair => (pair.Key, (IDictionary<string, object>)BuildProperties(pair.Value))).ToArray());
        }
    }

    public Task<object> GetPropertyAsync(int Id, string Name)
    {
        Refresh();
        lock (_gate)
        {
            if (_index.TryGetValue(Id, out LinuxTrayMenuItem? item) && BuildProperties(item).TryGetValue(Name, out object? value))
                return Task.FromResult(value);
        }

        return Task.FromResult<object>(string.Empty);
    }

    public Task EventAsync(int Id, string EventId, object Data, uint Timestamp)
    {
        if (!string.Equals(EventId, "clicked", StringComparison.Ordinal))
            return Task.CompletedTask;

        LinuxTrayMenuItem? item;
        lock (_gate)
        {
            _index.TryGetValue(Id, out item);
        }

        if (item?.Activated is null)
            return Task.CompletedTask;

        try
        {
            item.Activated();
        }
        catch (Exception ex)
        {
            Voidstrap.App.Logger?.WriteLine("LinuxTrayMenu::Event", "The tray menu action failed: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    public Task<int[]> EventGroupAsync((int, string, object, uint)[] Events)
    {
        foreach ((int id, string eventId, object data, uint timestamp) in Events ?? [])
            EventAsync(id, eventId, data, timestamp);

        return Task.FromResult(Array.Empty<int>());
    }

    public Task<bool> AboutToShowAsync(int Id)
    {
        Refresh();
        return Task.FromResult(true);
    }

    public Task<(int[] updatesNeeded, int[] idErrors)> AboutToShowGroupAsync(int[] Ids)
    {
        Refresh();
        return Task.FromResult((Ids ?? [], Array.Empty<int>()));
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
