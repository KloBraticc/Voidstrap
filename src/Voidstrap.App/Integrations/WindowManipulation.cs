using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Windows.Win32.UI.Accessibility;
using Voidstrap.Extensions;
using Voidstrap.Models.Entities;

namespace Voidstrap.Integrations
{
    public class WindowManipulation : IDisposable
    {
        private WINEVENTPROC? _setTitleHook;
        private Windows.Win32.UnhookWinEventSafeHandle? _winEventHook;
        private bool _disposed;

        private HWND _hWnd;
        private uint _robloxPID;

        private volatile string _currentDesiredTitle;

        private readonly ActivityWatcher? _activityWatcher;

        private long _lastTitleSetTicks;
        private int _titleRestorePending;
        private static readonly TimeSpan TitleSetThrottle = TimeSpan.FromMilliseconds(100);

        public WindowManipulation(long windowHandle, long robloxProcessId, ActivityWatcher? activityWatcher = null)
        {
            const string LOG_IDENT = "WindowManipulation";

            App.Logger.WriteLine(LOG_IDENT, $"Got window handle as {windowHandle}");
            _hWnd = (HWND)(IntPtr)windowHandle;
            _robloxPID = (uint)robloxProcessId;
            _activityWatcher = activityWatcher;

            _currentDesiredTitle = string.IsNullOrWhiteSpace(App.Settings.Prop.RobloxTitle)
                ? "Voidstrap"
                : App.Settings.Prop.RobloxTitle;
        }

        public void Start()
        {
            if (App.Settings.Prop.FakeBorderlessFullscreen)
                FakeBorderless();

            ApplyWindowModifications();
            ApplyWindowBackdrop();

            if (_activityWatcher != null && App.Settings.Prop.CycleTitleWithGameName)
            {
                _activityWatcher.OnGameJoin += OnGameJoin;
                _activityWatcher.OnGameLeave += OnGameLeave;

                if (_activityWatcher.InGame && _activityWatcher.Data?.UniverseId > 0)
                    _ = Task.Run(() => UpdateTitleWithGameNameAsync(_activityWatcher.Data.UniverseId));
            }
        }

