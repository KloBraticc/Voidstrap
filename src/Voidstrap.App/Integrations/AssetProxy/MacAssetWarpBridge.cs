using System;
using System.Diagnostics;
using System.Threading;

namespace Voidstrap.Integrations.AssetProxy;

internal static class MacAssetWarpBridge
{
	private const string LogIdent = "MacAssetWarpBridge";

	private static readonly string[] ProxyVariables = ["HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy"];

	private static Uri? _address;

	private static int _environmentSet;

	public static Voidstrap.Platform.OperationResult Enable(Uri address)
	{
		Volatile.Write(ref _address, address);
		App.Logger?.WriteLine(LogIdent, "AssetWarp will route Roblox through " + address);
		return Voidstrap.Platform.OperationResult.Success();
	}

	public static void Disable()
	{
		Volatile.Write(ref _address, null);
		ClearLaunchEnvironment();
	}

	public static bool ApplyLaunchEnvironment()
	{
		Uri? address = Volatile.Read(ref _address);
		if (!OperatingSystem.IsMacOS() || address is null)
			return false;
		string value = address.GetLeftPart(UriPartial.Authority);
		foreach (string name in ProxyVariables)
		{
			if (!RunLaunchctl("setenv", name, value))
			{
				ClearLaunchEnvironment();
				return false;
			}
		}
		Interlocked.Exchange(ref _environmentSet, 1);
		App.Logger?.WriteLine(LogIdent, "Roblox will start with the AssetWarp proxy");
		return true;
	}

	public static void ClearLaunchEnvironment()
	{
		if (!OperatingSystem.IsMacOS() || Interlocked.Exchange(ref _environmentSet, 0) == 0)
			return;
		foreach (string name in ProxyVariables)
			RunLaunchctl("unsetenv", name, null);
	}

	private static bool RunLaunchctl(string verb, string name, string? value)
	{
		try
		{
			ProcessStartInfo info = new("/bin/launchctl") { UseShellExecute = false, CreateNoWindow = true };
			info.ArgumentList.Add(verb);
			info.ArgumentList.Add(name);
			if (value != null)
				info.ArgumentList.Add(value);
			using Process? process = Process.Start(info);
			return process != null && process.WaitForExit(5000) && process.ExitCode == 0;
		}
		catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
			App.Logger?.WriteLine(LogIdent, "launchctl " + verb + " failed: " + ex.Message);
			return false;
		}
	}
}
