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

	private static readonly List<LoadedPlugin> Loaded = [];

	private static readonly List<PaneInfo> OpenPanes = [];

	private static readonly Dictionary<string, string> Statuses = new(StringComparer.Ordinal);

	private static readonly Dictionary<string, JsonObject> SessionState = new(StringComparer.Ordinal);

	private static FileSystemWatcher? _watcher;

	private static Timer? _reloadTimer;

	private static bool _started;

	public static event Action? PanesChanged;

	public static event Action<string>? PaneInvalidated;

	public static event Action? StatusChanged;

	public sealed record PaneInfo(string Plugin, string Id, string Title);

	public static bool IsSupported => !Voidstrap.Utility.Platform.IsMacOS;

	public static IReadOnlyList<PaneInfo> Panes
	{
		get
		{
			lock (Gate)
				return OpenPanes.ToList();
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
			Statuses.Clear();
			_started = false;
		}
	}

	private static void OnPluginsChanged(object sender, FileSystemEventArgs e) => _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);

	private static void OnPluginsRenamed(object sender, RenamedEventArgs e) => _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);

	private static void OnReloadTimer(object? state)
	{
		App.Logger.WriteLine(LogIdent, "The mods folder changed, reloading Voidstrap mods");
		Reload();
	}

	public static void Reload()
	{
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
				App.Logger.WriteLine(LogIdent, manifest.Name + ": module did not load: " + Describe(ex));
			}
		}
		List<LoadedPlugin> old;
		lock (Gate)
		{
			old = Loaded.ToList();
			Loaded.Clear();
			Loaded.AddRange(fresh);
			HashSet<string> names = fresh.Select(plugin => plugin.Name).ToHashSet(StringComparer.Ordinal);
			OpenPanes.RemoveAll(pane => !names.Contains(pane.Plugin));
			foreach (string key in Statuses.Keys.Where(key => !names.Contains(key)).ToList())
				Statuses.Remove(key);
		}
		foreach (LoadedPlugin plugin in old)
			plugin.Dispose();
		Invoke("session.start", new JsonObject { ["source"] = old.Count > 0 ? "reload" : "startup" });
		PanesChanged?.Invoke();
		StatusChanged?.Invoke();
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
			App.Logger.WriteLine(LogIdent, hook.Plugin.Name + ": " + eventName + " hook skipped: " + Describe(ex));
			return nextCalled ? nextResult : Run(hooks, index + 1, eventName, input, bottom, scope);
		}
	}

	public static JsonNode? RenderPane(PaneInfo pane)
	{
		return Invoke("ui.render", new JsonObject
		{
			["component"] = "Pane",
			["requestId"] = pane.Id,
			["plugin"] = pane.Plugin,
			["surface"] = "desktop"
		}, static _ => null, "pane." + pane.Id);
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
			App.Logger.WriteLine(LogIdent, name + ": a UI handler failed: " + Describe(ex));
		}
		PaneInvalidated?.Invoke(name);
	}

	internal static JsonNode OpenPane(string plugin, string id, string title)
	{
		if (id.Length == 0 || id.Length > 64)
			throw new InvalidOperationException("a pane needs an id of 1 to 64 characters");
		lock (Gate)
		{
			OpenPanes.RemoveAll(pane => pane.Plugin == plugin && pane.Id == id);
			OpenPanes.Add(new PaneInfo(plugin, id, title.Length > 0 ? title : id));
		}
		PanesChanged?.Invoke();
		return new JsonObject { ["isPlaced"] = true };
	}

	internal static void ClosePane(string plugin, string id)
	{
		lock (Gate)
			OpenPanes.RemoveAll(pane => pane.Plugin == plugin && pane.Id == id);
		PanesChanged?.Invoke();
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
		StatusChanged?.Invoke();
	}

	internal static void Invalidate(string plugin) => PaneInvalidated?.Invoke(plugin);

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
			  const elements = Object.freeze({ Box: 'Box', Text: 'Text', Button: 'Button', Toggle: 'Toggle', TextInput: 'TextInput', Card: 'Card', Image: 'Image', Divider: 'Divider' });
			  globalThis.h = (type, props, ...children) => ({ type, props: props || {}, children: children.flat(Infinity).filter(child => child !== null && child !== undefined && child !== false) });
			  globalThis.Fragment = 'Box';
			  globalThis.$ = Object.freeze({
			    log: text => host.log(String(text)),
			    app: Object.freeze({ version: host.version, platform: host.platform }),
			    ui: Object.freeze({
			      resolve: () => elements,
			      open: request => parse(host.uiOpen(JSON.stringify(request || {}))),
			      close: id => host.uiClose(String(id)),
			      status: text => host.uiStatus(text === undefined || text === null ? '' : String(text)),
			      toast: (title, text) => host.toast(String(title), text === undefined ? '' : String(text)),
			      refresh: () => host.uiRefresh()
			    }),
			    settings: Object.freeze({
			      get: name => parse(host.settingGet(String(name))),
			      set: (name, value) => host.settingSet(String(name), JSON.stringify(value))
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
			      set: layout => host.sidebarSet(JSON.stringify(layout || {}))
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

		private readonly HashSet<string> _networkHosts;

		private int _handlerCounter;

		private int _timerCounter;

		private int _pending;

		private bool _disposed;

		public string Name { get; }

		private LoadedPlugin(PluginManifest manifest)
		{
			Name = manifest.Name;
			_store = new PluginStore(manifest.Name);
			_networkHosts = new HashSet<string>(manifest.NetworkHosts, StringComparer.OrdinalIgnoreCase);
			_engine = new Engine(options =>
			{
				options.Strict();
				options.TimeoutInterval(TimeSpan.FromSeconds(2));
				options.LimitMemory(64L * 1024 * 1024);
				options.LimitRecursion(256);
				options.EnableModules(manifest.Directory, restrictToBasePath: true);
			});
		}

		public static LoadedPlugin Load(PluginManifest manifest)
		{
			LoadedPlugin plugin = new(manifest);
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
				Voidstrap.Utility.AppNotifications.RecordInfo("mod." + Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("uiOpen", Host("uiOpen", a =>
			{
				JsonObject request = JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject();
				return OpenPane(Name, request["id"]?.ToString() ?? "", request["title"]?.ToString() ?? "").ToJsonString();
			}));
			host.Set("uiClose", Host("uiClose", a =>
			{
				ClosePane(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("uiStatus", Host("uiStatus", a =>
			{
				SetStatus(Name, Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("uiRefresh", Host("uiRefresh", _ =>
			{
				Invalidate(Name);
				return JsValue.Undefined;
			}));
			host.Set("settingGet", Host("settingGet", a => PluginSettings.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
			host.Set("settingSet", Host("settingSet", a =>
			{
				PluginSettings.Set(Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("storeGet", Host("storeGet", a => _store.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
			host.Set("storeSet", Host("storeSet", a =>
			{
				_store.Set(Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("clockStart", Host("clockStart", a =>
			{
				if (a.Length < 2 || a[1] is not Function callback)
					throw new InvalidOperationException("the clock needs a delay and a function");
				bool repeat = a.Length > 2 && a[2].IsBoolean() && a[2].AsBoolean();
				return StartTimer(a[0].IsNumber() ? a[0].AsNumber() : 0, callback, repeat);
			}));
			host.Set("clockCancel", Host("clockCancel", a =>
			{
				if (a.Length > 0 && a[0].IsNumber())
					CancelTimer((int)a[0].AsNumber());
				return JsValue.Undefined;
			}));
			host.Set("netFetch", Host("netFetch", a => Fetch(JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject())));
			host.Set("sidebarGet", Host("sidebarGet", _ => PluginSidebar.Get().ToJsonString()));
			host.Set("sidebarSet", Host("sidebarSet", a =>
			{
				PluginSidebar.Set(Name, JsonNode.Parse(Arg(a, 0)) as JsonObject ?? new JsonObject());
				return JsValue.Undefined;
			}));
			host.Set("stateGet", Host("stateGet", a => GetState(Name, Arg(a, 0)) is string json ? json : JsValue.Undefined));
			host.Set("stateSet", Host("stateSet", a =>
			{
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
					App.Logger.WriteLine(LogIdent, Name + ": clock hook skipped: " + Describe(ex));
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
							App.Logger.WriteLine(LogIdent, Name + ": a network callback failed: " + Describe(ex));
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
