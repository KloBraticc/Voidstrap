using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.Integrations;

internal sealed partial class RobloxScreenshotWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan FocusGrace = TimeSpan.FromSeconds(8);

    private readonly Action<string?> _onScreenshot;
    private readonly Dispatcher _dispatcher;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastShown = DateTime.MinValue;
    private DateTime _lastRobloxFocus = DateTime.MinValue;
    private uint _clipboardSequence;
    private bool _disposed;

    [LibraryImport("user32.dll", EntryPoint = "GetClipboardSequenceNumber")]
    private static partial uint GetClipboardSequenceNumber();

    public RobloxScreenshotWatcher(Dispatcher dispatcher, Action<string?> onScreenshot)
    {
        _dispatcher = dispatcher;
        _onScreenshot = onScreenshot;
    }

    public void Start()
    {
        const string LOG_IDENT = "RobloxScreenshotWatcher::Start";
        if (!Voidstrap.Utility.Platform.IsWindows)
            return;
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        Watch(Path.Combine(pictures, "Roblox"), robloxOnly: true);
        Watch(Path.Combine(pictures, "Screenshots"), robloxOnly: false);
        try
        {
            _clipboardSequence = GetClipboardSequenceNumber();
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Clipboard sequence unavailable: " + ex.Message);
        }
        _ = PollAsync(_cts.Token);
    }

    private void Watch(string folder, bool robloxOnly)
    {
        const string LOG_IDENT = "RobloxScreenshotWatcher::Watch";
        try
        {
            Directory.CreateDirectory(folder);
            FileSystemWatcher watcher = new(folder, "*.png") { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = false };
            watcher.Created += robloxOnly ? OnRobloxFileCreated : OnShellFileCreated;
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            App.Logger.WriteLine(LOG_IDENT, "Could not watch " + folder + ": " + ex.Message);
        }
    }

    private void OnRobloxFileCreated(object sender, FileSystemEventArgs e) => Raise(e.FullPath);

    private void OnShellFileCreated(object sender, FileSystemEventArgs e)
    {
        if (RobloxRecentlyFocused())
            Raise(e.FullPath);
    }

    private bool RobloxRecentlyFocused()
    {
        if (RobloxWindowTracker.IsRobloxForeground())
            return true;
        return DateTime.UtcNow - _lastRobloxFocus < FocusGrace;
    }

    private async Task PollAsync(CancellationToken token)
    {
        const string LOG_IDENT = "RobloxScreenshotWatcher::Poll";
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, token);
                if (RobloxWindowTracker.IsRobloxForeground())
                    _lastRobloxFocus = DateTime.UtcNow;
                uint sequence = GetClipboardSequenceNumber();
                if (sequence == _clipboardSequence)
                    continue;
                _clipboardSequence = sequence;
                if (!RobloxRecentlyFocused())
                    continue;
                bool image = await _dispatcher.InvokeAsync(ClipboardHasImage);
                if (image)
                    Raise(null);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Error: " + ex.Message);
            }
        }
    }

    private static bool ClipboardHasImage()
    {
        try
        {
            return Clipboard.ContainsImage();
        }
        catch (COMException)
        {
            return false;
        }
    }

    private void Raise(string? path)
    {
        if (_disposed)
            return;
        DateTime now = DateTime.UtcNow;
        lock (_watchers)
        {
            if (now - _lastShown < Debounce)
                return;
            _lastShown = now;
        }
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, _onScreenshot, path);
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        foreach (FileSystemWatcher watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnRobloxFileCreated;
            watcher.Created -= OnShellFileCreated;
            watcher.Dispose();
        }
        _watchers.Clear();
        GC.SuppressFinalize(this);
    }
}
