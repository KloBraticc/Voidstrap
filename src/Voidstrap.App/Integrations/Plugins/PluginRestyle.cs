using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.Integrations.Plugins;

internal static class PluginRestyle
{
    private const string LOG_IDENT = "PluginRestyle";

    private const int MaxRulesPerMod = 256;

    private const int MaxMovesPerMod = 64;

    private const int MaxResourcesPerMod = 256;

    private const int MaxTreeNodes = 1500;

    private const int MaxReportedErrors = 8;

    private const int UiTimeoutMs = 1500;

    private sealed record Rule(string Plugin, string Id, JsonObject Target, JsonObject Set);

    private sealed record Move(string Plugin, string Id, JsonObject Target, JsonObject Into, int Index);

    private sealed record Listener(string Plugin, string Id, JsonObject Target);

    private sealed record MoveOrigin(DependencyObject Parent, int Index);

    private sealed record ResourceOrigin(bool Existed, object? Value);

    private sealed class ApplyReport
    {
        public int Matched;

        public readonly List<string> Errors = [];

        public void Error(string text)
        {
            if (Errors.Count < MaxReportedErrors && !Errors.Contains(text))
                Errors.Add(text);
        }
    }

    private static readonly List<Rule> Rules = [];

    private static readonly List<Move> Moves = [];

    private static readonly List<Listener> Listeners = [];

    private static readonly Dictionary<(string Plugin, string Key), string> Resources = [];

    private static readonly Dictionary<string, ResourceOrigin> ResourceOrigins = [];

    private static readonly ConditionalWeakTable<FrameworkElement, Dictionary<DependencyProperty, object>> Originals = new();

    private static readonly ConditionalWeakTable<FrameworkElement, MoveOrigin> MoveOrigins = new();

    private static readonly HashSet<string> DeniedProperties = new(StringComparer.Ordinal)
    {
        "DataContext",
        "Template",
        "Style",
        "ItemsSource",
        "ItemTemplate",
        "ContentTemplate",
        "Command",
        "CommandParameter",
        "CommandTarget",
    };

