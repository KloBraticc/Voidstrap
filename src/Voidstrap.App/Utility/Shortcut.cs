using System.IO;
using System.Windows;
using ShellLink;
using Voidstrap.Enums;
using Voidstrap.Resources;
using Voidstrap.UI;

namespace Voidstrap.Utility;

internal static class Shortcut
{
	private static GenericTriState _loadStatus = GenericTriState.Unknown;

	public static void Create(string exePath, string exeArgs, string lnkPath)
	{
		Create(exePath, exeArgs, lnkPath, exePath);
	}

	public static void Create(string exePath, string exeArgs, string lnkPath, string iconPath)
	{
		if (File.Exists(lnkPath))
		{
			return;
		}
		try
		{
			ShellLink.Shortcut.CreateShortcut(exePath, exeArgs, iconPath, 0).WriteToFile(lnkPath);
			if (_loadStatus != GenericTriState.Successful)
			{
				_loadStatus = GenericTriState.Successful;
			}
		}
		catch (Exception ex)
		{
			App.Logger.WriteLine("Shortcut::Create", "Failed to create a shortcut for " + lnkPath + "!");
			App.Logger.WriteException("Shortcut::Create", ex);
			if (_loadStatus != GenericTriState.Failed)
			{
				_loadStatus = GenericTriState.Failed;
				Frontend.ShowMessageBox(Strings.Dialog_CannotCreateShortcuts, MessageBoxImage.Asterisk);
			}
		}
	}
}
