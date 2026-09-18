using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

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
                    var window = new Voidstrap.UI.Elements.RiShade.RiShadeWindow();
                    ApplyBounds(window);
                    window.SourceInitialized += Window_SourceInitialized;
                    window.Closed += Window_Closed;
                    _window = window;
                    window.Show();
                    window.Activate();
                    OpenChanged?.Invoke(true);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("RiShadePanel::Toggle", ex);
                }
            });
        }

        private static void ApplyBounds(Window w)
        {
            var s = RiShadeSettings.Current;
            var wa = Voidstrap.Utility.ScreenMetrics.WorkArea;
            w.Width = Math.Clamp(s.PanelW, w.MinWidth, Math.Max(w.MinWidth, wa.Width));
            w.Height = Math.Clamp(s.PanelH, w.MinHeight, Math.Max(w.MinHeight, wa.Height));
            bool remembered = s.PanelDock == 0
                && s.PanelX >= wa.Left - 50 && s.PanelX + 120 < wa.Right
                && s.PanelY >= wa.Top - 10 && s.PanelY + 80 < wa.Bottom;
            if (remembered)
            {
                w.Left = s.PanelX;
                w.Top = s.PanelY;
            }
            else
            {
                w.Left = wa.Left + (wa.Width - w.Width) / 2;
                w.Top = wa.Top + (wa.Height - w.Height) / 2;
            }
        }

        private static void Window_SourceInitialized(object? sender, EventArgs e)
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
                    if (w.WindowState == System.Windows.WindowState.Normal)
                    {
                        var s = RiShadeSettings.Current;
                        s.PanelDock = 0;
                        s.PanelX = w.Left;
                        s.PanelY = w.Top;
                        s.PanelW = w.Width;
                        s.PanelH = w.Height;
                        RiShadeSettings.Touch();
                    }
                }
                catch
                {
                }
                w.SourceInitialized -= Window_SourceInitialized;
                w.Closed -= Window_Closed;
            }
            Interlocked.Exchange(ref _hwnd, IntPtr.Zero);
            _window = null;
            OpenChanged?.Invoke(false);
        }
    }
}