        private void FakeBorderless()
        {
            const string LOG_IDENT = "WindowManipulation::BorderlessFullscreen";
            App.Logger.WriteLine(LOG_IDENT, "Setting Roblox to borderless fullscreen");

            const int GWLSTYLE = -16;

            int style = PInvoke.GetWindowLong(_hWnd, (WINDOW_LONG_PTR_INDEX)GWLSTYLE);

            const int WS_CAPTION = 0x00C00000;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_MINIMIZEBOX = 0x00020000;
            const int WS_MAXIMIZEBOX = 0x00010000;
            const int WS_SYSMENU = 0x00080000;

            style &= ~WS_CAPTION;
            style &= ~WS_THICKFRAME;
            style &= ~WS_MINIMIZEBOX;
            style &= ~WS_MAXIMIZEBOX;
            style &= ~WS_SYSMENU;

            Rectangle resolution;
            try
            {
                resolution = Screen.FromHandle((IntPtr)_hWnd).Bounds;
            }
            catch
            {
                resolution = Screen.PrimaryScreen.Bounds;
            }

            PInvoke.SetWindowLong(_hWnd, (WINDOW_LONG_PTR_INDEX)GWLSTYLE, style);

            PInvoke.SetWindowPos(_hWnd, (HWND)IntPtr.Zero, resolution.X, resolution.Y, resolution.Width, resolution.Height + 1, SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
        }

        private void ApplyWindowModifications()
        {
            const string LOG_IDENT = "WindowManipulation::ApplyWindowModifications";
            const int WINEVENT_OUTOFCONTEXT = 0x0;
            const int EVENT_OBJECT_NAMECHANGE = 0x800C;
            const int WM_SETICON = 0x0080;

            App.Logger.WriteLine(LOG_IDENT, "Applying window modifications");

            _setTitleHook = new(SetWindowTitleHook);

            try
            {
                App.Logger.WriteLine(LOG_IDENT, "Setting Roblox icon");
                using (var icon = GetWindowIcon())
                {
                    IntPtr hIconCopy = PInvoke.CopyIcon((HICON)icon.Handle);
                    PInvoke.SendMessage(_hWnd, WM_SETICON, 0, hIconCopy);
                    PInvoke.SendMessage(_hWnd, WM_SETICON, 1, hIconCopy);
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Failed to set the Roblox icon: " + ex.Message);
            }

            App.Logger.WriteLine(LOG_IDENT, $"Setting Roblox title to '{_currentDesiredTitle}'");

            PInvoke.SetWindowText(_hWnd, _currentDesiredTitle);

            var dispatcher = App.Current?.Dispatcher;
            if (dispatcher != null)
            {
                dispatcher.InvokeAsync(() =>
                {
                    if (_disposed)
                        return;
                    _winEventHook = PInvoke.SetWinEventHook(
                        EVENT_OBJECT_NAMECHANGE,
                        EVENT_OBJECT_NAMECHANGE,
                        null,
                        _setTitleHook,
                        _robloxPID,
                        0,
                        WINEVENT_OUTOFCONTEXT);
                    PInvoke.SetWindowText(_hWnd, _currentDesiredTitle);
                });
            }
            else
            {
                _winEventHook = PInvoke.SetWinEventHook(
                    EVENT_OBJECT_NAMECHANGE,
                    EVENT_OBJECT_NAMECHANGE,
                    null,
                    _setTitleHook,
                    _robloxPID,
                    0,
                    WINEVENT_OUTOFCONTEXT);
                PInvoke.SetWindowText(_hWnd, _currentDesiredTitle);
            }
        }

        private async void OnGameJoin(object? sender, EventArgs e)
        {
            try
            {
                if (_disposed || _activityWatcher == null || !App.Settings.Prop.CycleTitleWithGameName)
                    return;

                long universeId = _activityWatcher.Data?.UniverseId ?? 0;
                if (universeId <= 0)
                    return;

                await UpdateTitleWithGameNameAsync(universeId);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("WindowManipulation::OnGameJoin", ex);
            }
        }

        private void OnGameLeave(object? sender, EventArgs e)
        {
            if (_disposed || !App.Settings.Prop.CycleTitleWithGameName)
                return;

            string baseTitle = string.IsNullOrWhiteSpace(App.Settings.Prop.RobloxTitle)
                ? "Voidstrap"
                : App.Settings.Prop.RobloxTitle;
            App.Logger.WriteLine("WindowManipulation::OnGameLeave", $"Reverting title to '{baseTitle}'");

            _currentDesiredTitle = baseTitle;
            SetTitleSafe(baseTitle);
        }

        private async Task UpdateTitleWithGameNameAsync(long universeId)
        {
            const string LOG_IDENT = "WindowManipulation::UpdateTitleWithGameName";

            try
            {
                string baseTitle = string.IsNullOrWhiteSpace(App.Settings.Prop.RobloxTitle)
                    ? "Voidstrap"
                    : App.Settings.Prop.RobloxTitle;

                App.Logger.WriteLine(LOG_IDENT, $"Fetching game name for universe {universeId}");

                var details = UniverseDetails.LoadFromCache(universeId);
                if (details == null)
                {
                    await UniverseDetails.FetchSingle(universeId).ConfigureAwait(false);
                    details = UniverseDetails.LoadFromCache(universeId);
                }

                if (_disposed)
                    return;

                string gameName = details?.Data?.Name ?? string.Empty;

                if (string.IsNullOrWhiteSpace(gameName))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Game name is empty, keeping base title");
                    return;
                }

                string newTitle = string.IsNullOrEmpty(baseTitle)
                    ? gameName
                    : $"{baseTitle}: {gameName}";

                if (App.Settings.Prop.ShowServerInfoInTitle)
                {
                    long playing = details?.Data?.Playing ?? 0;
                    if (playing > 0)
                        newTitle += $" ({playing:N0} playing)";
                }

                App.Logger.WriteLine(LOG_IDENT, $"Updating window title to '{newTitle}'");
                _currentDesiredTitle = newTitle;

                SetTitleSafe(newTitle);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        private void SetTitleSafe(string title)
        {
            Interlocked.Exchange(ref _lastTitleSetTicks, DateTime.UtcNow.Ticks);
            PInvoke.SetWindowText(_hWnd, title);
        }

        private void SetWindowTitleHook(HWINEVENTHOOK hWinEventHook, uint iEvent, HWND hWnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            const string LOG_IDENT = "WindowManipulation::SetWindowTitleHook";

            if (_disposed || hWnd != _hWnd || idObject != 0)
                return;

            Span<char> titleBuffer = stackalloc char[256];
            int length = PInvoke.GetWindowText(_hWnd, titleBuffer);
            string currentTitle = length > 0 ? new string(titleBuffer.Slice(0, length)) : string.Empty;

            string desired = _currentDesiredTitle;
            if (currentTitle == desired)
                return;

            var now = DateTime.UtcNow;
            var lastSet = new DateTime(Interlocked.Read(ref _lastTitleSetTicks), DateTimeKind.Utc);
            if (now - lastSet < TitleSetThrottle)
            {
                if (Interlocked.CompareExchange(ref _titleRestorePending, 1, 0) == 0)
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(TitleSetThrottle);
                        Interlocked.Exchange(ref _titleRestorePending, 0);
                        if (!_disposed)
                            SetTitleSafe(_currentDesiredTitle);
                    });
                }
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Title changed to '{currentTitle}', restoring to '{desired}'");
            SetTitleSafe(desired);
        }

        private void ApplyWindowBackdrop()
        {
            const string LOG_IDENT = "WindowManipulation::ApplyWindowBackdrop";

            int backdropType = App.Settings.Prop.RobloxWindowBackdropType;
            if (backdropType <= 0 || backdropType == 1)
                return;

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                App.Logger.WriteLine(LOG_IDENT, "Windows 11 or later is required for window backdrop effects");
                return;
            }

            try
            {
                IntPtr hwnd = (IntPtr)_hWnd;

                int darkMode = 1;
                DwmSetWindowAttributeInt(hwnd, 20, ref darkMode, sizeof(int));

                if (backdropType == 5)
                {
                    MARGINS margins = new MARGINS { Left = -1, Top = -1, Right = -1, Bottom = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);

                    int accentState = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134) ? 4 : 3;
                    int color = unchecked((int)0x40000000);

                    AccentPolicy policy = new AccentPolicy { State = accentState, Flags = 2, Color = color };
                    int policySize = Marshal.SizeOf<AccentPolicy>();
                    IntPtr policyPtr = Marshal.AllocHGlobal(policySize);
                    try
                    {
                        Marshal.StructureToPtr(policy, policyPtr, false);
                        WindowCompositionAttributeData data = new WindowCompositionAttributeData
                        {
                            Attribute = 19,
                            Data = policyPtr,
                            Size = policySize
                        };
                        if (SetWindowCompositionAttribute(hwnd, ref data) != 0)
                        {
                            App.Logger.WriteLine(LOG_IDENT, "Applied Aero glass blur backdrop to Roblox window");
                        }
                        else
                        {
                            App.Logger.WriteLine(LOG_IDENT, "SetWindowCompositionAttribute failed for Aero blur");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(policyPtr);
                    }
                    return;
                }

                MARGINS dwmMargins = new MARGINS { Left = -1, Top = -1, Right = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref dwmMargins);

                int dwmBackdrop = backdropType switch
                {
                    1 => 1,
                    2 => 2,
                    3 => 3,
                    4 => 4,
                    _ => 0
                };

                int hr = DwmSetWindowAttributeInt(hwnd, 38, ref dwmBackdrop, sizeof(int));

                if (hr == 0)
                {
                    string backdropName = backdropType switch
                    {
                        1 => "None",
                        2 => "Mica",
                        3 => "Acrylic",
                        4 => "Mica Alt",
                        _ => "Unknown"
                    };
                    App.Logger.WriteLine(LOG_IDENT, $"Applied {backdropName} backdrop to Roblox window");
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"DwmSetWindowAttribute returned 0x{hr:X8}");
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }



        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
        private static extern int DwmSetWindowAttributeInt(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int State;
            public int Flags;
            public int Color;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int Size;
        }

        private static System.Drawing.Icon GetWindowIcon()
        {
            BootstrapperIcon icon = App.Settings.Prop.RobloxIcon;
            if (icon == BootstrapperIcon.IconCustom && !string.IsNullOrEmpty(App.Settings.Prop.RobloxIconCustomLocation) && File.Exists(App.Settings.Prop.RobloxIconCustomLocation))
            {
                try
                {
                    return new System.Drawing.Icon(App.Settings.Prop.RobloxIconCustomLocation);
                }
                catch
                {
                }
            }
            return icon.GetIcon();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_activityWatcher != null)
            {
                _activityWatcher.OnGameJoin -= OnGameJoin;
                _activityWatcher.OnGameLeave -= OnGameLeave;
            }

            var hook = _winEventHook;
            _winEventHook = null;
            if (hook != null)
            {
                var dispatcher = App.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasShutdownStarted)
                {
                    dispatcher.InvokeAsync(hook.Dispose);
                }
                else
                {
                    hook.Dispose();
                }
            }

            GC.SuppressFinalize(this);
        }
    }
}
