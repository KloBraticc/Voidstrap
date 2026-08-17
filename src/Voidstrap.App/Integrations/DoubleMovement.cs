using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using Nefarius.ViGEm.Client.Targets.DualShock4;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Voidstrap.Integrations
{
    public class DoubleMovement : IDisposable
    {
        private volatile bool _w, _a, _s, _d;
        private long _tW, _tA, _tS, _tD;

        private double _fwdRad;
        private double _strRad;
        private double _sens;
        private string _curve = "Linear";
        private int _ctrlType;
        private int _socdMode;

        private readonly ViGEmClient? _client;
        private readonly IXbox360Controller? _x360;
        private readonly IDualShock4Controller? _ds4;
        private readonly UnhookWindowsHookExSafeHandle? _hook;
        private readonly HOOKPROC _hookProc;
        private readonly int _targetPid;

        private Thread? _updateThread;
        private readonly CancellationTokenSource _cts = new();
        private readonly AutoResetEvent _stateChanged = new(false);
        private bool _disposed;

        private float _curX, _curY;
        private bool _focused;
        private bool _submitEverFailed;

        public bool BusAvailable { get; private set; }
        public bool ControllerPlugged { get; private set; }
        public bool HookInstalled { get; private set; }

        private string[] _mouseBtnBind = new string[7];
        private string _scrollUpBind = "", _scrollDownBind = "";
        private float _mouseSens = 3.0f;

        private UnhookWindowsHookExSafeHandle? _mouseHook;
        private HOOKPROC _mouseHookProc;
        private int _prevMouseX, _prevMouseY;
        private float _targetRX, _targetRY, _curRX, _curRY;
        private int _scrollBi = -1;
        private long _scrollReleaseTime;
        private bool _sendRightStick;

        private const string Tag = "DM";

        private static readonly Dictionary<string, int> BtnIndex = new()
        {
            { "A", 0 }, { "B", 1 }, { "X", 2 }, { "Y", 3 },
            { "LeftShoulder", 4 }, { "RightShoulder", 5 },
            { "LeftTrigger", 6 }, { "RightTrigger", 7 },
            { "Back", 8 }, { "Start", 9 },
            { "LeftThumb", 10 }, { "RightThumb", 11 },
            { "DPadUp", 12 }, { "DPadDown", 13 },
            { "DPadLeft", 14 }, { "DPadRight", 15 },
            { "Guide", 16 },
        };

        private const int ButtonCount = 17;

        private Dictionary<Keys, string> _keyToButton = new();
        private bool[] _buttonStates = new bool[ButtonCount];
        private DualShock4DPadDirection _lastDpadDir = DualShock4DPadDirection.None;

        private static Xbox360Button ToX360Button(int idx) => idx switch
        {
            0 => Xbox360Button.A, 1 => Xbox360Button.B,
            2 => Xbox360Button.X, 3 => Xbox360Button.Y,
            4 => Xbox360Button.LeftShoulder, 5 => Xbox360Button.RightShoulder,
            8 => Xbox360Button.Back, 9 => Xbox360Button.Start,
            10 => Xbox360Button.LeftThumb, 11 => Xbox360Button.RightThumb,
            12 => Xbox360Button.Up, 13 => Xbox360Button.Down,
            14 => Xbox360Button.Left, 15 => Xbox360Button.Right,
            16 => Xbox360Button.Guide,
            _ => throw new ArgumentOutOfRangeException(nameof(idx)),
        };

        private static DualShock4Button ToDS4Button(int idx) => idx switch
        {
            0 => DualShock4Button.Cross, 1 => DualShock4Button.Circle,
            2 => DualShock4Button.Square, 3 => DualShock4Button.Triangle,
            4 => DualShock4Button.ShoulderLeft, 5 => DualShock4Button.ShoulderRight,
            8 => DualShock4Button.Share, 9 => DualShock4Button.Options,
            10 => DualShock4Button.ThumbLeft, 11 => DualShock4Button.ThumbRight,
            16 => DualShock4Button.Triangle,
            _ => throw new ArgumentOutOfRangeException(nameof(idx)),
        };

        private static DualShock4DPadDirection ComputeDpadDir(bool up, bool down, bool left, bool right)
        {
            if (!up && !down && !left && !right) return DualShock4DPadDirection.None;
            if (up && !down && !left && !right) return DualShock4DPadDirection.North;
            if (up && !down && left && !right) return DualShock4DPadDirection.Northwest;
            if (up && !down && !left && right) return DualShock4DPadDirection.Northeast;
            if (!up && down && !left && !right) return DualShock4DPadDirection.South;
            if (!up && down && left && !right) return DualShock4DPadDirection.Southwest;
            if (!up && down && !left && right) return DualShock4DPadDirection.Southeast;
            if (!up && !down && left && !right) return DualShock4DPadDirection.West;
            if (!up && !down && !left && right) return DualShock4DPadDirection.East;
            return DualShock4DPadDirection.None;
        }

        private void ApplyButtons(bool[] b)
        {
            if (_ctrlType == 2)
            {
                if (_ds4 == null) return;
                _ds4.SetSliderValue(DualShock4Slider.LeftTrigger, b[6] ? (byte)255 : (byte)0);
                _ds4.SetSliderValue(DualShock4Slider.RightTrigger, b[7] ? (byte)255 : (byte)0);
                for (int i = 0; i < ButtonCount; i++)
                {
                    if (i is 6 or 7 or >= 12 and <= 15) continue;
                    _ds4.SetButtonState(ToDS4Button(i), b[i]);
                }
                var dpad = ComputeDpadDir(b[12], b[13], b[14], b[15]);
                _ds4.SetDPadDirection(dpad);
            }
            else
            {
                if (_x360 == null) return;
                _x360.SetSliderValue(Xbox360Slider.LeftTrigger, b[6] ? (byte)255 : (byte)0);
                _x360.SetSliderValue(Xbox360Slider.RightTrigger, b[7] ? (byte)255 : (byte)0);
                for (int i = 0; i < ButtonCount; i++)
                {
                    if (i is 6 or 7) continue;
                    _x360.SetButtonState(ToX360Button(i), b[i]);
                }
            }
        }

        public DoubleMovement(int targetPid)
        {
            _targetPid = targetPid;
            RefreshCache();
            _focused = IsRobloxFocused();

            App.Logger.WriteLine(Tag, $"Init PID={targetPid} focused={_focused}");

            try
            {
                _client = new ViGEmClient();
                BusAvailable = true;
            }
            catch (Exception ex)
            {
                bool svc = ViGEmInterop.IsServiceInstalled();
                App.Logger.WriteLine(Tag, $"No ViGEmBus ({ex.Message}) service={svc}");
                return;
            }

            try
            {
                if (_ctrlType == 2)
                {
                    _ds4 = _client.CreateDualShock4Controller();
                    _ds4.AutoSubmitReport = false;
                    ConnectTarget(() => _ds4.Connect());
                }
                else
                {
                    _x360 = _client.CreateXbox360Controller();
                    _x360.AutoSubmitReport = false;
                    ConnectTarget(() => _x360.Connect());
                }
                ControllerPlugged = true;
                App.Logger.WriteLine(Tag, $"Controller {(_ctrlType == 2 ? "DS4" : "X360")} ready");
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(Tag + "::Ctl", ex);
                return;
            }

            _hookProc = HookCallback;
            _mouseHookProc = MouseHookCallback;
            using var me = Process.GetCurrentProcess();
            using var mm = me.MainModule;
            if (mm != null)
            {
                var hm = PInvoke.GetModuleHandle(mm.ModuleName);
                _hook = PInvoke.SetWindowsHookEx(
                    WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _hookProc, hm, 0);
                HookInstalled = _hook is { IsInvalid: false };
                if (HookInstalled) App.Logger.WriteLine(Tag, "Hook=all-keys blocker active");

                _mouseHook = PInvoke.SetWindowsHookEx(
                    WINDOWS_HOOK_ID.WH_MOUSE_LL, _mouseHookProc, hm, 0);
                if (_mouseHook is { IsInvalid: false })
                    App.Logger.WriteLine(Tag, "MouseHook active (block+mouse->right stick)");
            }

            _updateThread = new Thread(SmoothLoop)
            {
                Name = "DM-Smooth",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _updateThread.Start();

            App.Logger.WriteLine(Tag, $"Ready bus={BusAvailable} ctl={ControllerPlugged} hook={HookInstalled}");
        }

        private static void ConnectTarget(Action connect)
        {
            try { connect(); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 997)
            {
                App.Logger.WriteLine(Tag, "ViGEm connect IO pending, controller still finishes connecting.");
            }
        }

        public static bool IsViGEmBusInstalled()
        {
            try { return ViGEmInterop.IsInstalled(); }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);


        public void RefreshCache()
        {
            try
            {
                _fwdRad = Math.Clamp(App.Settings.Prop.DoubleMovementForwardAngle, 5.0, 85.0) * (Math.PI / 180.0);
                _strRad = Math.Clamp(App.Settings.Prop.DoubleMovementStrafeAngle, 5.0, 85.0) * (Math.PI / 180.0);
                _sens = Math.Clamp(App.Settings.Prop.DoubleMovementSensitivity, 0.1, 2.0);
                _curve = App.Settings.Prop.DoubleMovementCurve ?? "Linear";
                _ctrlType = App.Settings.Prop.DoubleMovementControllerType == "DualShock4" ? 2 : 0;
                _socdMode = (int)Math.Clamp(App.Settings.Prop.DoubleMovementSocdMode, 0, 2);

                _keyToButton.Clear();
                foreach (var kb in App.Settings.Prop.DoubleMovementKeybinds)
                {
                    if (!string.IsNullOrEmpty(kb.Key) && !string.IsNullOrEmpty(kb.Button)
                        && Enum.TryParse<Keys>(kb.Key, out var key) && BtnIndex.ContainsKey(kb.Button))
                        _keyToButton[key] = kb.Button;
                }

                _mouseBtnBind = new string[7];
                _mouseBtnBind[0] = App.Settings.Prop.DMMouseLeft ?? "";
                _mouseBtnBind[1] = App.Settings.Prop.DMMouseRight ?? "";
                _mouseBtnBind[2] = App.Settings.Prop.DMMouseMiddle ?? "";
                _mouseBtnBind[3] = App.Settings.Prop.DMMouseX1 ?? "";
                _mouseBtnBind[4] = App.Settings.Prop.DMMouseX2 ?? "";
                _scrollUpBind = App.Settings.Prop.DMScrollUp ?? "";
                _scrollDownBind = App.Settings.Prop.DMScrollDown ?? "";
                _mouseSens = (float)Math.Clamp(App.Settings.Prop.DMMouseSensitivity, 0.5, 20.0);
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(Tag, $"Cache refresh error: {ex.Message}");
            }
        }

        public void RefreshSettings() => RefreshCache();

        private bool IsRobloxFocused()
        {
            var hwnd = PInvoke.GetForegroundWindow();
            if (hwnd == HWND.Null) return false;
            PInvoke.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == _targetPid;
        }

        private LRESULT HookCallback(int code, WPARAM wParam, LPARAM lParam)
        {
            if (code >= 0)
            {
                uint msg = (uint)wParam;
                if (msg == PInvoke.WM_KEYDOWN || msg == PInvoke.WM_SYSKEYDOWN ||
                    msg == PInvoke.WM_KEYUP || msg == PInvoke.WM_SYSKEYUP)
                {
                    try
                    {
                        var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                        var key = (Keys)kbd.vkCode;
                        bool down = msg == PInvoke.WM_KEYDOWN || msg == PInvoke.WM_SYSKEYDOWN;
                        bool focused = IsRobloxFocused();

                        if (focused)
                        {
                            if (key is Keys.W or Keys.A or Keys.S or Keys.D)
                            {
                                long now = Stopwatch.GetTimestamp();
                                switch (key)
                                {
                                    case Keys.W: _w = down; if (down) _tW = now; break;
                                    case Keys.A: _a = down; if (down) _tA = now; break;
                                    case Keys.S: _s = down; if (down) _tS = now; break;
                                    case Keys.D: _d = down; if (down) _tD = now; break;
                                }
                                _stateChanged.Set();
                                return (LRESULT)1;
                            }

                            if (_keyToButton.TryGetValue(key, out string? btnName))
                            {
                                if (BtnIndex.TryGetValue(btnName, out int bi))
                                    _buttonStates[bi] = down;
                                _stateChanged.Set();
                                return (LRESULT)1;
                            }

                            int flags = (int)kbd.flags;
                            bool altDown = (flags & 0x20) != 0;
                            bool winDown = (GetAsyncKeyState(0x5B) & 0x8000) != 0
                                        || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
                            if (altDown || winDown)
                                return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);

                            return (LRESULT)1;
                        }
                    }
                    catch { }
                }
            }
            return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
        }

        private const uint _WM_MOUSEMOVE = 0x0200;
        private const uint _WM_LBUTTONDOWN = 0x0201;
        private const uint _WM_LBUTTONUP = 0x0202;
        private const uint _WM_RBUTTONDOWN = 0x0204;
        private const uint _WM_RBUTTONUP = 0x0205;
        private const uint _WM_MBUTTONDOWN = 0x0207;
        private const uint _WM_MBUTTONUP = 0x0208;
        private const uint _WM_MOUSEWHEEL = 0x020A;
        private const uint _WM_XBUTTONDOWN = 0x020B;
        private const uint _WM_XBUTTONUP = 0x020C;

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseLLHookStruct
        {
            public int x;
            public int y;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private LRESULT MouseHookCallback(int code, WPARAM wParam, LPARAM lParam)
        {
            if (code >= 0)
            {
                uint msg = (uint)wParam;
                bool focused = IsRobloxFocused();

                if (!focused)
                {
                    if (msg == _WM_MOUSEMOVE)
                    {
                        var ms = Marshal.PtrToStructure<MouseLLHookStruct>(lParam);
                        _prevMouseX = ms.x;
                        _prevMouseY = ms.y;
                    }
                    return PInvoke.CallNextHookEx(_mouseHook, code, wParam, lParam);
                }

                try
                {
                    var mouse = Marshal.PtrToStructure<MouseLLHookStruct>(lParam);

                    if (msg == _WM_MOUSEMOVE)
                    {
                        int dx = mouse.x - _prevMouseX;
                        int dy = mouse.y - _prevMouseY;
                        _prevMouseX = mouse.x;
                        _prevMouseY = mouse.y;

                        if (Math.Abs(dx) > 500 || Math.Abs(dy) > 500)
                            return (LRESULT)1;

                        _targetRX = Math.Clamp(_targetRX + dx * _mouseSens, -32767f, 32767f);
                        _targetRY = Math.Clamp(_targetRY + dy * _mouseSens, -32767f, 32767f);
                        _sendRightStick = true;
                        _stateChanged.Set();
                        return (LRESULT)1;
                    }

                    int btnIdx = -1;
                    bool down = false;

                    if (msg == _WM_LBUTTONDOWN) { btnIdx = 0; down = true; }
                    else if (msg == _WM_LBUTTONUP) { btnIdx = 0; }
                    else if (msg == _WM_RBUTTONDOWN) { btnIdx = 1; down = true; }
                    else if (msg == _WM_RBUTTONUP) { btnIdx = 1; }
                    else if (msg == _WM_MBUTTONDOWN) { btnIdx = 2; down = true; }
                    else if (msg == _WM_MBUTTONUP) { btnIdx = 2; }
                    else if (msg == _WM_XBUTTONDOWN || msg == _WM_XBUTTONUP)
                    {
                        int xBtn = (int)((mouse.mouseData >> 16) & 0xFFFF);
                        btnIdx = xBtn == 1 ? 3 : 4;
                        down = msg == _WM_XBUTTONDOWN;
                    }
                    else if (msg == _WM_MOUSEWHEEL)
                    {
                        int delta = (short)(mouse.mouseData >> 16);
                        string scrollBtn = delta > 0 ? _scrollUpBind : _scrollDownBind;
                        if (!string.IsNullOrEmpty(scrollBtn) && BtnIndex.TryGetValue(scrollBtn, out int sBi))
                        {
                            _buttonStates[sBi] = true;
                            _scrollBi = sBi;
                            _scrollReleaseTime = Stopwatch.GetTimestamp();
                            _stateChanged.Set();
                        }
                        return (LRESULT)1;
                    }

                    if (btnIdx >= 0 && btnIdx < _mouseBtnBind.Length)
                    {
                        string btnName = _mouseBtnBind[btnIdx];
                        if (!string.IsNullOrEmpty(btnName) && BtnIndex.TryGetValue(btnName, out int bi))
                        {
                            _buttonStates[bi] = down;
                            _stateChanged.Set();
                        }
                        return (LRESULT)1;
                    }
                }
                catch { }
            }
            return PInvoke.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        private void SmoothLoop()
        {
            try
            {
                SmoothLoopCore();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                App.Logger.WriteException("DoubleMovement::SmoothLoop", ex);
            }
        }

        private void SmoothLoopCore()
        {
            var token = _cts.Token;
            while (!token.IsCancellationRequested)
            {
                bool needsAnimation = _focused && (_w || _a || _s || _d || Math.Abs(_curX) > 1f || Math.Abs(_curY) > 1f || _sendRightStick || Math.Abs(_targetRX) > 1f || Math.Abs(_targetRY) > 1f || _scrollBi >= 0);
                bool stateChanged;
                try { stateChanged = _stateChanged.WaitOne(needsAnimation ? 4 : 500); }
                catch { break; }
                if (token.IsCancellationRequested || _disposed || !ControllerPlugged)
                    continue;

                bool focusedNow = IsRobloxFocused();
                bool focusChanged = focusedNow != _focused;
                if (focusChanged)
                {
                    _focused = focusedNow;
                    App.Logger.WriteLine(Tag, focusedNow
                        ? "Roblox focused, controller engaged"
                        : "Roblox unfocused, controller disengaged");
                    if (!focusedNow)
                    {
                        _w = false; _a = false; _s = false; _d = false;
                        Array.Clear(_buttonStates, 0, _buttonStates.Length);
                        Submit(0, 0, 0, 0);
                        _curX = 0; _curY = 0;
                        _curRX = 0; _curRY = 0; _targetRX = 0; _targetRY = 0;
                        _sendRightStick = false; _scrollBi = -1;
                        continue;
                    }
                    else
                    {
                        RecoverHeldKeys();
                    }
                }

                if (!_focused)
                {
                    continue;
                }

                if (!stateChanged && !focusChanged && !needsAnimation)
                    continue;

                bool rw = _w, ra = _a, rs = _s, rd = _d;
                ResolveSocd(ref rw, ref ra, ref rs, ref rd);

                short tx, ty;
                ComputeTarget(rw, ra, rs, rd, out tx, out ty);

                float ftx = tx, fty = ty;
                _curX += (ftx - _curX) * 0.45f;
                _curY += (fty - _curY) * 0.45f;

                if (Math.Abs(ftx - _curX) < 1f) _curX = ftx;
                if (Math.Abs(fty - _curY) < 1f) _curY = fty;

                if (_sendRightStick || Math.Abs(_targetRX) > 1f || Math.Abs(_targetRY) > 1f)
                {
                    _curRX += (_targetRX - _curRX) * 0.25f;
                    _curRY += (_targetRY - _curRY) * 0.25f;
                    _targetRX *= 0.92f;
                    _targetRY *= 0.92f;
                    if (Math.Abs(_targetRX) < 1f) _targetRX = 0;
                    if (Math.Abs(_targetRY) < 1f) _targetRY = 0;
                    _sendRightStick = Math.Abs(_targetRX) > 1f || Math.Abs(_targetRY) > 1f;
                }

                if (_scrollBi >= 0)
                {
                    long elapsedMs = (Stopwatch.GetTimestamp() - _scrollReleaseTime) * 1000 / Stopwatch.Frequency;
                    if (elapsedMs >= 50)
                    {
                        _buttonStates[_scrollBi] = false;
                        _scrollBi = -1;
                    }
                }

                if (!Submit((short)_curX, (short)_curY, (short)_curRX, (short)_curRY))
                {
                    if (!_submitEverFailed)
                    {
                        _submitEverFailed = true;
                        App.Logger.WriteLine(Tag, "Submit failed, controller may be disconnected");
                    }
                }
                else
                {
                    _submitEverFailed = false;
                }
            }
        }

        private void RecoverHeldKeys()
        {
            if ((GetAsyncKeyState((int)Keys.W) & 0x8000) != 0) { _w = true; _tW = Stopwatch.GetTimestamp(); }
            if ((GetAsyncKeyState((int)Keys.A) & 0x8000) != 0) { _a = true; _tA = Stopwatch.GetTimestamp(); }
            if ((GetAsyncKeyState((int)Keys.S) & 0x8000) != 0) { _s = true; _tS = Stopwatch.GetTimestamp(); }
            if ((GetAsyncKeyState((int)Keys.D) & 0x8000) != 0) { _d = true; _tD = Stopwatch.GetTimestamp(); }

            foreach (var kvp in _keyToButton)
            {
                if ((GetAsyncKeyState((int)kvp.Key) & 0x8000) != 0)
                {
                    if (BtnIndex.TryGetValue(kvp.Value, out int bi))
                        _buttonStates[bi] = true;
                }
            }

            if (_w || _a || _s || _d)
                App.Logger.WriteLine(Tag, "Recovered held keys on focus regain");
        }

        private void ResolveSocd(ref bool rw, ref bool ra, ref bool rs, ref bool rd)
        {
            if (rw && rs)
            {
                switch (_socdMode)
                {
                    case 0:
                        if (_tW > _tS) rs = false;
                        else rw = false;
                        break;
                    case 1:
                        rw = false; rs = false;
                        break;
                    case 2:
                        rs = false;
                        break;
                }
            }

            if (ra && rd)
            {
                switch (_socdMode)
                {
                    case 0:
                        if (_tA > _tD) rd = false;
                        else ra = false;
                        break;
                    case 1:
                        ra = false; rd = false;
                        break;
                    case 2:
                        if (_tA > _tD) rd = false;
                        else ra = false;
                        break;
                }
            }
        }

        private void ComputeTarget(bool rw, bool ra, bool rs, bool rd, out short tx, out short ty)
        {
            tx = 0; ty = 0;
            short m = (short)(32767 * _sens);

            double aFwd = _fwdRad;
            double aStr = _strRad;

            if (rw && !rs)
            {
                if (ra && !rd) { tx = (short)(-m * Math.Sin(aFwd)); ty = (short)( m * Math.Cos(aFwd)); }
                else if (rd && !ra) { tx = (short)( m * Math.Sin(aFwd)); ty = (short)( m * Math.Cos(aFwd)); }
                else ty = m;
            }
            else if (rs && !rw)
            {
                if (ra && !rd) { tx = (short)(-m * Math.Sin(aStr)); ty = (short)(-m * Math.Cos(aStr)); }
                else if (rd && !ra) { tx = (short)( m * Math.Sin(aStr)); ty = (short)(-m * Math.Cos(aStr)); }
                else ty = (short)-m;
            }
            else
            {
                if (ra && !rd) tx = (short)-m;
                else if (rd && !ra) tx = m;
            }

            if (_curve == "Exponential")
            {
                double nrmX = (double)tx / 32767;
                double nrmY = (double)ty / 32767;
                tx = (short)(Math.Sign(nrmX) * Math.Pow(Math.Abs(nrmX), 2.0) * 32767);
                ty = (short)(Math.Sign(nrmY) * Math.Pow(Math.Abs(nrmY), 2.0) * 32767);
            }
        }

        private bool Submit(short lx, short ly, short rx, short ry)
        {
            try
            {
                if (_ctrlType == 2)
                {
                    if (_ds4 == null) return false;
                    _ds4.SetAxisValue(DualShock4Axis.LeftThumbX, (byte)((lx + 32768) >> 8));
                    _ds4.SetAxisValue(DualShock4Axis.LeftThumbY, (byte)(255 - ((ly + 32768) >> 8)));
                    _ds4.SetAxisValue(DualShock4Axis.RightThumbX, (byte)((rx + 32768) >> 8));
                    _ds4.SetAxisValue(DualShock4Axis.RightThumbY, (byte)(255 - ((ry + 32768) >> 8)));
                    ApplyButtons(_buttonStates);
                    _ds4.SubmitReport();
                }
                else
                {
                    if (_x360 == null) return false;
                    _x360.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
                    _x360.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
                    _x360.SetAxisValue(Xbox360Axis.RightThumbX, rx);
                    _x360.SetAxisValue(Xbox360Axis.RightThumbY, ry);
                    ApplyButtons(_buttonStates);
                    _x360.SubmitReport();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            App.Logger.WriteLine(Tag, "Dispose");

            _cts.Cancel();
            _stateChanged.Set();

            if (_updateThread != null && _updateThread.IsAlive)
            {
                if (!_updateThread.Join(1000))
                    App.Logger.WriteLine(Tag, "Thread did not exit in time");
            }

            _cts.Dispose();
            _stateChanged.Dispose();

            if (HookInstalled)
                _hook?.Dispose();

            try { _mouseHook?.Dispose(); }
            catch { }

            if (ControllerPlugged)
            {
                try { _x360?.Disconnect(); } catch { }
                try { _ds4?.Disconnect(); } catch { }
                App.Logger.WriteLine(Tag, "Controller removed");
            }

            try { _client?.Dispose(); } catch { }

            GC.SuppressFinalize(this);
        }
    }
}
