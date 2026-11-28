using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Xml.Linq;
using Voidstrap.UI.Elements.Bootstrapper;

namespace Voidstrap.Integrations.Keystrokes;

internal sealed class KeystrokeXmlView : ContentControl, IKeystrokeThemeHost, IDisposable
{
    private const int CpsIntervalMs = 500;

    private const int MouseIntervalMs = 33;

    private const int ReleaseFadeMs = 130;

    private const double MouseDecayMs = 120.0;

    private static readonly DoubleAnimation ReleaseFade = CreateReleaseFade();
    private readonly DispatcherTimer _cpsTimer = new() { Interval = TimeSpan.FromMilliseconds(CpsIntervalMs) };
    private readonly DispatcherTimer _mouseTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(MouseIntervalMs) };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly bool _liveInput;
    private readonly List<KeystrokeKeyVisual> _keys = new();
    private readonly Dictionary<string, List<KeystrokeKeyVisual>> _keysByInput = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Input, TextBlock Text, string Format)> _cpsTexts = new();
    private readonly List<(FrameworkElement Dot, double Travel, TranslateTransform Move, ScaleTransform Scale)> _circles = new();
    private readonly HashSet<string> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<double> _lmbClicks = new();
    private readonly Queue<double> _rmbClicks = new();
    private string _themeDirectory = string.Empty;
    private double _mouseX;
    private double _mouseY;
    private double _pendingDx;
    private double _pendingDy;
    private double _lastMouseFrame;
    private int _lastLmbCps = -1;
    private int _lastRmbCps = -1;
    private bool _disposed;

    public event Action<string?>? ThemeStateChanged;

    public KeystrokeXmlView(bool liveInput = false)
    {
        _liveInput = liveInput;
        Focusable = false;
        _cpsTimer.Tick += OnCpsTick;
        _mouseTimer.Tick += OnMouseTick;
        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private void OnViewLoaded(object sender, RoutedEventArgs e) => StartTimers();

    private void OnViewUnloaded(object sender, RoutedEventArgs e)
    {
        _cpsTimer.Stop();
        _mouseTimer.Stop();
    }

    private void StartTimers()
    {
        if (!IsRunning || !IsLoaded)
            return;
        if (_keys.Any(key => key.Cps != null) || _cpsTexts.Count > 0)
            _cpsTimer.Start();
        if (_circles.Count > 0)
        {
            _lastMouseFrame = _clock.Elapsed.TotalMilliseconds;
            _mouseTimer.Start();
        }
    }

    public bool IsRunning { get; private set; }

    internal IReadOnlyList<KeystrokeKeyVisual> Keys => _keys;

    string ICustomThemeHost.ThemeDir => _themeDirectory;

    List<string> ICustomThemeHost.UsedNames { get; } = new();

    bool ICustomThemeHost.AllowsElement(string name) => !CustomDialog.IsDialogOnlyElement(name);

    XElement? IKeystrokeThemeHost.KeyStyle { get; set; }

    void IKeystrokeThemeHost.AddKey(KeystrokeKeyVisual key)
    {
        _keys.Add(key);
        if (!_keysByInput.TryGetValue(key.Input, out List<KeystrokeKeyVisual>? visuals))
            _keysByInput[key.Input] = visuals = [];
        visuals.Add(key);
    }

    void IKeystrokeThemeHost.AddCpsText(string input, TextBlock text, string format) => _cpsTexts.Add((input, text, format));

    void IKeystrokeThemeHost.AddMouseCircle(FrameworkElement dot, double travel)
    {
        TranslateTransform move = new();
        ScaleTransform scale = new();
        dot.RenderTransform = new TransformGroup { Children = { scale, move } };
        _circles.Add((dot, travel, move, scale));
    }

    public static string? Validate(string source, string themeDirectory)
    {
        Application? application = Application.Current;
        if (application == null)
            return null;
        return application.Dispatcher.Invoke(() =>
        {
            using KeystrokeXmlView view = new();
            return view.Load(source, themeDirectory);
        });
    }

    public string? Load(string source, string themeDirectory)
    {
        Reset();
        _themeDirectory = themeDirectory;
        try
        {
            Content = CustomDialog.BuildKeystrokeOverlay(this, source);
        }
        catch (Exception ex)
        {
            Reset();
            ThemeStateChanged?.Invoke(ex.Message);
            return ex.Message;
        }
        IsRunning = true;
        if (_liveInput)
        {
            foreach (string input in _keysByInput.Keys)
            {
                if (KeystrokeInputService.IsNamedDown(input))
                    SetKey(input, true);
            }
        }
        UpdateCps();
        StartTimers();
        ThemeStateChanged?.Invoke(null);
        return null;
    }

    public void Unload()
    {
        Reset();
        Content = null;
    }

    private void Reset()
    {
        _cpsTimer.Stop();
        _mouseTimer.Stop();
        IsRunning = false;
        _keys.Clear();
        _keysByInput.Clear();
        _cpsTexts.Clear();
        _circles.Clear();
        _held.Clear();
        _lmbClicks.Clear();
        _rmbClicks.Clear();
        _mouseX = 0;
        _mouseY = 0;
        _pendingDx = 0;
        _pendingDy = 0;
        _lastLmbCps = -1;
        _lastRmbCps = -1;
        ((ICustomThemeHost)this).UsedNames.Clear();
        ((IKeystrokeThemeHost)this).KeyStyle = null;
    }

    public void SetKey(string name, bool down)
    {
        if (!IsRunning || (down ? !_held.Add(name) : !_held.Remove(name)))
            return;
        if (down && !_liveInput)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            if (name.Equals("LMB", StringComparison.OrdinalIgnoreCase))
                _lmbClicks.Enqueue(now);
            else if (name.Equals("RMB", StringComparison.OrdinalIgnoreCase))
                _rmbClicks.Enqueue(now);
        }
        if (_keysByInput.TryGetValue(name, out List<KeystrokeKeyVisual>? visuals))
        {
            foreach (KeystrokeKeyVisual key in visuals)
                ApplyState(key, down);
        }
        bool mouseButton = name.Equals("LMB", StringComparison.OrdinalIgnoreCase) || name.Equals("RMB", StringComparison.OrdinalIgnoreCase);
        if (!mouseButton)
            return;
        if (down)
            UpdateCps();
        double dotScale = _held.Contains("LMB") || _held.Contains("RMB") ? 1.2 : 1.0;
        foreach (var circle in _circles)
            circle.Scale.ScaleX = circle.Scale.ScaleY = dotScale;
    }

    public void AddMouse(double dx, double dy, double wheel)
    {
        _pendingDx += dx;
        _pendingDy += dy;
    }

    private static void ApplyState(KeystrokeKeyVisual key, bool down)
    {
        key.Highlight.BeginAnimation(OpacityProperty, null);
        if (down)
        {
            key.Highlight.Opacity = 1.0;
            key.Label.Foreground = key.PressedForeground;
            if (key.Cps != null)
                key.Cps.Foreground = key.PressedCpsForeground;
            key.Key.RenderTransform = Math.Abs(key.PressedScale - 1.0) > 0.001 ? new ScaleTransform(key.PressedScale, key.PressedScale) : Transform.Identity;
            return;
        }
        key.Highlight.BeginAnimation(OpacityProperty, ReleaseFade);
        key.Label.Foreground = key.Foreground;
        if (key.Cps != null)
            key.Cps.Foreground = key.CpsForeground;
        key.Key.RenderTransform = Transform.Identity;
    }

    private void OnCpsTick(object? sender, EventArgs e) => UpdateCps();

    private static DoubleAnimation CreateReleaseFade()
    {
        DoubleAnimation animation = new(0.0, TimeSpan.FromMilliseconds(ReleaseFadeMs));
        animation.Freeze();
        return animation;
    }

    private void UpdateCps()
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        int lmb = _liveInput ? KeystrokeInputService.GetLmbCps() : Count(_lmbClicks, now);
        int rmb = _liveInput ? KeystrokeInputService.GetRmbCps() : Count(_rmbClicks, now);
        if (lmb == _lastLmbCps && rmb == _lastRmbCps)
            return;
        _lastLmbCps = lmb;
        _lastRmbCps = rmb;
        foreach (KeystrokeKeyVisual key in _keys)
        {
            if (key.Cps != null)
                SetText(key.Cps, key.CpsFormat, key.Input.Equals("RMB", StringComparison.OrdinalIgnoreCase) ? rmb : lmb);
        }
        foreach (var (input, text, format) in _cpsTexts)
            SetText(text, format, input.Equals("RMB", StringComparison.OrdinalIgnoreCase) ? rmb : lmb);
    }

    private static void SetText(TextBlock text, string format, int value)
    {
        string next;
        try
        {
            next = string.Format(CultureInfo.InvariantCulture, format, value);
        }
        catch (FormatException)
        {
            next = value.ToString(CultureInfo.InvariantCulture);
        }
        if (text.Text != next)
            text.Text = next;
    }

    private static int Count(Queue<double> clicks, double now)
    {
        while (clicks.Count > 0 && now - clicks.Peek() > 1000)
            clicks.Dequeue();
        return clicks.Count;
    }

    private void OnMouseTick(object? sender, EventArgs e)
    {
        if (_liveInput)
        {
            (double dx, double dy, _) = KeystrokeInputService.TakeMouseMotion();
            _pendingDx += dx;
            _pendingDy += dy;
        }
        double now = _clock.Elapsed.TotalMilliseconds;
        if (_pendingDx == 0 && _pendingDy == 0 && _mouseX == 0 && _mouseY == 0)
        {
            _lastMouseFrame = now;
            return;
        }
        double decay = Math.Exp(-Math.Max(0, now - _lastMouseFrame) / MouseDecayMs);
        _lastMouseFrame = now;
        _mouseX = Math.Clamp((_mouseX + _pendingDx / MouseDecayMs) * decay, -1, 1);
        _mouseY = Math.Clamp((_mouseY + _pendingDy / MouseDecayMs) * decay, -1, 1);
        if (Math.Abs(_mouseX) < 0.0001)
            _mouseX = 0;
        if (Math.Abs(_mouseY) < 0.0001)
            _mouseY = 0;
        _pendingDx = 0;
        _pendingDy = 0;
        foreach (var circle in _circles)
        {
            double x = Math.Round(_mouseX * circle.Travel, 1);
            double y = Math.Round(_mouseY * circle.Travel, 1);
            if (circle.Move.X != x)
                circle.Move.X = x;
            if (circle.Move.Y != y)
                circle.Move.Y = y;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cpsTimer.Tick -= OnCpsTick;
        _mouseTimer.Tick -= OnMouseTick;
        Loaded -= OnViewLoaded;
        Unloaded -= OnViewUnloaded;
        Unload();
        GC.SuppressFinalize(this);
    }
}
