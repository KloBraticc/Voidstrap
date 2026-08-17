using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Voidstrap.Integrations.RiShade
{
    public static class RiShadePanel
    {
        private static Window? _window;
        private static IntPtr _hwnd;

        public static IntPtr CurrentHwnd => _hwnd;

        public static bool IsOpen => _window != null;

        public static event Action<bool>? OpenChanged;

        public static void Toggle(bool fromUi = false)
        {
            var app = Application.Current;
            if (app == null)
                return;
            app.Dispatcher.BeginInvoke((Action)delegate
            {
                try
                {
                    if (_window != null)
                    {
                        _window.Close();
                        return;
                    }
                    _window = BuildWindow(fromUi);
                    _window.Show();
                    _window.Activate();
                    OpenChanged?.Invoke(true);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("RiShadePanel::Toggle", ex);
                }
            });
        }

        private static Window BuildWindow(bool fromUi)
        {
            var header = new DockPanel { Margin = new Thickness(12, 10, 8, 6) };
            var title = new TextBlock
            {
                Text = "RiShade",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            };
            header.Children.Add(title);
            if (!fromUi)
            {
                var hint = new TextBlock
                {
                    Text = "F8 closes this panel",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 2, 0, 0),
                };
                header.Children.Add(hint);
            }

            var controls = new Voidstrap.UI.Elements.Controls.RiShadeControls { Margin = new Thickness(6, 0, 6, 8) };
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = controls,
            };
            DockPanel.SetDock(header, Dock.Top);
            var root = new DockPanel();
            root.Children.Add(header);
            root.Children.Add(scroll);

            var border = new System.Windows.Controls.Border
            {
                Background = BuildThemeBackground(),
                BorderBrush = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = root,
            };

            var s = RiShadeSettings.Current;
            var window = new Window
            {
                Title = "RiShade",
                Width = Math.Clamp(s.PanelW, 320, 900),
                Height = Math.Clamp(s.PanelH, 300, 1200),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.CanResizeWithGrip,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = border,
            };
            ApplyDock(window, s);
            window.MouseLeftButtonDown += Window_MouseLeftButtonDown;
            window.Loaded += Window_Loaded;
            window.Closed += Window_Closed;
            return window;
        }

        private static void ApplyDock(Window w, RiShadeSettings s)
        {
            var wa = SystemParameters.WorkArea;
            switch (s.PanelDock)
            {
                case 1:
                    w.Left = wa.Left + 8;
                    w.Top = wa.Top + (wa.Height - w.Height) / 2;
                    break;
                case 2:
                    w.Left = wa.Left + (wa.Width - w.Width) / 2;
                    w.Top = wa.Top + 8;
                    break;
                case 3:
                    w.Left = wa.Right - w.Width - 8;
                    w.Top = wa.Top + (wa.Height - w.Height) / 2;
                    break;
                case 4:
                    w.Left = wa.Left + (wa.Width - w.Width) / 2;
                    w.Top = wa.Bottom - w.Height - 8;
                    break;
                case 5:
                    w.Left = wa.Left + (wa.Width - w.Width) / 2;
                    w.Top = wa.Top + (wa.Height - w.Height) / 2;
                    break;
                default:
                    if (s.PanelX >= wa.Left - 50 && s.PanelX < wa.Right && s.PanelY >= wa.Top - 50 && s.PanelY < wa.Bottom)
                    {
                        w.Left = s.PanelX;
                        w.Top = s.PanelY;
                    }
                    else
                    {
                        w.Left = wa.Left + (wa.Width - w.Width) / 2;
                        w.Top = wa.Top + (wa.Height - w.Height) / 2;
                    }
                    break;
            }
        }

        private static void SnapAndSave(Window w)
        {
            var wa = SystemParameters.WorkArea;
            const double edge = 48;
            var s = RiShadeSettings.Current;
            double dLeft = w.Left - wa.Left;
            double dRight = wa.Right - (w.Left + w.Width);
            double dTop = w.Top - wa.Top;
            double dBottom = wa.Bottom - (w.Top + w.Height);
            double cx = w.Left + w.Width / 2 - (wa.Left + wa.Width / 2);
            double cy = w.Top + w.Height / 2 - (wa.Top + wa.Height / 2);
            int dock = 0;
            if (Math.Abs(cx) < 80 && Math.Abs(cy) < 80)
                dock = 5;
            else
            {
                double best = edge;
                if (dLeft < best) { best = dLeft; dock = 1; }
                if (dTop < best) { best = dTop; dock = 2; }
                if (dRight < best) { best = dRight; dock = 3; }
                if (dBottom < best) { dock = 4; }
            }
            s.PanelDock = dock;
            if (dock == 0)
            {
                s.PanelX = w.Left;
                s.PanelY = w.Top;
            }
            else
            {
                ApplyDock(w, s);
            }
            s.PanelW = w.Width;
            s.PanelH = w.Height;
            RiShadeSettings.Touch();
        }

        private static Brush BuildThemeBackground()
        {
            try
            {
                var app = Application.Current;
                if (app != null
                    && app.TryFindResource("WindowBackgroundColorPrimary") is Color primary
                    && app.TryFindResource("WindowBackgroundColorSecondary") is Color secondary
                    && app.TryFindResource("WindowBackgroundColorThird") is Color third)
                {
                    var brush = new LinearGradientBrush
                    {
                        StartPoint = new Point(1.0, 1.0),
                        EndPoint = new Point(0.0, 0.0),
                    };
                    primary.A = 245;
                    secondary.A = 245;
                    third.A = 245;
                    brush.GradientStops.Add(new GradientStop(primary, 0.0));
                    brush.GradientStops.Add(new GradientStop(secondary, 0.8));
                    brush.GradientStops.Add(new GradientStop(third, 1.1));
                    brush.Freeze();
                    return brush;
                }
            }
            catch
            {
            }
            return new SolidColorBrush(Color.FromArgb(242, 22, 24, 29));
        }

        private static void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ButtonState == MouseButtonState.Pressed && sender is Window w)
                {
                    w.DragMove();
                    SnapAndSave(w);
                }
            }
            catch
            {
            }
        }

        private static void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window w)
            {
                IntPtr h = new WindowInteropHelper(w).Handle;
                RiShadeInterop.SetWindowDisplayAffinity(h, RiShadeInterop.WDA_EXCLUDEFROMCAPTURE);
                Interlocked.Exchange(ref _hwnd, h);
            }
        }

        private static void Window_Closed(object? sender, EventArgs e)
        {
            if (sender is Window w)
            {
                try
                {
                    var s = RiShadeSettings.Current;
                    s.PanelW = w.Width;
                    s.PanelH = w.Height;
                    if (s.PanelDock == 0)
                    {
                        s.PanelX = w.Left;
                        s.PanelY = w.Top;
                    }
                    RiShadeSettings.Touch();
                }
                catch
                {
                }
                w.MouseLeftButtonDown -= Window_MouseLeftButtonDown;
                w.Loaded -= Window_Loaded;
                w.Closed -= Window_Closed;
            }
            Interlocked.Exchange(ref _hwnd, IntPtr.Zero);
            _window = null;
            OpenChanged?.Invoke(false);
        }
    }
}
