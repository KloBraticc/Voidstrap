using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Voidstrap.Integrations.ClassicTopBar;
using Voidstrap.Integrations.Overlays;

namespace Voidstrap.UI.Elements.ClassicTopBar;

internal static class ClassicTopBarOverlay
{
	private const string LogIdent = "ClassicTopBar";

	private const double ReferenceHeight = 1080;

	private static readonly object Gate = new object();

	private static readonly Dictionary<string, BitmapImage> Images = new(StringComparer.OrdinalIgnoreCase);

	private static ClassicTopBarWindow? _topBar;

	private static ClassicMenuWindow? _menu;

	private static int _reconcilePending;

	private static UnhookWindowsHookExSafeHandle? _hook;

	private static HOOKPROC? _hookProc;

	public static bool MenuVisible { get; private set; }

	public static bool ChatOpen { get; private set; }

	public static bool BackpackOpen { get; private set; }

	public static bool TopBarVisible { get; private set; } = true;

	public static bool ShiftLockEnabled { get; private set; }

	public static string UserName { get; private set; } = "Player";

	public static double Scale
	{
		get
		{
			RobloxWindowRect rect = RobloxWindowTracker.Current;
			double height = rect.Valid && rect.Height > 0 ? rect.Height : ReferenceHeight;
			return Math.Clamp(height / ReferenceHeight, 0.8, 2.0);
		}
	}

