using System;
using System.Windows.Threading;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations
{
    public sealed class LinuxAutoFullscreen : IDisposable
    {
        private readonly DispatcherTimer _timer;

        private const int RequiredStableTicks = 1;
        private const long StartupRetryMilliseconds = 5000;

        private bool _disposed;
        private bool _handled;
        private bool _requested;
        private int _stableTicks;
        private int _lastWidth;
        private int _lastHeight;
        private nint _window;
        private long _firstRequestAt;

        public LinuxAutoFullscreen()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            _timer.Tick += OnTick;
        }

        public void Start()
        {
            if (!Voidstrap.Utility.Platform.IsLinux || !App.Settings.Prop.SoberAutoFullscreen)
                return;

            _timer.Start();
            OnTick(this, EventArgs.Empty);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_disposed)
                return;

            try
            {
                Apply();
            }
            catch (Exception ex)
            {
                _timer.Stop();
                App.Logger.WriteLine("LinuxAutoFullscreen", "Fullscreen could not be applied: " + ex.Message);
            }
        }

        private void Apply()
        {
            LinuxWindowGeometry geometry = LinuxWindowInterop.FindRuntimeWindow(_window);

            if (geometry.Window == 0 || !LinuxWindowInterop.IsSoberRuntimeWindow(geometry.Window))
            {
                _window = 0;
                _handled = false;
                _requested = false;
                _stableTicks = 0;
                _timer.Interval = TimeSpan.FromMilliseconds(500);
                return;
            }

            if (_window != geometry.Window)
            {
                _window = geometry.Window;
                _handled = false;
                _requested = false;
                _stableTicks = 0;
                _lastWidth = geometry.Width;
                _lastHeight = geometry.Height;
                _timer.Interval = TimeSpan.FromMilliseconds(500);
            }

            if (_handled)
                return;

            if (LinuxWindowInterop.IsFullscreen(geometry.Window))
            {
                if (!_requested)
                    App.Logger.WriteLine("LinuxAutoFullscreen", "Roblox is already fullscreen, leaving it alone");
                if (!_requested || Environment.TickCount64 - _firstRequestAt >= StartupRetryMilliseconds)
                {
                    _handled = true;
                    _timer.Interval = TimeSpan.FromSeconds(1);
                    if (_requested)
                        App.Logger.WriteLine("LinuxAutoFullscreen", "Put the Roblox window into fullscreen");
                }
                return;
            }

            if (geometry.Width == _lastWidth && geometry.Height == _lastHeight && geometry.Width > 0)
                _stableTicks++;
            else
                _stableTicks = 0;

            _lastWidth = geometry.Width;
            _lastHeight = geometry.Height;

            if (_stableTicks < RequiredStableTicks)
                return;

            if (!LinuxWindowInterop.TrySetFullscreen(geometry.Window))
                return;

            if (!_requested)
            {
                _requested = true;
                _firstRequestAt = Environment.TickCount64;
            }
        }

        public void Dispose()
        {
            if (!_timer.Dispatcher.CheckAccess())
            {
                _timer.Dispatcher.Invoke(Dispose);
                return;
            }

            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            GC.SuppressFinalize(this);
        }
    }
}
