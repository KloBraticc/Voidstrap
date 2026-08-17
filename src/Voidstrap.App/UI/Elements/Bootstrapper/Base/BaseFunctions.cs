using System;
using System.Windows;

namespace Voidstrap.UI.Elements.Bootstrapper.Base;

internal static class BaseFunctions
{
	public static void ShowSuccess(string message, Action? callback = null)
	{
		Frontend.ShowMessageBox(message, MessageBoxImage.Asterisk);
		callback?.Invoke();
		App.Terminate();
	}
}
