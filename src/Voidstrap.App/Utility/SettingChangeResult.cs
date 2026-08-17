using System;

namespace Voidstrap.Utility;

internal readonly record struct SettingChangeResult(bool Success, string Message, Exception? Exception)
{
	public static SettingChangeResult Succeeded => new(true, string.Empty, null);
}

internal sealed class SettingChangeFailedEventArgs : EventArgs
{
	public SettingChangeResult Result { get; }

	public SettingChangeFailedEventArgs(SettingChangeResult result)
	{
		Result = result;
	}
}

internal static class SettingChangeNotifier
{
	public static event EventHandler<SettingChangeFailedEventArgs>? Failed;

	public static SettingChangeResult Try(string operation, string message, Action action)
	{
		try
		{
			action();
			return SettingChangeResult.Succeeded;
		}
		catch (Exception ex)
		{
			return Report(operation, message, ex);
		}
	}

	public static SettingChangeResult Report(string operation, string message, Exception exception)
	{
		App.Logger?.WriteException(operation, exception);
		SettingChangeResult result = new(false, message, exception);
		try
		{
			Failed?.Invoke(null, new SettingChangeFailedEventArgs(result));
		}
		catch (Exception notificationException)
		{
			App.Logger?.WriteException("SettingChangeNotifier::Report", notificationException);
		}
		return result;
	}
}
