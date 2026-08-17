using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Voidstrap.Integrations.GameChat
{
    public class GameChatKeyboardHook : IDisposable
    {
        private readonly GameChatOverlay _form;
        private readonly uint _robloxPid;
        private UnhookWindowsHookExSafeHandle? _hook;
        private HOOKPROC? _hookProc;
        private IntPtr _formHandle;
        private bool _chatMode;
		private bool _keyboardEnabled;
		private bool _externalOverlaySuppressed;
		private long _lastExternalOverlayToggleMs;
        private bool _disposed;
		private readonly ConcurrentQueue<Action> _pendingInput = new();
		private int _pendingInputCount;
		private int _inputFlushScheduled;
		private const int MaxPendingInput = 128;
		[ThreadStatic]
		private static byte[]? _keyState;
		[ThreadStatic]
		private static StringBuilder? _keyBuffer;

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const int VK_MENU = 0x12;
        private const int VK_CAPITAL = 0x14;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

        public GameChatKeyboardHook(GameChatOverlay form, uint robloxPid)
        {
            _form = form;
            _robloxPid = robloxPid;

            _form.ChatModeRequested += OnChatModeRequested;
            _form.ChatModeExited += OnChatModeExited;

			_hookProc = HookCallback;
		}

		public void SetEnabled(bool enabled)
		{
			if (_disposed || enabled == _keyboardEnabled)
				return;
			_keyboardEnabled = enabled;
			_externalOverlaySuppressed = false;
			if (enabled)
				StartKeyboardHook();
			else
			{
				_chatMode = false;
				StopKeyboardHook();
			}
		}

		private void StartKeyboardHook()
		{
			if (_disposed || !_keyboardEnabled || _hook is { IsInvalid: false })
				return;
            using var me = Process.GetCurrentProcess();
            using var mm = me.MainModule;
            if (mm != null)
            {
                var hm = PInvoke.GetModuleHandle(mm.ModuleName);
                _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _hookProc, hm, 0);
            }
            if (_hook == null || _hook.IsInvalid)
                App.Logger.WriteLine("GameChatKeyboardHook", "Failed to install keyboard hook");
        }

		private void StopKeyboardHook()
		{
			try
			{
				_hook?.Dispose();
			}
			catch
			{
			}
			_hook = null;
		}

        private static bool IsNonTextKey(Keys key) =>
            key == Keys.Escape ||
            key == Keys.Enter ||
            key == Keys.Back ||
            key == Keys.ControlKey ||
            key == Keys.ShiftKey ||
            key == Keys.LWin || key == Keys.RWin ||
            key == Keys.Menu ||
            key == Keys.Left || key == Keys.Right ||
            key == Keys.Up || key == Keys.Down;

        private bool IsChatContextForeground()
        {
            var hwnd = PInvoke.GetForegroundWindow();
            if (hwnd == HWND.Null)
                return false;
            if (_formHandle == IntPtr.Zero)
                _formHandle = _form.WindowHandle;
            if (_formHandle != IntPtr.Zero && hwnd == new HWND(_formHandle))
                return true;
            PInvoke.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == _robloxPid;
        }

        private LRESULT HookCallback(int code, WPARAM wParam, LPARAM lParam)
        {
			if (code < 0 || _disposed || !_keyboardEnabled)
                return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);

            uint msg = (uint)wParam;
            if (msg != PInvoke.WM_KEYDOWN && msg != PInvoke.WM_SYSKEYDOWN)
                return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);

            try
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var key = (Keys)kbd.vkCode;

                if (!_form.IsOverlayVisible)
                    return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);

                if (!IsChatContextForeground())
                    return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);

                bool isWinKey = key == Keys.LWin || key == Keys.RWin;
                bool isWinModifier = GetAsyncKeyState(VK_LWIN) < 0 || GetAsyncKeyState(VK_RWIN) < 0;
                if (isWinKey || isWinModifier)
                    return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);

                bool control = GetAsyncKeyState(VK_CONTROL) < 0;
                bool shift = GetAsyncKeyState(VK_SHIFT) < 0;
                bool alt = GetAsyncKeyState(VK_MENU) < 0;

				if (shift && key == Keys.Oemtilde && (_externalOverlaySuppressed || IsDiscordRunning()))
				{
					long now = Environment.TickCount64;
					if (now - _lastExternalOverlayToggleMs >= 500)
					{
						_lastExternalOverlayToggleMs = now;
						_externalOverlaySuppressed = !_externalOverlaySuppressed;
						if (_externalOverlaySuppressed && _chatMode)
						{
							_chatMode = false;
							QueueInput(_form.CancelChatMode);
						}
					}
					return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
				}

				if (_externalOverlaySuppressed)
					return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);

                if (!_chatMode)
                {
                    if (control && shift && key == Keys.C)
                    {
                        QueueInput(_form.ToggleVisibility);
                        return (LRESULT)1;
                    }

                    if (_form.IsWindowHidden)
                        return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);

                    if (key == Keys.OemQuestion)
                    {
                        _chatMode = true;
                        QueueInput(_form.StartChatMode);
                        return (LRESULT)1;
                    }
                    return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
                }

                if (control && key == Keys.V)
                {
                    QueueInput(_form.PasteFromClipboard);
                    return (LRESULT)1;
                }

                if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down)
                {
                    QueueInput(() => _form.HandleNavigation(key));
                    return (LRESULT)1;
                }

                if (key == Keys.Escape)
                {
                    _chatMode = false;
                    QueueInput(_form.CancelChatMode);
                    return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
                }

                if (key == Keys.Enter)
                {
                    _chatMode = false;
					QueueInput(SendPendingMessage);
                    return (LRESULT)1;
                }

                if (key == Keys.Back)
                {
                    QueueInput(_form.Backspace);
                    return (LRESULT)1;
                }

                string? text = TranslateKey(key, kbd.scanCode, control, shift, alt);
                if (!string.IsNullOrEmpty(text))
                {
                    QueueInput(() => _form.AppendTextFromKey(text));
                    return (LRESULT)1;
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine("GameChatKeyboardHook", "Hook error: " + ex.Message);
            }

            return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
        }

		private void QueueInput(Action action)
		{
			if (_disposed || _form.Dispatcher.HasShutdownStarted || _form.Dispatcher.HasShutdownFinished)
				return;
			_pendingInput.Enqueue(action);
			int count = Interlocked.Increment(ref _pendingInputCount);
			while (count > MaxPendingInput && _pendingInput.TryDequeue(out _))
				count = Interlocked.Decrement(ref _pendingInputCount);
			if (Interlocked.Exchange(ref _inputFlushScheduled, 1) != 0)
				return;
			try
			{
				_form.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FlushInput));
			}
			catch (InvalidOperationException)
			{
				Interlocked.Exchange(ref _inputFlushScheduled, 0);
			}
		}

		private void FlushInput()
		{
			int processed = 0;
			while (!_disposed && processed < 32 && _pendingInput.TryDequeue(out Action? action))
			{
				Interlocked.Decrement(ref _pendingInputCount);
				try
				{
					action();
				}
				catch (Exception ex)
				{
					App.Logger.WriteLine("GameChatKeyboardHook", "Input action failed: " + ex.Message);
				}
				processed++;
			}
			Interlocked.Exchange(ref _inputFlushScheduled, 0);
			if (!_pendingInput.IsEmpty)
				QueueInputFlush();
		}

		private void QueueInputFlush()
		{
			if (_disposed || Interlocked.Exchange(ref _inputFlushScheduled, 1) != 0)
				return;
			try
			{
				_form.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FlushInput));
			}
			catch (InvalidOperationException)
			{
				Interlocked.Exchange(ref _inputFlushScheduled, 0);
			}
		}

		private void SendPendingMessage()
		{
			_ = _form.Send();
		}

		private static long _discordProbeMs;

		private static bool _discordProbeResult;

		private const int DiscordProbeIntervalMs = 15000;

		private static bool IsDiscordRunning()
		{
			long now = Environment.TickCount64;
			if (_discordProbeMs != 0 && now - _discordProbeMs < DiscordProbeIntervalMs)
				return _discordProbeResult;
			_discordProbeMs = now;
			_discordProbeResult = ProbeDiscordRunning();
			return _discordProbeResult;
		}

		private static bool ProbeDiscordRunning()
		{
			string[] names = ["Discord", "DiscordCanary", "DiscordPTB"];
			foreach (string name in names)
			{
				Process[] processes;
				try
				{
					processes = Process.GetProcessesByName(name);
				}
				catch
				{
					continue;
				}
				bool found = processes.Length > 0;
				foreach (Process process in processes)
					process.Dispose();
				if (found)
					return true;
			}
			return false;
		}

        private static string? TranslateKey(Keys key, uint scanCode, bool control, bool shift, bool alt)
        {
            if ((control || alt) && !(control && alt))
                return null;

            if (IsNonTextKey(key))
                return null;

			byte[] state = _keyState ??= new byte[256];
			Array.Clear(state, 0, state.Length);
            if (shift)
                state[VK_SHIFT] = 0x80;
            if (control)
                state[VK_CONTROL] = 0x80;
            if (alt)
                state[VK_MENU] = 0x80;
            if ((GetKeyState(VK_CAPITAL) & 1) != 0)
                state[VK_CAPITAL] = 0x01;

			StringBuilder sb = _keyBuffer ??= new StringBuilder(8);
			sb.Clear();
            IntPtr layout = GetKeyboardLayout(0);
            int result = ToUnicodeEx((uint)key, scanCode, state, sb, sb.Capacity, 0, layout);
            return result > 0 ? sb.ToString() : null;
        }

        public void ResetChatMode()
        {
            _chatMode = false;
        }

        private void OnChatModeRequested(object? sender, EventArgs e)
        {
            _chatMode = true;
        }

        private void OnChatModeExited(object? sender, EventArgs e)
        {
            _chatMode = false;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _form.ChatModeRequested -= OnChatModeRequested;
            _form.ChatModeExited -= OnChatModeExited;
			_keyboardEnabled = false;
			while (_pendingInput.TryDequeue(out _))
			{
			}
			Interlocked.Exchange(ref _pendingInputCount, 0);
			Interlocked.Exchange(ref _inputFlushScheduled, 0);
			StopKeyboardHook();
            _hook = null;
            _hookProc = null;
            GC.SuppressFinalize(this);
        }
    }
}
