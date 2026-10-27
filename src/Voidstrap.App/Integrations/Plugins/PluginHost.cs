using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jint.Native.Function;
using System.Text.Json.Nodes;
using System.Threading;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Runtime.Interop;

namespace Voidstrap.Integrations.Plugins;

public static class PluginHost
{
	private const string LogIdent = "Plugins";

	private const int ReloadDelayMs = 400;

	private static readonly object Gate = new();

	private static readonly List<LoadedPlugin> Loaded = [];

	private static FileSystemWatcher? _watcher;

	private static Timer? _reloadTimer;

	private static bool _started;

	public static IReadOnlyList<string> LoadedNames
	{
		get
		{
			lock (Gate)
				return Loaded.Select(plugin => plugin.Name).ToList();
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
			App.Logger.WriteLine(LogIdent, "The plugins folder could not be watched: " + ex.Message);
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
			_started = false;
		}
	}

	private static void OnPluginsChanged(object sender, FileSystemEventArgs e) => _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);

	private static void OnPluginsRenamed(object sender, RenamedEventArgs e) => _reloadTimer?.Change(ReloadDelayMs, Timeout.Infinite);

	private static void OnReloadTimer(object? state)
	{
		App.Logger.WriteLine(LogIdent, "The plugins folder changed, reloading plugins");
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
		}
		foreach (LoadedPlugin plugin in old)
			plugin.Dispose();
		Invoke("app.start", new JsonObject { ["reason"] = old.Count > 0 ? "reload" : "start" });
	}

	public static JsonNode? Invoke(string eventName, JsonNode? input, Func<JsonNode?, JsonNode?>? bottom = null)
	{
		List<HookEntry> hooks;
		lock (Gate)
			hooks = Loaded.SelectMany(plugin => plugin.HooksFor(eventName)).ToList();
		bottom ??= static e => e;
		if (hooks.Count == 0)
			return bottom(input);
		return Run(hooks, 0, eventName, input, bottom);
	}

	private static JsonNode? Run(List<HookEntry> hooks, int index, string eventName, JsonNode? input, Func<JsonNode?, JsonNode?> bottom)
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
				nextResult = Run(hooks, index + 1, eventName, next ?? input?.DeepClone(), bottom);
				return nextResult;
			});
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine(LogIdent, hook.Plugin.Name + ": " + eventName + " hook skipped: " + Describe(ex));
			return nextCalled ? nextResult : Run(hooks, index + 1, eventName, input, bottom);
		}
	}

	internal static string Describe(Exception ex)
	{
		string message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
		return message.Length > 300 ? message[..300] : message;
	}

	internal sealed record HookEntry(LoadedPlugin Plugin, string Event, JsValue Function);

	internal sealed class LoadedPlugin : IDisposable
	{
		private const string Prelude = """
			(() => {
			  const host = globalThis.__voidstrapHost;
			  delete globalThis.__voidstrapHost;
			  const parse = text => text === undefined || text === null ? undefined : JSON.parse(text);
			  globalThis.$ = Object.freeze({
			    log: text => host.log(String(text)),
			    toast: (title, text) => host.toast(String(title), text === undefined ? '' : String(text)),
			    app: Object.freeze({ version: host.version, platform: host.platform }),
			    settings: Object.freeze({
			      get: name => parse(host.settingGet(String(name))),
			      set: (name, value) => host.settingSet(String(name), JSON.stringify(value))
			    }),
			    store: Object.freeze({
			      get: key => parse(host.storeGet(String(key))),
			      set: (key, value) => host.storeSet(String(key), JSON.stringify(value === undefined ? null : value))
			    })
			  });
			})();
			""";

		private readonly object _gate = new();

		private readonly Engine _engine;

		private readonly Dictionary<string, List<JsValue>> _hooks = new(StringComparer.Ordinal);

		private readonly PluginStore _store;

		public string Name { get; }

		private LoadedPlugin(PluginManifest manifest)
		{
			Name = manifest.Name;
			_store = new PluginStore(manifest.Name);
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
					if (eventName.Length == 0 || hook is not Function)
						throw new InvalidOperationException("on needs an event name and a hook function");
					if (!_hooks.TryGetValue(eventName, out List<JsValue>? list))
						_hooks[eventName] = list = [];
					list.Add(hook);
					return JsValue.Undefined;
				});
				_engine.Constraints.Reset();
				_engine.Invoke(register, on, ToJs(manifest.Options)).UnwrapIfPromise();
			}
		}

		private JsObject BuildHost()
		{
			JsObject host = new(_engine);
			host.Set("version", App.Version);
			host.Set("platform", Voidstrap.Utility.Platform.IsWindows ? "windows" : Voidstrap.Utility.Platform.IsMacOS ? "macos" : "linux");
			host.Set("log", new ClrFunction(_engine, "log", (_, a) =>
			{
				App.Logger.WriteLine(LogIdent, Name + ": " + Arg(a, 0));
				return JsValue.Undefined;
			}));
			host.Set("toast", new ClrFunction(_engine, "toast", (_, a) =>
			{
				Voidstrap.Utility.AppNotifications.RecordInfo("plugin." + Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("settingGet", new ClrFunction(_engine, "settingGet", (_, a) => PluginSettings.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
			host.Set("settingSet", new ClrFunction(_engine, "settingSet", (_, a) =>
			{
				PluginSettings.Set(Name, Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			host.Set("storeGet", new ClrFunction(_engine, "storeGet", (_, a) => _store.Get(Arg(a, 0)) is string json ? json : JsValue.Undefined));
			host.Set("storeSet", new ClrFunction(_engine, "storeSet", (_, a) =>
			{
				_store.Set(Arg(a, 0), Arg(a, 1));
				return JsValue.Undefined;
			}));
			return host;
		}

		private static string Arg(JsValue[] arguments, int index) => arguments.Length > index && !arguments[index].IsUndefined() && !arguments[index].IsNull() ? arguments[index].ToString() : "";

		public IEnumerable<HookEntry> HooksFor(string eventName)
		{
			lock (_gate)
			{
				if (!_hooks.TryGetValue(eventName, out List<JsValue>? list))
					return [];
				return list.Select(function => new HookEntry(this, eventName, function)).ToList();
			}
		}

		public JsonNode? Call(JsValue function, JsonNode? input, Func<JsonNode?, JsonNode?> next)
		{
			lock (_gate)
			{
				ClrFunction nextFunction = new(_engine, "next", (_, a) =>
				{
					JsonNode? rewritten = a.Length > 0 && !a[0].IsUndefined() ? ToNode(a[0]) : null;
					JsonNode? result = next(rewritten);
					_engine.Constraints.Reset();
					return ToJs(result);
				});
				_engine.Constraints.Reset();
				JsValue result = _engine.Invoke(function, JsValue.Undefined, new object[] { _engine.GetValue("$"), ToJs(input), nextFunction }).UnwrapIfPromise();
				return ToNode(result);
			}
		}

		private JsValue ToJs(JsonNode? node)
		{
			if (node == null)
				return JsValue.Undefined;
			JsValue value = new JsonParser(_engine).Parse(node.ToJsonString());
			return value;
		}

		private JsonNode? ToNode(JsValue value)
		{
			if (value.IsUndefined() || value.IsNull())
				return null;
			JsValue text = new Jint.Native.Json.JsonSerializer(_engine).Serialize(value);
			return text.IsUndefined() ? null : JsonNode.Parse(text.ToString());
		}

		public void Dispose()
		{
			lock (_gate)
			{
				_hooks.Clear();
				_engine.Dispose();
			}
		}
	}
}
