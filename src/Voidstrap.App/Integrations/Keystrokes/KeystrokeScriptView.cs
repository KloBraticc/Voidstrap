using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Voidstrap.Integrations.Keystrokes;

internal sealed class KeystrokeScriptView : ContentControl, IDisposable
{
    private const int IdleIntervalMs = 500;

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly bool _liveMouse;
    private KeystrokeScriptHost? _host;
    private KeystrokeScriptRenderer? _renderer;
    private string? _lastTree;
    private bool _disposed;

    public event Action<string?>? ScriptStateChanged;

    public KeystrokeScriptView(bool liveMouse = false)
    {
        _liveMouse = liveMouse;
        Focusable = false;
        _timer.Tick += OnTick;
    }

    public bool IsRunning => _host != null;

    public string? Load(string source, string themeDirectory)
    {
        KeystrokeScriptHost host;
        try
        {
            host = new KeystrokeScriptHost(source);
        }
        catch (Exception ex)
        {
            ScriptStateChanged?.Invoke(ex.Message);
            return ex.Message;
        }
        _timer.Stop();
        _host?.Dispose();
        _host = host;
        _renderer = new KeystrokeScriptRenderer(themeDirectory);
        _lastTree = null;
        _timer.Interval = TimeSpan.FromMilliseconds(host.Fps > 0 ? 1000.0 / host.Fps : IdleIntervalMs);
        _timer.Start();
        return RenderNow();
    }

    public void Unload()
    {
        _timer.Stop();
        _host?.Dispose();
        _host = null;
        _renderer = null;
        _lastTree = null;
        Content = null;
    }

    public void SetKey(string name, bool down)
    {
        if (_host == null)
            return;
        try
        {
            _host.SetKey(name, down);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return;
        }
        RenderNow();
    }

    public void AddMouse(double dx, double dy, double wheel) => _host?.AddMouse(dx, dy, wheel);

    private void OnTick(object? sender, EventArgs e)
    {
        if (_liveMouse && _host != null)
        {
            (double dx, double dy, double wheel) = KeystrokeInputService.TakeMouseMotion();
            _host.AddMouse(dx, dy, wheel);
        }
        RenderNow();
    }

    private string? RenderNow()
    {
        if (_host == null || _renderer == null)
            return null;
        try
        {
            JsonNode? tree = _host.Render();
            string text = tree?.ToJsonString() ?? "";
            if (text != _lastTree)
            {
                _lastTree = text;
                Content = _renderer.Build(tree);
            }
            ScriptStateChanged?.Invoke(null);
            return null;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private string Fail(Exception ex)
    {
        ScriptStateChanged?.Invoke(ex.Message);
        return ex.Message;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        Unload();
        GC.SuppressFinalize(this);
    }
}
