using System;
using System.ComponentModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Voidstrap.Integrations;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.Overlay
{
    public class OverlayWindow : Window, INotifyPropertyChanged
    {
        private TextBlock _pingTextBlock;
        private TextBlock _locationTextBlock;
        private TextBlock _timeTextBlock;
		private readonly StackPanel _readoutPanel;

        private readonly DispatcherTimer _updateTimer;
        private readonly RobloxOverlayAnchor _anchor;
		private readonly ActivityWatcher? _activityWatcher;
		private readonly CancellationTokenSource _lifetimeCts = new();

        private readonly bool _showPing = App.Settings.Prop.ServerPingCounter;
        private readonly bool _showTime = App.Settings.Prop.CurrentTimeDisplay;
        private readonly bool _showLocation = App.Settings.Prop.ShowServerDetailsUI;
		private readonly bool _fullSurface;

        private const double DefaultBrightness = 50;
        private double _brightness = App.Settings.Prop.Brightness;
        private double _lastAppliedBrightness = App.Settings.Prop.Brightness;

        private Border _darkOverlay;
        private Border _brightOverlay;

        private double _lastAppliedSaturation = App.Settings.Prop.Saturation;
        private double _lastAppliedContrast = App.Settings.Prop.Contrast;
        private double _lastAppliedColorTemperature = App.Settings.Prop.ColorTemperature;
        private bool _lastCbEnabled = App.Settings.Prop.ColorBlindnessEnabled;
        private int _lastCbType = App.Settings.Prop.ColorBlindnessType;
        private double _lastCbSeverity = App.Settings.Prop.ColorBlindnessSeverity;
        private bool _lastCbSimulate = App.Settings.Prop.ColorBlindnessSimulate;

        private string _serverIp;
        private string _lastServerIp;
        private bool _locationFetching;
		private string _serverLocation = "Location: unavailable";
        private int _networkUpdating;
        private int _networkUpdateCountdown;
        private bool _disposed;
		private string _lastTimeText = string.Empty;

        private static readonly Ping Ping = new Ping();

        public static bool SurfaceRequired
        {
            get
            {
                var settings = App.Settings.Prop;
                return settings.ServerPingCounter
                    || settings.CurrentTimeDisplay
                    || settings.ShowServerDetailsUI
                    || Math.Abs(settings.Brightness - DefaultBrightness) > 0.01;
            }
        }

		public bool MatchesCurrentSettings()
		{
			var settings = App.Settings.Prop;
			bool fullSurface = Math.Abs(settings.Brightness - DefaultBrightness) > 0.01;
			return settings.OverlaysEnabled
				&& _showPing == settings.ServerPingCounter
				&& _showTime == settings.CurrentTimeDisplay
				&& _showLocation == settings.ShowServerDetailsUI
				&& _fullSurface == fullSurface;
		}

        public OverlayWindow(ActivityWatcher? activityWatcher = null)
        {
			_activityWatcher = activityWatcher;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
			SnapsToDevicePixels = true;
			UseLayoutRounding = true;

            var root = new Grid();

            _darkOverlay = new Border
            {
                Background = Brushes.Black,
                Opacity = 0
            };
            _brightOverlay = new Border
            {
                Background = Brushes.White,
                Opacity = 0
            };

            root.Children.Add(_darkOverlay);
            root.Children.Add(_brightOverlay);

			_readoutPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
				Margin = new Thickness(0)
            };

            var readout = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0),
				Padding = new Thickness(12, 8, 12, 8),
				CornerRadius = new CornerRadius(8),
				Background = new SolidColorBrush(Color.FromArgb(228, 12, 13, 16)),
				BorderBrush = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)),
				BorderThickness = new Thickness(1),
				Child = _readoutPanel
            };

            if (_showPing)
            {
                _pingTextBlock = CreateTextBlock(Brushes.LightSkyBlue);
				_pingTextBlock.Text = "Ping: unavailable";
				_readoutPanel.Children.Add(_pingTextBlock);
            }

            if (_showLocation)
            {
                _locationTextBlock = CreateTextBlock(Brushes.LightGreen);
                _locationTextBlock.Text = _serverLocation;
				_readoutPanel.Children.Add(_locationTextBlock);
            }

            if (_showTime)
            {
                _timeTextBlock = CreateTextBlock(Brushes.Cyan);
				_lastTimeText = ClockDisplay.Format(DateTime.UtcNow);
				_timeTextBlock.Text = _lastTimeText;
				_readoutPanel.Children.Add(_timeTextBlock);
            }

            if (_showPing || _showLocation || _showTime)
                root.Children.Add(readout);
            Content = root;

            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _updateTimer.Tick += OnUpdateTimerTick;

			_fullSurface = Math.Abs(_brightness - DefaultBrightness) > 0.01;
			if (!_fullSurface)
            {
				Width = 360;
				Height = 18 + new[] { _showPing, _showLocation, _showTime }.Count(value => value) * 25;
            }
			_anchor = new RobloxOverlayAnchor(this, placement: _fullSurface ? RobloxOverlayPlacement.Fill : RobloxOverlayPlacement.TopRight);

            Loaded += OnOverlayLoaded;
			IsVisibleChanged += OnOverlayVisibilityChanged;
            Closed += OnUIWindowClosed;
        }

        private void OnOverlayLoaded(object? sender, RoutedEventArgs e)
        {
            MakeClickThrough();
            ApplyBrightness();
            ApplyColorEffects();
			UpdateActiveState(IsVisible);
        }

		private void OnOverlayVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			UpdateActiveState(e.NewValue is true);
		}

		private void UpdateActiveState(bool active)
		{
			if (_disposed)
				return;
			if (active)
			{
				if (!_updateTimer.IsEnabled)
					_updateTimer.Start();
				return;
			}
			_updateTimer.Stop();
		}

        private void OnUpdateTimerTick(object? sender, EventArgs e)
        {
            if (_disposed)
                return;
            try
            {
                UpdateEffects();
                UpdateReadouts();
                if ((_showPing || _showLocation) && _networkUpdateCountdown-- <= 0)
                {
                    _networkUpdateCountdown = 3;
                    _ = UpdateNetworkStatsGuardedAsync();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayWindow::Update", ex);
            }
        }

        private async Task UpdateNetworkStatsGuardedAsync()
        {
            if (_disposed || Interlocked.Exchange(ref _networkUpdating, 1) != 0)
                return;
            try
            {
                await UpdateNetworkStatsAsync();
            }
			catch (OperationCanceledException) when (_disposed)
			{
			}
            catch (Exception ex)
            {
                App.Logger.WriteException("OverlayWindow::NetworkUpdate", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _networkUpdating, 0);
            }
        }

        private void OnUIWindowClosed(object? sender, EventArgs e)
        {
			if (_disposed)
				return;
            _disposed = true;
			_lifetimeCts.Cancel();
            _updateTimer.Stop();
            _updateTimer.Tick -= OnUpdateTimerTick;
            _anchor.Dispose();
			_lifetimeCts.Dispose();
            Loaded -= OnOverlayLoaded;
			IsVisibleChanged -= OnOverlayVisibilityChanged;
            Closed -= OnUIWindowClosed;
            if (Application.Current?.Resources["OverlayWindow"] is OverlayWindow overlay && ReferenceEquals(overlay, this))
                Application.Current.Resources.Remove("OverlayWindow");
            try
            {
                Voidstrap.Utility.ScreenColorEffect.Reset();
            }
            catch
            {
            }
			PropertyChanged = null;
        }

        public double Brightness
        {
            get => _brightness;
            set
            {
                double clamped = Math.Clamp(value, 0, 100);
                if (_brightness != clamped)
                {
                    _brightness = clamped;
                    App.Settings.Prop.Brightness = clamped;
                    ApplyBrightness();
                    OnPropertyChanged(nameof(Brightness));
                }
            }
        }

        private void ApplyBrightness()
        {
            if (_brightness == DefaultBrightness)
            {
                _darkOverlay.Opacity = 0;
                _brightOverlay.Opacity = 0;
                return;
            }

            if (_brightness < DefaultBrightness)
            {
                double percent = (DefaultBrightness - _brightness) / DefaultBrightness;
                _darkOverlay.Opacity = percent;
                _brightOverlay.Opacity = 0;
            }
            else
            {
                double percent = (_brightness - DefaultBrightness) / DefaultBrightness;
                _brightOverlay.Opacity = percent;
                _darkOverlay.Opacity = 0;
            }
        }

        private void ApplyColorEffects()
        {
            Voidstrap.Utility.ScreenColorEffect.ApplyConfigured();
        }

        private void UpdateEffects()
        {
            if (Math.Abs(App.Settings.Prop.Brightness - _lastAppliedBrightness) > 0.01)
            {
                _lastAppliedBrightness = App.Settings.Prop.Brightness;
                Brightness = _lastAppliedBrightness;
            }

            bool colorChanged = false;
            if (Math.Abs(App.Settings.Prop.Saturation - _lastAppliedSaturation) > 0.01)
            {
                _lastAppliedSaturation = App.Settings.Prop.Saturation;
                colorChanged = true;
            }
            if (Math.Abs(App.Settings.Prop.Contrast - _lastAppliedContrast) > 0.01)
            {
                _lastAppliedContrast = App.Settings.Prop.Contrast;
                colorChanged = true;
            }
            if (Math.Abs(App.Settings.Prop.ColorTemperature - _lastAppliedColorTemperature) > 0.01)
            {
                _lastAppliedColorTemperature = App.Settings.Prop.ColorTemperature;
                colorChanged = true;
            }
            if (App.Settings.Prop.ColorBlindnessEnabled != _lastCbEnabled)
            {
                _lastCbEnabled = App.Settings.Prop.ColorBlindnessEnabled;
                colorChanged = true;
            }
            if (App.Settings.Prop.ColorBlindnessType != _lastCbType)
            {
                _lastCbType = App.Settings.Prop.ColorBlindnessType;
                colorChanged = true;
            }
            if (Math.Abs(App.Settings.Prop.ColorBlindnessSeverity - _lastCbSeverity) > 0.01)
            {
                _lastCbSeverity = App.Settings.Prop.ColorBlindnessSeverity;
                colorChanged = true;
            }
            if (App.Settings.Prop.ColorBlindnessSimulate != _lastCbSimulate)
            {
                _lastCbSimulate = App.Settings.Prop.ColorBlindnessSimulate;
                colorChanged = true;
            }
            if (colorChanged)
            {
                ApplyColorEffects();
            }
        }

        private void UpdateReadouts()
        {
            if (_disposed)
                return;

            if (!_showTime || _timeTextBlock == null)
                return;

            string timeText = ClockDisplay.Format(DateTime.UtcNow);
            if (!string.Equals(timeText, _lastTimeText, StringComparison.Ordinal))
            {
                _lastTimeText = timeText;
                _timeTextBlock.Text = timeText;
            }
        }

        private async Task UpdateNetworkStatsAsync()
        {
            if (_disposed)
                return;

            if (!_showPing && !_showLocation)
                return;

			string activityIp = _activityWatcher?.Data?.MachineAddressValid == true ? _activityWatcher.Data.MachineAddress : "";
			if (!string.IsNullOrEmpty(activityIp))
			{
				_serverIp = activityIp;
			}

            if (string.IsNullOrEmpty(_serverIp))
            {
                if (_showPing)
					_pingTextBlock.Text = "Ping: unavailable";
                if (_showLocation)
					_locationTextBlock.Text = "Location: unavailable";
                return;
            }

            if (_showPing)
            {
                int ping = await PingServerAsync(_serverIp, _lifetimeCts.Token);
                if (_disposed)
                    return;
				_pingTextBlock.Text = ping > 0 ? $"Ping: {ping} ms" : "Ping: unavailable";
            }

            if (_showLocation && !_locationFetching && _serverIp != _lastServerIp)
            {
                string serverIp = _serverIp;
                _lastServerIp = serverIp;
                _locationFetching = true;
				_locationTextBlock.Text = "Location: unavailable";
				try
				{
					string location = await GetServerLocationAsync(serverIp, _lifetimeCts.Token);
					if (_disposed)
						return;
					_serverLocation = location;
					_locationTextBlock.Text = location;
				}
				finally
				{
					_locationFetching = false;
				}
            }
        }

        private static async Task<int> PingServerAsync(string ip, CancellationToken token)
        {
            try
            {
				var reply = await Ping.SendPingAsync(ip, 1000).WaitAsync(token);
                return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
            }
			catch (OperationCanceledException)
			{
				throw;
			}
            catch { return -1; }
        }

        private async Task<string> GetServerLocationAsync(string ip, CancellationToken token)
        {
            try
            {
				if (_activityWatcher?.InGame == true && _activityWatcher.Data.PlaceId > 0 && !string.IsNullOrWhiteSpace(_activityWatcher.Data.JobId))
				{
					string? exact = await _activityWatcher.Data.QueryServerLocation(token);
					if (!string.IsNullOrWhiteSpace(exact))
						return "Location: " + exact;
				}
				var dc = await Voidstrap.Integrations.VoidstrapMatchmaker.LookupUnknownIpAsync(ip, token);
                if (dc == null || string.IsNullOrEmpty(dc.City)) return "Location: Unknown";
                string country = Voidstrap.Integrations.VoidstrapMatchmaker.CountryToDisplayName(dc.Country);
                return string.IsNullOrEmpty(country) || string.Equals(country, dc.City, StringComparison.OrdinalIgnoreCase)
                    ? "Location: " + dc.City
                    : "Location: " + dc.City + ", " + country;
            }
			catch (OperationCanceledException) { return "Location: Unknown"; }
            catch { return "Location: Unknown"; }
        }

        private static TextBlock CreateTextBlock(Brush color) => new()
        {
			FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text"),
            Foreground = color,
			TextWrapping = TextWrapping.NoWrap,
			TextTrimming = TextTrimming.None,
			LineHeight = 23,
			Margin = new Thickness(0, 1, 0, 1)
        };

        private void MakeClickThrough()
        {
            if (Voidstrap.Utility.Platform.IsLinux)
            {
            	Voidstrap.Integrations.Overlays.LinuxOverlaySurface.MakeClickThrough(this);
            	return;
            }

            var hwnd = new WindowInteropHelper(this).Handle;
            int style = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
