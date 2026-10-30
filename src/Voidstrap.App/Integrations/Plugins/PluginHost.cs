using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime.Interop;

namespace Voidstrap.Integrations.Plugins;

public static class PluginHost
{
    private const string LogIdent = "VoidstrapMods";

    private const int ReloadDelayMs = 750;

    private static readonly object Gate = new();

    private static readonly object ReloadGate = new();

    internal static readonly SemaphoreSlim MutationGate = new(1, 1);

    private static string? _reloadSignature;

    private static readonly List<LoadedPlugin> Loaded = [];

    private static readonly List<PaneInfo> OpenPanes = [];

    private static readonly List<BannerInfo> OpenBanners = [];

    private static readonly Dictionary<string, string> Statuses = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, JsonObject> SessionState = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, List<string>> Problems = new(StringComparer.Ordinal);

    private const int MaxProblems = 50;

    [ThreadStatic]
    private static int _renderDepth;

    public static event Action? ModsChanged;

    public sealed record ModInfo(
        string Name,
        string Version,
        string Description,
        string Directory,
        string Consent,
        bool Loaded,
        IReadOnlyList<string> Hooks,
        IReadOnlyList<string> Problems
    );

    public const string InvalidState = "invalid";

    private static readonly HashSet<string> KnownEvents = new(StringComparer.Ordinal)
    {
        "session.start",
        "session.end",
        "roblox.launch",
        "game.join",
        "game.leave",
        "page.navigate",
        "settings.changed",
        "ui.render",
        "cursor.move",
        "cursor.down",
        "cursor.up",
        "cursor.enter",
        "cursor.leave",
        "cursor.scroll",
    };

    private static readonly HashSet<string> PendingRestarts = new(StringComparer.Ordinal);

