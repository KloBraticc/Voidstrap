using System;
using System.Windows;

namespace Voidstrap.Utility;

public static class ClipboardService
{
	public static bool SetText(string? text)
	{
		string value = text ?? string.Empty;

		if (Platform.IsLinux && Voidstrap.Platform.Linux.LinuxClipboard.SetText(value))
			return true;

		try
		{
			Clipboard.SetText(value);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ClipboardService::SetText", "The clipboard could not be updated: " + ex.Message);
			return false;
		}
	}

	public static bool SetDataObject(object? data, bool copy = true)
	{
		if (data is string text)
			return SetText(text);

		if (Platform.IsLinux)
			return SetText(data?.ToString());

		try
		{
			Clipboard.SetDataObject(data!, copy);
			return true;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ClipboardService::SetDataObject", "The clipboard could not be updated: " + ex.Message);
			return false;
		}
	}

	public static string GetText()
	{
		if (Platform.IsLinux)
		{
			string? native = Voidstrap.Platform.Linux.LinuxClipboard.GetText();
			if (native is not null)
				return native;
		}

		try
		{
			return Clipboard.GetText() ?? string.Empty;
		}
		catch (Exception ex)
		{
			App.Logger?.WriteLine("ClipboardService::GetText", "The clipboard could not be read: " + ex.Message);
			return string.Empty;
		}
	}

	public static bool ContainsText()
	{
		if (Platform.IsLinux)
			return GetText().Length > 0;

		try
		{
			return Clipboard.ContainsText();
		}
		catch (Exception)
		{
			return false;
		}
	}
}
