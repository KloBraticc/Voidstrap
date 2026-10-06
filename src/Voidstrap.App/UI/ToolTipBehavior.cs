using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace Voidstrap.UI;

internal static partial class ToolTipBehavior
{
	private const int WmNcHitTest = 0x0084;

	private const int HtTransparent = -1;

	private const int GwlExStyle = -20;

	private const long WsExLayered = 0x00080000;

	private const long WsExTransparent = 0x00000020;

	private const long WsExNoActivate = 0x08000000;

	private static readonly HashSet<ToolTip> OpenToolTips = new HashSet<ToolTip>();

	private static readonly ConditionalWeakTable<HwndSource, object> HookedSources = new ConditionalWeakTable<HwndSource, object>();

	private static readonly HwndSourceHook HitTestHook = PassHitTestThrough;

	private static bool _installed;

	public static void Install()
	{
		if (_installed)
			return;
		_installed = true;
		EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnToolTipOpened));
		EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.ClosedEvent, new RoutedEventHandler(OnToolTipClosed));
		EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel), true);
		EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), true);
	}

	private static void OnToolTipOpened(object sender, RoutedEventArgs e)
	{
		if (sender is not ToolTip toolTip)
			return;
		OpenToolTips.Add(toolTip);
		toolTip.IsHitTestVisible = false;
		if (OperatingSystem.IsWindows())
			MakeClickThrough(toolTip);
		else if (OperatingSystem.IsLinux() && !TryMakeLinuxClickThrough(toolTip))
			toolTip.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => RetryLinuxClickThrough(toolTip));
	}

	private static void RetryLinuxClickThrough(ToolTip toolTip)
	{
		if (toolTip.IsOpen)
			TryMakeLinuxClickThrough(toolTip);
	}

	private static bool TryMakeLinuxClickThrough(ToolTip toolTip)
	{
		try
		{
			if (PresentationSource.FromVisual(toolTip) is not HwndSource source || source.IsDisposed)
				return false;
			nint handle = source.Handle;
			if (handle == 0 || !Voidstrap.Platform.Linux.LinuxWindowInterop.IsLiveWindow(handle))
				return false;
			return Voidstrap.Platform.Linux.LinuxWindowInterop.TrySetClickThrough(handle);
		}
		catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
		{
			return false;
		}
	}

	private static void OnToolTipClosed(object sender, RoutedEventArgs e)
	{
		if (sender is ToolTip toolTip)
			OpenToolTips.Remove(toolTip);
	}

	private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
	{
		CloseAll();
	}

	private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
	{
		CloseAll();
	}

	public static void CloseAll()
	{
		if (OpenToolTips.Count == 0)
			return;
		foreach (ToolTip toolTip in OpenToolTips.ToArray())
		{
			try
			{
				toolTip.IsOpen = false;
			}
			catch (InvalidOperationException)
			{
			}
		}
		OpenToolTips.Clear();
	}

	private static void MakeClickThrough(ToolTip toolTip)
	{
		try
		{
			if (PresentationSource.FromVisual(toolTip) is not HwndSource source || source.IsDisposed)
				return;
			if (!HookedSources.TryGetValue(source, out _))
			{
				source.AddHook(HitTestHook);
				HookedSources.Add(source, new object());
			}
			nint handle = source.Handle;
			if (handle == 0)
				return;
			long style = (long)GetWindowLongPtrW(handle, GwlExStyle);
			if ((style & WsExLayered) == 0)
				return;
			long wanted = style | WsExTransparent | WsExNoActivate;
			if (wanted != style)
				SetWindowLongPtrW(handle, GwlExStyle, (nint)wanted);
		}
		catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
		{
		}
	}

	private static nint PassHitTestThrough(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
	{
		if (msg != WmNcHitTest)
			return 0;
		handled = true;
		return HtTransparent;
	}

	[LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static partial nint GetWindowLongPtrW(nint hWnd, int index);

	[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
	private static partial nint SetWindowLongPtrW(nint hWnd, int index, nint value);
}
