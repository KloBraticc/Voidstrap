using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Net.Http;
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

	private const int ReloadDelayMs = 400;

	private static readonly object Gate = new();

	private static readonly object ReloadGate = new();

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

	public sealed record ModInfo(string Name, string Version, string Description, string Directory, string Consent, bool Loaded, IReadOnlyList<string> Hooks, IReadOnlyList<string> Problems);

	public static void Problem(string mod, string text)
	{
		App.Logger.WriteLine(LogIdent, mod + ": " + text);
		lock (Gate)
		{
			if (!Problems.TryGetValue(mod, out List<string>? list))
				Problems[mod] = list = [];
			list.Add(DateTime.Now.ToString("HH:mm:ss") + " " + text);
			if (list.Count > MaxProblems)
				list.RemoveAt(0);
		}
		ModsChanged?.Invoke();
	}

	public static void ClearProblems(string mod)
	{
		lock (Gate)
			Problems.Remove(mod);
		ModsChanged?.Invoke();
	}

	public static IReadOnlyList<ModInfo> ListMods()
	{
		List<PluginManifest> manifests = PluginManifest.Discover(Paths.Plugins, report: false);
		Dictionary<string, bool> consent = App.Settings.Prop.PluginConsent ?? [];
		List<ModInfo> result = [];
		lock (Gate)
		{
			foreach (PluginManifest manifest in manifests)
			{
				LoadedPlugin? loaded = Loaded.FirstOrDefault(plugin => plugin.Name == manifest.Name);
				string state = consent.TryGetValue(manifest.Name, out bool allowed) ? (allowed ? "on" : "off") : "not asked yet";
				result.Add(new ModInfo(manifest.Name, manifest.Version, manifest.Description, manifest.Directory, state, loaded != null, loaded?.DescribeHooks() ?? [], Problems.TryGetValue(manifest.Name, out List<string>? list) ? list.ToList() : []));
			}
		}
		return result;
	}

	public static void SetEnabled(string name, bool enabled, bool reload = true)
	{
		Dictionary<string, bool> consent = App.Settings.Prop.PluginConsent ??= [];
		lock (Gate)
			consent[name] = enabled;
		App.Settings.SaveDeferred();
		App.Logger.WriteLine(LogIdent, name + ": turned " + (enabled ? "on" : "off") + " by the user");
		if (reload)
			Task.Run(Reload);
	}

	public static void ForgetConsent(string name)
	{
		lock (Gate)
		{
			App.Settings.Prop.PluginConsent?.Remove(name);
			App.Settings.Prop.PluginConsent?.Remove(PluginConsent.PermissionKey(name, PluginManifest.RobloxAccountPermission));
		}
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

	public static bool IsSupported => !Voidstrap.Utility.Platform.IsMacOS;

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
		if (!IsSupported)
			return;
		lock (Gate)
		{
			if (_started)
				return;
			_started = true;
		}
		try
		{
			Directory.CreateDirectory(Paths.Plugins);
			_reloadTimer = new Timer(OnReloadTimer, null, Timeout.Infinite, Timeout.Infinite);
			_watcher = new FileSystemWatcher(Paths.Plugins)
			{
				IncludeSubdirectories = true,
				NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
			};
			_watcher.Changed += OnPluginsChanged;
			_watcher.Created += OnPluginsChanged;
			_watcher.Deleted += OnPluginsChanged;
			_watcher.Renamed += OnPluginsRenamed;
			_watcher.EnableRaisingEvents = true;
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, "The mods folder could not be watched: " + ex.Message);
		}
		Reload();
	}

	public static void Shutdown()
	{
		if (_watcher != null)
		{
			_watcher.EnableRaisingEvents = false;
			_watcher.Changed -= OnPluginsChanged;
			_watcher.Created -= OnPluginsChanged;
			_watcher.Deleted -= OnPluginsChanged;
			_watcher.Renamed -= OnPluginsRenamed;
			_watcher.Dispose();
			_watcher = null;
		}
		_reloadTimer?.Dispose();
		_reloadTimer = null;
		lock (Gate)
		{
			foreach (LoadedPlugin plugin in Loaded)
				plugin.Dispose();
			Loaded.Clear();
			OpenPanes.Clear();
			OpenBanners.Clear();
			Statuses.Clear();
			_started = false;
		}
	}

	private static bool IsGenerated(string path) => path.Contains(Path.DirectorySeparatorChar + PluginManifest.ManifestFolder + Path.DirectorySeparatorChar + "types", StringComparison.OrdinalIgnoreCase);

	private static void OnPluginsChanged(object sender, FileSystemEventArgs e)
	{
		if (!IsGenerated(e.FullPath))
			_reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);
	}

	private static void OnPluginsRenamed(object sender, RenamedEventArgs e) => _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);

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
		lock (ReloadGate)
		{
			try
			{
				_reloadTimer?.Change(Timeout.Infinite, Timeout.Infinite);
				lock (Gate)
				{
					OpenPanes.Clear();
					OpenBanners.Clear();
					Statuses.Clear();
				}
				PluginSidebar.ClearModPages();
				PluginAnchors.ClearAll();
				List<PluginManifest> manifests = PluginManifest.Discover(Paths.Plugins);
				List<LoadedPlugin> fresh = [];
				foreach (PluginManifest manifest in manifests)
				{
					if (!PluginConsent.IsAllowed(manifest))
						continue;
					try
					{
						fresh.Add(LoadedPlugin.Load(manifest));
						App.Logger.WriteLine(LogIdent, manifest.Name + ": loaded " + manifest.Version);
					}
					catch (Exception ex)
					{
						Problem(manifest.Name, "the module did not load: " + Describe(ex));
					}
				}
				List<LoadedPlugin> old;
				HashSet<string> names;
				lock (Gate)
				{
					old = Loaded.ToList();
					Loaded.Clear();
					Loaded.AddRange(fresh);
					names = fresh.Select(plugin => plugin.Name).ToHashSet(StringComparer.Ordinal);
				}
				PluginDialogManager.ClosePluginDialogs(names);
				foreach (LoadedPlugin plugin in old)
					plugin.Dispose();
				Invoke("session.start", new JsonObject { ["source"] = old.Count > 0 ? "reload" : "startup" });
				PluginSidebar.ApplyToWindows();
				PluginAnchors.ApplyAll();
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
	}

	public static JsonNode? Invoke(string eventName, JsonNode? input, Func<JsonNode?, JsonNode?>? bottom = null, string? scope = null)
	{
		List<HookEntry> hooks;
		lock (Gate)
			hooks = Loaded.SelectMany(plugin => plugin.HooksFor(eventName)).Where(hook => Matches(hook.Matcher, input)).ToList();
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

	private static JsonNode? Run(List<HookEntry> hooks, int index, string eventName, JsonNode? input, Func<JsonNode?, JsonNode?> bottom, string? scope)
	{
		if (index >= hooks.Count)
			return bottom(input);
		HookEntry hook = hooks[index];
		bool nextCalled = false;
		JsonNode? nextResult = null;
		try
		{
			return hook.Plugin.Call(hook.Function, input, next =>
			{
				nextCalled = true;
				nextResult = Run(hooks, index + 1, eventName, next ?? input?.DeepClone(), bottom, scope);
				return nextResult;
			}, scope);
		}
		catch (Exception ex)
		{
			Problem(hook.Plugin.Name, eventName + " hook skipped: " + Describe(ex));
			return nextCalled ? nextResult : Run(hooks, index + 1, eventName, input, bottom, scope);
		}
	}

	public static JsonNode? RenderPane(PaneInfo pane)
	{
		_renderDepth++;
		try
		{
			return Invoke("ui.render", new JsonObject
			{
				["component"] = "Pane",
				["requestId"] = pane.Id,
				["plugin"] = pane.Plugin,
				["surface"] = "desktop"
			}, static _ => null, "pane." + pane.Id);
		}
		finally
		{
			_renderDepth--;
		}
	}

	public static JsonNode? RenderCustomPage(string plugin, string id)
	{
		_renderDepth++;
		try
		{
			return Invoke("ui.render", new JsonObject
			{
				["component"] = "Page",
				["requestId"] = id,
				["plugin"] = plugin,
				["surface"] = "desktop"
			}, static _ => null, "page." + id);
		}
		finally
		{
			_renderDepth--;
		}
	}

	public static JsonNode? RenderWidget(string plugin, string id)
	{
		_renderDepth++;
		try
		{
			return Invoke("ui.render", new JsonObject
			{
				["component"] = "Widget",
				["requestId"] = id,
				["plugin"] = plugin,
				["surface"] = "desktop"
			}, static _ => null, "widget." + id);
		}
		finally
		{
			_renderDepth--;
		}
	}

	public static JsonNode? RenderBanner(BannerInfo banner)
	{
		_renderDepth++;
		try
		{
			return Invoke("ui.render", new JsonObject
			{
				["component"] = "Banner",
				["requestId"] = banner.Id,
				["plugin"] = banner.Plugin,
				["surface"] = "desktop"
			}, static _ => null, "banner." + banner.Id);
		}
		finally
		{
			_renderDepth--;
		}
	}

	public static JsonNode? RenderDialog(string plugin, string id)
	{
		_renderDepth++;
		try
		{
			return Invoke("ui.render", new JsonObject
			{
				["component"] = "Dialog",
				["requestId"] = id,
				["plugin"] = plugin,
				["surface"] = "desktop"
			}, static _ => null, "dialog." + id);
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

		private const int MaxTreeNodes = 5000;

		private const string Prelude = """
			(() => {
			  const host = globalThis.__voidstrapHost;
			  delete globalThis.__voidstrapHost;
			  const parse = text => text === undefined || text === null ? undefined : JSON.parse(text);
			  const elements = Object.freeze({
			    Box: 'Box', Stack: 'Box', Wrap: 'Wrap', Grid: 'Grid', Scroll: 'Scroll',
			    Text: 'Text', Badge: 'Badge', Icon: 'Icon', ColorSwatch: 'ColorSwatch',
			    Button: 'Button', Toggle: 'Toggle', CheckBox: 'CheckBox', TextInput: 'TextInput',
			    NumberInput: 'NumberInput', Slider: 'Slider', Select: 'Select',
			    ProgressBar: 'ProgressBar', InfoBar: 'InfoBar', Divider: 'Divider', Spacer: 'Spacer',
			    Image: 'Image', Card: 'Card', Expander: 'Expander', Tabs: 'Tabs', Table: 'Table'
			  });
			  globalThis.h = (type, props, ...children) => ({ type, props: props || {}, children: children.flat(Infinity).filter(child => child !== null && child !== undefined && child !== false) });
			  globalThis.Fragment = 'Box';
			  globalThis.$ = Object.freeze({
			    log: text => host.log(String(text)),
			    app: Object.freeze({ version: host.version, platform: host.platform }),
			    ui: Object.freeze({
			      resolve: () => elements,
			      open: request => parse(host.uiOpen(JSON.stringify(request || {}))),
			      close: id => host.uiClose(String(id)),
			      openBanner: request => parse(host.uiOpenBanner(JSON.stringify(typeof request === 'string' ? { id: request } : (request || {})))),
			      closeBanner: id => host.uiCloseBanner(String(id)),
			      attach: request => parse(host.uiAttach(JSON.stringify(request || {}))),
			      detach: id => host.uiDetach(String(id)),
			      anchors: () => parse(host.uiAnchors()),
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
			      set: (key, value) => host.storeSet(String(key), JSON.stringify(value === undefined ? null : value))
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

		private readonly Dictionary<string, JsValue> _handlers = new(StringComparer.Ordinal);

		private readonly PluginStore _store;

		private const int MaxTimers = 32;

		private const int MinRepeatMs = 500;

		private const int MaxResponseBytes = 1024 * 1024;

		private static readonly TimeSpan AwaitLimit = TimeSpan.FromSeconds(10);

		private static readonly HttpClient NetClient = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromSeconds(10) };

		private readonly Dictionary<int, Timer> _timers = [];

		private readonly Dictionary<string, JsonNode?> _sandboxState = new(StringComparer.Ordinal);

		private readonly HashSet<string> _networkHosts;

		private readonly bool _sandbox;

		private int _handlerCounter;

		private int _timerCounter;

		private int _pending;

		private bool _disposed;

		public string Name { get; }

		private LoadedPlugin(PluginManifest manifest, bool sandbox = false)
		{
			Name = manifest.Name;
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
			plugin.Initialize(manifest);
			return plugin;
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
				ClrFunction on = new(_engine, "on", (_, arguments) =>
				{
					string eventName = arguments.Length > 0 ? arguments[0].ToString() : "";
					JsValue hook = arguments.Length > 2 ? arguments[2] : arguments.Length > 1 ? arguments[1] : JsValue.Undefined;
					JsonObject? matcher = arguments.Length > 2 ? ToNode(arguments[1], null) as JsonObject : null;
					if (eventName.Length == 0 || hook is not Function)
						throw new InvalidOperationException("on needs an event name and a hook function");
					_hooks.Add((eventName, matcher, hook));
					return JsValue.Undefined;
				});
				_engine.Constraints.Reset();
				Settle(_engine.Invoke(register, on, ToJs(manifest.Options)));
			}
		}

		private ClrFunction Host(string name, Func<JsValue[], JsValue> body) => new(_engine, name, (_, arguments) => body(arguments));

		private JsObject BuildHost()
		{
			JsObject host = new(_engine);
			host.Set("version", App.Version);
			host.Set("platform", Voidstrap.Utility.Platform.IsWindows ? "windows" : "linux");
			host.Set("log", Host("log", a =>
			{
				App.Logger.WriteLine(LogIdent, Name + ": " + Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("toast", Host("toast", a =>
			{
				if (!_sandbox)
					Voidstrap.Utility.AppNotifications.RecordInfo("mod." + Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("uiOpen", Host("uiOpen", a =>
			{
				JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
				if (_sandbox)
					return new JsonObject { ["isPlaced"] = true }.ToJsonString();
				return OpenPane(Name, request["id"]?.ToString() ?? "", request["title"]?.ToString() ?? "").ToJsonString();
			}));
			host.Set("uiClose", Host("uiClose", a =>
			{
				if (!_sandbox)
					ClosePane(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("uiOpenBanner", Host("uiOpenBanner", a =>
			{
				JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
				string pos = string.Equals(request["position"]?.ToString(), "bottom", StringComparison.OrdinalIgnoreCase) ? "bottom" : "top";
				if (_sandbox)
					return new JsonObject { ["isPlaced"] = true, ["position"] = pos }.ToJsonString();
				return OpenBanner(Name, request["id"]?.ToString() ?? "banner", pos).ToJsonString();
			}));
			host.Set("uiAttach", Host("uiAttach", a =>
			{
				JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
				if (_sandbox)
					return new JsonObject { ["isPlaced"] = true, ["reason"] = "" }.ToJsonString();
				return PluginAnchors.Attach(Name, request).ToJsonString();
			}));
			host.Set("uiDetach", Host("uiDetach", a =>
			{
				if (!_sandbox)
					PluginAnchors.Detach(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("uiAnchors", Host("uiAnchors", _ => _sandbox ? "[]" : PluginNavigation.InvokeUiBounded(() => PluginAnchors.Map().ToJsonString(), "[]", 1500)));
			host.Set("uiCloseBanner", Host("uiCloseBanner", a =>
			{
				if (!_sandbox)
					CloseBanner(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("uiOpenDialog", Host("uiOpenDialog", a =>
			{
				JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
				if (!_sandbox)
					PluginDialogManager.OpenDialog(Name, request);
				return JsValue.Undefined;
			}));
			host.Set("uiCloseDialog", Host("uiCloseDialog", a =>
			{
				if (!_sandbox)
					PluginDialogManager.CloseDialog(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("uiMessage", Host("uiMessage", a =>
			{
				if (!_sandbox)
					PluginDialogManager.ShowMessage(Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("uiConfirm", Host("uiConfirm", a => _sandbox ? true : PluginDialogManager.ShowConfirm(Name, Arg(a, 0), Arg(a, 1))));
			host.Set("uiStatus", Host("uiStatus", a =>
			{
				if (!_sandbox)
					SetStatus(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("uiRefresh", Host("uiRefresh", _ =>
			{
				if (!_sandbox)
					Invalidate(Name);
				return JsValue.Undefined;
			}));
			host.Set("settingList", Host("settingList", _ => PluginSettings.List()));
			host.Set("settingGet", Host("settingGet", a => PluginSettings.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
			host.Set("settingSet", Host("settingSet", a =>
			{
				if (!_sandbox)
					PluginSettings.Set(Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("flagGetAll", Host("flagGetAll", _ => PluginFastFlags.GetAll()));
			host.Set("flagGet", Host("flagGet", a => PluginFastFlags.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
			host.Set("flagSet", Host("flagSet", a =>
			{
				if (!_sandbox)
					PluginFastFlags.Set(Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("flagRemove", Host("flagRemove", a =>
			{
				if (!_sandbox)
					PluginFastFlags.Remove(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("flagClear", Host("flagClear", _ =>
			{
				if (!_sandbox)
					PluginFastFlags.Clear(Name);
				return JsValue.Undefined;
			}));
			host.Set("storeGet", Host("storeGet", a => _store.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
			host.Set("storeSet", Host("storeSet", a =>
			{
				if (!_sandbox)
					_store.Set(Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("clockStart", Host("clockStart", a =>
			{
				if (a.Length < 2 || a[1] is not Function callback)
					throw new InvalidOperationException("the clock needs a delay and a function");
				bool repeat = a.Length > 2 && a[2].IsBoolean() && a[2].AsBoolean();
				return _sandbox ? 1 : StartTimer(a[0].IsNumber() ? a[0].AsNumber() : 0, callback, repeat);
			}));
			host.Set("clockCancel", Host("clockCancel", a =>
			{
				if (!_sandbox && a.Length > 0 && a[0].IsNumber())
					CancelTimer((int)a[0].AsNumber());
				return JsValue.Undefined;
			}));
			host.Set("netFetch", Host("netFetch", a => Fetch(JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject())));
			host.Set("sidebarGet", Host("sidebarGet", _ => PluginSidebar.Get().ToJsonString()));
			host.Set("sidebarSet", Host("sidebarSet", a =>
			{
				if (!_sandbox)
					PluginSidebar.Set(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject());
				return JsValue.Undefined;
			}));
			host.Set("sidebarReset", Host("sidebarReset", _ =>
			{
				if (!_sandbox)
					PluginSidebar.Reset(Name);
				return JsValue.Undefined;
			}));
			host.Set("sidebarAddPage", Host("sidebarAddPage", a =>
			{
				JsonObject spec = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
				if (_sandbox)
				{
					string id = (spec["id"]?.ToString() ?? "main").Trim();
					string title = spec["title"]?.ToString() ?? id;
					string section = spec["section"]?.ToString() ?? "Core";
					string icon = spec["icon"]?.ToString() ?? "Apps24";
					return new JsonObject { ["key"] = PluginSidebar.MakePageKey(Name, id), ["id"] = id, ["title"] = title, ["section"] = section, ["icon"] = icon }.ToJsonString();
				}
				return PluginSidebar.AddPage(Name, spec).ToJsonString();
			}));
			host.Set("sidebarRemovePage", Host("sidebarRemovePage", a =>
			{
				if (!_sandbox)
					PluginSidebar.RemovePage(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("navCurrent", Host("navCurrent", _ => PluginNavigation.Current()));
			host.Set("navPages", Host("navPages", _ => PluginNavigation.Pages()));
			host.Set("navOpen", Host("navOpen", a =>
			{
				if (!_sandbox)
					PluginNavigation.Open(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("themeGet", Host("themeGet", _ => PluginTheme.Get()));
			host.Set("themeSet", Host("themeSet", a =>
			{
				if (!_sandbox)
					PluginTheme.Set(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject());
				return JsValue.Undefined;
			}));
			host.Set("themeGetBrush", Host("themeGetBrush", a => PluginTheme.GetBrush(Arg(a, 0)) is string hex ? hex : JsValue.Undefined));
			host.Set("themeSetBrush", Host("themeSetBrush", a =>
			{
				if (!_sandbox)
					PluginTheme.SetBrush(Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("themeResetBrushes", Host("themeResetBrushes", _ =>
			{
				if (!_sandbox)
					PluginTheme.ResetBrushes(Name);
				return JsValue.Undefined;
			}));
			host.Set("themeListBrushes", Host("themeListBrushes", _ => PluginTheme.ListBrushes()));
			host.Set("windowGet", Host("windowGet", _ => PluginWindow.Get()));
			host.Set("windowSet", Host("windowSet", a =>
			{
				if (!_sandbox)
					PluginWindow.Set(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject());
				return JsValue.Undefined;
			}));
			host.Set("robloxStatus", Host("robloxStatus", _ => PluginRoblox.Status()));
			host.Set("robloxRequest", Host("robloxRequest", a =>
			{
				if (_sandbox)
					return RunAsync(() => Task.FromResult(new JsonObject { ["status"] = 200, ["ok"] = true, ["text"] = "{}" }));
				PluginRobloxAccount.EnsureAllowed(Name);
				(Uri uri, string method, string? body) = PluginRobloxAccount.Validate(JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject());
				return RunAsync(() => PluginRobloxAccount.SendAsync(uri, method, body, CancellationToken.None));
			}));
			host.Set("robloxAccount", Host("robloxAccount", _ =>
			{
				if (_sandbox)
					return RunAsync(() => Task.FromResult(new JsonObject { ["signedIn"] = true, ["userId"] = 1, ["username"] = "TestUser", ["displayName"] = "Test User" }));
				PluginRobloxAccount.EnsureAllowed(Name);
				return RunAsync(() => PluginRobloxAccount.AccountAsync(CancellationToken.None));
			}));
			host.Set("robloxLaunch", Host("robloxLaunch", a =>
			{
				if (!_sandbox)
					PluginRoblox.Launch(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject());
				return JsValue.Undefined;
			}));
			host.Set("clipboardGet", Host("clipboardGet", _ => PluginClipboard.Get()));
			host.Set("clipboardSet", Host("clipboardSet", a =>
			{
				if (!_sandbox)
					PluginClipboard.Set(Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("stateGet", Host("stateGet", a =>
			{
				if (_sandbox)
					return _sandboxState.TryGetValue(Arg(a, 0), out JsonNode? val) && val != null ? val.ToJsonString() : JsValue.Undefined;
				return GetState(Name, Arg(a, 0)) is string json ? json : JsValue.Undefined;
			}));
			host.Set("stateSet", Host("stateSet", a =>
			{
				if (_sandbox)
					_sandboxState[Arg(a, 0)] = JsonNode.Parse(Arg(a, 1));
				else
					SetState(Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
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
			lock (_gate)
			{
				if (_disposed || !_timers.ContainsKey(tick.Item1))
					return;
				if (!tick.Item3)
					CancelTimer(tick.Item1);
				try
				{
					_engine.Constraints.Reset();
					Settle(_engine.Invoke(tick.Item2, JsValue.Undefined, Array.Empty<object>()));
				}
				catch (Exception ex)
				{
					Problem(Name, "clock hook skipped: " + Describe(ex));
				}
			}
			Invalidate(Name);
		}

		private JsValue Fetch(JsonObject request)
		{
			string url = request["url"]?.ToString() ?? "";
			if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps || !_networkHosts.Contains(uri.IdnHost))
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
					using HttpRequestMessage message = new(new HttpMethod(method), uri);
					if (body != null)
						message.Content = new StringContent(body, System.Text.Encoding.UTF8, request["contentType"]?.ToString() ?? "application/json");
					if (headers != null)
						foreach (KeyValuePair<string, JsonNode?> header in headers)
							if (!header.Key.Equals("cookie", StringComparison.OrdinalIgnoreCase) && !header.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase))
								message.Headers.TryAddWithoutValidation(header.Key, header.Value?.ToString());
					using HttpResponseMessage response = await NetClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
					string text = await Voidstrap.Utility.Http.ReadStringBoundedAsync(response.Content, MaxResponseBytes, CancellationToken.None).ConfigureAwait(false);
					result = new JsonObject { ["status"] = (int)response.StatusCode, ["ok"] = response.IsSuccessStatusCode, ["text"] = text };
				}
				catch (Exception ex)
				{
					error = Describe(ex);
				}
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
					Monitor.PulseAll(_gate);
				}
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
					Monitor.PulseAll(_gate);
				}
				Invalidate(Name);
			});
			return promise;
		}

		private JsValue Settle(JsValue value)
		{
			if (!value.IsPromise())
				return value;
			DateTime deadline = DateTime.UtcNow + AwaitLimit;
			while (_pending > 0 && DateTime.UtcNow < deadline)
				Monitor.Wait(_gate, 50);
			_engine.Constraints.Reset();
			return value.UnwrapIfPromise(TimeSpan.FromMilliseconds(100));
		}

		private const string TestPrelude = """
			(() => {
			  const host = globalThis.__voidstrapTest;
			  delete globalThis.__voidstrapTest;
			  const tests = [];
			  const show = value => JSON.stringify(value);
			  globalThis.test = (name, body) => tests.push({ name: String(name), body });
			  globalThis.expect = actual => Object.freeze({
			    toBe: expected => { if (actual !== expected) throw new Error('expected ' + show(expected) + ' but got ' + show(actual)); },
			    toEqual: expected => { if (show(actual) !== show(expected)) throw new Error('expected ' + show(expected) + ' but got ' + show(actual)); },
			    toBeTruthy: () => { if (!actual) throw new Error('expected a truthy value but got ' + show(actual)); },
			    toContain: expected => { if (!(actual && actual.includes && actual.includes(expected))) throw new Error(show(actual) + ' does not contain ' + show(expected)); }
			  });
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

		public IReadOnlyList<string> DescribeHooks()
		{
			lock (_gate)
				return _hooks.Select(hook => hook.Event + (hook.Matcher != null ? " " + hook.Matcher.ToJsonString() : "")).ToList();
		}

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
				host.Set("emit", Host("emit", a => EmitOwn(Arg(a, 0), JsonNode.Parse(Arg(a, 1)), "test")?.ToJsonString() is string json ? json : JsValue.Undefined));
				host.Set("render", Host("render", a =>
				{
					string id = Arg(a, 0);
					string preferred = a.Length > 1 ? Arg(a, 1) : "Pane";
					string[] candidates = preferred == "Pane" ? ["Pane", "Page", "Banner", "Dialog"] : [preferred];
					foreach (string comp in candidates)
					{
						string scopePrefix = comp.ToLowerInvariant() + "." + id;
						JsonNode? tree = EmitOwn("ui.render", new JsonObject { ["component"] = comp, ["requestId"] = id, ["plugin"] = Name, ["surface"] = "desktop" }, scopePrefix);
						if (tree is JsonObject rendered && rendered["type"] != null)
							return rendered.ToJsonString();
					}
					return JsValue.Undefined;
				}));
				host.Set("press", Host("press", a =>
				{
					if (!_handlers.TryGetValue(Arg(a, 0), out JsValue? handler))
						throw new InvalidOperationException("the handler is no longer drawn");
					_engine.Constraints.Reset();
					Settle(_engine.Invoke(handler, JsValue.Undefined, new object[] { ToJs(JsonNode.Parse(Arg(a, 1))) }));
					return JsValue.Undefined;
				}));
				_engine.SetValue("__voidstrapTest", host);
				_engine.Execute(TestPrelude);
				_engine.Modules.Import("./" + modulePath.Replace('\\', '/'));
				_engine.Constraints.Reset();
				JsValue results = Settle(_engine.Invoke("__voidstrapRunTests"));
				return JsonNode.Parse(results.AsString()) as JsonArray ?? new JsonArray();
			}
		}

		private static string Arg(JsValue[] arguments, int index) => arguments.Length > index && !arguments[index].IsUndefined() && !arguments[index].IsNull() ? arguments[index].ToString() : "";

		public IEnumerable<HookEntry> HooksFor(string eventName)
		{
			lock (_gate)
				return _hooks.Where(hook => hook.Event == eventName).Select(hook => new HookEntry(this, hook.Event, hook.Matcher, hook.Function)).ToList();
		}

		public JsonNode? Call(JsValue function, JsonNode? input, Func<JsonNode?, JsonNode?> next, string? scope)
		{
			lock (_gate)
			{
				if (scope != null)
				{
					string prefix = Name + "|" + scope + "|";
					foreach (string key in _handlers.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList())
						_handlers.Remove(key);
				}
				ClrFunction nextFunction = new(_engine, "next", (_, a) =>
				{
					JsonNode? rewritten = a.Length > 0 && !a[0].IsUndefined() ? ToNode(a[0], scope) : null;
					JsonNode? result = next(rewritten);
					_engine.Constraints.Reset();
					return ToJs(result);
				});
				_engine.Constraints.Reset();
				JsValue result = Settle(_engine.Invoke(function, JsValue.Undefined, new object[] { _engine.GetValue("$"), ToJs(input), nextFunction }));
				return ToNode(result, scope);
			}
		}

		public void InvokeHandler(string handlerId, JsonNode? argument)
		{
			lock (_gate)
			{
				if (!_handlers.TryGetValue(handlerId, out JsValue? handler))
					return;
				_engine.Constraints.Reset();
				Settle(_engine.Invoke(handler, JsValue.Undefined, new object[] { ToJs(argument) }));
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
				_handlers.Clear();
				Monitor.PulseAll(_gate);
				_engine.Dispose();
			}
		}
	}
}
