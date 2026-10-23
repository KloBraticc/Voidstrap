using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using PropertyStore.Flags;
using PropertyStore.Structures;
using ShellLink;
using ShellLink.Structures;
using Voidstrap.Enums;
using Voidstrap.Resources;
using Voidstrap.UI;

namespace Voidstrap.Utility;

internal static class Shortcut
{
	private static GenericTriState _loadStatus = GenericTriState.Unknown;

	public const string AppUserModelId = "KloBraticc.Voidstrap";

	private static readonly Guid AppUserModelFormat = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

	public static void Create(string exePath, string exeArgs, string lnkPath)
	{
		Create(exePath, exeArgs, lnkPath, exePath);
	}

	public static string Suffix => Platform.IsLinux ? ".desktop" : Platform.IsMacOS ? ".app" : ".lnk";

	public static bool Exists(string path) => Platform.IsMacOS ? Voidstrap.Platform.MacOS.MacOSShortcut.Exists(path) : File.Exists(path);

	public static void Delete(string path)
	{
		if (Platform.IsMacOS)
			Voidstrap.Platform.MacOS.MacOSShortcut.Delete(path);
		else
			File.Delete(path);
	}

	public static bool Create(string exePath, string exeArgs, string lnkPath, string iconPath, bool overwrite = false)
	{
		if (Platform.IsWindows && !overwrite && File.Exists(lnkPath))
		{
			return true;
		}
		try
		{
			if (Platform.IsMacOS)
			{
				bool imageIcon = Path.GetExtension(iconPath).ToLowerInvariant() is ".png" or ".ico" or ".icns" or ".jpg" or ".jpeg";
				Voidstrap.Platform.MacOS.MacOSShortcut.Create(lnkPath, exePath, exeArgs, imageIcon ? iconPath : Branding.MacIconPath);
			}
			else if (Platform.IsLinux)
			{
				if (!LinuxDesktopEntry.CreateShortcut(lnkPath, Path.GetFileNameWithoutExtension(lnkPath), exePath, exeArgs, iconPath))
					throw new IOException("The desktop entry could not be written to " + lnkPath);
			}
			else
			{
				string temporary = lnkPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
				try
				{
					ShellLink.Shortcut link = ShellLink.Shortcut.CreateShortcut(exePath, exeArgs, iconPath, 0);
					if (string.IsNullOrEmpty(exeArgs))
						link.ExtraData.PropertyStoreDataBlock = AppUserModelBlock();
					link.WriteToFile(temporary);
					File.Move(temporary, lnkPath, overwrite);
				}
				finally
				{
					if (File.Exists(temporary))
						File.Delete(temporary);
				}
			}
			if (_loadStatus != GenericTriState.Successful)
			{
				_loadStatus = GenericTriState.Successful;
			}
			return true;
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
			return false;
		}
	}

	private static PropertyStoreDataBlock AppUserModelBlock()
	{
		byte[] characters = Encoding.Unicode.GetBytes(AppUserModelId + "\0");
		byte[] value = new byte[4 + ((characters.Length + 3) & ~3)];
		BitConverter.GetBytes((uint)(AppUserModelId.Length + 1)).CopyTo(value, 0);
		characters.CopyTo(value, 4);
		SerializedPropertyStorage storage = new(new List<SerializedPropertyValue> { new IntegerName(5, new TypedPropertyValue(PropertyType.VT_LPWSTR, value)) })
		{
			FormatID = AppUserModelFormat
		};
		return new PropertyStoreDataBlock(new List<SerializedPropertyStorage> { storage });
	}
}
