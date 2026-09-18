using System;
using System.Runtime.InteropServices;

namespace Voidstrap.UI.Elements.ClassicTopBar;

internal static partial class ClassicTopBarNative
{
	private const int GwlExStyle = -20;
	private const int WsExToolWindow = 0x80;
	private const int WsExNoActivate = 0x08000000;

	[LibraryImport("user32.dll", EntryPoint = "GetWindowLongA")]
	private static partial int GetWindowLong(IntPtr hwnd, int index);

	[LibraryImport("user32.dll", EntryPoint = "SetWindowLongA")]
	private static partial int SetWindowLong(IntPtr hwnd, int index, int style);

	public static void MakeNonActivating(IntPtr hwnd)
	{
		if (hwnd == IntPtr.Zero)
		{
			return;
		}
		try
		{
			int style = GetWindowLong(hwnd, GwlExStyle);
			_ = SetWindowLong(hwnd, GwlExStyle, style | WsExToolWindow | WsExNoActivate);
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ClassicTopBar", "The overlay window style could not be set: " + ex.Message);
		}
	}
}
