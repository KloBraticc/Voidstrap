using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Voidstrap.UI;

internal static class MacKeyboardFocus
{
	private static bool _installed;

	public static void Install()
	{
		if (_installed || !Voidstrap.Utility.Platform.IsMacOS)
			return;
		_installed = true;
		InputManager.Current.PreProcessInput += OnPreProcessInput;
		EventManager.RegisterClassHandler(typeof(UIElement), Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostKeyboardFocus), true);
	}

	private static void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
	{
		if (Keyboard.FocusedElement == null && e.StagingItem.Input.GetType().Name == "InputReportEventArgs")
			FocusActiveWindow();
	}

	private static void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
	{
		if (e.NewFocus == null && ReferenceEquals(sender, e.OldFocus) && Application.Current is { } app)
			app.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(RestoreFocus));
	}

	private static void RestoreFocus()
	{
		if (Keyboard.FocusedElement == null)
			FocusActiveWindow();
	}

	private static void FocusActiveWindow()
	{
		Window? window = Application.Current?.Windows.OfType<Window>().LastOrDefault(candidate => candidate.IsActive && candidate.IsVisible);
		if (window != null)
			Keyboard.Focus(window);
	}
}