	public static void Reconcile()
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
		{
			return;
		}
		if (!application.Dispatcher.CheckAccess())
		{
			if (Interlocked.Exchange(ref _reconcilePending, 1) != 0)
			{
				return;
			}
			try
			{
				application.Dispatcher.BeginInvoke(new Action(ReconcileCore));
			}
			catch (InvalidOperationException)
			{
				Interlocked.Exchange(ref _reconcilePending, 0);
			}
			return;
		}
		ReconcileCore();
	}

	private static void ReconcileCore()
	{
		Interlocked.Exchange(ref _reconcilePending, 0);
		if (!App.Settings.Prop.ClassicTopBarEnabled || !ClassicTopBarMod.AssetsReady)
		{
			CloseAll();
			return;
		}
		lock (Gate)
		{
			if (_topBar is { IsClosing: false })
			{
				_topBar.RefreshLayout();
				return;
			}
		}
		try
		{
			UserName = ClassicTopBarMod.ReadUserName();
			ClassicTopBarWindow window = new ClassicTopBarWindow();
			_ = RefreshUserNameAsync();
			lock (Gate)
			{
				_topBar = window;
			}
			window.Show();
			InstallHook();
			App.Logger?.WriteLine(LogIdent, "The classic topbar overlay is showing");
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("ClassicTopBarOverlay::Reconcile", ex);
		}
	}

	public static void CloseAll()
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
		{
			return;
		}
		if (!application.Dispatcher.CheckAccess())
		{
			application.Dispatcher.BeginInvoke(new Action(CloseAll));
			return;
		}

		RemoveHook();
		ClassicTopBarWindow? bar;
		ClassicMenuWindow? menu;
		lock (Gate)
		{
			bar = _topBar;
			menu = _menu;
			_topBar = null;
			_menu = null;
		}
		MenuVisible = false;
		ChatOpen = false;
		BackpackOpen = false;
		TopBarVisible = true;
		try
		{
			menu?.Close();
		}
		catch (InvalidOperationException)
		{
		}
		try
		{
			bar?.Close();
		}
		catch (InvalidOperationException)
		{
		}
	}

	public static BitmapImage? Image(string name)
	{
		lock (Images)
		{
			if (Images.TryGetValue(name, out BitmapImage? cached))
			{
				return cached;
			}
		}
		string? path = ClassicTopBarMod.ResolveAsset(name);
		if (path == null)
		{
			return null;
		}
		try
		{
			BitmapImage image = new BitmapImage();
			image.BeginInit();
			image.CacheOption = BitmapCacheOption.OnLoad;
			image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
			image.UriSource = new Uri(path);
			image.EndInit();
			image.Freeze();
			lock (Images)
			{
				Images[name] = image;
			}
			return image;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The image " + name + " could not be loaded: " + ex.Message);
			return null;
		}
	}

	public static void ToggleMenu()
	{
		if (MenuVisible)
		{
			HideMenu();
			return;
		}
		ShowMenu();
	}

	public static void ShowMenu()
	{
		if (MenuVisible)
		{
			return;
		}
		if (!TopBarVisible)
		{
			SetTopBarVisible(true);
		}
		ClassicMenuWindow window;
		lock (Gate)
		{
			if (_menu is { IsClosing: false })
			{
				window = _menu;
			}
			else
			{
				window = new ClassicMenuWindow();
				_menu = window;
			}
		}
		MenuVisible = true;
		window.Show();
		window.PlayOpen();
		_topBar?.RefreshIcons();
	}

	public static void HideMenu()
	{
		ClassicMenuWindow? window;
		lock (Gate)
		{
			window = _menu;
		}
		MenuVisible = false;
		_topBar?.RefreshIcons();
		if (window == null)
		{
			return;
		}
		window.PlayClose();
	}

	public static void ReleaseMenu(ClassicMenuWindow window)
	{
		lock (Gate)
		{
			if (ReferenceEquals(_menu, window))
			{
				_menu = null;
			}
		}
	}

	public static void ReleaseTopBar(ClassicTopBarWindow window)
	{
		lock (Gate)
		{
			if (ReferenceEquals(_topBar, window))
			{
				_topBar = null;
			}
		}
	}

	public static void SetTopBarVisible(bool visible)
	{
		TopBarVisible = visible;
		_topBar?.ApplyVisibility();
	}

	public static void SetChatOpen(bool open)
	{
		ChatOpen = open;
		_topBar?.RefreshIcons();
	}

	public static void SetBackpackOpen(bool open)
	{
		BackpackOpen = open;
		_topBar?.RefreshIcons();
	}

	private static async Task RefreshUserNameAsync()
	{
		string name = await ClassicTopBarMod.RefreshUserNameAsync(CancellationToken.None).ConfigureAwait(true);
		if (string.Equals(name, UserName, StringComparison.Ordinal))
		{
			return;
		}
		UserName = name;
		_topBar?.RefreshLayout();
	}

	public static void SetShiftLock(bool enabled)
	{
		ShiftLockEnabled = enabled;
	}

	private static void InstallHook()
	{
		if (_hook is { IsInvalid: false })
		{
			return;
		}
		try
		{
			_hookProc = HookCallback;
			using Process current = Process.GetCurrentProcess();
			using ProcessModule? module = current.MainModule;
			if (module == null)
			{
				return;
			}
			_hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _hookProc, PInvoke.GetModuleHandle(module.ModuleName), 0);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteException("ClassicTopBarOverlay::InstallHook", ex);
		}
	}

	private static void RemoveHook()
	{
		UnhookWindowsHookExSafeHandle? hook = _hook;
		_hook = null;
		_hookProc = null;
		try
		{
			hook?.Dispose();
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine(LogIdent, "The keyboard hook could not be released: " + ex.Message);
		}
	}

	private static LRESULT HookCallback(int code, WPARAM wParam, LPARAM lParam)
	{
		if (code >= 0)
		{
			try
			{
				uint message = (uint)wParam;
				bool down = message == PInvoke.WM_KEYDOWN || message == PInvoke.WM_SYSKEYDOWN;
				bool up = message == PInvoke.WM_KEYUP || message == PInvoke.WM_SYSKEYUP;
				if (down || up)
				{
					KBDLLHOOKSTRUCT keyboard = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
					ushort key = (ushort)keyboard.vkCode;
					if (ShouldSwallowEscape(key, keyboard.dwExtraInfo))
					{
						if (down)
						{
							Post(ToggleMenu);
						}
						return new LRESULT(1);
					}
					if (down)
					{
						OnKeyDown(key);
					}
				}
			}
			catch (Exception ex)
			{
				App.Logger?.WriteLine(LogIdent, "A key could not be handled: " + ex.Message);
			}
		}
		return PInvoke.CallNextHookEx(_hook, code, wParam, lParam);
	}

	private static bool ShouldSwallowEscape(ushort key, nuint extraInfo)
	{
		return key == ClassicTopBarInput.VkEscape
			&& extraInfo != ClassicTopBarInput.InjectedTag
			&& RobloxWindowTracker.IsRobloxForeground();
	}

	private static void OnKeyDown(ushort key)
	{
		bool robloxFocused = RobloxWindowTracker.IsRobloxForeground();
		if (key == ClassicTopBarInput.VkF4 && robloxFocused)
		{
			Post(() => SetTopBarVisible(!TopBarVisible));
			return;
		}
		if (!robloxFocused)
		{
			return;
		}
		if (key == ClassicTopBarInput.VkOemSlash && !MenuVisible)
		{
			Post(() => SetChatOpen(true));
			return;
		}
		if (key == ClassicTopBarInput.VkReturn && ChatOpen)
		{
			Post(() => SetChatOpen(false));
			return;
		}
		if (key == ClassicTopBarInput.VkOemTilde)
		{
			Post(() => SetBackpackOpen(!BackpackOpen));
		}
	}

	private static void Post(Action action)
	{
		Application? application = Application.Current;
		if (application == null || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished)
		{
			return;
		}
		try
		{
			application.Dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Input);
		}
		catch (InvalidOperationException)
		{
		}
	}

	public static void ShowRecording(bool visible)
	{
		_topBar?.ShowRecording(visible);
	}
}
