using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Voidstrap.Integrations.AssetProxy;

internal static class MacAssetWarpBridge
{
	private const string LogIdent = "MacAssetWarpBridge";

	private static readonly string[] ProxyVariables = ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy"];

	private static Uri? _address;

	public static Voidstrap.Platform.OperationResult Enable(Uri address)
	{
		Volatile.Write(ref _address, address);
		App.Logger?.WriteLine(LogIdent, "AssetWarp will route Roblox through " + address);
		return Voidstrap.Platform.OperationResult.Success();
	}

	public static void Disable()
	{
		Volatile.Write(ref _address, null);
	}

	public static bool StartRoblox()
	{
		Uri? address = Volatile.Read(ref _address);
		if (!OperatingSystem.IsMacOS() || address is null)
			return false;
		string? bundle = Voidstrap.Utility.MacRobloxBundle.Find();
		string executable = bundle == null ? "" : Path.Combine(bundle, "Contents", "MacOS", "RobloxPlayer");
		if (!File.Exists(executable))
		{
			App.Logger?.WriteLine(LogIdent, "RobloxPlayer was not found, Roblox starts without the AssetWarp proxy");
			return false;
		}
		try
		{
			ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true };
			string value = address.GetLeftPart(UriPartial.Authority);
			foreach (string name in ProxyVariables)
				info.Environment[name] = value;
			using Process? process = Process.Start(info);
			if (process == null)
				return false;
			App.Logger?.WriteLine(LogIdent, "Started Roblox " + process.Id + " with the AssetWarp proxy");
			return true;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			App.Logger?.WriteLine(LogIdent, "Roblox could not be started with the AssetWarp proxy: " + ex.Message);
			return false;
		}
	}
}