    private static readonly Dictionary<string, DependencyProperty> AttachedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Grid.Row"] = Grid.RowProperty,
        ["Grid.Column"] = Grid.ColumnProperty,
        ["Grid.RowSpan"] = Grid.RowSpanProperty,
        ["Grid.ColumnSpan"] = Grid.ColumnSpanProperty,
        ["DockPanel.Dock"] = DockPanel.DockProperty,
        ["Panel.ZIndex"] = Panel.ZIndexProperty,
        ["Canvas.Left"] = Canvas.LeftProperty,
        ["Canvas.Top"] = Canvas.TopProperty,
        ["Canvas.Right"] = Canvas.RightProperty,
        ["Canvas.Bottom"] = Canvas.BottomProperty,
    };

    private static DispatcherTimer? _timer;

    private static bool _handlerRegistered;

    private static bool _applying;

    private static bool _suspended;

    private static bool HasWork => Rules.Count > 0 || Moves.Count > 0;

    private static Voidstrap.UI.Elements.Settings.MainWindow? MainWindow() =>
        Application.Current?.Windows.OfType<Voidstrap.UI.Elements.Settings.MainWindow>().FirstOrDefault();

    private static T OnUi<T>(Func<T> action, T fallback) => PluginNavigation.InvokeUiBounded(action, fallback, UiTimeoutMs);

    private static JsonObject Failed(string reason) =>
        new()
        {
            ["ok"] = false,
            ["matched"] = 0,
            ["errors"] = new JsonArray(reason),
        };

    private static JsonObject Report(ApplyReport report)
    {
        JsonArray errors = [];
        foreach (string error in report.Errors)
            errors.Add(error);
        return new JsonObject
        {
            ["ok"] = report.Errors.Count == 0,
            ["matched"] = report.Matched,
            ["errors"] = errors,
        };
    }

    private static JsonObject Unavailable() => Failed("the Voidstrap window did not answer in time");

    public static JsonObject AddRule(string plugin, JsonObject request) => OnUi(() => AddRuleCore(plugin, request), Unavailable());

    private static JsonObject AddRuleCore(string plugin, JsonObject request, List<FrameworkElement>? elements = null)
    {
        string id = request["id"]?.ToString() ?? "";
        if (id.Length is 0 or > 64)
            return Failed("a rule needs an id of 1 to 64 characters");
        if (request["target"] is not JsonObject target || target.Count == 0)
            return Failed("a rule needs a target such as { name: 'SidebarToggle' }");
        if (request["set"] is not JsonObject set || set.Count == 0)
            return Failed("a rule needs a set object with at least one property");
        bool replaced = Rules.RemoveAll(rule => rule.Plugin == plugin && rule.Id == id) > 0;
        if (Rules.Count(rule => rule.Plugin == plugin) >= MaxRulesPerMod)
            return Failed("a mod can keep at most " + MaxRulesPerMod + " rules");
        Rule added = new(plugin, id, (JsonObject)target.DeepClone(), (JsonObject)set.DeepClone());
        Rules.Add(added);
        EnsureHooked();
        if (replaced)
            ReapplyEverything();
        ApplyReport report = new();
        if (!_suspended)
            ApplyRule(added, elements ?? Snapshot(), report);
        return Report(report);
    }

    public static JsonObject AddMove(string plugin, JsonObject request) => OnUi(() => AddMoveCore(plugin, request), Unavailable());

    private static JsonObject AddMoveCore(string plugin, JsonObject request)
    {
        string id = request["id"]?.ToString() ?? "";
        if (id.Length is 0 or > 64)
            return Failed("a move needs an id of 1 to 64 characters");
        if (request["target"] is not JsonObject target || target.Count == 0)
            return Failed("a move needs a target such as { name: 'SidebarToggle' }");
        if (request["into"] is not JsonObject into || into.Count == 0)
            return Failed("a move needs an into target naming a panel");
        bool replaced = Moves.RemoveAll(move => move.Plugin == plugin && move.Id == id) > 0;
        if (Moves.Count(move => move.Plugin == plugin) >= MaxMovesPerMod)
            return Failed("a mod can keep at most " + MaxMovesPerMod + " moves");
        int index = request["index"] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : -1;
        Move added = new(plugin, id, (JsonObject)target.DeepClone(), (JsonObject)into.DeepClone(), index);
        Moves.Add(added);
        EnsureHooked();
        if (replaced)
            ReapplyEverything();
        ApplyReport report = new();
        if (!_suspended)
            ApplyMove(added, Snapshot(), report);
        return Report(report);
    }

    public static JsonObject SetResources(string plugin, JsonObject values) => OnUi(() => SetResourcesCore(plugin, values), Unavailable());

    private static JsonObject SetResourcesCore(string plugin, JsonObject values)
    {
        ApplyReport report = new();
        foreach (KeyValuePair<string, JsonNode?> entry in values)
        {
            string text = entry.Value?.ToString() ?? "";
            if (text.Length == 0)
            {
                Resources.Remove((plugin, entry.Key));
                continue;
            }
            if (!Resources.ContainsKey((plugin, entry.Key)) && Resources.Keys.Count(key => key.Plugin == plugin) >= MaxResourcesPerMod)
            {
                report.Error("a mod can override at most " + MaxResourcesPerMod + " resources");
                continue;
            }
            if (ConvertResource(entry.Key, text, report) == null)
                continue;
            Resources[(plugin, entry.Key)] = text;
            report.Matched++;
        }
        EnsureHooked();
        ApplyResources();
        return Report(report);
    }

    public static JsonObject Redesign(string plugin, JsonObject spec) => OnUi(() => RedesignCore(plugin, spec), Unavailable());

    private static JsonObject RedesignCore(string plugin, JsonObject spec)
    {
        if (spec["replace"] is JsonValue replace && replace.TryGetValue(out bool wipe) && wipe)
        {
            ForgetPlugin(plugin);
            ReapplyEverything();
        }
        int matched = 0;
        JsonArray errors = [];
        void Collect(JsonObject result)
        {
            matched += result["matched"]?.GetValue<int>() ?? 0;
            foreach (JsonNode? error in result["errors"]?.AsArray() ?? [])
                if (errors.Count < MaxReportedErrors)
                    errors.Add(error?.ToString());
        }
        if (spec["resources"] is JsonObject resources)
            Collect(SetResourcesCore(plugin, resources));
        foreach (JsonNode? move in spec["moves"]?.AsArray() ?? [])
            if (move is JsonObject request)
                Collect(AddMoveCore(plugin, request));
        List<FrameworkElement> elements = Snapshot();
        foreach (JsonNode? rule in spec["rules"]?.AsArray() ?? [])
            if (rule is JsonObject request)
                Collect(AddRuleCore(plugin, request, elements));
        return new JsonObject
        {
            ["ok"] = errors.Count == 0,
            ["matched"] = matched,
            ["errors"] = errors,
        };
    }

    public static void Remove(string plugin, string id) =>
        OnUi(
            () =>
            {
                Rules.RemoveAll(rule => rule.Plugin == plugin && rule.Id == id);
                Moves.RemoveAll(move => move.Plugin == plugin && move.Id == id);
                ReapplyEverything();
                return true;
            },
            false
        );

    public static void Clear(string plugin) =>
        OnUi(
            () =>
            {
                ForgetPlugin(plugin);
                ReapplyEverything();
                return true;
            },
            false
        );

    private static void ForgetPlugin(string plugin)
    {
        Rules.RemoveAll(rule => rule.Plugin == plugin);
        Moves.RemoveAll(move => move.Plugin == plugin);
        Listeners.RemoveAll(listener => listener.Plugin == plugin);
        foreach ((string Plugin, string Key) key in Resources.Keys.Where(key => key.Plugin == plugin).ToList())
            Resources.Remove(key);
    }

    internal static void RemovePlugins(IReadOnlySet<string> plugins)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;
        OnUi(
            () =>
            {
                foreach (string plugin in plugins)
                    ForgetPlugin(plugin);
                ReapplyEverything();
                return true;
            },
            false
        );
    }

    public static void ClearAll()
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;
        dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                Rules.Clear();
                Moves.Clear();
                Listeners.Clear();
                Resources.Clear();
                ReapplyEverything();
            })
        );
    }

    public static void ToggleSuspended()
    {
        _suspended = !_suspended;
        App.Logger.WriteLine(LOG_IDENT, _suspended ? "Mod UI changes suspended" : "Mod UI changes resumed");
        ReapplyEverything();
    }

    public static JsonObject List(string plugin) =>
        OnUi(
            () =>
            {
                JsonArray rules = [];
                foreach (Rule rule in Rules.Where(rule => rule.Plugin == plugin))
                    rules.Add(new JsonObject { ["id"] = rule.Id, ["target"] = rule.Target.DeepClone(), ["set"] = rule.Set.DeepClone() });
                JsonArray moves = [];
                foreach (Move move in Moves.Where(move => move.Plugin == plugin))
                    moves.Add(new JsonObject { ["id"] = move.Id, ["target"] = move.Target.DeepClone(), ["into"] = move.Into.DeepClone(), ["index"] = move.Index });
                JsonObject resources = [];
                foreach (KeyValuePair<(string Plugin, string Key), string> entry in Resources.Where(entry => entry.Key.Plugin == plugin))
                    resources[entry.Key.Key] = entry.Value;
                return new JsonObject
                {
                    ["suspended"] = _suspended,
                    ["rules"] = rules,
                    ["moves"] = moves,
                    ["resources"] = resources,
                };
            },
            new JsonObject()
        );

    public static void Schedule()
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;
        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartTimer));
    }

    internal static void EnsureHooked()
    {
        if (_handlerRegistered)
            return;
        _handlerRegistered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnAnyElementLoaded));
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent, new RoutedEventHandler(OnAnyClick), true);
    }

    private static void OnAnyElementLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window)
            PluginAnchors.ApplyAll();
        if (!_applying && HasWork && !_suspended)
            StartTimer();
    }

    private static void StartTimer()
    {
        if (!HasWork || _suspended)
            return;
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += Timer_Tick;
        }
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    private static void Timer_Tick(object? sender, EventArgs e)
    {
        _timer?.Stop();
        ApplyAllNow();
    }

    private static IEnumerable<Window> AllWindows() => Application.Current?.Windows.OfType<Window>() ?? [];

    private static List<FrameworkElement> Snapshot() =>
        AllWindows()
            .SelectMany(window => PluginAnchors.Descendants(window).OfType<FrameworkElement>())
            .Where(element => element is not Window)
            .ToList();

    private static bool WindowMatches(FrameworkElement element, string wanted)
    {
        Window? window = element as Window ?? Window.GetWindow(element);
        return window != null
            && (string.Equals(window.GetType().Name, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(window.Title, wanted, StringComparison.OrdinalIgnoreCase));
    }

    public static JsonArray Windows() =>
        OnUi(
            () =>
            {
                JsonArray result = [];
                foreach (Window window in AllWindows())
                    result.Add(
                        new JsonObject
                        {
                            ["type"] = window.GetType().Name,
                            ["title"] = window.Title,
                            ["visible"] = window.IsVisible,
                            ["active"] = window.IsActive,
                            ["width"] = Math.Round(window.ActualWidth),
                            ["height"] = Math.Round(window.ActualHeight),
                        }
                    );
                return result;
            },
            new JsonArray()
        );

    public static JsonObject Listen(string plugin, JsonObject request) =>
        OnUi(
            () =>
            {
                string id = request["id"]?.ToString() ?? "";
                if (id.Length is 0 or > 64 || request["target"] is not JsonObject target || target.Count == 0)
                    return Failed("listen needs an id and a target");
                Listeners.RemoveAll(listener => listener.Plugin == plugin && listener.Id == id);
                if (Listeners.Count(listener => listener.Plugin == plugin) >= MaxRulesPerMod)
                    return Failed("a mod can keep at most " + MaxRulesPerMod + " listeners");
                Listeners.Add(new Listener(plugin, id, (JsonObject)target.DeepClone()));
                EnsureHooked();
                return new JsonObject { ["ok"] = true, ["matched"] = Select(target, Snapshot()).Count, ["errors"] = new JsonArray() };
            },
            Unavailable()
        );

    public static JsonObject Press(JsonObject target) =>
        OnUi(
            () =>
            {
                FrameworkElement? element = Select(target, Snapshot()).FirstOrDefault(found => found is ButtonBase);
                if (element == null)
                    return Failed("no button matched");
                AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(element);
                if (peer?.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
                    invoke.Invoke();
                else if (peer?.GetPattern(PatternInterface.Toggle) is IToggleProvider toggle)
                    toggle.Toggle();
                else
                    return Failed("that button cannot be pressed");
                return new JsonObject { ["ok"] = true, ["matched"] = 1, ["errors"] = new JsonArray() };
            },
            Unavailable()
        );

    private static void OnAnyClick(object sender, RoutedEventArgs e)
    {
        if (Listeners.Count == 0 || sender is not FrameworkElement element)
            return;
        List<FrameworkElement> single = [element];
        foreach (Listener listener in Listeners)
        {
            if (Select(listener.Target, single).Count == 0)
                continue;
            JsonObject payload = new()
            {
                ["id"] = listener.Id,
                ["plugin"] = listener.Plugin,
                ["event"] = "click",
                ["name"] = element.Name,
                ["type"] = element.GetType().Name,
                ["text"] = TextOf(element),
                ["window"] = Window.GetWindow(element)?.GetType().Name ?? "",
            };
            _ = Task.Run(() => PluginHost.Invoke("ui.event", payload));
        }
    }

    private static readonly (string Key, string Role)[] StyleKeys =
    [
        ("WindowBackgroundColorPrimary", "background"),
        ("WindowBackgroundColorSecondary", "background"),
        ("WindowBackgroundColorThird", "background"),
        ("WindowBackgroundColorTertiary", "background"),
        ("PrimaryBackgroundColor", "background"),
        ("ApplicationBackgroundBrush", "background"),
        ("CardBackgroundFillColorDefaultBrush", "surface"),
        ("CardBackgroundFillColorSecondaryBrush", "surface"),
        ("ControlFillColorDefaultBrush", "surface"),
        ("ControlFillColorDefault", "surface"),
        ("ControlFillColorSecondaryBrush", "surface"),
        ("ControlFillColorTertiaryBrush", "surface"),
        ("SubtleFillColorSecondaryBrush", "surface"),
        ("CardStrokeColorDefaultBrush", "border"),
        ("ControlStrokeColorDefaultBrush", "border"),
        ("TextFillColorPrimaryBrush", "text"),
        ("TextFillColorSecondaryBrush", "muted"),
        ("TextFillColorTertiaryBrush", "muted"),
        ("AccentFillColorDefaultBrush", "accent"),
        ("AccentFillColorSecondaryBrush", "accent"),
        ("AccentFillColorTertiaryBrush", "accent"),
        ("SystemAccentBrush", "accent"),
        ("SystemAccentColor", "accent"),
        ("ControlCornerRadius", "radius"),
        ("OverlayCornerRadius", "radius"),
    ];

    private static readonly Dictionary<string, JsonObject> StylePresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["glass"] = new JsonObject
        {
            ["background"] = "#4D14141C",
            ["surface"] = "#1FFFFFFF",
            ["border"] = "#33FFFFFF",
            ["text"] = "#FFFFFFFF",
            ["muted"] = "#B3FFFFFF",
            ["accent"] = "#FF0A84FF",
            ["radius"] = "14",
            ["backdrop"] = "Acrylic",
        },
        ["midnight"] = new JsonObject
        {
            ["background"] = "#FF0B0D14",
            ["surface"] = "#FF171A24",
            ["border"] = "#FF2A2E3D",
            ["text"] = "#FFF2F4FA",
            ["muted"] = "#FF9AA1B5",
            ["accent"] = "#FF7C8CFF",
            ["radius"] = "10",
        },
        ["light"] = new JsonObject
        {
            ["background"] = "#FFF4F5F8",
            ["surface"] = "#FFFFFFFF",
            ["border"] = "#FFD9DCE3",
            ["text"] = "#FF16181D",
            ["muted"] = "#FF5D6470",
            ["accent"] = "#FF0A66D8",
            ["radius"] = "10",
        },
    };

    public static JsonObject Style(string plugin, JsonObject request) => OnUi(() => StyleCore(plugin, request), Unavailable());

    public static string? StyleBackdrop(JsonObject request)
    {
        if (request["backdrop"]?.ToString() is { Length: > 0 } backdrop)
            return backdrop;
        return request["preset"]?.ToString() is { } preset && StylePresets.TryGetValue(preset, out JsonObject? values) ? values["backdrop"]?.ToString() : null;
    }

    private static JsonObject StyleCore(string plugin, JsonObject request)
    {
        JsonObject merged = [];
        string? preset = request["preset"]?.ToString();
        if (preset != null)
        {
            if (!StylePresets.TryGetValue(preset, out JsonObject? values))
                return Failed("unknown preset " + preset + ", use " + string.Join(", ", StylePresets.Keys));
            foreach (KeyValuePair<string, JsonNode?> entry in values)
                merged[entry.Key] = entry.Value?.DeepClone();
        }
        foreach (KeyValuePair<string, JsonNode?> entry in request)
            if (entry.Key != "preset")
                merged[entry.Key] = entry.Value?.DeepClone();
        JsonObject resources = [];
        foreach ((string key, string role) in StyleKeys)
            if (merged[role]?.ToString() is { Length: > 0 } value && Application.Current?.TryFindResource(key) != null)
                resources[key] = value;
        return SetResourcesCore(plugin, resources);
    }

    private static void ApplyAllNow()
    {
        if (_suspended || !HasWork)
            return;
        _applying = true;
        try
        {
            List<FrameworkElement> elements = Snapshot();
            ApplyReport ignored = new();
            foreach (Move move in Moves)
                ApplyMove(move, elements, ignored);
            if (Moves.Count > 0)
                elements = Snapshot();
            foreach (Rule rule in Rules)
                ApplyRule(rule, elements, ignored);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not apply mod UI changes: " + ex.Message);
        }
        finally
        {
            _applying = false;
        }
    }

    private static void ReapplyEverything()
    {
        _applying = true;
        try
        {
            RestoreProperties();
            RestoreMoves();
            ApplyResources();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not restore the original UI: " + ex.Message);
        }
        finally
        {
            _applying = false;
        }
        ApplyAllNow();
    }

    private static void RestoreProperties()
    {
        foreach (KeyValuePair<FrameworkElement, Dictionary<DependencyProperty, object>> entry in Originals.ToList())
        {
            foreach (KeyValuePair<DependencyProperty, object> original in entry.Value)
                RestoreValue(entry.Key, original.Key, original.Value);
            Originals.Remove(entry.Key);
        }
    }

    private static void RestoreValue(FrameworkElement element, DependencyProperty property, object value)
    {
        try
        {
            if (value == DependencyProperty.UnsetValue)
                element.ClearValue(property);
            else if (value is BindingExpressionBase binding)
                BindingOperations.SetBinding(element, property, binding.ParentBindingBase);
            else
                element.SetValue(property, value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            element.ClearValue(property);
        }
    }

    private static void RestoreMoves()
    {
        foreach (KeyValuePair<FrameworkElement, MoveOrigin> entry in MoveOrigins.ToList())
        {
            if (Detach(entry.Key))
                Attach(entry.Key, entry.Value.Parent, entry.Value.Index);
            MoveOrigins.Remove(entry.Key);
        }
    }

    private static void ApplyRule(Rule rule, List<FrameworkElement> elements, ApplyReport report)
    {
        List<FrameworkElement> matches = Select(rule.Target, elements);
        report.Matched += matches.Count;
        if (matches.Count == 0)
            report.Error("rule " + rule.Id + " matched nothing yet, it applies again whenever the element appears");
        foreach (FrameworkElement element in matches)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in rule.Set)
                ApplyProperty(element, entry.Key, entry.Value, report);
            ApplyTransform(element, rule.Set, report);
        }
    }

    private static readonly string[] TransformKeys = ["rotate", "scale", "scaleX", "scaleY", "translateX", "translateY"];

    private static void ApplyProperty(FrameworkElement element, string name, JsonNode? value, ApplyReport report)
    {
        if (TransformKeys.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;
        if (name.Equals("hidden", StringComparison.OrdinalIgnoreCase))
        {
            bool hidden = value is JsonValue flag && flag.TryGetValue(out bool parsed) && parsed;
            SetTracked(element, UIElement.VisibilityProperty, hidden ? Visibility.Collapsed : Visibility.Visible);
            return;
        }
        DependencyProperty? property = ResolveProperty(element, name);
        if (property == null || property.ReadOnly)
        {
            report.Error(element.GetType().Name + " has no writable property " + name);
            return;
        }
        if (DeniedProperties.Contains(property.Name))
        {
            report.Error(property.Name + " cannot be changed by a mod");
            return;
        }
        if (IsTwoWayBound(element, property))
        {
            report.Error(property.Name + " is bound to a setting on " + element.GetType().Name + " and cannot be overwritten");
            return;
        }
        string text = value?.ToString() ?? "";
        if (text.StartsWith("theme:", StringComparison.Ordinal))
        {
            Remember(element, property);
            element.SetResourceReference(property, text["theme:".Length..]);
            return;
        }
        try
        {
            object? converted = ConvertValue(property.PropertyType, value, text);
            if (!property.IsValidValue(converted))
            {
                report.Error(text + " is not valid for " + property.Name);
                return;
            }
            SetTracked(element, property, converted);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or InvalidOperationException or OverflowException)
        {
            report.Error(text + " is not valid for " + property.Name);
        }
    }

    private static object? ConvertValue(Type type, JsonNode? value, string text)
    {
        if (value == null)
            return null;
        if (type == typeof(object) || type == typeof(string))
            return text;
        if (type == typeof(bool) && value is JsonValue flag && flag.TryGetValue(out bool parsed))
            return parsed;
        if (type == typeof(double) && value is JsonValue number && number.TryGetValue(out double amount))
            return amount;
        TypeConverter converter = TypeDescriptor.GetConverter(type);
        return converter.ConvertFromInvariantString(text);
    }

    private static void ApplyTransform(FrameworkElement element, JsonObject set, ApplyReport report)
    {
        if (!TransformKeys.Any(key => set.Any(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase))))
            return;
        try
        {
            double Read(string key, double fallback)
            {
                JsonNode? node = set.FirstOrDefault(entry => entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
                return node != null ? Convert.ToDouble(node.ToString(), CultureInfo.InvariantCulture) : fallback;
            }
            double scale = Read("scale", 1.0);
            TransformGroup group = new();
            group.Children.Add(new ScaleTransform(Read("scaleX", scale), Read("scaleY", scale)));
            group.Children.Add(new RotateTransform(Read("rotate", 0.0)));
            group.Children.Add(new TranslateTransform(Read("translateX", 0.0), Read("translateY", 0.0)));
            group.Freeze();
            SetTracked(element, UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
            SetTracked(element, UIElement.RenderTransformProperty, group);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            report.Error("rotate, scale and translate need numbers");
        }
    }

    private static void Remember(FrameworkElement element, DependencyProperty property)
    {
        Dictionary<DependencyProperty, object> originals = Originals.GetOrCreateValue(element);
        if (!originals.ContainsKey(property))
            originals[property] = element.ReadLocalValue(property);
    }

    private static void SetTracked(FrameworkElement element, DependencyProperty property, object? value)
    {
        Remember(element, property);
        if (!Equals(element.ReadLocalValue(property), value))
            element.SetValue(property, value);
    }

    private static bool IsTwoWayBound(FrameworkElement element, DependencyProperty property)
    {
        Binding? binding = BindingOperations.GetBinding(element, property);
        if (binding == null)
            return false;
        if (binding.Mode == BindingMode.TwoWay || binding.Mode == BindingMode.OneWayToSource)
            return true;
        return binding.Mode == BindingMode.Default
            && property.GetMetadata(element.DependencyObjectType) is FrameworkPropertyMetadata { BindsTwoWayByDefault: true };
    }

    private static DependencyProperty? ResolveProperty(FrameworkElement element, string name)
    {
        if (AttachedProperties.TryGetValue(name, out DependencyProperty? attached))
            return attached;
        if (name.Length == 0)
            return null;
        string pascal = char.ToUpperInvariant(name[0]) + name[1..];
        return DependencyPropertyDescriptor.FromName(pascal, element.GetType(), element.GetType())?.DependencyProperty;
    }

    private static void ApplyMove(Move move, List<FrameworkElement> elements, ApplyReport report)
    {
        FrameworkElement? element = Select(move.Target, elements).FirstOrDefault();
        Panel? destination = Select(move.Into, elements).OfType<Panel>().FirstOrDefault();
        if (element == null || destination == null)
        {
            report.Error("move " + move.Id + " found no element or destination panel yet, it applies again whenever they appear");
            return;
        }
        report.Matched++;
        if (destination.IsItemsHost)
        {
            report.Error("move " + move.Id + " cannot use a list generated panel as the destination");
            return;
        }
        if (ReferenceEquals(element.Parent, destination))
            return;
        if (element == destination || element.IsAncestorOf(destination))
        {
            report.Error("move " + move.Id + " cannot move an element into itself");
            return;
        }
        DependencyObject? parent = element.Parent;
        int index = parent is Panel from ? from.Children.IndexOf(element) : 0;
        if (parent == null || !Detach(element))
        {
            report.Error("move " + move.Id + " can only move elements that sit directly in a panel, border or content control");
            return;
        }
        if (!MoveOrigins.TryGetValue(element, out _))
            MoveOrigins.Add(element, new MoveOrigin(parent, index));
        int count = destination.Children.Count;
        destination.Children.Insert(move.Index < 0 || move.Index > count ? count : move.Index, element);
    }

    private static bool Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel { IsItemsHost: false } panel:
                panel.Children.Remove(element);
                return true;
            case Decorator decorator when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                return true;
            case ContentControl control when ReferenceEquals(control.Content, element):
                control.Content = null;
                return true;
            default:
                return false;
        }
    }

    private static void Attach(FrameworkElement element, DependencyObject parent, int index)
    {
        switch (parent)
        {
            case Panel panel:
                panel.Children.Insert(Math.Clamp(index, 0, panel.Children.Count), element);
                break;
            case Decorator decorator:
                decorator.Child = element;
                break;
            case ContentControl control:
                control.Content = element;
                break;
        }
    }

    private static void ApplyResources()
    {
        ResourceDictionary? target = Application.Current?.Resources;
        if (target == null)
            return;
        Dictionary<string, string> wanted = [];
        if (!_suspended)
            foreach (KeyValuePair<(string Plugin, string Key), string> entry in Resources)
                wanted[entry.Key.Key] = entry.Value;
        foreach (string key in ResourceOrigins.Keys.Where(key => !wanted.ContainsKey(key)).ToList())
        {
            ResourceOrigin origin = ResourceOrigins[key];
            if (origin.Existed)
                target[key] = origin.Value;
            else
                target.Remove(key);
            ResourceOrigins.Remove(key);
        }
        ApplyReport ignored = new();
        foreach (KeyValuePair<string, string> entry in wanted)
        {
            if (!ResourceOrigins.ContainsKey(entry.Key))
            {
                bool existed = target.Contains(entry.Key);
                ResourceOrigins[entry.Key] = new ResourceOrigin(existed, existed ? target[entry.Key] : null);
            }
            object? converted = ConvertResource(entry.Key, entry.Value, ignored);
            if (converted != null)
                target[entry.Key] = converted;
        }
    }

    private static object? ConvertResource(string key, string text, ApplyReport report)
    {
        object? current = Application.Current?.TryFindResource(key);
        if (current == null)
        {
            report.Error("no theme resource is named " + key + ", $.ui.resources() lists them");
            return null;
        }
        try
        {
            if (current is Brush)
            {
                Brush brush = (Brush)new BrushConverter().ConvertFromInvariantString(text)!;
                brush.Freeze();
                return brush;
            }
            return TypeDescriptor.GetConverter(current.GetType()).ConvertFromInvariantString(text);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            report.Error(text + " is not valid for the resource " + key);
            return null;
        }
    }

    public static JsonArray ListResources() =>
        OnUi(
            () =>
            {
                SortedDictionary<string, string> found = new(StringComparer.Ordinal);
                void Walk(ResourceDictionary dictionary)
                {
                    foreach (ResourceDictionary merged in dictionary.MergedDictionaries)
                        Walk(merged);
                    foreach (object key in dictionary.Keys)
                    {
                        if (key is not string name)
                            continue;
                        object? value = dictionary[key];
                        if (value is SolidColorBrush or Color or Thickness or CornerRadius or double)
                            found[name] = (value is SolidColorBrush brush ? brush.Color.ToString() : Convert.ToString(value, CultureInfo.InvariantCulture)) ?? "";
                    }
                }
                if (Application.Current != null)
                    Walk(Application.Current.Resources);
                if (MainWindow() is { } window)
                    Walk(window.Resources);
                JsonArray result = [];
                foreach (KeyValuePair<string, string> entry in found)
                    result.Add(new JsonObject { ["key"] = entry.Key, ["value"] = entry.Value });
                return result;
            },
            new JsonArray()
        );

    public static JsonArray Query(JsonObject target) =>
        OnUi(
            () =>
            {
                JsonArray result = [];
                foreach (FrameworkElement element in Select(target, Snapshot()).Take(MaxTreeNodes))
                    result.Add(Describe(element, Window.GetWindow(element)));
                return result;
            },
            new JsonArray()
        );

    public static JsonObject Tree(JsonObject request) =>
        OnUi(
            () =>
            {
                Window? window = (Window?)MainWindow() ?? AllWindows().FirstOrDefault();
                if (window == null)
                    return new JsonObject();
                if (request["window"]?.ToString() is { } wanted && AllWindows().FirstOrDefault(open => WindowMatches(open, wanted)) is { } chosen)
                    window = chosen;
                FrameworkElement root = window;
                if (request["root"] is JsonObject target && Select(target, Snapshot()).FirstOrDefault() is { } picked)
                    root = picked;
                int depth = request["depth"] is JsonValue value && value.TryGetValue(out int parsed) ? Math.Clamp(parsed, 1, 40) : 8;
                bool namedOnly = request["namedOnly"]?.GetValue<bool>() == true;
                int budget = MaxTreeNodes;
                return BuildNode(root, window, depth, namedOnly, ref budget);
            },
            new JsonObject()
        );

    private static JsonObject BuildNode(FrameworkElement element, Window window, int depth, bool namedOnly, ref int budget)
    {
        budget--;
        JsonObject node = Describe(element, window);
        if (depth <= 0)
            return node;
        JsonArray children = [];
        void Visit(DependencyObject parent, int remaining, ref int left)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count && left > 0; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement framework && (!namedOnly || framework.Name.Length > 0))
                    children.Add(BuildNode(framework, window, remaining - 1, namedOnly, ref left));
                else
                    Visit(child, remaining, ref left);
            }
        }
        Visit(element, depth, ref budget);
        if (children.Count > 0)
            node["children"] = children;
        return node;
    }

    private static JsonObject Describe(FrameworkElement element, Window? window)
    {
        JsonObject node = new() { ["type"] = element.GetType().Name };
        if (element.Name.Length > 0)
            node["name"] = element.Name;
        string text = TextOf(element);
        if (text.Length > 0)
            node["text"] = text.Length > 80 ? text[..80] : text;
        if (window != null)
            node["window"] = window.GetType().Name;
        node["visible"] = element.IsVisible;
        node["panel"] = element is Panel;
        if (window != null && element.IsVisible && !ReferenceEquals(element, window))
        {
            try
            {
                Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
                node["x"] = Math.Round(bounds.X);
                node["y"] = Math.Round(bounds.Y);
                node["width"] = Math.Round(bounds.Width);
                node["height"] = Math.Round(bounds.Height);
            }
            catch (InvalidOperationException)
            {
                node["width"] = Math.Round(element.ActualWidth);
                node["height"] = Math.Round(element.ActualHeight);
            }
        }
        return node;
    }

    private static string TextOf(FrameworkElement element) =>
        element switch
        {
            PasswordBox => "",
            TextBlock block => block.Text,
            HeaderedItemsControl headered => headered.Header?.ToString() ?? "",
            ContentControl content when content.Content is string text => text,
            _ => "",
        };

    private static List<FrameworkElement> Select(JsonObject target, List<FrameworkElement> elements)
    {
        string? page = target["page"]?.ToString();
        if (page != null && !string.Equals(MainWindow()?.GetFrame()?.Content?.GetType().Name, page, StringComparison.OrdinalIgnoreCase))
            return [];
        string? wantedWindow = target["window"]?.ToString();
        string? name = target["name"]?.ToString();
        string? type = target["type"]?.ToString();
        string? text = target["text"]?.ToString();
        string? contains = target["textContains"]?.ToString();
        string? ancestor = target["ancestor"]?.ToString();
        string? parent = target["parent"]?.ToString();
        bool everything = target["all"] is JsonValue flag && flag.TryGetValue(out bool all) && all;
        if (wantedWindow != null)
            elements = elements.Where(element => WindowMatches(element, wantedWindow)).ToList();
        if (!everything && wantedWindow == null && name == null && type == null && text == null && contains == null && ancestor == null && parent == null)
            return [];
        IEnumerable<FrameworkElement> found = elements.Where(element =>
            (name == null || NameMatches(element.Name, name))
            && (type == null || IsOfType(element, type))
            && (text == null || TextOf(element) == text)
            && (contains == null || TextOf(element).Contains(contains, StringComparison.OrdinalIgnoreCase))
            && (parent == null || VisualTreeHelper.GetParent(element) is FrameworkElement direct && NameMatches(direct.Name, parent))
            && (ancestor == null || HasAncestor(element, ancestor))
        );
        if (target["index"] is JsonValue position && position.TryGetValue(out int index))
            return found.Skip(Math.Max(index, 0)).Take(1).ToList();
        return found.ToList();
    }

    private static bool NameMatches(string actual, string pattern) =>
        pattern.EndsWith('*') ? actual.StartsWith(pattern[..^1], StringComparison.Ordinal) : actual == pattern;

    private static bool IsOfType(FrameworkElement element, string type)
    {
        for (Type? current = element.GetType(); current != null && current != typeof(FrameworkElement); current = current.BaseType)
        {
            if (current.Name == type)
                return true;
        }
        return false;
    }

    private static bool HasAncestor(FrameworkElement element, string name)
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(element); current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement framework && NameMatches(framework.Name, name))
                return true;
        }
        return false;
    }
}
