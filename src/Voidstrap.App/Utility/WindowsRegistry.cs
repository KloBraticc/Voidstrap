using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using Voidstrap.Extensions;

namespace Voidstrap.Utility;

internal static class WindowsRegistry
{
	private const string RobloxPlaceKey = "Roblox.Place";

	private const string ClassesRoot = "Software\\Classes\\";

	public static readonly List<RegistryKey> Roots = Platform.SupportsRegistry
		? new List<RegistryKey>
		{
			Registry.CurrentUser,
			Registry.LocalMachine
		}
		: new List<RegistryKey>();

	public static void RegisterProtocol(string key, string name, string handler, string handlerParam = "%1")
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		string value = "\"" + handler + "\" " + handlerParam;
		string subkey = "Software\\Classes\\" + key;
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(subkey);
			using RegistryKey registryKey2 = registryKey?.CreateSubKey("DefaultIcon");
			using RegistryKey registryKey3 = registryKey?.CreateSubKey("shell\\open\\command");
			if (registryKey == null || registryKey2 == null || registryKey3 == null)
			{
				throw new InvalidOperationException("Failed to create subkeys for " + key);
			}
			registryKey.SetValueSafe("", "URL:" + name + " Protocol");
			registryKey.SetValueSafe("URL Protocol", "");
			registryKey2.SetValueSafe("", "\"" + handler + "\",0");
			registryKey3.SetValueSafe("", value);
			if (!string.Equals(registryKey3.GetValue("") as string, value, StringComparison.Ordinal))
				throw new IOException("Protocol command verification failed for " + key);
		}
		catch (Exception value2)
		{
			App.Logger.WriteLine("Registry::RegisterProtocol", $"Failed to register {key}: {value2}");
		}
	}

	public static void RegisterPlayer()
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		RegisterPlayer(Paths.Application, "-player \"%1\"");
	}

	public static void RegisterPlayer(string handler, string handlerParam)
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		RegisterProtocol("roblox", "Roblox", handler, handlerParam);
		RegisterProtocol("roblox-player", "Roblox", handler, handlerParam);
	}

	public static void RegisterVoidstrap()
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		RegisterProtocol("voidstrap", "Voidstrap", Paths.Application, "-theme \"%1\"");
	}

	public static void RegisterStudio()
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		RegisterStudioProtocol(Paths.Application, "-studio \"%1\"");
		RegisterStudioFileClass(Paths.Application, "-studio \"%1\"");
		RegisterStudioFileTypes();
	}

	public static void RegisterStudioProtocol(string handler, string handlerParam)
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		RegisterProtocol("roblox-studio", "Roblox Studio", handler, handlerParam);
		RegisterProtocol("roblox-studio-auth", "Roblox Studio Auth", handler, handlerParam);
	}

	public static void RegisterStudioFileTypes()
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		RegisterStudioFileType(".rbxl");
		RegisterStudioFileType(".rbxlx");
	}

	public static void RegisterStudioFileClass(string handler, string handlerParam)
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		string value = "\"" + handler + "\" " + handlerParam;
		string value2 = handler + ",0";
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Classes\\Roblox.Place");
			using RegistryKey registryKey2 = registryKey?.CreateSubKey("DefaultIcon");
			using RegistryKey registryKey3 = registryKey?.CreateSubKey("shell\\open");
			using RegistryKey registryKey4 = registryKey3?.CreateSubKey("command");
			if (registryKey == null || registryKey2 == null || registryKey4 == null)
			{
				throw new InvalidOperationException("Failed to create subkeys for Roblox.Place");
			}
			registryKey.SetValueSafe("", "Roblox Place");
			registryKey3.SetValueSafe("", "Open");
			registryKey2.SetValueSafe("", value2);
			registryKey4.SetValueSafe("", value);
		}
		catch (Exception value3)
		{
			App.Logger.WriteLine("Registry::RegisterStudioFileClass", $"Failed: {value3}");
		}
	}

	public static void RegisterStudioFileType(string extension)
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Classes\\" + extension);
			registryKey?.CreateSubKey("Roblox.Place\\ShellNew");
			registryKey?.SetValueSafe("", "Roblox.Place");
		}
		catch (Exception value)
		{
			App.Logger.WriteLine("Registry::RegisterStudioFileType", $"Failed for {extension}: {value}");
		}
	}

	public static void RegisterApis()
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		try
		{
			using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey("Software\\Voidstrap");
			registryKey?.SetValueSafe("ApplicationPath", Paths.Application);
			registryKey?.SetValueSafe("InstallationPath", Paths.Base);
		}
		catch (Exception value)
		{
			App.Logger.WriteLine("Registry::RegisterApis", $"Failed: {value}");
		}
	}

	public static void Unregister(string key)
	{
		if (!Platform.SupportsRegistry)
		{
			return;
		}
		try
		{
			Registry.CurrentUser.DeleteSubKeyTree("Software\\Classes\\" + key, throwOnMissingSubKey: false);
		}
		catch (Exception value)
		{
			App.Logger.WriteLine("Registry::Unregister", $"Failed to unregister {key}: {value}");
		}
	}
}