    public static void Problem(string mod, string text)
    {
        lock (Gate)
        {
            if (!Problems.TryGetValue(mod, out List<string>? list))
                Problems[mod] = list = [];
            if (list.Count > 0 && list[^1].EndsWith(" " + text, StringComparison.Ordinal))
                return;
            App.Logger.WriteLine(LogIdent, mod + ": " + text);
            list.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text);
            if (list.Count > MaxProblems)
                list.RemoveAt(0);
        }
        ModsChanged?.Invoke();
    }

    public static void ClearProblems(string mod)
    {
        bool removed;
        lock (Gate)
            removed = Problems.Remove(mod);
        if (removed)
            ModsChanged?.Invoke();
    }

    public static IReadOnlyList<ModInfo> ListMods()
    {
        List<PluginManifest> manifests = PluginManifest.Discover(Paths.Plugins, report: false);
        List<LoadedPlugin> snapshot;
        lock (Gate)
            snapshot = Loaded.ToList();
        foreach (LoadedPlugin plugin in snapshot)
            if (!manifests.Any(manifest => manifest.Name == plugin.Name))
                manifests.Add(plugin.Manifest);
        List<ModInfo> result = [];
        lock (Gate)
        {
            foreach (PluginManifest manifest in manifests)
            {
                LoadedPlugin? loaded = snapshot.FirstOrDefault(plugin => plugin.Name == manifest.Name);
                result.Add(
                    new ModInfo(
                        manifest.Name,
                        manifest.Version,
                        manifest.Description,
                        manifest.Directory,
                        "",
                        loaded != null,
                        [],
                        Problems.TryGetValue(manifest.Name, out List<string>? list) ? list.ToList() : []
                    )
                );
            }
        }
        for (int i = 0; i < manifests.Count; i++)
        {
            LoadedPlugin? loaded = snapshot.FirstOrDefault(plugin => plugin.Name == manifests[i].Name);
            List<string> hooks = loaded?.DescribeHooks().ToList() ?? [];
            result[i] = result[i] with { Consent = PluginConsent.GetState(manifests[i]), Hooks = hooks };
        }
        lock (Gate)
        {
            foreach (KeyValuePair<string, List<string>> entry in Problems)
            {
                string folder = Path.Combine(Paths.Plugins, entry.Key);
                if (!manifests.Any(manifest => manifest.Name == entry.Key) && Directory.Exists(folder))
                    result.Add(new ModInfo(entry.Key, "", "", folder, InvalidState, false, [], entry.Value.ToList()));
            }
        }
        return result;
    }

    public static void SetEnabled(string name, bool enabled, bool reload = true)
    {
        PluginConsent.SetEnabledState(name, enabled);
        if (!enabled)
            PluginRollback.Restore(name);
        if (enabled && System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
        {
            PluginManifest? manifest = PluginManifest
                .Discover(Paths.Plugins, report: false)
                .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (manifest != null && manifest.HasPermission(PluginManifest.RobloxAccountPermission))
                _ = PluginConsent.IsAllowedAsync(manifest);
        }
        App.Settings.SaveDeferred();
        App.Logger.WriteLine(LogIdent, name + ": turned " + (enabled ? "on" : "off") + " by the user");
        ModsChanged?.Invoke();
        if (reload)
            Task.Run(Reload);
    }

    public static void DeleteMod(string name)
    {
        PluginConsent.SetEnabledState(name, false);
        string folder = Path.Combine(Paths.Plugins, name);
        MutationGate.Wait();
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
        }
        finally
        {
            MutationGate.Release();
        }
        ForgetConsent(name);
        Reload();
        lock (Gate)
        {
            SessionState.Remove(name);
            Problems.Remove(name);
        }
        PluginRollback.Restore(name);
        PluginStore.DeleteData(name);
        PluginOptions.Delete(name);
        ModsChanged?.Invoke();
    }

    public static void ReloadNow()
    {
        _reloadSignature = null;
        Reload();
    }

    public static void RestartMod(string name)
    {
        lock (Gate)
            PendingRestarts.Add(name);
        ReloadNow();
    }

    public static void ForgetConsent(string name)
    {
        PluginConsent.Forget(name);
        App.Settings.SaveDeferred();
    }

    private static FileSystemWatcher? _watcher;

    private static Timer? _reloadTimer;

    private static bool _started;

    public static event Action? PanesChanged;

    public static event Action? BannersChanged;

    public static event Action<string>? PaneInvalidated;

    public static event Action? StatusChanged;

    public sealed record PaneInfo(string Plugin, string Id, string Title);

    public sealed record BannerInfo(string Plugin, string Id, string Position);

    public static IReadOnlyList<PaneInfo> Panes
    {
        get
        {
            lock (Gate)
                return OpenPanes.ToList();
        }
    }

    public static IReadOnlyList<BannerInfo> Banners
    {
        get
        {
            lock (Gate)
                return OpenBanners.ToList();
        }
    }

    public static IReadOnlyList<string> StatusLines
    {
        get
        {
            lock (Gate)
                return Statuses.Select(pair => pair.Key + ": " + pair.Value).ToList();
        }
    }

    public static void Start()
    {
        lock (Gate)
        {
            if (_started)
                return;
            _started = true;
        }
        PluginCursor.Start();
        try
        {
            Directory.CreateDirectory(Paths.Plugins);
            _reloadTimer = new Timer(OnReloadTimer, null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(Paths.Plugins)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            _watcher.Changed += OnPluginsChanged;
            _watcher.Created += OnPluginsChanged;
            _watcher.Deleted += OnPluginsChanged;
            _watcher.Renamed += OnPluginsRenamed;
            _watcher.Error += OnPluginsWatcherError;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "The mods folder could not be watched: " + ex.Message);
        }
        Reload();
        ModLibrary.StartAutoUpdater();
    }

    public static void Shutdown()
    {
        ModLibrary.StopAutoUpdater();
        List<LoadedPlugin> doomed;
        lock (Gate)
        {
            _started = false;
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnPluginsChanged;
                _watcher.Created -= OnPluginsChanged;
                _watcher.Deleted -= OnPluginsChanged;
                _watcher.Renamed -= OnPluginsRenamed;
                _watcher.Error -= OnPluginsWatcherError;
                _watcher.Dispose();
                _watcher = null;
            }
            _reloadTimer?.Dispose();
            _reloadTimer = null;
            doomed = Loaded.ToList();
            Loaded.Clear();
            OpenPanes.Clear();
            OpenBanners.Clear();
            Statuses.Clear();
            _reloadSignature = null;
        }
        foreach (LoadedPlugin plugin in doomed)
            plugin.Dispose();
        PluginModPages.ClearAll();
        PluginRestyle.ClearAll();
        PluginCursor.Shutdown();
    }

    private static bool IsGenerated(string path)
    {
        string marker = Path.DirectorySeparatorChar + PluginManifest.ManifestFolder;
        return path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || path.Contains(marker + Path.DirectorySeparatorChar + "types", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(marker, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetReloadSignature(string root, string? mod = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (Directory.Exists(root))
        {
            EnumerationOptions options = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (
                string path in Directory.EnumerateFiles(root, "*", options).Where(path => !IsGenerated(path)).Order(StringComparer.Ordinal)
            )
            {
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, path) + "\0"));
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                byte[] buffer = new byte[8192];
                int count;
                while ((count = stream.Read(buffer)) > 0)
                    hash.AppendData(buffer.AsSpan(0, count));
                hash.AppendData([0]);
            }
        }
        foreach (KeyValuePair<string, bool> consent in PluginConsent.GetStates().OrderBy(pair => pair.Key, StringComparer.Ordinal))
            if (mod == null || consent.Key == mod || consent.Key.StartsWith(mod + "#", StringComparison.Ordinal))
                hash.AppendData(Encoding.UTF8.GetBytes(consent.Key + "\0" + consent.Value + "\0"));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void OnPluginsChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsGenerated(e.FullPath))
            _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);
    }

    private static void OnPluginsRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsGenerated(e.FullPath) && !IsGenerated(e.OldFullPath))
            _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);
    }

    private static void OnPluginsWatcherError(object sender, ErrorEventArgs e)
    {
        try
        {
            App.Logger.WriteLine(LogIdent, "The mods folder watch hit an error: " + Describe(e.GetException()));
            lock (Gate)
            {
                if (_started)
                {
                    _watcher?.Dispose();
                    _watcher = null;
                    try
                    {
                        FileSystemWatcher replacement = new(Paths.Plugins)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                        };
                        replacement.Changed += OnPluginsChanged;
                        replacement.Created += OnPluginsChanged;
                        replacement.Deleted += OnPluginsChanged;
                        replacement.Renamed += OnPluginsRenamed;
                        replacement.Error += OnPluginsWatcherError;
                        replacement.EnableRaisingEvents = true;
                        _watcher = replacement;
                        _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LogIdent, "The mods folder could not be watched again: " + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LogIdent, "The mods folder watch error could not be handled: " + ex.Message);
        }
    }

    private static void OnReloadTimer(object? state)
    {
        try
        {
            App.Logger.WriteLine(LogIdent, "The mods folder changed, reloading Voidstrap mods");
            Reload();
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("PluginHost::OnReloadTimer", ex);
        }
    }

    public static void Reload()
    {
        if (System.Windows.Application.Current?.Dispatcher.CheckAccess() == true)
        {
            Task.Run(Reload);
            return;
        }
        if (!Monitor.TryEnter(ReloadGate))
        {
            _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);
            return;
        }
        try
        {
            foreach (PluginManifest manifest in PluginManifest.Discover(Paths.Plugins, report: false))
                PluginConsent.IsAllowed(manifest);
        }
        catch (Exception ex)
        {
            App.Logger.WriteException("PluginHost::Reload", ex);
        }
        MutationGate.Wait();
        try
        {
            try
            {
                _reloadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                string signature = GetReloadSignature(Paths.Plugins);
                if (signature == _reloadSignature)
                    return;
                List<LoadedPlugin> old;
                lock (Gate)
                    old = Loaded.ToList();
                List<PluginManifest> manifests = PluginManifest.Discover(Paths.Plugins);
                List<LoadedPlugin> fresh = [];
                HashSet<string> restarts;
                lock (Gate)
                {
                    restarts = PendingRestarts.ToHashSet(StringComparer.Ordinal);
                    PendingRestarts.Clear();
                }
                foreach (PluginManifest manifest in manifests)
                {
                    if (!PluginConsent.IsAllowed(manifest))
                    {
                        ClearProblems(manifest.Name);
                        continue;
                    }
                    try
                    {
                        LoadedPlugin? unchanged = old.FirstOrDefault(plugin =>
                            plugin.Name == manifest.Name && plugin.SourceSignature == GetReloadSignature(manifest.Directory, manifest.Name)
                        );
                        if (unchanged != null && !restarts.Contains(manifest.Name))
                        {
                            fresh.Add(unchanged);
                            continue;
                        }
                        ClearProblems(manifest.Name);
                        fresh.Add(LoadedPlugin.Load(manifest));
                        App.Logger.WriteLine(LogIdent, manifest.Name + ": loaded " + manifest.Version);
                    }
                    catch (Exception ex)
                    {
                        Problem(manifest.Name, "the module did not load: " + Describe(ex));
                        LoadedPlugin? previous = old.FirstOrDefault(plugin => plugin.Name == manifest.Name);
                        if (previous != null && PluginConsent.GetState(previous.Manifest) == "on")
                            fresh.Add(previous);
                    }
                }
                _reloadSignature = signature;
                foreach (LoadedPlugin plugin in old)
                    if (
                        !manifests.Any(manifest => manifest.Name == plugin.Name)
                        && Directory.Exists(plugin.Manifest.Directory)
                        && PluginConsent.GetState(plugin.Manifest) == "on"
                    )
                        fresh.Add(plugin);
                List<LoadedPlugin> replaced = old.Where(plugin => !fresh.Contains(plugin)).ToList();
                List<LoadedPlugin> added = fresh.Where(plugin => !old.Contains(plugin)).ToList();
                HashSet<string> changed = replaced.Concat(added).Select(plugin => plugin.Name).ToHashSet(StringComparer.Ordinal);
                HashSet<string> unchangedNames = old.Where(plugin => fresh.Contains(plugin))
                    .Select(plugin => plugin.Name)
                    .ToHashSet(StringComparer.Ordinal);
                lock (Gate)
                {
                    Loaded.Clear();
                    OpenPanes.RemoveAll(pane => changed.Contains(pane.Plugin));
                    OpenBanners.RemoveAll(banner => changed.Contains(banner.Plugin));
                    foreach (string plugin in changed)
                        Statuses.Remove(plugin);
                    Loaded.AddRange(fresh);
                }
                PluginDialogManager.ClosePluginDialogs(unchangedNames);
                foreach (LoadedPlugin plugin in replaced)
                    plugin.Dispose();
                PluginSidebar.RemovePluginPages(unchangedNames);
                PluginModPages.RemovePlugins(changed);
                PluginAnchors.RemovePlugins(changed);
                PluginRestyle.RemovePlugins(changed);
                PluginRollback.RestoreMissing();
                PluginWindow.RemovePlugins(changed);
                JsonObject started = new() { ["source"] = old.Count > 0 ? "reload" : "startup" };
                List<HookEntry> hooks = added
                    .SelectMany(plugin => plugin.HooksFor("session.start"))
                    .Where(hook => Matches(hook.Matcher, started))
                    .ToList();
                Run(hooks, 0, "session.start", started, static e => e, null);
                PluginSidebar.ApplyToWindows();
                PluginAnchors.ApplyAll();
                PluginCursor.RefreshHooks();
                ModsChanged?.Invoke();
                PanesChanged?.Invoke();
                BannersChanged?.Invoke();
                StatusChanged?.Invoke();
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("PluginHost::Reload", ex);
            }
        }
        finally
        {
            MutationGate.Release();
            Monitor.Exit(ReloadGate);
        }
    }

    public static bool HasHook(string eventName)
    {
        List<LoadedPlugin> snapshot;
        lock (Gate)
            snapshot = Loaded.ToList();
        foreach (LoadedPlugin plugin in snapshot)
        {
            if (plugin.HasHook(eventName))
                return true;
        }
        return false;
    }

    public static JsonNode? Invoke(string eventName, JsonNode? input, Func<JsonNode?, JsonNode?>? bottom = null, string? scope = null)
    {
        List<LoadedPlugin> snapshot;
        lock (Gate)
            snapshot = Loaded.ToList();
        if (eventName == "ui.render")
            snapshot = snapshot.Where(plugin => input?["plugin"]?.ToString() == plugin.Name).ToList();
        List<HookEntry> hooks = snapshot
            .SelectMany(plugin => plugin.HooksFor(eventName))
            .Where(hook => Matches(hook.Matcher, input))
            .ToList();
        bottom ??= static e => e;
        if (hooks.Count == 0)
            return bottom(input);
        return Run(hooks, 0, eventName, input, bottom, scope);
    }

    private static bool Matches(JsonObject? matcher, JsonNode? input)
    {
        if (matcher == null)
            return true;
        JsonObject? values = input as JsonObject;
        foreach (KeyValuePair<string, JsonNode?> rule in matcher)
        {
            string actual = values?[rule.Key]?.ToString() ?? "";
            bool hit = rule.Value is JsonArray options
                ? options.Any(option => string.Equals(option?.ToString(), actual, StringComparison.Ordinal))
                : string.Equals(rule.Value?.ToString(), actual, StringComparison.Ordinal);
            if (!hit)
                return false;
        }
        return true;
    }

    private static JsonNode? Run(
        List<HookEntry> hooks,
        int index,
        string eventName,
        JsonNode? input,
        Func<JsonNode?, JsonNode?> bottom,
        string? scope
    )
    {
        if (index >= hooks.Count)
            return bottom(input);
        HookEntry hook = hooks[index];
        bool nextCalled = false;
        JsonNode? nextResult = null;
        try
        {
            bool settlePromises = eventName == "roblox.launch" || scope == "test";
            return hook.Plugin.Call(
                hook.Function,
                input,
                next =>
                {
                    if (nextCalled)
                        throw new InvalidOperationException("a hook may call next only once");
                    nextCalled = true;
                    nextResult = Run(hooks, index + 1, eventName, next ?? input?.DeepClone(), bottom, scope);
                    return nextResult;
                },
                scope,
                settlePromises
            );
        }
        catch (Exception ex)
        {
            Problem(hook.Plugin.Name, eventName + " hook skipped: " + Describe(ex));
            return nextCalled ? nextResult : Run(hooks, index + 1, eventName, input, bottom, scope);
        }
    }

    public static JsonNode? RenderPane(PaneInfo pane) => Render(pane.Plugin, "Pane", pane.Id);

    public static JsonNode? RenderCustomPage(string plugin, string id) => Render(plugin, "Page", id);

    public static JsonNode? RenderWidget(string plugin, string id) => Render(plugin, "Widget", id);

    public static JsonNode? RenderBanner(BannerInfo banner) => Render(banner.Plugin, "Banner", banner.Id);

    public static JsonNode? RenderDialog(string plugin, string id) => Render(plugin, "Dialog", id);

    private static JsonNode? Render(string plugin, string component, string id)
    {
        JsonObject input = new()
        {
            ["component"] = component,
            ["requestId"] = id,
            ["plugin"] = plugin,
            ["surface"] = "desktop",
        };
        LoadedPlugin? owner;
        lock (Gate)
            owner = Loaded.FirstOrDefault(candidate => candidate.Name == plugin);
        if (owner != null && !owner.HooksFor("ui.render").Any(hook => Matches(hook.Matcher, input)))
            Problem(
                plugin,
                "nothing draws the "
                    + component
                    + " "
                    + id
                    + ", add on('ui.render', { component: '"
                    + component
                    + "', requestId: '"
                    + id
                    + "' }, ...)"
            );
        _renderDepth++;
        try
        {
            return Invoke("ui.render", input, static _ => null, component.ToLowerInvariant() + "." + id);
        }
        finally
        {
            _renderDepth--;
        }
    }

    public static void InvokeHandler(string handlerId, JsonNode? argument)
    {
        int split = handlerId.IndexOf('|');
        if (split <= 0)
            return;
        string name = handlerId[..split];
        LoadedPlugin? plugin;
        lock (Gate)
            plugin = Loaded.FirstOrDefault(candidate => candidate.Name == name);
        if (plugin == null)
            return;
        try
        {
            plugin.InvokeHandler(handlerId, argument);
        }
        catch (Exception ex)
        {
            Problem(name, "a UI handler failed: " + Describe(ex));
        }
        PaneInvalidated?.Invoke(name);
    }

    internal static JsonNode OpenPane(string plugin, string id, string title)
    {
        if (id.Length == 0 || id.Length > 64)
            throw new InvalidOperationException("a pane needs an id of 1 to 64 characters");
        string resolvedTitle = title.Length > 0 ? title : id;
        bool changed = false;
        lock (Gate)
        {
            PaneInfo? existing = OpenPanes.FirstOrDefault(pane => pane.Plugin == plugin && pane.Id == id);
            if (existing == null || !string.Equals(existing.Title, resolvedTitle, StringComparison.Ordinal))
            {
                OpenPanes.RemoveAll(pane => pane.Plugin == plugin && pane.Id == id);
                OpenPanes.Add(new PaneInfo(plugin, id, resolvedTitle));
                changed = true;
            }
        }
        if (changed && _renderDepth == 0)
            PanesChanged?.Invoke();
        return new JsonObject { ["isPlaced"] = true };
    }

    internal static void ClosePane(string plugin, string id)
    {
        int removed;
        lock (Gate)
            removed = OpenPanes.RemoveAll(pane => pane.Plugin == plugin && pane.Id == id);
        if (removed > 0 && _renderDepth == 0)
            PanesChanged?.Invoke();
    }

    internal static JsonNode OpenBanner(string plugin, string id, string position)
    {
        if (id.Length == 0 || id.Length > 64)
            throw new InvalidOperationException("a banner needs an id of 1 to 64 characters");
        string pos = string.Equals(position, "bottom", StringComparison.OrdinalIgnoreCase) ? "bottom" : "top";
        bool changed = false;
        lock (Gate)
        {
            BannerInfo? existing = OpenBanners.FirstOrDefault(b => b.Plugin == plugin && b.Id == id);
            if (existing == null || !string.Equals(existing.Position, pos, StringComparison.Ordinal))
            {
                OpenBanners.RemoveAll(b => b.Plugin == plugin && b.Id == id);
                OpenBanners.Add(new BannerInfo(plugin, id, pos));
                changed = true;
            }
        }
        if (changed && _renderDepth == 0)
            BannersChanged?.Invoke();
        return new JsonObject { ["isPlaced"] = true, ["position"] = pos };
    }

    internal static void CloseBanner(string plugin, string id)
    {
        int removed;
        lock (Gate)
            removed = OpenBanners.RemoveAll(b => b.Plugin == plugin && b.Id == id);
        if (removed > 0 && _renderDepth == 0)
            BannersChanged?.Invoke();
    }

    internal static void SetStatus(string plugin, string? text)
    {
        lock (Gate)
        {
            if (string.IsNullOrEmpty(text))
                Statuses.Remove(plugin);
            else
                Statuses[plugin] = text.Length > 200 ? text[..200] : text;
        }
        if (_renderDepth == 0)
            StatusChanged?.Invoke();
    }

    internal static void Invalidate(string plugin)
    {
        if (_renderDepth == 0)
            PaneInvalidated?.Invoke(plugin);
    }

    internal static string? GetState(string plugin, string key)
    {
        lock (Gate)
            return SessionState.TryGetValue(plugin, out JsonObject? values) ? values[key]?.ToJsonString() : null;
    }

    internal static void SetState(string plugin, string key, string json)
    {
        lock (Gate)
        {
            if (!SessionState.TryGetValue(plugin, out JsonObject? values))
                SessionState[plugin] = values = new JsonObject();
            values[key] = JsonNode.Parse(json);
        }
        if (_renderDepth == 0)
            PaneInvalidated?.Invoke(plugin);
    }

    internal static string Describe(Exception ex)
    {
        string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length > 300 ? message[..300] : message;
    }

    internal sealed record HookEntry(LoadedPlugin Plugin, string Event, JsonObject? Matcher, JsValue Function);

    internal sealed class LoadedPlugin : IDisposable
    {
        private const int MaxTreeDepth = 64;

        private const int MaxTreeNodes = 50000;

        private const string Prelude = """
			(() => {
			  const host = globalThis.__voidstrapHost;
			  delete globalThis.__voidstrapHost;
			  const parse = text => text === undefined || text === null ? undefined : JSON.parse(text);
			  host.setWatch(promise => { promise.then(undefined, error => host.problem(String(error && error.message || error))); });
			  const elements = Object.freeze({
			    Box: 'Box', Stack: 'Box', Wrap: 'Wrap', Grid: 'Grid', Canvas: 'Canvas', Scroll: 'Scroll',
			    Text: 'Text', Badge: 'Badge', Icon: 'Icon', ColorSwatch: 'ColorSwatch',
			    Button: 'Button', Toggle: 'Toggle', CheckBox: 'CheckBox', TextInput: 'TextInput',
			    NumberInput: 'NumberInput', Slider: 'Slider', Select: 'Select',
			    ProgressBar: 'ProgressBar', InfoBar: 'InfoBar', Divider: 'Divider', Spacer: 'Spacer',
			    Image: 'Image', Sprite: 'Sprite', Card: 'Card', Expander: 'Expander', Tabs: 'Tabs', Table: 'Table'
			  });
			  globalThis.h = (type, props, ...children) => ({ type, props: props || {}, children: children.flat(Infinity).filter(child => child !== null && child !== undefined && child !== false) });
			  globalThis.Fragment = 'Box';
			  globalThis.$ = Object.freeze({
			    log: text => host.log(String(text)),
			    mod: Object.freeze({
			      name: host.modName,
			      version: host.modVersion,
			      options: () => parse(host.modOptions())
			    }),
			    app: Object.freeze({
			      version: host.version,
			      platform: host.platform,
			      texts: () => parse(host.appTextList()),
			      setText: (target, text) => host.appTextSet(String(target), text === undefined || text === null ? '' : String(text)),
			      clearText: target => Boolean(host.appTextClear(String(target))),
			      background: () => parse(host.appBackgroundGet()),
			      setBackground: request => String(host.appBackgroundSet(typeof request === 'string' ? JSON.stringify({ file: request }) : JSON.stringify(request || {}))),
			      clearBackground: () => host.appBackgroundClear()
			    }),
			    ui: Object.freeze({
			      resolve: () => elements,
			      open: request => parse(host.uiOpen(JSON.stringify(request || {}))),
			      close: id => host.uiClose(String(id)),
			      openBanner: request => parse(host.uiOpenBanner(JSON.stringify(typeof request === 'string' ? { id: request } : (request || {})))),
			      closeBanner: id => host.uiCloseBanner(String(id)),
			      attach: request => parse(host.uiAttach(JSON.stringify(request || {}))),
			      detach: id => host.uiDetach(String(id)),
			      anchors: () => parse(host.uiAnchors()),
			      tree: request => parse(host.uiTree(JSON.stringify(request || {}))),
			      query: target => parse(host.uiQuery(JSON.stringify(target || {}))),
			      rule: request => parse(host.uiRule(JSON.stringify(request || {}))),
			      move: request => parse(host.uiMove(JSON.stringify(request || {}))),
			      resources: () => parse(host.uiResources()),
			      setResources: values => parse(host.uiSetResources(JSON.stringify(values || {}))),
			      redesign: spec => parse(host.uiRedesign(JSON.stringify(spec || {}))),
			      undo: id => host.uiUndo(String(id)),
			      reset: () => host.uiReset(),
			      changes: () => parse(host.uiChanges()),
			      openDialog: request => host.uiOpenDialog(JSON.stringify(typeof request === 'string' ? { id: request } : (request || {}))),
			      closeDialog: id => host.uiCloseDialog(String(id)),
			      message: (text, title) => host.uiMessage(String(text), title === undefined ? '' : String(title)),
			      confirm: (text, title) => Boolean(host.uiConfirm(String(text), title === undefined ? '' : String(title))),
			      status: text => host.uiStatus(text === undefined || text === null ? '' : String(text)),
			      toast: (title, text) => host.toast(String(title), text === undefined ? '' : String(text)),
			      refresh: () => host.uiRefresh()
			    }),
			    settings: Object.freeze({
			      list: () => parse(host.settingList()),
			      get: name => parse(host.settingGet(String(name))),
			      set: (name, value) => host.settingSet(String(name), JSON.stringify(value))
			    }),
			    fastFlags: Object.freeze({
			      getAll: () => parse(host.flagGetAll()),
			      get: name => parse(host.flagGet(String(name))),
			      set: (name, value) => host.flagSet(String(name), JSON.stringify(value === undefined ? null : value)),
			      remove: name => host.flagRemove(String(name)),
			      clear: () => host.flagClear()
			    }),
			    store: Object.freeze({
			      get: key => parse(host.storeGet(String(key))),
			      set: (key, value) => host.storeSet(String(key), JSON.stringify(value === undefined ? null : value)),
			      remove: key => host.storeRemove(String(key)),
			      keys: () => parse(host.storeKeys())
			    }),
			    clock: Object.freeze({
			      now: () => Date.now(),
			      after: (ms, fn) => host.clockStart(Number(ms), fn, false),
			      every: (ms, fn) => host.clockStart(Number(ms), fn, true),
			      cancel: id => host.clockCancel(Number(id))
			    }),
			    net: Object.freeze({
			      fetch: request => host.netFetch(JSON.stringify(typeof request === 'string' ? { url: request } : (request || {}))).then(text => JSON.parse(text))
			    }),
			    sidebar: Object.freeze({
			      get: () => parse(host.sidebarGet()),
			      set: layout => host.sidebarSet(JSON.stringify(layout || {})),
			      reset: () => host.sidebarReset(),
			      addPage: page => parse(host.sidebarAddPage(JSON.stringify(page || {}))),
			      removePage: id => host.sidebarRemovePage(String(id))
			    }),
			    modpages: Object.freeze({
			      add: spec => parse(host.modpagesAdd(JSON.stringify(spec || {}))),
			      remove: id => host.modpagesRemove(String(id)),
			      list: () => parse(host.modpagesList())
			    }),
			    nav: Object.freeze({
			      current: () => String(host.navCurrent()),
			      pages: () => parse(host.navPages()),
			      open: page => host.navOpen(String(page))
			    }),
			    theme: Object.freeze({
			      get: () => parse(host.themeGet()),
			      set: spec => host.themeSet(JSON.stringify(spec || {})),
			      getBrush: key => host.themeGetBrush(String(key)),
			      setBrush: (key, color) => host.themeSetBrush(String(key), String(color)),
			      resetBrushes: () => host.themeResetBrushes(),
			      listBrushes: () => parse(host.themeListBrushes())
			    }),
			    window: Object.freeze({
			      get: () => parse(host.windowGet()),
			      set: spec => host.windowSet(JSON.stringify(spec || {}))
			    }),
			    roblox: Object.freeze({
			      status: () => parse(host.robloxStatus()),
			      launch: spec => host.robloxLaunch(JSON.stringify(spec || {})),
			      request: request => host.robloxRequest(JSON.stringify(typeof request === 'string' ? { url: request } : (request || {}))).then(text => JSON.parse(text)),
			      account: () => host.robloxAccount().then(text => JSON.parse(text))
			    }),
			    clipboard: Object.freeze({
			      get: () => String(host.clipboardGet()),
			      set: text => host.clipboardSet(String(text === undefined || text === null ? '' : text))
			    }),
			    cursor: Object.freeze({
			      get: () => parse(host.cursorGet())
			    }),
			    state: Object.freeze({
			      get: key => parse(host.stateGet(String(key))),
			      set: (key, value) => host.stateSet(String(key), JSON.stringify(value === undefined ? null : value))
			    })
			  });
			  globalThis.atom = (ref, initial) => Object.freeze({ ref: String(ref), initial });
			  globalThis.read = ($, item) => { const value = $.state.get(item.ref); return value === undefined ? item.initial : value; };
			  globalThis.update = ($, item, change) => $.state.set(item.ref, change(read($, item)));
			})();
			""";

        private readonly object _gate = new();

        private readonly Engine _engine;

        private readonly List<(string Event, JsonObject? Matcher, JsValue Function)> _hooks = [];

        private volatile IReadOnlyList<string> _hookDescriptions = Array.Empty<string>();

        private readonly Dictionary<string, JsValue> _handlers = new(StringComparer.Ordinal);

        private readonly PluginStore _store;

        private const int MaxTimers = 32;

        private const int MinRepeatMs = 500;

        private const int MaxResponseBytes = 1024 * 1024;

        private static readonly TimeSpan AwaitLimit = TimeSpan.FromSeconds(10);

        private static readonly HttpClient NetClient = new(
            new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(8),
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(10),
        };

        private readonly Dictionary<int, Timer> _timers = [];

        private readonly Dictionary<string, JsonNode?> _sandboxState = new(StringComparer.Ordinal);

        private readonly Dictionary<string, JsonNode?> _sandboxStore = new(StringComparer.Ordinal);

        private readonly List<string> _warnings = [];

        private JsValue _watch = JsValue.Undefined;

        public IReadOnlyList<string> Warnings => _warnings;

        private readonly HashSet<string> _networkHosts;

        private readonly bool _sandbox;

        private int _handlerCounter;

        private int _timerCounter;

        private int _pending;

        private bool _disposed;

        public string Name { get; }

        public PluginManifest Manifest { get; }

        public string SourceSignature { get; }

        private LoadedPlugin(PluginManifest manifest, bool sandbox = false)
        {
            Name = manifest.Name;
            Manifest = manifest;
            SourceSignature = GetReloadSignature(manifest.Directory, manifest.Name);
            _sandbox = sandbox;
            _store = new PluginStore(manifest.Name);
            _networkHosts = new HashSet<string>(manifest.NetworkHosts, StringComparer.OrdinalIgnoreCase);
            _engine = new Engine(options =>
            {
                options.Strict();
                options.TimeoutInterval(TimeSpan.FromSeconds(2));
                options.LimitMemory(64L * 1024 * 1024);
                options.LimitRecursion(256);
                options.EnableModules(new ModModuleLoader(manifest.Directory));
            });
        }

        public static LoadedPlugin Load(PluginManifest manifest, bool sandbox = false)
        {
            ModTypes.Write(manifest.Directory);
            LoadedPlugin plugin = new(manifest, sandbox);
            try
            {
                plugin.Initialize(manifest);
                return plugin;
            }
            catch
            {
                plugin.Dispose();
                throw;
            }
        }

        private void Initialize(PluginManifest manifest)
        {
            lock (_gate)
            {
                _engine.SetValue("__voidstrapHost", BuildHost());
                _engine.Execute(Prelude);
                JsValue module = _engine.Modules.Import("./" + manifest.ModulePath.Replace('\\', '/'));
                JsValue register = module.Get("register");
                if (register is not Function)
                    throw new InvalidOperationException("the module does not export a register function");
                ClrFunction on = new(
                    _engine,
                    "on",
                    (_, arguments) =>
                    {
                        string eventName = arguments.Length > 0 ? arguments[0].ToString() : "";
                        JsValue hook =
                            arguments.Length > 2 ? arguments[2]
                            : arguments.Length > 1 ? arguments[1]
                            : JsValue.Undefined;
                        JsonObject? matcher = arguments.Length > 2 ? ToNode(arguments[1], null) as JsonObject : null;
                        if (eventName.Length == 0 || hook is not Function)
                            throw new InvalidOperationException("on needs an event name and a hook function");
                        if (!KnownEvents.Contains(eventName))
                        {
                            _warnings.Add("the event " + eventName + " does not exist, this hook never runs");
                            if (!_sandbox)
                                Problem(Name, _warnings[^1]);
                        }
                        _hooks.Add((eventName, matcher, hook));
                        return JsValue.Undefined;
                    }
                );
                _engine.Constraints.Reset();
                Settle(_engine.Invoke(register, on, ToJs(_sandbox ? manifest.Options : PluginOptions.Effective(manifest))));
                _hookDescriptions = _hooks
                    .Select(hook => hook.Event + (hook.Matcher != null ? " " + hook.Matcher.ToJsonString() : ""))
                    .ToArray();
            }
        }

        private ClrFunction Host(string name, Func<JsValue[], JsValue> body) => new(_engine, name, (_, arguments) => body(arguments));

        private JsObject BuildHost()
        {
            JsObject host = new(_engine);
            host.Set("version", App.Version);
            host.Set("platform", Voidstrap.Utility.Platform.IsWindows ? "windows" : Voidstrap.Utility.Platform.IsMacOS ? "macos" : "linux");
            host.Set("modName", Name);
            host.Set("modVersion", Manifest.Version);
            host.Set(
                "modOptions",
                Host("modOptions", _ => (_sandbox ? Manifest.Options : PluginOptions.Effective(Manifest)).ToJsonString())
            );
            host.Set(
                "setWatch",
                Host(
                    "setWatch",
                    a =>
                    {
                        _watch = a.Length > 0 ? a[0] : JsValue.Undefined;
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "problem",
                Host(
                    "problem",
                    a =>
                    {
                        Problem(Name, "a promise was rejected: " + Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "log",
                Host(
                    "log",
                    a =>
                    {
                        App.Logger.WriteLine(LogIdent, Name + ": " + Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "toast",
                Host(
                    "toast",
                    a =>
                    {
                        if (!_sandbox)
                            Voidstrap.Utility.AppNotifications.RecordInfo("mod." + Name, Arg(a, 0), Arg(a, 1));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiOpen",
                Host(
                    "uiOpen",
                    a =>
                    {
                        JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
                        if (_sandbox)
                            return new JsonObject { ["isPlaced"] = true }.ToJsonString();
                        return OpenPane(Name, request["id"]?.ToString() ?? "", request["title"]?.ToString() ?? "").ToJsonString();
                    }
                )
            );
            host.Set(
                "uiClose",
                Host(
                    "uiClose",
                    a =>
                    {
                        if (!_sandbox)
                            ClosePane(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiOpenBanner",
                Host(
                    "uiOpenBanner",
                    a =>
                    {
                        JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
                        string pos = string.Equals(request["position"]?.ToString(), "bottom", StringComparison.OrdinalIgnoreCase)
                            ? "bottom"
                            : "top";
                        if (_sandbox)
                            return new JsonObject { ["isPlaced"] = true, ["position"] = pos }.ToJsonString();
                        return OpenBanner(Name, request["id"]?.ToString() ?? "banner", pos).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiAttach",
                Host(
                    "uiAttach",
                    a =>
                    {
                        JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
                        if (_sandbox)
                            return new JsonObject { ["isPlaced"] = true, ["reason"] = "" }.ToJsonString();
                        return PluginAnchors.Attach(Name, request).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiDetach",
                Host(
                    "uiDetach",
                    a =>
                    {
                        if (!_sandbox)
                            PluginAnchors.Detach(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiAnchors",
                Host(
                    "uiAnchors",
                    _ => _sandbox ? "[]" : PluginNavigation.InvokeUiBounded(() => PluginAnchors.Map().ToJsonString(), "[]", 1500)
                )
            );
            host.Set(
                "uiTree",
                Host(
                    "uiTree",
                    a =>
                    {
                        return _sandbox ? "{}" : PluginRestyle.Tree(JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject()).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiQuery",
                Host(
                    "uiQuery",
                    a =>
                    {
                        return _sandbox ? "[]" : PluginRestyle.Query(JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject()).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiRule",
                Host(
                    "uiRule",
                    a =>
                    {
                        return _sandbox ? new JsonObject { ["ok"] = true, ["matched"] = 0, ["errors"] = new JsonArray() }.ToJsonString() : PluginRestyle.AddRule(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject()).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiMove",
                Host(
                    "uiMove",
                    a =>
                    {
                        return _sandbox ? new JsonObject { ["ok"] = true, ["matched"] = 0, ["errors"] = new JsonArray() }.ToJsonString() : PluginRestyle.AddMove(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject()).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiResources",
                Host(
                    "uiResources",
                    a =>
                    {
                        return _sandbox ? "[]" : PluginRestyle.ListResources().ToJsonString();
                    }
                )
            );
            host.Set(
                "uiSetResources",
                Host(
                    "uiSetResources",
                    a =>
                    {
                        return _sandbox ? new JsonObject { ["ok"] = true, ["matched"] = 0, ["errors"] = new JsonArray() }.ToJsonString() : PluginRestyle.SetResources(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject()).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiRedesign",
                Host(
                    "uiRedesign",
                    a =>
                    {
                        return _sandbox ? new JsonObject { ["ok"] = true, ["matched"] = 0, ["errors"] = new JsonArray() }.ToJsonString() : PluginRestyle.Redesign(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject()).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiUndo",
                Host(
                    "uiUndo",
                    a =>
                    {
                        if (!_sandbox)
                            PluginRestyle.Remove(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiReset",
                Host(
                    "uiReset",
                    a =>
                    {
                        if (!_sandbox)
                            PluginRestyle.Clear(Name);
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiChanges",
                Host(
                    "uiChanges",
                    a =>
                    {
                        return _sandbox ? "{}" : PluginRestyle.List(Name).ToJsonString();
                    }
                )
            );
            host.Set(
                "uiCloseBanner",
                Host(
                    "uiCloseBanner",
                    a =>
                    {
                        if (!_sandbox)
                            CloseBanner(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiOpenDialog",
                Host(
                    "uiOpenDialog",
                    a =>
                    {
                        JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
                        if (!_sandbox)
                            PluginDialogManager.OpenDialog(Name, request);
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiCloseDialog",
                Host(
                    "uiCloseDialog",
                    a =>
                    {
                        if (!_sandbox)
                            PluginDialogManager.CloseDialog(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiMessage",
                Host(
                    "uiMessage",
                    a =>
                    {
                        if (!_sandbox)
                            PluginDialogManager.ShowMessage(Name, Arg(a, 0), Arg(a, 1));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("uiConfirm", Host("uiConfirm", a => _sandbox ? true : PluginDialogManager.ShowConfirm(Name, Arg(a, 0), Arg(a, 1))));
            host.Set(
                "uiStatus",
                Host(
                    "uiStatus",
                    a =>
                    {
                        if (!_sandbox)
                            SetStatus(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "uiRefresh",
                Host(
                    "uiRefresh",
                    _ =>
                    {
                        if (!_sandbox)
                            Invalidate(Name);
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("settingList", Host("settingList", _ => PluginSettings.List()));
            host.Set("settingGet", Host("settingGet", a => PluginSettings.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
            host.Set(
                "settingSet",
                Host(
                    "settingSet",
                    a =>
                    {
                        if (!_sandbox)
                            PluginRollback.TrackSettings(Name, [Arg(a, 0)], () => PluginSettings.Set(Name, Arg(a, 0), Arg(a, 1)));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("flagGetAll", Host("flagGetAll", _ => PluginFastFlags.GetAll()));
            host.Set("flagGet", Host("flagGet", a => PluginFastFlags.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
            host.Set(
                "flagSet",
                Host(
                    "flagSet",
                    a =>
                    {
                        if (!_sandbox)
                            PluginRollback.TrackFlags(Name, [Arg(a, 0)], () => PluginFastFlags.Set(Name, Arg(a, 0), Arg(a, 1)));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "flagRemove",
                Host(
                    "flagRemove",
                    a =>
                    {
                        if (!_sandbox)
                            PluginRollback.TrackFlags(Name, [Arg(a, 0)], () => PluginFastFlags.Remove(Name, Arg(a, 0)));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "flagClear",
                Host(
                    "flagClear",
                    _ =>
                    {
                        if (!_sandbox)
                            PluginRollback.TrackFlags(Name, null, () => PluginFastFlags.Clear(Name));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "storeGet",
                Host(
                    "storeGet",
                    a =>
                    {
                        if (_sandbox)
                            return _sandboxStore.TryGetValue(Arg(a, 0), out JsonNode? val) && val != null
                                ? val.ToJsonString()
                                : JsValue.Undefined;
                        return _store.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "storeSet",
                Host(
                    "storeSet",
                    a =>
                    {
                        if (_sandbox)
                            _sandboxStore[Arg(a, 0)] = JsonNode.Parse(Arg(a, 1));
                        else
                            _store.Set(Arg(a, 0), Arg(a, 1));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "storeRemove",
                Host(
                    "storeRemove",
                    a =>
                    {
                        if (_sandbox)
                            _sandboxStore.Remove(Arg(a, 0));
                        else
                            _store.Remove(Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "storeKeys",
                Host(
                    "storeKeys",
                    _ =>
                        new JsonArray(
                            (_sandbox ? _sandboxStore.Keys.ToList() : _store.Keys()).Select(key => (JsonNode?)key).ToArray()
                        ).ToJsonString()
                )
            );
            host.Set(
                "clockStart",
                Host(
                    "clockStart",
                    a =>
                    {
                        if (a.Length < 2 || a[1] is not Function callback)
                            throw new InvalidOperationException("the clock needs a delay and a function");
                        bool repeat = a.Length > 2 && a[2].IsBoolean() && a[2].AsBoolean();
                        return _sandbox ? 1 : StartTimer(a[0].IsNumber() ? a[0].AsNumber() : 0, callback, repeat);
                    }
                )
            );
            host.Set(
                "clockCancel",
                Host(
                    "clockCancel",
                    a =>
                    {
                        if (!_sandbox && a.Length > 0 && a[0].IsNumber())
                            CancelTimer((int)a[0].AsNumber());
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("netFetch", Host("netFetch", a => Fetch(JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject())));
            host.Set("sidebarGet", Host("sidebarGet", _ => PluginSidebar.Get().ToJsonString()));
            host.Set(
                "sidebarSet",
                Host(
                    "sidebarSet",
                    a =>
                    {
                        if (!_sandbox)
                            PluginRollback.TrackSettings(Name, PluginRollback.SidebarSettings, () => PluginSidebar.Set(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject()));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "sidebarReset",
                Host(
                    "sidebarReset",
                    _ =>
                    {
                        if (!_sandbox)
                            PluginRollback.TrackSettings(Name, PluginRollback.SidebarSettings, () => PluginSidebar.Reset(Name));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "sidebarAddPage",
                Host(
                    "sidebarAddPage",
                    a =>
                    {
                        JsonObject spec = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
                        if (_sandbox)
                        {
                            string id = (spec["id"]?.ToString() ?? "main").Trim();
                            string title = spec["title"]?.ToString() ?? id;
                            string section = spec["section"]?.ToString() ?? "Core";
                            string icon = spec["icon"]?.ToString() ?? "Apps24";
                            return new JsonObject
                            {
                                ["key"] = PluginSidebar.MakePageKey(Name, id),
                                ["id"] = id,
                                ["title"] = title,
                                ["section"] = section,
                                ["icon"] = icon,
                            }.ToJsonString();
                        }
                        return PluginSidebar.AddPage(Name, spec).ToJsonString();
                    }
                )
            );
            host.Set(
                "sidebarRemovePage",
                Host(
                    "sidebarRemovePage",
                    a =>
                    {
                        if (!_sandbox)
                            PluginSidebar.RemovePage(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "modpagesAdd",
                Host(
                    "modpagesAdd",
                    a =>
                    {
                        JsonObject spec = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
                        if (_sandbox)
                        {
                            string id = (spec["id"]?.ToString() ?? "main").Trim();
                            string title = spec["title"]?.ToString() ?? id;
                            string icon = spec["icon"]?.ToString() ?? "PuzzlePiece24";
                            return new JsonObject
                            {
                                ["key"] = "ModMenu_Test_" + id,
                                ["id"] = id,
                                ["title"] = title,
                                ["icon"] = icon,
                            }.ToJsonString();
                        }
                        return PluginModPages.AddPage(Name, spec).ToJsonString();
                    }
                )
            );
            host.Set(
                "modpagesRemove",
                Host(
                    "modpagesRemove",
                    a =>
                    {
                        if (!_sandbox)
                            PluginModPages.RemovePage(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("modpagesList", Host("modpagesList", _ => _sandbox ? "[]" : PluginModPages.ListJson()));
            host.Set("navCurrent", Host("navCurrent", _ => PluginNavigation.Current()));
            host.Set("navPages", Host("navPages", _ => PluginNavigation.Pages()));
            host.Set(
                "navOpen",
                Host(
                    "navOpen",
                    a =>
                    {
                        if (!_sandbox)
                            PluginNavigation.Open(Name, Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("themeGet", Host("themeGet", _ => PluginTheme.Get()));
            host.Set(
                "themeSet",
                Host(
                    "themeSet",
                    a =>
                    {
                        if (!_sandbox)
                        {
                            JsonObject spec = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
                            if (spec["accentColor"] != null)
                                PluginRollback.MarkBrushes(Name);
                            PluginRollback.TrackBackground<object?>(
                                Name,
                                () =>
                                {
                                    PluginRollback.TrackSettings(Name, PluginRollback.ThemeSettings, () => PluginTheme.Set(Name, spec));
                                    return null;
                                }
                            );
                        }
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("themeGetBrush", Host("themeGetBrush", a => PluginTheme.GetBrush(Arg(a, 0)) is string hex ? hex : JsValue.Undefined));
            host.Set(
                "themeSetBrush",
                Host(
                    "themeSetBrush",
                    a =>
                    {
                        if (!_sandbox)
                        {
                            PluginRollback.MarkBrushes(Name);
                            PluginTheme.SetBrush(Name, Arg(a, 0), Arg(a, 1));
                        }
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "themeResetBrushes",
                Host(
                    "themeResetBrushes",
                    _ =>
                    {
                        if (!_sandbox)
                            PluginTheme.ResetBrushes(Name);
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("themeListBrushes", Host("themeListBrushes", _ => PluginTheme.ListBrushes()));
            host.Set(
                "appTextList",
                Host(
                    "appTextList",
                    _ =>
                    {
                        JsonObject list = new();
                        foreach (KeyValuePair<string, string> pair in Voidstrap.Utility.AppControl.ListText())
                            list[pair.Key] = pair.Value;
                        return list.ToJsonString();
                    }
                )
            );
            host.Set(
                "appTextSet",
                Host(
                    "appTextSet",
                    a =>
                    {
                        if (!_sandbox)
                            return PluginRollback.TrackTexts(Name, () => Voidstrap.Utility.AppControl.SetText(Arg(a, 0), Arg(a, 1)));
                        return Arg(a, 0);
                    }
                )
            );
            host.Set(
                "appTextClear",
                Host(
                    "appTextClear",
                    a =>
                    {
                        if (!_sandbox)
                            return PluginRollback.TrackTexts(Name, () => Voidstrap.Utility.AppControl.ClearText(Arg(a, 0)));
                        return false;
                    }
                )
            );
            host.Set("appBackgroundGet", Host("appBackgroundGet", _ => Voidstrap.Utility.AppControl.GetBackground().ToJsonString()));
            host.Set(
                "appBackgroundSet",
                Host(
                    "appBackgroundSet",
                    a =>
                    {
                        if (_sandbox)
                            return "";
                        JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
                        string stored = PluginRollback.TrackBackground(
                            Name,
                            () =>
                                Voidstrap.Utility.AppControl.SetBackground(
                                    request["file"]?.ToString() ?? "",
                                    request["gradientOpacity"] is JsonValue grad && grad.TryGetValue(out double gradValue) ? gradValue : null,
                                    request["blackOverlayOpacity"] is JsonValue black && black.TryGetValue(out double blackValue)
                                        ? blackValue
                                        : null,
                                    request["displayEverywhere"] is JsonValue disp && disp.TryGetValue(out bool dispValue) ? dispValue : null
                                )
                        );
                        return stored;
                    }
                )
            );
            host.Set(
                "appBackgroundClear",
                Host(
                    "appBackgroundClear",
                    _ =>
                    {
                        if (!_sandbox)
                            PluginRollback.TrackBackground<object?>(
                                Name,
                                () =>
                                {
                                    Voidstrap.Utility.AppControl.ClearBackground();
                                    return null;
                                }
                            );
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("windowGet", Host("windowGet", _ => PluginWindow.Get()));
            host.Set(
                "windowSet",
                Host(
                    "windowSet",
                    a =>
                    {
                        if (!_sandbox)
                            PluginWindow.Set(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject());
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("robloxStatus", Host("robloxStatus", _ => PluginRoblox.Status()));
            host.Set(
                "robloxRequest",
                Host(
                    "robloxRequest",
                    a =>
                    {
                        if (_sandbox)
                            return RunAsync(() =>
                                Task.FromResult(
                                    new JsonObject
                                    {
                                        ["status"] = 200,
                                        ["ok"] = true,
                                        ["text"] = "{}",
                                    }
                                )
                            );
                        PluginRobloxAccount.EnsureAllowed(Name);
                        (Uri uri, string method, string? body) = PluginRobloxAccount.Validate(
                            JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject()
                        );
                        return RunAsync(() => PluginRobloxAccount.SendAsync(uri, method, body, CancellationToken.None));
                    }
                )
            );
            host.Set(
                "robloxAccount",
                Host(
                    "robloxAccount",
                    _ =>
                    {
                        if (_sandbox)
                            return RunAsync(() =>
                                Task.FromResult(
                                    new JsonObject
                                    {
                                        ["signedIn"] = true,
                                        ["userId"] = 1,
                                        ["username"] = "TestUser",
                                        ["displayName"] = "Test User",
                                    }
                                )
                            );
                        PluginRobloxAccount.EnsureAllowed(Name);
                        return RunAsync(() => PluginRobloxAccount.AccountAsync(CancellationToken.None));
                    }
                )
            );
            host.Set(
                "robloxLaunch",
                Host(
                    "robloxLaunch",
                    a =>
                    {
                        if (!_sandbox)
                            PluginRoblox.Launch(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject());
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("clipboardGet", Host("clipboardGet", _ => PluginClipboard.Get()));
            host.Set(
                "clipboardSet",
                Host(
                    "clipboardSet",
                    a =>
                    {
                        if (!_sandbox)
                            PluginClipboard.Set(Arg(a, 0));
                        return JsValue.Undefined;
                    }
                )
            );
            host.Set("cursorGet", Host("cursorGet", _ => PluginCursor.Get().ToJsonString()));
            host.Set(
                "stateGet",
                Host(
                    "stateGet",
                    a =>
                    {
                        if (_sandbox)
                            return _sandboxState.TryGetValue(Arg(a, 0), out JsonNode? val) && val != null
                                ? val.ToJsonString()
                                : JsValue.Undefined;
                        return GetState(Name, Arg(a, 0)) is string json ? json : JsValue.Undefined;
                    }
                )
            );
            host.Set(
                "stateSet",
                Host(
                    "stateSet",
                    a =>
                    {
                        if (_sandbox)
                            _sandboxState[Arg(a, 0)] = JsonNode.Parse(Arg(a, 1));
                        else
                            SetState(Name, Arg(a, 0), Arg(a, 1));
                        return JsValue.Undefined;
                    }
                )
            );
            return host;
        }

        private JsValue StartTimer(double milliseconds, JsValue callback, bool repeat)
        {
            if (_timers.Count >= MaxTimers)
                throw new InvalidOperationException("a mod can run at most " + MaxTimers + " timers");
            int delay = (int)Math.Clamp(double.IsFinite(milliseconds) ? milliseconds : 0, repeat ? MinRepeatMs : 0, int.MaxValue / 2);
            int id = ++_timerCounter;
            Timer timer = new(OnTimer, (id, callback, repeat), delay, repeat ? delay : Timeout.Infinite);
            _timers[id] = timer;
            return id;
        }

        private void CancelTimer(int id)
        {
            if (_timers.Remove(id, out Timer? timer))
                timer.Dispose();
        }

        private void OnTimer(object? state)
        {
            if (state is not ValueTuple<int, JsValue, bool> tick)
                return;
            bool shouldInvalidate;
            lock (_gate)
            {
                if (_disposed || !_timers.ContainsKey(tick.Item1))
                    return;
                if (!tick.Item3)
                    CancelTimer(tick.Item1);
                try
                {
                    _engine.Constraints.Reset();
                    JsValue result = _engine.Invoke(tick.Item2, JsValue.Undefined, Array.Empty<object>());
                    Watch(result);
                    _engine.Advanced.ProcessTasks();
                    shouldInvalidate = !result.IsPromise();
                }
                catch (Exception ex)
                {
                    CancelTimer(tick.Item1);
                    Problem(Name, "a clock callback failed and its timer stopped: " + Describe(ex));
                    shouldInvalidate = false;
                }
            }
            if (shouldInvalidate)
                Invalidate(Name);
        }

        private JsValue Fetch(JsonObject request)
        {
            string url = request["url"]?.ToString() ?? "";
            if (
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !_networkHosts.Contains(uri.IdnHost)
            )
                throw new InvalidOperationException("the mod may only reach https hosts listed under network in its plugin.json");
            string method = (request["method"]?.ToString() ?? "GET").ToUpperInvariant();
            if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
                throw new InvalidOperationException("unsupported request method " + method);
            string? body = request["body"]?.ToString();
            JsonObject? headers = request["headers"] as JsonObject;
            var (promise, resolve, reject) = _engine.Advanced.RegisterPromise();
            _pending++;
            _ = Task.Run(async () =>
            {
                JsonObject? result = null;
                string error = "";
                try
                {
                    using CancellationTokenSource deadline = new(AwaitLimit);
                    using HttpRequestMessage message = new(new HttpMethod(method), uri);
                    if (body != null)
                        message.Content = new StringContent(
                            body,
                            System.Text.Encoding.UTF8,
                            request["contentType"]?.ToString() ?? "application/json"
                        );
                    if (headers != null)
                        foreach (KeyValuePair<string, JsonNode?> header in headers)
                            if (
                                !header.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase)
                                && !header.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                            )
                                message.Headers.TryAddWithoutValidation(header.Key, header.Value?.ToString());
                    using HttpResponseMessage response = await NetClient
                        .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                        .ConfigureAwait(false);
                    string text = await Voidstrap
                        .Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, deadline.Token)
                        .ConfigureAwait(false);
                    result = new JsonObject
                    {
                        ["status"] = (int)response.StatusCode,
                        ["ok"] = response.IsSuccessStatusCode,
                        ["text"] = text,
                    };
                }
                catch (Exception ex)
                {
                    error = Describe(ex);
                }
                bool idle;
                lock (_gate)
                {
                    _pending--;
                    if (!_disposed)
                    {
                        try
                        {
                            _engine.Constraints.Reset();
                            if (result != null)
                                resolve(result.ToJsonString());
                            else
                                reject(error);
                            _engine.Advanced.ProcessTasks();
                        }
                        catch (Exception ex)
                        {
                            Problem(Name, "a network callback failed: " + Describe(ex));
                        }
                    }
                    idle = _pending == 0;
                    Monitor.PulseAll(_gate);
                }
                if (idle)
                    Invalidate(Name);
            });
            return promise;
        }

        private JsValue RunAsync(Func<Task<JsonObject>> work)
        {
            var (promise, resolve, reject) = _engine.Advanced.RegisterPromise();
            _pending++;
            _ = Task.Run(async () =>
            {
                JsonObject? result = null;
                string error = "";
                try
                {
                    result = await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    error = Describe(ex);
                }
                bool idle;
                lock (_gate)
                {
                    _pending--;
                    if (!_disposed)
                    {
                        try
                        {
                            _engine.Constraints.Reset();
                            if (result != null)
                                resolve(result.ToJsonString());
                            else
                                reject(error);
                            _engine.Advanced.ProcessTasks();
                        }
                        catch (Exception ex)
                        {
                            Problem(Name, "a request callback failed: " + Describe(ex));
                        }
                    }
                    idle = _pending == 0;
                    Monitor.PulseAll(_gate);
                }
                if (idle)
                    Invalidate(Name);
            });
            return promise;
        }

        private void Watch(JsValue value)
        {
            if (value.IsPromise() && _watch is Function)
                _engine.Invoke(_watch, JsValue.Undefined, new object[] { value });
        }

        private JsValue Settle(JsValue value)
        {
            if (!value.IsPromise())
                return value;
            DateTime deadline = DateTime.UtcNow + AwaitLimit;
            while (!_disposed && _pending > 0 && DateTime.UtcNow < deadline)
                Monitor.Wait(_gate, 50);
            if (_disposed || _pending > 0)
                return JsValue.Undefined;
            _engine.Constraints.Reset();
            try
            {
                return value.UnwrapIfPromise(TimeSpan.FromMilliseconds(100));
            }
            catch (Jint.Runtime.PromiseRejectedException ex)
            {
                Problem(Name, "a promise was rejected: " + Describe(ex));
                return JsValue.Undefined;
            }
            catch (Exception ex)
            {
                Problem(Name, "evaluating promise failed: " + Describe(ex));
                return JsValue.Undefined;
            }
        }

        private const string TestPrelude = """
			(() => {
			  const host = globalThis.__voidstrapTest;
			  delete globalThis.__voidstrapTest;
			  const tests = [];
			  const show = value => JSON.stringify(value);
			  const matchers = (actual, negated) => {
			    const check = (passed, message) => { if (passed === negated) throw new Error((negated ? 'not: ' : '') + message); };
			    return Object.freeze({
			      toBe: expected => check(actual === expected, 'expected ' + show(expected) + ' but got ' + show(actual)),
			      toEqual: expected => check(show(actual) === show(expected), 'expected ' + show(expected) + ' but got ' + show(actual)),
			      toBeTruthy: () => check(Boolean(actual), 'expected a truthy value but got ' + show(actual)),
			      toBeFalsy: () => check(!actual, 'expected a falsy value but got ' + show(actual)),
			      toBeDefined: () => check(actual !== undefined, 'expected a defined value'),
			      toBeNull: () => check(actual === null, 'expected null but got ' + show(actual)),
			      toBeGreaterThan: expected => check(actual > expected, 'expected ' + show(actual) + ' to be greater than ' + show(expected)),
			      toBeLessThan: expected => check(actual < expected, 'expected ' + show(actual) + ' to be less than ' + show(expected)),
			      toContain: expected => check(Boolean(actual && actual.includes && actual.includes(expected)), show(actual) + ' does not contain ' + show(expected)),
			      toThrow: () => { let threw = false; try { actual(); } catch { threw = true; } check(threw, 'expected the function to throw'); }
			    });
			  };
			  globalThis.test = (name, body) => tests.push({ name: String(name), body });
			  globalThis.expect = actual => Object.freeze({ ...matchers(actual, false), not: matchers(actual, true) });
			  const parse = text => text === undefined || text === null ? undefined : JSON.parse(text);
			  const find = (node, key) => {
			    if (!node || typeof node !== 'object') return null;
			    if (node.props && node.props.key === key) return node;
			    for (const child of node.children || []) { const hit = find(child, key); if (hit) return hit; }
			    return null;
			  };
			  globalThis.mod = Object.freeze({
			    emit: (event, e) => parse(host.emit(String(event), JSON.stringify(e === undefined ? null : e))),
			    render: id => parse(host.render(String(id), 'Pane')),
			    renderPage: id => parse(host.render(String(id), 'Page')),
			    renderBanner: id => parse(host.render(String(id), 'Banner')),
			    renderDialog: id => parse(host.render(String(id), 'Dialog')),
			    renderWidget: id => parse(host.render(String(id), 'Widget')),
			    press: (tree, key, value) => {
			      const node = find(tree, key);
			      if (!node) throw new Error('no element with key ' + key);
			      const handler = node.props.onPress || node.props.onChange || node.props.onSubmit;
			      if (!handler || !handler.$handler) throw new Error('the element ' + key + ' has no handler');
			      host.press(handler.$handler, JSON.stringify(value === undefined ? null : value));
			    }
			  });
			  globalThis.__voidstrapRunTests = async () => {
			    const results = [];
			    for (const item of tests) {
			      try { await item.body(); results.push({ name: item.name, ok: true }); }
			      catch (error) { results.push({ name: item.name, ok: false, error: String(error && error.message || error) }); }
			    }
			    return JSON.stringify(results);
			  };
			})();
			""";

        public IReadOnlyList<string> DescribeHooks() => _hookDescriptions;

        private JsonNode? EmitOwn(string eventName, JsonNode? input, string scope)
        {
            List<HookEntry> hooks = HooksFor(eventName).Where(hook => Matches(hook.Matcher, input)).ToList();
            return Run(hooks, 0, eventName, input, static e => e, scope);
        }

        public JsonArray RunTests(string modulePath)
        {
            lock (_gate)
            {
                JsObject host = new(_engine);
                host.Set(
                    "emit",
                    Host(
                        "emit",
                        a => EmitOwn(Arg(a, 0), JsonNode.Parse(Arg(a, 1)), "test")?.ToJsonString() is string json ? json : JsValue.Undefined
                    )
                );
                host.Set(
                    "render",
                    Host(
                        "render",
                        a =>
                        {
                            string id = Arg(a, 0);
                            string preferred = a.Length > 1 ? Arg(a, 1) : "Pane";
                            string[] candidates = preferred == "Pane" ? ["Pane", "Page", "Banner", "Dialog"] : [preferred];
                            foreach (string comp in candidates)
                            {
                                string scopePrefix = comp.ToLowerInvariant() + "." + id;
                                JsonNode? tree = EmitOwn(
                                    "ui.render",
                                    new JsonObject
                                    {
                                        ["component"] = comp,
                                        ["requestId"] = id,
                                        ["plugin"] = Name,
                                        ["surface"] = "desktop",
                                    },
                                    scopePrefix
                                );
                                if (tree is JsonObject rendered && rendered["type"] != null)
                                    return rendered.ToJsonString();
                            }
                            return JsValue.Undefined;
                        }
                    )
                );
                host.Set(
                    "press",
                    Host(
                        "press",
                        a =>
                        {
                            if (!_handlers.TryGetValue(Arg(a, 0), out JsValue? handler))
                                throw new InvalidOperationException("the handler is no longer drawn");
                            _engine.Constraints.Reset();
                            Settle(_engine.Invoke(handler, JsValue.Undefined, new object[] { ToJs(JsonNode.Parse(Arg(a, 1))) }));
                            return JsValue.Undefined;
                        }
                    )
                );
                _engine.SetValue("__voidstrapTest", host);
                _engine.Execute(TestPrelude);
                _engine.Modules.Import("./" + modulePath.Replace('\\', '/'));
                _engine.Constraints.Reset();
                JsValue results = Settle(_engine.Invoke("__voidstrapRunTests"));
                return JsonNode.Parse(results.AsString()) as JsonArray ?? new JsonArray();
            }
        }

        private static string Arg(JsValue[] arguments, int index) =>
            arguments.Length > index && !arguments[index].IsUndefined() && !arguments[index].IsNull() ? arguments[index].ToString() : "";

        public bool HasHook(string eventName)
        {
            lock (_gate)
            {
                for (int i = 0; i < _hooks.Count; i++)
                {
                    if (string.Equals(_hooks[i].Event, eventName, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
        }

        public IEnumerable<HookEntry> HooksFor(string eventName)
        {
            lock (_gate)
                return _hooks
                    .Where(hook => hook.Event == eventName)
                    .Select(hook => new HookEntry(this, hook.Event, hook.Matcher, hook.Function))
                    .ToList();
        }

        public JsonNode? Call(JsValue function, JsonNode? input, Func<JsonNode?, JsonNode?> next, string? scope, bool settlePromises = true)
        {
            lock (_gate)
            {
                if (_disposed)
                    return next(input);
                if (scope != null)
                {
                    string prefix = Name + "|" + scope + "|";
                    foreach (string key in _handlers.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                        _handlers.Remove(key);
                }
                ClrFunction nextFunction = new(
                    _engine,
                    "next",
                    (_, a) =>
                    {
                        JsonNode? rewritten = a.Length > 0 && !a[0].IsUndefined() ? ToNode(a[0], scope) : null;
                        JsonNode? result = next(rewritten);
                        _engine.Constraints.Reset();
                        return ToJs(result);
                    }
                );
                _engine.Constraints.Reset();
                JsValue raw = _engine.Invoke(
                    function,
                    JsValue.Undefined,
                    new object[] { _engine.GetValue("$"), ToJs(input), nextFunction }
                );
                if (!settlePromises)
                {
                    Watch(raw);
                    if (raw.IsPromise() && scope != null)
                        Problem(Name, "a ui.render hook cannot be async, draw from state and load data in a handler or session.start");
                    _engine.Advanced.ProcessTasks();
                    return raw.IsPromise() ? null : ToNode(raw, scope);
                }
                JsValue settled = Settle(raw);
                return ToNode(settled, scope);
            }
        }

        public void InvokeHandler(string handlerId, JsonNode? argument)
        {
            lock (_gate)
            {
                if (_disposed || !_handlers.TryGetValue(handlerId, out JsValue? handler))
                    return;
                try
                {
                    _engine.Constraints.Reset();
                    JsValue result = _engine.Invoke(handler, JsValue.Undefined, new object[] { ToJs(argument) });
                    Watch(result);
                    _engine.Advanced.ProcessTasks();
                }
                catch (Exception ex)
                {
                    Problem(Name, "handler failed: " + Describe(ex));
                }
            }
        }

        private JsValue ToJs(JsonNode? node) => node == null ? JsValue.Undefined : new JsonParser(_engine).Parse(node.ToJsonString());

        private JsonNode? ToNode(JsValue value, string? scope)
        {
            int count = 0;
            return Convert(value, scope, 0, ref count);
        }

        private JsonNode? Convert(JsValue value, string? scope, int depth, ref int count)
        {
            if (++count > MaxTreeNodes || depth > MaxTreeDepth)
                throw new InvalidOperationException("the returned value is too large");
            if (value.IsUndefined() || value.IsNull())
                return null;
            if (value.IsBoolean())
                return JsonValue.Create(value.AsBoolean());
            if (value.IsNumber())
            {
                double number = value.AsNumber();
                return double.IsFinite(number) ? JsonValue.Create(number) : null;
            }
            if (value.IsString())
                return JsonValue.Create(value.AsString());
            if (value is Function)
            {
                if (scope == null)
                    return null;
                string id = Name + "|" + scope + "|" + (++_handlerCounter);
                _handlers[id] = value;
                return new JsonObject { ["$handler"] = id };
            }
            if (value.IsArray())
            {
                JsonArray array = new();
                Jint.Native.JsArray source = value.AsArray();
                for (uint i = 0; i < source.Length; i++)
                    array.Add(Convert(source.Get(i), scope, depth + 1, ref count));
                return array;
            }
            if (value is ObjectInstance obj)
            {
                JsonObject result = new();
                foreach (JsValue key in obj.GetOwnPropertyKeys(Jint.Runtime.Types.String))
                {
                    JsonNode? item = Convert(obj.Get(key), scope, depth + 1, ref count);
                    if (item != null)
                        result[key.ToString()] = item;
                }
                return result;
            }
            return null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (Timer timer in _timers.Values)
                    timer.Dispose();
                _timers.Clear();
                _hooks.Clear();
                _hookDescriptions = Array.Empty<string>();
                _handlers.Clear();
                Monitor.PulseAll(_gate);
                _engine.Dispose();
            }
        }
    }
}
