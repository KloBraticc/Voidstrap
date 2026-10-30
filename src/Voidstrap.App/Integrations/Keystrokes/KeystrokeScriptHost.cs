using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using Jint;
using Jint.Native;

namespace Voidstrap.Integrations.Keystrokes;

internal sealed class KeystrokeScriptHost : IDisposable
{
    private const string Prelude = """
        var overlay = { fps: 0 };
        function __add(list, item) {
          if (item === null || item === undefined || item === false || item === true) return;
          if (Array.isArray(item)) { for (var i = 0; i < item.length; i++) __add(list, item[i]); return; }
          if (typeof item === 'string' || typeof item === 'number') { list.push(text(String(item))); return; }
          list.push(item);
        }
        function __node(type, args) {
          var props = {}, children = [], start = 0;
          if (args.length && args[0] && typeof args[0] === 'object' && !Array.isArray(args[0]) && !args[0].type) { props = args[0]; start = 1; }
          for (var i = start; i < args.length; i++) __add(children, args[i]);
          return { type: type, props: props, children: children };
        }
        function box() { return __node('box', arguments); }
        function row() { var n = __node('box', arguments); n.props.direction = 'row'; return n; }
        function column() { var n = __node('box', arguments); n.props.direction = 'column'; return n; }
        function stack() { return __node('stack', arguments); }
        function canvas() { return __node('canvas', arguments); }
        function text(value, props) { return { type: 'text', props: Object.assign({}, props, { value: String(value) }), children: [] }; }
        function image(src, props) { return { type: 'image', props: Object.assign({}, props, { src: src }), children: [] }; }
        function rect(props) { return { type: 'rect', props: props || {}, children: [] }; }
        function ellipse(props) { return { type: 'ellipse', props: props || {}, children: [] }; }
        function line(props) { return { type: 'line', props: props || {}, children: [] }; }
        function path(d, props) { return { type: 'path', props: Object.assign({}, props, { d: d }), children: [] }; }
        function __frame(json) {
          var input = JSON.parse(json);
          input.down = function (name) { return !!input.keys[String(name).toUpperCase()]; };
          input.since = function (name) { var at = input.changed[String(name).toUpperCase()]; return at === undefined ? Infinity : input.time - at; };
          input.count = function (name) { return input.counts[String(name).toUpperCase()] || 0; };
          input.show = function (name) {
            var key = String(name).toUpperCase();
            if (key === 'W' || key === 'A' || key === 'S' || key === 'D') return input.settings.showKeys;
            if (key === 'LMB' || key === 'RMB') return input.settings.showMouseButtons;
            if (key === 'SHIFT' || key === 'SPACE') return input.settings.showSpaceAndShift;
            return true;
          };
          return JSON.stringify(render(input));
        }
        function __key(name, down) { if (typeof onKey === 'function') onKey(name, down); }
        """;

    private readonly Engine _engine;
    private readonly Dictionary<string, double> _changed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<double> _lmbClicks = new();
    private readonly Queue<double> _rmbClicks = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _mouseX;
    private double _mouseY;
    private double _pendingDx;
    private double _pendingDy;
    private double _pendingWheel;
    private double _lastFrame;
    private bool _disposed;

    public int Fps { get; private set; }

    public KeystrokeScriptHost(string source)
    {
        _engine = new Engine(options =>
        {
            options.Strict();
            options.TimeoutInterval(TimeSpan.FromMilliseconds(250));
            options.LimitMemory(32L * 1024 * 1024);
            options.LimitRecursion(128);
        });
        _engine.Evaluate(Prelude);
        _engine.Evaluate(source);
        if (_engine.Evaluate("typeof render").AsString() != "function")
            throw new InvalidDataException("The overlay code must define a function named render(input)");
        Fps = (int)Math.Clamp(_engine.Evaluate("+overlay.fps || 0").AsNumber(), 0, 60);
    }

    public void SetKey(string name, bool down)
    {
        string key = name.ToUpperInvariant();
        double now = _clock.Elapsed.TotalMilliseconds;
        if (down ? !_held.Add(key) : !_held.Remove(key))
            return;
        _changed[key] = now;
        if (down)
        {
            _counts[key] = _counts.GetValueOrDefault(key) + 1;
            if (key == "LMB")
                _lmbClicks.Enqueue(now);
            else if (key == "RMB")
                _rmbClicks.Enqueue(now);
        }
        _engine.Invoke("__key", name, down);
    }

    public void AddMouse(double dx, double dy, double wheel)
    {
        _pendingDx += dx;
        _pendingDy += dy;
        _pendingWheel += wheel;
    }

    public JsonNode? Render()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        double dt = Math.Max(0, now - _lastFrame);
        _lastFrame = now;
        double decay = Math.Exp(-dt / 120.0);
        _mouseX = Math.Clamp((_mouseX + _pendingDx / 120.0) * decay, -1, 1);
        _mouseY = Math.Clamp((_mouseY + _pendingDy / 120.0) * decay, -1, 1);

        var settings = App.Settings.Prop;
        JsonObject input = new()
        {
            ["time"] = now,
            ["dt"] = dt,
            ["keys"] = new JsonObject(_held.Select(k => KeyValuePair.Create<string, JsonNode?>(k, true))),
            ["changed"] = new JsonObject(_changed.Select(k => KeyValuePair.Create<string, JsonNode?>(k.Key, k.Value))),
            ["counts"] = new JsonObject(_counts.Select(k => KeyValuePair.Create<string, JsonNode?>(k.Key, k.Value))),
            ["cps"] = new JsonObject { ["lmb"] = Cps(_lmbClicks, now), ["rmb"] = Cps(_rmbClicks, now) },
            ["mouse"] = new JsonObject
            {
                ["x"] = _mouseX,
                ["y"] = _mouseY,
                ["dx"] = _pendingDx,
                ["dy"] = _pendingDy,
                ["wheel"] = _pendingWheel,
            },
            ["accent"] = ColorText(KeystrokeTheme.GetEffectiveAccentColor()),
            ["settings"] = new JsonObject
            {
                ["showKeys"] = settings.KeystrokesShowKeys,
                ["showMouseButtons"] = settings.KeystrokesShowMouseButtons,
                ["showSpaceAndShift"] = settings.KeystrokesShowSpaceAndShift,
                ["showCps"] = settings.KeystrokesShowCps,
            },
        };
        _pendingDx = 0;
        _pendingDy = 0;
        _pendingWheel = 0;

        JsValue result = _engine.Invoke("__frame", input.ToJsonString());
        return result.IsString() ? JsonNode.Parse(result.AsString()) : null;
    }

    private static int Cps(Queue<double> clicks, double now)
    {
        while (clicks.Count > 0 && now - clicks.Peek() > 1000)
            clicks.Dequeue();
        return clicks.Count;
    }

    private static string ColorText(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _engine.Dispose();
    }
}
