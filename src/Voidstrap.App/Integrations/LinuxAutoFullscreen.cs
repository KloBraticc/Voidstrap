using System;
using System.Windows.Threading;
using Voidstrap.Platform.Linux;

namespace Voidstrap.Integrations
{
    public sealed class LinuxAutoFullscreen : IDisposable
    {
        private const int MaxAttempts = 90;

        private readonly DispatcherTimer _timer;

        private const int MinimumTicksBeforeApply = 12;
        private const int RequiredStableTicks = 3;

        private int _attempts;
        private bool _applied;
        private bool _disposed;
        private int _stableTicks;
        private int _lastWidth;
        private int _lastHeight;

        public LinuxAutoFullscreen()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(1000)
            };

            _timer.Tick += OnTick;
        }

        public void Start()
        {
            if (!Voidstrap.Utility.Platform.IsLinux || !App.Settings.Prop.SoberAutoFullscreen)
                return;

            _timer.Start();
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_disposed)
                return;

            try
            {
                _attempts++;

                if (Apply() || _attempts >= MaxAttempts)
                    _timer.Stop();
            }
            catch (Exception ex)
            {
                _timer.Stop();
                App.Logger.WriteLine("LinuxAutoFullscreen", "Fullscreen could not be applied: " + ex.Message);
            }
        }

        private bool Apply()
        {
            if (_applied)
                return true;

            LinuxWindowGeometry geometry = LinuxWindowInterop.FindRuntimeWindow();

            if (geometry.Window == 0)
            {
                _stableTicks = 0;
                return false;
            }

            if (LinuxWindowInterop.IsFullscreen(geometry.Window))
            {
                _applied = true;
                App.Logger.WriteLine("LinuxAutoFullscreen", "Roblox is already fullscreen, leaving it alone");
                return true;
            }

            if (geometry.Width == _lastWidth && geometry.Height == _lastHeight && geometry.Width > 0)
                _stableTicks++;
            else
                _stableTicks = 0;

            _lastWidth = geometry.Width;
            _lastHeight = geometry.Height;

            if (_attempts < MinimumTicksBeforeApply || _stableTicks < RequiredStableTicks)
                return false;

            if (!LinuxWindowInterop.TrySetFullscreen(geometry.Window))
                return false;

            _applied = true;
            App.Logger.WriteLine("LinuxAutoFullscreen", "Put the Roblox window into fullscreen");
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            GC.SuppressFinalize(this);
        }
    }
}
