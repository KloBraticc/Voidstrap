using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Voidstrap.Utility
{
    public class DisplayInfo
    {
        public string DeviceName { get; set; } = string.Empty;

        public string FriendlyName { get; set; } = string.Empty;

        public bool IsPrimary { get; set; }

        public int X { get; set; }

        public int Y { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public int RefreshRate { get; set; }

        public int Number { get; set; }
    }

    public static class DisplaySystem
    {
        public const int Success = 0;

        private const int ENUM_CURRENT_SETTINGS = -1;

        private const uint CDS_UPDATEREGISTRY = 0x1;

        private const uint CDS_TEST = 0x2;

        private const int DISP_CHANGE_FAILED = -1;

        private const uint DM_PELSWIDTH = 0x80000;

        private const uint DM_PELSHEIGHT = 0x100000;

        private const uint DM_DISPLAYFREQUENCY = 0x400000;

        private const uint DM_INTERLACED = 0x2;

        private const uint DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

        private const uint DISPLAY_DEVICE_PRIMARY_DEVICE = 0x4;

        private const uint DISPLAY_DEVICE_MIRRORING_DRIVER = 0x8;

        private const uint SWP_NOACTIVATE = 0x10;

        private const uint SWP_SHOWWINDOW = 0x40;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public uint cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private static DEVMODE NewDevMode()
        {
            return new DEVMODE
            {
                dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE))
            };
        }

        private static DISPLAY_DEVICE NewDisplayDevice()
        {
            return new DISPLAY_DEVICE
            {
                cb = (uint)Marshal.SizeOf(typeof(DISPLAY_DEVICE))
            };
        }

        private static string? Normalize(string? deviceName)
        {
            return string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
        }

        public static List<DisplayInfo> GetDisplays()
        {
            List<DisplayInfo> list = new List<DisplayInfo>();
            if (!Platform.IsWindows)
            {
                (double portableWidth, double portableHeight) = ScreenMetrics.GetPrimary();
                list.Add(new DisplayInfo
                {
                    DeviceName = string.Empty,
                    FriendlyName = "Primary Display",
                    IsPrimary = true,
                    X = 0,
                    Y = 0,
                    Width = (int)portableWidth,
                    Height = (int)portableHeight,
                    RefreshRate = 60,
                    Number = 1
                });
                return list;
            }
            try
            {
                for (uint i = 0; ; i++)
                {
                    DISPLAY_DEVICE adapter = NewDisplayDevice();
                    if (!EnumDisplayDevices(null, i, ref adapter, 0))
                    {
                        break;
                    }
                    if ((adapter.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0 || (adapter.StateFlags & DISPLAY_DEVICE_MIRRORING_DRIVER) != 0)
                    {
                        continue;
                    }
                    DEVMODE dm = NewDevMode();
                    if (!EnumDisplaySettings(adapter.DeviceName, ENUM_CURRENT_SETTINGS, ref dm))
                    {
                        continue;
                    }
                    string friendly = adapter.DeviceString;
                    DISPLAY_DEVICE monitor = NewDisplayDevice();
                    if (EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0) && !string.IsNullOrWhiteSpace(monitor.DeviceString))
                    {
                        friendly = monitor.DeviceString;
                    }
                    list.Add(new DisplayInfo
                    {
                        DeviceName = adapter.DeviceName,
                        FriendlyName = friendly,
                        IsPrimary = (adapter.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0,
                        X = dm.dmPositionX,
                        Y = dm.dmPositionY,
                        Width = (int)dm.dmPelsWidth,
                        Height = (int)dm.dmPelsHeight,
                        RefreshRate = (int)dm.dmDisplayFrequency,
                        Number = list.Count + 1
                    });
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("DisplaySystem", "Failed to enumerate displays: " + ex.Message);
            }
            if (list.Count == 0)
            {
                DisplayMode? current = GetCurrentMode(null);
                list.Add(new DisplayInfo
                {
                    DeviceName = string.Empty,
                    FriendlyName = "Primary Display",
                    IsPrimary = true,
                    X = 0,
                    Y = 0,
                    Width = current?.Width ?? 1920,
                    Height = current?.Height ?? 1080,
                    RefreshRate = current?.RefreshRate ?? 60,
                    Number = 1
                });
            }
            return list;
        }

        public static List<DisplayMode> GetModes(string? deviceName)
        {
            if (!Platform.IsWindows)
            {
                DisplayMode? portable = GetCurrentMode(deviceName);
                return portable == null ? new List<DisplayMode>() : new List<DisplayMode> { portable };
            }
            string? device = Normalize(deviceName);
            List<DisplayMode> list = new List<DisplayMode>();
            DEVMODE dm = NewDevMode();
            int index = 0;
            while (EnumDisplaySettings(device, index++, ref dm))
            {
                if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0 || dm.dmDisplayFrequency <= 1)
                {
                    continue;
                }
                if ((dm.dmDisplayFlags & DM_INTERLACED) != 0)
                {
                    continue;
                }
                uint w = dm.dmPelsWidth;
                uint h = dm.dmPelsHeight;
                uint hz = dm.dmDisplayFrequency;
                if (!list.Any((DisplayMode m) => m.Width == w && m.Height == h && m.RefreshRate == hz))
                {
                    list.Add(new DisplayMode
                    {
                        Width = (int)w,
                        Height = (int)h,
                        RefreshRate = (int)hz
                    });
                }
            }
            return list.OrderByDescending((DisplayMode m) => m.Width).ThenByDescending((DisplayMode m) => m.Height).ThenByDescending((DisplayMode m) => m.RefreshRate).ToList();
        }

        public static DisplayMode? GetCurrentMode(string? deviceName)
        {
            if (!Platform.IsWindows)
            {
                (double portableWidth, double portableHeight) = ScreenMetrics.GetPrimary();
                return new DisplayMode
                {
                    Width = (int)portableWidth,
                    Height = (int)portableHeight,
                    RefreshRate = 60
                };
            }
            string? device = Normalize(deviceName);
            DEVMODE dm = NewDevMode();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm))
            {
                if (device == null)
                {
                    return null;
                }
                dm = NewDevMode();
                if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
                {
                    return null;
                }
            }
            return new DisplayMode
            {
                Width = (int)dm.dmPelsWidth,
                Height = (int)dm.dmPelsHeight,
                RefreshRate = (int)dm.dmDisplayFrequency
            };
        }

        public static int ApplyMode(string? deviceName, int width, int height, int refreshRate)
        {
            if (!Platform.IsWindows)
            {
                return DISP_CHANGE_FAILED;
            }
            string? device = Normalize(deviceName);
            DEVMODE dm = NewDevMode();
            if (!EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm))
            {
                device = null;
                dm = NewDevMode();
                if (!EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
                {
                    return DISP_CHANGE_FAILED;
                }
            }
            if (dm.dmPelsWidth == (uint)width && dm.dmPelsHeight == (uint)height && dm.dmDisplayFrequency == (uint)refreshRate)
            {
                return Success;
            }
            dm.dmPelsWidth = (uint)width;
            dm.dmPelsHeight = (uint)height;
            dm.dmDisplayFrequency = (uint)refreshRate;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            int test = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_TEST, IntPtr.Zero);
            if (test != Success)
            {
                return test;
            }
            return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        }

        public static string DescribeError(int code)
        {
            return code switch
            {
                1 => "The computer must be restarted for the change to take effect.",
                -1 => "The display driver rejected the request.",
                -2 => "This resolution is not supported by the display.",
                -3 => "The settings could not be written.",
                -5 => "Invalid display settings were provided.",
                -6 => "The system is in a dual view state that prevents the change.",
                _ => $"Display change failed with code {code}."
            };
        }

        public static void IdentifyDisplays()
        {
            List<DisplayInfo> displays = GetDisplays();
            List<Window> windows = new List<Window>();
            foreach (DisplayInfo display in displays)
            {
                try
                {
                    Border content = new Border
                    {
                        CornerRadius = new CornerRadius(10),
                        Background = new SolidColorBrush(Color.FromArgb(232, 26, 27, 32)),
                        Child = new TextBlock
                        {
                            Text = display.Number.ToString(),
                            FontSize = 64,
                            FontWeight = FontWeights.Bold,
                            Foreground = Brushes.White,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    };
                    Window window = new Window
                    {
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize,
                        AllowsTransparency = true,
                        Background = Brushes.Transparent,
                        Topmost = true,
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Focusable = false,
                        IsHitTestVisible = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Width = 130,
                        Height = 130,
                        Content = content
                    };
                    window.Show();
                    IntPtr hwnd = new WindowInteropHelper(window).Handle;
                    int x = display.X + 24;
                    int y = Math.Max(display.Y + 8, display.Y + display.Height - 154);
                    SetWindowPos(hwnd, HWND_TOPMOST, x, y, 130, 130, SWP_NOACTIVATE | SWP_SHOWWINDOW);
                    windows.Add(window);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine("DisplaySystem", "Identify overlay failed: " + ex.Message);
                }
            }
            if (windows.Count > 0)
            {
                new IdentifySession(windows);
            }
        }

        private sealed class IdentifySession
        {
            private readonly List<Window> _windows;

            private readonly DispatcherTimer _timer;

            public IdentifySession(List<Window> windows)
            {
                _windows = windows;
                _timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1800)
                };
                _timer.Tick += OnTick;
                _timer.Start();
            }

            private void OnTick(object? sender, EventArgs e)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
                foreach (Window window in _windows)
                {
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                    }
                }
                _windows.Clear();
            }
        }
    }
}
