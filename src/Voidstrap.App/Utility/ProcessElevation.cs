using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Collections.Generic;

namespace Voidstrap.Utility;

public static class ProcessElevation
{
	private const int ERROR_CANCELLED = 1223;

	public static bool IsAdministrator()
	{
		try
		{
			using WindowsIdentity ntIdentity = WindowsIdentity.GetCurrent();
			return new WindowsPrincipal(ntIdentity).IsInRole(WindowsBuiltInRole.Administrator);
		}
		catch
		{
			return false;
		}
	}

	public static bool TryRestartElevated(string arguments = "")
	{
		string? processPath = InstallLocationResolver.ResolveExecutable() ?? Environment.ProcessPath;
		if (string.IsNullOrEmpty(processPath))
		{
			return false;
		}
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = processPath,
			Arguments = arguments,
			UseShellExecute = true,
			Verb = "runas"
		};
		try
		{
			Process.Start(startInfo);
			return true;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			return false;
		}
		catch
		{
			return false;
		}
	}

	public static bool TryRestartElevated(IEnumerable<string> arguments)
	{
		string? processPath = InstallLocationResolver.ResolveExecutable() ?? Environment.ProcessPath;
		if (string.IsNullOrEmpty(processPath))
			return false;
		ProcessStartInfo startInfo = new ProcessStartInfo
		{
			FileName = processPath,
			UseShellExecute = true,
			Verb = "runas"
		};
		foreach (string argument in arguments)
			startInfo.ArgumentList.Add(argument);
		try
		{
			Process.Start(startInfo);
			return true;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
		{
			return false;
		}
		catch
		{
			return false;
		}
	}
}
