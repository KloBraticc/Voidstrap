using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Voidstrap.UI.Elements.Settings
{
    public partial class DownloadProgressWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public DownloadProgressWindow(object dataContext)
        {
            Voidstrap.UI.RoundedWindowChrome.Prepare(this);
            InitializeComponent();
            DataContext = dataContext;
            Resources["AppAccentBrush"] = GetAccentBrush();
            SourceInitialized += OnSourceInitialized;
        }

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int useDark = App.Settings.Prop.Theme2.GetFinal() == Voidstrap.Enums.Theme.Light ? 0 : 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
            }
            catch
            {
            }
        }

        private static Brush GetAccentBrush()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key?.GetValue("AccentColor") is int abgr)
                {
                    byte r = (byte)(abgr & 0xFF);
                    byte g = (byte)((abgr >> 8) & 0xFF);
                    byte b = (byte)((abgr >> 16) & 0xFF);
                    var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
                    brush.Freeze();
                    return brush;
                }
            }
            catch
            {
            }
            try
            {
                var c = Voidstrap.Utility.SystemAccent.GetGlassColor();
                var glass = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                glass.Freeze();
                return glass;
            }
            catch
            {
            }
            var fallback = new SolidColorBrush(Color.FromRgb(255, 153, 0));
            fallback.Freeze();
            return fallback;
        }
    }
}
